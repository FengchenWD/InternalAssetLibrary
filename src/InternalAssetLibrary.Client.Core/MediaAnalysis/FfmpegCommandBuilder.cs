using System.Globalization;

namespace InternalAssetLibrary.Client.Core.MediaAnalysis;

public enum LutExportPreset
{
    ProRes422Hq,
    HighQualityH264
}

public enum AudioExportPreset
{
    Flac,
    Wav,
    Mp3,
    Aac
}

public static class FfmpegCommandBuilder
{
    public static MediaToolCommand BuildThumbnail(
        string inputPath,
        string outputPath,
        int maximumEdge = 640,
        string ffmpegExecutable = "ffmpeg",
        bool overwrite = true)
    {
        ValidateDimension(maximumEdge, 64, 4096, nameof(maximumEdge));
        var (input, output) = NormalizeInputOutput(inputPath, outputPath);
        return Command(
            ffmpegExecutable,
            [
                "-hide_banner", "-loglevel", "error", "-nostdin",
                "-i", input,
                "-map", "0:v:0",
                "-frames:v", "1",
                "-vf", $"scale={maximumEdge}:{maximumEdge}:force_original_aspect_ratio=decrease",
                "-c:v", "libwebp",
                "-quality", "82",
                overwrite ? "-y" : "-n",
                output
            ]);
    }

    public static MediaToolCommand BuildWaveform(
        string inputPath,
        string outputPath,
        int width = 2048,
        int height = 256,
        string color = "0x22D3EE",
        string ffmpegExecutable = "ffmpeg",
        bool overwrite = true)
    {
        ValidateDimension(width, 64, 8192, nameof(width));
        ValidateDimension(height, 64, 2048, nameof(height));
        ArgumentException.ThrowIfNullOrWhiteSpace(color);
        if (color.Any(character => character is ';' or '[' or ']' or ',' or '\r' or '\n'))
        {
            throw new ArgumentException("Waveform color contains filter syntax characters.", nameof(color));
        }

        var (input, output) = NormalizeInputOutput(inputPath, outputPath);
        var filter = $"[0:a:0]aformat=channel_layouts=mono,volume=-12dB,showwavespic=s={width}x{height}:split_channels=0:scale=sqrt:filter=average:colors={color}[wave]";
        return Command(
            ffmpegExecutable,
            [
                "-hide_banner", "-loglevel", "error", "-nostdin",
                "-i", input,
                "-filter_complex", filter,
                "-map", "[wave]",
                "-frames:v", "1",
                overwrite ? "-y" : "-n",
                output
            ]);
    }

