using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using InternalAssetLibrary.Client.Core.Http;
using InternalAssetLibrary.Client.Core.LocalAssets;
using InternalAssetLibrary.Client.Core.MediaAnalysis;
using InternalAssetLibrary.Contracts;
using InternalAssetLibrary.Server.Data;
using InternalAssetLibrary.Server.Security;
using InternalAssetLibrary.Server.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

internal static class MediaDerivativeSelfTests
{
    public static void CosEntityTagsDoNotReplaceTrustedDerivativeHashes() =>
        CosEntityTagsDoNotReplaceTrustedDerivativeHashesAsync().GetAwaiter().GetResult();

    public static void StaleDerivativeSnapshotsRefreshBeforeDownload() =>
        StaleDerivativeSnapshotsRefreshBeforeDownloadAsync().GetAwaiter().GetResult();

    public static void OldAssetsQueueVersionedDerivativeBackfill()
    {
        var now = new DateTimeOffset(2026, 8, 28, 8, 0, 0, TimeSpan.Zero);
        var asset = new AssetRecord
        {
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            CurrentVersionId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Name = "Old video",
            Category = AssetCategories.Video,
            OriginalFileName = "old.mp4",
            Extension = ".mp4",
            SizeBytes = 100,
            ContentHash = new string('A', 64),
            ObjectKey = "originals/old.mp4",
            HasOriginal = true,
            UploadedAt = now,
            UpdatedAt = now,
            Derivatives = new AssetDerivativesRecord()
        };

        True(AssetDerivativeRules.Normalize(asset, now));
        Equal(DerivativeState.Queued, asset.Derivatives.Thumbnail.State);
        True(asset.Derivatives.Thumbnail.ServerBackfillEligible);
        Equal(DerivativeState.Queued, asset.Derivatives.Proxy.State);
        False(asset.Derivatives.Proxy.ServerBackfillEligible);

        var key = AssetDerivativeRules.BuildObjectKey(
            asset,
            AssetDerivativeKind.Thumbnail,
            Guid.Parse("33333333-3333-3333-3333-333333333333"));
        True(key.Contains(asset.Id.ToString("N"), StringComparison.Ordinal));
        True(key.Contains(asset.CurrentVersionId.ToString("N"), StringComparison.Ordinal));
        True(key.EndsWith(".webp", StringComparison.Ordinal));

        var nextVersion = Guid.NewGuid();
        asset.CurrentVersionId = nextVersion;
        AssetDerivativeRules.ResetForCurrentVersion(asset, now.AddMinutes(1), serverThumbnailBackfill: false);
        Equal(nextVersion, asset.Derivatives.Thumbnail.AssetVersionId);
        False(asset.Derivatives.Thumbnail.ServerBackfillEligible);
        Equal(0, AssetDerivativeRules.StoredObjectKeys(asset).Count());

        var oldAudioThumbnailKey = "derivatives/audio/thumbnail-v1-album-art.webp";
        var audio = new AssetRecord
        {
            Id = Guid.Parse("44444444-4444-4444-4444-444444444444"),
            CurrentVersionId = Guid.Parse("55555555-5555-5555-5555-555555555555"),
            Name = "Old BGM",
            Category = AssetCategories.Bgm,
            OriginalFileName = "old.mp3",
            Extension = ".mp3",
            SizeBytes = 100,
            ContentHash = new string('B', 64),
            ObjectKey = "originals/old.mp3",
            HasOriginal = true,
            UploadedAt = now,
            UpdatedAt = now,
            Derivatives = ReadyDerivatives(
                Guid.Parse("55555555-5555-5555-5555-555555555555"),
                oldAudioThumbnailKey,
                now)
        };

        True(AssetDerivativeRules.Normalize(audio, now.AddMinutes(2)));
        Equal(AssetDerivativeRules.AudioWaveformThumbnailFormatVersion, audio.Derivatives.Thumbnail.FormatVersion);
        Equal(DerivativeState.Queued, audio.Derivatives.Thumbnail.State);
        True(audio.Derivatives.Thumbnail.ServerBackfillEligible);
        Equal(oldAudioThumbnailKey, audio.Derivatives.Thumbnail.ObjectKey);
        True(AssetDerivativeRules.StoredObjectKeys(audio).Contains(oldAudioThumbnailKey, StringComparer.Ordinal));
        True(AssetDerivativeRules.BuildObjectKey(
                audio,
                AssetDerivativeKind.Thumbnail,
                Guid.Parse("66666666-6666-6666-6666-666666666666"))
            .Contains("thumbnail-v4-", StringComparison.Ordinal));

        var visual = new AssetRecord
        {
            Id = Guid.Parse("77777777-7777-7777-7777-777777777777"),
            CurrentVersionId = Guid.Parse("88888888-8888-8888-8888-888888888888"),
            Name = "Current video",
            Category = AssetCategories.Video,
            OriginalFileName = "current.mp4",
            Extension = ".mp4",
            SizeBytes = 100,
            ContentHash = new string('C', 64),
            ObjectKey = "originals/current.mp4",
            HasOriginal = true,
            UploadedAt = now,
            UpdatedAt = now,
            Derivatives = ReadyDerivatives(
                Guid.Parse("88888888-8888-8888-8888-888888888888"),
                "derivatives/video/thumbnail-v1-frame.webp",
                now)
        };

        False(AssetDerivativeRules.Normalize(visual, now.AddMinutes(2)));
        Equal(AssetDerivativeRules.ThumbnailFormatVersion, visual.Derivatives.Thumbnail.FormatVersion);
        Equal(DerivativeState.Ready, visual.Derivatives.Thumbnail.State);
        True(AssetDerivativeRules.BuildObjectKey(
                visual,
                AssetDerivativeKind.Thumbnail,
                Guid.Parse("99999999-9999-9999-9999-999999999999"))
            .Contains("thumbnail-v1-", StringComparison.Ordinal));

        True(CloudMediaDerivativeService.UsesWaveformThumbnail(ApiAssetCategory.Bgm));
        True(CloudMediaDerivativeService.UsesWaveformThumbnail(ApiAssetCategory.SoundEffect));
        False(CloudMediaDerivativeService.UsesWaveformThumbnail(ApiAssetCategory.Video));
        False(CloudMediaDerivativeService.UsesWaveformThumbnail(ApiAssetCategory.Image));

        var audioArguments = DerivativeBackfillService.BuildThumbnailArguments(
            "source.mp3",
            "thumbnail.webp",
            AssetCategories.SoundEffect,
            640);
        var serverWaveformFilter = audioArguments.Single(argument =>
            argument.Contains("showwavespic", StringComparison.Ordinal));
        Contains("showwavespic=s=640x213", serverWaveformFilter);
        Contains("volume=-12dB", serverWaveformFilter);
        Contains("scale=sqrt", serverWaveformFilter);
        Contains("filter=average", serverWaveformFilter);
        var clientWaveformFilter = FfmpegCommandBuilder.BuildWaveform(
                "source.mp3",
                "thumbnail.webp",
                width: 640,
                height: 213)
            .Arguments.Single(argument => argument.Contains("showwavespic", StringComparison.Ordinal));
        Equal(clientWaveformFilter, serverWaveformFilter);
        True(audioArguments.Contains("[wave]", StringComparer.Ordinal));
        False(audioArguments.Contains("0:v:0?", StringComparer.Ordinal));

        var videoArguments = DerivativeBackfillService.BuildThumbnailArguments(
            "source.mp4",
            "thumbnail.webp",
            AssetCategories.Video,
            640);
        True(videoArguments.Contains("0:v:0?", StringComparer.Ordinal));
        False(videoArguments.Any(argument => argument.Contains("showwavespic", StringComparison.Ordinal)));
    }

