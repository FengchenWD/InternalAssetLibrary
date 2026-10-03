using System.Collections.Concurrent;

namespace InternalAssetLibrary.Client.Core.Auth;

public enum SessionTokenStorageScope
{
    MemoryOnly,
    OperatingSystemUserVault
}

public interface ISessionTokenStore
{
    SessionTokenStorageScope StorageScope { get; }

    ValueTask<string?> GetAsync(Uri serverOrigin, CancellationToken cancellationToken = default);

    ValueTask SaveAsync(Uri serverOrigin, string token, CancellationToken cancellationToken = default);

    ValueTask DeleteAsync(Uri serverOrigin, CancellationToken cancellationToken = default);
}

public static class SessionTokenStoreFactory
{
    public const string DefaultCredentialTargetPrefix = "InternalAssetLibrary.SessionToken";

    public static ISessionTokenStore CreateDefault(
        string credentialTargetPrefix = DefaultCredentialTargetPrefix) =>
        OperatingSystem.IsWindows()
            ? new WindowsCredentialSessionTokenStore(credentialTargetPrefix)
            : new InMemorySessionTokenStore();
}

public sealed class InMemorySessionTokenStore : ISessionTokenStore
{
    private readonly ConcurrentDictionary<string, string> _tokens = new(StringComparer.Ordinal);

    public SessionTokenStorageScope StorageScope => SessionTokenStorageScope.MemoryOnly;

    public ValueTask<string?> GetAsync(
        Uri serverOrigin,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _tokens.TryGetValue(ServerOrigin.Normalize(serverOrigin), out var token);
        return ValueTask.FromResult(token);
    }

    public ValueTask SaveAsync(
        Uri serverOrigin,
        string token,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        _tokens[ServerOrigin.Normalize(serverOrigin)] = token;
        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteAsync(
        Uri serverOrigin,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _tokens.TryRemove(ServerOrigin.Normalize(serverOrigin), out _);
        return ValueTask.CompletedTask;
    }
}

internal static class ServerOrigin
{
    public static string Normalize(Uri serverOrigin)
    {
        ArgumentNullException.ThrowIfNull(serverOrigin);
        if (!serverOrigin.IsAbsoluteUri ||
            (serverOrigin.Scheme != Uri.UriSchemeHttps && serverOrigin.Scheme != Uri.UriSchemeHttp) ||
            !string.IsNullOrEmpty(serverOrigin.UserInfo))
        {
            throw new ArgumentException(
                "The server origin must be an absolute HTTP or HTTPS URI without user information.",
                nameof(serverOrigin));
        }

        return serverOrigin.GetLeftPart(UriPartial.Authority) + "/";
    }
}
