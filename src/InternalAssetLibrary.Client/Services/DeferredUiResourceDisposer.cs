using Avalonia.Threading;

namespace InternalAssetLibrary.Client.Services;

internal static class DeferredUiResourceDisposer
{
    private static readonly TimeSpan BindingReleaseDelay = TimeSpan.FromMilliseconds(250);
    private static readonly object Gate = new();
    private static readonly HashSet<IDisposable> Pending = new(ReferenceEqualityComparer.Instance);
    private static bool _flushScheduled;

    public static void Dispose(IDisposable? resource)
    {
        if (resource is null)
        {
            return;
        }

        lock (Gate)
        {
            if (!Pending.Add(resource) || _flushScheduled)
            {
                return;
            }

            _flushScheduled = true;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            ScheduleFlush();
        }
        else
        {
            Dispatcher.UIThread.Post(ScheduleFlush, DispatcherPriority.Background);
        }
    }

    private static void ScheduleFlush() => DispatcherTimer.RunOnce(
        Flush,
        BindingReleaseDelay,
        DispatcherPriority.Background);

    private static void Flush()
    {
        IDisposable[] batch;
        lock (Gate)
        {
            batch = [.. Pending];
        }

        foreach (var resource in batch)
        {
            try
            {
                resource.Dispose();
            }
            catch (Exception exception)
            {
                ClientDiagnostics.WriteException("deferred-ui-resource-dispose", exception, isFatal: false);
            }
        }

        lock (Gate)
        {
            foreach (var resource in batch)
            {
                Pending.Remove(resource);
            }

            if (Pending.Count == 0)
            {
                _flushScheduled = false;
                return;
            }
        }

        ScheduleFlush();
    }
}
