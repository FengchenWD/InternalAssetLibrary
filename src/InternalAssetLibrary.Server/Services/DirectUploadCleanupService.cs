using InternalAssetLibrary.Server.Api;
using InternalAssetLibrary.Server.Data;
using InternalAssetLibrary.Server.Realtime;

namespace InternalAssetLibrary.Server.Services;

internal sealed class DirectUploadCleanupService(
    IAppDataStore store,
    ObjectDeletionOutbox cleanups,
    ILibraryChangeNotifier changes,
    ILogger<DirectUploadCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await CleanupAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await CleanupAsync(stoppingToken);
        }
    }

    private async Task CleanupAsync(CancellationToken cancellationToken)
    {
        try
        {
            await cleanups.RetryPendingAsync(cancellationToken);
            var now = DateTimeOffset.UtcNow;
            var expired = await store.UpdateAsync(state =>
            {
                var sessions = state.DirectUploadSessions.Where(session => session.ExpiresAt <= now).ToArray();
                foreach (var session in sessions)
                {
                    cleanups.EnqueueAbort(state, session);
                    state.DirectUploadSessions.Remove(session);
                    var asset = state.Assets.FirstOrDefault(item =>
                        item.Id == session.AssetId &&
                        item.CurrentVersionId == session.AssetVersionId &&
                        !item.HasOriginal);
                    if (asset is not null)
                    {
                        state.Assets.Remove(asset);
                        ApiCommon.Audit(state, null, "asset.direct-upload.expired", "asset", asset.Id.ToString(), session.Id.ToString());
                    }
                }

                return sessions;
            }, cancellationToken);

            foreach (var session in expired)
            {
                try
                {
                    await cleanups.TryProcessAsync([session.ObjectKey], cancellationToken);
                }
                catch (Exception exception)
                {
                    logger.LogWarning(
                        exception,
                        "Could not abort expired multipart upload {SessionId} ({ProviderUploadId}).",
                        session.Id,
                        session.ProviderUploadId);
                }

                await changes.NotifyAsync(
                    cancellationToken,
                    LibraryChangeTarget.Assets(session.AssetId),
                    LibraryChangeTarget.Derivatives(session.AssetId),
                    LibraryChangeTarget.Profiles(session.UserId));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Expired direct upload cleanup failed.");
        }
    }
}
