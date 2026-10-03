using InternalAssetLibrary.Client.Core.LocalAssets;
using InternalAssetLibrary.Client.Core.Markers;
using InternalAssetLibrary.Contracts;

internal static class LocalAssetFileSystemMutationSelfTests
{
    public static void RenamesPreserveIdentityTagsAndMarkers() =>
        RenamesPreserveIdentityTagsAndMarkersAsync().GetAwaiter().GetResult();

    public static void FailedCatalogSavesRollBackPhysicalMoves() =>
        FailedCatalogSavesRollBackPhysicalMovesAsync().GetAwaiter().GetResult();

    public static void RecycleBinOperationsStayAtomic() =>
        RecycleBinOperationsStayAtomicAsync().GetAwaiter().GetResult();

    public static void NamesAndCollisionsAreValidated() =>
        NamesAndCollisionsAreValidatedAsync().GetAwaiter().GetResult();

    public static void BatchTagsAndRenamesStayAtomic() =>
        BatchTagsAndRenamesStayAtomicAsync().GetAwaiter().GetResult();

    public static void FailedBatchRenameRollsBackEveryFile() =>
        FailedBatchRenameRollsBackEveryFileAsync().GetAwaiter().GetResult();

    public static void RecycledAssetsReconcileCatalogSaveFailures() =>
        RecycledAssetsReconcileCatalogSaveFailuresAsync().GetAwaiter().GetResult();

    public static void AtomicCopiesNeverExposePartialOrOverwriteExistingFiles() =>
        AtomicCopiesNeverExposePartialOrOverwriteExistingFilesAsync().GetAwaiter().GetResult();

    public static void BatchRenameRollbackDiagnosticsExposeRecoveryPaths() =>
        BatchRenameRollbackDiagnosticsExposeRecoveryPathsCore();

    public static void MovesPreserveIdentityTagsAndBatchAtomicity() =>
        MovesPreserveIdentityTagsAndBatchAtomicityAsync().GetAwaiter().GetResult();

    public static void FailedMoveCatalogSavesRollBackEveryFile() =>
        FailedMoveCatalogSavesRollBackEveryFileAsync().GetAwaiter().GetResult();

    private static async Task MovesPreserveIdentityTagsAndBatchAtomicityAsync()
    {
        var container = Directory.CreateTempSubdirectory("ial-move-assets-").FullName;
        var sourceRoot = Path.Combine(container, "source");
        var firstDirectory = Path.Combine(sourceRoot, "first");
        var secondDirectory = Path.Combine(sourceRoot, "second");
        var targetRoot = Path.Combine(container, "target");
        var targetDirectory = Path.Combine(targetRoot, "selected");
        var firstAudio = Path.Combine(firstDirectory, "same.wav");
        var collidingAudio = Path.Combine(secondDirectory, "same.wav");
        var video = Path.Combine(secondDirectory, "clip.mp4");

        try
        {
            Directory.CreateDirectory(firstDirectory);
            Directory.CreateDirectory(secondDirectory);
            Directory.CreateDirectory(targetDirectory);
            await File.WriteAllBytesAsync(firstAudio, [1, 2, 3]);
            await File.WriteAllBytesAsync(collidingAudio, [4, 5, 6]);
            await File.WriteAllBytesAsync(video, [7, 8, 9]);

            var store = new InMemoryLocalAssetCatalogStore();
            using var service = new LocalAssetIndexService(store, recycleBin: new RecordingRecycleBin());
            var sourceIndex = await service.IndexFolderAsync(sourceRoot, false);
            var targetIndex = await service.IndexFolderAsync(targetRoot, false);
            var catalog = targetIndex.Catalog;
            var first = catalog.Assets.Single(asset => asset.FullPath == firstAudio);
            var collision = catalog.Assets.Single(asset => asset.FullPath == collidingAudio);
            var clip = catalog.Assets.Single(asset => asset.FullPath == video);
            catalog = await service.SetTagsAsync(first.Id, ["保留标签"]);

            await ThrowsAsync<IOException>(() => service.MoveAssetsAsync(
                [first.Id, collision.Id],
                targetIndex.Folder.Id,
                "selected"));
            True(File.Exists(firstAudio));
            True(File.Exists(collidingAudio));
            False(File.Exists(Path.Combine(targetDirectory, "same.wav")));

            var moved = await service.MoveAssetsAsync(
                [first.Id, clip.Id],
                targetIndex.Folder.Id,
                "selected");
            var movedFirst = moved.Assets.Single(asset => asset.Id == first.Id);
            var movedClip = moved.Assets.Single(asset => asset.Id == clip.Id);
            Equal(first.Id, movedFirst.Id);
            Equal(clip.Id, movedClip.Id);
            SequenceEqual(["保留标签"], movedFirst.Tags);
            Equal(Path.Combine(targetDirectory, "same.wav"), movedFirst.FullPath);
            Equal(Path.Combine(targetDirectory, "clip.mp4"), movedClip.FullPath);
            True(File.Exists(movedFirst.FullPath));
            True(File.Exists(movedClip.FullPath));
            False(File.Exists(firstAudio));
            False(File.Exists(video));
            True(File.Exists(collidingAudio));
        }
        finally
        {
            if (Directory.Exists(container))
            {
                Directory.Delete(container, true);
            }
        }
    }

