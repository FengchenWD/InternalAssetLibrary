namespace InternalAssetLibrary.Contracts;

public enum AssetCategory
{
    Bgm,
    SoundEffect,
    Image,
    Video
}

public enum AssetStatus
{
    Processing,
    Active,
    PreviewUnavailable,
    RecycleBin,
    Failed
}

public enum AssetSortField
{
    Name,
    UploadedAt,
    FileSize
}

public sealed record AssetQuery(
    int Page = 1,
    int PageSize = 50,
    string? Search = null,
    AssetCategory? Category = null,
    string? FileExtension = null,
    IReadOnlyList<Guid>? TagIds = null,
    DateTimeOffset? UploadedFrom = null,
    DateTimeOffset? UploadedTo = null,
    AssetSortField SortBy = AssetSortField.Name,
    SortDirection SortDirection = SortDirection.Ascending,
    Guid? UploadedByUserId = null,
    Guid? FolderId = null,
    bool IncludeDescendantFolders = true,
    bool RootOnly = false);

public sealed record RecycleBinClearResult(int DeletedCount);

public sealed record AssetSummary(
    Guid Id,
    Guid CurrentVersionId,
    string Name,
    AssetCategory Category,
    string FileExtension,
    long FileSize,
    AssetStatus Status,
    string? ThumbnailUrl,
    string? PreviewUrl,
    DateTimeOffset UploadedAt,
    UserSummary UploadedBy,
    IReadOnlyList<TagSummary> Tags,
    AssetDerivativesInfo? Derivatives = null,
    Guid? FolderId = null);

public sealed record AssetDetail(
    Guid Id,
    Guid CurrentVersionId,
    string Name,
    string? Note,
    AssetCategory Category,
    string OriginalFileName,
    string FileExtension,
    string ContentType,
    long FileSize,
    string Sha256,
    AssetStatus Status,
    string? ThumbnailUrl,
    string? PreviewUrl,
    DateTimeOffset UploadedAt,
    DateTimeOffset UpdatedAt,
    UserSummary UploadedBy,
    IReadOnlyList<TagSummary> Tags,
    AssetDerivativesInfo? Derivatives = null,
    Guid? FolderId = null);

public sealed record BeginUploadRequest(
    string Name,
    string OriginalFileName,
    AssetCategory Category,
    long FileSize,
    string Sha256,
    string? Note,
    IReadOnlyList<Guid> TagIds,
    Guid? FolderId = null);

public sealed record UpdateAssetRequest(
    string Name,
    string? Note,
    IReadOnlyList<Guid> TagIds);

public sealed record ReplaceAssetSourceRequest(
    string OriginalFileName,
    long FileSize,
    string Sha256);

public sealed record AssetFolderSummary(
    Guid Id,
    Guid? ParentId,
    string Name,
    UserSummary CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CreateAssetFolderRequest(string Name, Guid? ParentId = null);

public sealed record RenameAssetFolderRequest(string Name);

public sealed record MoveAssetFolderRequest(Guid? ParentId);

public sealed record MoveAssetToFolderRequest(Guid? FolderId);

public sealed record AssetUploadLimits(
    long ImageBytes = 5L * 1024 * 1024 * 1024,
    long AudioBytes = 5L * 1024 * 1024 * 1024,
    long VideoBytes = 15L * 1024 * 1024 * 1024)
{
    public long For(AssetCategory category) => category switch
    {
        AssetCategory.Image => ImageBytes,
        AssetCategory.Bgm or AssetCategory.SoundEffect => AudioBytes,
        AssetCategory.Video => VideoBytes,
        _ => throw new ArgumentOutOfRangeException(nameof(category))
    };
}
