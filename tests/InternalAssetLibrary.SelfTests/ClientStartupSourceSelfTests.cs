internal static class ClientStartupSourceSelfTests
{
    public static void UiDispatcherDiagnosticsWaitForPlatformInitialization()
    {
        var root = RepositoryRoot();
        var diagnostics = File.ReadAllText(Path.Combine(
            root,
            "src",
            "InternalAssetLibrary.Client",
            "Services",
            "ClientDiagnostics.cs"));
        var app = File.ReadAllText(Path.Combine(
            root,
            "src",
            "InternalAssetLibrary.Client",
            "App.axaml.cs"));

        var globalHandlerStart = RequiredIndexOf(
            diagnostics,
            "public static void RegisterGlobalHandlers()");
        var uiHandlerStart = RequiredIndexOf(
            diagnostics,
            "public static void RegisterUiDispatcherHandler()");
        var globalHandler = diagnostics[globalHandlerStart..uiHandlerStart];
        False(globalHandler.Contains("Dispatcher.UIThread", StringComparison.Ordinal));
        True(diagnostics[uiHandlerStart..].Contains(
            "Dispatcher.UIThread.UnhandledException",
            StringComparison.Ordinal));

        var frameworkInitialization = RequiredIndexOf(
            app,
            "public override void OnFrameworkInitializationCompleted()");
        var dispatcherRegistration = app.IndexOf(
            "ClientDiagnostics.RegisterUiDispatcherHandler();",
            frameworkInitialization,
            StringComparison.Ordinal);
        var mainWindowCreation = app.IndexOf(
            "desktop.MainWindow = new MainWindow();",
            frameworkInitialization,
            StringComparison.Ordinal);
        True(dispatcherRegistration >= frameworkInitialization);
        True(mainWindowCreation > dispatcherRegistration);
    }

    private static int RequiredIndexOf(string value, string search)
    {
        var index = value.IndexOf(search, StringComparison.Ordinal);
        if (index < 0)
        {
            throw new InvalidOperationException($"Expected source to contain '{search}'.");
        }

        return index;
    }

    private static void True(bool value)
    {
        if (!value)
        {
            throw new InvalidOperationException("Expected true.");
        }
    }

    private static void False(bool value) => True(!value);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "InternalAssetLibrary.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