    private static async Task FailedMoveCatalogSavesRollBackEveryFileAsync()
    {
        var container = Directory.CreateTempSubdirectory("ial-move-assets-rollback-").FullName;
        var sourceRoot = Path.Combine(container, "source");
        var targetRoot = Path.Combine(container, "target");
        var targetDirectory = Path.Combine(targetRoot, "selected");
        var audio = Path.Combine(sourceRoot, "audio.wav");
        var video = Path.Combine(sourceRoot, "video.mp4");

        try
        {
            Directory.CreateDirectory(sourceRoot);
            Directory.CreateDirectory(targetDirectory);
            await File.WriteAllBytesAsync(audio, [1, 2, 3]);
            await File.WriteAllBytesAsync(video, [4, 5, 6]);
            var store = new FailNextSaveCatalogStore();
            using var service = new LocalAssetIndexService(store, recycleBin: new RecordingRecycleBin());
            _ = await service.IndexFolderAsync(sourceRoot, false);
            var targetIndex = await service.IndexFolderAsync(targetRoot, false);
            var before = await service.GetCatalogAsync();
            var ids = before.Assets.Select(asset => asset.Id).ToArray();

            store.FailNextSave();
            await ThrowsAsync<IOException>(() => service.MoveAssetsAsync(
                ids,
                targetIndex.Folder.Id,
                "selected"));

            True(File.Exists(audio));
            True(File.Exists(video));
            False(File.Exists(Path.Combine(targetDirectory, "audio.wav")));
            False(File.Exists(Path.Combine(targetDirectory, "video.mp4")));
            var after = await service.GetCatalogAsync();
            SequenceEqual(
                before.Assets.OrderBy(asset => asset.Id),
                after.Assets.OrderBy(asset => asset.Id));
        }
        finally
        {
            if (Directory.Exists(container))
            {
                Directory.Delete(container, true);
            }
        }
    }

