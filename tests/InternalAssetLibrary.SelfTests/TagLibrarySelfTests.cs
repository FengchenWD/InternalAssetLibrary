using System.Net;
using System.Text;
using System.Text.Json;
using InternalAssetLibrary.Client.Core.Http;
using InternalAssetLibrary.Client.Core.LocalAssets;
using InternalAssetLibrary.Client.Core.Tags;

internal static class TagLibrarySelfTests
{
    public static void LocalLibraryPersistsUnusedTagsAndValidatesAssignments() =>
        LocalLibraryPersistsUnusedTagsAndValidatesAssignmentsAsync().GetAwaiter().GetResult();

    public static void SchemaTwoCatalogsRecoverTheirTagLibrary() =>
        SchemaTwoCatalogsRecoverTheirTagLibraryAsync().GetAwaiter().GetResult();

    public static void CloudLibraryReusesNamesAndCreatesMissingTags() =>
        CloudLibraryReusesNamesAndCreatesMissingTagsAsync().GetAwaiter().GetResult();

    private static async Task LocalLibraryPersistsUnusedTagsAndValidatesAssignmentsAsync()
    {
        var store = new InMemoryLocalAssetCatalogStore();
        var assetId = Guid.NewGuid();
        await store.SaveAsync(new LocalAssetCatalog(
            LocalAssetCatalog.CurrentSchemaVersion,
            [],
            [CreateAsset(assetId, ["历史标签"])],
            [],
            ["历史标签"]));
        using var service = new LocalAssetIndexService(store);

        var catalog = await service.CreateTagAsync("  未使用  ");
        SequenceEqual(["历史标签", "未使用"], catalog.TagLibrary);
        catalog = await service.SetTagsAsync(assetId, [" 未使用 ", "历史标签", "未使用"]);
        SequenceEqual(["历史标签", "未使用"], catalog.Assets.Single().Tags);

        await ThrowsAsync<InvalidOperationException>(() => service.CreateTagAsync("未使用"));
        catalog = await service.SetTagsAsync(assetId, ["未建标签"]);
        True(catalog.TagLibrary.Contains("未建标签", StringComparer.Ordinal));
        await ThrowsAsync<ArgumentException>(() => service.CreateTagAsync(new string('a', 33)));

        catalog = await service.RenameTagAsync("未使用", " 稍后使用 ");
        SequenceEqual(["历史标签", "未建标签", "稍后使用"], catalog.TagLibrary);
        SequenceEqual(["未建标签"], catalog.Assets.Single().Tags);
        catalog = await service.DeleteTagAsync("稍后使用");
        SequenceEqual(["历史标签", "未建标签"], catalog.TagLibrary);
        SequenceEqual(["未建标签"], catalog.Assets.Single().Tags);

        for (var index = 1; index <= 20; index++)
        {
            catalog = await service.CreateTagAsync($"标签{index:00}");
        }

        await ThrowsAsync<ArgumentException>(() => service.SetTagsAsync(
            assetId,
            catalog.TagLibrary.Take(21)));
    }

