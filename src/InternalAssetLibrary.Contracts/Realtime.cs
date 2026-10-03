namespace InternalAssetLibrary.Contracts;

public sealed record LibraryChangeNotification(long Revision, string Scope, Guid? EntityId);

public static class LibraryRealtimeProtocol
{
    public const string HubPath = "/hubs/library";
    public const string ChangeEvent = "libraryChanged";
}

public static class LibraryChangeScopes
{
    public const string Assets = "assets";
    public const string Tags = "tags";
    public const string Markers = "markers";
    public const string Users = "users";
    public const string Profiles = "profiles";
    public const string Derivatives = "derivatives";
    public const string Luts = "luts";
}
