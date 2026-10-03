using System.Security.Cryptography;
using System.Text;
using InternalAssetLibrary.Client.Core.LocalAssets;
using SkiaSharp;

namespace InternalAssetLibrary.Client.Core.MediaAnalysis;

public sealed class MediaThumbnailService
{
    private const int CacheVersion = 1;
    private readonly string _cacheDirectory;
    private readonly string _ffmpegExecutable;
    private readonly StaticImageThumbnailService _staticImages;
    private readonly IMediaToolRunner _runner;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public MediaThumbnailService(
        string cacheDirectory,
        string ffmpegExecutable,
        StaticImageThumbnailService staticImages,
        IMediaToolRunner? runner = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpegExecutable);
        _cacheDirectory = Path.GetFullPath(cacheDirectory);
        _ffmpegExecutable = ffmpegExecutable.Trim();
        _staticImages = staticImages ?? throw new ArgumentNullException(nameof(staticImages));
        _runner = runner ?? new ProcessMediaToolRunner();
        Directory.CreateDirectory(_cacheDirectory);
    }

    public MediaToolAvailability FfmpegAvailability => MediaToolLocator.Locate(_ffmpegExecutable);

    public async Task<StaticImageThumbnail?> GetOrCreateWaveformAsync(
        string sourcePath,
        int maximumWidth = 960,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        if (maximumWidth is < 64 or > StaticImageThumbnailService.MaximumOutputEdge)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumWidth));
        }

        var fullPath = Path.GetFullPath(sourcePath);
        var source = new FileInfo(fullPath);
        if (!source.Exists || source.Length <= 0)
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            source.Refresh();
            if (!source.Exists)
            {
                return null;
            }

            var sourceWriteTime = new DateTimeOffset(source.LastWriteTimeUtc, TimeSpan.Zero);
            var cachePath = Path.Combine(
                _cacheDirectory,
                CreateCacheKey(fullPath, sourceWriteTime, source.Length, maximumWidth, "waveform-v5") + ".webp");
            var cached = ReadThumbnail(cachePath, sourceWriteTime, fromCache: true, maximumWidth);
            if (cached is not null)
            {
                Touch(cachePath);
                return cached;
            }

            var ffmpeg = FfmpegAvailability;
            if (!ffmpeg.IsAvailable)
            {
                return null;
            }

            var temporaryPath = Path.Combine(_cacheDirectory, $".{Guid.NewGuid():N}.webp");
            try
            {
                var command = FfmpegCommandBuilder.BuildWaveform(
                    fullPath,
                    temporaryPath,
                    width: maximumWidth,
                    height: Math.Max(64, maximumWidth / 3),
                    ffmpegExecutable: ffmpeg.ResolvedExecutable!);
                var result = await _runner.RunAsync(command, cancellationToken).ConfigureAwait(false);
                if (result.Succeeded && ReadThumbnail(
                        temporaryPath,
                        sourceWriteTime,
                        fromCache: false,
                        maximumWidth) is { } generated)
                {
                    source.Refresh();
                    if (source.Exists && source.LastWriteTimeUtc == sourceWriteTime.UtcDateTime)
                    {
                        File.Move(temporaryPath, cachePath, overwrite: true);
                        return generated with { CachePath = cachePath };
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            finally
            {
                TryDelete(temporaryPath);
            }

            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<StaticImageThumbnail?> GetOrCreateAsync(
        string sourcePath,
        int maximumEdge = 640,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        if (maximumEdge is < 64 or > StaticImageThumbnailService.MaximumOutputEdge)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEdge));
        }

        var fullPath = Path.GetFullPath(sourcePath);
        var source = new FileInfo(fullPath);
        if (!source.Exists || source.Length <= 0)
        {
            return null;
        }

        var extension = source.Extension;
        if (StaticImageThumbnailService.CanDecodeDirectly(extension))
        {
            var direct = await _staticImages.GetOrCreateAsync(fullPath, maximumEdge, cancellationToken)
                .ConfigureAwait(false);
            if (direct is not null)
            {
                return direct;
            }
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            source.Refresh();
            if (!source.Exists)
            {
                return null;
            }

            var sourceWriteTime = new DateTimeOffset(source.LastWriteTimeUtc, TimeSpan.Zero);
            var cachePath = Path.Combine(
                _cacheDirectory,
                CreateCacheKey(fullPath, sourceWriteTime, source.Length, maximumEdge) + ".webp");
            var cached = ReadThumbnail(cachePath, sourceWriteTime, fromCache: true, maximumEdge);
            if (cached is not null)
            {
                Touch(cachePath);
                return cached;
            }

            var ffmpeg = FfmpegAvailability;
            if (ffmpeg.IsAvailable)
            {
                var temporaryPath = Path.Combine(_cacheDirectory, $".{Guid.NewGuid():N}.webp");
                try
                {
                    var command = FfmpegCommandBuilder.BuildThumbnail(
                        fullPath,
                        temporaryPath,
                        maximumEdge,
                        ffmpeg.ResolvedExecutable!);
                    var result = await _runner.RunAsync(command, cancellationToken).ConfigureAwait(false);
                    if (!result.Succeeded &&
                        MediaExtensionClassifier.TryClassify(extension, out var mediaType) &&
                        mediaType == LocalMediaType.Audio)
                    {
                        TryDelete(temporaryPath);
                        result = await _runner.RunAsync(
                                FfmpegCommandBuilder.BuildWaveform(
                                    fullPath,
                                    temporaryPath,
                                    width: maximumEdge,
                                    height: Math.Max(64, maximumEdge / 3),
                                    ffmpegExecutable: ffmpeg.ResolvedExecutable!),
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    if (result.Succeeded && ReadThumbnail(
                            temporaryPath,
                            sourceWriteTime,
                            fromCache: false,
                            maximumEdge) is { } generated)
                    {
                        source.Refresh();
                        if (source.Exists && source.LastWriteTimeUtc == sourceWriteTime.UtcDateTime)
                        {
                            File.Move(temporaryPath, cachePath, overwrite: true);
                            return generated with { CachePath = cachePath };
                        }
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
                finally
                {
                    TryDelete(temporaryPath);
                }
            }

            return await _staticImages.GetOrCreateAsync(fullPath, maximumEdge, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static StaticImageThumbnail? ReadThumbnail(
        string path,
        DateTimeOffset sourceWriteTime,
        bool fromCache,
        int maximumEdge)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(path);
            using var codec = SKCodec.Create(stream);
            if (codec is null || codec.FrameCount > 1 || codec.Info.Width <= 0 || codec.Info.Height <= 0 ||
                codec.Info.Width > maximumEdge || codec.Info.Height > maximumEdge)
            {
                return null;
            }

            return new StaticImageThumbnail(
                path,
                codec.Info.Width,
                codec.Info.Height,
                sourceWriteTime,
                fromCache);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string CreateCacheKey(
        string fullPath,
        DateTimeOffset lastWriteTime,
        long length,
        int maximumEdge,
        string kind = "thumbnail-v1")
    {
        var normalizedPath = OperatingSystem.IsWindows() ? fullPath.ToUpperInvariant() : fullPath;
        var value = $"{CacheVersion}\n{kind}\n{normalizedPath}\n{lastWriteTime.UtcTicks}\n{length}\n{maximumEdge}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
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
}
