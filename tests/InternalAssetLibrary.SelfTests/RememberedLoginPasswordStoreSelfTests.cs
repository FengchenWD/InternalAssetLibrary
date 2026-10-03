using InternalAssetLibrary.Client.Core.Auth;
using InternalAssetLibrary.Client.Core.Settings;

internal static class RememberedLoginPasswordStoreSelfTests
{
    public static void AccountsAndPasswordsRemainIsolated() =>
        AccountsAndPasswordsRemainIsolatedAsync().GetAwaiter().GetResult();

    public static void DeletingCredentialIsIdempotentAndScoped() =>
        DeletingCredentialIsIdempotentAndScopedAsync().GetAwaiter().GetResult();

    private static async Task AccountsAndPasswordsRemainIsolatedAsync()
    {
        var stores = new Dictionary<string, ISessionTokenStore>(StringComparer.Ordinal);
        var vault = new RememberedLoginPasswordStore(
            $"InternalAssetLibrary.SelfTests.Passwords.{Guid.NewGuid():N}",
            prefix =>
            {
                if (!stores.TryGetValue(prefix, out var store))
                {
                    store = new InMemorySessionTokenStore();
                    stores.Add(prefix, store);
                }

                return store;
            });
        var origin = new Uri("https://assets.example.invalid/");
        var otherOrigin = new Uri("https://other.example.invalid/");
        var firstUser = Guid.NewGuid();
        var secondUser = Guid.NewGuid();

        await vault.SaveAsync(origin, firstUser, "first-password-123");
        await vault.SaveAsync(origin, secondUser, "second-password-456");
        await vault.SaveAsync(otherOrigin, firstUser, "other-password-789");

        Equal("first-password-123", await vault.GetAsync(origin, firstUser));
        Equal("second-password-456", await vault.GetAsync(origin, secondUser));
        Equal("other-password-789", await vault.GetAsync(otherOrigin, firstUser));

        await vault.DeleteAsync(origin, firstUser);
        Equal<string?>(null, await vault.GetAsync(origin, firstUser));
        Equal("second-password-456", await vault.GetAsync(origin, secondUser));
        Equal("other-password-789", await vault.GetAsync(otherOrigin, firstUser));

        var newest = DateTimeOffset.UtcNow;
        var normalized = new ClientSettings
        {
            SavedLoginAccounts =
            [
                new SavedLoginAccount(firstUser, " old-name ", " Old ", false, newest.AddMinutes(-1)),
                new SavedLoginAccount(firstUser, " new-name ", " New ", true, newest),
                new SavedLoginAccount(Guid.Empty, "invalid", "Invalid", false, newest)
            ]
        }.ValidateAndNormalize();

        Equal(1, normalized.SavedLoginAccounts.Count);
        Equal("new-name", normalized.SavedLoginAccounts[0].Username);
        Equal("New", normalized.SavedLoginAccounts[0].DisplayName);
        Equal(true, normalized.SavedLoginAccounts[0].PasswordRemembered);
    }

    private static async Task DeletingCredentialIsIdempotentAndScopedAsync()
    {
        var stores = new Dictionary<string, ISessionTokenStore>(StringComparer.Ordinal);
        var vault = new RememberedLoginPasswordStore(
            $"InternalAssetLibrary.SelfTests.PasswordDeletion.{Guid.NewGuid():N}",
            prefix =>
            {
                if (!stores.TryGetValue(prefix, out var store))
                {
                    store = new InMemorySessionTokenStore();
                    stores.Add(prefix, store);
                }

                return store;
            });
        var origin = new Uri("https://assets.example.invalid/");
        var deletedUser = Guid.NewGuid();
        var retainedUser = Guid.NewGuid();

        await vault.SaveAsync(origin, deletedUser, "deleted-password-123");
        await vault.SaveAsync(origin, retainedUser, "retained-password-456");
        await vault.DeleteAsync(origin, deletedUser);
        await vault.DeleteAsync(origin, deletedUser);

        Equal<string?>(null, await vault.GetAsync(origin, deletedUser));
        Equal("retained-password-456", await vault.GetAsync(origin, retainedUser));
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', but was '{actual}'.");
        }
    }
}
