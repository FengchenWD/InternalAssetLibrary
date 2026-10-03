using System.Globalization;
using System.Text.Json;

namespace InternalAssetLibrary.Client.Core.MediaAnalysis;

public enum MediaStreamKind
{
    Unknown,
    Video,
    Audio,
    Subtitle,
    Data,
    Attachment
}

public sealed record MediaStreamInfo(
    int Index,
    MediaStreamKind Kind,
    string? CodecName,
    string? CodecLongName,
    string? Profile,
    TimeSpan? Duration,
    long? BitRate,
    int? Width,
    int? Height,
    double? FrameRate,
    string? PixelFormat,
    string? ColorSpace,
    string? ColorTransfer,
    string? ColorPrimaries,
    int? SampleRate,
    int? ChannelCount,
    string? ChannelLayout,
    string? Language,
    string? Title,
    bool IsDefault,
    bool IsForced,
    bool IsAttachedPicture);

public sealed record MediaAudioTrackInfo(
    int StreamIndex,
    string? Codec,
    string? Language,
    string? Title,
    int? SampleRate,
    int? ChannelCount,
    string? ChannelLayout,
    bool IsDefault);

public sealed record MediaInformation(
    string SourcePath,
    string? FormatName,
    string? FormatLongName,
    TimeSpan? Duration,
    long? SizeBytes,
    long? BitRate,
    IReadOnlyList<MediaStreamInfo> Streams,
    IReadOnlyDictionary<string, string> Tags)
{
    public IReadOnlyList<MediaAudioTrackInfo> AudioTracks => Streams
        .Where(stream => stream.Kind == MediaStreamKind.Audio)
        .Select(stream => new MediaAudioTrackInfo(
            stream.Index,
            stream.CodecName,
            stream.Language,
            stream.Title,
            stream.SampleRate,
            stream.ChannelCount,
            stream.ChannelLayout,
            stream.IsDefault))
        .ToArray();

    public bool HasVideo => Streams.Any(stream =>
        stream.Kind == MediaStreamKind.Video && !stream.IsAttachedPicture);
}

public sealed record MediaProbeResult(
    bool Succeeded,
    MediaToolAvailability Availability,
    MediaInformation? Information,
    string? Diagnostic);

public sealed class FfprobeMediaAnalyzer
{
    private readonly string _ffprobeExecutable;
    private readonly IMediaToolRunner _runner;

    public FfprobeMediaAnalyzer(
        string ffprobeExecutable = "ffprobe",
        IMediaToolRunner? runner = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ffprobeExecutable);
        _ffprobeExecutable = ffprobeExecutable.Trim();
        _runner = runner ?? new ProcessMediaToolRunner();
    }

    public MediaToolAvailability Availability => MediaToolLocator.Locate(_ffprobeExecutable);

    public async Task<MediaProbeResult> ProbeAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var source = Path.GetFullPath(sourcePath);
        if (!File.Exists(source))
        {
            throw new FileNotFoundException("The media file does not exist.", source);
        }

        var availability = Availability;
        if (!availability.IsAvailable)
        {
            return new MediaProbeResult(false, availability, null, availability.Diagnostic);
        }

        var command = BuildProbeCommand(availability.ResolvedExecutable!, source);
        MediaToolExecutionResult execution;
        try
        {
            execution = await _runner.RunAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new MediaProbeResult(false, availability, null, exception.Message);
        }

        if (!execution.Succeeded)
        {
            return new MediaProbeResult(
                false,
                availability,
                null,
                string.IsNullOrWhiteSpace(execution.StandardError)
                    ? $"ffprobe exited with code {execution.ExitCode}."
                    : execution.StandardError.Trim());
        }

        try
        {
            return new MediaProbeResult(
                true,
                availability,
                FfprobeJsonParser.Parse(execution.StandardOutput, source),
                null);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or FormatException)
        {
            return new MediaProbeResult(false, availability, null, $"ffprobe returned invalid JSON: {exception.Message}");
        }
    }

    public static MediaToolCommand BuildProbeCommand(string ffprobeExecutable, string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ffprobeExecutable);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        return new MediaToolCommand(
            ffprobeExecutable,
            [
                "-v", "error",
                "-print_format", "json",
                "-show_format",
                "-show_streams",
                // Do not include format.filename: some Windows ffprobe builds emit
                // unescaped backslashes there, which makes their JSON invalid.
                "-show_entries", "stream:format=format_name,format_long_name,duration,size,bit_rate,tags",
                Path.GetFullPath(sourcePath)
            ]);
    }
}

