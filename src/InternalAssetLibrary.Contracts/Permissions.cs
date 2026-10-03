namespace InternalAssetLibrary.Contracts;

[Flags]
public enum UserPermission : long
{
    None = 0,
    Browse = 1L << 0,
    Preview = 1L << 1,
    Download = 1L << 2,
    Upload = 1L << 3,
    ModifyTags = 1L << 4,
    EditOwnMarkers = 1L << 5,
    EditOwnAssets = 1L << 6,
    DeleteOwnAssets = 1L << 7,
    ManageAssets = 1L << 8,
    ManageTags = 1L << 9,
    ManageUsers = 1L << 10,
    ManagePermissions = 1L << 11,
    ViewAuditLog = 1L << 12,

    MemberDefault = Browse | Preview | Download | Upload | ModifyTags |
                    EditOwnMarkers | EditOwnAssets | DeleteOwnAssets,
    AdministratorDefault = MemberDefault | ManageAssets | ManageTags |
                           ManageUsers | ManagePermissions | ViewAuditLog
}

[Flags]
public enum AssetCategoryAccess
{
    None = 0,
    Bgm = 1 << 0,
    SoundEffect = 1 << 1,
    Image = 1 << 2,
    Video = 1 << 3,
    All = Bgm | SoundEffect | Image | Video
}

public sealed record EffectivePermissions(
    UserRole Role,
    UserPermission Permissions,
    AssetCategoryAccess CategoryAccess);

public sealed record UpdatePermissionsRequest(
    UserRole Role,
    UserPermission Permissions,
    AssetCategoryAccess CategoryAccess);
