using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using InternalAssetLibrary.Client.Services;

namespace InternalAssetLibrary.Client;

public sealed record EditorExportOptions(
    bool UseSourceSettings,
    string Format,
    int Width,
    int Height,
    double FrameRate,
    int VideoBitrateKbps,
    string AudioFormat,
    int AudioBitrateKbps);

public sealed partial class EditorExportOptionsWindow : Window
{
    private readonly bool _isAudio;
    private readonly int _sourceWidth;
    private readonly int _sourceHeight;
    private readonly double _sourceFrameRate;
    private readonly int _sourceVideoBitrateKbps;
    private readonly string _sourceVideoFormat;
    private readonly string _sourceAudioFormat;
    private readonly int _sourceAudioBitrateKbps;
    private bool _isInitializing;

    public EditorExportOptionsWindow() : this(false, 1920, 1080, 30, 8000, "mp4", "wav", 192) { }

    public EditorExportOptionsWindow(
        bool isAudio,
        int width,
        int height,
        double frameRate,
        int videoBitrateKbps,
        string sourceVideoFormat,
        string sourceAudioFormat,
        int audioBitrateKbps)
    {
        _isInitializing = true;
        InitializeComponent();
        _isAudio = isAudio;
        _sourceWidth = Math.Max(16, width);
        _sourceHeight = Math.Max(16, height);
        _sourceFrameRate = frameRate > 0 && double.IsFinite(frameRate) ? frameRate : 30;
        _sourceVideoBitrateKbps = Math.Clamp(videoBitrateKbps, 128, 200000);
        _sourceVideoFormat = NormalizeVideoFormat(sourceVideoFormat);
        _sourceAudioFormat = NormalizeAudioFormat(sourceAudioFormat);
        _sourceAudioBitrateKbps = Math.Clamp(audioBitrateKbps, 32, 1536);

        VideoOptionsPanel.IsVisible = !_isAudio;
        AudioOptionsPanel.IsVisible = _isAudio;
        WidthBox.Text = _sourceWidth.ToString(CultureInfo.InvariantCulture);
        HeightBox.Text = _sourceHeight.ToString(CultureInfo.InvariantCulture);
        FrameRateBox.Text = _sourceFrameRate.ToString("0.###", CultureInfo.InvariantCulture);
        VideoBitrateBox.Text = _sourceVideoBitrateKbps.ToString(CultureInfo.InvariantCulture);
        AudioBitrateBox.Text = _sourceAudioBitrateKbps.ToString(CultureInfo.InvariantCulture);
        VideoFormatBox.SelectedIndex = _sourceVideoFormat == "mov" ? 1 : 0;
        AudioFormatBox.SelectedIndex = _sourceAudioFormat switch
        {
            "wav" => 1,
            "flac" => 2,
            "m4a" => 3,
            _ => 0
        };
        RatioBox.SelectedIndex = FindRatioIndex(_sourceWidth, _sourceHeight);
        ApplySourceSettingsState();
        _isInitializing = false;
    }

    private void UseSourceSettings_OnChanged(object? sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        ApplySourceSettingsState();
    }

    private void RatioBox_OnChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Avalonia can raise SelectionChanged while InitializeComponent is still
        // wiring named controls. Do not read sibling controls until construction
        // and the initial source settings have completed.
        if (_isInitializing || UseSourceSettingsBox is null || RatioBox is null || WidthBox is null || HeightBox is null)
        {
            return;
        }

        if (UseSourceSettingsBox.IsChecked == true || RatioBox.SelectedIndex == 4 ||
            !int.TryParse(WidthBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) || width < 16)
        {
            return;
        }

        var (ratioWidth, ratioHeight) = RatioBox.SelectedIndex switch
        {
            1 => (9, 16),
            2 => (4, 3),
            3 => (1, 1),
            _ => (16, 9)
        };
        HeightBox.Text = Math.Max(16, (int)Math.Round(width * ratioHeight / (double)ratioWidth))
            .ToString(CultureInfo.InvariantCulture);
    }

    private void ApplySourceSettingsState()
    {
        var enabled = UseSourceSettingsBox.IsChecked != true;
        VideoOptionsPanel.IsEnabled = enabled;
        AudioOptionsPanel.IsEnabled = enabled;
        if (!enabled)
        {
            WidthBox.Text = _sourceWidth.ToString(CultureInfo.InvariantCulture);
            HeightBox.Text = _sourceHeight.ToString(CultureInfo.InvariantCulture);
            FrameRateBox.Text = _sourceFrameRate.ToString("0.###", CultureInfo.InvariantCulture);
            VideoBitrateBox.Text = _sourceVideoBitrateKbps.ToString(CultureInfo.InvariantCulture);
            AudioBitrateBox.Text = _sourceAudioBitrateKbps.ToString(CultureInfo.InvariantCulture);
            VideoFormatBox.SelectedIndex = _sourceVideoFormat == "mov" ? 1 : 0;
            AudioFormatBox.SelectedIndex = _sourceAudioFormat switch
            {
                "wav" => 1,
                "flac" => 2,
                "m4a" => 3,
                _ => 0
            };
        }
    }

    private void Confirm_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!int.TryParse(WidthBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) ||
            !int.TryParse(HeightBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var height) ||
            !double.TryParse(FrameRateBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var fps) ||
            !int.TryParse(VideoBitrateBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var videoBitrate) ||
            !int.TryParse(AudioBitrateBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var audioBitrate) ||
            width is < 16 or > 16384 || height is < 16 or > 16384 || fps <= 0 || !double.IsFinite(fps) ||
            fps > 240 || videoBitrate is < 128 or > 200000 || audioBitrate is < 32 or > 1536)
        {
            StatusText.Text = UiLocalization.Text("请输入有效参数：分辨率 16-16384，帧率 0-240，视频码率 128-200000 kbps，音频码率 32-1536 kbps。");
            return;
        }

        Close(new EditorExportOptions(
            UseSourceSettingsBox.IsChecked == true,
            _isAudio ? GetAudioFormat() : GetVideoFormat(),
            width,
            height,
            fps,
            videoBitrate,
            GetAudioFormat(),
            audioBitrate));
    }

    private string GetVideoFormat() => VideoFormatBox.SelectedIndex == 1 ? "mov" : "mp4";

    private string GetAudioFormat() => AudioFormatBox.SelectedIndex switch
    {
        1 => "wav",
        2 => "flac",
        3 => "m4a",
        _ => "mp3"
    };

    private void Cancel_OnClick(object? sender, RoutedEventArgs e) => Close();

    private static int FindRatioIndex(int width, int height)
    {
        var ratio = width / (double)height;
        if (Math.Abs(ratio - 16d / 9d) < 0.03) return 0;
        if (Math.Abs(ratio - 9d / 16d) < 0.03) return 1;
        if (Math.Abs(ratio - 4d / 3d) < 0.03) return 2;
        if (Math.Abs(ratio - 1) < 0.03) return 3;
        return 4;
    }

    private static string NormalizeVideoFormat(string? format) =>
        string.Equals(format?.Trim().TrimStart('.'), "mov", StringComparison.OrdinalIgnoreCase) ? "mov" : "mp4";

    private static string NormalizeAudioFormat(string? format) => format?.Trim().TrimStart('.').ToLowerInvariant() switch
    {
        "wav" => "wav",
        "flac" => "flac",
        "m4a" or "aac" => "m4a",
        _ => "mp3"
    };
}
