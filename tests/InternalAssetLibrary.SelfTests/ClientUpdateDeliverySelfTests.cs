using System.Net;
using System.Security.Cryptography;
using System.Text;
using InternalAssetLibrary.Client.Core.Http;
using InternalAssetLibrary.Client.Core.Updates;
using InternalAssetLibrary.Contracts;
using InternalAssetLibrary.Server.Api;
using InternalAssetLibrary.Server.Security;
using InternalAssetLibrary.Server.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

internal static class ClientUpdateDeliverySelfTests
{
    public static void PublishedSignaturesPreserveTimestampPrecision()
    {
        var info = new ClientReleaseInfo("1.1.2", "0.2.0-preview.6.3",
            DateTimeOffset.Parse("2026-10-02T14:33:12.3809204+08:00"),
            "InternalAssetLibrary.Client.Setup-1.1.2.exe", 142085832,
            "B46718AA5CB15EC3FAA4DEB6A3E04C1C21CBE1428E9405310D2C20D8FA692AC3",
            "/api/client/releases/latest/download",
            "1.1.2：共享素材文件夹可缩到一行高度；官网 Markdown 当前段落显示原符号、其他段落实时渲染；恢复底部栏目导航；后台掉登录直接显示登录页并保留本机草稿。保留现有素材、传输、编辑、管理和更新能力。\n1.1.2: Compact one-row folder area; block-based live Markdown with editable syntax; restored bottom navigation; expired website sessions return to login without losing local drafts. Existing library, transfer, editing and management features are preserved.")
        { Signature = "KcPVR5sojwFgJgO5MsBmOyTvcCG1s/GXFXJSYe4RHeRykMHdpS8BINmbodI7ktRjMqZ3ZDLHYKYzo2e98DbpcA==" };
        False(ClientReleaseSignature.Verify(info));
        True(ClientReleaseSignature.Verify(info with { PublishedAt = DateTimeOffset.Parse("2026-10-02T14:33:12+08:00") }));
        var corrected = info with { Signature = "UHWCLfw1h3QGFR4n1f0DLLP3C5z8VHONuDqUscGRC6ppzGLC2OPN7V0BJtu2cQ6F+2pMbRDouMHQd/Bgf6O1VQ==" };
        True(ClientReleaseSignature.Verify(corrected));
        False(ClientReleaseSignature.Verify(corrected with { Version = "1.1.3" }));
    }

