using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using InternalAssetLibrary.Server.Data;

internal static class SqliteAppDataStoreSelfTests
{
    public static void ConcurrentWritesSurviveRestart() =>
        ConcurrentWritesSurviveRestartAsync().GetAwaiter().GetResult();

    public static void VerifiedBackupCanBeRestored() =>
        VerifiedBackupCanBeRestoredAsync().GetAwaiter().GetResult();

    private static async Task ConcurrentWritesSurviveRestartAsync()
    {
        var directory = Directory.CreateTempSubdirectory("ial-sqlite-concurrency-").FullName;
        try
        {
            var databasePath = Path.Combine(directory, "library.db");
            using (var store = CreateStore(directory, databasePath))
            {
                await store.InitializeAsync();
                await Task.WhenAll(Enumerable.Range(0, 32).Select(index => store.UpdateAsync(state =>
                {
                    state.AuditLog.Add(Audit(index));
                    return true;
                })));
                Equal(32, await store.ReadAsync(state => state.AuditLog.Count));
            }

            using (var restarted = CreateStore(directory, databasePath))
            {
                await restarted.InitializeAsync();
                var actions = await restarted.ReadAsync(state => state.AuditLog
                    .OrderBy(item => item.OccurredAt)
                    .Select(item => item.Action)
                    .ToArray());
                SequenceEqual(Enumerable.Range(0, 32).Select(index => $"test.{index}"), actions);
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static async Task VerifiedBackupCanBeRestoredAsync()
    {
        var directory = Directory.CreateTempSubdirectory("ial-sqlite-backup-").FullName;
        try
        {
            var databasePath = Path.Combine(directory, "library.db");
            var backupPath = Path.Combine(directory, "backups", "verified.db");
            SqliteBackupResult backup;
            using (var store = CreateStore(directory, databasePath))
            {
                await store.InitializeAsync();
                await store.UpdateAsync(state =>
                {
                    state.AuditLog.Add(Audit(7));
                    return true;
                });
                backup = await store.CreateVerifiedBackupAsync(backupPath);
            }

            True(File.Exists(backup.Path));
            Equal(new FileInfo(backup.Path).Length, backup.SizeBytes);
            Equal(64, backup.Sha256.Length);
            True(backup.Revision >= 2);

            using var restored = CreateStore(directory, backup.Path);
            await restored.InitializeAsync();
            var entry = await restored.ReadAsync(state => state.AuditLog.Single());
            Equal("test.7", entry.Action);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static SqliteAppDataStore CreateStore(string contentRoot, string databasePath)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Sqlite:DatabasePath"] = databasePath
            })
            .Build();
        return new SqliteAppDataStore(
            new TestHostEnvironment(contentRoot),
            configuration,
            NullLogger<SqliteAppDataStore>.Instance);
    }

    private static AuditRecord Audit(int index) => new()
    {
        Id = Guid.NewGuid(),
        Action = $"test.{index}",
        TargetType = "self-test",
        TargetId = index.ToString(),
        OccurredAt = DateTimeOffset.UnixEpoch.AddSeconds(index)
    };

    private static void True(bool condition)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Expected true.");
        }
    }

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

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "InternalAssetLibrary.SelfTests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
