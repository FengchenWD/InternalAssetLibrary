using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using InternalAssetLibrary.Client.Core.Auth;
using InternalAssetLibrary.Client.Core.Http;
using InternalAssetLibrary.Client.Core.Settings;
using InternalAssetLibrary.Client.Services;

namespace InternalAssetLibrary.Client;

public sealed record LoginSessionResult(
    string Token,
    ApiCurrentUser User,
    string Password,
    bool RememberAccount,
    bool RememberPassword);

public sealed record SavedLoginAccountItem(SavedLoginAccount Account, bool IsCurrent)
{
    public string DisplayLabel => string.IsNullOrWhiteSpace(Account.DisplayName)
        ? Account.Username
        : Account.DisplayName;

    public string DetailLabel => IsCurrent
        ? UiLocalization.Format("@{0} · 当前正在使用", Account.Username)
        : Account.PasswordRemembered
            ? UiLocalization.Format("@{0} · 已安全记住密码", Account.Username)
            : UiLocalization.Format("@{0} · 需要输入密码", Account.Username);

    public string Initial => DisplayLabel[..1].ToUpperInvariant();

    public string ActionLabel => UiLocalization.Text(IsCurrent ? "当前账号" : "登录");

    public bool CanLogin => !IsCurrent;
}

public sealed partial class LoginWindow : Window
{
    private readonly AssetLibraryApiClient _api;
    private readonly StaticAccessTokenProvider _tokenProvider;
    private readonly Uri _serverOrigin;
    private readonly IRememberedLoginPasswordStore _passwordStore;
    private readonly Func<SavedLoginAccount, Task>? _deleteSavedAccount;
    private readonly Guid? _currentUserId;
    private readonly List<SavedLoginAccount> _savedAccounts = [];
    private readonly string? _originalAccessToken;
    private bool _completed;
    private bool _updatingRememberChoices;

    public LoginWindow()
    {
        _api = null!;
        _tokenProvider = null!;
        _serverOrigin = null!;
        _passwordStore = null!;
        InitializeComponent();
        Services.UiLocalization.Apply(this, Services.UiLocalization.CurrentLanguage);
    }

    public LoginWindow(
        AssetLibraryApiClient api,
        StaticAccessTokenProvider tokenProvider,
        Uri serverOrigin,
        IReadOnlyList<SavedLoginAccount> savedAccounts,
        IRememberedLoginPasswordStore passwordStore,
        Guid? currentUserId = null,
        Func<SavedLoginAccount, Task>? deleteSavedAccount = null)
        : this()
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        _serverOrigin = serverOrigin ?? throw new ArgumentNullException(nameof(serverOrigin));
        _passwordStore = passwordStore ?? throw new ArgumentNullException(nameof(passwordStore));
        _deleteSavedAccount = deleteSavedAccount;
        _currentUserId = currentUserId;
        _originalAccessToken = tokenProvider.AccessToken;
        ServerAddressText.Text = serverOrigin.GetLeftPart(UriPartial.Authority);

        _savedAccounts.AddRange(savedAccounts ?? []);
        var items = RefreshSavedAccounts();
        LoginHeadingText.Text = UiLocalization.Text(currentUserId.HasValue ? "切换账号" : "连接团队素材库");
        Title = UiLocalization.Text(currentUserId.HasValue ? "切换账号" : "登录云汀素材管理工具");

        var suggested = items.FirstOrDefault(item => !item.IsCurrent) ?? items.FirstOrDefault();
        if (suggested is not null)
        {
            SelectSavedAccount(suggested.Account);
        }

