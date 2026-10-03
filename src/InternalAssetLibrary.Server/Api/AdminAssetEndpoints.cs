using System.Globalization;
using InternalAssetLibrary.Contracts;
using InternalAssetLibrary.Server.Data;
using InternalAssetLibrary.Server.Realtime;
using InternalAssetLibrary.Server.Security;
using InternalAssetLibrary.Server.Services;

namespace InternalAssetLibrary.Server.Api;

internal static class AdminAssetEndpoints
{
    public static void MapAdminAssetEndpoints(this WebApplication app)
    {
        app.MapGet("/api/admin/users", ListAdminUsersAsync);
        app.MapPost("/api/admin/users", CreateUserAsync);
        app.MapPut("/api/admin/users/{userId:guid}/status", SetAccountStatusAsync);
        app.MapDelete("/api/admin/users/{userId:guid}", DeleteUserAsync);
        app.MapPut("/api/admin/users/{userId:guid}/permissions", SetPermissionsAsync);
        app.MapPut("/api/admin/users/{userId:guid}/username", SetUsernameAsync);
        app.MapPost("/api/admin/users/{userId:guid}/reset-password", ResetPasswordAsync);
        app.MapDelete("/api/admin/users/{userId:guid}/email", ClearEmailAsync);
        app.MapDelete("/api/admin/users/{userId:guid}/avatar", ClearAvatarAsync);
        app.MapPost("/api/admin/users/{userId:guid}/public-profile/clear", ClearPublicProfileAsync);
        app.MapGet("/api/admin/audit", ListAuditAsync);
        app.MapGet("/api/admin/settings", GetServerSettingsAsync);
        app.MapPut("/api/admin/settings", UpdateServerSettingsAsync);

        app.MapGet("/api/tags", ListTagsAsync);
        app.MapPost("/api/tags", CreateTagAsync);
        app.MapPut("/api/tags/{tagId:guid}", RenameTagAsync);
        app.MapDelete("/api/tags/{tagId:guid}", DeleteTagAsync);

        app.MapGet("/api/assets", ListAssetsAsync);
        app.MapGet("/api/assets/{assetId:guid}", GetAssetAsync);
        app.MapPost("/api/assets", CreateAssetAsync);
        app.MapPut("/api/assets/{assetId:guid}", UpdateAssetAsync);
        app.MapPut("/api/assets/{assetId:guid}/category", UpdateAssetCategoryAsync);
        app.MapPut("/api/assets/{assetId:guid}/tags", UpdateTagsAsync);
        app.MapDelete("/api/assets/{assetId:guid}", RecycleAssetAsync);
        app.MapPost("/api/assets/{assetId:guid}/restore", RestoreAssetAsync);
        app.MapDelete("/api/assets/{assetId:guid}/permanent", PermanentlyDeleteAssetAsync);
        app.MapGet("/api/me/recycle-bin", ListMyRecycleBinAsync);
        app.MapDelete("/api/me/recycle-bin", ClearMyRecycleBinAsync);
        app.MapPost("/api/me/recycle-bin/{assetId:guid}/restore", RestoreMyRecycleBinAssetAsync);
        app.MapDelete("/api/me/recycle-bin/{assetId:guid}", PermanentlyDeleteMyRecycleBinAssetAsync);
        app.MapGet("/api/library/status", GetLibraryStatusAsync);
    }

    private static async Task<IResult> ListAdminUsersAsync(HttpContext context, IAppDataStore store)
    {
        _ = AccessControl.RequireAdmin(context);
        var users = await store.ReadAsync(state => state.Users
            .OrderByDescending(user => user.IsEnabled)
            .ThenBy(user => user.Username)
            .Select(ApiCommon.ToAdminSummary).ToList(), context.RequestAborted);
        return Results.Ok(users);
    }

