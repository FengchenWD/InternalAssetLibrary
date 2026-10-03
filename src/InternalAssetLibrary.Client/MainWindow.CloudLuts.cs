using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using InternalAssetLibrary.Client.Services;
using InternalAssetLibrary.Client.ViewModels;
using InternalAssetLibrary.Contracts;

namespace InternalAssetLibrary.Client;

public sealed partial class MainWindow
{
    public ObservableCollection<ProfileLutCardViewModel> SharedCloudLuts { get; } = [];

    private async Task<IReadOnlyList<TeamLutSummary>> ListAllSharedCloudLutsAsync(
        CancellationToken cancellationToken)
    {
        if (_api is null)
        {
            return [];
        }

        const int pageSize = 200;
        var luts = new List<TeamLutSummary>();
        for (var pageNumber = 1; ; pageNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await _api.ListTeamLutsAsync(new Core.Http.ApiTeamLutListQuery(
                Page: pageNumber,
                PageSize: pageSize), cancellationToken);
            luts.AddRange(page.Items.Where(lut =>
                lut.State == TeamLutState.Active && lut.HasContent));
            if (page.Items.Count == 0 || luts.Count >= page.Total)
            {
                return luts;
            }
        }
    }

    private void RebuildSharedCloudLuts(IEnumerable<TeamLutSummary> luts)
    {
        var selectedIds = SharedCloudLuts
            .Where(item => item.IsSelected)
            .Select(item => item.Lut.Id)
            .ToHashSet();
        SharedCloudLuts.Clear();
        foreach (var lut in luts)
        {
            SharedCloudLuts.Add(new ProfileLutCardViewModel(lut)
            {
                IsSelected = selectedIds.Contains(lut.Id)
            });
        }

        UpdateCloudLutSelectionState();
    }

    private void ClearSharedCloudLuts()
    {
        SharedCloudLuts.Clear();
        CloudLutScroll.IsVisible = false;
        CloudLutListScroll.IsVisible = false;
        CloudLutEmptyState.IsVisible = false;
        UpdateCloudLutSelectionState();
    }

    private async void SharedCloudLutCard_OnTapped(object? sender, TappedEventArgs eventArgs)
    {
        if (eventArgs.Source is Control source &&
            (source is CheckBox || source.FindAncestorOfType<CheckBox>() is not null))
        {
            return;
        }

        if (sender is not Border { DataContext: ProfileLutCardViewModel selected })
        {
            return;
        }

        eventArgs.Handled = true;
        try
        {
            await CreateLutLibraryWindow(selected.Lut.Id)
                .ShowDialog(this);
            await ReloadLutsAsync();
            if (_cloudCategoryFilterIndex == 5 && SharedPage.IsVisible)
            {
                await RefreshCloudAssetsAsync();
            }
        }
        catch (Exception exception)
        {
            UiLocalization.SetText(
                CloudSummaryText,
                "LUT 列表刷新失败：{0}",
                UserMessage(exception));
        }
    }

    private void SharedCloudLutSelection_OnChanged(object? sender, RoutedEventArgs eventArgs)
    {
        if (!_isUpdatingCloudLutSelection)
        {
            UpdateCloudLutSelectionState();
        }
    }

    private void CloudLutSelectAll_OnChanged(object? sender, RoutedEventArgs eventArgs)
    {
        if (_isUpdatingCloudLutSelection || CloudLutSelectAllCheckBox.IsChecked is not { } isChecked)
        {
            return;
        }

        _isUpdatingCloudLutSelection = true;
        try
        {
            foreach (var lut in SharedCloudLuts)
            {
                lut.IsSelected = isChecked;
            }
        }
        finally
        {
            _isUpdatingCloudLutSelection = false;
        }

        UpdateCloudLutSelectionState();
    }

    private void UpdateCloudLutSelectionState()
    {
        var selectedCount = SharedCloudLuts.Count(item => item.IsSelected);
        _isUpdatingCloudLutSelection = true;
        try
        {
            CloudLutSelectAllCheckBox.IsEnabled =
                !_isCloudLutBatchOperationRunning && SharedCloudLuts.Count > 0;
            CloudLutSelectAllCheckBox.IsChecked = selectedCount switch
            {
                0 => false,
                _ when selectedCount == SharedCloudLuts.Count => true,
                _ => null
            };
        }
        finally
        {
            _isUpdatingCloudLutSelection = false;
        }

        UiLocalization.SetText(
            CloudLutSelectionSummaryText,
            selectedCount == 0 ? "未选择 LUT" : "已选 {0:N0} 个 LUT",
            selectedCount);
        BatchDownloadCloudLutsButton.IsEnabled =
            !_isCloudLutBatchOperationRunning && selectedCount > 0 && _api is not null;
    }

