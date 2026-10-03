using Avalonia.Threading;
using InternalAssetLibrary.Client.Services;

namespace InternalAssetLibrary.Client;

public sealed partial class MainWindow
{
    private CancellationTokenSource? _transientNotificationCancellation;

    private void ShowTransientNotification(string chineseFormat, params object?[] arguments)
    {
        var message = UiLocalization.Format(chineseFormat, arguments);
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ShowTransientNotification(message));
            return;
        }

        var cancellation = new CancellationTokenSource();
        Interlocked.Exchange(ref _transientNotificationCancellation, cancellation)?.Cancel();
        TransientNotificationText.Text = message;
        TransientNotification.IsVisible = true;
        TransientNotification.Opacity = 1;
        _ = HideTransientNotificationAsync(cancellation);
    }

    private async Task HideTransientNotificationAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3.5), cancellation.Token);
            TransientNotification.Opacity = 0;
            await Task.Delay(TimeSpan.FromMilliseconds(180), cancellation.Token);
            TransientNotification.IsVisible = false;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Interlocked.CompareExchange(
                ref _transientNotificationCancellation,
                null,
                cancellation);
            cancellation.Dispose();
        }
    }
}
