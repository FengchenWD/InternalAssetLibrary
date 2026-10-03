using System.ComponentModel;
using InternalAssetLibrary.Client.Core.Auth;

internal static class SessionTokenStoreSelfTests
{
    public static void OriginScopedStorageWorks() =>
        OriginScopedStorageWorksAsync().GetAwaiter().GetResult();

    private static async Task OriginScopedStorageWorksAsync()
    {
        await AssertOriginIsolationAsync(new InMemorySessionTokenStore());

        var defaultStore = SessionTokenStoreFactory.CreateDefault(
            $"InternalAssetLibrary.SelfTests.Factory.{Guid.NewGuid():N}");
        Equal(
            OperatingSystem.IsWindows()
                ? SessionTokenStorageScope.OperatingSystemUserVault
                : SessionTokenStorageScope.MemoryOnly,
            defaultStore.StorageScope);

        if (!OperatingSystem.IsWindows())
        {
            await AssertOriginIsolationAsync(defaultStore);
            return;
        }

        var windowsStore = new WindowsCredentialSessionTokenStore(
            $"InternalAssetLibrary.SelfTests.{Guid.NewGuid():N}");
        try
        {
            await AssertOriginIsolationAsync(windowsStore);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1312)
        {
            Console.WriteLine("INFO  Windows Credential Manager integration skipped: no logon session is available.");
        }
    }

    private static async Task AssertOriginIsolationAsync(ISessionTokenStore store)
    {
        var firstOrigin = new Uri("https://FIRST.example.invalid:443/api/assets?ignored=true");
        var normalizedFirstOrigin = new Uri("https://first.example.invalid/");
        var secondOrigin = new Uri("https://first.example.invalid:8443/");
        var firstToken = $"first-{Guid.NewGuid():N}";
        var replacementToken = $"replacement-{Guid.NewGuid():N}";
        var secondToken = $"second-{Guid.NewGuid():N}";

        try
        {
            await store.DeleteAsync(firstOrigin);
            await store.DeleteAsync(secondOrigin);
            await store.SaveAsync(firstOrigin, firstToken);
            await store.SaveAsync(secondOrigin, secondToken);

            Equal(firstToken, await store.GetAsync(normalizedFirstOrigin));
            Equal(secondToken, await store.GetAsync(secondOrigin));

            await store.SaveAsync(normalizedFirstOrigin, replacementToken);
            Equal(replacementToken, await store.GetAsync(firstOrigin));

            await store.DeleteAsync(firstOrigin);
            Equal<string?>(null, await store.GetAsync(normalizedFirstOrigin));
            Equal(secondToken, await store.GetAsync(secondOrigin));
        }
        finally
        {
            await store.DeleteAsync(firstOrigin);
            await store.DeleteAsync(secondOrigin);
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
