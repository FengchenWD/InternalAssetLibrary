using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace InternalAssetLibrary.Server.Data;

/// <summary>Ordered, transactional migrations. Version 2 can downgrade losslessly to 1; business state is never dropped.</summary>
internal static class SqliteSchemaMigrator
{
    internal const int CurrentVersion = 2;
    private const string InitialSql = """
        CREATE TABLE application_state (
            id INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),
            revision INTEGER NOT NULL CHECK (revision > 0),
            state_json TEXT NOT NULL CHECK (length(state_json) > 0),
            updated_utc TEXT NOT NULL);
        """;
    private const string AuditSql = """
        CREATE TABLE IF NOT EXISTS schema_migrations (
            run_id TEXT NOT NULL PRIMARY KEY,
            from_version INTEGER NOT NULL,
            to_version INTEGER NOT NULL,
            migration_sha256 TEXT NOT NULL,
            completed_utc TEXT NOT NULL,
            backup_path TEXT,
            result TEXT NOT NULL);
        """;

    internal static async Task<int> VersionAsync(SqliteConnection connection, CancellationToken token = default)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(token));
    }

    internal static async Task CheckIntegrityAsync(SqliteConnection connection, CancellationToken token = default,
        SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA integrity_check;";
        using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token) || reader.GetString(0) != "ok" || await reader.ReadAsync(token))
            throw new InvalidDataException("SQLite integrity_check failed; no migration or serving writes is permitted.");
    }

    internal static async Task MigrateAsync(SqliteConnection connection, string databasePath,
        int targetVersion = CurrentVersion, CancellationToken token = default)
    {
        var version = await VersionAsync(connection, token);
        if (version < 0 || version > CurrentVersion || targetVersion is < 1 or > CurrentVersion)
            throw new InvalidDataException($"Unsupported SQLite schema transition {version} -> {targetVersion}.");
        await CheckIntegrityAsync(connection, token);
        using (var wal = connection.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode=WAL;";
            if (!string.Equals(Convert.ToString(await wal.ExecuteScalarAsync(token)), "wal", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("SQLite WAL mode could not be enabled.");
        }
        while (version != targetVersion)
        {
            var next = version < targetVersion ? version + 1 : version - 1;
            var sql = next == 1 && version == 0 ? InitialSql : AuditSql;
            var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));
            var runId = Guid.NewGuid().ToString("N");
            string? backupPath = null;
            if (version > 0) backupPath = await SnapshotAsync(connection, databasePath, version, next, runId, token);
            // The external journal survives a rollback even when the migration creates the audit table itself.
            await JournalAsync(databasePath, runId, version, next, checksum, backupPath, "started");
            try
            {
                using var transaction = connection.BeginTransaction();
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = sql + $"\nPRAGMA user_version={next};";
                await command.ExecuteNonQueryAsync(token);
                if (version != 0)
                {
                    command.CommandText = "INSERT INTO schema_migrations VALUES($run,$from,$to,$hash,$utc,$backup,'committed');";
                    command.Parameters.AddWithValue("$run", runId);
                    command.Parameters.AddWithValue("$from", version);
                    command.Parameters.AddWithValue("$to", next);
                    command.Parameters.AddWithValue("$hash", checksum);
                    command.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));
                    command.Parameters.AddWithValue("$backup", (object?)backupPath ?? DBNull.Value);
                    await command.ExecuteNonQueryAsync(token);
                }
                await CheckIntegrityAsync(connection, token, transaction);
                await transaction.CommitAsync(token);
            }
            catch
            {
                await JournalAsync(databasePath, runId, version, next, checksum, backupPath, "rolled-back");
                throw;
            }
            await JournalAsync(databasePath, runId, version, next, checksum, backupPath, "committed");
            version = next;
        }
    }

    private static async Task<string> SnapshotAsync(SqliteConnection source, string databasePath,
        int from, int to, string runId, CancellationToken token)
    {
        var directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(databasePath))!, "migration-backups");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"schema-{from}-to-{to}-{runId}.db");
        using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Pooling = false }.ToString()))
        {
            await destination.OpenAsync(token);
            source.BackupDatabase(destination);
            await CheckIntegrityAsync(destination, token);
        }
        using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
        await File.WriteAllTextAsync(path + ".json", JsonSerializer.Serialize(new
            { fromVersion = from, toVersion = to, sha256 = hash, sizeBytes = stream.Length, createdUtc = DateTimeOffset.UtcNow }), token);
        return path;
    }

    private static Task JournalAsync(string databasePath, string runId, int from, int to,
        string hash, string? backup, string result) => File.AppendAllTextAsync(databasePath + ".migrations.jsonl",
        JsonSerializer.Serialize(new { runId, fromVersion = from, toVersion = to, migrationSha256 = hash,
            backupPath = backup, result, utc = DateTimeOffset.UtcNow }) + "\n");
}
