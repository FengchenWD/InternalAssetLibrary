namespace InternalAssetLibrary.Client.Core.Platform;

public enum PlayerFileKind
{
    Audio,
    Video,
    Image
}

public sealed record PlayerFileAssociation(
    string Extension,
    PlayerFileKind Kind,
    string ProgId,
    string FriendlyTypeName);

public static class DefaultPlayerFileAssociations
{
    public const string AudioProgId = "FengchenWD.InternalAssetLibrary.Audio";
    public const string VideoProgId = "FengchenWD.InternalAssetLibrary.Video";
    public const string ImageProgId = "FengchenWD.InternalAssetLibrary.Image";

    public static IReadOnlyList<PlayerFileAssociation> All { get; } =
    [
        Audio(".mp3"),
        Audio(".aac"),
        Audio(".ogg"),
        Audio(".m4a"),
        Audio(".flac"),
        Audio(".ape"),
        Audio(".wav"),
        Audio(".alac"),
        Video(".mp4"),
        Video(".mkv"),
        Video(".mov"),
        Video(".avi"),
        Video(".wmv"),
        Video(".flv"),
        Video(".webm"),
        Video(".m3u8"),
        Image(".jpg"),
        Image(".jpeg"),
        Image(".png"),
        Image(".gif"),
        Image(".webp"),
        Image(".bmp"),
        Image(".tiff"),
        Image(".tif"),
        Image(".svg")
    ];

    private static readonly HashSet<string> SupportedExtensions =
        All.Select(association => association.Extension)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static bool SupportsPath(string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        SupportedExtensions.Contains(Path.GetExtension(path));

    private static PlayerFileAssociation Audio(string extension) =>
        new(extension, PlayerFileKind.Audio, AudioProgId, "内部共享素材库音频");

    private static PlayerFileAssociation Video(string extension) =>
        new(extension, PlayerFileKind.Video, VideoProgId, "内部共享素材库视频");

    private static PlayerFileAssociation Image(string extension) =>
        new(extension, PlayerFileKind.Image, ImageProgId, "内部共享素材库图片");
}