    public static MediaToolCommand BuildFilmstrip(
        string inputPath,
        string outputPath,
        TimeSpan duration,
        int frameCount = 10,
        int frameWidth = 320,
        int frameHeight = 180,
        string ffmpegExecutable = "ffmpeg",
        bool overwrite = true)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        ValidateDimension(frameCount, 2, 100, nameof(frameCount));
        ValidateDimension(frameWidth, 64, 1920, nameof(frameWidth));
        ValidateDimension(frameHeight, 64, 1080, nameof(frameHeight));
        var (input, output) = NormalizeInputOutput(inputPath, outputPath);
        var framesPerSecond = frameCount / duration.TotalSeconds;
        // Every tile cell must have exactly the same dimensions. The previous
        // aspect-ratio-preserving scale followed by a fixed pad could make
        // FFmpeg reject a frame that was one pixel larger than the pad target.
        var filter = string.Join(',',
            $"fps={framesPerSecond.ToString("0.########", CultureInfo.InvariantCulture)}:round=up",
            $"scale={frameWidth}:{frameHeight}",
            "setsar=1",
            "tpad=stop_mode=clone:stop_duration=1",
            $"tile={frameCount}x1:padding=0:margin=0");
        return Command(
            ffmpegExecutable,
            [
                "-hide_banner", "-loglevel", "error", "-nostdin",
                "-i", input,
                "-map", "0:v:0",
                "-an",
                "-frames:v", "1",
                "-vf", filter,
                "-c:v", "libwebp",
                "-quality", "78",
                overwrite ? "-y" : "-n",
                output
            ]);
    }

    public static MediaToolCommand BuildLutExport(
        string inputPath,
        string lutPath,
        string outputPath,
        LutExportPreset preset = LutExportPreset.ProRes422Hq,
        string ffmpegExecutable = "ffmpeg",
        bool overwrite = false)
        => BuildVideoExport(
            inputPath,
            outputPath,
            lutPath: lutPath,
            preset: preset,
            ffmpegExecutable: ffmpegExecutable,
            overwrite: overwrite);

    public static MediaToolCommand BuildVideoExport(
        string inputPath,
        string outputPath,
        TimeSpan? inPoint = null,
        TimeSpan? outPoint = null,
        string? lutPath = null,
        LutExportPreset preset = LutExportPreset.ProRes422Hq,
        string ffmpegExecutable = "ffmpeg",
        bool overwrite = false)
    {
        var (input, output) = NormalizeInputOutput(inputPath, outputPath);
        ValidateTrimRange(inPoint, outPoint);

        string? lut = null;
        if (!string.IsNullOrWhiteSpace(lutPath))
        {
            lut = Path.GetFullPath(lutPath);
            if (!string.Equals(Path.GetExtension(lut), ".cube", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("LUT export requires a .cube file.", nameof(lutPath));
            }

            if (PathsEqual(input, lut) || PathsEqual(output, lut))
            {
                throw new ArgumentException("Input, output, and LUT paths must be different.");
            }
        }

        var arguments = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostdin",
            "-i", input
        };
        AddTrimArguments(arguments, inPoint, outPoint);
        arguments.AddRange(["-map", "0:v:0", "-map", "0:a?"]);
        if (lut is not null)
        {
            arguments.AddRange(["-vf", BuildLutPreviewFilter(lut)]);
        }

        arguments.AddRange(["-map_metadata", "0"]);
        switch (preset)
        {
            case LutExportPreset.ProRes422Hq:
                arguments.AddRange([
                    "-c:v", "prores_ks",
                    "-profile:v", "3",
                    "-pix_fmt", "yuv422p10le",
                    "-c:a", "pcm_s24le"
                ]);
                break;
            case LutExportPreset.HighQualityH264:
                arguments.AddRange([
                    "-c:v", "libx264",
                    "-preset", "medium",
                    "-crf", "16",
                    "-pix_fmt", "yuv420p",
                    "-c:a", "aac",
                    "-b:a", "256k",
                    "-movflags", "+faststart"
                ]);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(preset));
        }

        arguments.Add(overwrite ? "-y" : "-n");
        arguments.Add(output);
        return Command(ffmpegExecutable, arguments);
    }

    public static MediaToolCommand BuildAudioTrimExport(
        string inputPath,
        string outputPath,
        TimeSpan inPoint,
        TimeSpan outPoint,
        AudioExportPreset preset = AudioExportPreset.Flac,
        string ffmpegExecutable = "ffmpeg",
        bool overwrite = false)
    {
        var (input, output) = NormalizeInputOutput(inputPath, outputPath);
        ValidateTrimRange(inPoint, outPoint);
        var arguments = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostdin",
            "-i", input
        };
        AddTrimArguments(arguments, inPoint, outPoint);
        arguments.AddRange(["-map", "0:a:0", "-vn", "-map_metadata", "0"]);
        switch (preset)
        {
            case AudioExportPreset.Flac:
                arguments.AddRange(["-c:a", "flac", "-compression_level", "8"]);
                break;
            case AudioExportPreset.Wav:
                arguments.AddRange(["-c:a", "pcm_s24le"]);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(preset));
        }

        arguments.Add(overwrite ? "-y" : "-n");
        arguments.Add(output);
        return Command(ffmpegExecutable, arguments);
    }

    public static string BuildLutPreviewFilter(string lutPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lutPath);
        var fullPath = Path.GetFullPath(lutPath).Replace('\\', '/');
        var escaped = fullPath
            .Replace(":", "\\:", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal);
        return $"lut3d=file='{escaped}'";
    }

    private static MediaToolCommand Command(string executable, IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        return new MediaToolCommand(executable.Trim(), arguments).Validate();
    }

    private static (string Input, string Output) NormalizeInputOutput(string inputPath, string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var input = Path.GetFullPath(inputPath);
        var output = Path.GetFullPath(outputPath);
        if (PathsEqual(input, output))
        {
            throw new ArgumentException("Media processing output must not overwrite its input path.", nameof(outputPath));
        }

        return (input, output);
    }

    private static void ValidateTrimRange(TimeSpan? inPoint, TimeSpan? outPoint)
    {
        if (inPoint < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(inPoint));
        }

        if (outPoint < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(outPoint));
        }

        if (outPoint.HasValue && outPoint.Value <= (inPoint ?? TimeSpan.Zero))
        {
            throw new ArgumentException("The export out point must be later than its in point.", nameof(outPoint));
        }
    }

    private static void AddTrimArguments(
        ICollection<string> arguments,
        TimeSpan? inPoint,
        TimeSpan? outPoint)
    {
        if (inPoint.HasValue)
        {
            arguments.Add("-ss");
            arguments.Add(FormatSeconds(inPoint.Value));
        }

        if (outPoint.HasValue)
        {
            arguments.Add("-t");
            arguments.Add(FormatSeconds(outPoint.Value - (inPoint ?? TimeSpan.Zero)));
        }
    }

    private static string FormatSeconds(TimeSpan value) =>
        value.TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture);

    private static bool PathsEqual(string first, string second) =>
        string.Equals(
            Path.GetFullPath(first),
            Path.GetFullPath(second),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void ValidateDimension(int value, int minimum, int maximum, string parameterName)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(parameterName, $"Value must be between {minimum} and {maximum}.");
        }
    }
}
