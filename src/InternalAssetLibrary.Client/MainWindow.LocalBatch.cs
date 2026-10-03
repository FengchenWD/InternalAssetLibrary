using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using InternalAssetLibrary.Client.Core.LocalAssets;
using InternalAssetLibrary.Client.Services;
using InternalAssetLibrary.Client.ViewModels;

namespace InternalAssetLibrary.Client;

public sealed partial class MainWindow
{
    private bool _isLocalBatchMode;
    private bool _isUpdatingLocalSelection;
    private bool _isLocalBatchOperationRunning;
    private CancellationTokenSource? _localBatchOperationCancellation;

    private void LocalBatchMode_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        _isLocalBatchMode = LocalBatchModeButton.IsChecked == true;
        _isUpdatingLocalSelection = true;
        try
        {
            foreach (var asset in VisibleLocalAssets)
            {
                asset.IsBatchMode = _isLocalBatchMode;
                if (!_isLocalBatchMode)
                {
                    asset.IsBatchSelected = false;
                }
            }
        }
        finally
        {
            _isUpdatingLocalSelection = false;
        }

        LocalBatchToolbar.IsVisible = _isLocalBatchMode;
        UpdateLocalSelectionState();
    }

    private void LocalAssetSelection_OnChanged(object? sender, RoutedEventArgs eventArgs)
    {
        if (_isUpdatingLocalSelection)
        {
            return;
        }

        if (sender is CheckBox
            {
                DataContext: AssetCardViewModel asset,
                IsChecked: { } isChecked
            })
        {
            asset.IsBatchSelected = isChecked;
        }

        UpdateLocalSelectionState();
    }

    private void LocalSelectAll_OnChanged(object? sender, RoutedEventArgs eventArgs)
    {
        if (_isUpdatingLocalSelection || LocalSelectAllCheckBox.IsChecked is not { } isChecked)
        {
            return;
        }

        _isUpdatingLocalSelection = true;
        try
        {
            foreach (var asset in VisibleLocalAssets)
            {
                asset.IsBatchSelected = isChecked;
            }
        }
        finally
        {
            _isUpdatingLocalSelection = false;
        }

        UpdateLocalSelectionState();
    }

    private void UpdateLocalSelectionState()
    {
        if (!_isLocalBatchMode)
        {
            LocalBatchToolbar.IsVisible = false;
        }

        var selected = SelectedLocalBatchAssets();
        var allAvailable = selected.Length > 0 && selected.All(IsLocalAssetAvailable);
        var unavailableCount = selected.Count(asset => !IsLocalAssetAvailable(asset));

        _isUpdatingLocalSelection = true;
        try
        {
            LocalSelectAllCheckBox.IsEnabled = _isLocalBatchMode &&
                                               !_isLocalBatchOperationRunning &&
                                               VisibleLocalAssets.Count > 0;
            LocalSelectAllCheckBox.IsChecked = selected.Length switch
            {
                0 => false,
                _ when selected.Length == VisibleLocalAssets.Count => true,
                _ => null
            };
        }
        finally
        {
            _isUpdatingLocalSelection = false;
        }

        UiLocalization.SetText(
            LocalSelectionSummaryText,
            selected.Length switch
            {
                0 => "未选择素材",
                _ when unavailableCount > 0 => "已选 {0:N0} 项，其中 {1:N0} 项当前不可用",
                _ => "已选 {0:N0} 项"
            },
            selected.Length,
            unavailableCount);
        LocalBatchModeButton.IsEnabled = !_isLocalBatchOperationRunning;
        BatchSaveLocalAssetsButton.IsEnabled = !_isLocalBatchOperationRunning && allAvailable;
        BatchTagLocalAssetsButton.IsEnabled = !_isLocalBatchOperationRunning && selected.Length > 0;
        BatchRenameLocalAssetsButton.IsEnabled = !_isLocalBatchOperationRunning && allAvailable;
        BatchRecycleLocalAssetsButton.IsEnabled = !_isLocalBatchOperationRunning && allAvailable;
    }

    private AssetCardViewModel[] SelectedLocalBatchAssets() =>
        VisibleLocalAssets.Where(asset => asset.IsBatchSelected).ToArray();

    private static bool IsLocalAssetAvailable(AssetCardViewModel asset) =>
        asset.IsAvailable && File.Exists(asset.FullPath);

    private void SetLocalBatchOperationRunning(bool isRunning)
    {
        _isLocalBatchOperationRunning = isRunning;
        UpdateLocalSelectionState();
    }

    private void CancelLocalBatchOperation_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        CancelLocalBatchOperationButton.IsEnabled = false;
        UiLocalization.SetText(LocalBatchProgressText, "正在取消批量操作…");
        _localBatchOperationCancellation?.Cancel();
    }

    private void UpdateLocalBatchProgress(int completed, int total)
    {
        LocalBatchProgressBar.Value = total == 0 ? 0 : completed * 100d / total;
        UiLocalization.SetText(LocalBatchProgressText, "正在删除 {0:N0} / {1:N0}", completed, total);
    }

    private async void BatchSaveLocalAssets_OnClick(object? sender, RoutedEventArgs eventArgs) =>
        await RunGuardedInteractionAsync(
            "batch-save-local-assets",
            BatchSaveLocalAssetsAsync,
            LocalSummaryText);

    private async Task BatchSaveLocalAssetsAsync()
    {
        var selected = SelectedLocalBatchAssets();
        if (selected.Length == 0 || selected.Any(asset => !IsLocalAssetAvailable(asset)))
        {
            return;
        }

        var folders = await RunNativePickerAsync(
            "batch-save-local-assets",
            () => StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = UiLocalization.Text("选择本地素材批量另存为目录"),
                AllowMultiple = false
            }),
            LocalSummaryText);
        var targetDirectory = folders is { Count: > 0 }
            ? folders[0].Path.LocalPath
            : null;
        if (string.IsNullOrWhiteSpace(targetDirectory))
        {
            UiLocalization.SetText(LocalSummaryText, "已取消批量另存为。");
            return;
        }

        SetLocalBatchOperationRunning(true);
        try
        {
            var succeeded = 0;
            var failed = 0;
            foreach (var asset in selected)
            {
                try
                {
                    await CopyLocalAssetToDirectoryAsync(asset.FullPath, targetDirectory);
                    succeeded++;
                }
                catch (Exception exception)
                {
                    failed++;
                    ClientDiagnostics.WriteException("batch-save-local-asset", exception, isFatal: false);
                }
            }

            UiLocalization.SetText(
                LocalSummaryText,
                failed == 0
                    ? "批量另存为完成：{0:N0} 个文件。"
                    : "批量另存为完成：成功 {0:N0} 个，失败 {1:N0} 个。",
                succeeded,
                failed);
        }
        finally
        {
            SetLocalBatchOperationRunning(false);
        }
    }

    private static async Task<string> CopyLocalAssetToDirectoryAsync(
        string sourcePath,
        string targetDirectory) =>
        await AtomicLocalFileCopy.CopyToUniqueFileAsync(sourcePath, targetDirectory);

    private async void BatchTagLocalAssets_OnClick(object? sender, RoutedEventArgs eventArgs) =>
        await RunGuardedInteractionAsync(
            "batch-tag-local-assets",
            BatchTagLocalAssetsAsync,
            LocalSummaryText);

    private async Task BatchTagLocalAssetsAsync()
    {
        var selected = SelectedLocalBatchAssets();
        if (selected.Length == 0)
        {
            return;
        }

        var selectedIds = selected.Select(asset => asset.Id).ToArray();
        var tagLibraryChanged = false;
        var result = await new TagPickerWindow(
                "批量添加标签",
                UiLocalization.Format(
                    "为已选的 {0:N0} 个本地素材追加标签，原有标签会保留。",
                    selected.Length),
                _catalog.TagLibrary.Select(name => new TagPickerEntry(
                    null,
                    name,
                    _catalog.Assets.Count(asset => asset.Tags.Contains(
                        name,
                        StringComparer.OrdinalIgnoreCase)))),
                selectedTags: [],
                async name =>
                {
                    _catalog = await _indexService.CreateTagAsync(name);
                    tagLibraryChanged = true;
                    var persistedName = _catalog.TagLibrary.Single(tag =>
                        tag.Equals(name, StringComparison.OrdinalIgnoreCase));
                    return new TagPickerEntry(null, persistedName, 0);
                },
                async entry =>
                {
                    _catalog = await _indexService.DeleteTagAsync(entry.Name);
                    tagLibraryChanged = true;
                })
            .ShowDialog<TagPickerResult?>(this);
        if (result is null)
        {
            if (tagLibraryChanged)
            {
                ApplyLocalFilter();
            }

            return;
        }

        if (result.Tags.Count == 0)
        {
            UiLocalization.SetText(LocalSummaryText, "未选择要追加的标签。");
            if (tagLibraryChanged)
            {
                ApplyLocalFilter();
            }

            return;
        }

        SetLocalBatchOperationRunning(true);
        try
        {
            _catalog = await _indexService.AddTagsToAssetsAsync(selectedIds, result.Tags);
            foreach (var asset in VisibleLocalAssets)
            {
                asset.IsBatchSelected = false;
            }

            ApplyLocalFilter();
            UiLocalization.SetText(LocalSummaryText, "已为 {0:N0} 个本地素材追加标签。", selectedIds.Length);
        }
        finally
        {
            SetLocalBatchOperationRunning(false);
        }
    }

    private async void BatchRenameLocalAssets_OnClick(object? sender, RoutedEventArgs eventArgs) =>
        await RunGuardedInteractionAsync(
            "batch-rename-local-assets",
            BatchRenameLocalAssetsAsync,
            LocalSummaryText);

    private async Task BatchRenameLocalAssetsAsync()
    {
        var selected = SelectedLocalBatchAssets();
        if (selected.Length == 0 || selected.Any(asset => !IsLocalAssetAvailable(asset)))
        {
            return;
        }

        var rename = await new LocalBatchRenameWindow(
                selected.Select(asset => Path.GetFileName(asset.FullPath)).ToArray())
            .ShowDialog<LocalBatchRenameResult?>(this);
        if (rename is null)
        {
            return;
        }

        var preview = string.Join(
            Environment.NewLine,
            rename.FileNames.Take(5).Select(name => $"• {name}"));
        if (rename.FileNames.Count > 5)
        {
            preview += Environment.NewLine + UiLocalization.Format(
                "…另有 {0:N0} 个文件",
                rename.FileNames.Count - 5);
        }

        var confirmed = await new MessageDialogWindow(
                "确认批量重命名",
                UiLocalization.Format(
                    "将按以下规则重命名 {0:N0} 个真实文件：\n\n{1}\n\n文件扩展名保持不变。",
                    selected.Length,
                    preview),
                "确认重命名",
                "取消")
            .ShowDialog<bool>(this);
        if (!confirmed)
        {
            return;
        }

        SetLocalBatchOperationRunning(true);
        try
        {
            CloseLocalImagePreview();
            await StopDetailPreviewAsync(savePosition: true);
            var requests = selected.Select((asset, index) =>
                    new LocalAssetRenameRequest(asset.Id, rename.FileNames[index]))
                .ToArray();
            _catalog = await _indexService.RenameAssetsAsync(requests);
            foreach (var asset in VisibleLocalAssets)
            {
                asset.IsBatchSelected = false;
            }

            ApplyLocalFilter();
            UiLocalization.SetText(LocalSummaryText, "已批量重命名 {0:N0} 个文件。", requests.Length);
        }
        finally
        {
            SetLocalBatchOperationRunning(false);
        }
    }

    private async void BatchRecycleLocalAssets_OnClick(object? sender, RoutedEventArgs eventArgs) =>
        await RunGuardedInteractionAsync(
            "batch-recycle-local-assets",
            BatchRecycleLocalAssetsAsync,
            LocalSummaryText);

    private async Task BatchRecycleLocalAssetsAsync()
    {
        var selected = SelectedLocalBatchAssets();
        if (selected.Length == 0 || selected.Any(asset => !IsLocalAssetAvailable(asset)))
        {
            return;
        }

        var confirmed = await new MessageDialogWindow(
                "批量删除本地文件",
                UiLocalization.Format(
                    "选中的 {0:N0} 个真实文件将移入 Windows 回收站，并从本地素材目录中移除。",
                    selected.Length),
                "确认删除",
                "取消")
            .ShowDialog<bool>(this);
        if (!confirmed)
        {
            return;
        }

        SetLocalBatchOperationRunning(true);
        var cancellation = new CancellationTokenSource();
        _localBatchOperationCancellation = cancellation;
        LocalBatchProgressPanel.IsVisible = true;
        CancelLocalBatchOperationButton.IsEnabled = true;
        UpdateLocalBatchProgress(0, selected.Length);
        try
        {
            CloseLocalImagePreview();
            await StopDetailPreviewAsync(savePosition: true);
            var succeeded = 0;
            var failedIds = new HashSet<Guid>();
            var retainedIds = selected.Select(asset => asset.Id).ToHashSet();
            var canceled = false;
            for (var index = 0; index < selected.Length; index++)
            {
                var asset = selected[index];
                try
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    _catalog = await _indexService.RecycleAssetAsync(asset.Id, cancellation.Token);
                    retainedIds.Remove(asset.Id);
                    succeeded++;
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    canceled = true;
                    break;
                }
                catch (Exception exception)
                {
                    failedIds.Add(asset.Id);
                    ClientDiagnostics.WriteException("batch-recycle-local-asset", exception, isFatal: false);
                }

                UpdateLocalBatchProgress(index + 1, selected.Length);
            }

            foreach (var asset in VisibleLocalAssets)
            {
                asset.IsBatchSelected = retainedIds.Contains(asset.Id);
            }

            ApplyLocalFilter();
            if (canceled)
            {
                UiLocalization.SetText(
                    LocalSummaryText,
                    "批量删除已取消：成功 {0:N0} 个，失败 {1:N0} 个，未处理 {2:N0} 个；其余项目保持选中。",
                    succeeded,
                    failedIds.Count,
                    selected.Length - succeeded - failedIds.Count);
            }
            else
            {
                UiLocalization.SetText(
                    LocalSummaryText,
                    failedIds.Count == 0
                        ? "已将 {0:N0} 个本地文件移入 Windows 回收站。"
                        : "批量删除完成：成功 {0:N0} 个，失败 {1:N0} 个；失败项已保留选中。",
                    succeeded,
                    failedIds.Count);
            }
        }
        finally
        {
            if (ReferenceEquals(_localBatchOperationCancellation, cancellation))
            {
                _localBatchOperationCancellation = null;
            }

            cancellation.Dispose();
            LocalBatchProgressPanel.IsVisible = false;
            SetLocalBatchOperationRunning(false);
        }
    }
}