public static class FfprobeJsonParser
{
    public static MediaInformation Parse(string json, string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The ffprobe root value must be an object.");
        }

        var root = document.RootElement;
        var streams = new List<MediaStreamInfo>();
        if (root.TryGetProperty("streams", out var streamArray) && streamArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var stream in streamArray.EnumerateArray())
            {
                streams.Add(ParseStream(stream));
            }
        }

        var format = root.TryGetProperty("format", out var formatValue) && formatValue.ValueKind == JsonValueKind.Object
            ? formatValue
            : default;
        return new MediaInformation(
            Path.GetFullPath(sourcePath),
            GetString(format, "format_name"),
            GetString(format, "format_long_name"),
            GetDuration(format, "duration"),
            GetLong(format, "size"),
            GetLong(format, "bit_rate"),
            streams.OrderBy(stream => stream.Index).ToArray(),
            GetTags(format));
    }

    private static MediaStreamInfo ParseStream(JsonElement stream)
    {
        var disposition = stream.TryGetProperty("disposition", out var dispositionValue) &&
                          dispositionValue.ValueKind == JsonValueKind.Object
            ? dispositionValue
            : default;
        var tags = GetTags(stream);
        return new MediaStreamInfo(
            GetInt(stream, "index") ?? throw new InvalidDataException("A media stream is missing its index."),
            ParseKind(GetString(stream, "codec_type")),
            GetString(stream, "codec_name"),
            GetString(stream, "codec_long_name"),
            GetString(stream, "profile"),
            GetDuration(stream, "duration"),
            GetLong(stream, "bit_rate"),
            GetInt(stream, "width"),
            GetInt(stream, "height"),
            ParseRational(GetString(stream, "avg_frame_rate")) ?? ParseRational(GetString(stream, "r_frame_rate")),
            GetString(stream, "pix_fmt"),
            GetString(stream, "color_space"),
            GetString(stream, "color_transfer"),
            GetString(stream, "color_primaries"),
            GetInt(stream, "sample_rate"),
            GetInt(stream, "channels"),
            GetString(stream, "channel_layout"),
            GetTag(tags, "language"),
            GetTag(tags, "title"),
            GetBoolean(disposition, "default"),
            GetBoolean(disposition, "forced"),
            GetBoolean(disposition, "attached_pic"));
    }

    private static MediaStreamKind ParseKind(string? value) => value switch
    {
        "video" => MediaStreamKind.Video,
        "audio" => MediaStreamKind.Audio,
        "subtitle" => MediaStreamKind.Subtitle,
        "data" => MediaStreamKind.Data,
        "attachment" => MediaStreamKind.Attachment,
        _ => MediaStreamKind.Unknown
    };

    private static string? GetString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static int? GetInt(JsonElement element, string name) =>
        int.TryParse(GetString(element, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static long? GetLong(JsonElement element, string name) =>
        long.TryParse(GetString(element, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static TimeSpan? GetDuration(JsonElement element, string name) =>
        double.TryParse(GetString(element, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) &&
        double.IsFinite(seconds) && seconds >= 0
            ? TimeSpan.FromSeconds(seconds)
            : null;

    private static bool GetBoolean(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => value.TryGetInt32(out var number) && number != 0,
            JsonValueKind.String => value.GetString() is "1" or "true" or "yes",
            _ => false
        };
    }

    private static double? ParseRational(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var parts = value.Split('/', 2);
        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) &&
            denominator != 0)
        {
            var ratio = numerator / denominator;
            return double.IsFinite(ratio) && ratio > 0 ? ratio : null;
        }

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
               double.IsFinite(parsed) && parsed > 0
            ? parsed
            : null;
    }

    private static IReadOnlyDictionary<string, string> GetTags(JsonElement element)
    {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("tags", out var value) ||
            value.ValueKind != JsonValueKind.Object)
        {
            return tags;
        }

        foreach (var property in value.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is { } text)
            {
                tags[property.Name] = text;
            }
        }

        return tags;
    }

    private static string? GetTag(IReadOnlyDictionary<string, string> tags, string name) =>
        tags.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
}
