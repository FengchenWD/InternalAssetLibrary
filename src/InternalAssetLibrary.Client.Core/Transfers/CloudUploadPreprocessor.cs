using InternalAssetLibrary.Client.Core.LocalAssets;
using InternalAssetLibrary.Client.Core.MediaAnalysis;
using System.Text.Json;

namespace InternalAssetLibrary.Client.Core.Transfers;

public sealed record PreparedCloudUpload(string UploadPath, bool IsTemporary)
{
    public static PreparedCloudUpload Original(string path) => new(path, false);
}

/// <summary>Applies the cloud upload compatibility policy without changing the user's source file.</summary>
public sealed class CloudUploadPreprocessor
{
    private readonly string _ffmpegExecutable;
    private readonly IMediaToolRunner _runner;
    private readonly string _ffprobeExecutable;

    public CloudUploadPreprocessor(string ffmpegExecutable, IMediaToolRunner? runner = null, string? ffprobeExecutable = null)
    {
        _ffmpegExecutable = ffmpegExecutable;
        _runner = runner ?? new ProcessMediaToolRunner();
        _ffprobeExecutable = ffprobeExecutable ?? Path.Combine(Path.GetDirectoryName(ffmpegExecutable) ?? "",
            Path.HasExtension(ffmpegExecutable) ? "ffprobe.exe" : "ffprobe");
    }

    public async Task<PreparedCloudUpload> PrepareAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var extension = Path.GetExtension(fullPath).ToLowerInvariant();
        if (extension == ".psd")
        {
            return PreparedCloudUpload.Original(fullPath);
        }

        if (!MediaExtensionClassifier.TryClassify(fullPath, out var mediaType))
        {
            throw new InvalidDataException($"不支持上传的文件格式：{extension}");
        }
        var policy = await ProbeAsync(fullPath, mediaType == LocalMediaType.Image, cancellationToken).ConfigureAwait(false);
        if (mediaType == LocalMediaType.Image && await HasAnimationContainerAsync(fullPath, cancellationToken))
            policy = policy with { Animated = true };
        if (IsDirectPremiereFormat(extension) && !(mediaType == LocalMediaType.Image && policy.Animated && extension != ".gif") && (mediaType != LocalMediaType.Video ||
            policy.Codec is "h264" or "hevc" or "prores" or "qtrle" or "mjpeg" or "png"))
            return PreparedCloudUpload.Original(fullPath);

        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "InternalAssetLibrary", "cloud-upload", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        var outputExtension = mediaType switch
        {
            LocalMediaType.Audio => ".mp3",
            LocalMediaType.Video => policy.HasAlpha ? ".mov" : ".mp4",
            LocalMediaType.Image when policy.Animated => ".gif",
            LocalMediaType.Image => policy.HasAlpha ? ".png" : ".jpg",
            _ => throw new InvalidDataException("无法判断上传文件类型。")
        };
        var output = Path.Combine(temporaryDirectory, Path.GetFileNameWithoutExtension(fullPath) + outputExtension);
        string[] args = mediaType switch
        {
            LocalMediaType.Audio => ["-hide_banner", "-loglevel", "error", "-nostdin", "-i", fullPath, "-map_metadata", "0", "-c:a", "libmp3lame", "-b:a", "320k", "-y", output],
            LocalMediaType.Video when policy.HasAlpha => ["-hide_banner", "-loglevel", "error", "-nostdin", "-i", fullPath, "-map", "0:v:0", "-map", "0:a?", "-c:v", "prores_ks", "-profile:v", "4", "-pix_fmt", "yuva444p10le", "-alpha_bits", "16", "-c:a", "pcm_s16le", "-y", output],
            LocalMediaType.Video => ["-hide_banner", "-loglevel", "error", "-nostdin", "-i", fullPath, "-map", "0:v:0", "-map", "0:a?", "-c:v", "libx264", "-preset", "medium", "-crf", "18", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", "-y", output],
            LocalMediaType.Image when policy.Animated => ["-hide_banner", "-loglevel", "error", "-nostdin", "-i", fullPath, "-filter_complex", "[0:v]split[a][b];[a]palettegen=reserve_transparent=1[p];[b][p]paletteuse=alpha_threshold=128", "-loop", "0", "-y", output],
            LocalMediaType.Image => ["-hide_banner", "-loglevel", "error", "-nostdin", "-i", fullPath, "-frames:v", "1", "-q:v", "2", "-y", output],
            _ => throw new InvalidDataException("无法转换该文件类型。")
        };
        // FFmpeg's native VP8/VP9 decoder can discard WebM alpha. Select the alpha-capable decoder before the input.
        if (policy.HasAlpha && policy.Codec is "vp8" or "vp9")
        {
            var inputIndex = Array.IndexOf(args, "-i");
            args = [..args[..inputIndex], "-c:v", policy.Codec == "vp9" ? "libvpx-vp9" : "libvpx", ..args[inputIndex..]];
        }
        try
        {
        var result = await _runner.RunAsync(new MediaToolCommand(_ffmpegExecutable, args).Validate(), cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded || !File.Exists(output) || new FileInfo(output).Length == 0)
        {
            TryDelete(output);
            throw new InvalidDataException(string.IsNullOrWhiteSpace(result.StandardError) ? "上传前转码失败。" : result.StandardError.Trim());
        }

        var converted = await ProbeAsync(output, mediaType == LocalMediaType.Image, cancellationToken).ConfigureAwait(false);
        if ((policy.HasAlpha && !converted.HasAlpha) ||
            (mediaType == LocalMediaType.Image && policy.Animated && !converted.Animated))
            throw new InvalidDataException("转码无法可靠保留透明通道或动画，已停止上传；请使用兼容格式。 ");
        return new PreparedCloudUpload(output, true);
        }
        catch { TryDelete(output); throw; }
    }

