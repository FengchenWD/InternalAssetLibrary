using System.Text.Json;
using System.Text.Json.Serialization;
using InternalAssetLibrary.Client.Core.Tags;

namespace InternalAssetLibrary.Client.Core.LocalAssets;

public interface ILocalAssetCatalogStore
{
    Task<LocalAssetCatalog> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(LocalAssetCatalog catalog, CancellationToken cancellationToken = default);
}

public sealed class InMemoryLocalAssetCatalogStore : ILocalAssetCatalogStore
{
    private LocalAssetCatalog _catalog = LocalAssetCatalog.Empty;

    public Task<LocalAssetCatalog> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Volatile.Read(ref _catalog));
    }

    public Task SaveAsync(LocalAssetCatalog catalog, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        cancellationToken.ThrowIfCancellationRequested();
        Volatile.Write(ref _catalog, catalog);
        return Task.CompletedTask;
    }
}

public sealed class JsonLocalAssetCatalogStore : ILocalAssetCatalogStore, IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonLocalAssetCatalogStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
    }

    public async Task<LocalAssetCatalog> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_filePath))
            {
                return LocalAssetCatalog.Empty;
            }

            LocalAssetCatalog? catalog;
            await using (var stream = new FileStream(
                             _filePath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                catalog = await JsonSerializer.DeserializeAsync<LocalAssetCatalog>(
                    stream,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
            }

            if (catalog is null || catalog.SchemaVersion is < 1 or > LocalAssetCatalog.CurrentSchemaVersion)
            {
                throw new InvalidDataException("The local asset index has an unsupported schema.");
            }

            var folders = catalog.Folders ?? [];
            var originalAssets = catalog.Assets ?? [];
            var tagLibrary = TagRules.NormalizeLibrary(
                (catalog.TagLibrary ?? []).Concat(originalAssets.SelectMany(asset => asset.Tags ?? [])));
            var canonicalTags = tagLibrary.ToDictionary(tag => tag, StringComparer.OrdinalIgnoreCase);
            var assets = originalAssets.Select(asset => asset with
            {
                Tags = TagRules.NormalizeLibrary(asset.Tags)
                    .Take(TagRules.MaximumTagsPerAsset)
                    .Select(tag => canonicalTags[tag])
                    .ToArray()
            }).ToArray();
            var directories = catalog.SchemaVersion == 1
                ? RecoverDirectories(folders, assets)
                : catalog.Directories ?? [];
            var normalized = new LocalAssetCatalog(
                LocalAssetCatalog.CurrentSchemaVersion,
                folders,
                assets,
                directories,
                tagLibrary);
            var needsSave = catalog.SchemaVersion < LocalAssetCatalog.CurrentSchemaVersion ||
                            catalog.Folders is null ||
                            catalog.Assets is null ||
                            catalog.Directories is null ||
                            catalog.TagLibrary is null ||
                            !catalog.TagLibrary.SequenceEqual(tagLibrary, StringComparer.Ordinal) ||
                            originalAssets.Where((asset, index) =>
                                asset.Tags is null ||
                                !asset.Tags.SequenceEqual(assets[index].Tags, StringComparer.Ordinal)).Any();
            if (needsSave)
            {
                await SaveCoreAsync(normalized, cancellationToken).ConfigureAwait(false);
            }

            return normalized;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(LocalAssetCatalog catalog, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SaveCoreAsync(catalog, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SaveCoreAsync(LocalAssetCatalog catalog, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = _filePath + ".tmp";
        await using (var stream = new FileStream(
                         temporaryPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         64 * 1024,
                         FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                catalog,
                SerializerOptions,
                cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporaryPath, _filePath, true);
    }

    private static IndexedDirectory[] RecoverDirectories(
        IReadOnlyCollection<IndexedFolder> folders,
        IEnumerable<LocalAsset> assets)
    {
        var folderAvailability = folders.ToDictionary(
            folder => folder.Id,
            folder => folder.Availability == IndexedFolderAvailability.Offline
                ? IndexedDirectoryAvailability.Offline
                : IndexedDirectoryAvailability.Available);
        var pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var directories = new Dictionary<string, IndexedDirectory>(pathComparer);

        foreach (var asset in assets)
        {
            var relativeDirectory = Path.GetDirectoryName(asset.RelativePath);
            while (!string.IsNullOrEmpty(relativeDirectory) && relativeDirectory != ".")
            {
                relativeDirectory = NormalizeRelativePath(relativeDirectory);
                var key = $"{asset.FolderId:N}\0{relativeDirectory}";
                directories.TryAdd(key, new IndexedDirectory(
                    asset.FolderId,
                    relativeDirectory,
                    Path.GetFileName(relativeDirectory),
                    folderAvailability.GetValueOrDefault(
                        asset.FolderId,
                        IndexedDirectoryAvailability.TemporarilyUnavailable)));
                relativeDirectory = Path.GetDirectoryName(relativeDirectory);
            }
        }

        return directories.Values
            .OrderBy(directory => directory.FolderId)
            .ThenBy(directory => directory.RelativePath, pathComparer)
            .ToArray();
    }

    private static string NormalizeRelativePath(string path) =>
        Path.TrimEndingDirectorySeparator(
            path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar));

    public void Dispose() => _gate.Dispose();
}
