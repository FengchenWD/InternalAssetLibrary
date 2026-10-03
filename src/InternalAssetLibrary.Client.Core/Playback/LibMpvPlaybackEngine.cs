using System.Globalization;
using System.Runtime.InteropServices;
using InternalAssetLibrary.Client.Core.MediaAnalysis;

namespace InternalAssetLibrary.Client.Core.Playback;

public sealed record LibMpvEngineOptions(
    string? NativeLibraryPath = null,
    bool EnableHardwareDecoding = true);

public sealed record PlaybackEngineCreationResult(
    IPlaybackEngine Engine,
    PlaybackEngineAvailability Availability);

public static class LibMpvRuntime
{
    public static PlaybackEngineAvailability Probe(string? nativeLibraryPath = null)
    {
        LibMpvNativeLibrary.TryLoad(nativeLibraryPath, out var library, out var availability);
        library?.Dispose();
        return availability;
    }

    public static PlaybackEngineCreationResult Create(LibMpvEngineOptions? options = null)
    {
        options ??= new LibMpvEngineOptions();
        if (!LibMpvNativeLibrary.TryLoad(
                options.NativeLibraryPath,
                out var library,
                out var availability))
        {
            return new PlaybackEngineCreationResult(new UnavailablePlaybackEngine(availability), availability);
        }

        try
        {
            var engine = new LibMpvPlaybackEngine(library!, availability, options);
            return new PlaybackEngineCreationResult(engine, availability);
        }
        catch (Exception exception)
        {
            library!.Dispose();
            var failed = new PlaybackEngineAvailability(
                false,
                PlaybackEngineUnavailableReason.InitializationFailed,
                exception.Message,
                availability.NativeLibrary);
            return new PlaybackEngineCreationResult(new UnavailablePlaybackEngine(failed), failed);
        }
    }
}

public sealed class UnavailablePlaybackEngine(PlaybackEngineAvailability availability) : IPlaybackEngine
{
    public PlaybackEngineAvailability Availability { get; } = availability.IsAvailable
        ? throw new ArgumentException("An unavailable engine needs an unavailable state.", nameof(availability))
        : availability;

    public PlaybackEngineState State => PlaybackEngineState.Unavailable(Availability.Diagnostic);

    public event EventHandler<PlaybackEngineStateChangedEventArgs>? StateChanged
    {
        add { }
        remove { }
    }

    public Task SetVideoHostAsync(nint nativeWindowHandle, CancellationToken cancellationToken = default) =>
        Task.FromException(CreateException());

    public Task LoadAsync(PlaybackItem item, TimeSpan startPosition, CancellationToken cancellationToken = default) =>
        Task.FromException(CreateException());

    public Task PlayAsync(CancellationToken cancellationToken = default) => Task.FromException(CreateException());

    public Task PauseAsync(CancellationToken cancellationToken = default) => Task.FromException(CreateException());

    public Task StopAsync(CancellationToken cancellationToken = default) => Task.FromException(CreateException());

    public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default) =>
        Task.FromException(CreateException());

    public Task SetVolumeAsync(double volume, CancellationToken cancellationToken = default) =>
        Task.FromException(CreateException());

    public Task SetMutedAsync(bool muted, CancellationToken cancellationToken = default) =>
        Task.FromException(CreateException());

    public Task SetSpeedAsync(double speed, CancellationToken cancellationToken = default) =>
        Task.FromException(CreateException());

    public Task StepFrameAsync(bool forward, CancellationToken cancellationToken = default) =>
        Task.FromException(CreateException());

    public Task TakeScreenshotAsync(string outputPath, CancellationToken cancellationToken = default) =>
        Task.FromException(CreateException());

    public Task SetLutAsync(string? cubePath, CancellationToken cancellationToken = default) =>
        Task.FromException(CreateException());

    public Task AddSubtitleAsync(string subtitlePath, CancellationToken cancellationToken = default) =>
        Task.FromException(CreateException());

    public Task<IReadOnlyList<PlaybackAudioTrack>> GetAudioTracksAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromException<IReadOnlyList<PlaybackAudioTrack>>(CreateException());

    public Task SetSelectedAudioTracksAsync(
        IReadOnlyCollection<int> trackIds,
        CancellationToken cancellationToken = default) =>
        Task.FromException(CreateException());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private InvalidOperationException CreateException() =>
        new(Availability.Diagnostic ?? "The libmpv playback runtime is unavailable.");
}

