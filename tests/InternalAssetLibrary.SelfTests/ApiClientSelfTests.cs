using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using InternalAssetLibrary.Client.Core.Http;
using InternalAssetLibrary.Contracts;

internal static class ApiClientSelfTests
{
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AssetId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid VersionId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid MarkerSetId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid MarkerId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid LutId = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid FolderId = Guid.Parse("77777777-7777-7777-7777-777777777777");

    public static void AuthenticationAndUriRules() => AuthenticationAndUriRulesAsync().GetAwaiter().GetResult();
    public static void DtoFormatsMatchServer() => DtoFormatsMatchServerAsync().GetAwaiter().GetResult();
    public static void PersonalRecycleBinRoutesStayScoped() => PersonalRecycleBinRoutesStayScopedAsync().GetAwaiter().GetResult();
    public static void PersonalRecycleBinLoadsEveryPage() => PersonalRecycleBinLoadsEveryPageAsync().GetAwaiter().GetResult();
    public static void AssetFolderRoutesPreserveHierarchy() => AssetFolderRoutesPreserveHierarchyAsync().GetAwaiter().GetResult();
    public static void DirectFolderQueriesAndAudioCategoryRoutes() =>
        DirectFolderQueriesAndAudioCategoryRoutesAsync().GetAwaiter().GetResult();
    public static void TransfersRemainStreaming() => TransfersRemainStreamingAsync().GetAwaiter().GetResult();
    public static void ProblemDetailsAreExposed() => ProblemDetailsAreExposedAsync().GetAwaiter().GetResult();
    public static void DerivativeAndLutRoutesStreamContent() => DerivativeAndLutRoutesStreamContentAsync().GetAwaiter().GetResult();
    public static void TeamLutListsPreserveUploaderScope() => TeamLutListsPreserveUploaderScopeAsync().GetAwaiter().GetResult();
    public static void InvalidSuccessBodiesAreClassified() => InvalidSuccessBodiesAreClassifiedAsync().GetAwaiter().GetResult();

    public static void VisibleNetworkMessagesHideAddresses()
    {
        var origin = new Uri("https://assets.example.invalid:8443/");
        Equal(
            "请求 [服务器地址已隐藏] 失败：权限不足。",
            UserVisibleNetworkMessage.RedactLocations(
                "请求 https://assets.example.invalid:8443/api/assets?trace=private 失败：权限不足。",
                origin));
        Equal(
            "连接 [服务器地址已隐藏] 被拒绝。",
            UserVisibleNetworkMessage.RedactLocations("连接 127.0.0.1:5019 被拒绝。"));
        Equal(
            "连接 [服务器地址已隐藏] 失败：证书无效。",
            UserVisibleNetworkMessage.RedactLocations(
                "连接 assets.example.invalid:8443 失败：证书无效。",
                origin));
        Equal(
            "权限不足。",
            UserVisibleNetworkMessage.RedactLocations("权限不足。", origin));

        var credentialMessage = UserVisibleNetworkMessage.RedactLocations(
            "Authorization: Bearer top-secret password=\"open-sesame\" access_token=abc123");
        False(credentialMessage.Contains("top-secret", StringComparison.Ordinal));
        False(credentialMessage.Contains("open-sesame", StringComparison.Ordinal));
        False(credentialMessage.Contains("abc123", StringComparison.Ordinal));
        True(credentialMessage.Contains("[凭据已隐藏]", StringComparison.Ordinal));
    }

