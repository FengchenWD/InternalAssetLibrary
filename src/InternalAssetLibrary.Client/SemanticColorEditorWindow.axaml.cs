using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using InternalAssetLibrary.Client.Core.Settings;
using InternalAssetLibrary.Client.Services;

namespace InternalAssetLibrary.Client;

public sealed class ColorSlotEditor : INotifyPropertyChanged
{
    private string _value;
    private Color _selectedColor;

    public ColorSlotEditor(string key, string label, string value)
    {
        Key = key;
        Label = label;
        _value = value;
        _selectedColor = Color.Parse(value);
    }

    public string Key { get; }

    public string Label { get; }

    public string Value
    {
        get => _value;
        set
        {
            if (_value == value)
            {
                return;
            }

            _value = value;
            if (TryParseColor(value, out var color))
            {
                SetSelectedColor(color, updateValue: false);
            }

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public Color SelectedColor
    {
        get => _selectedColor;
        set => SetSelectedColor(value, updateValue: true);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? ValueChanged;

    public static bool TryParseColor(string? value, out Color color)
    {
        color = default;
        return !string.IsNullOrWhiteSpace(value) &&
            (value.Length == 7 || value.Length == 9) &&
            value[0] == '#' &&
            Color.TryParse(value, out color);
    }

    public static string FormatColor(Color color) =>
        color.A == byte.MaxValue
            ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
            : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    private void SetSelectedColor(Color color, bool updateValue)
    {
        if (_selectedColor != color)
        {
            _selectedColor = color;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedColor)));
        }

        if (updateValue)
        {
            var formatted = FormatColor(color);
            if (_value == formatted)
            {
                return;
            }

            _value = formatted;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}

public sealed partial class SemanticColorEditorWindow : Window
{
    private ClientSettings _settings;
    private SemanticColorPalette _dark;
    private SemanticColorPalette _light;
    private bool _loading;
    private int _loadedPaletteIndex;

    public SemanticColorEditorWindow()
    {
        _settings = new ClientSettings();
        _dark = _settings.DarkColors;
        _light = _settings.LightColors;
        InitializeComponent();
        DataContext = this;
        Services.UiLocalization.Apply(this, Services.UiLocalization.CurrentLanguage);
    }

    public SemanticColorEditorWindow(ClientSettings settings)
        : this()
    {
        _settings = settings;
        _dark = settings.DarkColors;
        _light = settings.LightColors;
        _loadedPaletteIndex = 0;
        LoadSlots(_dark);
    }

    public ObservableCollection<ColorSlotEditor> Slots { get; } = [];

    private void LoadSlots(SemanticColorPalette palette)
    {
        _loading = true;
        Slots.Clear();
        Add("PageBackground", UiLocalization.Text("页面背景"), palette.PageBackground);
        Add("Surface", UiLocalization.Text("卡片与浮层"), palette.Surface);
        Add("Accent", UiLocalization.Text("强调色"), palette.Accent);
        Add("PrimaryText", UiLocalization.Text("主文字"), palette.PrimaryText);
        Add("SecondaryText", UiLocalization.Text("次文字"), palette.SecondaryText);
        Add("Border", UiLocalization.Text("边框"), palette.Border);
        Add("Success", UiLocalization.Text("成功"), palette.Success);
        Add("Warning", UiLocalization.Text("警告"), palette.Warning);
        Add("Error", UiLocalization.Text("错误"), palette.Error);
        _loading = false;
        UpdateWarnings();
    }

    private void Add(string key, string label, string value)
    {
        var slot = new ColorSlotEditor(key, label, value);
        slot.ValueChanged += (_, _) =>
        {
            if (!_loading)
            {
                UpdateWarnings();
            }
        };
        Slots.Add(slot);
    }

    private void PalettePicker_OnSelectionChanged(object? sender, SelectionChangedEventArgs eventArgs)
    {
        if (_loading || Slots.Count == 0)
        {
            return;
        }

        var requestedIndex = PalettePicker.SelectedIndex;
        if (!TryCaptureCurrent(_loadedPaletteIndex))
        {
            _loading = true;
            PalettePicker.SelectedIndex = _loadedPaletteIndex;
            _loading = false;
            return;
        }

        _loadedPaletteIndex = requestedIndex;
        LoadSlots(_loadedPaletteIndex == 1 ? _light : _dark);
    }

    private void RestoreDefaults_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        LoadSlots(PalettePicker.SelectedIndex == 1 ? DefaultColorPalettes.Light : DefaultColorPalettes.Dark);
        _ = TryCaptureCurrent(_loadedPaletteIndex);
    }

    private void Preview_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (!TryCaptureCurrent(_loadedPaletteIndex))
        {
            return;
        }

        ApplyPalette(_loadedPaletteIndex == 1 ? _light : _dark);
        StatusText.Text = UiLocalization.Text("已临时应用当前主题预览；取消会在主窗口恢复原设置。");
    }

    private void Save_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (!TryCaptureCurrent(_loadedPaletteIndex))
        {
            return;
        }

