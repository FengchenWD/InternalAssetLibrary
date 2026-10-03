namespace InternalAssetLibrary.Client.Core.Auth;

public interface IRememberedLoginPasswordStore
{
    SessionTokenStorageScope StorageScope { get; }

    ValueTask<string?> GetAsync(
        Uri serverOrigin,
        Guid userId,
        CancellationToken cancellationToken = default);

    ValueTask SaveAsync(
        Uri serverOrigin,
        Guid userId,
        string password,
        CancellationToken cancellationToken = default);

    ValueTask DeleteAsync(
        Uri serverOrigin,
        Guid userId,
        CancellationToken cancellationToken = default);
}

public sealed class RememberedLoginPasswordStore : IRememberedLoginPasswordStore
{
    public const string DefaultCredentialTargetPrefix = "InternalAssetLibrary.LoginPassword";

    private readonly string _targetPrefix;
    private readonly Func<string, ISessionTokenStore> _storeFactory;
    private readonly Dictionary<Guid, ISessionTokenStore> _stores = [];
    private readonly object _sync = new();

    public RememberedLoginPasswordStore(
        string targetPrefix = DefaultCredentialTargetPrefix,
        Func<string, ISessionTokenStore>? storeFactory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPrefix);
        _targetPrefix = targetPrefix.Trim();
        _storeFactory = storeFactory ?? SessionTokenStoreFactory.CreateDefault;
    }

    public SessionTokenStorageScope StorageScope => OperatingSystem.IsWindows()
        ? SessionTokenStorageScope.OperatingSystemUserVault
        : SessionTokenStorageScope.MemoryOnly;

    public ValueTask<string?> GetAsync(
        Uri serverOrigin,
        Guid userId,
        CancellationToken cancellationToken = default) =>
        GetStore(userId).GetAsync(serverOrigin, cancellationToken);

    public ValueTask SaveAsync(
        Uri serverOrigin,
        Guid userId,
        string password,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(password) || password.Length > 128)
        {
            throw new ArgumentException("The remembered password must contain 1 to 128 characters.", nameof(password));
        }

        return GetStore(userId).SaveAsync(serverOrigin, password, cancellationToken);
    }

    public ValueTask DeleteAsync(
        Uri serverOrigin,
        Guid userId,
        CancellationToken cancellationToken = default) =>
        GetStore(userId).DeleteAsync(serverOrigin, cancellationToken);

    private ISessionTokenStore GetStore(Guid userId)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("The remembered account must have a user identifier.", nameof(userId));
        }

        lock (_sync)
        {
            if (_stores.TryGetValue(userId, out var store))
            {
                return store;
            }

            store = _storeFactory($"{_targetPrefix}.{userId:N}");
            _stores.Add(userId, store);
            return store;
        }
    }
}