    private static async Task RenamesPreserveIdentityTagsAndMarkersAsync()
    {
        var container = Directory.CreateTempSubdirectory("ial-rename-").FullName;
        var root = Path.Combine(container, "library");
        var child = Path.Combine(root, "child");
        var source = Path.Combine(child, "source.wav");
        var markerPath = Path.Combine(container, "markers.json");

        try
        {
            Directory.CreateDirectory(child);
            await File.WriteAllBytesAsync(source, [1, 2, 3, 4]);
            var store = new InMemoryLocalAssetCatalogStore();
            using var service = new LocalAssetIndexService(store, recycleBin: new RecordingRecycleBin());
            using var markers = new LocalMarkerService(markerPath);

            var indexed = await service.IndexFolderAsync(root, false);
            var original = indexed.Catalog.Assets.Single();
            var tagged = (await service.SetTagsAsync(original.Id, ["常用", "音效"])).Assets.Single();
            var markerSet = await markers.CreateAsync(original.Id, "剪辑点", TimeSpan.FromSeconds(5));
            await markers.AddMarkerAsync(
                original.Id,
                markerSet.Id,
                new UpsertMarkerRequest(TimeSpan.FromSeconds(1), "落点", "保留"));

            var fileRenamed = await service.RenameAssetAsync(original.Id, "renamed.wav");
            var afterFileRename = fileRenamed.Assets.Single();
            Equal(tagged.Id, afterFileRename.Id);
            Equal(tagged.AddedAtUtc, afterFileRename.AddedAtUtc);
            SequenceEqual(tagged.Tags, afterFileRename.Tags);
            True(File.Exists(Path.Combine(child, "renamed.wav")));
            False(File.Exists(source));
            Equal(1, (await markers.ListAsync(original.Id)).Single().MarkerCount);

            var directoryRenamed = await service.RenameDirectoryAsync(
                indexed.Folder.Id,
                "child",
                "renamed-child");
            var afterDirectoryRename = directoryRenamed.Assets.Single();
            Equal(tagged.Id, afterDirectoryRename.Id);
            SequenceEqual(tagged.Tags, afterDirectoryRename.Tags);
            Equal(
                Path.Combine("renamed-child", "renamed.wav"),
                afterDirectoryRename.RelativePath);
            True(File.Exists(Path.Combine(root, "renamed-child", "renamed.wav")));
            Equal(1, (await markers.ListAsync(original.Id)).Single().MarkerCount);

            var rootRenamed = await service.RenameDirectoryAsync(
                indexed.Folder.Id,
                string.Empty,
                "renamed-library");
            var renamedRootPath = Path.Combine(container, "renamed-library");
            Equal(renamedRootPath, rootRenamed.Folders.Single().Path);
            Equal(tagged.Id, rootRenamed.Assets.Single().Id);
            True(File.Exists(Path.Combine(renamedRootPath, "renamed-child", "renamed.wav")));
            Equal(1, (await markers.ListAsync(original.Id)).Single().MarkerCount);
        }
        finally
        {
            if (Directory.Exists(container))
            {
                Directory.Delete(container, true);
            }
        }
    }

