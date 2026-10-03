internal static class ClientP61RegressionSourceSelfTests
{
    public static void ResizableLayoutEditorAndSelectionRemainWired()
    {
        var root = RepositoryRoot();
        var xaml = Read(root, "src", "InternalAssetLibrary.Client", "MainWindow.axaml");
        var window = Read(root, "src", "InternalAssetLibrary.Client", "MainWindow.axaml.cs");
        var localMenus = Read(
            root,
            "src",
            "InternalAssetLibrary.Client",
            "MainWindow.LocalFileSystemMenus.cs");

        Contains("x:Name=\"EditorNav\"", xaml);
        Contains("Tag=\"editor\"", xaml);
        Contains("x:Name=\"EditorPage\"", xaml);
        Contains("<controls:EditorWorkspaceView x:Name=\"EditorWorkspace\"", xaml);
        DoesNotContain("该功能将在后续版本开放", xaml);
        Contains("x:Name=\"LocalFolderSplitter\"", xaml);
        Contains("x:Name=\"DetailSplitter\"", xaml);
        AtLeast(2, Count("ResizeBehavior=\"PreviousAndNext\"", xaml));
        Contains("Click=\"DetailSidebarToggle_OnClick\"", xaml);
        Contains("EditorPage.IsVisible = page == \"editor\";", window);
        Contains("DetailExpandButton.IsVisible = showDetail && !expanded;", window);
        Contains("DetailColumn.Width = new GridLength(0);", window);
        DoesNotContain("_expandedDetailWidth : 52", window);
        Contains("LocalAssetScroll.SizeChanged += (_, _) => ScheduleAdaptiveAssetCardWidthUpdate();", window);
        Contains("CloudAssetScroll.SizeChanged += (_, _) => ScheduleAdaptiveAssetCardWidthUpdate();", window);
        Contains("if (_adaptiveCardWidthUpdatePending || _shutdownStarted)", window);
        DoesNotContain("SizeChanged += (_, _) => UpdateAdaptiveAssetCardWidths();", window);

        var localClick = Slice(
            window,
            "private void AssetCard_OnPointerPressed(",
            "private void AssetCard_OnPointerReleased(");
        Contains("SelectLocalAsset(asset);", localClick);
        Contains("_settings.SingleClickPreviewEnabled", localClick);
        Contains("BeginSelectedLocalPreviewAsync(asset)", localClick);
        Contains("StopDetailPreviewAsync(savePosition: true)", localClick);

        var cloudClick = Slice(
            window,
            "private void CloudAssetCard_OnPointerPressed(",
            "private async void CloudAssetCard_OnPointerMoved(");
        Contains("SelectCloudAsset(asset);", cloudClick);
        Contains("_settings.SingleClickPreviewEnabled", cloudClick);
        Contains("BeginSelectedCloudPreviewAsync(asset)", cloudClick);
        Contains("StopDetailPreviewAsync(savePosition: true)", cloudClick);

        foreach (var operation in new[]
                 {
                     "RenameAssetAsync",
                     "RecycleAssetAsync",
                     "CreateSubdirectoryAsync",
                     "RenameDirectoryAsync",
                     "RecycleDirectoryAsync"
                 })
        {
            Contains(operation, localMenus);
        }

        Contains("UiLocalization.Text(\"打开文件\")", localMenus);
        Contains("UiLocalization.Text(\"编辑标签\")", localMenus);
        Contains("UiLocalization.Text(\"删除文件夹\")", localMenus);
    }

    public static void AudioDetailsUseVisibleIndependentWaveforms()
    {
        var root = RepositoryRoot();
        var xaml = Read(root, "src", "InternalAssetLibrary.Client", "MainWindow.axaml");
        var window = Read(root, "src", "InternalAssetLibrary.Client", "MainWindow.axaml.cs");
        var thumbnails = Read(
            root,
            "src",
            "InternalAssetLibrary.Client.Core",
            "MediaAnalysis",
            "MediaThumbnailService.cs");
        var commands = Read(
            root,
            "src",
            "InternalAssetLibrary.Client.Core",
            "MediaAnalysis",
            "FfmpegCommandBuilder.cs");

        var localArtwork = Slice(
            window,
            "private void BeginLocalDetailArtworkLoad(",
            "private void BeginCloudDetailArtworkLoad(");
        Contains("asset.MediaType != LocalMediaType.Audio", localArtwork);
        Contains("LoadOwnedDetailWaveformAsync(", localArtwork);

        var cloudArtwork = Slice(
            window,
            "private void BeginCloudDetailArtworkLoad(",
            "private async Task LoadCloudDetailWaveformAsync(");
        Contains("ApiAssetCategory.Bgm or ApiAssetCategory.SoundEffect", cloudArtwork);
        Contains("LoadCloudDetailWaveformAsync(", cloudArtwork);

        var ownedWaveform = Slice(
            window,
            "private async Task LoadOwnedDetailWaveformAsync(",
            "private async Task LoadDetailWaveformAsync(");
        Contains("await LoadDetailWaveformAsync(", ownedWaveform);
        Contains("finally", ownedWaveform);
        Contains("FinishDetailArtworkOperation(cancellation);", ownedWaveform);
        Contains("_mediaThumbnailService.GetOrCreateWaveformAsync(", window);

        Contains("\"waveform-v5\"", thumbnails);
        Contains("showwavespic", commands);
        Contains("volume=-12dB", commands);
        Contains("scale=sqrt", commands);
        Contains("filter=average", commands);
        var detailImage = Slice(xaml, "<Image x:Name=\"DetailPreviewImage\"", "<TextBlock x:Name=\"DetailInitial\"");
        Contains("Stretch=\"Uniform\"", detailImage);
        DoesNotContain("Stretch=\"UniformToFill\"", detailImage);
    }

    private static string Read(string root, params string[] path) =>
        File.ReadAllText(Path.Combine([root, .. path]));

    private static string Slice(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = start < 0
            ? -1
            : source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        if (start < 0 || end < 0)
        {
            throw new InvalidOperationException($"Could not slice source between '{startMarker}' and '{endMarker}'.");
        }

        return source[start..end];
    }

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

    private static void DoesNotContain(string unexpected, string source)
    {
        if (source.Contains(unexpected, StringComparison.Ordinal))
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
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "InternalAssetLibrary.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