    private async Task<UploadMediaPolicy> ProbeAsync(string path, bool countFrames, CancellationToken token)
    {
        var arguments = new List<string> { "-v", "error", "-show_streams", "-of", "json" };
        if (countFrames) arguments.Add("-count_frames");
        arguments.Add(path);
        var result = await _runner.RunAsync(new MediaToolCommand(_ffprobeExecutable, arguments), token).ConfigureAwait(false);
        if (!result.Succeeded) throw new InvalidDataException("无法读取素材编码和透明通道，已停止上传以保护原文件。 ");
        return ReadPolicy(result.StandardOutput);
    }

    internal static UploadMediaPolicy ReadPolicy(string json)
    {
        using var document = JsonDocument.Parse(json);
        foreach (var stream in document.RootElement.GetProperty("streams").EnumerateArray())
        {
            if (!stream.TryGetProperty("codec_type", out var kind) || kind.GetString() != "video") continue;
            var pixel = stream.TryGetProperty("pix_fmt", out var format) ? format.GetString() ?? "" : "";
            var alpha = pixel is "rgba" or "argb" or "bgra" or "abgr" or "pal8" || pixel.StartsWith("rgba64") || pixel.StartsWith("bgra64") ||
                pixel.StartsWith("yuva") || pixel.StartsWith("gbrap") || pixel.StartsWith("ya");
            if (stream.TryGetProperty("tags", out var tags) && tags.TryGetProperty("alpha_mode", out var mode)) alpha |= mode.ToString() == "1";
            var frames = stream.TryGetProperty("nb_read_frames", out var count) ? count.ToString() :
                stream.TryGetProperty("nb_frames", out count) ? count.ToString() : "0";
            return new UploadMediaPolicy(stream.GetProperty("codec_name").GetString() ?? "", alpha,
                long.TryParse(frames, out var number) && number > 1);
        }
        return new UploadMediaPolicy("", false, false);
    }

    internal sealed record UploadMediaPolicy(string Codec, bool HasAlpha, bool Animated);

    private static async Task<bool> HasAnimationContainerAsync(string path, CancellationToken token)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".webp" or ".png" or ".apng")) return false;
        await using var input = File.OpenRead(path);
        input.Position = extension == ".webp" ? 12 : 8;
        var header = new byte[8];
        while (input.Position + 8 <= input.Length)
        {
            await input.ReadExactlyAsync(header, token);
            var webp = extension == ".webp";
            var name = System.Text.Encoding.ASCII.GetString(header, webp ? 0 : 4, 4);
            if (name is "ANIM" or "acTL") return true;
            var length = webp ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)) :
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header);
            var skip = webp ? (long)length + (length & 1) : (long)length + 4;
            if (skip > input.Length - input.Position) throw new InvalidDataException("图片容器损坏，已停止上传。");
            input.Position += skip;
        }
        return false;
    }

    private static bool IsDirectPremiereFormat(string extension) => extension switch
    {
        ".mp4" or ".mov" or ".mp3" or ".wav" or ".aac" or ".m4a" or ".jpg" or ".jpeg" or ".png" or ".gif" => true,
        _ => false
    };

    public static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
