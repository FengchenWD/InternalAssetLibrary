using System.Net.Mail;
using System.Text.RegularExpressions;
using InternalAssetLibrary.Contracts;
using InternalAssetLibrary.Server.Data;
using InternalAssetLibrary.Server.Security;

namespace InternalAssetLibrary.Server.Api;

internal static partial class ApiCommon
{
    public static UserRecord CurrentUser(AppState state, RequestIdentity identity)
    {
        var user = state.Users.FirstOrDefault(item => item.Id == identity.UserId && item.IsEnabled);
        return user ?? throw new ApiException(StatusCodes.Status401Unauthorized, "session_invalid", "登录状态已失效。", "Bearer");
    }

    public static UserRecord CurrentAdmin(AppState state, RequestIdentity identity)
    {
        var user = CurrentUser(state, identity);
        if (!user.IsAdmin)
        {
            throw new ApiException(StatusCodes.Status403Forbidden, "administrator_required", "此操作仅管理员可用。");
        }

        return user;
    }

    public static void RequirePermission(UserRecord user, string permission)
    {
        if (!user.IsAdmin && !user.Permissions.Contains(permission))
        {
            throw new ApiException(StatusCodes.Status403Forbidden, "permission_denied", "当前账号没有执行此操作的权限。");
        }
    }

    public static bool CanAccessCategory(UserRecord user, string category)
    {
        if (user.IsAdmin)
        {
            return true;
        }

        var permission = category switch
        {
            AssetCategories.Bgm => PermissionNames.AccessBgm,
            AssetCategories.SoundEffect => PermissionNames.AccessSoundEffects,
            AssetCategories.Image => PermissionNames.AccessImages,
            AssetCategories.Video => PermissionNames.AccessVideos,
            _ => string.Empty
        };
        return permission.Length > 0 && user.Permissions.Contains(permission);
    }

    public static void RequireCategory(UserRecord user, string category)
    {
        if (!CanAccessCategory(user, category))
        {
            throw new ApiException(StatusCodes.Status403Forbidden, "category_denied", "当前账号无权访问此素材大类。");
        }
    }

