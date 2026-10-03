using System.Text.Json;

namespace InternalAssetLibrary.Client.Core.Downloads;

public sealed record PersistentDownloadMapping(
    DownloadAssetKey Key,
    string OriginalFileName,
    string FullPath,
    long SizeBytes,
    DateTimeOffset DownloadedAtUtc,
    string? Sha256);

public interface IPersistentDownloadRegistry
{
    Task<PersistentDownloadMapping?> FindAsync(
        DownloadAssetKey key,
        bool requireExistingFile = true,
        CancellationToken cancellationToken = default);

    Task RegisterAsync(
        PersistentDownloadMapping mapping,
        CancellationToken cancellationToken = default);

    Task RemoveMappingAsync(
        DownloadAssetKey key,
        CancellationToken cancellationToken = default);
}

public sealed class JsonPersistentDownloadRegistry : IPersistentDownloadRegistry, IDisposable
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonPersistentDownloadRegistry(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
    }

    public async Task<PersistentDownloadMapping?> FindAsync(
        DownloadAssetKey key,
        bool requireExistingFile = true,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            var mapping = state.Mappings.FirstOrDefault(item => item.Key == key);
            return mapping is not null && (!requireExistingFile || File.Exists(mapping.FullPath))
                ? mapping
                : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RegisterAsync(
        PersistentDownloadMapping mapping,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        if (mapping.Key.AssetId == Guid.Empty || mapping.Key.VersionId == Guid.Empty)
        {
            throw new ArgumentException("Asset and version identifiers must not be empty.", nameof(mapping));
        }

        if (mapping.SizeBytes < 0 || !Path.IsPathFullyQualified(mapping.FullPath))
        {
            throw new ArgumentException("The completed download mapping is invalid.", nameof(mapping));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            var mappings = state.Mappings
                .Where(item => item.Key != mapping.Key)
                .Append(mapping with { FullPath = Path.GetFullPath(mapping.FullPath) })
                .OrderBy(item => item.DownloadedAtUtc)
                .ToArray();
            await SaveCoreAsync(state with { Mappings = mappings }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveMappingAsync(
        DownloadAssetKey key,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            var updated = state with
            {
                Mappings = state.Mappings.Where(item => item.Key != key).ToArray()
            };
            await SaveCoreAsync(updated, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<DownloadRegistryState> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
        {
            return DownloadRegistryState.Empty;
        }

        try
        {
            await using var stream = new FileStream(
                _filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var state = await JsonSerializer.DeserializeAsync<DownloadRegistryState>(
                stream,
                SerializerOptions,
                cancellationToken).ConfigureAwait(false);
            if (state is null || state.SchemaVersion != SchemaVersion)
            {
                throw new InvalidDataException("The persistent download map has an unsupported schema.");
            }

            return state with { Mappings = state.Mappings ?? [] };
        }
        catch (JsonException exception)
        {
            QuarantineCorruptRegistry(exception);
            return DownloadRegistryState.Empty;
        }
        catch (InvalidDataException exception)
        {
            QuarantineCorruptRegistry(exception);
            return DownloadRegistryState.Empty;
        }
    }

    private async Task SaveCoreAsync(
        DownloadRegistryState state,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = _filePath + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    state,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _filePath, true);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    private void QuarantineCorruptRegistry(Exception exception)
    {
        try
        {
            var quarantine = _filePath + $".corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
            File.Move(_filePath, quarantine, overwrite: false);
        }
        catch (Exception quarantineException)
        {
            System.Diagnostics.Debug.WriteLine($"Could not quarantine download registry: {quarantineException.Message}");
            System.Diagnostics.Debug.WriteLine(exception.Message);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    public void Dispose() => _gate.Dispose();

    private sealed record DownloadRegistryState(int SchemaVersion, PersistentDownloadMapping[] Mappings)
    {
        public static DownloadRegistryState Empty { get; } = new(
            JsonPersistentDownloadRegistry.SchemaVersion,
            []);
    }
}
