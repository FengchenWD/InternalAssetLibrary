namespace InternalAssetLibrary.Contracts;

public sealed record ClientReleaseInfo(
    string Version,
    string MinimumCompatibleVersion,
    DateTimeOffset PublishedAt,
    string InstallerFileName,
    long InstallerSizeBytes,
    string InstallerSha256,
    string DownloadPath,
    string? ReleaseNotes)
{
    public string? Signature { get; init; }
}

public sealed record ClientUpdatePackageInfo(
    string Version,
    string InstallerPath,
    long InstallerSizeBytes,
    string InstallerSha256);

public sealed record ClientUpdateTransaction(
    int SchemaVersion,
    Guid TransactionId,
    DateTimeOffset CreatedAtUtc,
    int ClientProcessId,
    DateTimeOffset ClientProcessStartedAtUtc,
    string InstallDirectory,
    string ClientExecutablePath,
    string HealthConfirmationPath,
    ClientUpdatePackageInfo TargetPackage,
    ClientUpdatePackageInfo? RollbackPackage,
    string? ReleaseNotes)
{
    public string? RecoverySnapshotDirectory { get; init; }
}

public sealed record ClientInstalledUpdateState(
    int SchemaVersion,
    Guid TransactionId,
    DateTimeOffset InstalledAtUtc,
    ClientUpdatePackageInfo Package,
    string? ReleaseNotes,
    Guid? LastShownTransactionId);
