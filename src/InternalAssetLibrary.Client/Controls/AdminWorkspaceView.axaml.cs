using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using InternalAssetLibrary.Client.Core.Http;
using InternalAssetLibrary.Client.ViewModels;

namespace InternalAssetLibrary.Client.Controls;

public sealed partial class AdminWorkspaceView : UserControl
{
    private const double Mebibyte = 1024d * 1024d;
    private const double Gibibyte = 1024d * 1024d * 1024d;
    private Func<AssetLibraryApiClient?>? _apiProvider;
    private Func<Uri?>? _serverOriginProvider;
    private Action<string>? _navigate;
    private bool _refreshing;
    private ApiServerSettings? _settings;

    public AdminWorkspaceView()
    {
        InitializeComponent();
        DataContext = this;
    }

    public ObservableCollection<AdminUserItemViewModel> AdminUsers { get; } = [];

    public ObservableCollection<AdminAuditItemViewModel> AdminAuditItems { get; } = [];

    public void Configure(
        Func<AssetLibraryApiClient?> apiProvider,
        Func<Uri?> serverOriginProvider,
        Action<string> navigate)
    {
        _apiProvider = apiProvider ?? throw new ArgumentNullException(nameof(apiProvider));
        _serverOriginProvider = serverOriginProvider ?? throw new ArgumentNullException(nameof(serverOriginProvider));
        _navigate = navigate ?? throw new ArgumentNullException(nameof(navigate));
    }

    public async Task RefreshAsync()
    {
        if (_refreshing || _apiProvider?.Invoke() is not { } api)
        {
            return;
        }

        _refreshing = true;
        AdminStatusText.Text = "正在读取管理数据…";
        try
        {
            var usersTask = api.ListAdminUsersAsync();
            var auditTask = api.ListAdminAuditAsync();
            var settingsTask = api.GetAdminServerSettingsAsync();
            await Task.WhenAll(usersTask, auditTask, settingsTask);
            ApplyUsers(await usersTask);
            ApplyAudit(await auditTask);
            ApplySettings(await settingsTask);
            AdminStatusText.Text = "管理数据已刷新。客户端不会读取或显示任何云服务密钥。";
        }
        catch (Exception exception)
        {
            AdminStatusText.Text = $"读取管理数据失败：{exception.Message}";
        }
        finally
        {
            _refreshing = false;
        }
    }

    private async Task RefreshUsersAsync()
    {
        if (_apiProvider?.Invoke() is not { } api) return;
        ApplyUsers(await api.ListAdminUsersAsync());
    }

    private void ApplyUsers(IReadOnlyList<ApiAdminUser> users)
    {
        AdminUsers.Clear();
        foreach (var user in users)
        {
            AdminUsers.Add(new AdminUserItemViewModel(user));
        }

        AdminUserCountText.Text = $"{AdminUsers.Count} 个团队账号";
    }

    private async Task RefreshAuditAsync()
    {
        if (_apiProvider?.Invoke() is not { } api) return;
        ApplyAudit(await api.ListAdminAuditAsync());
    }

    private void ApplyAudit(IReadOnlyList<ApiAuditRecord> records)
    {
        AdminAuditItems.Clear();
        foreach (var record in records)
        {
            AdminAuditItems.Add(new AdminAuditItemViewModel(record));
        }

        AdminAuditCountText.Text = $"最近 {AdminAuditItems.Count} 条操作";
    }

