using InternalAssetLibrary.Contracts;

namespace InternalAssetLibrary.Client.Core.Markers;

public sealed record LocalMarkerSetSummary(
    Guid Id,
    Guid LocalAssetId,
    string Name,
    int MarkerCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record LocalMarkerSetDetail(
    Guid Id,
    Guid LocalAssetId,
    string Name,
    TimeSpan? AssetDuration,
    MarkerCsvRecordingInfo RecordingInfo,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<MarkerItem> Markers);
