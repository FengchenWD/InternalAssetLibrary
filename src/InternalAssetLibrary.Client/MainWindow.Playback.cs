using System.Collections.ObjectModel;
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using InternalAssetLibrary.Client.Core.Http;
using InternalAssetLibrary.Client.Core.LocalAssets;
using InternalAssetLibrary.Client.Core.Lut;
using InternalAssetLibrary.Client.Core.Markers;
using InternalAssetLibrary.Client.Core.MediaAnalysis;
using InternalAssetLibrary.Client.Core.Platform;
using InternalAssetLibrary.Client.Core.Playback;
using InternalAssetLibrary.Client.Core.Settings;
using InternalAssetLibrary.Client.ViewModels;
using InternalAssetLibrary.Client.Services;
using InternalAssetLibrary.Contracts;

namespace InternalAssetLibrary.Client;

public sealed partial class MainWindow
{
    private static FilePickerFileType PlayerMediaFileType => new(UiLocalization.Text("支持的媒体"))
    {
        Patterns = DefaultPlayerFileAssociations.All
            .Select(item => $"*{item.Extension}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray()
    };

    private static FilePickerFileType SubtitleFileType => new(UiLocalization.Text("字幕"))
    {
        Patterns = ["*.srt", "*.ass", "*.ssa", "*.vtt", "*.sub"]
    };

    private readonly PlaybackVolumeCoordinator _playbackVolumeCoordinator = new();
    private readonly JsonLocalLutLibrary _localLutLibrary = new(AppPaths.LocalLutLibraryFile);
    private readonly Dictionary<string, PlaybackOrigin> _playbackOrigins = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _playerLutOptionKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _detailLutOptionKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PlayerTrimRange> _playerTrimRanges = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TemporaryMarkerSet> _temporaryPlayerMarkerSets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int[]> _detailAudioTrackSelections = new(StringComparer.Ordinal);
    private PlaybackController _detailPlayback = null!;
    private PlaybackController _playerPlayback = null!;
    private MediaThumbnailService _mediaThumbnailService = null!;
    private FfprobeMediaAnalyzer _mediaAnalyzer = null!;
    private DispatcherTimer _playbackUiTimer = null!;
    private CancellationTokenSource? _playerSeekCancellation;
    private CancellationTokenSource? _detailSeekCancellation;
    private CancellationTokenSource? _playerVisualCancellation;
    private CancellationTokenSource? _detailPreviewCancellation;
    private CancellationTokenSource? _playerLutCancellation;
    private CancellationTokenSource? _detailLutCancellation;
    private CancellationTokenSource? _playerAudioTrackCancellation;
    private CancellationTokenSource? _detailAudioTrackCancellation;
    private CancellationTokenSource? _playerMarkerCancellation;
    private CancellationTokenSource? _volumeChangeCancellation;
    private CancellationTokenSource? _playbackSettingsSaveCancellation;
    private readonly SemaphoreSlim _detailPreviewGate = new(1, 1);
    private readonly SemaphoreSlim _playerVisualGate = new(1, 1);
    private Bitmap? _playerArtworkBitmap;
    private Bitmap? _playerTimelineBitmap;
    private DetailPreviewMode _detailPreviewMode;
    private string? _detailPreviewKey;
    private string? _detailPreviewRequestedKey;
    private string? _lastPlayerTrackKey;
    private string? _lastDetailTrackKey;
    private string? _lastPlayerVisualKey;
    private string? _playerExportUiKey;
    private long _detailPreviewGeneration;
    private long _playerVisualGeneration;
    private long _playerLutGeneration;
    private long _detailLutGeneration;
    private long _playerAudioTrackGeneration;
    private long _detailAudioTrackGeneration;
    private long _playbackPreferenceGeneration;
    private bool _suppressPlayerPosition;
    private bool _suppressDetailPosition;
    private bool _suppressPlayerTrackSelection;
    private bool _suppressDetailTrackSelection;
    private bool _suppressPlayerLutSelection;
    private bool _suppressDetailLutSelection;
    private bool _suppressPlayerMarkerSetSelection;
    private bool _suppressPlayerQueueSelection;
    private bool _suppressVolumeChange;
    private bool _playbackInitializedAfterSettings;
    private bool _playbackDisposed;
    private bool _playerTimelineMarkersDirty = true;
    private double _lastPlayerTimelineDuration = -1;
    private double _lastPlayerTimelineWidth = -1;
    private double _playerImageZoom = 1;
    private Vector _playerImageTranslation;
    private Point _playerImageDragOrigin;
    private Vector _playerImageDragStartTranslation;
    private bool _isPlayerImageDragging;
    private readonly ScaleTransform _playerArtworkScaleTransform = new();
    private readonly TranslateTransform _playerArtworkTranslateTransform = new();

    public ObservableCollection<PlayerQueueItemViewModel> PlayerQueueItems { get; } = [];

    public ObservableCollection<AudioTrackOptionViewModel> PlayerAudioTracks { get; } = [];

    public ObservableCollection<AudioTrackOptionViewModel> DetailAudioTracks { get; } = [];

    public ObservableCollection<LutOptionViewModel> AvailableLuts { get; } = [];

    public ObservableCollection<PlayerMarkerSetOptionViewModel> PlayerMarkerSets { get; } = [];

    public ObservableCollection<PlayerMarkerItemViewModel> PlayerMarkers { get; } = [];

    private void InitializePlaybackUi()
    {
        var artworkTransform = new TransformGroup();
        artworkTransform.Children.Add(_playerArtworkScaleTransform);
        artworkTransform.Children.Add(_playerArtworkTranslateTransform);
        PlayerArtworkImage.RenderTransform = artworkTransform;

        var nativeLibraryPath = ResolveRuntimePath(
            "INTERNAL_ASSET_LIBRARY_LIBMPV",
            AppPaths.LibMpvPath);
        var detailEngine = LibMpvRuntime.Create(new LibMpvEngineOptions(nativeLibraryPath));
        var playerEngine = LibMpvRuntime.Create(new LibMpvEngineOptions(nativeLibraryPath));
        _detailPlayback = new PlaybackController(detailEngine.Engine, _playbackVolumeCoordinator);
        _playerPlayback = new PlaybackController(playerEngine.Engine, _playbackVolumeCoordinator);
        _detailPlayback.StateChanged += DetailPlayback_OnStateChanged;
        _playerPlayback.StateChanged += PlayerPlayback_OnStateChanged;

        var ffmpegPath = ResolveRuntimePath(
            "INTERNAL_ASSET_LIBRARY_FFMPEG",
            AppPaths.FfmpegPath);
        _mediaThumbnailService = new MediaThumbnailService(
            AppPaths.MediaDerivativeCacheDirectory,
            ffmpegPath,
            _thumbnailService);
        _mediaAnalyzer = new FfprobeMediaAnalyzer(ResolveRuntimePath(
            "INTERNAL_ASSET_LIBRARY_FFPROBE",
            AppPaths.FfprobePath));

        DetailVideoHost.HostReady += (_, handle) =>
            _ = BindVideoHostAsync(_detailPlayback, handle, DetailStatus);
        PlayerVideoHost.HostReady += (_, handle) =>
            _ = BindVideoHostAsync(_playerPlayback, handle, PlayerStatusText);

        // The visible detail host can be created while InitializeComponent is
        // still attaching the XAML tree. In that case HostReady has already
        // fired before the playback controllers subscribe above.
        if (DetailVideoHost.IsHostReady)
        {
            _ = BindVideoHostAsync(_detailPlayback, DetailVideoHost.HostHandle, DetailStatus);
        }

        if (PlayerVideoHost.IsHostReady)
        {
            _ = BindVideoHostAsync(_playerPlayback, PlayerVideoHost.HostHandle, PlayerStatusText);
        }

        _playbackUiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _playbackUiTimer.Tick += PlaybackUiTimer_OnTick;
        _playbackUiTimer.Start();

        App.FileOpenActivations.ActivationAvailable += FileOpenActivations_OnAvailable;
        UiLocalization.Register(this, static window => window.RefreshPlaybackLocalization());
    }

    private async Task InitializePlaybackAfterSettingsAsync()
    {
        if (_playbackInitializedAfterSettings)
        {
            return;
        }

        _playbackInitializedAfterSettings = true;
        _suppressVolumeChange = true;
        MasterVolumeSlider.Value = _settings.MasterVolume;
        DetailVolumeSlider.Value = _settings.MasterVolume;
        SettingsVolumeSlider.Value = _settings.MasterVolume;
        MasterVolumeText.Text = $"{_settings.MasterVolume:0}%";
        SettingsVolumeText.Text = $"{_settings.MasterVolume:0}%";
        _suppressVolumeChange = false;
        await _playerPlayback.SetVolumeAsync(_settings.MasterVolume);
        await _playerPlayback.SetSpeedAsync(_settings.PlaybackSpeed);
        _playerPlayback.RepeatMode = _settings.PlaybackLoopEnabled
            ? PlaybackRepeatMode.All
            : PlaybackRepeatMode.Off;
        _playerPlayback.OrderMode = _settings.PlaybackShuffleEnabled
            ? PlaybackOrderMode.Shuffle
            : PlaybackOrderMode.Sequential;
        UpdatePlaybackModeButtons();
        SelectPlaybackSpeed(_settings.PlaybackSpeed);
        await ReloadLutsAsync();
        UpdateMediaRuntimeStatus();
        await DrainFileOpenActivationsAsync();
    }

    private Task DisposePlaybackAsync()
    {
        if (_playbackDisposed)
        {
            return Task.CompletedTask;
        }

        _playbackDisposed = true;
        InvalidatePendingPlaybackPreferenceWrites();
        App.FileOpenActivations.ActivationAvailable -= FileOpenActivations_OnAvailable;
        _playbackUiTimer.Stop();
        _playerSeekCancellation?.Cancel();
        _detailSeekCancellation?.Cancel();
        _detailPreviewCancellation?.Cancel();
        _playerLutCancellation?.Cancel();
        _detailLutCancellation?.Cancel();
        _playerAudioTrackCancellation?.Cancel();
        _detailAudioTrackCancellation?.Cancel();
        _playerMarkerCancellation?.Cancel();
        _playerVisualCancellation?.Cancel();
        ClearPlayerVisuals();
        _localLutLibrary.Dispose();

        return Task.Run(async () =>
        {
            try
            {
                await Task.WhenAll(
                    _detailPlayback.DisposeAsync().AsTask(),
                    _playerPlayback.DisposeAsync().AsTask()).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                ClientDiagnostics.WriteException("playback-shutdown", exception, isFatal: false);
            }
        });
    }

    private static string ResolveRuntimePath(string environmentVariable, string bundledPath)
    {
        var configured = Environment.GetEnvironmentVariable(environmentVariable);
        return string.IsNullOrWhiteSpace(configured)
            ? bundledPath
            : Path.GetFullPath(configured.Trim());
    }

    private async Task BindVideoHostAsync(
        PlaybackController controller,
        nint handle,
        TextBlock status)
    {
        if (_playbackDisposed)
        {
            return;
        }

        try
        {
            var result = await controller.SetVideoHostAsync(handle);
            if (result.Failure == PlaybackOperationFailure.EngineUnavailable)
            {
                UiLocalization.SetText(status, "媒体运行库不可用");
                return;
            }

            ShowPlaybackResult(result, status);
        }
        catch (ObjectDisposedException) when (_playbackDisposed)
        {
        }
    }

    private void FileOpenActivations_OnAvailable() =>
        Dispatcher.UIThread.Post(() => _ = DrainFileOpenActivationsSafelyAsync());

    private async Task DrainFileOpenActivationsSafelyAsync()
    {
        try
        {
            await DrainFileOpenActivationsAsync();
        }
        catch (Exception exception)
        {
            ClientDiagnostics.WriteException("file-open-activation", exception, isFatal: false);
            UiLocalization.SetText(PlayerStatusText, "打开媒体失败：{0}", UserMessage(exception));
        }
    }

    private async Task DrainFileOpenActivationsAsync()
    {
        foreach (var request in App.FileOpenActivations.Drain())
        {
            await OpenExternalFilesAsync(request.FilePaths);
        }
    }

    private async void OpenPlayerFiles_OnClick(object? sender, RoutedEventArgs eventArgs) =>
        await RunGuardedInteractionAsync(
            "open-player-files",
            OpenPlayerFilesAsync,
            PlayerStatusText);

    private async Task OpenPlayerFilesAsync()
    {
        var files = await RunNativePickerAsync(
            "open-player-files",
            () => StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = UiLocalization.Text("打开媒体文件"),
                AllowMultiple = true,
                FileTypeFilter = [PlayerMediaFileType]
            }),
            PlayerStatusText);
        if (files is null)
        {
            return;
        }

        await OpenExternalFilesAsync(files
            .Select(file => file.Path.LocalPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .ToArray());
    }

