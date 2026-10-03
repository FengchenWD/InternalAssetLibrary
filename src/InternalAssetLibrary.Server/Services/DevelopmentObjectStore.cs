using System.Buffers;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace InternalAssetLibrary.Server.Services;

internal sealed record ObjectWriteResult(string ObjectKey, long SizeBytes, string Sha256);
internal sealed record ObjectDownloadUrl(Uri Url, DateTimeOffset ExpiresAt);
internal sealed record MultipartUploadHandle(string ProviderUploadId);
internal sealed record MultipartUploadPart(int PartNumber, string ETag);

internal interface IObjectStore
{
    string StorageKind { get; }

    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

    Task<ObjectWriteResult> PutAsync(
        string objectKey,
        Stream source,
        long maximumBytes,
        CancellationToken cancellationToken = default);

    ValueTask<Stream?> OpenReadAsync(
        string objectKey,
        CancellationToken cancellationToken = default);

    ValueTask<ObjectDownloadUrl?> CreateDownloadUrlAsync(
        string objectKey,
        string? downloadFileName = null,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(string objectKey, CancellationToken cancellationToken = default);

    bool SupportsDirectMultipartUpload => false;

    Task<MultipartUploadHandle> StartMultipartUploadAsync(
        string objectKey,
        long sizeBytes,
        string sha256,
        string contentType,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This object store does not support direct multipart uploads.");

    ValueTask<ObjectDownloadUrl> CreateUploadPartUrlAsync(
        string objectKey,
        string providerUploadId,
        int partNumber,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This object store does not support direct multipart uploads.");

    Task<ObjectWriteResult> CompleteMultipartUploadAsync(
        string objectKey,
        string providerUploadId,
        IReadOnlyList<MultipartUploadPart> parts,
        long expectedSizeBytes,
        string expectedSha256,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This object store does not support direct multipart uploads.");

    Task AbortMultipartUploadAsync(
        string objectKey,
        string providerUploadId,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
}

internal sealed partial class DevelopmentObjectStore : IObjectStore
{
    private const int BufferSize = 128 * 1024;
    private const int MaximumKeyLength = 1_024;
    private const int MaximumSegmentLength = 128;
    private static readonly HashSet<string> WindowsDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };
    private readonly string _rootPath;
    private readonly string _rootPathWithSeparator;
    private readonly StringComparison _pathComparison;
    private readonly ILogger<DevelopmentObjectStore> _logger;

    public string StorageKind => "development-filesystem";

    public DevelopmentObjectStore(
        IHostEnvironment environment,
        IConfiguration configuration,
        ILogger<DevelopmentObjectStore> logger)
    {
        var allowInProduction = configuration.GetValue("DevelopmentStorage:AllowInProduction", false);
        if (!environment.IsDevelopment() && !allowInProduction)
        {
            throw new InvalidOperationException(
                "The filesystem object store is a development implementation. Configure Tencent COS before production, " +
                "or explicitly set DevelopmentStorage:AllowInProduction=true for a temporary internal deployment.");
        }

        var configuredPath = configuration["DevelopmentStorage:ObjectPath"] ?? "App_Data/objects";
        _rootPath = Path.GetFullPath(configuredPath, environment.ContentRootPath);
        _rootPathWithSeparator = Path.EndsInDirectorySeparator(_rootPath)
            ? _rootPath
            : _rootPath + Path.DirectorySeparatorChar;
        _pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        _logger = logger;

        Directory.CreateDirectory(_rootPath);
    }

    public async Task<ObjectWriteResult> PutAsync(
        string objectKey,
        Stream source,
        long maximumBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
        {
            throw new ArgumentException("The source stream must be readable.", nameof(source));
        }

        if (maximumBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes), "The maximum size cannot be negative.");
        }

        var destinationPath = ResolveObjectPath(objectKey);
        var destinationDirectory = Path.GetDirectoryName(destinationPath)!;
        Directory.CreateDirectory(destinationDirectory);
        EnsurePathHasNoReparsePoints(destinationDirectory);

        if (File.Exists(destinationPath))
        {
            throw new IOException($"Object '{objectKey}' already exists.");
        }

        var temporaryPath = Path.Combine(
            destinationDirectory,
            $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        long bytesWritten = 0;

        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var destination = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                while (true)
                {
                    var bytesRead = await source.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken);
                    if (bytesRead == 0)
                    {
                        break;
                    }

                    if (bytesRead > maximumBytes - bytesWritten)
                    {
                        throw new InvalidDataException($"Object '{objectKey}' exceeds the {maximumBytes} byte limit.");
                    }

                    await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                    hash.AppendData(buffer, 0, bytesRead);
                    bytesWritten += bytesRead;
                }

                await destination.FlushAsync(cancellationToken);
                destination.Flush(flushToDisk: true);
            }

            // File.Move without overwrite is an atomic same-volume publish and closes the race
            // between the earlier existence check and another writer publishing the same key.
            File.Move(temporaryPath, destinationPath);
            return new ObjectWriteResult(objectKey, bytesWritten, Convert.ToHexString(hash.GetHashAndReset()));
        }
        catch
        {
            TryDeleteTemporaryFile(temporaryPath);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Directory.Exists(_rootPath));
    }