    private static async Task CosEntityTagsDoNotReplaceTrustedDerivativeHashesAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ial-derivative-cos-etag-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var expectedBytes = Encoding.UTF8.GetBytes("verified proxy bytes");
            var damagedBytes = Encoding.UTF8.GetBytes("corrupted proxy byte");
            Equal(expectedBytes.Length, damagedBytes.Length);
            var trustedSha256 = Convert.ToHexString(SHA256.HashData(expectedBytes));
            var cosEntityTag = Convert.ToHexString(MD5.HashData(expectedBytes));
            var call = 0;
            using var http = new HttpClient(new StubHandler((_, _) =>
            {
                call++;
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(call == 1 ? expectedBytes : damagedBytes)
                };
                response.Headers.ETag = new EntityTagHeaderValue($"\"{cosEntityTag}\"");
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/mp4");
                return Task.FromResult(response);
            }));
            using var staticImages = new StaticImageThumbnailService(Path.Combine(root, "static"));
            var thumbnails = new MediaThumbnailService(
                Path.Combine(root, "thumbnails"),
                "missing-ffmpeg",
                staticImages);
            var service = new CloudMediaDerivativeService(
                new AssetLibraryApiClient(http, new Uri("https://library.example/")),
                thumbnails,
                Path.Combine(root, "cloud"),
                "missing-ffmpeg");
            var first = CloudAudioAsset(
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                trustedSha256,
                expectedBytes.LongLength);

            var cached = await service.GetCachedDerivativeAsync(first, AssetDerivativeKind.Proxy);

            True(cached is not null && File.Exists(cached));
            True(cached!.StartsWith(Path.Combine(root, "cloud"), StringComparison.OrdinalIgnoreCase));
            SequenceEqual(expectedBytes, await File.ReadAllBytesAsync(cached));

            var damaged = CloudAudioAsset(
                Guid.Parse("22222222-2222-2222-2222-222222222222"),
                trustedSha256,
                expectedBytes.LongLength);
            try
            {
                _ = await service.GetCachedDerivativeAsync(damaged, AssetDerivativeKind.Proxy);
            }
            catch (InvalidDataException exception)
            {
                Contains("trusted server metadata", exception.Message);
                Equal(2, call);
                return;
            }

            throw new InvalidOperationException("Expected corrupted derivative content to fail SHA-256 validation.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task StaleDerivativeSnapshotsRefreshBeforeDownloadAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ial-stale-derivative-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var currentVersionId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
            var staleVersionId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
            var updatedAt = new DateTimeOffset(2026, 8, 31, 0, 0, 0, TimeSpan.Zero);
            var cases = new[]
            {
                (State: AssetDerivativeState.Queued, VersionId: currentVersionId, SizeBytes: 1L, ETag: new string('1', 64)),
                (State: AssetDerivativeState.Ready, VersionId: staleVersionId, SizeBytes: 1L, ETag: new string('2', 64)),
                (State: AssetDerivativeState.Ready, VersionId: currentVersionId, SizeBytes: 0L, ETag: (string?)null)
            };

            for (var index = 0; index < cases.Length; index++)
            {
                var assetId = Guid.NewGuid();
                var expectedBytes = Encoding.UTF8.GetBytes($"waveform-{index}");
                var trustedSha256 = Convert.ToHexString(SHA256.HashData(expectedBytes));
                var readyThumbnail = new AssetDerivativeInfo(
                    AssetDerivativeKind.Thumbnail,
                    currentVersionId,
                    AssetDerivativeState.Ready,
                    AssetDerivativeRules.AudioWaveformThumbnailFormatVersion,
                    $"/api/assets/{assetId:D}/derivatives/thumbnail",
                    "image/webp",
                    expectedBytes.LongLength,
                    trustedSha256,
                    null,
                    null,
                    0,
                    updatedAt);
                var unavailableProxy = new AssetDerivativeInfo(
                    AssetDerivativeKind.Proxy,
                    currentVersionId,
                    AssetDerivativeState.Unavailable,
                    AssetDerivativeRules.ProxyFormatVersion,
                    null,
                    "audio/mp4",
                    0,
                    null,
                    null,
                    null,
                    0,
                    updatedAt);
                var stale = cases[index];
                var staleThumbnail = readyThumbnail with
                {
                    State = stale.State,
                    AssetVersionId = stale.VersionId,
                    SizeBytes = stale.SizeBytes,
                    ETag = stale.ETag
                };
                var asset = CloudAudioAssetWithDerivatives(
                    assetId,
                    currentVersionId,
                    new AssetDerivativesInfo(staleThumbnail, unavailableProxy));
                var metadataRequests = 0;
                var downloadRequests = 0;
                using var http = new HttpClient(new StubHandler((request, _) =>
                {
                    var path = request.RequestUri!.AbsolutePath;
                    if (path == $"/api/assets/{assetId:D}/derivatives")
                    {
                        metadataRequests++;
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = JsonContent.Create(new AssetDerivativesInfo(readyThumbnail, unavailableProxy))
                        });
                    }

                    if (path == $"/api/assets/{assetId:D}/derivatives/thumbnail")
                    {
                        downloadRequests++;
                        var response = new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new ByteArrayContent(expectedBytes)
                        };
                        response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/webp");
                        return Task.FromResult(response);
                    }

                    throw new InvalidOperationException($"Unexpected request: {request.Method} {path}");
                }));
                using var staticImages = new StaticImageThumbnailService(Path.Combine(root, $"static-{index}"));
                var thumbnails = new MediaThumbnailService(
                    Path.Combine(root, $"thumbnails-{index}"),
                    "missing-ffmpeg",
                    staticImages);
                var service = new CloudMediaDerivativeService(
                    new AssetLibraryApiClient(http, new Uri("https://library.example/")),
                    thumbnails,
                    Path.Combine(root, $"cloud-{index}"),
                    "missing-ffmpeg");

                var cached = await service.GetCachedDerivativeAsync(asset, AssetDerivativeKind.Thumbnail);

                True(cached is not null && File.Exists(cached));
                SequenceEqual(expectedBytes, await File.ReadAllBytesAsync(cached!));
                Equal(1, metadataRequests);
                Equal(1, downloadRequests);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public static void CubeValidationAcceptsDeclaredRowsAndRejectsUnsafeShapes() =>
        CubeValidationAcceptsDeclaredRowsAndRejectsUnsafeShapesAsync().GetAwaiter().GetResult();

    public static void RealDjiCubeAndThumbnailEnvelopeAreAccepted() =>
        RealDjiCubeAndThumbnailEnvelopeAreAcceptedAsync().GetAwaiter().GetResult();

    public static void LegacyServerAssetsPersistDerivativeMigration() =>
        LegacyServerAssetsPersistDerivativeMigrationAsync().GetAwaiter().GetResult();

    private static async Task CubeValidationAcceptsDeclaredRowsAndRejectsUnsafeShapesAsync()
    {
        const string validOneDimensional = """
            TITLE "Simple 1D"
            LUT_1D_SIZE 2
            DOMAIN_MIN 0 0 0
            DOMAIN_MAX 1 1 1
            0 0 0
            1 1 1
            """;
        await using (var stream = Utf8(validOneDimensional))
        {
            var info = await CubeLutValidator.ValidateAsync(stream);
            Equal(2, info.OneDimensionalSize);
            Equal(null, info.ThreeDimensionalSize);
            Equal(2, info.DataRows);
        }

        const string validThreeDimensional = """
            # minimal cube
            LUT_3D_SIZE 2
            0 0 0
            0 0 1
            0 1 0
            0 1 1
            1 0 0
            1 0 1
            1 1 0
            1 1 1
            """;
        await using (var stream = Utf8(validThreeDimensional))
        {
            var info = await CubeLutValidator.ValidateAsync(stream);
            Equal(2, info.ThreeDimensionalSize);
            Equal(8, info.DataRows);
        }

        await ThrowsApiAsync("LUT_3D_SIZE 66\n", "invalid_cube_lut");
        await ThrowsApiAsync("LUT_3D_SIZE 2\n0 0 0\n", "invalid_cube_lut");
        await ThrowsApiAsync("LUT_1D_SIZE 2\n0 0 NaN\n1 1 1\n", "invalid_cube_lut");
    }

    private static async Task RealDjiCubeAndThumbnailEnvelopeAreAcceptedAsync()
    {
        var cube = new System.Text.StringBuilder("LUT_3D_SIZE 33\n");
        for (var b = 0; b < 33; b++)
            for (var g = 0; g < 33; g++)
                for (var r = 0; r < 33; r++)
                    cube.Append(System.FormattableString.Invariant($"{r / 32.0:F6} {g / 32.0:F6} {b / 32.0:F6}\n"));
        await using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(cube.ToString())))
        {
            var info = await CubeLutValidator.ValidateAsync(stream);
            Equal(33, info.ThreeDimensionalSize);
            Equal(35_937, info.DataRows);
        }

        var onePixelWebP = Convert.FromBase64String(
            "UklGRiIAAABXRUJQVlA4IBYAAAAwAQCdASoBAAEADsD+JaQAA3AAAAAA");
        var dimensions = ThumbnailWebPValidator.Validate(onePixelWebP);
        Equal(1, dimensions.Width);
        Equal(1, dimensions.Height);

        var tooWide = onePixelWebP.ToArray();
        tooWide[26] = 0x81;
        tooWide[27] = 0x02;
        try
        {
            _ = ThumbnailWebPValidator.Validate(tooWide);
        }
        catch (ApiException exception)
        {
            Equal("invalid_thumbnail", exception.Code);
            return;
        }

        throw new InvalidOperationException("Expected an oversized thumbnail to be rejected.");
    }

    private static async Task LegacyServerAssetsPersistDerivativeMigrationAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ial-derivative-migration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var dataPath = Path.Combine(root, "state.json");
        try
        {
            await File.WriteAllTextAsync(dataPath, """
                {
                  "schemaVersion": 1,
                  "assets": [{
                    "id": "11111111-1111-1111-1111-111111111111",
                    "currentVersionId": "22222222-2222-2222-2222-222222222222",
                    "name": "Legacy",
                    "category": "bgm",
                    "originalFileName": "legacy.mp3",
                    "extension": ".mp3",
                    "sizeBytes": 10,
                    "contentHash": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
                    "objectKey": "originals/legacy.mp3",
                    "hasOriginal": true,
                    "tags": [],
                    "uploadedByUserId": "33333333-3333-3333-3333-333333333333",
                    "uploadedAt": "2026-08-01T00:00:00Z",
                    "updatedAt": "2026-08-01T00:00:00Z",
                    "previousVersions": []
                  }]
                }
                """);
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DevelopmentStorage:DataPath"] = "state.json"
                })
                .Build();
            using var store = new AtomicJsonDataStore(
                new TestHostEnvironment(root),
                configuration,
                NullLogger<AtomicJsonDataStore>.Instance);
            await store.InitializeAsync();
            var migrated = await store.ReadAsync(state => state.Assets.Single().Derivatives);
            Equal(DerivativeState.Queued, migrated.Thumbnail.State);
            Equal(AssetDerivativeRules.AudioWaveformThumbnailFormatVersion, migrated.Thumbnail.FormatVersion);
            True(migrated.Thumbnail.ServerBackfillEligible);
            Equal(DerivativeState.Queued, migrated.Proxy.State);

            var persisted = await File.ReadAllTextAsync(dataPath);
            True(persisted.Contains("\"derivatives\"", StringComparison.Ordinal));
            True(persisted.Contains("\"serverBackfillEligible\": true", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "InternalAssetLibrary.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private static MemoryStream Utf8(string value) =>
        new(Encoding.UTF8.GetBytes(value), writable: false);

    private static ApiAsset CloudAudioAsset(Guid assetId, string proxySha256, long proxySize)
    {
        var versionId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var updatedAt = new DateTimeOffset(2026, 8, 31, 0, 0, 0, TimeSpan.Zero);
        var unavailableThumbnail = new AssetDerivativeInfo(
            AssetDerivativeKind.Thumbnail,
            versionId,
            AssetDerivativeState.Unavailable,
            AssetDerivativeRules.AudioWaveformThumbnailFormatVersion,
            null,
            "image/webp",
            0,
            null,
            null,
            null,
            0,
            updatedAt);
        var proxy = new AssetDerivativeInfo(
            AssetDerivativeKind.Proxy,
            versionId,
            AssetDerivativeState.Ready,
            AssetDerivativeRules.ProxyFormatVersion,
            $"/api/assets/{assetId:D}/derivatives/proxy",
            "audio/mp4",
            proxySize,
            proxySha256,
            null,
            null,
            0,
            updatedAt);
        return new ApiAsset(
            assetId,
            versionId,
            "Audio",
            ApiAssetCategory.Bgm,
            "audio.flac",
            ".flac",
            proxySize,
            new string('A', 64),
            $"originals/{assetId:N}.flac",
            true,
            null,
            [],
            new ApiAssetUploader(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), "member", "Member"),
            updatedAt,
            updatedAt,
            1,
            0,
            ApiAssetState.Active,
            null,
            null,
            new AssetDerivativesInfo(unavailableThumbnail, proxy));
    }

    private static ApiAsset CloudAudioAssetWithDerivatives(
        Guid assetId,
        Guid versionId,
        AssetDerivativesInfo derivatives)
    {
        var updatedAt = new DateTimeOffset(2026, 8, 31, 0, 0, 0, TimeSpan.Zero);
        return new ApiAsset(
            assetId,
            versionId,
            "Audio",
            ApiAssetCategory.Bgm,
            "audio.flac",
            ".flac",
            1,
            new string('A', 64),
            $"originals/{assetId:N}.flac",
            true,
            null,
            [],
            new ApiAssetUploader(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), "member", "Member"),
            updatedAt,
            updatedAt,
            1,
            0,
            ApiAssetState.Active,
            null,
            null,
            derivatives);
    }

    private static AssetDerivativesRecord ReadyDerivatives(
        Guid assetVersionId,
        string thumbnailObjectKey,
        DateTimeOffset updatedAt) => new()
    {
        Thumbnail = new AssetDerivativeRecord
        {
            AssetVersionId = assetVersionId,
            State = DerivativeState.Ready,
            FormatVersion = AssetDerivativeRules.ThumbnailFormatVersion,
            ObjectKey = thumbnailObjectKey,
            ContentType = "image/webp",
            SizeBytes = 42,
            ETag = new string('D', 64),
            UpdatedAt = updatedAt
        },
        Proxy = new AssetDerivativeRecord
        {
            AssetVersionId = assetVersionId,
            State = DerivativeState.Unavailable,
            FormatVersion = AssetDerivativeRules.ProxyFormatVersion,
            UpdatedAt = updatedAt
        }
    };

    private static async Task ThrowsApiAsync(string value, string code)
    {
        await using var stream = Utf8(value);
        try
        {
            _ = await CubeLutValidator.ValidateAsync(stream);
        }
        catch (ApiException exception)
        {
            Equal(code, exception.Code);
            return;
        }

        throw new InvalidOperationException("Expected an ApiException.");
    }

    private static void True(bool condition)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Expected true.");
        }
    }

    private static void False(bool condition) => True(!condition);

    private static void Contains(string expectedSubstring, string actual)
    {
        if (!actual.Contains(expectedSubstring, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected text to contain '{expectedSubstring}'.");
        }
    }

    private static void SequenceEqual(byte[] expected, byte[] actual)
    {
        if (!expected.AsSpan().SequenceEqual(actual))
        {
            throw new InvalidOperationException("Expected byte sequences to be equal.");
        }
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
        }
    }

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "InternalAssetLibrary.SelfTests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request, cancellationToken);
    }
}
