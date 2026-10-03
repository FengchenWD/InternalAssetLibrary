using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using InternalAssetLibrary.Client.Core.LocalAssets;
using SkiaSharp;

internal static class StaticImageThumbnailServiceSelfTests
{
    public static void OutputIsBoundedAndCacheIsReused() =>
        OutputIsBoundedAndCacheIsReusedAsync().GetAwaiter().GetResult();

    public static void SourceChangesInvalidateAndCacheStaysBounded() =>
        SourceChangesInvalidateAndCacheStaysBoundedAsync().GetAwaiter().GetResult();

    public static void UnsafeInputsDowngradeAndDisposeAllowsInFlightWork() =>
        UnsafeInputsDowngradeAndDisposeAllowsInFlightWorkAsync().GetAwaiter().GetResult();

    public static void WindowsShellThumbnailsReleaseGdiHandles() =>
        WindowsShellThumbnailsReleaseGdiHandlesAsync().GetAwaiter().GetResult();

    private static async Task OutputIsBoundedAndCacheIsReusedAsync()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var sourcePath = Path.Combine(root, "wide.png");
            await File.WriteAllBytesAsync(sourcePath, CreateRaster(SKEncodedImageFormat.Png, 640, 320, SKColors.CornflowerBlue));
            File.SetLastWriteTimeUtc(sourcePath, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));

            using var service = new StaticImageThumbnailService(Path.Combine(root, "cache"));
            var first = await service.GetOrCreateAsync(sourcePath, 128);
            NotNull(first);
            Equal(128, first!.PixelWidth);
            Equal(64, first.PixelHeight);
            False(first.FromCache);
            True(File.Exists(first.CachePath));

            var second = await service.GetOrCreateAsync(sourcePath, 128);
            NotNull(second);
            Equal(first.CachePath, second!.CachePath);
            True(second.FromCache);

            using var stream = File.OpenRead(second.CachePath);
            using var codec = SKCodec.Create(stream);
            NotNull(codec);
            Equal(SKEncodedImageFormat.Png, codec!.EncodedFormat);
            True(codec.Info.Width <= 128 && codec.Info.Height <= 128);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task SourceChangesInvalidateAndCacheStaysBoundedAsync()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var cache = Path.Combine(root, "cache");
            using var service = new StaticImageThumbnailService(cache, maximumCacheFiles: 2);
            var sourcePath = Path.Combine(root, "changing.png");
            await File.WriteAllBytesAsync(sourcePath, CreateRaster(SKEncodedImageFormat.Png, 80, 40, SKColors.Red));
            var firstWrite = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(sourcePath, firstWrite);
            var first = await service.GetOrCreateAsync(sourcePath, 96);
            NotNull(first);

            await File.WriteAllBytesAsync(sourcePath, CreateRaster(SKEncodedImageFormat.Png, 80, 40, SKColors.Blue));
            File.SetLastWriteTimeUtc(sourcePath, firstWrite.AddSeconds(2));
            var changed = await service.GetOrCreateAsync(sourcePath, 96);
            NotNull(changed);
            NotEqual(first!.CachePath, changed!.CachePath);

            var thirdPath = Path.Combine(root, "third.png");
            await File.WriteAllBytesAsync(thirdPath, CreateRaster(SKEncodedImageFormat.Png, 40, 80, SKColors.Green));
            File.SetLastWriteTimeUtc(thirdPath, firstWrite.AddSeconds(4));
            NotNull(await service.GetOrCreateAsync(thirdPath, 96));
            Equal(2, Directory.GetFiles(cache, "*.png").Length);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task UnsafeInputsDowngradeAndDisposeAllowsInFlightWorkAsync()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var service = new StaticImageThumbnailService(Path.Combine(root, "cache"));
            var invalidPath = Path.Combine(root, "invalid.png");
            await File.WriteAllTextAsync(invalidPath, "not an image");
            Null(await service.GetOrCreateAsync(invalidPath, 128));

            var svgPath = Path.Combine(root, "external.svg");
            await File.WriteAllTextAsync(svgPath, "<svg><image href=\"https://invalid.example/image.png\"/></svg>");
            Null(await service.GetOrCreateAsync(svgPath, 128));

            var gifPath = Path.Combine(root, "single-frame.gif");
            await File.WriteAllBytesAsync(
                gifPath,
                Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///ywAAAAAAQABAAACAUwAOw=="));
            var gifThumbnail = await service.GetOrCreateAsync(gifPath, 128);
            if (OperatingSystem.IsWindows())
            {
                NotNull(gifThumbnail);
            }
            else
            {
                Null(gifThumbnail);
            }

            var apngPath = Path.Combine(root, "animated.png");
            await File.WriteAllBytesAsync(apngPath, CreateAnimatedPng());
            Null(await service.GetOrCreateAsync(apngPath, 128));

            var webpPath = Path.Combine(root, "animated.webp");
            await File.WriteAllBytesAsync(webpPath, CreateAnimatedWebp());
            Null(await service.GetOrCreateAsync(webpPath, 128));

            var hugeBmpPath = Path.Combine(root, "huge.bmp");
            var hugeBmp = CreateHugeBmpHeader();
            using (var codec = SKCodec.Create(new MemoryStream(hugeBmp, writable: false)))
            {
                NotNull(codec);
                True((long)codec!.Info.Width * codec.Info.Height > StaticImageThumbnailService.MaximumSourcePixels);
            }
            await File.WriteAllBytesAsync(hugeBmpPath, hugeBmp);
            Null(await service.GetOrCreateAsync(hugeBmpPath, 128));

            var validPath = Path.Combine(root, "in-flight.png");
            await File.WriteAllBytesAsync(validPath, CreateRaster(SKEncodedImageFormat.Png, 1_024, 768, SKColors.Gold));
            var inFlight = service.GetOrCreateAsync(validPath, 512);
            service.Dispose();
            NotNull(await inFlight);
            await ThrowsAsync<ObjectDisposedException>(() => service.GetOrCreateAsync(validPath, 128));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task WindowsShellThumbnailsReleaseGdiHandlesAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateTemporaryRoot();
        try
        {
            var gifBytes = Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///ywAAAAAAQABAAACAUwAOw==");
            using var service = new StaticImageThumbnailService(Path.Combine(root, "cache"));
            var warmupPath = Path.Combine(root, "warmup.gif");
            await File.WriteAllBytesAsync(warmupPath, gifBytes);
            NotNull(await service.GetOrCreateAsync(warmupPath, 128));

            var processHandle = Process.GetCurrentProcess().Handle;
            var before = GetGuiResources(processHandle, 0);
            for (var index = 0; index < 24; index++)
            {
                var sourcePath = Path.Combine(root, $"shell-{index}.gif");
                await File.WriteAllBytesAsync(sourcePath, gifBytes);
                NotNull(await service.GetOrCreateAsync(sourcePath, 128));
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var after = GetGuiResources(processHandle, 0);
            if (before != 0 && after != 0)
            {
                True(after <= before + 2);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(nint process, uint flags);

    private static byte[] CreateRaster(SKEncodedImageFormat format, int width, int height, SKColor color)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(format, 92);
        return encoded?.ToArray() ?? throw new InvalidOperationException($"Could not encode {format} test input.");
    }

    private static byte[] CreateAnimatedPng()
    {
        var png = CreateRaster(SKEncodedImageFormat.Png, 4, 4, SKColors.Purple);
        var ihdrEnd = 8 + 12 + checked((int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(8, 4)));
        using var result = new MemoryStream();
        result.Write(png, 0, ihdrEnd);
        Span<byte> animationControl = stackalloc byte[20];
        BinaryPrimitives.WriteUInt32BigEndian(animationControl[..4], 8);
        "acTL"u8.CopyTo(animationControl.Slice(4, 4));
        BinaryPrimitives.WriteUInt32BigEndian(animationControl.Slice(8, 4), 2);
        result.Write(animationControl);
        result.Write(png, ihdrEnd, png.Length - ihdrEnd);
        return result.ToArray();
    }

    private static byte[] CreateAnimatedWebp()
    {
        var extendedHeader = new byte[10];
        extendedHeader[0] = 0x02;
        using var body = new MemoryStream();
        body.Write("WEBP"u8);
        WriteWebpChunk(body, "VP8X", extendedHeader);
        WriteWebpChunk(body, "ANIM", new byte[6]);
        var riffBody = body.ToArray();
        using var result = new MemoryStream();
        result.Write("RIFF"u8);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(length, checked((uint)riffBody.Length));
        result.Write(length);
        result.Write(riffBody);
        return result.ToArray();
    }

    private static void WriteWebpChunk(Stream destination, string name, byte[] payload)
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

    private static byte[] CreateHugeBmpHeader()
    {
        var bytes = new byte[54];
        "BM"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(2, 4), checked((uint)bytes.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(10, 4), 54);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(14, 4), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18, 4), 10_000);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22, 4), 10_000);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(26, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(28, 2), 24);
        return bytes;
    }

    private static string CreateTemporaryRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ial-thumbnails-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task ThrowsAsync<TException>(Func<Task> action) where TException : Exception
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

    private static void Null(object? value) => True(value is null);

    private static void NotNull(object? value) => True(value is not null);

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', but was '{actual}'.");
        }
    }

    private static void NotEqual<T>(T unexpected, T actual)
    {
        if (EqualityComparer<T>.Default.Equals(unexpected, actual))
        {
            throw new InvalidOperationException($"Did not expect '{actual}'.");
        }
    }
}
