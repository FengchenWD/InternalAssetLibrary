using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using InternalAssetLibrary.Client.Core.Platform;
using InternalAssetLibrary.Client.Services;

namespace InternalAssetLibrary.Client;

public sealed partial class App : Application
{
    public static FileOpenActivationBroker FileOpenActivations { get; private set; } = new();

    internal static void ConfigureFileOpenActivations(FileOpenActivationBroker broker)
    {
        ArgumentNullException.ThrowIfNull(broker);
        FileOpenActivations = broker;
    }

    public override void Initialize()
    {
        WindowsImeCompatibility.Initialize();
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        ClientDiagnostics.RegisterUiDispatcherHandler();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
