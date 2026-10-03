using System.Globalization;
using InternalAssetLibrary.Client.Core.MediaAnalysis;

namespace InternalAssetLibrary.Client.Core.Editor;

public static class EditorTemporaryVideoService
{
    public static MediaToolCommand BuildAudioExportCommand(
        string inputPath,
        string outputPath,
        TimeSpan inPoint,
        TimeSpan outPoint,
        double volumeDb,
        AudioExportPreset preset = AudioExportPreset.Wav,
        string ffmpegExecutable = "ffmpeg")
        => BuildAudioExportCommand(
            inputPath,
            outputPath,
            inPoint,
            outPoint,
            volumeDb,
            preset,
            bitrateKbps: 192,
            ffmpegExecutable);

    public static MediaToolCommand BuildAudioExportCommand(
        string inputPath,
        string outputPath,
        TimeSpan inPoint,
        TimeSpan outPoint,
        double volumeDb,
        AudioExportPreset preset,
        int bitrateKbps,
        string ffmpegExecutable = "ffmpeg")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (inPoint < TimeSpan.Zero || outPoint <= inPoint)
        {
            throw new ArgumentOutOfRangeException(nameof(outPoint), "The output point must be later than the input point.");
        }

        if (bitrateKbps is < 32 or > 1536)
        {
            throw new ArgumentOutOfRangeException(nameof(bitrateKbps), "Audio bitrate must be between 32 and 1536 kbps.");
        }

        var input = Path.GetFullPath(inputPath);
        var output = Path.GetFullPath(outputPath);
        if (string.Equals(input, output, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new ArgumentException("The export must not overwrite the source audio.", nameof(outputPath));
        }

        var arguments = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostdin",
            "-ss", FormatSeconds(inPoint),
            "-i", input,
            "-t", FormatSeconds(outPoint - inPoint),
            "-map", "0:a:0",
            "-vn",
            "-map_metadata", "0",
            "-af", $"volume={Math.Clamp(volumeDb, -60, 12).ToString("0.###", CultureInfo.InvariantCulture)}dB"
        };
        arguments.AddRange(preset switch
        {
            AudioExportPreset.Flac => ["-c:a", "flac", "-compression_level", "8"],
            AudioExportPreset.Wav => ["-c:a", "pcm_s24le"],
            AudioExportPreset.Mp3 => ["-c:a", "libmp3lame", "-b:a", $"{bitrateKbps}k"],
            AudioExportPreset.Aac => ["-c:a", "aac", "-b:a", $"{bitrateKbps}k"],
            _ => throw new ArgumentOutOfRangeException(nameof(preset))
        });
        arguments.AddRange(["-y", output]);
        return new MediaToolCommand(ffmpegExecutable, arguments).Validate();
    }

    public static MediaToolCommand BuildExportCommand(
        string inputPath,
        string outputPath,
        TimeSpan inPoint,
        TimeSpan outPoint,
        double volumeDb,
        string format,
        int width,
        int height,
        double frameRate,
        string ffmpegExecutable = "ffmpeg")
        => BuildExportCommand(
            inputPath,
            outputPath,
            inPoint,
            outPoint,
            volumeDb,
            format,
            width,
            height,
            frameRate,
            videoBitrateKbps: 8000,
            audioBitrateKbps: 192,
            ffmpegExecutable);

    public static MediaToolCommand BuildExportCommand(
        string inputPath,
        string outputPath,
        TimeSpan inPoint,
        TimeSpan outPoint,
        double volumeDb,
        string format,
        int width,
        int height,
        double frameRate,
        int videoBitrateKbps,
        int audioBitrateKbps = 192,
        string ffmpegExecutable = "ffmpeg")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(format);
        if (width < 16 || height < 16 || frameRate <= 0 || !double.IsFinite(frameRate))
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Export dimensions and frame rate must be positive.");
        }

        if (videoBitrateKbps is < 128 or > 200000)
        {
            throw new ArgumentOutOfRangeException(nameof(videoBitrateKbps), "Video bitrate must be between 128 and 200000 kbps.");
        }

        if (audioBitrateKbps is < 32 or > 1536)
        {
            throw new ArgumentOutOfRangeException(nameof(audioBitrateKbps), "Audio bitrate must be between 32 and 1536 kbps.");
        }

        var normalizedOutput = Path.ChangeExtension(outputPath, "." + format.Trim().ToLowerInvariant());
        var command = BuildExportCommand(inputPath, normalizedOutput, inPoint, outPoint, volumeDb, ffmpegExecutable);
        var args = command.Arguments.ToList();
        var outputIndex = args.Count - 1;
        args.Insert(outputIndex, "-vf");
        args.Insert(outputIndex + 1, $"scale={width}:{height},fps={frameRate.ToString("0.###", CultureInfo.InvariantCulture)}");
        args.Insert(outputIndex, "-b:v");
        args.Insert(outputIndex + 1, $"{videoBitrateKbps}k");
        var audioBitrateIndex = args.IndexOf("-b:a");
        if (audioBitrateIndex >= 0)
        {
            args[audioBitrateIndex + 1] = $"{audioBitrateKbps}k";
        }
        return new MediaToolCommand(command.Executable, args).Validate();
    }

    public static MediaToolCommand BuildExportCommand(
        string inputPath,
        string outputPath,
        TimeSpan inPoint,
        TimeSpan outPoint,
        double volumeDb,
        string ffmpegExecutable = "ffmpeg")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (inPoint < TimeSpan.Zero || outPoint <= inPoint)
        {
            throw new ArgumentOutOfRangeException(nameof(outPoint), "The output point must be later than the input point.");
        }

        var input = Path.GetFullPath(inputPath);
        var output = Path.GetFullPath(outputPath);
        if (string.Equals(input, output, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new ArgumentException("The export must not overwrite the source video.", nameof(outputPath));
        }

        var duration = outPoint - inPoint;
        var arguments = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostdin",
            "-ss", FormatSeconds(inPoint),
            "-i", input,
            "-t", FormatSeconds(duration),
            "-map", "0:v:0",
            "-map", "0:a?",
            "-map_metadata", "0",
            "-c:v", "libx264",
            "-preset", "medium",
            "-crf", "18",
            "-pix_fmt", "yuv420p",
            "-c:a", "aac",
            "-b:a", "192k",
            "-af", $"volume={Math.Clamp(volumeDb, -60, 12).ToString("0.###", CultureInfo.InvariantCulture)}dB",
            "-movflags", "+faststart",
            "-y",
            output
        };

        return new MediaToolCommand(ffmpegExecutable, arguments).Validate();
    }

    private static string FormatSeconds(TimeSpan value) =>
        value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
}
