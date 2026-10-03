using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;

namespace InternalAssetLibrary.Client.Controls;

public sealed class MpvVideoHost : NativeControlHost
{
    private const uint WsChild = 0x40000000;
    private const uint WsVisible = 0x10000000;
    private const uint WsClipSiblings = 0x04000000;
    private const uint WsClipChildren = 0x02000000;
    private const uint SsBlackRect = 0x00000004;

    public nint HostHandle { get; private set; }

    public bool IsHostReady => HostHandle != 0;

    public event EventHandler<nint>? HostReady;

    public event EventHandler? HostDestroyed;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (!OperatingSystem.IsWindows())
        {
            return base.CreateNativeControlCore(parent);
        }

        var handle = CreateWindowExW(
            0,
            "STATIC",
            string.Empty,
            WsChild | WsVisible | WsClipSiblings | WsClipChildren | SsBlackRect,
            0,
            0,
            1,
            1,
            parent.Handle,
            0,
            GetModuleHandleW(null),
            0);
        if (handle == 0)
        {
            throw new InvalidOperationException(
                $"Could not create the media host window (Win32 {Marshal.GetLastWin32Error()}).");
        }

        HostHandle = handle;
        HostReady?.Invoke(this, handle);
        return new PlatformHandle(handle, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        var handle = control.Handle;
        if (handle != 0 && OperatingSystem.IsWindows())
        {
            _ = DestroyWindow(handle);
        }

        HostHandle = 0;
        HostDestroyed?.Invoke(this, EventArgs.Empty);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint parameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? moduleName);
}
