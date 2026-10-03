using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using InternalAssetLibrary.Client.Core.Http;
using InternalAssetLibrary.Client.Core.Lut;
using InternalAssetLibrary.Contracts;

namespace InternalAssetLibrary.Client;

public sealed partial class LutLibraryWindow : Window
{
    private const string UploadPermission = "assets.upload";
    private const string EditPermission = "assets.edit-own";
    private const string DeletePermission = "assets.delete-own";

    private static readonly FilePickerFileType CubeFileType = new("CUBE LUT")
    {
        Patterns = ["*.cube"]
    };

    private readonly ILocalLutLibrary? _localLibrary;
    private readonly SemaphoreSlim _nativePickerGate = new(1, 1);
    private readonly AssetLibraryApiClient? _api;
    private readonly ApiCurrentUser? _currentUser;
    private readonly Guid? _initialCloudLutId;
    private readonly Func<string, string?, Guid?, Task<TeamLutSummary>>? _queueUpload;
    private readonly Func<TeamLutSummary, string, Task<string>>? _queueDownload;
    private readonly CubeLutValidator _validator = new();
    private bool _isBusy;
    private bool _initialCloudSelectionApplied;
    private bool _closing;

    public LutLibraryWindow()
    {
        InitializeComponent();
        DataContext = this;
        Services.UiLocalization.Apply(this, Services.UiLocalization.CurrentLanguage);
        Closing += (_, _) => _closing = true;
    }

    public LutLibraryWindow(
        ILocalLutLibrary localLibrary,
        AssetLibraryApiClient? api,
        ApiCurrentUser? currentUser,
        Guid? initialCloudLutId = null,
        Func<string, string?, Guid?, Task<TeamLutSummary>>? queueUpload = null,
        Func<TeamLutSummary, string, Task<string>>? queueDownload = null)
        : this()
    {
        _localLibrary = localLibrary;
        _api = api;
        _currentUser = currentUser;
        _initialCloudLutId = initialCloudLutId;
        _queueUpload = queueUpload;
        _queueDownload = queueDownload;
        UploadCloudButton.IsEnabled = CanUse(UploadPermission);
        if (_api is null || _currentUser is null)
        {
            CloudDescriptionText.Text = T("登录团队账号后可查看和调用云端 LUT。");
        }

        Opened += async (_, _) => await ReloadAllAsync();
    }

    public ObservableCollection<LocalLutItemViewModel> LocalItems { get; } = [];

    public ObservableCollection<CloudLutItemViewModel> CloudItems { get; } = [];

