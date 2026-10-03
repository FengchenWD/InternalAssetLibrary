using System.Runtime.ExceptionServices;
using InternalAssetLibrary.Client.Core.Tags;

namespace InternalAssetLibrary.Client.Core.LocalAssets;

public sealed class LocalAssetIndexService : IDisposable
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private readonly ILocalAssetCatalogStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly Func<string, bool> _isContainingStorageAvailable;
    private readonly IRecycleBinFileSystem _recycleBin;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LocalAssetIndexService(
        ILocalAssetCatalogStore store,
        TimeProvider? timeProvider = null,
        Func<string, bool>? isContainingStorageAvailable = null,
        IRecycleBinFileSystem? recycleBin = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _isContainingStorageAvailable =
            isContainingStorageAvailable ?? IsContainingStorageAvailable;
        _recycleBin = recycleBin ?? new WindowsRecycleBinFileSystem();
    }

    public Task<LocalAssetCatalog> GetCatalogAsync(CancellationToken cancellationToken = default) =>
        _store.LoadAsync(cancellationToken);

    public async Task<LocalAssetIndexResult> IndexFolderAsync(
        string folderPath,
        bool? isExternalStorage = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizeFolderPath(folderPath);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var catalog = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            var existingFolder = catalog.Folders.FirstOrDefault(folder =>
                PathComparer.Equals(folder.Path, normalizedPath));
            if (existingFolder is null)
            {
                var existingParent = catalog.Folders.FirstOrDefault(folder =>
                    IsStrictDescendantPath(folder.Path, normalizedPath));
                if (existingParent is not null)
                {
                    throw new InvalidOperationException(
                        $"不能添加该子文件夹，因为它已经包含在已连接的文件夹“{existingParent.Path}”中。");
                }
            }

            var now = _timeProvider.GetUtcNow();
            var folder = existingFolder ?? new IndexedFolder(
                Guid.NewGuid(),
                normalizedPath,
                isExternalStorage ?? DetectExternalStorage(normalizedPath),
                IndexedFolderAvailability.Offline,
                now,
                null);

            if (isExternalStorage.HasValue && folder.IsExternalStorage != isExternalStorage.Value)
            {
                folder = folder with { IsExternalStorage = isExternalStorage.Value };
            }

            var descendantFolders = catalog.Folders
                .Where(candidate => candidate.Id != folder.Id &&
                                    IsStrictDescendantPath(normalizedPath, candidate.Path))
                .ToArray();
            if (descendantFolders.Length > 0)
            {
                catalog = MergeDescendantRoots(catalog, folder, descendantFolders);
            }

            LocalAssetIndexResult result;
            if (!Directory.Exists(normalizedPath))
            {
                result = _isContainingStorageAvailable(normalizedPath)
                    ? BuildMissingFolderResult(catalog, folder, now)
                    : BuildOfflineResult(catalog, folder, now);
            }
            else
            {
                result = ScanFolder(catalog, folder, now, cancellationToken);
            }

            await _store.SaveAsync(result.Catalog, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<LocalAssetIndexResult>> RefreshAllAsync(
        CancellationToken cancellationToken = default)
    {
        var catalog = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<LocalAssetIndexResult>(catalog.Folders.Length);
        foreach (var folder in catalog.Folders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentCatalog = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (!currentCatalog.Folders.Any(candidate => candidate.Id == folder.Id))
            {
                continue;
            }

            results.Add(await IndexFolderAsync(
                folder.Path,
                folder.IsExternalStorage,
                cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    public async Task<LocalStorageAvailabilityProbeResult> ProbeStorageAvailabilityAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var catalog = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            var disconnected = new List<IndexedFolder>();
            var reconnected = new List<IndexedFolder>();
            var missing = new List<IndexedFolder>();

            foreach (var folder in catalog.Folders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var exists = Directory.Exists(folder.Path);
                if (exists)
                {
                    if (folder.Availability is IndexedFolderAvailability.Offline or
                        IndexedFolderAvailability.Missing)
                    {
                        reconnected.Add(folder);
                    }
                }
                else if (_isContainingStorageAvailable(folder.Path))
                {
                    var hasIndexedContents = catalog.Assets.Any(asset => asset.FolderId == folder.Id) ||
                                             catalog.Directories.Any(directory => directory.FolderId == folder.Id);
                    if (folder.Availability != IndexedFolderAvailability.Missing || hasIndexedContents)
                    {
                        missing.Add(folder);
                    }
                }
                else if (folder.Availability != IndexedFolderAvailability.Offline)
                {
                    disconnected.Add(folder);
                }
            }

            if (disconnected.Count == 0 && missing.Count == 0)
            {
                return new LocalStorageAvailabilityProbeResult(catalog, [], reconnected, []);
            }

            var now = _timeProvider.GetUtcNow();
            var disconnectedIds = disconnected.Select(folder => folder.Id).ToHashSet();
            var missingIds = missing.Select(folder => folder.Id).ToHashSet();
            var updated = catalog with
            {
                Folders = catalog.Folders.Select(folder =>
                {
                    if (disconnectedIds.Contains(folder.Id))
                    {
                        return folder with
                        {
                            Availability = IndexedFolderAvailability.Offline,
                            LastScanAtUtc = now
                        };
                    }

                    return missingIds.Contains(folder.Id)
                        ? folder with
                        {
                            Availability = IndexedFolderAvailability.Missing,
                            LastScanAtUtc = now
                        }
                        : folder;
                }).ToArray(),
                Assets = catalog.Assets
                    .Where(asset => !missingIds.Contains(asset.FolderId))
                    .Select(asset => disconnectedIds.Contains(asset.FolderId)
                        ? asset with { Availability = LocalAssetAvailability.OfflineStorage }
                        : asset)
                    .ToArray(),
                Directories = catalog.Directories
                    .Where(directory => !missingIds.Contains(directory.FolderId))
                    .Select(directory => disconnectedIds.Contains(directory.FolderId)
                        ? directory with { Availability = IndexedDirectoryAvailability.Offline }
                        : directory)
                    .ToArray()
            };

            await _store.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
            return new LocalStorageAvailabilityProbeResult(updated, disconnected, reconnected, missing);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LocalAssetCatalog> SetTagsAsync(
        Guid assetId,
        IEnumerable<string> tags,
        CancellationToken cancellationToken = default)
    {
        var normalizedTags = TagRules.NormalizeSelection(tags);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var catalog = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            var libraryByName = catalog.TagLibrary.ToDictionary(
                tag => tag,
                StringComparer.OrdinalIgnoreCase);
            foreach (var tag in normalizedTags)
            {
                libraryByName.TryAdd(tag, tag);
            }

            normalizedTags = normalizedTags.Select(tag => libraryByName[tag]).ToArray();
            var found = false;
            var assets = catalog.Assets.Select(asset =>
            {
                if (asset.Id != assetId)
                {
                    return asset;
                }

                found = true;
                return asset with { Tags = normalizedTags };
            }).ToArray();

            if (!found)
            {
                throw new KeyNotFoundException($"Local asset '{assetId}' does not exist.");
            }

            var updated = catalog with
            {
                Assets = assets,
                TagLibrary = libraryByName.Values
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            };
            await _store.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LocalAssetCatalog> CreateTagAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = TagRules.NormalizeName(name);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var catalog = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (catalog.TagLibrary.Contains(normalizedName, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("该标签已存在。");
            }

            var updated = catalog with
            {
                TagLibrary = catalog.TagLibrary
                    .Append(normalizedName)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            };
            await _store.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LocalAssetCatalog> RenameTagAsync(
        string currentName,
        string newName,
        CancellationToken cancellationToken = default)
    {
        var normalizedCurrentName = TagRules.NormalizeName(currentName);
        var normalizedNewName = TagRules.NormalizeName(newName);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var catalog = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            var existingName = catalog.TagLibrary.FirstOrDefault(tag =>
                tag.Equals(normalizedCurrentName, StringComparison.OrdinalIgnoreCase));
            if (existingName is null)
            {
                throw new KeyNotFoundException($"本地标签“{normalizedCurrentName}”不存在。");
            }

            if (catalog.TagLibrary.Any(tag =>
                    !tag.Equals(existingName, StringComparison.OrdinalIgnoreCase) &&
                    tag.Equals(normalizedNewName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("该标签已存在。");
            }

            var updated = catalog with
            {
                TagLibrary = catalog.TagLibrary
                    .Select(tag => tag.Equals(existingName, StringComparison.OrdinalIgnoreCase)
                        ? normalizedNewName
                        : tag)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                Assets = catalog.Assets.Select(asset => asset with
                {
                    Tags = asset.Tags
                        .Select(tag => tag.Equals(existingName, StringComparison.OrdinalIgnoreCase)
                            ? normalizedNewName
                            : tag)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Order(StringComparer.OrdinalIgnoreCase)
                        .ToArray()
                }).ToArray()
            };
            await _store.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LocalAssetCatalog> DeleteTagAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = TagRules.NormalizeName(name);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var catalog = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (!catalog.TagLibrary.Contains(normalizedName, StringComparer.OrdinalIgnoreCase))
            {
                throw new KeyNotFoundException($"本地标签“{normalizedName}”不存在。");
            }

            var updated = catalog with
            {
                TagLibrary = catalog.TagLibrary
                    .Where(tag => !tag.Equals(normalizedName, StringComparison.OrdinalIgnoreCase))
                    .ToArray(),
                Assets = catalog.Assets.Select(asset => asset with
                {
                    Tags = asset.Tags
                        .Where(tag => !tag.Equals(normalizedName, StringComparison.OrdinalIgnoreCase))
                        .ToArray()
                }).ToArray()
            };
            await _store.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LocalAssetCatalog> RemoveFolderAsync(
        Guid folderId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var catalog = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            var updated = catalog with
            {
                Folders = catalog.Folders.Where(folder => folder.Id != folderId).ToArray(),
                Assets = catalog.Assets.Where(asset => asset.FolderId != folderId).ToArray(),
                Directories = catalog.Directories
                    .Where(directory => directory.FolderId != folderId)
                    .ToArray()
            };

            await _store.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LocalAssetCatalog> RenameAssetAsync(
        Guid assetId,
        string newFileName,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = NormalizeLeafName(newFileName, nameof(newFileName));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var catalog = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            var asset = catalog.Assets.SingleOrDefault(candidate => candidate.Id == assetId)
                        ?? throw new KeyNotFoundException($"Local asset '{assetId}' does not exist.");
            if (!File.Exists(asset.FullPath))
            {
                throw new FileNotFoundException("要重命名的文件不存在。", asset.FullPath);
            }

            var targetExtension = Path.GetExtension(normalizedName);
            if (!targetExtension.Equals(asset.Extension, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("重命名不能更改文件扩展名。");
            }

            var sourcePath = NormalizeFilePath(asset.FullPath);
            var targetPath = NormalizeFilePath(Path.Combine(
                Path.GetDirectoryName(sourcePath)!,
                normalizedName));
            if (string.Equals(sourcePath, targetPath, StringComparison.Ordinal))
            {
                return catalog;
            }

            ThrowIfTargetExists(sourcePath, targetPath);
            File.Move(sourcePath, targetPath);
            var movedFile = new FileInfo(targetPath);
            var folder = catalog.Folders.Single(candidate => candidate.Id == asset.FolderId);
            var updatedAsset = asset with
            {
                FullPath = targetPath,
                RelativePath = Path.GetRelativePath(folder.Path, targetPath),
                FileName = movedFile.Name,
                SizeBytes = movedFile.Length,
                LastWriteTimeUtc = new DateTimeOffset(movedFile.LastWriteTimeUtc, TimeSpan.Zero)
            };
            var updated = catalog with
            {
                Assets = catalog.Assets
                    .Select(candidate => candidate.Id == asset.Id ? updatedAsset : candidate)
                    .OrderBy(candidate => candidate.FullPath, PathComparer)
                    .ToArray()
            };

            try
            {
                await _store.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
                return updated;
            }
            catch (Exception exception)
            {
                await RollBackMoveAndRethrowAsync(
                    () => File.Move(targetPath, sourcePath),
                    catalog,
                    exception).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LocalAssetCatalog> AddTagsToAssetsAsync(
        IReadOnlyCollection<Guid> assetIds,
        IReadOnlyCollection<string> tags,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assetIds);
        ArgumentNullException.ThrowIfNull(tags);
        var ids = assetIds.Distinct().ToHashSet();
        if (ids.Count == 0)
        {
            throw new ArgumentException("至少需要选择一个本地素材。", nameof(assetIds));
        }

        var normalizedTags = TagRules.NormalizeLibrary(tags);
        if (normalizedTags.Length == 0)
        {
            throw new ArgumentException("至少需要选择一个标签。", nameof(tags));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var catalog = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (ids.Any(id => catalog.Assets.All(asset => asset.Id != id)))
            {
                throw new KeyNotFoundException("一个或多个本地素材已不存在。");
            }

            var updated = catalog with
            {
                TagLibrary = TagRules.NormalizeLibrary(catalog.TagLibrary.Concat(normalizedTags)),
                Assets = catalog.Assets.Select(asset => ids.Contains(asset.Id)
                    ? asset with
                    {
                        Tags = TagRules.NormalizeSelection(asset.Tags.Concat(normalizedTags))
                    }
                    : asset).ToArray()
            };
            await _store.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LocalAssetCatalog> MoveAssetsAsync(
        IReadOnlyCollection<Guid> assetIds,
        Guid targetFolderId,
        string? targetRelativeDirectoryPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assetIds);
        var ids = assetIds.Distinct().ToHashSet();
        if (ids.Count == 0)
        {
            throw new ArgumentException("至少需要选择一个本地素材。", nameof(assetIds));
        }

        var normalizedTargetRelativePath = NormalizeIndexedRelativePath(targetRelativeDirectoryPath);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var catalog = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            var targetFolder = catalog.Folders.SingleOrDefault(folder => folder.Id == targetFolderId)
                               ?? throw new KeyNotFoundException($"Indexed folder '{targetFolderId}' does not exist.");
            if (targetFolder.Availability is IndexedFolderAvailability.Offline or IndexedFolderAvailability.Missing)
            {
                throw new IOException("目标素材文件夹当前不可用。");
            }

            EnsureIndexedDirectory(catalog, targetFolder, normalizedTargetRelativePath);
            var targetDirectory = normalizedTargetRelativePath.Length == 0
                ? targetFolder.Path
                : NormalizeFolderPath(Path.Combine(targetFolder.Path, normalizedTargetRelativePath));
            if (!Directory.Exists(targetDirectory))
            {
                throw new DirectoryNotFoundException($"目标文件夹不存在：{targetDirectory}");
            }

            var assetsById = catalog.Assets.ToDictionary(asset => asset.Id);
            var plans = new List<AssetMovePlan>(ids.Count);
            foreach (var id in ids)
            {
                if (!assetsById.TryGetValue(id, out var asset))
                {
                    throw new KeyNotFoundException($"Local asset '{id}' does not exist.");
                }

                if (!asset.IsAvailable || !File.Exists(asset.FullPath))
                {
                    throw new FileNotFoundException("要移动的本地素材当前不可用。", asset.FullPath);
                }

                var sourcePath = NormalizeFilePath(asset.FullPath);
                var targetPath = NormalizeFilePath(Path.Combine(targetDirectory, asset.FileName));
                if (PathComparer.Equals(sourcePath, targetPath))
                {
                    continue;
                }

                plans.Add(new AssetMovePlan(asset, sourcePath, targetPath));
            }

            if (plans.Count == 0)
            {
                return catalog;
            }

            if (plans.Select(plan => plan.TargetPath).Distinct(PathComparer).Count() != plans.Count)
            {
                throw new IOException("所选素材在目标文件夹中会产生重复文件名。");
            }

            foreach (var plan in plans)
            {
                if (File.Exists(plan.TargetPath) || Directory.Exists(plan.TargetPath))
                {
                    throw new IOException($"目标文件已经存在：{Path.GetFileName(plan.TargetPath)}");
                }
            }

            var moved = new List<AssetMovePlan>(plans.Count);
            try
            {
                foreach (var plan in plans)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    MoveFileSafely(plan.SourcePath, plan.TargetPath);
                    moved.Add(plan);
                }
            }
            catch (Exception exception)
            {
                ThrowAfterAssetMoveRollback(exception, moved);
                throw;
            }

            var replacements = plans.ToDictionary(plan => plan.Asset.Id, plan =>
            {
                var movedFile = new FileInfo(plan.TargetPath);
                return plan.Asset with
                {
                    FolderId = targetFolder.Id,
                    FullPath = plan.TargetPath,
                    RelativePath = Path.GetRelativePath(targetFolder.Path, plan.TargetPath),
                    FileName = movedFile.Name,
                    SizeBytes = movedFile.Length,
                    LastWriteTimeUtc = new DateTimeOffset(movedFile.LastWriteTimeUtc, TimeSpan.Zero),
                    Availability = LocalAssetAvailability.Available
                };
            });
            var updated = catalog with
            {
                Assets = catalog.Assets
                    .Select(asset => replacements.GetValueOrDefault(asset.Id, asset))
                    .OrderBy(asset => asset.FullPath, PathComparer)
                    .ToArray()
            };

            try
            {
                await _store.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
                return updated;
            }
            catch (Exception exception)
            {
                ThrowAfterAssetMoveRollback(exception, moved);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LocalAssetCatalog> RenameAssetsAsync(
        IReadOnlyList<LocalAssetRenameRequest> requests,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count == 0)
        {
            throw new ArgumentException("至少需要选择一个本地素材。", nameof(requests));
        }

        if (requests.Select(request => request.AssetId).Distinct().Count() != requests.Count)
        {
            throw new ArgumentException("批量重命名不能重复包含同一个素材。", nameof(requests));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var catalog = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            var assetsById = catalog.Assets.ToDictionary(asset => asset.Id);
            var plans = new List<BatchRenamePlan>(requests.Count);
            foreach (var request in requests)
            {
                if (!assetsById.TryGetValue(request.AssetId, out var asset))
                {
                    throw new KeyNotFoundException($"Local asset '{request.AssetId}' does not exist.");
                }

                if (!File.Exists(asset.FullPath))
                {
                    throw new FileNotFoundException("要重命名的文件不存在。", asset.FullPath);
                }

                var normalizedName = NormalizeLeafName(request.NewFileName, nameof(requests));
                if (!Path.GetExtension(normalizedName).Equals(asset.Extension, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("批量重命名不能更改文件扩展名。");
                }

                var sourcePath = NormalizeFilePath(asset.FullPath);
                var targetPath = NormalizeFilePath(Path.Combine(
                    Path.GetDirectoryName(sourcePath)!,
                    normalizedName));
                if (string.Equals(sourcePath, targetPath, StringComparison.Ordinal))
                {
                    continue;
                }

                string temporaryPath;
                do
                {
                    temporaryPath = Path.Combine(
                        Path.GetDirectoryName(sourcePath)!,
                        $".ial-rename-{Guid.NewGuid():N}.tmp");
                }
                while (File.Exists(temporaryPath) || Directory.Exists(temporaryPath));

                plans.Add(new BatchRenamePlan(asset, sourcePath, temporaryPath, targetPath));
            }

            if (plans.Count == 0)
            {
                return catalog;
            }

            if (plans.Select(plan => plan.TargetPath).Distinct(PathComparer).Count() != plans.Count)
            {
                throw new IOException("批量重命名生成了重复的目标文件名。");
            }

            var movingSources = plans.Select(plan => plan.SourcePath).ToHashSet(PathComparer);
            foreach (var plan in plans)
            {
                if ((File.Exists(plan.TargetPath) || Directory.Exists(plan.TargetPath)) &&
                    !movingSources.Contains(plan.TargetPath))
                {
                    throw new IOException($"目标文件已经存在：{Path.GetFileName(plan.TargetPath)}");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            var staged = new List<BatchRenamePlan>(plans.Count);
            try
            {
                foreach (var plan in plans)
                {
                    File.Move(plan.SourcePath, plan.TemporaryPath);
                    staged.Add(plan);
                }
            }
            catch (Exception exception)
            {
                ThrowAfterBatchRenameRollback(exception, staged, fromTargets: false);
                throw;
            }

            var finalized = new List<BatchRenamePlan>(plans.Count);
            try
            {
                foreach (var plan in plans)
                {
                    File.Move(plan.TemporaryPath, plan.TargetPath);
                    finalized.Add(plan);
                }
            }
            catch (Exception exception)
            {
                ThrowAfterBatchRenameRollback(exception, plans, finalized);
                throw;
            }

            var replacements = plans.ToDictionary(plan => plan.Asset.Id, plan =>
            {
                var movedFile = new FileInfo(plan.TargetPath);
                var folder = catalog.Folders.Single(candidate => candidate.Id == plan.Asset.FolderId);
                return plan.Asset with
                {
                    FullPath = plan.TargetPath,
                    RelativePath = Path.GetRelativePath(folder.Path, plan.TargetPath),
                    FileName = movedFile.Name,
                    SizeBytes = movedFile.Length,
                    LastWriteTimeUtc = new DateTimeOffset(movedFile.LastWriteTimeUtc, TimeSpan.Zero)
                };
            });
            var updated = catalog with
            {
                Assets = catalog.Assets
                    .Select(asset => replacements.GetValueOrDefault(asset.Id, asset))
                    .OrderBy(asset => asset.FullPath, PathComparer)
                    .ToArray()
            };

            try
            {
                await _store.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
                return updated;
            }
            catch (Exception exception)
            {
                var failures = RollBackFinalizedBatchRename(plans, exception);
                try
                {
                    await _store.SaveAsync(catalog, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception rollbackException)
                {
                    failures.Add(rollbackException);
                }

                ThrowBatchRenameFailure(failures);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LocalAssetCatalog> RenameDirectoryAsync(
        Guid folderId,
        string relativePath,
        string newName,
        CancellationToken cancellationToken = default)
    {
        var normalizedRelativePath = NormalizeIndexedRelativePath(relativePath);
        var normalizedName = NormalizeLeafName(newName, nameof(newName));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var catalog = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            var folder = catalog.Folders.SingleOrDefault(candidate => candidate.Id == folderId)
                         ?? throw new KeyNotFoundException($"Indexed folder '{folderId}' does not exist.");
            EnsureIndexedDirectory(catalog, folder, normalizedRelativePath);

            var sourcePath = normalizedRelativePath.Length == 0
                ? folder.Path
                : NormalizeFolderPath(Path.Combine(folder.Path, normalizedRelativePath));
            if (!Directory.Exists(sourcePath))
            {
                throw new DirectoryNotFoundException($"要重命名的文件夹不存在：{sourcePath}");
            }

            var parentPath = Path.GetDirectoryName(sourcePath)
                             ?? throw new InvalidOperationException("不能重命名文件系统根目录。");
            var targetPath = NormalizeFolderPath(Path.Combine(parentPath, normalizedName));
            if (string.Equals(sourcePath, targetPath, StringComparison.Ordinal))
            {
                return catalog;
            }

            ThrowIfTargetExists(sourcePath, targetPath);
            if (normalizedRelativePath.Length == 0 && catalog.Folders.Any(candidate =>
                    candidate.Id != folder.Id && PathComparer.Equals(candidate.Path, targetPath)))
            {
                throw new InvalidOperationException("目标路径已经是一个已连接的素材文件夹。");
            }

            Directory.Move(sourcePath, targetPath);
            var updatedFolderPath = normalizedRelativePath.Length == 0 ? targetPath : folder.Path;
            var updatedFolder = normalizedRelativePath.Length == 0
                ? folder with { Path = targetPath }
                : folder;
            var updatedAssets = catalog.Assets.Select(asset =>
            {
                if (asset.FolderId != folder.Id || !IsPathInMovedTree(sourcePath, asset.FullPath))
                {
                    return asset;
                }

                var movedPath = MovePath(sourcePath, targetPath, asset.FullPath);
                return asset with
                {
                    FullPath = movedPath,
                    RelativePath = Path.GetRelativePath(updatedFolderPath, movedPath)
                };
            }).OrderBy(asset => asset.FullPath, PathComparer).ToArray();
            var updatedDirectories = catalog.Directories.Select(directory =>
            {
                if (directory.FolderId != folder.Id)
                {
                    return directory;
                }

                var fullPath = NormalizeFolderPath(Path.Combine(folder.Path, directory.RelativePath));
                if (!IsPathInMovedTree(sourcePath, fullPath))
                {
                    return directory;
                }

                var movedPath = MovePath(sourcePath, targetPath, fullPath);
                var movedRelativePath = NormalizeRelativeDirectoryPath(
                    Path.GetRelativePath(updatedFolderPath, movedPath));
                return directory with
                {
                    RelativePath = movedRelativePath,
                    Name = Path.GetFileName(movedPath)
                };
            }).OrderBy(directory => directory.FolderId)
                .ThenBy(directory => directory.RelativePath, PathComparer)
                .ToArray();
            var updated = ReplaceFolder(catalog with
            {
                Assets = updatedAssets,
                Directories = updatedDirectories
            }, updatedFolder);

            try
            {
                await _store.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
                return updated;
            }
            catch (Exception exception)
            {
                await RollBackMoveAndRethrowAsync(
                    () => Directory.Move(targetPath, sourcePath),
                    catalog,
                    exception).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LocalAssetCatalog> CreateSubdirectoryAsync(
        Guid folderId,
        string parentRelativePath,
        string name,
        CancellationToken cancellationToken = default)
    {
        var normalizedParentPath = NormalizeIndexedRelativePath(parentRelativePath);
        var normalizedName = NormalizeLeafName(name, nameof(name));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var catalog = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            var folder = catalog.Folders.SingleOrDefault(candidate => candidate.Id == folderId)
                         ?? throw new KeyNotFoundException($"Indexed folder '{folderId}' does not exist.");
            EnsureIndexedDirectory(catalog, folder, normalizedParentPath);

            var parentPath = normalizedParentPath.Length == 0
                ? folder.Path
                : NormalizeFolderPath(Path.Combine(folder.Path, normalizedParentPath));
            if (!Directory.Exists(parentPath))
            {
                throw new DirectoryNotFoundException($"父文件夹不存在：{parentPath}");
            }

            var targetPath = NormalizeFolderPath(Path.Combine(parentPath, normalizedName));
            if (File.Exists(targetPath) || Directory.Exists(targetPath))
            {
                throw new IOException("同名文件或文件夹已经存在。");
            }

            Directory.CreateDirectory(targetPath);
            var relativeTargetPath = NormalizeRelativeDirectoryPath(
                Path.GetRelativePath(folder.Path, targetPath));
            var updated = catalog with
            {
                Directories = catalog.Directories
                    .Append(new IndexedDirectory(
                        folder.Id,
                        relativeTargetPath,
                        normalizedName,
                        IndexedDirectoryAvailability.Available))
                    .OrderBy(directory => directory.FolderId)
                    .ThenBy(directory => directory.RelativePath, PathComparer)
                    .ToArray()
            };

            try
            {
                await _store.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
                return updated;
            }
            catch (Exception exception)
            {
                await RollBackMoveAndRethrowAsync(
                    () => Directory.Delete(targetPath, recursive: false),
                    catalog,
                    exception).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LocalAssetCatalog> RecycleAssetAsync(
        Guid assetId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var catalog = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            var asset = catalog.Assets.SingleOrDefault(candidate => candidate.Id == assetId)
                        ?? throw new KeyNotFoundException($"Local asset '{assetId}' does not exist.");
            await _recycleBin.RecycleFileAsync(asset.FullPath, cancellationToken).ConfigureAwait(false);

            var updated = catalog with
            {
                Assets = catalog.Assets.Where(candidate => candidate.Id != asset.Id).ToArray()
            };
            try
            {
                await _store.SaveAsync(updated, CancellationToken.None).ConfigureAwait(false);
                return updated;
            }
            catch (Exception saveException)
            {
                return await ReconcileRecycledAssetAsync(asset, saveException).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LocalAssetCatalog> RecycleDirectoryAsync(
        Guid folderId,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        var normalizedRelativePath = NormalizeIndexedRelativePath(relativePath);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var catalog = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            var folder = catalog.Folders.SingleOrDefault(candidate => candidate.Id == folderId)
                         ?? throw new KeyNotFoundException($"Indexed folder '{folderId}' does not exist.");
            EnsureIndexedDirectory(catalog, folder, normalizedRelativePath);
            var directoryPath = normalizedRelativePath.Length == 0
                ? folder.Path
                : NormalizeFolderPath(Path.Combine(folder.Path, normalizedRelativePath));
            await _recycleBin.RecycleDirectoryAsync(directoryPath, cancellationToken)
                .ConfigureAwait(false);

            LocalAssetCatalog updated;
            if (normalizedRelativePath.Length == 0)
            {
                updated = catalog with
                {
                    Folders = catalog.Folders.Where(candidate => candidate.Id != folder.Id).ToArray(),
                    Assets = catalog.Assets.Where(asset => asset.FolderId != folder.Id).ToArray(),
                    Directories = catalog.Directories
                        .Where(directory => directory.FolderId != folder.Id)
                        .ToArray()
                };
            }
            else
            {
                updated = catalog with
                {
                    Assets = catalog.Assets.Where(asset =>
                        asset.FolderId != folder.Id ||
                        !IsPathInMovedTree(directoryPath, asset.FullPath)).ToArray(),
                    Directories = catalog.Directories.Where(directory =>
                    {
                        if (directory.FolderId != folder.Id)
                        {
                            return true;
                        }

                        var fullPath = NormalizeFolderPath(
                            Path.Combine(folder.Path, directory.RelativePath));
                        return !IsPathInMovedTree(directoryPath, fullPath);
                    }).ToArray()
                };
            }

            await _store.SaveAsync(updated, CancellationToken.None).ConfigureAwait(false);
            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static LocalAssetIndexResult BuildOfflineResult(
        LocalAssetCatalog catalog,
        IndexedFolder folder,
        DateTimeOffset now)
    {
        var offlineFolder = folder with
        {
            Availability = IndexedFolderAvailability.Offline,
            LastScanAtUtc = now
        };
        var assets = catalog.Assets.Select(asset => asset.FolderId == folder.Id
            ? asset with { Availability = LocalAssetAvailability.OfflineStorage }
            : asset).ToArray();
        var directories = catalog.Directories.Select(directory => directory.FolderId == folder.Id
            ? directory with { Availability = IndexedDirectoryAvailability.Offline }
            : directory).ToArray();
        var updated = ReplaceFolder(catalog with
        {
            Assets = assets,
            Directories = directories
        }, offlineFolder);

        return new LocalAssetIndexResult(updated, offlineFolder, 0, []);
    }

    private async Task<LocalAssetCatalog> ReconcileRecycledAssetAsync(
        LocalAsset recycledAsset,
        Exception originalSaveException)
    {
        try
        {
            var latest = await _store.LoadAsync(CancellationToken.None).ConfigureAwait(false);
            var reconciled = latest with
            {
                Assets = latest.Assets
                    .Where(candidate => candidate.Id != recycledAsset.Id)
                    .ToArray()
            };
            await _store.SaveAsync(reconciled, CancellationToken.None).ConfigureAwait(false);
            return reconciled;
        }
        catch (Exception reconciliationException)
        {
            throw new AggregateException(
                $"文件已移入 Windows 回收站，但本地素材索引未能完成对账。请刷新素材库；" +
                $"若需要恢复文件，请从 Windows 回收站还原到原路径：{recycledAsset.FullPath}",
                originalSaveException,
                reconciliationException);
        }
    }

    private static LocalAssetIndexResult BuildMissingFolderResult(
        LocalAssetCatalog catalog,
        IndexedFolder folder,
        DateTimeOffset now)
    {
        var missingFolder = folder with
        {
            Availability = IndexedFolderAvailability.Missing,
            LastScanAtUtc = now
        };
        var updated = ReplaceFolder(catalog with
        {
            Assets = catalog.Assets.Where(asset => asset.FolderId != folder.Id).ToArray(),
            Directories = catalog.Directories
                .Where(directory => directory.FolderId != folder.Id)
                .ToArray()
        }, missingFolder);

        return new LocalAssetIndexResult(updated, missingFolder, 0, []);
    }

    private static LocalAssetIndexResult ScanFolder(
        LocalAssetCatalog catalog,
        IndexedFolder folder,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var issues = new List<LocalAssetIndexIssue>();
        var priorAssets = new Dictionary<string, LocalAsset>(PathComparer);
        foreach (var asset in catalog.Assets.Where(asset => asset.FolderId == folder.Id))
        {
            priorAssets.TryAdd(NormalizeFilePath(asset.FullPath), asset);
        }

        var priorDirectories = new Dictionary<string, IndexedDirectory>(PathComparer);
        foreach (var directory in catalog.Directories.Where(directory => directory.FolderId == folder.Id))
        {
            priorDirectories.TryAdd(
                NormalizeRelativeDirectoryPath(directory.RelativePath),
                directory);
        }

        var foundPaths = new HashSet<string>(PathComparer);
        var scannedAssets = new List<LocalAsset>();
        var scannedDirectories = new Dictionary<string, IndexedDirectory>(PathComparer);
        var unreadableDirectoryRoots = new HashSet<string>(PathComparer);
        var directories = new Stack<string>();
        directories.Push(folder.Path);

        while (directories.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                foreach (var filePath in Directory.EnumerateFiles(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!MediaExtensionClassifier.TryClassify(filePath, out var mediaType))
                    {
                        continue;
                    }

                    try
                    {
                        var normalizedFilePath = NormalizeFilePath(filePath);
                        var file = new FileInfo(normalizedFilePath);
                        if (!file.Exists)
                        {
                            continue;
                        }

                        priorAssets.TryGetValue(normalizedFilePath, out var prior);
                        foundPaths.Add(normalizedFilePath);
                        scannedAssets.Add(new LocalAsset(
                            prior?.Id ?? Guid.NewGuid(),
                            folder.Id,
                            normalizedFilePath,
                            Path.GetRelativePath(folder.Path, normalizedFilePath),
                            file.Name,
                            MediaExtensionClassifier.NormalizeExtension(file.Extension),
                            mediaType,
                            file.Length,
                            new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero),
                            prior?.AddedAtUtc ?? now,
                            LocalAssetAvailability.Available,
                            prior?.Tags ?? []));
                    }
                    catch (Exception exception) when (IsRecoverableFileSystemException(exception))
                    {
                        issues.Add(new LocalAssetIndexIssue(filePath, exception.Message));
                    }
                }
            }
            catch (Exception exception) when (IsRecoverableFileSystemException(exception))
            {
                issues.Add(new LocalAssetIndexIssue(directory, exception.Message));
                MarkDirectoryUnavailable(directory);
                unreadableDirectoryRoots.Add(NormalizeFolderPath(directory));
            }

            try
            {
                foreach (var childDirectory in Directory.EnumerateDirectories(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var normalizedChildDirectory = NormalizeFolderPath(childDirectory);
                    try
                    {
                        if ((File.GetAttributes(normalizedChildDirectory) & FileAttributes.ReparsePoint) == 0)
                        {
                            AddDirectory(
                                normalizedChildDirectory,
                                IndexedDirectoryAvailability.Available);
                            directories.Push(normalizedChildDirectory);
                        }
                    }
                    catch (Exception exception) when (IsRecoverableFileSystemException(exception))
                    {
                        issues.Add(new LocalAssetIndexIssue(normalizedChildDirectory, exception.Message));
                        AddDirectory(
                            normalizedChildDirectory,
                            IndexedDirectoryAvailability.TemporarilyUnavailable);
                        unreadableDirectoryRoots.Add(normalizedChildDirectory);
                    }
                }
            }
            catch (Exception exception) when (IsRecoverableFileSystemException(exception))
            {
                issues.Add(new LocalAssetIndexIssue(directory, exception.Message));
                MarkDirectoryUnavailable(directory);
                unreadableDirectoryRoots.Add(NormalizeFolderPath(directory));
            }
        }

        foreach (var priorDirectory in priorDirectories.Values)
        {
            var relativePath = NormalizeRelativeDirectoryPath(priorDirectory.RelativePath);
            if (scannedDirectories.ContainsKey(relativePath))
            {
                continue;
            }

            var fullPath = NormalizeFolderPath(Path.Combine(folder.Path, relativePath));
            if (unreadableDirectoryRoots.Any(root =>
                    PathComparer.Equals(root, fullPath) || IsStrictDescendantPath(root, fullPath)))
            {
                scannedDirectories[relativePath] = priorDirectory with
                {
                    RelativePath = relativePath,
                    Availability = IndexedDirectoryAvailability.TemporarilyUnavailable
                };
            }
        }

        scannedAssets.AddRange(priorAssets
            .Where(pair => !foundPaths.Contains(pair.Key) &&
                           unreadableDirectoryRoots.Any(root =>
                               PathComparer.Equals(root, pair.Key) ||
                               IsStrictDescendantPath(root, pair.Key)))
            .Select(pair => pair.Value with
            {
                Availability = LocalAssetAvailability.TemporarilyUnavailable
            }));

        var updatedFolder = folder with
        {
            Availability = issues.Count == 0
                ? IndexedFolderAvailability.Online
                : IndexedFolderAvailability.PartiallyAvailable,
            LastScanAtUtc = now
        };
        var otherAssets = catalog.Assets.Where(asset => asset.FolderId != folder.Id);
        var otherDirectories = catalog.Directories.Where(directory => directory.FolderId != folder.Id);
        var updatedCatalog = ReplaceFolder(catalog with
        {
            Assets = otherAssets
                .Concat(scannedAssets)
                .OrderBy(asset => asset.FullPath, PathComparer)
                .ToArray(),
            Directories = otherDirectories
                .Concat(scannedDirectories.Values)
                .OrderBy(directory => directory.FolderId)
                .ThenBy(directory => directory.RelativePath, PathComparer)
                .ToArray()
        }, updatedFolder);

        return new LocalAssetIndexResult(
            updatedCatalog,
            updatedFolder,
            scannedAssets.Count(asset => asset.IsAvailable),
            issues);

        void AddDirectory(string fullPath, IndexedDirectoryAvailability availability)
        {
            var relativePath = NormalizeRelativeDirectoryPath(
                Path.GetRelativePath(folder.Path, fullPath));
            if (relativePath.Length == 0)
            {
                return;
            }

            scannedDirectories[relativePath] = new IndexedDirectory(
                folder.Id,
                relativePath,
                Path.GetFileName(fullPath),
                availability);
        }

        void MarkDirectoryUnavailable(string fullPath)
        {
            var normalizedPath = NormalizeFolderPath(fullPath);
            if (!PathComparer.Equals(normalizedPath, folder.Path))
            {
                AddDirectory(normalizedPath, IndexedDirectoryAvailability.TemporarilyUnavailable);
            }
        }
    }

    private static LocalAssetCatalog MergeDescendantRoots(
        LocalAssetCatalog catalog,
        IndexedFolder parentFolder,
        IReadOnlyCollection<IndexedFolder> descendantFolders)
    {
        var descendantById = descendantFolders.ToDictionary(folder => folder.Id);
        var mergedFolderIds = descendantById.Keys.Append(parentFolder.Id).ToHashSet();
        var mergedAssets = new Dictionary<string, LocalAsset>(PathComparer);
        foreach (var asset in catalog.Assets.Where(asset => mergedFolderIds.Contains(asset.FolderId)))
        {
            var remapped = asset.FolderId == parentFolder.Id
                ? asset
                : asset with
                {
                    FolderId = parentFolder.Id,
                    RelativePath = Path.GetRelativePath(parentFolder.Path, asset.FullPath)
                };
            mergedAssets.TryAdd(NormalizeFilePath(remapped.FullPath), remapped);
        }

        var mergedDirectories = new Dictionary<string, IndexedDirectory>(PathComparer);
        foreach (var directory in catalog.Directories.Where(directory =>
                     mergedFolderIds.Contains(directory.FolderId)))
        {
            if (directory.FolderId == parentFolder.Id)
            {
                AddMergedDirectory(directory.RelativePath, directory.Availability);
                continue;
            }

            var childFolder = descendantById[directory.FolderId];
            var fullPath = Path.Combine(childFolder.Path, directory.RelativePath);
            AddMergedDirectory(
                Path.GetRelativePath(parentFolder.Path, fullPath),
                directory.Availability);
        }

        foreach (var childFolder in descendantFolders)
        {
            AddMergedDirectory(
                Path.GetRelativePath(parentFolder.Path, childFolder.Path),
                childFolder.Availability switch
                {
                    IndexedFolderAvailability.Online => IndexedDirectoryAvailability.Available,
                    IndexedFolderAvailability.PartiallyAvailable =>
                        IndexedDirectoryAvailability.TemporarilyUnavailable,
                    _ => IndexedDirectoryAvailability.Offline
                });
        }

        return catalog with
        {
            Folders = catalog.Folders
                .Where(folder => !descendantById.ContainsKey(folder.Id))
                .ToArray(),
            Assets = catalog.Assets
                .Where(asset => !mergedFolderIds.Contains(asset.FolderId))
                .Concat(mergedAssets.Values)
                .OrderBy(asset => asset.FullPath, PathComparer)
                .ToArray(),
            Directories = catalog.Directories
                .Where(directory => !mergedFolderIds.Contains(directory.FolderId))
                .Concat(mergedDirectories.Values)
                .OrderBy(directory => directory.FolderId)
                .ThenBy(directory => directory.RelativePath, PathComparer)
                .ToArray()
        };

        void AddMergedDirectory(string relativePath, IndexedDirectoryAvailability availability)
        {
            var normalizedRelativePath = NormalizeRelativeDirectoryPath(relativePath);
            if (normalizedRelativePath.Length == 0)
            {
                return;
            }

            mergedDirectories.TryAdd(normalizedRelativePath, new IndexedDirectory(
                parentFolder.Id,
                normalizedRelativePath,
                Path.GetFileName(normalizedRelativePath),
                availability));
        }
    }

    private static LocalAssetCatalog ReplaceFolder(
        LocalAssetCatalog catalog,
        IndexedFolder replacement)
    {
        var folders = catalog.Folders
            .Where(folder => folder.Id != replacement.Id)
            .Append(replacement)
            .OrderBy(folder => folder.Path, PathComparer)
            .ToArray();

        return catalog with { Folders = folders };
    }

    private static void EnsureIndexedDirectory(
        LocalAssetCatalog catalog,
        IndexedFolder folder,
        string relativePath)
    {
        if (relativePath.Length == 0)
        {
            return;
        }

        var fullPath = NormalizeFolderPath(Path.Combine(folder.Path, relativePath));
        if (!IsStrictDescendantPath(folder.Path, fullPath))
        {
            throw new ArgumentException("文件夹路径必须位于已连接的素材目录中。", nameof(relativePath));
        }

        if (!catalog.Directories.Any(directory =>
                directory.FolderId == folder.Id &&
                PathComparer.Equals(
                    NormalizeRelativeDirectoryPath(directory.RelativePath),
                    relativePath)))
        {
            throw new KeyNotFoundException($"Indexed directory '{relativePath}' does not exist.");
        }
    }

    private async Task RollBackMoveAndRethrowAsync(
        Action rollBackPhysicalChange,
        LocalAssetCatalog originalCatalog,
        Exception originalException)
    {
        var failures = new List<Exception> { originalException };
        try
        {
            rollBackPhysicalChange();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        try
        {
            await _store.SaveAsync(originalCatalog, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        if (failures.Count > 1)
        {
            throw new AggregateException("文件系统操作失败，并且未能完整回滚。", failures);
        }

        ExceptionDispatchInfo.Capture(originalException).Throw();
    }

    private static void ThrowAfterBatchRenameRollback(
        Exception originalException,
        IReadOnlyList<BatchRenamePlan> staged,
        bool fromTargets)
    {
        var failures = new List<Exception> { originalException };
        for (var index = staged.Count - 1; index >= 0; index--)
        {
            var plan = staged[index];
            try
            {
                var currentPath = fromTargets ? plan.TargetPath : plan.TemporaryPath;
                if (File.Exists(currentPath) && !File.Exists(plan.SourcePath))
                {
                    File.Move(currentPath, plan.SourcePath);
                }
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }

            if (DescribeBatchRenameRollbackFailure(
                    plan.SourcePath,
                    plan.TemporaryPath,
                    plan.TargetPath) is { } stateFailure)
            {
                failures.Add(stateFailure);
            }
        }

        ThrowBatchRenameFailure(failures);
    }

    private static void ThrowAfterBatchRenameRollback(
        Exception originalException,
        IReadOnlyList<BatchRenamePlan> plans,
        IReadOnlyList<BatchRenamePlan> finalized)
    {
        ThrowBatchRenameFailure(RollBackBatchRename(plans, finalized, originalException));
    }

    private static List<Exception> RollBackFinalizedBatchRename(
        IReadOnlyList<BatchRenamePlan> plans,
        Exception originalException) =>
        RollBackBatchRename(plans, plans, originalException);

    private static List<Exception> RollBackBatchRename(
        IReadOnlyList<BatchRenamePlan> plans,
        IReadOnlyList<BatchRenamePlan> finalized,
        Exception originalException)
    {
        var failures = new List<Exception> { originalException };
        foreach (var plan in finalized.Reverse())
        {
            try
            {
                if (File.Exists(plan.TargetPath) && !File.Exists(plan.TemporaryPath))
                {
                    File.Move(plan.TargetPath, plan.TemporaryPath);
                }
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        foreach (var plan in plans.Reverse())
        {
            try
            {
                if (File.Exists(plan.TemporaryPath) && !File.Exists(plan.SourcePath))
                {
                    File.Move(plan.TemporaryPath, plan.SourcePath);
                }
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        foreach (var plan in plans)
        {
            if (DescribeBatchRenameRollbackFailure(
                    plan.SourcePath,
                    plan.TemporaryPath,
                    plan.TargetPath) is { } stateFailure)
            {
                failures.Add(stateFailure);
            }
        }

        return failures;
    }

    internal static IOException? DescribeBatchRenameRollbackFailure(
        string sourcePath,
        string temporaryPath,
        string targetPath)
    {
        var sourceRestored = File.Exists(sourcePath);
        var temporaryRetained = File.Exists(temporaryPath);
        if (sourceRestored && !temporaryRetained)
        {
            return null;
        }

        return new IOException(
            "批量重命名未能完整恢复文件。" +
            $"原路径：{sourcePath}；临时恢复路径：{temporaryPath}；目标路径：{targetPath}。" +
            "请勿删除仍存在的临时文件，并将其手动移回原路径。");
    }

    private static void ThrowBatchRenameFailure(IReadOnlyList<Exception> failures)
    {
        if (failures.Count > 1)
        {
            throw new AggregateException("批量重命名失败，并且未能完整回滚。", failures);
        }

        ExceptionDispatchInfo.Capture(failures[0]).Throw();
    }

    private static void ThrowIfTargetExists(string sourcePath, string targetPath)
    {
        if ((File.Exists(targetPath) || Directory.Exists(targetPath)) &&
            !PathComparer.Equals(sourcePath, targetPath))
        {
            throw new IOException("同名文件或文件夹已经存在。");
        }
    }

    private sealed record BatchRenamePlan(
        LocalAsset Asset,
        string SourcePath,
        string TemporaryPath,
        string TargetPath);

    private sealed record AssetMovePlan(LocalAsset Asset, string SourcePath, string TargetPath);

    private static void MoveFileSafely(string sourcePath, string targetPath)
    {
        var sourceRoot = Path.GetPathRoot(sourcePath);
        var targetRoot = Path.GetPathRoot(targetPath);
        if (PathComparer.Equals(sourceRoot, targetRoot))
        {
            File.Move(sourcePath, targetPath, overwrite: false);
            return;
        }

        var temporaryPath = targetPath + $".ial-move-{Guid.NewGuid():N}.tmp";
        try
        {
            File.Copy(sourcePath, temporaryPath, overwrite: false);
            File.Move(temporaryPath, targetPath, overwrite: false);
            try
            {
                File.Delete(sourcePath);
            }
            catch
            {
                File.Delete(targetPath);
                throw;
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void ThrowAfterAssetMoveRollback(
        Exception originalException,
        IReadOnlyList<AssetMovePlan> moved)
    {
        var failures = new List<Exception> { originalException };
        for (var index = moved.Count - 1; index >= 0; index--)
        {
            var plan = moved[index];
            try
            {
                if (File.Exists(plan.TargetPath) && !File.Exists(plan.SourcePath))
                {
                    MoveFileSafely(plan.TargetPath, plan.SourcePath);
                }
            }
            catch (Exception rollbackException)
            {
                failures.Add(rollbackException);
            }
        }

        if (failures.Count > 1)
        {
            throw new AggregateException("移动本地素材失败，并且未能完整回滚。", failures);
        }

        ExceptionDispatchInfo.Capture(originalException).Throw();
    }

    private static bool IsPathInMovedTree(string rootPath, string candidatePath) =>
        PathComparer.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidatePath))) ||
        IsStrictDescendantPath(rootPath, candidatePath);

    private static string MovePath(string sourceRoot, string targetRoot, string candidatePath)
    {
        var suffix = Path.GetRelativePath(sourceRoot, candidatePath);
        if (Path.IsPathRooted(suffix) || suffix == ".." ||
            suffix.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            suffix.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("要更新的索引路径不在重命名目录中。");
        }

        return NormalizeFilePath(Path.Combine(targetRoot, suffix));
    }

    private static string NormalizeLeafName(string name, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name, parameterName);
        var normalized = name.Trim();
        if (normalized is "." or ".." ||
            normalized.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            normalized.Contains(Path.DirectorySeparatorChar) ||
            normalized.Contains(Path.AltDirectorySeparatorChar) ||
            Path.GetFileName(normalized) != normalized)
        {
            throw new ArgumentException("名称只能包含一个有效的文件名或文件夹名。", parameterName);
        }

        if (OperatingSystem.IsWindows())
        {
            if (normalized.EndsWith(' ') || normalized.EndsWith('.'))
            {
                throw new ArgumentException("Windows 文件名不能以空格或句点结尾。", parameterName);
            }

            var stem = Path.GetFileNameWithoutExtension(normalized);
            if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
                IsNumberedDeviceName(stem, "COM") ||
                IsNumberedDeviceName(stem, "LPT"))
            {
                throw new ArgumentException("该名称是 Windows 保留的设备名称。", parameterName);
            }
        }

        return normalized;

        static bool IsNumberedDeviceName(string value, string prefix) =>
            value.Length == 4 &&
            value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            value[3] is >= '1' and <= '9';
    }

    private static string NormalizeIndexedRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        if (Path.IsPathRooted(path))
        {
            throw new ArgumentException("文件夹索引路径必须是相对路径。", nameof(path));
        }

        var segments = path.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or ".."))
        {
            throw new ArgumentException("文件夹索引路径不能跳出素材目录。", nameof(path));
        }

        return NormalizeRelativeDirectoryPath(string.Join(Path.DirectorySeparatorChar, segments));
    }

    private static string NormalizeFolderPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
    }

    private static string NormalizeFilePath(string path) => Path.GetFullPath(path);

    private static string NormalizeRelativeDirectoryPath(string path)
    {
        if (string.IsNullOrEmpty(path) || path == ".")
        {
            return string.Empty;
        }

        return Path.TrimEndingDirectorySeparator(
            path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar));
    }

    private static bool IsStrictDescendantPath(string parentPath, string candidatePath)
    {
        var relativePath = Path.GetRelativePath(parentPath, candidatePath);
        if (relativePath == "." || Path.IsPathRooted(relativePath))
        {
            return false;
        }

        return relativePath != ".." &&
               !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
               !relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static bool IsContainingStorageAvailable(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            return !string.IsNullOrWhiteSpace(root) && Directory.Exists(root);
        }
        catch (Exception exception) when (IsRecoverableFileSystemException(exception))
        {
            return false;
        }
    }

    private static bool DetectExternalStorage(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            return !string.IsNullOrEmpty(root) &&
                   new DriveInfo(root).DriveType == DriveType.Removable;
        }
        catch (Exception exception) when (IsRecoverableFileSystemException(exception))
        {
            return false;
        }
    }

    private static bool IsRecoverableFileSystemException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or System.Security.SecurityException;

    public void Dispose() => _gate.Dispose();
}