        Opened += LoginWindow_OnOpened;
        Closing += LoginWindow_OnClosing;
    }

    private void LoginWindow_OnOpened(object? sender, EventArgs eventArgs)
    {
        if (string.IsNullOrWhiteSpace(IdentifierBox.Text))
        {
            IdentifierBox.Focus();
        }
        else
        {
            PasswordBox.Focus();
        }
    }

    private SavedLoginAccountItem[] RefreshSavedAccounts()
    {
        var items = _savedAccounts
            .OrderByDescending(account => account.LastUsedAt)
            .Select(account => new SavedLoginAccountItem(account, account.UserId == _currentUserId))
            .ToArray();
        SavedAccountsList.ItemsSource = items;
        SavedAccountsSection.IsVisible = items.Length > 0;
        return items;
    }

    private void LoginWindow_OnClosing(object? sender, WindowClosingEventArgs eventArgs)
    {
        if (!_completed)
        {
            _tokenProvider.AccessToken = _originalAccessToken;
        }
    }

    private async void Login_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        var identifier = IdentifierBox.Text?.Trim() ?? string.Empty;
        var password = PasswordBox.Text ?? string.Empty;
        if (identifier.Length == 0 || password.Length == 0)
        {
            LoginStatusText.Text = UiLocalization.Text("请输入用户名（或绑定邮箱）和密码。");
            return;
        }

        await AuthenticateAsync(identifier, password);
    }

    private async void SavedAccountLogin_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not Button { DataContext: SavedLoginAccountItem item } || item.IsCurrent)
        {
            return;
        }

        SelectSavedAccount(item.Account);
        if (!item.Account.PasswordRemembered)
        {
            LoginStatusText.Text = UiLocalization.Format("请输入 @{0} 的密码。", item.Account.Username);
            PasswordBox.Focus();
            return;
        }

        SetBusy(true);
        LoginStatusText.Text = UiLocalization.Format("正在读取 @{0} 的安全凭据...", item.Account.Username);
        try
        {
            var password = await _passwordStore.GetAsync(_serverOrigin, item.Account.UserId);
            if (string.IsNullOrEmpty(password))
            {
                SetRememberChoices(rememberAccount: true, rememberPassword: false);
                LoginStatusText.Text = UiLocalization.Text("系统凭据中没有这个账号的密码，请重新输入。");
                PasswordBox.Focus();
                return;
            }

            await AuthenticateAsync(item.Account.Username, password, managesBusyState: false);
        }
        catch (Exception exception)
        {
            _tokenProvider.AccessToken = _originalAccessToken;
            SetRememberChoices(rememberAccount: true, rememberPassword: false);
            LoginStatusText.Text = UiLocalization.Format("读取系统凭据失败：{0}", exception.Message);
            PasswordBox.Focus();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void DeleteSavedAccount_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not MenuItem { DataContext: SavedLoginAccountItem item } ||
            _deleteSavedAccount is null)
        {
            return;
        }

        var confirmed = await new MessageDialogWindow(
                UiLocalization.Text("删除保存的账号"),
                UiLocalization.Format(
                    "确定从这台电脑删除 @{0} 的保存记录和密码凭据吗？不会删除服务器账号，也不会退出当前登录。",
                    item.Account.Username),
                UiLocalization.Text("确认删除"),
                UiLocalization.Text("取消"))
            .ShowDialog<bool>(this);
        if (!confirmed)
        {
            return;
        }

        SetBusy(true);
        LoginStatusText.Text = UiLocalization.Format("正在删除 @{0} 的本机保存记录...", item.Account.Username);
        try
        {
            await _deleteSavedAccount(item.Account);
            _savedAccounts.RemoveAll(account => account.UserId == item.Account.UserId);
            RefreshSavedAccounts();
            if (string.Equals(
                    IdentifierBox.Text?.Trim(),
                    item.Account.Username,
                    StringComparison.OrdinalIgnoreCase))
            {
                PasswordBox.Text = string.Empty;
                SetRememberChoices(rememberAccount: false, rememberPassword: false);
            }

            LoginStatusText.Text = item.IsCurrent
                ? UiLocalization.Text("已删除本机保存的账号和凭据，当前登录状态未改变。")
                : UiLocalization.Text("已删除本机保存的账号和凭据。");
        }
        catch (Exception exception)
        {
            LoginStatusText.Text = UiLocalization.Format("删除保存的账号失败：{0}", exception.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task AuthenticateAsync(string identifier, string password, bool managesBusyState = true)
    {
        if (managesBusyState)
        {
            SetBusy(true);
        }

        LoginStatusText.Text = UiLocalization.Text("正在登录...");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var login = await _api.LoginAsync(
                new ApiLoginRequest(identifier, password),
                timeout.Token);
            _tokenProvider.AccessToken = login.Token;
            if (login.User.MustChangePassword)
            {
                await new MessageDialogWindow(
                        "密码安全建议",
                        "首次登录成功。建议尽快在个人资料中修改管理员提供的初始密码。")
                    .ShowDialog(this);
            }

            Finish(login.Token, login.User, password);
        }
        catch (Exception exception)
        {
            _tokenProvider.AccessToken = _originalAccessToken;
            LoginStatusText.Text = UserMessage(exception);
        }
        finally
        {
            if (managesBusyState)
            {
                SetBusy(false);
            }
        }
    }

    private void Finish(string token, ApiCurrentUser user, string password)
    {
        var rememberPassword = RememberPasswordToggle.IsChecked == true;
        var rememberAccount = rememberPassword || RememberAccountToggle.IsChecked == true;
        _completed = true;
        PasswordBox.Text = string.Empty;
        Close(new LoginSessionResult(token, user, password, rememberAccount, rememberPassword));
    }

    private void SelectSavedAccount(SavedLoginAccount account)
    {
        IdentifierBox.Text = account.Username;
        PasswordBox.Text = string.Empty;
        SetRememberChoices(rememberAccount: true, account.PasswordRemembered);
    }

    private void SetRememberChoices(bool rememberAccount, bool rememberPassword)
    {
        _updatingRememberChoices = true;
        RememberAccountToggle.IsChecked = rememberAccount || rememberPassword;
        RememberPasswordToggle.IsChecked = rememberPassword;
        _updatingRememberChoices = false;
    }

    private void RememberAccount_OnChanged(object? sender, RoutedEventArgs eventArgs)
    {
        if (!_updatingRememberChoices && RememberAccountToggle.IsChecked != true)
        {
            SetRememberChoices(rememberAccount: false, rememberPassword: false);
        }
    }

    private void RememberPassword_OnChanged(object? sender, RoutedEventArgs eventArgs)
    {
        if (!_updatingRememberChoices && RememberPasswordToggle.IsChecked == true)
        {
            SetRememberChoices(rememberAccount: true, rememberPassword: true);
        }
    }

    private void SetBusy(bool value)
    {
        LoginButton.IsEnabled = !value;
        SavedAccountsList.IsEnabled = !value;
    }

    private void LoginField_OnKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Enter)
        {
            Login_OnClick(sender, new RoutedEventArgs());
            eventArgs.Handled = true;
        }
    }

    private void Cancel_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        _tokenProvider.AccessToken = _originalAccessToken;
        PasswordBox.Text = string.Empty;
        Close(null);
    }

    private string UserMessage(Exception exception) =>
        ApiErrorLocalization.Message(exception, _serverOrigin);
}
