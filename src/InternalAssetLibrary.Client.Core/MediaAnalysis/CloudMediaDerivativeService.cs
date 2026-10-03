using System.Collections.Concurrent;
using System.Security.Cryptography;
using InternalAssetLibrary.Client.Core.Http;
using InternalAssetLibrary.Client.Core.LocalAssets;
using InternalAssetLibrary.Contracts;
using SkiaSharp;

namespace InternalAssetLibrary.Client.Core.MediaAnalysis;

public enum CloudDerivativeProcessingState
{
    Skipped,
    Ready,
    Unavailable,
    Failed
}

public sealed record CloudDerivativeProcessingItem(
    AssetDerivativeKind Kind,
    CloudDerivativeProcessingState State,
    string? Diagnostic = null);

public sealed record CloudDerivativeProcessingResult(
    CloudDerivativeProcessingItem Thumbnail,
    CloudDerivativeProcessingItem Proxy)
{
    public bool HasWarning => Thumbnail.State is CloudDerivativeProcessingState.Unavailable or CloudDerivativeProcessingState.Failed ||
                              Proxy.State is CloudDerivativeProcessingState.Unavailable or CloudDerivativeProcessingState.Failed;
}

public sealed class CloudMediaDerivativeService
{
    private const int ThumbnailMaximumEdge = 640;
    private readonly AssetLibraryApiClient _api;
    private readonly MediaThumbnailService _thumbnails;
    private readonly string _cacheDirectory;
    private readonly string _workDirectory;
    private readonly string _ffmpegExecutable;
    private readonly IMediaToolRunner _runner;
    private readonly SemaphoreSlim _generationGate = new(1, 1);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _downloadGates = new(StringComparer.OrdinalIgnoreCase);

