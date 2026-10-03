using InternalAssetLibrary.Client.Core.Platform;

namespace InternalAssetLibrary.Client.Core.Playback;

public static class PlaybackFolderScanner
{
    public static IReadOnlyList<string> Discover(string folderPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        var root = Path.GetFullPath(folderPath);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(root);
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        return Directory.EnumerateFiles(root, "*", options)
            .Where(DefaultPlayerFileAssociations.SupportsPath)
            .OrderBy(path => Path.GetRelativePath(root, path), StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
