internal static class ProfileLutSourceSelfTests
{
    public static void SharedLibraryRoutesCubeUploadsAndExposesTeamLuts()
    {
        var root = RepositoryRoot();
        var windowSource = File.ReadAllText(Path.Combine(
            root,
            "src",
            "InternalAssetLibrary.Client",
            "MainWindow.axaml.cs"));
        var windowXaml = File.ReadAllText(Path.Combine(
            root,
            "src",
            "InternalAssetLibrary.Client",
            "MainWindow.axaml"));
        var sharedLutSource = File.ReadAllText(Path.Combine(
            root,
            "src",
            "InternalAssetLibrary.Client",
            "MainWindow.CloudLuts.cs"));
        var lutWindowSource = File.ReadAllText(Path.Combine(
            root,
            "src",
            "InternalAssetLibrary.Client",
            "LutLibraryWindow.axaml.cs"));
        var lutWindowXaml = File.ReadAllText(Path.Combine(
            root,
            "src",
            "InternalAssetLibrary.Client",
            "LutLibraryWindow.axaml"));
        var uploadService = File.ReadAllText(Path.Combine(
            root,
            "src",
            "InternalAssetLibrary.Client",
            "Services",
            "TeamLutUploadService.cs"));

        Contains("Path.GetExtension(path).Equals(\".cube\"", windowSource);
        Contains("UploadCloudLutsAsync(supportedLuts)", windowSource);
        Contains("ListAllSharedCloudLutsAsync", sharedLutSource);
        Contains("SharedCloudLutCard_OnTapped", sharedLutSource);
        Contains("initialCloudLutId", lutWindowSource);
        Contains("UploadSelectedLocal_OnClick", lutWindowSource);
        var downloadAndConnect = MethodSource(lutWindowSource, "DownloadCloud_OnClick");
        var externalDownload = MethodSource(lutWindowSource, "DownloadCloudToExternal_OnClick");
        var windowBatchDownload = MethodSource(lutWindowSource, "DownloadSelectedCloudLuts_OnClick");
        var downloadHelper = MethodSource(
            lutWindowSource,
            "private async Task DownloadTeamLutToFileAsync");
        var sharedBatchDownload = MethodSource(sharedLutSource, "BatchDownloadCloudLuts_OnClick");
        Contains("_localLibrary.ImportAsync", downloadAndConnect);
        Contains("SaveFilePickerAsync", externalDownload);
        Contains("DownloadTeamLutToFileAsync", externalDownload);
        Contains("DownloadTeamLutAsync", downloadHelper);
        DoesNotContain("_localLibrary.ImportAsync", externalDownload);
        Contains("DownloadTeamLutToFileAsync", windowBatchDownload);
        DoesNotContain("_localLibrary.ImportAsync", windowBatchDownload);
        Contains("QueueLutDownload", sharedBatchDownload);
        DoesNotContain("_localLutLibrary.ImportAsync", sharedBatchDownload);
        Contains("x:Name=\"UploadSelectedLocalButton\"", lutWindowXaml);
        Contains("Click=\"DownloadCloudToExternal_OnClick\">下载</Button>", lutWindowXaml);
        Contains("x:Name=\"CloudLutViewModePanel\"", windowXaml);
        Contains("x:Name=\"CloudLutSelectAllCheckBox\"", windowXaml);
        Contains("Click=\"BatchDownloadCloudLuts_OnClick\">批量下载", windowXaml);
        Contains("SelectionMode=\"Multiple,Toggle\"", lutWindowXaml);
        Contains("x:Name=\"LocalLutActionPanel\"", lutWindowXaml);
        Contains("x:Name=\"CloudLutActionPanel\"", lutWindowXaml);
        Equal(2, lutWindowXaml.Split(
            "MinHeight=\"40\"",
            StringSplitOptions.None).Length - 1);
        DoesNotContain("Margin=\"0,0,8,8\"", lutWindowXaml);
        DoesNotContain("Margin=\"0,0,0,8\"", lutWindowXaml);
        Contains("Click=\"DownloadSelectedCloudLuts_OnClick\">批量下载", lutWindowXaml);
        Contains("x:Name=\"CloudLutListScroll\"", windowXaml);
        Equal(2, windowXaml.Split(
            "ItemsSource=\"{Binding SharedCloudLuts}\"",
            StringSplitOptions.None).Length - 1);
        Contains("CloudLutScroll.IsVisible = showsLuts && _currentUser is not null && SharedCloudLuts.Count > 0 && isGrid;", windowSource);
        Contains("CloudLutListScroll.IsVisible = showsLuts && _currentUser is not null && SharedCloudLuts.Count > 0 && !isGrid;", windowSource);
        Contains("TryRemoveIncompleteUploadAsync", uploadService);
    }

    public static void UserProfilesExposeOnlyOwnedActiveTeamLuts()
    {
        var root = RepositoryRoot();
        var profileSource = File.ReadAllText(Path.Combine(
            root,
            "src",
            "InternalAssetLibrary.Client",
            "MainWindow.ProfileLuts.cs"));
        var windowSource = File.ReadAllText(Path.Combine(
            root,
            "src",
            "InternalAssetLibrary.Client",
            "MainWindow.axaml.cs"));
        var xaml = File.ReadAllText(Path.Combine(
            root,
            "src",
            "InternalAssetLibrary.Client",
            "MainWindow.axaml"));
        var serverSource = File.ReadAllText(Path.Combine(
            root,
            "src",
            "InternalAssetLibrary.Server",
            "Api",
            "TeamLutEndpoints.cs"));

        Contains("UploaderId: uploaderId", profileSource);
        Contains("lut.State == TeamLutState.Active", profileSource);
        Contains("lut.UploadedBy.Id == uploaderId", profileSource);
        Contains("_profileLutSource = await lutsTask", windowSource);
        Contains("x:Name=\"ProfileLutCount\" Tag=\"lut\"", xaml);
        Contains("ItemsSource=\"{Binding ProfileLuts}\"", xaml);
        Contains("lut.UploadedByUserId == uploaderId", serverSource);
    }

    private static void Contains(string expectedSubstring, string actual)
    {
        if (!actual.Contains(expectedSubstring, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected source to contain '{expectedSubstring}'.");
        }
    }

    private static void DoesNotContain(string unexpectedSubstring, string actual)
    {
        if (actual.Contains(unexpectedSubstring, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Did not expect source to contain '{unexpectedSubstring}'.");
        }
    }

    private static string MethodSource(string source, string methodName)
    {
        var start = source.IndexOf(methodName, StringComparison.Ordinal);
        if (start < 0)
        {
            throw new InvalidOperationException($"Expected source to contain method '{methodName}'.");
        }

        var end = source.IndexOf("\n    private ", start + methodName.Length, StringComparison.Ordinal);
        return end < 0 ? source[start..] : source[start..end];
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
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
