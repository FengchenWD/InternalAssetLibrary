using InternalAssetLibrary.Server.Api;
using InternalAssetLibrary.Server.Data;

namespace InternalAssetLibrary.Server.Services;

internal static class CapacityStatus
{
    public static string Level(long usedBytes, long quotaBytes, IConfiguration configuration)
    {
        if (quotaBytes <= 0)
        {
            return "unknown";
        }

        var ratio = (double)usedBytes / quotaBytes;
        var critical = Math.Clamp(configuration.GetValue("Monitoring:CapacityCriticalRatio", 0.95), 0.01, 1);
        var warning = Math.Clamp(configuration.GetValue("Monitoring:CapacityWarningRatio", 0.85), 0.01, critical);
        return ratio >= critical ? "critical" : ratio >= warning ? "warning" : "normal";
    }
}

internal sealed class CapacityMonitorService(
    IAppDataStore store,
    IConfiguration configuration,
    ILogger<CapacityMonitorService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await CheckAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await CheckAsync(stoppingToken);
        }
    }

    private async Task CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            var quota = configuration.GetValue("Library:OriginalQuotaBytes", 107_374_182_400L);
            var used = await store.ReadAsync(AdminAssetEndpoints.OriginalBytes, cancellationToken);
            var level = CapacityStatus.Level(used, quota, configuration);
            var ratio = quota <= 0 ? 0 : (double)used / quota;
            if (level == "critical")
            {
                logger.LogError(
                    "Original asset capacity is critical: {UsedBytes}/{QuotaBytes} ({Ratio:P1}).",
                    used,
                    quota,
                    ratio);
            }
            else if (level == "warning")
            {
                logger.LogWarning(
                    "Original asset capacity warning: {UsedBytes}/{QuotaBytes} ({Ratio:P1}).",
                    used,
                    quota,
                    ratio);
            }
            else
            {
                logger.LogInformation(
                    "Original asset capacity is normal: {UsedBytes}/{QuotaBytes} ({Ratio:P1}).",
                    used,
                    quota,
                    ratio);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Capacity monitoring failed.");
        }
    }
}
