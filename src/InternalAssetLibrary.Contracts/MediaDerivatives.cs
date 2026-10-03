using System.Text.Json.Serialization;

namespace InternalAssetLibrary.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<AssetDerivativeKind>))]
public enum AssetDerivativeKind
{
    [JsonStringEnumMemberName("thumbnail")]
    Thumbnail,

    [JsonStringEnumMemberName("proxy")]
    Proxy
}

[JsonConverter(typeof(JsonStringEnumConverter<AssetDerivativeState>))]
public enum AssetDerivativeState
{
    [JsonStringEnumMemberName("queued")]
    Queued,

    [JsonStringEnumMemberName("processing")]
    Processing,

    [JsonStringEnumMemberName("ready")]
    Ready,

    [JsonStringEnumMemberName("unavailable")]
    Unavailable,

    [JsonStringEnumMemberName("failed")]
    Failed
}

public sealed record AssetDerivativeInfo(
    AssetDerivativeKind Kind,
    Guid AssetVersionId,
    AssetDerivativeState State,
    int FormatVersion,
    string? Url,
    string? ContentType,
    long SizeBytes,
    string? ETag,
    string? ErrorCode,
    string? ErrorMessage,
    int RetryCount,
    DateTimeOffset UpdatedAt);

public sealed record AssetDerivativesInfo(
    AssetDerivativeInfo Thumbnail,
    AssetDerivativeInfo Proxy);

public sealed record UpdateAssetDerivativeStatusRequest(
    Guid AssetVersionId,
    AssetDerivativeState State,
    string? ErrorCode = null,
    string? ErrorMessage = null);

public sealed record RetryAssetDerivativeRequest(
    Guid AssetVersionId,
    bool ServerBackfill = false);
