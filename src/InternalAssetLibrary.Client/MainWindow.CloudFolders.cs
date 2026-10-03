using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using InternalAssetLibrary.Client.Core.Http;
using InternalAssetLibrary.Client.Services;
using InternalAssetLibrary.Client.ViewModels;

namespace InternalAssetLibrary.Client;

public sealed partial class MainWindow
{
    private readonly SemaphoreSlim _cloudFolderOperationGate = new(1, 1);
    private IReadOnlyList<ApiAssetFolder> _cloudFolders = [];
    private Guid? _selectedCloudFolderId;
    private bool _suppressCloudFolderSelectionChanged;

    public ObservableCollection<CloudFolderNodeViewModel> CloudFolderNodes { get; } = [];

    public ObservableCollection<CloudFolderNodeViewModel> CompactCloudFolderNodes { get; } = [];

    public ObservableCollection<CloudFolderNodeViewModel> CurrentCloudFolderNodes { get; } = [];

    private async Task RefreshCloudLibraryAsync(CancellationToken cancellationToken = default)
    {
        await RefreshCloudFoldersAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await RefreshCloudAssetsAsync();
    }

    private async Task RefreshCloudFoldersAsync(CancellationToken cancellationToken = default)
    {
        if (_api is null || _currentUser is null)
        {
            ClearCloudFolders();
            return;
        }

        var expectedUserId = _currentUser.Id;
        var previousFolders = _cloudFolders;
        var previousSelection = _selectedCloudFolderId;
        var folders = await _api.ListAssetFoldersAsync(cancellationToken);
        if (cancellationToken.IsCancellationRequested || _currentUser?.Id != expectedUserId)
        {
            return;
        }

        _cloudFolders = folders;
        _selectedCloudFolderId = NearestExistingCloudFolder(
            previousSelection,
            previousFolders,
            folders);
        RebuildCloudFolderTree();
    }

    private static Guid? NearestExistingCloudFolder(
        Guid? selectedFolderId,
        IReadOnlyList<ApiAssetFolder> previousFolders,
        IReadOnlyList<ApiAssetFolder> refreshedFolders)
    {
        if (selectedFolderId is null)
        {
            return null;
        }

        var refreshedIds = refreshedFolders.Select(folder => folder.Id).ToHashSet();
        var previousById = previousFolders.ToDictionary(folder => folder.Id);
        var visited = new HashSet<Guid>();
        var candidate = selectedFolderId;
        while (candidate is { } candidateId && visited.Add(candidateId))
        {
            if (refreshedIds.Contains(candidateId))
            {
                return candidateId;
            }

            candidate = previousById.TryGetValue(candidateId, out var previous)
                ? previous.ParentId
                : null;
        }

        return null;
    }

    private void ClearCloudFolders()
    {
        _cloudFolders = [];
        _selectedCloudFolderId = null;
        CloudFolderNodes.Clear();
        CompactCloudFolderNodes.Clear();
        CurrentCloudFolderNodes.Clear();
        UpdateCloudFolderSelectionState();
    }