    private async void BatchDownloadCloudLuts_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_api is null || _isCloudLutBatchOperationRunning)
        {
            return;
        }

        var selected = SharedCloudLuts
            .Where(item => item.IsSelected)
            .Select(item => item.Lut)
            .ToArray();
        if (selected.Length == 0)
        {
            return;
        }

        var targetDirectory = await ChooseDownloadDirectoryAsync(
            UiLocalization.Text("选择团队 LUT 批量下载目录"));
        if (targetDirectory is null)
        {
            UiLocalization.SetText(CloudSummaryText, "已取消 LUT 批量下载。");
            return;
        }

        _isCloudLutBatchOperationRunning = true;
        UpdateCloudLutSelectionState();
        EmptyTransfersText.IsVisible = false;
        var succeeded = 0;
        var failed = 0;
        try
        {
            var jobs = selected.Select(lut => QueueLutDownload(lut, targetDirectory)).ToArray();
            foreach (var job in jobs)
            {
                try
                {
                    await job.Work;
                    succeeded++;
                }
                catch (Exception)
                {
                    failed++;
                }
            }

            UiLocalization.SetText(
                CloudSummaryText,
                failed == 0
                    ? "LUT 批量下载完成：{0:N0} 个文件。"
                    : "LUT 批量下载完成：成功 {0:N0} 个，失败 {1:N0} 个；可在传输任务中查看详情。",
                succeeded,
                failed);
            if (succeeded > 0)
            {
                ShowTransientNotification("下载完成：{0:N0} 个 LUT", succeeded);
            }
        }
        finally
        {
            _isCloudLutBatchOperationRunning = false;
            UpdateCloudLutSelectionState();
        }
    }

    private static string UniqueCloudLutPath(string directory, TeamLutSummary lut)
    {
        var originalName = Path.GetFileName(lut.OriginalFileName);
        var stem = SafeCloudLutFileName(Path.GetFileNameWithoutExtension(originalName));
        if (string.IsNullOrWhiteSpace(stem))
        {
            stem = SafeCloudLutFileName(lut.Name);
        }

        if (string.IsNullOrWhiteSpace(stem))
        {
            stem = "LUT";
        }

        var path = Path.Combine(directory, $"{stem}.cube");
        for (var suffix = 2; File.Exists(path); suffix++)
        {
            path = Path.Combine(directory, $"{stem}-{suffix}.cube");
        }

        return path;
    }

    private static string SafeCloudLutFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(value.Trim().Select(character => invalid.Contains(character) ? '_' : character));
    }

    private static void TryDeleteDownloadTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private async Task<CloudLutBatchUploadResult> UploadCloudLutsAsync(
        IReadOnlyList<string> paths)
    {
        if (_api is null || paths.Count == 0)
        {
            return default;
        }

        EmptyTransfersText.IsVisible = false;
        var succeeded = 0;
        var failed = 0;
        var jobs = paths.Select(path => QueueLutUpload(path)).ToArray();
        foreach (var job in jobs)
        {
            try
            {
                await job.Work;
                succeeded++;
            }
            catch (Exception)
            {
                failed++;
            }
        }

        await ReloadLutsAsync();
        return new CloudLutBatchUploadResult(succeeded, failed);
    }

    private readonly record struct CloudLutBatchUploadResult(int Succeeded, int Failed);

    private readonly record struct CloudMediaBatchUploadResult(
        int Succeeded,
        int Skipped,
        int Failed,
        int DerivativeWarnings);

    private sealed record CloudFolderUploadRoot(
        string Name,
        IReadOnlyList<string> RelativeDirectories,
        IReadOnlyList<CloudFolderUploadFile> MediaFiles,
        int UnsupportedFileCount);

    private sealed record CloudFolderUploadFile(string FullPath, string RelativeDirectory);
}
