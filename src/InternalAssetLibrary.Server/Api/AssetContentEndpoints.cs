using System.Globalization;
using InternalAssetLibrary.Server.Data;
using InternalAssetLibrary.Server.Realtime;
using InternalAssetLibrary.Server.Security;
using InternalAssetLibrary.Server.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Net.Http.Headers;

namespace InternalAssetLibrary.Server.Api;

internal static class AssetContentEndpoints
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    public static void MapAssetContentEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPut("/api/assets/{assetId:guid}/content", UploadInitialAsync);
        app.MapPut("/api/assets/{assetId:guid}/replacement", UploadReplacementAsync);
        app.MapGet("/api/assets/{assetId:guid}/content", PreviewAsync);
        app.MapGet("/api/assets/{assetId:guid}/download", DownloadAsync);
        app.MapGet("/api/assets/{assetId:guid}/versions/{versionId:guid}/download", DownloadVersionAsync);
    }

    private static async Task<IResult> UploadInitialAsync(
        Guid assetId,
        HttpContext context,
        IAppDataStore data,
        IObjectStore objects,
        IConfiguration configuration,
        ILibraryChangeNotifier changes)
    {
        var identity = AccessControl.RequirePermission(context, PermissionNames.UploadAssets);
        var target = await data.ReadAsync(state =>
        {
            var current = CurrentUser(state, identity, PermissionNames.UploadAssets);
            var asset = OwnedActiveAsset(state, current, assetId, PermissionNames.EditOwnAssets, requireEditPermission: false);
            if (asset.HasOriginal)
            {
                throw Conflict("original_already_uploaded", "素材原文件已经上传；换源请使用 replacement 接口。");
            }

            return new UploadTarget(
                asset.CurrentVersionId,
                asset.ObjectKey,
                asset.SizeBytes,
                asset.ContentHash);
        }, context.RequestAborted);

        PrepareRequestBody(context, target.SizeBytes);
        var write = await PutObjectAsync(objects, target.ObjectKey, context.Request, target.SizeBytes, context.RequestAborted);
        if (write.SizeBytes != target.SizeBytes || !write.Sha256.Equals(target.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            await objects.DeleteAsync(target.ObjectKey, CancellationToken.None);
            throw new ApiException(StatusCodes.Status400BadRequest, "file_content_mismatch", "实际文件大小或 SHA-256 与素材元数据不一致。");
        }

        var committed = false;
        try
        {
            var quota = configuration.GetValue("Library:OriginalQuotaBytes", 107_374_182_400L);
            var result = await data.UpdateAsync(state =>
            {
                var current = CurrentUser(state, identity, PermissionNames.UploadAssets);
                var asset = OwnedActiveAsset(state, current, assetId, PermissionNames.EditOwnAssets, requireEditPermission: false);
                if (asset.CurrentVersionId != target.VersionId || asset.HasOriginal || asset.ObjectKey != target.ObjectKey)
                {
                    throw Conflict("asset_changed_during_upload", "上传期间素材已发生变化，请刷新后重试。");
                }

                if (target.SizeBytes > quota - AdminAssetEndpoints.OriginalBytes(state))
                {
                    throw Conflict("quota_exceeded", "原文件配额不足，回收站和保留中的旧版本仍计入配额。");
                }

                asset.HasOriginal = true;
                asset.UpdatedAt = DateTimeOffset.UtcNow;
                AssetDerivativeRules.ResetForCurrentVersion(
                    asset,
                    asset.UpdatedAt,
                    serverThumbnailBackfill: false);
                ApiCommon.Audit(state, current.Id, "asset.original.uploaded", "asset", asset.Id.ToString(), asset.ObjectKey);
                return new
                {
                    Asset = ApiCommon.ToAsset(asset, state.Users.FirstOrDefault(user => user.Id == asset.UploadedByUserId)),
                    asset.UploadedByUserId
                };
            }, context.RequestAborted);
            committed = true;
            await changes.NotifyAsync(
                context.RequestAborted,
                LibraryChangeTarget.Assets(assetId),
                LibraryChangeTarget.Derivatives(assetId),
                LibraryChangeTarget.Profiles(result.UploadedByUserId));
            return Results.Ok(result.Asset);
        }
        finally
        {
            if (!committed)
            {
                await TryDeleteAsync(objects, target.ObjectKey);
            }
        }
    }

    private static async Task<IResult> UploadReplacementAsync(
        Guid assetId,
        HttpContext context,
        IAppDataStore data,
        IObjectStore objects,
        IConfiguration configuration,
        ObjectDeletionOutbox objectDeletions,
        ILibraryChangeNotifier changes)
    {
        var identity = AccessControl.RequireUser(context);
        var replacement = ParseReplacement(context, configuration);
        var target = await data.ReadAsync(state =>
        {
            var current = ApiCommon.CurrentUser(state, identity);
            var asset = OwnedActiveAsset(state, current, assetId, PermissionNames.EditOwnAssets, requireEditPermission: true);
            ValidateCategoryExtension(asset.Category, replacement.Extension);
            ValidateCategorySize(asset.Category, replacement.SizeBytes, configuration);
            if (!asset.HasOriginal)
            {
                throw Conflict("original_not_uploaded", "素材尚无原文件，请先使用 content 接口完成首次上传。");
            }

            if (HasStoredHash(state, replacement.Sha256))
            {
                throw Conflict("duplicate_content", "相同内容已经存在，无需重复上传或换源。");
            }

            var versionId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            return new ReplacementTarget(
                asset.CurrentVersionId,
                versionId,
                $"originals/{now:yyyy/MM}/{asset.Id:N}/{versionId:N}{replacement.Extension}");
        }, context.RequestAborted);

        PrepareRequestBody(context, replacement.SizeBytes);
        var write = await PutObjectAsync(objects, target.ObjectKey, context.Request, replacement.SizeBytes, context.RequestAborted);
        if (write.SizeBytes != replacement.SizeBytes || !write.Sha256.Equals(replacement.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            await objects.DeleteAsync(target.ObjectKey, CancellationToken.None);
            throw new ApiException(StatusCodes.Status400BadRequest, "file_content_mismatch", "实际文件大小或 SHA-256 与换源声明不一致。");
        }

        var committed = false;
        try
        {
            var library = configuration.GetSection("Library").Get<LibraryOptions>() ?? new();
            var result = await data.UpdateAsync(state =>
            {
                var current = ApiCommon.CurrentUser(state, identity);
                var asset = OwnedActiveAsset(state, current, assetId, PermissionNames.EditOwnAssets, requireEditPermission: true);
                if (asset.CurrentVersionId != target.PreviousVersionId)
                {
                    throw Conflict("asset_changed_during_upload", "上传期间素材版本已变化，请刷新后重试。");
                }

                if (HasStoredHash(state, replacement.Sha256) ||
                    replacement.SizeBytes > library.OriginalQuotaBytes - AdminAssetEndpoints.OriginalBytes(state))
                {
                    throw Conflict("quota_or_duplicate_conflict", "上传期间出现重复内容或原文件配额已不足。");
                }

                var now = DateTimeOffset.UtcNow;
                var derivativeKeys = objectDeletions.Enqueue(
                    state,
                    AssetDerivativeRules.StoredObjectKeys(asset),
                    now);
                asset.PreviousVersions.Add(new AssetVersionRecord
                {
                    Id = asset.CurrentVersionId,
                    Version = asset.Version,
                    OriginalFileName = asset.OriginalFileName,
                    Extension = asset.Extension,
                    SizeBytes = asset.SizeBytes,
                    ContentHash = asset.ContentHash,
                    ObjectKey = asset.ObjectKey,
                    HasOriginal = asset.HasOriginal,
                    CreatedAt = asset.UpdatedAt,
                    PurgeAfter = now.AddDays(Math.Clamp(library.RecycleRetentionDays, 1, 365))
                });
                asset.CurrentVersionId = target.NewVersionId;
                asset.Version++;
                asset.OriginalFileName = replacement.OriginalFileName;
                asset.Extension = replacement.Extension;
                asset.SizeBytes = replacement.SizeBytes;
                asset.ContentHash = replacement.Sha256;
                asset.ObjectKey = target.ObjectKey;
                asset.HasOriginal = true;
                asset.UpdatedAt = now;
                AssetDerivativeRules.ResetForCurrentVersion(
                    asset,
                    now,
                    serverThumbnailBackfill: false);
                ApiCommon.Audit(state, current.Id, "asset.source.replaced", "asset", asset.Id.ToString(), target.PreviousVersionId.ToString());
                return new
                {
                    Asset = ApiCommon.ToAsset(asset, state.Users.FirstOrDefault(user => user.Id == asset.UploadedByUserId)),
                    MarkerSetIds = state.MarkerSets
                        .Where(markerSet => markerSet.AssetId == asset.Id)
                        .Select(markerSet => markerSet.Id)
                        .ToArray(),
                    DerivativeKeys = derivativeKeys
                };
            }, context.RequestAborted);
            committed = true;
            var notifications = new List<LibraryChangeTarget>
            {
                LibraryChangeTarget.Assets(assetId),
                LibraryChangeTarget.Derivatives(assetId)
            };
            notifications.AddRange(result.MarkerSetIds.Select(markerSetId => LibraryChangeTarget.Markers(markerSetId)));
            await changes.NotifyAsync(context.RequestAborted, notifications.ToArray());
            await objectDeletions.TryProcessAsync(result.DerivativeKeys, CancellationToken.None);
            return Results.Ok(result.Asset);
        }
        finally
        {
            if (!committed)
            {
                await TryDeleteAsync(objects, target.ObjectKey);
            }
        }
    }

    private static Task<IResult> PreviewAsync(
        Guid assetId,
        HttpContext context,
        IAppDataStore data,
        IObjectStore objects) =>
        OpenCurrentAsync(assetId, context, data, objects, PermissionNames.PreviewAssets, download: false);

    private static Task<IResult> DownloadAsync(
        Guid assetId,
        HttpContext context,
        IAppDataStore data,
        IObjectStore objects) =>
        OpenCurrentAsync(assetId, context, data, objects, PermissionNames.DownloadAssets, download: true);

    private static async Task<IResult> OpenCurrentAsync(
        Guid assetId,
        HttpContext context,
        IAppDataStore data,
        IObjectStore objects,
        string permission,
        bool download)
    {
        var identity = AccessControl.RequirePermission(context, permission);
        var target = await data.ReadAsync(state =>
        {
            var current = CurrentUser(state, identity, permission);
            var asset = VisibleActiveAsset(state, current, assetId);
            if (!asset.HasOriginal)
            {
                throw Conflict("original_not_uploaded", "素材原文件尚未上传。");
            }

            return new DownloadTarget(
                asset.Id,
                asset.CurrentVersionId,
                asset.ObjectKey,
                asset.OriginalFileName,
                asset.Extension,
                asset.SizeBytes,
                asset.ContentHash,
                asset.UpdatedAt);
        }, context.RequestAborted);
        return await OpenResultAsync(context, data, objects, identity, target, download);
    }

    private static async Task<IResult> DownloadVersionAsync(
        Guid assetId,
        Guid versionId,
        HttpContext context,
        IAppDataStore data,
        IObjectStore objects)
    {
        var identity = AccessControl.RequirePermission(context, PermissionNames.DownloadAssets);
        var target = await data.ReadAsync(state =>
        {
            var current = CurrentUser(state, identity, PermissionNames.DownloadAssets);
            var asset = VisibleActiveAsset(state, current, assetId);
            if (versionId == asset.CurrentVersionId)
            {
                if (!asset.HasOriginal)
                {
                    throw Conflict("original_not_uploaded", "素材原文件尚未上传。");
                }

                return new DownloadTarget(
                    asset.Id,
                    asset.CurrentVersionId,
                    asset.ObjectKey,
                    asset.OriginalFileName,
                    asset.Extension,
                    asset.SizeBytes,
                    asset.ContentHash,
                    asset.UpdatedAt);
            }

            var version = asset.PreviousVersions.FirstOrDefault(item =>
                item.Id == versionId && item.HasOriginal && item.PurgeAfter > DateTimeOffset.UtcNow)
                ?? throw NotFound("asset_version_not_found", "素材旧版本不存在或已超过保留期。");
            return new DownloadTarget(
                asset.Id,
                version.Id,
                version.ObjectKey,
                version.OriginalFileName,
                version.Extension,
                version.SizeBytes,
                version.ContentHash,
                version.CreatedAt);
        }, context.RequestAborted);
        return await OpenResultAsync(context, data, objects, identity, target, download: true);
    }

    private static async Task<IResult> OpenResultAsync(
        HttpContext context,
        IAppDataStore data,
        IObjectStore objects,
        RequestIdentity identity,
        DownloadTarget target,
        bool download)
    {
        if (download)
        {
            await data.UpdateAsync(state =>
            {
                var current = CurrentUser(state, identity, PermissionNames.DownloadAssets);
                _ = VisibleActiveAsset(state, current, target.AssetId);
                state.DownloadLog.Add(new DownloadRecord
                {
                    Id = Guid.NewGuid(),
                    UserId = current.Id,
                    AssetId = target.AssetId,
                    AssetVersionId = target.VersionId,
                    SizeBytes = target.SizeBytes,
                    OccurredAt = DateTimeOffset.UtcNow
                });
                return true;
            }, context.RequestAborted);
        }

        var direct = await objects.CreateDownloadUrlAsync(
            target.ObjectKey,
            download ? target.OriginalFileName : null,
            context.RequestAborted);
        if (direct is not null)
        {
            context.Response.Headers["X-IAL-Object-Url-Expires"] = direct.ExpiresAt.ToString("O");
            return Results.Redirect(direct.Url.AbsoluteUri, permanent: false, preserveMethod: true);
        }

        var stream = await objects.OpenReadAsync(target.ObjectKey, context.RequestAborted)
            ?? throw Conflict("object_missing", "素材元数据存在，但对象存储中的原文件缺失。");

        var contentType = ContentTypes.TryGetContentType(target.OriginalFileName, out var resolved)
            ? resolved
            : "application/octet-stream";
        return Results.File(
            stream,
            contentType,
            fileDownloadName: download ? target.OriginalFileName : null,
            lastModified: target.LastModified,
            entityTag: new EntityTagHeaderValue($"\"{target.Sha256}\""),
            enableRangeProcessing: true);
    }

    private static UserRecord CurrentUser(AppState state, RequestIdentity identity, string permission)
    {
        var current = ApiCommon.CurrentUser(state, identity);
        ApiCommon.RequirePermission(current, permission);
        return current;
    }

    private static AssetRecord OwnedActiveAsset(
        AppState state,
        UserRecord current,
        Guid assetId,
        string memberPermission,
        bool requireEditPermission)
    {
        var asset = state.Assets.FirstOrDefault(item => item.Id == assetId)
            ?? throw NotFound("asset_not_found", "素材不存在。");
        if (!current.IsAdmin && asset.UploadedByUserId != current.Id)
        {
            throw new ApiException(StatusCodes.Status403Forbidden, "asset_owner_required", "只有上传者或管理员可以上传或替换原文件。");
        }

        if (!current.IsAdmin && requireEditPermission)
        {
            ApiCommon.RequirePermission(current, memberPermission);
        }

        ApiCommon.RequireCategory(current, asset.Category);
        if (asset.State != AssetState.Active)
        {
            throw Conflict("asset_recycled", "回收站中的素材不能上传或替换原文件。");
        }

        return asset;
    }

    private static AssetRecord VisibleActiveAsset(AppState state, UserRecord current, Guid assetId)
    {
        var asset = state.Assets.FirstOrDefault(item => item.Id == assetId);
        if (asset is null || asset.State != AssetState.Active || !ApiCommon.CanAccessCategory(current, asset.Category))
        {
            throw NotFound("asset_not_found", "素材不存在。");
        }

        return asset;
    }

    private static ReplacementRequest ParseReplacement(HttpContext context, IConfiguration configuration)
    {
        var originalFileName = Path.GetFileName(context.Request.Query["fileName"].ToString().Trim());
        if (string.IsNullOrWhiteSpace(originalFileName) || originalFileName.Length > 255)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_file_name", "换源文件名无效。");
        }

        if (!long.TryParse(
                context.Request.Query["sizeBytes"],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var sizeBytes))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_asset_size", "换源文件大小无效。");
        }

        var sha256 = context.Request.Query["sha256"].ToString().Trim().ToUpperInvariant();
        if (sha256.Length != 64 || sha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_content_hash", "SHA-256 必须为 64 位十六进制值。");
        }

        var extension = Path.GetExtension(originalFileName).ToLowerInvariant();
        var library = configuration.GetSection("Library").Get<LibraryOptions>() ?? new();
        if (sizeBytes <= 0 || sizeBytes > Math.Max(library.AudioMaxBytes, Math.Max(library.ImageMaxBytes, library.VideoMaxBytes)))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_asset_size", "换源文件大小超出服务器限制。");
        }

        return new ReplacementRequest(originalFileName, extension, sizeBytes, sha256);
    }

    private static void ValidateCategoryExtension(string category, string extension)
    {
        var allowed = category switch
        {
            AssetCategories.Bgm or AssetCategories.SoundEffect => AssetCategories.AudioExtensions.Contains(extension),
            AssetCategories.Image => AssetCategories.ImageExtensions.Contains(extension),
            AssetCategories.Video => AssetCategories.VideoExtensions.Contains(extension),
            _ => false
        };
        if (!allowed)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "category_extension_mismatch", "换源文件格式与素材大类不匹配。");
        }
    }

    private static void ValidateCategorySize(string category, long sizeBytes, IConfiguration configuration)
    {
        var limits = configuration.GetSection("Library").Get<LibraryOptions>() ?? new();
        var maximum = category switch
        {
            AssetCategories.Bgm or AssetCategories.SoundEffect => limits.AudioMaxBytes,
            AssetCategories.Image => limits.ImageMaxBytes,
            AssetCategories.Video => limits.VideoMaxBytes,
            _ => 0
        };
        if (sizeBytes <= 0 || sizeBytes > maximum)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_asset_size", $"换源文件大小必须大于 0 且不超过 {maximum} 字节。");
        }
    }

    private static bool HasStoredHash(AppState state, string sha256) => state.Assets.Any(asset =>
        asset.HasOriginal && asset.ContentHash.Equals(sha256, StringComparison.OrdinalIgnoreCase) ||
        asset.PreviousVersions.Any(version =>
            version.HasOriginal && version.ContentHash.Equals(sha256, StringComparison.OrdinalIgnoreCase)));

    private static void PrepareRequestBody(HttpContext context, long expectedBytes)
    {
        if (context.Request.ContentLength is not null && context.Request.ContentLength != expectedBytes)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "content_length_mismatch", "Content-Length 与声明的文件大小不一致。");
        }

        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false })
        {
            feature.MaxRequestBodySize = expectedBytes;
        }
    }

    private static async Task<ObjectWriteResult> PutObjectAsync(
        IObjectStore objects,
        string objectKey,
        HttpRequest request,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        try
        {
            return await objects.PutAsync(objectKey, request.Body, maximumBytes, cancellationToken);
        }
        catch (InvalidDataException)
        {
            throw new ApiException(StatusCodes.Status413PayloadTooLarge, "asset_body_too_large", "上传正文超过声明的文件大小或服务器限制。");
        }
    }

    private static async Task TryDeleteAsync(IObjectStore objects, string objectKey)
    {
        try
        {
            await objects.DeleteAsync(objectKey, CancellationToken.None);
        }
        catch
        {
            // A later object-store reconciliation job must remove crash or rollback leftovers.
        }
    }

    private static ApiException Conflict(string code, string message) =>
        new(StatusCodes.Status409Conflict, code, message);

    private static ApiException NotFound(string code, string message) =>
        new(StatusCodes.Status404NotFound, code, message);

    private sealed record UploadTarget(
        Guid VersionId,
        string ObjectKey,
        long SizeBytes,
        string Sha256);

    private sealed record ReplacementTarget(
        Guid PreviousVersionId,
        Guid NewVersionId,
        string ObjectKey);

    private sealed record ReplacementRequest(
        string OriginalFileName,
        string Extension,
        long SizeBytes,
        string Sha256);

    private sealed record DownloadTarget(
        Guid AssetId,
        Guid VersionId,
        string ObjectKey,
        string OriginalFileName,
        string Extension,
        long SizeBytes,
        string Sha256,
        DateTimeOffset LastModified);
}
