using System.Text.Json.Serialization;

namespace InternalAssetLibrary.Server.Data;

internal sealed class AppState
{
    public int SchemaVersion { get; set; } = 1;
    public List<UserRecord> Users { get; set; } = [];
    public List<SessionRecord> Sessions { get; set; } = [];
    public List<AssetRecord> Assets { get; set; } = [];
    public List<AssetFolderRecord> AssetFolders { get; set; } = [];
    public List<TagRecord> Tags { get; set; } = [];
    public List<MarkerSetRecord> MarkerSets { get; set; } = [];
    public List<DownloadRecord> DownloadLog { get; set; } = [];
    public List<AuditRecord> AuditLog { get; set; } = [];
    public List<PendingObjectDeletionRecord> PendingObjectDeletions { get; set; } = [];
    public List<TeamLutRecord> TeamLuts { get; set; } = [];
    public List<LoginFailureRecord> LoginFailures { get; set; } = [];
    public List<DirectUploadSessionRecord> DirectUploadSessions { get; set; } = [];
    public ServerSettingsRecord? ServerSettings { get; set; }
}

internal sealed class ServerSettingsRecord
{
    public bool Initialized { get; set; }
    public long OriginalQuotaBytes { get; set; } = 100L * 1024 * 1024 * 1024;
    public long AudioMaxBytes { get; set; } = 5L * 1024 * 1024 * 1024;
    public long ImageMaxBytes { get; set; } = 5L * 1024 * 1024 * 1024;
    public long VideoMaxBytes { get; set; } = 15L * 1024 * 1024 * 1024;
    public long ThumbnailMaxBytes { get; set; } = 10L * 1024 * 1024;
    public long ProxyMaxBytes { get; set; } = 5L * 1024 * 1024 * 1024;
    public long LutMaxBytes { get; set; } = 16L * 1024 * 1024;
    public int RecycleRetentionDays { get; set; } = 15;
    public int AuditRetentionDays { get; set; } = 30;
    public int DownloadLogRetentionDays { get; set; } = 10;
    public bool BackupsEnabled { get; set; } = true;
    public int BackupIntervalHours { get; set; } = 24;
    public int BackupRetentionDays { get; set; } = 30;
    public string BackupLocalPath { get; set; } = "App_Data/backups";
    public string BackupObjectPrefix { get; set; } = "backups/sqlite";
    public double CapacityWarningRatio { get; set; } = 0.85;
    public double CapacityCriticalRatio { get; set; } = 0.95;
    public bool DerivativesEnabled { get; set; } = true;
    public int DerivativePollIntervalSeconds { get; set; } = 30;
    public int DerivativeProcessTimeoutSeconds { get; set; } = 300;
    public int ThumbnailMaxEdge { get; set; } = 640;
    public int SessionLifetimeDays { get; set; } = 7;
    public int LoginFailureLimit { get; set; } = 5;
    public int LoginFailureWindowMinutes { get; set; } = 15;
    public int LoginBlockMinutes { get; set; } = 15;
    public int MultipartPartSizeBytes { get; set; } = 16 * 1024 * 1024;
    public int UploadSessionLifetimeHours { get; set; } = 24;
    public int SignedUrlLifetimeMinutes { get; set; } = 15;
}