        Close(_settings with { DarkColors = _dark, LightColors = _light });
    }

    private void Cancel_OnClick(object? sender, RoutedEventArgs eventArgs) => Close(null);

    private bool TryCaptureCurrent(int paletteIndex)
    {
        if (!TryBuildPalette(out var palette))
        {
            StatusText.Text = UiLocalization.Text("所有颜色都必须使用 #RRGGBB 或 #AARRGGBB 格式。");
            return false;
        }

        if (paletteIndex == 1)
        {
            _light = palette;
        }
        else
        {
            _dark = palette;
        }

        return true;
    }

    private bool TryBuildPalette(out SemanticColorPalette palette)
    {
        palette = null!;
        var values = Slots.ToDictionary(slot => slot.Key, slot => slot.Value.Trim(), StringComparer.Ordinal);
        if (values.Count != 9 || values.Values.Any(value => !ColorSlotEditor.TryParseColor(value, out _)))
        {
            return false;
        }

        palette = new SemanticColorPalette(
            values["PageBackground"],
            values["Surface"],
            values["Accent"],
            values["PrimaryText"],
            values["SecondaryText"],
            values["Border"],
            values["Success"],
            values["Warning"],
            values["Error"]);
        return true;
    }

    private void UpdateWarnings()
    {
        if (!TryBuildPalette(out var palette))
        {
            StatusText.Text = UiLocalization.Text("颜色值尚未完成。");
            return;
        }

        var warnings = ColorContrastAnalyzer.FindTextWarnings(palette);
        StatusText.Text = warnings.Count == 0
            ? UiLocalization.Text("文字与背景对比度符合 4.5:1 建议值。")
            : UiLocalization.Format(
                "对比度警告：有 {0:N0} 组文字与背景低于 4.5:1；仍允许保存。",
                warnings.Count);
    }

    private static void ApplyPalette(SemanticColorPalette colors)
    {
        SetBrush("PageBackgroundBrush", colors.PageBackground);
        SetBrush("NavigationBrush", colors.PageBackground);
        SetBrush("SurfaceBrush", colors.Surface);
        SetBrush("SurfaceRaisedBrush", Blend(colors.Surface, colors.PrimaryText, 0.06));
        SetBrush("AccentBrush", colors.Accent);
        SetBrush("AccentSoftBrush", Blend(colors.Surface, colors.Accent, 0.16));
        var selection = Blend(
            colors.Surface,
            colors.Accent,
            IsDark(colors.PageBackground) ? 0.42 : 0.30);
        SetBrush("SelectionBrush", selection);
        SetBrush("SelectionContentBrush", ChooseHighContrastContent(selection, colors.PrimaryText, colors.PageBackground));
        SetBrush("SelectionBorderBrush", colors.Accent);
        SetBrush("PrimaryTextBrush", colors.PrimaryText);
        SetBrush("SecondaryTextBrush", colors.SecondaryText);
        SetBrush("BorderBrush", colors.Border);
        SetBrush("SuccessBrush", colors.Success);
        SetBrush("WarningBrush", colors.Warning);
        SetBrush("ErrorBrush", colors.Error);
        SetBrush("AccentContentBrush", ChooseAccentContent(colors.Accent, colors.PageBackground, colors.PrimaryText));
    }

    private static string ChooseAccentContent(string accent, string pageBackground, string primaryText) =>
        ChooseHighContrastContent(accent, primaryText, pageBackground);

    private static string ChooseHighContrastContent(string background, string primaryText, string pageBackground)
    {
        var backgroundColor = Color.Parse(background);
        var candidates = new[] { primaryText, pageBackground, "#FFFFFF", "#000000" };
        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(candidate => ContrastRatio(backgroundColor, Color.Parse(candidate)))
            .First();
    }

    private static bool IsDark(string color) => ContrastRatio(Color.Parse(color), Colors.White) >
                                                ContrastRatio(Color.Parse(color), Colors.Black);

    private static double ContrastRatio(Color first, Color second)
    {
        static double RelativeLuminance(Color color)
        {
            static double Linearize(byte channel)
            {
                var value = channel / 255d;
                return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
            }

            return 0.2126 * Linearize(color.R) + 0.7152 * Linearize(color.G) + 0.0722 * Linearize(color.B);
        }

        var lighter = Math.Max(RelativeLuminance(first), RelativeLuminance(second));
        var darker = Math.Min(RelativeLuminance(first), RelativeLuminance(second));
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static void SetBrush(string key, string value) =>
        Avalonia.Application.Current!.Resources[key] = new SolidColorBrush(Color.Parse(value));

    private static string Blend(string background, string foreground, double foregroundWeight)
    {
        var back = Color.Parse(background);
        var front = Color.Parse(foreground);
        static byte Mix(byte left, byte right, double weight) =>
            (byte)Math.Clamp(Math.Round(left * (1 - weight) + right * weight), 0, 255);

        return Color.FromRgb(
            Mix(back.R, front.R, foregroundWeight),
            Mix(back.G, front.G, foregroundWeight),
            Mix(back.B, front.B, foregroundWeight)).ToString();
    }
}