public sealed class LibMpvPlaybackEngine : IPlaybackEngine
{
    private readonly LibMpvNativeLibrary _native;
    private readonly nint _player;
    private readonly CancellationTokenSource _eventCancellation = new();
    private Task? _eventTask;
    private readonly object _sync = new();
    private PlaybackEngineState _state = PlaybackEngineState.Idle;
    private PlaybackItem? _currentItem;
    private string? _currentLutVideoFilter;
    private string? _currentLutReadbackToken;
    private nint _videoHost;
    private bool _muted;
    private bool _initializationAttempted;
    private bool _initialized;
    private bool _disposed;

    internal LibMpvPlaybackEngine(
        LibMpvNativeLibrary native,
        PlaybackEngineAvailability availability,
        LibMpvEngineOptions options)
    {
        _native = native;
        Availability = availability;
        _player = _native.Create();
        if (_player == 0)
        {
            throw new InvalidOperationException("libmpv could not create a playback context.");
        }

        try
        {
            SetOption("config", "no");
            SetOption("terminal", "no");
            SetOption("idle", "yes");
            SetOption("keep-open", "no");
            SetOption("image-display-duration", "inf");
            // Copy-back keeps GPU decoding while making hardware frames available to LUT/video filters.
            SetOption("hwdec", options.EnableHardwareDecoding ? "auto-copy-safe" : "no");
        }
        catch
        {
            _native.TerminateDestroy(_player);
            throw;
        }
    }

    public PlaybackEngineAvailability Availability { get; }

    public PlaybackEngineState State
    {
        get
        {
            lock (_sync)
            {
                if (_disposed || !_initialized)
                {
                    return _state;
                }

                _state = QueryState(_state.Status, _state.Error);
                return _state;
            }
        }
    }

    public event EventHandler<PlaybackEngineStateChangedEventArgs>? StateChanged;

    public Task SetVideoHostAsync(nint nativeWindowHandle, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (nativeWindowHandle == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(nativeWindowHandle));
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_initialized)
            {
                if (_videoHost == nativeWindowHandle)
                {
                    return Task.CompletedTask;
                }

                throw new InvalidOperationException("The native video host is already bound.");
            }

            if (_initializationAttempted)
            {
                throw new InvalidOperationException("libmpv initialization has already been attempted.");
            }

