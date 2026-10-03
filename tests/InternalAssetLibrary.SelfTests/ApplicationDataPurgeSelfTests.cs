using InternalAssetLibrary.Client.Core.Auth;
using InternalAssetLibrary.Client.Core.Platform;

internal static class ApplicationDataPurgeSelfTests
{
    public static void ManagedDataAndCredentialsArePurgedWithoutTouchingMedia()
    {
        var parent = Directory.CreateTempSubdirectory("ial-purge-").FullName;
        var root = Path.Combine(parent, "app-data");
        var externalMediaDirectory = Path.Combine(parent, "media");
        var settings = Path.Combine(root, "settings.json");
        var diagnostics = Path.Combine(root, "diagnostics");
        var unmanagedMedia = Path.Combine(root, "downloaded-video.mp4");
        var externalMedia = Path.Combine(externalMediaDirectory, "source.wav");
        var credentialsPurged = false;

        try
        {
            Directory.CreateDirectory(diagnostics);
            Directory.CreateDirectory(externalMediaDirectory);
            File.WriteAllText(settings, "settings");
            File.WriteAllText(Path.Combine(diagnostics, "client.log"), "log");
            File.WriteAllBytes(unmanagedMedia, [1, 2, 3]);
            File.WriteAllBytes(externalMedia, [4, 5, 6]);

            ClientApplicationDataPurger.Purge(
                root,
                [settings, diagnostics],
                () => credentialsPurged = true);

            True(credentialsPurged);
            False(File.Exists(settings));
            False(Directory.Exists(diagnostics));
            True(File.Exists(unmanagedMedia));
            True(File.Exists(externalMedia));
        }
        finally
        {
            Directory.Delete(parent, true);
        }
    }

    public static void UnsafeTargetsAreRejectedBeforeCleanup()
    {
        var parent = Directory.CreateTempSubdirectory("ial-purge-scope-").FullName;
        var root = Path.Combine(parent, "app-data");
        var managed = Path.Combine(root, "settings.json");
        var external = Path.Combine(parent, "source.mp4");
        var credentialsPurged = false;

        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(managed, "settings");
            File.WriteAllBytes(external, [1, 2, 3]);

            Throws<ArgumentException>(() => ClientApplicationDataPurger.Purge(
                root,
                [managed, external],
                () => credentialsPurged = true));

            False(credentialsPurged);
            True(File.Exists(managed));
            True(File.Exists(external));
        }
        finally
        {
            Directory.Delete(parent, true);
        }
    }

    public static void CredentialTargetsAreStrictlyScoped()
    {
        var session = SessionTokenStoreFactory.DefaultCredentialTargetPrefix + ":v1:" + new string('A', 64);
        var password = RememberedLoginPasswordStore.DefaultCredentialTargetPrefix + "." +
                       Guid.NewGuid().ToString("N") + ":v1:" + new string('b', 64);

        True(ApplicationCredentialPurger.IsManagedCredentialTarget(session));
        True(ApplicationCredentialPurger.IsManagedCredentialTarget(password));
        False(ApplicationCredentialPurger.IsManagedCredentialTarget(null));
        False(ApplicationCredentialPurger.IsManagedCredentialTarget(
            SessionTokenStoreFactory.DefaultCredentialTargetPrefix + ".SelfTests:v1:" + new string('A', 64)));
        False(ApplicationCredentialPurger.IsManagedCredentialTarget(
            RememberedLoginPasswordStore.DefaultCredentialTargetPrefix + ".not-a-user:v1:" + new string('A', 64)));
        False(ApplicationCredentialPurger.IsManagedCredentialTarget(password + "extra"));
    }

    public static void InstallerUsesExplicitOptInCleanupCommand()
    {
        var root = RepositoryRoot();
        var installer = File.ReadAllText(Path.Combine(root, "packaging", "windows", "InternalAssetLibrary.Client.iss"));
        var program = File.ReadAllText(Path.Combine(root, "src", "InternalAssetLibrary.Client", "Program.cs"));
        var paths = File.ReadAllText(Path.Combine(root, "src", "InternalAssetLibrary.Client", "Services", "AppPaths.cs"));

        Contains("MB_YESNOCANCEL", installer);
        Contains("Choice = IDNO", installer);
        Contains("--purge-user-data", installer);
        Contains("Type: dirifempty; Name: \"{app}\"", installer);
        Contains("if UninstallSilent then", installer);
        Contains("--purge-user-data", program);
        True(program.IndexOf("PurgeUserDataOption", StringComparison.Ordinal) <
             program.IndexOf("ClientDiagnostics.RegisterGlobalHandlers", StringComparison.Ordinal));
        Contains("ManagedUserDataPaths", paths);
        False(installer.Contains(
            "[UninstallDelete]\r\nType: filesandordirs; Name: \"{localappdata}\\FengchenWD",
            StringComparison.Ordinal));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "InternalAssetLibrary.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private static void True(bool condition)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Expected true, but was false.");
        }
    }

    private static void False(bool condition) => True(!condition);

    private static void Contains(string expected, string actual)
    {
        if (!actual.Contains(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected source to contain '{expected}'.");
        }
    }

    private static void Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name} to be thrown.");
    }
}
