using InternalAssetLibrary.Client.Core.Http;

namespace InternalAssetLibrary.Client.ViewModels;

public sealed class AdminUserItemViewModel(ApiAdminUser user)
{
    public ApiAdminUser User { get; } = user;
    public string DisplayName => string.IsNullOrWhiteSpace(User.DisplayName) ? User.Username : User.DisplayName;
    public string Username => $"@{User.Username}";
    public string RoleLabel => User.IsAdmin ? "管理员" : "成员";
    public string StatusLabel => User.IsEnabled ? "正常" : "已停用";
    public string StatusActionLabel => User.IsEnabled ? "停用" : "启用";
    public string PermissionSummary => User.IsAdmin
        ? "管理员拥有全部管理能力"
        : User.Permissions.Count == 0 ? "未授予权限" : $"{User.Permissions.Count} 项权限";
}

public sealed class AdminAuditItemViewModel(ApiAuditRecord record)
{
    public ApiAuditRecord Record { get; } = record;
    public string TimeLabel => Record.OccurredAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string ActionLabel => Record.Action;
    public string TargetLabel => string.IsNullOrWhiteSpace(Record.TargetId)
        ? Record.TargetType
        : $"{Record.TargetType} / {Record.TargetId}";
    public string DetailLabel => string.IsNullOrWhiteSpace(Record.Detail) ? "-" : Record.Detail;
}

public sealed class AdminPermissionOption(string key, string label, bool isSelected)
{
    public string Key { get; } = key;
    public string Label { get; } = label;
    public bool IsSelected { get; set; } = isSelected;
}