    private void RebuildCloudFolderTree()
    {
        var currentUserId = _currentUser?.Id;
        var isAdmin = _currentUser?.IsAdmin == true;
        var nodesById = _cloudFolders.ToDictionary(
            folder => folder.Id,
            folder => new CloudFolderNodeViewModel(folder, currentUserId, isAdmin));

        _suppressCloudFolderSelectionChanged = true;
        try
        {
            CloudFolderNodes.Clear();
            CompactCloudFolderNodes.Clear();
            foreach (var folder in _cloudFolders
                         .OrderBy(folder => folder.Name, StringComparer.OrdinalIgnoreCase))
            {
                var node = nodesById[folder.Id];
                if (folder.ParentId is { } parentId && nodesById.TryGetValue(parentId, out var parent))
                {
                    parent.Children.Add(node);
                }
                else
                {
                    CloudFolderNodes.Add(node);
                }
            }

            var visited = new HashSet<Guid>();
            foreach (var root in CloudFolderNodes)
            {
                AddCompactCloudFolderNodes(root, visited);
            }

            CurrentCloudFolderNodes.Clear();
            foreach (var folder in _cloudFolders
                         .Where(folder => folder.ParentId == _selectedCloudFolderId)
                         .OrderBy(folder => folder.Name, StringComparer.OrdinalIgnoreCase))
            {
                CurrentCloudFolderNodes.Add(nodesById[folder.Id]);
            }

            if (_selectedCloudFolderId is { } selectedFolderId && !nodesById.ContainsKey(selectedFolderId))
            {
                _selectedCloudFolderId = null;
            }

            var selectedNode = _selectedCloudFolderId is { } selectedId
                ? nodesById.GetValueOrDefault(selectedId)
                : null;
            var folderTree = FindCloudFolderTree();
            if (folderTree is not null)
            {
                folderTree.SelectedItem = selectedNode;
            }
        }
        finally
        {
            _suppressCloudFolderSelectionChanged = false;
        }

        UpdateCloudFolderSelectionState();
        UpdateCloudDirectoryHeader();
    }

    private void AddCompactCloudFolderNodes(
        CloudFolderNodeViewModel node,
        HashSet<Guid> visited)
    {
        if (!visited.Add(node.Id))
        {
            return;
        }

        CompactCloudFolderNodes.Add(node);
        foreach (var child in node.Children.OrderBy(child => child.Name, StringComparer.OrdinalIgnoreCase))
        {
            AddCompactCloudFolderNodes(child, visited);
        }
    }

    private void UpdateCloudFolderSelectionState()
    {
        foreach (var node in CompactCloudFolderNodes)
        {
            node.IsCompactSelected = node.Id == _selectedCloudFolderId;
        }

        var allFoldersButton = this.GetVisualDescendants()
            .OfType<Button>()
            .FirstOrDefault(button => button.Name == "AllCloudFoldersButton");
        allFoldersButton?.Classes.Set("selected", !_selectedCloudFolderId.HasValue);
    }

    private TreeView? FindCloudFolderTree() => this.GetVisualDescendants()
        .OfType<TreeView>()
        .FirstOrDefault(tree => tree.Name == "CloudFolderTree");

    private void SelectCloudFolder(Guid? folderId, bool refreshAssets)
    {
        if (folderId is not null && _cloudFolders.All(folder => folder.Id != folderId.Value))
        {
            folderId = null;
        }

        var changed = _selectedCloudFolderId != folderId;
        _selectedCloudFolderId = folderId;
        UpdateCloudFolderSelectionState();
        RebuildCurrentCloudFolderNodes();

        _suppressCloudFolderSelectionChanged = true;
        try
        {
            var folderTree = FindCloudFolderTree();
            if (folderTree is not null)
            {
                folderTree.SelectedItem = folderId is { } selectedId
                    ? CompactCloudFolderNodes.FirstOrDefault(node => node.Id == selectedId)
                    : null;
            }
        }
        finally
        {
            _suppressCloudFolderSelectionChanged = false;
        }

        if (refreshAssets && changed)
        {
            ObserveNavigationTask(RefreshCloudAssetsAsync(), "select-cloud-folder");
        }
    }

    private void AllCloudFolders_OnClick(object? sender, RoutedEventArgs eventArgs) =>
        SelectCloudFolder(null, refreshAssets: true);

    private void CloudFolderTree_OnSelectionChanged(object? sender, SelectionChangedEventArgs eventArgs)
    {
        if (_suppressCloudFolderSelectionChanged || sender is not TreeView tree)
        {
            return;
        }

        SelectCloudFolder(
            (tree.SelectedItem as CloudFolderNodeViewModel)?.Id,
            refreshAssets: true);
    }

