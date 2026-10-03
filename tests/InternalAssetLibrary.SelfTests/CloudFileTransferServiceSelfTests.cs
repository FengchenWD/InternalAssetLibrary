using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InternalAssetLibrary.Client.Core.Downloads;
using InternalAssetLibrary.Client.Core.Http;
using InternalAssetLibrary.Client.Core.Transfers;

internal static class CloudFileTransferServiceSelfTests
{
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AssetId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid VersionId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid FolderId = Guid.Parse("77777777-7777-7777-7777-777777777777");

    public static void UploadHashesAndStreams() => UploadHashesAndStreamsAsync().GetAwaiter().GetResult();
    public static void FailedUploadCleansMetadata() => FailedUploadCleansMetadataAsync().GetAwaiter().GetResult();
    public static void DownloadVerifiesAndRegisters() => DownloadVerifiesAndRegistersAsync().GetAwaiter().GetResult();
    public static void UserVisibleDownloadsStayFlatAndPreserveNameCollisions() =>
        UserVisibleDownloadsStayFlatAndPreserveNameCollisionsAsync().GetAwaiter().GetResult();
    public static void UserVisibleInterruptedDownloadResumesWithRange() =>
        UserVisibleInterruptedDownloadResumesWithRangeAsync().GetAwaiter().GetResult();
    public static void ExplicitDestinationsReuseVerifiedCachedBytes() =>
        ExplicitDestinationsReuseVerifiedCachedBytesAsync().GetAwaiter().GetResult();
    public static void FailedDownloadPreservesExistingFile() =>
        FailedDownloadPreservesExistingFileAsync().GetAwaiter().GetResult();
    public static void InterruptedDownloadResumesWithRange() =>
        InterruptedDownloadResumesWithRangeAsync().GetAwaiter().GetResult();
    public static void InterruptedDirectUploadResumesRemainingParts() =>
        InterruptedDirectUploadResumesRemainingPartsAsync().GetAwaiter().GetResult();
    public static void CompletedDirectUploadReconcilesLostResponse() =>
        CompletedDirectUploadReconcilesLostResponseAsync().GetAwaiter().GetResult();

    private static async Task CompletedDirectUploadReconcilesLostResponseAsync()
    {
        var directory = Directory.CreateTempSubdirectory("ial-direct-upload-completed-").FullName;
        try
        {
            var bytes = Encoding.UTF8.GetBytes("completed object content");
            var sourcePath = Path.Combine(directory, "completed.mov");
            await File.WriteAllBytesAsync(sourcePath, bytes);
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            var sessionId = Guid.Parse("55555555-5555-5555-5555-555555555555");
            using var apiHttp = new HttpClient(new StubHandler((request, _) =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path == "/api/transfers/capabilities")
                {
                    return Task.FromResult(JsonResponse(
                        "{\"multipartUpload\":true,\"partSizeBytes\":5242880,\"sessionLifetimeHours\":24}"));
                }

                if (path == $"/api/assets/{AssetId:D}")
                {
                    return Task.FromResult(JsonResponse(AssetJson(hash, bytes.LongLength, hasOriginal: true)));
                }

                throw new InvalidOperationException($"Unexpected API request: {request.Method} {path}");
            }));
            using var directHttp = new HttpClient(new StubHandler((request, _) =>
                throw new InvalidOperationException($"Unexpected direct request: {request.RequestUri}")));
            using var registry = new JsonPersistentDownloadRegistry(Path.Combine(directory, "downloads.json"));
            using var resumeStore = new JsonDirectUploadResumeStore(Path.Combine(directory, "direct-uploads.json"));
            await resumeStore.UpsertAsync(new DirectUploadResumeRecord(
                "https://library.example/",
                sourcePath,
                bytes.LongLength,
                hash,
                AssetId,
                sessionId,
                new Dictionary<int, string> { [1] = "etag-1" },
                DateTimeOffset.UtcNow));
            var service = new CloudFileTransferService(
                new AssetLibraryApiClient(apiHttp, new Uri("https://library.example/")),
                new PersistentDownloadPathMapper(),
                registry,
                resumeStore,
                directHttp);

            var result = await service.UploadAsync(sourcePath, ApiAssetCategory.Video);

