namespace InternalAssetLibrary.Client.Core.LocalAssets;

public static class MediaExtensionClassifier
{
    private static readonly Dictionary<string, LocalMediaType> ExtensionTypes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [".jpg"] = LocalMediaType.Image,
            [".jpeg"] = LocalMediaType.Image,
            [".png"] = LocalMediaType.Image,
            [".gif"] = LocalMediaType.Image,
            [".webp"] = LocalMediaType.Image,
            [".bmp"] = LocalMediaType.Image,
            [".tiff"] = LocalMediaType.Image,
            [".tif"] = LocalMediaType.Image,
            [".svg"] = LocalMediaType.Image,
            [".ai"] = LocalMediaType.Image,
            [".eps"] = LocalMediaType.Image,
            [".psd"] = LocalMediaType.Image,
            [".mp3"] = LocalMediaType.Audio,
            [".aac"] = LocalMediaType.Audio,
            [".ogg"] = LocalMediaType.Audio,
            [".m4a"] = LocalMediaType.Audio,
            [".flac"] = LocalMediaType.Audio,
            [".ape"] = LocalMediaType.Audio,
            [".wav"] = LocalMediaType.Audio,
            [".alac"] = LocalMediaType.Audio,
            [".caf"] = LocalMediaType.Audio,
            [".mp4"] = LocalMediaType.Video,
            [".mkv"] = LocalMediaType.Video,
            [".mov"] = LocalMediaType.Video,
            [".avi"] = LocalMediaType.Video,
            [".wmv"] = LocalMediaType.Video,
            [".flv"] = LocalMediaType.Video,
            [".webm"] = LocalMediaType.Video,
            [".m3u8"] = LocalMediaType.Video
        };

    public static IReadOnlyCollection<string> SupportedExtensions => ExtensionTypes.Keys;

    public static IReadOnlyCollection<string> VideoExtensions =>
        ExtensionTypes.Where(item => item.Value == LocalMediaType.Video)
            .Select(item => item.Key)
            .ToArray();

    public static IReadOnlyCollection<string> AudioExtensions =>
        ExtensionTypes.Where(item => item.Value == LocalMediaType.Audio)
            .Select(item => item.Key)
            .ToArray();

    public static bool TryClassify(string pathOrExtension, out LocalMediaType mediaType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pathOrExtension);

        var extension = GetExtension(pathOrExtension);

        return ExtensionTypes.TryGetValue(extension, out mediaType);
    }

    public static string NormalizeExtension(string pathOrExtension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pathOrExtension);
        var extension = GetExtension(pathOrExtension);

        return extension.ToLowerInvariant();
    }

    private static string GetExtension(string value)
    {
        var isExtensionOnly = value.StartsWith('.') &&
                              value.IndexOf('.', 1) < 0 &&
                              value.IndexOfAny([
                                  Path.DirectorySeparatorChar,
                                  Path.AltDirectorySeparatorChar
                              ]) < 0;
        return isExtensionOnly ? value : Path.GetExtension(value);
    }
}
