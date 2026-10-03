using InternalAssetLibrary.Client.Core.Auth;

namespace InternalAssetLibrary.Client.Core.Platform;

public static class ClientApplicationDataPurger
{
    public static void Purge(string rootDirectory, IReadOnlyCollection<string> managedPaths) =>
        Purge(rootDirectory, managedPaths, ApplicationCredentialPurger.Purge);

    internal static void Purge(
        string rootDirectory,
        IReadOnlyCollection<string> managedPaths,
        Action purgeCredentials)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentNullException.ThrowIfNull(managedPaths);
        ArgumentNullException.ThrowIfNull(purgeCredentials);

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        var volumeRoot = Path.TrimEndingDirectorySeparator(Path.GetPathRoot(root) ?? string.Empty);
        if (root.Length == 0 || string.Equals(root, volumeRoot, PathComparison))
        {
            throw new ArgumentException("The application data root cannot be a volume root.", nameof(rootDirectory));
        }

        EnsureRootIsNotAReparsePoint(root);

        var rootPrefix = root + Path.DirectorySeparatorChar;
        var targets = managedPaths
            .Select(Path.GetFullPath)
            .Distinct(PathComparer)
            .ToArray();
        if (targets.Any(path =>
                string.Equals(path, root, PathComparison) ||
                !path.StartsWith(rootPrefix, PathComparison)))
        {
            throw new ArgumentException(
                "Every managed application data path must be a child of the application data root.",
                nameof(managedPaths));
        }

        var failures = new List<Exception>();
        foreach (var target in targets)
        {
            try
            {
                DeleteEntryWithoutFollowingLinks(target);
            }
            catch (Exception exception)
            {
                failures.Add(new IOException($"Could not delete managed application data '{target}'.", exception));
            }
        }

        try
        {
            purgeCredentials();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        try
        {
            if (Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any())
            {
                Directory.Delete(root, false);
            }
        }
        catch (Exception exception)
        {
            failures.Add(new IOException("Could not remove the empty application data root.", exception));
        }

        if (failures.Count > 0)
        {
            throw new AggregateException("Application data cleanup did not complete.", failures);
        }
    }

    private static void DeleteEntryWithoutFollowingLinks(string path)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return;
        }

        var isDirectory = attributes.HasFlag(FileAttributes.Directory);
        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            if (isDirectory)
            {
                Directory.Delete(path, false);
            }
            else
            {
                File.Delete(path);
            }

            return;
        }

        if (!isDirectory)
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
            return;
        }

        foreach (var child in Directory.EnumerateFileSystemEntries(path))
        {
            DeleteEntryWithoutFollowingLinks(child);
        }

        File.SetAttributes(path, FileAttributes.Normal);
        Directory.Delete(path, false);
    }

    private static void EnsureRootIsNotAReparsePoint(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        if (File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException("The application data root cannot be a symbolic link or reparse point.");
        }
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
