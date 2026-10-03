internal static class ClientP58RegressionSourceSelfTests
{
    public static void ProfilePasswordChangeRemainsWired()
    {
        var root = RepositoryRoot();
        var xaml = Read(root, "src", "InternalAssetLibrary.Client", "MainWindow.axaml");
        var window = Read(root, "src", "InternalAssetLibrary.Client", "MainWindow.axaml.cs");
        var dialogXaml = Read(root, "src", "InternalAssetLibrary.Client", "ChangePasswordWindow.axaml");
        var dialog = Read(root, "src", "InternalAssetLibrary.Client", "ChangePasswordWindow.axaml.cs");

        Contains("Click=\"ChangePassword_OnClick\"", xaml);
        Contains("PasswordChar=\"*\"", dialogXaml);
        Contains("CurrentPasswordBox", dialogXaml);
        Contains("ConfirmPasswordBox", dialogXaml);
        Contains("IsAcceptablePassword", dialog);
        Contains("new ApiChangePasswordRequest(", window);
        Contains("_rememberedPasswordStore.SaveAsync(_serverOrigin, userId, input.NewPassword)", window);
        Contains("account with { PasswordRemembered = false }", window);
    }

    public static void CloudAndDragRaceGuardsRemainWired()
    {
        var root = RepositoryRoot();
        var window = Read(root, "src", "InternalAssetLibrary.Client", "MainWindow.axaml.cs");
        var playback = Read(root, "src", "InternalAssetLibrary.Client", "MainWindow.Playback.cs");

        Contains("eventArgs.DataTransfer.Contains(DataFormat.File)", window);
        var transfers = Read(root, "src", "InternalAssetLibrary.Client", "MainWindow.Transfers.cs");
        Contains("_cloudUploadTransferGate.WaitAsync(token)", transfers);
        Contains("_cloudUploadTransferGate.Release()", transfers);
        Contains("_cloudAssetOpenCancellation?.Cancel();", window);
        Contains("_transferService.FindDownloadedAsync(", Slice(
            window,
            "private async Task<string?> ResolveCloudPreviewPathAsync(",
            "private async Task<string?> EnsureCloudAssetDownloadedAsync("));

        var selectedPreview = Slice(
            playback,
            "private async Task BeginSelectedCloudPreviewAsync(",
            "private async Task OpenDetailPreviewAsync(");
        Contains("allowOriginalDownload: false", selectedPreview);
        DoesNotContain("allowOriginalDownload: true", selectedPreview);

        var cloudOpen = Slice(
            playback,
            "private async void CloudAssetCard_OnDoubleTapped(",
            "private static bool IsProfessionalDesignFile(");
        Contains("_cloudAssetOpenGate.WaitAsync(0)", cloudOpen);
        Contains("await StopDetailPreviewAsync(savePosition: true)", cloudOpen);
        Contains("catch (OperationCanceledException)", cloudOpen);
        Contains("_cloudAssetOpenGate.Release()", cloudOpen);

        var detailPlayPause = Slice(
            playback,
            "private async void DetailPlayPause_OnClick(",
            "private async void PlayerPlayPause_OnClick(");
        Contains("_detailPreviewGate.WaitAsync(cancellationToken)", detailPlayPause);
        Contains("catch (Exception exception)", detailPlayPause);
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
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "InternalAssetLibrary.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