    private static async Task<IResult> CreateUserAsync(
        CreateUserRequest request,
        HttpContext context,
        IAppDataStore store,
        PasswordService passwords,
        ILibraryChangeNotifier changes)
    {
        var identity = AccessControl.RequireAdmin(context);
        var username = ApiCommon.ValidateUsername(request.Username);
        var normalized = ApiCommon.Normalize(username);
        var temporaryPassword = request.TemporaryPassword ?? string.Empty;
        if (!PasswordService.IsAcceptable(temporaryPassword, out var error))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "weak_password", error!);
        }

        var displayName = request.DisplayName?.Trim() ?? string.Empty;
        if (displayName.Length > 40)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_profile", "显示名称不能超过 40 个字符。");
        }

        var permissions = request.Permissions is null
            ? PermissionNames.MemberDefaults()
            : ApiCommon.ValidatePermissions(request.Permissions);
        var now = DateTimeOffset.UtcNow;
        var passwordHash = passwords.Hash(temporaryPassword);
        var user = await store.UpdateAsync(state =>
        {
            _ = ApiCommon.CurrentAdmin(state, identity);
            if (state.Users.Any(item => item.NormalizedUsername == normalized))
            {
                throw new ApiException(StatusCodes.Status409Conflict, "username_in_use", "该用户名已存在。");
            }

            var created = new UserRecord
            {
                Id = Guid.NewGuid(),
                Username = username,
                NormalizedUsername = normalized,
                DisplayName = displayName,
                PasswordHash = passwordHash,
                MustChangePassword = true,
                IsEnabled = true,
                IsAdmin = request.IsAdmin,
                Permissions = permissions,
                CreatedAt = now,
                UpdatedAt = now
            };
            state.Users.Add(created);
            ApiCommon.Audit(state, identity.UserId, "admin.user.created", "user", created.Id.ToString(), $"@{created.Username}");
            return created;
        }, context.RequestAborted);
        await changes.NotifyAsync(context.RequestAborted, LibraryChangeTarget.Users(user.Id));
        return Results.Created($"/api/users/{user.Id}", ApiCommon.ToAdminSummary(user));
    }

    private static async Task<IResult> SetAccountStatusAsync(
        Guid userId,
        AccountStatusRequest request,
        HttpContext context,
        IAppDataStore store,
        ILibraryChangeNotifier changes)
    {
        var identity = AccessControl.RequireAdmin(context);
        if (identity.UserId == userId && !request.IsEnabled)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "cannot_disable_self", "不能停用当前登录的管理员账号。");
        }

        var user = await store.UpdateAsync(state =>
        {
            _ = ApiCommon.CurrentAdmin(state, identity);
            var target = state.Users.FirstOrDefault(item => item.Id == userId)
                ?? throw new ApiException(StatusCodes.Status404NotFound, "user_not_found", "用户不存在。");

            if (!request.IsEnabled && target.IsAdmin && target.IsEnabled &&
                state.Users.Count(item => item.IsAdmin && item.IsEnabled) <= 1)
            {
                throw new ApiException(StatusCodes.Status409Conflict, "last_administrator", "不能停用最后一个可用管理员账号。");
            }

            target.IsEnabled = request.IsEnabled;
            target.UpdatedAt = DateTimeOffset.UtcNow;
            if (!target.IsEnabled)
            {
                state.Sessions.RemoveAll(session => session.UserId == target.Id);
            }

            ApiCommon.Audit(
                state,
                identity.UserId,
                target.IsEnabled ? "admin.user.enabled" : "admin.user.disabled",
                "user",
                target.Id.ToString());
            return target;
        }, context.RequestAborted);
        await changes.NotifyAsync(context.RequestAborted, LibraryChangeTarget.Users(user.Id));
        return Results.Ok(ApiCommon.ToAdminSummary(user));
    }

    private static async Task<IResult> SetPermissionsAsync(
        Guid userId,
        AccountPermissionsRequest request,
        HttpContext context,
        IAppDataStore store,
        ILibraryChangeNotifier changes)
    {
        var identity = AccessControl.RequireAdmin(context);
        var permissions = ApiCommon.ValidatePermissions(request.Permissions);
        var user = await store.UpdateAsync(state =>
        {
            _ = ApiCommon.CurrentAdmin(state, identity);
            var target = state.Users.FirstOrDefault(item => item.Id == userId)
                ?? throw new ApiException(StatusCodes.Status404NotFound, "user_not_found", "用户不存在。");
            if (target.IsAdmin && !request.IsAdmin && target.IsEnabled &&
                state.Users.Count(item => item.IsAdmin && item.IsEnabled) <= 1)
            {
                throw new ApiException(StatusCodes.Status409Conflict, "last_administrator", "不能移除最后一个可用管理员的管理员权限。");
            }

            target.IsAdmin = request.IsAdmin;
            target.Permissions = permissions;
            target.UpdatedAt = DateTimeOffset.UtcNow;
            ApiCommon.Audit(state, identity.UserId, "admin.user.permissions.updated", "user", target.Id.ToString());
            return target;
        }, context.RequestAborted);
        await changes.NotifyAsync(context.RequestAborted, LibraryChangeTarget.Users(user.Id));
        return Results.Ok(ApiCommon.ToAdminSummary(user));
    }

    private static async Task<IResult> DeleteUserAsync(
        Guid userId,
        HttpContext context,
        IAppDataStore store,
        AvatarFileStore files,
        ILoggerFactory loggerFactory,
        ILibraryChangeNotifier changes)
    {
        var identity = AccessControl.RequireAdmin(context);
        if (identity.UserId == userId)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "cannot_delete_self", "不能删除当前登录的管理员账号。");
        }

        var avatarFileName = await store.UpdateAsync(state =>
        {
            _ = ApiCommon.CurrentAdmin(state, identity);
            var target = state.Users.FirstOrDefault(item => item.Id == userId)
                ?? throw new ApiException(StatusCodes.Status404NotFound, "user_not_found", "用户不存在。");
            if (target.IsEnabled)
            {
                throw new ApiException(StatusCodes.Status409Conflict, "user_must_be_disabled", "必须先停用账号，才能永久删除。");
            }

            foreach (var asset in state.Assets.Where(item => item.UploadedByUserId == target.Id))
            {
                asset.UploadedByUsername = target.Username;
                asset.UploadedByDisplayName = target.DisplayName;
            }

            foreach (var folder in state.AssetFolders.Where(item => item.CreatedByUserId == target.Id))
            {
                folder.CreatedByUsername = target.Username;
                folder.CreatedByDisplayName = target.DisplayName;
            }

            var ownerDisplayName = string.IsNullOrWhiteSpace(target.DisplayName)
                ? target.Username
                : target.DisplayName;
            foreach (var markerSet in state.MarkerSets.Where(item => item.OwnerUserId == target.Id))
            {
                markerSet.OwnerDisplayName = ownerDisplayName;
            }

            state.Sessions.RemoveAll(session => session.UserId == target.Id);
            state.Users.Remove(target);
            ApiCommon.Audit(state, identity.UserId, "admin.user.deleted", "user", target.Id.ToString(), $"@{target.Username}");
            return target.AvatarFileName;
        }, context.RequestAborted);

        try
        {
            await files.DeleteAsync(avatarFileName, CancellationToken.None);
        }
        catch (Exception exception)
        {
            loggerFactory.CreateLogger("DeletedAccountAvatarCleanup").LogError(
                exception,
                "Could not delete avatar file {AvatarFileName} after account {UserId} was permanently deleted.",
                avatarFileName,
                userId);
        }
        await changes.NotifyAsync(
            context.RequestAborted,
            LibraryChangeTarget.Users(userId),
            LibraryChangeTarget.Profiles(userId),
            LibraryChangeTarget.Assets(null),
            LibraryChangeTarget.Markers(null));
        return Results.NoContent();
    }

    private static async Task<IResult> SetUsernameAsync(
        Guid userId,
        UsernameRequest request,
        HttpContext context,
        IAppDataStore store,
        ILibraryChangeNotifier changes)
    {
        var identity = AccessControl.RequireAdmin(context);
        var username = ApiCommon.ValidateUsername(request.Username);
        var normalized = ApiCommon.Normalize(username);
        var user = await store.UpdateAsync(state =>
        {
            _ = ApiCommon.CurrentAdmin(state, identity);
            var target = state.Users.FirstOrDefault(item => item.Id == userId)
                ?? throw new ApiException(StatusCodes.Status404NotFound, "user_not_found", "用户不存在。");
            if (state.Users.Any(item => item.Id != target.Id && item.NormalizedUsername == normalized))
            {
                throw new ApiException(StatusCodes.Status409Conflict, "username_in_use", "该用户名已存在。");
            }

            target.Username = username;
            target.NormalizedUsername = normalized;
            target.UpdatedAt = DateTimeOffset.UtcNow;
            ApiCommon.Audit(state, identity.UserId, "admin.user.username.updated", "user", target.Id.ToString());
            return target;
        }, context.RequestAborted);
        await changes.NotifyAsync(
            context.RequestAborted,
            LibraryChangeTarget.Users(user.Id),
            LibraryChangeTarget.Profiles(user.Id));
        return Results.Ok(ApiCommon.ToAdminSummary(user));
    }

    private static async Task<IResult> ResetPasswordAsync(
        Guid userId,
        ResetPasswordRequest request,
        HttpContext context,
        IAppDataStore store,
        PasswordService passwords,
        ILibraryChangeNotifier changes)
    {
        var identity = AccessControl.RequireAdmin(context);
        var temporaryPassword = request.TemporaryPassword ?? string.Empty;
        if (!PasswordService.IsAcceptable(temporaryPassword, out var error))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "weak_password", error!);
        }

        var hash = passwords.Hash(temporaryPassword);
        await store.UpdateAsync(state =>
        {
            _ = ApiCommon.CurrentAdmin(state, identity);
            var target = state.Users.FirstOrDefault(item => item.Id == userId)
                ?? throw new ApiException(StatusCodes.Status404NotFound, "user_not_found", "用户不存在。");
            target.PasswordHash = hash;
            target.MustChangePassword = true;
            target.UpdatedAt = DateTimeOffset.UtcNow;
            state.Sessions.RemoveAll(session => session.UserId == target.Id);
            ApiCommon.Audit(state, identity.UserId, "admin.user.password.reset", "user", target.Id.ToString());
            return true;
        }, context.RequestAborted);
        await changes.NotifyAsync(context.RequestAborted, LibraryChangeTarget.Users(userId));
        return Results.NoContent();
    }

    private static async Task<IResult> ClearEmailAsync(
        Guid userId,
        HttpContext context,
        IAppDataStore store,
        ILibraryChangeNotifier changes)
    {
        var identity = AccessControl.RequireAdmin(context);
        await store.UpdateAsync(state =>
        {
            _ = ApiCommon.CurrentAdmin(state, identity);
            var target = state.Users.FirstOrDefault(item => item.Id == userId)
                ?? throw new ApiException(StatusCodes.Status404NotFound, "user_not_found", "用户不存在。");
            target.Email = null;
            target.NormalizedEmail = null;
            target.UpdatedAt = DateTimeOffset.UtcNow;
            ApiCommon.Audit(state, identity.UserId, "admin.user.email.cleared", "user", target.Id.ToString());
            return true;
        }, context.RequestAborted);
        await changes.NotifyAsync(context.RequestAborted, LibraryChangeTarget.Users(userId));
        return Results.NoContent();
    }

    private static async Task<IResult> ClearAvatarAsync(
        Guid userId,
        HttpContext context,
        IAppDataStore store,
        AvatarFileStore files,
        ILibraryChangeNotifier changes,
        ObjectDeletionOutbox objectDeletions,
        ILoggerFactory loggerFactory)
    {
        var identity = AccessControl.RequireAdmin(context);
        var logger = loggerFactory.CreateLogger("AdminAssetEndpoints");
        var fileName = await store.UpdateAsync(state =>
        {
            _ = ApiCommon.CurrentAdmin(state, identity);
            var target = state.Users.FirstOrDefault(item => item.Id == userId)
                ?? throw new ApiException(StatusCodes.Status404NotFound, "user_not_found", "用户不存在。");
            var old = target.AvatarFileName;
            target.AvatarFileName = null;
            target.AvatarContentType = null;
            target.UpdatedAt = DateTimeOffset.UtcNow;
            ApiCommon.Audit(state, identity.UserId, "admin.user.avatar.cleared", "user", target.Id.ToString());
            return old;
        }, context.RequestAborted);
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            try
            {
                await files.DeleteAsync(fileName, CancellationToken.None);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "头像文件 {AvatarFileName} 清理失败，将安排重试。", fileName);
                if (fileName.Contains('/'))
                {
                    await store.UpdateAsync(state =>
                    {
                        objectDeletions.Enqueue(state, [fileName], DateTimeOffset.UtcNow);
                        return true;
                    }, CancellationToken.None);
                }
            }
        }
        await changes.NotifyAsync(
            context.RequestAborted,
            LibraryChangeTarget.Profiles(userId),
            LibraryChangeTarget.Users(userId));
        return Results.NoContent();
    }

    private static async Task<IResult> ClearPublicProfileAsync(
        Guid userId,
        ClearPublicProfileRequest request,
        HttpContext context,
        IAppDataStore store,
        ILibraryChangeNotifier changes)
    {
        var identity = AccessControl.RequireAdmin(context);
        var requestedFields = ValidatePublicProfileFields(request.Fields);
        var result = await store.UpdateAsync(state =>
        {
            _ = ApiCommon.CurrentAdmin(state, identity);
            var target = state.Users.FirstOrDefault(item => item.Id == userId)
                ?? throw new ApiException(StatusCodes.Status404NotFound, "user_not_found", "用户不存在。");
            var clearedFields = new List<string>();

            if (requestedFields.Contains("displayName") && target.DisplayName.Length > 0)
            {
                target.DisplayName = string.Empty;
                clearedFields.Add("displayName");
            }

            if (requestedFields.Contains("bio") && target.Bio is not null)
            {
                target.Bio = null;
                clearedFields.Add("bio");
            }

            ClearTeamField(
                requestedFields,
                "birthday",
                target.BirthdayVisibility,
                () =>
                {
                    target.Birthday = null;
                    target.BirthdayVisibility = ProfileVisibility.Private;
                },
                clearedFields);
            ClearTeamField(
                requestedFields,
                "gender",
                target.GenderVisibility,
                () =>
                {
                    target.Gender = null;
                    target.CustomGender = null;
                    target.GenderVisibility = ProfileVisibility.Private;
                },
                clearedFields);
            ClearTeamField(
                requestedFields,
                "contact",
                target.ContactVisibility,
                () =>
                {
                    target.Contact = null;
                    target.ContactVisibility = ProfileVisibility.Private;
                },
                clearedFields);

            if (clearedFields.Count > 0)
            {
                target.UpdatedAt = DateTimeOffset.UtcNow;
            }

            ApiCommon.Audit(
                state,
                identity.UserId,
                "admin.user.public_profile.cleared",
                "user",
                target.Id.ToString(),
                clearedFields.Count == 0 ? null : string.Join(',', clearedFields));
            return new ClearPublicProfileResult(target.Id, clearedFields);
        }, context.RequestAborted);
        await changes.NotifyAsync(
            context.RequestAborted,
            LibraryChangeTarget.Profiles(userId),
            LibraryChangeTarget.Users(userId));
        return Results.Ok(result);
    }

    private static HashSet<string> ValidatePublicProfileFields(IEnumerable<string>? fields)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "displayName",
            "bio",
            "birthday",
            "gender",
            "contact"
        };
        var requested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in fields ?? [])
        {
            var normalized = field?.Trim() ?? string.Empty;
            if (!allowed.Contains(normalized))
            {
                throw new ApiException(
                    StatusCodes.Status400BadRequest,
                    "invalid_profile_field",
                    "只能清除 displayName、bio、birthday、gender 或 contact。");
            }

            requested.Add(allowed.First(item => item.Equals(normalized, StringComparison.OrdinalIgnoreCase)));
        }

        return requested;
    }

    private static void ClearTeamField(
        IReadOnlySet<string> requestedFields,
        string field,
        ProfileVisibility visibility,
        Action clear,
        ICollection<string> clearedFields)
    {
        if (!requestedFields.Contains(field) || visibility != ProfileVisibility.Team)
        {
            return;
        }

        clear();
        clearedFields.Add(field);
    }

    private static async Task<IResult> ListAuditAsync(HttpContext context, IAppDataStore store)
    {
        _ = AccessControl.RequireAdmin(context);
        var limit = QueryInt(context, "limit", 100, 1, 500);
        var records = await store.ReadAsync(state => state.AuditLog
            .OrderByDescending(item => item.OccurredAt)
            .Take(limit)
            .ToList(), context.RequestAborted);
        return Results.Ok(records);
    }

    private static IResult GetServerSettingsAsync(HttpContext context, ServerSettingsService settings)
    {
        _ = AccessControl.RequireAdmin(context);
        return Results.Ok(settings.Current.ToResponse());
    }

    private static async Task<IResult> UpdateServerSettingsAsync(
        ServerSettingsRequest request,
        HttpContext context,
        ServerSettingsService settings)
    {
        var identity = AccessControl.RequireAdmin(context);
        var updated = await settings.UpdateAsync(
            new ServerSettingsUpdate(
                request.OriginalQuotaBytes, request.AudioMaxBytes, request.ImageMaxBytes,
                request.VideoMaxBytes, request.ThumbnailMaxBytes, request.ProxyMaxBytes,
                request.LutMaxBytes, request.RecycleRetentionDays, request.AuditRetentionDays,
                request.DownloadLogRetentionDays, request.BackupsEnabled, request.BackupIntervalHours,
                request.BackupRetentionDays, request.BackupLocalPath, request.BackupObjectPrefix,
                request.CapacityWarningRatio, request.CapacityCriticalRatio, request.DerivativesEnabled,
                request.DerivativePollIntervalSeconds, request.DerivativeProcessTimeoutSeconds,
                request.ThumbnailMaxEdge, request.SessionLifetimeDays, request.LoginFailureLimit,
                request.LoginFailureWindowMinutes, request.LoginBlockMinutes,
                request.MultipartPartSizeBytes, request.UploadSessionLifetimeHours, request.SignedUrlLifetimeMinutes),
            identity.UserId,
            context.RequestAborted);
        return Results.Ok(updated.ToResponse());
    }

    private static async Task<IResult> ListTagsAsync(HttpContext context, IAppDataStore store)
    {
        var identity = AccessControl.RequirePermission(context, PermissionNames.BrowseAssets);
        var tags = await store.ReadAsync(state =>
        {
            var current = ApiCommon.CurrentUser(state, identity);
            ApiCommon.RequirePermission(current, PermissionNames.BrowseAssets);
            return state.Tags
                .OrderBy(tag => tag.Name, StringComparer.OrdinalIgnoreCase)
                .Select(tag => ToTag(tag, state.Assets.Count(asset => asset.Tags.Contains(tag.Name))))
                .ToList();
        }, context.RequestAborted);
        return Results.Ok(tags);
    }

    private static async Task<IResult> CreateTagAsync(
        TagRequest request,
        HttpContext context,
        IAppDataStore store,
        ILibraryChangeNotifier changes)
    {
        var identity = AccessControl.RequirePermission(context, PermissionNames.ModifyTags);
        var name = ApiCommon.ValidateTagName(request.Name);
        var tag = await store.UpdateAsync(state =>
        {
            var current = ApiCommon.CurrentUser(state, identity);
            ApiCommon.RequirePermission(current, PermissionNames.ModifyTags);
            if (state.Tags.Any(tag => tag.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ApiException(StatusCodes.Status409Conflict, "tag_name_in_use", "该标签名称已存在。");
            }

            var now = DateTimeOffset.UtcNow;
            var created = new TagRecord
            {
                Id = Guid.NewGuid(),
                Name = name,
                CreatedByUserId = current.Id,
                CreatedAt = now,
                UpdatedAt = now
            };
            state.Tags.Add(created);
            ApiCommon.Audit(state, current.Id, "tag.created", "tag", created.Id.ToString(), created.Name);
            return created;
        }, context.RequestAborted);
        await changes.NotifyAsync(context.RequestAborted, LibraryChangeTarget.Tags(tag.Id));
        return Results.Created($"/api/tags/{tag.Id}", ToTag(tag, 0));
    }

    private static async Task<IResult> RenameTagAsync(
        Guid tagId,
        TagRequest request,
        HttpContext context,
        IAppDataStore store,
        ILibraryChangeNotifier changes)
    {
        var identity = AccessControl.RequireAdmin(context);
        var name = ApiCommon.ValidateTagName(request.Name);
        var result = await store.UpdateAsync(state =>
        {
            var current = ApiCommon.CurrentAdmin(state, identity);
            var tag = state.Tags.FirstOrDefault(item => item.Id == tagId)
                ?? throw new ApiException(StatusCodes.Status404NotFound, "tag_not_found", "标签不存在。");
            if (state.Tags.Any(item => item.Id != tag.Id && item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ApiException(StatusCodes.Status409Conflict, "tag_name_in_use", "该标签名称已存在。");
            }

            if (string.Equals(tag.Name, name, StringComparison.Ordinal))
            {
                return new { Tag = tag, UsageCount = state.Assets.Count(asset => asset.Tags.Contains(tag.Name)) };
            }

            var oldName = tag.Name;
            var now = DateTimeOffset.UtcNow;
            var affectedAssets = 0;
            foreach (var asset in state.Assets.Where(asset => asset.Tags.Contains(oldName)))
            {
                asset.Tags.Remove(oldName);
                asset.Tags.Add(name);
                asset.UpdatedAt = now;
                affectedAssets++;
            }

            tag.Name = name;
            tag.UpdatedAt = now;
            ApiCommon.Audit(
                state,
                current.Id,
                "tag.renamed",
                "tag",
                tag.Id.ToString(),
                $"{oldName} -> {name}; assets={affectedAssets}");
            return new { Tag = tag, UsageCount = affectedAssets };
        }, context.RequestAborted);
        await changes.NotifyAsync(
            context.RequestAborted,
            LibraryChangeTarget.Tags(tagId),
            LibraryChangeTarget.Assets(null));
        return Results.Ok(ToTag(result.Tag, result.UsageCount));
    }

    private static async Task<IResult> DeleteTagAsync(
        Guid tagId,
        HttpContext context,
        IAppDataStore store,
        ILibraryChangeNotifier changes)
    {
        var identity = AccessControl.RequireAdmin(context);
        await store.UpdateAsync(state =>
        {
            var current = ApiCommon.CurrentAdmin(state, identity);
            var tag = state.Tags.FirstOrDefault(item => item.Id == tagId)
                ?? throw new ApiException(StatusCodes.Status404NotFound, "tag_not_found", "标签不存在。");
            var now = DateTimeOffset.UtcNow;
            var affectedAssets = 0;
            foreach (var asset in state.Assets.Where(asset => asset.Tags.Contains(tag.Name)))
            {
                asset.Tags.Remove(tag.Name);
                asset.UpdatedAt = now;
                affectedAssets++;
            }

            state.Tags.Remove(tag);
            ApiCommon.Audit(
                state,
                current.Id,
                "tag.deleted",
                "tag",
                tag.Id.ToString(),
                $"{tag.Name}; assets={affectedAssets}");
            return true;
        }, context.RequestAborted);
        await changes.NotifyAsync(
            context.RequestAborted,
            LibraryChangeTarget.Tags(tagId),
            LibraryChangeTarget.Assets(null));
        return Results.NoContent();
    }

    private static Task<IResult> ListAssetsAsync(HttpContext context, IAppDataStore store) =>
        ListAssetsCoreAsync(context, store, personalRecycleBin: false);

    private static Task<IResult> ListMyRecycleBinAsync(HttpContext context, IAppDataStore store) =>
        ListAssetsCoreAsync(context, store, personalRecycleBin: true);

    private static async Task<IResult> ListAssetsCoreAsync(
        HttpContext context,
        IAppDataStore store,
        bool personalRecycleBin)
    {
        var identity = AccessControl.RequirePermission(context, PermissionNames.BrowseAssets);
        var search = context.Request.Query["search"].ToString().Trim();
        var category = context.Request.Query["category"].ToString().Trim();
        var tag = context.Request.Query["tag"].ToString().Trim();
        var uploaderText = context.Request.Query["uploaderId"].ToString();
        var uploaderId = Guid.Empty;
        var hasUploader = !personalRecycleBin && Guid.TryParse(uploaderText, out uploaderId);
        var trash = personalRecycleBin ||
            context.Request.Query["state"].ToString().Equals("recycled", StringComparison.OrdinalIgnoreCase);
        var page = QueryInt(context, "page", 1, 1, 100_000);
        var pageSize = QueryInt(context, "pageSize", 40, 1, 100);
        var requestedSort = context.Request.Query["sort"].ToString().ToLowerInvariant();
        var requestedOrder = context.Request.Query["order"].ToString();
        var sort = requestedSort.Length == 0 ? "name" : requestedSort;
        var descending = requestedOrder.Length == 0
            ? !sort.Equals("name", StringComparison.OrdinalIgnoreCase)
            : !requestedOrder.Equals("asc", StringComparison.OrdinalIgnoreCase);
        var from = QueryDate(context, "uploadedFrom");
        var to = QueryDate(context, "uploadedTo");
        var folderText = context.Request.Query["folderId"].ToString().Trim();
        Guid? folderId = null;
        if (folderText.Length > 0)
        {
            if (!Guid.TryParse(folderText, out var parsedFolderId))
            {
                throw new ApiException(
                    StatusCodes.Status400BadRequest,
                    "invalid_folder_id",
                    "查询参数 folderId 不是有效标识。");
            }

            folderId = parsedFolderId;
        }
        var includeDescendants = !context.Request.Query["includeDescendants"].ToString()
            .Equals("false", StringComparison.OrdinalIgnoreCase);
        var rootOnly = context.Request.Query["rootOnly"].ToString()
            .Equals("true", StringComparison.OrdinalIgnoreCase);

        var result = await store.ReadAsync(state =>
        {
            var current = ApiCommon.CurrentUser(state, identity);
            ApiCommon.RequirePermission(current, PermissionNames.BrowseAssets);
            if (personalRecycleBin)
            {
                ApiCommon.RequirePermission(current, PermissionNames.DeleteOwnAssets);
            }

            IEnumerable<AssetRecord> query = state.Assets.Where(asset =>
                trash
                    ? asset.State == AssetState.Recycled &&
                      (personalRecycleBin
                          ? asset.UploadedByUserId == current.Id
                          : current.IsAdmin || asset.UploadedByUserId == current.Id)
                    : asset.State == AssetState.Active);
            query = query.Where(asset => ApiCommon.CanAccessCategory(current, asset.Category));

            if (folderId is { } selectedFolderId)
            {
                var folderIds = includeDescendants
                    ? AssetFolderEndpoints.DescendantIds(state, selectedFolderId)
                    : new HashSet<Guid> { AssetFolderEndpoints.RequireFolder(state, selectedFolderId).Id };
                query = query.Where(asset => asset.FolderId is { } assignedFolderId && folderIds.Contains(assignedFolderId));
            }
            else if (rootOnly)
            {
                query = query.Where(asset => asset.FolderId is null);
            }

            if (search.Length > 0)
            {
                query = query.Where(asset =>
                    asset.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    asset.OriginalFileName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    (asset.Notes?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    asset.Tags.Any(item => item.Contains(search, StringComparison.OrdinalIgnoreCase)));
            }

            if (category.Length > 0)
            {
                query = query.Where(asset => asset.Category.Equals(category, StringComparison.OrdinalIgnoreCase));
            }

            if (tag.Length > 0)
            {
                query = query.Where(asset => asset.Tags.Contains(tag));
            }

            if (hasUploader)
            {
                query = query.Where(asset => asset.UploadedByUserId == uploaderId);
                if (!trash)
                {
                    query = query.Where(asset => asset.HasOriginal);
                }
            }

            if (from is not null)
            {
                query = query.Where(asset => asset.UploadedAt >= from.Value);
            }

            if (to is not null)
            {
                query = query.Where(asset => asset.UploadedAt <= to.Value);
            }

            query = (sort, descending) switch
            {
                ("name", false) => query.OrderBy(asset => asset.Name),
                ("name", true) => query.OrderByDescending(asset => asset.Name),
                ("size", false) => query.OrderBy(asset => asset.SizeBytes),
                ("size", true) => query.OrderByDescending(asset => asset.SizeBytes),
                (_, false) => query.OrderBy(asset => asset.UploadedAt),
                _ => query.OrderByDescending(asset => asset.UploadedAt)
            };

            var all = query.ToList();
            var items = all.Skip((page - 1) * pageSize).Take(pageSize).Select(asset =>
            {
                var uploader = state.Users.FirstOrDefault(user => user.Id == asset.UploadedByUserId);
                return ApiCommon.ToAsset(asset, uploader);
            }).ToList();
            return new PageResult<object>(items, page, pageSize, all.Count);
        }, context.RequestAborted);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetAssetAsync(Guid assetId, HttpContext context, IAppDataStore store)
    {
        var identity = AccessControl.RequirePermission(context, PermissionNames.BrowseAssets);
        var result = await store.ReadAsync(state =>
        {
            var current = ApiCommon.CurrentUser(state, identity);
            ApiCommon.RequirePermission(current, PermissionNames.BrowseAssets);
            var asset = state.Assets.FirstOrDefault(item => item.Id == assetId)
                ?? throw new ApiException(StatusCodes.Status404NotFound, "asset_not_found", "素材不存在。");
            if (!ApiCommon.CanAccessCategory(current, asset.Category))
            {
                throw new ApiException(StatusCodes.Status404NotFound, "asset_not_found", "素材不存在。");
            }
            if (asset.State == AssetState.Recycled && !current.IsAdmin && asset.UploadedByUserId != current.Id)
            {
                throw new ApiException(StatusCodes.Status404NotFound, "asset_not_found", "素材不存在。");
            }

            return ApiCommon.ToAsset(asset, state.Users.FirstOrDefault(user => user.Id == asset.UploadedByUserId));
        }, context.RequestAborted);
        return Results.Ok(result);
    }

    private static async Task<IResult> CreateAssetAsync(
        CreateAssetRequest request,
        HttpContext context,
        IAppDataStore store,
        IConfiguration configuration,
        ILibraryChangeNotifier changes)
    {
        var identity = AccessControl.RequirePermission(context, PermissionNames.UploadAssets);
        var name = ApiCommon.TrimRequired(request.Name, 200, "素材名称");
        var category = request.Category?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!AssetCategories.All.Contains(category))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_category", "素材大类无效。");
        }

        var originalFileName = Path.GetFileName(request.OriginalFileName?.Trim());
        if (string.IsNullOrWhiteSpace(originalFileName) || originalFileName.Length > 255)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_file_name", "原文件名无效。");
        }

        var extension = Path.GetExtension(originalFileName).ToLowerInvariant();
        ValidateCategoryExtension(category, extension);
        var durationSeconds = ApiCommon.ValidateAssetDuration(request.DurationSeconds);
        var library = configuration.GetSection("Library").Get<LibraryOptions>() ?? new();
        ValidateAssetSize(category, request.SizeBytes, library);
        var contentHash = (request.ContentHash ?? string.Empty).Trim().ToUpperInvariant();
        if (contentHash.Length != 64 || contentHash.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_content_hash", "ContentHash 必须为 64 位 SHA-256 十六进制值。");
        }

        var notes = ApiCommon.TrimOptional(request.Notes, 2_000, "备注");
        var tags = ApiCommon.ValidateTags(request.Tags);
        var now = DateTimeOffset.UtcNow;
        var asset = await store.UpdateAsync(state =>
        {
            var current = ApiCommon.CurrentUser(state, identity);
            ApiCommon.RequirePermission(current, PermissionNames.UploadAssets);
            ApiCommon.RequireCategory(current, category);
            _ = AssetFolderEndpoints.RequireExistingFolder(state, request.FolderId);
            var duplicate = state.Assets.FirstOrDefault(item => item.ContentHash == contentHash);
            if (duplicate is not null)
            {
                throw new ApiException(
                    StatusCodes.Status409Conflict,
                    "duplicate_content",
                    $"相同内容已存在，素材 ID：{duplicate.Id}");
            }

            var usedBytes = OriginalBytes(state);
            if (request.SizeBytes > library.OriginalQuotaBytes - usedBytes)
            {
                throw new ApiException(StatusCodes.Status409Conflict, "quota_exceeded", "原文件配额不足，回收站和旧版本仍计入配额。");
            }

            var registeredTags = RegisterCatalogTags(state, tags, current.Id);
            var id = Guid.NewGuid();
            var created = new AssetRecord
            {
                Id = id,
                CurrentVersionId = id,
                Name = name,
                Category = category,
                OriginalFileName = originalFileName,
                Extension = extension,
                SizeBytes = request.SizeBytes,
                DurationSeconds = durationSeconds,
                ContentHash = contentHash,
                ObjectKey = $"originals/{now:yyyy/MM}/{id:N}{extension}",
                Notes = notes,
                Tags = registeredTags,
                FolderId = request.FolderId,
                UploadedByUserId = current.Id,
                UploadedByUsername = current.Username,
                UploadedByDisplayName = current.DisplayName,
                UploadedAt = now,
                UpdatedAt = now
            };
            AssetDerivativeRules.InitializePendingOriginal(created, now);
            state.Assets.Add(created);
            ApiCommon.Audit(
                state,
                current.Id,
                "asset.created",
                "asset",
                created.Id.ToString(),
                $"folder={created.FolderId?.ToString("D") ?? "root"}");
            return new { Asset = created, Uploader = current };
        }, context.RequestAborted);
        await changes.NotifyAsync(
            context.RequestAborted,
            asset.Asset.Tags.Count > 0
                ? [LibraryChangeTarget.Assets(asset.Asset.Id), LibraryChangeTarget.Tags(null)]
                : [LibraryChangeTarget.Assets(asset.Asset.Id)]);
        return Results.Created($"/api/assets/{asset.Asset.Id}", ApiCommon.ToAsset(asset.Asset, asset.Uploader));
    }

    private static async Task<IResult> UpdateAssetAsync(
        Guid assetId,
        UpdateAssetRequest request,
        HttpContext context,
        IAppDataStore store,
        ILibraryChangeNotifier changes)
    {
        var identity = AccessControl.RequireUser(context);
        var name = ApiCommon.TrimRequired(request.Name, 200, "素材名称");
        var notes = ApiCommon.TrimOptional(request.Notes, 2_000, "备注");
        var result = await store.UpdateAsync(state =>
        {
            var current = ApiCommon.CurrentUser(state, identity);
            var asset = OwnedAsset(state, current, assetId);
            ApiCommon.RequireCategory(current, asset.Category);
            if (!current.IsAdmin)
            {
                ApiCommon.RequirePermission(current, PermissionNames.EditOwnAssets);
            }
            EnsureActive(asset);
            asset.Name = name;
            asset.Notes = notes;
            asset.UpdatedAt = DateTimeOffset.UtcNow;
            ApiCommon.Audit(state, current.Id, "asset.metadata.updated", "asset", asset.Id.ToString());
            return new { Asset = asset, Uploader = state.Users.FirstOrDefault(user => user.Id == asset.UploadedByUserId) };
        }, context.RequestAborted);
        await changes.NotifyAsync(context.RequestAborted, LibraryChangeTarget.Assets(result.Asset.Id));
        return Results.Ok(ApiCommon.ToAsset(result.Asset, result.Uploader));
    }

    private static async Task<IResult> UpdateAssetCategoryAsync(
        Guid assetId,
        UpdateAssetCategoryRequest request,
        HttpContext context,
        IAppDataStore store,
        ILibraryChangeNotifier changes)
    {
        var identity = AccessControl.RequireUser(context);
        var category = request.Category?.Trim().ToLowerInvariant() ?? string.Empty;
        if (category is not (AssetCategories.Bgm or AssetCategories.SoundEffect))
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "invalid_audio_category",
                "音频分类只能选择 BGM 或音效。");
        }

        var result = await store.UpdateAsync(state =>
        {
            var current = ApiCommon.CurrentUser(state, identity);
            var asset = OwnedAsset(state, current, assetId);
            if (asset.Category is not (AssetCategories.Bgm or AssetCategories.SoundEffect))
            {
                throw new ApiException(
                    StatusCodes.Status409Conflict,
                    "asset_not_audio",
                    "只有音频素材可以修改为 BGM 或音效。");
            }

            if (!current.IsAdmin)
            {
                ApiCommon.RequirePermission(current, PermissionNames.EditOwnAssets);
            }

            ApiCommon.RequireCategory(current, asset.Category);
            ApiCommon.RequireCategory(current, category);
            EnsureActive(asset);
            var previousCategory = asset.Category;
            asset.Category = category;
            asset.UpdatedAt = DateTimeOffset.UtcNow;
            ApiCommon.Audit(
                state,
                current.Id,
                "asset.category.updated",
                "asset",
                asset.Id.ToString(),
                $"from={previousCategory}; to={category}");
            return new
            {
                Asset = asset,
                Uploader = state.Users.FirstOrDefault(user => user.Id == asset.UploadedByUserId)
            };
        }, context.RequestAborted);
        await changes.NotifyAsync(context.RequestAborted, LibraryChangeTarget.Assets(result.Asset.Id));
        return Results.Ok(ApiCommon.ToAsset(result.Asset, result.Uploader));
    }

    private static async Task<IResult> UpdateTagsAsync(
        Guid assetId,
        UpdateTagsRequest request,
        HttpContext context,
        IAppDataStore store,
        ILibraryChangeNotifier changes)
    {
        var identity = AccessControl.RequirePermission(context, PermissionNames.ModifyTags);
        var tags = ApiCommon.ValidateTags(request.Tags);
        var result = await store.UpdateAsync(state =>
        {
            var current = ApiCommon.CurrentUser(state, identity);
            ApiCommon.RequirePermission(current, PermissionNames.ModifyTags);
            var asset = state.Assets.FirstOrDefault(item => item.Id == assetId)
                ?? throw new ApiException(StatusCodes.Status404NotFound, "asset_not_found", "素材不存在。");
            ApiCommon.RequireCategory(current, asset.Category);
            EnsureActive(asset);
            asset.Tags = request.CreateMissing is false
                ? RequireCatalogTags(state, tags)
                : RegisterCatalogTags(state, tags, current.Id);
            asset.UpdatedAt = DateTimeOffset.UtcNow;
            ApiCommon.Audit(state, current.Id, "asset.tags.updated", "asset", asset.Id.ToString());
            return new { Asset = asset, Uploader = state.Users.FirstOrDefault(user => user.Id == asset.UploadedByUserId) };
        }, context.RequestAborted);
        await changes.NotifyAsync(
            context.RequestAborted,
            LibraryChangeTarget.Assets(result.Asset.Id),
            LibraryChangeTarget.Tags(null));
        return Results.Ok(ApiCommon.ToAsset(result.Asset, result.Uploader));
    }

    private static async Task<IResult> RecycleAssetAsync(
        Guid assetId,
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
            var asset = OwnedAsset(state, current, assetId);
            ApiCommon.RequireCategory(current, asset.Category);
            if (!current.IsAdmin)
            {
                ApiCommon.RequirePermission(current, PermissionNames.DeleteOwnAssets);
            }

            EnsureActive(asset);
            asset.State = AssetState.Recycled;
            asset.RecycledAt = DateTimeOffset.UtcNow;
            asset.PurgeAfter = asset.RecycledAt.Value.AddDays(retentionDays);
            asset.UpdatedAt = asset.RecycledAt.Value;
            ApiCommon.Audit(state, current.Id, "asset.recycled", "asset", asset.Id.ToString());
            return new { Asset = asset, Uploader = state.Users.FirstOrDefault(user => user.Id == asset.UploadedByUserId) };
        }, context.RequestAborted);
        await changes.NotifyAsync(
            context.RequestAborted,
            result.Asset.HasOriginal
                ? [LibraryChangeTarget.Assets(result.Asset.Id), LibraryChangeTarget.Profiles(result.Asset.UploadedByUserId)]
                : [LibraryChangeTarget.Assets(result.Asset.Id)]);
        return Results.Ok(ApiCommon.ToAsset(result.Asset, result.Uploader));
    }

    private static async Task<IResult> RestoreAssetAsync(
        Guid assetId,
        HttpContext context,
        IAppDataStore store,
        ILibraryChangeNotifier changes) =>
        await RestoreAssetCoreAsync(assetId, context, store, changes, personalRecycleBin: false);

    private static async Task<IResult> RestoreMyRecycleBinAssetAsync(
        Guid assetId,
        HttpContext context,
        IAppDataStore store,
        ILibraryChangeNotifier changes) =>
        await RestoreAssetCoreAsync(assetId, context, store, changes, personalRecycleBin: true);

    private static async Task<IResult> RestoreAssetCoreAsync(
        Guid assetId,
        HttpContext context,
        IAppDataStore store,
        ILibraryChangeNotifier changes,
        bool personalRecycleBin)
    {
        var identity = AccessControl.RequireUser(context);
        var result = await store.UpdateAsync(state =>
        {
            var current = ApiCommon.CurrentUser(state, identity);
            var asset = personalRecycleBin
                ? PersonalAsset(state, current, assetId)
                : OwnedAsset(state, current, assetId);
            ApiCommon.RequireCategory(current, asset.Category);
            if (!current.IsAdmin)
            {
                ApiCommon.RequirePermission(current, PermissionNames.DeleteOwnAssets);
            }
            if (asset.State != AssetState.Recycled)
            {
                throw new ApiException(StatusCodes.Status409Conflict, "asset_not_recycled", "素材不在回收站中。");
            }

            asset.State = AssetState.Active;
            asset.RecycledAt = null;
            asset.PurgeAfter = null;
            asset.UpdatedAt = DateTimeOffset.UtcNow;
            ApiCommon.Audit(state, current.Id, "asset.restored", "asset", asset.Id.ToString());
            return new { Asset = asset, Uploader = state.Users.FirstOrDefault(user => user.Id == asset.UploadedByUserId) };
        }, context.RequestAborted);
        await changes.NotifyAsync(
            context.RequestAborted,
            result.Asset.HasOriginal
                ? [LibraryChangeTarget.Assets(result.Asset.Id), LibraryChangeTarget.Profiles(result.Asset.UploadedByUserId)]
                : [LibraryChangeTarget.Assets(result.Asset.Id)]);
        return Results.Ok(ApiCommon.ToAsset(result.Asset, result.Uploader));
    }

    private static async Task<IResult> PermanentlyDeleteAssetAsync(
        Guid assetId,
        HttpContext context,
        IAppDataStore store,
        ObjectDeletionOutbox objectDeletions,
        ILibraryChangeNotifier changes) =>
        await PermanentlyDeleteAssetCoreAsync(
            assetId,
            context,
            store,
            objectDeletions,
            changes,
            personalRecycleBin: false);

    private static async Task<IResult> PermanentlyDeleteMyRecycleBinAssetAsync(
        Guid assetId,
        HttpContext context,
        IAppDataStore store,
        ObjectDeletionOutbox objectDeletions,
        ILibraryChangeNotifier changes) =>
        await PermanentlyDeleteAssetCoreAsync(
            assetId,
            context,
            store,
            objectDeletions,
            changes,
            personalRecycleBin: true);

    private static async Task<IResult> PermanentlyDeleteAssetCoreAsync(
        Guid assetId,
        HttpContext context,
        IAppDataStore store,
        ObjectDeletionOutbox objectDeletions,
        ILibraryChangeNotifier changes,
        bool personalRecycleBin)
    {
        var identity = AccessControl.RequireUser(context);
        var deletion = await store.UpdateAsync(state =>
        {
            var current = ApiCommon.CurrentUser(state, identity);
            var asset = personalRecycleBin
                ? PersonalAsset(state, current, assetId)
                : OwnedAsset(state, current, assetId);
            ApiCommon.RequireCategory(current, asset.Category);
            if (!current.IsAdmin)
            {
                ApiCommon.RequirePermission(current, PermissionNames.DeleteOwnAssets);
            }
            if (asset.State != AssetState.Recycled)
            {
                throw new ApiException(StatusCodes.Status409Conflict, "recycle_first", "永久删除前必须先将素材移入回收站。");
            }

            var keys = asset.PreviousVersions
                .Where(version => version.HasOriginal)
                .Select(version => version.ObjectKey)
                .ToList();
            if (asset.HasOriginal)
            {
                keys.Add(asset.ObjectKey);
            }

            keys.AddRange(AssetDerivativeRules.StoredObjectKeys(asset));

            var markerSetIds = state.MarkerSets
                .Where(markerSet => markerSet.AssetId == asset.Id)
                .Select(markerSet => markerSet.Id)
                .ToArray();
            var hadTags = asset.Tags.Count > 0;
            var queuedKeys = objectDeletions.Enqueue(state, keys, DateTimeOffset.UtcNow);
            state.Assets.Remove(asset);
            state.MarkerSets.RemoveAll(markerSet => markerSet.AssetId == asset.Id);
            ApiCommon.Audit(state, current.Id, "asset.permanently_deleted", "asset", asset.Id.ToString(), asset.ObjectKey);
            return new PermanentAssetDeletion(
                [asset.Id],
                queuedKeys,
                markerSetIds,
                hadTags);
        }, context.RequestAborted);

        await CompletePermanentDeletionAsync(
            deletion,
            context.RequestAborted,
            objectDeletions,
            changes);
        return Results.NoContent();
    }

    private static async Task<IResult> ClearMyRecycleBinAsync(
        HttpContext context,
        IAppDataStore store,
        ObjectDeletionOutbox objectDeletions,
        ILibraryChangeNotifier changes)
    {
        var identity = AccessControl.RequirePermission(context, PermissionNames.BrowseAssets);
        _ = AccessControl.RequirePermission(context, PermissionNames.DeleteOwnAssets);
        var deletion = await store.UpdateAsync(state =>
        {
            var current = ApiCommon.CurrentUser(state, identity);
            ApiCommon.RequirePermission(current, PermissionNames.BrowseAssets);
            ApiCommon.RequirePermission(current, PermissionNames.DeleteOwnAssets);
            var assets = state.Assets
                .Where(asset =>
                    asset.UploadedByUserId == current.Id &&
                    asset.State == AssetState.Recycled &&
                    ApiCommon.CanAccessCategory(current, asset.Category))
                .ToArray();
            var assetIds = assets.Select(asset => asset.Id).ToArray();
            var assetIdSet = assetIds.ToHashSet();
            var objectKeys = new List<string>();
            foreach (var asset in assets)
            {
                objectKeys.AddRange(asset.PreviousVersions
                    .Where(version => version.HasOriginal)
                    .Select(version => version.ObjectKey));
                if (asset.HasOriginal)
                {
                    objectKeys.Add(asset.ObjectKey);
                }

                objectKeys.AddRange(AssetDerivativeRules.StoredObjectKeys(asset));

                ApiCommon.Audit(
                    state,
                    current.Id,
                    "asset.permanently_deleted",
                    "asset",
                    asset.Id.ToString(),
                    asset.ObjectKey);
            }

            var markerSetIds = state.MarkerSets
                .Where(markerSet => assetIdSet.Contains(markerSet.AssetId))
                .Select(markerSet => markerSet.Id)
                .ToArray();
            var hadTags = assets.Any(asset => asset.Tags.Count > 0);
            var queuedKeys = objectDeletions.Enqueue(state, objectKeys, DateTimeOffset.UtcNow);
            state.Assets.RemoveAll(asset => assetIdSet.Contains(asset.Id));
            state.MarkerSets.RemoveAll(markerSet => assetIdSet.Contains(markerSet.AssetId));
            return new PermanentAssetDeletion(
                assetIds,
                queuedKeys,
                markerSetIds,
                hadTags);
        }, context.RequestAborted);

        await CompletePermanentDeletionAsync(
            deletion,
            context.RequestAborted,
            objectDeletions,
            changes);
        return Results.Ok(new RecycleBinClearResult(deletion.AssetIds.Length));
    }

    private static async Task CompletePermanentDeletionAsync(
        PermanentAssetDeletion deletion,
        CancellationToken cancellationToken,
        ObjectDeletionOutbox objectDeletions,
        ILibraryChangeNotifier changes)
    {
        var notifications = deletion.AssetIds
            .Select(assetId => LibraryChangeTarget.Assets(assetId))
            .ToList();
        notifications.AddRange(deletion.MarkerSetIds.Select(markerSetId => LibraryChangeTarget.Markers(markerSetId)));
        if (deletion.HadTags)
        {
            notifications.Add(LibraryChangeTarget.Tags(null));
        }

        if (notifications.Count > 0)
        {
            await changes.NotifyAsync(cancellationToken, notifications.ToArray());
        }

        await objectDeletions.TryProcessAsync(deletion.ObjectKeys, CancellationToken.None);
    }

    private sealed record PermanentAssetDeletion(
        Guid[] AssetIds,
        string[] ObjectKeys,
        Guid[] MarkerSetIds,
        bool HadTags);

    private static async Task<IResult> GetLibraryStatusAsync(HttpContext context, IAppDataStore store, IConfiguration configuration)
    {
        var identity = AccessControl.RequirePermission(context, PermissionNames.BrowseAssets);
        var quota = configuration.GetValue("Library:OriginalQuotaBytes", 107_374_182_400L);
        var status = await store.ReadAsync(state =>
        {
            var current = ApiCommon.CurrentUser(state, identity);
            ApiCommon.RequirePermission(current, PermissionNames.BrowseAssets);
            var visibleAssets = state.Assets.Where(asset => ApiCommon.CanAccessCategory(current, asset.Category)).ToList();
            var originalBytes = visibleAssets.Sum(asset =>
                (asset.HasOriginal ? asset.SizeBytes : 0) +
                asset.PreviousVersions.Where(version => version.HasOriginal).Sum(version => version.SizeBytes));
            return new
            {
                originalBytes,
                quotaBytes = quota,
                usageRatio = quota <= 0 ? 0 : (double)originalBytes / quota,
                capacityLevel = CapacityStatus.Level(originalBytes, quota, configuration),
                activeAssets = visibleAssets.Count(asset => asset.State == AssetState.Active),
                recycledAssets = current.IsAdmin
                    ? visibleAssets.Count(asset => asset.State == AssetState.Recycled)
                    : visibleAssets.Count(asset => asset.State == AssetState.Recycled && asset.UploadedByUserId == current.Id)
            };
        }, context.RequestAborted);
        return Results.Ok(status);
    }

    private static AssetRecord OwnedAsset(AppState state, UserRecord current, Guid assetId)
    {
        var asset = state.Assets.FirstOrDefault(item => item.Id == assetId)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "asset_not_found", "素材不存在。");
        if (!current.IsAdmin && asset.UploadedByUserId != current.Id)
        {
            throw new ApiException(StatusCodes.Status403Forbidden, "asset_owner_required", "只有上传者或管理员可以修改此素材。");
        }

        return asset;
    }

    private static AssetRecord PersonalAsset(AppState state, UserRecord current, Guid assetId) =>
        state.Assets.FirstOrDefault(item => item.Id == assetId && item.UploadedByUserId == current.Id)
        ?? throw new ApiException(StatusCodes.Status404NotFound, "asset_not_found", "素材不存在。");

    private static HashSet<string> RegisterCatalogTags(AppState state, IEnumerable<string> requestedTags, Guid actorUserId)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var requestedName in requestedTags)
        {
            var tag = state.Tags.FirstOrDefault(item => item.Name.Equals(requestedName, StringComparison.OrdinalIgnoreCase));
            if (tag is null)
            {
                var now = DateTimeOffset.UtcNow;
                tag = new TagRecord
                {
                    Id = Guid.NewGuid(),
                    Name = requestedName,
                    CreatedByUserId = actorUserId,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                state.Tags.Add(tag);
                ApiCommon.Audit(state, actorUserId, "tag.created", "tag", tag.Id.ToString(), tag.Name);
            }

            result.Add(tag.Name);
        }

        return result;
    }

    private static HashSet<string> RequireCatalogTags(AppState state, IEnumerable<string> requestedTags)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var requestedName in requestedTags)
        {
            var tag = state.Tags.FirstOrDefault(item =>
                item.Name.Equals(requestedName, StringComparison.OrdinalIgnoreCase));
            if (tag is null)
            {
                throw new ApiException(
                    StatusCodes.Status409Conflict,
                    "tag_catalog_changed",
                    "标签库已发生变化，请刷新后重试。");
            }

            result.Add(tag.Name);
        }

        return result;
    }

    private static object ToTag(TagRecord tag, int usageCount) => new
    {
        id = tag.Id,
        tag.Name,
        tag.CreatedByUserId,
        tag.CreatedAt,
        tag.UpdatedAt,
        usageCount
    };

    private static void EnsureActive(AssetRecord asset)
    {
        if (asset.State != AssetState.Active)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "asset_recycled", "回收站中的素材不能执行此操作。");
        }
    }

    private static void ValidateCategoryExtension(string category, string extension)
    {
        var valid = category switch
        {
            AssetCategories.Bgm or AssetCategories.SoundEffect => AssetCategories.AudioExtensions.Contains(extension),
            AssetCategories.Image => AssetCategories.ImageExtensions.Contains(extension),
            AssetCategories.Video => AssetCategories.VideoExtensions.Contains(extension),
            _ => false
        };
        if (!valid)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "category_extension_mismatch", "文件扩展名与素材大类不匹配。");
        }
    }

    private static void ValidateAssetSize(string category, long bytes, LibraryOptions options)
    {
        var maximum = category switch
        {
            AssetCategories.Bgm or AssetCategories.SoundEffect => options.AudioMaxBytes,
            AssetCategories.Image => options.ImageMaxBytes,
            AssetCategories.Video => options.VideoMaxBytes,
            _ => 0
        };
        if (bytes <= 0 || bytes > maximum)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_asset_size", $"素材大小必须大于 0 且不超过 {maximum} 字节。");
        }
    }

    internal static long OriginalBytes(AppState state) => state.Assets.Sum(asset =>
        (asset.HasOriginal ? asset.SizeBytes : 0) +
        asset.PreviousVersions.Where(version => version.HasOriginal).Sum(version => version.SizeBytes));

    private static int QueryInt(HttpContext context, string key, int fallback, int minimum, int maximum) =>
        int.TryParse(context.Request.Query[key], out var value) ? Math.Clamp(value, minimum, maximum) : fallback;

    private static DateTimeOffset? QueryDate(HttpContext context, string key)
    {
        var text = context.Request.Query[key].ToString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value))
        {
            return value.ToUniversalTime();
        }

        throw new ApiException(StatusCodes.Status400BadRequest, "invalid_date", $"查询参数 {key} 不是有效日期。");
    }
}