    private void CompactCloudFolder_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is Button { DataContext: CloudFolderNodeViewModel node })
        {
            SelectCloudFolder(node.Id, refreshAssets: true);
        }
    }

    private void CloudFolderCard_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is Button { DataContext: CloudFolderNodeViewModel node })
        {
            SelectCloudFolder(node.Id, refreshAssets: true);
        }
    }

    private void CloudFolderBack_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        var parentId = _selectedCloudFolderId is { } selectedId
            ? _cloudFolders.FirstOrDefault(folder => folder.Id == selectedId)?.ParentId
            : null;
        SelectCloudFolder(parentId, refreshAssets: true);
    }

    private void CloudRecursiveSearch_OnChanged(object? sender, RoutedEventArgs eventArgs)
    {
        if (!_isLoading && _currentUser is not null)
        {
            ObserveNavigationTask(RefreshCloudAssetsAsync(), "cloud-recursive-search");
        }
    }

    private void RebuildCurrentCloudFolderNodes()
    {
        CurrentCloudFolderNodes.Clear();
        var currentUserId = _currentUser?.Id;
        var isAdmin = _currentUser?.IsAdmin == true;
        foreach (var folder in _cloudFolders
                     .Where(folder => folder.ParentId == _selectedCloudFolderId)
                     .OrderBy(folder => folder.Name, StringComparer.OrdinalIgnoreCase))
        {
            CurrentCloudFolderNodes.Add(new CloudFolderNodeViewModel(folder, currentUserId, isAdmin));
        }

        UpdateCloudDirectoryHeader();
    }

    private void UpdateCloudDirectoryHeader()
    {
        if (!IsInitialized)
        {
            return;
        }

        CloudFolderBackButton.IsEnabled = _selectedCloudFolderId.HasValue;
        CloudFolderBreadcrumbText.Text = BuildCloudFolderBreadcrumb(_selectedCloudFolderId);
        CloudCurrentFoldersEmptyText.IsVisible = CurrentCloudFolderNodes.Count == 0;
    }

    private string BuildCloudFolderBreadcrumb(Guid? folderId)
    {
        if (folderId is null)
        {
            return UiLocalization.Text("全部文件夹");
        }

        var byId = _cloudFolders.ToDictionary(folder => folder.Id);
        var names = new Stack<string>();
        var visited = new HashSet<Guid>();
        var current = folderId;
        while (current is { } currentId && visited.Add(currentId) && byId.TryGetValue(currentId, out var folder))
        {
            names.Push(folder.Name);
            current = folder.ParentId;
        }

        return UiLocalization.Text("全部文件夹") + " / " + string.Join(" / ", names);
    }

    private void CloudFolderNode_OnDragOver(object? sender, DragEventArgs eventArgs)
    {
        if (sender is not Control
            {
                DataContext: CloudFolderNodeViewModel target
            } control)
        {
            eventArgs.DragEffects = DragDropEffects.None;
            return;
        }

        SetCloudFolderAssetDropState(control, target.Id, target.Name, eventArgs);
    }

    private void CloudFolderRoot_OnDragOver(object? sender, DragEventArgs eventArgs)
    {
        if (sender is not Control control)
        {
            eventArgs.DragEffects = DragDropEffects.None;
            return;
        }

        SetCloudFolderAssetDropState(control, null, UiLocalization.Text("根目录"), eventArgs);
    }

    private void SetCloudFolderAssetDropState(
        Control control,
        Guid? targetFolderId,
        string targetName,
        DragEventArgs eventArgs)
    {
        var assets = _activeCloudDragAssets;
        var canMove = HasActiveCloudAssetDrag() &&
                      assets is { Length: > 0 } &&
                      CanMoveCloudAssets(assets) &&
                      assets.Any(asset => asset.FolderId != targetFolderId);
        control.Classes.Set("dragActive", canMove);
        eventArgs.DragEffects = canMove ? DragDropEffects.Move : DragDropEffects.None;
        eventArgs.Handled = true;
        if (canMove)
        {
            UiLocalization.SetText(
                CloudSummaryText,
                "释放后将 {0:N0} 个素材移动到“{1}”",
                assets!.Length,
                targetName);
        }
    }

    private void CloudFolderNode_OnDragLeave(object? sender, DragEventArgs eventArgs)
    {
        if (sender is Control control)
        {
            control.Classes.Set("dragActive", false);
        }

        eventArgs.Handled = true;
    }

    private void CloudFolderNode_OnDrop(object? sender, DragEventArgs eventArgs)
    {
        if (sender is not Control
            {
                DataContext: CloudFolderNodeViewModel target
            } control)
        {
            eventArgs.DragEffects = DragDropEffects.None;
            return;
        }

        CompleteCloudFolderAssetDrop(control, target.Id, target.Name, eventArgs);
    }

    private void CloudFolderRoot_OnDrop(object? sender, DragEventArgs eventArgs)
    {
        if (sender is not Control control)
        {
            eventArgs.DragEffects = DragDropEffects.None;
            return;
        }

        CompleteCloudFolderAssetDrop(control, null, UiLocalization.Text("根目录"), eventArgs);
    }

    private void CompleteCloudFolderAssetDrop(
        Control control,
        Guid? targetFolderId,
        string targetName,
        DragEventArgs eventArgs)
    {
        control.Classes.Set("dragActive", false);
        var assets = _activeCloudDragAssets?.ToArray() ?? [];
        var canMove = assets.Length > 0 &&
                      CanMoveCloudAssets(assets) &&
                      assets.Any(asset => asset.FolderId != targetFolderId);
        eventArgs.DragEffects = canMove ? DragDropEffects.Move : DragDropEffects.None;
        eventArgs.Handled = true;
        if (canMove)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => _ = MoveCloudAssetsByDropSafelyAsync(assets, targetFolderId, targetName),
                Avalonia.Threading.DispatcherPriority.Background);
        }
    }

    private bool CanMoveCloudAssets(IReadOnlyList<ApiAsset> assets) =>
        _currentUser is { } current &&
        assets.Count > 0 &&
        (current.IsAdmin ||
         HasCurrentUserPermission("assets.edit-own") &&
         assets.All(asset => asset.UploadedBy.Id == current.Id));

    private async Task MoveCloudAssetsByDropSafelyAsync(
        IReadOnlyList<ApiAsset> assets,
        Guid? targetFolderId,
        string targetName)
    {
        await RunCloudFolderOperationAsync("拖动移动素材", async () =>
        {
            if (_api is null || !CanMoveCloudAssets(assets))
            {
                throw new InvalidOperationException(UiLocalization.Text("只能移动自己上传的素材；管理员可以移动全部素材。"));
            }

            var succeeded = 0;
            var failed = 0;
            foreach (var asset in assets.Where(asset => asset.FolderId != targetFolderId))
            {
                try
                {
                    await _api.MoveAssetToFolderAsync(
                        asset.Id,
                        new ApiMoveAssetToFolderRequest(targetFolderId));
                    succeeded++;
                }
                catch (Exception exception)
                {
                    failed++;
                    ClientDiagnostics.WriteException("cloud-folder-drop-move", exception, isFatal: false);
                }
            }

            await RefreshCloudAssetsAsync();
            UiLocalization.SetText(
                CloudSummaryText,
                failed == 0
                    ? "已将 {0:N0} 个素材移动到“{1}”。"
                    : "移动完成：成功 {0:N0} 个，失败 {2:N0} 个；目标“{1}”。",
                succeeded,
                targetName,
                failed);
        });
    }

    private async void CreateCloudFolder_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        var parentId = sender is MenuItem { Tag: CloudFolderNodeViewModel parent }
            ? parent.Id
            : _selectedCloudFolderId;
        await RunCloudFolderOperationAsync("新建文件夹", async () =>
        {
            var name = await new TextPromptWindow(
                    "新建云端文件夹",
                    parentId is null ? "在云端素材库根目录中创建文件夹。" : "在当前云端文件夹中创建子文件夹。")
                .ShowDialog<string?>(this);
            if (name is null || _api is null)
            {
                return;
            }

            var created = await _api.CreateAssetFolderAsync(
                new ApiCreateAssetFolderRequest(name, parentId));
            _selectedCloudFolderId = created.Id;
            await RefreshCloudFoldersAsync();
            await RefreshCloudAssetsAsync();
            UiLocalization.SetText(CloudSummaryText, "文件夹“{0}”已创建。", created.Name);
        });
    }

    private async void RenameCloudFolder_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not MenuItem { Tag: CloudFolderNodeViewModel node })
        {
            return;
        }

        await RunCloudFolderOperationAsync("重命名文件夹", async () =>
        {
            var name = await new TextPromptWindow(
                    "重命名云端文件夹",
                    "所有团队成员都可以重命名云端文件夹。",
                    node.Name)
                .ShowDialog<string?>(this);
            if (name is null || _api is null)
            {
                return;
            }

            var renamed = await _api.RenameAssetFolderAsync(
                node.Id,
                new ApiRenameAssetFolderRequest(name));
            await RefreshCloudFoldersAsync();
            UiLocalization.SetText(CloudSummaryText, "文件夹已重命名为“{0}”。", renamed.Name);
        });
    }

    private async void MoveCloudFolder_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not MenuItem { Tag: CloudFolderNodeViewModel { CanMoveOrDelete: true } node })
        {
            return;
        }

        await RunCloudFolderOperationAsync("移动文件夹", async () =>
        {
            var excluded = CloudFolderDescendantIds(node.Id);
            var selection = await new CloudFolderPickerWindow(
                    _cloudFolders,
                    node.ParentId,
                    excluded)
                .ShowDialog<CloudFolderSelection?>(this);
            if (selection is null || selection.FolderId == node.ParentId || _api is null)
            {
                return;
            }

            await _api.MoveAssetFolderAsync(
                node.Id,
                new ApiMoveAssetFolderRequest(selection.FolderId));
            await RefreshCloudFoldersAsync();
            await RefreshCloudAssetsAsync();
            UiLocalization.SetText(CloudSummaryText, "文件夹“{0}”已移动。", node.Name);
        });
    }

    private async void DeleteCloudFolder_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not MenuItem { Tag: CloudFolderNodeViewModel { CanMoveOrDelete: true } node })
        {
            return;
        }

        await RunCloudFolderOperationAsync("删除文件夹", async () =>
        {
            var confirmed = await new MessageDialogWindow(
                    "删除云端文件夹",
                    UiLocalization.Format(
                        "确定删除文件夹“{0}”吗？文件夹中的直属素材与直属子目录会提升到其父目录，素材文件本身不会被删除。",
                        node.Name),
                    "确认删除",
                    "取消")
                .ShowDialog<bool>(this);
            if (!confirmed || _api is null)
            {
                return;
            }

            await _api.DeleteAssetFolderAsync(node.Id);
            if (_selectedCloudFolderId == node.Id)
            {
                _selectedCloudFolderId = node.ParentId;
            }

            await RefreshCloudFoldersAsync();
            await RefreshCloudAssetsAsync();
            UiLocalization.SetText(CloudSummaryText, "文件夹“{0}”已删除，目录内容已提升到父目录。", node.Name);
        });
    }

    private HashSet<Guid> CloudFolderDescendantIds(Guid folderId)
    {
        var result = new HashSet<Guid> { folderId };
        var pending = new Queue<Guid>();
        pending.Enqueue(folderId);
        while (pending.TryDequeue(out var parentId))
        {
            foreach (var child in _cloudFolders.Where(folder => folder.ParentId == parentId))
            {
                if (result.Add(child.Id))
                {
                    pending.Enqueue(child.Id);
                }
            }
        }

        return result;
    }

    private void CloudFolderNode_OnContextRequested(
        object? sender,
        ContextRequestedEventArgs eventArgs)
    {
        if (sender is not Control { DataContext: CloudFolderNodeViewModel node } target)
        {
            return;
        }

        SelectCloudFolder(node.Id, refreshAssets: true);
        target.ContextMenu?.Close();
        var menu = CreateCloudFolderContextMenu(node);
        target.ContextMenu = menu;
        menu.Open(target);
        eventArgs.Handled = true;
    }

    private ContextMenu CreateCloudFolderContextMenu(CloudFolderNodeViewModel node)
    {
        var openItem = CloudFolderMenuItem("打开文件夹", node, OpenCloudFolder_OnClick);
        var createItem = CloudFolderMenuItem("新建子文件夹", node, CreateCloudFolder_OnClick);
        var renameItem = CloudFolderMenuItem("重命名", node, RenameCloudFolder_OnClick);
        var moveItem = CloudFolderMenuItem("移动文件夹", node, MoveCloudFolder_OnClick);
        var deleteItem = CloudFolderMenuItem("删除文件夹", node, DeleteCloudFolder_OnClick);
        moveItem.IsEnabled = node.CanMoveOrDelete;
        deleteItem.IsEnabled = node.CanMoveOrDelete;
        return new ContextMenu
        {
            ItemsSource = new object[]
            {
                openItem,
                new Separator(),
                createItem,
                renameItem,
                moveItem,
                deleteItem
            }
        };
    }

    private static MenuItem CloudFolderMenuItem(
        string header,
        CloudFolderNodeViewModel node,
        EventHandler<RoutedEventArgs> handler)
    {
        var item = new MenuItem
        {
            Header = UiLocalization.Text(header),
            Tag = node
        };
        item.Click += handler;
        return item;
    }

    private void OpenCloudFolder_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is MenuItem { Tag: CloudFolderNodeViewModel node })
        {
            SelectCloudFolder(node.Id, refreshAssets: true);
        }
    }

    private void CloudAsset_OnContextRequested(
        object? sender,
        ContextRequestedEventArgs eventArgs)
    {
        if (sender is not Control { DataContext: CloudAssetCardViewModel asset } target)
        {
            return;
        }

        SelectCloudAssetForContextMenu(asset);
        target.ContextMenu?.Close();
        var menu = CreateCloudAssetContextMenu(asset);
        target.ContextMenu = menu;
        menu.Open(target);
        eventArgs.Handled = true;
    }

    private void SelectCloudAssetForContextMenu(CloudAssetCardViewModel asset)
    {
        ResetPendingDragGesture();
        CommitHoverDetailSelection();
        if (_selectedCloudAsset?.Id != asset.Id)
        {
            SelectCloudAsset(asset);
        }

        ObserveNavigationTask(
            StopDetailPreviewAsync(savePosition: true),
            "stop-cloud-preview-for-context-menu");
    }

    private ContextMenu CreateCloudAssetContextMenu(CloudAssetCardViewModel asset)
    {
        var ownsAsset = _currentUser?.IsAdmin == true || _currentUser?.Id == asset.Asset.UploadedBy.Id;
        var canEditOwn = ownsAsset && HasCurrentUserPermission("assets.edit-own");
        var canDeleteOwn = ownsAsset && HasCurrentUserPermission("assets.delete-own");
        var downloadItem = CloudAssetMenuItem("下载", asset, DownloadSelectedCloudAsset_OnClick);
        downloadItem.IsEnabled = asset.Asset.HasOriginal && HasCurrentUserPermission("assets.download");
        var editTagsItem = CloudAssetMenuItem("编辑标签", asset, EditLocalTags_OnClick);
        editTagsItem.IsEnabled = HasCurrentUserPermission("assets.tags");
        var moveItem = CloudAssetMenuItem("移动到文件夹", asset, MoveCloudAssetToFolder_OnClick);
        moveItem.IsEnabled = canEditOwn;
        var categoryItem = CloudAssetMenuItem("修改分类", asset, ChangeCloudAudioCategory_OnClick);
        categoryItem.IsEnabled = canEditOwn &&
                                 asset.Asset.Category is ApiAssetCategory.Bgm or ApiAssetCategory.SoundEffect;
        var editItem = CloudAssetMenuItem("名称/备注", asset, EditSelectedCloudAsset_OnClick);
        editItem.IsEnabled = canEditOwn;
        var replaceItem = CloudAssetMenuItem("换源", asset, ReplaceSelectedCloudAsset_OnClick);
        replaceItem.IsEnabled = canEditOwn;
        var recycleItem = CloudAssetMenuItem("移至回收站", asset, RecycleSelectedCloudAsset_OnClick);
        recycleItem.IsEnabled = canDeleteOwn;
        return new ContextMenu
        {
            ItemsSource = new object[]
            {
                downloadItem,
                editTagsItem,
                moveItem,
                categoryItem,
                new Separator(),
                editItem,
                replaceItem,
                recycleItem
            }
        };
    }

    private async void ChangeCloudAudioCategory_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not MenuItem { Tag: CloudAssetCardViewModel asset } ||
            asset.Asset.Category is not (ApiAssetCategory.Bgm or ApiAssetCategory.SoundEffect) ||
            _api is null)
        {
            return;
        }

        var ownsAsset = _currentUser?.IsAdmin == true || _currentUser?.Id == asset.Asset.UploadedBy.Id;
        if (!ownsAsset || !HasCurrentUserPermission("assets.edit-own"))
        {
            return;
        }

        var category = await new AudioCategoryWindow(asset.Asset.Category)
            .ShowDialog<ApiAssetCategory?>(this);
        if (category is null || category == asset.Asset.Category)
        {
            return;
        }

        try
        {
            await _api.UpdateAssetCategoryAsync(
                asset.Id,
                new ApiUpdateAssetCategoryRequest(category.Value));
            await RefreshCloudAssetsAsync();
            UiLocalization.SetText(
                CloudSummaryText,
                "素材“{0}”已修改为{1}。",
                asset.Name,
                category == ApiAssetCategory.Bgm ? " BGM" : "音效");
        }
        catch (Exception exception)
        {
            UiLocalization.SetText(CloudSummaryText, "修改音频分类失败：{0}", UserMessage(exception));
            await new MessageDialogWindow("无法修改分类", UserMessage(exception)).ShowDialog(this);
        }
    }

    private static MenuItem CloudAssetMenuItem(
        string header,
        CloudAssetCardViewModel asset,
        EventHandler<RoutedEventArgs> handler)
    {
        var item = new MenuItem
        {
            Header = UiLocalization.Text(header),
            Tag = asset
        };
        item.Click += handler;
        return item;
    }

    private async void MoveCloudAssetToFolder_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not MenuItem { Tag: CloudAssetCardViewModel asset })
        {
            return;
        }

        var ownsAsset = _currentUser?.IsAdmin == true || _currentUser?.Id == asset.Asset.UploadedBy.Id;
        if (!ownsAsset || !HasCurrentUserPermission("assets.edit-own"))
        {
            return;
        }

        await RunCloudFolderOperationAsync("移动素材", async () =>
        {
            var selection = await new CloudFolderPickerWindow(
                    _cloudFolders,
                    asset.Asset.FolderId,
                    new HashSet<Guid>())
                .ShowDialog<CloudFolderSelection?>(this);
            if (selection is null || selection.FolderId == asset.Asset.FolderId || _api is null)
            {
                return;
            }

            await _api.MoveAssetToFolderAsync(
                asset.Id,
                new ApiMoveAssetToFolderRequest(selection.FolderId));
            await RefreshCloudAssetsAsync();
            UiLocalization.SetText(CloudSummaryText, "素材“{0}”已移动。", asset.Name);
        });
    }

    private async void BatchMoveCloudAssets_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_api is null || _currentUser is null)
        {
            return;
        }

        var selected = VisibleCloudAssets.Where(asset => asset.IsSelected).ToArray();
        var allOwn = selected.Length > 0 &&
                     selected.All(asset => asset.Asset.UploadedBy.Id == _currentUser.Id);
        if (selected.Length == 0 ||
            !_currentUser.IsAdmin && (!allOwn || !HasCurrentUserPermission("assets.edit-own")))
        {
            return;
        }

        var folderIds = selected
            .Select(asset => asset.Asset.FolderId)
            .Distinct()
            .Take(2)
            .ToArray();
        var currentFolderId = folderIds.Length == 1 ? folderIds[0] : null;
        var selection = await new CloudFolderPickerWindow(
                _cloudFolders,
                currentFolderId,
                new HashSet<Guid>())
            .ShowDialog<CloudFolderSelection?>(this);
        if (selection is null)
        {
            UiLocalization.SetText(CloudSummaryText, "批量移动素材已取消。" );
            return;
        }

        SetCloudBatchOperationRunning(true);
        try
        {
            var succeeded = 0;
            var unchanged = 0;
            var failedIds = new HashSet<Guid>();
            foreach (var asset in selected)
            {
                if (asset.Asset.FolderId == selection.FolderId)
                {
                    unchanged++;
                    continue;
                }

                try
                {
                    await _api.MoveAssetToFolderAsync(
                        asset.Id,
                        new ApiMoveAssetToFolderRequest(selection.FolderId));
                    succeeded++;
                }
                catch (Exception exception)
                {
                    failedIds.Add(asset.Id);
                    ClientDiagnostics.WriteException("batch-move-cloud-asset", exception, isFatal: false);
                }
            }

            await RefreshCloudAssetsAsync();
            foreach (var asset in VisibleCloudAssets.Where(asset => failedIds.Contains(asset.Id)))
            {
                asset.IsSelected = true;
            }

            UpdateCloudSelectionState();
            UiLocalization.SetText(
                CloudSummaryText,
                failedIds.Count == 0
                    ? "批量移动完成：移动 {0:N0} 个，目标目录中已有 {1:N0} 个。"
                    : "批量移动完成：成功 {0:N0} 个，未移动 {1:N0} 个，失败 {2:N0} 个；失败素材已保留选中。",
                succeeded,
                unchanged,
                failedIds.Count);
        }
        catch (Exception exception)
        {
            UiLocalization.SetText(CloudSummaryText, "批量移动素材失败：{0}", UserMessage(exception));
        }
        finally
        {
            SetCloudBatchOperationRunning(false);
        }
    }

    private async Task RunCloudFolderOperationAsync(string operation, Func<Task> action)
    {
        var localizedOperation = UiLocalization.Text(operation);
        if (!await _cloudFolderOperationGate.WaitAsync(0))
        {
            UiLocalization.SetText(CloudSummaryText, "另一项云端文件夹操作正在进行，请稍候。" );
            return;
        }

        try
        {
            if (_api is null || _currentUser is null)
            {
                throw new InvalidOperationException(UiLocalization.Text("请先连接并登录服务器。"));
            }

            await action();
        }
        catch (OperationCanceledException)
        {
            UiLocalization.SetText(CloudSummaryText, "{0}已取消。", localizedOperation);
        }
        catch (Exception exception)
        {
            var message = UserMessage(exception);
            UiLocalization.SetText(CloudSummaryText, "{0}失败：{1}", localizedOperation, message);
            await new MessageDialogWindow(
                    UiLocalization.Format("{0}失败", localizedOperation),
                    message)
                .ShowDialog(this);
        }
        finally
        {
            _cloudFolderOperationGate.Release();
        }
    }
}
