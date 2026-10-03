using System.Security.Cryptography;
using COSXML;
using COSXML.Auth;
using COSXML.CosException;
using COSXML.Model.Bucket;
using COSXML.Model.Object;
using COSXML.Model.Tag;
using InternalAssetLibrary.Server.Security;

namespace InternalAssetLibrary.Server.Services;

/// <summary>
/// Tencent COS implementation used by production deployments.  The SDK exposes
/// synchronous operations, so calls run on the thread pool to keep request
/// handlers from blocking the ASP.NET request thread.
/// </summary>
internal sealed class CosObjectStore : IObjectStore
{
    private const int BufferSize = 128 * 1024;
    private const long CredentialKeyLifetimeSeconds = 24 * 60 * 60;
    private readonly CosXmlServer _client;
    private readonly string _bucket;
    private readonly string _appid;
    private readonly string _region;
    private readonly bool _useHttps;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CosObjectStore> _logger;

    public CosObjectStore(IConfiguration configuration, ILogger<CosObjectStore> logger)
    {
        _configuration = configuration;
        var section = configuration.GetSection("Cos");
        _appid = Required(section["AppId"], "Cos:AppId");
        _region = Required(section["Region"], "Cos:Region");
        var bucketName = Required(section["Bucket"], "Cos:Bucket");
        var secretId = Required(section["SecretId"], "Cos:SecretId");
        var secretKey = Required(section["SecretKey"], "Cos:SecretKey");
        _useHttps = section.GetValue("UseHttps", true);
        if (!_useHttps)
        {
            throw new InvalidOperationException("Cos:UseHttps must remain true for private object storage.");
        }

        if (_appid.Any(character => !char.IsDigit(character)) || _appid.Length is < 1 or > 32)
        {
            throw new InvalidOperationException("Cos:AppId must contain only digits.");
        }

        if (bucketName.Contains('/') || bucketName.Contains('\\') || bucketName.Any(char.IsWhiteSpace))
        {
            throw new InvalidOperationException("Cos:Bucket must be a bucket name without a path.");
        }

        _bucket = bucketName.EndsWith('-' + _appid, StringComparison.OrdinalIgnoreCase)
            ? bucketName
            : bucketName + '-' + _appid;
        _logger = logger;

        var config = new CosXmlConfig.Builder()
            .SetAppid(_appid)
            .SetRegion(_region)
            .IsHttps(_useHttps)
            .SetConnectionTimeoutMs(30_000)
            .SetReadWriteTimeoutMs(30 * 60 * 1_000)
            .Build();
        var credentials = new DefaultQCloudCredentialProvider(
            secretId,
            secretKey,
            CredentialKeyLifetimeSeconds);
        _client = new CosXmlServer(config, credentials);
    }

