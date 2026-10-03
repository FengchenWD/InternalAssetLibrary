namespace InternalAssetLibrary.Contracts;

public enum UserStatus
{
    Active,
    Disabled
}

public enum UserRole
{
    Member,
    Administrator
}

public enum ProfileFieldVisibility
{
    SelfOnly,
    Team
}

public enum ProfileGender
{
    Unspecified,
    Male,
    Female,
    Custom
}

public sealed record UserSummary(
    Guid Id,
    string Username,
    string? DisplayName,
    string? AvatarUrl,
    UserStatus Status,
    UserRole Role);

public sealed record UserProfile(
    Guid Id,
    string Username,
    string? DisplayName,
    string? Biography,
    DateOnly? Birthday,
    ProfileGender Gender,
    string? CustomGender,
    string? Contact,
    string? AvatarUrl,
    UserStatus Status,
    ProfileFieldVisibility BirthdayVisibility,
    ProfileFieldVisibility GenderVisibility,
    ProfileFieldVisibility ContactVisibility);

public sealed record CurrentUserProfile(
    UserProfile Profile,
    string? LoginEmail,
    bool MustChangePassword,
    EffectivePermissions EffectivePermissions);

public sealed record UpdateUserProfileRequest(
    string? DisplayName,
    string? Biography,
    DateOnly? Birthday,
    ProfileGender Gender,
    string? CustomGender,
    string? Contact,
    ProfileFieldVisibility BirthdayVisibility,
    ProfileFieldVisibility GenderVisibility,
    ProfileFieldVisibility ContactVisibility);

public sealed record CreateUserRequest(
    string Username,
    string TemporaryPassword,
    UserRole Role,
    UserPermission Permissions,
    AssetCategoryAccess CategoryAccess);

public sealed record LoginRequest(string Login, string Password);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record BindEmailRequest(string? Email, string CurrentPassword);

public sealed record SetUserStatusRequest(UserStatus Status);

public sealed record ResetPasswordRequest(string TemporaryPassword);

public sealed record ClearPublicProfileRequest(IReadOnlyCollection<string>? Fields);

public sealed record ClearPublicProfileResult(Guid UserId, IReadOnlyList<string> ClearedFields);
