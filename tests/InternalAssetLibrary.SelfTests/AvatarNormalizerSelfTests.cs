using System.Buffers.Binary;
using System.Text;
using InternalAssetLibrary.Client.Core.Profiles;
using SkiaSharp;

internal static class AvatarNormalizerSelfTests
{
    public static void SupportedInputsBecomeSanitizedSquareWebp() =>
        SupportedInputsBecomeSanitizedSquareWebpAsync().GetAwaiter().GetResult();

    public static void InputSizeLimitIsEnforced() =>
        InputSizeLimitIsEnforcedAsync().GetAwaiter().GetResult();

    public static void InvalidAndAnimatedInputsAreRejected() =>
        InvalidAndAnimatedInputsAreRejectedAsync().GetAwaiter().GetResult();

    private static async Task SupportedInputsBecomeSanitizedSquareWebpAsync()
    {
        foreach (var format in new[]
                 {
                     SKEncodedImageFormat.Jpeg,
                     SKEncodedImageFormat.Png,
                     SKEncodedImageFormat.Webp
                 })
        {
            var input = CreateInputImage(format);
            using var source = new MemoryStream(input, writable: false);
            var avatar = await AvatarNormalizer.NormalizeAsync(source);

            Equal(AvatarNormalizer.OutputContentType, avatar.ContentType);
            Equal(AvatarNormalizer.OutputFileName, avatar.FileName);
            Equal(avatar.Content.Length, checked((int)avatar.SizeBytes));
            AssertNormalizedWebp(avatar);
        }

        var temporaryPath = Path.Combine(Path.GetTempPath(), $"avatar-{Guid.NewGuid():N}.png");
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, CreateInputImage(SKEncodedImageFormat.Png));
            AssertNormalizedWebp(await AvatarNormalizer.NormalizeFileAsync(temporaryPath));
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static async Task InputSizeLimitIsEnforcedAsync()
    {
        var png = CreateInputImage(SKEncodedImageFormat.Png);
        var inputLimit = checked((int)AvatarNormalizer.MaxInputBytes);
        var exactLimit = new byte[inputLimit];
        png.CopyTo(exactLimit, 0);

        using (var accepted = new MemoryStream(exactLimit, writable: false))
        {
            AssertNormalizedWebp(await AvatarNormalizer.NormalizeAsync(accepted));
        }

        var overLimit = new byte[inputLimit + 1];
        png.CopyTo(overLimit, 0);
        using var rejected = new MemoryStream(overLimit, writable: false);
        await ThrowsAsync<AvatarNormalizationException>(() => AvatarNormalizer.NormalizeAsync(rejected));
    }

    private static async Task InvalidAndAnimatedInputsAreRejectedAsync()
    {
        using (var invalid = new MemoryStream("not an image"u8.ToArray(), writable: false))
        {
            await ThrowsAsync<AvatarNormalizationException>(() => AvatarNormalizer.NormalizeAsync(invalid));
        }

        using var animated = new MemoryStream(CreateAnimatedWebp(), writable: false);
        await ThrowsAsync<AvatarNormalizationException>(() => AvatarNormalizer.NormalizeAsync(animated));

        using var animatedPng = new MemoryStream(CreateAnimatedPng(), writable: false);
        await ThrowsAsync<AvatarNormalizationException>(() => AvatarNormalizer.NormalizeAsync(animatedPng));
    }

