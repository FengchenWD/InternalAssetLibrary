using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using InternalAssetLibrary.Client.Core.Editor;
using InternalAssetLibrary.Client.Core.LocalAssets;
using InternalAssetLibrary.Client.Core.MediaAnalysis;
using InternalAssetLibrary.Client.Core.Playback;
using InternalAssetLibrary.Client.Core.Settings;
using InternalAssetLibrary.Client.Services;

namespace InternalAssetLibrary.Client.Controls;

public sealed partial class EditorWorkspaceView : UserControl
{
    private readonly FfprobeMediaAnalyzer _mediaAnalyzer = new(AppPaths.FfprobePath);
    private readonly FfmpegAudioPeakAnalyzer? _peakAnalyzer = CreatePeakAnalyzer();
    private readonly PlaybackController _playback;
    private readonly DispatcherTimer _timer;
    private CancellationTokenSource? _peakCancellation;
    private CancellationTokenSource? _timelinePreviewCancellation;
    private EditorAudioPeakEnvelope? _peakEnvelope;
    private Bitmap? _timelinePreviewBitmap;
    private string? _sourcePath;
    private LocalMediaType _sourceMediaType;
    private TimeSpan _duration;
    private TimeSpan _inPoint;
    private TimeSpan _outPoint;
    private double _volumeDb;
    private bool _suppressSliderChanges;
    private bool _suppressVolumeInputChanges;
    private bool _timelineDragging;
    private enum TrimHandle { None, In, Out, Range }
    private TrimHandle _trimHandle;
    private double _trimDragOffset;
    private int _sourceWidth = 1920;
    private int _sourceHeight = 1080;
    private double _sourceFrameRate = 30;
    private int _sourceVideoBitrateKbps = 8000;
    private int _sourceAudioBitrateKbps = 192;
    private string _sourceExtension = "mp4";
    private bool _disposed;
    private bool _hasUnexportedChanges;
    private Func<LocalAssetCatalog>? _catalogProvider;

    public EditorWorkspaceView()
    {
        InitializeComponent();
        DataContext = this;

        var playbackResult = LibMpvRuntime.Create(new LibMpvEngineOptions(AppPaths.LibMpvPath));
        _playback = new PlaybackController(playbackResult.Engine);
        _playback.StateChanged += Playback_OnStateChanged;
        VideoHost.HostReady += VideoHost_OnHostReady;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += Timer_OnTick;
        _timer.Start();
        SizeChanged += (_, _) => UpdateTimelineVisuals();
        UpdateTimelineVisuals();
    }

    public void Configure(
        InternalAssetLibrary.Client.Core.Markers.LocalMarkerService markerService,
        Func<LocalAssetCatalog> catalogProvider)
    {
        ArgumentNullException.ThrowIfNull(markerService);
        ArgumentNullException.ThrowIfNull(catalogProvider);
        _catalogProvider = catalogProvider;
    }

    public async Task ImportCatalogAssetsAsync(IEnumerable<LocalAsset> assets)
    {
        ArgumentNullException.ThrowIfNull(assets);
        var path = assets
            .Where(asset => asset.IsAvailable && File.Exists(asset.FullPath))
            .Select(asset => asset.FullPath)
            .FirstOrDefault(path => MediaExtensionClassifier.TryClassify(path, out var type) &&
                                    type is LocalMediaType.Video or LocalMediaType.Audio);
        if (path is not null)
        {
            await OpenSourceAsync(path);
        }
    }

    public void PauseWhenHidden()
    {
        if (_playback.State.Status == PlaybackStatus.Playing)
        {
            _ = _playback.PauseAsync();
        }
    }

