namespace InternalAssetLibrary.Client.Core.Downloads;

public readonly record struct DownloadAssetKey(Guid AssetId, Guid VersionId)
{
    public override string ToString() => $"{AssetId:N}/{VersionId:N}";
}

public sealed class PersistentDownloadPathMapper
{
    private static readonly HashSet<string> WindowsReservedNames = new(
        [
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        ],
        StringComparer.OrdinalIgnoreCase);

    public string GetTargetPath(
        string persistentDirectory,
        DownloadAssetKey key,
        string originalFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(persistentDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(originalFileName);
        if (key.AssetId == Guid.Empty || key.VersionId == Guid.Empty)
        {
            throw new ArgumentException("Asset and version identifiers must not be empty.", nameof(key));
        }

        var root = NormalizeRoot(persistentDirectory);
        var safeFileName = MakeSafeFileName(originalFileName);
        return EnsureWithinRoot(root, Path.Combine(
            root,
            key.AssetId.ToString("N"),
            key.VersionId.ToString("N"),
            safeFileName));
    }

    public string GetUserVisibleTargetPath(
        string targetDirectory,
        string originalFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(originalFileName);
        var root = NormalizeRoot(targetDirectory);
        return EnsureWithinRoot(root, Path.Combine(root, MakeSafeFileName(originalFileName)));
    }

    public string GetUserVisiblePartialPath(
        string targetDirectory,
        DownloadAssetKey key,
        string originalFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(originalFileName);
        if (key.AssetId == Guid.Empty || key.VersionId == Guid.Empty)
        {
            throw new ArgumentException("Asset and version identifiers must not be empty.", nameof(key));
        }

        var root = NormalizeRoot(targetDirectory);
        var partialFileName = $".ial-{key.AssetId:N}-{key.VersionId:N}.part";
        return EnsureWithinRoot(root, Path.Combine(root, partialFileName));
    }

    public string PrepareUserVisibleDirectory(
        string targetDirectory,
        string originalFileName)
    {
        var path = GetUserVisibleTargetPath(targetDirectory, originalFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    private static string NormalizeRoot(string directory) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory.Trim()));

    private static string EnsureWithinRoot(string root, string candidate)
    {
        var path = Path.GetFullPath(candidate);

        var rootPrefix = Path.EndsInDirectorySeparator(root)
            ? root
            : root + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!path.StartsWith(rootPrefix, comparison))
        {
            throw new InvalidOperationException("The resolved download path escaped the persistent directory.");
        }

        return path;
    }

    public string PrepareTargetDirectory(
        string persistentDirectory,
        DownloadAssetKey key,
        string originalFileName)
    {
        var path = GetTargetPath(persistentDirectory, key, originalFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    private static string MakeSafeFileName(string originalFileName)
    {
        var fileName = Path.GetFileName(originalFileName.Trim());
        var invalidCharacters = Path.GetInvalidFileNameChars().ToHashSet();
        var characters = fileName
            .Select(character => invalidCharacters.Contains(character) ? '_' : character)
            .ToArray();
        fileName = new string(characters).Trim().TrimEnd('.');

        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "asset";
        }

        if (WindowsReservedNames.Contains(Path.GetFileNameWithoutExtension(fileName)))
        {
            fileName = "_" + fileName;
        }

        const int maximumFileNameLength = 180;
        if (fileName.Length <= maximumFileNameLength)
        {
            return fileName;
        }

        var extension = Path.GetExtension(fileName);
        var nameLength = Math.Max(1, maximumFileNameLength - extension.Length);
        return fileName[..nameLength] + extension;
    }
}
