using InternalAssetLibrary.Server.Data;
using InternalAssetLibrary.Server.Realtime;
using InternalAssetLibrary.Server.Security;
using InternalAssetLibrary.Server.Services;
using Microsoft.AspNetCore.Mvc;
using System.Security.Cryptography;
using System.Text;

namespace InternalAssetLibrary.Server.Api;

internal static class AuthProfileEndpoints
{
    public static void MapAuthProfileEndpoints(this WebApplication app)
    {
        app.MapPost("/api/auth/login", LoginAsync).RequireRateLimiting("login");
        app.MapPost("/api/auth/logout", LogoutAsync);
        app.MapPost("/api/auth/change-password", ChangePasswordAsync);

        app.MapGet("/api/me", GetMeAsync);
        app.MapPut("/api/me/profile", UpdateProfileAsync);
        app.MapPut("/api/me/email", BindEmailAsync);
        app.MapDelete("/api/me/email", UnbindEmailAsync);
        app.MapPost("/api/me/avatar", UploadAvatarAsync);
        app.MapDelete("/api/me/avatar", DeleteAvatarAsync);

        app.MapGet("/api/users", ListUsersAsync);
        app.MapGet("/api/users/{userId:guid}", GetUserAsync);
        app.MapGet("/api/users/{userId:guid}/avatar", GetAvatarAsync);
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        IAppDataStore store,
        PasswordService passwords,
        IConfiguration configuration,
        HttpContext context)
    {
        var identifier = request.Identifier?.Trim() ?? string.Empty;
        if (identifier.Length is < 1 or > 254 || request.Password is null || request.Password.Length > 128)
        {
            throw InvalidCredentials();
        }

        var normalized = ApiCommon.Normalize(identifier);
        var identifierHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        var remoteAddress = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var now = DateTimeOffset.UtcNow;
        var blockMinutes = Math.Clamp(configuration.GetValue("Security:LoginBlockMinutes", 15), 1, 1440);
        var failureWindowMinutes = Math.Clamp(configuration.GetValue("Security:LoginFailureWindowMinutes", 15), 1, 1440);
        var failureLimit = Math.Clamp(configuration.GetValue("Security:LoginFailureLimit", 5), 2, 100);
        var blockedUntil = await store.ReadAsync(state => state.LoginFailures
            .FirstOrDefault(item =>
                item.IdentifierHash == identifierHash &&
                item.RemoteAddress == remoteAddress)?
            .BlockedUntil, context.RequestAborted);
        if (blockedUntil > now)
        {
            throw new ApiException(
                StatusCodes.Status429TooManyRequests,
                "login_temporarily_blocked",
                $"登录失败次数过多，请在 {blockedUntil:HH:mm:ss} 后重试。 ");
        }

        var candidate = await store.ReadAsync(state => state.Users.FirstOrDefault(user =>
            user.NormalizedUsername == normalized || user.NormalizedEmail == normalized), context.RequestAborted);

        if (!passwords.VerifyKnownOrDummy(request.Password, candidate?.PasswordHash, out var needsRehash) ||
            candidate is not { IsEnabled: true })
        {
            await store.UpdateAsync(state =>
            {
                var failure = state.LoginFailures.FirstOrDefault(item =>
                    item.IdentifierHash == identifierHash && item.RemoteAddress == remoteAddress);
                if (failure is null)
                {
                    failure = new LoginFailureRecord
                    {
                        IdentifierHash = identifierHash,
                        RemoteAddress = remoteAddress,
                        FailureCount = 0,
                        FirstFailureAt = now,
                        LastFailureAt = now
                    };
                    state.LoginFailures.Add(failure);
                }
                else if (failure.FirstFailureAt < now.AddMinutes(-failureWindowMinutes))
                {
                    failure.FailureCount = 0;
                    failure.FirstFailureAt = now;
                    failure.BlockedUntil = null;
                }

                failure.FailureCount++;
                failure.LastFailureAt = now;
                if (failure.FailureCount >= failureLimit)
                {
                    failure.BlockedUntil = now.AddMinutes(blockMinutes);
                }

                ApiCommon.Audit(
                    state,
                    null,
                    "auth.login.failed",
                    "authentication",
                    identifierHash[..16],
                    $"remote={remoteAddress}; count={failure.FailureCount}; blockedUntil={failure.BlockedUntil:O}");
                return true;
            }, context.RequestAborted);
            throw InvalidCredentials();
        }

        var token = TokenService.CreateToken();
        var tokenHash = TokenService.HashToken(token);
        var lifetimeDays = Math.Clamp(configuration.GetValue("Security:SessionLifetimeDays", 7), 1, 30);
        var login = await store.UpdateAsync(state =>
        {
            var current = state.Users.FirstOrDefault(item => item.Id == candidate.Id);
            if (current is not { IsEnabled: true } || current.PasswordHash != candidate.PasswordHash)
            {
                throw InvalidCredentials();
            }

            if (needsRehash)
            {
                current.PasswordHash = passwords.Hash(request.Password);
                current.UpdatedAt = now;
            }

            var recommendPasswordChange = FirstLoginPasswordRecommendation.Consume(current, now);

            state.Sessions.RemoveAll(session => session.ExpiresAt <= now);
            state.Sessions.Add(new SessionRecord
            {
                Id = Guid.NewGuid(),
                UserId = current.Id,
                TokenHash = tokenHash,
                CreatedAt = now,
                LastSeenAt = now,
                ExpiresAt = now.AddDays(lifetimeDays)
            });
            state.LoginFailures.RemoveAll(item =>
                item.IdentifierHash == identifierHash && item.RemoteAddress == remoteAddress);
            ApiCommon.Audit(state, current.Id, "auth.login", "user", current.Id.ToString());
            return new { User = current, RecommendPasswordChange = recommendPasswordChange };
        }, context.RequestAborted);

        return Results.Ok(new
        {
            token,
            user = ApiCommon.ToMe(login.User, login.RecommendPasswordChange)
        });
    }

