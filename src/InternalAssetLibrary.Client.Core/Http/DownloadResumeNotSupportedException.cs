namespace InternalAssetLibrary.Client.Core.Http;

public sealed class DownloadResumeNotSupportedException : IOException
{
    public DownloadResumeNotSupportedException() : base("The update server did not honor the requested resume offset.") { }
}
