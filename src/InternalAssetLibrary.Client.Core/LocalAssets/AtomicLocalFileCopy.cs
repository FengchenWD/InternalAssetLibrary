namespace InternalAssetLibrary.Client.Core.LocalAssets;

public static class AtomicLocalFileCopy
{
    private const int BufferSize = 1024 * 1024;

    public static async Task<string> CopyToUniqueFileAsync(
        string sourcePath,
        string targetDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);
        var sourceFullPath = Path.GetFullPath(sourcePath);
        var targetFullDirectory = Path.GetFullPath(targetDirectory);
        if (!File.Exists(sourceFullPath))
        {
            throw new FileNotFoundException("要另存为的文件不存在。", sourceFullPath);
        }

        if (!Directory.Exists(targetFullDirectory))
        {
            throw new DirectoryNotFoundException($"另存为目录不存在：{targetFullDirectory}");
        }

        var temporaryPath = Path.Combine(targetFullDirectory, $".ial-copy-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var source = new FileStream(
                             sourceFullPath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             BufferSize,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var destination = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             BufferSize,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var expectedLength = source.Length;
                await source.CopyToAsync(destination, BufferSize, cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (source.Length != expectedLength || destination.Length != expectedLength)
                {
                    throw new IOException("另存为期间源文件发生变化，未生成目标文件。");
                }
            }

            return MoveToUniqueTarget(temporaryPath, sourceFullPath, targetFullDirectory);
        }
        catch (Exception originalException)
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception cleanupException)
            {
                throw new AggregateException(
                    $"另存为失败，并且未能清理临时文件：{temporaryPath}",
                    originalException,
                    cleanupException);
            }

            throw;
        }
    }

    private static string MoveToUniqueTarget(
        string temporaryPath,
        string sourcePath,
        string targetDirectory)
    {
        var stem = Path.GetFileNameWithoutExtension(sourcePath);
        var extension = Path.GetExtension(sourcePath);
        for (var index = 1; index <= 10_000; index++)
        {
            var fileName = index == 1
                ? $"{stem}{extension}"
                : $"{stem} ({index}){extension}";
            var targetPath = Path.Combine(targetDirectory, fileName);
            try
            {
                File.Move(temporaryPath, targetPath, overwrite: false);
                return targetPath;
            }
            catch (IOException) when (File.Exists(targetPath) || Directory.Exists(targetPath))
            {
                // A concurrent writer won this name; try the next candidate without overwriting it.
            }
        }

        throw new IOException("目标目录中同名文件过多，无法生成安全的另存为名称。");
    }
}
