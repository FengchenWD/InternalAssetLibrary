namespace InternalAssetLibrary.Contracts;

public sealed record MarkerItem(
    Guid Id,
    TimeSpan Time,
    string? Name,
    string? Note);

public sealed record MarkerSetSummary(
    Guid Id,
    Guid AssetId,
    Guid AssetVersionId,
    string Name,
    Guid OwnerUserId,
    string OwnerDisplayName,
    int MarkerCount,
    bool IsBasedOnOldVersion,
    DateTimeOffset UpdatedAt);

public sealed record MarkerSetDetail(
    Guid Id,
    Guid AssetId,
    Guid AssetVersionId,
    string Name,
    Guid OwnerUserId,
    string OwnerDisplayName,
    bool IsBasedOnOldVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<MarkerItem> Markers);

public sealed record CreateMarkerSetRequest(
    Guid AssetId,
    Guid AssetVersionId,
    string Name);

public sealed record RenameMarkerSetRequest(string Name);

public sealed record CopyMarkerSetRequest(
    Guid SourceMarkerSetId,
    string Name);

public sealed record ImportMarkerSetRequest(
    Guid AssetId,
    Guid AssetVersionId,
    string Name);

public sealed record UpsertMarkerRequest(
    TimeSpan Time,
    string? Name,
    string? Note);

public sealed record MarkerCsvEntry(
    TimeSpan Time,
    string? Name = null,
    string? Note = null);

public sealed record MarkerCsvDocument(
    string? RecordingName,
    string? RecordingPath,
    DateTime? RecordingStartedAt,
    TimeSpan? RecordingDuration,
    IReadOnlyList<MarkerCsvEntry> Markers);

public sealed record MarkerCsvRecordingInfo(
    string? RecordingName,
    string? RecordingPath,
    DateTime? RecordingStartedAt,
    TimeSpan? RecordingDuration);

public sealed record MarkerSetAccess(
    bool CanView,
    bool CanEdit,
    bool CanDelete,
    bool CanCopy);