    private static async Task FailedCatalogSavesRollBackPhysicalMovesAsync()
    {
        var root = Directory.CreateTempSubdirectory("ial-rename-rollback-").FullName;
        var child = Path.Combine(root, "child");
        var source = Path.Combine(child, "source.mp3");

        try
        {
            Directory.CreateDirectory(child);
            await File.WriteAllBytesAsync(source, [1, 2, 3]);
            var store = new FailNextSaveCatalogStore();
            using var service = new LocalAssetIndexService(store, recycleBin: new RecordingRecycleBin());
            var indexed = await service.IndexFolderAsync(root, false);
            var original = indexed.Catalog.Assets.Single();
            await service.SetTagsAsync(original.Id, ["回滚标签"]);

            store.FailNextSave();
            await ThrowsAsync<IOException>(() => service.RenameAssetAsync(original.Id, "failed.mp3"));
            True(File.Exists(source));
            False(File.Exists(Path.Combine(child, "failed.mp3")));
            var afterFileFailure = await service.GetCatalogAsync();
            Equal(source, afterFileFailure.Assets.Single().FullPath);
            Equal(original.Id, afterFileFailure.Assets.Single().Id);
            SequenceEqual(["回滚标签"], afterFileFailure.Assets.Single().Tags);

            store.FailNextSave();
            await ThrowsAsync<IOException>(() => service.RenameDirectoryAsync(
                indexed.Folder.Id,
                "child",
                "failed-child"));
            True(Directory.Exists(child));
            False(Directory.Exists(Path.Combine(root, "failed-child")));
            var afterDirectoryFailure = await service.GetCatalogAsync();
            Equal(source, afterDirectoryFailure.Assets.Single().FullPath);
            Equal(original.Id, afterDirectoryFailure.Assets.Single().Id);

            store.FailNextSave();
            await ThrowsAsync<IOException>(() => service.CreateSubdirectoryAsync(
                indexed.Folder.Id,
                "child",
                "failed-new"));
            False(Directory.Exists(Path.Combine(child, "failed-new")));
            False((await service.GetCatalogAsync()).Directories.Any(directory =>
                directory.RelativePath.EndsWith("failed-new", StringComparison.Ordinal)));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static async Task RecycleBinOperationsStayAtomicAsync()
    {
        var root = Directory.CreateTempSubdirectory("ial-recycle-").FullName;
        var child = Path.Combine(root, "child");
        var nested = Path.Combine(child, "nested.wav");
        var sibling = Path.Combine(root, "sibling.wav");

        try
        {
            Directory.CreateDirectory(child);
            await File.WriteAllBytesAsync(nested, [1]);
            await File.WriteAllBytesAsync(sibling, [2]);
            var store = new InMemoryLocalAssetCatalogStore();
            var recycleBin = new RecordingRecycleBin();
            using var service = new LocalAssetIndexService(store, recycleBin: recycleBin);
            var indexed = await service.IndexFolderAsync(root, false);
            var siblingAsset = indexed.Catalog.Assets.Single(asset => asset.FullPath == sibling);

            recycleBin.FailNextOperation();
            await ThrowsAsync<IOException>(() => service.RecycleAssetAsync(siblingAsset.Id));
            Equal(2, (await service.GetCatalogAsync()).Assets.Length);
            True(File.Exists(sibling));

            var afterDirectoryRecycle = await service.RecycleDirectoryAsync(
                indexed.Folder.Id,
                "child");
            Equal(1, afterDirectoryRecycle.Assets.Length);
            Equal(siblingAsset.Id, afterDirectoryRecycle.Assets.Single().Id);
            False(afterDirectoryRecycle.Directories.Any(directory =>
                directory.RelativePath == "child" ||
                directory.RelativePath.StartsWith($"child{Path.DirectorySeparatorChar}", StringComparison.Ordinal)));
            Equal(child, recycleBin.RecycledDirectories.Single());

            var afterRootRecycle = await service.RecycleDirectoryAsync(
                indexed.Folder.Id,
                string.Empty);
            Equal(0, afterRootRecycle.Folders.Length);
            Equal(0, afterRootRecycle.Assets.Length);
            Equal(root, recycleBin.RecycledDirectories.Last());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static async Task NamesAndCollisionsAreValidatedAsync()
    {
        var root = Directory.CreateTempSubdirectory("ial-name-validation-").FullName;
        var source = Path.Combine(root, "source.mp3");
        var collision = Path.Combine(root, "collision.mp3");

        try
        {
            await File.WriteAllBytesAsync(source, [1]);
            await File.WriteAllBytesAsync(collision, [2]);
            var store = new InMemoryLocalAssetCatalogStore();
            using var service = new LocalAssetIndexService(store, recycleBin: new RecordingRecycleBin());
            var indexed = await service.IndexFolderAsync(root, false);
            var sourceAsset = indexed.Catalog.Assets.Single(asset => asset.FullPath == source);

            await ThrowsAsync<ArgumentException>(() => service.RenameAssetAsync(
                sourceAsset.Id,
                $"..{Path.DirectorySeparatorChar}outside.mp3"));
            await ThrowsAsync<InvalidOperationException>(() => service.RenameAssetAsync(
                sourceAsset.Id,
                "source.wav"));
            await ThrowsAsync<IOException>(() => service.RenameAssetAsync(
                sourceAsset.Id,
                "collision.mp3"));
            await ThrowsAsync<ArgumentException>(() => service.CreateSubdirectoryAsync(
                indexed.Folder.Id,
                string.Empty,
                ".."));
            True(File.Exists(source));
            Equal(2, (await service.GetCatalogAsync()).Assets.Length);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static async Task BatchTagsAndRenamesStayAtomicAsync()
    {
        var root = Directory.CreateTempSubdirectory("ial-batch-rename-").FullName;
        var alphaPath = Path.Combine(root, "alpha.mp3");
        var betaPath = Path.Combine(root, "beta.mp3");

        try
        {
            await File.WriteAllBytesAsync(alphaPath, [1]);
            await File.WriteAllBytesAsync(betaPath, [2]);
            var store = new InMemoryLocalAssetCatalogStore();
            using var service = new LocalAssetIndexService(store, recycleBin: new RecordingRecycleBin());
            var indexed = await service.IndexFolderAsync(root, false);
            var alpha = indexed.Catalog.Assets.Single(asset => asset.FullPath == alphaPath);
            var beta = indexed.Catalog.Assets.Single(asset => asset.FullPath == betaPath);

            var tagged = await service.AddTagsToAssetsAsync(
                [alpha.Id, beta.Id],
                ["精选", "待剪辑"]);
            True(tagged.Assets.All(asset =>
                asset.Tags.SequenceEqual(["待剪辑", "精选"], StringComparer.Ordinal)));

            var renamed = await service.RenameAssetsAsync(
            [
                new LocalAssetRenameRequest(alpha.Id, "beta.mp3"),
                new LocalAssetRenameRequest(beta.Id, "alpha.mp3")
            ]);
            var renamedAlpha = renamed.Assets.Single(asset => asset.Id == alpha.Id);
            var renamedBeta = renamed.Assets.Single(asset => asset.Id == beta.Id);
            Equal(betaPath, renamedAlpha.FullPath);
            Equal(alphaPath, renamedBeta.FullPath);
            SequenceEqual(["待剪辑", "精选"], renamedAlpha.Tags);
            SequenceEqual(["待剪辑", "精选"], renamedBeta.Tags);
            Equal((byte)1, (await File.ReadAllBytesAsync(betaPath)).Single());
            Equal((byte)2, (await File.ReadAllBytesAsync(alphaPath)).Single());
            False(Directory.EnumerateFiles(root).Any(path =>
                Path.GetFileName(path).StartsWith(".ial-rename-", StringComparison.Ordinal)));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static async Task FailedBatchRenameRollsBackEveryFileAsync()
    {
        var root = Directory.CreateTempSubdirectory("ial-batch-rollback-").FullName;
        var firstPath = Path.Combine(root, "first.mp3");
        var secondPath = Path.Combine(root, "second.wav");

        try
        {
            await File.WriteAllBytesAsync(firstPath, [1]);
            await File.WriteAllBytesAsync(secondPath, [2]);
            var store = new FailNextSaveCatalogStore();
            using var service = new LocalAssetIndexService(store, recycleBin: new RecordingRecycleBin());
            var indexed = await service.IndexFolderAsync(root, false);
            var first = indexed.Catalog.Assets.Single(asset => asset.FullPath == firstPath);
            var second = indexed.Catalog.Assets.Single(asset => asset.FullPath == secondPath);
            await service.AddTagsToAssetsAsync([first.Id, second.Id], ["回滚"]);

            store.FailNextSave();
            await ThrowsAsync<IOException>(() => service.RenameAssetsAsync(
            [
                new LocalAssetRenameRequest(first.Id, "renamed-01.mp3"),
                new LocalAssetRenameRequest(second.Id, "renamed-02.wav")
            ]));

            True(File.Exists(firstPath));
            True(File.Exists(secondPath));
            False(File.Exists(Path.Combine(root, "renamed-01.mp3")));
            False(File.Exists(Path.Combine(root, "renamed-02.wav")));
            False(Directory.EnumerateFiles(root).Any(path =>
                Path.GetFileName(path).StartsWith(".ial-rename-", StringComparison.Ordinal)));
            var catalog = await service.GetCatalogAsync();
            Equal(firstPath, catalog.Assets.Single(asset => asset.Id == first.Id).FullPath);
            Equal(secondPath, catalog.Assets.Single(asset => asset.Id == second.Id).FullPath);
            True(catalog.Assets.All(asset => asset.Tags.SequenceEqual(["回滚"])));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static async Task RecycledAssetsReconcileCatalogSaveFailuresAsync()
    {
        var firstRoot = Directory.CreateTempSubdirectory("ial-recycle-reconcile-").FullName;
        var secondRoot = Directory.CreateTempSubdirectory("ial-recycle-reconcile-fail-").FullName;
        try
        {
            var firstPath = Path.Combine(firstRoot, "recoverable.wav");
            await File.WriteAllBytesAsync(firstPath, [1, 2, 3]);
            var firstStore = new FailNextSaveCatalogStore();
            var firstRecycleBin = new RecordingRecycleBin();
            using (var firstService = new LocalAssetIndexService(firstStore, recycleBin: firstRecycleBin))
            {
                var indexed = await firstService.IndexFolderAsync(firstRoot, false);
                var asset = indexed.Catalog.Assets.Single();
                firstStore.FailNextSaves(1);

                var reconciled = await firstService.RecycleAssetAsync(asset.Id);

                False(File.Exists(firstPath));
                SequenceEqual([firstPath], firstRecycleBin.RecycledFiles);
                False(reconciled.Assets.Any(candidate => candidate.Id == asset.Id));
                False((await firstService.GetCatalogAsync()).Assets.Any(candidate => candidate.Id == asset.Id));
            }

            var secondPath = Path.Combine(secondRoot, "manual-recovery.wav");
            await File.WriteAllBytesAsync(secondPath, [4, 5, 6]);
            var secondStore = new FailNextSaveCatalogStore();
            var secondRecycleBin = new RecordingRecycleBin();
            using var secondService = new LocalAssetIndexService(secondStore, recycleBin: secondRecycleBin);
            var secondIndexed = await secondService.IndexFolderAsync(secondRoot, false);
            var secondAsset = secondIndexed.Catalog.Assets.Single();
            secondStore.FailNextSaves(2);

            var failure = await ThrowsAsync<AggregateException>(() =>
                secondService.RecycleAssetAsync(secondAsset.Id));

            False(File.Exists(secondPath));
            Contains(secondPath, failure.Message);
            Contains("刷新素材库", failure.Message);
            Contains("Windows 回收站", failure.Message);
        }
        finally
        {
            if (Directory.Exists(firstRoot))
            {
                Directory.Delete(firstRoot, true);
            }

            if (Directory.Exists(secondRoot))
            {
                Directory.Delete(secondRoot, true);
            }
        }
    }

    private static async Task AtomicCopiesNeverExposePartialOrOverwriteExistingFilesAsync()
    {
        var container = Directory.CreateTempSubdirectory("ial-atomic-copy-").FullName;
        var sourceDirectory = Path.Combine(container, "source");
        var targetDirectory = Path.Combine(container, "target");
        var sourcePath = Path.Combine(sourceDirectory, "sample.mp3");
        var content = Enumerable.Range(0, 4096).Select(index => (byte)(index % 251)).ToArray();
        try
        {
            Directory.CreateDirectory(sourceDirectory);
            Directory.CreateDirectory(targetDirectory);
            await File.WriteAllBytesAsync(sourcePath, content);

            var firstPath = await AtomicLocalFileCopy.CopyToUniqueFileAsync(sourcePath, targetDirectory);
            var secondPath = await AtomicLocalFileCopy.CopyToUniqueFileAsync(sourcePath, targetDirectory);

            Equal(Path.Combine(targetDirectory, "sample.mp3"), firstPath);
            Equal(Path.Combine(targetDirectory, "sample (2).mp3"), secondPath);
            SequenceEqual(content, await File.ReadAllBytesAsync(firstPath));
            SequenceEqual(content, await File.ReadAllBytesAsync(secondPath));
            False(Directory.EnumerateFiles(targetDirectory).Any(path =>
                Path.GetFileName(path).StartsWith(".ial-copy-", StringComparison.Ordinal)));

            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await ThrowsAsync<OperationCanceledException>(() =>
                AtomicLocalFileCopy.CopyToUniqueFileAsync(sourcePath, targetDirectory, canceled.Token));
            False(Directory.EnumerateFiles(targetDirectory).Any(path =>
                Path.GetFileName(path).StartsWith(".ial-copy-", StringComparison.Ordinal)));
        }
        finally
        {
            if (Directory.Exists(container))
            {
                Directory.Delete(container, true);
            }
        }
    }

    private static void BatchRenameRollbackDiagnosticsExposeRecoveryPathsCore()
    {
        var container = Directory.CreateTempSubdirectory("ial-rename-diagnostics-").FullName;
        var sourcePath = Path.Combine(container, "source.mp3");
        var temporaryPath = Path.Combine(container, ".ial-rename-test.tmp");
        var targetPath = Path.Combine(container, "target.mp3");
        try
        {
            File.WriteAllBytes(sourcePath, [1]);
            Equal<IOException?>(null, LocalAssetIndexService.DescribeBatchRenameRollbackFailure(
                sourcePath,
                temporaryPath,
                targetPath));

            File.Move(sourcePath, temporaryPath);
            var failure = LocalAssetIndexService.DescribeBatchRenameRollbackFailure(
                sourcePath,
                temporaryPath,
                targetPath) ?? throw new InvalidOperationException("Expected rollback diagnostics.");
            Contains(sourcePath, failure.Message);
            Contains(temporaryPath, failure.Message);
            Contains(targetPath, failure.Message);
        }
        finally
        {
            if (Directory.Exists(container))
            {
                Directory.Delete(container, true);
            }
        }
    }

    private sealed class FailNextSaveCatalogStore : ILocalAssetCatalogStore
    {
        private LocalAssetCatalog _catalog = LocalAssetCatalog.Empty;
        private int _remainingSaveFailures;

        public void FailNextSave() => FailNextSaves(1);

        public void FailNextSaves(int count) => _remainingSaveFailures = count;

        public Task<LocalAssetCatalog> LoadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_catalog);
        }

        public Task SaveAsync(
            LocalAssetCatalog catalog,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_remainingSaveFailures > 0)
            {
                _remainingSaveFailures--;
                throw new IOException("Injected catalog save failure.");
            }

            _catalog = catalog;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingRecycleBin : IRecycleBinFileSystem
    {
        private bool _failNextOperation;

        public List<string> RecycledFiles { get; } = [];

        public List<string> RecycledDirectories { get; } = [];

        public void FailNextOperation() => _failNextOperation = true;

        public Task RecycleFileAsync(string path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfRequested();
            RecycledFiles.Add(Path.GetFullPath(path));
            File.Delete(path);
            return Task.CompletedTask;
        }

        public Task RecycleDirectoryAsync(string path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfRequested();
            RecycledDirectories.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));
            Directory.Delete(path, true);
            return Task.CompletedTask;
        }

        private void ThrowIfRequested()
        {
            if (!_failNextOperation)
            {
                return;
            }

            _failNextOperation = false;
            throw new IOException("Injected recycle-bin failure.");
        }
    }

    private static async Task<TException> ThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException exception)
        {
            return exception;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name} to be thrown.");
    }

    private static void True(bool condition)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Expected true, but was false.");
        }
    }

    private static void False(bool condition) => True(!condition);

    private static void Contains(string expected, string actual)
    {
        if (!actual.Contains(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected '{actual}' to contain '{expected}'.");
        }
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', but was '{actual}'.");
        }
    }

    private static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException("Sequences are not equal.");
        }
    }
}
