using InternalAssetLibrary.Client.Core.LocalAssets;

namespace InternalAssetLibrary.Client.Core.Playback;

public enum PlaybackStatus
{
    Unavailable,
    Idle,
    Loading,
    Playing,
    Paused,
    Ended,
    Failed
}

public enum PlaybackRepeatMode
{
    Off,
    One,
    All
}

public enum PlaybackOrderMode
{
    Sequential,
    Shuffle
}

public enum PlaybackEngineUnavailableReason
{
    None,
    NativeLibraryNotFound,
    NativeApiIncomplete,
    InitializationFailed,
    UnsupportedPlatform
}

public sealed record PlaybackEngineAvailability(
    bool IsAvailable,
    PlaybackEngineUnavailableReason Reason,
    string? Diagnostic,
    string? NativeLibrary)
{
    public static PlaybackEngineAvailability Available(string nativeLibrary) =>
        new(true, PlaybackEngineUnavailableReason.None, null, nativeLibrary);
}

public sealed record PlaybackHttpHeader(string Name, string Value);

public sealed record PlaybackItem(
    string Key,
    string Source,
    string Title,
    LocalMediaType MediaType,
    IReadOnlyList<PlaybackHttpHeader>? HttpHeaders = null,
    bool ForceVideoOutput = false)
{
    public bool ShouldRenderVideo => MediaType == LocalMediaType.Video || ForceVideoOutput;

    public PlaybackItem ValidateAndNormalize()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Key);
        ArgumentException.ThrowIfNullOrWhiteSpace(Source);
        ArgumentException.ThrowIfNullOrWhiteSpace(Title);

        var headers = (HttpHeaders ?? [])
            .Select(header =>
            {
                ArgumentNullException.ThrowIfNull(header);
                if (string.IsNullOrWhiteSpace(header.Name) ||
                    header.Name.Any(character => !IsHttpTokenCharacter(character)))
                {
                    throw new ArgumentException("Playback HTTP header names must be non-empty tokens.", nameof(HttpHeaders));
                }

                if (header.Value.Any(character => character is ',' or '\r' or '\n'))
                {
                    throw new ArgumentException("Playback HTTP header values cannot contain commas or line breaks.", nameof(HttpHeaders));
                }

                return new PlaybackHttpHeader(header.Name.Trim(), header.Value.Trim());
            })
            .GroupBy(header => header.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToArray();

        return this with
        {
            Key = Key.Trim(),
            Source = Source.Trim(),
            Title = Title.Trim(),
            HttpHeaders = headers
        };
    }

    private static bool IsHttpTokenCharacter(char value) =>
        char.IsAsciiLetterOrDigit(value) || value is '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or
            '.' or '^' or '_' or '`' or '|' or '~';
}

public sealed record PlaybackAudioTrack(
    int Id,
    int? SourceStreamIndex,
    string? Title,
    string? Language,
    string? Codec,
    int? ChannelCount,
    bool IsDefault,
    bool IsSelected);

public sealed record PlaybackEngineState(
    PlaybackStatus Status,
    string? CurrentItemKey,
    string? MediaTitle,
    bool HasVideo,
    bool IsPaused,
    TimeSpan Position,
    TimeSpan? Duration,
    double Volume,
    double Speed,
    string? Error)
{
    public bool HasRenderableVideoFrame =>
        HasVideo && Status is PlaybackStatus.Playing or PlaybackStatus.Paused;

    public static PlaybackEngineState Unavailable(string? error) =>
        new(PlaybackStatus.Unavailable, null, null, false, false, TimeSpan.Zero, null, 100, 1, error);

    public static PlaybackEngineState Idle { get; } =
        new(PlaybackStatus.Idle, null, null, false, false, TimeSpan.Zero, null, 100, 1, null);
}

public sealed record PlaybackRecentEntry(
    PlaybackItem Item,
    DateTimeOffset LastPlayedAtUtc,
    TimeSpan LastPosition);

public enum PlaybackOperationFailure
{
    None,
    EngineUnavailable,
    EmptyQueue,
    EndOfQueue,
    InvalidRequest,
    EngineError
}

public sealed record PlaybackOperationResult(
    bool Succeeded,
    PlaybackOperationFailure Failure,
    string? Diagnostic)
{
    public static PlaybackOperationResult Success { get; } = new(true, PlaybackOperationFailure.None, null);

    public static PlaybackOperationResult Failed(PlaybackOperationFailure failure, string diagnostic) =>
        new(false, failure, diagnostic);
}

public sealed class PlaybackEngineStateChangedEventArgs(PlaybackEngineState state) : EventArgs
{
    public PlaybackEngineState State { get; } = state;
}

public interface IPlaybackEngine : IAsyncDisposable
{
    PlaybackEngineAvailability Availability { get; }

    PlaybackEngineState State { get; }

    event EventHandler<PlaybackEngineStateChangedEventArgs>? StateChanged;

    Task SetVideoHostAsync(nint nativeWindowHandle, CancellationToken cancellationToken = default);

    Task LoadAsync(PlaybackItem item, TimeSpan startPosition, CancellationToken cancellationToken = default);

    Task PlayAsync(CancellationToken cancellationToken = default);

    Task PauseAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default);

    Task SetVolumeAsync(double volume, CancellationToken cancellationToken = default);

    Task SetMutedAsync(bool muted, CancellationToken cancellationToken = default);

    Task SetSpeedAsync(double speed, CancellationToken cancellationToken = default);

    Task StepFrameAsync(bool forward, CancellationToken cancellationToken = default);

    Task TakeScreenshotAsync(string outputPath, CancellationToken cancellationToken = default);

    Task SetLutAsync(string? cubePath, CancellationToken cancellationToken = default);

    Task AddSubtitleAsync(string subtitlePath, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PlaybackAudioTrack>> GetAudioTracksAsync(CancellationToken cancellationToken = default);

    Task SetSelectedAudioTracksAsync(
        IReadOnlyCollection<int> trackIds,
        CancellationToken cancellationToken = default);
}
