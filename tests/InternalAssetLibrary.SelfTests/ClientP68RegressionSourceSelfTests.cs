internal static class ClientP68RegressionSourceSelfTests
{
    public static void ImageZoomAndPanStayWired()
    {
        var root = RepositoryRoot();
        var xaml = Read(root, "src", "InternalAssetLibrary.Client", "MainWindow.axaml");
        var playback = Read(root, "src", "InternalAssetLibrary.Client", "MainWindow.Playback.cs");

        Contains("PlayerArtworkViewport", xaml);
        Contains("PlayerImageToolsPanel", xaml);
        Contains("PlayerImageZoomOut_OnClick", xaml);
        Contains("PlayerImageZoomIn_OnClick", xaml);
        Contains("PlayerImageReset_OnClick", xaml);
        Contains("PlayerArtworkViewport_OnPointerWheelChanged", xaml);
        Contains("PlayerArtworkViewport_OnPointerPressed", xaml);
        Contains("PlayerArtworkViewport_OnPointerMoved", xaml);
        Contains("PlayerArtworkViewport_OnPointerReleased", xaml);
        Contains("Math.Clamp(zoom, 0.25, 8)", playback);
        Contains("IsCurrentStaticPlayerImage()", playback);
    }

    private static string Read(string root, params string[] parts) =>
        File.ReadAllText(parts.Aggregate(root, Path.Combine));

    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "InternalAssetLibrary.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static void Contains(string expected, string actual)
    {
        if (!actual.Contains(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected source to contain '{expected}'.");
        }
    }
}