            _initializationAttempted = true;
            SetOption("wid", FormatNativeWindowHandle(nativeWindowHandle));
            ThrowIfError(_native.Initialize(_player), "initialize libmpv");
            _videoHost = nativeWindowHandle;
            _initialized = true;
            SetPropertyCore("volume", _state.Volume.ToString("0.###", CultureInfo.InvariantCulture));
            SetPropertyCore("mute", _muted ? "yes" : "no");
            SetPropertyCore("speed", _state.Speed.ToString("0.###", CultureInfo.InvariantCulture));
            ApplyCurrentLutFilterCore();
            _eventTask = Task.Run(() => PumpEventsAsync(_eventCancellation.Token));
        }

        return Task.CompletedTask;
    }

    public Task LoadAsync(
        PlaybackItem item,
        TimeSpan startPosition,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(item);
        if (startPosition < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(startPosition));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var normalized = item.ValidateAndNormalize();

        var arguments = BuildLoadFileCommand(normalized.Source, startPosition);

        int loadError;
        lock (_sync)
        {
            EnsureInitialized();
            var headers = normalized.HttpHeaders ?? [];
            SetPropertyCore("http-header-fields", string.Join(',', headers.Select(header => $"{header.Name}: {header.Value}")));
            SetPropertyCore("lavfi-complex", string.Empty);
            SetPropertyCore("aid", "auto");
            // pause is a persistent mpv property. A hover preview may have paused the
            // previous file, so every replacement load must explicitly resume playback.
            SetPropertyCore("pause", "no");
            _currentItem = normalized;
            _state = new PlaybackEngineState(
                PlaybackStatus.Loading,
                normalized.Key,
                normalized.Title,
                normalized.ShouldRenderVideo,
                false,
                startPosition,
                null,
                _state.Volume,
                _state.Speed,
                null);
            loadError = _native.Command(_player, arguments);
        }

        if (loadError < 0)
        {
            var diagnostic = $"Failed to load media: {_native.GetErrorText(loadError)}";
            UpdateStateAndRaise(PlaybackStatus.Failed, diagnostic);
            throw new InvalidOperationException(diagnostic);
        }

        return Task.CompletedTask;
    }

    public Task PlayAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SetProperty("pause", "no");
        UpdateStateAndRaise(PlaybackStatus.Playing, null);
        return Task.CompletedTask;
    }

    public Task PauseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SetProperty("pause", "yes");
        UpdateStateAndRaise(PlaybackStatus.Paused, null);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ExecuteCommand(["stop"], "stop playback");
        lock (_sync)
        {
            _currentItem = null;
        }

        UpdateStateAndRaise(PlaybackStatus.Idle, null);
        return Task.CompletedTask;
    }

    public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
    {
        if (position < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }

        cancellationToken.ThrowIfCancellationRequested();
        SetProperty("time-pos", position.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        // End-of-file is a transient notification for a seekable editor. Once
        // the user drags back from the last frame, expose the paused state so
        // the next state update cannot clamp the position back to duration.
        var status = _state.Status == PlaybackStatus.Ended
            ? PlaybackStatus.Paused
            : _state.Status;
        UpdateStateAndRaise(status, null);
        return Task.CompletedTask;
    }

    public Task SetVolumeAsync(double volume, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(volume) || volume is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(volume));
        }

        cancellationToken.ThrowIfCancellationRequested();
        PlaybackEngineState state;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _state = _state with { Volume = volume };
            if (_initialized)
            {
                SetPropertyCore("volume", volume.ToString("0.###", CultureInfo.InvariantCulture));
                UpdateState(_state.Status, null);
            }

            state = _state;
        }

        RaiseStateChanged(state);
        return Task.CompletedTask;
    }

    public Task SetMutedAsync(bool muted, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _muted = muted;
            if (_initialized)
            {
                SetPropertyCore("mute", muted ? "yes" : "no");
            }
        }

        return Task.CompletedTask;
    }

    public Task SetSpeedAsync(double speed, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(speed) || speed is < 0.25 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(speed));
        }

        cancellationToken.ThrowIfCancellationRequested();
        PlaybackEngineState state;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _state = _state with { Speed = speed };
            if (_initialized)
            {
                SetPropertyCore("speed", speed.ToString("0.###", CultureInfo.InvariantCulture));
                UpdateState(_state.Status, null);
            }

            state = _state;
        }

        RaiseStateChanged(state);
        return Task.CompletedTask;
    }

    public Task StepFrameAsync(bool forward, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ExecuteCommand([forward ? "frame-step" : "frame-back-step"], "step video frame");
        UpdateStateAndRaise(PlaybackStatus.Paused, null);
        return Task.CompletedTask;
    }

    public Task TakeScreenshotAsync(string outputPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(outputPath);
        lock (_sync)
        {
            if (_currentItem is not null &&
                Path.IsPathFullyQualified(_currentItem.Source) &&
                PathsEqual(fullPath, _currentItem.Source))
            {
                throw new IOException("A screenshot must not overwrite the media being played.");
            }
        }

        if (File.Exists(fullPath))
        {
            throw new IOException("The screenshot output file already exists.");
        }

        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        ExecuteCommand(["screenshot-to-file", fullPath, "subtitles"], "save playback screenshot");
        return Task.CompletedTask;
    }

    public Task SetLutAsync(string? cubePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? videoFilter = null;
        string? readbackToken = null;
        if (!string.IsNullOrWhiteSpace(cubePath))
        {
            var fullPath = Path.GetFullPath(cubePath);
            if (!File.Exists(fullPath) ||
                !string.Equals(Path.GetExtension(fullPath), ".cube", StringComparison.OrdinalIgnoreCase))
            {
                throw new FileNotFoundException("The selected .cube LUT file is unavailable.", fullPath);
            }

            readbackToken = FfmpegCommandBuilder.BuildLutPreviewFilter(fullPath);
            videoFilter = $"lavfi=[{readbackToken}]";
        }

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _currentLutVideoFilter = videoFilter;
            _currentLutReadbackToken = readbackToken;
            if (!_initialized)
            {
                return Task.CompletedTask;
            }

            ApplyCurrentLutFilterCore();
            if (_currentItem?.ShouldRenderVideo == true && GetBooleanPropertyCore("pause"))
            {
                // Reapplying a filter normally redraws the paused frame. Some mpv builds
                // reject the zero-distance seek while loadfile is replacing that frame;
                // the filter itself is already active, so this redraw hint must not abort
                // the next preview or leak an old item's error into the new selection.
                _ = _native.Command(_player, ["seek", "0", "relative+exact"]);
            }
        }

        return Task.CompletedTask;
    }

    public static bool IsLutFilterReadbackMatch(string? expectedToken, string? actualFilter)
    {
        if (string.IsNullOrEmpty(expectedToken))
        {
            return string.IsNullOrWhiteSpace(actualFilter);
        }

        return actualFilter?.Contains(expectedToken, StringComparison.Ordinal) == true;
    }

    public Task AddSubtitleAsync(string subtitlePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subtitlePath);
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(subtitlePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The subtitle file is unavailable.", fullPath);
        }

        ExecuteCommand(["sub-add", fullPath, "select"], "add subtitle track");
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PlaybackAudioTrack>> GetAudioTracksAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var count = GetIntProperty("track-list/count") ?? 0;
        var tracks = new List<PlaybackAudioTrack>();
        for (var index = 0; index < count; index++)
        {
            if (!string.Equals(GetProperty($"track-list/{index}/type"), "audio", StringComparison.Ordinal))
            {
                continue;
            }

            var id = GetIntProperty($"track-list/{index}/id");
            if (!id.HasValue || id.Value <= 0)
            {
                continue;
            }

            tracks.Add(new PlaybackAudioTrack(
                id.Value,
                GetIntProperty($"track-list/{index}/ff-index"),
                NullIfEmpty(GetProperty($"track-list/{index}/title")),
                NullIfEmpty(GetProperty($"track-list/{index}/lang")),
                NullIfEmpty(GetProperty($"track-list/{index}/codec")),
                GetIntProperty($"track-list/{index}/demux-channel-count"),
                GetBooleanProperty($"track-list/{index}/default"),
                GetBooleanProperty($"track-list/{index}/selected")));
        }

        return Task.FromResult<IReadOnlyList<PlaybackAudioTrack>>(tracks);
    }

    public Task SetSelectedAudioTracksAsync(
        IReadOnlyCollection<int> trackIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trackIds);
        cancellationToken.ThrowIfCancellationRequested();
        var selected = trackIds.Distinct().Order().ToArray();
        if (selected.Any(id => id <= 0) || selected.Length != trackIds.Count)
        {
            throw new ArgumentException("Audio track identifiers must be unique positive values.", nameof(trackIds));
        }

        lock (_sync)
        {
            EnsureInitialized();

            // Keep the complete transition under one lock so rapid UI changes cannot
            // interleave an aid selection with another request's lavfi mix graph.
            SetPropertyCore("aid", "no");
            SetPropertyCore("lavfi-complex", string.Empty);
            if (selected.Length == 1)
            {
                SetPropertyCore("aid", selected[0].ToString(CultureInfo.InvariantCulture));
            }
            else if (selected.Length > 1)
            {
                SetPropertyCore("lavfi-complex", BuildAudioMixGraph(selected));
            }
        }

        return Task.CompletedTask;
    }

    public static string BuildAudioMixGraph(IEnumerable<int> trackIds)
    {
        ArgumentNullException.ThrowIfNull(trackIds);
        var selected = trackIds.Distinct().Order().ToArray();
        if (selected.Length < 2 || selected.Any(id => id <= 0))
        {
            throw new ArgumentException("At least two unique positive audio track identifiers are required.", nameof(trackIds));
        }

        var inputs = string.Join(' ', selected.Select(id => $"[aid{id}]") );
        return $"{inputs} amix=inputs={selected.Length}:normalize=1:dropout_transition=0 [ao]";
    }

    public static IReadOnlyList<string> BuildLoadFileCommand(string source, TimeSpan startPosition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (startPosition < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(startPosition));
        }

        var arguments = new List<string> { "loadfile", source, "replace" };
        if (startPosition > TimeSpan.Zero)
        {
            // mpv 0.38+ reserves the fourth loadfile argument for the playlist index.
            arguments.Add("-1");
            arguments.Add($"start={startPosition.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)}");
        }

        return arguments;
    }

    private async Task PumpEventsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var mediaEvent = _native.WaitEvent(_player, 0.1);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            switch (mediaEvent.EventId)
            {
                case MpvEventId.None:
                    break;
                case MpvEventId.StartFile:
                    UpdateStateAndRaise(PlaybackStatus.Loading, null);
                    break;
                case MpvEventId.FileLoaded:
                case MpvEventId.PlaybackRestart:
                    try
                    {
                        RestoreLutFilterIfNeeded();
                    }
                    catch (Exception exception)
                    {
                        UpdateStateAndRaise(
                            PlaybackStatus.Failed,
                            $"Failed to restore the selected LUT: {exception.Message}");
                        break;
                    }

                    UpdateStateAndRaise(
                        GetBooleanProperty("pause") ? PlaybackStatus.Paused : PlaybackStatus.Playing,
                        null);
                    break;
                case MpvEventId.EndFile:
                    HandleEndFile(mediaEvent);
                    break;
                case MpvEventId.Shutdown:
                    return;
                case MpvEventId.QueueOverflow:
                    UpdateStateAndRaise(PlaybackStatus.Failed, "libmpv event queue overflowed.");
                    break;
            }

            await Task.Yield();
        }
    }

    private void RestoreLutFilterIfNeeded()
    {
        lock (_sync)
        {
            if (_disposed || !_initialized ||
                IsLutFilterReadbackMatch(
                    _currentLutReadbackToken,
                    _native.GetPropertyString(_player, "vf")))
            {
                return;
            }

            ApplyCurrentLutFilterCore();
        }
    }

    private void ApplyCurrentLutFilterCore()
    {
        var videoFilter = _currentLutVideoFilter ?? string.Empty;
        SetPropertyCore("vf", videoFilter);
        var readback = _native.GetPropertyString(_player, "vf");
        if (!IsLutFilterReadbackMatch(_currentLutReadbackToken, readback))
        {
            throw new InvalidOperationException("libmpv did not retain the selected LUT video filter.");
        }
    }

    private void HandleEndFile(MpvEvent mediaEvent)
    {
        if (mediaEvent.Data == 0)
        {
            return;
        }

        var endFile = Marshal.PtrToStructure<MpvEventEndFile>(mediaEvent.Data);
        switch (endFile.Reason)
        {
            case MpvEndFileReason.EndOfFile:
                UpdateStateAndRaise(PlaybackStatus.Ended, null);
                break;
            case MpvEndFileReason.Error:
                UpdateStateAndRaise(
                    PlaybackStatus.Failed,
                    endFile.Error < 0 ? _native.GetErrorText(endFile.Error) : "Media playback failed.");
                break;
            case MpvEndFileReason.Stop:
            case MpvEndFileReason.Quit:
            case MpvEndFileReason.Redirect:
                // Stop is also emitted when loadfile replaces the current item.
                break;
        }
    }

    private PlaybackEngineState QueryState(PlaybackStatus status, string? error)
    {
        if (status == PlaybackStatus.Loading)
        {
            return _state with
            {
                Status = status,
                CurrentItemKey = _currentItem?.Key,
                MediaTitle = _currentItem?.Title,
                HasVideo = _currentItem?.ShouldRenderVideo == true,
                Error = error
            };
        }

        var position = GetDoubleProperty("time-pos") is { } seconds && seconds >= 0
            ? TimeSpan.FromSeconds(seconds)
            : _state.Position;
        TimeSpan? duration = GetDoubleProperty("duration") is { } durationSeconds && durationSeconds >= 0
            ? TimeSpan.FromSeconds(durationSeconds)
            : _state.Duration;
        var volume = GetDoubleProperty("volume") ?? _state.Volume;
        var speed = GetDoubleProperty("speed") ?? _state.Speed;
        return new PlaybackEngineState(
            status,
            _currentItem?.Key,
            _currentItem?.Title,
            _currentItem?.ShouldRenderVideo == true,
            status == PlaybackStatus.Paused || GetBooleanProperty("pause"),
            position,
            duration,
            volume,
            speed,
            error);
    }

    private void UpdateState(PlaybackStatus status, string? error)
    {
        var next = QueryState(status, error);
        if (status == PlaybackStatus.Ended && next.Duration is { } duration)
        {
            next = next with { Position = duration, IsPaused = true };
        }

        _state = next;
    }

    private void UpdateStateAndRaise(PlaybackStatus status, string? error)
    {
        PlaybackEngineState state;
        lock (_sync)
        {
            if (_disposed || !_initialized)
            {
                return;
            }

            UpdateState(status, error);
            state = _state;
        }

        RaiseStateChanged(state);
    }

    private void RaiseStateChanged(PlaybackEngineState state)
    {
        var args = new PlaybackEngineStateChangedEventArgs(state);
        foreach (EventHandler<PlaybackEngineStateChangedEventArgs> handler in
                 StateChanged?.GetInvocationList() ?? [])
        {
            try
            {
                handler(this, args);
            }
            catch
            {
                // A UI subscriber must not stop the native event pump.
            }
        }
    }

    private void SetOption(string name, string value) =>
        ThrowIfError(_native.SetOptionString(_player, name, value), $"set libmpv option '{name}'");

    private void SetProperty(string name, string value)
    {
        lock (_sync)
        {
            EnsureInitialized();
            SetPropertyCore(name, value);
        }
    }

    private void SetPropertyCore(string name, string value)
    {
        ThrowIfError(_native.SetPropertyString(_player, name, value), $"set libmpv property '{name}'");
    }

    private void ExecuteCommand(IReadOnlyList<string> arguments, string operation)
    {
        lock (_sync)
        {
            EnsureInitialized();
            ThrowIfError(_native.Command(_player, arguments), operation);
        }
    }

    private string? GetProperty(string name)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized)
        {
            return null;
        }

        return _native.GetPropertyString(_player, name);
    }

    private void EnsureInitialized()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized)
        {
            throw new InvalidOperationException(
                "Bind the native video host before loading or controlling media.");
        }
    }

    private int? GetIntProperty(string name) =>
        int.TryParse(GetProperty(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private double? GetDoubleProperty(string name) =>
        double.TryParse(GetProperty(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
        double.IsFinite(value)
            ? value
            : null;

    private bool GetBooleanProperty(string name) =>
        GetProperty(name) is "yes" or "true" or "1";

    private bool GetBooleanPropertyCore(string name) =>
        _native.GetPropertyString(_player, name) is "yes" or "true" or "1";

    private void ThrowIfError(int errorCode, string operation)
    {
        if (errorCode < 0)
        {
            throw new InvalidOperationException($"Failed to {operation}: {_native.GetErrorText(errorCode)}");
        }
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static bool PathsEqual(string first, string second) =>
        string.Equals(
            Path.GetFullPath(first),
            Path.GetFullPath(second),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string FormatNativeWindowHandle(nint handle) =>
        OperatingSystem.IsWindows()
            ? unchecked((ulong)(nuint)handle).ToString(CultureInfo.InvariantCulture)
            : handle.ToInt64().ToString(CultureInfo.InvariantCulture);

    public async ValueTask DisposeAsync()
    {
        Task? eventTask;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            eventTask = _eventTask;
        }

        _eventCancellation.Cancel();
        try
        {
            if (eventTask is not null)
            {
                try
                {
                    await eventTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch
                {
                    // Native event-pump failures must not leak the player context during shutdown.
                }
            }
        }
        finally
        {
            _native.TerminateDestroy(_player);
            _eventCancellation.Dispose();
            _native.Dispose();
        }
    }
}

internal enum MpvEventId
{
    None = 0,
    Shutdown = 1,
    StartFile = 6,
    EndFile = 7,
    FileLoaded = 8,
    PlaybackRestart = 21,
    QueueOverflow = 24
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEvent
{
    public MpvEventId EventId;
    public int Error;
    public ulong ReplyUserData;
    public nint Data;
}

internal enum MpvEndFileReason
{
    EndOfFile = 0,
    Stop = 2,
    Quit = 3,
    Error = 4,
    Redirect = 5
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventEndFile
{
    public MpvEndFileReason Reason;
    public int Error;
}

internal sealed class LibMpvNativeLibrary : IDisposable
{
    private static readonly string[] WindowsCandidates = ["mpv-2.dll", "libmpv-2.dll", "mpv-1.dll"];
    private static readonly string[] MacCandidates = ["libmpv.2.dylib", "libmpv.dylib"];
    private static readonly string[] LinuxCandidates = ["libmpv.so.2", "libmpv.so.1", "libmpv.so"];

    private readonly nint _library;
    private readonly MpvCreate _create;
    private readonly MpvInitialize _initialize;
    private readonly MpvTerminateDestroy _terminateDestroy;
    private readonly MpvSetOptionString _setOptionString;
    private readonly MpvSetPropertyString _setPropertyString;
    private readonly MpvGetPropertyString _getPropertyString;
    private readonly MpvCommand _command;
    private readonly MpvWaitEvent _waitEvent;
    private readonly MpvErrorString _errorString;
    private readonly MpvFree _free;
    private bool _disposed;

    private LibMpvNativeLibrary(nint library)
    {
        _library = library;
        _create = Load<MpvCreate>("mpv_create");
        _initialize = Load<MpvInitialize>("mpv_initialize");
        _terminateDestroy = Load<MpvTerminateDestroy>("mpv_terminate_destroy");
        _setOptionString = Load<MpvSetOptionString>("mpv_set_option_string");
        _setPropertyString = Load<MpvSetPropertyString>("mpv_set_property_string");
        _getPropertyString = Load<MpvGetPropertyString>("mpv_get_property_string");
        _command = Load<MpvCommand>("mpv_command");
        _waitEvent = Load<MpvWaitEvent>("mpv_wait_event");
        _errorString = Load<MpvErrorString>("mpv_error_string");
        _free = Load<MpvFree>("mpv_free");
    }

    public static bool TryLoad(
        string? explicitPath,
        out LibMpvNativeLibrary? library,
        out PlaybackEngineAvailability availability)
    {
        library = null;
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var fullPath = Path.GetFullPath(explicitPath);
            return TryCandidate(fullPath, out library, out availability);
        }

        var candidates = OperatingSystem.IsWindows()
            ? WindowsCandidates
            : OperatingSystem.IsMacOS()
                ? MacCandidates
                : OperatingSystem.IsLinux()
                    ? LinuxCandidates
                    : [];
        if (candidates.Length == 0)
        {
            availability = new PlaybackEngineAvailability(
                false,
                PlaybackEngineUnavailableReason.UnsupportedPlatform,
                "libmpv loading is not configured for this operating system.",
                null);
            return false;
        }

        PlaybackEngineAvailability? incompatibleLibrary = null;
        foreach (var candidate in candidates)
        {
            if (TryCandidate(candidate, out library, out availability, reportMissing: false))
            {
                return true;
            }

            if (availability.Reason == PlaybackEngineUnavailableReason.NativeApiIncomplete)
            {
                incompatibleLibrary ??= availability;
            }
        }

        if (incompatibleLibrary is not null)
        {
            availability = incompatibleLibrary;
            return false;
        }

        availability = new PlaybackEngineAvailability(
            false,
            PlaybackEngineUnavailableReason.NativeLibraryNotFound,
            $"libmpv was not found. Tried: {string.Join(", ", candidates)}.",
            null);
        return false;
    }

    private static bool TryCandidate(
        string candidate,
        out LibMpvNativeLibrary? library,
        out PlaybackEngineAvailability availability,
        bool reportMissing = true)
    {
        library = null;
        if (!NativeLibrary.TryLoad(candidate, out var handle))
        {
            availability = new PlaybackEngineAvailability(
                false,
                PlaybackEngineUnavailableReason.NativeLibraryNotFound,
                reportMissing ? $"libmpv could not be loaded from '{candidate}'." : null,
                candidate);
            return false;
        }

        try
        {
            library = new LibMpvNativeLibrary(handle);
            availability = PlaybackEngineAvailability.Available(candidate);
            return true;
        }
        catch (Exception exception) when (exception is EntryPointNotFoundException or MissingMethodException)
        {
            NativeLibrary.Free(handle);
            availability = new PlaybackEngineAvailability(
                false,
                PlaybackEngineUnavailableReason.NativeApiIncomplete,
                $"The libmpv library is missing a required API: {exception.Message}",
                candidate);
            return false;
        }
    }

    public nint Create() => _create();

    public int Initialize(nint player) => _initialize(player);

    public void TerminateDestroy(nint player) => _terminateDestroy(player);

    public int SetOptionString(nint player, string name, string value) =>
        _setOptionString(player, name, value);

    public int SetPropertyString(nint player, string name, string value) =>
        _setPropertyString(player, name, value);

    public string? GetPropertyString(nint player, string name)
    {
        var pointer = _getPropertyString(player, name);
        if (pointer == 0)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUTF8(pointer);
        }
        finally
        {
            _free(pointer);
        }
    }

    public int Command(nint player, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var strings = new nint[arguments.Count];
        var pointerArray = Marshal.AllocHGlobal((arguments.Count + 1) * IntPtr.Size);
        try
        {
            for (var index = 0; index < arguments.Count; index++)
            {
                strings[index] = Marshal.StringToCoTaskMemUTF8(arguments[index]);
                Marshal.WriteIntPtr(pointerArray, index * IntPtr.Size, strings[index]);
            }

            Marshal.WriteIntPtr(pointerArray, arguments.Count * IntPtr.Size, 0);
            return _command(player, pointerArray);
        }
        finally
        {
            foreach (var pointer in strings)
            {
                if (pointer != 0)
                {
                    Marshal.FreeCoTaskMem(pointer);
                }
            }

            Marshal.FreeHGlobal(pointerArray);
        }
    }

    public MpvEvent WaitEvent(nint player, double timeout)
    {
        var pointer = _waitEvent(player, timeout);
        return pointer == 0 ? default : Marshal.PtrToStructure<MpvEvent>(pointer);
    }

    public string GetErrorText(int errorCode) =>
        Marshal.PtrToStringUTF8(_errorString(errorCode)) ?? $"libmpv error {errorCode}";

    private T Load<T>(string exportName)
        where T : Delegate
    {
        if (!NativeLibrary.TryGetExport(_library, exportName, out var address))
        {
            throw new EntryPointNotFoundException(exportName);
        }

        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        NativeLibrary.Free(_library);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint MpvCreate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MpvInitialize(nint player);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void MpvTerminateDestroy(nint player);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MpvSetOptionString(
        nint player,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MpvSetPropertyString(
        nint player,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint MpvGetPropertyString(
        nint player,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MpvCommand(nint player, nint arguments);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint MpvWaitEvent(nint player, double timeout);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint MpvErrorString(int errorCode);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void MpvFree(nint data);
}
