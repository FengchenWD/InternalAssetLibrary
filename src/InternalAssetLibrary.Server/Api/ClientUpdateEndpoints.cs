using InternalAssetLibrary.Core;
using InternalAssetLibrary.Server.Services;
using InternalAssetLibrary.Server.Security;

namespace InternalAssetLibrary.Server.Api;

internal static class ClientUpdateEndpoints
{
    public static void MapClientUpdateEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/client/releases/latest", GetLatestAsync);
        app.MapGet("/api/client/releases/latest/download", DownloadLatestAsync);
    }

    private const string ManualTransitionVersion = "0.2.0-preview.6.8";
    private static readonly SemanticVersion BrokenUpdaterStart =
        SemanticVersion.Parse("0.2.0-preview.6.6");
    private static readonly SemanticVersion FixedUpdaterStart =
        SemanticVersion.Parse(ManualTransitionVersion);

    internal static async Task<IResult> GetLatestAsync(
        HttpContext context,
        ClientReleaseProvider releases,
        CancellationToken cancellationToken)
    {
        AccessControl.RequireUser(context);
        context.Response.Headers.CacheControl = "no-store";
        var release = await ReadReleaseAsync(context, releases, cancellationToken);
        EnsureReleaseIsAvailableToClient(context, release);
        return release is null ? Results.NoContent() : Results.Ok(release.Info);
    }

    internal static async Task<IResult> DownloadLatestAsync(
        HttpContext context,
        ClientReleaseProvider releases,
        IObjectStore objects,
        CancellationToken cancellationToken)
    {
        AccessControl.RequireUser(context);
        var release = await ReadReleaseAsync(context, releases, cancellationToken)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "client_release_not_found", "当前没有可下载的客户端更新。 ");
        EnsureReleaseIsAvailableToClient(context, release);

        context.Response.Headers.CacheControl = "private, no-store";
        if (release.InstallerObjectKey is { } objectKey)
        {
            var direct = await objects.CreateDownloadUrlAsync(
                objectKey,
                release.Info.InstallerFileName,
                cancellationToken);
            if (direct is not null)
            {
                context.Response.Headers["X-IAL-Object-Url-Expires"] = direct.ExpiresAt.ToString("O");
                return Results.Redirect(direct.Url.AbsoluteUri, permanent: false, preserveMethod: true);
            }

            if (objects.StorageKind == "development-filesystem")
            {
                var objectStream = await objects.OpenReadAsync(objectKey, cancellationToken);
                if (objectStream is not null)
                {
                    return InstallerFile(objectStream, release);
                }
            }
        }

        if (release.LocalInstallerPath is null)
        {
            throw new ApiException(
                StatusCodes.Status503ServiceUnavailable,
                "client_release_object_unavailable",
                "客户端更新安装包暂时不可用，请稍后重试。 ");
        }

        var stream = new FileStream(
            release.LocalInstallerPath,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                BufferSize = 128 * 1024,
                Options = FileOptions.Asynchronous | FileOptions.RandomAccess
            });
        return InstallerFile(stream, release);
    }

    private static async Task<ClientRelease?> ReadReleaseAsync(HttpContext context, ClientReleaseProvider releases, CancellationToken cancellationToken)
    {
        try { return await releases.TryGetLatestAsync(cancellationToken); }
        catch (InvalidDataException exception)
        {
            context.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("ClientUpdates")
                .LogError(exception, "Rejected invalid client release manifest.");
            throw new ApiException(StatusCodes.Status503ServiceUnavailable, "client_release_manifest_invalid",
                "客户端更新清单无效或签名校验失败；更新已阻止，请管理员重新发布已签名的清单。 ");
        }
    }

    private static IResult InstallerFile(Stream stream, ClientRelease release) =>
        Results.File(
            stream,
            "application/vnd.microsoft.portable-executable",
            release.Info.InstallerFileName,
            lastModified: release.Info.PublishedAt,
            entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{release.Info.InstallerSha256}\""),
            enableRangeProcessing: true);

    internal static bool RequiresManualTransition(string? currentVersion, string targetVersion)
    {
        if (!SemanticVersion.TryParse(currentVersion, out var current) ||
            !SemanticVersion.TryParse(targetVersion, out var target))
        {
            return false;
        }

        return current.CompareTo(BrokenUpdaterStart) >= 0 &&
               current.CompareTo(FixedUpdaterStart) < 0 &&
               target.CompareTo(FixedUpdaterStart) >= 0;
    }

    private static void EnsureReleaseIsAvailableToClient(HttpContext context, ClientRelease? release)
    {
        if (release is null ||
            !RequiresManualTransition(
                context.Request.Headers[ClientCompatibilityMiddleware.VersionHeader].ToString().Trim(),
                release.Info.Version))
        {
            return;
        }

        context.Response.Headers["X-IAL-Manual-Update-Version"] = ManualTransitionVersion;
        throw new ApiException(
            StatusCodes.Status426UpgradeRequired,
            "client_update_manual_install_required",
            $"当前版本的自动更新组件存在兼容问题，不能通过应用内更新。请手动下载并运行 {ManualTransitionVersion} 中文安装包覆盖安装。");
    }
}
