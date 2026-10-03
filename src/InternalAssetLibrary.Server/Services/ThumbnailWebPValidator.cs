using System.Buffers.Binary;
using InternalAssetLibrary.Server.Security;

namespace InternalAssetLibrary.Server.Services;

internal static class ThumbnailWebPValidator
{
    public static (int Width, int Height) Validate(ReadOnlySpan<byte> bytes, int maximumEdge = 640)
    {
        if (maximumEdge <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEdge));
        }

        if (bytes.Length < 20 ||
            !bytes[..4].SequenceEqual("RIFF"u8) ||
            !bytes.Slice(8, 4).SequenceEqual("WEBP"u8) ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(4, 4)) != bytes.Length - 8)
        {
            throw InvalidThumbnail("缩略图必须是结构有效的 WebP 文件。");
        }

        int? extendedWidth = null;
        int? extendedHeight = null;
        int? imageWidth = null;
        int? imageHeight = null;
        var imageChunks = 0;
        var offset = 12;
        while (offset <= bytes.Length - 8)
        {
            var type = bytes.Slice(offset, 4);
            var chunkLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + 4, 4));
            if (chunkLength > int.MaxValue || offset + 8L + chunkLength > bytes.Length)
            {
                throw InvalidThumbnail("WebP 缩略图结构无效。");
            }

            var data = bytes.Slice(offset + 8, (int)chunkLength);
            if (type.SequenceEqual("ANIM"u8) || type.SequenceEqual("ANMF"u8))
            {
                throw InvalidThumbnail("缩略图必须是静态 WebP 图片。");
            }

            if (type.SequenceEqual("VP8X"u8))
            {
                if (data.Length != 10 || extendedWidth is not null || (data[0] & 0x02) != 0)
                {
                    throw InvalidThumbnail("WebP 缩略图扩展头无效。");
                }

                extendedWidth = 1 + data[4] + (data[5] << 8) + (data[6] << 16);
                extendedHeight = 1 + data[7] + (data[8] << 8) + (data[9] << 16);
            }
            else if (type.SequenceEqual("VP8 "u8))
            {
                if (imageChunks != 0 || data.Length < 10 ||
                    !data.Slice(3, 3).SequenceEqual(new byte[] { 0x9D, 0x01, 0x2A }))
                {
                    throw InvalidThumbnail("WebP 缩略图图像数据无效。");
                }

                imageChunks++;
                imageWidth = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(6, 2)) & 0x3FFF;
                imageHeight = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(8, 2)) & 0x3FFF;
            }
            else if (type.SequenceEqual("VP8L"u8))
            {
                if (imageChunks != 0 || data.Length < 5 || data[0] != 0x2F)
                {
                    throw InvalidThumbnail("WebP 缩略图图像数据无效。");
                }

                imageChunks++;
                var bits = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(1, 4));
                imageWidth = (int)(bits & 0x3FFF) + 1;
                imageHeight = (int)((bits >> 14) & 0x3FFF) + 1;
            }

            offset = checked(offset + 8 + (int)chunkLength + ((int)chunkLength & 1));
        }

        if (offset != bytes.Length || imageChunks != 1 || imageWidth is null || imageHeight is null ||
            imageWidth <= 0 || imageHeight <= 0 ||
            imageWidth > maximumEdge || imageHeight > maximumEdge ||
            extendedWidth is not null && (extendedWidth != imageWidth || extendedHeight != imageHeight))
        {
            throw InvalidThumbnail($"缩略图最长边不能超过 {maximumEdge} 像素。");
        }

        return (imageWidth.Value, imageHeight.Value);
    }

    private static ApiException InvalidThumbnail(string message) => new(
        StatusCodes.Status415UnsupportedMediaType,
        "invalid_thumbnail",
        message);
}
