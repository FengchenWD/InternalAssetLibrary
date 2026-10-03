using System.Buffers.Binary;
using System.Text;
using InternalAssetLibrary.Client.Core.Profiles;
using InternalAssetLibrary.Server.Security;
using InternalAssetLibrary.Server.Services;
using SkiaSharp;

internal static class ServerAvatarValidationSelfTests
{
    public static void NormalizedStaticWebpIsAccepted() =>
        NormalizedStaticWebpIsAcceptedAsync().GetAwaiter().GetResult();

    public static void ForgedAvatarUploadsAreRejected() =>
        ForgedAvatarUploadsAreRejectedAsync().GetAwaiter().GetResult();

    private static async Task NormalizedStaticWebpIsAcceptedAsync()
    {
        using var source = new MemoryStream(CreatePng(), writable: false);
        var normalized = await AvatarNormalizer.NormalizeAsync(source);

        AvatarFileStore.ValidateContentType("image/webp");
        var result = AvatarFileStore.ValidateNormalizedWebP(normalized.Content.Span);
        Equal(".webp", result.Extension);
        Equal("image/webp", result.ContentType);
    }

    private static async Task ForgedAvatarUploadsAreRejectedAsync()
    {
        ThrowsUnsupported(() => AvatarFileStore.ValidateContentType("image/png"));
        ThrowsUnsupported(() => AvatarFileStore.ValidateContentType(null));
        ThrowsUnsupported(() => AvatarFileStore.ValidateNormalizedWebP(CreatePng()));
        ThrowsUnsupported(() => AvatarFileStore.ValidateNormalizedWebP(CreateHeaderOnlyWebp()));
        ThrowsUnsupported(() => AvatarFileStore.ValidateNormalizedWebP(CreateAnimatedWebp()));

        using var source = new MemoryStream(CreatePng(), writable: false);
        var normalized = await AvatarNormalizer.NormalizeAsync(source);
        var incorrectRiffLength = normalized.Content.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(
            incorrectRiffLength.AsSpan(4, 4),
            checked((uint)incorrectRiffLength.Length));
        ThrowsUnsupported(() => AvatarFileStore.ValidateNormalizedWebP(incorrectRiffLength));
    }

    private static byte[] CreatePng()
    {
        using var bitmap = new SKBitmap(16, 16, SKColorType.Rgba8888, SKAlphaType.Opaque);
        bitmap.Erase(SKColors.DeepSkyBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded?.ToArray() ?? throw new InvalidOperationException("Could not encode PNG test input.");
    }

    private static byte[] CreateHeaderOnlyWebp()
    {
        var extendedHeader = new byte[10];
        WriteUInt24(extendedHeader.AsSpan(4, 3), 511);
        WriteUInt24(extendedHeader.AsSpan(7, 3), 511);
        return CreateWebp(("VP8X", extendedHeader));
    }

    private static byte[] CreateAnimatedWebp()
    {
        var extendedHeader = new byte[10];
        extendedHeader[0] = 0x02;
        WriteUInt24(extendedHeader.AsSpan(4, 3), 511);
        WriteUInt24(extendedHeader.AsSpan(7, 3), 511);
        return CreateWebp(("VP8X", extendedHeader), ("ANIM", new byte[6]));
    }

    private static byte[] CreateWebp(params (string Name, byte[] Payload)[] chunks)
    {
        using var body = new MemoryStream();
        body.Write("WEBP"u8);
        Span<byte> length = stackalloc byte[4];
        foreach (var (name, payload) in chunks)
        {
            body.Write(Encoding.ASCII.GetBytes(name));
            BinaryPrimitives.WriteUInt32LittleEndian(length, checked((uint)payload.Length));
            body.Write(length);
            body.Write(payload);
            if ((payload.Length & 1) != 0)
            {
                body.WriteByte(0);
            }
        }

        var riffBody = body.ToArray();
        using var result = new MemoryStream();
        result.Write("RIFF"u8);
        Span<byte> riffLength = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(riffLength, checked((uint)riffBody.Length));
        result.Write(riffLength);
        result.Write(riffBody);
        return result.ToArray();
    }

    private static void WriteUInt24(Span<byte> destination, int value)
    {
        destination[0] = (byte)value;
        destination[1] = (byte)(value >> 8);
        destination[2] = (byte)(value >> 16);
    }

    private static void ThrowsUnsupported(Action action)
    {
        try
        {
            action();
        }
        catch (ApiException exception) when (
            exception.StatusCode == 415 &&
            exception.Code == "unsupported_avatar")
        {
            return;
        }

        throw new InvalidOperationException("Expected unsupported_avatar to be thrown.");
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', but was '{actual}'.");
        }
    }
}