    public async Task ShutdownAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _peakCancellation?.Cancel();
        _timelinePreviewCancellation?.Cancel();
        try
        {
            await _playback.DisposeAsync();
        }
        catch (Exception exception)
        {
            ClientDiagnostics.WriteException("editor-playback-shutdown", exception, isFatal: false);
        }
        finally
        {
            _peakCancellation?.Dispose();
            _timelinePreviewCancellation?.Dispose();
            _timelinePreviewBitmap?.Dispose();
        }
    }

    private static FfmpegAudioPeakAnalyzer? CreatePeakAnalyzer()
    {
        try
        {
            return new FfmpegAudioPeakAnalyzer(AppPaths.FfmpegPath);
        }
        catch (Exception exception)
        {
            ClientDiagnostics.WriteException("editor-meter-unavailable", exception, isFatal: false);
            return null;
        }
    }

    private async void VideoHost_OnHostReady(object? sender, nint handle)
    {
        await _playback.SetVideoHostAsync(handle);
    }

    private async void OpenVideo_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var sourcePicker = new EditorSourcePickerWindow(_catalogProvider?.Invoke().Assets ?? []);
        var selection = await sourcePicker.ShowDialog<EditorSourceSelection?>(owner);
        var path = selection?.Path;
        if (path is not null)
        {
            await OpenSourceAsync(path);
        }
    }

    private async Task OpenSourceAsync(string path)
    {
        if (_disposed)
        {
            return;
        }

        path = Path.GetFullPath(path);
        if (!MediaExtensionClassifier.TryClassify(path, out var mediaType) ||
            mediaType is not (LocalMediaType.Video or LocalMediaType.Audio))
        {
            SetStatus("只支持导入视频或音频文件。");
            return;
        }

        if (_hasUnexportedChanges && _sourcePath is not null &&
            !string.Equals(_sourcePath, path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            if (TopLevel.GetTopLevel(this) is not Window owner)
            {
                return;
            }

            var confirmed = await new MessageDialogWindow(
                "关闭当前视频",
                "当前视频有尚未导出的修改，是否关闭现有文件并打开新文件？",
                "关闭并打开",
                "取消").ShowDialog<bool?>(owner);
            if (confirmed != true)
            {
                return;
            }
        }

        try
        {
            SetStatus("正在读取视频信息…");
            var probe = await _mediaAnalyzer.ProbeAsync(path);
            if (!probe.Succeeded || probe.Information?.Duration is not { } duration || duration <= TimeSpan.Zero ||
                (mediaType == LocalMediaType.Video && !probe.Information.HasVideo) ||
                (mediaType == LocalMediaType.Audio && probe.Information.AudioTracks.Count == 0))
            {
            SetStatus(probe.Diagnostic is null
                ? UiLocalization.Text("无法读取视频信息。")
                : probe.Diagnostic);
                return;
            }

            await _playback.StopAsync();
            var item = new PlaybackItem("temporary-editor-source", path, Path.GetFileName(path), mediaType);
            _playback.ReplaceQueue([item], item.Key);
            var openResult = await _playback.OpenAsync(item);
            if (!openResult.Succeeded)
            {
                SetStatus(openResult.Diagnostic is null
                    ? UiLocalization.Text("播放器无法打开视频。")
                    : openResult.Diagnostic);
                return;
            }

            await _playback.PauseAsync();
            await _playback.SeekAsync(TimeSpan.Zero);

            _sourcePath = path;
            _sourceMediaType = mediaType;
            _duration = duration;
            _inPoint = TimeSpan.Zero;
            _outPoint = duration;
            _volumeDb = 0;
            _hasUnexportedChanges = false;
            _peakEnvelope = null;
            _sourceWidth = probe.Information.Streams.FirstOrDefault(stream => stream.Kind == MediaStreamKind.Video)?.Width ?? 1920;
            _sourceHeight = probe.Information.Streams.FirstOrDefault(stream => stream.Kind == MediaStreamKind.Video)?.Height ?? 1080;
            _sourceFrameRate = probe.Information.Streams.FirstOrDefault(stream => stream.Kind == MediaStreamKind.Video)?.FrameRate ?? 30;
            var sourceVideoStream = probe.Information.Streams.FirstOrDefault(stream => stream.Kind == MediaStreamKind.Video);
            var sourceAudioStream = probe.Information.Streams.FirstOrDefault(stream => stream.Kind == MediaStreamKind.Audio);
            _sourceVideoBitrateKbps = NormalizeBitrateKbps(sourceVideoStream?.BitRate, 8000, 128, 200000);
            _sourceAudioBitrateKbps = NormalizeBitrateKbps(sourceAudioStream?.BitRate, 192, 32, 1536);
            _sourceExtension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            _suppressSliderChanges = true;
            PreviewSlider.Maximum = duration.TotalSeconds;
            PreviewSlider.Value = 0;
            _suppressVolumeInputChanges = true;
            VolumeInput.Text = "0";
            VolumeSlider.Value = 0;
            _suppressVolumeInputChanges = false;
            _suppressSliderChanges = false;

            SourceNameText.Text = Path.GetFileName(path);
            VideoHost.IsVisible = mediaType == LocalMediaType.Video;
            EmptyPreviewText.IsVisible = mediaType == LocalMediaType.Audio;
            EmptyPreviewText.Text = mediaType == LocalMediaType.Audio
                ? "音频正在播放；下方时间轴显示波形"
                : "打开一个本地视频开始处理";
            MediaInfoText.Text = UiLocalization.Format(
                "格式 {0}  时长 {1}  音频流 {2} 条",
                probe.Information.FormatName ?? UiLocalization.Text("视频文件"),
                FormatTime(duration),
                probe.Information.AudioTracks.Count);
            UpdateTrimLabels();
            TimeText.Text = $"{FormatTime(TimeSpan.Zero)} / {FormatTime(_duration)}";
            TimelineTimeText.Text = FormatTime(TimeSpan.Zero);
            SetStatus(mediaType == LocalMediaType.Audio
                ? "已打开临时音频。不会创建或保存工程文件。"
                : "已打开临时视频。不会创建或保存工程文件。");
            await _playback.SetVolumeAsync(DbToPlaybackVolume(_volumeDb));
            QueuePeakAnalysis(probe.Information.AudioTracks.FirstOrDefault()?.StreamIndex ?? -1);
            QueueTimelinePreview(mediaType, duration);
            UpdateTimelineVisuals();
        }
        catch (Exception exception)
        {
            ClientDiagnostics.WriteException("editor-open-video", exception, isFatal: false);
            SetStatus(UiLocalization.Format("打开视频失败：{0}", exception.Message));
        }
    }

    private async void PlayPause_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_sourcePath is null)
        {
            return;
        }

        if (_playback.State.Position >= _duration - TimeSpan.FromMilliseconds(100))
        {
            await _playback.SeekAsync(_inPoint);
        }

        await _playback.PlayPauseAsync();
    }

    private async void Stop_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        await _playback.PauseAsync();
        await _playback.SeekAsync(_inPoint);
    }

    private async void PreviewSlider_OnChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs eventArgs)
    {
        if (_suppressSliderChanges || _sourcePath is null)
        {
            return;
        }

        await _playback.SeekAsync(TimeSpan.FromSeconds(Math.Clamp(eventArgs.NewValue, 0, _duration.TotalSeconds)));
        UpdateTimelineVisuals();
    }

    private void SetInPoint_OnClick(object? sender, RoutedEventArgs e) => SetTrimPoint(isIn: true);
    private void SetOutPoint_OnClick(object? sender, RoutedEventArgs e) => SetTrimPoint(isIn: false);

    private void SetTrimPoint(bool isIn)
    {
        var position = TimeSpan.FromSeconds(Math.Clamp(_playback.State.Position.TotalSeconds, 0, _duration.TotalSeconds));
        if (isIn) _inPoint = position < _outPoint ? position : TimeSpan.Zero;
        else _outPoint = position > _inPoint ? position : _duration;
        _hasUnexportedChanges = true;
        UpdateTrimLabels();
        UpdateTimelineVisuals();
    }

    private async void VolumeInput_OnChanged(object? sender, TextChangedEventArgs eventArgs)
    {
        if (_suppressVolumeInputChanges || _sourcePath is null)
        {
            return;
        }

        if (!TryParseVolume(VolumeInput.Text, out var value))
        {
            return;
        }

        _volumeDb = value;
        _hasUnexportedChanges = true;
        _suppressVolumeInputChanges = true;
        VolumeSlider.Value = _volumeDb;
        _suppressVolumeInputChanges = false;
        await _playback.SetVolumeAsync(DbToPlaybackVolume(_volumeDb));
    }

    private async void VolumeSlider_OnChanged(object? sender, RangeBaseValueChangedEventArgs eventArgs)
    {
        if (_suppressVolumeInputChanges || _sourcePath is null)
        {
            return;
        }

        _volumeDb = Math.Clamp(eventArgs.NewValue, -60, 12);
        _hasUnexportedChanges = true;
        _suppressVolumeInputChanges = true;
        VolumeInput.Text = _volumeDb.ToString("0.#", CultureInfo.CurrentCulture);
        _suppressVolumeInputChanges = false;
        await _playback.SetVolumeAsync(DbToPlaybackVolume(_volumeDb));
    }

    private async void VolumeInput_OnLostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (!TryParseVolume(VolumeInput.Text, out var value))
        {
            value = _volumeDb;
        }

        _volumeDb = Math.Clamp(value, -60, 12);
        _suppressVolumeInputChanges = true;
        VolumeInput.Text = _volumeDb.ToString("0.#", CultureInfo.CurrentCulture);
        VolumeSlider.Value = _volumeDb;
        _suppressVolumeInputChanges = false;
        if (_sourcePath is not null)
        {
            await _playback.SetVolumeAsync(DbToPlaybackVolume(_volumeDb));
        }
    }

    private static bool TryParseVolume(string? text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) &&
        double.IsFinite(value) && value is >= -60 and <= 12;

    private async void ExportVideo_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        try
        {
            if (_sourcePath is null || _duration <= TimeSpan.Zero ||
                TopLevel.GetTopLevel(this) is not Window owner || owner.StorageProvider is not { } storage)
            {
                SetStatus("请先打开一个视频。");
                return;
            }

            var sourceVideo = _sourceWidth > 0 && _sourceHeight > 0 ? (_sourceWidth, _sourceHeight, _sourceFrameRate) : (1920, 1080, 30d);
            var settings = await new EditorExportOptionsWindow(
                _sourceMediaType == LocalMediaType.Audio,
                sourceVideo.Item1,
                sourceVideo.Item2,
                sourceVideo.Item3,
                _sourceVideoBitrateKbps,
                _sourceExtension,
                _sourceExtension,
                _sourceAudioBitrateKbps).ShowDialog<EditorExportOptions?>(owner);
            if (settings is null) return;

            if (_sourceMediaType == LocalMediaType.Audio)
            {
                await ExportAudioAsync(owner, storage, settings);
                return;
            }

            var extension = "." + settings.Format;
            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "导出处理后的视频",
                SuggestedFileName = Path.GetFileNameWithoutExtension(_sourcePath) + "_processed" + extension,
                DefaultExtension = extension.TrimStart('.'),
                FileTypeChoices = [new FilePickerFileType("视频文件") { Patterns = [$"*{extension}"] }]
            });
            var outputPath = file?.TryGetLocalPath();
            if (outputPath is null)
            {
                return;
            }

            await _playback.PauseAsync();
            SetStatus("正在导出视频…");
            var command = EditorTemporaryVideoService.BuildExportCommand(
                _sourcePath,
                outputPath,
                _inPoint,
                _outPoint,
                _volumeDb,
                settings.Format,
                settings.Width,
                settings.Height,
                settings.FrameRate,
                settings.VideoBitrateKbps,
                settings.AudioBitrateKbps,
                AppPaths.FfmpegPath);
            var result = await new ProcessMediaToolRunner().RunAsync(command);
            SetStatus(result.Succeeded
                ? UiLocalization.Text("导出完成。")
                : UiLocalization.Format("导出失败：{0}", result.StandardError.Trim()));
            if (result.Succeeded)
            {
                _hasUnexportedChanges = false;
            }
        }
        catch (Exception exception)
        {
            ClientDiagnostics.WriteException("editor-export-video", exception, isFatal: false);
            SetStatus(UiLocalization.Format("导出失败：{0}", exception.Message));
        }
    }

    private async Task ExportAudioAsync(Window owner, IStorageProvider storage, EditorExportOptions settings)
    {
        var audioFormat = settings.AudioFormat;
        var extension = "." + audioFormat;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出处理后的音频",
            SuggestedFileName = Path.GetFileNameWithoutExtension(_sourcePath) + "_processed" + extension,
            DefaultExtension = audioFormat,
            FileTypeChoices =
            [
                new FilePickerFileType("音频文件") { Patterns = [$"*{extension}"] }
            ]
        });
        var outputPath = file?.TryGetLocalPath();
        if (outputPath is null || _sourcePath is null)
        {
            return;
        }

        try
        {
            await _playback.PauseAsync();
            SetStatus("正在导出音频…");
            var preset = audioFormat switch
            {
                "flac" => AudioExportPreset.Flac,
                "wav" => AudioExportPreset.Wav,
                "m4a" => AudioExportPreset.Aac,
                _ => AudioExportPreset.Mp3
            };
            var command = EditorTemporaryVideoService.BuildAudioExportCommand(
                _sourcePath,
                outputPath,
                _inPoint,
                _outPoint,
                _volumeDb,
                preset,
                settings.AudioBitrateKbps,
                AppPaths.FfmpegPath);
            var result = await new ProcessMediaToolRunner().RunAsync(command);
            SetStatus(result.Succeeded
                ? UiLocalization.Text("导出完成。")
                : UiLocalization.Format("导出失败：{0}", result.StandardError.Trim()));
            if (result.Succeeded)
            {
                _hasUnexportedChanges = false;
            }
        }
        catch (Exception exception)
        {
            ClientDiagnostics.WriteException("editor-export-audio", exception, isFatal: false);
            SetStatus(UiLocalization.Format("导出失败：{0}", exception.Message));
        }
    }

    private void TrimRange_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_duration <= TimeSpan.Zero || TrimRangeCanvas.Bounds.Width <= 0) return;
        var x = e.GetPosition(TrimRangeCanvas).X;
        var inX = _inPoint.TotalSeconds / _duration.TotalSeconds * TrimRangeCanvas.Bounds.Width;
        var outX = _outPoint.TotalSeconds / _duration.TotalSeconds * TrimRangeCanvas.Bounds.Width;
        _trimHandle = Math.Abs(x - inX) <= 12 ? TrimHandle.In : Math.Abs(x - outX) <= 12 ? TrimHandle.Out : x > inX && x < outX ? TrimHandle.Range : TrimHandle.None;
        if (_trimHandle == TrimHandle.None) return;
        _trimDragOffset = x - (_trimHandle == TrimHandle.Out ? outX : inX);
        e.Pointer.Capture(TrimRangeCanvas);
    }

    private void TrimRange_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_trimHandle == TrimHandle.None || _duration <= TimeSpan.Zero) return;
        var width = TrimRangeCanvas.Bounds.Width;
        var x = Math.Clamp(e.GetPosition(TrimRangeCanvas).X - _trimDragOffset, 0, width);
        var seconds = x / width * _duration.TotalSeconds;
        if (_trimHandle == TrimHandle.In) _inPoint = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, _outPoint.TotalSeconds - 0.01));
        else if (_trimHandle == TrimHandle.Out) _outPoint = TimeSpan.FromSeconds(Math.Clamp(seconds, _inPoint.TotalSeconds + 0.01, _duration.TotalSeconds));
        else
        {
            var span = _outPoint - _inPoint;
            var start = Math.Clamp(seconds, 0, _duration.TotalSeconds - span.TotalSeconds);
            _inPoint = TimeSpan.FromSeconds(start); _outPoint = _inPoint + span;
        }
        UpdateTrimLabels(); UpdateTimelineVisuals();
        _hasUnexportedChanges = true;
    }

    private void TrimRange_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _trimHandle = TrimHandle.None;
        e.Pointer.Capture(null);
    }

    private void Timeline_OnPointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (_sourcePath is null || _duration <= TimeSpan.Zero || TimelineCanvas.Bounds.Width <= 0)
        {
            return;
        }

        var x = eventArgs.GetPosition(TimelineCanvas).X;
        var width = TimelineCanvas.Bounds.Width;
        var inX = _inPoint.TotalSeconds / _duration.TotalSeconds * width;
        var outX = _outPoint.TotalSeconds / _duration.TotalSeconds * width;
        _trimHandle = Math.Abs(x - inX) <= 16
            ? TrimHandle.In
            : Math.Abs(x - outX) <= 16
                ? TrimHandle.Out
                : TrimHandle.None;
        if (_trimHandle != TrimHandle.None)
        {
            _trimDragOffset = x - (_trimHandle == TrimHandle.Out ? outX : inX);
            eventArgs.Pointer.Capture(TimelineCanvas);
            return;
        }

        _timelineDragging = true;
        eventArgs.Pointer.Capture(TimelineCanvas);
        SeekTimeline(x);
    }

    private void Timeline_OnPointerMoved(object? sender, PointerEventArgs eventArgs)
    {
        if (_trimHandle != TrimHandle.None)
        {
            UpdateTrimFromPointer(
                eventArgs.GetPosition(TimelineCanvas).X - _trimDragOffset,
                TimelineCanvas.Bounds.Width,
                _trimHandle);
        }
        else if (_timelineDragging)
        {
            SeekTimeline(eventArgs.GetPosition(TimelineCanvas).X);
        }
    }

    private void Timeline_OnPointerReleased(object? sender, PointerReleasedEventArgs eventArgs)
    {
        _timelineDragging = false;
        _trimHandle = TrimHandle.None;
        eventArgs.Pointer.Capture(null);
    }

    private void UpdateTrimFromPointer(double x, double width, TrimHandle handle)
    {
        if (_duration <= TimeSpan.Zero || width <= 0 || handle == TrimHandle.None)
        {
            return;
        }

        x = Math.Clamp(x, 0, width);
        var seconds = x / width * _duration.TotalSeconds;
        if (handle == TrimHandle.In)
        {
            _inPoint = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, _outPoint.TotalSeconds - 0.01));
        }
        else if (handle == TrimHandle.Out)
        {
            _outPoint = TimeSpan.FromSeconds(Math.Clamp(seconds, _inPoint.TotalSeconds + 0.01, _duration.TotalSeconds));
        }
        else
        {
            var span = _outPoint - _inPoint;
            var start = Math.Clamp(seconds, 0, _duration.TotalSeconds - span.TotalSeconds);
            _inPoint = TimeSpan.FromSeconds(start);
            _outPoint = _inPoint + span;
        }

        _hasUnexportedChanges = true;
        UpdateTrimLabels();
        UpdateTimelineVisuals();
    }

    private void SeekTimeline(double x)
    {
        if (_duration <= TimeSpan.Zero || TimelineCanvas.Bounds.Width <= 0)
        {
            return;
        }

        var position = TimeSpan.FromSeconds(Math.Clamp(x / TimelineCanvas.Bounds.Width, 0, 1) * _duration.TotalSeconds);
        _ = _playback.SeekAsync(position);
        _suppressSliderChanges = true;
        PreviewSlider.Value = position.TotalSeconds;
        _suppressSliderChanges = false;
        UpdateTimelineVisuals();
    }

    private void Workspace_OnDragOver(object? sender, DragEventArgs eventArgs)
    {
        var files = eventArgs.DataTransfer.TryGetFiles()?.ToArray() ?? [];
        eventArgs.DragEffects = files.Any(file => MediaExtensionClassifier.TryClassify(file.Name, out var type) &&
                                                   type is LocalMediaType.Video or LocalMediaType.Audio)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private async void Workspace_OnDrop(object? sender, DragEventArgs eventArgs)
    {
        var path = eventArgs.DataTransfer.TryGetFiles()?.Select(file => file.TryGetLocalPath()).FirstOrDefault(path => path is not null);
        if (path is not null)
        {
            await OpenSourceAsync(path);
        }
    }

    private async void Workspace_OnKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Source is TextBox ||
            eventArgs.Source is Control control && control.FindAncestorOfType<TextBox>() is not null)
        {
            return;
        }

        if (eventArgs.Key == Key.Space)
        {
            eventArgs.Handled = true;
            await PlayPause_OnKeyAsync();
        }
    }

    private async Task PlayPause_OnKeyAsync()
    {
        if (_sourcePath is not null)
        {
            await _playback.PlayPauseAsync();
        }
    }

    private void Playback_OnStateChanged(object? sender, PlaybackEngineStateChangedEventArgs eventArgs)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed)
            {
                return;
            }

            var state = eventArgs.State;
            if (!_timelineDragging)
            {
                _suppressSliderChanges = true;
                PreviewSlider.Value = state.Position.TotalSeconds;
                _suppressSliderChanges = false;
            }

            TimeText.Text = $"{FormatTime(state.Position)} / {FormatTime(_duration)}";
            TimelineTimeText.Text = FormatTime(state.Position);
            UpdateTimelineVisuals();
        });
    }

    private void Timer_OnTick(object? sender, EventArgs eventArgs)
    {
        if (_sourcePath is null)
        {
            return;
        }

        var state = _playback.State;
        if (!_timelineDragging)
        {
            _suppressSliderChanges = true;
            PreviewSlider.Value = Math.Clamp(state.Position.TotalSeconds, 0, _duration.TotalSeconds);
            _suppressSliderChanges = false;
        }

        TimeText.Text = $"{FormatTime(state.Position)} / {FormatTime(_duration)}";
        TimelineTimeText.Text = FormatTime(state.Position);
        if (_peakEnvelope is { } envelope)
        {
            var peak = envelope.Read(state.Position.TotalSeconds, _volumeDb);
            LevelMeter.LeftDb = peak.LeftDb;
            LevelMeter.RightDb = peak.RightDb;
        }
        UpdateTimelineVisuals();
    }

    private void QueuePeakAnalysis(int streamIndex)
    {
        _peakCancellation?.Cancel();
        _peakCancellation?.Dispose();
        _peakCancellation = new CancellationTokenSource();
        _peakEnvelope = null;
        if (_peakAnalyzer is null || _sourcePath is null || streamIndex < 0)
        {
            return;
        }

        var source = _sourcePath;
        var cancellationToken = _peakCancellation.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                var envelope = await _peakAnalyzer.AnalyzeAsync(source, streamIndex, cancellationToken);
                await Dispatcher.UIThread.InvokeAsync(() => _peakEnvelope = envelope);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                ClientDiagnostics.WriteException("editor-meter-analysis", exception, isFatal: false);
            }
        }, cancellationToken);
    }

    private void QueueTimelinePreview(LocalMediaType mediaType, TimeSpan duration)
    {
        _timelinePreviewCancellation?.Cancel();
        _timelinePreviewCancellation?.Dispose();
        _timelinePreviewCancellation = new CancellationTokenSource();
        _timelinePreviewBitmap?.Dispose();
        _timelinePreviewBitmap = null;
        TimelineFilmstrip.Source = null;
        TimelineFilmstrip.IsVisible = false;
        if (_sourcePath is null || duration <= TimeSpan.Zero)
        {
            return;
        }

        var source = _sourcePath;
        var outputPath = Path.Combine(
            AppPaths.EditorCacheDirectory,
            $"timeline-{Guid.NewGuid():N}.webp");
        var cancellationToken = _timelinePreviewCancellation.Token;
        _ = GenerateTimelinePreviewAsync(source, outputPath, mediaType, duration, cancellationToken);
    }

    private async Task GenerateTimelinePreviewAsync(
        string source,
        string outputPath,
        LocalMediaType mediaType,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.EditorCacheDirectory);
            var command = mediaType == LocalMediaType.Audio
                ? FfmpegCommandBuilder.BuildWaveform(
                    source,
                    outputPath,
                    width: 2048,
                    height: 256,
                    ffmpegExecutable: AppPaths.FfmpegPath)
                : FfmpegCommandBuilder.BuildFilmstrip(
                    source,
                    outputPath,
                    duration,
                    frameCount: 12,
                    frameWidth: 240,
                    frameHeight: 135,
                    ffmpegExecutable: AppPaths.FfmpegPath);
            var result = await new ProcessMediaToolRunner().RunAsync(command, cancellationToken);
            if (!result.Succeeded || !File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            {
                if (!cancellationToken.IsCancellationRequested &&
                    string.Equals(_sourcePath, source, OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal))
                {
                    await Dispatcher.UIThread.InvokeAsync(() => SetStatus(
                        UiLocalization.Format("时间线预览生成失败：{0}",
                            string.IsNullOrWhiteSpace(result.StandardError)
                            ? UiLocalization.Format("FFmpeg 退出代码 {0}", result.ExitCode)
                                : result.StandardError.Trim())));
                }
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_disposed || cancellationToken.IsCancellationRequested ||
                    !string.Equals(_sourcePath, source, OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal))
                {
                    return;
                }

                var bitmap = new Bitmap(outputPath);
                _timelinePreviewBitmap?.Dispose();
                _timelinePreviewBitmap = bitmap;
                TimelineFilmstrip.Source = bitmap;
                TimelineFilmstrip.IsVisible = true;
                UpdateTimelineVisuals();
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ClientDiagnostics.WriteException("editor-timeline-preview", exception, isFatal: false);
        }
        finally
        {
            try
            {
                File.Delete(outputPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private void UpdateTimelineVisuals()
    {
        var width = TimelineCanvas.Bounds.Width;
        if (width <= 0 || _duration <= TimeSpan.Zero)
        {
            TimelineClipBar.Width = 0;
            TimelinePlayhead.IsVisible = false;
            TimelineInHandle.IsVisible = false;
            TimelineOutHandle.IsVisible = false;
            return;
        }

        TimelinePlayhead.IsVisible = true;
        TimelineInHandle.IsVisible = true;
        TimelineOutHandle.IsVisible = true;
        TimelineFilmstrip.Width = width;
        var left = width * _inPoint.TotalSeconds / _duration.TotalSeconds;
        var right = width * _outPoint.TotalSeconds / _duration.TotalSeconds;
        TimelineClipBar.Width = Math.Max(4, right - left);
        Canvas.SetLeft(TimelineClipBar, left);
        Canvas.SetLeft(TimelineInHandle, Math.Clamp(left - 5, 0, Math.Max(0, width - 10)));
        Canvas.SetLeft(TimelineOutHandle, Math.Clamp(right - 5, 0, Math.Max(0, width - 10)));
        Canvas.SetLeft(
            TimelinePlayhead,
            Math.Clamp(
                width * Math.Clamp(_playback.State.Position.TotalSeconds, 0, _duration.TotalSeconds) /
                _duration.TotalSeconds,
                0,
                Math.Max(0, width - 2)));
        var trimWidth = TrimRangeCanvas.Bounds.Width;
        if (trimWidth > 0)
        {
            var sideLeft = trimWidth * _inPoint.TotalSeconds / _duration.TotalSeconds;
            var sideRight = trimWidth * _outPoint.TotalSeconds / _duration.TotalSeconds;
            Canvas.SetLeft(TrimRangeSelection, sideLeft); TrimRangeSelection.Width = Math.Max(4, sideRight - sideLeft);
            Canvas.SetLeft(TrimInHandle, sideLeft - 4); Canvas.SetLeft(TrimOutHandle, sideRight - 4);
        }
    }

    private void UpdateTrimLabels()
    {
        TrimSummaryText.Text = $"{FormatTime(_inPoint)} - {FormatTime(_outPoint)}";
    }

    private void SetStatus(string text) => StatusText.Text = text;

    private static double DbToPlaybackVolume(double db) => Math.Clamp(100 * Math.Pow(10, db / 20), 0, 200);

    private static int NormalizeBitrateKbps(long? bitrateBitsPerSecond, int fallback, int minimum, int maximum)
    {
        var value = bitrateBitsPerSecond is > 0
            ? (int)Math.Clamp((bitrateBitsPerSecond.Value + 500) / 1000, minimum, maximum)
            : fallback;
        return Math.Clamp(value, minimum, maximum);
    }

    private static string FormatTime(TimeSpan value) =>
        $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}.{value.Milliseconds:000}";
}
