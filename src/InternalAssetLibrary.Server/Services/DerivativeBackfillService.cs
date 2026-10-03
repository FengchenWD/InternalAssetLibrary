using System.ComponentModel;
using System.Diagnostics;
using InternalAssetLibrary.Contracts;
using InternalAssetLibrary.Server.Api;
using InternalAssetLibrary.Server.Data;
using InternalAssetLibrary.Server.Realtime;

namespace InternalAssetLibrary.Server.Services;

internal sealed class DerivativeBackfillService(
    IAppDataStore store,
    IObjectStore objects,
    ObjectDeletionOutbox objectDeletions,
    IConfiguration configuration,
    ServerSettingsService settings,
    ILibraryChangeNotifier changes,
    ILogger<DerivativeBackfillService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverInterruptedWorkAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            var current = settings.Current;
            var options = new DerivativeProcessingOptions
            {
                Enabled = current.DerivativesEnabled,
                FfmpegPath = configuration["Derivatives:FfmpegPath"] ?? "ffmpeg",
                PollIntervalSeconds = current.DerivativePollIntervalSeconds,
                ProcessTimeoutSeconds = current.DerivativeProcessTimeoutSeconds,
                ThumbnailMaxEdge = current.ThumbnailMaxEdge
            };
            var delay = TimeSpan.FromSeconds(Math.Clamp(options.PollIntervalSeconds, 5, 3600));
            if (!options.Enabled)
            {
                await Task.Delay(delay, stoppingToken);
                continue;
            }

            var target = await TryAcquireAsync(stoppingToken);
            if (target is null)
            {
                await Task.Delay(delay, stoppingToken);
                continue;
            }

            await ProcessAsync(target, options, stoppingToken);
        }
    }

    private Task RecoverInterruptedWorkAsync(CancellationToken cancellationToken) =>
        store.UpdateAsync(state =>
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var asset in state.Assets)
            {
                var thumbnail = asset.Derivatives.Thumbnail;
                if (thumbnail.State == DerivativeState.Processing && thumbnail.ServerBackfillEligible)
                {
                    thumbnail.State = DerivativeState.Queued;
                    thumbnail.ErrorCode = "backfill_worker_restarted";
                    thumbnail.ErrorMessage = null;
                    thumbnail.UpdatedAt = now;
                }
            }

            return true;
        }, cancellationToken);

    private Task<BackfillTarget?> TryAcquireAsync(CancellationToken cancellationToken) =>
        store.UpdateAsync(state =>
        {
            var asset = state.Assets
                .Where(item =>
                    item.State == AssetState.Active &&
                    item.HasOriginal &&
                    item.Derivatives.Thumbnail.State == DerivativeState.Queued &&
                    item.Derivatives.Thumbnail.ServerBackfillEligible)
                .OrderBy(item => item.UploadedAt)
                .FirstOrDefault();
            if (asset is null)
            {
                return null;
            }

            var now = DateTimeOffset.UtcNow;
            asset.Derivatives.Thumbnail.State = DerivativeState.Processing;
            asset.Derivatives.Thumbnail.ErrorCode = null;
            asset.Derivatives.Thumbnail.ErrorMessage = null;
            asset.Derivatives.Thumbnail.UpdatedAt = now;
            return new BackfillTarget(
                asset.Id,
                asset.CurrentVersionId,
                asset.ObjectKey,
                asset.Extension,
                asset.Category,
                AssetDerivativeRules.BuildObjectKey(asset, AssetDerivativeKind.Thumbnail, Guid.NewGuid()));
        }, cancellationToken);

    private async Task ProcessAsync(
        BackfillTarget target,
        DerivativeProcessingOptions options,
        CancellationToken cancellationToken)
    {
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ial-thumbnail-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        var inputPath = Path.Combine(temporaryDirectory, "source" + target.Extension);
        var outputPath = Path.Combine(temporaryDirectory, "thumbnail.webp");
        try
        {
            await using (var source = await objects.OpenReadAsync(target.OriginalObjectKey, cancellationToken))
            {
                if (source is null)
                {
                    await MarkAsync(target, DerivativeState.Failed, "original_object_missing", "对象存储中的原文件缺失。", cancellationToken);
                    return;
                }

                await using var destination = new FileStream(
                    inputPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await source.CopyToAsync(destination, cancellationToken);
            }

            var processResult = await RunFfmpegAsync(
                inputPath,
                outputPath,
                target.Category,
                options,
                cancellationToken);
            if (processResult.ExitCode != 0 || !File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            {
                var unavailable = IsNoPreviewStream(processResult.StandardError);
                await MarkAsync(
                    target,
                    unavailable ? DerivativeState.Unavailable : DerivativeState.Failed,
                    unavailable ? "preview_stream_unavailable" : "ffmpeg_thumbnail_failed",
                    LimitError(processResult.StandardError),
                    cancellationToken);
                return;
            }

            var bytes = await File.ReadAllBytesAsync(outputPath, cancellationToken);
            _ = ThumbnailWebPValidator.Validate(bytes, Math.Clamp(options.ThumbnailMaxEdge, 64, 640));
            await using var upload = new MemoryStream(bytes, writable: false);
            var write = await objects.PutAsync(target.DerivativeObjectKey, upload, bytes.Length, cancellationToken);
            var commit = await store.UpdateAsync(state =>
            {
                var asset = state.Assets.FirstOrDefault(item =>
                    item.Id == target.AssetId &&
                    item.CurrentVersionId == target.AssetVersionId &&
                    item.State == AssetState.Active);
                if (asset is null || asset.Derivatives.Thumbnail.State != DerivativeState.Processing ||
                    !asset.Derivatives.Thumbnail.ServerBackfillEligible)
                {
                    objectDeletions.Enqueue(state, [target.DerivativeObjectKey], DateTimeOffset.UtcNow);
                    return new BackfillCommitResult(false, [target.DerivativeObjectKey]);
                }

                var thumbnail = asset.Derivatives.Thumbnail;
                var staleKeys = string.IsNullOrWhiteSpace(thumbnail.ObjectKey)
                    ? []
                    : objectDeletions.Enqueue(state, [thumbnail.ObjectKey], DateTimeOffset.UtcNow);
                thumbnail.State = DerivativeState.Ready;
                thumbnail.ObjectKey = target.DerivativeObjectKey;
                thumbnail.ContentType = "image/webp";
                thumbnail.SizeBytes = write.SizeBytes;
                thumbnail.ETag = write.Sha256;
                thumbnail.ErrorCode = null;
                thumbnail.ErrorMessage = null;
                thumbnail.ServerBackfillEligible = false;
                thumbnail.UpdatedAt = DateTimeOffset.UtcNow;
                ApiCommon.Audit(
                    state,
                    null,
                    "asset.derivative.backfilled",
                    "asset",
                    asset.Id.ToString(),
                    "thumbnail");
                return new BackfillCommitResult(true, staleKeys);
            }, cancellationToken);

            if (!commit.Committed)
            {
                await objectDeletions.TryProcessAsync(commit.StaleObjectKeys, CancellationToken.None);
                return;
            }

            await objectDeletions.TryProcessAsync(commit.StaleObjectKeys, CancellationToken.None);
            await changes.NotifyAsync(cancellationToken, LibraryChangeTarget.Derivatives(target.AssetId));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Win32Exception exception)
        {
            logger.LogError(exception, "Configured FFmpeg executable {FfmpegPath} could not be started.", options.FfmpegPath);
            await MarkAsync(target, DerivativeState.Failed, "ffmpeg_unavailable", "服务器无法启动 FFmpeg。", CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Thumbnail backfill failed for asset {AssetId}.", target.AssetId);
            await MarkAsync(target, DerivativeState.Failed, "thumbnail_backfill_failed", LimitError(exception.Message), CancellationToken.None);
        }
        finally
        {
            TryDeleteDirectory(temporaryDirectory);
        }
    }

    private async Task MarkAsync(
        BackfillTarget target,
        DerivativeState stateValue,
        string errorCode,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        var changed = await store.UpdateAsync(state =>
        {
            var asset = state.Assets.FirstOrDefault(item =>
                item.Id == target.AssetId &&
                item.CurrentVersionId == target.AssetVersionId);
            if (asset is null || asset.Derivatives.Thumbnail.State != DerivativeState.Processing ||
                !asset.Derivatives.Thumbnail.ServerBackfillEligible)
            {
                return false;
            }

            var thumbnail = asset.Derivatives.Thumbnail;
            thumbnail.State = stateValue;
            thumbnail.ErrorCode = errorCode;
            thumbnail.ErrorMessage = errorMessage;
            thumbnail.ServerBackfillEligible = false;
            thumbnail.UpdatedAt = DateTimeOffset.UtcNow;
            return true;
        }, cancellationToken);
        if (changed)
        {
            await changes.NotifyAsync(cancellationToken, LibraryChangeTarget.Derivatives(target.AssetId));
        }
    }

    private static async Task<ProcessResult> RunFfmpegAsync(
        string inputPath,
        string outputPath,
        string category,
        DerivativeProcessingOptions options,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = options.FfmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            }
        };
        var maximumEdge = Math.Clamp(options.ThumbnailMaxEdge, 64, 640);
        foreach (var argument in BuildThumbnailArguments(inputPath, outputPath, category, maximumEdge))
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.ProcessTimeoutSeconds, 10, 3600)));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return new ProcessResult(-1, "FFmpeg processing timed out.");
        }
        catch
        {
            TryKill(process);
            throw;
        }

        _ = await standardOutput;
        return new ProcessResult(process.ExitCode, await standardError);
    }

    internal static IReadOnlyList<string> BuildThumbnailArguments(
        string inputPath,
        string outputPath,
        string category,
        int maximumEdge)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var edge = Math.Clamp(maximumEdge, 64, 640);
        var arguments = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-i", inputPath
        };
        if (AssetDerivativeRules.UsesWaveformThumbnail(category))
        {
            var height = Math.Max(64, edge / 3);
            arguments.AddRange([
                "-filter_complex",
                $"[0:a:0]aformat=channel_layouts=mono,volume=-12dB,showwavespic=s={edge}x{height}:split_channels=0:scale=sqrt:filter=average:colors=0x22D3EE[wave]",
                "-map", "[wave]"
            ]);
        }
        else
        {
            arguments.AddRange([
                "-map", "0:v:0?",
                "-vf", $"scale={edge}:{edge}:force_original_aspect_ratio=decrease:flags=lanczos"
            ]);
        }

        arguments.AddRange(["-frames:v", "1", "-c:v", "libwebp", "-quality", "82", outputPath]);
        return arguments;
    }

    private static bool IsNoPreviewStream(string value) =>
        value.Contains("does not contain any stream", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("matches no streams", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("Output file does not contain", StringComparison.OrdinalIgnoreCase);

    private static string? LimitError(string? value)
    {
        var result = value?.Trim();
        return string.IsNullOrEmpty(result) ? null : result[..Math.Min(result.Length, 500)];
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private sealed record BackfillTarget(
        Guid AssetId,
        Guid AssetVersionId,
        string OriginalObjectKey,
        string Extension,
        string Category,
        string DerivativeObjectKey);

    private sealed record BackfillCommitResult(bool Committed, string[] StaleObjectKeys);

    private sealed record ProcessResult(int ExitCode, string StandardError);
}
