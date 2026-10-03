using System.Globalization;
using InternalAssetLibrary.Contracts;
using InternalAssetLibrary.Server.Data;
using InternalAssetLibrary.Server.Realtime;
using InternalAssetLibrary.Server.Security;
using InternalAssetLibrary.Server.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;

namespace InternalAssetLibrary.Server.Api;

internal static class AssetDerivativeEndpoints
{
    public static void MapAssetDerivativeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/assets/{assetId:guid}/derivatives", GetStatusAsync);
        app.MapGet("/api/assets/{assetId:guid}/derivatives/{kind}", DownloadAsync);
        app.MapPut("/api/assets/{assetId:guid}/derivatives/{kind}", UploadAsync);
        app.MapPut("/api/assets/{assetId:guid}/derivatives/{kind}/status", UpdateStatusAsync);
        app.MapPost("/api/assets/{assetId:guid}/derivatives/{kind}/retry", RetryAsync);
    }

    private static async Task<IResult> GetStatusAsync(
        Guid assetId,
        HttpContext context,
        IAppDataStore store)
    {
        var identity = AccessControl.RequirePermission(context, PermissionNames.BrowseAssets);
        var result = await store.ReadAsync(state =>
        {
            var current = ApiCommon.CurrentUser(state, identity);
            ApiCommon.RequirePermission(current, PermissionNames.BrowseAssets);
            var asset = VisibleActiveAsset(state, current, assetId);
            return ApiCommon.ToDerivatives(asset);
        }, context.RequestAborted);
        return Results.Ok(result);
    }

    private static async Task<IResult> DownloadAsync(
        Guid assetId,
        string kind,
        HttpContext context,
        IAppDataStore store,
        IObjectStore objects)
    {
        var parsedKind = ParseKind(kind);
        var identity = AccessControl.RequirePermission(context, PermissionNames.PreviewAssets);
        var target = await store.ReadAsync(state =>
        {
            var current = ApiCommon.CurrentUser(state, identity);
            ApiCommon.RequirePermission(current, PermissionNames.PreviewAssets);
            var asset = VisibleActiveAsset(state, current, assetId);
            var derivative = AssetDerivativeRules.Select(asset, parsedKind);
            if (derivative.State != DerivativeState.Ready || string.IsNullOrWhiteSpace(derivative.ObjectKey))
            {
                throw new ApiException(
                    StatusCodes.Status409Conflict,
                    "derivative_not_ready",
                    $"派生文件当前状态为 {derivative.State.ToString().ToLowerInvariant()}。");
            }

            return new DerivativeDownloadTarget(
                derivative.ObjectKey,
                derivative.ContentType ?? DefaultContentType(asset, parsedKind),
                derivative.ETag ?? string.Empty,
                derivative.UpdatedAt);
        }, context.RequestAborted);

        var direct = await objects.CreateDownloadUrlAsync(target.ObjectKey, cancellationToken: context.RequestAborted);
        if (direct is not null)
        {
            context.Response.Headers["X-IAL-Object-Url-Expires"] = direct.ExpiresAt.ToString("O");
            return Results.Redirect(direct.Url.AbsoluteUri, permanent: false, preserveMethod: true);
        }

        var stream = await objects.OpenReadAsync(target.ObjectKey, context.RequestAborted)
            ?? throw new ApiException(
                StatusCodes.Status409Conflict,
                "derivative_object_missing",
                "派生文件元数据存在，但对象存储中的文件缺失。");
        return Results.File(
            stream,
            target.ContentType,
            lastModified: target.LastModified,
            entityTag: new EntityTagHeaderValue($"\"{target.ETag}\""),
            enableRangeProcessing: true);
    }

    private static async Task<IResult> UploadAsync(
        Guid assetId,
        string kind,
        HttpContext context,
        IAppDataStore store,
        IObjectStore objects,
        ObjectDeletionOutbox objectDeletions,
        IConfiguration configuration,
        ILibraryChangeNotifier changes)
    {
        var parsedKind = ParseKind(kind);
        var identity = AccessControl.RequireUser(context);
        var versionId = ParseVersionId(context);
        var expectedBytes = ParsePositiveInt64(context, "sizeBytes", "派生文件大小无效。");
        var expectedHash = ParseSha256(context.Request.Query["sha256"]);
        var options = configuration.GetSection("Library").Get<LibraryOptions>() ?? new();
        var maximumBytes = parsedKind == AssetDerivativeKind.Thumbnail
            ? options.ThumbnailMaxBytes
            : options.ProxyMaxBytes;
        if (expectedBytes > maximumBytes)
        {
            throw new ApiException(StatusCodes.Status413PayloadTooLarge, "derivative_too_large", "派生文件超过服务器限制。");
        }

        var target = await store.ReadAsync(state =>
        {
            var current = ApiCommon.CurrentUser(state, identity);
            var asset = OwnedActiveAsset(state, current, assetId);
            RequireCurrentVersion(asset, versionId);
            EnsureApplicable(asset, parsedKind);
            return new DerivativeUploadTarget(
                asset.CurrentVersionId,
                AssetDerivativeRules.BuildObjectKey(asset, parsedKind, Guid.NewGuid()),
                DefaultContentType(asset, parsedKind));
        }, context.RequestAborted);

        ValidateRequestContentType(context, target.ContentType);
        PrepareRequestBody(context, expectedBytes);
        ObjectWriteResult write;
        try
        {
            write = await objects.PutAsync(target.ObjectKey, context.Request.Body, maximumBytes, context.RequestAborted);
        }
        catch (InvalidDataException)
        {
            throw new ApiException(StatusCodes.Status413PayloadTooLarge, "derivative_too_large", "派生文件超过服务器限制。");
        }

        if (write.SizeBytes != expectedBytes || !write.Sha256.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            await TryDeleteAsync(objects, target.ObjectKey);
            throw new ApiException(StatusCodes.Status400BadRequest, "derivative_content_mismatch", "派生文件大小或 SHA-256 与声明不一致。");
        }

        try
        {
            if (parsedKind == AssetDerivativeKind.Thumbnail)
            {
                await ValidateThumbnailAsync(objects, target.ObjectKey, options.ThumbnailMaxBytes, context.RequestAborted);
            }
        }
        catch
        {
            await TryDeleteAsync(objects, target.ObjectKey);
            throw;
        }

        var committed = false;
        try
        {
            var result = await store.UpdateAsync(state =>
            {
                var current = ApiCommon.CurrentUser(state, identity);
                var asset = OwnedActiveAsset(state, current, assetId);
                RequireCurrentVersion(asset, target.AssetVersionId);
                EnsureApplicable(asset, parsedKind);
                var derivative = AssetDerivativeRules.Select(asset, parsedKind);
                var staleKeys = string.IsNullOrWhiteSpace(derivative.ObjectKey)
                    ? []
                    : objectDeletions.Enqueue(state, [derivative.ObjectKey], DateTimeOffset.UtcNow);
                derivative.AssetVersionId = asset.CurrentVersionId;
                derivative.State = DerivativeState.Ready;
                derivative.ObjectKey = target.ObjectKey;
                derivative.ContentType = target.ContentType;
                derivative.SizeBytes = write.SizeBytes;
                derivative.ETag = write.Sha256;
                derivative.ErrorCode = null;
                derivative.ErrorMessage = null;
                derivative.ServerBackfillEligible = false;
                derivative.UpdatedAt = DateTimeOffset.UtcNow;
                asset.UpdatedAt = derivative.UpdatedAt;
                ApiCommon.Audit(
                    state,
                    current.Id,
                    "asset.derivative.uploaded",
                    "asset",
                    asset.Id.ToString(),
                    AssetDerivativeRules.KindValue(parsedKind));
                return new DerivativeMutationResult(ApiCommon.ToDerivatives(asset), staleKeys);
            }, context.RequestAborted);
            committed = true;
            await changes.NotifyAsync(context.RequestAborted, LibraryChangeTarget.Derivatives(assetId));
            await objectDeletions.TryProcessAsync(result.StaleObjectKeys, CancellationToken.None);
            return Results.Ok(result.Derivatives);
        }
        finally
        {
            if (!committed)
            {
                await TryDeleteAsync(objects, target.ObjectKey);
            }
        }
    }

    private static async Task<IResult> UpdateStatusAsync(
        Guid assetId,
        string kind,
        UpdateAssetDerivativeStatusRequest request,
        HttpContext context,
        IAppDataStore store,
        ObjectDeletionOutbox objectDeletions,
        ILibraryChangeNotifier changes)
    {
        var parsedKind = ParseKind(kind);
        var stateValue = request.State switch
        {
            AssetDerivativeState.Queued => DerivativeState.Queued,
            AssetDerivativeState.Processing => DerivativeState.Processing,
            AssetDerivativeState.Unavailable => DerivativeState.Unavailable,
            AssetDerivativeState.Failed => DerivativeState.Failed,
            AssetDerivativeState.Ready => throw new ApiException(
                StatusCodes.Status400BadRequest,
                "invalid_derivative_state",
                "ready 状态只能通过成功上传派生文件设置。"),
            _ => throw new ApiException(StatusCodes.Status400BadRequest, "invalid_derivative_state", "派生状态无效。")
        };
        var errorCode = ValidateErrorCode(request.ErrorCode, stateValue);
        var errorMessage = ApiCommon.TrimOptional(request.ErrorMessage, 500, "派生错误信息");
        var identity = AccessControl.RequireUser(context);
        var result = await store.UpdateAsync(state =>
        {
            var current = ApiCommon.CurrentUser(state, identity);
            var asset = OwnedActiveAsset(state, current, assetId);
            RequireCurrentVersion(asset, request.AssetVersionId);
            EnsureApplicable(asset, parsedKind);
            var derivative = AssetDerivativeRules.Select(asset, parsedKind);
            var staleKeys = string.IsNullOrWhiteSpace(derivative.ObjectKey)
                ? []
                : objectDeletions.Enqueue(state, [derivative.ObjectKey], DateTimeOffset.UtcNow);
            AssetDerivativeRules.ClearObject(derivative);
            derivative.State = stateValue;
            derivative.ErrorCode = stateValue is DerivativeState.Failed or DerivativeState.Unavailable ? errorCode : null;
            derivative.ErrorMessage = stateValue is DerivativeState.Failed or DerivativeState.Unavailable ? errorMessage : null;
            derivative.ServerBackfillEligible = false;
            derivative.UpdatedAt = DateTimeOffset.UtcNow;
            ApiCommon.Audit(
                state,
                current.Id,
                "asset.derivative.status",
                "asset",
                asset.Id.ToString(),
                $"{AssetDerivativeRules.KindValue(parsedKind)}:{stateValue.ToString().ToLowerInvariant()}");
            return new DerivativeMutationResult(ApiCommon.ToDerivatives(asset), staleKeys);
        }, context.RequestAborted);
        await changes.NotifyAsync(context.RequestAborted, LibraryChangeTarget.Derivatives(assetId));
        await objectDeletions.TryProcessAsync(result.StaleObjectKeys, CancellationToken.None);
        return Results.Ok(result.Derivatives);
    }

    private static async Task<IResult> RetryAsync(
        Guid assetId,
        string kind,
        RetryAssetDerivativeRequest request,
        HttpContext context,
        IAppDataStore store,
        ObjectDeletionOutbox objectDeletions,
        ILibraryChangeNotifier changes)
    {
        var parsedKind = ParseKind(kind);
        var identity = AccessControl.RequireUser(context);
        var result = await store.UpdateAsync(state =>
        {
            var current = ApiCommon.CurrentUser(state, identity);
            var asset = OwnedActiveAsset(state, current, assetId);
            RequireCurrentVersion(asset, request.AssetVersionId);
            EnsureApplicable(asset, parsedKind);
            if (request.ServerBackfill && !current.IsAdmin)
            {
                throw new ApiException(StatusCodes.Status403Forbidden, "administrator_required", "只有管理员可以请求服务器回填缩略图。");
            }

            if (request.ServerBackfill && parsedKind != AssetDerivativeKind.Thumbnail)
            {
                throw new ApiException(StatusCodes.Status400BadRequest, "server_proxy_disabled", "服务器不承担代理文件转码。");
            }

            var derivative = AssetDerivativeRules.Select(asset, parsedKind);
            var staleKeys = string.IsNullOrWhiteSpace(derivative.ObjectKey)
                ? []
                : objectDeletions.Enqueue(state, [derivative.ObjectKey], DateTimeOffset.UtcNow);
            AssetDerivativeRules.ClearObject(derivative);
            derivative.State = DerivativeState.Queued;
            derivative.ErrorCode = null;
            derivative.ErrorMessage = null;
            derivative.RetryCount = derivative.RetryCount == int.MaxValue
                ? int.MaxValue
                : derivative.RetryCount + 1;
            derivative.ServerBackfillEligible = request.ServerBackfill;
            derivative.UpdatedAt = DateTimeOffset.UtcNow;
            ApiCommon.Audit(
                state,
                current.Id,
                "asset.derivative.retried",
                "asset",
                asset.Id.ToString(),
                AssetDerivativeRules.KindValue(parsedKind));
            return new DerivativeMutationResult(ApiCommon.ToDerivatives(asset), staleKeys);
        }, context.RequestAborted);
        await changes.NotifyAsync(context.RequestAborted, LibraryChangeTarget.Derivatives(assetId));
        await objectDeletions.TryProcessAsync(result.StaleObjectKeys, CancellationToken.None);
        return Results.Ok(result.Derivatives);
    }

    private static AssetRecord OwnedActiveAsset(AppState state, UserRecord current, Guid assetId)
    {
        var asset = state.Assets.FirstOrDefault(item => item.Id == assetId)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "asset_not_found", "素材不存在。");
        if (!current.IsAdmin && asset.UploadedByUserId != current.Id)
        {
            throw new ApiException(StatusCodes.Status403Forbidden, "asset_owner_required", "只有上传者或管理员可以维护派生文件。");
        }

        if (!current.IsAdmin)
        {
            ApiCommon.RequirePermission(current, PermissionNames.EditOwnAssets);
        }

        ApiCommon.RequireCategory(current, asset.Category);
        if (asset.State != AssetState.Active || !asset.HasOriginal)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "asset_not_ready", "素材必须处于可用状态且已有原文件。");
        }

        return asset;
    }

    private static AssetRecord VisibleActiveAsset(AppState state, UserRecord current, Guid assetId)
    {
        var asset = state.Assets.FirstOrDefault(item => item.Id == assetId);
        if (asset is null || asset.State != AssetState.Active || !asset.HasOriginal ||
            !ApiCommon.CanAccessCategory(current, asset.Category))
        {
            throw new ApiException(StatusCodes.Status404NotFound, "asset_not_found", "素材不存在。");
        }

        return asset;
    }

    private static void EnsureApplicable(AssetRecord asset, AssetDerivativeKind kind)
    {
        if (kind == AssetDerivativeKind.Proxy && !AssetDerivativeRules.SupportsProxy(asset.Category))
        {
            throw new ApiException(StatusCodes.Status409Conflict, "proxy_not_applicable", "此素材类型不需要代理文件。");
        }
    }

    private static void RequireCurrentVersion(AssetRecord asset, Guid requestedVersionId)
    {
        if (requestedVersionId == Guid.Empty || requestedVersionId != asset.CurrentVersionId)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "asset_version_changed", "素材版本已经变化，请刷新后重试。");
        }
    }

    private static AssetDerivativeKind ParseKind(string value) => value.ToLowerInvariant() switch
    {
        "thumbnail" => AssetDerivativeKind.Thumbnail,
        "proxy" => AssetDerivativeKind.Proxy,
        _ => throw new ApiException(StatusCodes.Status404NotFound, "derivative_kind_not_found", "派生文件类型不存在。")
    };

    private static Guid ParseVersionId(HttpContext context) =>
        Guid.TryParse(context.Request.Query["assetVersionId"], out var versionId)
            ? versionId
            : throw new ApiException(StatusCodes.Status400BadRequest, "invalid_asset_version", "素材版本标识无效。");

    private static long ParsePositiveInt64(HttpContext context, string name, string message) =>
        long.TryParse(context.Request.Query[name], NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : throw new ApiException(StatusCodes.Status400BadRequest, "invalid_derivative_size", message);

    private static string ParseSha256(string? value)
    {
        var result = (value ?? string.Empty).Trim().ToUpperInvariant();
        if (result.Length != 64 || result.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_content_hash", "SHA-256 必须为 64 位十六进制值。");
        }

        return result;
    }

    private static string? ValidateErrorCode(string? value, DerivativeState state)
    {
        if (state is not (DerivativeState.Failed or DerivativeState.Unavailable))
        {
            return null;
        }

        var result = string.IsNullOrWhiteSpace(value) ? "processing_failed" : value.Trim();
        if (result.Length > 64 || result.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-'))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_error_code", "派生错误代码格式无效。");
        }

        return result;
    }

    private static string DefaultContentType(AssetRecord asset, AssetDerivativeKind kind) => kind switch
    {
        AssetDerivativeKind.Thumbnail => "image/webp",
        AssetDerivativeKind.Proxy when asset.Category == AssetCategories.Video => "video/mp4",
        AssetDerivativeKind.Proxy => "audio/mp4",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static void ValidateRequestContentType(HttpContext context, string expected)
    {
        var actual = context.Request.ContentType?.Split(';', 2)[0].Trim();
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new ApiException(
                StatusCodes.Status415UnsupportedMediaType,
                "invalid_derivative_content_type",
                $"此派生文件必须使用 {expected}。" );
        }
    }

    private static void PrepareRequestBody(HttpContext context, long expectedBytes)
    {
        if (context.Request.ContentLength is not null && context.Request.ContentLength != expectedBytes)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "content_length_mismatch", "Content-Length 与声明大小不一致。");
        }

        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false })
        {
            feature.MaxRequestBodySize = expectedBytes;
        }
    }

    private static async Task ValidateThumbnailAsync(
        IObjectStore objects,
        string objectKey,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = await objects.OpenReadAsync(objectKey, cancellationToken)
            ?? throw new ApiException(StatusCodes.Status409Conflict, "derivative_object_missing", "缩略图写入后无法读取。");
        await using var memory = new MemoryStream((int)Math.Min(maximumBytes, 1024 * 1024));
        await stream.CopyToAsync(memory, cancellationToken);
        _ = ThumbnailWebPValidator.Validate(memory.GetBuffer().AsSpan(0, checked((int)memory.Length)));
    }

    private static async Task TryDeleteAsync(IObjectStore objects, string objectKey)
    {
        try
        {
            await objects.DeleteAsync(objectKey, CancellationToken.None);
        }
        catch
        {
            // The durable outbox handles only committed objects; rollback leftovers are reconciled separately.
        }
    }

    private sealed record DerivativeUploadTarget(Guid AssetVersionId, string ObjectKey, string ContentType);

    private sealed record DerivativeDownloadTarget(
        string ObjectKey,
        string ContentType,
        string ETag,
        DateTimeOffset LastModified);

    private sealed record DerivativeMutationResult(
        AssetDerivativesInfo Derivatives,
        string[] StaleObjectKeys);
}
