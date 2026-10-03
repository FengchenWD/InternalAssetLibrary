using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace InternalAssetLibrary.Server.Data;

internal sealed record SqliteBackupResult(string Path, long SizeBytes, string Sha256, long Revision);

internal interface ISqliteBackupSource
{
    Task<SqliteBackupResult> CreateVerifiedBackupAsync(
        string destinationPath,
        CancellationToken cancellationToken = default);
}

internal sealed class SqliteAppDataStore : IAppDataStore, ISqliteBackupSource, IDisposable
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly ILogger<SqliteAppDataStore> _logger;
    private AppState? _state;
    private long _revision;

    public SqliteAppDataStore(
        IHostEnvironment environment,
        IConfiguration configuration,
        ILogger<SqliteAppDataStore> logger)
    {
        var options = configuration.GetSection("Sqlite").Get<SqliteStorageOptions>() ?? new();
        var databasePath = Path.GetFullPath(options.DatabasePath, environment.ContentRootPath);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true
        }.ToString();
        DatabasePath = databasePath;
        _logger = logger;
    }

    public string StorageKind => "sqlite-wal";

    internal string DatabasePath { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_state is not null)
            {
                return;
            }

            await using var connection = await OpenConnectionAsync(cancellationToken);
            await SqliteSchemaMigrator.MigrateAsync(connection, DatabasePath, token: cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT revision, state_json FROM application_state WHERE id = 1;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                var initial = new AppState();
                AtomicJsonDataStore.Normalize(initial);
                await reader.DisposeAsync();
                await PersistAsync(connection, initial, 1, cancellationToken);
                _state = initial;
                _revision = 1;
                return;
            }

            var revision = reader.GetInt64(0);
            var json = reader.GetString(1);
            AppState loaded;
            try
            {
                loaded = JsonSerializer.Deserialize<AppState>(json, _jsonOptions)
                    ?? throw new InvalidDataException("The SQLite application state is empty.");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException(
                    "The SQLite application state is invalid. Restore a verified database backup.",
                    exception);
            }

            if (AtomicJsonDataStore.Normalize(loaded))
            {
                revision++;
                await reader.DisposeAsync();
                await PersistAsync(connection, loaded, revision, cancellationToken);
            }
            _state = loaded;
            _revision = revision;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TResult> ReadAsync<TResult>(
        Func<AppState, TResult> read,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(read);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureInitialized();
            return read(Clone(_state!));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TResult> UpdateAsync<TResult>(
        Func<AppState, TResult> update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureInitialized();
            var working = Clone(_state!);
            var result = update(working);
            AtomicJsonDataStore.Normalize(working);
            var nextRevision = checked(_revision + 1);

            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
            await PersistAsync(connection, working, nextRevision, CancellationToken.None, transaction);
            await transaction.CommitAsync(CancellationToken.None);

            _state = working;
            _revision = nextRevision;
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SqliteBackupResult> CreateVerifiedBackupAsync(
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var fullDestinationPath = Path.GetFullPath(destinationPath);
        if (string.Equals(fullDestinationPath, DatabasePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The backup destination must differ from the live database.", nameof(destinationPath));
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureInitialized();
            Directory.CreateDirectory(Path.GetDirectoryName(fullDestinationPath)!);
            if (File.Exists(fullDestinationPath))
            {
                throw new IOException($"Backup destination '{fullDestinationPath}' already exists.");
            }

            try
            {
                await using var source = await OpenConnectionAsync(cancellationToken);
                var destinationConnectionString = new SqliteConnectionStringBuilder
                {
                    DataSource = fullDestinationPath,
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    Cache = SqliteCacheMode.Private,
                    Pooling = false
                }.ToString();
                await using (var destination = new SqliteConnection(destinationConnectionString))
                {
                    await destination.OpenAsync(cancellationToken);
                    source.BackupDatabase(destination);
                }

                var verifiedRevision = await VerifyBackupAsync(fullDestinationPath, cancellationToken);
                if (verifiedRevision != _revision)
                {
                    throw new InvalidDataException(
                        $"Backup revision {verifiedRevision} does not match live revision {_revision}.");
                }

                await using var backupStream = new FileStream(
                    fullDestinationPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var hash = await SHA256.HashDataAsync(backupStream, cancellationToken);
                return new SqliteBackupResult(
                    fullDestinationPath,
                    backupStream.Length,
                    Convert.ToHexString(hash),
                    verifiedRevision);
            }
            catch
            {
                TryDeleteBackup(fullDestinationPath);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA busy_timeout = 10000;
            PRAGMA foreign_keys = ON;
            PRAGMA synchronous = FULL;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private async Task PersistAsync(
        SqliteConnection connection,
        AppState state,
        long revision,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        var json = JsonSerializer.Serialize(state, _jsonOptions);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO application_state (id, revision, state_json, updated_utc)
            VALUES (1, $revision, $state, $updated)
            ON CONFLICT(id) DO UPDATE SET
                revision = excluded.revision,
                state_json = excluded.state_json,
                updated_utc = excluded.updated_utc;
            """;
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$state", json);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        var rows = await command.ExecuteNonQueryAsync(cancellationToken);
        if (rows != 1)
        {
            throw new InvalidOperationException($"Expected one SQLite state row to be written, but wrote {rows}.");
        }

        _logger.LogDebug("Persisted SQLite application state revision {Revision}.", revision);
    }

    private async Task<long> VerifyBackupAsync(string path, CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using (var integrity = connection.CreateCommand())
        {
            integrity.CommandText = "PRAGMA integrity_check;";
            var result = Convert.ToString(await integrity.ExecuteScalarAsync(cancellationToken));
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"SQLite backup integrity check failed: {result}");
            }
        }

        await using var stateCommand = connection.CreateCommand();
        stateCommand.CommandText = "SELECT revision, state_json FROM application_state WHERE id = 1;";
        await using var reader = await stateCommand.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException("SQLite backup does not contain the application state row.");
        }

        var revision = reader.GetInt64(0);
        var state = JsonSerializer.Deserialize<AppState>(reader.GetString(1), _jsonOptions)
            ?? throw new InvalidDataException("SQLite backup contains an empty application state.");
        AtomicJsonDataStore.Normalize(state);
        return revision;
    }

    private AppState Clone(AppState state)
    {
        var json = JsonSerializer.Serialize(state, _jsonOptions);
        var clone = JsonSerializer.Deserialize<AppState>(json, _jsonOptions)
            ?? throw new InvalidOperationException("Unable to clone the SQLite application state.");
        AtomicJsonDataStore.Normalize(clone);
        return clone;
    }

    private void EnsureInitialized()
    {
        if (_state is null)
        {
            throw new InvalidOperationException("The SQLite application state has not been initialized.");
        }
    }

    private void TryDeleteBackup(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not remove failed SQLite backup {Path}.", path);
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _gate.Dispose();
    }
}

internal sealed class SqliteStorageOptions
{
    public string DatabasePath { get; set; } = "App_Data/library.db";
}
