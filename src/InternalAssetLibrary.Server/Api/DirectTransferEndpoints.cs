using InternalAssetLibrary.Contracts;
using InternalAssetLibrary.Server.Data;
using InternalAssetLibrary.Server.Realtime;
using InternalAssetLibrary.Server.Security;
using InternalAssetLibrary.Server.Services;

namespace InternalAssetLibrary.Server.Api;

internal static class DirectTransferEndpoints
{
    public static void MapDirectTransferEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/transfers/capabilities", GetCapabilities);
        app.MapPost("/api/assets/{assetId:guid}/direct-upload", StartAssetUploadAsync);
        app.MapGet("/api/direct-uploads/{sessionId:guid}", GetSessionAsync);
        app.MapGet("/api/direct-uploads/{sessionId:guid}/parts/{partNumber:int}", GetPartUrlAsync);
        app.MapPost("/api/direct-uploads/{sessionId:guid}/complete", CompleteAsync);
        app.MapDelete("/api/direct-uploads/{sessionId:guid}", AbortAsync);
    }

    private static IResult GetCapabilities(HttpContext context, IObjectStore objects, IConfiguration configuration)
    {
        _ = AccessControl.RequireUser(context);
        var partSize = ValidPartSize(configuration);
        var lifetimeHours = Math.Clamp(configuration.GetValue("Cos:UploadSessionLifetimeHours", 24), 1, 168);
        return Results.Ok(new DirectTransferCapabilities(
            objects.SupportsDirectMultipartUpload,
            partSize,
            lifetimeHours));
    }

    private static async Task<IResult> StartAssetUploadAsync(
        Guid assetId,
        HttpContext context,
        IAppDataStore store,
        IObjectStore objects,
        IConfiguration configuration)
    {
        var identity = AccessControl.RequirePermission(context, PermissionNames.UploadAssets);
        if (!objects.SupportsDirectMultipartUpload)
        {
            throw Conflict("direct_upload_unavailable", "当前对象存储未启用直传，请使用兼容上传方式。 ");
        }

        var now = DateTimeOffset.UtcNow;
        var partSize = ValidPartSize(configuration);
        var lifetimeHours = Math.Clamp(configuration.GetValue("Cos:UploadSessionLifetimeHours", 24), 1, 168);
        var target = await store.ReadAsync(state =>
        {
            var current = ApiCommon.CurrentUser(state, identity);
            ApiCommon.RequirePermission(current, PermissionNames.UploadAssets);
            var asset = state.Assets.FirstOrDefault(item => item.Id == assetId && item.State == AssetState.Active)
                ?? throw NotFound("asset_not_found", "素材不存在。 ");
            if (!current.IsAdmin && asset.UploadedByUserId != current.Id)
            {
                throw new ApiException(StatusCodes.Status403Forbidden, "asset_owner_required", "只有上传者或管理员可以上传此素材。 ");
            }
            if (asset.HasOriginal)
            {
                throw Conflict("original_already_uploaded", "素材原文件已经上传。 ");
            }

            var existing = state.DirectUploadSessions.FirstOrDefault(session =>
                session.UserId == current.Id &&
                session.AssetId == asset.Id &&
                session.AssetVersionId == asset.CurrentVersionId &&
                session.ExpiresAt > now);
            return new { Current = current, Asset = asset, Existing = existing };
        }, context.RequestAborted);
        if (target.Existing is not null)
        {
            return Results.Ok(ToContract(target.Existing));
        }

        var partCount = checked((int)((target.Asset.SizeBytes + partSize - 1L) / partSize));
        if (partCount is < 1 or > 10_000)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_part_count", "文件大小无法按当前分片规则上传。 ");
        }

        var handle = await objects.StartMultipartUploadAsync(
            target.Asset.ObjectKey,
            target.Asset.SizeBytes,
            target.Asset.ContentHash,
            "application/octet-stream",
            context.RequestAborted);
        var created = new DirectUploadSessionRecord
        {
            Id = Guid.NewGuid(),
            UserId = target.Current.Id,
            AssetId = target.Asset.Id,
            AssetVersionId = target.Asset.CurrentVersionId,
            ObjectKey = target.Asset.ObjectKey,
            ProviderUploadId = handle.ProviderUploadId,
            SizeBytes = target.Asset.SizeBytes,
            Sha256 = target.Asset.ContentHash,
            PartSizeBytes = partSize,
            CreatedAt = now,
            ExpiresAt = now.AddHours(lifetimeHours)
        };

        try
        {
            var session = await store.UpdateAsync(state =>
            {
                var current = ApiCommon.CurrentUser(state, identity);
                var asset = state.Assets.FirstOrDefault(item => item.Id == created.AssetId && item.State == AssetState.Active)
                    ?? throw NotFound("asset_not_found", "素材不存在。 ");
                if (asset.CurrentVersionId != created.AssetVersionId || asset.HasOriginal || asset.ObjectKey != created.ObjectKey)
                {
                    throw Conflict("asset_changed_during_upload", "创建直传会话期间素材已变化，请刷新后重试。 ");
                }

                var raced = state.DirectUploadSessions.FirstOrDefault(item =>
                    item.UserId == current.Id && item.AssetId == asset.Id && item.ExpiresAt > now);
                if (raced is not null)
                {
                    return new { Session = raced, Added = false };
                }

                state.DirectUploadSessions.Add(created);
                ApiCommon.Audit(state, current.Id, "asset.direct-upload.started", "asset", asset.Id.ToString(), created.Id.ToString());
                return new { Session = created, Added = true };
            }, context.RequestAborted);
            if (!session.Added)
            {
                await objects.AbortMultipartUploadAsync(
                    created.ObjectKey,
                    created.ProviderUploadId,
                    CancellationToken.None);
            }

            return Results.Ok(ToContract(session.Session));
        }
        catch
        {
            await objects.AbortMultipartUploadAsync(
                created.ObjectKey,
                created.ProviderUploadId,
                CancellationToken.None);
            throw;
        }
    }

    private static async Task<IResult> GetSessionAsync(
        Guid sessionId,
        HttpContext context,
        IAppDataStore store)
    {
        var identity = AccessControl.RequireUser(context);
        var session = await store.ReadAsync(state => OwnedSession(state, identity, sessionId), context.RequestAborted);
        return Results.Ok(ToContract(session));
    }

    private static async Task<IResult> GetPartUrlAsync(
        Guid sessionId,
        int partNumber,
        HttpContext context,
        IAppDataStore store,
        IObjectStore objects)
    {
        var identity = AccessControl.RequireUser(context);
        var session = await store.ReadAsync(state => OwnedSession(state, identity, sessionId), context.RequestAborted);
        var partCount = checked((int)((session.SizeBytes + session.PartSizeBytes - 1L) / session.PartSizeBytes));
        if (partNumber < 1 || partNumber > partCount)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_part_number", "分片编号无效。 ");
        }

        var signed = await objects.CreateUploadPartUrlAsync(
            session.ObjectKey,
            session.ProviderUploadId,
            partNumber,
            context.RequestAborted);
        if (signed.Url.Scheme != Uri.UriSchemeHttps || signed.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            throw new InvalidOperationException("Object storage returned an invalid upload part URL.");
        }

        return Results.Ok(new DirectUploadPartUrl(
            session.Id,
            partNumber,
            signed.Url.AbsoluteUri,
            signed.ExpiresAt));
    }

    private static async Task<IResult> CompleteAsync(
        Guid sessionId,
        CompleteDirectUploadRequest request,
        HttpContext context,
        IAppDataStore store,
        IObjectStore objects,
        IConfiguration configuration,
        ILibraryChangeNotifier changes)
    {
        var identity = AccessControl.RequireUser(context);
        var session = await store.ReadAsync(state => OwnedSession(state, identity, sessionId), context.RequestAborted);
        var partCount = checked((int)((session.SizeBytes + session.PartSizeBytes - 1L) / session.PartSizeBytes));
        var parts = ValidateParts(request.Parts, partCount);
        var write = await objects.CompleteMultipartUploadAsync(
            session.ObjectKey,
            session.ProviderUploadId,
            parts,
            session.SizeBytes,
            session.Sha256,
            context.RequestAborted);
        if (write.SizeBytes != session.SizeBytes || !write.Sha256.Equals(session.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            await objects.DeleteAsync(session.ObjectKey, CancellationToken.None);
            throw new ApiException(StatusCodes.Status400BadRequest, "file_content_mismatch", "直传文件大小或 SHA-256 校验失败。 ");
        }

        try
        {
            var quota = configuration.GetValue("Library:OriginalQuotaBytes", 107_374_182_400L);
            var result = await store.UpdateAsync(state =>
            {
                var current = ApiCommon.CurrentUser(state, identity);
                var activeSession = OwnedSession(state, identity, sessionId);
                var asset = state.Assets.FirstOrDefault(item => item.Id == activeSession.AssetId && item.State == AssetState.Active)
                    ?? throw NotFound("asset_not_found", "素材不存在。 ");
                if (asset.CurrentVersionId != activeSession.AssetVersionId || asset.HasOriginal || asset.ObjectKey != activeSession.ObjectKey)
                {
                    throw Conflict("asset_changed_during_upload", "完成直传期间素材已变化，请刷新后重试。 ");
                }
                if (activeSession.SizeBytes > quota - AdminAssetEndpoints.OriginalBytes(state))
                {
                    throw Conflict("quota_exceeded", "原文件配额不足。 ");
                }

                asset.HasOriginal = true;
                asset.UpdatedAt = DateTimeOffset.UtcNow;
                AssetDerivativeRules.ResetForCurrentVersion(asset, asset.UpdatedAt, serverThumbnailBackfill: false);
                state.DirectUploadSessions.Remove(activeSession);
                ApiCommon.Audit(state, current.Id, "asset.direct-upload.completed", "asset", asset.Id.ToString(), sessionId.ToString());
                return new
                {
                    Asset = ApiCommon.ToAsset(asset, state.Users.FirstOrDefault(user => user.Id == asset.UploadedByUserId)),
                    asset.UploadedByUserId
                };
            }, context.RequestAborted);
            await changes.NotifyAsync(
                context.RequestAborted,
                LibraryChangeTarget.Assets(session.AssetId),
                LibraryChangeTarget.Derivatives(session.AssetId),
                LibraryChangeTarget.Profiles(result.UploadedByUserId));
            return Results.Ok(result.Asset);
        }
        catch
        {
            await objects.DeleteAsync(session.ObjectKey, CancellationToken.None);
            await store.UpdateAsync(state =>
            {
                state.DirectUploadSessions.RemoveAll(item => item.Id == sessionId);
                return true;
            }, CancellationToken.None);
            throw;
        }
    }

    private static async Task<IResult> AbortAsync(
        Guid sessionId,
        HttpContext context,
        IAppDataStore store,
        IObjectStore objects,
        ObjectDeletionOutbox cleanups)
    {
        var identity = AccessControl.RequireUser(context);
        var session = await store.UpdateAsync(state =>
        {
            var owned = OwnedSession(state, identity, sessionId);
            cleanups.EnqueueAbort(state, owned);
            state.DirectUploadSessions.Remove(owned);
            ApiCommon.Audit(state, identity.UserId, "asset.direct-upload.aborted", "asset", owned.AssetId.ToString(), sessionId.ToString());
            return owned;
        }, context.RequestAborted);
        await cleanups.TryProcessAsync([session.ObjectKey], CancellationToken.None);
        return Results.NoContent();
    }

    private static DirectUploadSessionRecord OwnedSession(
        AppState state,
        RequestIdentity identity,
        Guid sessionId)
    {
        var current = ApiCommon.CurrentUser(state, identity);
        var session = state.DirectUploadSessions.FirstOrDefault(item => item.Id == sessionId)
            ?? throw NotFound("direct_upload_not_found", "直传会话不存在或已过期。 ");
        if (!current.IsAdmin && session.UserId != current.Id)
        {
            throw new ApiException(StatusCodes.Status403Forbidden, "direct_upload_owner_required", "不能访问其他用户的直传会话。 ");
        }
        if (session.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            throw new ApiException(StatusCodes.Status410Gone, "direct_upload_expired", "直传会话已过期，请重新上传。 ");
        }

        return session;
    }

    private static IReadOnlyList<MultipartUploadPart> ValidateParts(
        IReadOnlyList<DirectUploadCompletedPart>? rawParts,
        int expectedCount)
    {
        var parts = (rawParts ?? [])
            .OrderBy(part => part.PartNumber)
            .Select(part => new MultipartUploadPart(part.PartNumber, NormalizeETag(part.ETag)))
            .ToArray();
        if (parts.Length != expectedCount || parts.Select(part => part.PartNumber).Distinct().Count() != expectedCount ||
            parts.Where((part, index) => part.PartNumber != index + 1).Any())
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_completed_parts", "完成上传时必须提交全部且不重复的分片。 ");
        }

        return parts;
    }

    private static string NormalizeETag(string? value)
    {
        var result = value?.Trim().Trim('"') ?? string.Empty;
        if (result.Length is < 1 or > 256 || result.Any(char.IsControl))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_part_etag", "分片 ETag 无效。 ");
        }

        return result;
    }

    private static DirectUploadSession ToContract(DirectUploadSessionRecord session) => new(
        session.Id,
        session.AssetId,
        session.AssetVersionId,
        session.SizeBytes,
        session.Sha256,
        session.PartSizeBytes,
        checked((int)((session.SizeBytes + session.PartSizeBytes - 1L) / session.PartSizeBytes)),
        session.ExpiresAt);

    private static int ValidPartSize(IConfiguration configuration)
    {
        var partSize = configuration.GetValue("Cos:MultipartPartSizeBytes", 16 * 1024 * 1024);
        if (partSize is < 5 * 1024 * 1024 or > 512 * 1024 * 1024)
        {
            throw new InvalidOperationException("Cos:MultipartPartSizeBytes must be between 5 MiB and 512 MiB.");
        }

        return partSize;
    }

    private static ApiException NotFound(string code, string message) =>
        new(StatusCodes.Status404NotFound, code, message);

    private static ApiException Conflict(string code, string message) =>
        new(StatusCodes.Status409Conflict, code, message);
}
