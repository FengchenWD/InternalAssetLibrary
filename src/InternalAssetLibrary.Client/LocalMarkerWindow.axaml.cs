using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using InternalAssetLibrary.Client.Core.Markers;
using InternalAssetLibrary.Client.Services;
using InternalAssetLibrary.Contracts;

namespace InternalAssetLibrary.Client;

public sealed record LocalMarkerSetOption(Guid Id, string Name, int MarkerCount)
{
    public string DisplayLabel => UiLocalization.Format("{0}（{1}）", Name, MarkerCount);
}

public sealed record LocalMarkerRow(Guid Id, TimeSpan Time, string? Name, string? Note)
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

public sealed partial class LocalMarkerWindow : Window
{
    private static FilePickerFileType CsvFileType => new(UiLocalization.Text("标记 CSV"))
    {
        Patterns = ["*.csv"],
        MimeTypes = ["text/csv"]
    };

    private readonly LocalMarkerService _service;
    private readonly SemaphoreSlim _nativePickerGate = new(1, 1);
    private readonly Guid _assetId;
    private readonly string _assetPath;
    private LocalMarkerSetDetail? _activeSet;
    private bool _changingSelection;
    private bool _closing;

    public LocalMarkerWindow()
    {
        _service = null!;
        _assetPath = string.Empty;
        InitializeComponent();
        DataContext = this;
        Services.UiLocalization.Apply(this, Services.UiLocalization.CurrentLanguage);
        Closing += (_, _) => _closing = true;
    }