internal sealed class UserRecord
{
    public Guid Id { get; set; }
    public required string Username { get; set; }
    public required string NormalizedUsername { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public required string PasswordHash { get; set; }
    public bool MustChangePassword { get; set; } = true;
    public bool IsEnabled { get; set; } = true;
    public bool IsAdmin { get; set; }
    public HashSet<string> Permissions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? Email { get; set; }
    public string? NormalizedEmail { get; set; }
    public string? Bio { get; set; }
    public DateOnly? Birthday { get; set; }
    public string? Gender { get; set; }
    public string? CustomGender { get; set; }
    public string? Contact { get; set; }
    public ProfileVisibility BirthdayVisibility { get; set; } = ProfileVisibility.Private;
    public ProfileVisibility GenderVisibility { get; set; } = ProfileVisibility.Private;
    public ProfileVisibility ContactVisibility { get; set; } = ProfileVisibility.Private;
    public string? AvatarFileName { get; set; }
    public string? AvatarContentType { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class SessionRecord
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public required string TokenHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

internal sealed class AssetRecord
{
    public Guid Id { get; set; }
    public Guid CurrentVersionId { get; set; }
    public required string Name { get; set; }
    public required string Category { get; set; }
    public required string OriginalFileName { get; set; }
    public required string Extension { get; set; }
    public long SizeBytes { get; set; }
    public double? DurationSeconds { get; set; }
    public required string ContentHash { get; set; }
    public required string ObjectKey { get; set; }
    public bool HasOriginal { get; set; }
    public string? Notes { get; set; }
    public HashSet<string> Tags { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Guid? FolderId { get; set; }
    public Guid UploadedByUserId { get; set; }
    public string UploadedByUsername { get; set; } = string.Empty;
    public string UploadedByDisplayName { get; set; } = string.Empty;
    public DateTimeOffset UploadedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int Version { get; set; } = 1;
    public AssetState State { get; set; } = AssetState.Active;
    public DateTimeOffset? RecycledAt { get; set; }
    public DateTimeOffset? PurgeAfter { get; set; }
    public List<AssetVersionRecord> PreviousVersions { get; set; } = [];
    public AssetDerivativesRecord Derivatives { get; set; } = new();
}

internal sealed class AssetFolderRecord
{
    public Guid Id { get; set; }
    public Guid? ParentId { get; set; }
    public required string Name { get; set; }
    public Guid CreatedByUserId { get; set; }
    public string CreatedByUsername { get; set; } = string.Empty;
    public string CreatedByDisplayName { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class AssetDerivativesRecord
{
    public AssetDerivativeRecord Thumbnail { get; set; } = new();
    public AssetDerivativeRecord Proxy { get; set; } = new();
}

internal sealed class AssetDerivativeRecord
{
    public Guid AssetVersionId { get; set; }
    public DerivativeState State { get; set; } = DerivativeState.Unavailable;
    public int FormatVersion { get; set; } = 1;
    public string? ObjectKey { get; set; }
    public string? ContentType { get; set; }
    public long SizeBytes { get; set; }
    public string? ETag { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public int RetryCount { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public bool ServerBackfillEligible { get; set; }
}

internal sealed class TagRecord
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class AssetVersionRecord
{
    public Guid Id { get; set; }
    public int Version { get; set; }
    public required string OriginalFileName { get; set; }
    public required string Extension { get; set; }
    public long SizeBytes { get; set; }
    public required string ContentHash { get; set; }
    public required string ObjectKey { get; set; }
    public bool HasOriginal { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset PurgeAfter { get; set; }
}

internal sealed class TeamLutRecord
{
    public Guid Id { get; set; }
    public Guid CurrentVersionId { get; set; }
    public required string Name { get; set; }
    public required string OriginalFileName { get; set; }
    public string? Notes { get; set; }
    public long SizeBytes { get; set; }
    public required string ContentHash { get; set; }
    public required string ObjectKey { get; set; }
    public bool HasContent { get; set; }
    public int Version { get; set; } = 1;
    public Guid UploadedByUserId { get; set; }
    public string UploadedByUsername { get; set; } = string.Empty;
    public string UploadedByDisplayName { get; set; } = string.Empty;
    public DateTimeOffset UploadedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public TeamLutState State { get; set; } = TeamLutState.Active;
    public DateTimeOffset? RecycledAt { get; set; }
    public DateTimeOffset? PurgeAfter { get; set; }
}

internal sealed class MarkerSetRecord
{
    public Guid Id { get; set; }
    public Guid AssetId { get; set; }
    public Guid AssetVersionId { get; set; }
    public required string Name { get; set; }
    public Guid OwnerUserId { get; set; }
    public string OwnerDisplayName { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<MarkerRecord> Markers { get; set; } = [];
}

internal sealed class MarkerRecord
{
    public Guid Id { get; set; }
    public long TimeMilliseconds { get; set; }
    public string? Name { get; set; }
    public string? Note { get; set; }
}

internal sealed class AuditRecord
{
    public Guid Id { get; set; }
    public Guid? ActorUserId { get; set; }
    public required string Action { get; set; }
    public required string TargetType { get; set; }
    public string? TargetId { get; set; }
    public string? Detail { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

internal sealed class DownloadRecord
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid AssetId { get; set; }
    public Guid AssetVersionId { get; set; }
    public long SizeBytes { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

internal sealed class PendingObjectDeletionRecord
{
    public required string ObjectKey { get; set; }
    public string? ProviderUploadId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public int FailureCount { get; set; }
}

internal sealed class LoginFailureRecord
{
    public required string IdentifierHash { get; set; }
    public required string RemoteAddress { get; set; }
    public int FailureCount { get; set; }
    public DateTimeOffset FirstFailureAt { get; set; }
    public DateTimeOffset LastFailureAt { get; set; }
    public DateTimeOffset? BlockedUntil { get; set; }
}

internal sealed class DirectUploadSessionRecord
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid AssetId { get; set; }
    public Guid AssetVersionId { get; set; }
    public required string ObjectKey { get; set; }
    public required string ProviderUploadId { get; set; }
    public long SizeBytes { get; set; }
    public required string Sha256 { get; set; }
    public int PartSizeBytes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter<ProfileVisibility>))]
internal enum ProfileVisibility
{
    Private,
    Team
}

[JsonConverter(typeof(JsonStringEnumConverter<AssetState>))]
internal enum AssetState
{
    Active,
    Recycled
}

[JsonConverter(typeof(JsonStringEnumConverter<DerivativeState>))]
internal enum DerivativeState
{
    Queued,
    Processing,
    Ready,
    Unavailable,
    Failed
}

[JsonConverter(typeof(JsonStringEnumConverter<TeamLutState>))]
internal enum TeamLutState
{
    Active,
    Recycled
}

internal static class PermissionNames
{
    public const string BrowseAssets = "assets.browse";
    public const string PreviewAssets = "assets.preview";
    public const string DownloadAssets = "assets.download";
    public const string UploadAssets = "assets.upload";
    public const string ModifyTags = "assets.tags";
    public const string MaintainMarkers = "markers.maintain";
    public const string EditOwnAssets = "assets.edit-own";
    public const string DeleteOwnAssets = "assets.delete-own";
    public const string AccessBgm = "assets.category.bgm";
    public const string AccessSoundEffects = "assets.category.sound-effect";
    public const string AccessImages = "assets.category.image";
    public const string AccessVideos = "assets.category.video";
    public const string ManageSiteContent = "site.content.manage";
    public const string ManageSiteMedia = "site.media.manage";
    public const string ReadSiteAudit = "site.audit.read";

    public static readonly HashSet<string> All = new(StringComparer.OrdinalIgnoreCase)
    {
        BrowseAssets,
        PreviewAssets,
        DownloadAssets,
        UploadAssets,
        ModifyTags,
        MaintainMarkers,
        EditOwnAssets,
        DeleteOwnAssets,
        AccessBgm,
        AccessSoundEffects,
        AccessImages,
        AccessVideos,
        ManageSiteContent,
        ManageSiteMedia,
        ReadSiteAudit
    };

    public static HashSet<string> MemberDefaults() => new(All, StringComparer.OrdinalIgnoreCase);
}

internal static class AssetCategories
{
    public const string Bgm = "bgm";
    public const string SoundEffect = "sound-effect";
    public const string Image = "image";
    public const string Video = "video";

    public static readonly HashSet<string> All = new(StringComparer.OrdinalIgnoreCase)
    {
        Bgm,
        SoundEffect,
        Image,
        Video
    };

    public static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".aac", ".ogg", ".m4a", ".flac", ".ape", ".wav", ".alac", ".caf"
    };

    public static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".tiff", ".tif", ".svg", ".ai", ".eps", ".psd"
    };

    public static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".flv", ".webm", ".m3u8"
    };
}

internal sealed class DevelopmentStorageOptions
{
    public string DataPath { get; set; } = "App_Data/development-data.json";
    public string AvatarPath { get; set; } = "App_Data/avatars";
    public string ObjectPath { get; set; } = "App_Data/objects";
    public bool AllowInProduction { get; set; }
}

internal sealed class SecurityOptions
{
    public int SessionLifetimeDays { get; set; } = 7;
    public int PasswordIterations { get; set; } = 600_000;
}

internal sealed class LibraryOptions
{
    public long OriginalQuotaBytes { get; set; } = 107_374_182_400;
    public long AudioMaxBytes { get; set; } = 5_368_709_120;
    public long ImageMaxBytes { get; set; } = 5_368_709_120;
    public long VideoMaxBytes { get; set; } = 16_106_127_360;
    public int RecycleRetentionDays { get; set; } = 15;
    public int AuditRetentionDays { get; set; } = 30;
    public int DownloadLogRetentionDays { get; set; } = 10;
    public long ThumbnailMaxBytes { get; set; } = 10 * 1024 * 1024;
    public long ProxyMaxBytes { get; set; } = 5L * 1024 * 1024 * 1024;
    public long LutMaxBytes { get; set; } = 16L * 1024 * 1024;
}

internal sealed class DerivativeProcessingOptions
{
    public bool Enabled { get; set; }
    public string FfmpegPath { get; set; } = "ffmpeg";
    public int PollIntervalSeconds { get; set; } = 30;
    public int ProcessTimeoutSeconds { get; set; } = 300;
    public int ThumbnailMaxEdge { get; set; } = 640;
}

internal sealed class BootstrapOptions
{
    public bool Enabled { get; set; }
    public string Username { get; set; } = "admin";
    public string TemporaryPassword { get; set; } = string.Empty;
    public string DisplayName { get; set; } = "Administrator";
}
