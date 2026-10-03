using InternalAssetLibrary.Contracts;
using InternalAssetLibrary.Server.Realtime;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

internal static class LibraryRealtimeSelfTests
{
    public static void PublisherPreservesScopeEntityAndRevision() =>
        PublisherPreservesScopeEntityAndRevisionAsync().GetAwaiter().GetResult();

    public static void NotificationMappingsAndFailureIsolationWork() =>
        NotificationMappingsAndFailureIsolationWorkAsync().GetAwaiter().GetResult();

    public static void DesktopReconnectContractRemainsWired()
    {
        var root = RepositoryRoot();
        var clientSource = File.ReadAllText(Path.Combine(
            root,
            "src",
            "InternalAssetLibrary.Client.Core",
            "Realtime",
            "LibraryRealtimeClient.cs"));
        var windowSource = File.ReadAllText(Path.Combine(
            root,
            "src",
            "InternalAssetLibrary.Client",
            "MainWindow.Realtime.cs"));

        Contains("event EventHandler<LibraryRealtimeConnectedEventArgs>? Connected", clientSource);
        Contains("if (waitingForHandshake)", clientSource);
        Contains("isReconnect = _hasConnected", clientSource);
        Contains("RaiseConnected();", clientSource);
        Contains("if (!eventArgs.IsReconnect", windowSource);
        Contains("RefreshAfterRealtimeReconnectAsync", windowSource);
        Contains("await _api.GetCurrentUserAsync()", windowSource);
        Contains("refreshOwnProfile: false", windowSource);
        Contains("refreshAssets || refreshUsers", windowSource);
        Contains("!ReferenceEquals(sender, _realtimeClient)", windowSource);
        Contains("IsCurrentRealtimeSession(expectedClient, expectedUserId)", windowSource);
        Contains("currentUser.Id != expectedUserId", windowSource);
        Contains("ProfilePage.IsVisible", windowSource);
        Contains("SharedPage.IsVisible", windowSource);
    }

    private static async Task PublisherPreservesScopeEntityAndRevisionAsync()
    {
        var context = new RecordingHubContext();
        var publisher = new SignalRLibraryChangePublisher(context);
        var entityId = Guid.NewGuid();

        var first = await publisher.PublishAsync(" assets ", entityId);
        var second = await publisher.PublishAsync(LibraryChangeScopes.Tags);

        Equal(first.Revision + 1, second.Revision);
        Equal(LibraryChangeScopes.Assets, first.Scope);
        Equal<Guid?>(entityId, first.EntityId);
        Equal<Guid?>(null, second.EntityId);
        Equal(2, context.Proxy.Calls.Count);
        Equal(LibraryRealtimeProtocol.ChangeEvent, context.Proxy.Calls[0].Method);
        Equal(first, (LibraryChangeNotification)context.Proxy.Calls[0].Arguments.Single()!);
        Equal(second, (LibraryChangeNotification)context.Proxy.Calls[1].Arguments.Single()!);
    }

    private static async Task NotificationMappingsAndFailureIsolationWorkAsync()
    {
        var entityId = Guid.NewGuid();
        var changes = new[]
        {
            LibraryChangeTarget.Assets(entityId),
            LibraryChangeTarget.Tags(entityId),
            LibraryChangeTarget.Markers(entityId),
            LibraryChangeTarget.Users(entityId),
            LibraryChangeTarget.Profiles(entityId),
            LibraryChangeTarget.Derivatives(entityId),
            LibraryChangeTarget.Luts(entityId)
        };
        Equal(LibraryChangeScopes.Assets, changes[0].Scope);
        Equal(LibraryChangeScopes.Tags, changes[1].Scope);
        Equal(LibraryChangeScopes.Markers, changes[2].Scope);
        Equal(LibraryChangeScopes.Users, changes[3].Scope);
        Equal(LibraryChangeScopes.Profiles, changes[4].Scope);
        Equal(LibraryChangeScopes.Derivatives, changes[5].Scope);
        Equal(LibraryChangeScopes.Luts, changes[6].Scope);
        foreach (var change in changes)
        {
            Equal<Guid?>(entityId, change.EntityId);
        }

        var publisher = new FirstCallFailsPublisher();
        var logger = new RecordingLogger<BestEffortLibraryChangeNotifier>();
        var notifier = new BestEffortLibraryChangeNotifier(publisher, logger);
        await notifier.NotifyAsync(
            CancellationToken.None,
            LibraryChangeTarget.Assets(entityId),
            LibraryChangeTarget.Profiles(entityId));
        Equal(2, publisher.Attempts.Count);
        Equal(LibraryChangeScopes.Assets, publisher.Attempts[0].Scope);
        Equal(LibraryChangeScopes.Profiles, publisher.Attempts[1].Scope);
        Equal(1, logger.WarningCount);
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', but was '{actual}'.");
        }
    }

    private static void Contains(string expectedSubstring, string actual)
    {
        if (!actual.Contains(expectedSubstring, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected source to contain '{expectedSubstring}'.");
        }
    }

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

    private sealed class RecordingHubContext : IHubContext<LibraryRealtimeHub>
    {
        public RecordingClientProxy Proxy { get; } = new();

        public IHubClients Clients => new RecordingHubClients(Proxy);

        public IGroupManager Groups => throw new NotSupportedException();
    }

    private sealed class RecordingHubClients(IClientProxy proxy) : IHubClients
    {
        public IClientProxy All => proxy;

        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) =>
            throw new NotSupportedException();

        public IClientProxy Client(string connectionId) => throw new NotSupportedException();

        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => throw new NotSupportedException();

        public IClientProxy Group(string groupName) => throw new NotSupportedException();

        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) =>
            throw new NotSupportedException();

        public IClientProxy Groups(IReadOnlyList<string> groupNames) => throw new NotSupportedException();

        public IClientProxy User(string userId) => throw new NotSupportedException();

        public IClientProxy Users(IReadOnlyList<string> userIds) => throw new NotSupportedException();
    }

    private sealed class RecordingClientProxy : IClientProxy
    {
        public List<RecordedCall> Calls { get; } = [];

        public Task SendCoreAsync(
            string method,
            object?[] args,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(new RecordedCall(method, args));
            return Task.CompletedTask;
        }
    }

    private sealed class FirstCallFailsPublisher : ILibraryChangePublisher
    {
        public List<LibraryChangeTarget> Attempts { get; } = [];

        public Task<LibraryChangeNotification> PublishAsync(
            string scope,
            Guid? entityId = null,
            CancellationToken cancellationToken = default)
        {
            Attempts.Add(new LibraryChangeTarget(scope, entityId));
            if (Attempts.Count == 1)
            {
                throw new InvalidOperationException("Simulated SignalR failure.");
            }

            return Task.FromResult(new LibraryChangeNotification(Attempts.Count, scope, entityId));
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public int WarningCount { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                WarningCount++;
            }
        }
    }

    private sealed record RecordedCall(string Method, object?[] Arguments);
}
