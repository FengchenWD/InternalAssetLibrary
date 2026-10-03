using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using InternalAssetLibrary.Client.Core.Http;
using InternalAssetLibrary.Client.ViewModels;

namespace InternalAssetLibrary.Client;

public sealed record AdminUserEditorResult(
    string Username,
    string? DisplayName,
    string? TemporaryPassword,
    bool IsAdmin,
    IReadOnlyList<string> Permissions);

public sealed partial class AdminUserEditorWindow : Window
{
    private static readonly (string Key, string Label)[] AvailablePermissions =
    [
        ("assets.browse", "浏览素材"),
        ("assets.preview", "预览素材"),
        ("assets.download", "下载素材"),
        ("assets.upload", "上传素材"),
        ("assets.tags", "修改标签"),
        ("markers.maintain", "维护标记"),
        ("assets.edit-own", "编辑自己的素材"),
        ("assets.delete-own", "删除自己的素材"),
        ("assets.category.bgm", "访问 BGM"),
        ("assets.category.sound-effect", "访问音效"),
        ("assets.category.image", "访问图片"),
        ("assets.category.video", "访问视频")
    ];

    private readonly bool _isCreating;

    public AdminUserEditorWindow() : this(null)
    {
    }

    public AdminUserEditorWindow(ApiAdminUser? user)
    {
        InitializeComponent();
        DataContext = this;
        _isCreating = user is null;
        var selected = user?.Permissions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, label) in AvailablePermissions)
        {
            Permissions.Add(new AdminPermissionOption(key, label, selected?.Contains(key) ?? true));
        }

        HeadingText.Text = _isCreating ? "创建团队账号" : "编辑账号";
        DescriptionText.Text = _isCreating ? "创建后用户首次登录会收到修改密码建议" : "修改用户名、角色与权限";
        CreateOnlyFields.IsVisible = _isCreating;
        UsernameBox.Text = user?.Username ?? string.Empty;
        IsAdminBox.IsChecked = user?.IsAdmin ?? false;
    }

    public ObservableCollection<AdminPermissionOption> Permissions { get; } = [];

    private void Save_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        var username = UsernameBox.Text?.Trim() ?? string.Empty;
        if (username.Length is < 3 or > 32 ||
            !char.IsAsciiLetterOrDigit(username[0]) ||
            username.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-')))
        {
            ValidationText.Text = "用户名需为 3 至 32 位，只能使用字母、数字、点、下划线和连字符，且首位必须是字母或数字。";
            return;
        }

        var password = PasswordBox.Text ?? string.Empty;
        if (_isCreating && (password.Length is < 8 or > 128))
        {
            ValidationText.Text = "初始密码必须为 8 至 128 位。";
            return;
        }

        Close(new AdminUserEditorResult(
            username,
            _isCreating ? DisplayNameBox.Text?.Trim() : null,
            _isCreating ? password : null,
            IsAdminBox.IsChecked == true,
            Permissions.Where(option => option.IsSelected).Select(option => option.Key).ToArray()));
    }

    private void Cancel_OnClick(object? sender, RoutedEventArgs eventArgs) => Close(null);
}
