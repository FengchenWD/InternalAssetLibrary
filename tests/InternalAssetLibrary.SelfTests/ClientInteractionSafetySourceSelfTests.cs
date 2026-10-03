internal static class ClientInteractionSafetySourceSelfTests
{
    public static void NativePickersAndDragStartsRemainSingleFlight()
    {
        var root = RepositoryRoot();
        var window = File.ReadAllText(Path.Combine(
            root,
            "src",
            "InternalAssetLibrary.Client",
            "MainWindow.axaml.cs"));
        var playback = File.ReadAllText(Path.Combine(
            root,
            "src",
            "InternalAssetLibrary.Client",
            "MainWindow.Playback.cs"));
        var safety = File.ReadAllText(Path.Combine(
            root,
            "src",
            "InternalAssetLibrary.Client",
            "MainWindow.InteractionSafety.cs"));

        Contains("_nativePickerGate.Wait(0)", safety);
        Contains("_nativePickerGate.Release()", safety);
        AtLeast(2, Count("if (_shutdownStarted)", safety));
        Contains("return _shutdownStarted ? null : result;", safety);
        AtLeast(5, Count("RunNativePickerAsync(", window));
        AtLeast(9, Count("RunNativePickerAsync(", playback));

        Contains("Interlocked.CompareExchange(ref _dragStartOperationActive, 1, 0)", window);
        Exactly(3, Count("if (!TryBeginDragStartOperation())", window));
        Exactly(3, Count("EndDragStartOperation();", window));
        var reset = Slice(
            window,
            "private void ResetPendingDragGesture()",
            "private bool TryBeginDragStartOperation()");
        Contains("_isDragStartPending = false", reset);
        Contains("Volatile.Read(ref _systemDragActive) == 0", reset);
        Contains("_activeLocalDragAssets = null", reset);
        Contains("_activeCloudDragAssets = null", reset);
        Contains("_dragPreparationCancellation", reset);
        Contains("Activated += (_, _) => ResetPendingDragGesture();", window);
        Contains("Volatile.Read(ref _systemDragActive) != 0", window);
        Contains("Interlocked.CompareExchange(ref _systemDragActive, 1, 0)", window);
        Contains("WaitAsync(cancellationToken)", window);

        var closing = Slice(
            window,
            "private async void OnClosing(",
            "private static async Task CompleteShutdownStepAsync(");
        Contains("_shutdownStarted = true;", closing);
        Contains("ResetPendingDragGesture();", closing);

        var cloudDrop = Slice(
            window,
            "private async Task PrepareDroppedCloudUploadSafelyAsync(",
            "private static bool IsSupportedCloudUploadPath(");
        Contains("if (_shutdownStarted)", cloudDrop);
        Contains("shared-library-drop-dialog", cloudDrop);
    }

    public static void ModalNativePickersRemainSingleFlightAndCloseSafe()
    {
        var root = RepositoryRoot();
        var clientRoot = Path.Combine(root, "src", "InternalAssetLibrary.Client");
        var localMarkers = File.ReadAllText(Path.Combine(clientRoot, "LocalMarkerWindow.axaml.cs"));
        var cloudMarkers = File.ReadAllText(Path.Combine(clientRoot, "CloudMarkerWindow.axaml.cs"));
        var lutLibrary = File.ReadAllText(Path.Combine(clientRoot, "LutLibraryWindow.axaml.cs"));

        AssertModalPickerSafety(localMarkers, expectedPickerCalls: 2);
        AssertModalPickerSafety(cloudMarkers, expectedPickerCalls: 2);
        AssertModalPickerSafety(lutLibrary, expectedPickerCalls: 5);
    }

    private static void AssertModalPickerSafety(string source, int expectedPickerCalls)
    {
        Contains("private readonly SemaphoreSlim _nativePickerGate = new(1, 1);", source);
        Contains("Closing += (_, _) => _closing = true;", source);
        Contains("if (_closing)", source);
        Contains("_nativePickerGate.Wait(0)", source);
        Contains("return _closing ? null : result;", source);
        Contains("_nativePickerGate.Release()", source);
        Exactly(1, Count("private async Task<TResult?> RunNativePickerAsync<TResult>", source));
        Exactly(expectedPickerCalls, Count("RunNativePickerAsync(", source));
        DoesNotContain("await StorageProvider.", source);
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

    private static string Slice(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = start < 0
            ? -1
            : source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        if (start < 0 || end < 0)
        {
            throw new InvalidOperationException(
                $"Could not slice source between '{startMarker}' and '{endMarker}'.");
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

    private static void AtLeast(int minimum, int actual)
    {
        if (actual < minimum)
        {
            throw new InvalidOperationException($"Expected at least {minimum}, got {actual}.");
        }
    }

    private static void Exactly(int expected, int actual)
    {
        if (actual != expected)
        {
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
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
