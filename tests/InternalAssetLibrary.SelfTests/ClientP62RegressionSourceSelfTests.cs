internal static class ClientP62RegressionSourceSelfTests
{
    public static void WaveformListsAndCloudSelectionStayWired()
    {
        var root = RepositoryRoot();
        var xaml = Read(root, "src", "InternalAssetLibrary.Client", "MainWindow.axaml");
        var app = Read(root, "src", "InternalAssetLibrary.Client", "App.axaml");
        var window = Read(root, "src", "InternalAssetLibrary.Client", "MainWindow.axaml.cs");
        var localViewModel = Read(
            root,
            "src",
            "InternalAssetLibrary.Client",
            "ViewModels",
            "AssetCardViewModel.cs");
        var cloudViewModel = Read(
            root,
            "src",
            "InternalAssetLibrary.Client",
            "ViewModels",
            "CloudViewModels.cs");
        var playback = Read(root, "src", "InternalAssetLibrary.Client", "MainWindow.Playback.cs");

        var localLoading = Slice(
            window,
            "private async Task LoadLocalThumbnailsAsync(",
            "private async Task LoadLocalDurationAsync(");
        Contains("asset.MediaType == LocalMediaType.Audio", localLoading);
        Contains("GetOrCreateWaveformAsync(", localLoading);
        Contains("maximumWidth: 320", localLoading);
        Contains("await LoadLocalDurationAsync(asset", localLoading);

        var localList = Slice(
            xaml,
            "<ScrollViewer x:Name=\"LocalAssetListScroll\"",
            "<TextBlock x:Name=\"LocalFilteredEmptyState\"");
        Contains("Height=\"142\"", localList);
        Contains("Text=\"{Binding Name}\"", localList);
        Contains("Text=\"{Binding Extension}\"", localList);
        Contains("Text=\"{Binding DurationLabel}\"", localList);
        Contains("IsVisible=\"{Binding IsTimedMedia}\"", localList);
        Contains("Stretch=\"{Binding ListThumbnailStretch}\"", localList);
        Contains("public Stretch ListThumbnailStretch", localViewModel);

        var cloudGrid = Slice(
            xaml,
            "<ScrollViewer x:Name=\"CloudAssetScroll\"",
            "<ScrollViewer x:Name=\"CloudAssetListScroll\"");
        var checkbox = Slice(cloudGrid, "<CheckBox Grid.RowSpan=\"2\"", "<Border Background=");
        Contains("HorizontalAlignment=\"Left\"", checkbox);
        Contains("Margin=\"9,9,0,0\"", checkbox);
        var extensionBadge = Slice(
            cloudGrid,
            "<Border HorizontalAlignment=\"Right\" VerticalAlignment=\"Top\"",
            "</Border>");
        Contains("Text=\"{Binding Extension}\"", extensionBadge);

        var cloudList = Slice(
            xaml,
            "<ScrollViewer x:Name=\"CloudAssetListScroll\"",
            "<TextBlock x:Name=\"CloudEmptyState\"");
        Contains("Height=\"146\"", cloudList);
        Contains("<Grid ColumnDefinitions=\"40,*\">", cloudList);
        Contains("Text=\"{Binding DurationLabel}\"", cloudList);
        Contains("IsChecked=\"{Binding IsSelected, Mode=TwoWay}\"", cloudList);
        Contains("Stretch=\"{Binding ListThumbnailStretch}\"", cloudList);
        Contains("public Stretch ListThumbnailStretch", cloudViewModel);

        var profileAssets = Slice(
            xaml,
            "<ItemsControl x:Name=\"ProfileAssetItems\"",
            "<ItemsControl x:Name=\"ProfileLutItems\"");
        Contains("Source=\"{Binding Thumbnail}\"", profileAssets);
        Contains("IsVisible=\"{Binding HasThumbnail}\"", profileAssets);

        Contains("Border.mediaBadge", app);
        Contains("{DynamicResource SurfaceRaisedBrush}", app);
        Contains("TextBlock.mediaBadgeText", app);
        DoesNotContain("Background=\"#AA0B1117\"", xaml);
        Contains("public bool IsTimedMedia", localViewModel);
        Contains("public string DurationLabel", localViewModel);
        Contains("public bool IsTimedMedia", cloudViewModel);
        Contains("Asset.DurationSeconds", cloudViewModel);
        Contains("public bool IsHighlighted => IsSelected || IsDetailSelected;", cloudViewModel);

        var playerVisuals = Slice(
            playback,
            "private async Task RefreshPlayerVisualsAsync(",
            "private void ThrowIfPlayerVisualOperationIsStale(");
        Contains("item.MediaType == LocalMediaType.Audio", playerVisuals);
        Contains("GetOrCreateWaveformAsync(", playerVisuals);

        var cloudClick = Slice(
            window,
            "private void CloudAssetCard_OnPointerPressed(",
            "private static bool OriginatesFromCheckBox(");
        Contains("OriginatesFromCheckBox(eventArgs)", cloudClick);
        Contains("SelectCloudAsset(asset);", cloudClick);
        DoesNotContain("asset.IsSelected =", cloudClick);
    }

    public static void DetailCollapseEditorAndCosProgressStayWired()
    {
        var root = RepositoryRoot();
        var xaml = Read(root, "src", "InternalAssetLibrary.Client", "MainWindow.axaml");
        var window = Read(root, "src", "InternalAssetLibrary.Client", "MainWindow.axaml.cs");
        var updates = Read(
            root,
            "src",
            "InternalAssetLibrary.Client.Core",
            "Updates",
            "ClientUpdateService.cs");

        var expandIndex = xaml.IndexOf("x:Name=\"DetailExpandButton\"", StringComparison.Ordinal);
        var splitterIndex = xaml.IndexOf("x:Name=\"DetailSplitter\"", StringComparison.Ordinal);
        var sidebarIndex = xaml.IndexOf("x:Name=\"DetailSidebar\"", StringComparison.Ordinal);
        True(expandIndex >= 0 && expandIndex < splitterIndex && splitterIndex < sidebarIndex);
        var expandButton = Slice(xaml, "<Button x:Name=\"DetailExpandButton\"", "</Button>");
        Contains("Grid.Column=\"3\"", expandButton);
        Contains("VerticalAlignment=\"Bottom\"", expandButton);
        Contains("ZIndex=\"200\"", expandButton);
        Contains("Classes=\"detailExpand detailExpandFloating\"", expandButton);
        DoesNotContain("Classes=\"quiet\"", expandButton);
        var detailExpandStyle = Slice(
            Read(root, "src", "InternalAssetLibrary.Client", "App.axaml"),
            "<Style Selector=\"Button.detailExpand\">",
            "</Style>");
        Contains("{DynamicResource SurfaceRaisedBrush}", detailExpandStyle);
        Contains("{DynamicResource AccentBrush}", detailExpandStyle);
        Contains("Property=\"Opacity\" Value=\"1\"", detailExpandStyle);

        var layout = Slice(
            window,
            "private void UpdateDetailSidebarLayout(",
            "private void CurrentUserAvatar_OnClick(");
        Contains("var expanded = showDetail && _isDetailSidebarExpanded;", layout);
        Contains("DetailSidebar.IsVisible = expanded;", layout);
        AtLeast(2, Count("new GridLength(0)", layout));
        DoesNotContain(": 52", layout);

        AtLeast(1, Count("Text=\"✂\"", xaml));
        AtLeast(2, Count("Stretch=\"{Binding ListThumbnailStretch}\"", xaml));
        Contains("x:Name=\"ClientUpdateProgressPanel\"", xaml);
        Contains("x:Name=\"ClientUpdateProgressBar\"", xaml);
        Contains("x:Name=\"ClientUpdateProgressText\"", xaml);
        Contains("DownloadVerifiedInstallerWithProgressAsync(", window);
        Contains("ClientUpdateDownloadProgress", window);
        Contains("腾讯云 COS 直连下载", window);
        Contains("public int Percentage", updates);
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

    private static void True(bool value)
    {
        if (!value)
        {
            throw new InvalidOperationException("Expected condition to be true.");
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
