using InternalAssetLibrary.Server.Data;

namespace InternalAssetLibrary.Server.Services;

internal sealed class ObjectDeletionOutbox(
    IAppDataStore store,
    IObjectStore objects,
    ILogger<ObjectDeletionOutbox> logger) : IDisposable
{
    private readonly SemaphoreSlim _processGate = new(1, 1);

    public string[] Enqueue(
        AppState state,
        IEnumerable<string> objectKeys,
        DateTimeOffset createdAt)
    {
        var keys = objectKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var queued = state.PendingObjectDeletions
            .Where(item => item.ProviderUploadId is null)
            .Select(item => item.ObjectKey)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            if (queued.Add(key))
            {
                state.PendingObjectDeletions.Add(new PendingObjectDeletionRecord
                {
                    ObjectKey = key,
                    CreatedAt = createdAt
                });
            }
        }

        return keys;
    }

    public void EnqueueAbort(AppState state, DirectUploadSessionRecord session)
    {
        if (!state.PendingObjectDeletions.Any(item => item.ObjectKey == session.ObjectKey &&
            item.ProviderUploadId == session.ProviderUploadId))
            state.PendingObjectDeletions.Add(new PendingObjectDeletionRecord
            {
                ObjectKey = session.ObjectKey, ProviderUploadId = session.ProviderUploadId,
                CreatedAt = DateTimeOffset.UtcNow
            });
    }

    public async Task RetryPendingAsync(CancellationToken cancellationToken)
    {
        var keys = await store.ReadAsync(
            state => state.PendingObjectDeletions.Select(item => item.ObjectKey).ToArray(),
            cancellationToken);
        await TryProcessAsync(keys, cancellationToken);
    }

    public async Task TryProcessAsync(
        IEnumerable<string> objectKeys,
        CancellationToken cancellationToken)
    {
        var keys = objectKeys.Distinct(StringComparer.Ordinal).ToArray();
        if (keys.Length == 0)
        {
            return;
        }

        await _processGate.WaitAsync(cancellationToken);
        try
        {
            foreach (var key in keys)
            {
                var pendingTasks = await store.ReadAsync(state => state.PendingObjectDeletions
                    .Where(item => item.ObjectKey == key).ToArray(), cancellationToken);
                foreach (var pendingTask in pendingTasks)
                {
                try
                {
                    if (pendingTask.ProviderUploadId is { } uploadId)
                        await objects.AbortMultipartUploadAsync(key, uploadId, cancellationToken);
                    else await objects.DeleteAsync(key, cancellationToken);
                    await store.UpdateAsync(state =>
                    {
                        state.PendingObjectDeletions.RemoveAll(item =>
                            item.ObjectKey.Equals(key, StringComparison.Ordinal) &&
                            item.ProviderUploadId == pendingTask.ProviderUploadId);
                        return true;
                    }, CancellationToken.None);
                }
                catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
                {
                    await RecordFailureAsync(key, pendingTask.ProviderUploadId);
                    logger.LogInformation(
                        exception,
                        "Object deletion for {ObjectKey} was interrupted and remains scheduled for retry.",
                        key);
                    break;
                }
                catch (Exception exception)
                {
                    await RecordFailureAsync(key, pendingTask.ProviderUploadId);
                    logger.LogError(
                        exception,
                        "Could not delete object {ObjectKey}; the durable deletion task remains scheduled for retry.",
                        key);
                }
                }
            }
        }
        finally
        {
            _processGate.Release();
        }
    }

    private async Task RecordFailureAsync(string objectKey, string? providerUploadId)
    {
        try
        {
            await store.UpdateAsync(state =>
            {
                var pending = state.PendingObjectDeletions.FirstOrDefault(item =>
                    item.ObjectKey.Equals(objectKey, StringComparison.Ordinal) && item.ProviderUploadId == providerUploadId);
                if (pending is not null)
                {
                    pending.LastAttemptAt = DateTimeOffset.UtcNow;
                    if (pending.FailureCount < int.MaxValue)
                    {
                        pending.FailureCount++;
                    }
                }

                return true;
            }, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Could not update the durable deletion attempt for {ObjectKey}; the original task remains persisted.",
                objectKey);
        }
    }

    public void Dispose() => _processGate.Dispose();
}
