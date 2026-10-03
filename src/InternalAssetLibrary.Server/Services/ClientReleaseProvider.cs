using System.Text.Json;
using InternalAssetLibrary.Contracts;
using InternalAssetLibrary.Core;

namespace InternalAssetLibrary.Server.Services;

internal sealed class ClientReleaseProvider
{
    internal const string InstallerObjectPrefix = "client-updates";

    private readonly string _manifestPath;
    private readonly string _releaseRoot;
    private readonly string _minimumCompatibleVersion;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public ClientReleaseProvider(IHostEnvironment environment, IConfiguration configuration)
    {
        var configuredManifest = configuration["ClientUpdates:ManifestPath"] ?? "App_Data/updates/latest.json";
        _manifestPath = Path.GetFullPath(configuredManifest, environment.ContentRootPath);
        _releaseRoot = Path.GetDirectoryName(_manifestPath)!;
        var configuredMinimum = configuration["ClientUpdates:MinimumCompatibleVersion"]?.Trim();
        if (string.IsNullOrEmpty(configuredMinimum) || !SemanticVersion.TryParse(configuredMinimum, out _))
        {
            throw new InvalidOperationException(
                "ClientUpdates:MinimumCompatibleVersion must be a semantic version.");
        }

        _minimumCompatibleVersion = configuredMinimum;
    }

    public string MinimumCompatibleVersion() => _minimumCompatibleVersion;

    public async Task<ClientRelease?> TryGetLatestAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_manifestPath))
        {
            return null;
        }

        await using var stream = new FileStream(
            _manifestPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            32 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        ClientReleaseManifest manifest;
        try
        {
            manifest = await JsonSerializer.DeserializeAsync<ClientReleaseManifest>(
                stream,
                _jsonOptions,
                cancellationToken) ?? throw new InvalidDataException("Client release manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Client release manifest is invalid JSON.", exception);
        }

        ValidateManifest(manifest);
        var installerPath = ResolveLocalInstallerPath(manifest);
        var objectKey = NormalizeObjectKey(manifest);
        if (objectKey is null && installerPath is null)
        {
            throw new InvalidDataException("Client release installer is missing from the update directory.");
        }

        var info = new ClientReleaseInfo(
            manifest.Version,
            manifest.MinimumCompatibleVersion,
            manifest.PublishedAt,
            Path.GetFileName(manifest.InstallerFileName),
            manifest.InstallerSizeBytes,
            manifest.InstallerSha256.ToUpperInvariant(),
            "/api/client/releases/latest/download",
            string.IsNullOrWhiteSpace(manifest.ReleaseNotes) ? null : manifest.ReleaseNotes.Trim()) { Signature = manifest.Signature };
        if ((SemanticVersion.Parse(info.Version).CompareTo(SemanticVersion.Parse("1.1.1")) >= 0 || info.Signature is not null) && !ClientReleaseSignature.Verify(info))
            throw new InvalidDataException("客户端更新清单的 ECDSA 签名无效；未发布该更新。 ");
        return new ClientRelease(info,
            installerPath,
            objectKey);
    }

    internal static string BuildInstallerObjectKey(string version, string installerFileName) =>
        $"{InstallerObjectPrefix}/{version}/{installerFileName}";

    private string? ResolveLocalInstallerPath(ClientReleaseManifest manifest)
    {
        var installerPath = Path.GetFullPath(manifest.InstallerFileName, _releaseRoot);
        var releaseRootWithSeparator = Path.EndsInDirectorySeparator(_releaseRoot)
            ? _releaseRoot
            : _releaseRoot + Path.DirectorySeparatorChar;
        if (!installerPath.StartsWith(releaseRootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Client release installer is outside the update directory.");
        }

        if (!File.Exists(installerPath))
        {
            return null;
        }

        if (new FileInfo(installerPath).Length != manifest.InstallerSizeBytes)
        {
            throw new InvalidDataException("Client release installer size does not match the manifest.");
        }

        return installerPath;
    }

    private static string? NormalizeObjectKey(ClientReleaseManifest manifest)
    {
        var objectKey = manifest.InstallerObjectKey?.Trim();
        if (string.IsNullOrEmpty(objectKey))
        {
            return null;
        }

        var expected = BuildInstallerObjectKey(manifest.Version, manifest.InstallerFileName);
        if (!objectKey.Equals(expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Client release installerObjectKey must be '{expected}'.");
        }

        return objectKey;
    }

    private static void ValidateManifest(ClientReleaseManifest manifest)
    {
        if (!SemanticVersion.TryParse(manifest.Version, out var latest) ||
            !SemanticVersion.TryParse(manifest.MinimumCompatibleVersion, out var minimum) ||
            latest.CompareTo(minimum) < 0 ||
            manifest.PublishedAt == default ||
            string.IsNullOrWhiteSpace(manifest.InstallerFileName) ||
            Path.GetFileName(manifest.InstallerFileName) != manifest.InstallerFileName ||
            !manifest.InstallerFileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            manifest.InstallerSizeBytes <= 0 ||
            manifest.InstallerSha256.Length != 64 ||
            !manifest.InstallerSha256.All(char.IsAsciiHexDigit))
        {
            throw new InvalidDataException("Client release manifest contains invalid values.");
        }
    }

    private sealed class ClientReleaseManifest
    {
        public string Version { get; set; } = string.Empty;
        public string MinimumCompatibleVersion { get; set; } = string.Empty;
        public DateTimeOffset PublishedAt { get; set; }
        public string InstallerFileName { get; set; } = string.Empty;
        public long InstallerSizeBytes { get; set; }
        public string InstallerSha256 { get; set; } = string.Empty;
        public string? InstallerObjectKey { get; set; }
        public string? ReleaseNotes { get; set; }
        public string? Signature { get; set; }
    }
}

internal sealed record ClientRelease(
    ClientReleaseInfo Info,
    string? LocalInstallerPath,
    string? InstallerObjectKey);
