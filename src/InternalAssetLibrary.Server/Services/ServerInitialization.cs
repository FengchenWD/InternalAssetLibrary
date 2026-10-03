using InternalAssetLibrary.Server.Api;
using InternalAssetLibrary.Server.Data;
using InternalAssetLibrary.Server.Realtime;
using InternalAssetLibrary.Server.Security;

namespace InternalAssetLibrary.Server.Services;

internal static class ServerBootstrapper
{
    public static async Task InitializeAsync(WebApplication app, CancellationToken cancellationToken = default)
    {
        var store = app.Services.GetRequiredService<IAppDataStore>();
        await store.InitializeAsync(cancellationToken);

        var hasUsers = await store.ReadAsync(state => state.Users.Count > 0, cancellationToken);
        if (hasUsers)
        {
            return;
        }

        var sectionName = app.Environment.IsDevelopment() ? "DevelopmentBootstrap" : "Bootstrap";
        var options = app.Configuration.GetSection(sectionName).Get<BootstrapOptions>() ?? new();
        if (!options.Enabled)
        {
            if (!app.Environment.IsDevelopment())
            {
                throw new InvalidOperationException(
                    "The production database has no users and administrator bootstrap is disabled.");
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(options.TemporaryPassword) ||
            options.TemporaryPassword.Contains("REPLACE_WITH", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{sectionName} is enabled but TemporaryPassword is empty or still uses the example placeholder. " +
                "Set it with an environment variable; do not commit it.");
        }

        if (!PasswordService.IsAcceptable(options.TemporaryPassword, out var error))
        {
            throw new InvalidOperationException($"Administrator bootstrap password is invalid: {error}");
        }

        var username = ApiCommon.ValidateUsername(options.Username);
        var normalized = ApiCommon.Normalize(username);
        var passwords = app.Services.GetRequiredService<PasswordService>();
        var hash = passwords.Hash(options.TemporaryPassword);
        var created = await store.UpdateAsync(state =>
        {
            if (state.Users.Count > 0)
            {
                return false;
            }

            var now = DateTimeOffset.UtcNow;
            var admin = new UserRecord
            {
                Id = Guid.NewGuid(),
                Username = username,
                NormalizedUsername = normalized,
                DisplayName = options.DisplayName.Trim(),
                PasswordHash = hash,
                MustChangePassword = true,
                IsEnabled = true,
                IsAdmin = true,
                Permissions = PermissionNames.MemberDefaults(),
                CreatedAt = now,
                UpdatedAt = now
            };
            state.Users.Add(admin);
            ApiCommon.Audit(state, null, "bootstrap.admin.created", "user", admin.Id.ToString(), $"@{admin.Username}");
            return true;
        }, cancellationToken);

        if (created)
        {
            app.Logger.LogWarning(
                "Created the initial administrator @{Username}. Recommend changing the initial password after first login.",
                username);
        }
    }
}

internal sealed class DevelopmentDataCleanupService(
    IAppDataStore store,
    ObjectDeletionOutbox objectDeletions,
    ILibraryChangeNotifier changes,
    IConfiguration configuration,
    ILogger<DevelopmentDataCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await CleanupAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(6));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await CleanupAsync(stoppingToken);
        }
    }

    private async Task CleanupAsync(CancellationToken cancellationToken)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            var settings = await store.ReadAsync(state => state.ServerSettings, cancellationToken);
            var auditDays = Math.Clamp(settings?.AuditRetentionDays ?? configuration.GetValue("Library:AuditRetentionDays", 30), 1, 3650);
            var downloadDays = Math.Clamp(settings?.DownloadLogRetentionDays ?? configuration.GetValue("Library:DownloadLogRetentionDays", 10), 1, 3650);
            var cleanup = await store.UpdateAsync(state =>
            {
                var keys = new List<string>();
                state.Sessions.RemoveAll(session => session.ExpiresAt <= now);
                state.AuditLog.RemoveAll(entry => entry.OccurredAt < now.AddDays(-auditDays));
                state.DownloadLog.RemoveAll(entry => entry.OccurredAt < now.AddDays(-downloadDays));
                state.LoginFailures.RemoveAll(entry => entry.LastFailureAt < now.AddDays(-30));

                foreach (var asset in state.Assets)
                {
                    var expiredVersions = asset.PreviousVersions.Where(version => version.PurgeAfter <= now).ToList();
                    keys.AddRange(expiredVersions.Where(version => version.HasOriginal).Select(version => version.ObjectKey));
                    asset.PreviousVersions.RemoveAll(version => version.PurgeAfter <= now);
                }

                var expired = state.Assets.Where(asset =>
                    asset.State == AssetState.Recycled && asset.PurgeAfter <= now).ToList();
                var expiredAssets = new List<ExpiredAssetChange>(expired.Count);
                foreach (var asset in expired)
                {
                    if (asset.HasOriginal)
                    {
                        keys.Add(asset.ObjectKey);
                    }

                    keys.AddRange(AssetDerivativeRules.StoredObjectKeys(asset));

                    keys.AddRange(asset.PreviousVersions.Where(version => version.HasOriginal).Select(version => version.ObjectKey));
                    var markerSetIds = state.MarkerSets
                        .Where(markerSet => markerSet.AssetId == asset.Id)
                        .Select(markerSet => markerSet.Id)
                        .ToArray();
                    state.MarkerSets.RemoveAll(markerSet => markerSet.AssetId == asset.Id);
                    state.Assets.Remove(asset);
                    expiredAssets.Add(new ExpiredAssetChange(
                        asset.Id,
                        asset.UploadedByUserId,
                        markerSetIds));
                    ApiCommon.Audit(state, null, "asset.retention.purged", "asset", asset.Id.ToString(), asset.ObjectKey);
                }

                var expiredLuts = state.TeamLuts.Where(lut =>
                    lut.State == TeamLutState.Recycled && lut.PurgeAfter <= now).ToList();
                foreach (var lut in expiredLuts)
                {
                    if (lut.HasContent)
                    {
                        keys.Add(lut.ObjectKey);
                    }

                    state.TeamLuts.Remove(lut);
                    ApiCommon.Audit(state, null, "lut.retention.purged", "lut", lut.Id.ToString(), lut.ObjectKey);
                }

                objectDeletions.Enqueue(state, keys, now);
                return new CleanupChanges(
                    expiredAssets,
                    expiredLuts.Select(lut => lut.Id).ToArray());
            }, cancellationToken);
            await objectDeletions.RetryPendingAsync(cancellationToken);
            foreach (var asset in cleanup.Assets)
            {
                var notifications = new List<LibraryChangeTarget>
                {
                    LibraryChangeTarget.Assets(asset.AssetId),
                    LibraryChangeTarget.Derivatives(asset.AssetId),
                    LibraryChangeTarget.Profiles(asset.UploaderId)
                };
                notifications.AddRange(asset.MarkerSetIds.Select(markerSetId =>
                    LibraryChangeTarget.Markers(markerSetId)));
                await changes.NotifyAsync(cancellationToken, notifications.ToArray());
            }

            foreach (var lutId in cleanup.LutIds)
            {
                await changes.NotifyAsync(cancellationToken, LibraryChangeTarget.Luts(lutId));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Development data cleanup failed.");
        }
    }

    private sealed record CleanupChanges(
        IReadOnlyList<ExpiredAssetChange> Assets,
        IReadOnlyList<Guid> LutIds);

    private sealed record ExpiredAssetChange(
        Guid AssetId,
        Guid UploaderId,
        IReadOnlyList<Guid> MarkerSetIds);
}