    private async Task ReloadAllAsync()
    {
        if (_localLibrary is null || _isBusy || _closing)
        {
            return;
        }

        SetBusy(true, T("正在刷新 LUT 库…"));
        try
        {
            await ReloadLocalAsync();
            await ReloadCloudAsync();
            SetStatus(T("LUT 库已刷新。"));
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ReloadLocalAsync()
    {
        if (_closing)
        {
            return;
        }

        var selectedId = (LocalLutList.SelectedItem as LocalLutItemViewModel)?.Entry.Id;
        var entries = await _localLibrary!.LoadAsync();
        if (_closing)
        {
            return;
        }

        LocalItems.Clear();
        foreach (var entry in entries)
        {
            LocalItems.Add(new LocalLutItemViewModel(entry));
        }

        LocalLutList.SelectedItem = LocalItems.FirstOrDefault(item => item.Entry.Id == selectedId);
        LocalEmptyText.IsVisible = LocalItems.Count == 0;
        UpdateLocalActions();
    }

    private async Task ReloadCloudAsync()
    {
        if (_closing)
        {
            return;
        }

        var selectedIds = (CloudLutList.SelectedItems?.OfType<CloudLutItemViewModel>() ?? [])
            .Select(item => item.Lut.Id)
            .ToHashSet();
        ApiPage<TeamLutSummary>? page = null;
        if (_api is not null && _currentUser is not null)
        {
            page = await _api.ListTeamLutsAsync(new ApiTeamLutListQuery(PageSize: 200));
        }

        if (_closing)
        {
            return;
        }

        CloudItems.Clear();
        if (page is not null)
        {
            foreach (var lut in page.Items)
            {
                CloudItems.Add(new CloudLutItemViewModel(
                    lut,
                    CanEdit(lut),
                    CanDelete(lut)));
            }
        }

        var targetId = !_initialCloudSelectionApplied ? _initialCloudLutId : null;
        var selection = CloudLutList.SelectedItems;
        selection?.Clear();
        foreach (var item in CloudItems.Where(item => selectedIds.Contains(item.Lut.Id)))
        {
            selection?.Add(item);
        }

        if ((selection?.Count ?? 0) == 0 && targetId is not null)
        {
            var target = CloudItems.FirstOrDefault(item => item.Lut.Id == targetId);
            if (target is not null)
            {
                selection?.Add(target);
            }
        }

        _initialCloudSelectionApplied = true;
        CloudEmptyText.IsVisible = CloudItems.Count == 0;
        CloudEmptyText.Text = _api is null || _currentUser is null
            ? T("登录后显示团队云端 LUT。")
            : T("团队云端 LUT 库为空。");
        UpdateCloudActions();
    }

    private async void RefreshAll_OnClick(object? sender, RoutedEventArgs eventArgs) =>
        await ReloadAllAsync();

    private async void ConnectLocal_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_localLibrary is null || _isBusy)
        {
            return;
        }

        var files = await RunNativePickerAsync(
            "lut-connect-local",
            () => StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = T("连接本地 LUT"),
                AllowMultiple = true,
                FileTypeFilter = [CubeFileType]
            }));
        if (files is null)
        {
            return;
        }

        var paths = files
            .Select(file => file.Path.LocalPath)
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Cast<string>()
            .ToArray();
        if (paths.Length == 0)
        {
            return;
        }

        SetBusy(true, T("正在检查 LUT 文件…"));
        try
        {
            foreach (var path in paths)
            {
                await _localLibrary.ImportAsync(path);
                if (_closing)
                {
                    return;
                }
            }

            await ReloadLocalAsync();
            SetStatus(string.Format(T("已连接 {0:N0} 个本地 LUT。"), paths.Length));
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void RefreshLocal_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_localLibrary is null ||
            LocalLutList.SelectedItem is not LocalLutItemViewModel selected ||
            _isBusy)
        {
            return;
        }

        SetBusy(true, T("正在检查 LUT 文件…"));
        try
        {
            await _localLibrary.RefreshAsync(selected.Entry.Id);
            await ReloadLocalAsync();
            StatusText.Text = T("文件状态已更新。");
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void RemoveLocal_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_localLibrary is null ||
            LocalLutList.SelectedItem is not LocalLutItemViewModel selected ||
            _isBusy)
        {
            return;
        }

        var confirmed = await new MessageDialogWindow(
                T("移除本地 LUT 链接"),
                T("只会从 LUT 列表移除链接，不会删除磁盘上的 .cube 文件。"),
                T("移除链接"),
                T("取消"))
            .ShowDialog<bool>(this);
        if (!confirmed)
        {
            return;
        }

        SetBusy(true, T("正在移除链接…"));
        try
        {
            await _localLibrary.RemoveAsync(selected.Entry.Id);
            await ReloadLocalAsync();
            StatusText.Text = T("本地 LUT 链接已移除。");
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void UploadCloud_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_api is null || _currentUser is null || !CanUse(UploadPermission) || _isBusy)
        {
            return;
        }

        var files = await RunNativePickerAsync(
            "lut-upload-cloud",
            () => StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = T("上传团队 LUT"),
                AllowMultiple = false,
                FileTypeFilter = [CubeFileType]
            }));
        if (files is null)
        {
            return;
        }

        var path = files.FirstOrDefault()?.Path.LocalPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        var validation = await _validator.ValidateFileAsync(path);
        if (_closing)
        {
            return;
        }

        if (!validation.IsValid || validation.Descriptor is not { } descriptor)
        {
            SetStatus(validation.Diagnostic);
            return;
        }

        var defaultName = string.IsNullOrWhiteSpace(descriptor.Title)
            ? Path.GetFileNameWithoutExtension(path)
            : descriptor.Title;
        var name = await new TextPromptWindow(
                T("上传团队 LUT"),
                T("设置团队成员在 LUT 列表中看到的名称。"),
                defaultName)
            .ShowDialog<string?>(this);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        SetBusy(true, T("正在上传团队 LUT…"));
        try
        {
            if (_queueUpload is not null) await _queueUpload(path, name, null);
            else await new Services.TeamLutUploadService(_api, _validator).UploadAsync(path, name);
            if (_closing)
            {
                return;
            }

            await ReloadCloudAsync();
            SetStatus(T("团队 LUT 已上传。"));
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void UploadSelectedLocal_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_api is null ||
            _currentUser is null ||
            !CanUse(UploadPermission) ||
            LocalLutList.SelectedItem is not LocalLutItemViewModel selected ||
            !selected.Entry.IsAvailable ||
            _isBusy)
        {
            return;
        }

        SetBusy(true, T("正在上传已选 LUT…"));
        try
        {
            var uploaded = _queueUpload is not null
                ? await _queueUpload(selected.Entry.FullPath, selected.Entry.DisplayName, null)
                : (await new Services.TeamLutUploadService(_api, _validator).UploadAsync(selected.Entry.FullPath, selected.Entry.DisplayName)).Lut;
            await ReloadCloudAsync();
            CloudLutList.SelectedItem = CloudItems.FirstOrDefault(item => item.Lut.Id == uploaded.Id);
            StatusText.Text = T("已选本地 LUT 已上传到团队云端。");
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void DownloadCloud_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_api is null || _localLibrary is null ||
            SingleSelectedCloudItem() is not { } selected || _isBusy)
        {
            return;
        }

        SetBusy(true, T("正在下载并检查团队 LUT…"));
        try
        {
            Directory.CreateDirectory(Services.AppPaths.CloudLutDownloadDirectory);
            var target = UniqueDownloadPath(selected.Lut);
            if (_queueDownload is not null) target = await _queueDownload(selected.Lut, target);
            else await using (var stream = new FileStream(
                target,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous))
            {
                await _api.DownloadTeamLutAsync(selected.Lut.Id, stream);
            }

            await _localLibrary.ImportAsync(target, selected.Lut.Name);
            await ReloadLocalAsync();
            StatusText.Text = T("团队 LUT 已下载并连接到本地库。");
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void DownloadCloudToExternal_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_api is null ||
            sender is not Button { DataContext: CloudLutItemViewModel selected } ||
            _isBusy)
        {
            return;
        }

        var target = await RunNativePickerAsync(
            "lut-download-external",
            () => StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = T("下载团队 LUT"),
                SuggestedFileName = Path.GetFileName(selected.Lut.OriginalFileName),
                DefaultExtension = "cube",
                FileTypeChoices = [CubeFileType]
            }));
        var targetPath = target?.Path.LocalPath;
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return;
        }

        var directory = Path.GetDirectoryName(targetPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            SetStatus(T("选择的保存位置无效。"));
            return;
        }

        SetBusy(true, T("正在下载团队 LUT…"));
        try
        {
            await DownloadTeamLutToFileAsync(selected.Lut, targetPath, overwrite: true);
            SetStatus(string.Format(T("团队 LUT 已下载到：{0}"), targetPath));
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void DownloadSelectedCloudLuts_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_api is null || _isBusy)
        {
            return;
        }

        var selected = SelectedCloudItems().Select(item => item.Lut).ToArray();
        if (selected.Length == 0)
        {
            return;
        }

        var folders = await RunNativePickerAsync(
            "lut-batch-download-folder",
            () => StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = T("选择团队 LUT 批量下载目录"),
                AllowMultiple = false
            }));
        if (folders is null)
        {
            return;
        }

        var targetDirectory = folders.FirstOrDefault()?.Path.LocalPath;
        if (string.IsNullOrWhiteSpace(targetDirectory))
        {
            SetStatus(T("已取消 LUT 批量下载。"));
            return;
        }

        SetBusy(true, T("正在批量下载团队 LUT…"));
        var succeeded = 0;
        var failed = 0;
        try
        {
            foreach (var lut in selected)
            {
                if (_closing)
                {
                    return;
                }

                try
                {
                    var targetPath = UniqueExternalDownloadPath(targetDirectory, lut);
                    await DownloadTeamLutToFileAsync(lut, targetPath, overwrite: false);
                    succeeded++;
                }
                catch
                {
                    failed++;
                }
            }

            SetStatus(failed == 0
                ? string.Format(T("LUT 批量下载完成：{0:N0} 个文件。"), succeeded)
                : string.Format(
                    T("LUT 批量下载完成：成功 {0:N0} 个，失败 {1:N0} 个。"),
                    succeeded,
                    failed));
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void EditCloud_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_api is null ||
            SingleSelectedCloudItem() is not { CanEdit: true } selected ||
            _isBusy)
        {
            return;
        }

        var name = await new TextPromptWindow(
                T("修改 LUT 名称"),
                T("名称会立即同步给所有团队成员。"),
                selected.Lut.Name)
            .ShowDialog<string?>(this);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var note = await new TextPromptWindow(
                T("修改 LUT 备注"),
                T("备注可留空。"),
                selected.Lut.Note)
            .ShowDialog<string?>(this);
        if (note is null)
        {
            return;
        }

        SetBusy(true, T("正在保存 LUT 信息…"));
        try
        {
            await _api.UpdateTeamLutAsync(selected.Lut.Id, new UpdateTeamLutRequest(name.Trim(), note));
            await ReloadCloudAsync();
            StatusText.Text = T("团队 LUT 信息已更新。");
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void ReplaceCloud_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_api is null ||
            SingleSelectedCloudItem() is not { CanEdit: true } selected ||
            _isBusy)
        {
            return;
        }

        var files = await RunNativePickerAsync(
            "lut-replace-cloud",
            () => StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = T("选择新的 LUT 原文件"),
                AllowMultiple = false,
                FileTypeFilter = [CubeFileType]
            }));
        if (files is null)
        {
            return;
        }

        var path = files.FirstOrDefault()?.Path.LocalPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        var validation = await _validator.ValidateFileAsync(path);
        if (_closing)
        {
            return;
        }

        if (!validation.IsValid || validation.Descriptor is not { SizeBytes: { } size, Sha256: { } sha256 })
        {
            SetStatus(validation.Diagnostic);
            return;
        }

        var confirmed = await new MessageDialogWindow(
                T("更换 LUT 原文件"),
                T("新版本会替换团队库中的当前 LUT 内容，但不会修改显示名称和备注。"),
                T("确认换源"),
                T("取消"))
            .ShowDialog<bool>(this);
        if (!confirmed)
        {
            return;
        }

        SetBusy(true, T("正在上传 LUT 新版本…"));
        try
        {
            if (_queueUpload is not null) await _queueUpload(path, selected.Lut.Name, selected.Lut.Id);
            else
            {
            await using var stream = OpenRead(path);
            await _api.UploadTeamLutReplacementAsync(
                selected.Lut.Id,
                Path.GetFileName(path),
                size,
                sha256,
                stream);
            }
            if (_closing)
            {
                return;
            }

            await ReloadCloudAsync();
            SetStatus(T("团队 LUT 已换源。"));
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void RecycleCloud_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_api is null ||
            SingleSelectedCloudItem() is not { CanDelete: true } selected ||
            _isBusy)
        {
            return;
        }

        var confirmed = await new MessageDialogWindow(
                T("将 LUT 移至回收站"),
                string.Format(T("“{0}”将从团队 LUT 列表移除，并进入回收站。"), selected.Lut.Name),
                T("确认移入"),
                T("取消"))
            .ShowDialog<bool>(this);
        if (!confirmed)
        {
            return;
        }

        SetBusy(true, T("正在移入回收站…"));
        try
        {
            await _api.RecycleTeamLutAsync(selected.Lut.Id);
            await ReloadCloudAsync();
            StatusText.Text = T("团队 LUT 已移入回收站。");
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void LocalLutList_OnSelectionChanged(object? sender, SelectionChangedEventArgs eventArgs) =>
        UpdateLocalActions();

    private void CloudLutList_OnSelectionChanged(object? sender, SelectionChangedEventArgs eventArgs) =>
        UpdateCloudActions();

    private void UpdateLocalActions()
    {
        var selected = LocalLutList.SelectedItem is LocalLutItemViewModel;
        RefreshLocalButton.IsEnabled = selected && !_isBusy;
        RemoveLocalButton.IsEnabled = selected && !_isBusy;
        UploadSelectedLocalButton.IsEnabled =
            LocalLutList.SelectedItem is LocalLutItemViewModel { Entry.IsAvailable: true } &&
            !_isBusy &&
            CanUse(UploadPermission);
    }

    private void UpdateCloudActions()
    {
        var selectedItems = SelectedCloudItems();
        var selected = selectedItems.Count == 1 ? selectedItems[0] : null;
        DownloadCloudButton.IsEnabled = selected is not null && !_isBusy;
        DownloadSelectedCloudLutsButton.IsEnabled = selectedItems.Count > 0 && !_isBusy;
        EditCloudButton.IsEnabled = selected?.CanEdit == true && !_isBusy;
        ReplaceCloudButton.IsEnabled = selected?.CanEdit == true && !_isBusy;
        RecycleCloudButton.IsEnabled = selected?.CanDelete == true && !_isBusy;
        CloudSelectionHint.Text = selectedItems.Count switch
        {
            0 => string.Empty,
            > 1 => string.Format(T("已选 {0:N0} 个 LUT，可批量下载到指定目录。"), selectedItems.Count),
            _ when selected!.CanEdit => T("你可以下载、调用和维护这个 LUT。"),
            _ => T("他人上传的 LUT 可下载和调用，但只有上传者与管理员可修改。")
        };
    }

    private bool CanEdit(TeamLutSummary lut) =>
        _currentUser is not null &&
        (_currentUser.IsAdmin ||
         _currentUser.Id == lut.UploadedBy.Id && CanUse(EditPermission));

    private bool CanDelete(TeamLutSummary lut) =>
        _currentUser is not null &&
        (_currentUser.IsAdmin ||
         _currentUser.Id == lut.UploadedBy.Id && CanUse(DeletePermission));

    private bool CanUse(string permission) =>
        _currentUser is not null &&
        (_currentUser.IsAdmin || _currentUser.Permissions.Contains(permission, StringComparer.OrdinalIgnoreCase));

    private void SetBusy(bool isBusy, string? status = null)
    {
        _isBusy = isBusy;
        if (_closing)
        {
            return;
        }

        DialogContent.IsEnabled = !isBusy;
        if (status is not null)
        {
            StatusText.Text = status;
        }

        UpdateLocalActions();
        UpdateCloudActions();
        UploadCloudButton.IsEnabled = !isBusy && CanUse(UploadPermission);
    }

    private void SetStatus(string status)
    {
        if (!_closing)
        {
            StatusText.Text = status;
        }
    }

    private async Task<TResult?> RunNativePickerAsync<TResult>(string operation, Func<Task<TResult>> picker)
        where TResult : class?
    {
        if (_closing)
        {
            return null;
        }

        if (!_nativePickerGate.Wait(0))
        {
            if (!_closing)
            {
                StatusText.Text = T("已有文件选择窗口正在打开，请先完成或取消当前选择。");
            }

            return null;
        }

        try
        {
            var result = await picker();
            return _closing ? null : result;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception exception)
        {
            Services.ClientDiagnostics.WriteException($"native-picker-{operation}", exception, isFatal: false);
            if (!_closing)
            {
                StatusText.Text = string.Format(T("无法打开文件选择窗口：{0}"), exception.Message);
            }

            return null;
        }
        finally
        {
            _nativePickerGate.Release();
        }
    }

    private IReadOnlyList<CloudLutItemViewModel> SelectedCloudItems() =>
        (CloudLutList.SelectedItems?.OfType<CloudLutItemViewModel>() ?? []).ToArray();

    private CloudLutItemViewModel? SingleSelectedCloudItem()
    {
        var selected = SelectedCloudItems();
        return selected.Count == 1 ? selected[0] : null;
    }

    private async Task DownloadTeamLutToFileAsync(
        TeamLutSummary lut,
        string targetPath,
        bool overwrite)
    {
        var directory = Path.GetDirectoryName(targetPath)
            ?? throw new InvalidDataException(T("选择的保存位置无效。"));
        Directory.CreateDirectory(directory);
        if (_queueDownload is not null)
        {
            await _queueDownload(lut, targetPath);
            return;
        }
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.download");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous))
            {
                await _api!.DownloadTeamLutAsync(lut.Id, stream);
            }

            File.Move(temporaryPath, targetPath, overwrite);
        }
        finally
        {
            TryDeleteTemporaryDownload(temporaryPath);
        }
    }

    private static string UniqueExternalDownloadPath(string directory, TeamLutSummary lut)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var stem = string.Concat(Path.GetFileNameWithoutExtension(lut.OriginalFileName)
            .Trim()
            .Select(character => invalid.Contains(character) ? '_' : character));
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

    private static void TryDeleteTemporaryDownload(string path)
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

    private static FileStream OpenRead(string path) => new(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        64 * 1024,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static string UniqueDownloadPath(TeamLutSummary lut)
    {
        var fileName = Path.GetFileName(lut.OriginalFileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var path = Path.Combine(Services.AppPaths.CloudLutDownloadDirectory, fileName);
        for (var suffix = 2; File.Exists(path); suffix++)
        {
            path = Path.Combine(Services.AppPaths.CloudLutDownloadDirectory, $"{stem}-{suffix}.cube");
        }

        return path;
    }

    private static string T(string chinese) => Services.UiLocalization.Text(chinese);

    private void Close_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (!_isBusy)
        {
            Close();
        }
    }
}

