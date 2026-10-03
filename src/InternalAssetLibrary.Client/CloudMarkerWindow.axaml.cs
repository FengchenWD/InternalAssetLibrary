using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using InternalAssetLibrary.Client.Core.Http;
using InternalAssetLibrary.Client.Services;
using InternalAssetLibrary.Contracts;

namespace InternalAssetLibrary.Client;

public sealed record CloudMarkerSetOption(
    Guid Id,
    string Name,
    Guid OwnerUserId,
    string OwnerDisplayName,
    int MarkerCount,
    bool IsBasedOnOldVersion)
{
    public string OwnerLabel => UiLocalization.Format("所有者：{0}", OwnerDisplayName);

    public string VersionLabel => UiLocalization.Text(IsBasedOnOldVersion ? "旧素材版本" : "当前素材版本");

    public string MarkerCountLabel => UiLocalization.Format("{0:N0} 个标记", MarkerCount);
}

public sealed record CloudMarkerRow(Guid Id, TimeSpan Time, string? Name, string? Note)
{
    public string TimeLabel => FormatTime(Time);

    public string NameLabel => string.IsNullOrWhiteSpace(Name) ? "-" : Name;

    public string NoteLabel => string.IsNullOrWhiteSpace(Note) ? "-" : Note;

    private static string FormatTime(TimeSpan time)
    {
        var hours = (long)Math.Floor(time.TotalHours);
        return $"{hours:00}:{time.Minutes:00}:{time.Seconds:00}";
    }
}

public sealed partial class CloudMarkerWindow : Window
{
    private static FilePickerFileType CsvFileType => new(UiLocalization.Text("标记 CSV"))
    {
        Patterns = ["*.csv"],
        MimeTypes = ["text/csv"]
    };

    private readonly AssetLibraryApiClient _api;
    private readonly SemaphoreSlim _nativePickerGate = new(1, 1);
    private readonly ApiAsset _asset;
    private readonly ApiCurrentUser _currentUser;
    private readonly Func<Task<string?>>? _ensureLocalPath;
    private MarkerSetDetail? _activeSet;
    private MarkerSetAccess? _activeAccess;
    private bool _changingSelection;
    private bool _closing;

    public CloudMarkerWindow()
    {
        _api = null!;
        _asset = null!;
        _currentUser = null!;
        InitializeComponent();
        DataContext = this;
        Services.UiLocalization.Apply(this, Services.UiLocalization.CurrentLanguage);
        ApplyAccess(null);
        Closing += (_, _) => _closing = true;
    }

