using InternalAssetLibrary.Contracts;
using InternalAssetLibrary.Server.Security;
using Microsoft.AspNetCore.SignalR;

namespace InternalAssetLibrary.Server.Realtime;

internal sealed class LibraryRealtimeHub : Hub;

internal sealed class LibraryRealtimeAuthorizationMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments(LibraryRealtimeProtocol.HubPath))
        {
            _ = AccessControl.RequireUser(context);
        }

        return next(context);
    }
}

internal interface ILibraryChangePublisher
{
    Task<LibraryChangeNotification> PublishAsync(
        string scope,
        Guid? entityId = null,
        CancellationToken cancellationToken = default);
}

internal readonly record struct LibraryChangeTarget(string Scope, Guid? EntityId)
{
    public static LibraryChangeTarget Assets(Guid? entityId) => new(LibraryChangeScopes.Assets, entityId);

    public static LibraryChangeTarget Tags(Guid? entityId) => new(LibraryChangeScopes.Tags, entityId);

    public static LibraryChangeTarget Markers(Guid? entityId) => new(LibraryChangeScopes.Markers, entityId);

    public static LibraryChangeTarget Users(Guid? entityId) => new(LibraryChangeScopes.Users, entityId);

    public static LibraryChangeTarget Profiles(Guid? entityId) => new(LibraryChangeScopes.Profiles, entityId);

    public static LibraryChangeTarget Derivatives(Guid? entityId) => new(LibraryChangeScopes.Derivatives, entityId);

    public static LibraryChangeTarget Luts(Guid? entityId) => new(LibraryChangeScopes.Luts, entityId);
}

internal interface ILibraryChangeNotifier
{
    Task NotifyAsync(CancellationToken cancellationToken, params LibraryChangeTarget[] changes);
}

internal sealed class BestEffortLibraryChangeNotifier(
    ILibraryChangePublisher publisher,
    ILogger<BestEffortLibraryChangeNotifier> logger) : ILibraryChangeNotifier
{
    public async Task NotifyAsync(CancellationToken cancellationToken, params LibraryChangeTarget[] changes)
    {
        foreach (var change in changes)
        {
            try
            {
                await publisher.PublishAsync(change.Scope, change.EntityId, cancellationToken);
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Could not publish realtime change {Scope}/{EntityId} after a successful business update.",
                    change.Scope,
                    change.EntityId);
            }
        }
    }
}

internal sealed class SignalRLibraryChangePublisher(IHubContext<LibraryRealtimeHub> hubContext)
    : ILibraryChangePublisher
{
    private long _revision = DateTimeOffset.UtcNow.UtcTicks;

    public async Task<LibraryChangeNotification> PublishAsync(
        string scope,
        Guid? entityId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        var notification = new LibraryChangeNotification(
            Interlocked.Increment(ref _revision),
            scope.Trim(),
            entityId);
        await hubContext.Clients.All.SendAsync(
            LibraryRealtimeProtocol.ChangeEvent,
            notification,
            cancellationToken);
        return notification;
    }
}