    public ValueTask<Stream?> OpenReadAsync(
        string objectKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolveObjectPath(objectKey);
        EnsurePathHasNoReparsePoints(Path.GetDirectoryName(path)!);

        try
        {
            FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.RandomAccess);
            return ValueTask.FromResult<Stream?>(stream);
        }
        catch (FileNotFoundException)
        {
            return ValueTask.FromResult<Stream?>(null);
        }
        catch (DirectoryNotFoundException)
        {
            return ValueTask.FromResult<Stream?>(null);
        }
    }

    public Task DeleteAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolveObjectPath(objectKey);
        EnsurePathHasNoReparsePoints(Path.GetDirectoryName(path)!);
        File.Delete(path);
        return Task.CompletedTask;
    }

    public ValueTask<ObjectDownloadUrl?> CreateDownloadUrlAsync(
        string objectKey,
        string? downloadFileName = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<ObjectDownloadUrl?>(null);
    }

    private string ResolveObjectPath(string objectKey)
    {
        if (string.IsNullOrWhiteSpace(objectKey) || objectKey.Length > MaximumKeyLength ||
            objectKey[0] == '/' || objectKey[^1] == '/' || objectKey.Contains('\\'))
        {
            throw new ArgumentException("The object key is not a safe relative path.", nameof(objectKey));
        }

        var segments = objectKey.Split('/');
        if (segments.Length == 0 || segments.Any(segment => !IsSafeSegment(segment)))
        {
            throw new ArgumentException("The object key contains an unsafe path segment.", nameof(objectKey));
        }

        var path = Path.GetFullPath(Path.Combine([_rootPath, .. segments]));
        if (!path.StartsWith(_rootPathWithSeparator, _pathComparison))
        {
            throw new ArgumentException("The object key resolves outside the object store.", nameof(objectKey));
        }

        return path;
    }

    private static bool IsSafeSegment(string segment)
    {
        if (segment.Length is 0 or > MaximumSegmentLength ||
            !SafeSegmentPattern().IsMatch(segment) || segment.EndsWith('.'))
        {
            return false;
        }

        var firstDot = segment.IndexOf('.');
        var deviceNameCandidate = firstDot < 0 ? segment : segment[..firstDot];
        return !WindowsDeviceNames.Contains(deviceNameCandidate);
    }

    private void EnsurePathHasNoReparsePoints(string directoryPath)
    {
        var relativePath = Path.GetRelativePath(_rootPath, directoryPath);
        if (relativePath == ".")
        {
            return;
        }

        var current = _rootPath;
        foreach (var segment in relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            var directory = new DirectoryInfo(current);
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Object store paths cannot traverse symbolic links or reparse points.");
            }
        }
    }

    private void TryDeleteTemporaryFile(string temporaryPath)
    {
        try
        {
            File.Delete(temporaryPath);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not remove temporary object file {Path}.", temporaryPath);
        }
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeSegmentPattern();
}
