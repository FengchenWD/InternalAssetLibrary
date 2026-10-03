using InternalAssetLibrary.Updater;
using InternalAssetLibrary.Client.Core.Updates;
using InternalAssetLibrary.Contracts;
using System.Text.Json;

internal static class UpdaterSelfTests
{
    public static void TransitionVersionUsesVisibleSetupBeforeSilentUpdates()
    {
        False(ClientUpdateInstallationPolicy.ShouldUseSilentInstaller(
            "0.2.0-preview.6.7",
            "0.2.0-preview.6.8"));
        True(ClientUpdateInstallationPolicy.ShouldUseSilentInstaller(
            "0.2.0-preview.6.8",
            "0.2.0-preview.6.9"));
        False(ClientUpdateInstallationPolicy.ShouldUseSilentInstaller(
            "0.2.0-preview.6.8",
            "0.2.0-preview.6.8"));
    }

    public static async Task CamelCaseClientTransactionsDeserializeInUpdater()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var transactionId = Guid.NewGuid();
        var transaction = new ClientUpdateTransaction(
            1,
            transactionId,
            DateTimeOffset.UtcNow,
            42,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            @"D:\InternalAssetLibrary",
            @"D:\InternalAssetLibrary\InternalAssetLibrary.Client.exe",
            @"D:\Updates\healthy.txt",
            new ClientUpdatePackageInfo("0.2.0-preview.6.8", @"D:\Updates\setup.exe", 123, new string('A', 64)),
            null,
            "notes");
        await using var json = new MemoryStream();
        await JsonSerializer.SerializeAsync(json, transaction, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        json.Position = 0;

        var parsed = await UpdateWorkflow.DeserializeTransactionAsync(json);

        Equal(transactionId, parsed?.TransactionId);
        Equal(1, parsed?.SchemaVersion);
        Equal(42, parsed?.ClientProcessId);
        Equal("0.2.0-preview.6.8", parsed?.TargetPackage.Version);
    }

    public static void SilentInstallerArgumentsPreserveInstallDirectory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var installer = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "client setup.exe"));
        var installDirectory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "IAL custom install"));
        var startInfo = UpdateWorkflow.CreateInstallerStartInfo(installer, installDirectory);

        Equal(installer, startInfo.FileName);
        False(startInfo.UseShellExecute);
        SequenceEqual(
            [
                "/VERYSILENT",
                "/SUPPRESSMSGBOXES",
                "/NORESTART",
                "/CLOSEAPPLICATIONS",
                "/LANG=chinesesimplified",
                $"/DIR={installDirectory}"
            ],
            startInfo.ArgumentList);
    }

    private static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException("Sequences are not equal.");
        }
    }

    private static void False(bool value)
    {
        if (value)
        {
            throw new InvalidOperationException("Expected false, but was true.");
        }
    }

    private static void True(bool value)
    {
        if (!value)
        {
            throw new InvalidOperationException("Expected true, but was false.");
        }
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', but was '{actual}'.");
        }
    }
}