    private void ApplySettings(ApiServerSettings settings)
    {
        _settings = settings;
        OriginalQuotaBox.Text = FormatNumber(settings.OriginalQuotaBytes / Gibibyte);
        AudioMaxBox.Text = FormatNumber(settings.AudioMaxBytes / Mebibyte);
        ImageMaxBox.Text = FormatNumber(settings.ImageMaxBytes / Mebibyte);
        VideoMaxBox.Text = FormatNumber(settings.VideoMaxBytes / Gibibyte);
        ThumbnailMaxBox.Text = FormatNumber(settings.ThumbnailMaxBytes / Mebibyte);
        ProxyMaxBox.Text = FormatNumber(settings.ProxyMaxBytes / Gibibyte);
        LutMaxBox.Text = FormatNumber(settings.LutMaxBytes / Mebibyte);
        RecycleDaysBox.Text = settings.RecycleRetentionDays.ToString(CultureInfo.CurrentCulture);
        AuditDaysBox.Text = settings.AuditRetentionDays.ToString(CultureInfo.CurrentCulture);
        DownloadDaysBox.Text = settings.DownloadLogRetentionDays.ToString(CultureInfo.CurrentCulture);
        BackupsEnabledBox.IsChecked = settings.BackupsEnabled;
        BackupIntervalBox.Text = settings.BackupIntervalHours.ToString(CultureInfo.CurrentCulture);
        BackupRetentionBox.Text = settings.BackupRetentionDays.ToString(CultureInfo.CurrentCulture);
        BackupPathBox.Text = settings.BackupLocalPath;
        BackupPrefixBox.Text = settings.BackupObjectPrefix;
        WarningRatioBox.Text = FormatNumber(settings.CapacityWarningRatio * 100);
        CriticalRatioBox.Text = FormatNumber(settings.CapacityCriticalRatio * 100);
        DerivativesEnabledBox.IsChecked = settings.DerivativesEnabled;
        DerivativePollBox.Text = settings.DerivativePollIntervalSeconds.ToString(CultureInfo.CurrentCulture);
        DerivativeTimeoutBox.Text = settings.DerivativeProcessTimeoutSeconds.ToString(CultureInfo.CurrentCulture);
        ThumbnailEdgeBox.Text = settings.ThumbnailMaxEdge.ToString(CultureInfo.CurrentCulture);
        SessionDaysBox.Text = settings.SessionLifetimeDays.ToString(CultureInfo.CurrentCulture);
        LoginLimitBox.Text = settings.LoginFailureLimit.ToString(CultureInfo.CurrentCulture);
        LoginWindowBox.Text = settings.LoginFailureWindowMinutes.ToString(CultureInfo.CurrentCulture);
        LoginBlockBox.Text = settings.LoginBlockMinutes.ToString(CultureInfo.CurrentCulture);
        MultipartSizeBox.Text = FormatNumber(settings.MultipartPartSizeBytes / Mebibyte);
        UploadSessionBox.Text = settings.UploadSessionLifetimeHours.ToString(CultureInfo.CurrentCulture);
        SignedUrlBox.Text = settings.SignedUrlLifetimeMinutes.ToString(CultureInfo.CurrentCulture);
        SettingsStatusText.Text = "设置已读取。保存后由服务端校验并立即应用。";
    }

    private async void RefreshAll_OnClick(object? sender, RoutedEventArgs eventArgs) => await RefreshAsync();

