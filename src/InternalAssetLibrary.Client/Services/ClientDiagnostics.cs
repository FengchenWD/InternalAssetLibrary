using System.Reflection;
using System.Text;
using Avalonia.Threading;
using InternalAssetLibrary.Client.Core.Http;

namespace InternalAssetLibrary.Client.Services;

internal static class ClientDiagnostics
{
    private static readonly object WriteGate = new();
    private static int _globalHandlersRegistered;
    private static int _uiDispatcherHandlerRegistered;

    public static void RegisterGlobalHandlers()
    {
        if (Interlocked.Exchange(ref _globalHandlersRegistered, 1) != 0)
        {
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            var exception = eventArgs.ExceptionObject as Exception ??
                new InvalidOperationException("A non-Exception object reached the unhandled exception handler.");
            WriteException("app-domain", exception, eventArgs.IsTerminating);
        };
        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            WriteException("unobserved-task", eventArgs.Exception, isFatal: false);
            eventArgs.SetObserved();
        };
    }

    public static void RegisterUiDispatcherHandler()
    {
        if (Interlocked.Exchange(ref _uiDispatcherHandlerRegistered, 1) != 0)
        {
            return;
        }

        Dispatcher.UIThread.UnhandledException += (_, eventArgs) =>
        {
            var canceled = eventArgs.Exception is OperationCanceledException;
            WriteException("ui-dispatcher", eventArgs.Exception, isFatal: !canceled);
            eventArgs.Handled = canceled;
        };
    }

    public static void WriteException(string source, Exception exception, bool isFatal)
    {
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(source);
            ArgumentNullException.ThrowIfNull(exception);
            var version = Assembly.GetEntryAssembly()?
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion ?? "unknown";
            var details = UserVisibleNetworkMessage.RedactLocations(exception.ToString());
            var entry = new StringBuilder()
                .AppendLine("---")
                .Append("utc: ").AppendLine(DateTimeOffset.UtcNow.ToString("O"))
                .Append("source: ").AppendLine(source)
                .Append("fatal: ").AppendLine(isFatal ? "true" : "false")
                .Append("version: ").AppendLine(version)
                .Append("os: ").AppendLine(Environment.OSVersion.VersionString)
                .AppendLine(details)
                .ToString();

            lock (WriteGate)
            {
                Directory.CreateDirectory(AppPaths.DiagnosticsDirectory);
                var logPath = Path.Combine(
                    AppPaths.DiagnosticsDirectory,
                    $"client-{DateTime.UtcNow:yyyyMMdd}.log");
                File.AppendAllText(logPath, entry, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }
        catch
        {
            // Diagnostics must never replace the original exception.
        }
    }
}
