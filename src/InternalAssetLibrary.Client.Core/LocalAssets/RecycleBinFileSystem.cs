using Microsoft.VisualBasic.FileIO;

namespace InternalAssetLibrary.Client.Core.LocalAssets;

public interface IRecycleBinFileSystem
{
    Task RecycleFileAsync(string path, CancellationToken cancellationToken = default);

    Task RecycleDirectoryAsync(string path, CancellationToken cancellationToken = default);
}

public sealed class WindowsRecycleBinFileSystem : IRecycleBinFileSystem
{
    public Task RecycleFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizeExistingFile(path);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(
            () => FileSystem.DeleteFile(
                normalizedPath,
                UIOption.OnlyErrorDialogs,
                RecycleOption.SendToRecycleBin,
                UICancelOption.ThrowException),
            CancellationToken.None);
    }

    public Task RecycleDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizeExistingDirectory(path);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(
            () => FileSystem.DeleteDirectory(
                normalizedPath,
                UIOption.OnlyErrorDialogs,
                RecycleOption.SendToRecycleBin,
                UICancelOption.ThrowException),
            CancellationToken.None);
    }

    private static string NormalizeExistingFile(string path)
    {
        EnsureWindows();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalizedPath = Path.GetFullPath(path);
        return File.Exists(normalizedPath)
            ? normalizedPath
            : throw new FileNotFoundException("要删除的文件不存在。", normalizedPath);
    }

    private static string NormalizeExistingDirectory(string path)
    {
        EnsureWindows();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return Directory.Exists(normalizedPath)
            ? normalizedPath
            : throw new DirectoryNotFoundException($"要删除的文件夹不存在：{normalizedPath}");
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("当前版本仅支持 Windows 回收站。");
        }
    }
}
