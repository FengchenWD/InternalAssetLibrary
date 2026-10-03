internal static class ClientP63RegressionSourceSelfTests
{
    public static void LocalBatchSelectionAndOperationsStayWired()
    {
        var root = RepositoryRoot();
        var xaml = Read(root, "src", "InternalAssetLibrary.Client", "MainWindow.axaml");
        var window = Read(root, "src", "InternalAssetLibrary.Client", "MainWindow.axaml.cs");
        var batch = Read(root, "src", "InternalAssetLibrary.Client", "MainWindow.LocalBatch.cs");
        var playback = Read(root, "src", "InternalAssetLibrary.Client", "MainWindow.Playback.cs");
        var viewModel = Read(
            root,
            "src",
            "InternalAssetLibrary.Client",
            "ViewModels",
            "AssetCardViewModel.cs");
        var index = Read(
            root,
            "src",
            "InternalAssetLibrary.Client.Core",
            "LocalAssets",
            "LocalAssetIndexService.cs");
        var atomicCopy = Read(
            root,
            "src",
            "InternalAssetLibrary.Client.Core",
            "LocalAssets",
            "AtomicLocalFileCopy.cs");

        Contains("x:Name=\"LocalBatchModeButton\"", xaml);
        Contains("x:Name=\"LocalBatchToolbar\"", xaml);
        Contains("Click=\"BatchSaveLocalAssets_OnClick\"", xaml);
        Contains("Click=\"BatchTagLocalAssets_OnClick\"", xaml);
        Contains("Click=\"BatchRenameLocalAssets_OnClick\"", xaml);
        Contains("Click=\"BatchRecycleLocalAssets_OnClick\"", xaml);
        AtLeast(2, Count("IsChecked=\"{Binding IsBatchSelected, Mode=TwoWay}\"", xaml));
        AtLeast(2, Count("IsVisible=\"{Binding IsBatchMode}\"", xaml));
        AtLeast(2, Count("IsVisible=\"{Binding IsHighlighted}\"", xaml));

        Contains("public bool IsBatchSelected", viewModel);
        Contains("public bool IsDetailSelected", viewModel);
        Contains("public bool IsBatchMode", viewModel);
        Contains("public bool IsHighlighted => IsBatchSelected || IsDetailSelected;", viewModel);
        Contains("asset.IsDetailSelected = true;", window);
        Contains("_selectedLocalAsset.IsDetailSelected = false;", window);
        Contains("selectedAssetIds.Contains(asset.Id)", window);
        Contains("card.IsBatchMode = _isLocalBatchMode;", window);
        Contains("if (OriginatesFromCheckBox(eventArgs))", playback);

        Contains("AtomicLocalFileCopy.CopyToUniqueFileAsync(sourcePath, targetDirectory)", batch);
        Contains("FileMode.CreateNew", atomicCopy);
        Contains("File.Move(temporaryPath, targetPath, overwrite: false)", atomicCopy);
        Contains("AddTagsToAssetsAsync(selectedIds, result.Tags)", batch);
        Contains("RenameAssetsAsync(requests)", batch);
        Contains("RecycleAssetAsync(asset.Id, cancellation.Token)", batch);
        Contains("new LocalBatchRenameWindow(", batch);
        Contains("new MessageDialogWindow(", batch);
        Contains("public async Task<LocalAssetCatalog> AddTagsToAssetsAsync(", index);
        Contains("public async Task<LocalAssetCatalog> RenameAssetsAsync(", index);
        Contains("File.Move(plan.SourcePath, plan.TemporaryPath);", index);
        Contains("RollBackBatchRename(", index);
    }

    private static string Read(string root, params string[] path) =>
        File.ReadAllText(Path.Combine([root, .. path]));

    private static int Count(string value, string source)
    {
        var count = 0;
        for (var index = 0;
             (index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0;
             index += value.Length)
        {
            count++;
        }

        return count;
    }

    private static void Contains(string expected, string source)
    {
        if (!source.Contains(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected source to contain '{expected}'.");
        }
    }

    private static void AtLeast(int minimum, int actual)
    {
        if (actual < minimum)
        {
            throw new InvalidOperationException($"Expected at least '{minimum}', got '{actual}'.");
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "InternalAssetLibrary.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
