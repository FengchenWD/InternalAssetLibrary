using Avalonia.Threading;
using InternalAssetLibrary.Client.Core.Realtime;
using InternalAssetLibrary.Client.Services;
using InternalAssetLibrary.Contracts;

namespace InternalAssetLibrary.Client;

public sealed partial class MainWindow
{
    private readonly HashSet<string> _pendingRealtimeScopes = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _realtimeRefreshTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(350)
    };
    private LibraryRealtimeClient? _realtimeClient;
    private bool _pendingRealtimeCurrentUserRefresh;

    private void InitializeRealtimeUi()
    {
        _realtimeRefreshTimer.Tick += RealtimeRefreshTimer_OnTick;
        UiLocalization.Register(this, static window => window.RefreshRealtimeLocalization());
    }

    private async Task StartRealtimeAsync()
    {
        await StopRealtimeAsync();
        if (_serverOrigin is null || _currentUser is null ||
            string.IsNullOrWhiteSpace(_tokenProvider.AccessToken))
        {
            return;
        }

        var client = new LibraryRealtimeClient(_httpClient, _serverOrigin, _tokenProvider);
        client.ChangeReceived += RealtimeClient_OnChangeReceived;
        client.Connected += RealtimeClient_OnConnected;
        _realtimeClient = client;
        await client.StartAsync();
    }

    private async Task StopRealtimeAsync()
    {
        _realtimeRefreshTimer.Stop();
        _pendingRealtimeScopes.Clear();
        _pendingRealtimeCurrentUserRefresh = false;
        var client = _realtimeClient;
        _realtimeClient = null;
        if (client is null)
        {
            return;
        }

        client.ChangeReceived -= RealtimeClient_OnChangeReceived;
        client.Connected -= RealtimeClient_OnConnected;
        await client.DisposeAsync();
    }

    private void RealtimeClient_OnConnected(object? sender, LibraryRealtimeConnectedEventArgs eventArgs)
    {
        if (!eventArgs.IsReconnect || !ReferenceEquals(sender, _realtimeClient))
        {
            return;
        }

        Dispatcher.UIThread.Post(() => _ = RefreshAfterRealtimeReconnectAsync(sender));
    }

    private void RealtimeClient_OnChangeReceived(object? sender, LibraryChangeReceivedEventArgs eventArgs)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(sender, _realtimeClient) || _currentUser is null)
            {
                return;
            }

            _pendingRealtimeScopes.Add(eventArgs.Notification.Scope);
            if (eventArgs.Notification.Scope is LibraryChangeScopes.Users or LibraryChangeScopes.Profiles &&
                (eventArgs.Notification.EntityId is null ||
                 eventArgs.Notification.EntityId == _currentUser.Id))
            {
                _pendingRealtimeCurrentUserRefresh = true;
            }

            _realtimeRefreshTimer.Stop();
            _realtimeRefreshTimer.Start();
        });
    }

    private async void RealtimeRefreshTimer_OnTick(object? sender, EventArgs eventArgs)
    {
        _realtimeRefreshTimer.Stop();
        if (_currentUser is null ||
            _pendingRealtimeScopes.Count == 0 && !_pendingRealtimeCurrentUserRefresh)
        {
            _pendingRealtimeScopes.Clear();
            _pendingRealtimeCurrentUserRefresh = false;
            return;
        }

        var scopes = _pendingRealtimeScopes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _pendingRealtimeScopes.Clear();
        var refreshCurrentUser = _pendingRealtimeCurrentUserRefresh;
        _pendingRealtimeCurrentUserRefresh = false;
        var expectedClient = _realtimeClient;
        var expectedUserId = _currentUser.Id;
        try
        {
            var refreshAssets = scopes.Overlaps(
            [
                LibraryChangeScopes.Assets,
                LibraryChangeScopes.Tags,
                LibraryChangeScopes.Derivatives
            ]);
            var refreshUsers = scopes.Overlaps(
            [
                LibraryChangeScopes.Users,
                LibraryChangeScopes.Profiles
            ]);

            if (refreshCurrentUser && _api is not null)
            {
                var currentUser = await _api.GetCurrentUserAsync();
                if (!IsCurrentRealtimeSession(expectedClient, expectedUserId) ||
                    currentUser.Id != expectedUserId)
                {
                    return;
                }

                ApplyCurrentUser(currentUser, refreshOwnProfile: false);
            }

            if (!IsCurrentRealtimeSession(expectedClient, expectedUserId))
            {
                return;
            }

            var tasks = new List<Task>();
            if (refreshAssets && SharedPage.IsVisible)
            {
                tasks.Add(RefreshCloudLibraryAsync());
            }
            else if (refreshUsers && SharedPage.IsVisible)
            {
                tasks.Add(RefreshCloudAssetsAsync());
            }

            if (refreshAssets && RecycleBinPage.IsVisible)
            {
                tasks.Add(RefreshMyRecycleBinAsync());
            }

            if ((refreshAssets || refreshUsers) && ProfilePage.IsVisible &&
                _displayedProfileUserId is not null)
            {
                tasks.Add(RefreshDisplayedProfileAsync());
            }

            if (refreshUsers && UsersPage.IsVisible)
            {
                tasks.Add(RefreshUsersAsync());
            }

            if (scopes.Contains(LibraryChangeScopes.Luts))
            {
                tasks.Add(ReloadLutsAsync());
                if (!refreshAssets && !refreshUsers && SharedPage.IsVisible &&
                    _cloudCategoryFilterIndex == 5)
                {
                    tasks.Add(RefreshCloudAssetsAsync());
                }

                if (!refreshAssets && !refreshUsers && ProfilePage.IsVisible &&
                    _displayedProfileUserId is not null)
                {
                    tasks.Add(RefreshDisplayedProfileAssetsAsync());
                }
            }

            if (SharedPage.IsVisible && scopes.Contains(LibraryChangeScopes.Markers) &&
                _selectedCloudAsset is { } selectedCloudAsset)
            {
                tasks.Add(RefreshCloudMarkerSummaryAsync(selectedCloudAsset.Asset));
            }

            await Task.WhenAll(tasks);
        }
        catch (Exception exception)
        {
            if (!IsCurrentRealtimeSession(expectedClient, expectedUserId))
            {
                return;
            }

            if (await HandleSessionFailureAsync(exception))
            {
                return;
            }

            UiLocalization.SetText(
                CloudSummaryText,
                "实时刷新失败：{0}",
                UserMessage(exception));
        }
    }

    private async Task RefreshAfterRealtimeReconnectAsync(object? expectedClient)
    {
        if (!ReferenceEquals(expectedClient, _realtimeClient) ||
            _api is null || _currentUser is null)
        {
            return;
        }

        var expectedUserId = _currentUser.Id;
        try
        {
            var currentUser = await _api.GetCurrentUserAsync();
            if (!IsCurrentRealtimeSession(expectedClient, expectedUserId) ||
                currentUser.Id != expectedUserId)
            {
                return;
            }

            ApplyCurrentUser(currentUser, refreshOwnProfile: false);

            var tasks = new List<Task> { ReloadLutsAsync() };
            if (SharedPage.IsVisible)
            {
                tasks.Add(RefreshCloudLibraryAsync());
            }

            if (UsersPage.IsVisible)
            {
                tasks.Add(RefreshUsersAsync());
            }

            if (RecycleBinPage.IsVisible)
            {
                tasks.Add(RefreshMyRecycleBinAsync());
            }

            if (ProfilePage.IsVisible && _displayedProfileUserId is not null)
            {
                tasks.Add(RefreshDisplayedProfileAsync());
            }

            if (SharedPage.IsVisible && _selectedCloudAsset is { } selectedCloudAsset)
            {
                tasks.Add(RefreshCloudMarkerSummaryAsync(selectedCloudAsset.Asset));
            }

            await Task.WhenAll(tasks);
        }
        catch (Exception exception)
        {
            if (!IsCurrentRealtimeSession(expectedClient, expectedUserId))
            {
                return;
            }

            if (await HandleSessionFailureAsync(exception))
            {
                return;
            }

            UiLocalization.SetText(
                CloudSummaryText,
                "重连后的补偿刷新失败：{0}",
                UserMessage(exception));
        }
    }

    private async Task RefreshDisplayedProfileAsync()
    {
        if (!ProfilePage.IsVisible || _api is null || _displayedProfileUserId is null)
        {
            return;
        }

        var requestedUserId = _displayedProfileUserId.Value;
        var requestVersion = Volatile.Read(ref _profilePageRequestVersion);
        if (requestedUserId == _currentUser?.Id)
        {
            var current = await _api.GetCurrentUserAsync();
            if (!ProfilePage.IsVisible ||
                requestVersion != Volatile.Read(ref _profilePageRequestVersion) ||
                _displayedProfileUserId != requestedUserId ||
                current.Id != requestedUserId)
            {
                return;
            }

            ApplyCurrentUser(current);
            ShowCurrentProfile();
            return;
        }

        var profile = await _api.GetUserAsync(requestedUserId);
        if (!ProfilePage.IsVisible ||
            requestVersion != Volatile.Read(ref _profilePageRequestVersion) ||
            _displayedProfileUserId != requestedUserId || profile.Id != requestedUserId)
        {
            return;
        }

        _publicProfileSnapshot = profile;
        ProfileTitleText.Text = string.IsNullOrWhiteSpace(profile.DisplayName)
            ? profile.Username
            : profile.DisplayName;
        ProfileSubtitleText.Text = $"@{profile.Username}";
        if (string.IsNullOrWhiteSpace(profile.Bio))
        {
            UiLocalization.SetText(ProfileHeaderBioText, "这个人还没有写个人简介。");
        }
        else
        {
            ProfileHeaderBioText.Text = profile.Bio;
        }

        UiLocalization.SetText(
            ProfileHeaderRoleText,
            profile.IsEnabled ? "团队成员" : "已停用账号");
        PopulateProfileFields(
            profile.Username,
            profile.DisplayName,
            profile.Bio,
            profile.Birthday,
            profile.Gender,
            profile.CustomGender,
            profile.Contact);
        await LoadProfileAvatarAsync(profile.Id, profile.HasAvatar);
        if (!ProfilePage.IsVisible ||
            requestVersion != Volatile.Read(ref _profilePageRequestVersion) ||
            _displayedProfileUserId != requestedUserId)
        {
            return;
        }

        SetProfileEditable(false);
        UpdateProfileAssetCounts(profile.AssetCounts);
        await LoadProfileAssetsAsync(profile.Id);
    }

    private bool IsCurrentRealtimeSession(object? expectedClient, Guid expectedUserId) =>
        ReferenceEquals(expectedClient, _realtimeClient) && _currentUser?.Id == expectedUserId;

    private void RefreshRealtimeLocalization()
    {
        if (_publicProfileSnapshot is not { } profile ||
            _displayedProfileUserId == _currentUser?.Id)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(profile.Bio))
        {
            UiLocalization.SetText(ProfileHeaderBioText, "这个人还没有写个人简介。");
        }

        UiLocalization.SetText(
            ProfileHeaderRoleText,
            profile.IsEnabled ? "团队成员" : "已停用账号");
    }
}
