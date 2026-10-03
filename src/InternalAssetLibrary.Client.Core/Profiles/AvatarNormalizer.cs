using System.Buffers.Binary;
using InternalAssetLibrary.Core;
using SkiaSharp;

namespace InternalAssetLibrary.Client.Core.Profiles;

public static class AvatarNormalizer
{
    public const int OutputWidth = 512;
    public const int OutputHeight = 512;
    public const int WebpQuality = 90;
    public const long MaxInputBytes = InputValidator.AvatarMaxBytes;
    public const string OutputContentType = "image/webp";
    public const string OutputFileName = "avatar.webp";

    public static async Task<NormalizedAvatar> NormalizeFileAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        await using var source = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await NormalizeAsync(source, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<NormalizedAvatar> NormalizeAsync(
        Stream source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
        {
            throw new ArgumentException("The avatar input stream must be readable.", nameof(source));
        }

        var input = await ReadInputAsync(source, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        if (IsAnimatedWebp(input) || IsAnimatedPng(input))
        {
            throw new AvatarNormalizationException("Animated avatars are not supported.");
        }

        using var encodedInput = SKData.CreateCopy(input);
        using var codec = SKCodec.Create(encodedInput);
        if (codec is null)
        {
            throw new AvatarNormalizationException("The avatar is not a valid image.");
        }

        if (codec.EncodedFormat is not (
            SKEncodedImageFormat.Jpeg or
            SKEncodedImageFormat.Png or
            SKEncodedImageFormat.Webp))
        {
            throw new AvatarNormalizationException("Only JPEG, PNG, and static WebP avatars are supported.");
        }

        if (codec.FrameCount > 1)
        {
            throw new AvatarNormalizationException("Animated avatars are not supported.");
        }

        using var decoded = SKBitmap.Decode(codec);
        if (decoded is null || decoded.Width <= 0 || decoded.Height <= 0)
        {
            throw new AvatarNormalizationException("The avatar image could not be decoded.");
        }

        var outputInfo = new SKImageInfo(
            OutputWidth,
            OutputHeight,
            SKColorType.Rgba8888,
            SKAlphaType.Premul);
        using var output = new SKBitmap(outputInfo);
        using (var canvas = new SKCanvas(output))
        using (var paint = new SKPaint
        {
            FilterQuality = SKFilterQuality.High,
            IsAntialias = true
        })
        {
            canvas.Clear(SKColors.Transparent);
            canvas.SetMatrix(CreateOrientationMatrix(codec.EncodedOrigin));

            var cropSize = Math.Min(decoded.Width, decoded.Height);
            var cropLeft = (decoded.Width - cropSize) / 2f;
            var cropTop = (decoded.Height - cropSize) / 2f;
            var sourceRect = SKRect.Create(cropLeft, cropTop, cropSize, cropSize);
            var destinationRect = SKRect.Create(0, 0, OutputWidth, OutputHeight);
            canvas.DrawBitmap(decoded, sourceRect, destinationRect, paint);
            canvas.Flush();
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var image = SKImage.FromBitmap(output);
        using var encodedOutput = image.Encode(SKEncodedImageFormat.Webp, WebpQuality);
        if (encodedOutput is null)
        {
            throw new InvalidOperationException("SkiaSharp could not encode the normalized avatar as WebP.");
        }

        var bytes = encodedOutput.ToArray();
        return new NormalizedAvatar(bytes);
    }

    private static async Task<byte[]> ReadInputAsync(Stream source, CancellationToken cancellationToken)
    {
        using var destination = new MemoryStream();
        var buffer = new byte[64 * 1024];
        long totalBytes = 0;

        while (true)
        {
            var remainingWithSentinel = MaxInputBytes + 1 - totalBytes;
            var requestedBytes = (int)Math.Min(buffer.Length, remainingWithSentinel);
            var bytesRead = await source.ReadAsync(
                buffer.AsMemory(0, requestedBytes),
                cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                break;
            }

            totalBytes += bytesRead;
            if (totalBytes > MaxInputBytes)
            {
                throw new AvatarNormalizationException(
                    $"The avatar input exceeds the {MaxInputBytes}-byte limit.");
            }

            destination.Write(buffer, 0, bytesRead);
        }

        if (totalBytes == 0)
        {
            throw new AvatarNormalizationException("The avatar input is empty.");
        }

        return destination.ToArray();
    }

    private static SKMatrix CreateOrientationMatrix(SKEncodedOrigin origin)
    {
        const float size = OutputWidth;
        return origin switch
        {
            SKEncodedOrigin.TopRight => Matrix(-1, 0, size, 0, 1, 0),
            SKEncodedOrigin.BottomRight => Matrix(-1, 0, size, 0, -1, size),
            SKEncodedOrigin.BottomLeft => Matrix(1, 0, 0, 0, -1, size),
            SKEncodedOrigin.LeftTop => Matrix(0, 1, 0, 1, 0, 0),
            SKEncodedOrigin.RightTop => Matrix(0, -1, size, 1, 0, 0),
            SKEncodedOrigin.RightBottom => Matrix(0, -1, size, -1, 0, size),
            SKEncodedOrigin.LeftBottom => Matrix(0, 1, 0, -1, 0, size),
            _ => SKMatrix.CreateIdentity()
        };
    }

    private static SKMatrix Matrix(
        float scaleX,
        float skewX,
        float transX,
        float skewY,
        float scaleY,
        float transY) =>
        new()
        {
            ScaleX = scaleX,
            SkewX = skewX,
            TransX = transX,
            SkewY = skewY,
            ScaleY = scaleY,
            TransY = transY,
            Persp2 = 1
        };

    private static bool IsAnimatedWebp(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 12 ||
            !bytes[..4].SequenceEqual("RIFF"u8) ||
            !bytes.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return false;
        }

        var declaredEnd = Math.Min(
            bytes.Length,
            checked((int)Math.Min((long)BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(4, 4)) + 8, int.MaxValue)));
        var offset = 12;
        while (offset <= declaredEnd - 8)
        {
            var chunkName = bytes.Slice(offset, 4);
            var chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + 4, 4));
            var payloadStart = offset + 8;
            var payloadEnd = (long)payloadStart + chunkSize;
            if (payloadEnd > declaredEnd)
            {
                return false;
            }

            if (chunkName.SequenceEqual("ANIM"u8) ||
                chunkName.SequenceEqual("ANMF"u8) ||
                (chunkName.SequenceEqual("VP8X"u8) && chunkSize > 0 && (bytes[payloadStart] & 0x02) != 0))
            {
                return true;
            }

            var nextOffset = payloadEnd + (chunkSize & 1);
            if (nextOffset > int.MaxValue)
            {
                return false;
            }

            offset = (int)nextOffset;
        }

        return false;
    }

    private static bool IsAnimatedPng(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 8 || bytes[0] != 0x89 || !bytes.Slice(1, 7).SequenceEqual("PNG\r\n\x1a\n"u8))
        {
            return false;
        }

        var offset = 8;
        while (offset <= bytes.Length - 12)
        {
            var chunkSize = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset, 4));
            if (bytes.Slice(offset + 4, 4).SequenceEqual("acTL"u8))
            {
                return true;
            }

            var nextOffset = (long)offset + 12 + chunkSize;
            if (nextOffset > bytes.Length || nextOffset > int.MaxValue)
            {
                return false;
            }

            offset = (int)nextOffset;
        }

        return false;
    }
}

public sealed class NormalizedAvatar
{
    private readonly byte[] _content;

    internal NormalizedAvatar(byte[] content)
    {
        _content = content;
    }

    public ReadOnlyMemory<byte> Content => _content;
    public long SizeBytes => _content.LongLength;
    public string ContentType => AvatarNormalizer.OutputContentType;
    public string FileName => AvatarNormalizer.OutputFileName;

    public Stream OpenRead() => new MemoryStream(_content, writable: false);
}

public sealed class AvatarNormalizationException(string message) : Exception(message);
