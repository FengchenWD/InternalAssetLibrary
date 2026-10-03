using Avalonia;
using System;
using InternalAssetLibrary.Client.Core.Platform;
using InternalAssetLibrary.Client.Services;

namespace InternalAssetLibrary.Client;

internal static class Program
{
    private const string PurgeUserDataOption = "--purge-user-data";
    private const string PostUpdateOption = "--post-update";

    internal static Guid? PostUpdateTransactionId { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 1 && string.Equals(args[0], PurgeUserDataOption, StringComparison.Ordinal))
        {
            try
            {
                ClientApplicationDataPurger.Purge(AppPaths.RootDirectory, AppPaths.ManagedUserDataPaths);
                return 0;
            }
            catch
            {
                return 4;
            }
        }

        ClientDiagnostics.RegisterGlobalHandlers();
        try
        {
            var startupArguments = args;
            if (args.Length == 2 &&
                string.Equals(args[0], PostUpdateOption, StringComparison.Ordinal) &&
                Guid.TryParseExact(args[1], "N", out var transactionId))
            {
                PostUpdateTransactionId = transactionId;
                startupArguments = [];
            }

            if (!FileOpenCommandLine.TryParse(startupArguments, out var startupRequest, out _))
            {
                return 2;
            }

            using var coordinator = new SingleInstanceCoordinator("FengchenWD.InternalAssetLibrary");
            if (!coordinator.TryBecomePrimary())
            {
                var forwarded = coordinator.ForwardAsync(startupRequest, TimeSpan.FromSeconds(5))
                    .GetAwaiter()
                    .GetResult();
                if (forwarded)
                {
                    return 0;
                }

                if (!coordinator.TryBecomePrimary())
                {
                    return 3;
                }
            }

            var activationBroker = new FileOpenActivationBroker();
            App.ConfigureFileOpenActivations(activationBroker);
            if (startupRequest.HasFiles)
            {
                activationBroker.Publish(startupRequest);
            }

            coordinator.ActivationReceived += activationBroker.Publish;
            coordinator.StartListening();
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime([]);
        }
        catch (Exception exception)
        {
            ClientDiagnostics.WriteException("main", exception, isFatal: true);
            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder
        .Configure<App>()
        .UsePlatformDetect()
        .LogToTrace();
}
