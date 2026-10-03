using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using SkiaSharp;

namespace InternalAssetLibrary.Client.Core.LocalAssets;

public sealed record StaticImageThumbnail(
    string CachePath,
    int PixelWidth,
    int PixelHeight,
    DateTimeOffset SourceLastWriteTimeUtc,
    bool FromCache);

public sealed class StaticImageThumbnailService : IDisposable
{
    public const int MaximumOutputEdge = 1_024;
    public const long MaximumSourceBytes = 256L * 1024 * 1024;
    public const long MaximumSourcePixels = 40_000_000;
    public const int DefaultMaximumCacheFiles = 2_048;
    public const long DefaultMaximumCacheBytes = 512L * 1024 * 1024;

    private const int CacheVersion = 2;
    private const int MaximumContainerChunks = 256;
    private static readonly HashSet<string> CandidateExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".bmp"
    };

    public static bool CanDecodeDirectly(string extension) =>
        !string.IsNullOrWhiteSpace(extension) &&
        CandidateExtensions.Contains(extension.StartsWith('.') ? extension : "." + extension);

    private readonly string _cacheDirectory;
    private readonly int _maximumCacheFiles;
    private readonly long _maximumCacheBytes;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public StaticImageThumbnailService(
        string cacheDirectory,
        int maximumCacheFiles = DefaultMaximumCacheFiles,
        long maximumCacheBytes = DefaultMaximumCacheBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        if (maximumCacheFiles <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCacheFiles));
        }

        if (maximumCacheBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCacheBytes));
        }

        _cacheDirectory = Path.GetFullPath(cacheDirectory);
        _maximumCacheFiles = maximumCacheFiles;
        _maximumCacheBytes = maximumCacheBytes;
        Directory.CreateDirectory(_cacheDirectory);
        PruneCache(protectedPath: null);
    }

    public async Task<StaticImageThumbnail?> GetOrCreateAsync(
        string sourcePath,
        int maximumEdge,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        if (maximumEdge is < 32 or > MaximumOutputEdge)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEdge));
        }

        var fullPath = Path.GetFullPath(sourcePath);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(
                () => TryGetOrCreateCore(fullPath, maximumEdge, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private StaticImageThumbnail? TryGetOrCreateCore(
        string sourcePath,
        int maximumEdge,
        CancellationToken cancellationToken)
    {
        try
        {
            return GetOrCreateCore(sourcePath, maximumEdge, cancellationToken);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private StaticImageThumbnail? GetOrCreateCore(
        string sourcePath,
        int maximumEdge,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = new FileInfo(sourcePath);
        if (!source.Exists || source.Length <= 0)
        {
            return null;
        }

        var sourceWriteTime = new DateTimeOffset(source.LastWriteTimeUtc, TimeSpan.Zero);
        var cachePath = Path.Combine(
            _cacheDirectory,
            CreateCacheKey(source.FullName, sourceWriteTime, source.Length, maximumEdge) + ".png");
        var cached = ReadCachedThumbnail(cachePath, sourceWriteTime, fromCache: true);
        if (cached is not null)
        {
            TouchCacheFile(cachePath);
            return cached;
        }

        var generated = GenerateThumbnail(source, maximumEdge, cancellationToken);
        if (generated is null || generated.PngBytes.LongLength > _maximumCacheBytes)
        {
            return null;
        }

        source.Refresh();
        if (!source.Exists || source.Length != generated.SourceLength || source.LastWriteTimeUtc != sourceWriteTime.UtcDateTime)
        {
            return null;
        }

        var temporaryPath = Path.Combine(_cacheDirectory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporaryPath, generated.PngBytes);
            try
            {
                File.Move(temporaryPath, cachePath, overwrite: false);
            }
            catch (IOException) when (File.Exists(cachePath))
            {
                File.Delete(temporaryPath);
            }

            TouchCacheFile(cachePath);
            PruneCache(cachePath);
            return new StaticImageThumbnail(
                cachePath,
                generated.Width,
                generated.Height,
                sourceWriteTime,
                FromCache: false);
        }
        catch (IOException)
        {
            TryDelete(temporaryPath);
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            TryDelete(temporaryPath);
            return null;
        }
    }

    private static GeneratedThumbnail? GenerateThumbnail(
        FileInfo source,
        int maximumEdge,
        CancellationToken cancellationToken)
    {
        if (!CandidateExtensions.Contains(source.Extension) || source.Length > MaximumSourceBytes)
        {
            return WindowsShellThumbnail.TryGenerate(source, maximumEdge, cancellationToken);
        }

        try
        {
            using var stream = new FileStream(
                source.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                64 * 1024,
                FileOptions.SequentialScan);
            if (HasAnimationOrUnsafeContainer(stream))
            {
                return null;
            }

            stream.Position = 0;
            using var codec = SKCodec.Create(stream);
            if (codec is null || codec.FrameCount > 1 || !IsSupportedFormat(codec.EncodedFormat))
            {
                return null;
            }

            var sourceInfo = codec.Info;
            if (sourceInfo.Width <= 0 || sourceInfo.Height <= 0 ||
                (long)sourceInfo.Width * sourceInfo.Height > MaximumSourcePixels)
            {
                return null;
            }

            var desiredScale = Math.Min(1f, maximumEdge / (float)Math.Max(sourceInfo.Width, sourceInfo.Height));
            var scaledSize = codec.GetScaledDimensions(desiredScale);
            if (scaledSize.Width <= 0 || scaledSize.Height <= 0 ||
                (long)scaledSize.Width * scaledSize.Height > MaximumSourcePixels)
            {
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var decodeInfo = new SKImageInfo(
                scaledSize.Width,
                scaledSize.Height,
                SKColorType.Rgba8888,
                SKAlphaType.Premul);
            using var decoded = new SKBitmap(decodeInfo);
            var decodeResult = codec.GetPixels(decodeInfo, decoded.GetPixels());
            if (decodeResult != SKCodecResult.Success)
            {
                return null;
            }

            var swapsAxes = SwapsAxes(codec.EncodedOrigin);
            var orientedWidth = swapsAxes ? decoded.Height : decoded.Width;
            var orientedHeight = swapsAxes ? decoded.Width : decoded.Height;
            var outputScale = Math.Min(1f, maximumEdge / (float)Math.Max(orientedWidth, orientedHeight));
            var outputWidth = Math.Max(1, (int)Math.Round(orientedWidth * outputScale));
            var outputHeight = Math.Max(1, (int)Math.Round(orientedHeight * outputScale));
            if (outputWidth > maximumEdge || outputHeight > maximumEdge)
            {
                return null;
            }

            var outputInfo = new SKImageInfo(
                outputWidth,
                outputHeight,
                SKColorType.Rgba8888,
                SKAlphaType.Premul);
            using var output = new SKBitmap(outputInfo);
            using (var canvas = new SKCanvas(output))
            using (var paint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High })
            {
                canvas.Clear(SKColors.Transparent);
                canvas.SetMatrix(CreateOrientationMatrix(
                    codec.EncodedOrigin,
                    outputScale,
                    outputWidth,
                    outputHeight));
                canvas.DrawBitmap(decoded, 0, 0, paint);
                canvas.Flush();
            }

            cancellationToken.ThrowIfCancellationRequested();
            using var image = SKImage.FromBitmap(output);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            return encoded is null
                ? null
                : new GeneratedThumbnail(encoded.ToArray(), outputWidth, outputHeight, source.Length);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static class WindowsShellThumbnail
    {
        private const uint DibRgbColors = 0;
        private const uint BiRgb = 0;

        public static GeneratedThumbnail? TryGenerate(
            FileInfo source,
            int maximumEdge,
            CancellationToken cancellationToken)
        {
            if (!OperatingSystem.IsWindows())
            {
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            IShellItemImageFactory? factory = null;
            nint bitmapHandle = 0;
            try
            {
                var interfaceId = typeof(IShellItemImageFactory).GUID;
                var result = SHCreateItemFromParsingName(source.FullName, 0, ref interfaceId, out factory);
                if (result < 0 || factory is null)
                {
                    return null;
                }

                result = factory.GetImage(
                    new NativeSize(maximumEdge, maximumEdge),
                    ShellImageFlags.ThumbnailOnly,
                    out bitmapHandle);
                if (result < 0 || bitmapHandle == 0)
                {
                    return null;
                }

                cancellationToken.ThrowIfCancellationRequested();
                return EncodeBitmap(bitmapHandle, source.Length, maximumEdge);
            }
            catch (COMException)
            {
                return null;
            }
            catch (ExternalException)
            {
                return null;
            }
            finally
            {
                if (bitmapHandle != 0)
                {
                    DeleteObject(bitmapHandle);
                }

                if (factory is not null && Marshal.IsComObject(factory))
                {
                    Marshal.FinalReleaseComObject(factory);
                }
            }
        }

        private static GeneratedThumbnail? EncodeBitmap(nint bitmapHandle, long sourceLength, int maximumEdge)
        {
            if (GetObject(bitmapHandle, Marshal.SizeOf<NativeBitmap>(), out var bitmap) == 0 ||
                bitmap.Width <= 0 || bitmap.Height <= 0 ||
                bitmap.Width > maximumEdge || bitmap.Height > maximumEdge)
            {
                return null;
            }

            var byteCount = checked(bitmap.Width * bitmap.Height * 4);
            var pixels = new byte[byteCount];
            var pinned = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            nint deviceContext = 0;
            try
            {
                var info = new BitmapInfo
                {
                    Header = new BitmapInfoHeader
                    {
                        Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                        Width = bitmap.Width,
                        Height = -bitmap.Height,
                        Planes = 1,
                        BitCount = 32,
                        Compression = BiRgb,
                        SizeImage = (uint)byteCount
                    }
                };
                deviceContext = GetDC(0);
                if (deviceContext == 0 || GetDIBits(
                        deviceContext,
                        bitmapHandle,
                        0,
                        (uint)bitmap.Height,
                        pinned.AddrOfPinnedObject(),
                        ref info,
                        DibRgbColors) != bitmap.Height)
                {
                    return null;
                }
            }
            finally
            {
                if (deviceContext != 0)
                {
                    ReleaseDC(0, deviceContext);
                }

                pinned.Free();
            }

            var hasAlpha = false;
            for (var index = 3; index < pixels.Length; index += 4)
            {
                hasAlpha |= pixels[index] != 0;
            }

            if (!hasAlpha)
            {
                for (var index = 3; index < pixels.Length; index += 4)
                {
                    pixels[index] = byte.MaxValue;
                }
            }

            using var output = new SKBitmap(new SKImageInfo(
                bitmap.Width,
                bitmap.Height,
                SKColorType.Bgra8888,
                SKAlphaType.Premul));
            Marshal.Copy(pixels, 0, output.GetPixels(), pixels.Length);
            using var image = SKImage.FromBitmap(output);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            return encoded is null
                ? null
                : new GeneratedThumbnail(encoded.ToArray(), bitmap.Width, bitmap.Height, sourceLength);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHCreateItemFromParsingName(
            string path,
            nint bindingContext,
            ref Guid interfaceId,
            [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? factory);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteObject(nint handle);

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetObject(nint handle, int size, out NativeBitmap bitmap);

        [DllImport("gdi32.dll")]
        private static extern int GetDIBits(
            nint deviceContext,
            nint bitmap,
            uint startScan,
            uint scanLines,
            nint bits,
            ref BitmapInfo bitmapInfo,
            uint usage);

        [DllImport("user32.dll")]
        private static extern nint GetDC(nint window);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(nint window, nint deviceContext);

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B")]
        private interface IShellItemImageFactory
        {
            [PreserveSig]
            int GetImage(NativeSize size, ShellImageFlags flags, out nint bitmapHandle);
        }

        [Flags]
        private enum ShellImageFlags : uint
        {
            ThumbnailOnly = 0x00000008
        }

        [StructLayout(LayoutKind.Sequential)]
        private readonly record struct NativeSize(int Width, int Height);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeBitmap
        {
            public int Type;
            public int Width;
            public int Height;
            public int WidthBytes;
            public ushort Planes;
            public ushort BitsPixel;
            public nint Bits;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfoHeader
        {
            public uint Size;
            public int Width;
            public int Height;
            public ushort Planes;
            public ushort BitCount;
            public uint Compression;
            public uint SizeImage;
            public int XPelsPerMeter;
            public int YPelsPerMeter;
            public uint ColorsUsed;
            public uint ColorsImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfo
        {
            public BitmapInfoHeader Header;
            public uint Colors;
        }
    }

    private static StaticImageThumbnail? ReadCachedThumbnail(
        string cachePath,
        DateTimeOffset sourceWriteTime,
        bool fromCache)
    {
        if (!File.Exists(cachePath))
        {
            return null;
        }

        try
        {
            int width;
            int height;
            using (var stream = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var codec = SKCodec.Create(stream))
            {
                if (codec is null || codec.EncodedFormat != SKEncodedImageFormat.Png ||
                    codec.FrameCount > 1 || codec.Info.Width is <= 0 or > MaximumOutputEdge ||
                    codec.Info.Height is <= 0 or > MaximumOutputEdge)
                {
                    width = 0;
                    height = 0;
                }
                else
                {
                    width = codec.Info.Width;
                    height = codec.Info.Height;
                }
            }

            if (width == 0 || height == 0)
            {
                TryDelete(cachePath);
                return null;
            }

            return new StaticImageThumbnail(
                cachePath,
                width,
                height,
                sourceWriteTime,
                fromCache);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsSupportedFormat(SKEncodedImageFormat format) => format is
        SKEncodedImageFormat.Jpeg or
        SKEncodedImageFormat.Png or
        SKEncodedImageFormat.Webp or
        SKEncodedImageFormat.Bmp;

    private static bool HasAnimationOrUnsafeContainer(Stream stream)
    {
        Span<byte> signature = stackalloc byte[12];
        if (!TryReadExactly(stream, signature))
        {
            return true;
        }

        stream.Position = 0;
        if (signature[..6].SequenceEqual("GIF87a"u8) || signature[..6].SequenceEqual("GIF89a"u8))
        {
            return true;
        }

        if (signature[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            return PngHasAnimationOrInvalidLayout(stream);
        }

        if (signature[..4].SequenceEqual("RIFF"u8) && signature.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return WebpHasAnimationOrInvalidLayout(stream);
        }

        return false;
    }

    private static bool PngHasAnimationOrInvalidLayout(Stream stream)
    {
        stream.Position = 8;
        Span<byte> header = stackalloc byte[8];
        for (var chunkIndex = 0; chunkIndex < MaximumContainerChunks; chunkIndex++)
        {
            if (!TryReadExactly(stream, header))
            {
                return true;
            }

            var length = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
            var type = header[4..8];
            if (type.SequenceEqual("acTL"u8) || type.SequenceEqual("fcTL"u8) || type.SequenceEqual("fdAT"u8))
            {
                return true;
            }

            if (type.SequenceEqual("IDAT"u8) || type.SequenceEqual("IEND"u8))
            {
                return false;
            }

            if (length > stream.Length - stream.Position - 4)
            {
                return true;
            }

            stream.Seek(length + 4L, SeekOrigin.Current);
        }

        return true;
    }

    private static bool WebpHasAnimationOrInvalidLayout(Stream stream)
    {
        Span<byte> riffHeader = stackalloc byte[12];
        stream.Position = 0;
        if (!TryReadExactly(stream, riffHeader))
        {
            return true;
        }

        var declaredLength = BinaryPrimitives.ReadUInt32LittleEndian(riffHeader.Slice(4, 4));
        if (declaredLength + 8L != stream.Length)
        {
            return true;
        }

        Span<byte> chunkHeader = stackalloc byte[8];
        for (var chunkIndex = 0; chunkIndex < MaximumContainerChunks && stream.Position < stream.Length; chunkIndex++)
        {
            if (!TryReadExactly(stream, chunkHeader))
            {
                return true;
            }

            var length = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader.Slice(4, 4));
            var type = chunkHeader[..4];
            if (length > stream.Length - stream.Position)
            {
                return true;
            }

            if (type.SequenceEqual("ANIM"u8) || type.SequenceEqual("ANMF"u8))
            {
                return true;
            }

            if (type.SequenceEqual("VP8X"u8) && length > 0)
            {
                var flags = stream.ReadByte();
                if (flags < 0 || (flags & 0x02) != 0)
                {
                    return true;
                }

                stream.Seek(length - 1L + (length & 1), SeekOrigin.Current);
            }
            else
            {
                stream.Seek(length + (length & 1), SeekOrigin.Current);
            }
        }

        return stream.Position != stream.Length;
    }

    private static bool TryReadExactly(Stream stream, Span<byte> destination)
    {
        var total = 0;
        while (total < destination.Length)
        {
            var count = stream.Read(destination[total..]);
            if (count == 0)
            {
                return false;
            }

            total += count;
        }

        return true;
    }

    private static bool SwapsAxes(SKEncodedOrigin origin) => origin is
        SKEncodedOrigin.LeftTop or
        SKEncodedOrigin.RightTop or
        SKEncodedOrigin.RightBottom or
        SKEncodedOrigin.LeftBottom;

    private static SKMatrix CreateOrientationMatrix(
        SKEncodedOrigin origin,
        float scale,
        int outputWidth,
        int outputHeight) => origin switch
        {
            SKEncodedOrigin.TopRight => Matrix(-scale, 0, outputWidth, 0, scale, 0),
            SKEncodedOrigin.BottomRight => Matrix(-scale, 0, outputWidth, 0, -scale, outputHeight),
            SKEncodedOrigin.BottomLeft => Matrix(scale, 0, 0, 0, -scale, outputHeight),
            SKEncodedOrigin.LeftTop => Matrix(0, scale, 0, scale, 0, 0),
            SKEncodedOrigin.RightTop => Matrix(0, -scale, outputWidth, scale, 0, 0),
            SKEncodedOrigin.RightBottom => Matrix(0, -scale, outputWidth, -scale, 0, outputHeight),
            SKEncodedOrigin.LeftBottom => Matrix(0, scale, 0, -scale, 0, outputHeight),
            _ => SKMatrix.CreateScale(scale, scale)
        };

    private static SKMatrix Matrix(
        float scaleX,
        float skewX,
        float transX,
        float skewY,
        float scaleY,
        float transY) => new()
        {
            ScaleX = scaleX,
            SkewX = skewX,
            TransX = transX,
            SkewY = skewY,
            ScaleY = scaleY,
            TransY = transY,
            Persp2 = 1
        };

    private static string CreateCacheKey(
        string fullPath,
        DateTimeOffset sourceWriteTime,
        long sourceLength,
        int maximumEdge)
    {
        var normalizedPath = OperatingSystem.IsWindows() ? fullPath.ToUpperInvariant() : fullPath;
        var value = $"{CacheVersion}\n{normalizedPath}\n{sourceWriteTime.UtcTicks}\n{sourceLength}\n{maximumEdge}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private void PruneCache(string? protectedPath)
    {
        try
        {
            var files = new DirectoryInfo(_cacheDirectory)
                .EnumerateFiles("*.png", SearchOption.TopDirectoryOnly)
                .OrderByDescending(file => PathEquals(file.FullName, protectedPath))
                .ThenByDescending(file => file.LastWriteTimeUtc)
                .ToArray();
            long retainedBytes = 0;
            var retainedFiles = 0;
            foreach (var file in files)
            {
                if (retainedFiles < _maximumCacheFiles && retainedBytes + file.Length <= _maximumCacheBytes)
                {
                    retainedFiles++;
                    retainedBytes += file.Length;
                    continue;
                }

                TryDelete(file.FullName);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static bool PathEquals(string left, string? right) => right is not null && string.Equals(
        left,
        right,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void TouchCacheFile(string path)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
    }

    private sealed record GeneratedThumbnail(byte[] PngBytes, int Width, int Height, long SourceLength);
}