    public string StorageKind => "tencent-cos";

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await ExecuteAsync(() => _client.HeadBucket(new HeadBucketRequest(_bucket)), cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Tencent COS availability check failed for bucket {Bucket}.", _bucket);
            return false;
        }
    }

    public async Task<ObjectWriteResult> PutAsync(
        string objectKey,
        Stream source,
        long maximumBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateMaximum(maximumBytes);
        var key = ValidateObjectKey(objectKey);
        await using var upload = await PrepareUploadAsync(source, maximumBytes, cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = new PutObjectRequest(
                _bucket,
                key,
                upload.Stream,
                upload.Offset,
                upload.Length);
            _ = await ExecuteAsync(() => _client.PutObject(request), cancellationToken);
            return new ObjectWriteResult(
                key,
                upload.Length,
                upload.Sha256);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw StorageFailure("put", key, exception);
        }
    }

    internal static async Task<PreparedUpload> PrepareUploadAsync(
        Stream source,
        long maximumBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateMaximum(maximumBytes);

        if (source.CanSeek)
        {
            var offset = source.Position;
            var available = source.Length - offset;
            if (available < 0 || available > maximumBytes)
            {
                throw SizeLimitExceeded(maximumBytes);
            }

            var validated = await ValidateAndCopyAsync(
                source,
                destination: null,
                maximumBytes,
                cancellationToken);
            source.Position = offset;
            return new PreparedUpload(
                source,
                offset,
                validated.Length,
                validated.Sha256,
                temporaryPath: null);
        }

        var temporaryPath = Path.Combine(
            Path.GetTempPath(),
            $"ial-cos-upload-{Guid.NewGuid():N}.tmp");
        FileStream? temporary = null;
        try
        {
            temporary = new FileStream(
                temporaryPath,
                new FileStreamOptions
                {
                    Access = FileAccess.ReadWrite,
                    Mode = FileMode.CreateNew,
                    Share = FileShare.None,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose,
                    BufferSize = BufferSize
                });
            var validated = await ValidateAndCopyAsync(
                source,
                temporary,
                maximumBytes,
                cancellationToken);
            await temporary.FlushAsync(cancellationToken);
            temporary.Position = 0;
            return new PreparedUpload(
                temporary,
                0,
                validated.Length,
                validated.Sha256,
                temporaryPath);
        }
        catch
        {
            if (temporary is not null)
            {
                try
                {
                    await temporary.DisposeAsync();
                }
                finally
                {
                    TryDelete(temporaryPath);
                }
            }
            else
            {
                TryDelete(temporaryPath);
            }

            throw;
        }
    }

    public async ValueTask<Stream?> OpenReadAsync(
        string objectKey,
        CancellationToken cancellationToken = default)
    {
        var key = ValidateObjectKey(objectKey);
        cancellationToken.ThrowIfCancellationRequested();
        var fileName = $"ial-cos-{Guid.NewGuid():N}.bin";
        var tempDirectory = Path.GetTempPath();
        var localPath = Path.Combine(tempDirectory, fileName);
        try
        {
            await ExecuteAsync(
                () => _client.GetObject(new GetObjectRequest(_bucket, key, tempDirectory, fileName)),
                cancellationToken);
            if (!File.Exists(localPath))
            {
                return null;
            }

            return new FileStream(
                localPath,
                new FileStreamOptions
                {
                    Access = FileAccess.Read,
                    Mode = FileMode.Open,
                    Share = FileShare.Read | FileShare.Delete,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose,
                    BufferSize = BufferSize
                });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            TryDelete(localPath);
            if (IsNotFound(exception))
            {
                return null;
            }

            throw StorageFailure("read", key, exception);
        }
    }

    public ValueTask<ObjectDownloadUrl?> CreateDownloadUrlAsync(
        string objectKey,
        string? downloadFileName = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = ValidateObjectKey(objectKey);
        var now = DateTimeOffset.UtcNow;
        var lifetimeSeconds = Math.Clamp(_configuration.GetValue("Cos:SignedUrlLifetimeMinutes", 15), 1, 60) * 60L;
        var objectUrl = new Uri(_client.GetObjectUrl(_bucket, key));
        var signature = new PreSignatureStruct
        {
            appid = _appid,
            bucket = _bucket,
            region = _region,
            host = objectUrl.Host,
            key = key,
            isHttps = _useHttps,
            signHost = true,
            httpMethod = "GET",
            signDurationSecond = lifetimeSeconds,
            keyDurationSecond = CredentialKeyLifetimeSeconds,
            queryParameters = new Dictionary<string, string>(StringComparer.Ordinal),
            headers = new Dictionary<string, string>(StringComparer.Ordinal)
        };
        if (!string.IsNullOrWhiteSpace(downloadFileName))
        {
            var safeName = Path.GetFileName(downloadFileName.Trim());
            if (safeName.Length > 0)
            {
                signature.queryParameters["response-content-disposition"] =
                    "attachment; filename*=UTF-8''" + safeName;
            }
        }

        var signed = _client.GenerateSignURL(signature);
        if (!Uri.TryCreate(signed, UriKind.Absolute, out var url) ||
            url.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(url.UserInfo))
        {
            throw new InvalidOperationException("Tencent COS returned an invalid signed URL.");
        }

        return ValueTask.FromResult<ObjectDownloadUrl?>(new ObjectDownloadUrl(url, now.AddSeconds(lifetimeSeconds)));
    }

    public async Task DeleteAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        var key = ValidateObjectKey(objectKey);
        try
        {
            await ExecuteAsync(() => _client.DeleteObject(new DeleteObjectRequest(_bucket, key)), cancellationToken);
        }
        catch (Exception exception) when (IsNotFound(exception))
        {
            // Deletion is idempotent, matching the development object store.
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw StorageFailure("delete", key, exception);
        }
    }

    public bool SupportsDirectMultipartUpload => true;

    public async Task<MultipartUploadHandle> StartMultipartUploadAsync(
        string objectKey,
        long sizeBytes,
        string sha256,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        ValidateMaximum(sizeBytes);
        var key = ValidateObjectKey(objectKey);
        ValidateSha256(sha256);
        try
        {
            var request = new InitMultipartUploadRequest(_bucket, key);
            request.SetRequestHeader("Content-Type", string.IsNullOrWhiteSpace(contentType)
                ? "application/octet-stream"
                : contentType);
            request.SetRequestHeader("x-cos-meta-sha256", sha256.ToUpperInvariant());
            request.SetRequestHeader("x-cos-meta-size", sizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var result = await ExecuteAsync(() => _client.InitMultipartUpload(request), cancellationToken);
            var uploadId = result.initMultipartUpload?.uploadId?.Trim();
            if (string.IsNullOrWhiteSpace(uploadId))
            {
                throw new InvalidDataException("Tencent COS did not return a multipart upload ID.");
            }

            return new MultipartUploadHandle(uploadId);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw StorageFailure("start multipart upload", key, exception);
        }
    }

    public ValueTask<ObjectDownloadUrl> CreateUploadPartUrlAsync(
        string objectKey,
        string providerUploadId,
        int partNumber,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (partNumber is < 1 or > 10_000 || string.IsNullOrWhiteSpace(providerUploadId))
        {
            throw new ArgumentOutOfRangeException(nameof(partNumber), "Multipart upload part parameters are invalid.");
        }

        var key = ValidateObjectKey(objectKey);
        var lifetimeSeconds = Math.Clamp(_configuration.GetValue("Cos:SignedUrlLifetimeMinutes", 15), 1, 60) * 60L;
        var objectUrl = new Uri(_client.GetObjectUrl(_bucket, key));
        var signature = new PreSignatureStruct
        {
            appid = _appid,
            bucket = _bucket,
            region = _region,
            host = objectUrl.Host,
            key = key,
            isHttps = _useHttps,
            signHost = true,
            httpMethod = "PUT",
            signDurationSecond = lifetimeSeconds,
            keyDurationSecond = CredentialKeyLifetimeSeconds,
            queryParameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["uploadId"] = providerUploadId,
                ["partNumber"] = partNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)
            },
            headers = new Dictionary<string, string>(StringComparer.Ordinal)
        };
        var signed = _client.GenerateSignURL(signature);
        if (!Uri.TryCreate(signed, UriKind.Absolute, out var url) ||
            url.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(url.UserInfo))
        {
            throw new InvalidOperationException("Tencent COS returned an invalid multipart upload URL.");
        }

        return ValueTask.FromResult(new ObjectDownloadUrl(url, DateTimeOffset.UtcNow.AddSeconds(lifetimeSeconds)));
    }

    public async Task<ObjectWriteResult> CompleteMultipartUploadAsync(
        string objectKey,
        string providerUploadId,
        IReadOnlyList<MultipartUploadPart> parts,
        long expectedSizeBytes,
        string expectedSha256,
        CancellationToken cancellationToken = default)
    {
        var key = ValidateObjectKey(objectKey);
        ValidateMaximum(expectedSizeBytes);
        ValidateSha256(expectedSha256);
        if (string.IsNullOrWhiteSpace(providerUploadId) || parts.Count == 0)
        {
            throw new ArgumentException("Multipart upload completion parameters are invalid.");
        }

        try
        {
            var request = new CompleteMultipartUploadRequest(_bucket, key, providerUploadId);
            foreach (var part in parts.OrderBy(item => item.PartNumber))
            {
                if (part.PartNumber is < 1 or > 10_000 || string.IsNullOrWhiteSpace(part.ETag))
                {
                    throw new ArgumentException("Multipart upload part ETag is invalid.");
                }

                request.SetPartNumberAndETag(part.PartNumber, part.ETag.Trim().Trim('"'));
            }

            await ExecuteAsync(() => _client.CompleteMultiUpload(request), cancellationToken);
            var head = await ExecuteAsync(() => _client.HeadObject(new HeadObjectRequest(_bucket, key)), cancellationToken);
            var actualSize = head.size;
            var actualSha256 = ReadHeader(head.responseHeaders, "x-cos-meta-sha256");
            if (actualSize != expectedSizeBytes ||
                !string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                await TryDeleteAfterFailedWriteAsync(key);
                throw new InvalidDataException(
                    $"Completed COS object metadata did not match the expected size or SHA-256 for '{key}'.");
            }

            return new ObjectWriteResult(key, actualSize, actualSha256.ToUpperInvariant());
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw StorageFailure("complete multipart upload", key, exception);
        }
    }

    public async Task AbortMultipartUploadAsync(
        string objectKey,
        string providerUploadId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(providerUploadId))
        {
            return;
        }

        var key = ValidateObjectKey(objectKey);
        try
        {
            await ExecuteAsync(
                () => _client.AbortMultiUpload(new AbortMultipartUploadRequest(_bucket, key, providerUploadId)),
                cancellationToken);
        }
        catch (Exception exception) when (IsNotFound(exception))
        {
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw StorageFailure("abort multipart upload", key, exception);
        }
    }

    private async Task<T> ExecuteAsync<T>(Func<T> operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await Task.Run(operation).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private ApiException StorageFailure(string operation, string objectKey, Exception exception)
    {
        _logger.LogError(exception, "Tencent COS {Operation} failed for {ObjectKey}.", operation, objectKey);
        return new ApiException(
            StatusCodes.Status503ServiceUnavailable,
            "object_storage_unavailable",
            "腾讯云对象存储暂时不可用，请稍后重试。 ");
    }

    private async Task TryDeleteAfterFailedWriteAsync(string objectKey)
    {
        try
        {
            await DeleteAsync(objectKey, CancellationToken.None);
        }
        catch (Exception cleanupException)
        {
            _logger.LogWarning(
                cleanupException,
                "Could not remove incomplete Tencent COS object {ObjectKey}.",
                objectKey);
        }
    }

    private static string Required(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains("REPLACE_WITH", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{name} must be configured with a real value before using Tencent COS.");
        }

        return value.Trim();
    }

    private static string ValidateObjectKey(string objectKey)
    {
        if (string.IsNullOrWhiteSpace(objectKey) || objectKey.Length > 1_024 ||
            objectKey[0] == '/' || objectKey[^1] == '/' || objectKey.Contains('\\'))
        {
            throw new ArgumentException("The object key is not a safe relative path.", nameof(objectKey));
        }

        foreach (var segment in objectKey.Split('/'))
        {
            if (segment.Length is 0 or > 128 || segment is "." or ".." ||
                segment.Any(character => char.IsControl(character) || character is '\r' or '\n'))
            {
                throw new ArgumentException("The object key contains an unsafe path segment.", nameof(objectKey));
            }
        }

        return objectKey;
    }

    private static void ValidateMaximum(long maximumBytes)
    {
        if (maximumBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes), "The object size must be positive.");
        }
    }

    private static void ValidateSha256(string value)
    {
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("SHA-256 must be a 64-character hexadecimal value.", nameof(value));
        }
    }

    private static string ReadHeader(
        IReadOnlyDictionary<string, List<string>>? headers,
        string name)
    {
        if (headers is null)
        {
            return string.Empty;
        }

        foreach (var pair in headers)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value.FirstOrDefault()?.Trim() ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private static bool IsNotFound(Exception exception) =>
        exception is CosServerException { statusCode: 404 } ||
        exception is FileNotFoundException or DirectoryNotFoundException ||
        exception.Message.Contains("NoSuchKey", StringComparison.OrdinalIgnoreCase);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }

    private static async Task<(long Length, string Sha256)> ValidateAndCopyAsync(
        Stream source,
        Stream? destination,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = GC.AllocateUninitializedArray<byte>(BufferSize);
        long bytesRead = 0;
        while (true)
        {
            var remaining = maximumBytes - bytesRead;
            var requested = remaining > 0
                ? (int)Math.Min(remaining, buffer.Length)
                : 1;
            var read = await source.ReadAsync(
                buffer.AsMemory(0, requested),
                cancellationToken);
            if (read == 0)
            {
                break;
            }

            bytesRead += read;
            if (bytesRead > maximumBytes)
            {
                throw SizeLimitExceeded(maximumBytes);
            }

            hash.AppendData(buffer, 0, read);
            if (destination is not null)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }

        return (bytesRead, Convert.ToHexString(hash.GetHashAndReset()));
    }

    private static InvalidDataException SizeLimitExceeded(long maximumBytes) =>
        new($"The source stream exceeds the {maximumBytes} byte limit.");

    internal sealed class PreparedUpload(
        Stream stream,
        long offset,
        long length,
        string sha256,
        string? temporaryPath) : IAsyncDisposable
    {
        private int _disposed;

        public Stream Stream { get; } = stream;
        public long Offset { get; } = offset;
        public long Length { get; } = length;
        public string Sha256 { get; } = sha256;
        public string? TemporaryPath { get; } = temporaryPath;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0 || TemporaryPath is null)
            {
                return;
            }

            try
            {
                await Stream.DisposeAsync();
            }
            finally
            {
                TryDelete(TemporaryPath);
            }
        }
    }
}