    private async void RefreshUsers_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        try { await RefreshUsersAsync(); }
        catch (Exception exception) { AdminStatusText.Text = $"刷新账号失败：{exception.Message}"; }
    }

    private async void RefreshAudit_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        try { await RefreshAuditAsync(); }
        catch (Exception exception) { AdminStatusText.Text = $"刷新审计记录失败：{exception.Message}"; }
    }

    private async void ReloadSettings_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_apiProvider?.Invoke() is not { } api) return;
        try { ApplySettings(await api.GetAdminServerSettingsAsync()); }
        catch (Exception exception) { SettingsStatusText.Text = $"读取设置失败：{exception.Message}"; }
    }

    private void SelectAdminTab_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is Button { Tag: string index } && int.TryParse(index, out var selected))
        {
            AdminTabs.SelectedIndex = selected;
        }
    }

    private void NavigateModule_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is Button { Tag: string module })
        {
            _navigate?.Invoke(module);
        }
    }

    private async void CreateUser_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_apiProvider?.Invoke() is not { } api || TopLevel.GetTopLevel(this) is not Window owner) return;
        var result = await new AdminUserEditorWindow().ShowDialog<AdminUserEditorResult?>(owner);
        if (result is null) return;
        try
        {
            await api.CreateAdminUserAsync(new ApiCreateAdminUserRequest(
                result.Username,
                result.TemporaryPassword!,
                result.DisplayName,
                result.IsAdmin,
                result.Permissions));
            await RefreshUsersAsync();
            AdminStatusText.Text = $"账号 @{result.Username} 已创建。";
        }
        catch (Exception exception)
        {
            await ShowErrorAsync(owner, "无法创建账号", exception);
        }
    }

    private async void EditUser_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not Button { DataContext: AdminUserItemViewModel item } ||
            _apiProvider?.Invoke() is not { } api || TopLevel.GetTopLevel(this) is not Window owner) return;
        var result = await new AdminUserEditorWindow(item.User).ShowDialog<AdminUserEditorResult?>(owner);
        if (result is null) return;
        try
        {
            if (!string.Equals(result.Username, item.User.Username, StringComparison.Ordinal))
            {
                await api.SetAdminUsernameAsync(item.User.Id, new ApiAdminUsernameRequest(result.Username));
            }

            await api.SetAdminUserPermissionsAsync(
                item.User.Id,
                new ApiAdminPermissionsRequest(result.IsAdmin, result.Permissions));
            await RefreshUsersAsync();
            AdminStatusText.Text = $"账号 @{result.Username} 已更新。";
        }
        catch (Exception exception)
        {
            await ShowErrorAsync(owner, "无法更新账号", exception);
        }
    }

    private async void ToggleUserStatus_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not Button { DataContext: AdminUserItemViewModel item } ||
            _apiProvider?.Invoke() is not { } api || TopLevel.GetTopLevel(this) is not Window owner) return;
        var targetEnabled = !item.User.IsEnabled;
        if (!targetEnabled)
        {
            var confirmed = await new MessageDialogWindow(
                "停用账号",
                $"停用 @{item.User.Username} 后，该账号的现有登录会话会失效。是否继续？",
                "停用",
                "取消").ShowDialog<bool?>(owner);
            if (confirmed != true) return;
        }

        try
        {
            await api.SetAdminUserStatusAsync(item.User.Id, new ApiAdminAccountStatusRequest(targetEnabled));
            await RefreshUsersAsync();
        }
        catch (Exception exception)
        {
            await ShowErrorAsync(owner, "无法修改账号状态", exception);
        }
    }

    private async void ResetUserPassword_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not Button { DataContext: AdminUserItemViewModel item } ||
            _apiProvider?.Invoke() is not { } api || TopLevel.GetTopLevel(this) is not Window owner) return;
        var password = await new AdminPasswordResetWindow().ShowDialog<string?>(owner);
        if (password is null) return;
        try
        {
            await api.ResetAdminUserPasswordAsync(item.User.Id, new ApiAdminResetPasswordRequest(password));
            AdminStatusText.Text = $"@{item.User.Username} 的临时密码已重置，现有会话已失效。";
        }
        catch (Exception exception)
        {
            await ShowErrorAsync(owner, "无法重置密码", exception);
        }
    }

    private async void DeleteUser_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not Button { DataContext: AdminUserItemViewModel item } ||
            _apiProvider?.Invoke() is not { } api || TopLevel.GetTopLevel(this) is not Window owner) return;
        var confirmed = await new MessageDialogWindow(
            "永久删除账号",
            $"只能删除已停用账号。删除 @{item.User.Username} 不会删除其已上传素材。是否继续？",
            "永久删除",
            "取消").ShowDialog<bool?>(owner);
        if (confirmed != true) return;
        try
        {
            await api.DeleteAdminUserAsync(item.User.Id);
            await RefreshUsersAsync();
        }
        catch (Exception exception)
        {
            await ShowErrorAsync(owner, "无法删除账号", exception);
        }
    }

    private async void SaveSettings_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_settings is null || _apiProvider?.Invoke() is not { } api) return;
        try
        {
            var request = _settings with
            {
                OriginalQuotaBytes = ToBytes(ParsePositive(OriginalQuotaBox, "原始素材总配额"), Gibibyte),
                AudioMaxBytes = ToBytes(ParsePositive(AudioMaxBox, "音频文件上限"), Mebibyte),
                ImageMaxBytes = ToBytes(ParsePositive(ImageMaxBox, "图片文件上限"), Mebibyte),
                VideoMaxBytes = ToBytes(ParsePositive(VideoMaxBox, "视频文件上限"), Gibibyte),
                ThumbnailMaxBytes = ToBytes(ParsePositive(ThumbnailMaxBox, "缩略图对象上限"), Mebibyte),
                ProxyMaxBytes = ToBytes(ParsePositive(ProxyMaxBox, "代理对象上限"), Gibibyte),
                LutMaxBytes = ToBytes(ParsePositive(LutMaxBox, "LUT 文件上限"), Mebibyte),
                RecycleRetentionDays = ParseInt(RecycleDaysBox, "回收站保留天数"),
                AuditRetentionDays = ParseInt(AuditDaysBox, "审计记录保留天数"),
                DownloadLogRetentionDays = ParseInt(DownloadDaysBox, "下载日志保留天数"),
                BackupsEnabled = BackupsEnabledBox.IsChecked == true,
                BackupIntervalHours = ParseInt(BackupIntervalBox, "备份间隔"),
                BackupRetentionDays = ParseInt(BackupRetentionBox, "备份保留天数"),
                BackupLocalPath = BackupPathBox.Text?.Trim() ?? string.Empty,
                BackupObjectPrefix = BackupPrefixBox.Text?.Trim() ?? string.Empty,
                CapacityWarningRatio = ParsePositive(WarningRatioBox, "容量警告比例") / 100,
                CapacityCriticalRatio = ParsePositive(CriticalRatioBox, "容量临界比例") / 100,
                DerivativesEnabled = DerivativesEnabledBox.IsChecked == true,
                DerivativePollIntervalSeconds = ParseInt(DerivativePollBox, "派生任务轮询间隔"),
                DerivativeProcessTimeoutSeconds = ParseInt(DerivativeTimeoutBox, "派生任务超时"),
                ThumbnailMaxEdge = ParseInt(ThumbnailEdgeBox, "缩略图最大边长"),
                SessionLifetimeDays = ParseInt(SessionDaysBox, "会话有效期"),
                LoginFailureLimit = ParseInt(LoginLimitBox, "登录失败次数上限"),
                LoginFailureWindowMinutes = ParseInt(LoginWindowBox, "失败统计窗口"),
                LoginBlockMinutes = ParseInt(LoginBlockBox, "登录封禁时长"),
                MultipartPartSizeBytes = checked((int)ToBytes(ParsePositive(MultipartSizeBox, "分片大小"), Mebibyte)),
                UploadSessionLifetimeHours = ParseInt(UploadSessionBox, "上传会话有效期"),
                SignedUrlLifetimeMinutes = ParseInt(SignedUrlBox, "COS 临时链接有效期")
            };
            ApplySettings(await api.UpdateAdminServerSettingsAsync(request));
            SettingsStatusText.Text = "服务端设置已保存并应用。";
        }
        catch (Exception exception)
        {
            SettingsStatusText.Text = $"保存失败：{exception.Message}";
        }
    }

    private void OpenWebAdmin_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_serverOriginProvider?.Invoke() is not { IsAbsoluteUri: true } origin) return;
        var adminUri = new Uri(origin.GetLeftPart(UriPartial.Authority) + "/admin", UriKind.Absolute);
        Process.Start(new ProcessStartInfo(adminUri.AbsoluteUri) { UseShellExecute = true });
    }

    private static string FormatNumber(double value) => value.ToString("0.###", CultureInfo.CurrentCulture);

    private static double ParsePositive(TextBox box, string label)
    {
        if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) ||
            !double.IsFinite(value) || value <= 0)
        {
            throw new InvalidOperationException($"{label}必须填写大于 0 的数字。");
        }

        return value;
    }

    private static int ParseInt(TextBox box, string label)
    {
        if (!int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var value) || value <= 0)
        {
            throw new InvalidOperationException($"{label}必须填写大于 0 的整数。");
        }

        return value;
    }

    private static long ToBytes(double value, double unit)
    {
        var bytes = value * unit;
        if (bytes > long.MaxValue) throw new InvalidOperationException("容量设置超出允许范围。");
        return checked((long)Math.Round(bytes));
    }

    private static async Task ShowErrorAsync(Window owner, string title, Exception exception) =>
        await new MessageDialogWindow(title, exception.Message).ShowDialog(owner);
}
