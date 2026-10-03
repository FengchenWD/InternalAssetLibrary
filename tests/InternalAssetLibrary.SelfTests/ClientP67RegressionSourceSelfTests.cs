internal static class ClientP67RegressionSourceSelfTests
{
    public static void FolderBrowsingDragDownloadsAndUploadsStayWired()
    {
        var root = RepositoryRoot();
        var clientDirectory = Path.Combine(root, "src", "InternalAssetLibrary.Client");
        var xaml = File.ReadAllText(Path.Combine(clientDirectory, "MainWindow.axaml"));
        var main = File.ReadAllText(Path.Combine(clientDirectory, "MainWindow.axaml.cs"));
        var cloudFolders = File.ReadAllText(Path.Combine(clientDirectory, "MainWindow.CloudFolders.cs"));
        var downloadWindow = File.ReadAllText(Path.Combine(clientDirectory, "CloudDownloadDestinationWindow.axaml"));
        var categoryWindow = File.ReadAllText(Path.Combine(clientDirectory, "AudioCategoryWindow.axaml"));

        Contains("private ApiAsset[]? _activeCloudDragAssets;", main);
        Contains("private LocalAsset[]? _activeLocalDragAssets;", main);
        Contains("DownloadCloudAssetsToLocalFolderSafelyAsync(target, cloudAssets)", main);
        Contains("MoveLocalAssetsToFolderSafelyAsync(target, localAssets)", main);
        Contains("_indexService.MoveAssetsAsync(", main);
        Contains("new CloudDownloadDestinationWindow(LocalFolderNodes, _settings.DownloadToDefaultDirectory)", main);
        Contains("ChooseCloudFolderUpload_OnClick", main);
        Contains("SnapshotCloudUploadFolder", main);
        Contains("PrepareCloudFolderUploadAsync", main);
        Contains("GetFolderDisplayName(rootPath)", main);

        AtLeast(2, Count("DragDrop.Drop=\"LocalFolderNode_OnDrop\"", xaml));
        AtLeast(3, Count("DragDrop.Drop=\"CloudFolderNode_OnDrop\"", xaml));
        Contains("Click=\"ChooseCloudFolderUpload_OnClick\">上传文件夹", xaml);
        Contains("x:Name=\"CloudFolderBackButton\"", xaml);
        Contains("x:Name=\"CloudFolderBreadcrumbText\"", xaml);
        Contains("x:Name=\"CloudRecursiveSearchToggle\"", xaml);
        Contains("ItemsSource=\"{Binding CurrentCloudFolderNodes}\"", xaml);

        Contains("CurrentCloudFolderNodes", cloudFolders);
        Contains("folder.ParentId == _selectedCloudFolderId", cloudFolders);
        Contains("CompleteCloudFolderAssetDrop", cloudFolders);
        Contains("MoveCloudAssetsByDropSafelyAsync", cloudFolders);
        Contains("CanMoveCloudAssets", cloudFolders);
        Contains("new AudioCategoryWindow(asset.Asset.Category)", cloudFolders);
        Contains("UpdateAssetCategoryAsync", cloudFolders);

        Contains("ItemsSource=\"{Binding FolderNodes}\"", downloadWindow);
        Contains("ChooseComputerFolder_OnClick", downloadWindow);
        Contains("下载到所选目录", downloadWindow);
        Contains("ComboBoxItem Content=\"BGM\"", categoryWindow);
        Contains("ComboBoxItem Content=\"音效\"", categoryWindow);
    }

    public static void LibraryRefreshStillResetsPreviewPlayback()
    {
        var main = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "src",
            "InternalAssetLibrary.Client",
            "MainWindow.axaml.cs"));
        var localRefresh = Section(main, "private async void RefreshLocal_OnClick", "private async void LocalStorageMonitor_OnTick");
        var cloudRefresh = Section(main, "private async void RefreshCloud_OnClick", "private async Task RefreshCloudAssetsAsync");

        Contains("await StopDetailPreviewAsync(savePosition: false);", localRefresh);
        Contains("_previewSession.ResetForLibraryRefresh();", localRefresh);
        Contains("await StopDetailPreviewAsync(savePosition: false);", cloudRefresh);
        Contains("_previewSession.ResetForLibraryRefresh();", cloudRefresh);
    }

    private static string Section(string source, string start, string end)
    {
        var startIndex = source.IndexOf(start, StringComparison.Ordinal);
        var endIndex = startIndex < 0
            ? -1
            : source.IndexOf(end, startIndex + start.Length, StringComparison.Ordinal);
        if (startIndex < 0 || endIndex < 0)
        {
            throw new InvalidOperationException($"Could not find source section '{start}' to '{end}'.");
        }

        return source[startIndex..endIndex];
    }

    private static int Count(string value, string source)
    {
        var count = 0;
        for (var offset = 0; (offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0;)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }

    private static void AtLeast(int minimum, int actual)
    {
        if (actual < minimum)
        {
            throw new InvalidOperationException($"Expected at least '{minimum}', got '{actual}'.");
        }
    }

    private static void Contains(string expected, string actual)
    {
        if (!actual.Contains(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected source to contain '{expected}'.");
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "InternalAssetLibrary.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