    private async void AddPlayerQueueItems_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        try
        {
            var availableAssets = _catalog.Assets
                .Where(asset => asset.IsAvailable &&
                                File.Exists(asset.FullPath) &&
                                DefaultPlayerFileAssociations.SupportsPath(asset.FullPath))
                .OrderBy(asset => asset.FileName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var selection = await new PlayerQueueAddWindow(availableAssets)
                .ShowDialog<PlayerQueueAddSelection?>(this);
            if (selection is null)
            {
                return;
            }

            if (selection.UseFilePicker)
            {
                var files = await RunNativePickerAsync(
                    "add-player-queue-files",
                    () => StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                    {
                        Title = UiLocalization.Text("选择要加入播放队列的媒体文件"),
                        AllowMultiple = true,
                        FileTypeFilter = [PlayerMediaFileType]
                    }),
                    PlayerToolStatusText);
                if (files is null)
                {
                    return;
                }

                AppendPlayerQueueItems(files
                    .Select(file => file.Path.LocalPath)
                    .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    .Cast<string>()
                    .Select(CreateExternalPlaybackItem)
                    .OfType<PlaybackItem>());
                return;
            }

            var selectedIds = selection.LocalAssetIds.ToHashSet();
            AppendPlayerQueueItems(_catalog.Assets
                .Where(asset => selectedIds.Contains(asset.Id) && asset.IsAvailable && File.Exists(asset.FullPath))
                .Select(CreateLocalPlaybackItem));
        }
        catch (Exception exception)
        {
            ClientDiagnostics.WriteException("add-player-queue-items", exception, isFatal: false);
            UiLocalization.SetText(PlayerToolStatusText, "加入播放队列失败：{0}", UserMessage(exception));
        }
    }

    private void AppendPlayerQueueItems(IEnumerable<PlaybackItem> items)
    {
        var existingKeys = _playerPlayback.Queue
            .Select(item => item.Key)
            .ToHashSet(StringComparer.Ordinal);
        var added = 0;
        var skipped = 0;
        foreach (var item in items.DistinctBy(item => item.Key))
        {
            if (!existingKeys.Add(item.Key))
            {
                skipped++;
                continue;
            }

            _playerPlayback.Enqueue(item);
            added++;
        }

        SyncPlayerQueue();
        if (added == 0)
        {
            UiLocalization.SetText(
                PlayerToolStatusText,
                skipped > 0 ? "所选素材已在播放队列中。" : "没有选择可加入播放队列的媒体。" );
            return;
        }

        UiLocalization.SetText(
            PlayerToolStatusText,
            skipped == 0
                ? "已加入播放队列：{0:N0} 项。"
                : "已加入 {0:N0} 项，跳过 {1:N0} 个重复素材。",
            added,
            skipped);
    }

    private async void PlaybackShortcuts_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        try
        {
            var updated = await new PlaybackShortcutSettingsWindow(_settings.PlaybackShortcuts)
                .ShowDialog<PlaybackShortcutSettings?>(this);
            if (updated is null)
            {
                return;
            }

            _settings = _settings with { PlaybackShortcuts = updated.ValidateAndNormalize() };
            await SaveSettingsAsync();
            UiLocalization.SetText(PlayerToolStatusText, "快捷键设置已保存。");
        }
        catch (Exception exception)
        {
            UiLocalization.SetText(PlayerToolStatusText, "保存快捷键失败：{0}", UserMessage(exception));
        }
    }

    private async Task ExecutePlaybackShortcutAsync(PlaybackShortcutAction action)
    {
        switch (action)
        {
            case PlaybackShortcutAction.PlayPause:
                ShowPlaybackResult(await _playerPlayback.PlayPauseAsync(), PlayerStatusText);
                break;
            case PlaybackShortcutAction.SeekBackwardFiveSeconds:
                await SeekPlayerRelativeAsync(-5);
                break;
            case PlaybackShortcutAction.SeekForwardFiveSeconds:
                await SeekPlayerRelativeAsync(5);
                break;
            case PlaybackShortcutAction.SeekBackwardOneSecond:
                await SeekPlayerRelativeAsync(-1);
                break;
            case PlaybackShortcutAction.SeekForwardOneSecond:
                await SeekPlayerRelativeAsync(1);
                break;
            case PlaybackShortcutAction.SetInPoint:
                PlayerSetInPoint_OnClick(null, new RoutedEventArgs());
                break;
            case PlaybackShortcutAction.SetOutPoint:
                PlayerSetOutPoint_OnClick(null, new RoutedEventArgs());
                break;
            case PlaybackShortcutAction.AddMarker:
                await AddMarkerAtCurrentPositionAsync(_playerPlayback, useDetailSelection: false);
                break;
            case PlaybackShortcutAction.OpenFiles:
                OpenPlayerFiles_OnClick(null, new RoutedEventArgs());
                break;
            case PlaybackShortcutAction.OpenFolder:
                OpenPlayerFolder_OnClick(null, new RoutedEventArgs());
                break;
            case PlaybackShortcutAction.Export:
                ExportPlayerMedia_OnClick(null, new RoutedEventArgs());
                break;
            case PlaybackShortcutAction.VolumeUp:
                await SetMasterVolumeFromUiAsync(Math.Min(100, _settings.MasterVolume + 5));
                UiLocalization.SetText(PlayerToolStatusText, "总音量：{0:0}%", _settings.MasterVolume);
                break;
            case PlaybackShortcutAction.VolumeDown:
                await SetMasterVolumeFromUiAsync(Math.Max(0, _settings.MasterVolume - 5));
                UiLocalization.SetText(PlayerToolStatusText, "总音量：{0:0}%", _settings.MasterVolume);
                break;
            case PlaybackShortcutAction.ToggleFullscreen:
                PlayerFullscreen_OnClick(null, new RoutedEventArgs());
                break;
            case PlaybackShortcutAction.ExitFullscreen:
                if (WindowState == WindowState.FullScreen)
                {
                    WindowState = WindowState.Normal;
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, null);
        }
    }

    private async Task SeekPlayerRelativeAsync(double seconds)
    {
        if (_playerPlayback.CurrentItem is null)
        {
            UiLocalization.SetText(PlayerToolStatusText, "请先打开媒体文件。");
            return;
        }

        var duration = _playerPlayback.State.Duration?.TotalSeconds;
        var targetSeconds = Math.Max(0, _playerPlayback.State.Position.TotalSeconds + seconds);
        if (duration is > 0)
        {
            targetSeconds = Math.Min(duration.Value, targetSeconds);
        }

        ShowPlaybackResult(
            await _playerPlayback.SeekAsync(TimeSpan.FromSeconds(targetSeconds)),
            PlayerStatusText);
    }

    private async void OpenPlayerFolder_OnClick(object? sender, RoutedEventArgs eventArgs) =>
        await RunGuardedInteractionAsync(
            "open-player-folder",
            OpenPlayerFolderAsync,
            PlayerStatusText);

    private async Task OpenPlayerFolderAsync()
    {
        var folders = await RunNativePickerAsync(
            "open-player-folder",
            () => StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = UiLocalization.Text("打开媒体文件夹"),
                AllowMultiple = false
            }),
            PlayerStatusText);
        if (folders is null || folders.Count == 0 ||
            string.IsNullOrWhiteSpace(folders[0].Path.LocalPath))
        {
            return;
        }

        try
        {
            UiLocalization.SetText(PlayerStatusText, "正在扫描文件夹…");
            var paths = await Task.Run(() => PlaybackFolderScanner.Discover(folders[0].Path.LocalPath));
            if (paths.Count == 0)
            {
                UiLocalization.SetText(PlayerStatusText, "该文件夹及其子文件夹中没有支持的媒体。");
                return;
            }

            await OpenExternalFilesAsync(paths);
            UiLocalization.SetText(PlayerToolStatusText, "已从文件夹加入 {0:N0} 个媒体文件。", paths.Count);
        }
        catch (Exception exception)
        {
            ClientDiagnostics.WriteException("open-player-folder", exception, isFatal: false);
            UiLocalization.SetText(PlayerStatusText, "文件夹扫描失败：{0}", UserMessage(exception));
        }
    }

    private async Task OpenExternalFilesAsync(IReadOnlyList<string> filePaths)
    {
        var items = filePaths
            .Where(File.Exists)
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .Select(CreateExternalPlaybackItem)
            .Where(item => item is not null)
            .Cast<PlaybackItem>()
            .ToArray();
        if (items.Length == 0)
        {
            return;
        }

        NavigateTo("player");
        if (items.Length == 1)
        {
            await OpenPlayerItemAsync(items[0]);
            return;
        }

        var shouldStart = _playerPlayback.CurrentItem is null;
        foreach (var item in items)
        {
            if (_playerPlayback.Queue.Any(existing => existing.Key == item.Key))
            {
                continue;
            }

            _playerPlayback.Enqueue(item);
        }

        SyncPlayerQueue();
        if (shouldStart)
        {
            await ShowPlaybackResultAsync(_playerPlayback.PlayCurrentAsync(), PlayerStatusText);
            await RefreshPlayerVisualsAsync(_playerPlayback.CurrentItem);
        }
    }

    private PlaybackItem? CreateExternalPlaybackItem(string filePath)
    {
        if (!DefaultPlayerFileAssociations.SupportsPath(filePath) ||
            !MediaExtensionClassifier.TryClassify(filePath, out var mediaType))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(filePath);
        var key = "external:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(
                OperatingSystem.IsWindows() ? fullPath.ToUpperInvariant() : fullPath)));
        _playbackOrigins[key] = new PlaybackOrigin(null, fullPath, null);
        return new PlaybackItem(
            key,
            fullPath,
            Path.GetFileNameWithoutExtension(fullPath),
            mediaType,
            ForceVideoOutput: mediaType == LocalMediaType.Image && IsAnimatedImage(fullPath));
    }

    private PlaybackItem CreateLocalPlaybackItem(AssetCardViewModel asset)
    {
        var key = $"local:{asset.Id:N}";
        _playbackOrigins[key] = new PlaybackOrigin(asset.Id, asset.FullPath, null);
        return new PlaybackItem(
            key,
            asset.FullPath,
            asset.Name,
            asset.MediaType,
            ForceVideoOutput: asset.MediaType == LocalMediaType.Image && IsAnimatedImage(asset.FullPath));
    }

    private PlaybackItem CreateLocalPlaybackItem(LocalAsset asset)
    {
        var key = $"local:{asset.Id:N}";
        _playbackOrigins[key] = new PlaybackOrigin(asset.Id, asset.FullPath, null);
        return new PlaybackItem(
            key,
            asset.FullPath,
            Path.GetFileNameWithoutExtension(asset.FileName),
            asset.MediaType,
            ForceVideoOutput: asset.MediaType == LocalMediaType.Image && IsAnimatedImage(asset.FullPath));
    }

    private PlaybackItem CreateCloudPlaybackItem(ApiAsset asset, string localPath)
    {
        var key = $"cloud:{asset.Id:N}:{asset.CurrentVersionId:N}";
        _playbackOrigins[key] = new PlaybackOrigin(null, localPath, asset);
        var mediaType = asset.Category is ApiAssetCategory.Bgm or ApiAssetCategory.SoundEffect
            ? LocalMediaType.Audio
            : asset.Category == ApiAssetCategory.Video
                ? LocalMediaType.Video
                : LocalMediaType.Image;
        return new PlaybackItem(
            key,
            localPath,
            asset.Name,
            mediaType,
            ForceVideoOutput: mediaType == LocalMediaType.Image && IsAnimatedImage(localPath));
    }

    private async Task OpenPlayerItemAsync(PlaybackItem item)
    {
        await StopDetailPreviewAsync(savePosition: true);
        ResetPlayerImageTransform();
        ClearTemporaryMarkersExcept([item.Key]);
        _playerLutOptionKeys.Clear();
        _playerTrimRanges.Clear();
        _playerExportUiKey = null;
        var result = await _playerPlayback.OpenAsync(item);
        ShowPlaybackResult(result, PlayerStatusText);
        SyncPlayerQueue();
        if (result.Succeeded)
        {
            await RefreshPlayerVisualsAsync(item);
            await RefreshAudioTracksAsync(_playerPlayback, PlayerAudioTracks, isPlayer: true);
            SyncPlayerLutSelection();
            UpdatePlayerTrimUi();
            await RefreshPlayerMarkersAsync();
        }
    }

    private async void AssetCard_OnDoubleTapped(object? sender, TappedEventArgs eventArgs)
    {
        if (OriginatesFromCheckBox(eventArgs))
        {
            eventArgs.Handled = true;
            return;
        }

        if (sender is not Border { DataContext: AssetCardViewModel asset } || !asset.IsAvailable)
        {
            return;
        }

        eventArgs.Handled = true;
        try
        {
            if (IsProfessionalDesignFile(asset.FullPath))
            {
                OpenWithSystemDefault(asset.FullPath);
                return;
            }

            NavigateTo("player");
            await OpenPlayerItemAsync(CreateLocalPlaybackItem(asset));
        }
        catch (Exception exception)
        {
            ClientDiagnostics.WriteException("open-local-player-item", exception, isFatal: false);
            UiLocalization.SetText(LocalSummaryText, "打开播放器失败：{0}", UserMessage(exception));
        }
    }

    private async void CloudAssetCard_OnDoubleTapped(object? sender, TappedEventArgs eventArgs)
    {
        if (OriginatesFromCheckBox(eventArgs))
        {
            eventArgs.Handled = true;
            return;
        }

        if (sender is not Border { DataContext: CloudAssetCardViewModel asset } || !asset.Asset.HasOriginal)
        {
            return;
        }

        eventArgs.Handled = true;
        if (!await _cloudAssetOpenGate.WaitAsync(0))
        {
            UiLocalization.SetText(CloudSummaryText, "已有云端素材正在打开，请完成或取消目录选择后再试。");
            return;
        }

        var cancellation = new CancellationTokenSource();
        _cloudAssetOpenCancellation = cancellation;
        try
        {
            await StopDetailPreviewAsync(savePosition: true);
            cancellation.Token.ThrowIfCancellationRequested();
            var localPath = await EnsureCloudAssetDownloadedAsync(
                asset.Asset,
                cancellationToken: cancellation.Token);
            if (localPath is null)
            {
                return;
            }

            cancellation.Token.ThrowIfCancellationRequested();

            if (IsProfessionalDesignFile(localPath))
            {
                OpenWithSystemDefault(localPath);
                return;
            }

            NavigateTo("player");
            await OpenPlayerItemAsync(CreateCloudPlaybackItem(asset.Asset, localPath));
        }
        catch (OperationCanceledException)
        {
            UiLocalization.SetText(CloudSummaryText, "已取消打开云端素材。");
        }
        catch (Exception exception)
        {
            ClientDiagnostics.WriteException("open-cloud-player-item", exception, isFatal: false);
            UiLocalization.SetText(
                CloudSummaryText,
                "打开播放器失败：{0}",
                UserMessage(exception));
        }
        finally
        {
            if (ReferenceEquals(_cloudAssetOpenCancellation, cancellation))
            {
                _cloudAssetOpenCancellation = null;
            }

            cancellation.Dispose();
            _cloudAssetOpenGate.Release();
        }
    }

    private static bool IsProfessionalDesignFile(string path) =>
        Path.GetExtension(path).Equals(".psd", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".ai", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".eps", StringComparison.OrdinalIgnoreCase);

    private static void OpenWithSystemDefault(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
        {
            UseShellExecute = true
        });
    }

    private async Task BeginSelectedLocalPreviewAsync(AssetCardViewModel asset)
    {
        if (!CanUsePlaybackPreview(asset.MediaType, asset.FullPath) || !asset.IsAvailable)
        {
            await StopDetailPreviewAsync(savePosition: true);
            return;
        }

        var item = CreateLocalPlaybackItem(asset);
        var operation = BeginDetailPreviewOperation(DetailPreviewMode.Selected, item.Key);
        try
        {
            await OpenDetailPreviewAsync(item, operation);
        }
        finally
        {
            FinishDetailPreviewOperation(operation);
        }
    }

    private async Task BeginHoverLocalPreviewAsync(AssetCardViewModel asset)
    {
        if (!CanUsePlaybackPreview(asset.MediaType, asset.FullPath) || !asset.IsAvailable)
        {
            return;
        }

        var item = CreateLocalPlaybackItem(asset);
        BeginHoverDetailSelection(item.Key);
        SelectLocalAsset(asset);
        var operation = BeginDetailPreviewOperation(DetailPreviewMode.Hover, item.Key);
        try
        {
            await OpenDetailPreviewAsync(item, operation);
        }
        finally
        {
            FinishDetailPreviewOperation(operation);
        }
    }

    private async Task BeginSelectedCloudPreviewAsync(CloudAssetCardViewModel asset)
    {
        if (!asset.Asset.HasOriginal || asset.Asset.Category == ApiAssetCategory.Image &&
            !IsAnimatedImage(asset.Asset.Extension))
        {
            await StopDetailPreviewAsync(savePosition: true);
            return;
        }

        var requestedKey = $"cloud:{asset.Id:N}:{asset.Asset.CurrentVersionId:N}";
        var operation = BeginDetailPreviewOperation(DetailPreviewMode.Selected, requestedKey);
        try
        {
            UiLocalization.SetText(DetailStatus, "正在准备预览…");
            var localPath = await TryGetCloudPreviewPathAsync(
                asset.Asset,
                allowOriginalDownload: false,
                cancellationToken: operation.CancellationToken);
            if (localPath is null)
            {
                if (IsCurrentDetailPreviewOperation(operation) && _selectedCloudAsset?.Id == asset.Id)
                {
                    UiLocalization.SetText(DetailStatus, "云端预览尚未缓存；可双击下载后播放。");
                }

                return;
            }

            if (!IsCurrentDetailPreviewOperation(operation) ||
                _selectedCloudAsset?.Id != asset.Id)
            {
                return;
            }

            if (asset.Asset.Category == ApiAssetCategory.Image && !IsAnimatedImage(localPath))
            {
                await StopDetailPreviewAsync(savePosition: true);
                return;
            }

            await OpenDetailPreviewAsync(CreateCloudPlaybackItem(asset.Asset, localPath), operation);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (IsCurrentDetailPreviewOperation(operation) &&
                _selectedCloudAsset?.Id == asset.Id)
            {
                UiLocalization.SetText(
                    DetailStatus,
                    "预览失败：{0}",
                UserMessage(exception));
            }
        }
        finally
        {
            FinishDetailPreviewOperation(operation);
        }
    }

    private async Task OpenDetailPreviewAsync(
        PlaybackItem item,
        DetailPreviewOperation operation)
    {
        try
        {
            await _detailPreviewGate.WaitAsync(operation.CancellationToken);
            try
            {
                if (!IsCurrentDetailPreviewOperation(operation))
                {
                    return;
                }

                if (_detailPreviewKey is not null && _detailPreviewKey != item.Key)
                {
                    SaveDetailPreviewPosition();
                }

                _detailPreviewKey = item.Key;
                var startPosition = _previewSession.Begin(item.Key);
                await _detailPlayback.SetMutedAsync(
                    _settings.VideoPreviewMuted && item.ShouldRenderVideo,
                    operation.CancellationToken);
                if (!IsCurrentDetailPreviewOperation(operation))
                {
                    return;
                }

                var result = await _detailPlayback.OpenAsync(
                    item,
                    startPosition,
                    operation.CancellationToken);
                if (!IsCurrentDetailPreviewOperation(operation))
                {
                    return;
                }

                ShowPlaybackResult(result, DetailStatus);
                DetailPlaybackControls.IsVisible = result.Succeeded;
                if (result.Succeeded)
                {
                    var muteResult = await _detailPlayback.SetMutedAsync(
                        _settings.VideoPreviewMuted && item.ShouldRenderVideo,
                        operation.CancellationToken);
                    if (!IsCurrentDetailPreviewOperation(operation))
                    {
                        return;
                    }

                    ShowPlaybackResult(muteResult, DetailStatus);
                    if (!muteResult.Succeeded)
                    {
                        return;
                    }

                    UiLocalization.SetText(DetailStatus, "正在播放");
                    SyncDetailLutSelection();
                    await ApplyLutSelectionAsync(
                        isPlayer: false,
                        DetailLutPicker.SelectedItem as LutOptionViewModel,
                        item.Key,
                        operation.Generation);
                }
            }
            finally
            {
                _detailPreviewGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private DetailPreviewOperation BeginDetailPreviewOperation(
        DetailPreviewMode mode,
        string? requestedKey)
    {
        CancelDetailLutSelection();
        CancelDetailAudioTrackSelection();
        var cancellation = new CancellationTokenSource();
        Interlocked.Exchange(ref _detailPreviewCancellation, cancellation)?.Cancel();
        _detailPreviewMode = mode;
        _detailPreviewRequestedKey = requestedKey;
        return new DetailPreviewOperation(
            ++_detailPreviewGeneration,
            cancellation,
            cancellation.Token);
    }

    private void FinishDetailPreviewOperation(DetailPreviewOperation operation)
    {
        Interlocked.CompareExchange(ref _detailPreviewCancellation, null, operation.Source);
        operation.Source.Dispose();
    }

    private bool IsCurrentDetailPreviewOperation(DetailPreviewOperation operation) =>
        operation.Generation == _detailPreviewGeneration &&
        !operation.CancellationToken.IsCancellationRequested;

    private static bool CanUsePlaybackPreview(LocalMediaType mediaType, string path) =>
        mediaType is LocalMediaType.Audio or LocalMediaType.Video || IsAnimatedImage(path);

    private static bool IsAnimatedImage(string pathOrExtension)
    {
        var extension = pathOrExtension.StartsWith('.')
            ? pathOrExtension
            : Path.GetExtension(pathOrExtension);
        if (extension.Equals(".gif", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!extension.Equals(".webp", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !File.Exists(pathOrExtension) || WebpHasAnimation(pathOrExtension);
    }

    private static bool WebpHasAnimation(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> riffHeader = stackalloc byte[12];
            stream.ReadExactly(riffHeader);
            if (!riffHeader[..4].SequenceEqual("RIFF"u8) ||
                !riffHeader.Slice(8, 4).SequenceEqual("WEBP"u8))
            {
                return false;
            }

            Span<byte> chunkHeader = stackalloc byte[8];
            for (var chunkIndex = 0; chunkIndex < 4_096 && stream.Position <= stream.Length - 8; chunkIndex++)
            {
                stream.ReadExactly(chunkHeader);
                var length = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader.Slice(4, 4));
                var remaining = stream.Length - stream.Position;
                if (length > remaining)
                {
                    return false;
                }

                var type = chunkHeader[..4];
                if (type.SequenceEqual("ANIM"u8) || type.SequenceEqual("ANMF"u8))
                {
                    return true;
                }

                if (type.SequenceEqual("VP8X"u8) && length > 0)
                {
                    var flags = stream.ReadByte();
                    if (flags < 0 || (flags & 0x02) != 0)
                    {
                        return flags >= 0;
                    }

                    stream.Seek(length - 1L + (length & 1), SeekOrigin.Current);
                }
                else
                {
                    stream.Seek(length + (length & 1), SeekOrigin.Current);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
        }

        return false;
    }

    private async Task EndHoverPreviewAsync(string key)
    {
        if (_detailPreviewMode != DetailPreviewMode.Hover || _detailPreviewRequestedKey != key)
        {
            return;
        }

        SaveDetailPreviewPosition();
        var operation = BeginDetailPreviewOperation(DetailPreviewMode.None, null);
        try
        {
            await _detailPreviewGate.WaitAsync(operation.CancellationToken);
            try
            {
                if (IsCurrentDetailPreviewOperation(operation))
                {
                    await _detailPlayback.PauseAsync(operation.CancellationToken);
                }
            }
            finally
            {
                _detailPreviewGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            FinishDetailPreviewOperation(operation);
        }
    }

    private void BeginHoverDetailSelection(string key)
    {
        _hoverDetailSelectionKey = key;
    }

    private void CommitHoverDetailSelection()
    {
        _hoverDetailSelectionKey = null;
    }

    private void DiscardHoverDetailSelection() => CommitHoverDetailSelection();

    private void CommitHoverDetailSelection(string key)
    {
        if (!string.Equals(_hoverDetailSelectionKey, key, StringComparison.Ordinal))
        {
            return;
        }

        CommitHoverDetailSelection();
    }

    private async Task StopDetailPreviewAsync(bool savePosition)
    {
        if (savePosition)
        {
            SaveDetailPreviewPosition();
        }

        var operation = BeginDetailPreviewOperation(DetailPreviewMode.None, null);
        try
        {
            await _detailPreviewGate.WaitAsync(operation.CancellationToken);
            try
            {
                if (IsCurrentDetailPreviewOperation(operation))
                {
                    await _detailPlayback.StopAsync(operation.CancellationToken);
                    _detailPreviewKey = null;
                    DetailPlaybackControls.IsVisible = SelectedDetailPlaybackKey() is not null;
                }
            }
            finally
            {
                _detailPreviewGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            FinishDetailPreviewOperation(operation);
        }
    }

    private void SaveDetailPreviewPosition()
    {
        if (_detailPreviewKey is null)
        {
            return;
        }

        _previewSession.SavePosition(
            _detailPreviewKey,
            _detailPlayback.State.Position,
            _detailPlayback.State.Duration);
    }

    private void CloudAssetCard_OnPointerEntered(object? sender, PointerEventArgs eventArgs)
    {
        if (CloudHoverToggle.IsChecked != true || _isDragging ||
            sender is not Border { DataContext: CloudAssetCardViewModel asset } ||
            eventArgs.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (asset.Asset.Category == ApiAssetCategory.Image && !IsAnimatedImage(asset.Asset.Extension))
        {
            return;
        }

        // Cloud hover uses a derivative proxy when available. Until it is ready,
        // selecting the item remains non-blocking and shows its derivative state.
        var key = $"cloud:{asset.Id:N}:{asset.Asset.CurrentVersionId:N}";
        BeginHoverDetailSelection(key);
        SelectCloudAsset(asset);
        _ = BeginCloudHoverPreviewAsync(asset);
    }

    private async Task BeginCloudHoverPreviewAsync(CloudAssetCardViewModel asset)
    {
        var requestedKey = $"cloud:{asset.Id:N}:{asset.Asset.CurrentVersionId:N}";
        var operation = BeginDetailPreviewOperation(DetailPreviewMode.Hover, requestedKey);
        try
        {
            var previewPath = await TryGetCloudPreviewPathAsync(
                asset.Asset,
                allowOriginalDownload: false,
                cancellationToken: operation.CancellationToken);
            if (previewPath is null || !IsCurrentDetailPreviewOperation(operation) ||
                _selectedCloudAsset?.Id != asset.Id)
            {
                return;
            }

            await OpenDetailPreviewAsync(CreateCloudPlaybackItem(asset.Asset, previewPath), operation);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (IsCurrentDetailPreviewOperation(operation) &&
                _selectedCloudAsset?.Id == asset.Id)
            {
                UiLocalization.SetText(
                    DetailStatus,
                    "悬停预览暂不可用：{0}",
                    UserMessage(exception));
            }
        }
        finally
        {
            FinishDetailPreviewOperation(operation);
        }
    }

    private async void CloudAssetCard_OnPointerExited(object? sender, PointerEventArgs eventArgs)
    {
        if (sender is not Border { DataContext: CloudAssetCardViewModel asset })
        {
            return;
        }

        var key = $"cloud:{asset.Id:N}:{asset.Asset.CurrentVersionId:N}";
        await EndHoverPreviewAsync(key);
        CommitHoverDetailSelection(key);
    }

    private Task<string?> TryGetCloudPreviewPathAsync(
        ApiAsset asset,
        bool allowOriginalDownload,
        CancellationToken cancellationToken = default) =>
        ResolveCloudPreviewPathAsync(asset, allowOriginalDownload, cancellationToken);

    private async void DetailPlayPause_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        var selectedKey = SelectedDetailPlaybackKey();
        if (selectedKey is null)
        {
            return;
        }

        if (!string.Equals(_detailPreviewKey, selectedKey, StringComparison.Ordinal))
        {
            if (_selectedLocalAsset is { } local)
            {
                await BeginSelectedLocalPreviewAsync(local);
            }
            else if (_selectedCloudAsset is { } cloud)
            {
                await BeginSelectedCloudPreviewAsync(cloud);
            }

            return;
        }

        var generation = _detailPreviewGeneration;
        var cancellationToken = _detailPreviewCancellation?.Token ?? CancellationToken.None;
        try
        {
            await _detailPreviewGate.WaitAsync(cancellationToken);
            try
            {
                if (generation != _detailPreviewGeneration || cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                ShowPlaybackResult(
                    await _detailPlayback.PlayPauseAsync(cancellationToken),
                    DetailStatus);
            }
            finally
            {
                _detailPreviewGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ClientDiagnostics.WriteException("detail-play-pause", exception, isFatal: false);
            UiLocalization.SetText(DetailStatus, "播放控制失败：{0}", UserMessage(exception));
        }
    }

    private string? SelectedDetailPlaybackKey()
    {
        if (_selectedLocalAsset is { IsAvailable: true } local &&
            CanUsePlaybackPreview(local.MediaType, local.FullPath))
        {
            return $"local:{local.Id:N}";
        }

        if (_selectedCloudAsset is { Asset.HasOriginal: true } cloud)
        {
            var isPlayable = cloud.Asset.Category is ApiAssetCategory.Bgm or
                ApiAssetCategory.SoundEffect or ApiAssetCategory.Video ||
                cloud.Asset.Category == ApiAssetCategory.Image && IsAnimatedImage(cloud.Asset.Extension);
            return isPlayable
                ? $"cloud:{cloud.Id:N}:{cloud.Asset.CurrentVersionId:N}"
                : null;
        }

        return null;
    }

    private async void PlayerPlayPause_OnClick(object? sender, RoutedEventArgs eventArgs) =>
        ShowPlaybackResult(await _playerPlayback.PlayPauseAsync(), PlayerStatusText);

    private async void PlayerPrevious_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        ShowPlaybackResult(await _playerPlayback.PreviousAsync(), PlayerStatusText);
        SyncPlayerQueue();
        await RefreshPlayerVisualsAsync(_playerPlayback.CurrentItem);
        SyncPlayerLutSelection();
        UpdatePlayerTrimUi();
        await RefreshPlayerMarkersAsync();
    }

    private async void PlayerNext_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        ShowPlaybackResult(await _playerPlayback.NextAsync(), PlayerStatusText);
        SyncPlayerQueue();
        await RefreshPlayerVisualsAsync(_playerPlayback.CurrentItem);
        SyncPlayerLutSelection();
        UpdatePlayerTrimUi();
        await RefreshPlayerMarkersAsync();
    }

    private async void PlayerFrameBack_OnClick(object? sender, RoutedEventArgs eventArgs) =>
        ShowPlaybackResult(await _playerPlayback.StepFrameAsync(forward: false), PlayerStatusText);

    private async void PlayerFrameForward_OnClick(object? sender, RoutedEventArgs eventArgs) =>
        ShowPlaybackResult(await _playerPlayback.StepFrameAsync(forward: true), PlayerStatusText);

    private void PlayerFullscreen_OnClick(object? sender, RoutedEventArgs eventArgs) =>
        WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;

    private void PlayerLoop_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        _playerPlayback.RepeatMode = _playerPlayback.RepeatMode == PlaybackRepeatMode.Off
            ? PlaybackRepeatMode.All
            : PlaybackRepeatMode.Off;
        _settings = _settings with { PlaybackLoopEnabled = _playerPlayback.RepeatMode != PlaybackRepeatMode.Off };
        UpdatePlaybackModeButtons();
        UiLocalization.SetText(
            PlayerToolStatusText,
            _settings.PlaybackLoopEnabled ? "循环播放已开启" : "循环播放已关闭");
        QueuePlaybackSettingsSave();
    }

    private void PlayerShuffle_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        _playerPlayback.OrderMode = _playerPlayback.OrderMode == PlaybackOrderMode.Sequential
            ? PlaybackOrderMode.Shuffle
            : PlaybackOrderMode.Sequential;
        _settings = _settings with { PlaybackShuffleEnabled = _playerPlayback.OrderMode == PlaybackOrderMode.Shuffle };
        UpdatePlaybackModeButtons();
        UiLocalization.SetText(
            PlayerToolStatusText,
            _settings.PlaybackShuffleEnabled ? "随机播放已开启" : "随机播放已关闭");
        QueuePlaybackSettingsSave();
    }

    private void UpdatePlaybackModeButtons()
    {
        var loopEnabled = _playerPlayback.RepeatMode != PlaybackRepeatMode.Off;
        var shuffleEnabled = _playerPlayback.OrderMode == PlaybackOrderMode.Shuffle;
        PlayerLoopButton.Classes.Set("selected", loopEnabled);
        PlayerShuffleButton.Classes.Set("selected", shuffleEnabled);
        UiLocalization.SetContent(PlayerLoopButton, loopEnabled ? "循环：开" : "循环：关");
        UiLocalization.SetContent(PlayerShuffleButton, shuffleEnabled ? "随机：开" : "随机：关");
    }

    private async void PlaybackSpeedPicker_OnSelectionChanged(object? sender, SelectionChangedEventArgs eventArgs)
    {
        if (_isLoading || PlaybackSpeedPicker.SelectedItem is not ComboBoxItem { Tag: string value } ||
            !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed))
        {
            return;
        }

        var preferenceGeneration = Volatile.Read(ref _playbackPreferenceGeneration);
        try
        {
            var result = await _playerPlayback.SetSpeedAsync(speed);
            ShowPlaybackResult(result, PlayerStatusText);
            if (result.Succeeded &&
                preferenceGeneration == Volatile.Read(ref _playbackPreferenceGeneration))
            {
                _settings = _settings with { PlaybackSpeed = speed };
                QueuePlaybackSettingsSave();
            }
        }
        catch (Exception exception)
        {
            UiLocalization.SetText(PlayerToolStatusText, "保存失败：{0}", exception.Message);
        }
    }

    private void SelectPlaybackSpeed(double speed)
    {
        foreach (var item in PlaybackSpeedPicker.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is string value &&
                double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var candidate) &&
                Math.Abs(candidate - speed) < 0.001)
            {
                PlaybackSpeedPicker.SelectedItem = item;
                return;
            }
        }
    }

    private async void MasterVolumeSlider_OnValueChanged(object? sender, RangeBaseValueChangedEventArgs eventArgs) =>
        await SetMasterVolumeFromUiAsync(eventArgs.NewValue);

    private async void DetailVolumeSlider_OnValueChanged(object? sender, RangeBaseValueChangedEventArgs eventArgs) =>
        await SetMasterVolumeFromUiAsync(eventArgs.NewValue);

    private async void SettingsVolumeSlider_OnValueChanged(object? sender, RangeBaseValueChangedEventArgs eventArgs) =>
        await SetMasterVolumeFromUiAsync(eventArgs.NewValue);

    private async Task SetMasterVolumeFromUiAsync(double value)
    {
        if (_suppressVolumeChange || _isLoading)
        {
            return;
        }

        var normalized = Math.Clamp(Math.Round(value), 0, 100);
        _suppressVolumeChange = true;
        MasterVolumeSlider.Value = normalized;
        DetailVolumeSlider.Value = normalized;
        SettingsVolumeSlider.Value = normalized;
        MasterVolumeText.Text = $"{normalized:0}%";
        SettingsVolumeText.Text = $"{normalized:0}%";
        _suppressVolumeChange = false;

        var cancellation = new CancellationTokenSource();
        var previousCancellation = Interlocked.Exchange(ref _volumeChangeCancellation, cancellation);
        previousCancellation?.Cancel();
        try
        {
            var result = await _playerPlayback.SetVolumeAsync(normalized, cancellation.Token);
            if (result.Succeeded && ReferenceEquals(_volumeChangeCancellation, cancellation))
            {
                _settings = _settings with { MasterVolume = normalized };
                QueuePlaybackSettingsSave();
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(_volumeChangeCancellation, cancellation))
            {
                UiLocalization.SetText(PlayerToolStatusText, "保存失败：{0}", exception.Message);
            }
        }
        finally
        {
            Interlocked.CompareExchange(ref _volumeChangeCancellation, null, cancellation);
            cancellation.Dispose();
        }
    }

    private void QueuePlaybackSettingsSave()
    {
        var cancellation = new CancellationTokenSource();
        var previousCancellation = Interlocked.Exchange(
            ref _playbackSettingsSaveCancellation,
            cancellation);
        previousCancellation?.Cancel();
        _ = SavePlaybackSettingsAfterDelayAsync(cancellation);
    }

    private void InvalidatePendingPlaybackPreferenceWrites()
    {
        Interlocked.Increment(ref _playbackPreferenceGeneration);
        var volumeCancellation = Interlocked.Exchange(ref _volumeChangeCancellation, null);
        volumeCancellation?.Cancel();
        var settingsSaveCancellation = Interlocked.Exchange(
            ref _playbackSettingsSaveCancellation,
            null);
        settingsSaveCancellation?.Cancel();
    }

    private async Task SavePlaybackSettingsAfterDelayAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(350), cancellation.Token);
            await SaveSettingsAsync();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ClientDiagnostics.WriteException("playback-settings-save", exception, isFatal: false);
            UiLocalization.SetText(PlayerToolStatusText, "保存失败：{0}", exception.Message);
        }
        finally
        {
            Interlocked.CompareExchange(
                ref _playbackSettingsSaveCancellation,
                null,
                cancellation);
            cancellation.Dispose();
        }
    }

    private void PlayerPositionSlider_OnValueChanged(object? sender, RangeBaseValueChangedEventArgs eventArgs)
    {
        if (!_suppressPlayerPosition)
        {
            DebounceSeek(_playerPlayback, eventArgs.NewValue, isPlayer: true);
        }
    }

    private void DetailPositionSlider_OnValueChanged(object? sender, RangeBaseValueChangedEventArgs eventArgs)
    {
        if (!_suppressDetailPosition)
        {
            DebounceSeek(_detailPlayback, eventArgs.NewValue, isPlayer: false);
        }
    }

    private void DebounceSeek(PlaybackController controller, double seconds, bool isPlayer)
    {
        var previous = isPlayer ? _playerSeekCancellation : _detailSeekCancellation;
        previous?.Cancel();
        var cancellation = new CancellationTokenSource();
        if (isPlayer)
        {
            _playerSeekCancellation = cancellation;
        }
        else
        {
            _detailSeekCancellation = cancellation;
        }

        _ = SeekAfterDelayAsync(controller, seconds, cancellation, isPlayer);
    }

    private async Task SeekAfterDelayAsync(
        PlaybackController controller,
        double seconds,
        CancellationTokenSource cancellation,
        bool isPlayer)
    {
        try
        {
            await Task.Delay(120, cancellation.Token);
            await controller.SeekAsync(TimeSpan.FromSeconds(Math.Max(0, seconds)), cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ClientDiagnostics.WriteException("playback-seek", exception, isFatal: false);
        }
        finally
        {
            if (isPlayer)
            {
                Interlocked.CompareExchange(ref _playerSeekCancellation, null, cancellation);
            }
            else
            {
                Interlocked.CompareExchange(ref _detailSeekCancellation, null, cancellation);
            }

            cancellation.Dispose();
        }
    }

    private void PlaybackUiTimer_OnTick(object? sender, EventArgs eventArgs)
    {
        UpdatePlayerPlaybackUi(_playerPlayback.State);
        UpdateDetailPlaybackUi(_detailPlayback.State);
    }

    private void PlayerPlayback_OnStateChanged(object? sender, PlaybackEngineStateChangedEventArgs eventArgs) =>
        Dispatcher.UIThread.Post(() =>
        {
            UpdatePlayerPlaybackUi(eventArgs.State);
            SyncPlayerQueue();
            if (eventArgs.State.CurrentItemKey is { } visualKey &&
                visualKey != _lastPlayerVisualKey)
            {
                _lastPlayerVisualKey = visualKey;
                _ = RefreshPlayerVisualsForKeyAsync(visualKey);
            }

            if (eventArgs.State.Status == PlaybackStatus.Loading)
            {
                _lastPlayerTrackKey = null;
                PlayerAudioTracks.Clear();
            }
            else if (eventArgs.State.Status is PlaybackStatus.Playing or PlaybackStatus.Paused &&
                     _lastPlayerTrackKey != eventArgs.State.CurrentItemKey &&
                     eventArgs.State.CurrentItemKey is not null)
            {
                _lastPlayerTrackKey = eventArgs.State.CurrentItemKey;
                _ = RefreshAudioTracksAsync(_playerPlayback, PlayerAudioTracks, isPlayer: true);
            }
        });

    private async Task RefreshPlayerVisualsForKeyAsync(string key)
    {
        var item = _playerPlayback.Queue.FirstOrDefault(candidate => candidate.Key == key);
        if (item is null || _playerPlayback.CurrentItem?.Key != key)
        {
            return;
        }

        await RefreshPlayerVisualsAsync(item);
        if (_playerPlayback.CurrentItem?.Key == key)
        {
            SyncPlayerLutSelection();
            UpdatePlayerTrimUi();
            await RefreshPlayerMarkersAsync();
        }
    }

    private void DetailPlayback_OnStateChanged(object? sender, PlaybackEngineStateChangedEventArgs eventArgs) =>
        Dispatcher.UIThread.Post(() =>
        {
            UpdateDetailPlaybackUi(eventArgs.State);
            if (eventArgs.State.Status == PlaybackStatus.Loading)
            {
                _lastDetailTrackKey = null;
                DetailAudioTracks.Clear();
            }
            else if (eventArgs.State.Status is PlaybackStatus.Playing or PlaybackStatus.Paused &&
                     _lastDetailTrackKey != eventArgs.State.CurrentItemKey &&
                     eventArgs.State.CurrentItemKey is not null)
            {
                _lastDetailTrackKey = eventArgs.State.CurrentItemKey;
                _ = RefreshAudioTracksAsync(
                    _detailPlayback,
                    DetailAudioTracks,
                    isPlayer: false,
                    expectedItemKey: eventArgs.State.CurrentItemKey);
            }
        });

    private void UpdatePlayerPlaybackUi(PlaybackEngineState state)
    {
        _suppressPlayerPosition = true;
        PlayerPositionSlider.Maximum = Math.Max(1, state.Duration?.TotalSeconds ?? 1);
        PlayerPositionSlider.Value = Math.Clamp(
            state.Position.TotalSeconds,
            PlayerPositionSlider.Minimum,
            PlayerPositionSlider.Maximum);
        _suppressPlayerPosition = false;
        PlayerTimeText.Text = $"{FormatPlaybackTime(state.Position)} / {FormatPlaybackTime(state.Duration)}";
        UpdatePlayerTimelineMarkers();
        PlayerPlayPauseButton.Content = state.IsPaused ||
                                        state.Status is PlaybackStatus.Paused or PlaybackStatus.Idle or PlaybackStatus.Ended
            ? "▶"
            : "Ⅱ";
        switch (state.Status)
        {
            case PlaybackStatus.Loading:
                UiLocalization.SetText(PlayerStatusText, "正在载入媒体…");
                break;
            case PlaybackStatus.Playing when state.MediaTitle is not null:
                PlayerStatusText.Text = state.MediaTitle;
                break;
            case PlaybackStatus.Playing:
                UiLocalization.SetText(PlayerStatusText, "正在播放");
                break;
            case PlaybackStatus.Paused when state.MediaTitle is not null:
                UiLocalization.SetText(PlayerStatusText, "已暂停 · {0}", state.MediaTitle);
                break;
            case PlaybackStatus.Paused:
                UiLocalization.SetText(PlayerStatusText, "已暂停");
                break;
            case PlaybackStatus.Failed when state.Error is not null:
            case PlaybackStatus.Unavailable when state.Error is not null:
                PlayerStatusText.Text = state.Error;
                break;
            case PlaybackStatus.Failed:
                UiLocalization.SetText(PlayerStatusText, "播放失败");
                break;
            case PlaybackStatus.Unavailable:
                UiLocalization.SetText(PlayerStatusText, "媒体运行库不可用");
                break;
            case PlaybackStatus.Ended:
                UiLocalization.SetText(PlayerStatusText, "播放结束");
                break;
            default:
                if (_playerPlayback.CurrentItem is null)
                {
                    UiLocalization.SetText(
                        PlayerStatusText,
                        "双击素材或打开媒体文件开始播放");
                }

                break;
        }
        var currentMediaType = _playerPlayback.CurrentItem?.MediaType;
        var supportsRangeExport = currentMediaType is LocalMediaType.Audio or LocalMediaType.Video;
        var supportsLut = currentMediaType == LocalMediaType.Video;
        PlayerLutToolsPanel.IsVisible = supportsLut;
        PlayerTrimToolsPanel.IsVisible = supportsRangeExport;
        PlayerExportWithLutToggle.IsVisible = supportsLut;
        PlayerAudioTrackButton.IsVisible = supportsRangeExport;
        var showArtwork = !state.HasRenderableVideoFrame &&
                          state.Status != PlaybackStatus.Ended &&
                          PlayerArtworkImage.Source is not null;
        PlayerVideoHost.IsVisible = state.HasRenderableVideoFrame;
        PlayerEndedBackdrop.IsVisible = state.HasVideo && state.Status == PlaybackStatus.Ended;
        PlayerArtworkViewport.IsVisible = showArtwork;
        PlayerPlaceholder.IsVisible = !state.HasRenderableVideoFrame &&
                                      state.Status != PlaybackStatus.Ended &&
                                      !showArtwork;
        PlayerImageToolsPanel.IsVisible = showArtwork && IsCurrentStaticPlayerImage();
        PlayerArtworkViewport.IsHitTestVisible = IsCurrentStaticPlayerImage();
    }

    private void UpdateDetailPlaybackUi(PlaybackEngineState state)
    {
        _suppressDetailPosition = true;
        DetailPositionSlider.Maximum = Math.Max(1, state.Duration?.TotalSeconds ?? 1);
        DetailPositionSlider.Value = Math.Clamp(
            state.Position.TotalSeconds,
            DetailPositionSlider.Minimum,
            DetailPositionSlider.Maximum);
        _suppressDetailPosition = false;
        DetailTimeText.Text = FormatPlaybackTime(state.Position);
        DetailPlayPauseButton.Content = state.IsPaused ||
                                        state.Status is PlaybackStatus.Paused or PlaybackStatus.Idle or PlaybackStatus.Ended
            ? "▶"
            : "Ⅱ";
        var detailSupportsLut = _detailPlayback.CurrentItem?.MediaType == LocalMediaType.Video;
        DetailLutPicker.IsVisible = detailSupportsLut;
        DetailLutToolsPanel.IsVisible = detailSupportsLut;
        DetailVideoHost.IsVisible = state.HasRenderableVideoFrame &&
                                    DetailPlaybackControls.IsVisible &&
                                    state.Status != PlaybackStatus.Ended;
        DetailEndedBackdrop.IsVisible = state.HasVideo &&
                                        DetailPlaybackControls.IsVisible &&
                                        state.Status == PlaybackStatus.Ended;
        var showDetailFallback = !state.HasRenderableVideoFrame &&
                                 state.Status != PlaybackStatus.Ended;
        DetailPreviewImage.IsVisible = showDetailFallback && DetailPreviewImage.Source is not null;
        DetailInitial.IsVisible = showDetailFallback && DetailPreviewImage.Source is null;
        DetailExportLutButton.IsVisible = detailSupportsLut;
    }

    private static string FormatPlaybackTime(TimeSpan? value)
    {
        if (value is null)
        {
            return "--:--";
        }

        var totalHours = (long)Math.Floor(value.Value.TotalHours);
        return totalHours > 0
            ? $"{totalHours:00}:{value.Value.Minutes:00}:{value.Value.Seconds:00}"
            : $"{value.Value.Minutes:00}:{value.Value.Seconds:00}";
    }

    private async Task RefreshAudioTracksAsync(
        PlaybackController controller,
        ObservableCollection<AudioTrackOptionViewModel> target,
        bool isPlayer,
        string? expectedItemKey = null)
    {
        var ownsSelectionSuppression = false;
        try
        {
            expectedItemKey ??= controller.CurrentItem?.Key;
            var tracks = await controller.GetAudioTracksAsync();
            if (!StringComparer.Ordinal.Equals(controller.CurrentItem?.Key, expectedItemKey))
            {
                return;
            }

            if (!isPlayer && tracks.Count > 0 && expectedItemKey is not null)
            {
                var hasRememberedSelection = _detailAudioTrackSelections.TryGetValue(
                    expectedItemKey,
                    out var remembered);
                var selectedIds = hasRememberedSelection
                    ? remembered!.Where(id => tracks.Any(track => track.Id == id)).ToArray()
                    : tracks.Where(track => track.IsSelected).Select(track => track.Id).ToArray();
                if ((!hasRememberedSelection || remembered!.Length > 0) && selectedIds.Length == 0)
                {
                    selectedIds =
                    [tracks.FirstOrDefault(track => track.IsDefault)?.Id ?? tracks[0].Id];
                }

                var selectionResult = await controller.SetSelectedAudioTracksAsync(
                    selectedIds,
                    expectedItemKey);
                if (!selectionResult.Succeeded ||
                    !StringComparer.Ordinal.Equals(controller.CurrentItem?.Key, expectedItemKey))
                {
                    return;
                }

                tracks = await controller.GetAudioTracksAsync();
                if (!StringComparer.Ordinal.Equals(controller.CurrentItem?.Key, expectedItemKey))
                {
                    return;
                }
            }

            var suppress = isPlayer ? _suppressPlayerTrackSelection : _suppressDetailTrackSelection;
            if (suppress)
            {
                return;
            }

            if (isPlayer)
            {
                _suppressPlayerTrackSelection = true;
            }
            else
            {
                _suppressDetailTrackSelection = true;
            }
            ownsSelectionSuppression = true;

            target.Clear();
            foreach (var track in tracks)
            {
                target.Add(new AudioTrackOptionViewModel(
                    track.Id,
                    () => BuildAudioTrackLabel(track),
                    track.IsSelected));
            }

            if (isPlayer)
            {
                var (format, arguments) = tracks.Count switch
                {
                    0 => ("无音轨", Array.Empty<object?>()),
                    1 => ("1 条音轨", Array.Empty<object?>()),
                    _ => ("音轨 {0}", new object?[] { tracks.Count })
                };
                UiLocalization.SetContent(PlayerAudioTrackButton, format, arguments);
            }
            else
            {
                DetailAudioTrackButton.IsEnabled = tracks.Count > 0;
            }
        }
        catch
        {
            target.Clear();
        }
        finally
        {
            if (ownsSelectionSuppression && isPlayer)
            {
                _suppressPlayerTrackSelection = false;
            }
            else if (ownsSelectionSuppression)
            {
                _suppressDetailTrackSelection = false;
            }
        }
    }

    private static string BuildAudioTrackLabel(PlaybackAudioTrack track)
    {
        var title = string.IsNullOrWhiteSpace(track.Title)
            ? UiLocalization.Format("音轨 {0}", track.Id)
            : track.Title;
        var language = string.IsNullOrWhiteSpace(track.Language) ? null : track.Language;
        var codec = string.IsNullOrWhiteSpace(track.Codec) ? null : track.Codec.ToUpperInvariant();
        return string.Join(" · ", new[] { title, language, codec }.Where(value => value is not null));
    }

    private async void PlayerAudioTrack_OnChanged(object? sender, RoutedEventArgs eventArgs)
    {
        if (_suppressPlayerTrackSelection)
        {
            return;
        }

        SynchronizeAudioTrackCheckBox(sender);
        await ApplySelectedAudioTracksLatestAsync(
            _playerPlayback,
            PlayerAudioTracks,
            PlayerStatusText,
            isPlayer: true);
    }

    private async void DetailAudioTrack_OnChanged(object? sender, RoutedEventArgs eventArgs)
    {
        if (_suppressDetailTrackSelection)
        {
            return;
        }

        SynchronizeAudioTrackCheckBox(sender);
        await ApplySelectedAudioTracksLatestAsync(
            _detailPlayback,
            DetailAudioTracks,
            DetailStatus,
            isPlayer: false);
    }

    private static void SynchronizeAudioTrackCheckBox(object? sender)
    {
        if (sender is CheckBox { DataContext: AudioTrackOptionViewModel option } checkBox)
        {
            option.IsSelected = checkBox.IsChecked == true;
        }
    }

    private async Task ApplySelectedAudioTracksLatestAsync(
        PlaybackController controller,
        IEnumerable<AudioTrackOptionViewModel> options,
        TextBlock status,
        bool isPlayer)
    {
        var cancellation = new CancellationTokenSource();
        var expectedItemKey = controller.CurrentItem?.Key;
        CancellationTokenSource? previous;
        long generation;
        if (isPlayer)
        {
            previous = Interlocked.Exchange(ref _playerAudioTrackCancellation, cancellation);
            generation = Interlocked.Increment(ref _playerAudioTrackGeneration);
        }
        else
        {
            previous = Interlocked.Exchange(ref _detailAudioTrackCancellation, cancellation);
            generation = Interlocked.Increment(ref _detailAudioTrackGeneration);
        }

        previous?.Cancel();
        try
        {
            var selectedIds = options
                .Where(item => item.IsSelected)
                .Select(item => checked((int)item.Id))
                .ToArray();
            if (expectedItemKey is null)
            {
                return;
            }

            var result = await controller.SetSelectedAudioTracksAsync(
                selectedIds,
                expectedItemKey,
                cancellation.Token);
            var currentGeneration = isPlayer
                ? Volatile.Read(ref _playerAudioTrackGeneration)
                : Volatile.Read(ref _detailAudioTrackGeneration);
            if (generation == currentGeneration &&
                !cancellation.IsCancellationRequested &&
                StringComparer.Ordinal.Equals(controller.CurrentItem?.Key, expectedItemKey))
            {
                ShowPlaybackResult(result, status);
                if (!isPlayer && result.Succeeded && expectedItemKey is not null)
                {
                    _detailAudioTrackSelections[expectedItemKey] = selectedIds;
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ClientDiagnostics.WriteException("audio-track-selection", exception, isFatal: false);
            if (ReferenceEquals(
                    isPlayer ? _playerAudioTrackCancellation : _detailAudioTrackCancellation,
                    cancellation))
            {
                UiLocalization.SetText(status, "音轨切换失败：{0}", UserMessage(exception));
            }
        }
        finally
        {
            if (isPlayer)
            {
                Interlocked.CompareExchange(ref _playerAudioTrackCancellation, null, cancellation);
            }
            else
            {
                Interlocked.CompareExchange(ref _detailAudioTrackCancellation, null, cancellation);
            }

            cancellation.Dispose();
        }
    }

    private void SyncPlayerQueue()
    {
        var currentKey = _playerPlayback.CurrentItem?.Key;
        var queue = _playerPlayback.Queue;
        var queueMatches = PlayerQueueItems.Count == queue.Count;
        if (queueMatches)
        {
            for (var index = 0; index < queue.Count; index++)
            {
                var item = queue[index];
                var viewModel = PlayerQueueItems[index];
                if (viewModel.Id != QueueViewModelId(item.Key) ||
                    !StringComparer.Ordinal.Equals(viewModel.Source, item.Source) ||
                    !StringComparer.Ordinal.Equals(viewModel.Name, item.Title))
                {
                    queueMatches = false;
                    break;
                }
            }
        }

        _suppressPlayerQueueSelection = true;
        try
        {
            if (!queueMatches)
            {
                PlayerQueueList.SelectedIndex = -1;
                PlayerQueueList.ItemsSource = null;
                PlayerQueueItems.Clear();
                foreach (var item in queue)
                {
                    PlayerQueueItems.Add(new PlayerQueueItemViewModel(
                        QueueViewModelId(item.Key),
                        item.Source,
                        item.Title,
                        item.MediaType switch
                        {
                            LocalMediaType.Audio => "音频",
                            LocalMediaType.Video => "视频",
                            _ => "图片"
                        }));
                }

                PlayerQueueList.ItemsSource = PlayerQueueItems;
            }

            for (var index = 0; index < queue.Count; index++)
            {
                PlayerQueueItems[index].IsCurrent = queue[index].Key == currentKey;
            }
        }
        finally
        {
            _suppressPlayerQueueSelection = false;
        }

        UiLocalization.SetText(PlayerQueueCountText, "{0:N0} 项", PlayerQueueItems.Count);
    }

    private async void PlayerSidePane_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        var showMarkers = sender is Button { Tag: string tag } && tag == "markers";
        PlayerQueuePane.IsVisible = !showMarkers;
        PlayerMarkerPane.IsVisible = showMarkers;
        PlayerQueuePaneButton.Classes.Set("selected", !showMarkers);
        PlayerMarkersPaneButton.Classes.Set("selected", showMarkers);
        if (showMarkers)
        {
            await RefreshPlayerMarkersAsync();
        }
    }

    private static Guid QueueViewModelId(string key)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private async void PlayerQueueList_OnSelectionChanged(object? sender, SelectionChangedEventArgs eventArgs)
    {
        if (_suppressPlayerQueueSelection ||
            PlayerQueueList.SelectedItem is not PlayerQueueItemViewModel selected)
        {
            return;
        }

        var queue = _playerPlayback.Queue;
        var selectedIndex = Array.FindIndex(
            queue.ToArray(),
            item => QueueViewModelId(item.Key) == selected.Id);
        if (selectedIndex < 0)
        {
            return;
        }

        ShowPlaybackResult(
            await _playerPlayback.PlayIndexAsync(selectedIndex),
            PlayerStatusText);
        SyncPlayerQueue();
        await RefreshPlayerVisualsAsync(_playerPlayback.CurrentItem);
        SyncPlayerLutSelection();
        UpdatePlayerTrimUi();
        await RefreshPlayerMarkersAsync();
    }

    private async void RemovePlayerQueueItem_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not Button { DataContext: PlayerQueueItemViewModel selected })
        {
            return;
        }

        var item = _playerPlayback.Queue.FirstOrDefault(queue => QueueViewModelId(queue.Key) == selected.Id);
        if (item is not null)
        {
            var result = await _playerPlayback.RemoveFromQueueAsync(item.Key);
            ShowPlaybackResult(result, PlayerStatusText);
            if (!result.Succeeded)
            {
                return;
            }

            ClearTemporaryMarkerState(item.Key);
            _playerLutOptionKeys.Remove(item.Key);
            _playerTrimRanges.Remove(item.Key);
            SyncPlayerQueue();
            await RefreshPlayerVisualsAsync(_playerPlayback.CurrentItem);
            if (_playerPlayback.CurrentItem is null)
            {
                PlayerAudioTracks.Clear();
            }
            else
            {
                await RefreshAudioTracksAsync(_playerPlayback, PlayerAudioTracks, isPlayer: true);
            }

            SyncPlayerLutSelection();
            UpdatePlayerTrimUi();
            await RefreshPlayerMarkersAsync();
        }
    }

    private async void ClearPlayerQueue_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        await _playerPlayback.StopAsync();
        _playerPlayback.ReplaceQueue([]);
        ClearTemporaryMarkersExcept([]);
        _playerLutOptionKeys.Clear();
        _playerTrimRanges.Clear();
        SyncPlayerQueue();
        await RefreshPlayerVisualsAsync(null);
        PlayerAudioTracks.Clear();
        PlayerMarkerSets.Clear();
        PlayerMarkers.Clear();
        PlayerMarkerEmptyText.IsVisible = true;
        PlayerManageMarkersButton.IsEnabled = false;
        SyncPlayerLutSelection();
        UpdatePlayerTrimUi();
    }

    private async void ShowRecentPlayback_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        var recent = _playerPlayback.Recent;
        var message = recent.Count == 0
            ? UiLocalization.Text("暂无最近播放记录。")
            : string.Join(Environment.NewLine, recent.Take(12).Select((item, index) =>
                $"{index + 1}. {item.Item.Title} · {item.LastPlayedAtUtc.ToLocalTime():MM-dd HH:mm}"));
        await new MessageDialogWindow(UiLocalization.Text("最近播放"), message).ShowDialog(this);
    }

    private async Task RefreshPlayerVisualsAsync(PlaybackItem? item)
    {
        if (item is not null &&
            !StringComparer.Ordinal.Equals(_playerPlayback.CurrentItem?.Key, item.Key))
        {
            return;
        }

        var generation = Interlocked.Increment(ref _playerVisualGeneration);
        var cancellation = new CancellationTokenSource();
        var cancellationToken = cancellation.Token;
        var previousCancellation = Interlocked.Exchange(ref _playerVisualCancellation, cancellation);

        var gateAcquired = false;
        try
        {
            previousCancellation?.Cancel();
            ClearPlayerVisuals();
            if (item is null || !File.Exists(item.Source))
            {
                return;
            }

            await _playerVisualGate.WaitAsync(cancellationToken);
            gateAcquired = true;
            ThrowIfPlayerVisualOperationIsStale(item, generation, cancellationToken);

            var artwork = item.MediaType == LocalMediaType.Audio
                ? await _mediaThumbnailService.GetOrCreateWaveformAsync(
                    item.Source,
                    maximumWidth: 1_024,
                    cancellationToken)
                : await _mediaThumbnailService.GetOrCreateAsync(
                    item.Source,
                    maximumEdge: 1_024,
                    cancellationToken);
            ThrowIfPlayerVisualOperationIsStale(item, generation, cancellationToken);
            if (artwork is not null)
            {
                var artworkBitmap = new Bitmap(artwork.CachePath);
                var artworkTransferred = false;
                try
                {
                    ThrowIfPlayerVisualOperationIsStale(item, generation, cancellationToken);
                    var previousArtworkBitmap = _playerArtworkBitmap;
                    PlayerArtworkImage.Source = artworkBitmap;
                    _playerArtworkBitmap = artworkBitmap;
                    artworkTransferred = true;
                    DeferredUiResourceDisposer.Dispose(previousArtworkBitmap);
                    PlayerArtworkImage.IsVisible = true;
                    PlayerPlaceholderGlyph.IsVisible = false;
                    PlayerPlaceholderText.IsVisible = false;
                    UpdatePlayerPlaybackUi(_playerPlayback.State);
                }
                finally
                {
                    if (!artworkTransferred)
                    {
                        artworkBitmap.Dispose();
                    }
                }
            }

            await RefreshTimelinePreviewAsync(item, generation, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (item is not null && IsCurrentPlayerVisualOperation(item, generation, cancellationToken))
            {
                UiLocalization.SetText(
                    PlayerToolStatusText,
                    "预览图生成失败：{0}",
                    exception.Message);
            }
        }
        finally
        {
            if (gateAcquired)
            {
                _playerVisualGate.Release();
            }

            Interlocked.CompareExchange(ref _playerVisualCancellation, null, cancellation);
            cancellation.Dispose();
        }
    }

    private async Task RefreshTimelinePreviewAsync(
        PlaybackItem item,
        long generation,
        CancellationToken cancellationToken)
    {
        ThrowIfPlayerVisualOperationIsStale(item, generation, cancellationToken);
        var ffmpeg = _mediaThumbnailService.FfmpegAvailability;
        if (!ffmpeg.IsAvailable || item.MediaType == LocalMediaType.Image)
        {
            return;
        }

        var source = new FileInfo(item.Source);
        var cacheKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"timeline-v2\n{source.FullName}\n{source.LastWriteTimeUtc.Ticks}\n{source.Length}")));
        Directory.CreateDirectory(AppPaths.MediaDerivativeCacheDirectory);
        var path = Path.Combine(AppPaths.MediaDerivativeCacheDirectory, cacheKey + ".webp");
        for (var attempt = 0; attempt < 2; attempt++)
        {
            ThrowIfPlayerVisualOperationIsStale(item, generation, cancellationToken);
            if (File.Exists(path) && new FileInfo(path).Length == 0)
            {
                File.Delete(path);
            }

            if (!File.Exists(path))
            {
                var temporaryPath = Path.Combine(
                    AppPaths.MediaDerivativeCacheDirectory,
                    $".{cacheKey}.{Guid.NewGuid():N}.webp");
                try
                {
                    var command = item.MediaType == LocalMediaType.Audio
                        ? FfmpegCommandBuilder.BuildWaveform(
                            item.Source,
                            temporaryPath,
                            1_800,
                            180,
                            ffmpegExecutable: ffmpeg.ResolvedExecutable!)
                        : _playerPlayback.State.Duration is { } duration && duration > TimeSpan.Zero
                            ? FfmpegCommandBuilder.BuildFilmstrip(
                                item.Source,
                                temporaryPath,
                                duration,
                                frameCount: 10,
                                frameWidth: 240,
                                frameHeight: 135,
                                ffmpegExecutable: ffmpeg.ResolvedExecutable!)
                            : null;
                    if (command is null)
                    {
                        return;
                    }

                    var result = await new ProcessMediaToolRunner().RunAsync(command, cancellationToken);
                    ThrowIfPlayerVisualOperationIsStale(item, generation, cancellationToken);
                    if (!result.Succeeded ||
                        !File.Exists(temporaryPath) ||
                        new FileInfo(temporaryPath).Length == 0)
                    {
                        return;
                    }

                    ThrowIfPlayerVisualOperationIsStale(item, generation, cancellationToken);
                    File.Move(temporaryPath, path, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temporaryPath))
                    {
                        File.Delete(temporaryPath);
                    }
                }
            }

            Bitmap timelineBitmap;
            try
            {
                ThrowIfPlayerVisualOperationIsStale(item, generation, cancellationToken);
                timelineBitmap = new Bitmap(path);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch when (attempt == 0)
            {
                File.Delete(path);
                continue;
            }

            var timelineTransferred = false;
            try
            {
                ThrowIfPlayerVisualOperationIsStale(item, generation, cancellationToken);
                var previousTimelineBitmap = _playerTimelineBitmap;
                PlayerTimelineImage.Source = timelineBitmap;
                _playerTimelineBitmap = timelineBitmap;
                timelineTransferred = true;
                DeferredUiResourceDisposer.Dispose(previousTimelineBitmap);
                PlayerTimelinePreview.IsVisible = true;
                PlayerToolStatusText.Text = string.Empty;
                return;
            }
            finally
            {
                if (!timelineTransferred)
                {
                    timelineBitmap.Dispose();
                }
            }
        }
    }

    private bool IsCurrentPlayerVisualOperation(
        PlaybackItem item,
        long generation,
        CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested &&
        generation == Volatile.Read(ref _playerVisualGeneration) &&
        StringComparer.Ordinal.Equals(_playerPlayback.CurrentItem?.Key, item.Key);

    private void ThrowIfPlayerVisualOperationIsStale(
        PlaybackItem item,
        long generation,
        CancellationToken cancellationToken)
    {
        if (IsCurrentPlayerVisualOperation(item, generation, cancellationToken))
        {
            return;
        }

        throw new OperationCanceledException(cancellationToken);
    }

    private void ClearPlayerVisuals()
    {
        ResetPlayerImageTransform();
        PlayerArtworkImage.Source = null;
        PlayerArtworkImage.IsVisible = false;
        PlayerArtworkViewport.IsVisible = false;
        PlayerImageToolsPanel.IsVisible = false;
        PlayerTimelineImage.Source = null;
        PlayerTimelinePreview.IsVisible = false;
        PlayerPlaceholderGlyph.IsVisible = true;
        PlayerPlaceholderText.IsVisible = true;
        DeferredUiResourceDisposer.Dispose(_playerArtworkBitmap);
        _playerArtworkBitmap = null;
        DeferredUiResourceDisposer.Dispose(_playerTimelineBitmap);
        _playerTimelineBitmap = null;
    }

    private bool IsCurrentStaticPlayerImage() =>
        _playerPlayback.CurrentItem is { MediaType: LocalMediaType.Image, ForceVideoOutput: false };

    private void PlayerImageZoomOut_OnClick(object? sender, RoutedEventArgs eventArgs) =>
        SetPlayerImageZoom(_playerImageZoom / 1.25);

    private void PlayerImageZoomIn_OnClick(object? sender, RoutedEventArgs eventArgs) =>
        SetPlayerImageZoom(_playerImageZoom * 1.25);

    private void PlayerImageReset_OnClick(object? sender, RoutedEventArgs eventArgs) =>
        ResetPlayerImageTransform();

    private void PlayerArtworkViewport_OnPointerWheelChanged(object? sender, PointerWheelEventArgs eventArgs)
    {
        if (!IsCurrentStaticPlayerImage())
        {
            return;
        }

        SetPlayerImageZoom(_playerImageZoom * (eventArgs.Delta.Y > 0 ? 1.15 : 1 / 1.15));
        eventArgs.Handled = true;
    }

    private void PlayerArtworkViewport_OnPointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        var point = eventArgs.GetCurrentPoint(PlayerArtworkViewport);
        if (!IsCurrentStaticPlayerImage() ||
            _playerImageZoom <= 1 ||
            !point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        _isPlayerImageDragging = true;
        _playerImageDragOrigin = point.Position;
        _playerImageDragStartTranslation = _playerImageTranslation;
        eventArgs.Pointer.Capture(PlayerArtworkViewport);
        eventArgs.Handled = true;
    }

    private void PlayerArtworkViewport_OnPointerMoved(object? sender, PointerEventArgs eventArgs)
    {
        if (!_isPlayerImageDragging || eventArgs.Pointer.Captured != PlayerArtworkViewport)
        {
            return;
        }

        var position = eventArgs.GetPosition(PlayerArtworkViewport);
        _playerImageTranslation = _playerImageDragStartTranslation + (position - _playerImageDragOrigin);
        ApplyPlayerImageTransform();
        eventArgs.Handled = true;
    }

    private void PlayerArtworkViewport_OnPointerReleased(object? sender, PointerReleasedEventArgs eventArgs)
    {
        if (!_isPlayerImageDragging)
        {
            return;
        }

        _isPlayerImageDragging = false;
        eventArgs.Pointer.Capture(null);
        eventArgs.Handled = true;
    }

    private void PlayerArtworkViewport_OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs eventArgs) =>
        _isPlayerImageDragging = false;

    private void SetPlayerImageZoom(double zoom)
    {
        if (!IsCurrentStaticPlayerImage())
        {
            return;
        }

        _playerImageZoom = Math.Clamp(zoom, 0.25, 8);
        if (_playerImageZoom <= 1)
        {
            _playerImageTranslation = default;
        }

        ApplyPlayerImageTransform();
    }

    private void ResetPlayerImageTransform()
    {
        _isPlayerImageDragging = false;
        _playerImageZoom = 1;
        _playerImageTranslation = default;
        ApplyPlayerImageTransform();
    }

    private void ApplyPlayerImageTransform()
    {
        _playerArtworkScaleTransform.ScaleX = _playerImageZoom;
        _playerArtworkScaleTransform.ScaleY = _playerImageZoom;
        _playerArtworkTranslateTransform.X = _playerImageTranslation.X;
        _playerArtworkTranslateTransform.Y = _playerImageTranslation.Y;
        PlayerImageZoomText.Text = $"{_playerImageZoom:P0}";
    }

    private async void PlayerScreenshot_OnClick(object? sender, RoutedEventArgs eventArgs) =>
        await RunGuardedInteractionAsync(
            "player-screenshot",
            SavePlayerScreenshotAsync,
            PlayerToolStatusText);

    private async Task SavePlayerScreenshotAsync()
    {
        if (_playerPlayback.CurrentItem is null)
        {
            return;
        }

        var target = await RunNativePickerAsync(
            "player-screenshot",
            () => StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = UiLocalization.Text("保存当前画面"),
                SuggestedFileName = $"{_playerPlayback.CurrentItem.Title}-{DateTime.Now:yyyyMMdd-HHmmss}.png",
                DefaultExtension = "png",
                FileTypeChoices =
                [
                    new FilePickerFileType(UiLocalization.Text("PNG 图片")) { Patterns = ["*.png"] }
                ]
            }),
            PlayerToolStatusText);
        if (target is null)
        {
            return;
        }

        ShowPlaybackResult(
            await _playerPlayback.TakeScreenshotAsync(target.Path.LocalPath),
            PlayerToolStatusText);
    }

    private async void PlayerSubtitle_OnClick(object? sender, RoutedEventArgs eventArgs) =>
        await RunGuardedInteractionAsync(
            "player-subtitle",
            AddPlayerSubtitleAsync,
            PlayerToolStatusText);

    private async Task AddPlayerSubtitleAsync()
    {
        var files = await RunNativePickerAsync(
            "player-subtitle",
            () => StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = UiLocalization.Text("加载外挂字幕"),
                AllowMultiple = false,
                FileTypeFilter = [SubtitleFileType]
            }),
            PlayerToolStatusText);
        if (files is null || files.Count == 0)
        {
            return;
        }

        ShowPlaybackResult(
            await _playerPlayback.AddSubtitleAsync(files[0].Path.LocalPath),
            PlayerToolStatusText);
    }

    private async void PlayerMediaInfo_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        var item = _playerPlayback.CurrentItem;
        if (item is null || !File.Exists(item.Source))
        {
            return;
        }

        try
        {
            var ffprobePath = ResolveRuntimePath(
                "INTERNAL_ASSET_LIBRARY_FFPROBE",
                AppPaths.FfprobePath);
            var result = await new FfprobeMediaAnalyzer(ffprobePath).ProbeAsync(item.Source);
            var message = result.Information is { } info
                ? BuildMediaInformationText(info)
                : UiLocalization.Format(
                    "无法读取完整媒体信息：{0}\n\n当前播放时长：{1}",
                    result.Diagnostic,
                    FormatPlaybackTime(_playerPlayback.State.Duration));
            await new MessageDialogWindow(UiLocalization.Text("媒体信息"), message).ShowDialog(this);
        }
        catch (Exception exception)
        {
            ClientDiagnostics.WriteException("player-media-info", exception, isFatal: false);
            UiLocalization.SetText(PlayerToolStatusText, "媒体信息读取失败：{0}", UserMessage(exception));
        }
    }

    private static string BuildMediaInformationText(MediaInformation info)
    {
        var lines = new List<string>
        {
            UiLocalization.Format(
                "格式：{0}",
                info.FormatLongName ?? info.FormatName ?? UiLocalization.Text("未知")),
            UiLocalization.Format("时长：{0}", FormatPlaybackTime(info.Duration)),
            UiLocalization.Format(
                "码率：{0}",
                info.BitRate is { } bitRate
                    ? $"{bitRate / 1_000d:0.#} kb/s"
                    : UiLocalization.Text("未知")),
            UiLocalization.Format("流数量：{0}", info.Streams.Count)
        };
        lines.AddRange(info.Streams.Select(stream => stream.Kind switch
        {
            MediaStreamKind.Video =>
                UiLocalization.Format(
                    "视频 #{0}：{1} · {2}×{3} · {4:0.###} fps · {5}",
                    stream.Index,
                    stream.CodecLongName ?? stream.CodecName ?? UiLocalization.Text("未知"),
                    stream.Width,
                    stream.Height,
                    stream.FrameRate,
                    stream.PixelFormat),
            MediaStreamKind.Audio =>
                UiLocalization.Format(
                    "音频 #{0}：{1} · {2} Hz · {3} 声道 · {4}",
                    stream.Index,
                    stream.CodecLongName ?? stream.CodecName ?? UiLocalization.Text("未知"),
                    stream.SampleRate,
                    stream.ChannelCount,
                    stream.Language ?? UiLocalization.Text("未标注语言")),
            MediaStreamKind.Subtitle =>
                UiLocalization.Format(
                    "字幕 #{0}：{1} · {2}",
                    stream.Index,
                    stream.CodecLongName ?? stream.CodecName ?? UiLocalization.Text("未知"),
                    stream.Language ?? UiLocalization.Text("未标注语言")),
            _ => UiLocalization.Format(
                "{0} #{1}：{2}",
                stream.Kind,
                stream.Index,
                stream.CodecLongName ?? stream.CodecName ?? UiLocalization.Text("未知"))
        }));
        return string.Join(Environment.NewLine, lines);
    }

    private async Task RefreshPlayerMarkersAsync()
    {
        var item = _playerPlayback.CurrentItem;
        PlayerMarkerSets.Clear();
        PlayerMarkers.Clear();
        MarkPlayerTimelineMarkersDirty();
        PlayerMarkerEmptyText.IsVisible = true;
        PlayerManageMarkersButton.IsEnabled = false;
        if (item is null || !_playbackOrigins.TryGetValue(item.Key, out var origin))
        {
            CancelPlayerMarkerOperation();
            PlayerMarkerEmptyText.Text = UiLocalization.Text("当前素材未导入素材库，不能保存标记");
            return;
        }

        var operation = BeginPlayerMarkerOperation();
        try
        {
            IReadOnlyList<PlayerMarkerSetOptionViewModel> options;
            Guid? preferredId = null;
            if (origin.LocalAssetId is { } localAssetId)
            {
                var sets = await _localMarkerService.ListAsync(localAssetId, operation.Token);
                options = sets.Select(set => new PlayerMarkerSetOptionViewModel(
                        set.Id,
                        UiLocalization.Format("{0} · {1:N0} 个标记", set.Name, set.MarkerCount)))
                    .ToArray();
                preferredId = sets.FirstOrDefault()?.Id;
            }
            else if (origin.CloudAsset is { } cloudAsset && _api is not null && _currentUser is not null)
            {
                var sets = await _api.ListMarkerSetsAsync(cloudAsset.Id, operation.Token);
                var ordered = sets
                    .OrderBy(set => set.IsBasedOnOldVersion ? 1 : 0)
                    .ThenBy(set => set.OwnerUserId == _currentUser.Id ? 0 : 1)
                    .ThenByDescending(set => set.UpdatedAt)
                    .ToArray();
                options = ordered.Select(set => new PlayerMarkerSetOptionViewModel(
                        set.Id,
                        UiLocalization.Format(
                            set.IsBasedOnOldVersion
                                ? "{0} · {1} · {2:N0} 个标记 · 旧版本"
                                : "{0} · {1} · {2:N0} 个标记",
                            set.OwnerDisplayName,
                            set.Name,
                            set.MarkerCount)))
                    .ToArray();
                preferredId = ordered.FirstOrDefault(set =>
                        !set.IsBasedOnOldVersion && set.OwnerUserId == _currentUser.Id)?.Id
                    ?? ordered.FirstOrDefault(set => !set.IsBasedOnOldVersion)?.Id
                    ?? ordered.FirstOrDefault()?.Id;
            }
            else if (IsTemporaryPlaybackOrigin(origin))
            {
                var temporarySet = GetOrCreateTemporaryMarkerSet(item.Key);
                options =
                [
                    new PlayerMarkerSetOptionViewModel(
                        temporarySet.Id,
                        UiLocalization.Format("临时标记 · {0:N0} 个标记", temporarySet.Markers.Count))
                ];
                preferredId = temporarySet.Id;
            }
            else
            {
                options = [];
            }

            if (!IsCurrentPlayerMarkerOperation(operation, item.Key))
            {
                return;
            }

            _suppressPlayerMarkerSetSelection = true;
            foreach (var option in options)
            {
                PlayerMarkerSets.Add(option);
            }

            PlayerMarkerSetPicker.SelectedItem = PlayerMarkerSets.FirstOrDefault(option => option.Id == preferredId)
                ?? PlayerMarkerSets.FirstOrDefault();
            _suppressPlayerMarkerSetSelection = false;
            PlayerManageMarkersButton.IsEnabled =
                origin.LocalAssetId is not null || origin.CloudAsset is not null;
            if (PlayerMarkerSetPicker.SelectedItem is PlayerMarkerSetOptionViewModel selected)
            {
                await LoadPlayerMarkerSetAsync(selected, item.Key, operation);
            }
            else
            {
                PlayerMarkerEmptyText.Text = UiLocalization.Text("当前素材还没有标记集");
                PlayerMarkerEmptyText.IsVisible = true;
            }
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (IsCurrentPlayerMarkerOperation(operation, item.Key))
            {
                PlayerMarkerEmptyText.Text = UiLocalization.Format("标记读取失败：{0}", UserMessage(exception));
                PlayerMarkerEmptyText.IsVisible = true;
            }
        }
        finally
        {
            _suppressPlayerMarkerSetSelection = false;
            FinishPlayerMarkerOperation(operation);
        }
    }

    private async void PlayerMarkerSetPicker_OnSelectionChanged(object? sender, SelectionChangedEventArgs eventArgs)
    {
        if (_suppressPlayerMarkerSetSelection ||
            PlayerMarkerSetPicker.SelectedItem is not PlayerMarkerSetOptionViewModel selected ||
            _playerPlayback.CurrentItem is not { } item)
        {
            return;
        }

        var operation = BeginPlayerMarkerOperation();
        try
        {
            await LoadPlayerMarkerSetAsync(selected, item.Key, operation);
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (IsCurrentPlayerMarkerOperation(operation, item.Key))
            {
                PlayerMarkerEmptyText.Text = UiLocalization.Format("标记读取失败：{0}", UserMessage(exception));
                PlayerMarkerEmptyText.IsVisible = true;
            }
        }
        finally
        {
            FinishPlayerMarkerOperation(operation);
        }
    }

    private async Task LoadPlayerMarkerSetAsync(
        PlayerMarkerSetOptionViewModel selected,
        string itemKey,
        PlayerMarkerOperation operation)
    {
        if (!_playbackOrigins.TryGetValue(itemKey, out var origin))
        {
            return;
        }

        IReadOnlyList<MarkerItem> markers;
        if (origin.LocalAssetId is { } localAssetId)
        {
            markers = (await _localMarkerService.GetAsync(localAssetId, selected.Id, operation.Token)).Markers;
        }
        else if (origin.CloudAsset is not null && _api is not null)
        {
            markers = (await _api.GetMarkerSetAsync(selected.Id, operation.Token)).Markers;
        }
        else if (IsTemporaryPlaybackOrigin(origin) &&
                 _temporaryPlayerMarkerSets.TryGetValue(itemKey, out var temporarySet) &&
                 temporarySet.Id == selected.Id)
        {
            markers = temporarySet.Markers.ToArray();
        }
        else
        {
            markers = [];
        }

        if (!IsCurrentPlayerMarkerOperation(operation, itemKey) ||
            PlayerMarkerSetPicker.SelectedItem is not PlayerMarkerSetOptionViewModel current ||
            current.Id != selected.Id)
        {
            return;
        }

        PlayerMarkers.Clear();
        foreach (var marker in markers.OrderBy(marker => marker.Time))
        {
            PlayerMarkers.Add(new PlayerMarkerItemViewModel(
                marker.Id,
                marker.Time,
                marker.Name,
                marker.Note));
        }

        PlayerMarkerEmptyText.Text = UiLocalization.Text("当前标记集还没有标记");
        PlayerMarkerEmptyText.IsVisible = PlayerMarkers.Count == 0;
        MarkPlayerTimelineMarkersDirty();
    }

    private void PlayerTimelineMarkersCanvas_OnSizeChanged(object? sender, SizeChangedEventArgs eventArgs) =>
        MarkPlayerTimelineMarkersDirty();

    private void MarkPlayerTimelineMarkersDirty()
    {
        _playerTimelineMarkersDirty = true;
        UpdatePlayerTimelineMarkers();
    }

    private void UpdatePlayerTimelineMarkers()
    {
        var duration = _playerPlayback.State.Duration?.TotalSeconds ?? 0;
        var width = PlayerTimelineMarkersCanvas.Bounds.Width;
        if (!_playerTimelineMarkersDirty &&
            Math.Abs(duration - _lastPlayerTimelineDuration) < 0.001 &&
            Math.Abs(width - _lastPlayerTimelineWidth) < 0.5)
        {
            return;
        }

        _playerTimelineMarkersDirty = false;
        _lastPlayerTimelineDuration = duration;
        _lastPlayerTimelineWidth = width;
        PlayerTimelineMarkersCanvas.Children.Clear();
        if (duration <= 0 || width <= 4)
        {
            return;
        }

        var availableWidth = width - 4;
        foreach (var marker in PlayerMarkers)
        {
            var tick = new Border { IsHitTestVisible = false };
            tick.Classes.Add("timelineMarker");
            Canvas.SetLeft(
                tick,
                Math.Clamp(marker.Time.TotalSeconds / duration, 0, 1) * availableWidth);
            Canvas.SetTop(tick, 0);
            PlayerTimelineMarkersCanvas.Children.Add(tick);
        }

        if (_playerPlayback.CurrentItem is { } item &&
            _playerTrimRanges.TryGetValue(item.Key, out var range))
        {
            AddPlayerTimelineRangeTick(range.InPoint, duration, availableWidth, "timelineInPoint");
            AddPlayerTimelineRangeTick(range.OutPoint, duration, availableWidth, "timelineOutPoint");
        }
    }

    private void AddPlayerTimelineRangeTick(
        TimeSpan? position,
        double durationSeconds,
        double availableWidth,
        string styleClass)
    {
        if (position is null)
        {
            return;
        }

        var tick = new Border { IsHitTestVisible = false };
        tick.Classes.Add(styleClass);
        Canvas.SetLeft(
            tick,
            Math.Clamp(position.Value.TotalSeconds / durationSeconds, 0, 1) * availableWidth);
        Canvas.SetTop(tick, 0);
        PlayerTimelineMarkersCanvas.Children.Add(tick);
    }

    private void PausePlayerWhenLeavingPage()
    {
        if (_playerPlayback.CurrentItem is null ||
            _playerPlayback.State.IsPaused ||
            _playerPlayback.State.Status is PlaybackStatus.Idle or PlaybackStatus.Paused or
                PlaybackStatus.Ended or PlaybackStatus.Failed or PlaybackStatus.Unavailable)
        {
            return;
        }

        _ = PausePlayerWhenLeavingPageAsync();
    }

    private async Task PausePlayerWhenLeavingPageAsync()
    {
        try
        {
            await _playerPlayback.PauseAsync();
        }
        catch (Exception exception)
        {
            ClientDiagnostics.WriteException("pause-player-on-navigation", exception, isFatal: false);
        }
    }

    private async void PlayerMarkerList_OnDoubleTapped(object? sender, TappedEventArgs eventArgs)
    {
        if (PlayerMarkerList.SelectedItem is not PlayerMarkerItemViewModel marker)
        {
            return;
        }

        eventArgs.Handled = true;
        ShowPlaybackResult(await _playerPlayback.SeekAsync(marker.Time), PlayerStatusText);
    }

    private async void PlayerManageMarkers_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        var item = _playerPlayback.CurrentItem;
        if (item is null || !_playbackOrigins.TryGetValue(item.Key, out var origin))
        {
            return;
        }

        if (origin.LocalAssetId is { } localAssetId && origin.LocalPath is { } localPath)
        {
            await new LocalMarkerWindow(_localMarkerService, localAssetId, item.Title, localPath)
                .ShowDialog(this);
        }
        else if (origin.CloudAsset is { } cloudAsset && _api is not null && _currentUser is not null)
        {
            await new CloudMarkerWindow(
                    _api,
                    cloudAsset,
                    _currentUser,
                    () => Task.FromResult(File.Exists(item.Source) ? item.Source : null))
                .ShowDialog(this);
        }

        await RefreshPlayerMarkersAsync();
    }

    private PlayerMarkerOperation BeginPlayerMarkerOperation()
    {
        var cancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _playerMarkerCancellation, cancellation);
        previous?.Cancel();
        return new PlayerMarkerOperation(cancellation, cancellation.Token);
    }

    private void FinishPlayerMarkerOperation(PlayerMarkerOperation operation)
    {
        Interlocked.CompareExchange(ref _playerMarkerCancellation, null, operation.Source);
        operation.Source.Dispose();
    }

    private void CancelPlayerMarkerOperation() =>
        Interlocked.Exchange(ref _playerMarkerCancellation, null)?.Cancel();

    private bool IsCurrentPlayerMarkerOperation(PlayerMarkerOperation operation, string itemKey) =>
        ReferenceEquals(_playerMarkerCancellation, operation.Source) &&
        !operation.Token.IsCancellationRequested &&
        _playerPlayback.CurrentItem?.Key == itemKey;

    private async void DetailAddMarker_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        try
        {
            await AddMarkerAtCurrentPositionAsync(_detailPlayback, useDetailSelection: true);
        }
        catch (Exception exception)
        {
            UiLocalization.SetText(DetailStatus, "标记添加失败：{0}", UserMessage(exception));
        }
    }

    private async void PlayerAddMarker_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        try
        {
            await AddMarkerAtCurrentPositionAsync(_playerPlayback, useDetailSelection: false);
        }
        catch (Exception exception)
        {
            UiLocalization.SetText(PlayerToolStatusText, "标记添加失败：{0}", UserMessage(exception));
        }
    }

    private async Task AddMarkerAtCurrentPositionAsync(
        PlaybackController controller,
        bool useDetailSelection)
    {
        var status = useDetailSelection ? DetailStatus : PlayerToolStatusText;
        var current = controller.CurrentItem;
        var state = controller.State;
        if (current is null ||
            !StringComparer.Ordinal.Equals(state.CurrentItemKey, current.Key) ||
            !_playbackOrigins.TryGetValue(current.Key, out var origin))
        {
            UiLocalization.SetText(
                status,
                "当前没有可保存标记的素材正在播放。");
            return;
        }

        var position = TimeSpan.FromSeconds(Math.Floor(Math.Max(0, state.Position.TotalSeconds)));

        if (origin.LocalAssetId is { } localAssetId && origin.LocalPath is { } localPath)
        {
            await AddLocalMarkerAsync(localAssetId, localPath, position, state.Duration);
            if (useDetailSelection && _selectedLocalAsset?.Id == localAssetId &&
                controller.CurrentItem?.Key == current.Key)
            {
                await RefreshSelectedMarkerSummaryAsync(localAssetId);
            }

            UiLocalization.SetText(
                status,
                "已在 {0} 添加本地标记。",
                FormatPlaybackTime(position));
        }
        else if (origin.CloudAsset is { } cloudAsset)
        {
            await AddCloudMarkerAsync(cloudAsset, position);
            if (useDetailSelection && _selectedCloudAsset?.Id == cloudAsset.Id &&
                controller.CurrentItem?.Key == current.Key)
            {
                await RefreshCloudMarkerSummaryAsync(cloudAsset);
            }

            UiLocalization.SetText(
                status,
                "已在 {0} 添加云端标记。",
                FormatPlaybackTime(position));
        }
        else if (IsTemporaryPlaybackOrigin(origin))
        {
            var temporarySet = GetOrCreateTemporaryMarkerSet(current.Key);
            temporarySet.Markers.Add(new MarkerItem(Guid.NewGuid(), position, null, null));
            temporarySet.Markers.Sort(static (left, right) => left.Time.CompareTo(right.Time));
            UiLocalization.SetText(
                status,
                "已在 {0} 添加临时标记；从播放队列移除文件后会自动清除。",
                FormatPlaybackTime(position));
        }
        else
        {
            UiLocalization.SetText(
                status,
                "外部媒体未导入素材库，不能保存素材标记。");
        }

        if (!useDetailSelection)
        {
            await RefreshPlayerMarkersAsync();
        }
    }

    private async Task AddLocalMarkerAsync(
        Guid assetId,
        string assetPath,
        TimeSpan position,
        TimeSpan? duration)
    {
        var sets = await _localMarkerService.ListAsync(assetId);
        var set = sets.FirstOrDefault();
        var setId = set?.Id ?? (await _localMarkerService.CreateAsync(
            assetId,
            UiLocalization.Text("我的标记"),
            duration,
            new MarkerCsvRecordingInfo(
                Path.GetFileName(assetPath),
                assetPath,
                null,
                duration is null
                    ? null
                    : TimeSpan.FromSeconds(Math.Floor(duration.Value.TotalSeconds))))).Id;
        await _localMarkerService.AddMarkerAsync(
            assetId,
            setId,
            new UpsertMarkerRequest(position, null, null));
    }

    private async Task AddCloudMarkerAsync(ApiAsset asset, TimeSpan position)
    {
        if (_api is null || _currentUser is null)
        {
            throw new InvalidOperationException("请先登录云端账号。");
        }

        var sets = await _api.ListMarkerSetsAsync(asset.Id);
        var set = sets.FirstOrDefault(item =>
            item.OwnerUserId == _currentUser.Id && !item.IsBasedOnOldVersion);
        var setId = set?.Id ?? (await _api.CreateMarkerSetAsync(
            new CreateMarkerSetRequest(
                asset.Id,
                asset.CurrentVersionId,
                UiLocalization.Text("我的标记")))).Id;
        await _api.AddMarkerAsync(setId, new UpsertMarkerRequest(position, null, null));
    }

    private async Task ReloadLutsAsync()
    {
        AvailableLuts.Clear();
        try
        {
            foreach (var lut in await _localLutLibrary.LoadAsync())
            {
                AvailableLuts.Add(new LutOptionViewModel(
                    $"local:{lut.Id:N}",
                    lut.DisplayName,
                    "本地",
                    lut.FullPath,
                    null,
                    null,
                    lut.IsAvailable));
            }
        }
        catch (Exception exception)
        {
            ClientDiagnostics.WriteException("local-lut-load", exception, isFatal: false);
            UiLocalization.SetText(PlayerToolStatusText, "LUT 加载失败：{0}", exception.Message);
        }

        if (_api is not null && _currentUser is not null)
        {
            try
            {
                var page = await _api.ListTeamLutsAsync(new ApiTeamLutListQuery(PageSize: 200));
                foreach (var lut in page.Items)
                {
                    var cachePath = CloudLutCachePath(lut.Id, lut.Version);
                    AvailableLuts.Add(new LutOptionViewModel(
                        $"cloud:{lut.Id:N}:{lut.Version}",
                        lut.Name,
                        "云端",
                        File.Exists(cachePath) ? cachePath : null,
                        lut.Id,
                        lut.Version,
                        lut.HasContent));
                }
            }
            catch (Exception exception)
            {
                UiLocalization.SetText(
                    PlayerToolStatusText,
                    "云端 LUT 加载失败：{0}",
                    exception.Message);
            }
        }

        var availableKeys = AvailableLuts.Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
        var removedPlayerKeys = _playerLutOptionKeys
            .Where(pair => !availableKeys.Contains(pair.Value))
            .Select(pair => pair.Key)
            .ToArray();
        var removedDetailKeys = _detailLutOptionKeys
            .Where(pair => !availableKeys.Contains(pair.Value))
            .Select(pair => pair.Key)
            .ToArray();
        foreach (var key in removedPlayerKeys)
        {
            _playerLutOptionKeys.Remove(key);
        }

        foreach (var key in removedDetailKeys)
        {
            _detailLutOptionKeys.Remove(key);
        }

        if (_playerPlayback.CurrentItem is { } playerItem && removedPlayerKeys.Contains(playerItem.Key))
        {
            ShowPlaybackResult(await _playerPlayback.SetLutAsync(null), PlayerToolStatusText);
        }

        if (_detailPlayback.CurrentItem is { } detailItem && removedDetailKeys.Contains(detailItem.Key))
        {
            ShowPlaybackResult(await _detailPlayback.SetLutAsync(null), DetailStatus);
        }

        SyncPlayerLutSelection();
        SyncDetailLutSelection();
    }

    private async void PlayerLutPicker_OnSelectionChanged(object? sender, SelectionChangedEventArgs eventArgs)
    {
        if (_suppressPlayerLutSelection)
        {
            return;
        }

        await ApplyLutSelectionAsync(
            isPlayer: true,
            PlayerLutPicker.SelectedItem as LutOptionViewModel);
    }

    private async void DetailLutPicker_OnSelectionChanged(object? sender, SelectionChangedEventArgs eventArgs)
    {
        if (_suppressDetailLutSelection)
        {
            return;
        }

        await ApplyLutSelectionAsync(
            isPlayer: false,
            DetailLutPicker.SelectedItem as LutOptionViewModel);
    }

    private async Task ApplyLutSelectionAsync(bool isPlayer, LutOptionViewModel? selectedLut)
        => await ApplyLutSelectionAsync(isPlayer, selectedLut, null, null);

    private async Task ApplyLutSelectionAsync(
        bool isPlayer,
        LutOptionViewModel? selectedLut,
        string? expectedItemKey,
        long? expectedDetailPreviewGeneration)
    {
        var operation = BeginLutSelectionOperation(isPlayer);
        var controller = isPlayer ? _playerPlayback : _detailPlayback;
        expectedItemKey ??= controller.CurrentItem?.Key;
        var status = isPlayer ? PlayerToolStatusText : DetailStatus;
        try
        {
            string? path = null;
            if (selectedLut is { IsAvailable: true })
            {
                path = await ResolveLutPathAsync(selectedLut, operation.CancellationToken);
            }

            if (!IsCurrentLutSelection(
                    isPlayer,
                    operation,
                    selectedLut,
                    expectedItemKey,
                    expectedDetailPreviewGeneration))
            {
                return;
            }

            if (expectedItemKey is null)
            {
                return;
            }

            var result = await controller.SetLutAsync(
                path,
                expectedItemKey,
                operation.CancellationToken);
            if (IsCurrentLutSelection(
                    isPlayer,
                    operation,
                    selectedLut,
                    expectedItemKey,
                    expectedDetailPreviewGeneration))
            {
                ShowPlaybackResult(result, status);
                if (result.Succeeded && controller.CurrentItem is { } current)
                {
                    var selections = isPlayer ? _playerLutOptionKeys : _detailLutOptionKeys;
                    if (selectedLut is null)
                    {
                        selections.Remove(current.Key);
                    }
                    else
                    {
                        selections[current.Key] = selectedLut.Key;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (IsCurrentLutSelection(
                    isPlayer,
                    operation,
                    selectedLut,
                    expectedItemKey,
                    expectedDetailPreviewGeneration))
            {
                UiLocalization.SetText(status, "LUT 加载失败：{0}", UserMessage(exception));
            }
        }
        finally
        {
            FinishLutSelectionOperation(isPlayer, operation);
        }
    }

    private LutSelectionOperation BeginLutSelectionOperation(bool isPlayer)
    {
        var cancellation = new CancellationTokenSource();
        if (isPlayer)
        {
            Interlocked.Exchange(ref _playerLutCancellation, cancellation)?.Cancel();
            return new LutSelectionOperation(
                ++_playerLutGeneration,
                cancellation,
                cancellation.Token);
        }

        Interlocked.Exchange(ref _detailLutCancellation, cancellation)?.Cancel();
        return new LutSelectionOperation(
            ++_detailLutGeneration,
            cancellation,
            cancellation.Token);
    }

    private void FinishLutSelectionOperation(bool isPlayer, LutSelectionOperation operation)
    {
        if (isPlayer)
        {
            Interlocked.CompareExchange(ref _playerLutCancellation, null, operation.Source);
        }
        else
        {
            Interlocked.CompareExchange(ref _detailLutCancellation, null, operation.Source);
        }

        operation.Source.Dispose();
    }

    private void CancelDetailLutSelection()
    {
        Interlocked.Exchange(ref _detailLutCancellation, null)?.Cancel();
        _detailLutGeneration++;
    }

    private void CancelDetailAudioTrackSelection()
    {
        var cancellation = Interlocked.Exchange(ref _detailAudioTrackCancellation, null);
        cancellation?.Cancel();
        Interlocked.Increment(ref _detailAudioTrackGeneration);
    }

    private bool IsCurrentLutSelection(
        bool isPlayer,
        LutSelectionOperation operation,
        LutOptionViewModel? selectedLut,
        string? expectedItemKey,
        long? expectedDetailPreviewGeneration)
    {
        var generation = isPlayer ? _playerLutGeneration : _detailLutGeneration;
        var currentSelection = isPlayer ? PlayerLutPicker.SelectedItem : DetailLutPicker.SelectedItem;
        return operation.Generation == generation &&
               !operation.CancellationToken.IsCancellationRequested &&
               ReferenceEquals(currentSelection, selectedLut) &&
               StringComparer.Ordinal.Equals(
                   (isPlayer ? _playerPlayback : _detailPlayback).CurrentItem?.Key,
                   expectedItemKey) &&
               (isPlayer || expectedDetailPreviewGeneration is null ||
                expectedDetailPreviewGeneration == _detailPreviewGeneration);
    }

    private async Task<string> ResolveLutPathAsync(
        LutOptionViewModel lut,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(lut.LocalPath) && File.Exists(lut.LocalPath))
        {
            return lut.LocalPath;
        }

        if (lut.CloudId is not { } cloudId || lut.CloudVersion is not { } cloudVersion || _api is null)
        {
            throw new FileNotFoundException(
                UiLocalization.Text("LUT 原文件当前不可用。"),
                lut.LocalPath);
        }

        Directory.CreateDirectory(AppPaths.CloudLutDownloadDirectory);
        var targetPath = CloudLutCachePath(cloudId, cloudVersion);
        if (File.Exists(targetPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return targetPath;
        }

        var temporaryPath = $"{targetPath}.{Guid.NewGuid():N}.download.cube";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous))
            {
                await _api.DownloadTeamLutAsync(cloudId, stream, cancellationToken);
            }

            var validation = await new CubeLutValidator().ValidateFileAsync(temporaryPath, cancellationToken);
            if (!validation.IsValid)
            {
                throw new InvalidDataException(validation.Diagnostic);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, targetPath, true);
            return targetPath;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string CloudLutCachePath(Guid lutId, int version) =>
        Path.Combine(AppPaths.CloudLutDownloadDirectory, $"{lutId:N}-v{version}.cube");

    private void SyncPlayerLutSelection()
    {
        var optionKey = _playerPlayback.CurrentItem is { } item
            ? _playerLutOptionKeys.GetValueOrDefault(item.Key)
            : null;
        _suppressPlayerLutSelection = true;
        PlayerLutPicker.SelectedItem = optionKey is null
            ? null
            : AvailableLuts.FirstOrDefault(option => option.Key == optionKey);
        _suppressPlayerLutSelection = false;
    }

    private void SyncDetailLutSelection()
    {
        var optionKey = _detailPlayback.CurrentItem is { } item
            ? _detailLutOptionKeys.GetValueOrDefault(item.Key)
            : null;
        _suppressDetailLutSelection = true;
        DetailLutPicker.SelectedItem = optionKey is null
            ? null
            : AvailableLuts.FirstOrDefault(option => option.Key == optionKey);
        _suppressDetailLutSelection = false;
    }

    private async void ClearPlayerLut_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        var wasAlreadyClear = PlayerLutPicker.SelectedItem is null;
        PlayerLutPicker.SelectedItem = null;
        if (wasAlreadyClear)
        {
            await ApplyLutSelectionAsync(isPlayer: true, selectedLut: null);
        }

        PlayerExportWithLutToggle.IsChecked = false;
        UiLocalization.SetText(PlayerToolStatusText, "已清除当前素材的预览 LUT。");
    }

    private async void ClearDetailLut_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        var wasAlreadyClear = DetailLutPicker.SelectedItem is null;
        DetailLutPicker.SelectedItem = null;
        if (wasAlreadyClear)
        {
            await ApplyLutSelectionAsync(isPlayer: false, selectedLut: null);
        }
    }

    private async void ManageLuts_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        try
        {
            await CreateLutLibraryWindow()
                .ShowDialog(this);
            await ReloadLutsAsync();
        }
        catch (Exception exception)
        {
            UiLocalization.SetText(PlayerToolStatusText, "LUT 加载失败：{0}", exception.Message);
        }
    }

    private void PlayerSetInPoint_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (!TryGetPlayerRangeContext(out var item, out var position))
        {
            return;
        }

        var current = _playerTrimRanges.GetValueOrDefault(item.Key);
        var outPoint = current?.OutPoint > position ? current.OutPoint : null;
        _playerTrimRanges[item.Key] = new PlayerTrimRange(position, outPoint);
        UpdatePlayerTrimUi();
        if (outPoint is null && current?.OutPoint is not null)
        {
            UiLocalization.SetText(PlayerToolStatusText, "已设置入点；原出点不晚于新入点，已清除出点。");
        }
        else
        {
            UiLocalization.SetText(PlayerToolStatusText, "已设置入点：{0}", FormatTrimTime(position));
        }
    }

    private void PlayerSetOutPoint_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (!TryGetPlayerRangeContext(out var item, out var position))
        {
            return;
        }

        var current = _playerTrimRanges.GetValueOrDefault(item.Key);
        if (current?.InPoint is { } inPoint && position <= inPoint)
        {
            UiLocalization.SetText(PlayerToolStatusText, "出点必须晚于入点。");
            return;
        }

        _playerTrimRanges[item.Key] = new PlayerTrimRange(current?.InPoint, position);
        UpdatePlayerTrimUi();
        UiLocalization.SetText(PlayerToolStatusText, "已设置出点：{0}", FormatTrimTime(position));
    }

    private void PlayerClearRange_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_playerPlayback.CurrentItem is { } item)
        {
            _playerTrimRanges.Remove(item.Key);
        }

        UpdatePlayerTrimUi();
        UiLocalization.SetText(PlayerToolStatusText, "已清除当前素材的裁剪范围。");
    }

    private bool TryGetPlayerRangeContext(out PlaybackItem item, out TimeSpan position)
    {
        item = _playerPlayback.CurrentItem!;
        position = default;
        if (item is null || item.MediaType is not (LocalMediaType.Audio or LocalMediaType.Video))
        {
            UiLocalization.SetText(PlayerToolStatusText, "请先播放视频或音频素材。");
            return false;
        }

        position = TimeSpan.FromMilliseconds(Math.Max(
            0,
            Math.Round(_playerPlayback.State.Position.TotalMilliseconds)));
        return true;
    }

    private void UpdatePlayerTrimUi()
    {
        var item = _playerPlayback.CurrentItem;
        var range = item is null ? null : _playerTrimRanges.GetValueOrDefault(item.Key);
        PlayerRangeText.Text = UiLocalization.Format(
            "入点 {0} · 出点 {1}",
            range?.InPoint is { } inPoint ? FormatTrimTime(inPoint) : "--:--",
            range?.OutPoint is { } outPoint ? FormatTrimTime(outPoint) : "--:--");
        if (_playerExportUiKey != item?.Key)
        {
            _playerExportUiKey = item?.Key;
            PlayerExportWithLutToggle.IsChecked = false;
        }

        MarkPlayerTimelineMarkersDirty();
    }

    private static string FormatTrimTime(TimeSpan value)
    {
        var hours = (long)Math.Floor(value.TotalHours);
        return $"{hours:00}:{value.Minutes:00}:{value.Seconds:00}.{value.Milliseconds:000}";
    }

    private async void ExportPlayerMedia_OnClick(object? sender, RoutedEventArgs eventArgs) =>
        await RunGuardedInteractionAsync(
            "export-player-media",
            ExportPlayerMediaAsync,
            PlayerToolStatusText);

    private async Task ExportPlayerMediaAsync()
    {
        var item = _playerPlayback.CurrentItem;
        if (item is null || item.MediaType is not (LocalMediaType.Audio or LocalMediaType.Video))
        {
            UiLocalization.SetText(PlayerToolStatusText, "请先播放视频或音频素材。");
            return;
        }

        var inputPath = item.Source;
        if (!File.Exists(inputPath))
        {
            UiLocalization.SetText(PlayerToolStatusText, "原文件尚未下载到本机，请先下载后再裁剪或导出。");
            return;
        }

        var range = _playerTrimRanges.GetValueOrDefault(item.Key);
        var hasInPoint = range?.InPoint is not null;
        var hasOutPoint = range?.OutPoint is not null;
        if (hasInPoint != hasOutPoint)
        {
            UiLocalization.SetText(PlayerToolStatusText, "裁剪需要同时设置入点和出点；也可以清除范围后导出完整原文件。");
            return;
        }

        if (range is { InPoint: { } inPoint, OutPoint: { } outPoint } && outPoint <= inPoint)
        {
            UiLocalization.SetText(PlayerToolStatusText, "出点必须晚于入点。");
            return;
        }

        var hasRange = hasInPoint && hasOutPoint;
        var applyLut = item.MediaType == LocalMediaType.Video &&
                       PlayerExportWithLutToggle.IsChecked == true;
        if (!hasRange && !applyLut)
        {
            await ExportPlayerOriginalCopyAsync(inputPath);
            return;
        }

        if (item.MediaType == LocalMediaType.Audio)
        {
            await ExportPlayerAudioTrimAsync(item, inputPath, range!);
            return;
        }

        await ExportPlayerVideoAsync(item, inputPath, range, applyLut);
    }

    private async Task ExportPlayerOriginalCopyAsync(string inputPath)
    {
        var extension = Path.GetExtension(inputPath);
        var copySuffix = UiLocalization.Text("副本");
        var target = await RunNativePickerAsync(
            "export-original-copy",
            () => StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = UiLocalization.Text("导出原文件副本"),
                SuggestedFileName = $"{Path.GetFileNameWithoutExtension(inputPath)}-{copySuffix}{extension}",
                DefaultExtension = extension.TrimStart('.'),
                FileTypeChoices =
                [
                    new FilePickerFileType(UiLocalization.Text("原文件格式"))
                    {
                        Patterns = [$"*{extension}"]
                    }
                ]
            }),
            PlayerToolStatusText);
        if (target is null)
        {
            return;
        }

        try
        {
            var outputPath = target.Path.LocalPath;
            if (PathsEqualForExport(inputPath, outputPath))
            {
                UiLocalization.SetText(PlayerToolStatusText, "导出位置不能覆盖正在播放的原文件。");
                return;
            }

            UiLocalization.SetText(PlayerToolStatusText, "正在复制原文件，媒体内容不会重新编码…");
            await Task.Run(() => File.Copy(inputPath, outputPath, overwrite: true));
            UiLocalization.SetText(PlayerToolStatusText, "已导出原文件副本：{0}", outputPath);
        }
        catch (Exception exception)
        {
            UiLocalization.SetText(PlayerToolStatusText, "导出失败：{0}", UserMessage(exception));
        }
    }

    private async Task ExportPlayerAudioTrimAsync(
        PlaybackItem item,
        string inputPath,
        PlayerTrimRange range)
    {
        var preset = await new AudioExportPresetWindow().ShowDialog<AudioExportPreset?>(this);
        if (preset is null || range.InPoint is not { } inPoint || range.OutPoint is not { } outPoint)
        {
            return;
        }

        var extension = preset == AudioExportPreset.Flac ? "flac" : "wav";
        var clipSuffix = UiLocalization.Text("片段");
        var target = await RunNativePickerAsync(
            "export-audio-trim",
            () => StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = UiLocalization.Text("导出音频片段"),
                SuggestedFileName = $"{Path.GetFileNameWithoutExtension(item.Source)}-{clipSuffix}.{extension}",
                DefaultExtension = extension,
                FileTypeChoices =
                [
                    new FilePickerFileType(extension.ToUpperInvariant())
                    {
                        Patterns = [$"*.{extension}"]
                    }
                ]
            }),
            PlayerToolStatusText);
        if (target is null)
        {
            return;
        }

        var ffmpeg = _mediaThumbnailService.FfmpegAvailability;
        if (!ffmpeg.IsAvailable)
        {
            PlayerToolStatusText.Text = ffmpeg.Diagnostic ?? UiLocalization.Text("FFmpeg 不可用。");
            return;
        }

        try
        {
            var command = FfmpegCommandBuilder.BuildAudioTrimExport(
                inputPath,
                target.Path.LocalPath,
                inPoint,
                outPoint,
                preset.Value,
                ffmpeg.ResolvedExecutable!);
            await RunPlayerExportAsync(command, target.Path.LocalPath);
        }
        catch (Exception exception)
        {
            UiLocalization.SetText(PlayerToolStatusText, "导出失败：{0}", UserMessage(exception));
        }
    }

    private async Task ExportPlayerVideoAsync(
        PlaybackItem item,
        string inputPath,
        PlayerTrimRange? range,
        bool applyLut)
    {
        string? lutPath = null;
        if (applyLut)
        {
            if (PlayerLutPicker.SelectedItem is not LutOptionViewModel { IsAvailable: true } selectedLut)
            {
                UiLocalization.SetText(PlayerToolStatusText, "已开启套 LUT 导出，请先为当前视频选择可用的 LUT。");
                return;
            }

            try
            {
                lutPath = await ResolveLutPathAsync(selectedLut, CancellationToken.None);
            }
            catch (Exception exception)
            {
                UiLocalization.SetText(PlayerToolStatusText, "LUT 加载失败：{0}", UserMessage(exception));
                return;
            }
        }

        var preset = await new LutExportPresetWindow().ShowDialog<LutExportPreset?>(this);
        if (preset is null)
        {
            return;
        }

        var extension = preset == LutExportPreset.ProRes422Hq ? "mov" : "mp4";
        var suffix = range is null
            ? (applyLut ? "LUT" : UiLocalization.Text("导出"))
            : UiLocalization.Text("片段");
        var target = await RunNativePickerAsync(
            "export-video",
            () => StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = UiLocalization.Text("导出视频"),
                SuggestedFileName = $"{Path.GetFileNameWithoutExtension(item.Source)}-{suffix}.{extension}",
                DefaultExtension = extension,
                FileTypeChoices =
                [
                    new FilePickerFileType(preset == LutExportPreset.ProRes422Hq ? "ProRes MOV" : "H.264 MP4")
                    {
                        Patterns = [$"*.{extension}"]
                    }
                ]
            }),
            PlayerToolStatusText);
        if (target is null)
        {
            return;
        }

        var ffmpeg = _mediaThumbnailService.FfmpegAvailability;
        if (!ffmpeg.IsAvailable)
        {
            PlayerToolStatusText.Text = ffmpeg.Diagnostic ?? UiLocalization.Text("FFmpeg 不可用。");
            return;
        }

        try
        {
            var command = FfmpegCommandBuilder.BuildVideoExport(
                inputPath,
                target.Path.LocalPath,
                range?.InPoint,
                range?.OutPoint,
                lutPath,
                preset.Value,
                ffmpeg.ResolvedExecutable!);
            await RunPlayerExportAsync(command, target.Path.LocalPath);
        }
        catch (Exception exception)
        {
            UiLocalization.SetText(PlayerToolStatusText, "导出失败：{0}", UserMessage(exception));
        }
    }

    private async Task RunPlayerExportAsync(MediaToolCommand command, string outputPath)
    {
        UiLocalization.SetText(PlayerToolStatusText, "正在导出，原文件不会被修改…");
        var result = await new ProcessMediaToolRunner().RunAsync(command);
        UiLocalization.SetText(
            PlayerToolStatusText,
            result.Succeeded ? "已导出：{0}" : "导出失败：{0}",
            result.Succeeded ? outputPath : result.StandardError.Trim());
    }

    private static bool PathsEqualForExport(string first, string second) =>
        string.Equals(
            Path.GetFullPath(first),
            Path.GetFullPath(second),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private async void ExportPlayerWithLut_OnClick(object? sender, RoutedEventArgs eventArgs) =>
        await RunGuardedInteractionAsync(
            "export-player-with-lut",
            () => ExportWithLutAsync(
                _playerPlayback.CurrentItem,
                PlayerLutPicker.SelectedItem as LutOptionViewModel,
                PlayerToolStatusText),
            PlayerToolStatusText);

    private async void ExportDetailWithLut_OnClick(object? sender, RoutedEventArgs eventArgs) =>
        await RunGuardedInteractionAsync(
            "export-detail-with-lut",
            () => ExportWithLutAsync(
                _detailPlayback.CurrentItem,
                DetailLutPicker.SelectedItem as LutOptionViewModel,
                DetailStatus),
            DetailStatus);

    private async Task ExportWithLutAsync(
        PlaybackItem? item,
        LutOptionViewModel? lut,
        TextBlock status)
    {
        if (item is null || item.MediaType != LocalMediaType.Video || lut is not { IsAvailable: true })
        {
            UiLocalization.SetText(status, "请先播放视频并选择可用的 LUT。");
            return;
        }

        string lutPath;
        try
        {
            lutPath = await ResolveLutPathAsync(lut, CancellationToken.None);
        }
        catch (Exception exception)
        {
            UiLocalization.SetText(status, "LUT 加载失败：{0}", UserMessage(exception));
            return;
        }

        var preset = await new LutExportPresetWindow().ShowDialog<LutExportPreset?>(this);
        if (preset is null)
        {
            return;
        }

        var extension = preset == LutExportPreset.ProRes422Hq ? "mov" : "mp4";
        var target = await RunNativePickerAsync(
            "export-with-lut",
            () => StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = UiLocalization.Text("套 LUT 导出"),
                SuggestedFileName = $"{Path.GetFileNameWithoutExtension(item.Source)}-LUT.{extension}",
                DefaultExtension = extension,
                FileTypeChoices =
                [
                    new FilePickerFileType(preset == LutExportPreset.ProRes422Hq ? "ProRes MOV" : "H.264 MP4")
                    {
                        Patterns = [$"*.{extension}"]
                    }
                ]
            }),
            status);
        if (target is null)
        {
            return;
        }

        var ffmpeg = _mediaThumbnailService.FfmpegAvailability;
        if (!ffmpeg.IsAvailable)
        {
            if (ffmpeg.Diagnostic is not null)
            {
                status.Text = ffmpeg.Diagnostic;
            }
            else
            {
                UiLocalization.SetText(status, "FFmpeg 不可用。");
            }
            return;
        }

        var inputPath = item.Source;
        if (_playbackOrigins.TryGetValue(item.Key, out var origin) && origin.CloudAsset is { } cloudAsset)
        {
            try
            {
                UiLocalization.SetText(status, "正在准备云端原文件…");
                inputPath = await EnsureCloudAssetDownloadedAsync(cloudAsset)
                    ?? throw new InvalidOperationException("Cloud original is unavailable.");
            }
            catch (OperationCanceledException)
            {
                UiLocalization.SetText(status, "已取消下载。");
                return;
            }
            catch (Exception exception)
            {
                UiLocalization.SetText(status, "下载失败：{0}", UserMessage(exception));
                return;
            }
        }

        try
        {
            var command = FfmpegCommandBuilder.BuildLutExport(
                inputPath,
                lutPath,
                target.Path.LocalPath,
                preset.Value,
                ffmpeg.ResolvedExecutable!);
            UiLocalization.SetText(status, "正在导出，原文件不会被修改…");
            var result = await new ProcessMediaToolRunner().RunAsync(command);
            UiLocalization.SetText(
                status,
                result.Succeeded ? "已导出：{0}" : "导出失败：{0}",
                result.Succeeded ? target.Path.LocalPath : result.StandardError.Trim());
        }
        catch (Exception exception)
        {
            UiLocalization.SetText(status, "导出失败：{0}", UserMessage(exception));
        }
    }

    private async void RecheckMediaRuntime_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        var result = GetMediaRuntimeStatus();
        ApplyMediaRuntimeStatus(result);
        await new MessageDialogWindow(
                UiLocalization.Text("媒体运行库检查"),
                UiLocalization.Format(
                    "libmpv：{0}\n{1}\n\nFFmpeg：{2}\n{3}\n\nffprobe：{4}\n{5}",
                    UiLocalization.Text(result.Mpv.IsAvailable ? "可用" : "缺失"),
                    RuntimeLocationOrDiagnostic(result.Mpv.NativeLibrary, result.Mpv.Diagnostic),
                    UiLocalization.Text(result.Ffmpeg.IsAvailable ? "可用" : "缺失"),
                    RuntimeLocationOrDiagnostic(result.Ffmpeg.ResolvedExecutable, result.Ffmpeg.Diagnostic),
                    UiLocalization.Text(result.Ffprobe.IsAvailable ? "可用" : "缺失"),
                    RuntimeLocationOrDiagnostic(result.Ffprobe.ResolvedExecutable, result.Ffprobe.Diagnostic)))
            .ShowDialog(this);
    }

    private void UpdateMediaRuntimeStatus()
    {
        ApplyMediaRuntimeStatus(GetMediaRuntimeStatus());
    }

    private MediaRuntimeStatus GetMediaRuntimeStatus() => new(
        _playerPlayback.Availability,
        _mediaThumbnailService.FfmpegAvailability,
        MediaToolLocator.Locate(ResolveRuntimePath(
            "INTERNAL_ASSET_LIBRARY_FFPROBE",
            AppPaths.FfprobePath)));

    private void ApplyMediaRuntimeStatus(MediaRuntimeStatus result)
    {
        MediaRuntimeStatusText.Text = UiLocalization.Format(
            "libmpv {0} · FFmpeg {1} · ffprobe {2}",
            UiLocalization.Text(result.Mpv.IsAvailable ? "可用" : "缺失"),
            UiLocalization.Text(result.Ffmpeg.IsAvailable ? "可用" : "缺失"),
            UiLocalization.Text(result.Ffprobe.IsAvailable ? "可用" : "缺失"));
    }

    private static string RuntimeLocationOrDiagnostic(string? location, string? diagnostic) =>
        !string.IsNullOrWhiteSpace(location)
            ? UiLocalization.Format("位置：{0}", location)
            : UiLocalization.Format("说明：{0}", string.IsNullOrWhiteSpace(diagnostic)
                ? UiLocalization.Text("未找到可用运行库")
                : diagnostic);

    private async void ConfigureDefaultPlayer_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        try
        {
            var service = new WindowsDefaultPlayerRegistrationService();
            service.RegisterAndOpenDefaultAppsSettings();
        }
        catch (Exception exception)
        {
            await new MessageDialogWindow(
                    UiLocalization.Text("配置默认播放器"),
                    exception.Message)
                .ShowDialog(this);
        }
    }

    private static async Task ShowPlaybackResultAsync(
        Task<PlaybackOperationResult> operation,
        TextBlock status)
    {
        ShowPlaybackResult(await operation, status);
    }

    private static void ShowPlaybackResult(PlaybackOperationResult result, TextBlock status)
    {
        if (!result.Succeeded)
        {
            if (result.Diagnostic is not null)
            {
                status.Text = result.Diagnostic;
            }
            else
            {
                UiLocalization.SetText(status, "播放操作失败。");
            }
        }
    }

    private void RefreshPlaybackLocalization()
    {
        UpdatePlayerPlaybackUi(_playerPlayback.State);
        UpdateMediaRuntimeStatus();
        SyncPlayerQueue();
    }

    private sealed record MediaRuntimeStatus(
        PlaybackEngineAvailability Mpv,
        MediaToolAvailability Ffmpeg,
        MediaToolAvailability Ffprobe);

    private sealed record PlaybackOrigin(Guid? LocalAssetId, string? LocalPath, ApiAsset? CloudAsset);

    private static bool IsTemporaryPlaybackOrigin(PlaybackOrigin origin) =>
        origin.LocalAssetId is null && origin.CloudAsset is null && origin.LocalPath is not null;

    private TemporaryMarkerSet GetOrCreateTemporaryMarkerSet(string itemKey)
    {
        if (!_temporaryPlayerMarkerSets.TryGetValue(itemKey, out var markerSet))
        {
            markerSet = new TemporaryMarkerSet(Guid.NewGuid(), []);
            _temporaryPlayerMarkerSets[itemKey] = markerSet;
        }

        return markerSet;
    }

    private void ClearTemporaryMarkersExcept(IReadOnlyCollection<string> retainedKeys)
    {
        var retained = retainedKeys.ToHashSet(StringComparer.Ordinal);
        var removedKeys = _temporaryPlayerMarkerSets.Keys
            .Concat(_playbackOrigins
                .Where(pair => IsTemporaryPlaybackOrigin(pair.Value))
                .Select(pair => pair.Key))
            .Where(key => !retained.Contains(key))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        foreach (var key in removedKeys)
        {
            ClearTemporaryMarkerState(key);
        }
    }

    private void ClearTemporaryMarkerState(string itemKey)
    {
        _temporaryPlayerMarkerSets.Remove(itemKey);
        if (_playbackOrigins.TryGetValue(itemKey, out var origin) && IsTemporaryPlaybackOrigin(origin))
        {
            _playbackOrigins.Remove(itemKey);
        }
    }

    private readonly record struct DetailPreviewOperation(
        long Generation,
        CancellationTokenSource Source,
        CancellationToken CancellationToken);

    private readonly record struct LutSelectionOperation(
        long Generation,
        CancellationTokenSource Source,
        CancellationToken CancellationToken);

    private readonly record struct PlayerMarkerOperation(
        CancellationTokenSource Source,
        CancellationToken Token);

    private sealed record PlayerTrimRange(TimeSpan? InPoint, TimeSpan? OutPoint);

    private sealed record TemporaryMarkerSet(Guid Id, List<MarkerItem> Markers);

    private enum DetailPreviewMode
    {
        None,
        Hover,
        Selected
    }
}