public sealed class LocalLutItemViewModel(LocalLutEntry entry)
{
    public LocalLutEntry Entry { get; } = entry;

    public string Name => Entry.DisplayName;

    public string Path => Entry.FullPath;

    public string Status => Services.UiLocalization.Text(Entry.IsAvailable ? "可用" : "不可用");

    public string Detail => Entry.Descriptor is { } descriptor
        ? descriptor.Kind switch
        {
            CubeLutKind.OneDimensional => $"1D · {descriptor.OneDimensionalSize}",
            CubeLutKind.ThreeDimensional => $"3D · {descriptor.ThreeDimensionalSize}³",
            _ => $"1D {descriptor.OneDimensionalSize} + 3D {descriptor.ThreeDimensionalSize}³"
        }
        : Entry.Diagnostic ?? Services.UiLocalization.Text("无法读取文件");
}

public sealed class CloudLutItemViewModel(
    TeamLutSummary lut,
    bool canEdit,
    bool canDelete)
{
    public TeamLutSummary Lut { get; } = lut;

    public bool CanEdit { get; } = canEdit;

    public bool CanDelete { get; } = canDelete;

    public string Name => Lut.Name;

    public string OwnerLine => $"{Lut.UploadedBy.DisplayName} · {Lut.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm}";

    public string Detail => string.IsNullOrWhiteSpace(Lut.Note)
        ? Lut.OriginalFileName
        : $"{Lut.OriginalFileName} · {Lut.Note}";

    public string VersionText => $"v{Lut.Version}";
}
