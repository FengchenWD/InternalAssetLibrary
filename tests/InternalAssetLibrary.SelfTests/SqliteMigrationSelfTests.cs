using InternalAssetLibrary.Server.Data;
using Microsoft.Data.Sqlite;

internal static class SqliteMigrationSelfTests
{
    public static void UpgradeDowngradeAndAudit() => RunUpgradeAsync().GetAwaiter().GetResult();
    public static void FailureRollsBack() => RunFailureAsync().GetAwaiter().GetResult();

    private static async Task RunUpgradeAsync()
    {
        var directory = Directory.CreateTempSubdirectory("ial-migration-").FullName;
        try
        {
            var path = Path.Combine(directory, "library.db");
            using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
            await connection.OpenAsync();
            await SqliteSchemaMigrator.MigrateAsync(connection, path, 1);
            await ExecuteAsync(connection, "INSERT INTO application_state VALUES(1,7,'{\"schemaVersion\":1,\"users\":[]}','2026-01-01');");
            await SqliteSchemaMigrator.MigrateAsync(connection, path);
            Equal(2, await SqliteSchemaMigrator.VersionAsync(connection));
            Equal("wal", await ScalarAsync(connection, "PRAGMA journal_mode;"));
            Equal("7", await ScalarAsync(connection, "SELECT revision FROM application_state;"));
            Equal("1", await ScalarAsync(connection, "SELECT COUNT(*) FROM schema_migrations;"));
            await SqliteSchemaMigrator.MigrateAsync(connection, path);
            Equal("1", await ScalarAsync(connection, "SELECT COUNT(*) FROM schema_migrations;"));
            await SqliteSchemaMigrator.MigrateAsync(connection, path, 1);
            Equal(1, await SqliteSchemaMigrator.VersionAsync(connection));
            Equal("7", await ScalarAsync(connection, "SELECT revision FROM application_state;"));
            await SqliteSchemaMigrator.MigrateAsync(connection, path);
            Equal("3", await ScalarAsync(connection, "SELECT COUNT(*) FROM schema_migrations;"));
            if (Directory.GetFiles(Path.Combine(directory, "migration-backups"), "*.db").Length != 3)
                throw new Exception("Every non-empty schema transition needs a verified snapshot.");
            await ExecuteAsync(connection, "PRAGMA user_version=99;");
            await ThrowsAsync<InvalidDataException>(() => SqliteSchemaMigrator.MigrateAsync(connection, path));
            Equal(99, await SqliteSchemaMigrator.VersionAsync(connection));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task RunFailureAsync()
    {
        var directory = Directory.CreateTempSubdirectory("ial-migration-failure-").FullName;
        try
        {
            var path = Path.Combine(directory, "library.db");
            using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
            await connection.OpenAsync();
            await SqliteSchemaMigrator.MigrateAsync(connection, path, 1);
            await ExecuteAsync(connection, """
                INSERT INTO application_state VALUES(1,12,'{}','2026-01-01');
                CREATE TABLE schema_migrations(run_id TEXT PRIMARY KEY,from_version INTEGER,to_version INTEGER,
                    migration_sha256 TEXT,completed_utc TEXT,backup_path TEXT,result TEXT);
                CREATE TRIGGER fail_migration BEFORE INSERT ON schema_migrations BEGIN SELECT RAISE(ABORT,'injected failure'); END;
                """);
            await ThrowsAsync<SqliteException>(() => SqliteSchemaMigrator.MigrateAsync(connection, path));
            Equal(1, await SqliteSchemaMigrator.VersionAsync(connection));
            Equal("12", await ScalarAsync(connection, "SELECT revision FROM application_state;"));
            Equal("0", await ScalarAsync(connection, "SELECT COUNT(*) FROM schema_migrations;"));
            if (!File.ReadAllText(path + ".migrations.jsonl").Contains("rolled-back")) throw new Exception("Missing failure audit.");
            await SqliteSchemaMigrator.CheckIntegrityAsync(connection);
            await ExecuteAsync(connection, "DROP TRIGGER fail_migration;");
            await SqliteSchemaMigrator.MigrateAsync(connection, path);
            Equal(2, await SqliteSchemaMigrator.VersionAsync(connection));
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => SqliteSchemaMigrator.MigrateAsync(connection, path, 1, canceled.Token));
            Equal(2, await SqliteSchemaMigrator.VersionAsync(connection));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task<string> ScalarAsync(SqliteConnection connection, string sql)
    { using var command = connection.CreateCommand(); command.CommandText = sql; return Convert.ToString(await command.ExecuteScalarAsync())!; }
    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    { using var command = connection.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync(); }
    private static void Equal<T>(T expected, T actual)
    { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}."); }
}
