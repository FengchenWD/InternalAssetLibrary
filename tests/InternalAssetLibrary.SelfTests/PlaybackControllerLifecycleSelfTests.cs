using InternalAssetLibrary.Client.Core.LocalAssets;
using InternalAssetLibrary.Client.Core.Playback;

internal static class PlaybackControllerLifecycleSelfTests
{
    public static void ConcurrentDisposeCancelsAndDrainsPlaybackOperations() =>
        ConcurrentDisposeCancelsAndDrainsPlaybackOperationsAsync().GetAwaiter().GetResult();

    private static async Task ConcurrentDisposeCancelsAndDrainsPlaybackOperationsAsync()
    {
        var engine = new BlockingPlaybackEngine();
        var controller = new PlaybackController(engine);
        var item = new PlaybackItem(
            "concurrent-video",
            "C:/media/concurrent.mov",
            "Concurrent",
            LocalMediaType.Video);

        var openTask = controller.OpenAsync(item);
        await engine.LoadEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var seekTask = controller.SeekAsync(TimeSpan.FromSeconds(10));
        await engine.SeekEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var lutTask = controller.SetLutAsync("C:/media/look.cube");
        var tracksTask = controller.SetSelectedAudioTracksAsync([1, 2]);
        False(lutTask.IsCompleted);
        False(tracksTask.IsCompleted);

        var firstDispose = controller.DisposeAsync().AsTask();
        var secondDispose = controller.DisposeAsync().AsTask();

        await ExpectCancellationAsync(lutTask);
        await ExpectCancellationAsync(tracksTask);
        False(firstDispose.IsCompleted);
        Equal(0, engine.DisposeCount);

        engine.ReleaseOperations();
        await ExpectCancellationAsync(openTask);
        await ExpectCancellationAsync(seekTask);
        await firstDispose.WaitAsync(TimeSpan.FromSeconds(2));
        await secondDispose.WaitAsync(TimeSpan.FromSeconds(2));
        await controller.DisposeAsync();

        Equal(1, engine.DisposeCount);
        Throws<ObjectDisposedException>(() => controller.Enqueue(item));
    }

    private static async Task ExpectCancellationAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (OperationCanceledException)
        {
            return;
        }

        throw new InvalidOperationException("Expected the playback operation to be cancelled during disposal.");
    }

    private sealed class BlockingPlaybackEngine : IPlaybackEngine
    {
        private readonly TaskCompletionSource _releaseOperations =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private PlaybackEngineState _state = PlaybackEngineState.Idle;
        private int _disposeCount;

        public TaskCompletionSource LoadEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SeekEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public PlaybackEngineAvailability Availability { get; } =
            PlaybackEngineAvailability.Available("blocking-test-engine");

        public PlaybackEngineState State => _state;

        public event EventHandler<PlaybackEngineStateChangedEventArgs>? StateChanged
        {
            add { }
            remove { }
        }

        public Task SetVideoHostAsync(nint nativeWindowHandle, CancellationToken cancellationToken = default) =>
            CompletedAsync(cancellationToken);

        public async Task LoadAsync(
            PlaybackItem item,
            TimeSpan startPosition,
            CancellationToken cancellationToken = default)
        {
            LoadEntered.TrySetResult();
            await _releaseOperations.Task.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            _state = new PlaybackEngineState(
                PlaybackStatus.Playing,
                item.Key,
                item.Title,
                item.ShouldRenderVideo,
                false,
                startPosition,
                TimeSpan.FromMinutes(1),
                100,
                1,
                null);
        }

        public Task PlayAsync(CancellationToken cancellationToken = default) =>
            CompletedAsync(cancellationToken);

        public Task PauseAsync(CancellationToken cancellationToken = default) =>
            CompletedAsync(cancellationToken);

        public Task StopAsync(CancellationToken cancellationToken = default) =>
            CompletedAsync(cancellationToken);

        public async Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
        {
            SeekEntered.TrySetResult();
            await _releaseOperations.Task.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }

        public Task SetVolumeAsync(double volume, CancellationToken cancellationToken = default) =>
            CompletedAsync(cancellationToken);

        public Task SetMutedAsync(bool muted, CancellationToken cancellationToken = default) =>
            CompletedAsync(cancellationToken);

        public Task SetSpeedAsync(double speed, CancellationToken cancellationToken = default) =>
            CompletedAsync(cancellationToken);

        public Task StepFrameAsync(bool forward, CancellationToken cancellationToken = default) =>
            CompletedAsync(cancellationToken);

        public Task TakeScreenshotAsync(string outputPath, CancellationToken cancellationToken = default) =>
            CompletedAsync(cancellationToken);

        public Task SetLutAsync(string? cubePath, CancellationToken cancellationToken = default) =>
            CompletedAsync(cancellationToken);

        public Task AddSubtitleAsync(string subtitlePath, CancellationToken cancellationToken = default) =>
            CompletedAsync(cancellationToken);

        public Task<IReadOnlyList<PlaybackAudioTrack>> GetAudioTracksAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<PlaybackAudioTrack>>([]);
        }

        public Task SetSelectedAudioTracksAsync(
            IReadOnlyCollection<int> trackIds,
            CancellationToken cancellationToken = default) =>
            CompletedAsync(cancellationToken);

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            return ValueTask.CompletedTask;
        }

        public void ReleaseOperations() => _releaseOperations.TrySetResult();

        private static Task CompletedAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private static void False(bool condition)
    {
        if (condition)
        {
            throw new InvalidOperationException("Expected false, but was true.");
        }
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', but was '{actual}'.");
        }
    }

    private static void Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }
}
