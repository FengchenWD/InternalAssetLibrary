using System.Security.Cryptography;
using InternalAssetLibrary.Client.Core.Http;
using InternalAssetLibrary.Contracts;
using InternalAssetLibrary.Core;

namespace InternalAssetLibrary.Client.Core.Updates;

public sealed record ClientUpdateCheckResult(
    string CurrentVersion,
    ClientReleaseInfo? Release,
    bool IsUpdateAvailable,
    bool IsCurrentVersionSupported);

public sealed record ClientUpdateDownloadProgress(long BytesDownloaded, long TotalBytes)
{
    public double Fraction => TotalBytes <= 0
        ? 0
        : Math.Clamp((double)BytesDownloaded / TotalBytes, 0, 1);

    public int Percentage => (int)Math.Floor(Fraction * 100);
}

public sealed class ClientUpdateService
{
    private readonly AssetLibraryApiClient _api;
    private readonly string _currentVersionText;
    private readonly SemanticVersion _currentVersion;

    public ClientUpdateService(AssetLibraryApiClient api, string currentVersion)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _currentVersionText = currentVersion?.Trim() ?? string.Empty;
        _currentVersion = SemanticVersion.Parse(_currentVersionText);
    }

    public async Task<ClientUpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var release = await _api.GetLatestClientReleaseAsync(cancellationToken).ConfigureAwait(false);
        if (release is null)
        {
            return new ClientUpdateCheckResult(_currentVersionText, null, false, true);
        }

        var latest = SemanticVersion.Parse(release.Version);
        ValidateRelease(release);
        var minimum = SemanticVersion.Parse(release.MinimumCompatibleVersion);
        if (latest.CompareTo(minimum) < 0)
        {
            throw new InvalidDataException("The update manifest has a latest version below its minimum compatible version.");
        }

        return new ClientUpdateCheckResult(
            _currentVersionText,
            release,
            latest.CompareTo(_currentVersion) > 0,
            _currentVersion.CompareTo(minimum) >= 0);
    }

    public Task<string> DownloadVerifiedInstallerAsync(
        ClientReleaseInfo release,
        string downloadDirectory,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default) =>
        DownloadVerifiedInstallerCoreAsync(
            release,
            downloadDirectory,
            progress,
            detailedProgress: null,
            cancellationToken);

    public Task<string> DownloadVerifiedInstallerWithProgressAsync(
        ClientReleaseInfo release,
        string downloadDirectory,
        IProgress<ClientUpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        DownloadVerifiedInstallerCoreAsync(
            release,
            downloadDirectory,
            byteProgress: null,
            progress,
            cancellationToken);

    private async Task<string> DownloadVerifiedInstallerCoreAsync(
        ClientReleaseInfo release,
        string downloadDirectory,
        IProgress<long>? byteProgress,
        IProgress<ClientUpdateDownloadProgress>? detailedProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentException.ThrowIfNullOrWhiteSpace(downloadDirectory);
        ValidateRelease(release);

        var directory = Path.GetFullPath(downloadDirectory);
        Directory.CreateDirectory(directory);
        var destinationPath = Path.Combine(directory, release.InstallerFileName);
        var temporaryPath = destinationPath + "." + release.InstallerSha256[..16] + ".part";
        if (File.Exists(temporaryPath) && new FileInfo(temporaryPath).Length > release.InstallerSizeBytes) File.Delete(temporaryPath);
        try
        {
            var offset = File.Exists(temporaryPath) ? new FileInfo(temporaryPath).Length : 0;
            detailedProgress?.Report(new ClientUpdateDownloadProgress(offset, release.InstallerSizeBytes));
            if (offset < release.InstallerSizeBytes)
            {
            await using (var file = new FileStream(
                temporaryPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var reporting = new ProgressWriteStream(
                file,
                release.InstallerSizeBytes,
                byteProgress,
                detailedProgress, offset))
            {
                try { await _api.DownloadLatestClientInstallerRangeAsync(release, offset, reporting, cancellationToken).ConfigureAwait(false); }
                catch (DownloadResumeNotSupportedException)
                {
                    file.SetLength(0); file.Position = 0;
                    await using var restarted = new ProgressWriteStream(file, release.InstallerSizeBytes, byteProgress, detailedProgress);
                    await _api.DownloadLatestClientInstallerAsync(release, restarted, cancellationToken).ConfigureAwait(false);
                }
                await reporting.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            }

            var info = new FileInfo(temporaryPath);
            if (info.Length != release.InstallerSizeBytes)
            {
                throw new InvalidDataException("Downloaded installer size does not match the release manifest.");
            }

            string hash;
            await using (var input = new FileStream(
                temporaryPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                hash = Convert.ToHexString(
                    await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false));
            }

            if (!hash.Equals(release.InstallerSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Downloaded installer SHA-256 does not match the release manifest.");
            }

            File.Move(temporaryPath, destinationPath, overwrite: true);
            return destinationPath;
        }
        catch (InvalidDataException)
        {
            File.Delete(temporaryPath);
            throw;
        }
    }

    private static void ValidateRelease(ClientReleaseInfo release)
    {
        _ = SemanticVersion.Parse(release.Version);
        _ = SemanticVersion.Parse(release.MinimumCompatibleVersion);
        if ((SemanticVersion.Parse(release.Version).CompareTo(SemanticVersion.Parse("1.1.1")) >= 0 || release.Signature is not null) && !ClientReleaseSignature.Verify(release))
            throw new InvalidDataException("更新清单签名无效，已停止安装。 Update manifest signature verification failed.");
        if (Path.GetFileName(release.InstallerFileName) != release.InstallerFileName ||
            !release.InstallerFileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            release.InstallerSizeBytes <= 0 ||
            release.InstallerSha256.Length != 64 ||
            !release.InstallerSha256.All(char.IsAsciiHexDigit))
        {
            throw new InvalidDataException("The client release metadata is invalid.");
        }
    }

    private sealed class ProgressWriteStream(
        Stream destination,
        long totalBytes,
        IProgress<long>? byteProgress,
        IProgress<ClientUpdateDownloadProgress>? detailedProgress, long initialOffset = 0) : Stream
    {
        private long _written = initialOffset;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => destination.CanWrite;
        public override long Length => destination.Length;
        public override long Position { get => destination.Position; set => throw new NotSupportedException(); }
        public override void Flush() => destination.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => destination.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => destination.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count)
        {
            destination.Write(buffer, offset, count);
            Report(count);
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await destination.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            Report(buffer.Length);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                destination.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await destination.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }

        private void Report(int count)
        {
            _written = checked(_written + count);
            byteProgress?.Report(_written);
            detailedProgress?.Report(new ClientUpdateDownloadProgress(_written, totalBytes));
        }
    }
}
