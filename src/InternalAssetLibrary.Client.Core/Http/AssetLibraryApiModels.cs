using System.Text.Json.Serialization;
using InternalAssetLibrary.Contracts;

namespace InternalAssetLibrary.Client.Core.Http;

public sealed record ApiLoginRequest(string Identifier, string Password);

public sealed record ApiLoginResponse(string Token, ApiCurrentUser User);

public sealed record ApiChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record ApiUpdateProfileRequest(
    string? DisplayName,
    string? Bio,
    DateOnly? Birthday,
    ApiProfileGender? Gender,
    string? CustomGender,
    string? Contact,
    ApiProfileVisibility BirthdayVisibility,
    ApiProfileVisibility GenderVisibility,
    ApiProfileVisibility ContactVisibility);

public sealed record ApiBindEmailRequest(string Email, string CurrentPassword);

public sealed record ApiPasswordConfirmationRequest(string CurrentPassword);

public sealed record ApiCurrentUser(
    Guid Id,
    string Username,
    string DisplayName,
    string? Email,
    string? Bio,
    DateOnly? Birthday,
    ApiProfileGender? Gender,
    string? CustomGender,
    string? Contact,
    ApiProfileVisibility BirthdayVisibility,
    ApiProfileVisibility GenderVisibility,
    ApiProfileVisibility ContactVisibility,
    bool HasAvatar,
    bool IsAdmin,
    bool IsEnabled,
    bool MustChangePassword,
    IReadOnlyList<string> Permissions);

public sealed record ApiAdminUser(
    Guid Id,
    string Username,
    string DisplayName,
    bool IsEnabled,
    bool IsAdmin,
    bool MustChangePassword,
    bool HasEmail,
    bool HasAvatar,
    IReadOnlyList<string> Permissions,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ApiCreateAdminUserRequest(
    string Username,
    string TemporaryPassword,
    string? DisplayName,
    bool IsAdmin,
    IReadOnlyList<string> Permissions);

public sealed record ApiAdminAccountStatusRequest(bool IsEnabled);

public sealed record ApiAdminPermissionsRequest(bool IsAdmin, IReadOnlyList<string> Permissions);

public sealed record ApiAdminUsernameRequest(string Username);

public sealed record ApiAdminResetPasswordRequest(string TemporaryPassword);

public sealed record ApiAuditRecord(
    Guid Id,
    Guid? ActorUserId,
    string Action,
    string TargetType,
    string? TargetId,
    string? Detail,
    DateTimeOffset OccurredAt);

public sealed record ApiServerSettings(
    long OriginalQuotaBytes,
    long AudioMaxBytes,
    long ImageMaxBytes,
    long VideoMaxBytes,
    long ThumbnailMaxBytes,
    long ProxyMaxBytes,
    long LutMaxBytes,
    int RecycleRetentionDays,
    int AuditRetentionDays,
    int DownloadLogRetentionDays,
    bool BackupsEnabled,
    int BackupIntervalHours,
    int BackupRetentionDays,
    string BackupLocalPath,
    string BackupObjectPrefix,
    double CapacityWarningRatio,
    double CapacityCriticalRatio,
    bool DerivativesEnabled,
    int DerivativePollIntervalSeconds,
    int DerivativeProcessTimeoutSeconds,
    int ThumbnailMaxEdge,
    int SessionLifetimeDays,
    int LoginFailureLimit,
    int LoginFailureWindowMinutes,
    int LoginBlockMinutes,
    int MultipartPartSizeBytes,
    int UploadSessionLifetimeHours,
    int SignedUrlLifetimeMinutes);

public sealed record ApiPublicUserProfile(
    Guid Id,
    string Username,
    string DisplayName,
    string? Bio,
    DateOnly? Birthday,
    ApiProfileGender? Gender,
    string? CustomGender,
    string? Contact,
    bool HasAvatar,
    bool IsEnabled,
    DateTimeOffset CreatedAt,
    IReadOnlyDictionary<string, int> AssetCounts);

public sealed record ApiPage<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);

public sealed record ApiUserListQuery(string? Search = null, int Page = 1, int PageSize = 30);

public sealed record ApiAssetUploader(Guid Id, string Username, string DisplayName);

