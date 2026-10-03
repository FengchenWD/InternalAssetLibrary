using Avalonia.Controls;
using InternalAssetLibrary.Client.Services;

namespace InternalAssetLibrary.Client;

public sealed partial class MainWindow
{
    private readonly SemaphoreSlim _nativePickerGate = new(1, 1);

    private async Task<T?> RunNativePickerAsync<T>(
        string operation,
        Func<Task<T>> picker,
        TextBlock? status = null)
        where T : class?
    {
        if (_shutdownStarted)
        {
            return null;
        }

        if (!_nativePickerGate.Wait(0))
        {
            if (!_shutdownStarted && status is not null)
            {
                UiLocalization.SetText(status, "已有文件选择窗口正在打开，请先完成或取消当前选择。");
            }

            return null;
        }

        try
        {
            var result = await picker();
            return _shutdownStarted ? null : result;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception exception)
        {
            ClientDiagnostics.WriteException($"native-picker-{operation}", exception, isFatal: false);
            if (!_shutdownStarted && status is not null)
            {
                UiLocalization.SetText(status, "无法打开文件选择窗口：{0}", UserMessage(exception));
            }

            return null;
        }
        finally
        {
            _nativePickerGate.Release();
        }
    }

    private async Task RunGuardedInteractionAsync(
        string operation,
        Func<Task> action,
        TextBlock? status = null)
    {
        if (_shutdownStarted)
        {
            return;
        }

        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ClientDiagnostics.WriteException($"interaction-{operation}", exception, isFatal: false);
            if (!_shutdownStarted && status is not null)
            {
                UiLocalization.SetText(status, "操作失败：{0}", UserMessage(exception));
            }
        }
    }
}