    public static void VerifyPublishedManifest(string path)
    {
        var root = CreateTemporaryDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "updates"));
            File.Copy(path, Path.Combine(root, "updates", "latest.json"));
            var release = CreateProvider(root).TryGetLatestAsync().GetAwaiter().GetResult();
            if (release is null || !ClientReleaseSignature.Verify(release.Info))
                throw new InvalidDataException("发布清单未通过服务端及客户端共用的验签逻辑。");
            Console.WriteLine($"PASS: C# provider and pinned-key signature verified {release.Info.Version}");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    public static void CosManifestsDoNotRequireServerInstallerCopies()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var updates = Path.Combine(root, "updates");
            Directory.CreateDirectory(updates);
            const string version = "0.2.0-preview.6.2";
            const string installer = "InternalAssetLibrary.Client.Setup-0.2.0-preview.6.2.exe";
            var objectKey = ClientReleaseProvider.BuildInstallerObjectKey(version, installer);
            File.WriteAllText(
                Path.Combine(updates, "latest.json"),
                $$"""
                {
                  "version":"{{version}}",
                  "minimumCompatibleVersion":"0.2.0-preview.6.1",
                  "publishedAt":"2026-08-31T00:00:00+00:00",
                  "installerFileName":"{{installer}}",
                  "installerSizeBytes":123456,
                  "installerSha256":"{{new string('A', 64)}}",
                  "installerObjectKey":"{{objectKey}}",
                  "releaseNotes":"COS delivery"
                }
                """,
                Encoding.UTF8);
            var provider = CreateProvider(root);

            var release = provider.TryGetLatestAsync().GetAwaiter().GetResult();

            NotNull(release);
            Equal(objectKey, release!.InstallerObjectKey);
            Equal(null, release.LocalInstallerPath);
            Equal(123456L, release.Info.InstallerSizeBytes);
            Equal("/api/client/releases/latest/download", release.Info.DownloadPath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public static void CosManifestObjectKeysFollowTheFixedConvention()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var updates = Path.Combine(root, "updates");
            Directory.CreateDirectory(updates);
            File.WriteAllText(
                Path.Combine(updates, "latest.json"),
                $$"""
                {
                  "version":"0.2.0-preview.6.2",
                  "minimumCompatibleVersion":"0.2.0-preview.6.1",
                  "publishedAt":"2026-08-31T00:00:00+00:00",
                  "installerFileName":"client.exe",
                  "installerSizeBytes":12,
                  "installerSha256":"{{new string('B', 64)}}",
                  "installerObjectKey":"client-updates/wrong/client.exe"
                }
                """,
                Encoding.UTF8);

            Throws<InvalidDataException>(() =>
                CreateProvider(root).TryGetLatestAsync().GetAwaiter().GetResult());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public static void ManualTransitionBlocksOnlyBrokenUpdaterVersions()
    {
        const string transition = "0.2.0-preview.6.8";
        False(ClientUpdateEndpoints.RequiresManualTransition("0.2.0-preview.6.3", transition));
        False(ClientUpdateEndpoints.RequiresManualTransition("0.2.0-preview.6.4", transition));
        False(ClientUpdateEndpoints.RequiresManualTransition("0.2.0-preview.6.5", transition));
        True(ClientUpdateEndpoints.RequiresManualTransition("0.2.0-preview.6.6", transition));
        True(ClientUpdateEndpoints.RequiresManualTransition("0.2.0-preview.6.7", transition));
        False(ClientUpdateEndpoints.RequiresManualTransition(transition, transition));
        False(ClientUpdateEndpoints.RequiresManualTransition(transition, "0.2.0-preview.6.9"));
        True(ClientUpdateEndpoints.RequiresManualTransition("0.2.0-preview.6.7", "0.2.0-preview.6.9"));

        var root = CreateTemporaryDirectory();
        try
        {
            var updates = Path.Combine(root, "updates");
            Directory.CreateDirectory(updates);
            File.WriteAllText(
                Path.Combine(updates, "latest.json"),
                $$"""
                {
                  "version":"{{transition}}",
                  "minimumCompatibleVersion":"0.2.0-preview.6.3",
                  "publishedAt":"2026-09-05T00:00:00+00:00",
                  "installerFileName":"InternalAssetLibrary.Client.Setup-{{transition}}.exe",
                  "installerSizeBytes":123456,
                  "installerSha256":"{{new string('D', 64)}}",
                  "installerObjectKey":"client-updates/{{transition}}/InternalAssetLibrary.Client.Setup-{{transition}}.exe"
                }
                """,
                Encoding.UTF8);

            var blocked = AuthenticatedContext("0.2.0-preview.6.7");
            var exception = Catch<ApiException>(() => ClientUpdateEndpoints.GetLatestAsync(
                    blocked,
                    CreateProvider(root),
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult());
            Equal(StatusCodes.Status426UpgradeRequired, exception.StatusCode);
            Equal("client_update_manual_install_required", exception.Code);
            Equal(transition, blocked.Response.Headers["X-IAL-Manual-Update-Version"].ToString());
            True(exception.Message.Contains("手动下载并运行", StringComparison.Ordinal));

            var legacyManual = AuthenticatedContext("0.2.0-preview.6.5");
            NotNull(ClientUpdateEndpoints.GetLatestAsync(
                    legacyManual,
                    CreateProvider(root),
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult());

            var fixedUpdater = AuthenticatedContext(transition);
            NotNull(ClientUpdateEndpoints.GetLatestAsync(
                    fixedUpdater,
                    CreateProvider(root),
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public static void CosDownloadEndpointReturnsSignedRedirect()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var updates = Path.Combine(root, "updates");
            Directory.CreateDirectory(updates);
            const string version = "0.2.0-preview.6.2";
            const string installer = "client.exe";
            var objectKey = ClientReleaseProvider.BuildInstallerObjectKey(version, installer);
            File.WriteAllText(
                Path.Combine(updates, "latest.json"),
                $$"""
                {
                  "version":"{{version}}",
                  "minimumCompatibleVersion":"0.2.0-preview.6.1",
                  "publishedAt":"2026-08-31T00:00:00+00:00",
                  "installerFileName":"{{installer}}",
                  "installerSizeBytes":12,
                  "installerSha256":"{{new string('C', 64)}}",
                  "installerObjectKey":"{{objectKey}}"
                }
                """,
                Encoding.UTF8);
            var expiresAt = new DateTimeOffset(2026, 8, 31, 0, 15, 0, TimeSpan.Zero);
            var store = new SignedUrlObjectStore(
                new ObjectDownloadUrl(
                    new Uri("https://private.cos.ap-shanghai.myqcloud.com/client.exe?q-signature=test"),
                    expiresAt));
            var context = new DefaultHttpContext();
            using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
            context.RequestServices = services;
            context.Items[BearerSessionMiddleware.IdentityKey] = new RequestIdentity(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "tester",
                IsAdmin: false,
                []);

            var result = ClientUpdateEndpoints.DownloadLatestAsync(
                    context,
                    CreateProvider(root),
                    store,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            result.ExecuteAsync(context).GetAwaiter().GetResult();

            Equal(StatusCodes.Status307TemporaryRedirect, context.Response.StatusCode);
            Equal(store.Download.Url.AbsoluteUri, context.Response.Headers.Location.ToString());
            Equal(expiresAt.ToString("O"), context.Response.Headers["X-IAL-Object-Url-Expires"].ToString());
            Equal(objectKey, store.RequestedObjectKey);
            Equal(installer, store.RequestedFileName);
            Equal(0, store.OpenReadCalls);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public static void ClientDownloadsReportBoundedProgressAndVerifyBytes()
    {
        var bytes = Enumerable.Range(0, 300_000).Select(index => (byte)(index % 251)).ToArray();
        var release = ReleaseFor(bytes);
        var progress = new List<ClientUpdateDownloadProgress>();
        using var http = new HttpClient(new StubHandler((request, _) =>
        {
            Equal("/api/client/releases/latest/download", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes)
            });
        }));
        var service = new ClientUpdateService(
            new AssetLibraryApiClient(http, new Uri("https://library.example/")),
            "0.2.0-preview.6.1");
        var directory = CreateTemporaryDirectory();
        try
        {
            var result = service.DownloadVerifiedInstallerWithProgressAsync(
                    release,
                    directory,
                    new InlineProgress<ClientUpdateDownloadProgress>(progress.Add))
                .GetAwaiter()
                .GetResult();

            SequenceEqual(bytes, File.ReadAllBytes(result));
            True(progress.Count >= 2);
            Equal(0L, progress[0].BytesDownloaded);
            Equal(bytes.LongLength, progress[^1].BytesDownloaded);
            Equal(bytes.LongLength, progress[^1].TotalBytes);
            Equal(100, progress[^1].Percentage);
            True(progress.All(item => item.BytesDownloaded >= 0 && item.BytesDownloaded <= item.TotalBytes));
            True(progress.Zip(progress.Skip(1)).All(pair =>
                pair.First.BytesDownloaded <= pair.Second.BytesDownloaded));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    public static void CancelledClientDownloadsPreserveAndResumePartialFiles()
    {
        var bytes = Enumerable.Range(0, 300_000).Select(index => (byte)(index % 239)).ToArray();
        var release = ReleaseFor(bytes);
        var resumed = false;
        using var http = new HttpClient(new StubHandler((request, _) =>
        {
            var offset = request.Headers.Range?.Ranges.Single().From ?? 0;
            resumed |= offset > 0;
            var response = new HttpResponseMessage(offset > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes[(int)offset..])
            };
            if (offset > 0) response.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(offset, bytes.Length - 1, bytes.Length);
            return Task.FromResult(response);
        }));
        var service = new ClientUpdateService(
            new AssetLibraryApiClient(http, new Uri("https://library.example/")),
            "0.2.0-preview.6.1");
        var directory = CreateTemporaryDirectory();
        using var cancellation = new CancellationTokenSource();
        try
        {
            Throws<OperationCanceledException>(() =>
                service.DownloadVerifiedInstallerWithProgressAsync(
                        release,
                        directory,
                        new InlineProgress<ClientUpdateDownloadProgress>(item =>
                        {
                            if (item.BytesDownloaded > 0)
                            {
                                cancellation.Cancel();
                            }
                        }),
                        cancellation.Token)
                    .GetAwaiter()
                    .GetResult());

            Equal(1, Directory.GetFiles(directory, "*.part", SearchOption.TopDirectoryOnly).Length);
            Equal(0, Directory.GetFiles(directory, "*.exe", SearchOption.TopDirectoryOnly).Length);
            var result = service.DownloadVerifiedInstallerWithProgressAsync(release, directory, null).GetAwaiter().GetResult();
            True(resumed);
            SequenceEqual(bytes, File.ReadAllBytes(result));
            Equal(0, Directory.GetFiles(directory, "*.part").Length);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    public static void OversizedClientReleaseManifestsAreRejected()
    {
        var oversizedJson = "{" + $"\"padding\":\"{new string('x', 300_000)}\"" + "}";
        using var http = new HttpClient(new StubHandler((request, _) =>
        {
            Equal("/api/client/releases/latest", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(oversizedJson, Encoding.UTF8, "application/json")
            });
        }));
        var client = new AssetLibraryApiClient(http, new Uri("https://library.example/"));

        var exception = Catch<InvalidDataException>(() =>
            client.GetLatestClientReleaseAsync().GetAwaiter().GetResult());

        True(exception.Message.Contains("response exceeds", StringComparison.OrdinalIgnoreCase));
    }

    public static void ServerAndPublisherKeepInstallerBytesOffTheAppServer()
    {
        var repository = RepositoryRoot();
        var endpoint = File.ReadAllText(Path.Combine(
            repository,
            "src",
            "InternalAssetLibrary.Server",
            "Api",
            "ClientUpdateEndpoints.cs"));
        Contains("CreateDownloadUrlAsync", endpoint);
        Contains("Results.Redirect", endpoint);
        True(endpoint.Split("AccessControl.RequireUser(context);", StringSplitOptions.None).Length >= 3);
        Contains("objects.StorageKind == \"development-filesystem\"", endpoint);
        Contains("client_release_object_unavailable", endpoint);

        var publisher = File.ReadAllText(Path.Combine(
            repository,
            "deployment",
            "scripts",
            "publish-client-update.ps1"));
        Contains("client-updates/$Version/$versionedInstallerName", publisher);
        Contains("installerObjectKey = $installerObjectKey", publisher);
        Contains("private COS key", publisher);
        Contains("-Mode Sign -PrivateKeyPath $SigningKeyPath", publisher);
        Contains("-Mode Verify -ManifestPath $incomingManifest", publisher);
    }

    private static ClientReleaseProvider CreateProvider(string root)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ClientUpdates:ManifestPath"] = "updates/latest.json",
                ["ClientUpdates:MinimumCompatibleVersion"] = "0.2.0-preview.6.3"
            })
            .Build();
        return new ClientReleaseProvider(new TestHostEnvironment(root), configuration);
    }

    private static ClientReleaseInfo ReleaseFor(byte[] bytes) => new(
        "0.2.0-preview.6.2",
        "0.2.0-preview.6.1",
        new DateTimeOffset(2026, 8, 31, 0, 0, 0, TimeSpan.Zero),
        "client.exe",
        bytes.LongLength,
        Convert.ToHexString(SHA256.HashData(bytes)),
        "/api/client/releases/latest/download",
        null);

    private static DefaultHttpContext AuthenticatedContext(string clientVersion)
    {
        var context = new DefaultHttpContext();
        context.Items[BearerSessionMiddleware.IdentityKey] = new RequestIdentity(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "tester",
            IsAdmin: false,
            []);
        context.Request.Headers[ClientCompatibilityMiddleware.VersionHeader] = clientVersion;
        return context;
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ial-update-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "InternalAssetLibrary.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static void Contains(string expected, string actual)
    {
        if (!actual.Contains(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected source to contain '{expected}'.");
        }
    }

    private static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException("Sequences are not equal.");
        }
    }

    private static void NotNull(object? value) => True(value is not null);

    private static void True(bool condition)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Expected true, but was false.");
        }
    }

    private static void False(bool condition) => True(!condition);

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', but was '{actual}'.");
        }
    }

    private static void Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name} to be thrown.");
    }

    private static TException Catch<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException exception)
        {
            return exception;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name} to be thrown.");
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request, cancellationToken);
    }

    private sealed class SignedUrlObjectStore(ObjectDownloadUrl download) : IObjectStore
    {
        public ObjectDownloadUrl Download { get; } = download;
        public string StorageKind => "tencent-cos";
        public string? RequestedObjectKey { get; private set; }
        public string? RequestedFileName { get; private set; }
        public int OpenReadCalls { get; private set; }

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<ObjectWriteResult> PutAsync(
            string objectKey,
            Stream source,
            long maximumBytes,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<Stream?> OpenReadAsync(
            string objectKey,
            CancellationToken cancellationToken = default)
        {
            OpenReadCalls++;
            return ValueTask.FromResult<Stream?>(null);
        }

        public ValueTask<ObjectDownloadUrl?> CreateDownloadUrlAsync(
            string objectKey,
            string? downloadFileName = null,
            CancellationToken cancellationToken = default)
        {
            RequestedObjectKey = objectKey;
            RequestedFileName = downloadFileName;
            return ValueTask.FromResult<ObjectDownloadUrl?>(Download);
        }

        public Task DeleteAsync(string objectKey, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "InternalAssetLibrary.SelfTests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
