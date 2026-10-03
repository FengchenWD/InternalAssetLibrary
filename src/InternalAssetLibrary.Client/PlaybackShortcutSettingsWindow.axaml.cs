using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using InternalAssetLibrary.Client.Core.Settings;
using InternalAssetLibrary.Client.Services;

namespace InternalAssetLibrary.Client;

public sealed partial class PlaybackShortcutSettingsWindow : Window
{
    private PlaybackShortcutSettings _workingSettings;
    private PlaybackShortcutAction? _capturingAction;

    public PlaybackShortcutSettingsWindow()
        : this(new PlaybackShortcutSettings())
    {
    }

    public PlaybackShortcutSettingsWindow(PlaybackShortcutSettings settings)
    {
        _workingSettings = settings.ValidateAndNormalize();
        InitializeComponent();
        DataContext = this;
        UiLocalization.Register(this, static window => window.RefreshLocalization());
        UiLocalization.Apply(this, UiLocalization.CurrentLanguage);
        RebuildRows();
    }

    public ObservableCollection<PlaybackShortcutRowViewModel> Rows { get; } = [];

    private void RebuildRows()
    {
        Rows.Clear();
        foreach (var definition in PlaybackShortcutCatalog.Definitions)
        {
            Rows.Add(new PlaybackShortcutRowViewModel(
                definition.Action,
                UiLocalization.Text(definition.ChineseName),
                FormatGesture(definition.GetGesture(_workingSettings))));
        }
    }

    private void RefreshLocalization()
    {
        foreach (var row in Rows)
        {
            row.ActionLabel = UiLocalization.Text(
                PlaybackShortcutCatalog.Get(row.Action).ChineseName);
            row.GestureLabel = FormatGesture(
                PlaybackShortcutCatalog.Get(row.Action).GetGesture(_workingSettings));
        }
    }

    private void ShortcutButton_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not Button { Tag: PlaybackShortcutAction action })
        {
            return;
        }

        _capturingAction = action;
        UiLocalization.SetText(
            ShortcutStatusText,
            "正在设置“{0}”，请按下新的快捷键。",
            UiLocalization.Text(PlaybackShortcutCatalog.Get(action).ChineseName));
    }

    private void Window_OnKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (_capturingAction is not { } action)
        {
            if (eventArgs.Key == Key.Escape)
            {
                Close(null);
                eventArgs.Handled = true;
            }

            return;
        }

        eventArgs.Handled = true;
        if (!PlaybackShortcutGesture.TryCreate(eventArgs, out var gesture))
        {
            UiLocalization.SetText(ShortcutStatusText, "请同时按下一个非修饰键。");
            return;
        }

        var serialized = gesture.Serialize();
        var conflict = PlaybackShortcutCatalog.Definitions.FirstOrDefault(definition =>
            definition.Action != action &&
            string.Equals(
                definition.GetGesture(_workingSettings),
                serialized,
                StringComparison.OrdinalIgnoreCase));
        if (conflict is not null)
        {
            UiLocalization.SetText(
                ShortcutStatusText,
                "“{0}”已被“{1}”使用，请先修改冲突项。",
                gesture.ToDisplayText(),
                UiLocalization.Text(conflict.ChineseName));
            return;
        }

        var definition = PlaybackShortcutCatalog.Get(action);
        _workingSettings = definition.SetGesture(_workingSettings, serialized);
        var row = Rows.Single(value => value.Action == action);
        row.GestureLabel = gesture.ToDisplayText();
        _capturingAction = null;
        UiLocalization.SetText(
            ShortcutStatusText,
            "“{0}”已设置为 {1}。",
            UiLocalization.Text(definition.ChineseName),
            gesture.ToDisplayText());
    }

    private void RestoreDefaults_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        _workingSettings = new PlaybackShortcutSettings();
        _capturingAction = null;
        RebuildRows();
        UiLocalization.SetText(ShortcutStatusText, "快捷键已恢复默认值，点击保存后生效。");
    }

    private void Save_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        try
        {
            Close(_workingSettings.ValidateAndNormalize());
        }
        catch (Exception exception)
        {
            UiLocalization.SetText(ShortcutStatusText, "快捷键设置无效：{0}", exception.Message);
        }
    }

    private void Cancel_OnClick(object? sender, RoutedEventArgs eventArgs) => Close(null);

    private static string FormatGesture(string value) =>
        PlaybackShortcutGesture.TryParse(value, out var gesture)
            ? gesture.ToDisplayText()
            : value;
}

public sealed class PlaybackShortcutRowViewModel : INotifyPropertyChanged
{
    private string _actionLabel;
    private string _gestureLabel;

    internal PlaybackShortcutRowViewModel(
        PlaybackShortcutAction action,
        string actionLabel,
        string gestureLabel)
    {
        Action = action;
        _actionLabel = actionLabel;
        _gestureLabel = gestureLabel;
    }

    public PlaybackShortcutAction Action { get; }

    public string ActionLabel
    {
        get => _actionLabel;
        set => SetField(ref _actionLabel, value);
    }

    public string GestureLabel
    {
        get => _gestureLabel;
        set => SetField(ref _gestureLabel, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField(ref string field, string value, [CallerMemberName] string? propertyName = null)
    {
        if (string.Equals(field, value, StringComparison.Ordinal))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