    private static byte[] CreateInputImage(SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(
            new SKImageInfo(900, 300, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint())
        {
            canvas.Clear(SKColors.Red);
            paint.Color = SKColors.Lime;
            canvas.DrawRect(SKRect.Create(300, 0, 300, 300), paint);
            paint.Color = SKColors.Blue;
            canvas.DrawRect(SKRect.Create(600, 0, 300, 300), paint);
            canvas.Flush();
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(format, 95);
        return encoded?.ToArray() ?? throw new InvalidOperationException($"Could not encode {format} test input.");
    }

    private static byte[] CreateAnimatedWebp()
    {
        using var body = new MemoryStream();
        body.Write("WEBP"u8);

        var extendedHeader = new byte[10];
        extendedHeader[0] = 0x02;
        WriteUInt24(extendedHeader.AsSpan(4, 3), 0);
        WriteUInt24(extendedHeader.AsSpan(7, 3), 0);
        WriteChunk(body, "VP8X", extendedHeader);
        WriteChunk(body, "ANIM", new byte[6]);

        var riffBody = body.ToArray();
        using var result = new MemoryStream();
        result.Write("RIFF"u8);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(length, checked((uint)riffBody.Length));
        result.Write(length);
        result.Write(riffBody);
        return result.ToArray();
    }

    private static byte[] CreateAnimatedPng()
    {
        var png = CreateInputImage(SKEncodedImageFormat.Png);
        var ihdrEnd = 8 + 12 + checked((int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(8, 4)));
        using var result = new MemoryStream();
        result.Write(png, 0, ihdrEnd);

        Span<byte> animationControl = stackalloc byte[20];
        BinaryPrimitives.WriteUInt32BigEndian(animationControl[..4], 8);
        "acTL"u8.CopyTo(animationControl.Slice(4, 4));
        BinaryPrimitives.WriteUInt32BigEndian(animationControl.Slice(8, 4), 2);
        BinaryPrimitives.WriteUInt32BigEndian(animationControl.Slice(12, 4), 0);
        result.Write(animationControl);
        result.Write(png, ihdrEnd, png.Length - ihdrEnd);
        return result.ToArray();
    }

    private static void WriteChunk(Stream destination, string name, byte[] payload)
    {
        destination.Write(Encoding.ASCII.GetBytes(name));
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(length, checked((uint)payload.Length));
        destination.Write(length);
        destination.Write(payload);
        if ((payload.Length & 1) != 0)
        {
            destination.WriteByte(0);
        }
    }

    private static void WriteUInt24(Span<byte> destination, int value)
    {
        destination[0] = (byte)value;
        destination[1] = (byte)(value >> 8);
        destination[2] = (byte)(value >> 16);
    }

    private static void AssertNormalizedWebp(NormalizedAvatar avatar)
    {
        var bytes = avatar.Content.Span;
        True(bytes.Length >= 12);
        True(bytes[..4].SequenceEqual("RIFF"u8));
        True(bytes.Slice(8, 4).SequenceEqual("WEBP"u8));

        foreach (var chunkName in ReadChunkNames(bytes))
        {
            False(chunkName is "ANIM" or "ANMF" or "EXIF" or "ICCP" or "XMP ");
        }

        using var stream = avatar.OpenRead();
        using var codec = SKCodec.Create(stream);
        True(codec is not null);
        Equal(SKEncodedImageFormat.Webp, codec!.EncodedFormat);
        True(codec.FrameCount <= 1);
        Equal(AvatarNormalizer.OutputWidth, codec.Info.Width);
        Equal(AvatarNormalizer.OutputHeight, codec.Info.Height);

        using var bitmap = SKBitmap.Decode(codec);
        True(bitmap is not null);
        var center = bitmap!.GetPixel(AvatarNormalizer.OutputWidth / 2, AvatarNormalizer.OutputHeight / 2);
        True(center.Green > 200);
        True(center.Red < 40);
        True(center.Blue < 40);
    }

    private static IReadOnlyList<string> ReadChunkNames(ReadOnlySpan<byte> bytes)
    {
        var names = new List<string>();
        var declaredEnd = Math.Min(
            bytes.Length,
            checked((int)Math.Min((long)BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(4, 4)) + 8, int.MaxValue)));
        var offset = 12;
        while (offset <= declaredEnd - 8)
        {
            var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + 4, 4));
            names.Add(Encoding.ASCII.GetString(bytes.Slice(offset, 4)));
            var next = (long)offset + 8 + size + (size & 1);
            if (next > declaredEnd || next > int.MaxValue)
            {
                break;
            }

            offset = (int)next;
        }

        return names;
    }

    private static async Task ThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name} to be thrown.");
    }

    private static void True(bool condition)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Expected true, but was false.");
        }
    }

    private static void False(bool condition) => True(!condition);

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', but was '{actual}'.");
        }
    }
}