public sealed record ApiAsset(
    Guid Id,
    Guid CurrentVersionId,
    string Name,
    ApiAssetCategory Category,
    string OriginalFileName,
    string Extension,
    long SizeBytes,
    string ContentHash,
    string ObjectKey,
    bool HasOriginal,
    string? Notes,
    IReadOnlyList<string> Tags,
    ApiAssetUploader UploadedBy,
    DateTimeOffset UploadedAt,
    DateTimeOffset UpdatedAt,
    int Version,
    int PreviousVersionCount,
    ApiAssetState State,
    DateTimeOffset? RecycledAt,
    DateTimeOffset? PurgeAfter,
    AssetDerivativesInfo? Derivatives = null,
    double? DurationSeconds = null,
    Guid? FolderId = null);

public sealed record ApiAssetFolder(
    Guid Id,
    Guid? ParentId,
    string Name,
    ApiAssetUploader CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ApiObjectDownloadResult(
    string? ETag,
    long? ContentLength,
    string? ContentType,
    DateTimeOffset? LastModified);

public sealed record ApiTeamLutListQuery(
    string? Search = null,
    TeamLutState State = TeamLutState.Active,
    int Page = 1,
    int PageSize = 100,
    Guid? UploaderId = null);

public sealed record ApiAssetListQuery(
    string? Search = null,
    ApiAssetCategory? Category = null,
    string? Tag = null,
    Guid? UploaderId = null,
    ApiAssetState State = ApiAssetState.Active,
    int Page = 1,
    int PageSize = 40,
    ApiAssetSort Sort = ApiAssetSort.Name,
    ApiSortOrder Order = ApiSortOrder.Ascending,
    DateTimeOffset? UploadedFrom = null,
    DateTimeOffset? UploadedTo = null,
    Guid? FolderId = null,
    bool IncludeDescendantFolders = true,
    bool RootOnly = false);

public sealed record ApiRecycleBinListQuery(
    string? Search = null,
    ApiAssetCategory? Category = null,
    string? Tag = null,
    int Page = 1,
    int PageSize = 40,
    ApiAssetSort Sort = ApiAssetSort.Name,
    ApiSortOrder Order = ApiSortOrder.Ascending,
    DateTimeOffset? UploadedFrom = null,
    DateTimeOffset? UploadedTo = null);

public sealed record ApiCreateAssetRequest(
    string Name,
    ApiAssetCategory Category,
    string OriginalFileName,
    long SizeBytes,
    string ContentHash,
    string? Notes,
    IReadOnlyList<string> Tags,
    double? DurationSeconds = null,
    Guid? FolderId = null);

public sealed record ApiUpdateAssetRequest(string Name, string? Notes);

public sealed record ApiUpdateAssetCategoryRequest(ApiAssetCategory Category);

public sealed record ApiCreateAssetFolderRequest(string Name, Guid? ParentId = null);

public sealed record ApiRenameAssetFolderRequest(string Name);

public sealed record ApiMoveAssetFolderRequest(Guid? ParentId);

public sealed record ApiMoveAssetToFolderRequest(Guid? FolderId);

public sealed record ApiUpdateAssetTagsRequest(
    IReadOnlyList<string> Tags,
    bool CreateMissing = true);

public sealed record ApiTag(
    Guid Id,
    string Name,
    Guid CreatedByUserId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int UsageCount);

public sealed record ApiTagNameRequest(string Name);

[JsonConverter(typeof(JsonStringEnumConverter<ApiProfileVisibility>))]
public enum ApiProfileVisibility
{
    [JsonStringEnumMemberName("private")]
    Private,

    [JsonStringEnumMemberName("team")]
    Team
}

[JsonConverter(typeof(JsonStringEnumConverter<ApiProfileGender>))]
public enum ApiProfileGender
{
    [JsonStringEnumMemberName("male")]
    Male,

    [JsonStringEnumMemberName("female")]
    Female,

    [JsonStringEnumMemberName("custom")]
    Custom
}

[JsonConverter(typeof(JsonStringEnumConverter<ApiAssetCategory>))]
public enum ApiAssetCategory
{
    [JsonStringEnumMemberName("bgm")]
    Bgm,

    [JsonStringEnumMemberName("sound-effect")]
    SoundEffect,

    [JsonStringEnumMemberName("image")]
    Image,

    [JsonStringEnumMemberName("video")]
    Video
}

[JsonConverter(typeof(JsonStringEnumConverter<ApiAssetState>))]
public enum ApiAssetState
{
    [JsonStringEnumMemberName("active")]
    Active,

    [JsonStringEnumMemberName("recycled")]
    Recycled
}

public enum ApiAssetSort
{
    UploadedAt,
    Name,
    Size
}

public enum ApiSortOrder
{
    Ascending,
    Descending
}
