using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace InternalAssetLibrary.Client.Services;

internal static class WindowsImeCompatibility
{
    private const uint InputLanguageChangedMessage = 0x0051;
    private static IDisposable? _focusRegistration;

    public static void Initialize()
    {
        _focusRegistration ??= InputElement.GotFocusEvent.AddClassHandler<TextBox>(
            static (textBox, _) =>
            {
                InputMethod.SetIsInputMethodEnabled(textBox, true);
                if (OperatingSystem.IsWindows())
                {
                    Dispatcher.UIThread.Post(RebindFocusedWindow, DispatcherPriority.Input);
                }
            },
            RoutingStrategies.Bubble,
            handledEventsToo: true);
    }

    private static void RebindFocusedWindow()
    {
        var focusedWindow = GetFocus();
        if (focusedWindow == IntPtr.Zero)
        {
            return;
        }

        _ = SendMessageW(
            focusedWindow,
            InputLanguageChangedMessage,
            IntPtr.Zero,
            GetKeyboardLayout(0));
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint threadId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageW(
        IntPtr windowHandle,
        uint message,
        IntPtr wordParameter,
        IntPtr longParameter);
}
