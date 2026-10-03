namespace InternalAssetLibrary.Server.Api;

internal sealed record LoginRequest(string Identifier, string Password);
internal sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
internal sealed record EmailRequest(string Email, string CurrentPassword);
internal sealed record PasswordConfirmationRequest(string CurrentPassword);

internal sealed record ProfileRequest(
    string? DisplayName,
    string? Bio,
    DateOnly? Birthday,
    string? Gender,
    string? CustomGender,
    string? Contact,
    string? BirthdayVisibility,
    string? GenderVisibility,
    string? ContactVisibility);

internal sealed record CreateUserRequest(
    string Username,
    string TemporaryPassword,
    string? DisplayName,
    bool IsAdmin,
    string[]? Permissions);

internal sealed record ResetPasswordRequest(string TemporaryPassword);
internal sealed record AccountStatusRequest(bool IsEnabled);
internal sealed record AccountPermissionsRequest(bool IsAdmin, string[]? Permissions);
internal sealed record UsernameRequest(string Username);

internal sealed record ServerSettingsRequest(
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

internal sealed record CreateAssetRequest(
    string Name,
    string Category,
    string OriginalFileName,
    long SizeBytes,
    string ContentHash,
    string? Notes,
    string[]? Tags,
    double? DurationSeconds = null,
    Guid? FolderId = null);

internal sealed record UpdateAssetRequest(string Name, string? Notes);
internal sealed record UpdateAssetCategoryRequest(string Category);
internal sealed record UpdateTagsRequest(string[]? Tags, bool? CreateMissing = null);
internal sealed record TagRequest(string Name);
internal sealed record CreateAssetFolderApiRequest(string Name, Guid? ParentId = null);
internal sealed record RenameAssetFolderApiRequest(string Name);
internal sealed record MoveAssetFolderApiRequest(Guid? ParentId);
internal sealed record MoveAssetToFolderApiRequest(Guid? FolderId);

internal sealed record PageResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);
