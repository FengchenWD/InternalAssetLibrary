namespace InternalAssetLibrary.Contracts;

public sealed record DirectTransferCapabilities(
    bool MultipartUpload,
    int PartSizeBytes,
    int SessionLifetimeHours);

public sealed record DirectUploadSession(
    Guid Id,
    Guid AssetId,
    Guid AssetVersionId,
    long SizeBytes,
    string Sha256,
    int PartSizeBytes,
    int PartCount,
    DateTimeOffset ExpiresAt);

public sealed record DirectUploadPartUrl(
    Guid SessionId,
    int PartNumber,
    string Url,
    DateTimeOffset ExpiresAt);

public sealed record DirectUploadCompletedPart(int PartNumber, string ETag);

public sealed record CompleteDirectUploadRequest(IReadOnlyList<DirectUploadCompletedPart> Parts);
