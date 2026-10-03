namespace InternalAssetLibrary.Client.Services;

internal static class AppPaths
{
    private static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FengchenWD",
        "InternalAssetLibrary");

    public static string RootDirectory => Root;

    public static string CatalogFile => Path.Combine(Root, "local-assets.json");

    public static string SettingsFile => Path.Combine(Root, "settings.json");

    public static string DownloadRegistryFile => Path.Combine(Root, "downloads.json");

    public static string DirectUploadResumeFile => Path.Combine(Root, "direct-uploads.json");
    public static string TransferTasksFile => Path.Combine(Root, "transfer-tasks.json");
    public static string AccountTransfersDirectory => Path.Combine(Root, "account-transfers");

    public static string LocalMarkersFile => Path.Combine(Root, "local-markers.json");

    public static string LocalLutLibraryFile => Path.Combine(Root, "local-luts.json");

    public static string ThumbnailCacheDirectory => Path.Combine(Root, "thumbnail-cache");

    public static string CloudThumbnailSourceDirectory => Path.Combine(Root, "cloud-thumbnail-sources");

    public static string CloudDerivativeCacheDirectory => Path.Combine(Root, "cloud-derivatives");

    public static string MediaDerivativeCacheDirectory => Path.Combine(Root, "media-derivatives");

    public static string CloudLutDownloadDirectory => Path.Combine(Root, "cloud-luts");

    public static string DiagnosticsDirectory => Path.Combine(Root, "diagnostics");

    public static string EditorDirectory => Path.Combine(Root, "editor");

    public static string EditorCacheDirectory => Path.Combine(EditorDirectory, "cache");

    public static string UpdateDownloadDirectory => Path.Combine(Root, "updates");

    public static string UpdateTransactionsDirectory => Path.Combine(UpdateDownloadDirectory, "transactions");

    public static string InstalledUpdateStateFile => Path.Combine(UpdateDownloadDirectory, "installed-update.json");

    public static IReadOnlyCollection<string> ManagedUserDataPaths { get; } =
    [
        CatalogFile,
        CatalogFile + ".tmp",
        SettingsFile,
        SettingsFile + ".tmp",
        DownloadRegistryFile,
        DownloadRegistryFile + ".tmp",
        DirectUploadResumeFile,
        DirectUploadResumeFile + ".tmp",
        TransferTasksFile,
        TransferTasksFile + ".tmp",
        AccountTransfersDirectory,
        LocalMarkersFile,
        LocalMarkersFile + ".tmp",
        LocalLutLibraryFile,
        LocalLutLibraryFile + ".tmp",
        ThumbnailCacheDirectory,
        CloudThumbnailSourceDirectory,
        CloudDerivativeCacheDirectory,
        MediaDerivativeCacheDirectory,
        CloudLutDownloadDirectory,
        UpdateDownloadDirectory,
        DiagnosticsDirectory,
        EditorDirectory
    ];

    public static string RuntimeDirectory => Path.Combine(AppContext.BaseDirectory, "runtime");

    public static string LibMpvPath => Path.Combine(RuntimeDirectory, "libmpv-2.dll");

    public static string FfmpegPath => Path.Combine(RuntimeDirectory, "ffmpeg.exe");

    public static string FfprobePath => Path.Combine(RuntimeDirectory, "ffprobe.exe");
}
