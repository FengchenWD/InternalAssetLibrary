using InternalAssetLibrary.Core;
using InternalAssetLibrary.Server.Api;
using InternalAssetLibrary.Server.Services;

namespace InternalAssetLibrary.Server.Security;

internal sealed class ClientCompatibilityMiddleware(
    RequestDelegate next,
    ClientReleaseProvider releases,
    IConfiguration configuration)
{
    public const string VersionHeader = "X-IAL-Client-Version";

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api") ||
            context.Request.Path.StartsWithSegments("/api/client/releases"))
        {
            await next(context);
            return;
        }

        var requireHeader = configuration.GetValue("ClientUpdates:RequireVersionHeader", true);
        var rawVersion = context.Request.Headers[VersionHeader].ToString().Trim();
        if (string.IsNullOrEmpty(rawVersion))
        {
            // Same-origin browser administration is versioned with the server itself.
            var fetchSite = context.Request.Headers["Sec-Fetch-Site"].ToString().Trim().ToLowerInvariant();
            if (!requireHeader || fetchSite is "same-origin" or "same-site" or "none")
            {
                await next(context);
                return;
            }

            throw new ApiException(
                StatusCodes.Status426UpgradeRequired,
                "client_version_required",
                "客户端版本信息缺失，请安装当前发布的客户端。 ");
        }

        var minimumText = releases.MinimumCompatibleVersion();
        if (!SemanticVersion.TryParse(rawVersion, out var actual))
        {
            throw new ApiException(
                StatusCodes.Status426UpgradeRequired,
                "client_version_invalid",
                "客户端版本格式无效，请安装当前发布的客户端。 ");
        }

        var minimum = SemanticVersion.Parse(minimumText);
        if (actual.CompareTo(minimum) < 0)
        {
            context.Response.Headers["X-IAL-Minimum-Client-Version"] = minimumText;
            throw new ApiException(
                StatusCodes.Status426UpgradeRequired,
                "client_update_required",
                $"当前客户端版本过旧，服务器要求至少使用 {minimumText}。 ");
        }

        await next(context);
    }
}
