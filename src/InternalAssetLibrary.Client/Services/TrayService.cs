using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace InternalAssetLibrary.Client.Services;

public sealed class TrayService : IDisposable
{
    private readonly Application _application;
    private readonly Window _mainWindow;
    private readonly TrayIcon _trayIcon;
    private readonly NativeMenuItem _showItem;
    private readonly NativeMenuItem _exitItem;
    private bool _disposed;

    public TrayService(
        Application application,
        Window mainWindow,
        WindowIcon icon,
        string toolTipText = "云汀素材管理工具")
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(mainWindow);
        ArgumentNullException.ThrowIfNull(icon);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolTipText);

        _application = application;
        _mainWindow = mainWindow;

        _showItem = new NativeMenuItem("显示主窗口");
        _showItem.Click += (_, _) => RestoreMainWindow();
        _exitItem = new NativeMenuItem("关闭软件");
        _exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        var menu = new NativeMenu();
        menu.Items.Add(_showItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(_exitItem);

        _trayIcon = new TrayIcon
        {
            Icon = icon,
            ToolTipText = toolTipText.Trim(),
            Menu = menu,
            IsVisible = true
        };
        _trayIcon.Clicked += (_, _) => RestoreMainWindow();

        var icons = new TrayIcons { _trayIcon };
        TrayIcon.SetIcons(_application, icons);
    }

    public event EventHandler? ExitRequested;

    public void UpdateText(string toolTipText, string showMainWindowText, string exitApplicationText)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(toolTipText);
        ArgumentException.ThrowIfNullOrWhiteSpace(showMainWindowText);
        ArgumentException.ThrowIfNullOrWhiteSpace(exitApplicationText);
        _trayIcon.ToolTipText = toolTipText.Trim();
        _showItem.Header = showMainWindowText.Trim();
        _exitItem.Header = exitApplicationText.Trim();
    }

    public void MinimizeToTray()
    {
        ThrowIfDisposed();
        _mainWindow.Hide();
    }

    public void RestoreMainWindow()
    {
        ThrowIfDisposed();
        Dispatcher.UIThread.Post(() =>
        {
            if (!_mainWindow.IsVisible)
            {
                _mainWindow.Show();
            }

            if (_mainWindow.WindowState == WindowState.Minimized)
            {
                _mainWindow.WindowState = WindowState.Normal;
            }

            _mainWindow.Activate();
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        TrayIcon.SetIcons(_application, new TrayIcons());
        _trayIcon.Dispose();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
