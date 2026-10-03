using InternalAssetLibrary.Client.Core.Http;
using InternalAssetLibrary.Client.Core.Realtime;
using InternalAssetLibrary.Contracts;

if (args.Length != 3 || !Uri.TryCreate(args[0], UriKind.Absolute, out var serverOrigin))
{
    Console.Error.WriteLine("Usage: InternalAssetLibrary.RealtimeSmoke <server-origin> <temporary-password> <new-password>");
    return 2;
}

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
var tokens = new StaticAccessTokenProvider();
var api = new AssetLibraryApiClient(httpClient, serverOrigin, tokens);
var login = await api.LoginAsync(new ApiLoginRequest("admin", args[1]), timeout.Token);
tokens.AccessToken = login.Token;
var user = login.User.MustChangePassword
    ? await api.ChangePasswordAsync(new ApiChangePasswordRequest(args[1], args[2]), timeout.Token)
    : login.User;

var notificationReceived = new TaskCompletionSource<LibraryChangeNotification>(
    TaskCreationOptions.RunContinuationsAsynchronously);
var connected = new TaskCompletionSource(
    TaskCreationOptions.RunContinuationsAsynchronously);
await using var realtime = new LibraryRealtimeClient(httpClient, serverOrigin, tokens);
realtime.Connected += (_, eventArgs) =>
{
    if (!eventArgs.IsReconnect)
    {
        connected.TrySetResult();
    }
};
realtime.ChangeReceived += (_, eventArgs) =>
{
    if (eventArgs.Notification.Scope == LibraryChangeScopes.Profiles &&
        eventArgs.Notification.EntityId == user.Id)
    {
        notificationReceived.TrySetResult(eventArgs.Notification);
    }
};

await realtime.StartAsync(timeout.Token);
await connected.Task.WaitAsync(timeout.Token);
await Task.Delay(TimeSpan.FromSeconds(36), timeout.Token);
await api.UpdateCurrentProfileAsync(new ApiUpdateProfileRequest(
    user.DisplayName,
    $"realtime-smoke-{Guid.NewGuid():N}",
    user.Birthday,
    user.Gender,
    user.CustomGender,
    user.Contact,
    user.BirthdayVisibility,
    user.GenderVisibility,
    user.ContactVisibility), timeout.Token);

var notification = await notificationReceived.Task.WaitAsync(timeout.Token);
if (notification.Revision <= 0)
{
    throw new InvalidOperationException("The SignalR notification revision was invalid.");
}

Console.WriteLine($"PASS SignalR desktop subscription: {notification.Scope}/{notification.EntityId:D} revision {notification.Revision}");
return 0;