    public static void NetworkFailuresAreClassified()
    {
        Equal(
            NetworkFailureKind.NameResolution,
            NetworkFailureClassifier.Classify(new HttpRequestException(
                HttpRequestError.NameResolutionError,
                "DNS failed")).Kind);
        Equal(
            NetworkFailureKind.ConnectionRefused,
            NetworkFailureClassifier.Classify(new HttpRequestException(
                HttpRequestError.ConnectionError,
                "Connection failed",
                new SocketException((int)SocketError.ConnectionRefused))).Kind);
        Equal(
            NetworkFailureKind.Timeout,
            NetworkFailureClassifier.Classify(new TaskCanceledException("Timed out")).Kind);
        Equal(
            NetworkFailureKind.Tls,
            NetworkFailureClassifier.Classify(new HttpRequestException(
                HttpRequestError.SecureConnectionError,
                "TLS failed",
                new AuthenticationException("Bad certificate"))).Kind);
        var httpFailure = NetworkFailureClassifier.Classify(new HttpRequestException(
            HttpRequestError.Unknown,
            "Bad gateway",
            inner: null,
            HttpStatusCode.BadGateway));
        Equal(NetworkFailureKind.HttpStatus, httpFailure.Kind);
        Equal(HttpStatusCode.BadGateway, httpFailure.StatusCode);
        Equal(
            NetworkFailureKind.InvalidHttpResponse,
            NetworkFailureClassifier.Classify(new InvalidApiResponseException("HTML is not JSON")).Kind);
    }