    public LocalMarkerWindow(LocalMarkerService service, Guid assetId, string assetName, string assetPath)
        : this()
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _assetId = assetId == Guid.Empty
            ? throw new ArgumentException("Asset ID cannot be empty.", nameof(assetId))
            : assetId;
        _assetPath = assetPath;
        AssetNameText.Text = assetName;
        Opened += async (_, _) => await ReloadSetsAsync();
    }

    public ObservableCollection<LocalMarkerSetOption> MarkerSets { get; } = [];

    public ObservableCollection<LocalMarkerRow> Markers { get; } = [];

    private async Task ReloadSetsAsync(Guid? selectId = null)
    {
        if (_closing)
        {
            return;
        }

        try
        {
            var sets = await _service.ListAsync(_assetId);
            if (_closing)
            {
                return;
            }

            _changingSelection = true;
            MarkerSets.Clear();
            foreach (var set in sets)
            {
                MarkerSets.Add(new LocalMarkerSetOption(set.Id, set.Name, set.MarkerCount));
            }

            MarkerSetPicker.SelectedItem = MarkerSets.FirstOrDefault(item => item.Id == selectId) ?? MarkerSets.FirstOrDefault();
            _changingSelection = false;
            await LoadSelectedSetAsync();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async void MarkerSetPicker_OnSelectionChanged(object? sender, SelectionChangedEventArgs eventArgs)
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
        Markers.Clear();
        if (MarkerSetPicker.SelectedItem is not LocalMarkerSetOption selected)
        {
            EmptyMarkersText.Text = UiLocalization.Text("新建或导入一个标记集后即可添加标记");
            EmptyMarkersText.IsVisible = true;
            StatusText.Text = "";
            ClearEditor();
            return;
        }

        try
        {
            var activeSet = await _service.GetAsync(_assetId, selected.Id);
            if (_closing)
            {
                return;
            }

            _activeSet = activeSet;
            foreach (var marker in _activeSet.Markers)
            {
                Markers.Add(new LocalMarkerRow(marker.Id, marker.Time, marker.Name, marker.Note));
            }

            EmptyMarkersText.Text = UiLocalization.Text("当前标记集还没有标记");
            EmptyMarkersText.IsVisible = Markers.Count == 0;
            StatusText.Text = UiLocalization.Format(
                "共 {0:N0} 个标记；界面中新建和修改的时间会吸附到整秒。",
                Markers.Count);
            ClearEditor();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async void NewSet_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        var name = await new TextPromptWindow("新建标记集", "输入标记集名称。")
            .ShowDialog<string?>(this);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        try
        {
            var created = await _service.CreateAsync(
                _assetId,
                name,
                recordingInfo: new MarkerCsvRecordingInfo(Path.GetFileName(_assetPath), _assetPath, null, null));
            await ReloadSetsAsync(created.Id);
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async void RenameSet_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_activeSet is null)
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
            var renamed = await _service.RenameAsync(_assetId, _activeSet.Id, name);
            await ReloadSetsAsync(renamed.Id);
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async void DeleteSet_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_activeSet is null)
        {
            return;
        }

        var activeSet = _activeSet;
        var confirmed = await new MessageDialogWindow(
                "删除标记集",
                UiLocalization.Format(
                    "确定删除标记集“{0}”吗？其中的 {1:N0} 个标记也会被删除，此操作无法恢复。",
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
            await _service.DeleteMarkerSetAsync(_assetId, activeSet.Id);
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
            "local-marker-import",
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

        try
        {
            await using var input = await files[0].OpenReadAsync();
            using var memory = new MemoryStream();
            await input.CopyToAsync(memory);
            var name = Path.GetFileNameWithoutExtension(files[0].Name);
            var imported = await _service.ImportCsvAsync(_assetId, name, memory.ToArray());
            await ReloadSetsAsync(imported.Id);
            if (!_closing)
            {
                StatusText.Text = UiLocalization.Text("CSV 已作为新的标记集导入，没有覆盖已有标记。");
            }
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async void Export_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_activeSet is not { } activeSet)
        {
            return;
        }

        var target = await RunNativePickerAsync(
            "local-marker-export",
            () => StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = UiLocalization.Text("导出九列标记 CSV"),
                SuggestedFileName = $"{Path.GetFileNameWithoutExtension(_assetPath)}.markers.csv",
                DefaultExtension = "csv",
                FileTypeChoices = [CsvFileType]
            }));
        if (target is null)
        {
            return;
        }

        try
        {
            var bytes = await _service.ExportCsvAsync(_assetId, activeSet.Id);
            await using var output = await target.OpenWriteAsync();
            output.SetLength(0);
            await output.WriteAsync(bytes);
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
        if (MarkerList.SelectedItem is not LocalMarkerRow marker)
        {
            ClearEditor();
            return;
        }

        MarkerTimeBox.Text = marker.TimeLabel;
        MarkerNameBox.Text = marker.Name;
        MarkerNoteBox.Text = marker.Note;
        SaveMarker_OnClickButtonText(UiLocalization.Text("保存修改"));
    }

    private async void SaveMarker_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_activeSet is null)
        {
            StatusText.Text = UiLocalization.Text("请先新建或导入一个标记集。");
            return;
        }

        if (!TryParseWholeSecond(MarkerTimeBox.Text, out var time))
        {
            StatusText.Text = UiLocalization.Text("时间格式无效，请使用 HH:mm:ss；小时可以超过 23。");
            return;
        }

        var request = new UpsertMarkerRequest(time, EmptyToNull(MarkerNameBox.Text), EmptyToNull(MarkerNoteBox.Text));
        try
        {
            if (MarkerList.SelectedItem is LocalMarkerRow selected)
            {
                await _service.UpdateMarkerAsync(_assetId, _activeSet.Id, selected.Id, request);
            }
            else
            {
                await _service.AddMarkerAsync(_assetId, _activeSet.Id, request);
            }

            await ReloadSetsAsync(_activeSet.Id);
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async void DeleteMarker_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_activeSet is null || MarkerList.SelectedItem is not LocalMarkerRow selected)
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
            await _service.DeleteMarkerAsync(_assetId, _activeSet.Id, selected.Id);
            await ReloadSetsAsync(_activeSet.Id);
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private void ClearEditor_OnClick(object? sender, RoutedEventArgs eventArgs) => ClearEditor();

    private void ClearEditor()
    {
        MarkerList.SelectedItem = null;
        MarkerTimeBox.Text = "00:00:00";
        MarkerNameBox.Text = string.Empty;
        MarkerNoteBox.Text = string.Empty;
        SaveMarker_OnClickButtonText(UiLocalization.Text("添加标记"));
    }

    private void SaveMarker_OnClickButtonText(string value)
    {
        SaveMarkerButton.Content = value;
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
                StatusText.Text = UiLocalization.Format("无法打开文件选择窗口：{0}", exception.Message);
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
            StatusText.Text = UiLocalization.Format("操作失败：{0}", exception.Message);
        }
    }

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void Close_OnClick(object? sender, RoutedEventArgs eventArgs) => Close();
}
