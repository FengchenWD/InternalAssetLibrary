internal static class ClientShutdownSourceSelfTests
{
    public static void MainWindowShutdownRemainsResponsiveAndBounded()
    {
        var root = RepositoryRoot();
        var mainWindow = File.ReadAllText(Path.Combine(
            root,
            "src",
            "InternalAssetLibrary.Client",
            "MainWindow.axaml.cs"));
        var playback = File.ReadAllText(Path.Combine(
            root,
            "src",
            "InternalAssetLibrary.Client",
            "MainWindow.Playback.cs"));

        var closing = Slice(mainWindow, "private async void OnClosing(", "private static async Task CompleteShutdownStepAsync(");
        Contains("eventArgs.Cancel = true;", closing);
        Contains("if (_shutdownStarted)", closing);
        Contains("await Task.WhenAll(", closing);
        Contains("_shutdownCompleted = true;", closing);
        Contains("Dispatcher.UIThread.Post(() => Close());", closing);
        DoesNotContain("GetAwaiter().GetResult()", closing);
        DoesNotContain(".Wait(", closing);

        var boundedStep = Slice(
            mainWindow,
            "private static async Task CompleteShutdownStepAsync(",
            "private static void ObserveLateShutdownFailure(");
        Contains("await task.WaitAsync(ShutdownStepTimeout);", boundedStep);
        Contains("catch (TimeoutException exception)", boundedStep);

        var playbackDispose = Slice(
            playback,
            "private Task DisposePlaybackAsync()",
            "private static string ResolveRuntimePath(");
        Contains("return Task.Run(async () =>", playbackDispose);
        Contains("ClientDiagnostics.WriteException(\"playback-shutdown\"", playbackDispose);
        DoesNotContain(".Wait(", playbackDispose);
        DoesNotContain("GetAwaiter().GetResult()", playbackDispose);
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        if (start < 0)
        {
            throw new InvalidOperationException($"Expected source to contain '{startMarker}'.");
        }

        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        if (end < 0)
        {
            throw new InvalidOperationException($"Expected source after '{startMarker}' to contain '{endMarker}'.");
        }

        return source[start..end];
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