    public CloudMarkerWindow(
        AssetLibraryApiClient api,
        ApiAsset asset,
        ApiCurrentUser currentUser,
        Func<Task<string?>>? ensureLocalPath = null)
        : this()
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _asset = asset ?? throw new ArgumentNullException(nameof(asset));
        _currentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));
        _ensureLocalPath = ensureLocalPath;
        AssetNameText.Text = asset.Name;
        AssetMetaText.Text = UiLocalization.Format(
            "{0} · {1} · 团队标记",
            CategoryLabel(asset.Category),
            asset.OriginalFileName);

        var canMaintain = currentUser.IsAdmin || currentUser.Permissions.Contains(
            "markers.maintain",
            StringComparer.OrdinalIgnoreCase);
        NewSetButton.IsEnabled = canMaintain;
        ImportButton.IsEnabled = canMaintain;
        Opened += async (_, _) => await ReloadSetsAsync();
    }

    public ObservableCollection<CloudMarkerSetOption> MarkerSets { get; } = [];

    public ObservableCollection<CloudMarkerRow> Markers { get; } = [];

    private async Task ReloadSetsAsync(Guid? selectId = null)
    {
        if (_closing)
        {
            return;
        }

        try
        {
            StatusText.Text = UiLocalization.Text("正在读取团队标记…");
            var sets = await _api.ListMarkerSetsAsync(_asset.Id);
            if (_closing)
            {
                return;
            }

            _changingSelection = true;
            MarkerSets.Clear();
            foreach (var set in sets)
            {
                MarkerSets.Add(new CloudMarkerSetOption(
                    set.Id,
                    set.Name,
                    set.OwnerUserId,
                    set.OwnerDisplayName,
                    set.MarkerCount,
                    set.IsBasedOnOldVersion));
            }

            SetCountText.Text = MarkerSets.Count.ToString("N0", CultureInfo.CurrentCulture);
            EmptySetsText.IsVisible = MarkerSets.Count == 0;
            MarkerSetList.SelectedItem = MarkerSets.FirstOrDefault(item => item.Id == selectId) ?? MarkerSets.FirstOrDefault();
            _changingSelection = false;
            StatusText.Text = UiLocalization.Text(
                MarkerSets.Count == 0 ? "尚无团队标记集。" : "团队标记已刷新。");
            await LoadSelectedSetAsync();
        }
        catch (Exception exception)
        {
            _changingSelection = false;
            ShowError(exception);
        }
    }

    private async void MarkerSetList_OnSelectionChanged(object? sender, SelectionChangedEventArgs eventArgs)
    {
        if (!_changingSelection)
        {
            await LoadSelectedSetAsync();
        }
    }

    private async Task LoadSelectedSetAsync()
    {
        if (_closing)
        {
            return;
        }

        _activeSet = null;
        _activeAccess = null;
        Markers.Clear();
        ApplyAccess(null);
        ClearEditor();
        if (MarkerSetList.SelectedItem is not CloudMarkerSetOption selected)
        {
            EmptyMarkersText.Text = UiLocalization.Text("请选择一个标记集");
            EmptyMarkersText.IsVisible = true;
            AccessText.Text = "";
            return;
        }

        try
        {
            var detailTask = _api.GetMarkerSetAsync(selected.Id);
            var accessTask = _api.GetMarkerSetAccessAsync(selected.Id);
            await Task.WhenAll(detailTask, accessTask);
            if (_closing ||
                MarkerSetList.SelectedItem is not CloudMarkerSetOption current ||
                current.Id != selected.Id)
            {
                return;
            }

            _activeSet = await detailTask;
            _activeAccess = await accessTask;
            foreach (var marker in _activeSet.Markers)
            {
                Markers.Add(new CloudMarkerRow(marker.Id, marker.Time, marker.Name, marker.Note));
            }

            EmptyMarkersText.Text = UiLocalization.Text("当前标记集还没有标记");
            EmptyMarkersText.IsVisible = Markers.Count == 0;
            ApplyAccess(_activeAccess);
            AccessText.Text = AccessLabel(_activeSet, _activeAccess);
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async void NewSet_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        var name = await new TextPromptWindow("新建云端标记集", "输入标记集名称。")
            .ShowDialog<string?>(this);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        try
        {
            var created = await _api.CreateMarkerSetAsync(new CreateMarkerSetRequest(
                _asset.Id,
                _asset.CurrentVersionId,
                name.Trim()));
            await ReloadSetsAsync(created.Id);
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async void RenameSet_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_activeSet is null || _activeAccess?.CanEdit != true)
        {
            return;
        }

        var name = await new TextPromptWindow("重命名标记集", "输入新的标记集名称。", _activeSet.Name)
            .ShowDialog<string?>(this);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        try
        {
            var renamed = await _api.RenameMarkerSetAsync(
                _activeSet.Id,
                new RenameMarkerSetRequest(name.Trim()));
            await ReloadSetsAsync(renamed.Id);
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async void CopySet_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_activeSet is null || _activeAccess?.CanCopy != true || _activeSet.OwnerUserId == _currentUser.Id)
        {
            return;
        }

        var name = await new TextPromptWindow(
                "复制标记集",
                "副本会保存到你的名下，之后可以独立编辑。",
                UiLocalization.Format("{0} - 副本", _activeSet.Name))
            .ShowDialog<string?>(this);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        try
        {
            var copy = await _api.CopyMarkerSetAsync(new CopyMarkerSetRequest(_activeSet.Id, name.Trim()));
            await ReloadSetsAsync(copy.Id);
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async void DeleteSet_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_activeSet is null || _activeAccess?.CanDelete != true)
        {
            return;
        }

        var activeSet = _activeSet;
        var confirmed = await new MessageDialogWindow(
                "删除云端标记集",
                UiLocalization.Format(
                    "确定删除云端标记集“{0}”吗？其中的 {1:N0} 个标记也会被删除，此操作无法恢复。",
                    activeSet.Name,
                    activeSet.Markers.Count),
                "确认删除",
                "取消")
            .ShowDialog<bool>(this);
        if (!confirmed)
        {
            return;
        }

        try
        {
            await _api.DeleteMarkerSetAsync(activeSet.Id);
            await ReloadSetsAsync();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async void Import_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        var files = await RunNativePickerAsync(
            "cloud-marker-import",
            () => StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = UiLocalization.Text("导入九列标记 CSV"),
                AllowMultiple = false,
                FileTypeFilter = [CsvFileType]
            }));
        if (files is null || files.Count == 0)
        {
            return;
        }

        var suggestedName = MarkerSetNameFromFile(files[0].Name);
        var name = await new TextPromptWindow(
                "导入为新标记集",
                "CSV 会作为你名下的新标记集导入，不会覆盖已有标记。",
                suggestedName)
            .ShowDialog<string?>(this);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        try
        {
            await using var input = await files[0].OpenReadAsync();
            var imported = await _api.ImportMarkerCsvAsync(
                new ImportMarkerSetRequest(_asset.Id, _asset.CurrentVersionId, name.Trim()),
                input,
                input.CanSeek ? input.Length : null);
            await ReloadSetsAsync(imported.Id);
            if (!_closing)
            {
                StatusText.Text = UiLocalization.Text("CSV 已作为你的新标记集导入。");
            }
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async void Export_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_activeSet is not { } activeSet || _activeAccess?.CanView != true)
        {
            return;
        }

        var target = await RunNativePickerAsync(
            "cloud-marker-export",
            () => StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = UiLocalization.Text("导出九列标记 CSV"),
                SuggestedFileName = $"{SafeFileName(activeSet.Name)}.markers.csv",
                DefaultExtension = "csv",
                FileTypeChoices = [CsvFileType]
            }));
        if (target is null)
        {
            return;
        }

        try
        {
            var localPath = _ensureLocalPath is null ? null : await _ensureLocalPath();
            if (_ensureLocalPath is not null && string.IsNullOrWhiteSpace(localPath))
            {
                if (!_closing)
                {
                    StatusText.Text = UiLocalization.Text("未选择素材默认保存目录，已取消导出。");
                }

                return;
            }

            await using var output = await target.OpenWriteAsync();
            if (output.CanSeek)
            {
                output.SetLength(0);
            }

            await _api.ExportMarkerCsvAsync(
                activeSet.Id,
                output,
                new MarkerCsvRecordingInfo(_asset.OriginalFileName, localPath, null, null));
            if (!_closing)
            {
                StatusText.Text = UiLocalization.Text("已按固定九列格式导出 CSV。");
            }
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private void MarkerList_OnSelectionChanged(object? sender, SelectionChangedEventArgs eventArgs)
    {
        if (MarkerList.SelectedItem is not CloudMarkerRow marker)
        {
            ClearEditor();
            return;
        }

        MarkerTimeBox.Text = marker.TimeLabel;
        MarkerNameBox.Text = marker.Name;
        MarkerNoteBox.Text = marker.Note;
        SaveMarkerButton.Content = UiLocalization.Text("保存修改");
        DeleteMarkerButton.IsEnabled = _activeAccess?.CanDelete == true;
    }

    private async void SaveMarker_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_activeSet is null || _activeAccess?.CanEdit != true)
        {
            return;
        }

        if (!TryParseWholeSecond(MarkerTimeBox.Text, out var time))
        {
            StatusText.Text = UiLocalization.Text("时间格式无效，请使用 HH:mm:ss；小时可以超过 23。");
            return;
        }

        var request = new UpsertMarkerRequest(
            time,
            EmptyToNull(MarkerNameBox.Text),
            EmptyToNull(MarkerNoteBox.Text));
        try
        {
            var activeSetId = _activeSet.Id;
            if (MarkerList.SelectedItem is CloudMarkerRow selected)
            {
                await _api.UpdateMarkerAsync(activeSetId, selected.Id, request);
            }
            else
            {
                await _api.AddMarkerAsync(activeSetId, request);
            }

            await ReloadSetsAsync(activeSetId);
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async void DeleteMarker_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_activeSet is null || _activeAccess?.CanDelete != true || MarkerList.SelectedItem is not CloudMarkerRow selected)
        {
            return;
        }

        var message = string.IsNullOrWhiteSpace(selected.Name)
            ? UiLocalization.Format(
                "确定删除 {0} 处的未命名标记吗？此操作无法恢复。",
                selected.TimeLabel)
            : UiLocalization.Format(
                "确定删除 {0} 处的标记“{1}”吗？此操作无法恢复。",
                selected.TimeLabel,
                selected.Name);
        var confirmed = await new MessageDialogWindow(
                "删除标记",
                message,
                "确认删除",
                "取消")
            .ShowDialog<bool>(this);
        if (!confirmed)
        {
            return;
        }

        try
        {
            var activeSetId = _activeSet.Id;
            await _api.DeleteMarkerAsync(activeSetId, selected.Id);
            await ReloadSetsAsync(activeSetId);
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async void Refresh_OnClick(object? sender, RoutedEventArgs eventArgs) => await ReloadSetsAsync(_activeSet?.Id);

    private void ClearEditor_OnClick(object? sender, RoutedEventArgs eventArgs) => ClearEditor();

    private void ClearEditor()
    {
        MarkerList.SelectedItem = null;
        MarkerTimeBox.Text = "00:00:00";
        MarkerNameBox.Text = string.Empty;
        MarkerNoteBox.Text = string.Empty;
        SaveMarkerButton.Content = UiLocalization.Text("添加标记");
        DeleteMarkerButton.IsEnabled = false;
    }

    private void ApplyAccess(MarkerSetAccess? access)
    {
        var hasSet = _activeSet is not null;
        var canEdit = hasSet && access?.CanEdit == true;
        RenameSetButton.IsEnabled = canEdit;
        CopySetButton.IsEnabled = hasSet && access?.CanCopy == true && _activeSet!.OwnerUserId != _currentUser?.Id;
        ExportButton.IsEnabled = hasSet && access?.CanView == true;
        DeleteSetButton.IsEnabled = hasSet && access?.CanDelete == true;
        MarkerTimeBox.IsEnabled = canEdit;
        MarkerNameBox.IsEnabled = canEdit;
        MarkerNoteBox.IsEnabled = canEdit;
        ClearEditorButton.IsEnabled = canEdit;
        SaveMarkerButton.IsEnabled = canEdit;
        DeleteMarkerButton.IsEnabled = hasSet && access?.CanDelete == true && MarkerList.SelectedItem is CloudMarkerRow;
    }

    private string AccessLabel(MarkerSetDetail set, MarkerSetAccess access)
    {
        var version = UiLocalization.Text(set.IsBasedOnOldVersion ? "基于旧素材版本" : "基于当前素材版本");
        if (access.CanEdit)
        {
            return UiLocalization.Format(
                "你的标记集 · {0} · 新建和修改时间会吸附到整秒。",
                version);
        }

        if (access.CanDelete && set.OwnerUserId != _currentUser.Id)
        {
            return UiLocalization.Format(
                "{0} 的只读标记 · {1} · 可复制到自己；管理员可删除但不能修改。",
                set.OwnerDisplayName,
                version);
        }

        return access.CanCopy
            ? UiLocalization.Format(
                "{0} 的只读标记 · {1} · 可复制到自己编辑。",
                set.OwnerDisplayName,
                version)
            : UiLocalization.Format("{0} 的只读标记 · {1}。", set.OwnerDisplayName, version);
    }

    private static bool TryParseWholeSecond(string? value, out TimeSpan time)
    {
        time = default;
        var parts = (value ?? string.Empty).Trim().Split(':');
        if (parts.Length != 3 ||
            !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) ||
            !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ||
            hours < 0 || minutes is < 0 or > 59 || seconds is < 0 or > 59)
        {
            return false;
        }

        try
        {
            time = TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static string CategoryLabel(ApiAssetCategory category) => category switch
    {
        ApiAssetCategory.Bgm => "BGM",
        ApiAssetCategory.SoundEffect => UiLocalization.Text("音效"),
        ApiAssetCategory.Image => UiLocalization.Text("图片"),
        ApiAssetCategory.Video => UiLocalization.Text("视频"),
        _ => UiLocalization.Text("素材")
    };

    private static string MarkerSetNameFromFile(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        return name.EndsWith(".markers", StringComparison.OrdinalIgnoreCase)
            ? name[..^8]
            : name;
    }

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = string.Concat(value.Select(character => invalid.Contains(character) ? '_' : character));
        return string.IsNullOrWhiteSpace(safe) ? "markers" : safe;
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
                StatusText.Text = UiLocalization.Text("已有文件选择窗口正在打开，请先完成或取消当前选择。");
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
            ClientDiagnostics.WriteException($"native-picker-{operation}", exception, isFatal: false);
            if (!_closing)
            {
                StatusText.Text = UiLocalization.Format(
                    "无法打开文件选择窗口：{0}",
                    UserVisibleNetworkMessage.RedactLocations(exception.Message, _api.BaseAddress));
            }

            return null;
        }
        finally
        {
            _nativePickerGate.Release();
        }
    }

    private void ShowError(Exception exception)
    {
        if (!_closing)
        {
            StatusText.Text = UiLocalization.Format(
                "操作失败：{0}",
                UserVisibleNetworkMessage.RedactLocations(exception.Message, _api.BaseAddress));
        }
    }

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void Close_OnClick(object? sender, RoutedEventArgs eventArgs) => Close();
}
