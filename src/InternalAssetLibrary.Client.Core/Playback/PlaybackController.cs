namespace InternalAssetLibrary.Client.Core.Playback;

public sealed class PlaybackController : IAsyncDisposable
{
    public const int MaximumRecentItems = 100;

    private readonly IPlaybackEngine _engine;
    private readonly PlaybackVolumeCoordinator _volumeCoordinator;
    private readonly IDisposable _volumeRegistration;
    private readonly Random _random;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly object _lifecycleSync = new();
    private readonly TaskCompletionSource _disposeCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<PlaybackItem> _queue = [];
    private readonly List<PlaybackRecentEntry> _recent = [];
    private readonly Dictionary<string, string> _itemLutPaths = new(StringComparer.Ordinal);
    private TaskCompletionSource? _operationsDrained;
    private int _activeOperations;
    private int _disposeState;
    private int _currentIndex = -1;

    public PlaybackController(
        IPlaybackEngine engine,
        PlaybackVolumeCoordinator? volumeCoordinator = null,
        Random? random = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _volumeCoordinator = volumeCoordinator ?? new PlaybackVolumeCoordinator();
        Volume = _volumeCoordinator.Volume;
        _volumeRegistration = _volumeCoordinator.Register(ApplyCoordinatedVolumeAsync);
        _random = random ?? Random.Shared;
        _engine.StateChanged += OnEngineStateChanged;
    }

    public PlaybackEngineAvailability Availability => _engine.Availability;

    public PlaybackEngineState State => _engine.State;

    public IReadOnlyList<PlaybackItem> Queue
    {
        get
        {
            lock (_queue)
            {
                return _queue.ToArray();
            }
        }
    }

    public IReadOnlyList<PlaybackRecentEntry> Recent
    {
        get
        {
            lock (_queue)
            {
                return _recent.ToArray();
            }
        }
    }

    public PlaybackItem? CurrentItem
    {
        get
        {
            lock (_queue)
            {
                return _currentIndex >= 0 && _currentIndex < _queue.Count
                    ? _queue[_currentIndex]
                    : null;
            }
        }
    }

    public string? CurrentLutPath => CurrentItem is { } item ? GetLutPath(item.Key) : null;

    public PlaybackRepeatMode RepeatMode { get; set; }

    public PlaybackOrderMode OrderMode { get; set; }

    public double Volume { get; private set; } = 100;

    public bool IsMuted { get; private set; }

    public double Speed { get; private set; } = 1;

    public event EventHandler<PlaybackEngineStateChangedEventArgs>? StateChanged;

    public PlaybackVolumeCoordinator VolumeCoordinator => _volumeCoordinator;

