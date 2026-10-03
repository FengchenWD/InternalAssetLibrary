namespace InternalAssetLibrary.Client.Core.Playback;

public sealed class PlaybackVolumeCoordinator
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, Func<double, CancellationToken, Task>> _targets = [];
    private double _volume;

    public PlaybackVolumeCoordinator(double initialVolume = 100)
    {
        ValidateVolume(initialVolume);
        _volume = initialVolume;
    }

    public double Volume
    {
        get
        {
            lock (_sync)
            {
                return _volume;
            }
        }
    }

    internal IDisposable Register(Func<double, CancellationToken, Task> target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var id = Guid.NewGuid();
        lock (_sync)
        {
            _targets.Add(id, target);
        }

        return new Registration(this, id);
    }

    public async Task SetVolumeAsync(double volume, CancellationToken cancellationToken = default)
    {
        ValidateVolume(volume);
        Func<double, CancellationToken, Task>[] targets;
        lock (_sync)
        {
            _volume = volume;
            targets = _targets.Values.ToArray();
        }

        cancellationToken.ThrowIfCancellationRequested();
        await Task.WhenAll(targets.Select(target => target(volume, cancellationToken))).ConfigureAwait(false);
    }

    private void Unregister(Guid id)
    {
        lock (_sync)
        {
            _targets.Remove(id);
        }
    }

    private static void ValidateVolume(double volume)
    {
        if (!double.IsFinite(volume) || volume is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(volume), "Playback volume must be between 0 and 100.");
        }
    }

    private sealed class Registration(PlaybackVolumeCoordinator owner, Guid id) : IDisposable
    {
        private PlaybackVolumeCoordinator? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Unregister(id);
    }
}
