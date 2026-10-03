using System.Globalization;
using InternalAssetLibrary.Contracts;
using InternalAssetLibrary.Server.Data;
using InternalAssetLibrary.Server.Realtime;
using InternalAssetLibrary.Server.Security;
using InternalAssetLibrary.Server.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;

namespace InternalAssetLibrary.Server.Api;

internal static class TeamLutEndpoints
{
    public static void MapTeamLutEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/luts", ListAsync);
        app.MapGet("/api/luts/{lutId:guid}", GetAsync);
        app.MapPost("/api/luts", CreateAsync);
        app.MapPut("/api/luts/{lutId:guid}", UpdateAsync);
        app.MapPut("/api/luts/{lutId:guid}/content", UploadInitialAsync);
        app.MapPut("/api/luts/{lutId:guid}/replacement", UploadReplacementAsync);
        app.MapGet("/api/luts/{lutId:guid}/download", DownloadAsync);
        app.MapDelete("/api/luts/{lutId:guid}", RecycleAsync);
        app.MapPost("/api/luts/{lutId:guid}/restore", RestoreAsync);
        app.MapDelete("/api/luts/{lutId:guid}/permanent", PermanentlyDeleteAsync);
    }

    private static async Task<IResult> ListAsync(HttpContext context, IAppDataStore store)
    {
        var identity = AccessControl.RequireUser(context);
        var search = context.Request.Query["search"].ToString().Trim();
        var requestedState = context.Request.Query["state"].ToString();
        var recycled = requestedState.Equals("recycled", StringComparison.OrdinalIgnoreCase);
        var uploaderText = context.Request.Query["uploaderId"].ToString();
        var hasUploader = Guid.TryParse(uploaderText, out var uploaderId);
        var page = Math.Clamp(ParsePage(context.Request.Query["page"], 1), 1, 1_000_000);
        var pageSize = Math.Clamp(ParsePage(context.Request.Query["pageSize"], 100), 1, 200);
        var result = await store.ReadAsync(state =>
        {
            var current = ApiCommon.CurrentUser(state, identity);
            IEnumerable<TeamLutRecord> query = state.TeamLuts.Where(lut =>
                lut.State == (recycled ? Data.TeamLutState.Recycled : Data.TeamLutState.Active) &&
                (recycled ? current.IsAdmin || lut.UploadedByUserId == current.Id : lut.HasContent));
            if (search.Length > 0)
            {
                query = query.Where(lut =>
                    lut.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    lut.OriginalFileName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    (lut.Notes?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false));
            }

            if (hasUploader)
            {
                query = query.Where(lut => lut.UploadedByUserId == uploaderId);
            }

            var ordered = query
                .OrderBy(lut => lut.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(lut => lut.Id)
                .ToArray();
            var items = ordered
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(lut => ApiCommon.ToTeamLut(
                    lut,
                    state.Users.FirstOrDefault(user => user.Id == lut.UploadedByUserId)))
                .ToArray();
            return new PageResult<TeamLutSummary>(items, page, pageSize, ordered.Length);
        }, context.RequestAborted);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetAsync(Guid lutId, HttpContext context, IAppDataStore store)
    {
        var identity = AccessControl.RequireUser(context);
        var result = await store.ReadAsync(state =>
        {
            var current = ApiCommon.CurrentUser(state, identity);
            var lut = VisibleLut(state, current, lutId, allowRecycledOwner: true);
            return ApiCommon.ToTeamLut(lut, state.Users.FirstOrDefault(user => user.Id == lut.UploadedByUserId));
        }, context.RequestAborted);
        return Results.Ok(result);
    }

    private static async Task<IResult> CreateAsync(
        CreateTeamLutRequest request,
        HttpContext context,
        IAppDataStore store,
        IConfiguration configuration,
        ILibraryChangeNotifier changes)
    {
        var identity = AccessControl.RequirePermission(context, PermissionNames.UploadAssets);
        var name = ApiCommon.TrimRequired(request.Name, 200, "LUT 名称");
        var originalFileName = ValidateFileName(request.OriginalFileName);
        var note = ApiCommon.TrimOptional(request.Note, 2_000, "LUT 备注");
        var sha256 = ValidateSha256(request.Sha256);
        var maximum = configuration.GetValue("Library:LutMaxBytes", 16L * 1024 * 1024);
        if (request.SizeBytes <= 0 || request.SizeBytes > maximum)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_lut_size", $"LUT 文件必须大于 0 且不超过 {maximum} 字节。");
        }

        var result = await store.UpdateAsync(state =>
        {
            var current = ApiCommon.CurrentUser(state, identity);
            ApiCommon.RequirePermission(current, PermissionNames.UploadAssets);
            var now = DateTimeOffset.UtcNow;
            var id = Guid.NewGuid();
            var versionId = Guid.NewGuid();
            var lut = new TeamLutRecord
            {
                Id = id,
                CurrentVersionId = versionId,
                Name = name,
                OriginalFileName = originalFileName,
                Notes = note,
                SizeBytes = request.SizeBytes,
                ContentHash = sha256,
                ObjectKey = BuildObjectKey(now, id, versionId),
                UploadedByUserId = current.Id,
                UploadedByUsername = current.Username,
                UploadedByDisplayName = current.DisplayName,
                UploadedAt = now,
                UpdatedAt = now
            };
            state.TeamLuts.Add(lut);
            ApiCommon.Audit(state, current.Id, "lut.created", "lut", lut.Id.ToString());
            return ApiCommon.ToTeamLut(lut, current);
        }, context.RequestAborted);
        await changes.NotifyAsync(context.RequestAborted, LibraryChangeTarget.Luts(result.Id));
        return Results.Created($"/api/luts/{result.Id:D}", result);
    }

    private static async Task<IResult> UpdateAsync(
        Guid lutId,
        UpdateTeamLutRequest request,
        HttpContext context,
        IAppDataStore store,
        ILibraryChangeNotifier changes)
    {
        var identity = AccessControl.RequireUser(context);
        var name = ApiCommon.TrimRequired(request.Name, 200, "LUT 名称");
        var note = ApiCommon.TrimOptional(request.Note, 2_000, "LUT 备注");
        var result = await store.UpdateAsync(state =>
        {
            var current = ApiCommon.CurrentUser(state, identity);
            var lut = OwnedActiveLut(state, current, lutId, PermissionNames.EditOwnAssets);
            lut.Name = name;
            lut.Notes = note;
            lut.UpdatedAt = DateTimeOffset.UtcNow;
            ApiCommon.Audit(state, current.Id, "lut.metadata.updated", "lut", lut.Id.ToString());
            return ApiCommon.ToTeamLut(lut, state.Users.FirstOrDefault(user => user.Id == lut.UploadedByUserId));
        }, context.RequestAborted);
        await changes.NotifyAsync(context.RequestAborted, LibraryChangeTarget.Luts(lutId));
        return Results.Ok(result);
    }

    private static async Task<IResult> UploadInitialAsync(
        Guid lutId,
        HttpContext context,
        IAppDataStore store,
        IObjectStore objects,
        IConfiguration configuration,
        ILibraryChangeNotifier changes)
    {
        var identity = AccessControl.RequirePermission(context, PermissionNames.UploadAssets);
        var target = await store.ReadAsync(state =>
        {
            var current = ApiCommon.CurrentUser(state, identity);
            ApiCommon.RequirePermission(current, PermissionNames.UploadAssets);
            var lut = OwnedActiveLut(state, current, lutId, PermissionNames.UploadAssets, enforceMemberPermission: false);
            if (lut.HasContent)
            {
                throw Conflict("lut_content_exists", "LUT 文件已经上传；换源请使用 replacement 接口。");
            }

            return new LutUploadTarget(lut.CurrentVersionId, lut.ObjectKey, lut.SizeBytes, lut.ContentHash);
        }, context.RequestAborted);

        var write = await WriteAndValidateAsync(target, context, objects, configuration);
        var committed = false;
        try
        {
            var result = await store.UpdateAsync(state =>
            {
                var current = ApiCommon.CurrentUser(state, identity);
                ApiCommon.RequirePermission(current, PermissionNames.UploadAssets);
                var lut = OwnedActiveLut(state, current, lutId, PermissionNames.UploadAssets, enforceMemberPermission: false);
                if (lut.CurrentVersionId != target.VersionId || lut.HasContent || lut.ObjectKey != target.ObjectKey)
                {
                    throw Conflict("lut_changed_during_upload", "上传期间 LUT 已发生变化，请刷新后重试。");
                }

                lut.HasContent = true;
                lut.UpdatedAt = DateTimeOffset.UtcNow;
                ApiCommon.Audit(state, current.Id, "lut.content.uploaded", "lut", lut.Id.ToString(), lut.ObjectKey);
                return ApiCommon.ToTeamLut(lut, state.Users.FirstOrDefault(user => user.Id == lut.UploadedByUserId));
            }, context.RequestAborted);
            committed = true;
            await changes.NotifyAsync(context.RequestAborted, LibraryChangeTarget.Luts(lutId));
            return Results.Ok(result);
        }
        finally
        {
            if (!committed)
            {
                await TryDeleteAsync(objects, write.ObjectKey);
            }
        }
    }

    private static async Task<IResult> UploadReplacementAsync(
        Guid lutId,
        HttpContext context,
        IAppDataStore store,
        IObjectStore objects,
        ObjectDeletionOutbox objectDeletions,
        IConfiguration configuration,
        ILibraryChangeNotifier changes)
    {
        var identity = AccessControl.RequireUser(context);
        var fileName = ValidateFileName(context.Request.Query["fileName"]);
        var sizeBytes = ParsePositiveInt64(context.Request.Query["sizeBytes"], "LUT 文件大小无效。");
        var sha256 = ValidateSha256(context.Request.Query["sha256"]);
        var maximum = configuration.GetValue("Library:LutMaxBytes", 16L * 1024 * 1024);
        if (sizeBytes > maximum)
        {
            throw new ApiException(StatusCodes.Status413PayloadTooLarge, "lut_too_large", "LUT 文件超过服务器限制。");
        }

        var target = await store.ReadAsync(state =>
        {
            var current = ApiCommon.CurrentUser(state, identity);
            var lut = OwnedActiveLut(state, current, lutId, PermissionNames.EditOwnAssets);
            if (!lut.HasContent)
            {
                throw Conflict("lut_content_missing", "LUT 尚无原文件，请先完成首次上传。");
            }

            var now = DateTimeOffset.UtcNow;
            var versionId = Guid.NewGuid();
            return new LutReplacementTarget(
                lut.CurrentVersionId,
                versionId,
                BuildObjectKey(now, lut.Id, versionId),
                fileName,
                sizeBytes,
                sha256);
        }, context.RequestAborted);

        var write = await WriteAndValidateAsync(
            new LutUploadTarget(target.NewVersionId, target.ObjectKey, target.SizeBytes, target.Sha256),
            context,
            objects,
            configuration);
        var committed = false;
        try
        {
            var result = await store.UpdateAsync(state =>
            {
                var current = ApiCommon.CurrentUser(state, identity);
                var lut = OwnedActiveLut(state, current, lutId, PermissionNames.EditOwnAssets);
                if (lut.CurrentVersionId != target.PreviousVersionId)
                {
                    throw Conflict("lut_changed_during_upload", "上传期间 LUT 版本已变化，请刷新后重试。");
                }

                var staleKeys = objectDeletions.Enqueue(state, [lut.ObjectKey], DateTimeOffset.UtcNow);
                lut.CurrentVersionId = target.NewVersionId;
                lut.Version++;
                lut.OriginalFileName = target.OriginalFileName;
                lut.SizeBytes = target.SizeBytes;
                lut.ContentHash = target.Sha256;
                lut.ObjectKey = target.ObjectKey;
                lut.HasContent = true;
                lut.UpdatedAt = DateTimeOffset.UtcNow;
                ApiCommon.Audit(state, current.Id, "lut.source.replaced", "lut", lut.Id.ToString(), target.PreviousVersionId.ToString());
                return new LutMutationResult(
                    ApiCommon.ToTeamLut(lut, state.Users.FirstOrDefault(user => user.Id == lut.UploadedByUserId)),
                    staleKeys);
            }, context.RequestAborted);
            committed = true;
            await changes.NotifyAsync(context.RequestAborted, LibraryChangeTarget.Luts(lutId));
            await objectDeletions.TryProcessAsync(result.StaleObjectKeys, CancellationToken.None);
            return Results.Ok(result.Lut);
        }
        finally
        {
            if (!committed)
            {
                await TryDeleteAsync(objects, write.ObjectKey);
            }
        }
    }

    private static async Task<IResult> DownloadAsync(
        Guid lutId,
        HttpContext context,
        IAppDataStore store,
        IObjectStore objects)
    {
        var identity = AccessControl.RequireUser(context);
        var target = await store.ReadAsync(state =>
        {
            var current = ApiCommon.CurrentUser(state, identity);
            var lut = VisibleLut(state, current, lutId, allowRecycledOwner: false);
            if (!lut.HasContent)
            {
                throw Conflict("lut_content_missing", "LUT 文件尚未上传。");
            }

            return new LutDownloadTarget(lut.ObjectKey, lut.OriginalFileName, lut.ContentHash, lut.UpdatedAt);
        }, context.RequestAborted);
        var direct = await objects.CreateDownloadUrlAsync(
            target.ObjectKey,
            target.FileName,
            context.RequestAborted);
        if (direct is not null)
        {
            context.Response.Headers["X-IAL-Object-Url-Expires"] = direct.ExpiresAt.ToString("O");
            return Results.Redirect(direct.Url.AbsoluteUri, permanent: false, preserveMethod: true);
        }

        var stream = await objects.OpenReadAsync(target.ObjectKey, context.RequestAborted)
            ?? throw Conflict("lut_object_missing", "LUT 元数据存在，但对象存储中的文件缺失。");
        return Results.File(
            stream,
            "application/x-cube",
            target.FileName,
            target.LastModified,
            new EntityTagHeaderValue($"\"{target.Sha256}\""),
            enableRangeProcessing: true);
    }

    private static async Task<IResult> RecycleAsync(
        Guid lutId,
        HttpContext context,
        IAppDataStore store,
        IConfiguration configuration,
        ILibraryChangeNotifier changes)
    {
        var identity = AccessControl.RequireUser(context);
        var retentionDays = Math.Clamp(configuration.GetValue("Library:RecycleRetentionDays", 15), 1, 365);
        var result = await store.UpdateAsync(state =>
        {
            var current = ApiCommon.CurrentUser(state, identity);
            var lut = OwnedActiveLut(state, current, lutId, PermissionNames.DeleteOwnAssets);
            lut.State = Data.TeamLutState.Recycled;
            lut.RecycledAt = DateTimeOffset.UtcNow;
            lut.PurgeAfter = lut.RecycledAt.Value.AddDays(retentionDays);
            lut.UpdatedAt = lut.RecycledAt.Value;
            ApiCommon.Audit(state, current.Id, "lut.recycled", "lut", lut.Id.ToString());
            return ApiCommon.ToTeamLut(lut, state.Users.FirstOrDefault(user => user.Id == lut.UploadedByUserId));
        }, context.RequestAborted);
        await changes.NotifyAsync(context.RequestAborted, LibraryChangeTarget.Luts(lutId));
        return Results.Ok(result);
    }

    private static async Task<IResult> RestoreAsync(
        Guid lutId,
        HttpContext context,
        IAppDataStore store,
        ILibraryChangeNotifier changes)
    {
        var identity = AccessControl.RequireUser(context);
        var result = await store.UpdateAsync(state =>
        {
            var current = ApiCommon.CurrentUser(state, identity);
            var lut = OwnedLut(state, current, lutId, PermissionNames.DeleteOwnAssets);
            if (lut.State != Data.TeamLutState.Recycled)
            {
                throw Conflict("lut_not_recycled", "LUT 不在回收站中。");
            }

            lut.State = Data.TeamLutState.Active;
            lut.RecycledAt = null;
            lut.PurgeAfter = null;
            lut.UpdatedAt = DateTimeOffset.UtcNow;
            ApiCommon.Audit(state, current.Id, "lut.restored", "lut", lut.Id.ToString());
            return ApiCommon.ToTeamLut(lut, state.Users.FirstOrDefault(user => user.Id == lut.UploadedByUserId));
        }, context.RequestAborted);
        await changes.NotifyAsync(context.RequestAborted, LibraryChangeTarget.Luts(lutId));
        return Results.Ok(result);
    }

    private static async Task<IResult> PermanentlyDeleteAsync(
        Guid lutId,
        HttpContext context,
        IAppDataStore store,
        ObjectDeletionOutbox objectDeletions,
        ILibraryChangeNotifier changes)
    {
        var identity = AccessControl.RequireUser(context);
        var keys = await store.UpdateAsync(state =>
        {
            var current = ApiCommon.CurrentUser(state, identity);
            var lut = OwnedLut(state, current, lutId, PermissionNames.DeleteOwnAssets);
            if (lut.State != Data.TeamLutState.Recycled)
            {
                throw Conflict("recycle_first", "永久删除 LUT 前必须先移入回收站。");
            }

            var queued = lut.HasContent
                ? objectDeletions.Enqueue(state, [lut.ObjectKey], DateTimeOffset.UtcNow)
                : [];
            state.TeamLuts.Remove(lut);
            ApiCommon.Audit(state, current.Id, "lut.permanently_deleted", "lut", lut.Id.ToString(), lut.ObjectKey);
            return queued;
        }, context.RequestAborted);
        await changes.NotifyAsync(context.RequestAborted, LibraryChangeTarget.Luts(lutId));
        await objectDeletions.TryProcessAsync(keys, CancellationToken.None);
        return Results.NoContent();
    }

    private static async Task<ObjectWriteResult> WriteAndValidateAsync(
        LutUploadTarget target,
        HttpContext context,
        IObjectStore objects,
        IConfiguration configuration)
    {
        var maximum = configuration.GetValue("Library:LutMaxBytes", 16L * 1024 * 1024);
        if (target.SizeBytes > maximum)
        {
            throw new ApiException(StatusCodes.Status413PayloadTooLarge, "lut_too_large", "LUT 文件超过服务器限制。");
        }

        ValidateContentType(context.Request.ContentType);
        PrepareRequestBody(context, target.SizeBytes);
        ObjectWriteResult write;
        try
        {
            write = await objects.PutAsync(target.ObjectKey, context.Request.Body, maximum, context.RequestAborted);
        }
        catch (InvalidDataException)
        {
            throw new ApiException(StatusCodes.Status413PayloadTooLarge, "lut_too_large", "LUT 文件超过服务器限制。");
        }

        if (write.SizeBytes != target.SizeBytes || !write.Sha256.Equals(target.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            await TryDeleteAsync(objects, target.ObjectKey);
            throw new ApiException(StatusCodes.Status400BadRequest, "lut_content_mismatch", "LUT 文件大小或 SHA-256 与声明不一致。");
        }

        try
        {
            await using var stream = await objects.OpenReadAsync(target.ObjectKey, context.RequestAborted)
                ?? throw Conflict("lut_object_missing", "LUT 写入后无法读取。");
            _ = await CubeLutValidator.ValidateAsync(stream, context.RequestAborted);
        }
        catch
        {
            await TryDeleteAsync(objects, target.ObjectKey);
            throw;
        }

        return write;
    }

    private static TeamLutRecord OwnedActiveLut(
        AppState state,
        UserRecord current,
        Guid lutId,
        string permission,
        bool enforceMemberPermission = true)
    {
        var lut = OwnedLut(state, current, lutId, permission, enforceMemberPermission);
        if (lut.State != Data.TeamLutState.Active)
        {
            throw Conflict("lut_recycled", "回收站中的 LUT 不能修改。");
        }

        return lut;
    }

    private static TeamLutRecord OwnedLut(
        AppState state,
        UserRecord current,
        Guid lutId,
        string permission,
        bool enforceMemberPermission = true)
    {
        var lut = state.TeamLuts.FirstOrDefault(item => item.Id == lutId)
            ?? throw NotFound();
        if (!current.IsAdmin && lut.UploadedByUserId != current.Id)
        {
            throw new ApiException(StatusCodes.Status403Forbidden, "lut_owner_required", "只有上传者或管理员可以修改此 LUT。");
        }

        if (!current.IsAdmin && enforceMemberPermission)
        {
            ApiCommon.RequirePermission(current, permission);
        }

        return lut;
    }

    private static TeamLutRecord VisibleLut(
        AppState state,
        UserRecord current,
        Guid lutId,
        bool allowRecycledOwner)
    {
        var lut = state.TeamLuts.FirstOrDefault(item => item.Id == lutId) ?? throw NotFound();
        if (lut.State == Data.TeamLutState.Recycled &&
            (!allowRecycledOwner || !current.IsAdmin && lut.UploadedByUserId != current.Id))
        {
            throw NotFound();
        }

        return lut;
    }

    private static string ValidateFileName(string? value)
    {
        var fileName = Path.GetFileName(value?.Trim());
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 255 ||
            !Path.GetExtension(fileName).Equals(".cube", StringComparison.OrdinalIgnoreCase))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_lut_file_name", "LUT 文件名必须使用 .cube 扩展名。");
        }

        return fileName;
    }

    private static string ValidateSha256(string? value)
    {
        var result = (value ?? string.Empty).Trim().ToUpperInvariant();
        if (result.Length != 64 || result.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_content_hash", "SHA-256 必须为 64 位十六进制值。");
        }

        return result;
    }

    private static long ParsePositiveInt64(string? value, string message) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result) && result > 0
            ? result
            : throw new ApiException(StatusCodes.Status400BadRequest, "invalid_lut_size", message);

    private static int ParsePage(string? value, int fallback) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result) && result > 0
            ? result
            : fallback;

    private static string BuildObjectKey(DateTimeOffset now, Guid lutId, Guid versionId) =>
        $"luts/{now:yyyy/MM}/{lutId:N}/{versionId:N}.cube";

    private static void ValidateContentType(string? contentType)
    {
        var value = contentType?.Split(';', 2)[0].Trim();
        if (!string.Equals(value, "application/octet-stream", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(value, "application/x-cube", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(value, "text/plain", StringComparison.OrdinalIgnoreCase))
        {
            throw new ApiException(StatusCodes.Status415UnsupportedMediaType, "invalid_lut_content_type", "LUT 上传正文必须是 CUBE 文本文件。");
        }
    }

    private static void PrepareRequestBody(HttpContext context, long expectedBytes)
    {
        if (context.Request.ContentLength is not null && context.Request.ContentLength != expectedBytes)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "content_length_mismatch", "Content-Length 与声明的 LUT 大小不一致。");
        }

        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false })
        {
            feature.MaxRequestBodySize = expectedBytes;
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
        }
    }

    private static ApiException Conflict(string code, string message) =>
        new(StatusCodes.Status409Conflict, code, message);

    private static ApiException NotFound() =>
        new(StatusCodes.Status404NotFound, "lut_not_found", "LUT 不存在。");

    private sealed record LutUploadTarget(Guid VersionId, string ObjectKey, long SizeBytes, string Sha256);

    private sealed record LutReplacementTarget(
        Guid PreviousVersionId,
        Guid NewVersionId,
        string ObjectKey,
        string OriginalFileName,
        long SizeBytes,
        string Sha256);

    private sealed record LutDownloadTarget(
        string ObjectKey,
        string FileName,
        string Sha256,
        DateTimeOffset LastModified);

    private sealed record LutMutationResult(TeamLutSummary Lut, string[] StaleObjectKeys);
}