    public async Task<PlaybackOperationResult> OpenAsync(
        PlaybackItem item,
        TimeSpan startPosition = default,
        CancellationToken cancellationToken = default)
    {
        if (startPosition < TimeSpan.Zero)
        {
            return PlaybackOperationResult.Failed(
                PlaybackOperationFailure.InvalidRequest,
                "Playback position cannot be negative.");
        }

        var normalized = item.ValidateAndNormalize();
        using var operation = BeginOperation(cancellationToken);
        ReplaceQueueCore([normalized], normalized.Key);
        if (!_engine.Availability.IsAvailable)
        {
            return PlaybackOperationResult.Failed(
                PlaybackOperationFailure.EngineUnavailable,
                _engine.Availability.Diagnostic ?? "The playback engine is unavailable.");
        }

        await _gate.WaitAsync(operation.Token).ConfigureAwait(false);
        try
        {
            await LoadItemAsync(normalized, startPosition, operation.Token).ConfigureAwait(false);
            await _engine.SetVolumeAsync(Volume, operation.Token).ConfigureAwait(false);
            await _engine.SetSpeedAsync(Speed, operation.Token).ConfigureAwait(false);
            AddRecent(normalized, startPosition);
            return PlaybackOperationResult.Success;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return PlaybackOperationResult.Failed(PlaybackOperationFailure.EngineError, exception.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PlaybackOperationResult> PlayPauseAsync(CancellationToken cancellationToken = default)
    {
        PlaybackEngineState state;
        using (var operation = BeginOperation(cancellationToken))
        {
            state = _engine.State;
        }

        return state.Status is PlaybackStatus.Idle or PlaybackStatus.Ended || HasReachedEnd(state)
            ? await PlayCurrentAsync(cancellationToken).ConfigureAwait(false)
            : state.IsPaused || state.Status == PlaybackStatus.Paused
                ? await PlayAsync(cancellationToken).ConfigureAwait(false)
                : await PauseAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool HasReachedEnd(PlaybackEngineState state) =>
        state.Duration is { } duration &&
        duration > TimeSpan.Zero &&
        state.Position >= duration - TimeSpan.FromMilliseconds(250);

    public async Task<PlaybackOperationResult> SetVideoHostAsync(
        nint nativeWindowHandle,
        CancellationToken cancellationToken = default)
    {
        if (nativeWindowHandle == 0)
        {
            return PlaybackOperationResult.Failed(
                PlaybackOperationFailure.InvalidRequest,
                "The native video host handle cannot be zero.");
        }

        return await ExecuteAsync(
                token => _engine.SetVideoHostAsync(nativeWindowHandle, token),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public void ReplaceQueue(IEnumerable<PlaybackItem> items, string? currentItemKey = null)
    {
        ThrowIfDisposalStarted();
        ArgumentNullException.ThrowIfNull(items);
        var normalized = items.Select(item => item.ValidateAndNormalize()).ToArray();
        ReplaceQueueCore(normalized, currentItemKey);
    }

    private void ReplaceQueueCore(IReadOnlyList<PlaybackItem> normalized, string? currentItemKey)
    {
        if (normalized.Select(item => item.Key).Distinct(StringComparer.Ordinal).Count() != normalized.Count)
        {
            throw new ArgumentException("Playback queue item keys must be unique.", "items");
        }

        var selectedIndex = normalized.Count == 0 ? -1 : 0;
        if (currentItemKey is not null)
        {
            selectedIndex = -1;
            for (var index = 0; index < normalized.Count; index++)
            {
                if (StringComparer.Ordinal.Equals(normalized[index].Key, currentItemKey))
                {
                    selectedIndex = index;
                    break;
                }
            }
        }

        if (currentItemKey is not null && selectedIndex < 0)
        {
            throw new ArgumentException("The selected item is not in the replacement queue.", nameof(currentItemKey));
        }

        lock (_queue)
        {
            _queue.Clear();
            _queue.AddRange(normalized);
            _currentIndex = selectedIndex;
            _itemLutPaths.Clear();
        }
    }

    public void Enqueue(PlaybackItem item)
    {
        ThrowIfDisposalStarted();
        var normalized = item.ValidateAndNormalize();
        lock (_queue)
        {
            if (_queue.Any(existing => StringComparer.Ordinal.Equals(existing.Key, normalized.Key)))
            {
                throw new InvalidOperationException("The playback item is already in the queue.");
            }

            _queue.Add(normalized);
            if (_currentIndex < 0)
            {
                _currentIndex = 0;
            }
        }
    }

    public async Task<PlaybackOperationResult> RemoveFromQueueAsync(
        string itemKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemKey);
        using var operation = BeginOperation(cancellationToken);
        await _gate.WaitAsync(operation.Token).ConfigureAwait(false);
        try
        {
            PlaybackItem? nextItem = null;
            var removedEngineItem = false;
            lock (_queue)
            {
                var index = _queue.FindIndex(item => StringComparer.Ordinal.Equals(item.Key, itemKey));
                if (index < 0)
                {
                    return PlaybackOperationResult.Failed(
                        PlaybackOperationFailure.InvalidRequest,
                        "The requested playback queue item does not exist.");
                }

                removedEngineItem = StringComparer.Ordinal.Equals(_engine.State.CurrentItemKey, itemKey);
                _queue.RemoveAt(index);
                _itemLutPaths.Remove(itemKey);
                if (_queue.Count == 0)
                {
                    _currentIndex = -1;
                }
                else if (index < _currentIndex)
                {
                    _currentIndex--;
                }
                else if (index == _currentIndex)
                {
                    _currentIndex = Math.Min(index, _queue.Count - 1);
                }
                else if (_currentIndex >= _queue.Count)
                {
                    _currentIndex = _queue.Count - 1;
                }

                if (removedEngineItem && _currentIndex >= 0)
                {
                    nextItem = _queue[_currentIndex];
                }
            }

            if (!removedEngineItem)
            {
                return PlaybackOperationResult.Success;
            }

            if (nextItem is null)
            {
                await _engine.StopAsync(operation.Token).ConfigureAwait(false);
                return PlaybackOperationResult.Success;
            }

            await LoadItemAsync(nextItem, TimeSpan.Zero, operation.Token).ConfigureAwait(false);
            await _engine.SetVolumeAsync(Volume, operation.Token).ConfigureAwait(false);
            await _engine.SetSpeedAsync(Speed, operation.Token).ConfigureAwait(false);
            AddRecent(nextItem, TimeSpan.Zero);
            return PlaybackOperationResult.Success;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return PlaybackOperationResult.Failed(PlaybackOperationFailure.EngineError, exception.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<PlaybackOperationResult> PlayCurrentAsync(CancellationToken cancellationToken = default) =>
        PlayIndexAsync(_currentIndex, cancellationToken);

    public async Task<PlaybackOperationResult> PlayIndexAsync(
        int index,
        CancellationToken cancellationToken = default)
    {
        using var operation = BeginOperation(cancellationToken);
        if (!_engine.Availability.IsAvailable)
        {
            return PlaybackOperationResult.Failed(
                PlaybackOperationFailure.EngineUnavailable,
                _engine.Availability.Diagnostic ?? "The playback engine is unavailable.");
        }

        await _gate.WaitAsync(operation.Token).ConfigureAwait(false);
        try
        {
            PlaybackItem item;
            lock (_queue)
            {
                if (index < 0 || index >= _queue.Count)
                {
                    return PlaybackOperationResult.Failed(
                        PlaybackOperationFailure.EmptyQueue,
                        "The requested playback queue item does not exist.");
                }

                _currentIndex = index;
                item = _queue[index];
            }

            await LoadItemAsync(item, TimeSpan.Zero, operation.Token).ConfigureAwait(false);
            await _engine.SetVolumeAsync(Volume, operation.Token).ConfigureAwait(false);
            await _engine.SetSpeedAsync(Speed, operation.Token).ConfigureAwait(false);
            AddRecent(item, TimeSpan.Zero);
            return PlaybackOperationResult.Success;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return PlaybackOperationResult.Failed(PlaybackOperationFailure.EngineError, exception.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<PlaybackOperationResult> NextAsync(CancellationToken cancellationToken = default) =>
        MoveAsync(forward: true, causedByEnd: false, cancellationToken);

    public Task<PlaybackOperationResult> PreviousAsync(CancellationToken cancellationToken = default) =>
        MoveAsync(forward: false, causedByEnd: false, cancellationToken);

    public async Task<PlaybackOperationResult> PlayAsync(CancellationToken cancellationToken = default) =>
        await ExecuteAsync(token => _engine.PlayAsync(token), cancellationToken).ConfigureAwait(false);

    public async Task<PlaybackOperationResult> PauseAsync(CancellationToken cancellationToken = default) =>
        await ExecuteAsync(token => _engine.PauseAsync(token), cancellationToken).ConfigureAwait(false);

    public async Task<PlaybackOperationResult> StopAsync(CancellationToken cancellationToken = default)
    {
        return await ExecuteAsync(
                async token =>
                {
                    var current = CurrentItem;
                    if (current is not null)
                    {
                        AddRecent(current, _engine.State.Position);
                    }

                    await _engine.StopAsync(token).ConfigureAwait(false);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<PlaybackOperationResult> SeekAsync(
        TimeSpan position,
        CancellationToken cancellationToken = default)
    {
        if (position < TimeSpan.Zero)
        {
            return PlaybackOperationResult.Failed(
                PlaybackOperationFailure.InvalidRequest,
                "Playback position cannot be negative.");
        }

        return await ExecuteAsync(
                token =>
                {
                    var duration = _engine.State.Duration;
                    var normalized = duration.HasValue && position > duration.Value ? duration.Value : position;
                    return _engine.SeekAsync(normalized, token);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<PlaybackOperationResult> SetVolumeAsync(
        double volume,
        CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(volume) || volume is < 0 or > 100)
        {
            return PlaybackOperationResult.Failed(
                PlaybackOperationFailure.InvalidRequest,
                "Playback volume must be between 0 and 100.");
        }

        var result = await ExecuteAsync(token => _volumeCoordinator.SetVolumeAsync(volume, token), cancellationToken)
            .ConfigureAwait(false);
        return result;
    }

    public async Task<PlaybackOperationResult> SetMutedAsync(
        bool muted,
        CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync(token => _engine.SetMutedAsync(muted, token), cancellationToken)
            .ConfigureAwait(false);
        if (result.Succeeded)
        {
            IsMuted = muted;
        }

        return result;
    }

    public async Task<PlaybackOperationResult> SetSpeedAsync(
        double speed,
        CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(speed) || speed is < 0.25 or > 4)
        {
            return PlaybackOperationResult.Failed(
                PlaybackOperationFailure.InvalidRequest,
                "Playback speed must be between 0.25 and 4.");
        }

        var result = await ExecuteAsync(token => _engine.SetSpeedAsync(speed, token), cancellationToken)
            .ConfigureAwait(false);
        if (result.Succeeded)
        {
            Speed = speed;
        }

        return result;
    }

    public Task<PlaybackOperationResult> StepFrameAsync(
        bool forward,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(token => _engine.StepFrameAsync(forward, token), cancellationToken);

    public Task<PlaybackOperationResult> TakeScreenshotAsync(
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return Task.FromResult(PlaybackOperationResult.Failed(
                PlaybackOperationFailure.InvalidRequest,
                "A screenshot output path is required."));
        }

        return ExecuteAsync(token => _engine.TakeScreenshotAsync(outputPath, token), cancellationToken);
    }

    public async Task<PlaybackOperationResult> SetLutAsync(
        string? cubePath,
        CancellationToken cancellationToken = default)
        => await SetLutCoreAsync(cubePath, null, cancellationToken).ConfigureAwait(false);

    public async Task<PlaybackOperationResult> SetLutAsync(
        string? cubePath,
        string expectedItemKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedItemKey);
        return await SetLutCoreAsync(cubePath, expectedItemKey, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PlaybackOperationResult> SetLutCoreAsync(
        string? cubePath,
        string? expectedItemKey,
        CancellationToken cancellationToken)
    {
        using var operation = BeginOperation(cancellationToken);
        if (!_engine.Availability.IsAvailable)
        {
            return PlaybackOperationResult.Failed(
                PlaybackOperationFailure.EngineUnavailable,
                _engine.Availability.Diagnostic ?? "The playback engine is unavailable.");
        }

        var normalizedPath = string.IsNullOrWhiteSpace(cubePath)
            ? null
            : Path.GetFullPath(cubePath);
        await _gate.WaitAsync(operation.Token).ConfigureAwait(false);
        try
        {
            var item = CurrentItem;
            if (expectedItemKey is not null &&
                !StringComparer.Ordinal.Equals(item?.Key, expectedItemKey))
            {
                return PlaybackOperationResult.Failed(
                    PlaybackOperationFailure.InvalidRequest,
                    "The playback item changed before the LUT could be applied.");
            }

            await _engine.SetLutAsync(normalizedPath, operation.Token).ConfigureAwait(false);
            if (item is not null)
            {
                lock (_queue)
                {
                    var currentItemKey = _currentIndex >= 0 && _currentIndex < _queue.Count
                        ? _queue[_currentIndex].Key
                        : null;
                    if (!StringComparer.Ordinal.Equals(currentItemKey, item.Key))
                    {
                        return PlaybackOperationResult.Failed(
                            PlaybackOperationFailure.InvalidRequest,
                            "The playback item changed while the LUT was being applied.");
                    }

                    if (normalizedPath is null)
                    {
                        _itemLutPaths.Remove(item.Key);
                    }
                    else
                    {
                        _itemLutPaths[item.Key] = normalizedPath;
                    }
                }
            }

            return PlaybackOperationResult.Success;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return PlaybackOperationResult.Failed(PlaybackOperationFailure.EngineError, exception.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public string? GetLutPath(string itemKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemKey);
        lock (_queue)
        {
            return _itemLutPaths.GetValueOrDefault(itemKey);
        }
    }

    public Task<PlaybackOperationResult> AddSubtitleAsync(
        string subtitlePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(subtitlePath))
        {
            return Task.FromResult(PlaybackOperationResult.Failed(
                PlaybackOperationFailure.InvalidRequest,
                "A subtitle path is required."));
        }

        return ExecuteAsync(token => _engine.AddSubtitleAsync(subtitlePath, token), cancellationToken);
    }

    public async Task<PlaybackOperationResult> SetSelectedAudioTracksAsync(
        IReadOnlyCollection<int> trackIds,
        CancellationToken cancellationToken = default)
        => await SetSelectedAudioTracksCoreAsync(trackIds, null, cancellationToken).ConfigureAwait(false);

    public async Task<PlaybackOperationResult> SetSelectedAudioTracksAsync(
        IReadOnlyCollection<int> trackIds,
        string expectedItemKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedItemKey);
        return await SetSelectedAudioTracksCoreAsync(trackIds, expectedItemKey, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<PlaybackOperationResult> SetSelectedAudioTracksCoreAsync(
        IReadOnlyCollection<int> trackIds,
        string? expectedItemKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(trackIds);
        if (trackIds.Any(id => id <= 0) || trackIds.Distinct().Count() != trackIds.Count)
        {
            return PlaybackOperationResult.Failed(
                PlaybackOperationFailure.InvalidRequest,
                "Audio track identifiers must be unique positive values.");
        }

        using var operation = BeginOperation(cancellationToken);
        if (!_engine.Availability.IsAvailable)
        {
            return PlaybackOperationResult.Failed(
                PlaybackOperationFailure.EngineUnavailable,
                _engine.Availability.Diagnostic ?? "The playback engine is unavailable.");
        }

        await _gate.WaitAsync(operation.Token).ConfigureAwait(false);
        try
        {
            if (expectedItemKey is not null &&
                !StringComparer.Ordinal.Equals(CurrentItem?.Key, expectedItemKey))
            {
                return PlaybackOperationResult.Failed(
                    PlaybackOperationFailure.InvalidRequest,
                    "The playback item changed before the audio tracks could be applied.");
            }

            await _engine.SetSelectedAudioTracksAsync(trackIds, operation.Token).ConfigureAwait(false);
            return PlaybackOperationResult.Success;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return PlaybackOperationResult.Failed(PlaybackOperationFailure.EngineError, exception.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<PlaybackAudioTrack>> GetAudioTracksAsync(
        CancellationToken cancellationToken = default)
    {
        using var operation = BeginOperation(cancellationToken);
        return await _engine.GetAudioTracksAsync(operation.Token).ConfigureAwait(false);
    }

    private async Task<PlaybackOperationResult> MoveAsync(
        bool forward,
        bool causedByEnd,
        CancellationToken cancellationToken)
    {
        int targetIndex;
        lock (_queue)
        {
            if (_queue.Count == 0 || _currentIndex < 0)
            {
                return PlaybackOperationResult.Failed(PlaybackOperationFailure.EmptyQueue, "The playback queue is empty.");
            }

            if (causedByEnd && RepeatMode == PlaybackRepeatMode.One)
            {
                targetIndex = _currentIndex;
            }
            else if (OrderMode == PlaybackOrderMode.Shuffle && _queue.Count > 1)
            {
                var offset = _random.Next(_queue.Count - 1);
                targetIndex = offset >= _currentIndex ? offset + 1 : offset;
            }
            else
            {
                targetIndex = _currentIndex + (forward ? 1 : -1);
                if (targetIndex < 0 || targetIndex >= _queue.Count)
                {
                    if (RepeatMode == PlaybackRepeatMode.All)
                    {
                        targetIndex = (targetIndex + _queue.Count) % _queue.Count;
                    }
                    else
                    {
                        return PlaybackOperationResult.Failed(
                            PlaybackOperationFailure.EndOfQueue,
                            "Playback reached the end of the queue.");
                    }
                }
            }
        }

        return await PlayIndexAsync(targetIndex, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PlaybackOperationResult> ExecuteAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        using var lease = BeginOperation(cancellationToken);
        if (!_engine.Availability.IsAvailable)
        {
            return PlaybackOperationResult.Failed(
                PlaybackOperationFailure.EngineUnavailable,
                _engine.Availability.Diagnostic ?? "The playback engine is unavailable.");
        }

        try
        {
            lease.Token.ThrowIfCancellationRequested();
            await operation(lease.Token).ConfigureAwait(false);
            return PlaybackOperationResult.Success;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return PlaybackOperationResult.Failed(PlaybackOperationFailure.EngineError, exception.Message);
        }
    }

    private async Task LoadItemAsync(
        PlaybackItem item,
        TimeSpan startPosition,
        CancellationToken cancellationToken)
    {
        string? lutPath;
        lock (_queue)
        {
            lutPath = item.ShouldRenderVideo
                ? _itemLutPaths.GetValueOrDefault(item.Key)
                : null;
        }

        // LUT is global mpv state, so select (or explicitly clear) the target item's
        // remembered LUT before replacing the current file.
        await _engine.SetLutAsync(lutPath, cancellationToken).ConfigureAwait(false);
        await _engine.LoadAsync(item, startPosition, cancellationToken).ConfigureAwait(false);
    }

    private void AddRecent(PlaybackItem item, TimeSpan position)
    {
        var safeItem = item with { HttpHeaders = [] };
        lock (_queue)
        {
            _recent.RemoveAll(entry => StringComparer.Ordinal.Equals(entry.Item.Key, safeItem.Key));
            _recent.Insert(0, new PlaybackRecentEntry(safeItem, DateTimeOffset.UtcNow, position));
            if (_recent.Count > MaximumRecentItems)
            {
                _recent.RemoveRange(MaximumRecentItems, _recent.Count - MaximumRecentItems);
            }
        }
    }

    private async Task ApplyCoordinatedVolumeAsync(double volume, CancellationToken cancellationToken)
    {
        using var operation = BeginOperation(cancellationToken);
        Volume = volume;
        if (_engine.Availability.IsAvailable)
        {
            await _engine.SetVolumeAsync(volume, operation.Token).ConfigureAwait(false);
        }
    }

    private void OnEngineStateChanged(object? sender, PlaybackEngineStateChangedEventArgs args)
    {
        foreach (EventHandler<PlaybackEngineStateChangedEventArgs> handler in
                 StateChanged?.GetInvocationList() ?? [])
        {
            try
            {
                handler(this, args);
            }
            catch
            {
                // Playback policy must continue even if a UI subscriber fails.
            }
        }

        if (args.State.Status == PlaybackStatus.Ended)
        {
            var current = CurrentItem;
            if (current is not null)
            {
                AddRecent(current, args.State.Position);
            }

            _ = ContinueAfterEndAsync();
        }
    }

    private async Task ContinueAfterEndAsync()
    {
        try
        {
            await MoveAsync(forward: true, causedByEnd: true, CancellationToken.None).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposeState, 1, 0) == 0)
        {
            _ = DisposeCoreAsync();
        }

        return new ValueTask(_disposeCompletion.Task);
    }

    private async Task DisposeCoreAsync()
    {
        Exception? failure = null;
        try
        {
            _engine.StateChanged -= OnEngineStateChanged;
            _volumeRegistration.Dispose();
            _lifetimeCancellation.Cancel();

            Task drainTask;
            lock (_lifecycleSync)
            {
                drainTask = _activeOperations == 0
                    ? Task.CompletedTask
                    : (_operationsDrained ??=
                        new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            }

            await drainTask.ConfigureAwait(false);
            await _engine.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            _gate.Dispose();
            _lifetimeCancellation.Dispose();
            Volatile.Write(ref _disposeState, 2);
        }

        if (failure is null)
        {
            _disposeCompletion.TrySetResult();
        }
        else
        {
            _disposeCompletion.TrySetException(failure);
        }
    }

    private OperationLease BeginOperation(CancellationToken cancellationToken)
    {
        lock (_lifecycleSync)
        {
            ThrowIfDisposalStarted();
            _activeOperations++;
        }

        try
        {
            return new OperationLease(
                this,
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    _lifetimeCancellation.Token));
        }
        catch
        {
            EndOperation();
            throw;
        }
    }

    private void EndOperation()
    {
        TaskCompletionSource? drained = null;
        lock (_lifecycleSync)
        {
            _activeOperations--;
            if (_activeOperations == 0)
            {
                drained = _operationsDrained;
            }
        }

        drained?.TrySetResult();
    }

    private void ThrowIfDisposalStarted() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);

    private sealed class OperationLease(
        PlaybackController owner,
        CancellationTokenSource linkedCancellation) : IDisposable
    {
        private PlaybackController? _owner = owner;

        public CancellationToken Token => linkedCancellation.Token;

        public void Dispose()
        {
            var currentOwner = Interlocked.Exchange(ref _owner, null);
            if (currentOwner is null)
            {
                return;
            }

            try
            {
                linkedCancellation.Dispose();
            }
            finally
            {
                currentOwner.EndOperation();
            }
        }
    }
}
