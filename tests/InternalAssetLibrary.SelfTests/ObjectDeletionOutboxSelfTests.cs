using InternalAssetLibrary.Server.Data;
using InternalAssetLibrary.Server.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

internal static class ObjectDeletionOutboxSelfTests
{
    public static void FailedDeletesRemainQueuedAndRetry() =>
        FailedDeletesRemainQueuedAndRetryAsync().GetAwaiter().GetResult();

    public static void SchemaOneDataNormalizesTheOutbox() =>
        SchemaOneDataNormalizesTheOutboxAsync().GetAwaiter().GetResult();

    private static async Task FailedDeletesRemainQueuedAndRetryAsync()
    {
        var createdAt = new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);
        var root = Path.Combine(Path.GetTempPath(), $"ial-outbox-retry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var objects = new FailingObjectStore("originals/history.mp3");
        try
        {
            using (var store = CreateDataStore(root, "state.json"))
            {
                await store.InitializeAsync();
                using var outbox = new ObjectDeletionOutbox(
                    store,
                    objects,
                    NullLogger<ObjectDeletionOutbox>.Instance);
                var queued = await store.UpdateAsync(state =>
                    outbox.Enqueue(
                        state,
                        ["originals/current.mp3", "originals/history.mp3", "originals/history.mp3"],
                        createdAt));
                Equal(2, queued.Length);

                await outbox.RetryPendingAsync(CancellationToken.None);

                var pending = await store.ReadAsync(state => state.PendingObjectDeletions.ToArray());
                Equal(1, pending.Length);
                var failed = pending.Single();
                Equal("originals/history.mp3", failed.ObjectKey);
                Equal(createdAt, failed.CreatedAt);
                Equal(1, failed.FailureCount);
                True(failed.LastAttemptAt is not null);
            }

            using (var restartedStore = CreateDataStore(root, "state.json"))
            {
                await restartedStore.InitializeAsync();
                var restored = await restartedStore.ReadAsync(state => state.PendingObjectDeletions.ToArray());
                Equal(1, restored.Length);
                using var restartedOutbox = new ObjectDeletionOutbox(
                    restartedStore,
                    objects,
                    NullLogger<ObjectDeletionOutbox>.Instance);
                await restartedOutbox.RetryPendingAsync(CancellationToken.None);
                var remaining = await restartedStore.ReadAsync(state => state.PendingObjectDeletions.Count);
                Equal(0, remaining);
            }

            Equal(1, objects.Attempts["originals/current.mp3"]);
            Equal(2, objects.Attempts["originals/history.mp3"]);
            True(objects.Deleted.SetEquals(["originals/current.mp3", "originals/history.mp3"]));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task SchemaOneDataNormalizesTheOutboxAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ial-outbox-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await AssertStateLoadsAsync(root, "missing.json", "{\"schemaVersion\":1}", expectRewrite: false);
            await AssertStateLoadsAsync(
                root,
                "null.json",
                "{\"schemaVersion\":1,\"pendingObjectDeletions\":null}",
                expectRewrite: true);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task AssertStateLoadsAsync(
        string root,
        string fileName,
        string json,
        bool expectRewrite)
    {
        var path = Path.Combine(root, fileName);
        await File.WriteAllTextAsync(path, json);
        using var store = CreateDataStore(root, fileName);

        await store.InitializeAsync();
        var pending = await store.ReadAsync(state => state.PendingObjectDeletions.ToArray());
        Equal(0, pending.Length);

        if (expectRewrite)
        {
            var persisted = await File.ReadAllTextAsync(path);
            True(persisted.Contains("\"pendingObjectDeletions\": []", StringComparison.Ordinal));
        }
    }

    private static AtomicJsonDataStore CreateDataStore(string root, string fileName)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DevelopmentStorage:DataPath"] = fileName
            })
            .Build();
        return new AtomicJsonDataStore(
            new TestHostEnvironment(root),
            configuration,
            NullLogger<AtomicJsonDataStore>.Instance);
    }

    private sealed class FailingObjectStore(string failOnceKey) : IObjectStore
    {
        private bool _failed;

        public Dictionary<string, int> Attempts { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Deleted { get; } = new(StringComparer.Ordinal);

        public string StorageKind => "test";

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
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<Stream?>(null);

        public ValueTask<ObjectDownloadUrl?> CreateDownloadUrlAsync(
            string objectKey,
            string? downloadFileName = null,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ObjectDownloadUrl?>(null);

        public Task DeleteAsync(string objectKey, CancellationToken cancellationToken = default)
        {
            Attempts[objectKey] = Attempts.GetValueOrDefault(objectKey) + 1;
            if (!_failed && objectKey.Equals(failOnceKey, StringComparison.Ordinal))
            {
                _failed = true;
                throw new IOException("Simulated transient object deletion failure.");
            }

            Deleted.Add(objectKey);
            return Task.CompletedTask;
        }
    }

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "InternalAssetLibrary.SelfTests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static void True(bool condition)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Expected condition to be true.");
        }
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
        }
    }
}