    public CloudMediaDerivativeService(
        AssetLibraryApiClient api,
        MediaThumbnailService thumbnails,
        string cacheDirectory,
        string ffmpegExecutable,
        IMediaToolRunner? runner = null)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _thumbnails = thumbnails ?? throw new ArgumentNullException(nameof(thumbnails));
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpegExecutable);
        _cacheDirectory = Path.GetFullPath(cacheDirectory);
        _workDirectory = Path.Combine(_cacheDirectory, ".work");
        _ffmpegExecutable = ffmpegExecutable.Trim();
        _runner = runner ?? new ProcessMediaToolRunner();
        Directory.CreateDirectory(_cacheDirectory);
        Directory.CreateDirectory(_workDirectory);
    }

    public async Task<string?> GetCachedDerivativeAsync(
        ApiAsset asset,
        AssetDerivativeKind kind,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var derivative = asset.Derivatives is null ? null : Select(asset.Derivatives, kind);
        if (derivative is null || !IsReadyForDownload(asset, derivative))
        {
            var latest = await _api.GetAssetDerivativesAsync(asset.Id, cancellationToken)
                .ConfigureAwait(false);
            derivative = Select(latest, kind);
        }

        if (!IsReadyForDownload(asset, derivative))
        {
            return null;
        }

        var trustedSha256 = RequireTrustedSha256(derivative.ETag!);
        var cachePath = BuildCachePath(asset, derivative, trustedSha256);
        var gate = _downloadGates.GetOrAdd(cachePath, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (HasExactLength(cachePath, derivative.SizeBytes))
            {
                Touch(cachePath);
                return cachePath;
            }

            TryDelete(cachePath);
            var partialPath = cachePath + $".{Guid.NewGuid():N}.part";
            try
            {
                ApiObjectDownloadResult response;
                await using (var destination = new FileStream(
                                 partialPath,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 128 * 1024,
                                 FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    response = await _api.CopyAssetDerivativeAsync(
                        asset.Id,
                        kind,
                        destination,
                        cancellationToken).ConfigureAwait(false);
                    await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                var actual = new FileInfo(partialPath);
                if (actual.Length != derivative.SizeBytes ||
                    response.ContentLength is { } contentLength && contentLength != actual.Length ||
                    !ContentTypeMatches(kind, asset.Category, response.ContentType))
                {
                    throw new InvalidDataException("Downloaded derivative metadata does not match the server state.");
                }

                await using (var source = new FileStream(
                                 partialPath,
                                 FileMode.Open,
                                 FileAccess.Read,
                                 FileShare.Read,
                                 128 * 1024,
                                 FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    var hash = Convert.ToHexString(await SHA256.HashDataAsync(source, cancellationToken)
                        .ConfigureAwait(false));
                    // Redirect response ETags are COS MD5/multipart identifiers; the API metadata is the trusted SHA-256.
                    if (!hash.Equals(trustedSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException(
                            "Downloaded derivative SHA-256 does not match the trusted server metadata.");
                    }
                }

                File.Move(partialPath, cachePath, overwrite: true);
                PruneOlderAssetDerivatives(asset.Id, kind, cachePath);
                return cachePath;
            }
            finally
            {
                TryDelete(partialPath);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<CloudDerivativeProcessingResult> ProcessAfterOriginalUploadAsync(
        string sourcePath,
        ApiAsset asset,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(asset);
        var fullPath = Path.GetFullPath(sourcePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The uploaded source file is no longer available.", fullPath);
        }

        await _generationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var derivatives = asset.Derivatives ?? await _api.GetAssetDerivativesAsync(asset.Id, cancellationToken)
                .ConfigureAwait(false);
            var thumbnail = await ProcessOneAsync(
                asset,
                derivatives.Thumbnail,
                AssetDerivativeKind.Thumbnail,
                token => CreateThumbnailAsync(fullPath, asset.Category, token),
                cancellationToken).ConfigureAwait(false);
            var proxy = asset.Category is ApiAssetCategory.Bgm or ApiAssetCategory.SoundEffect or ApiAssetCategory.Video
                ? await ProcessOneAsync(
                    asset,
                    derivatives.Proxy,
                    AssetDerivativeKind.Proxy,
                    token => CreateProxyAsync(fullPath, asset.Category, token),
                    cancellationToken).ConfigureAwait(false)
                : new CloudDerivativeProcessingItem(
                    AssetDerivativeKind.Proxy,
                    CloudDerivativeProcessingState.Skipped);
            return new CloudDerivativeProcessingResult(thumbnail, proxy);
        }
        finally
        {
            _generationGate.Release();
        }
    }

    public static MediaToolCommand BuildProxyCommand(
        string inputPath,
        string outputPath,
        ApiAssetCategory category,
        string ffmpegExecutable = "ffmpeg")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpegExecutable);
        if (category is not (ApiAssetCategory.Bgm or ApiAssetCategory.SoundEffect or ApiAssetCategory.Video))
        {
            throw new ArgumentOutOfRangeException(nameof(category), "Only audio and video assets use a proxy.");
        }

        var input = Path.GetFullPath(inputPath);
        var output = Path.GetFullPath(outputPath);
        if (string.Equals(
                input,
                output,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new ArgumentException("The proxy must not overwrite its source.", nameof(outputPath));
        }

        var arguments = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostdin",
            "-i", input
        };
        if (category == ApiAssetCategory.Video)
        {
            arguments.AddRange([
                "-map", "0:v:0",
                "-map", "0:a?",
                "-sn", "-dn",
                "-vf", "scale=w='min(960,iw)':h='min(540,ih)':force_original_aspect_ratio=decrease:force_divisible_by=2",
                "-fpsmax", "30",
                "-c:v", "libx264",
                "-preset", "veryfast",
                "-crf", "24",
                "-maxrate", "2000k",
                "-bufsize", "4000k",
                "-pix_fmt", "yuv420p"
            ]);
        }
        else
        {
            arguments.AddRange(["-map", "0:a?", "-vn", "-sn", "-dn"]);
        }

        arguments.AddRange([
            "-c:a", "aac",
            "-b:a", "192k",
            "-map_metadata", "0",
            "-movflags", "+faststart",
            "-y",
            output
        ]);
        return new MediaToolCommand(ffmpegExecutable.Trim(), arguments).Validate();
    }

    internal static bool UsesWaveformThumbnail(ApiAssetCategory category) =>
        category is ApiAssetCategory.Bgm or ApiAssetCategory.SoundEffect;

    private async Task<CloudDerivativeProcessingItem> ProcessOneAsync(
        ApiAsset asset,
        AssetDerivativeInfo derivative,
        AssetDerivativeKind kind,
        Func<CancellationToken, Task<GeneratedDerivative?>> generate,
        CancellationToken cancellationToken)
    {
        if (derivative.AssetVersionId != asset.CurrentVersionId ||
            derivative.State is not (AssetDerivativeState.Queued or AssetDerivativeState.Processing))
        {
            return new CloudDerivativeProcessingItem(kind, CloudDerivativeProcessingState.Skipped);
        }

        GeneratedDerivative? generated = null;
        try
        {
            await _api.UpdateAssetDerivativeStatusAsync(
                asset.Id,
                kind,
                new UpdateAssetDerivativeStatusRequest(asset.CurrentVersionId, AssetDerivativeState.Processing),
                cancellationToken).ConfigureAwait(false);
            generated = await generate(cancellationToken).ConfigureAwait(false);
            if (generated is null)
            {
                const string diagnostic = "当前客户端缺少可用媒体处理运行库，或源文件没有可生成的预览流。";
                await _api.UpdateAssetDerivativeStatusAsync(
                    asset.Id,
                    kind,
                    new UpdateAssetDerivativeStatusRequest(
                        asset.CurrentVersionId,
                        AssetDerivativeState.Unavailable,
                        "preview_generation_unavailable",
                        diagnostic),
                    cancellationToken).ConfigureAwait(false);
                return new CloudDerivativeProcessingItem(
                    kind,
                    CloudDerivativeProcessingState.Unavailable,
                    diagnostic);
            }

            await using var source = new FileStream(
                generated.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(source, cancellationToken)
                .ConfigureAwait(false));
            source.Position = 0;
            if (kind == AssetDerivativeKind.Thumbnail)
            {
                await _api.UploadAssetThumbnailAsync(
                    asset.Id,
                    asset.CurrentVersionId,
                    hash,
                    source,
                    source.Length,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _api.UploadAssetProxyAsync(
                    asset.Id,
                    asset.CurrentVersionId,
                    hash,
                    source,
                    source.Length,
                    generated.ContentType,
                    cancellationToken).ConfigureAwait(false);
            }

            return new CloudDerivativeProcessingItem(kind, CloudDerivativeProcessingState.Ready);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var diagnostic = LimitDiagnostic(exception.Message);
            await TryMarkFailedAsync(asset, kind, diagnostic).ConfigureAwait(false);
            return new CloudDerivativeProcessingItem(kind, CloudDerivativeProcessingState.Failed, diagnostic);
        }
        finally
        {
            if (generated is not null)
            {
                TryDelete(generated.Path);
            }
        }
    }

    private async Task<GeneratedDerivative?> CreateThumbnailAsync(
        string sourcePath,
        ApiAssetCategory category,
        CancellationToken cancellationToken)
    {
        var thumbnail = UsesWaveformThumbnail(category)
            ? await _thumbnails.GetOrCreateWaveformAsync(
                sourcePath,
                ThumbnailMaximumEdge,
                cancellationToken).ConfigureAwait(false)
            : await _thumbnails.GetOrCreateAsync(
                sourcePath,
                ThumbnailMaximumEdge,
                cancellationToken).ConfigureAwait(false);
        if (thumbnail is null)
        {
            return null;
        }

        var outputPath = Path.Combine(_workDirectory, $"thumbnail-{Guid.NewGuid():N}.webp");
        try
        {
            using var decoded = SKBitmap.Decode(thumbnail.CachePath);
            if (decoded is null || decoded.Width <= 0 || decoded.Height <= 0)
            {
                return null;
            }

            var scale = Math.Min(1d, ThumbnailMaximumEdge / (double)Math.Max(decoded.Width, decoded.Height));
            var width = Math.Max(1, (int)Math.Round(decoded.Width * scale));
            var height = Math.Max(1, (int)Math.Round(decoded.Height * scale));
            using var resized = width == decoded.Width && height == decoded.Height
                ? null
                : decoded.Resize(new SKImageInfo(width, height), SKFilterQuality.High);
            if ((width != decoded.Width || height != decoded.Height) && resized is null)
            {
                return null;
            }

            var output = resized ?? decoded;
            using var image = SKImage.FromBitmap(output);
            using var encoded = image.Encode(SKEncodedImageFormat.Webp, 82);
            if (encoded is null || encoded.Size <= 0)
            {
                return null;
            }

            await using var destination = new FileStream(
                outputPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            encoded.SaveTo(destination);
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            return new GeneratedDerivative(outputPath, "image/webp");
        }
        catch
        {
            TryDelete(outputPath);
            throw;
        }
    }

    private async Task<GeneratedDerivative?> CreateProxyAsync(
        string sourcePath,
        ApiAssetCategory category,
        CancellationToken cancellationToken)
    {
        var availability = MediaToolLocator.Locate(_ffmpegExecutable);
        if (!availability.IsAvailable)
        {
            return null;
        }

        var contentType = category == ApiAssetCategory.Video ? "video/mp4" : "audio/mp4";
        var extension = category == ApiAssetCategory.Video ? ".mp4" : ".m4a";
        var outputPath = Path.Combine(_workDirectory, $"proxy-{Guid.NewGuid():N}{extension}");
        try
        {
            var result = await _runner.RunAsync(
                BuildProxyCommand(sourcePath, outputPath, category, availability.ResolvedExecutable!),
                cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded || !HasContent(outputPath))
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.StandardError)
                    ? $"FFmpeg exited with code {result.ExitCode}."
                    : LimitDiagnostic(result.StandardError));
            }

            return new GeneratedDerivative(outputPath, contentType);
        }
        catch
        {
            TryDelete(outputPath);
            throw;
        }
    }

    private async Task TryMarkFailedAsync(
        ApiAsset asset,
        AssetDerivativeKind kind,
        string diagnostic)
    {
        try
        {
            await _api.UpdateAssetDerivativeStatusAsync(
                asset.Id,
                kind,
                new UpdateAssetDerivativeStatusRequest(
                    asset.CurrentVersionId,
                    AssetDerivativeState.Failed,
                    kind == AssetDerivativeKind.Thumbnail ? "thumbnail_generation_failed" : "proxy_generation_failed",
                    diagnostic),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // The original upload remains successful even when status reporting is unreachable.
        }
    }

    private string BuildCachePath(
        ApiAsset asset,
        AssetDerivativeInfo derivative,
        string trustedSha256)
    {
        var suffix = derivative.Kind switch
        {
            AssetDerivativeKind.Thumbnail => ".webp",
            AssetDerivativeKind.Proxy when asset.Category == ApiAssetCategory.Video => ".mp4",
            AssetDerivativeKind.Proxy => ".m4a",
            _ => throw new ArgumentOutOfRangeException(nameof(derivative))
        };
        return Path.Combine(
            _cacheDirectory,
            $"{asset.Id:N}-{asset.CurrentVersionId:N}-{KindValue(derivative.Kind)}-v{derivative.FormatVersion}-{trustedSha256}{suffix}");
    }

    private static string RequireTrustedSha256(string value)
    {
        var sha256 = value.Trim().ToUpperInvariant();
        if (sha256.Length != 64 || sha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException("Derivative trusted SHA-256 metadata is invalid.");
        }

        return sha256;
    }

    private void PruneOlderAssetDerivatives(Guid assetId, AssetDerivativeKind kind, string retainedPath)
    {
        try
        {
            var pattern = $"{assetId:N}-*-{KindValue(kind)}-*";
            foreach (var path in Directory.EnumerateFiles(_cacheDirectory, pattern, SearchOption.TopDirectoryOnly))
            {
                if (!string.Equals(
                        path,
                        retainedPath,
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                {
                    TryDelete(path);
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static AssetDerivativeInfo Select(AssetDerivativesInfo derivatives, AssetDerivativeKind kind) =>
        kind == AssetDerivativeKind.Thumbnail ? derivatives.Thumbnail : derivatives.Proxy;

    private static bool IsReadyForDownload(ApiAsset asset, AssetDerivativeInfo derivative) =>
        derivative.State == AssetDerivativeState.Ready &&
        derivative.AssetVersionId == asset.CurrentVersionId &&
        derivative.SizeBytes > 0 &&
        !string.IsNullOrWhiteSpace(derivative.ETag);

    private static string KindValue(AssetDerivativeKind kind) =>
        kind == AssetDerivativeKind.Thumbnail ? "thumbnail" : "proxy";

    private static bool ContentTypeMatches(
        AssetDerivativeKind kind,
        ApiAssetCategory category,
        string? contentType)
    {
        var expected = kind == AssetDerivativeKind.Thumbnail
            ? "image/webp"
            : category == ApiAssetCategory.Video
                ? "video/mp4"
                : "audio/mp4";
        return string.Equals(expected, contentType, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasExactLength(string path, long expectedLength) =>
        File.Exists(path) && new FileInfo(path).Length == expectedLength;

    private static bool HasContent(string path) =>
        File.Exists(path) && new FileInfo(path).Length > 0;

    private static string LimitDiagnostic(string? value)
    {
        var diagnostic = string.IsNullOrWhiteSpace(value) ? "媒体派生处理失败。" : value.Trim();
        return diagnostic[..Math.Min(500, diagnostic.Length)];
    }

    private static void Touch(string path)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record GeneratedDerivative(string Path, string ContentType);
}
