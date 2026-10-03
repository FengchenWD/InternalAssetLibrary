using InternalAssetLibrary.Server.Data;

namespace InternalAssetLibrary.Server.Services;

internal sealed class SqliteBackupService(
    ISqliteBackupSource backupSource,
    IObjectStore objects,
    IHostEnvironment environment,
    ServerSettingsService settings,
    ILogger<SqliteBackupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var changed = settings.BackupSettingsChanged;
            var current = settings.Current;
            if (current.BackupsEnabled)
            {
                var options = new BackupOptions
                {
                    Enabled = true,
                    IntervalHours = current.BackupIntervalHours,
                    RetentionDays = current.BackupRetentionDays,
                    LocalPath = current.BackupLocalPath,
                    ObjectPrefix = current.BackupObjectPrefix
                };
                await RunBackupAsync(options, stoppingToken);
            }

            var delay = current.BackupsEnabled
                ? TimeSpan.FromHours(Math.Clamp(current.BackupIntervalHours, 1, 168))
                : TimeSpan.FromMinutes(5);
            try { await changed.WaitAsync(delay, stoppingToken); }
            catch (TimeoutException) { }
        }
    }

    private async Task RunBackupAsync(BackupOptions options, CancellationToken cancellationToken)
    {
        string? directory = null;
        var now = DateTimeOffset.UtcNow;
        try
        {
            directory = Path.GetFullPath(options.LocalPath, environment.ContentRootPath);
            Directory.CreateDirectory(directory);
            var fileName = $"library-{now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.db";
            var localPath = Path.Combine(directory, fileName);
            var backup = await backupSource.CreateVerifiedBackupAsync(localPath, cancellationToken);

            var prefix = NormalizePrefix(options.ObjectPrefix);
            var objectKey = $"{prefix}{now:yyyy/MM/dd}/{fileName}";
            await using var source = new FileStream(
                backup.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var uploaded = await objects.PutAsync(objectKey, source, backup.SizeBytes, cancellationToken);
            if (uploaded.SizeBytes != backup.SizeBytes ||
                !uploaded.Sha256.Equals(backup.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Uploaded SQLite backup does not match its verified local snapshot.");
            }

            logger.LogInformation(
                "Uploaded verified SQLite backup {ObjectKey}, revision {Revision}, SHA-256 {Sha256}.",
                objectKey,
                backup.Revision,
                backup.Sha256);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "SQLite backup failed.");
        }
        finally
        {
            if (directory is not null)
            {
                try
                {
                    RemoveExpiredLocalBackups(
                        directory,
                        Math.Clamp(options.RetentionDays, 1, 3650),
                        now);
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Could not apply local SQLite backup retention.");
                }
            }
        }
    }

    private static string NormalizePrefix(string prefix)
    {
        var normalized = prefix.Trim().Trim('/');
        if (string.IsNullOrEmpty(normalized) || normalized.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Backups:ObjectPrefix must be a non-empty safe object prefix.");
        }

        return normalized + "/";
    }

    private static void RemoveExpiredLocalBackups(string directory, int retentionDays, DateTimeOffset now)
    {
        var cutoff = now.UtcDateTime.AddDays(-retentionDays);
        foreach (var path in Directory.EnumerateFiles(directory, "library-*.db", SearchOption.TopDirectoryOnly))
        {
            if (File.GetLastWriteTimeUtc(path) < cutoff)
            {
                File.Delete(path);
            }
        }
    }
}

internal sealed class BackupOptions
{
    public bool Enabled { get; set; }
    public int IntervalHours { get; set; } = 24;
    public int RetentionDays { get; set; } = 30;
    public string LocalPath { get; set; } = "App_Data/backups";
    public string ObjectPrefix { get; set; } = "backups/sqlite";
}
