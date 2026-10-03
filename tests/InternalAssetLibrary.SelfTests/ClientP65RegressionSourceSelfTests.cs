internal static class ClientP65RegressionSourceSelfTests
{
    public static void SelectionBatchMoveInstallPathAndCloudDragStayWired()
    {
        var root = RepositoryRoot();
        var clientDirectory = Path.Combine(root, "src", "InternalAssetLibrary.Client");
        var xaml = File.ReadAllText(Path.Combine(clientDirectory, "MainWindow.axaml"));
        var main = File.ReadAllText(Path.Combine(clientDirectory, "MainWindow.axaml.cs"));
        var localBatch = File.ReadAllText(Path.Combine(clientDirectory, "MainWindow.LocalBatch.cs"));
        var cloudFolders = File.ReadAllText(Path.Combine(clientDirectory, "MainWindow.CloudFolders.cs"));
        var core = File.ReadAllText(Path.Combine(
            root,
            "src",
            "InternalAssetLibrary.Client.Core",
            "Platform",
            "WindowsDefaultPlayerRegistrationService.cs"));
        var installer = File.ReadAllText(Path.Combine(
            root,
            "packaging",
            "windows",
            "InternalAssetLibrary.Client.iss"));

        Contains("asset.IsBatchSelected = isChecked;", localBatch);
        Contains("asset.IsSelected = isChecked;", main);
        Contains("x:Name=\"BatchMoveCloudAssetsButton\"", xaml);
        Contains("Click=\"BatchMoveCloudAssets_OnClick\"", xaml);
        Contains("private async void BatchMoveCloudAssets_OnClick", cloudFolders);
        Contains("_currentUser.IsAdmin", cloudFolders);
        Contains("asset.Asset.UploadedBy.Id == _currentUser.Id", cloudFolders);
        Contains("failedIds.Contains(asset.Id)", cloudFolders);

        Contains("private async Task<bool> BeginFilesDragAsync", main);
        Contains("data.Add(DataTransferItem.CreateFile(file));", main);
        Contains("_activeCloudDragPaths", main);
        Contains("DragDrop.DragOver=\"CloudDragLocalNavigation_OnDragOver\"", xaml);
        AtLeast(2, Count("DragDrop.DragOver=\"LocalFolderNode_OnDragOver\"", xaml));
        AtLeast(2, Count("DragDrop.Drop=\"LocalFolderNode_OnDrop\"", xaml));
        Contains("QueueLocalFileDrop(target, dropped.Files);", main);
        Contains("targetFolder.FolderId.HasValue", main);

        Contains("Classes=\"detailExpand detailExpandFloating\"", xaml);
        Contains("Background=\"{DynamicResource SurfaceRaisedBrush}\"", Section(
            xaml,
            "x:Name=\"DetailSidebar\"",
            "x:Name=\"DetailExpandedContent\""));
        Contains("ReadInstalledExecutablePathWindows", core);
        Contains("MatchesInstalledExecutablePath", core);
        Contains("ValueName: \"InstallLocation\"; ValueData: \"{app}\"", installer);
        Contains("ValueName: \"ExecutablePath\"; ValueData: \"{app}\\{#AppExeName}\"", installer);
        Contains("#define CapabilitiesKey \"Software\\FengchenWD\\InternalAssetLibrary\\Capabilities\"", installer);
        NotContains("#define CapabilitiesKey \"{#ProductKey}", installer);
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

    private static void Contains(string expected, string actual)
    {
        if (!actual.Contains(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected source to contain '{expected}'.");
        }
    }

    private static void NotContains(string unexpected, string actual)
    {
        if (actual.Contains(unexpected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected source not to contain '{unexpected}'.");
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
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "InternalAssetLibrary.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
