internal static class ClientP66RegressionSourceSelfTests
{
    public static void OneClickUpdaterAndReleaseNotesStayWired()
    {
        var root = RepositoryRoot();
        var program = Read(root, "src", "InternalAssetLibrary.Client", "Program.cs");
        var window = Read(root, "src", "InternalAssetLibrary.Client", "MainWindow.axaml.cs");
        var xaml = Read(root, "src", "InternalAssetLibrary.Client", "MainWindow.axaml");
        var installer = Read(root, "packaging", "windows", "InternalAssetLibrary.Client.iss");
        var publisher = Read(root, "scripts", "publish-client.ps1");
        var environment = Read(root, "deployment", ".env.example");

        Contains("--post-update", program);
        Contains("ClientUpdateCoordinator.PrepareAsync", window);
        Contains("ClientUpdateCoordinator.Launch", window);
        Contains("AcknowledgePostUpdateAsync", window);
        Contains("GetPendingReleaseNotesAsync", window);
        Contains("ViewReleaseNotes_OnClick", xaml);
        Contains("InternalAssetLibrary.Updater.exe", installer);
        Contains("$updaterProject", publisher);
        Contains("role = 'updater'", publisher);
        Contains("ClientUpdates__MinimumCompatibleVersion=0.2.0-preview.6.3", environment);
    }

    public static void HoverSelectionAndResumeBehaviorStayWired()
    {
        var root = RepositoryRoot();
        var window = Read(root, "src", "InternalAssetLibrary.Client", "MainWindow.axaml.cs");
        var playback = Read(root, "src", "InternalAssetLibrary.Client", "MainWindow.Playback.cs");
        var session = Read(root, "src", "InternalAssetLibrary.Client.Core", "Playback", "PreviewPlaybackSession.cs");
        var controller = Read(root, "src", "InternalAssetLibrary.Client.Core", "Playback", "PlaybackController.cs");

        Contains("await EndHoverPreviewAsync(key);", window);
        Contains("CommitHoverDetailSelection(key);", window);
        Contains("await EndHoverPreviewAsync(key);", playback);
        Contains("CommitHoverDetailSelection(key);", playback);
        False(playback.Contains("RestoreHoverDetailSelection", StringComparison.Ordinal));
        Contains("position >= duration.Value - EndRestartTolerance", session);
        Contains("HasReachedEnd(state)", controller);
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

    private static void False(bool value)
    {
        if (value)
        {
            throw new InvalidOperationException("Expected false, but was true.");
        }
    }
}
