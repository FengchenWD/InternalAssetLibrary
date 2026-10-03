internal static class ClientP64RegressionSourceSelfTests
{
    public static void CloudFoldersWaveformsAndNotificationsStayWired()
    {
        var root = RepositoryRoot();
        var clientDirectory = Path.Combine(root, "src", "InternalAssetLibrary.Client");
        var xaml = File.ReadAllText(Path.Combine(clientDirectory, "MainWindow.axaml"));
        var mainSource = File.ReadAllText(Path.Combine(clientDirectory, "MainWindow.axaml.cs"));
        var folderSource = File.ReadAllText(Path.Combine(clientDirectory, "MainWindow.CloudFolders.cs"));
        var realtimeSource = File.ReadAllText(Path.Combine(clientDirectory, "MainWindow.Realtime.cs"));
        var notificationSource = File.ReadAllText(Path.Combine(clientDirectory, "MainWindow.Notifications.cs"));
        var derivativeSource = File.ReadAllText(Path.Combine(
            root,
            "src",
            "InternalAssetLibrary.Client.Core",
            "MediaAnalysis",
            "CloudMediaDerivativeService.cs"));
        var normalizedXaml = xaml.Replace("\r\n", "\n", StringComparison.Ordinal);

        Contains("x:Name=\"CloudFolderSidebar\"", xaml);
        Contains("x:Name=\"AllCloudFoldersButton\"", xaml);
        Contains("x:Name=\"CloudFolderTree\"", xaml);
        Contains("ItemsSource=\"{Binding CompactCloudFolderNodes}\"", xaml);
        Contains("Click=\"CreateCloudFolder_OnClick\">新建文件夹", xaml);
        Equal(2, CountOccurrences(xaml, "ContextRequested=\"CloudAsset_OnContextRequested\""));
        Contains("x:Name=\"TransientNotification\"", xaml);
        Contains("IsHitTestVisible=\"False\"", Section(
            normalizedXaml,
            "x:Name=\"TransientNotification\"",
            "</Border>\n  </Grid>"));

        Contains("ObserveNavigationTask(RefreshCloudLibraryAsync(), \"refresh-cloud-library\")", mainSource);
        Contains("FolderId: _selectedCloudFolderId", mainSource);
        Contains("IncludeDescendantFolders: CloudRecursiveSearchToggle.IsChecked == true", mainSource);
        Contains("RootOnly: _selectedCloudFolderId is null", mainSource);
        Contains("QueueUpload(path, category, options.Notes, options.Tags, targetFolderId)", mainSource);
        Contains("QueueUpload(selected.FullPath, category, options.Notes, canonicalTags, targetFolderId)", mainSource);
        var transfers = File.ReadAllText(Path.Combine(root, "src", "InternalAssetLibrary.Client", "MainWindow.Transfers.cs"));
        Contains("progress, token, record.FolderId", transfers);
        Contains("PrepareCloudUploadAsync(paths, _selectedCloudFolderId)", mainSource);
        Contains("ResolvePendingCloudDropAsync(pendingTransfer, targetFolderId)", mainSource);
        Contains("ShowTransientNotification(\"下载完成：{0}\"", mainSource);
        Contains("ShowTransientNotification(\"下载完成：{0:N0} 个素材\"", mainSource);

        Contains("private async Task RefreshCloudLibraryAsync", folderSource);
        Contains("await RefreshCloudFoldersAsync(cancellationToken)", folderSource);
        Contains("NearestExistingCloudFolder", folderSource);
        Contains("UiLocalization.Format(\"{0}失败\", localizedOperation)", folderSource);
        Contains("MoveAssetToFolderAsync", folderSource);
        Contains("DeleteAssetFolderAsync", folderSource);
        Equal(2, CountOccurrences(realtimeSource, "tasks.Add(RefreshCloudLibraryAsync())"));

        Contains("Task.Delay(TimeSpan.FromSeconds(3.5)", notificationSource);
        Contains("TransientNotification.Opacity = 0", notificationSource);
        Contains("UsesWaveformThumbnail(ApiAssetCategory category)", derivativeSource);
        Contains("GetOrCreateWaveformAsync", derivativeSource);
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

    private static int CountOccurrences(string source, string value)
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
