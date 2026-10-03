namespace InternalAssetLibrary.Client.Core.LocalAssets;

public enum LocalMediaType
{
    Audio,
    Image,
    Video
}

public enum IndexedFolderAvailability
{
    Online,
    PartiallyAvailable,
    Offline,
    Missing
}

public enum LocalAssetAvailability
{
    Available,
    TemporarilyUnavailable,
    OfflineStorage
}

public enum IndexedDirectoryAvailability
{
    Available,
    TemporarilyUnavailable,
    Offline
}

public sealed record IndexedFolder(
    Guid Id,
    string Path,
    bool IsExternalStorage,
    IndexedFolderAvailability Availability,
    DateTimeOffset AddedAtUtc,
    DateTimeOffset? LastScanAtUtc);

public sealed record IndexedDirectory(
    Guid FolderId,
    string RelativePath,
    string Name,
    IndexedDirectoryAvailability Availability);

public sealed record LocalAsset(
    Guid Id,
    Guid FolderId,
    string FullPath,
    string RelativePath,
    string FileName,
    string Extension,
    LocalMediaType MediaType,
    long SizeBytes,
    DateTimeOffset LastWriteTimeUtc,
    DateTimeOffset AddedAtUtc,
    LocalAssetAvailability Availability,
    string[] Tags)
{
    public bool IsAvailable => Availability == LocalAssetAvailability.Available;
}

public sealed record LocalAssetCatalog(
    int SchemaVersion,
    IndexedFolder[] Folders,
    LocalAsset[] Assets,
    IndexedDirectory[] Directories,
    string[] TagLibrary)
{
    public const int CurrentSchemaVersion = 3;

    public static LocalAssetCatalog Empty { get; } = new(
        CurrentSchemaVersion,
        [],
        [],
        [],
        []);
}

public sealed record LocalAssetIndexIssue(string Path, string Message);

public sealed record LocalAssetIndexResult(
    LocalAssetCatalog Catalog,
    IndexedFolder Folder,
    int IndexedFileCount,
    IReadOnlyList<LocalAssetIndexIssue> Issues);

public sealed record LocalStorageAvailabilityProbeResult(
    LocalAssetCatalog Catalog,
    IReadOnlyList<IndexedFolder> DisconnectedFolders,
    IReadOnlyList<IndexedFolder> ReconnectedFolders,
    IReadOnlyList<IndexedFolder> MissingFolders);

public sealed record LocalAssetRenameRequest(Guid AssetId, string NewFileName);
