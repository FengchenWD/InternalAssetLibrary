using Avalonia.Controls;
using Avalonia.Interactivity;
using InternalAssetLibrary.Client.Services;
using InternalAssetLibrary.Client.ViewModels;

namespace InternalAssetLibrary.Client;

public sealed partial class MainWindow
{
    private bool _isLocalFileSystemMenuActionRunning;

    private ContextMenu CreateLocalAssetContextMenu(AssetCardViewModel asset)
    {
        var canAccessFile = asset.IsAvailable && File.Exists(asset.FullPath);
        var openFileItem = new MenuItem
        {
            Header = UiLocalization.Text("打开文件"),
            Tag = asset,
            IsEnabled = canAccessFile
        };
        openFileItem.Click += OpenLocalAssetFile_OnClick;

        var editTagsItem = new MenuItem
        {
            Header = UiLocalization.Text("编辑标签"),
            Tag = asset
        };
        editTagsItem.Click += EditLocalAssetTagsFromContext_OnClick;

        var openLocationItem = new MenuItem
        {
            Header = UiLocalization.Text("打开文件所在位置"),
            Tag = asset,
            IsEnabled = canAccessFile
        };
        openLocationItem.Click += OpenLocalAssetLocation_OnClick;

        var renameItem = new MenuItem
        {
            Header = UiLocalization.Text("重命名"),
            Tag = asset,
            IsEnabled = canAccessFile
        };
        renameItem.Click += RenameLocalAsset_OnClick;

        var deleteItem = new MenuItem
        {
            Header = UiLocalization.Text("删除"),
            Tag = asset,
            IsEnabled = canAccessFile
        };
        deleteItem.Click += RecycleLocalAsset_OnClick;

        return new ContextMenu
        {
            ItemsSource = new object[]
            {
                openFileItem,
                editTagsItem,
                openLocationItem,
                new Separator(),
                renameItem,
                deleteItem
            }
        };
    }

    private ContextMenu CreateLocalFolderContextMenu(LocalFolderNodeViewModel node)
    {
        var canAccessFolder = !node.IsOffline && Directory.Exists(node.FullPath);
        var canChangeFolder = canAccessFolder && node.FolderId.HasValue;
        var canRenameOrRecycle = canChangeFolder && !IsFileSystemRoot(node.FullPath);

        var openItem = new MenuItem
        {
            Header = UiLocalization.Text("打开文件夹所在位置"),
            Tag = node,
            IsEnabled = canAccessFolder
        };
        openItem.Click += OpenLocalFolderLocation_OnClick;

        var createItem = new MenuItem
        {
            Header = UiLocalization.Text("新建子文件夹"),
            Tag = node,
            IsEnabled = canChangeFolder
        };
        createItem.Click += CreateLocalSubdirectory_OnClick;

        var renameItem = new MenuItem
        {
            Header = UiLocalization.Text("重命名"),
            Tag = node,
            IsEnabled = canRenameOrRecycle
        };
        renameItem.Click += RenameLocalDirectory_OnClick;

        var deleteItem = new MenuItem
        {
            Header = UiLocalization.Text("删除文件夹"),
            Tag = node,
            IsEnabled = canRenameOrRecycle
        };
        deleteItem.Click += RecycleLocalDirectory_OnClick;

        var uploadItem = new MenuItem
        {
            Header = UiLocalization.Text("上传文件夹及素材到云端"),
            Tag = node,
            IsEnabled = canAccessFolder && HasCurrentUserPermission("assets.upload")
        };
        uploadItem.Click += UploadLocalFolderToCloud_OnClick;

        var items = new List<object>
        {
            openItem,
            uploadItem,
            new Separator(),
            createItem,
            renameItem,
            deleteItem
        };
        if (node.CanRemoveFromCatalog)
        {
            var removeItem = new MenuItem
            {
                Header = UiLocalization.Text("从目录移除"),
                Tag = node
            };
            removeItem.Click += RemoveLocalFolderFromCatalog_OnClick;
            items.Add(new Separator());
            items.Add(removeItem);
        }

        return new ContextMenu { ItemsSource = items };
    }