    private static async Task<IResult> LogoutAsync(HttpContext context, IAppDataStore store)
    {
        var identity = AccessControl.RequireUser(context);
        await store.UpdateAsync(state =>
        {
            state.Sessions.RemoveAll(session => session.Id == identity.SessionId && session.UserId == identity.UserId);
            ApiCommon.Audit(state, identity.UserId, "auth.logout", "user", identity.UserId.ToString());
            return true;
        }, context.RequestAborted);
        return Results.NoContent();
    }

    private static async Task<IResult> ChangePasswordAsync(
        ChangePasswordRequest request,
        HttpContext context,
        IAppDataStore store,
        PasswordService passwords,
        ILibraryChangeNotifier changes)
    {
        var identity = AccessControl.RequireUser(context);
        var newPassword = request.NewPassword ?? string.Empty;
        if (!PasswordService.IsAcceptable(newPassword, out var error))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "weak_password", error!);
        }

        var candidate = await store.ReadAsync(state => ApiCommon.CurrentUser(state, identity), context.RequestAborted);
        if (!passwords.Verify(request.CurrentPassword ?? string.Empty, candidate.PasswordHash))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "incorrect_password", "当前密码不正确。");
        }

        var newHash = passwords.Hash(newPassword);
        var user = await store.UpdateAsync(state =>
        {
            var current = ApiCommon.CurrentUser(state, identity);
            if (current.PasswordHash != candidate.PasswordHash)
            {
                throw new ApiException(StatusCodes.Status409Conflict, "password_changed", "密码已在其他位置变更，请重新登录。");
            }

            current.PasswordHash = newHash;
            current.MustChangePassword = false;
            current.UpdatedAt = DateTimeOffset.UtcNow;
            state.Sessions.RemoveAll(session => session.UserId == current.Id && session.Id != identity.SessionId);
            ApiCommon.Audit(state, current.Id, "auth.password.changed", "user", current.Id.ToString());
            return current;
        }, context.RequestAborted);

        await changes.NotifyAsync(context.RequestAborted, LibraryChangeTarget.Users(user.Id));
        return Results.Ok(ApiCommon.ToMe(user));
    }

    private static async Task<IResult> GetMeAsync(HttpContext context, IAppDataStore store)
    {
        var identity = AccessControl.RequireUser(context);
        var user = await store.ReadAsync(state => ApiCommon.CurrentUser(state, identity), context.RequestAborted);
        return Results.Ok(ApiCommon.ToMe(user));
    }

    private static async Task<IResult> UpdateProfileAsync(
        ProfileRequest request,
        HttpContext context,
        IAppDataStore store,
        ILibraryChangeNotifier changes)
    {
        var identity = AccessControl.RequireUser(context);
        var displayName = request.DisplayName?.Trim() ?? string.Empty;
        if (displayName.Length > 40)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_profile", "显示名称不能超过 40 个字符。");
        }

        var bio = ApiCommon.TrimOptional(request.Bio, 500, "个人简介");
        var contact = ApiCommon.TrimOptional(request.Contact, 200, "联系方式");
        var gender = ApiCommon.ValidateGender(request.Gender, request.CustomGender, out var customGender);
        var birthdayVisibility = ApiCommon.ParseVisibility(request.BirthdayVisibility, "生日");
        var genderVisibility = ApiCommon.ParseVisibility(request.GenderVisibility, "性别");
        var contactVisibility = ApiCommon.ParseVisibility(request.ContactVisibility, "联系方式");

        var user = await store.UpdateAsync(state =>
        {
            var current = ApiCommon.CurrentUser(state, identity);
            current.DisplayName = displayName;
            current.Bio = bio;
            current.Birthday = request.Birthday;
            current.Gender = gender;
            current.CustomGender = customGender;
            current.Contact = contact;
            current.BirthdayVisibility = birthdayVisibility;
            current.GenderVisibility = genderVisibility;
            current.ContactVisibility = contactVisibility;
            current.UpdatedAt = DateTimeOffset.UtcNow;
            ApiCommon.Audit(state, current.Id, "profile.updated", "user", current.Id.ToString());
            return current;
        }, context.RequestAborted);

        await changes.NotifyAsync(
            context.RequestAborted,
            LibraryChangeTarget.Profiles(user.Id),
            LibraryChangeTarget.Users(user.Id));
        return Results.Ok(ApiCommon.ToMe(user));
    }

    private static async Task<IResult> BindEmailAsync(
        EmailRequest request,
        HttpContext context,
        IAppDataStore store,
        PasswordService passwords,
        ILibraryChangeNotifier changes)
    {
        var identity = AccessControl.RequireUser(context);
        var email = ApiCommon.ValidateEmail(request.Email);
        var normalizedEmail = ApiCommon.Normalize(email);
        var candidate = await store.ReadAsync(state => ApiCommon.CurrentUser(state, identity), context.RequestAborted);
        if (!passwords.Verify(request.CurrentPassword ?? string.Empty, candidate.PasswordHash))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "incorrect_password", "当前密码不正确。");
        }

        var user = await store.UpdateAsync(state =>
        {
            var current = ApiCommon.CurrentUser(state, identity);
            if (current.PasswordHash != candidate.PasswordHash)
            {
                throw new ApiException(StatusCodes.Status409Conflict, "password_changed", "密码已变更，请重试。");
            }

            if (state.Users.Any(other => other.Id != current.Id && other.NormalizedEmail == normalizedEmail))
            {
                throw new ApiException(StatusCodes.Status409Conflict, "email_in_use", "该邮箱已绑定其他账号。");
            }

            current.Email = email;
            current.NormalizedEmail = normalizedEmail;
            current.UpdatedAt = DateTimeOffset.UtcNow;
            ApiCommon.Audit(state, current.Id, "profile.email.bound", "user", current.Id.ToString());
            return current;
        }, context.RequestAborted);

        await changes.NotifyAsync(context.RequestAborted, LibraryChangeTarget.Users(user.Id));
        return Results.Ok(ApiCommon.ToMe(user));
    }

    private static async Task<IResult> UnbindEmailAsync(
        [FromBody] PasswordConfirmationRequest request,
        HttpContext context,
        IAppDataStore store,
        PasswordService passwords,
        ILibraryChangeNotifier changes)
    {
        var identity = AccessControl.RequireUser(context);
        var candidate = await store.ReadAsync(state => ApiCommon.CurrentUser(state, identity), context.RequestAborted);
        if (!passwords.Verify(request.CurrentPassword ?? string.Empty, candidate.PasswordHash))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "incorrect_password", "当前密码不正确。");
        }

        var user = await store.UpdateAsync(state =>
        {
            var current = ApiCommon.CurrentUser(state, identity);
            if (current.PasswordHash != candidate.PasswordHash)
            {
                throw new ApiException(StatusCodes.Status409Conflict, "password_changed", "密码已变更，请重试。");
            }

            current.Email = null;
            current.NormalizedEmail = null;
            current.UpdatedAt = DateTimeOffset.UtcNow;
            ApiCommon.Audit(state, current.Id, "profile.email.unbound", "user", current.Id.ToString());
            return current;
        }, context.RequestAborted);
        await changes.NotifyAsync(context.RequestAborted, LibraryChangeTarget.Users(user.Id));
        return Results.Ok(ApiCommon.ToMe(user));
    }

    private static async Task<IResult> UploadAvatarAsync(
        HttpContext context,
        IAppDataStore store,
        AvatarFileStore files,
        ILibraryChangeNotifier changes)
    {
        var identity = AccessControl.RequireUser(context);
        StoredAvatar? stored = null;
        try
        {
            stored = await files.SaveAsync(
                identity.UserId,
                context.Request.Body,
                context.Request.ContentLength,
                context.Request.ContentType,
                context.RequestAborted);
            var result = await store.UpdateAsync(state =>
            {
                var current = ApiCommon.CurrentUser(state, identity);
                var oldFile = current.AvatarFileName;
                current.AvatarFileName = stored.FileName;
                current.AvatarContentType = stored.ContentType;
                current.UpdatedAt = DateTimeOffset.UtcNow;
                ApiCommon.Audit(state, current.Id, "profile.avatar.updated", "user", current.Id.ToString());
                return new { User = current, OldFile = oldFile };
            }, context.RequestAborted);
            await files.DeleteAsync(result.OldFile, CancellationToken.None);
            await changes.NotifyAsync(
                context.RequestAborted,
                LibraryChangeTarget.Profiles(result.User.Id),
                LibraryChangeTarget.Users(result.User.Id));
            return Results.Ok(ApiCommon.ToMe(result.User));
        }
        catch
        {
            await files.DeleteAsync(stored?.FileName, CancellationToken.None);
            throw;
        }
    }

    private static async Task<IResult> DeleteAvatarAsync(
        HttpContext context,
        IAppDataStore store,
        AvatarFileStore files,
        ILibraryChangeNotifier changes)
    {
        var identity = AccessControl.RequireUser(context);
        var result = await store.UpdateAsync(state =>
        {
            var current = ApiCommon.CurrentUser(state, identity);
            var oldFile = current.AvatarFileName;
            current.AvatarFileName = null;
            current.AvatarContentType = null;
            current.UpdatedAt = DateTimeOffset.UtcNow;
            ApiCommon.Audit(state, current.Id, "profile.avatar.deleted", "user", current.Id.ToString());
            return new { User = current, OldFile = oldFile };
        }, context.RequestAborted);
        await files.DeleteAsync(result.OldFile, CancellationToken.None);
        await changes.NotifyAsync(
            context.RequestAborted,
            LibraryChangeTarget.Profiles(result.User.Id),
            LibraryChangeTarget.Users(result.User.Id));
        return Results.Ok(ApiCommon.ToMe(result.User));
    }

    private static async Task<IResult> ListUsersAsync(HttpContext context, IAppDataStore store)
    {
        var identity = AccessControl.RequireUser(context);
        var search = context.Request.Query["search"].ToString().Trim();
        var page = QueryInt(context, "page", 1, 1, 100_000);
        var pageSize = QueryInt(context, "pageSize", 30, 1, 100);

        var result = await store.ReadAsync(state =>
        {
            var current = ApiCommon.CurrentUser(state, identity);
            IEnumerable<UserRecord> query = state.Users;
            if (search.Length > 0)
            {
                query = query.Where(user =>
                    user.Username.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    user.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase));
            }

            var users = query
                .OrderByDescending(user => user.IsEnabled)
                .ThenBy(user => user.DisplayName)
                .ThenBy(user => user.Username)
                .ToList();
            var items = users.Skip((page - 1) * pageSize).Take(pageSize).Select(user =>
            {
                var counts = state.Assets
                    .Where(asset => asset.UploadedByUserId == user.Id &&
                        asset.State == AssetState.Active &&
                        asset.HasOriginal &&
                        (current.IsAdmin || current.Permissions.Contains(PermissionNames.BrowseAssets)) &&
                        ApiCommon.CanAccessCategory(current, asset.Category))
                    .GroupBy(asset => asset.Category)
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
                return ApiCommon.ToPublicProfile(user, identity.UserId, counts);
            }).ToList();
            return new PageResult<object>(items, page, pageSize, users.Count);
        }, context.RequestAborted);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetUserAsync(Guid userId, HttpContext context, IAppDataStore store)
    {
        var identity = AccessControl.RequireUser(context);
        var result = await store.ReadAsync(state =>
        {
            var current = ApiCommon.CurrentUser(state, identity);
            var user = state.Users.FirstOrDefault(item => item.Id == userId)
                ?? throw new ApiException(StatusCodes.Status404NotFound, "user_not_found", "用户不存在。");
            var counts = state.Assets
                .Where(asset => asset.UploadedByUserId == user.Id &&
                    asset.State == AssetState.Active &&
                    asset.HasOriginal &&
                    (current.IsAdmin || current.Permissions.Contains(PermissionNames.BrowseAssets)) &&
                    ApiCommon.CanAccessCategory(current, asset.Category))
                .GroupBy(asset => asset.Category)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
            return ApiCommon.ToPublicProfile(user, identity.UserId, counts);
        }, context.RequestAborted);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetAvatarAsync(
        Guid userId,
        HttpContext context,
        IAppDataStore store,
        AvatarFileStore files)
    {
        _ = AccessControl.RequireUser(context);
        var avatar = await store.ReadAsync(state =>
        {
            var user = state.Users.FirstOrDefault(item => item.Id == userId)
                ?? throw new ApiException(StatusCodes.Status404NotFound, "user_not_found", "用户不存在。");
            return user.AvatarFileName is null
                ? null
                : new { user.AvatarFileName, ContentType = user.AvatarContentType ?? "application/octet-stream" };
        }, context.RequestAborted);

        if (avatar is null)
        {
            return Results.NotFound();
        }

        var stream = await files.OpenReadAsync(avatar.AvatarFileName, context.RequestAborted);
        if (stream is null)
        {
            return Results.NotFound();
        }

        context.Response.Headers.CacheControl = "private, max-age=300";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        return Results.Stream(stream, avatar.ContentType);
    }

    private static int QueryInt(HttpContext context, string key, int fallback, int minimum, int maximum) =>
        int.TryParse(context.Request.Query[key], out var value) ? Math.Clamp(value, minimum, maximum) : fallback;

    private static ApiException InvalidCredentials() => new(
        StatusCodes.Status401Unauthorized,
        "invalid_credentials",
        "用户名、邮箱或密码不正确。",
        "Bearer");
}