    private static async Task SchemaTwoCatalogsRecoverTheirTagLibraryAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ial-tag-migration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var filePath = Path.Combine(root, "local-assets.json");
            var legacy = new
            {
                schemaVersion = 2,
                folders = Array.Empty<object>(),
                assets = new[]
                {
                    CreateAsset(Guid.NewGuid(), [" 常用 ", "常用", "配乐"])
                },
                directories = Array.Empty<object>()
            };
            await File.WriteAllTextAsync(
                filePath,
                JsonSerializer.Serialize(legacy, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

            using var store = new JsonLocalAssetCatalogStore(filePath);
            var migrated = await store.LoadAsync();
            Equal(LocalAssetCatalog.CurrentSchemaVersion, migrated.SchemaVersion);
            SequenceEqual(["常用", "配乐"], migrated.TagLibrary);

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(filePath));
            True(document.RootElement.TryGetProperty("tagLibrary", out _));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task CloudLibraryReusesNamesAndCreatesMissingTagsAsync()
    {
        using var handler = new TagApiHandler(["团队常用"]);
        using var httpClient = new HttpClient(handler);
        var api = new AssetLibraryApiClient(httpClient, new Uri("https://library.example/"));
        var service = new CloudTagLibraryService(api);

        var ensured = await service.EnsureAsync([" 团队常用 ", "新标签", "新标签"]);
        SequenceEqual(["团队常用", "新标签"], ensured);
        Equal(1, handler.CreateRequests);
        SequenceEqual(["团队常用", "新标签"], handler.TagNames.Order(StringComparer.OrdinalIgnoreCase));

        var tagToDelete = (await service.ListAsync()).Single(tag => tag.Name == "新标签");
        await service.DeleteAsync(tagToDelete.Id);
        Equal(1, handler.DeleteRequests);
        Equal(tagToDelete.Id, handler.DeletedTagId);
        SequenceEqual(["团队常用"], handler.TagNames);
        await ThrowsAsync<ArgumentException>(() => service.DeleteAsync(Guid.Empty));

        var assetId = Guid.NewGuid();
        await service.AssignExistingAsync(assetId, [" 团队常用 ", "团队常用"]);
        Equal(1, handler.AssignmentRequests);
        Equal(assetId, handler.AssignedAssetId);
        Equal(false, handler.AssignmentCreateMissing);
        SequenceEqual(["团队常用"], handler.AssignedTags);
        Equal(1, handler.CreateRequests);
        await ThrowsAsync<ArgumentException>(() => service.AssignExistingAsync(Guid.Empty, []));
    }

    private static LocalAsset CreateAsset(Guid id, string[] tags) => new(
        id,
        Guid.NewGuid(),
        @"C:\素材\sample.mp3",
        "sample.mp3",
        "sample.mp3",
        ".mp3",
        LocalMediaType.Audio,
        100,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        LocalAssetAvailability.Available,
        tags);

    private static async Task ThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
        }
    }

    private static void True(bool value)
    {
        if (!value)
        {
            throw new InvalidOperationException("Expected true.");
        }
    }

    private static void SequenceEqual(IEnumerable<string> expected, IEnumerable<string> actual)
    {
        if (!expected.SequenceEqual(actual, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}].");
        }
    }

    private sealed class TagApiHandler : HttpMessageHandler
    {
        private readonly List<(Guid Id, string Name)> _tags;

        public TagApiHandler(IEnumerable<string> names)
        {
            _tags = names.Select(name => (Guid.NewGuid(), name)).ToList();
        }

        public int CreateRequests { get; private set; }

        public int DeleteRequests { get; private set; }

        public Guid? DeletedTagId { get; private set; }

        public int AssignmentRequests { get; private set; }

        public Guid? AssignedAssetId { get; private set; }

        public bool? AssignmentCreateMissing { get; private set; }

        public IReadOnlyList<string> AssignedTags { get; private set; } = [];

        public IEnumerable<string> TagNames => _tags.Select(tag => tag.Name);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/api/tags")
            {
                return Json(HttpStatusCode.OK, _tags.Select(tag => ToResponse(tag.Id, tag.Name)).ToArray());
            }

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/api/tags")
            {
                CreateRequests++;
                var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var name = body.RootElement.GetProperty("name").GetString()!;
                var tag = (Guid.NewGuid(), name);
                _tags.Add(tag);
                return Json(HttpStatusCode.Created, ToResponse(tag.Item1, tag.name));
            }

            if (request.Method == HttpMethod.Delete &&
                request.RequestUri?.AbsolutePath.StartsWith("/api/tags/", StringComparison.Ordinal) == true &&
                Guid.TryParse(request.RequestUri.AbsolutePath["/api/tags/".Length..], out var tagId))
            {
                DeleteRequests++;
                DeletedTagId = tagId;
                _tags.RemoveAll(tag => tag.Id == tagId);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            const string assetPrefix = "/api/assets/";
            const string tagSuffix = "/tags";
            var path = request.RequestUri?.AbsolutePath;
            if (request.Method == HttpMethod.Put &&
                path?.StartsWith(assetPrefix, StringComparison.Ordinal) == true &&
                path.EndsWith(tagSuffix, StringComparison.Ordinal) &&
                Guid.TryParse(path[assetPrefix.Length..^tagSuffix.Length], out var assetId))
            {
                AssignmentRequests++;
                AssignedAssetId = assetId;
                using var body = JsonDocument.Parse(
                    await request.Content!.ReadAsStringAsync(cancellationToken));
                AssignmentCreateMissing = body.RootElement.GetProperty("createMissing").GetBoolean();
                AssignedTags = body.RootElement.GetProperty("tags")
                    .EnumerateArray()
                    .Select(item => item.GetString()!)
                    .ToArray();
                return Json(HttpStatusCode.OK, CreateAsset(assetId, AssignedTags));
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static ApiAsset CreateAsset(Guid id, IReadOnlyList<string> tags) => new(
            id,
            id,
            "sample.mp3",
            ApiAssetCategory.Bgm,
            "sample.mp3",
            ".mp3",
            100,
            new string('0', 64),
            $"originals/{id:N}.mp3",
            true,
            null,
            tags,
            new ApiAssetUploader(Guid.NewGuid(), "tester", "Tester"),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            1,
            0,
            ApiAssetState.Active,
            null,
            null);

        private static object ToResponse(Guid id, string name) => new
        {
            id,
            name,
            createdByUserId = Guid.NewGuid(),
            createdAt = DateTimeOffset.UtcNow,
            updatedAt = DateTimeOffset.UtcNow,
            usageCount = 0
        };

        private static HttpResponseMessage Json(HttpStatusCode status, object value) => new(status)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                Encoding.UTF8,
                "application/json")
        };
    }
}