    private void SelectLocalAssetForContextMenu(AssetCardViewModel asset)
    {
        ResetPendingDragGesture();
        CloseLocalImagePreview();
        CommitHoverDetailSelection();
        if (_selectedLocalAsset?.Id != asset.Id)
        {
            SelectLocalAsset(asset);
        }

        ObserveNavigationTask(
            StopDetailPreviewAsync(savePosition: true),
            "stop-local-preview-for-context-menu");
    }

    private void SelectLocalFolderNodeForContext(LocalFolderNodeViewModel node)
    {
        if (node.FolderId is not { } folderId)
        {
            return;
        }

        var selectedAssetId = _selectedLocalAsset?.Id;
        _selectedLocalFolderId = folderId;
        _selectedLocalRelativeDirectoryPath = string.IsNullOrEmpty(node.RelativePath)
            ? null
            : node.RelativePath;
        _suppressLocalFolderSelectionChanged = true;
        try
        {
            LocalFolderTree.SelectedItem = node;
        }
        finally
        {
            _suppressLocalFolderSelectionChanged = false;
        }

        UpdateLocalFolderSelectionState();
        ApplyLocalFilter();
        RestoreLocalAssetSelection(selectedAssetId);
    }

    private async void OpenLocalAssetFile_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not MenuItem { Tag: AssetCardViewModel asset })
        {
            return;
        }

        await RunLocalFileSystemMenuActionAsync(
            "open-local-asset-file",
            "无法打开文件",
            () =>
            {
                EnsureLocalAssetFileAvailable(asset);
                OpenWithSystemDefault(asset.FullPath);
                return Task.CompletedTask;
            });
    }

    private void EditLocalAssetTagsFromContext_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not MenuItem { Tag: AssetCardViewModel asset })
        {
            return;
        }

        if (_selectedLocalAsset?.Id != asset.Id)
        {
            SelectLocalAssetForContextMenu(asset);
        }

        EditLocalTags_OnClick(null, new RoutedEventArgs());
    }

    private async void RenameLocalAsset_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not MenuItem { Tag: AssetCardViewModel asset })
        {
            return;
        }

        await RunLocalFileSystemMenuActionAsync(
            "rename-local-asset",
            "无法重命名文件",
            async () =>
            {
                EnsureLocalAssetFileAvailable(asset);
                var currentName = Path.GetFileName(asset.FullPath);
                var newName = await new TextPromptWindow(
                        "重命名文件",
                        "输入新的文件名；文件扩展名不能更改。",
                        currentName)
                    .ShowDialog<string?>(this);
                if (newName is null || string.Equals(currentName, newName.Trim(), StringComparison.Ordinal))
                {
                    return;
                }

                CloseLocalImagePreview();
                await StopDetailPreviewAsync(savePosition: true);
                _catalog = await _indexService.RenameAssetAsync(asset.Id, newName);
                RefreshLocalCatalogViews(asset.Id);
                UiLocalization.SetText(
                    LocalSummaryText,
                    "文件已重命名为“{0}”。",
                    Path.GetFileName(_catalog.Assets.Single(item => item.Id == asset.Id).FullPath));
            });
    }

    private async void RecycleLocalAsset_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not MenuItem { Tag: AssetCardViewModel asset })
        {
            return;
        }

        await RunLocalFileSystemMenuActionAsync(
            "recycle-local-asset",
            "无法删除文件",
            async () =>
            {
                EnsureLocalAssetFileAvailable(asset);
                var fileName = Path.GetFileName(asset.FullPath);
                var confirmed = await new MessageDialogWindow(
                        "删除本地文件",
                        UiLocalization.Format(
                            "“{0}”将移入 Windows 回收站，并从本地素材目录中移除。",
                            fileName),
                        "确认删除",
                        "取消")
                    .ShowDialog<bool>(this);
                if (!confirmed)
                {
                    return;
                }

                CloseLocalImagePreview();
                await StopDetailPreviewAsync(savePosition: true);
                _catalog = await _indexService.RecycleAssetAsync(asset.Id);
                RefreshLocalCatalogViews(preferredAssetId: null);
                UiLocalization.SetText(
                    LocalSummaryText,
                    "“{0}”已移入 Windows 回收站。",
                    fileName);
            });
    }

    private async void CreateLocalSubdirectory_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not MenuItem
            {
                Tag: LocalFolderNodeViewModel { FolderId: { } folderId } node
            })
        {
            return;
        }

        await RunLocalFileSystemMenuActionAsync(
            "create-local-subdirectory",
            "无法新建文件夹",
            async () =>
            {
                EnsureLocalFolderAvailable(node);
                var name = await new TextPromptWindow(
                        "新建子文件夹",
                        "输入新文件夹名称。")
                    .ShowDialog<string?>(this);
                if (name is null)
                {
                    return;
                }

                _catalog = await _indexService.CreateSubdirectoryAsync(
                    folderId,
                    node.RelativePath,
                    name);
                _selectedLocalFolderId = folderId;
                _selectedLocalRelativeDirectoryPath = NormalizeRelativePath(
                    Path.Combine(node.RelativePath, name.Trim()));
                RefreshLocalCatalogViews(_selectedLocalAsset?.Id);
                UiLocalization.SetText(LocalSummaryText, "文件夹“{0}”已创建。", name.Trim());
            });
    }

    private async void RenameLocalDirectory_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not MenuItem
            {
                Tag: LocalFolderNodeViewModel { FolderId: { } folderId } node
            })
        {
            return;
        }

        await RunLocalFileSystemMenuActionAsync(
            "rename-local-directory",
            "无法重命名文件夹",
            async () =>
            {
                EnsureLocalFolderAvailable(node);
                if (IsFileSystemRoot(node.FullPath))
                {
                    throw new InvalidOperationException("不能重命名文件系统根目录。");
                }

                var newName = await new TextPromptWindow(
                        "重命名文件夹",
                        "输入新的文件夹名称。",
                        node.DisplayName)
                    .ShowDialog<string?>(this);
                if (newName is null || string.Equals(node.DisplayName, newName.Trim(), StringComparison.Ordinal))
                {
                    return;
                }

                var selectedAssetId = _selectedLocalAsset?.Id;
                CloseLocalImagePreview();
                await StopDetailPreviewAsync(savePosition: true);
                _catalog = await _indexService.RenameDirectoryAsync(
                    folderId,
                    node.RelativePath,
                    newName);
                _selectedLocalFolderId = folderId;
                _selectedLocalRelativeDirectoryPath = node.RelativePath.Length == 0
                    ? null
                    : NormalizeRelativePath(Path.Combine(
                        Path.GetDirectoryName(node.RelativePath) ?? string.Empty,
                        newName.Trim()));
                RefreshLocalCatalogViews(selectedAssetId);
                UiLocalization.SetText(LocalSummaryText, "文件夹已重命名为“{0}”。", newName.Trim());
            });
    }

    private async void RecycleLocalDirectory_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not MenuItem
            {
                Tag: LocalFolderNodeViewModel { FolderId: { } folderId } node
            })
        {
            return;
        }

        await RunLocalFileSystemMenuActionAsync(
            "recycle-local-directory",
            "无法删除文件夹",
            async () =>
            {
                EnsureLocalFolderAvailable(node);
                if (IsFileSystemRoot(node.FullPath))
                {
                    throw new InvalidOperationException("不能删除文件系统根目录。");
                }

                var confirmed = await new MessageDialogWindow(
                        "删除本地文件夹",
                        UiLocalization.Format(
                            "文件夹“{0}”及其中的文件将移入 Windows 回收站，并从本地素材目录中移除。",
                            node.DisplayName),
                        "确认删除",
                        "取消")
                    .ShowDialog<bool>(this);
                if (!confirmed)
                {
                    return;
                }

                var selectedAssetId = _selectedLocalAsset?.Id;
                CloseLocalImagePreview();
                await StopDetailPreviewAsync(savePosition: true);
                _catalog = await _indexService.RecycleDirectoryAsync(folderId, node.RelativePath);
                if (node.RelativePath.Length == 0)
                {
                    _selectedLocalFolderId = null;
                    _selectedLocalRelativeDirectoryPath = null;
                }
                else
                {
                    _selectedLocalFolderId = folderId;
                    _selectedLocalRelativeDirectoryPath = EmptyToNull(
                        NormalizeRelativePath(Path.GetDirectoryName(node.RelativePath)));
                }

                RefreshLocalCatalogViews(selectedAssetId);
                UiLocalization.SetText(
                    LocalSummaryText,
                    "文件夹“{0}”已移入 Windows 回收站。",
                    node.DisplayName);
            });
    }

    private async Task RunLocalFileSystemMenuActionAsync(
        string operation,
        string failureTitle,
        Func<Task> action)
    {
        if (_shutdownStarted)
        {
            return;
        }

        if (_isLocalFileSystemMenuActionRunning)
        {
            UiLocalization.SetText(LocalSummaryText, "已有本地文件操作正在进行，请完成后再试。");
            return;
        }

        _isLocalFileSystemMenuActionRunning = true;
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ClientDiagnostics.WriteException(operation, exception, isFatal: false);
            if (!_shutdownStarted)
            {
                try
                {
                    await new MessageDialogWindow(failureTitle, UserMessage(exception)).ShowDialog(this);
                }
                catch (Exception dialogException)
                {
                    ClientDiagnostics.WriteException(
                        $"{operation}-error-dialog",
                        dialogException,
                        isFatal: false);
                }
            }
        }
        finally
        {
            _isLocalFileSystemMenuActionRunning = false;
        }
    }

    private void RefreshLocalCatalogViews(Guid? preferredAssetId)
    {
        if (preferredAssetId is null || _catalog.Assets.All(asset => asset.Id != preferredAssetId.Value))
        {
            CloseDetail_OnClick(null, new RoutedEventArgs());
        }

        RebuildLocalFolderTree();
        ApplyLocalFilter();
        RestoreLocalAssetSelection(preferredAssetId);
    }

    private void RestoreLocalAssetSelection(Guid? assetId)
    {
        if (assetId is null)
        {
            return;
        }

        var card = VisibleLocalAssets.FirstOrDefault(asset => asset.Id == assetId.Value);
        if (card is null)
        {
            CloseDetail_OnClick(null, new RoutedEventArgs());
        }
        else
        {
            SelectLocalAsset(card);
        }
    }

    private static void EnsureLocalAssetFileAvailable(AssetCardViewModel asset)
    {
        if (!asset.IsAvailable || !File.Exists(asset.FullPath))
        {
            throw new FileNotFoundException(
                "该文件当前不可用。请重新连接存储设备并刷新素材库后再试。",
                asset.FullPath);
        }
    }

    private static void EnsureLocalFolderAvailable(LocalFolderNodeViewModel node)
    {
        if (node.IsOffline || !Directory.Exists(node.FullPath))
        {
            throw new DirectoryNotFoundException(
                "该文件夹当前不可用。请重新连接存储设备并刷新素材库后再试。");
        }
    }

    private static bool IsFileSystemRoot(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            return root is not null && LocalPathComparer.Equals(
                Path.TrimEndingDirectorySeparator(fullPath),
                Path.TrimEndingDirectorySeparator(root));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return true;
        }
    }
}
