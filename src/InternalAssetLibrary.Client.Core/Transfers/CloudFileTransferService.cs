using System.Security.Cryptography;
using InternalAssetLibrary.Client.Core.Downloads;
using InternalAssetLibrary.Client.Core.Http;
using InternalAssetLibrary.Contracts;

namespace InternalAssetLibrary.Client.Core.Transfers;

public enum CloudTransferStage
{
    Hashing,
    Uploading,
    Downloading,
    Completed
}

public sealed record CloudTransferProgress(
    CloudTransferStage Stage,
    long BytesProcessed,
    long TotalBytes);

public sealed class CloudFileTransferService
{
    private const int BufferSize = 128 * 1024;
    private static readonly HttpClient DefaultDirectTransferHttpClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = System.Net.DecompressionMethods.None
    })
    {
        Timeout = TimeSpan.FromMinutes(30)
    };

    private readonly AssetLibraryApiClient _apiClient;
    private readonly PersistentDownloadPathMapper _pathMapper;
    private readonly IPersistentDownloadRegistry _downloadRegistry;
    private readonly IDirectUploadResumeStore? _directUploadResumeStore;
    private readonly HttpClient _directTransferHttpClient;
    private readonly Func<string, CancellationToken, Task<double?>>? _durationProbe;

    public CloudFileTransferService(
        AssetLibraryApiClient apiClient,
        PersistentDownloadPathMapper pathMapper,
        IPersistentDownloadRegistry downloadRegistry,
        IDirectUploadResumeStore? directUploadResumeStore = null,
        HttpClient? directTransferHttpClient = null,
        Func<string, CancellationToken, Task<double?>>? durationProbe = null)
    {
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _pathMapper = pathMapper ?? throw new ArgumentNullException(nameof(pathMapper));
        _downloadRegistry = downloadRegistry ?? throw new ArgumentNullException(nameof(downloadRegistry));
        _directUploadResumeStore = directUploadResumeStore;
        _directTransferHttpClient = directTransferHttpClient ?? DefaultDirectTransferHttpClient;
        _durationProbe = durationProbe;
    }

    public async Task<ApiAsset> UploadAsync(
        string localPath,
        ApiAssetCategory category,
        string? notes = null,
        IReadOnlyList<string>? tags = null,
        IProgress<CloudTransferProgress>? progress = null,
        CancellationToken cancellationToken = default,
        Guid? folderId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        if (!Enum.IsDefined(category))
        {
            throw new ArgumentOutOfRangeException(nameof(category));
        }

        var fullPath = Path.GetFullPath(localPath);
        var originalFileName = Path.GetFileName(fullPath);
        var uploadTags = tags?.ToArray() ?? [];
        var name = Path.GetFileNameWithoutExtension(originalFileName);
        if (string.IsNullOrWhiteSpace(name))
        {
            name = originalFileName;
        }

        var durationSeconds = await TryProbeDurationAsync(fullPath, category, cancellationToken)
            .ConfigureAwait(false);

        await using var source = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var sizeBytes = source.Length;
        var sha256 = await ComputeSha256Async(source, sizeBytes, progress, cancellationToken)
            .ConfigureAwait(false);
        source.Position = 0;

        DirectTransferCapabilities? capabilities = null;
        if (_directUploadResumeStore is not null)
        {
            try
            {
                capabilities = await _apiClient.GetDirectTransferCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (AssetLibraryApiException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // Compatibility with pre-.6 development servers.
            }
        }

        if (capabilities?.MultipartUpload == true)
        {
            return await UploadDirectAsync(
                fullPath,
                name,
                category,
                originalFileName,
                sizeBytes,
                sha256,
                notes,
                uploadTags,
                durationSeconds,
                progress,
                cancellationToken,
                folderId).ConfigureAwait(false);
        }

        var created = await CreateAssetAsync(
            name,
            category,
            originalFileName,
            sizeBytes,
            sha256,
            notes,
            uploadTags,
            durationSeconds,
            cancellationToken,
            folderId).ConfigureAwait(false);

        try
        {
            progress?.Report(new CloudTransferProgress(CloudTransferStage.Uploading, 0, sizeBytes));
            using var upload = new ProgressReadStream(source, bytesProcessed =>
                progress?.Report(new CloudTransferProgress(
                    CloudTransferStage.Uploading,
                    bytesProcessed,
                    sizeBytes)));
            var completed = await _apiClient.UploadAssetContentAsync(
                created.Id,
                upload,
                sizeBytes,
                cancellationToken).ConfigureAwait(false);
            progress?.Report(new CloudTransferProgress(
                CloudTransferStage.Completed,
                sizeBytes,
                sizeBytes));
            return completed;
        }
        catch
        {
            await TryDeleteCreatedAssetAsync(created.Id).ConfigureAwait(false);
            throw;
        }
    }

    public async Task CancelPendingUploadAsync(string path, CancellationToken token = default)
    {
        if (_directUploadResumeStore is null) return;
        var resume = await _directUploadResumeStore.FindByPathAsync(_apiClient.BaseAddress, path, token).ConfigureAwait(false);
        if (resume is null) return;
        ApiAsset? asset;
        try { asset = await _apiClient.GetAssetAsync(resume.AssetId, token).ConfigureAwait(false); }
        catch (AssetLibraryApiException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound) { asset = null; }
        if (asset?.HasOriginal != true)
        {
            try { await _apiClient.AbortDirectUploadAsync(resume.SessionId, token).ConfigureAwait(false); }
            catch (AssetLibraryApiException exception) when (IsStaleDirectUpload(exception.StatusCode)) { }
            if (asset is not null)
            {
                _ = await _apiClient.RecycleAssetAsync(asset.Id, token).ConfigureAwait(false);
                await _apiClient.PermanentlyDeleteAssetAsync(asset.Id, token).ConfigureAwait(false);
            }
        }
        await _directUploadResumeStore.DeleteAsync(resume.SessionId, token).ConfigureAwait(false);
    }

    public void CancelPendingDownload(ApiAsset asset, string directory) =>
        File.Delete(_pathMapper.GetUserVisiblePartialPath(directory, new DownloadAssetKey(asset.Id, asset.CurrentVersionId), asset.OriginalFileName));

    private async Task<ApiAsset> UploadDirectAsync(
        string fullPath,
        string name,
        ApiAssetCategory category,
        string originalFileName,
        long sizeBytes,
        string sha256,
        string? notes,
        IReadOnlyList<string> tags,
        double? durationSeconds,
        IProgress<CloudTransferProgress>? progress,
        CancellationToken cancellationToken,
        Guid? folderId)
    {
        var resume = await _directUploadResumeStore!.FindAsync(
            _apiClient.BaseAddress,
            fullPath,
            sizeBytes,
            sha256,
            cancellationToken).ConfigureAwait(false);
        ApiAsset asset;
        DirectUploadSession session;
        if (resume is not null)
        {
            try
            {
                asset = await _apiClient.GetAssetAsync(resume.AssetId, cancellationToken).ConfigureAwait(false);
                if (asset.HasOriginal && asset.SizeBytes == sizeBytes &&
                    asset.ContentHash.Equals(sha256, StringComparison.OrdinalIgnoreCase))
                {
                    await TryDeleteResumeAsync(resume.SessionId).ConfigureAwait(false);
                    progress?.Report(new CloudTransferProgress(
                        CloudTransferStage.Completed,
                        sizeBytes,
                        sizeBytes));
                    return asset;
                }

                session = await _apiClient.GetDirectUploadSessionAsync(resume.SessionId, cancellationToken).ConfigureAwait(false);
                ValidateDirectSession(session, resume.SessionId, asset, sizeBytes, sha256);
                if (resume.CompletedParts.Keys.Any(partNumber =>
                        partNumber <= 0 || partNumber > session.PartCount))
                {
                    throw new InvalidDataException("Saved direct upload state does not match the current asset.");
                }
            }
            catch (InvalidDataException)
            {
                await CancelPendingUploadAsync(fullPath, cancellationToken).ConfigureAwait(false);
                await _directUploadResumeStore.DeleteAsync(resume.SessionId, CancellationToken.None).ConfigureAwait(false);
                resume = null;
                asset = null!;
                session = null!;
            }
            catch (AssetLibraryApiException exception) when (IsStaleDirectUpload(exception.StatusCode))
            {
                await CancelPendingUploadAsync(fullPath, cancellationToken).ConfigureAwait(false);
                await _directUploadResumeStore.DeleteAsync(resume.SessionId, CancellationToken.None).ConfigureAwait(false);
                resume = null;
                asset = null!;
                session = null!;
            }
        }
        else
        {
            asset = null!;
            session = null!;
        }

        if (resume is null)
        {
            asset = await CreateAssetAsync(
                name,
                category,
                originalFileName,
                sizeBytes,
                sha256,
                notes,
                tags,
                durationSeconds,
                cancellationToken,
                folderId).ConfigureAwait(false);
            try
            {
                session = await _apiClient.StartAssetDirectUploadAsync(asset.Id, cancellationToken).ConfigureAwait(false);
                ValidateDirectSession(session, session.Id, asset, sizeBytes, sha256);
            }
            catch
            {
                await TryDeleteCreatedAssetAsync(asset.Id).ConfigureAwait(false);
                throw;
            }

            resume = new DirectUploadResumeRecord(
                NormalizeOrigin(_apiClient.BaseAddress),
                fullPath,
                sizeBytes,
                sha256,
                asset.Id,
                session.Id,
                new Dictionary<int, string>(),
                DateTimeOffset.UtcNow);
            await _directUploadResumeStore.UpsertAsync(resume, cancellationToken).ConfigureAwait(false);
        }

        var completed = new Dictionary<int, string>(resume.CompletedParts);
        var completedBytes = completed.Keys.Sum(partNumber => PartLength(session, partNumber));
        progress?.Report(new CloudTransferProgress(CloudTransferStage.Uploading, completedBytes, sizeBytes));

        for (var partNumber = 1; partNumber <= session.PartCount; partNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (completed.ContainsKey(partNumber))
            {
                continue;
            }

            var beforePart = completedBytes;
            var etag = await UploadPartWithRetryAsync(
                fullPath,
                session,
                partNumber,
                bytes => progress?.Report(new CloudTransferProgress(
                    CloudTransferStage.Uploading,
                    beforePart + bytes,
                    sizeBytes)),
                cancellationToken).ConfigureAwait(false);
            completed[partNumber] = etag;
            completedBytes += PartLength(session, partNumber);
            resume = resume with
            {
                CompletedParts = new Dictionary<int, string>(completed),
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await _directUploadResumeStore.UpsertAsync(resume, cancellationToken).ConfigureAwait(false);
        }

        var result = await _apiClient.CompleteDirectUploadAsync(
            session.Id,
            new CompleteDirectUploadRequest(completed
                .OrderBy(part => part.Key)
                .Select(part => new DirectUploadCompletedPart(part.Key, part.Value))
                .ToArray()),
            cancellationToken).ConfigureAwait(false);
        await TryDeleteResumeAsync(session.Id).ConfigureAwait(false);
        progress?.Report(new CloudTransferProgress(CloudTransferStage.Completed, sizeBytes, sizeBytes));
        return result;
    }

    private async Task<string> UploadPartWithRetryAsync(
        string fullPath,
        DirectUploadSession session,
        int partNumber,
        Action<long> report,
        CancellationToken cancellationToken)
    {
        Exception? lastFailure = null;
        long reportedBytes = 0;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var signed = await _apiClient.GetDirectUploadPartUrlAsync(
                    session.Id,
                    partNumber,
                    cancellationToken).ConfigureAwait(false);
                if (!Uri.TryCreate(signed.Url, UriKind.Absolute, out var uploadUri) ||
                    uploadUri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uploadUri.UserInfo))
                {
                    throw new InvalidDataException("The server returned an invalid direct upload URL.");
                }

                var offset = checked((long)(partNumber - 1) * session.PartSizeBytes);
                var partLength = PartLength(session, partNumber);
                await using var source = new FileStream(
                    fullPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    BufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                source.Position = offset;
                using var bounded = new BoundedReadStream(source, partLength);
                using var reporting = new ProgressReadStream(bounded, bytes =>
                {
                    if (bytes <= reportedBytes)
                    {
                        return;
                    }

                    reportedBytes = bytes;
                    report(bytes);
                });
                using var content = new StreamContent(reporting, BufferSize);
                content.Headers.ContentLength = partLength;
                using var request = new HttpRequestMessage(HttpMethod.Put, uploadUri) { Content = content };
                using var response = await _directTransferHttpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException(
                        $"Direct upload part {partNumber} failed with HTTP {(int)response.StatusCode}.",
                        null,
                        response.StatusCode);
                }

                var etag = response.Headers.ETag?.Tag?.Trim('"');
                if (string.IsNullOrWhiteSpace(etag) && response.Headers.TryGetValues("ETag", out var values))
                {
                    etag = values.FirstOrDefault()?.Trim().Trim('"');
                }
                if (string.IsNullOrWhiteSpace(etag) || etag.Length > 256 || etag.Any(char.IsControl))
                {
                    throw new InvalidDataException("Object storage did not return a valid part ETag.");
                }

                return etag;
            }
            catch (Exception exception) when (
                attempt < 3 && (exception is HttpRequestException || exception is IOException))
            {
                lastFailure = exception;
                await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken).ConfigureAwait(false);
            }
        }

        throw lastFailure ?? new IOException($"Direct upload part {partNumber} failed.");
    }

    private Task<ApiAsset> CreateAssetAsync(
        string name,
        ApiAssetCategory category,
        string originalFileName,
        long sizeBytes,
        string sha256,
        string? notes,
        IReadOnlyList<string> tags,
        double? durationSeconds,
        CancellationToken cancellationToken,
        Guid? folderId) =>
        _apiClient.CreateAssetAsync(
            new ApiCreateAssetRequest(
                name,
                category,
                originalFileName,
                sizeBytes,
                sha256,
                notes,
                tags,
                durationSeconds,
                folderId),
            cancellationToken);

    private async Task<double?> TryProbeDurationAsync(
        string fullPath,
        ApiAssetCategory category,
        CancellationToken cancellationToken)
    {
        if (_durationProbe is null || category is not (
                ApiAssetCategory.Bgm or
                ApiAssetCategory.SoundEffect or
                ApiAssetCategory.Video))
        {
            return null;
        }

        try
        {
            var result = await _durationProbe(fullPath, cancellationToken).ConfigureAwait(false);
            return result is { } value &&
                   double.IsFinite(value) &&
                   value >= 0 &&
                   value <= TimeSpan.MaxValue.TotalSeconds
                ? value
                : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static long PartLength(DirectUploadSession session, int partNumber)
    {
        var offset = checked((long)(partNumber - 1) * session.PartSizeBytes);
        return Math.Min(session.PartSizeBytes, session.SizeBytes - offset);
    }

    private static void ValidateDirectSession(
        DirectUploadSession session,
        Guid expectedSessionId,
        ApiAsset asset,
        long expectedSizeBytes,
        string expectedSha256)
    {
        var expectedPartCount = session.PartSizeBytes > 0
            ? (expectedSizeBytes + session.PartSizeBytes - 1) / session.PartSizeBytes
            : 0;
        if (session.Id != expectedSessionId || session.AssetId != asset.Id ||
            session.AssetVersionId != asset.CurrentVersionId || session.SizeBytes != expectedSizeBytes ||
            !session.Sha256.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase) ||
            expectedPartCount is <= 0 or > int.MaxValue || session.PartCount != expectedPartCount)
        {
            throw new InvalidDataException("The direct upload session does not match the asset metadata.");
        }
    }

    private static string NormalizeOrigin(Uri uri) => uri.GetLeftPart(UriPartial.Authority).TrimEnd('/') + "/";

    private static bool IsStaleDirectUpload(System.Net.HttpStatusCode? statusCode) => statusCode is
        System.Net.HttpStatusCode.BadRequest or
        System.Net.HttpStatusCode.Forbidden or
        System.Net.HttpStatusCode.NotFound or
        System.Net.HttpStatusCode.Conflict or
        System.Net.HttpStatusCode.Gone;

    private async Task TryDeleteResumeAsync(Guid sessionId)
    {
        try
        {
            await _directUploadResumeStore!.DeleteAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The completed server asset is authoritative; a stale local resume record is harmless
            // and will be reconciled on the next upload attempt.
        }
    }

    public async Task<PersistentDownloadMapping> DownloadAsync(
        ApiAsset asset,
        string persistentDirectory,
        IProgress<CloudTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateDownload(asset, persistentDirectory);
        var key = new DownloadAssetKey(asset.Id, asset.CurrentVersionId);
        var targetPath = _pathMapper.PrepareTargetDirectory(
            persistentDirectory,
            key,
            asset.OriginalFileName);
        var partPath = targetPath + ".part";

        var verified = await DownloadVerifiedPartialAsync(
            asset,
            partPath,
            progress,
            cancellationToken).ConfigureAwait(false);
        File.Move(partPath, targetPath, true);
        return await RegisterCompletedDownloadAsync(
            asset,
            key,
            targetPath,
            verified,
            progress).ConfigureAwait(false);
    }

    public async Task<PersistentDownloadMapping> DownloadToUserDirectoryAsync(
        ApiAsset asset,
        string targetDirectory,
        IProgress<CloudTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateDownload(asset, targetDirectory);
        var key = new DownloadAssetKey(asset.Id, asset.CurrentVersionId);
        var preferredTargetPath = _pathMapper.PrepareUserVisibleDirectory(
            targetDirectory,
            asset.OriginalFileName);
        var partPath = _pathMapper.GetUserVisiblePartialPath(
            targetDirectory,
            key,
            asset.OriginalFileName);

        var existing = await _downloadRegistry.FindAsync(key, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            var existingDirectory = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(Path.GetDirectoryName(existing.FullPath)!));
            var requestedDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDirectory));
            var pathComparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (existingDirectory.Equals(requestedDirectory, pathComparison))
            {
                progress?.Report(new CloudTransferProgress(
                    CloudTransferStage.Completed,
                    existing.SizeBytes,
                    asset.SizeBytes));
                return existing;
            }

            progress?.Report(new CloudTransferProgress(CloudTransferStage.Downloading, 0, asset.SizeBytes));
            try
            {
                await using (var source = new FileStream(
                                 existing.FullPath,
                                 FileMode.Open,
                                 FileAccess.Read,
                                 FileShare.Read,
                                 BufferSize,
                                 FileOptions.Asynchronous | FileOptions.SequentialScan))
                await using (var destination = new FileStream(
                                 partPath,
                                 FileMode.Create,
                                 FileAccess.Write,
                                 FileShare.None,
                                 BufferSize,
                                 FileOptions.Asynchronous | FileOptions.SequentialScan))
                using (var progressDestination = new ProgressWriteStream(destination, 0, bytesProcessed =>
                           progress?.Report(new CloudTransferProgress(
                               CloudTransferStage.Downloading,
                               bytesProcessed,
                               asset.SizeBytes))))
                {
                    await source.CopyToAsync(progressDestination, cancellationToken).ConfigureAwait(false);
                    await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                if (new FileInfo(partPath).Length != asset.SizeBytes)
                {
                    throw new InvalidDataException("The cached download size does not match the asset metadata.");
                }

                var copiedTargetPath = MovePartialToUniqueTarget(partPath, preferredTargetPath);
                return await RegisterCompletedDownloadAsync(
                    asset,
                    key,
                    copiedTargetPath,
                    new VerifiedDownload(asset.SizeBytes, asset.ContentHash),
                    progress).ConfigureAwait(false);
            }
            catch
            {
                TryDeleteFile(partPath);
                throw;
            }
        }

        var verified = await DownloadVerifiedPartialAsync(
            asset,
            partPath,
            progress,
            cancellationToken).ConfigureAwait(false);
        var targetPath = MovePartialToUniqueTarget(partPath, preferredTargetPath);
        return await RegisterCompletedDownloadAsync(
            asset,
            key,
            targetPath,
            verified,
            progress).ConfigureAwait(false);
    }

    private async Task<VerifiedDownload> DownloadVerifiedPartialAsync(
        ApiAsset asset,
        string partPath,
        IProgress<CloudTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        long actualSize;
        string actualSha256;
        var resumeOffset = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
        if (resumeOffset < 0 || resumeOffset > asset.SizeBytes)
        {
            TryDeleteFile(partPath);
            resumeOffset = 0;
        }
        progress?.Report(new CloudTransferProgress(
            CloudTransferStage.Downloading,
            resumeOffset,
            asset.SizeBytes));

        await using (var file = new FileStream(
            partPath,
            FileMode.OpenOrCreate,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        using (var destination = new ProgressWriteStream(file, resumeOffset, bytesProcessed =>
            progress?.Report(new CloudTransferProgress(
                CloudTransferStage.Downloading,
                bytesProcessed,
                asset.SizeBytes))))
        {
            file.Position = resumeOffset;
            await _apiClient.DownloadAssetVersionRangeAsync(
                asset.Id,
                asset.CurrentVersionId,
                resumeOffset,
                destination,
                cancellationToken).ConfigureAwait(false);
            await file.FlushAsync(cancellationToken).ConfigureAwait(false);
            actualSize = file.Length;
        }

        if (actualSize != asset.SizeBytes)
        {
            throw new InvalidDataException(
                $"Downloaded size {actualSize} does not match expected size {asset.SizeBytes}.");
        }

        await using (var downloaded = new FileStream(
            partPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            actualSha256 = Convert.ToHexString(
                await SHA256.HashDataAsync(downloaded, cancellationToken).ConfigureAwait(false));
        }

        if (!actualSha256.Equals(asset.ContentHash, StringComparison.OrdinalIgnoreCase))
        {
            TryDeleteFile(partPath);
            throw new InvalidDataException("Downloaded SHA-256 does not match the asset metadata.");
        }

        return new VerifiedDownload(actualSize, actualSha256);
    }

    private async Task<PersistentDownloadMapping> RegisterCompletedDownloadAsync(
        ApiAsset asset,
        DownloadAssetKey key,
        string targetPath,
        VerifiedDownload verified,
        IProgress<CloudTransferProgress>? progress)
    {
        var mapping = new PersistentDownloadMapping(
            key,
            asset.OriginalFileName,
            targetPath,
            verified.SizeBytes,
            DateTimeOffset.UtcNow,
            verified.Sha256);
        await _downloadRegistry.RegisterAsync(mapping, CancellationToken.None).ConfigureAwait(false);
        progress?.Report(new CloudTransferProgress(
            CloudTransferStage.Completed,
            verified.SizeBytes,
            asset.SizeBytes));
        return mapping;
    }

    private static string MovePartialToUniqueTarget(string partPath, string preferredTargetPath)
    {
        var directory = Path.GetDirectoryName(preferredTargetPath)!;
        var extension = Path.GetExtension(preferredTargetPath);
        var stem = Path.GetFileNameWithoutExtension(preferredTargetPath);
        for (var suffix = 1; suffix <= 10_000; suffix++)
        {
            var candidate = suffix == 1
                ? preferredTargetPath
                : Path.Combine(directory, $"{stem} ({suffix}){extension}");
            try
            {
                File.Move(partPath, candidate, overwrite: false);
                return candidate;
            }
            catch (IOException) when (File.Exists(candidate))
            {
                // Keep the user's existing file and try the next available name.
            }
        }

        throw new IOException("目标目录中同名文件过多，无法生成唯一文件名（最多尝试 10,000 次）。");
    }

    private static void ValidateDownload(ApiAsset asset, string directory)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (asset.Id == Guid.Empty || asset.CurrentVersionId == Guid.Empty)
        {
            throw new ArgumentException("Asset and current version identifiers must not be empty.", nameof(asset));
        }

        if (!asset.HasOriginal)
        {
            throw new InvalidOperationException("The asset does not have downloadable original content.");
        }
    }

    public Task<PersistentDownloadMapping?> FindDownloadedAsync(
        ApiAsset asset,
        bool requireExistingFile = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return _downloadRegistry.FindAsync(
            new DownloadAssetKey(asset.Id, asset.CurrentVersionId),
            requireExistingFile,
            cancellationToken);
    }

    private static async Task<string> ComputeSha256Async(
        Stream source,
        long totalBytes,
        IProgress<CloudTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new CloudTransferProgress(CloudTransferStage.Hashing, 0, totalBytes));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        long bytesProcessed = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            hash.AppendData(buffer, 0, read);
            bytesProcessed += read;
            progress?.Report(new CloudTransferProgress(
                CloudTransferStage.Hashing,
                bytesProcessed,
                totalBytes));
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private async Task TryDeleteCreatedAssetAsync(Guid assetId)
    {
        // A lost HTTP completion response does not mean the committed original failed.
        // If the server cannot confirm the state, leave it for a later explicit cleanup.
        try
        {
            if ((await _apiClient.GetAssetAsync(assetId, CancellationToken.None).ConfigureAwait(false)).HasOriginal) return;
        }
        catch { return; }
        try
        {
            _ = await _apiClient.RecycleAssetAsync(assetId, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Cleanup is best effort; the upload exception remains the operation result.
        }

        try
        {
            await _apiClient.PermanentlyDeleteAssetAsync(assetId, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Cleanup is best effort; the upload exception remains the operation result.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Do not replace the transfer failure with a secondary cleanup failure.
        }
    }

    private sealed record VerifiedDownload(long SizeBytes, string Sha256);

    private sealed class ProgressReadStream(Stream source, Action<long> report) : Stream
    {
        private long _bytesRead;

        public override bool CanRead => source.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => source.Length;
        public override long Position
        {
            get => source.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = source.Read(buffer, offset, count);
            Report(read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            Report(read);
            return read;
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private void Report(int read)
        {
            if (read <= 0)
            {
                return;
            }

            _bytesRead += read;
            report(_bytesRead);
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class BoundedReadStream(Stream source, long length) : Stream
    {
        private long _position;

        public override bool CanRead => source.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (buffer.Length - offset < count)
            {
                throw new ArgumentException("Offset and count exceed the buffer length.");
            }

            var allowed = (int)Math.Min(count, length - _position);
            if (allowed <= 0)
            {
                return 0;
            }

            var read = source.Read(buffer, offset, allowed);
            _position += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var allowed = (int)Math.Min(buffer.Length, length - _position);
            if (allowed <= 0)
            {
                return 0;
            }

            var read = await source.ReadAsync(buffer[..allowed], cancellationToken).ConfigureAwait(false);
            _position += read;
            return read;
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ProgressWriteStream(Stream destination, long initialBytes, Action<long> report) : Stream
    {
        public long BytesWritten { get; private set; } = initialBytes;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => destination.CanWrite;
        public override long Length => BytesWritten;
        public override long Position
        {
            get => BytesWritten;
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            destination.Write(buffer, offset, count);
            Record(buffer.AsSpan(offset, count));
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await destination.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            Record(buffer.Span);
        }

        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private void Record(ReadOnlySpan<byte> bytes)
        {
            BytesWritten += bytes.Length;
            report(BytesWritten);
        }

        public override void Flush() => destination.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) =>
            destination.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