            True(result.HasOriginal);
            Equal<DirectUploadResumeRecord?>(null, await resumeStore.FindAsync(
                new Uri("https://library.example/"),
                sourcePath,
                bytes.LongLength,
                hash));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static async Task InterruptedDirectUploadResumesRemainingPartsAsync()
    {
        var directory = Directory.CreateTempSubdirectory("ial-direct-upload-resume-").FullName;
        try
        {
            const int partSize = 100_000;
            var bytes = Enumerable.Range(0, 250_000).Select(index => (byte)(index % 241)).ToArray();
            var sourcePath = Path.Combine(directory, "direct.mov");
            await File.WriteAllBytesAsync(sourcePath, bytes);
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            var sessionId = Guid.Parse("44444444-4444-4444-4444-444444444444");
            var allowRemainingParts = false;
            var createCount = 0;
            var startCount = 0;
            double? createdDuration = null;
            var uploadedParts = new List<int>();
            IReadOnlyList<int>? completedParts = null;

            using var apiHttp = new HttpClient(new StubHandler((request, cancellationToken) =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (request.Method == HttpMethod.Get && path == "/api/transfers/capabilities")
                {
                    return Task.FromResult(JsonResponse($$"""
                        {"multipartUpload":true,"partSizeBytes":{{partSize}},"sessionLifetimeHours":24}
                        """));
                }

                if (request.Method == HttpMethod.Post && path == "/api/assets")
                {
                    createCount++;
                    return CaptureCreatedAssetAsync(request, cancellationToken);
                }

                if (request.Method == HttpMethod.Get && path == $"/api/assets/{AssetId:D}")
                {
                    return Task.FromResult(JsonResponse(AssetJson(hash, bytes.LongLength, hasOriginal: false)));
                }

                if (request.Method == HttpMethod.Post && path == $"/api/assets/{AssetId:D}/direct-upload")
                {
                    startCount++;
                    return Task.FromResult(JsonResponse(SessionJson(
                        sessionId,
                        hash,
                        bytes.LongLength,
                        partSize)));
                }

                if (request.Method == HttpMethod.Get && path == $"/api/direct-uploads/{sessionId:D}")
                {
                    return Task.FromResult(JsonResponse(SessionJson(
                        sessionId,
                        hash,
                        bytes.LongLength,
                        partSize)));
                }

                var partPrefix = $"/api/direct-uploads/{sessionId:D}/parts/";
                if (request.Method == HttpMethod.Get && path.StartsWith(partPrefix, StringComparison.Ordinal))
                {
                    var partNumber = int.Parse(path[partPrefix.Length..]);
                    return Task.FromResult(JsonResponse($$"""
                        {"sessionId":"{{sessionId:D}}","partNumber":{{partNumber}},"url":"https://cos.example/upload/{{partNumber}}","expiresAt":"2099-01-01T00:00:00Z"}
                        """));
                }

                if (request.Method == HttpMethod.Post &&
                    path == $"/api/direct-uploads/{sessionId:D}/complete")
                {
                    return CompleteAsync(request, cancellationToken);
                }

                throw new InvalidOperationException($"Unexpected API request: {request.Method} {path}");

                async Task<HttpResponseMessage> CompleteAsync(
                    HttpRequestMessage completeRequest,
                    CancellationToken completeCancellationToken)
                {
                    var json = await completeRequest.Content!.ReadAsStringAsync(completeCancellationToken);
                    using var document = JsonDocument.Parse(json);
                    completedParts = document.RootElement.GetProperty("parts")
                        .EnumerateArray()
                        .Select(item => item.GetProperty("partNumber").GetInt32())
                        .ToArray();
                    return JsonResponse(AssetJson(hash, bytes.LongLength, hasOriginal: true));
                }

                async Task<HttpResponseMessage> CaptureCreatedAssetAsync(
                    HttpRequestMessage createRequest,
                    CancellationToken createCancellationToken)
                {
                    var json = await createRequest.Content!.ReadAsStringAsync(createCancellationToken);
                    using var document = JsonDocument.Parse(json);
                    createdDuration = document.RootElement.GetProperty("durationSeconds").GetDouble();
                    return JsonResponse(AssetJson(hash, bytes.LongLength, hasOriginal: false));
                }
            }));
            using var directHttp = new HttpClient(new StubHandler(async (request, cancellationToken) =>
            {
                var partNumber = int.Parse(request.RequestUri!.AbsolutePath.Split('/')[^1]);
                if (partNumber == 2 && !allowRemainingParts)
                {
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                }

                var offset = (partNumber - 1) * partSize;
                var expected = bytes[offset..Math.Min(offset + partSize, bytes.Length)];
                var actual = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                SequenceEqual(expected, actual);
                uploadedParts.Add(partNumber);
                var response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue($"\"etag-{partNumber}\"");
                return response;
            }));
            using var registry = new JsonPersistentDownloadRegistry(Path.Combine(directory, "downloads.json"));
            using var resumeStore = new JsonDirectUploadResumeStore(Path.Combine(directory, "direct-uploads.json"));
            var service = new CloudFileTransferService(
                new AssetLibraryApiClient(apiHttp, new Uri("https://library.example/")),
                new PersistentDownloadPathMapper(),
                registry,
                resumeStore,
                directHttp,
                (_, _) => Task.FromResult<double?>(321.5));

            await ThrowsAsync<HttpRequestException>(() =>
                service.UploadAsync(sourcePath, ApiAssetCategory.Video));
            var saved = await resumeStore.FindAsync(
                new Uri("https://library.example/"),
                sourcePath,
                bytes.LongLength,
                hash);
            True(saved is not null);
            SequenceEqual([1], saved!.CompletedParts.Keys);

            allowRemainingParts = true;
            var uploaded = await service.UploadAsync(sourcePath, ApiAssetCategory.Video);

            True(uploaded.HasOriginal);
            Equal(1, createCount);
            Equal(1, startCount);
            Equal<double?>(321.5, createdDuration);
            SequenceEqual([1, 2, 3], uploadedParts);
            SequenceEqual([1, 2, 3], completedParts!);
            Equal<DirectUploadResumeRecord?>(null, await resumeStore.FindAsync(
                new Uri("https://library.example/"),
                sourcePath,
                bytes.LongLength,
                hash));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static async Task UploadHashesAndStreamsAsync()
    {
        var directory = Directory.CreateTempSubdirectory("ial-transfer-upload-").FullName;
        try
        {
            var bytes = Enumerable.Range(0, 300_000).Select(index => (byte)(index % 251)).ToArray();
            var sourcePath = Path.Combine(directory, "sample video.mp4");
            await File.WriteAllBytesAsync(sourcePath, bytes);
            var expectedHash = Convert.ToHexString(SHA256.HashData(bytes));
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
                    Equal("sample video", body.RootElement.GetProperty("name").GetString());
                    Equal("video", body.RootElement.GetProperty("category").GetString());
                    Equal(expectedHash, body.RootElement.GetProperty("contentHash").GetString());
                    Equal(125.75, body.RootElement.GetProperty("durationSeconds").GetDouble());
                    Equal("统一备注", body.RootElement.GetProperty("notes").GetString());
                    Equal("常用", body.RootElement.GetProperty("tags")[0].GetString());
                    Equal(FolderId, body.RootElement.GetProperty("folderId").GetGuid());
                    return JsonResponse(AssetJson(expectedHash, bytes.LongLength, hasOriginal: false));
                }

                Equal(HttpMethod.Put, request.Method);
                Equal($"/api/assets/{AssetId:D}/content", request.RequestUri!.AbsolutePath);
                Equal(bytes.LongLength, request.Content!.Headers.ContentLength);
                await using var received = new MemoryStream();
                await request.Content.CopyToAsync(received, cancellationToken);
                SequenceEqual(bytes, received.ToArray());
                return JsonResponse(AssetJson(expectedHash, bytes.LongLength, hasOriginal: true));
            }));
            using var registry = new JsonPersistentDownloadRegistry(Path.Combine(directory, "downloads.json"));
            var service = CreateService(
                http,
                registry,
                (path, _) =>
                {
                    Equal(sourcePath, path);
                    return Task.FromResult<double?>(125.75);
                });
            var progress = new ProgressCollector();

            var uploaded = await service.UploadAsync(
                sourcePath,
                ApiAssetCategory.Video,
                "统一备注",
                ["常用"],
                progress,
                folderId: FolderId);

            Equal(AssetId, uploaded.Id);
            Equal(2, call);
            Equal(CloudTransferStage.Hashing, progress.Values[0].Stage);
            True(progress.Values.Any(item =>
                item.Stage == CloudTransferStage.Hashing && item.BytesProcessed == bytes.LongLength));
            True(progress.Values.Any(item =>
                item.Stage == CloudTransferStage.Uploading && item.BytesProcessed == bytes.LongLength));
            Equal(CloudTransferStage.Completed, progress.Values[^1].Stage);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static async Task FailedUploadCleansMetadataAsync()
    {
        var directory = Directory.CreateTempSubdirectory("ial-transfer-cleanup-").FullName;
        try
        {
            var sourcePath = Path.Combine(directory, "broken.mp4");
            var bytes = Encoding.UTF8.GetBytes("upload content");
            await File.WriteAllBytesAsync(sourcePath, bytes);
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            var calls = new List<string>();
            var durationProbeCalls = 0;
            var committed = false;

            using var http = new HttpClient(new StubHandler((request, _) =>
            {
                calls.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
                return request.Method.Method switch
                {
                    "GET" => Task.FromResult(JsonResponse(AssetJson(hash, bytes.LongLength, hasOriginal: committed))),
                    "POST" => Task.FromResult(JsonResponse(AssetJson(hash, bytes.LongLength, hasOriginal: false))),
                    "PUT" => Task.FromResult(ProblemResponse("initial_upload_failed")),
                    "DELETE" when request.RequestUri.AbsolutePath.EndsWith("/permanent", StringComparison.Ordinal) =>
                        Task.FromResult(ProblemResponse("cleanup_failed")),
                    "DELETE" => Task.FromResult(JsonResponse(AssetJson(
                        hash,
                        bytes.LongLength,
                        hasOriginal: false,
                        state: "recycled"))),
                    _ => throw new InvalidOperationException("Unexpected request.")
                };
            }));
            using var registry = new JsonPersistentDownloadRegistry(Path.Combine(directory, "downloads.json"));
            var service = CreateService(
                http,
                registry,
                (_, _) =>
                {
                    durationProbeCalls++;
                    throw new InvalidDataException("Unreadable media metadata.");
                });

            var exception = await ThrowsAsync<AssetLibraryApiException>(() =>
                service.UploadAsync(sourcePath, ApiAssetCategory.Video));

            Equal("initial_upload_failed", exception.Code);
            Equal(1, durationProbeCalls);
            SequenceEqual(
                [
                    "POST /api/assets",
                    $"PUT /api/assets/{AssetId:D}/content",
                    $"GET /api/assets/{AssetId:D}",
                    $"DELETE /api/assets/{AssetId:D}",
                    $"DELETE /api/assets/{AssetId:D}/permanent"
                ],
                calls);
            committed = true;
            calls.Clear();
            await ThrowsAsync<AssetLibraryApiException>(() => service.UploadAsync(sourcePath, ApiAssetCategory.Video));
            False(calls.Any(call => call.StartsWith("DELETE", StringComparison.Ordinal)));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static async Task DownloadVerifiesAndRegistersAsync()
    {
        var directory = Directory.CreateTempSubdirectory("ial-transfer-download-").FullName;
        try
        {
            var bytes = Enumerable.Range(0, 250_000).Select(index => (byte)(index % 239)).ToArray();
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            var asset = Asset(hash, bytes.LongLength, "download.mov");
            var responseContent = new StreamingResponseContent(bytes);

            using var http = new HttpClient(new StubHandler((request, _) =>
            {
                Equal(HttpMethod.Get, request.Method);
                Equal(
                    $"/api/assets/{AssetId:D}/versions/{VersionId:D}/download",
                    request.RequestUri!.AbsolutePath);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = responseContent
                });
            }));
            var registryPath = Path.Combine(directory, "registry", "downloads.json");
            using var registry = new JsonPersistentDownloadRegistry(registryPath);
            var mapper = new PersistentDownloadPathMapper();
            var service = new CloudFileTransferService(
                new AssetLibraryApiClient(http, new Uri("https://library.example/")),
                mapper,
                registry);
            var persistentDirectory = Path.Combine(directory, "persistent");
            var targetPath = mapper.PrepareTargetDirectory(
                persistentDirectory,
                new DownloadAssetKey(AssetId, VersionId),
                asset.OriginalFileName);
            await File.WriteAllTextAsync(targetPath, "old content");
            var progress = new ProgressCollector();

            var mapping = await service.DownloadAsync(asset, persistentDirectory, progress);
            var found = await service.FindDownloadedAsync(asset);

            SequenceEqual(bytes, await File.ReadAllBytesAsync(targetPath));
            False(File.Exists(targetPath + ".part"));
            Equal(targetPath, mapping.FullPath);
            Equal(hash, mapping.Sha256);
            Equal(mapping, found);
            Equal(0, responseContent.SerializeCallCount);
            Equal(CloudTransferStage.Downloading, progress.Values[0].Stage);
            Equal(CloudTransferStage.Completed, progress.Values[^1].Stage);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static async Task FailedDownloadPreservesExistingFileAsync()
    {
        var directory = Directory.CreateTempSubdirectory("ial-transfer-invalid-").FullName;
        try
        {
            var expectedBytes = Encoding.UTF8.GetBytes("expected bytes");
            var receivedBytes = Encoding.UTF8.GetBytes("changed! bytes");
            Equal(expectedBytes.LongLength, receivedBytes.LongLength);
            var asset = Asset(
                Convert.ToHexString(SHA256.HashData(expectedBytes)),
                expectedBytes.LongLength,
                "protected.mp3");
            using var http = new HttpClient(new StubHandler((_, _) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(receivedBytes)
                })));
            using var registry = new JsonPersistentDownloadRegistry(Path.Combine(directory, "downloads.json"));
            var mapper = new PersistentDownloadPathMapper();
            var service = new CloudFileTransferService(
                new AssetLibraryApiClient(http, new Uri("https://library.example/")),
                mapper,
                registry);
            var persistentDirectory = Path.Combine(directory, "persistent");
            var targetPath = mapper.PrepareTargetDirectory(
                persistentDirectory,
                new DownloadAssetKey(AssetId, VersionId),
                asset.OriginalFileName);
            var originalTarget = Encoding.UTF8.GetBytes("keep this existing file");
            await File.WriteAllBytesAsync(targetPath, originalTarget);
            await File.WriteAllTextAsync(targetPath + ".part", "stale partial file");

            await ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(asset, persistentDirectory));

            SequenceEqual(originalTarget, await File.ReadAllBytesAsync(targetPath));
            False(File.Exists(targetPath + ".part"));
            Equal(null, await service.FindDownloadedAsync(asset));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static async Task UserVisibleDownloadsStayFlatAndPreserveNameCollisionsAsync()
    {
        var directory = Directory.CreateTempSubdirectory("ial-transfer-flat-").FullName;
        try
        {
            var bytes = Encoding.UTF8.GetBytes("flat user-visible download");
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            var first = Asset(hash, bytes.LongLength, "shared-name.mov");
            var second = first with
            {
                Id = Guid.Parse("44444444-4444-4444-4444-444444444444"),
                CurrentVersionId = Guid.Parse("55555555-5555-5555-5555-555555555555")
            };
            using var http = new HttpClient(new StubHandler((_, _) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(bytes)
                })));
            using var registry = new JsonPersistentDownloadRegistry(Path.Combine(directory, "downloads.json"));
            var mapper = new PersistentDownloadPathMapper();
            var service = new CloudFileTransferService(
                new AssetLibraryApiClient(http, new Uri("https://library.example/")),
                mapper,
                registry);
            var targetDirectory = Path.Combine(directory, "selected");

            var firstMapping = await service.DownloadToUserDirectoryAsync(first, targetDirectory);
            var secondMapping = await service.DownloadToUserDirectoryAsync(second, targetDirectory);

            Equal(Path.Combine(targetDirectory, "shared-name.mov"), firstMapping.FullPath);
            Equal(Path.Combine(targetDirectory, "shared-name (2).mov"), secondMapping.FullPath);
            SequenceEqual(bytes, await File.ReadAllBytesAsync(firstMapping.FullPath));
            SequenceEqual(bytes, await File.ReadAllBytesAsync(secondMapping.FullPath));
            Equal(0, Directory.GetDirectories(targetDirectory).Length);
            Equal(2, Directory.GetFiles(targetDirectory).Length);
            Equal(firstMapping, await registry.FindAsync(new DownloadAssetKey(first.Id, first.CurrentVersionId)));
            Equal(secondMapping, await registry.FindAsync(new DownloadAssetKey(second.Id, second.CurrentVersionId)));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static async Task ExplicitDestinationsReuseVerifiedCachedBytesAsync()
    {
        var directory = Directory.CreateTempSubdirectory("ial-transfer-explicit-cache-").FullName;
        try
        {
            var bytes = Encoding.UTF8.GetBytes("reusable verified download");
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            var asset = Asset(hash, bytes.LongLength, "cached.mov");
            var requestCount = 0;
            using var http = new HttpClient(new StubHandler((_, _) =>
            {
                requestCount++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(bytes)
                });
            }));
            using var registry = new JsonPersistentDownloadRegistry(Path.Combine(directory, "downloads.json"));
            var service = new CloudFileTransferService(
                new AssetLibraryApiClient(http, new Uri("https://library.example/")),
                new PersistentDownloadPathMapper(),
                registry);
            var firstDirectory = Path.Combine(directory, "first");
            var secondDirectory = Path.Combine(directory, "second");

            var first = await service.DownloadToUserDirectoryAsync(asset, firstDirectory);
            Directory.CreateDirectory(secondDirectory);
            await File.WriteAllBytesAsync(Path.Combine(secondDirectory, asset.OriginalFileName), [99]);
            var second = await service.DownloadToUserDirectoryAsync(asset, secondDirectory);

            Equal(1, requestCount);
            Equal(Path.Combine(firstDirectory, "cached.mov"), first.FullPath);
            Equal(Path.Combine(secondDirectory, "cached (2).mov"), second.FullPath);
            SequenceEqual(bytes, await File.ReadAllBytesAsync(second.FullPath));
            Equal(0, Directory.GetDirectories(secondDirectory).Length);
            Equal(second, await registry.FindAsync(new DownloadAssetKey(asset.Id, asset.CurrentVersionId)));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static async Task UserVisibleInterruptedDownloadResumesWithRangeAsync()
    {
        var directory = Directory.CreateTempSubdirectory("ial-transfer-flat-resume-").FullName;
        try
        {
            var bytes = Enumerable.Range(0, 300_000).Select(index => (byte)(index % 229)).ToArray();
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            var asset = Asset(hash, bytes.LongLength, "resume-flat.mov");
            using var registry = new JsonPersistentDownloadRegistry(Path.Combine(directory, "downloads.json"));
            var mapper = new PersistentDownloadPathMapper();
            var targetDirectory = Path.Combine(directory, "selected");
            _ = mapper.PrepareUserVisibleDirectory(targetDirectory, asset.OriginalFileName);
            var partPath = mapper.GetUserVisiblePartialPath(
                targetDirectory,
                new DownloadAssetKey(asset.Id, asset.CurrentVersionId),
                asset.OriginalFileName);
            const int resumeOffset = 98_765;
            await File.WriteAllBytesAsync(partPath, bytes[..resumeOffset]);

            using var http = new HttpClient(new StubHandler((request, _) =>
            {
                Equal((long?)resumeOffset, request.Headers.Range?.Ranges.Single().From);
                var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent(bytes[resumeOffset..])
                };
                response.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(
                    resumeOffset,
                    bytes.LongLength - 1,
                    bytes.LongLength);
                return Task.FromResult(response);
            }));
            var service = new CloudFileTransferService(
                new AssetLibraryApiClient(http, new Uri("https://library.example/")),
                mapper,
                registry);

            var mapping = await service.DownloadToUserDirectoryAsync(asset, targetDirectory);

            Equal(Path.Combine(targetDirectory, asset.OriginalFileName), mapping.FullPath);
            SequenceEqual(bytes, await File.ReadAllBytesAsync(mapping.FullPath));
            False(File.Exists(partPath));
            Equal(0, Directory.GetDirectories(targetDirectory).Length);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static async Task InterruptedDownloadResumesWithRangeAsync()
    {
        var directory = Directory.CreateTempSubdirectory("ial-transfer-resume-").FullName;
        try
        {
            var bytes = Enumerable.Range(0, 400_000).Select(index => (byte)(index % 233)).ToArray();
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            var asset = Asset(hash, bytes.LongLength, "resume.mov");
            using var registry = new JsonPersistentDownloadRegistry(Path.Combine(directory, "downloads.json"));
            var mapper = new PersistentDownloadPathMapper();
            var persistentDirectory = Path.Combine(directory, "persistent");
            var targetPath = mapper.PrepareTargetDirectory(
                persistentDirectory,
                new DownloadAssetKey(AssetId, VersionId),
                asset.OriginalFileName);
            const int resumeOffset = 123_456;
            await File.WriteAllBytesAsync(targetPath + ".part", bytes[..resumeOffset]);

            using var http = new HttpClient(new StubHandler((request, _) =>
            {
                Equal((long?)resumeOffset, request.Headers.Range?.Ranges.Single().From);
                var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent(bytes[resumeOffset..])
                };
                response.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(
                    resumeOffset,
                    bytes.LongLength - 1,
                    bytes.LongLength);
                return Task.FromResult(response);
            }));
            var service = new CloudFileTransferService(
                new AssetLibraryApiClient(http, new Uri("https://library.example/")),
                mapper,
                registry);

            var mapping = await service.DownloadAsync(asset, persistentDirectory);

            SequenceEqual(bytes, await File.ReadAllBytesAsync(targetPath));
            Equal(hash, mapping.Sha256);
            False(File.Exists(targetPath + ".part"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static CloudFileTransferService CreateService(
        HttpClient http,
        IPersistentDownloadRegistry registry,
        Func<string, CancellationToken, Task<double?>>? durationProbe = null) => new(
        new AssetLibraryApiClient(http, new Uri("https://library.example/")),
        new PersistentDownloadPathMapper(),
        registry,
        durationProbe: durationProbe);

    private static ApiAsset Asset(string hash, long sizeBytes, string fileName) => new(
        AssetId,
        VersionId,
        Path.GetFileNameWithoutExtension(fileName),
        Path.GetExtension(fileName).Equals(".mp3", StringComparison.OrdinalIgnoreCase)
            ? ApiAssetCategory.Bgm
            : ApiAssetCategory.Video,
        fileName,
        Path.GetExtension(fileName),
        sizeBytes,
        hash,
        $"originals/{fileName}",
        true,
        null,
        [],
        new ApiAssetUploader(UserId, "member", "成员"),
        DateTimeOffset.Parse("2026-08-27T00:00:00+00:00"),
        DateTimeOffset.Parse("2026-08-27T00:00:00+00:00"),
        1,
        0,
        ApiAssetState.Active,
        null,
        null);

    private static string AssetJson(
        string hash,
        long sizeBytes,
        bool hasOriginal,
        string state = "active") => $$"""
        {
          "id":"{{AssetId:D}}",
          "currentVersionId":"{{VersionId:D}}",
          "name":"sample",
          "category":"video",
          "originalFileName":"sample.mp4",
          "extension":".mp4",
          "sizeBytes":{{sizeBytes}},
          "contentHash":"{{hash}}",
          "objectKey":"originals/sample.mp4",
          "hasOriginal":{{hasOriginal.ToString().ToLowerInvariant()}},
          "notes":null,
          "tags":[],
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

    private static string SessionJson(
        Guid sessionId,
        string hash,
        long sizeBytes,
        int partSize) => $$"""
        {
          "id":"{{sessionId:D}}",
          "assetId":"{{AssetId:D}}",
          "assetVersionId":"{{VersionId:D}}",
          "sizeBytes":{{sizeBytes}},
          "sha256":"{{hash}}",
          "partSizeBytes":{{partSize}},
          "partCount":{{(sizeBytes + partSize - 1) / partSize}},
          "expiresAt":"2099-01-01T00:00:00Z"
        }
        """;

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage ProblemResponse(string code) => new(HttpStatusCode.InternalServerError)
    {
        Content = new StringContent(
            JsonSerializer.Serialize(new { title = "failed", detail = "test failure", code }),
            Encoding.UTF8,
            "application/problem+json")
    };

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

    private sealed class ProgressCollector : IProgress<CloudTransferProgress>
    {
        public List<CloudTransferProgress> Values { get; } = [];

        public void Report(CloudTransferProgress value) => Values.Add(value);
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            handle(request, cancellationToken);
    }

    private sealed class StreamingResponseContent(byte[] bytes) : HttpContent
    {
        public int SerializeCallCount { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            SerializeCallCount++;
            throw new InvalidOperationException("The download response was buffered.");
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));

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
