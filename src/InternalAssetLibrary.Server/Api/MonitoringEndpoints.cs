using System.Security.Cryptography;
using System.Text;
using InternalAssetLibrary.Server.Data;
using InternalAssetLibrary.Server.Security;
using InternalAssetLibrary.Server.Services;

namespace InternalAssetLibrary.Server.Api;

internal static class MonitoringEndpoints
{
    public static void MapMonitoringEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/internal/metrics", GetMetricsAsync);
    }

    private static async Task<IResult> GetMetricsAsync(
        HttpContext context,
        IAppDataStore store,
        IConfiguration configuration)
    {
        RequireMonitoringToken(context, configuration);
        var quota = configuration.GetValue("Library:OriginalQuotaBytes", 107_374_182_400L);
        var snapshot = await store.ReadAsync(state => new
        {
            OriginalBytes = AdminAssetEndpoints.OriginalBytes(state),
            ActiveAssets = state.Assets.Count(asset => asset.State == AssetState.Active),
            RecycledAssets = state.Assets.Count(asset => asset.State == AssetState.Recycled),
            EnabledUsers = state.Users.Count(user => user.IsEnabled),
            PendingObjectDeletions = state.PendingObjectDeletions.Count
        }, context.RequestAborted);
        var capacityLevel = CapacityStatus.Level(snapshot.OriginalBytes, quota, configuration);
        var capacityRatio = quota <= 0 ? 0 : (double)snapshot.OriginalBytes / quota;
        var body = $$"""
            # TYPE ial_original_bytes gauge
            ial_original_bytes {{snapshot.OriginalBytes}}
            # TYPE ial_original_quota_bytes gauge
            ial_original_quota_bytes {{quota}}
            # TYPE ial_original_capacity_ratio gauge
            ial_original_capacity_ratio {{capacityRatio.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}}
            # TYPE ial_original_capacity_warning gauge
            ial_original_capacity_warning {{(capacityLevel is "warning" or "critical" ? 1 : 0)}}
            # TYPE ial_original_capacity_critical gauge
            ial_original_capacity_critical {{(capacityLevel == "critical" ? 1 : 0)}}
            # TYPE ial_assets gauge
            ial_assets{state="active"} {{snapshot.ActiveAssets}}
            ial_assets{state="recycled"} {{snapshot.RecycledAssets}}
            # TYPE ial_enabled_users gauge
            ial_enabled_users {{snapshot.EnabledUsers}}
            # TYPE ial_pending_object_deletions gauge
            ial_pending_object_deletions {{snapshot.PendingObjectDeletions}}
            """;
        return Results.Text(body + "\n", "text/plain; version=0.0.4; charset=utf-8");
    }

    private static void RequireMonitoringToken(HttpContext context, IConfiguration configuration)
    {
        var expected = configuration["Monitoring:MetricsToken"];
        var actual = context.Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(expected) || !actual.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            throw new ApiException(StatusCodes.Status401Unauthorized, "monitoring_authentication_required", "监控端点需要有效令牌。 ");
        }

        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(actual[7..].Trim());
        if (expectedBytes.Length != actualBytes.Length ||
            !CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes))
        {
            throw new ApiException(StatusCodes.Status401Unauthorized, "monitoring_authentication_required", "监控端点需要有效令牌。 ");
        }
    }
}
