namespace InternalAssetLibrary.Client.Core.Playback;

public sealed class PreviewPlaybackSession
{
    private static readonly TimeSpan EndRestartTolerance = TimeSpan.FromMilliseconds(250);
    private readonly object _sync = new();
    private readonly Dictionary<string, TimeSpan> _positions = new(StringComparer.Ordinal);
    private string? _activeAssetKey;
    private long _generation;

    public string? ActiveAssetKey
    {
        get
        {
            lock (_sync)
            {
                return _activeAssetKey;
            }
        }
    }

    public long Generation => Interlocked.Read(ref _generation);

    public TimeSpan Begin(string assetKey)
    {
        ValidateAssetKey(assetKey);
        lock (_sync)
        {
            _activeAssetKey = assetKey;
            return _positions.GetValueOrDefault(assetKey);
        }
    }

    public void SavePosition(string assetKey, TimeSpan position, TimeSpan? duration = null)
    {
        ValidateAssetKey(assetKey);
        var normalizedPosition = NormalizePosition(position, duration);
        lock (_sync)
        {
            _positions[assetKey] = normalizedPosition;
        }
    }

    public void End(string assetKey, TimeSpan position, TimeSpan? duration = null)
    {
        ValidateAssetKey(assetKey);
        var normalizedPosition = NormalizePosition(position, duration);
        lock (_sync)
        {
            _positions[assetKey] = normalizedPosition;
            if (StringComparer.Ordinal.Equals(_activeAssetKey, assetKey))
            {
                _activeAssetKey = null;
            }
        }
    }

    public TimeSpan GetPosition(string assetKey)
    {
        ValidateAssetKey(assetKey);
        lock (_sync)
        {
            return _positions.GetValueOrDefault(assetKey);
        }
    }

    public void Reset(string assetKey)
    {
        ValidateAssetKey(assetKey);
        lock (_sync)
        {
            _positions.Remove(assetKey);
            if (StringComparer.Ordinal.Equals(_activeAssetKey, assetKey))
            {
                _activeAssetKey = null;
            }
        }
    }

    public void ResetForLibraryRefresh()
    {
        lock (_sync)
        {
            _positions.Clear();
            _activeAssetKey = null;
            Interlocked.Increment(ref _generation);
        }
    }

    private static TimeSpan NormalizePosition(TimeSpan position, TimeSpan? duration)
    {
        if (position < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }

        if (!duration.HasValue)
        {
            return position;
        }

        if (duration.Value < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        if (duration.Value == TimeSpan.Zero ||
            position >= duration.Value - EndRestartTolerance)
        {
            return TimeSpan.Zero;
        }

        return position > duration.Value ? duration.Value : position;
    }

    private static void ValidateAssetKey(string assetKey) =>
        ArgumentException.ThrowIfNullOrWhiteSpace(assetKey);
}