    private static async Task InvalidSuccessBodiesAreClassifiedAsync()
    {
        using var http = new HttpClient(new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(
            HttpStatusCode.OK)
        {
            Content = new StringContent("<html>tunnel landing page</html>", Encoding.UTF8, "text/html")
        })));
        var client = new AssetLibraryApiClient(http, new Uri("https://library.example/"));

        var exception = await ThrowsAsync<InvalidApiResponseException>(async () =>
            await client.GetAsync<JsonElement>("healthz"));
        Equal(
            NetworkFailureKind.InvalidHttpResponse,
            NetworkFailureClassifier.Classify(exception).Kind);
    }

    private static async Task AuthenticationAndUriRulesAsync()
    {
        var calls = 0;
        using var http = new HttpClient(new StubHandler((request, _) =>
        {
            calls++;
            Equal(HttpMethod.Get, request.Method);
            Equal("https://library.example/team/api/me", request.RequestUri!.AbsoluteUri);
            Equal("Bearer", request.Headers.Authorization?.Scheme);
            Equal("test-token", request.Headers.Authorization?.Parameter);
            Equal("0.2.0-preview.6.1", request.Headers.GetValues("X-IAL-Client-Version").Single());
            return Task.FromResult(JsonResponse(CurrentUserJson()));
        }));
        var client = new AssetLibraryApiClient(
            http,
            new Uri("https://library.example/team"),
            new StaticAccessTokenProvider("test-token"),
            clientVersion: "0.2.0-preview.6.1");

        var current = await client.GetCurrentUserAsync();
        Equal(UserId, current.Id);
        Equal(ApiProfileGender.Custom, current.Gender);
        Equal(ApiProfileVisibility.Team, current.GenderVisibility);

        await ThrowsAsync<ArgumentException>(async () =>
            await client.GetAsync<JsonElement>("https://other.example/api/me"));
        await ThrowsAsync<ArgumentException>(async () =>
            await client.GetAsync<JsonElement>("//other.example/api/me"));
        Equal(1, calls);
    }

    private static async Task TeamLutListsPreserveUploaderScopeAsync()
    {
        using var http = new HttpClient(new StubHandler((request, _) =>
        {
            Equal(HttpMethod.Get, request.Method);
            Equal("/api/luts", request.RequestUri!.AbsolutePath);
            var query = request.RequestUri.Query;
            True(query.Contains($"uploaderId={UserId:D}", StringComparison.Ordinal));
            True(query.Contains("page=3", StringComparison.Ordinal));
            True(query.Contains("pageSize=25", StringComparison.Ordinal));
            return Task.FromResult(JsonResponse("""
                {"items":[],"page":3,"pageSize":25,"total":0}
                """));
        }));
        var client = new AssetLibraryApiClient(http, new Uri("https://library.example/"));

        var page = await client.ListTeamLutsAsync(new ApiTeamLutListQuery(
            Page: 3,
            PageSize: 25,
            UploaderId: UserId));

        Equal(0, page.Total);
    }

    private static async Task DtoFormatsMatchServerAsync()
    {
        var call = 0;
        using var http = new HttpClient(new StubHandler(async (request, cancellationToken) =>
        {
            call++;
            if (call == 1)
            {
                Equal(HttpMethod.Post, request.Method);
                Equal("/api/assets", request.RequestUri!.AbsolutePath);
                var json = await request.Content!.ReadAsStringAsync(cancellationToken);
                using var body = JsonDocument.Parse(json);
                Equal("sound-effect", body.RootElement.GetProperty("category").GetString());
                Equal(42.5, body.RootElement.GetProperty("durationSeconds").GetDouble());
                return JsonResponse(AssetJson("recycled"));
            }

            if (call == 2)
            {
                Equal(HttpMethod.Post, request.Method);
                Equal($"/api/marker-sets/{MarkerSetId:D}/markers", request.RequestUri!.AbsolutePath);
                var markerJson = await request.Content!.ReadAsStringAsync(cancellationToken);
                using var markerBody = JsonDocument.Parse(markerJson);
                Equal(
                    TimeSpan.FromMilliseconds(1250),
                    TimeSpan.Parse(markerBody.RootElement.GetProperty("time").GetString()!));
                return JsonResponse($$"""
                    {"id":"{{MarkerId:D}}","time":"01:02:03.0040000","name":"定位","note":"备注"}
                    """);
            }

            Equal(HttpMethod.Put, request.Method);
            Equal($"/api/assets/{AssetId:D}/tags", request.RequestUri!.AbsolutePath);
            var tagsJson = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var tagsBody = JsonDocument.Parse(tagsJson);
            False(tagsBody.RootElement.GetProperty("createMissing").GetBoolean());
            Equal("常用", tagsBody.RootElement.GetProperty("tags")[0].GetString());
            return JsonResponse(AssetJson("active"));
        }));
        var client = new AssetLibraryApiClient(http, new Uri("https://library.example/"));

        var asset = await client.CreateAssetAsync(new ApiCreateAssetRequest(
            "音效",
            ApiAssetCategory.SoundEffect,
            "sound.wav",
            4,
            new string('A', 64),
            null,
            ["常用"],
            42.5));
        Equal(ApiAssetCategory.SoundEffect, asset.Category);
        Equal(ApiAssetState.Recycled, asset.State);
        Equal<double?>(42.5, asset.DurationSeconds);

        var marker = await client.AddMarkerAsync(
            MarkerSetId,
            new UpsertMarkerRequest(TimeSpan.FromMilliseconds(1250), "定位", "备注"));
        Equal(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(3.004), marker.Time);
        _ = await client.UpdateAssetTagsAsync(
            AssetId,
            new ApiUpdateAssetTagsRequest(["常用"], CreateMissing: false));
        Equal(3, call);
    }

    private static async Task PersonalRecycleBinRoutesStayScopedAsync()
    {
        var call = 0;
        using var http = new HttpClient(new StubHandler((request, _) =>
        {
            call++;
            if (call == 1)
            {
                Equal(HttpMethod.Get, request.Method);
                Equal("/api/me/recycle-bin", request.RequestUri!.AbsolutePath);
                var query = request.RequestUri.Query;
                True(query.Contains("sort=name", StringComparison.Ordinal));
                True(query.Contains("order=asc", StringComparison.Ordinal));
                False(query.Contains("state=", StringComparison.Ordinal));
                False(query.Contains("uploaderId=", StringComparison.Ordinal));
                return Task.FromResult(JsonResponse($$"""
                    {"items":[{{AssetJson("recycled")}}],"page":1,"pageSize":40,"total":1}
                    """));
            }

            if (call == 2)
            {
                Equal(HttpMethod.Post, request.Method);
                Equal($"/api/me/recycle-bin/{AssetId:D}/restore", request.RequestUri!.AbsolutePath);
                return Task.FromResult(JsonResponse(AssetJson("active")));
            }

            if (call == 3)
            {
                Equal(HttpMethod.Delete, request.Method);
                Equal($"/api/me/recycle-bin/{AssetId:D}", request.RequestUri!.AbsolutePath);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }

            Equal(HttpMethod.Delete, request.Method);
            Equal("/api/me/recycle-bin", request.RequestUri!.AbsolutePath);
            return Task.FromResult(JsonResponse("{\"deletedCount\":7}"));
        }));
        var client = new AssetLibraryApiClient(http, new Uri("https://library.example/"));

        var recycled = await client.ListMyRecycleBinAsync();
        Equal(1, recycled.Total);
        Equal(ApiAssetState.Recycled, recycled.Items.Single().State);
        var restored = await client.RestoreMyRecycleBinAssetAsync(AssetId);
        Equal(ApiAssetState.Active, restored.State);
        await client.PermanentlyDeleteMyRecycleBinAssetAsync(AssetId);
        var cleared = await client.ClearMyRecycleBinAsync();
        Equal(7, cleared.DeletedCount);
        Equal(4, call);
    }

    private static async Task PersonalRecycleBinLoadsEveryPageAsync()
    {
        var call = 0;
        using var http = new HttpClient(new StubHandler((request, _) =>
        {
            call++;
            Equal(HttpMethod.Get, request.Method);
            Equal("/api/me/recycle-bin", request.RequestUri!.AbsolutePath);
            var query = request.RequestUri.Query;
            True(query.Contains($"page={call}", StringComparison.Ordinal));
            True(query.Contains("pageSize=100", StringComparison.Ordinal));
            True(query.Contains("sort=name", StringComparison.Ordinal));
            True(query.Contains("order=asc", StringComparison.Ordinal));
            return Task.FromResult(JsonResponse($$"""
                {"items":[{{AssetJson("recycled")}}],"page":{{call}},"pageSize":100,"total":2}
                """));
        }));
        var client = new AssetLibraryApiClient(http, new Uri("https://library.example/"));

        var recycled = await client.ListAllMyRecycleBinAsync();

        Equal(2, recycled.Count);
        Equal(2, call);
        True(recycled.All(asset => asset.State == ApiAssetState.Recycled));
    }

    private static async Task AssetFolderRoutesPreserveHierarchyAsync()
    {
        var call = 0;
        using var http = new HttpClient(new StubHandler(async (request, cancellationToken) =>
        {
            call++;
            if (call == 1)
            {
                Equal(HttpMethod.Get, request.Method);
                Equal("/api/assets", request.RequestUri!.AbsolutePath);
                True(request.RequestUri.Query.Contains($"folderId={FolderId:D}", StringComparison.Ordinal));
                True(request.RequestUri.Query.Contains("includeDescendants=true", StringComparison.Ordinal));
                return JsonResponse($$"""{"items":[{{AssetJson("active")}}],"page":1,"pageSize":40,"total":1}""");
            }

            if (call == 2)
            {
                Equal(HttpMethod.Get, request.Method);
                Equal("/api/asset-folders", request.RequestUri!.AbsolutePath);
                return JsonResponse($$"""[{{FolderJson()}}]""");
            }

            if (call is 3 or 4 or 5 or 6)
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                using var json = JsonDocument.Parse(body);
                if (call == 3)
                {
                    Equal(HttpMethod.Post, request.Method);
                    Equal("/api/asset-folders", request.RequestUri!.AbsolutePath);
                    Equal("云端素材", json.RootElement.GetProperty("name").GetString());
                    True(json.RootElement.GetProperty("parentId").ValueKind == JsonValueKind.Null);
                    return JsonResponse(FolderJson());
                }

                if (call == 4)
                {
                    Equal(HttpMethod.Put, request.Method);
                    Equal($"/api/asset-folders/{FolderId:D}", request.RequestUri!.AbsolutePath);
                    Equal("重命名", json.RootElement.GetProperty("name").GetString());
                    return JsonResponse(FolderJson());
                }

                if (call == 5)
                {
                    Equal(HttpMethod.Put, request.Method);
                    Equal($"/api/asset-folders/{FolderId:D}/parent", request.RequestUri!.AbsolutePath);
                    True(json.RootElement.GetProperty("parentId").ValueKind == JsonValueKind.Null);
                    return JsonResponse(FolderJson());
                }

                Equal(HttpMethod.Put, request.Method);
                Equal($"/api/assets/{AssetId:D}/folder", request.RequestUri!.AbsolutePath);
                Equal(FolderId, json.RootElement.GetProperty("folderId").GetGuid());
                return JsonResponse(AssetJson("active"));
            }

            Equal(HttpMethod.Delete, request.Method);
            Equal($"/api/asset-folders/{FolderId:D}", request.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }));
        var client = new AssetLibraryApiClient(http, new Uri("https://library.example/"));

        var assets = await client.ListAssetsAsync(new ApiAssetListQuery(FolderId: FolderId));
        Equal<Guid?>(FolderId, assets.Items.Single().FolderId);
        var folders = await client.ListAssetFoldersAsync();
        Equal(FolderId, folders.Single().Id);
        _ = await client.CreateAssetFolderAsync(new ApiCreateAssetFolderRequest("云端素材"));
        _ = await client.RenameAssetFolderAsync(FolderId, new ApiRenameAssetFolderRequest("重命名"));
        _ = await client.MoveAssetFolderAsync(FolderId, new ApiMoveAssetFolderRequest(null));
        var moved = await client.MoveAssetToFolderAsync(AssetId, new ApiMoveAssetToFolderRequest(FolderId));
        Equal<Guid?>(FolderId, moved.FolderId);
        await client.DeleteAssetFolderAsync(FolderId);
        Equal(7, call);
    }

    private static async Task DirectFolderQueriesAndAudioCategoryRoutesAsync()
    {
        var call = 0;
        using var http = new HttpClient(new StubHandler(async (request, cancellationToken) =>
        {
            call++;
            if (call == 1)
            {
                Equal(HttpMethod.Get, request.Method);
                Equal("/api/assets", request.RequestUri!.AbsolutePath);
                True(request.RequestUri.Query.Contains("rootOnly=true", StringComparison.Ordinal));
                False(request.RequestUri.Query.Contains("folderId=", StringComparison.Ordinal));
                False(request.RequestUri.Query.Contains("includeDescendants=", StringComparison.Ordinal));
            }
            else if (call == 2)
            {
                Equal(HttpMethod.Get, request.Method);
                Equal("/api/assets", request.RequestUri!.AbsolutePath);
                True(request.RequestUri.Query.Contains($"folderId={FolderId:D}", StringComparison.Ordinal));
                True(request.RequestUri.Query.Contains("includeDescendants=false", StringComparison.Ordinal));
                False(request.RequestUri.Query.Contains("rootOnly=", StringComparison.Ordinal));
            }
            else
            {
                Equal(HttpMethod.Put, request.Method);
                Equal($"/api/assets/{AssetId:D}/category", request.RequestUri!.AbsolutePath);
                var json = await request.Content!.ReadAsStringAsync(cancellationToken);
                using var body = JsonDocument.Parse(json);
                Equal(call == 3 ? "bgm" : "sound-effect", body.RootElement.GetProperty("category").GetString());
            }

            return JsonResponse(call <= 2
                ? $$"""{"items":[{{AssetJson("active")}}],"page":1,"pageSize":40,"total":1}"""
                : AssetJson("active"));
        }));
        var client = new AssetLibraryApiClient(http, new Uri("https://library.example/"));

        _ = await client.ListAssetsAsync(new ApiAssetListQuery(RootOnly: true));
        _ = await client.ListAssetsAsync(new ApiAssetListQuery(
            FolderId: FolderId,
            IncludeDescendantFolders: false));
        _ = await client.UpdateAssetCategoryAsync(
            AssetId,
            new ApiUpdateAssetCategoryRequest(ApiAssetCategory.Bgm));
        _ = await client.UpdateAssetCategoryAsync(
            AssetId,
            new ApiUpdateAssetCategoryRequest(ApiAssetCategory.SoundEffect));
        Equal(4, call);
    }

    private static async Task TransfersRemainStreamingAsync()
    {
        var uploadBytes = Enumerable.Range(0, 200_000).Select(index => (byte)(index % 251)).ToArray();
        var downloadBytes = Enumerable.Range(0, 150_000).Select(index => (byte)(index % 239)).ToArray();
        using var upload = new TrackingReadStream(uploadBytes);
        var responseContent = new StreamingResponseContent(downloadBytes);
        var call = 0;
        using var http = new HttpClient(new StubHandler(async (request, cancellationToken) =>
        {
            call++;
            if (call == 1)
            {
                Equal(HttpMethod.Put, request.Method);
                Equal($"/api/assets/{AssetId:D}/content", request.RequestUri!.AbsolutePath);
                Equal(uploadBytes.LongLength, request.Content!.Headers.ContentLength);
                Equal("application/octet-stream", request.Content.Headers.ContentType?.MediaType);
                await using var received = new MemoryStream();
                await request.Content.CopyToAsync(received, cancellationToken);
                SequenceEqual(uploadBytes, received.ToArray());
                return JsonResponse(AssetJson("active"));
            }

            Equal(HttpMethod.Get, request.Method);
            if (call == 2)
            {
                Equal($"/api/assets/{AssetId:D}/download", request.RequestUri!.AbsolutePath);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = responseContent };
            }

            Equal($"/api/assets/{AssetId:D}/content", request.RequestUri!.AbsolutePath);
            Equal("Bearer", request.Headers.Authorization?.Scheme);
            Equal("preview-token", request.Headers.Authorization?.Parameter);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamingResponseContent(downloadBytes)
            };
        }));
        var client = new AssetLibraryApiClient(
            http,
            new Uri("https://library.example/"),
            new StaticAccessTokenProvider("preview-token"));

        _ = await client.UploadAssetContentAsync(AssetId, upload, uploadBytes.LongLength);
        False(upload.WasDisposed);
        True(upload.MaximumReadRequest < uploadBytes.Length);

        await using var destination = new MemoryStream();
        await client.DownloadAssetAsync(AssetId, destination);
        SequenceEqual(downloadBytes, destination.ToArray());
        Equal(0, responseContent.SerializeCallCount);

        await using var boundedDestination = new MemoryStream();
        await ThrowsAsync<InvalidDataException>(async () =>
            await client.CopyAssetPreviewAsync(AssetId, boundedDestination, maximumBytes: 100_000));
        True(boundedDestination.Length <= 100_000);
        Equal(3, call);
    }

    private static async Task ProblemDetailsAreExposedAsync()
    {
        using var http = new HttpClient(new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(
            HttpStatusCode.UnprocessableEntity)
        {
            Content = new StringContent(
                """
                {"type":"about:blank","title":"请求无效","status":422,"detail":"名称不能为空。","code":"invalid_name","traceId":"trace-7","errors":{"name":["required"]}}
                """,
                Encoding.UTF8,
                "application/problem+json")
        })));
        var client = new AssetLibraryApiClient(http, new Uri("https://library.example/"));

        var exception = await ThrowsAsync<AssetLibraryApiException>(async () =>
            await client.GetCurrentUserAsync());
        Equal(HttpStatusCode.UnprocessableEntity, exception.StatusCode);
        Equal("请求无效", exception.Title);
        Equal("名称不能为空。", exception.Detail);
        Equal("invalid_name", exception.Code);
        Equal("trace-7", exception.TraceId);
        Equal("required", exception.ValidationErrors!["name"][0]);
    }

    private static async Task DerivativeAndLutRoutesStreamContentAsync()
    {
        var thumbnail = new byte[] { 1, 2, 3, 4 };
        var lutBytes = Encoding.UTF8.GetBytes("LUT_1D_SIZE 2\n0 0 0\n1 1 1\n");
        var call = 0;
        using var http = new HttpClient(new StubHandler(async (request, cancellationToken) =>
        {
            call++;
            if (call == 1)
            {
                Equal(HttpMethod.Put, request.Method);
                Equal($"/api/assets/{AssetId:D}/derivatives/thumbnail", request.RequestUri!.AbsolutePath);
                True(request.RequestUri.Query.Contains($"assetVersionId={VersionId:D}", StringComparison.Ordinal));
                True(request.RequestUri.Query.Contains("sizeBytes=4", StringComparison.Ordinal));
                Equal("image/webp", request.Content!.Headers.ContentType?.MediaType);
                await using var received = new MemoryStream();
                await request.Content.CopyToAsync(received, cancellationToken);
                SequenceEqual(thumbnail, received.ToArray());
                return JsonResponse(DerivativesJson());
            }

            if (call == 2)
            {
                Equal(HttpMethod.Get, request.Method);
                Equal($"/api/assets/{AssetId:D}/derivatives/thumbnail", request.RequestUri!.AbsolutePath);
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(thumbnail)
                };
                response.Headers.ETag = new EntityTagHeaderValue($"\"{new string('B', 64)}\"");
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/webp");
                response.Content.Headers.LastModified = new DateTimeOffset(2026, 8, 28, 0, 0, 0, TimeSpan.Zero);
                return response;
            }

            if (call == 3)
            {
                Equal(HttpMethod.Post, request.Method);
                Equal("/api/luts", request.RequestUri!.AbsolutePath);
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                using var json = JsonDocument.Parse(body);
                Equal("D-Log", json.RootElement.GetProperty("name").GetString());
                Equal("D-Log.cube", json.RootElement.GetProperty("originalFileName").GetString());
                return JsonResponse(TeamLutJson());
            }

            Equal(HttpMethod.Put, request.Method);
            Equal($"/api/luts/{LutId:D}/content", request.RequestUri!.AbsolutePath);
            Equal("application/x-cube", request.Content!.Headers.ContentType?.MediaType);
            await using var lutReceived = new MemoryStream();
            await request.Content.CopyToAsync(lutReceived, cancellationToken);
            SequenceEqual(lutBytes, lutReceived.ToArray());
            return JsonResponse(TeamLutJson());
        }));
        var client = new AssetLibraryApiClient(http, new Uri("https://library.example/"));

        await using (var source = new MemoryStream(thumbnail, writable: false))
        {
            var derivatives = await client.UploadAssetThumbnailAsync(
                AssetId,
                VersionId,
                new string('B', 64),
                source,
                thumbnail.LongLength);
            Equal(AssetDerivativeState.Ready, derivatives.Thumbnail.State);
        }

        await using (var destination = new MemoryStream())
        {
            var metadata = await client.CopyAssetDerivativeAsync(
                AssetId,
                AssetDerivativeKind.Thumbnail,
                destination);
            SequenceEqual(thumbnail, destination.ToArray());
            Equal(new string('B', 64), metadata.ETag);
            Equal("image/webp", metadata.ContentType);
        }

        var lut = await client.CreateTeamLutAsync(new CreateTeamLutRequest(
            "D-Log",
            "D-Log.cube",
            lutBytes.LongLength,
            new string('C', 64)));
        Equal(LutId, lut.Id);
        await using (var source = new MemoryStream(lutBytes, writable: false))
        {
            lut = await client.UploadTeamLutContentAsync(LutId, source, lutBytes.LongLength);
            Equal(TeamLutState.Active, lut.State);
        }

        Equal(4, call);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static string CurrentUserJson() => $$"""
        {
          "id":"{{UserId:D}}",
          "username":"member",
          "displayName":"成员",
          "email":null,
          "bio":null,
          "birthday":null,
          "gender":"custom",
          "customGender":"保密",
          "contact":null,
          "birthdayVisibility":"private",
          "genderVisibility":"team",
          "contactVisibility":"private",
          "hasAvatar":false,
          "isAdmin":false,
          "isEnabled":true,
          "mustChangePassword":false,
          "permissions":["assets.browse"]
        }
        """;

    private static string AssetJson(string state) => $$"""
        {
          "id":"{{AssetId:D}}",
          "currentVersionId":"{{VersionId:D}}",
          "name":"音效",
          "category":"sound-effect",
          "originalFileName":"sound.wav",
          "extension":".wav",
          "sizeBytes":4,
          "durationSeconds":42.5,
          "contentHash":"{{new string('A', 64)}}",
          "objectKey":"originals/sound.wav",
          "hasOriginal":true,
          "notes":null,
          "folderId":"{{FolderId:D}}",
          "tags":["常用"],
          "uploadedBy":{"id":"{{UserId:D}}","username":"member","displayName":"成员"},
          "uploadedAt":"2026-08-27T00:00:00+00:00",
          "updatedAt":"2026-08-27T00:00:00+00:00",
          "version":1,
          "previousVersionCount":0,
          "state":"{{state}}",
          "recycledAt":null,
          "purgeAfter":null
        }
        """;

    private static string FolderJson() => $$"""
        {
          "id":"{{FolderId:D}}",
          "parentId":null,
          "name":"云端素材",
          "createdBy":{"id":"{{UserId:D}}","username":"member","displayName":"成员"},
          "createdAt":"2026-08-31T00:00:00+00:00",
          "updatedAt":"2026-08-31T00:00:00+00:00"
        }
        """;

    private static string DerivativesJson() => $$"""
        {
          "thumbnail":{
            "kind":"thumbnail","assetVersionId":"{{VersionId:D}}","state":"ready","formatVersion":1,
            "url":"/api/assets/{{AssetId:D}}/derivatives/thumbnail","contentType":"image/webp","sizeBytes":4,
            "eTag":"{{new string('B', 64)}}","errorCode":null,"errorMessage":null,"retryCount":0,
            "updatedAt":"2026-08-28T00:00:00+00:00"
          },
          "proxy":{
            "kind":"proxy","assetVersionId":"{{VersionId:D}}","state":"queued","formatVersion":1,
            "url":null,"contentType":null,"sizeBytes":0,"eTag":null,"errorCode":null,"errorMessage":null,
            "retryCount":0,"updatedAt":"2026-08-28T00:00:00+00:00"
          }
        }
        """;

    private static string TeamLutJson() => $$"""
        {
          "id":"{{LutId:D}}","currentVersionId":"{{VersionId:D}}","name":"D-Log",
          "originalFileName":"D-Log.cube","note":null,"sizeBytes":31,"sha256":"{{new string('C', 64)}}",
          "hasContent":true,"version":1,"state":"active",
          "uploadedBy":{"id":"{{UserId:D}}","username":"member","displayName":"成员"},
          "uploadedAt":"2026-08-28T00:00:00+00:00","updatedAt":"2026-08-28T00:00:00+00:00",
          "recycledAt":null,"purgeAfter":null
        }
        """;

    private static async Task<TException> ThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException exception)
        {
            return exception;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name} to be thrown.");
    }

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

    private static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException("Sequences are not equal.");
        }
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            handle(request, cancellationToken);
    }

    private sealed class TrackingReadStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes, writable: false);

        public bool WasDisposed { get; private set; }
        public int MaximumReadRequest { get; private set; }
        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            MaximumReadRequest = Math.Max(MaximumReadRequest, count);
            return _inner.Read(buffer, offset, count);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            MaximumReadRequest = Math.Max(MaximumReadRequest, buffer.Length);
            return _inner.ReadAsync(buffer, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class StreamingResponseContent(byte[] bytes) : HttpContent
    {
        public int SerializeCallCount { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            SerializeCallCount++;
            throw new InvalidOperationException("The response was buffered instead of streamed.");
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new TrackingReadStream(bytes));

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return CreateContentReadStreamAsync();
        }

        protected override bool TryComputeLength(out long length)
        {
            length = bytes.LongLength;
            return true;
        }
    }
}