    public static string ValidateUsername(string value)
    {
        var username = (value ?? string.Empty).Trim();
        if (!UsernamePattern().IsMatch(username))
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "invalid_username",
                "用户名必须为 3 至 32 位，只能包含英文字母、数字、点、下划线和连字符，且首位必须为字母或数字。");
        }

        return username;
    }

    public static string Normalize(string value) => value.Trim().ToUpperInvariant();

    public static string ValidateEmail(string value)
    {
        var email = (value ?? string.Empty).Trim();
        if (email.Length is < 3 or > 254)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_email", "邮箱格式无效。");
        }

        try
        {
            var address = new MailAddress(email);
            if (!address.Address.Equals(email, StringComparison.OrdinalIgnoreCase))
            {
                throw new FormatException();
            }
        }
        catch (FormatException)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_email", "邮箱格式无效。");
        }

        return email;
    }

    public static string? TrimOptional(string? value, int maxLength, string field)
    {
        var result = value?.Trim();
        if (string.IsNullOrEmpty(result))
        {
            return null;
        }

        if (result.Length > maxLength)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_profile", $"{field}不能超过 {maxLength} 个字符。");
        }

        return result;
    }

    public static string TrimRequired(string? value, int maxLength, string field)
    {
        var result = value?.Trim();
        if (string.IsNullOrEmpty(result) || result.Length > maxLength)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_request", $"{field}不能为空且不能超过 {maxLength} 个字符。");
        }

        return result;
    }

    public static double? ValidateAssetDuration(double? durationSeconds)
    {
        if (durationSeconds is null)
        {
            return null;
        }

        if (!double.IsFinite(durationSeconds.Value) ||
            durationSeconds.Value < 0 ||
            durationSeconds.Value > TimeSpan.MaxValue.TotalSeconds)
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "invalid_duration",
                "素材时长必须是可表示的非负秒数。");
        }

        return durationSeconds.Value;
    }

    public static ProfileVisibility ParseVisibility(string? value, string field)
    {
        if (string.Equals(value, "team", StringComparison.OrdinalIgnoreCase))
        {
            return ProfileVisibility.Team;
        }

        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "private", StringComparison.OrdinalIgnoreCase))
        {
            return ProfileVisibility.Private;
        }

        throw new ApiException(StatusCodes.Status400BadRequest, "invalid_visibility", $"{field}可见性必须为 private 或 team。");
    }

    public static string? ValidateGender(string? value, string? customGender, out string? normalizedCustom)
    {
        var gender = TrimOptional(value, 16, "性别")?.ToLowerInvariant();
        var allowed = new[] { "male", "female", "custom" };
        if (gender is not null && !allowed.Contains(gender, StringComparer.Ordinal))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_gender", "性别必须为 male、female、custom 或留空。");
        }

        normalizedCustom = gender == "custom" ? TrimOptional(customGender, 40, "自定义性别") : null;
        return gender;
    }

    public static HashSet<string> ValidatePermissions(IEnumerable<string>? permissions)
    {
        var result = new HashSet<string>(permissions ?? [], StringComparer.OrdinalIgnoreCase);
        if (result.Any(permission => !PermissionNames.All.Contains(permission)))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_permission", "请求包含未知权限。");
        }

        return result;
    }

    public static HashSet<string> ValidateTags(IEnumerable<string>? tags)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawTag in tags ?? [])
        {
            result.Add(ValidateTagName(rawTag));
            if (result.Count > 20)
            {
                throw new ApiException(StatusCodes.Status400BadRequest, "too_many_tags", "每个素材最多可使用 20 个标签。");
            }
        }

        return result;
    }

    public static string ValidateTagName(string? value)
    {
        var tag = value?.Trim() ?? string.Empty;
        if (tag.Length is < 1 or > 32)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_tag", "标签名称必须为 1 至 32 个字符。");
        }

        return tag;
    }

    public static void Audit(
        AppState state,
        Guid? actorUserId,
        string action,
        string targetType,
        string? targetId,
        string? detail = null)
    {
        state.AuditLog.Add(new AuditRecord
        {
            Id = Guid.NewGuid(),
            ActorUserId = actorUserId,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            Detail = detail,
            OccurredAt = DateTimeOffset.UtcNow
        });
    }

    public static object ToMe(UserRecord user, bool? passwordChangeRecommendation = null) => new
    {
        id = user.Id,
        user.Username,
        user.DisplayName,
        user.Email,
        user.Bio,
        user.Birthday,
        user.Gender,
        user.CustomGender,
        user.Contact,
        birthdayVisibility = VisibilityValue(user.BirthdayVisibility),
        genderVisibility = VisibilityValue(user.GenderVisibility),
        contactVisibility = VisibilityValue(user.ContactVisibility),
        hasAvatar = user.AvatarFileName is not null,
        user.IsAdmin,
        user.IsEnabled,
        mustChangePassword = passwordChangeRecommendation ?? user.MustChangePassword,
        permissions = user.Permissions.Order(StringComparer.OrdinalIgnoreCase)
    };

    public static object ToAdminSummary(UserRecord user) => new
    {
        id = user.Id,
        user.Username,
        user.DisplayName,
        user.IsEnabled,
        user.IsAdmin,
        user.MustChangePassword,
        hasEmail = user.Email is not null,
        hasAvatar = user.AvatarFileName is not null,
        permissions = user.Permissions.Order(StringComparer.OrdinalIgnoreCase),
        user.CreatedAt,
        user.UpdatedAt
    };

    public static object ToPublicProfile(UserRecord user, Guid viewerId, IReadOnlyDictionary<string, int>? counts = null)
    {
        var own = user.Id == viewerId;
        return new
        {
            id = user.Id,
            user.Username,
            user.DisplayName,
            user.Bio,
            birthday = own || user.BirthdayVisibility == ProfileVisibility.Team ? user.Birthday : null,
            gender = own || user.GenderVisibility == ProfileVisibility.Team ? user.Gender : null,
            customGender = own || user.GenderVisibility == ProfileVisibility.Team ? user.CustomGender : null,
            contact = own || user.ContactVisibility == ProfileVisibility.Team ? user.Contact : null,
            hasAvatar = user.AvatarFileName is not null,
            user.IsEnabled,
            user.CreatedAt,
            assetCounts = counts ?? new Dictionary<string, int>()
        };
    }

    public static object ToAsset(AssetRecord asset, UserRecord? uploader) => new
    {
        id = asset.Id,
        currentVersionId = asset.CurrentVersionId,
        asset.Name,
        asset.Category,
        asset.OriginalFileName,
        asset.Extension,
        asset.SizeBytes,
        asset.DurationSeconds,
        asset.ContentHash,
        asset.ObjectKey,
        asset.HasOriginal,
        asset.Notes,
        asset.FolderId,
        tags = asset.Tags.Order(StringComparer.OrdinalIgnoreCase),
        uploadedBy = new
        {
            id = asset.UploadedByUserId,
            Username = uploader?.Username ?? asset.UploadedByUsername,
            DisplayName = uploader?.DisplayName ?? asset.UploadedByDisplayName
        },
        asset.UploadedAt,
        asset.UpdatedAt,
        asset.Version,
        previousVersionCount = asset.PreviousVersions.Count,
        state = asset.State == AssetState.Active ? "active" : "recycled",
        asset.RecycledAt,
        asset.PurgeAfter,
        derivatives = ToDerivatives(asset)
    };

    public static object ToAssetFolder(AssetFolderRecord folder, UserRecord? creator) => new
    {
        folder.Id,
        folder.ParentId,
        folder.Name,
        createdBy = new
        {
            id = folder.CreatedByUserId,
            Username = creator?.Username ?? folder.CreatedByUsername,
            DisplayName = creator?.DisplayName ?? folder.CreatedByDisplayName
        },
        folder.CreatedAt,
        folder.UpdatedAt
    };

    public static AssetDerivativesInfo ToDerivatives(AssetRecord asset) => new(
        ToDerivative(asset, AssetDerivativeKind.Thumbnail),
        ToDerivative(asset, AssetDerivativeKind.Proxy));

    public static AssetDerivativeInfo ToDerivative(AssetRecord asset, AssetDerivativeKind kind)
    {
        var record = AssetDerivativeRules.Select(asset, kind);
        return new AssetDerivativeInfo(
            kind,
            record.AssetVersionId,
            record.State switch
            {
                DerivativeState.Queued => AssetDerivativeState.Queued,
                DerivativeState.Processing => AssetDerivativeState.Processing,
                DerivativeState.Ready => AssetDerivativeState.Ready,
                DerivativeState.Unavailable => AssetDerivativeState.Unavailable,
                DerivativeState.Failed => AssetDerivativeState.Failed,
                _ => throw new ArgumentOutOfRangeException()
            },
            record.FormatVersion,
            record.State == DerivativeState.Ready
                ? $"/api/assets/{asset.Id:D}/derivatives/{AssetDerivativeRules.KindValue(kind)}"
                : null,
            record.ContentType,
            record.SizeBytes,
            record.ETag,
            record.ErrorCode,
            record.ErrorMessage,
            record.RetryCount,
            record.UpdatedAt);
    }

    public static TeamLutSummary ToTeamLut(TeamLutRecord lut, UserRecord? uploader) => new(
        lut.Id,
        lut.CurrentVersionId,
        lut.Name,
        lut.OriginalFileName,
        lut.Notes,
        lut.SizeBytes,
        lut.ContentHash,
        lut.HasContent,
        lut.Version,
        lut.State == InternalAssetLibrary.Server.Data.TeamLutState.Active
            ? InternalAssetLibrary.Contracts.TeamLutState.Active
            : InternalAssetLibrary.Contracts.TeamLutState.Recycled,
        new TeamLutUploader(
            lut.UploadedByUserId,
            uploader?.Username ?? lut.UploadedByUsername,
            uploader?.DisplayName ?? lut.UploadedByDisplayName),
        lut.UploadedAt,
        lut.UpdatedAt,
        lut.RecycledAt,
        lut.PurgeAfter);

    private static string VisibilityValue(ProfileVisibility visibility) =>
        visibility == ProfileVisibility.Team ? "team" : "private";

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{2,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex UsernamePattern();
}
