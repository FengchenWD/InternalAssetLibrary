using System.Buffers.Binary;
using InternalAssetLibrary.Server.Security;

namespace InternalAssetLibrary.Server.Services;

internal sealed record StoredAvatar(string FileName, string ContentType);

internal sealed class AvatarFileStore
{
    public const long MaximumBytes = 10 * 1024 * 1024;
    private readonly IObjectStore _objects;
    private readonly string _legacyDirectory;

    public AvatarFileStore(IObjectStore objects, IHostEnvironment environment, IConfiguration configuration)
    {
        _objects = objects;
        var configured = configuration["DevelopmentStorage:AvatarPath"] ?? "App_Data/avatars";
        _legacyDirectory = Path.GetFullPath(configured, environment.ContentRootPath);
    }

    public async Task<StoredAvatar> SaveAsync(
        Guid userId,
        Stream source,
        long? contentLength,
        string? contentType,
        CancellationToken cancellationToken)
    {
        ValidateContentType(contentType);
        if (contentLength is > MaximumBytes)
        {
            throw new ApiException(StatusCodes.Status413PayloadTooLarge, "avatar_too_large", "头像不能超过 10 MB。");
        }

        await using var memory = new MemoryStream(contentLength is > 0 ? (int)contentLength.Value : 0);
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var count = await source.ReadAsync(buffer, cancellationToken);
            if (count == 0)
            {
                break;
            }

            if (memory.Length + count > MaximumBytes)
            {
                throw new ApiException(StatusCodes.Status413PayloadTooLarge, "avatar_too_large", "头像不能超过 10 MB。");
            }

            await memory.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }

        var bytes = memory.ToArray();
        var (extension, normalizedContentType) = ValidateNormalizedWebP(bytes);
        var objectKey = $"avatars/{userId:N}/{Guid.NewGuid():N}{extension}";
        await using var input = new MemoryStream(bytes, writable: false);
        var written = await _objects.PutAsync(objectKey, input, bytes.LongLength, cancellationToken);
        if (written.SizeBytes != bytes.LongLength)
        {
            await _objects.DeleteAsync(objectKey, CancellationToken.None);
            throw new InvalidDataException("头像对象写入长度不一致。");
        }

        return new StoredAvatar(objectKey, normalizedContentType);
    }

    public async ValueTask<Stream?> OpenReadAsync(
        string objectKey,
        CancellationToken cancellationToken = default)
    {
        var stream = await _objects.OpenReadAsync(objectKey, cancellationToken);
        if (stream is not null || objectKey.Contains('/'))
        {
            return stream;
        }

        var legacyPath = LegacyPath(objectKey);
        try
        {
            return new FileStream(
                legacyPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    public async Task DeleteAsync(string? objectKey, CancellationToken cancellationToken = default)
    {
        if (objectKey is null)
        {
            return;
        }

        if (objectKey.Contains('/'))
        {
            await _objects.DeleteAsync(objectKey, cancellationToken);
        }
        else
        {
            File.Delete(LegacyPath(objectKey));
        }
    }

    private string LegacyPath(string fileName)
    {
        if (fileName != Path.GetFileName(fileName))
        {
            throw new InvalidOperationException("Invalid legacy avatar file name.");
        }

        return Path.Combine(_legacyDirectory, fileName);
    }

    internal static void ValidateContentType(string? contentType)
    {
        if (!string.Equals(contentType?.Trim(), "image/webp", StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidAvatar("头像 API 只接受 image/webp 请求正文。");
        }
    }

    internal static (string Extension, string ContentType) ValidateNormalizedWebP(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 20 ||
            !bytes[..4].SequenceEqual("RIFF"u8) ||
            !bytes.Slice(8, 4).SequenceEqual("WEBP"u8) ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(4, 4)) != bytes.Length - 8)
        {
            throw InvalidAvatar("头像 API 只接受客户端归一化后的静态 WebP 图片。");
        }

        int? extendedWidth = null;
        int? extendedHeight = null;
        int? imageWidth = null;
        int? imageHeight = null;
        var imageChunkCount = 0;
        var offset = 12;
        while (offset <= bytes.Length - 8)
        {
            var type = bytes.Slice(offset, 4);
            var chunkLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + 4, 4));
            if (chunkLength > int.MaxValue || offset + 8L + chunkLength > bytes.Length)
            {
                throw InvalidAvatar("WebP 头像结构无效。");
            }

            var data = bytes.Slice(offset + 8, (int)chunkLength);
            if (type.SequenceEqual("ANIM"u8) || type.SequenceEqual("ANMF"u8) ||
                type.SequenceEqual("EXIF"u8) || type.SequenceEqual("XMP "u8) ||
                type.SequenceEqual("ICCP"u8))
            {
                throw InvalidAvatar("头像不能包含动画或附加元数据。");
            }

            if (type.SequenceEqual("VP8X"u8))
            {
                if (data.Length != 10 || extendedWidth is not null || (data[0] & 0xEF) != 0)
                {
                    throw InvalidAvatar("WebP 头像结构无效。");
                }

                extendedWidth = 1 + data[4] + (data[5] << 8) + (data[6] << 16);
                extendedHeight = 1 + data[7] + (data[8] << 8) + (data[9] << 16);
            }
            else if (type.SequenceEqual("VP8 "u8))
            {
                if (imageChunkCount != 0 || data.Length < 10 ||
                    !data.Slice(3, 3).SequenceEqual(new byte[] { 0x9D, 0x01, 0x2A }))
                {
                    throw InvalidAvatar("WebP 头像结构无效。");
                }

                imageChunkCount++;
                imageWidth = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(6, 2)) & 0x3FFF;
                imageHeight = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(8, 2)) & 0x3FFF;
            }
            else if (type.SequenceEqual("VP8L"u8))
            {
                if (imageChunkCount != 0 || data.Length < 5 || data[0] != 0x2F)
                {
                    throw InvalidAvatar("WebP 头像结构无效。");
                }

                imageChunkCount++;
                var bits = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(1, 4));
                imageWidth = (int)(bits & 0x3FFF) + 1;
                imageHeight = (int)((bits >> 14) & 0x3FFF) + 1;
            }

            offset = checked(offset + 8 + (int)chunkLength + ((int)chunkLength & 1));
        }

        if (offset != bytes.Length || imageChunkCount != 1 ||
            imageWidth != 512 || imageHeight != 512 ||
            extendedWidth is not null && (extendedWidth != 512 || extendedHeight != 512))
        {
            throw InvalidAvatar("头像必须归一化为 512 x 512 像素。");
        }

        return (".webp", "image/webp");
    }

    private static ApiException InvalidAvatar(string message) => new(
        StatusCodes.Status415UnsupportedMediaType,
        "unsupported_avatar",
        message);
}
