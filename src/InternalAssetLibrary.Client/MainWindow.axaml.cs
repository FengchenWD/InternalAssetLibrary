using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using InternalAssetLibrary.Contracts;
using InternalAssetLibrary.Client.Core.Auth;
using InternalAssetLibrary.Client.Core.Downloads;
using InternalAssetLibrary.Client.Core.Http;
using InternalAssetLibrary.Client.Core.LocalAssets;
using InternalAssetLibrary.Client.Core.Markers;
using InternalAssetLibrary.Client.Core.MediaAnalysis;
using InternalAssetLibrary.Client.Core.Playback;
using InternalAssetLibrary.Client.Core.Profiles;
using InternalAssetLibrary.Client.Core.Settings;
using InternalAssetLibrary.Client.Core.Tags;
using InternalAssetLibrary.Client.Core.Transfers;
using InternalAssetLibrary.Client.Core.Updates;
using InternalAssetLibrary.Client.Services;
using InternalAssetLibrary.Client.ViewModels;

namespace InternalAssetLibrary.Client;

public sealed partial class MainWindow : Window
{
    private const double NavigationSidebarBreakpoint = 1_180;
    private const double LocalFolderSidebarBreakpoint = 1_280;
    private static readonly TimeSpan ShutdownStepTimeout = TimeSpan.FromSeconds(3);
    private static readonly StringComparer LocalPathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private readonly JsonLocalAssetCatalogStore _catalogStore = new(AppPaths.CatalogFile);
    private readonly JsonClientSettingsStore _settingsStore = new(AppPaths.SettingsFile);
    private readonly LocalMarkerService _localMarkerService = new(AppPaths.LocalMarkersFile);
    private readonly PreviewPlaybackSession _previewSession = new();
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromMinutes(30) };
    private readonly StaticAccessTokenProvider _tokenProvider = new();
    private readonly ISessionTokenStore _tokenStore = SessionTokenStoreFactory.CreateDefault();
    private readonly IRememberedLoginPasswordStore _rememberedPasswordStore = new RememberedLoginPasswordStore();
    private readonly JsonPersistentDownloadRegistry _downloadRegistry = new(AppPaths.DownloadRegistryFile);
    private readonly JsonDirectUploadResumeStore _directUploadResumeStore = new(AppPaths.DirectUploadResumeFile);
    private readonly PersistentDownloadPathMapper _downloadPathMapper = new();
    private readonly StaticImageThumbnailService _thumbnailService = new(AppPaths.ThumbnailCacheDirectory);
    private readonly SemaphoreSlim _cloudUploadTransferGate = new(1, 1);
    private readonly SemaphoreSlim _cloudAssetOpenGate = new(1, 1);
    private readonly SemaphoreSlim _localDropOperationGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TimeSpan> _localDurationCache =
        new(LocalPathComparer);
    private readonly LocalAssetIndexService _indexService;
    private readonly DispatcherTimer _localStorageMonitorTimer;
    private TrayService? _trayService;
    private LocalAssetCatalog _catalog = LocalAssetCatalog.Empty;
    private ClientSettings _settings = new();
    private AssetCardViewModel? _selectedLocalAsset;
    private CloudAssetCardViewModel? _selectedCloudAsset;
    private AssetLibraryApiClient? _api;
    private CloudFileTransferService? _transferService;
    private CloudUploadPreprocessor? _cloudUploadPreprocessor;
    private CloudMediaDerivativeService? _cloudDerivativeService;
    private CloudTagLibraryService? _cloudTagLibraryService;
    private ClientUpdateService? _updateService;
    private Uri? _serverOrigin;
    private ApiCurrentUser? _currentUser;
    private ApiCurrentUser? _profileSnapshot;
    private ApiPublicUserProfile? _publicProfileSnapshot;
    private Guid? _displayedProfileUserId;
    private Bitmap? _profileAvatarBitmap;
    private Bitmap? _currentUserAvatarBitmap;
    private IReadOnlyList<ApiAsset> _cloudAssets = [];
    private IReadOnlyList<ApiPublicUserProfile> _users = [];
    private IReadOnlyList<ApiAsset> _profileAssetSource = [];
    private ApiAssetCategory? _profileAssetCategory;
    private bool _isCloudFilterLoading;
    private bool _isLocalFilterLoading;
    private bool _isProfileEditing;
    private int _localCategoryFilterIndex;
    private int _cloudCategoryFilterIndex;
    private int _cloudRequestVersion;
    private int _userRequestVersion;
    private int _recycleBinRequestVersion;
    private int _profilePageRequestVersion;
    private int _profileContentRequestVersion;
    private int _profileAvatarRequestVersion;
    private Point? _dragStart;
    private bool _isDragging;
    private int _systemDragActive;
    private Point? _uploadDropPointerStart;
    private bool _uploadDropPointerMoved;
    private bool _isDragStartPending;
    private int _dragStartOperationActive;
    private long _dragGestureVersion;
    private string? _preparedDragPath;
    private Task<IStorageFile?>? _preparedDragFile;
    private CancellationTokenSource? _dragPreparationCancellation;
    private string[]? _activeCloudDragPaths;
    private ApiAsset[]? _activeCloudDragAssets;
    private LocalAsset[]? _activeLocalDragAssets;
    private bool _isLoading = true;
    private int _localThumbnailGeneration;
    private int _cloudThumbnailGeneration;
    private int _localHoverPreviewGeneration;
    private Guid? _hoveredLocalAssetId;
    private CancellationTokenSource? _localThumbnailCancellation;
    private CancellationTokenSource? _cloudThumbnailCancellation;
    private CancellationTokenSource? _localHoverPreviewCancellation;
    private string? _hoverDetailSelectionKey;
    private CancellationTokenSource? _cloudRefreshCancellation;
    private CancellationTokenSource? _usersRefreshCancellation;
    private CancellationTokenSource? _recycleBinRefreshCancellation;
    private CancellationTokenSource? _profileContentRefreshCancellation;
    private CancellationTokenSource? _cloudAssetOpenCancellation;
    private Bitmap? _localHoverPreviewBitmap;
    private Bitmap? _detailArtworkBitmap;
    private CancellationTokenSource? _detailArtworkCancellation;
    private int _detailArtworkGeneration;
    private CancellationTokenSource? _themeTransitionCancellation;
    private CancellationTokenSource? _sharedUploadDragLeaveCancellation;
    private bool _isSidebarExpanded = true;
    private bool _isDetailSidebarExpanded = true;
    private double _expandedLocalFolderWidth = 232;
    private double _expandedDetailWidth = 320;
    private bool _isNavigationSidebarNarrow;
    private string _activePage = "local";
    private Guid? _selectedLocalFolderId;
    private string? _selectedLocalRelativeDirectoryPath;
    private bool _suppressLocalFolderSelectionChanged;
    private bool _isCheckingLocalStorage;
    private bool _isUpdatingCloudSelection;
    private bool _isCloudBatchOperationRunning;
    private bool _isUpdatingCloudLutSelection;
    private bool _isCloudLutBatchOperationRunning;
    private bool _isSavingServerAddress;
    private bool _isServerAddressVisible;
    private bool _isCheckingForUpdates;
    private bool _adaptiveCardWidthUpdatePending;
    private bool _shutdownStarted;
    private bool _shutdownCompleted;
    private bool _closeDecisionInProgress;
    private bool _exitRequested;
    private bool _isApplyingCloseBehaviorSettings;
    private bool _profileOpenedFromUsers;
    private bool _localFiltersExpanded;
    private bool _cloudFiltersExpanded;
    private bool _filterLayoutInitialized;
    private bool _lastWindowWasMaximized;
    private LocalFolderNodeViewModel? _pendingLocalFolderRemoval;

    public MainWindow()
    {
        _indexService = new LocalAssetIndexService(_catalogStore);
        _localStorageMonitorTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _localStorageMonitorTimer.Tick += LocalStorageMonitor_OnTick;
        InitializeComponent();
        DataContext = this;
        if (Application.Current?.PlatformSettings is { } platformSettings)
        {
            platformSettings.ColorValuesChanged += PlatformSettings_OnColorValuesChanged;
        }
        EditorWorkspace.Configure(_localMarkerService, () => _catalog);
        AdminWorkspace.Configure(() => _api, () => _serverOrigin, NavigateFromAdmin);
        UiLocalization.Register(this, static window => window.RefreshMainWindowLocalization());
        UiLocalization.Apply(this, UiLanguage.ChineseSimplified);
        AuthorNameText.Text = string.Concat((char)0x98CE, (char)0x5C18, "WD");
        InitializeTray();
        InitializePlaybackUi();
        InitializeRealtimeUi();
        LocalAssetScroll.SizeChanged += (_, _) => ScheduleAdaptiveAssetCardWidthUpdate();
        CloudAssetScroll.SizeChanged += (_, _) => ScheduleAdaptiveAssetCardWidthUpdate();
        CloudContentsGrid.SizeChanged += (_, _) => ApplyCloudFolderHeight();
        CloudFolderHeightSplitter.AddHandler(InputElement.PointerReleasedEvent,
            CloudFolderHeight_OnReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        UiLocalization.SetText(ClientVersionText, "版本 {0}", GetClientVersion());
        SetSidebarExpanded(true);
        SetFilterPanelsExpanded(WindowState == WindowState.Maximized);
        Opened += OnOpened;
        Closing += OnClosing;
        SizeChanged += MainWindow_OnSizeChanged;
        Activated += (_, _) => ResetPendingDragGesture();
        Deactivated += (_, _) => ResetPendingDragGesture();
        PointerCaptureLost += (_, _) => ResetPendingDragGesture();
    }

    private void PlatformSettings_OnColorValuesChanged(object? sender, Avalonia.Platform.PlatformColorValues eventArgs)
    {
        if ((_settings.Theme == ThemePreference.System || _settings.FollowSystemAccent) && !_shutdownStarted)
        {
            Dispatcher.UIThread.Post(() => ApplyTheme(_settings.Theme));
        }
    }

    private void InitializeTray()
    {
        if (Application.Current is not { } application || Icon is null)
        {
            return;
        }

        try
        {
            _trayService = new TrayService(application, this, Icon);
            _trayService.ExitRequested += TrayService_OnExitRequested;
            UpdateTrayLocalization();
        }
        catch (Exception exception)
        {
            ClientDiagnostics.WriteException("tray-initialization", exception, isFatal: false);
            _trayService?.Dispose();
            _trayService = null;
        }
    }

    private void TrayService_OnExitRequested(object? sender, EventArgs eventArgs) =>
        RequestApplicationExit();

    private void RequestApplicationExit()
    {
        if (_shutdownStarted || _shutdownCompleted || _closeDecisionInProgress)
        {
            return;
        }

        _exitRequested = true;
        if (Dispatcher.UIThread.CheckAccess())
        {
            Close();
        }
        else
        {
            Dispatcher.UIThread.Post(Close);
        }
    }

    private void UpdateTrayLocalization() => _trayService?.UpdateText(
        UiLocalization.Text("云汀素材管理工具"),
        UiLocalization.Text("显示主窗口"),
        UiLocalization.Text("关闭软件"));

    public ObservableCollection<AssetCardViewModel> VisibleLocalAssets { get; } = [];

    public ObservableCollection<LocalFolderNodeViewModel> LocalFolderNodes { get; } = [];

    public ObservableCollection<LocalFolderNodeViewModel> CompactLocalFolderNodes { get; } = [];

    public ObservableCollection<CloudAssetCardViewModel> VisibleCloudAssets { get; } = [];

    public ObservableCollection<UserCardViewModel> VisibleUsers { get; } = [];

    public ObservableCollection<TransferItemViewModel> TransferItems { get; } = [];

    public ObservableCollection<CloudTagFilterItemViewModel> CloudTagFilters { get; } =
    [
        new("全部标签", isAll: true, isSelected: true)
    ];

    public ObservableCollection<CloudTagFilterItemViewModel> LocalTagFilters { get; } =
    [
        new("全部标签", isAll: true, isSelected: true)
    ];

    public ObservableCollection<CloudAssetCardViewModel> ProfileAssets { get; } = [];

    public ObservableCollection<RecycleBinAssetViewModel> MyRecycleBinAssets { get; } = [];

    private sealed record ServerHealthResponse(string Status);

    private static string GetClientVersion()
    {
        var informationalVersion = typeof(MainWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        return string.IsNullOrWhiteSpace(informationalVersion)
            ? UiLocalization.Text("未知")
            : informationalVersion.Split('+', 2)[0];
    }

    private async void OnOpened(object? sender, EventArgs eventArgs)
    {
        try
        {
            _settings = await _settingsStore.LoadAsync();
            _catalog = await _indexService.GetCatalogAsync();
            ApplySettingsToControls();
            ApplyTheme(_settings.Theme);
            RebuildLocalFolderTree();
            ApplyLocalFilter();
            ConfigureApi();
        }
        catch (Exception exception)
        {
            UiLocalization.SetText(LocalSummaryText, "读取本地数据失败：{0}", exception.Message);
        }
        finally
        {
            _isLoading = false;
        }

        try
        {
            await InitializePlaybackAfterSettingsAsync();
        }
        catch (Exception exception)
        {
            ClientDiagnostics.WriteException("playback-startup", exception, isFatal: false);
            UiLocalization.SetText(PlayerStatusText, "播放器初始化失败：{0}", exception.Message);
        }

        await HandleUpdateStartupAsync();

        try
        {
            await CheckLocalStorageAvailabilityAsync();
            _localStorageMonitorTimer.Start();
        }
        catch (Exception exception)
        {
            ClientDiagnostics.WriteException("local-storage-startup", exception, isFatal: false);
            UiLocalization.SetText(LocalSummaryText, "读取本地数据失败：{0}", exception.Message);
        }

        try
        {
            await TryRestoreSessionAsync();
        }
        catch (Exception exception)
        {
            ClientDiagnostics.WriteException("session-restore-startup", exception, isFatal: false);
            await SetLoggedOutStateAsync("服务器暂时不可用：{0}", UserMessage(exception));
        }

        if (_currentUser is not null)
        {
            await CheckForUpdatesAsync(showUpToDateMessage: false);
        }
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs eventArgs)
    {
        if (_shutdownCompleted)
        {
            return;
        }

        eventArgs.Cancel = true;
        if (_shutdownStarted)
        {
            return;
        }

        if (_closeDecisionInProgress)
        {
            return;
        }

        _closeDecisionInProgress = true;
        try
        {
            var forceExit = _exitRequested ||
                            eventArgs.CloseReason is WindowCloseReason.ApplicationShutdown or
                                WindowCloseReason.OSShutdown;
            var decision = forceExit
                ? CloseRequestDecision.ExitApplication
                : ClientCloseBehavior.Decide(_settings);
            if (decision == CloseRequestDecision.ShowPrompt)
            {
                var selection = await new CloseActionPromptWindow(_settings.CloseAction)
                    .ShowDialog<ClosePromptSelection?>(this);
                if (selection is null)
                {
                    return;
                }

                var resolution = ClientCloseBehavior.ResolvePrompt(_settings, selection);
                decision = resolution.Decision;
                if (resolution.ShouldSaveSettings)
                {
                    var previousSettings = _settings;
                    _settings = resolution.Settings;
                    ApplyCloseBehaviorSettingsToControls();
                    try
                    {
                        await _settingsStore.SaveAsync(_settings);
                    }
                    catch (Exception exception)
                    {
                        _settings = previousSettings;
                        ApplyCloseBehaviorSettingsToControls();
                        ClientDiagnostics.WriteException(
                            "remember-close-behavior",
                            exception,
                            isFatal: false);
                        UiLocalization.SetText(
                            CloseBehaviorStatusText,
                            "关闭选项保存失败，下次仍会询问：{0}",
                            exception.Message);
                    }
                }
            }

            if (decision == CloseRequestDecision.MinimizeToTray)
            {
                _exitRequested = false;
                if (_trayService is not null)
                {
                    _trayService.MinimizeToTray();
                }
                else
                {
                    WindowState = WindowState.Minimized;
                }

                return;
            }

            await ShutdownAsync();
        }
        catch (Exception exception)
        {
            _exitRequested = false;
            ClientDiagnostics.WriteException("close-decision", exception, isFatal: false);
        }
        finally
        {
            _closeDecisionInProgress = false;
        }
    }

    private async Task ShutdownAsync()
    {
        if (_shutdownStarted || _shutdownCompleted)
        {
            return;
        }

        _shutdownStarted = true;
        ResetPendingDragGesture();
        _localBatchOperationCancellation?.Cancel();
        try
        {
            await StopTransfersAsync();
            if (Application.Current?.PlatformSettings is { } platformSettings)
            {
                platformSettings.ColorValuesChanged -= PlatformSettings_OnColorValuesChanged;
            }
            _localStorageMonitorTimer.Stop();
            CancelLatestRefresh(ref _cloudRefreshCancellation);
            CancelLatestRefresh(ref _usersRefreshCancellation);
            CancelLatestRefresh(ref _recycleBinRefreshCancellation);
            CancelLatestRefresh(ref _profileContentRefreshCancellation);
            _cloudAssetOpenCancellation?.Cancel();
            CloseLocalImagePreview();
            ClearDetailArtwork();
            CancelLocalThumbnailLoading();
            CancelCloudThumbnailLoading();
            _themeTransitionCancellation?.Cancel();
            var realtimeShutdown = StopRealtimeAsync();
            var playbackShutdown = DisposePlaybackAsync();
            var editorShutdown = EditorWorkspace.ShutdownAsync();
            var settingsSave = SaveSettingsAsync();

            await Task.WhenAll(
                CompleteShutdownStepAsync(realtimeShutdown, "realtime"),
                CompleteShutdownStepAsync(playbackShutdown, "playback"),
                CompleteShutdownStepAsync(editorShutdown, "editor"),
                CompleteShutdownStepAsync(settingsSave, "settings"));

            _indexService.Dispose();
            _localMarkerService.Dispose();
            _catalogStore.Dispose();
            _settingsStore.Dispose();
            _downloadRegistry.Dispose();
            _directUploadResumeStore.Dispose();
            foreach (var resume in _accountResumeStores.Values) resume.Dispose();
            DisposeLocalAssetCards();
            DisposeCloudAssetCards();
            ClearSharedCloudLuts();
            DisposeProfileAssetCards();
            DisposeUserCards();
            _thumbnailService.Dispose();
            ProfileAvatarImage.Source = null;
            CurrentUserAvatarImage.Source = null;
            _profileAvatarBitmap?.Dispose();
            _currentUserAvatarBitmap?.Dispose();
            _httpClient.Dispose();
        }
        catch (Exception exception)
        {
            ClientDiagnostics.WriteException("shutdown-cleanup", exception, isFatal: false);
        }
        finally
        {
            try
            {
                if (_trayService is not null)
                {
                    _trayService.ExitRequested -= TrayService_OnExitRequested;
                    _trayService.Dispose();
                    _trayService = null;
                }
            }
            catch (Exception exception)
            {
                ClientDiagnostics.WriteException("tray-shutdown", exception, isFatal: false);
            }

            _shutdownCompleted = true;
            Dispatcher.UIThread.Post(() => Close());
        }
    }

    private static async Task CompleteShutdownStepAsync(Task task, string operation)
    {
        try
        {
            await task.WaitAsync(ShutdownStepTimeout);
        }
        catch (TimeoutException exception)
        {
            ClientDiagnostics.WriteException($"shutdown-{operation}-timeout", exception, isFatal: false);
            ObserveLateShutdownFailure(task, operation);
        }
        catch (Exception exception)
        {
            ClientDiagnostics.WriteException($"shutdown-{operation}", exception, isFatal: false);
        }
    }

    private static void ObserveLateShutdownFailure(Task task, string operation)
    {
        _ = task.ContinueWith(
            completed => ClientDiagnostics.WriteException(
                $"shutdown-{operation}-late",
                completed.Exception!.GetBaseException(),
                isFatal: false),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void Navigation_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not Button { Tag: string page })
        {
            return;
        }

        if (page == "profile")
        {
            SetProfileOpenedFromUsers(false);
        }

        NavigateTo(page);
    }

    private void SidebarToggle_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        SetSidebarExpanded(!_isSidebarExpanded);
    }

    private void SetSidebarExpanded(bool expanded)
    {
        _isSidebarExpanded = expanded;
        NavigationSidebar.Width = expanded ? 216 : 64;
        SidebarToggleButton.Classes.Set("collapsed", !expanded);
        SidebarToggleGlyph.Text = expanded ? "‹" : "›";
        UiLocalization.SetText(SidebarToggleLabel, expanded ? "收起侧栏" : "展开侧栏");
        ToolTip.SetTip(
            SidebarToggleButton,
            UiLocalization.Text(expanded ? "收起侧栏" : "展开侧栏"));
    }

    private void MainWindow_OnSizeChanged(object? sender, SizeChangedEventArgs eventArgs)
    {
        var isMaximized = WindowState == WindowState.Maximized;
        if (!_filterLayoutInitialized || isMaximized != _lastWindowWasMaximized)
        {
            SetFilterPanelsExpanded(isMaximized);
        }

        var isNarrow = Bounds.Width < NavigationSidebarBreakpoint;
        if (isNarrow != _isNavigationSidebarNarrow)
        {
            _isNavigationSidebarNarrow = isNarrow;
            SetSidebarExpanded(!isNarrow);
        }

        UpdateLocalFolderSidebarLayout();
        ScheduleAdaptiveAssetCardWidthUpdate();
    }

    private void SetFilterPanelsExpanded(bool expanded)
    {
        _localFiltersExpanded = expanded;
        _cloudFiltersExpanded = expanded;
        _lastWindowWasMaximized = WindowState == WindowState.Maximized;
        _filterLayoutInitialized = true;
        LocalFilterToggleButton.IsChecked = expanded;
        CloudFilterToggleButton.IsChecked = expanded;
        UiLocalization.SetContent(LocalFilterToggleButton, expanded ? "收起筛选" : "展开筛选");
        UiLocalization.SetContent(CloudFilterToggleButton, expanded ? "收起筛选" : "展开筛选");
        LocalCategoryFilterPanel.IsVisible = expanded;
        LocalSearchBox.IsVisible = expanded;
        LocalTagFilterPanel.IsVisible = expanded;
        LocalDateFilterPanel.IsVisible = expanded;
        CloudCategoryFilterPanel.IsVisible = expanded;
        ApplyCloudAssetDisplayMode();
    }

    private void LocalFilterToggle_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        _localFiltersExpanded = LocalFilterToggleButton.IsChecked == true;
        UiLocalization.SetContent(LocalFilterToggleButton, _localFiltersExpanded ? "收起筛选" : "展开筛选");
        LocalCategoryFilterPanel.IsVisible = _localFiltersExpanded;
        LocalSearchBox.IsVisible = _localFiltersExpanded;
        LocalTagFilterPanel.IsVisible = _localFiltersExpanded;
        LocalDateFilterPanel.IsVisible = _localFiltersExpanded;
    }

    private void CloudFilterToggle_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        _cloudFiltersExpanded = CloudFilterToggleButton.IsChecked == true;
        UiLocalization.SetContent(CloudFilterToggleButton, _cloudFiltersExpanded ? "收起筛选" : "展开筛选");
        CloudCategoryFilterPanel.IsVisible = _cloudFiltersExpanded;
        ApplyCloudAssetDisplayMode();
    }

    private ColumnDefinition LocalFolderColumn => MainContentGrid.ColumnDefinitions[1];

    private ColumnDefinition LocalFolderSplitterColumn => MainContentGrid.ColumnDefinitions[2];

    private ColumnDefinition DetailSplitterColumn => MainContentGrid.ColumnDefinitions[4];

    private ColumnDefinition DetailColumn => MainContentGrid.ColumnDefinitions[5];

    private async void LocalFolderSidebarToggle_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        _settings = _settings with
        {
            LocalFolderSidebarExpanded = !_settings.LocalFolderSidebarExpanded
        };
        UpdateLocalFolderSidebarLayout();
        if (!_isLoading)
        {
            try
            {
                await SaveSettingsAsync();
            }
            catch (Exception exception)
            {
                UiLocalization.SetText(LocalSummaryText, "文件夹栏设置保存失败：{0}", exception.Message);
            }
        }
    }

    private void UpdateLocalFolderSidebarLayout()
    {
        var isLocalPage = _activePage == "local";
        var isCloudPage = _activePage == "shared";
        if ((LocalFolderSidebar.IsVisible || CloudFolderSidebar.IsVisible) &&
            LocalFolderColumn.ActualWidth >= 180)
        {
            _expandedLocalFolderWidth = Math.Clamp(LocalFolderColumn.ActualWidth, 180, 420);
        }

        LocalFolderSidebar.IsVisible = isLocalPage;
        CloudFolderSidebar.IsVisible = isCloudPage;
        if (!isLocalPage && !isCloudPage)
        {
            LocalFolderSplitter.IsVisible = false;
            LocalFolderColumn.MinWidth = 0;
            LocalFolderColumn.MaxWidth = 0;
            LocalFolderColumn.Width = new GridLength(0);
            LocalFolderSplitterColumn.Width = new GridLength(0);
            return;
        }

        var isNarrow = Bounds.Width < LocalFolderSidebarBreakpoint;
        var expanded = _settings.LocalFolderSidebarExpanded && !isNarrow;
        LocalFolderColumn.MinWidth = expanded ? 180 : 64;
        LocalFolderColumn.MaxWidth = expanded ? 420 : 64;
        LocalFolderColumn.Width = new GridLength(expanded ? _expandedLocalFolderWidth : 64);
        LocalFolderSplitterColumn.Width = new GridLength(expanded ? 10 : 0);
        LocalFolderSplitter.IsVisible = expanded;
        LocalFolderTree.IsVisible = expanded;
        // 隐藏整个滚动容器；空 ScrollViewer 仍参与命中，会挡住下方的紧凑图标。
        ExpandedLocalFolderContent.IsVisible = expanded;
        LocalFolderSidebar.Padding = new Thickness(expanded ? 8 : 0);
        CompactLocalFolderList.IsVisible = !expanded;
        LocalFolderDropTarget.IsVisible = expanded;
        LocalFileImportDropTarget.IsVisible = expanded;
        ExpandedCloudFolderContent.IsVisible = expanded;
        CompactCloudFolderList.IsVisible = !expanded;
        LocalFolderSidebarToggleButton.Classes.Set("collapsed", !expanded);
        CloudFolderSidebarToggleButton.Classes.Set("collapsed", !expanded);
        LocalFolderSidebarToggleGlyph.Text = expanded ? "‹" : "›";
        CloudFolderSidebarToggleGlyph.Text = expanded ? "‹" : "›";
        var toggleTip = UiLocalization.Text(
            expanded
                ? "收起文件夹栏"
                : isNarrow && _settings.LocalFolderSidebarExpanded
                    ? "窗口变宽后自动展开文件夹栏"
                    : "展开文件夹栏");
        ToolTip.SetTip(LocalFolderSidebarToggleButton, toggleTip);
        ToolTip.SetTip(CloudFolderSidebarToggleButton, toggleTip);
    }

    private void DetailSidebarToggle_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_isDetailSidebarExpanded && DetailColumn.ActualWidth >= 260)
        {
            _expandedDetailWidth = Math.Clamp(DetailColumn.ActualWidth, 260, 520);
        }

        _isDetailSidebarExpanded = !_isDetailSidebarExpanded;
        UpdateDetailSidebarLayout(_activePage is "local" or "shared");
    }

    private void UpdateDetailSidebarLayout(bool showDetail)
    {
        if (DetailSidebar.IsVisible && _isDetailSidebarExpanded && DetailColumn.ActualWidth >= 260)
        {
            _expandedDetailWidth = Math.Clamp(DetailColumn.ActualWidth, 260, 520);
        }

        var expanded = showDetail && _isDetailSidebarExpanded;
        DetailExpandButton.IsVisible = showDetail && !expanded;
        DetailExpandedContent.IsVisible = expanded;
        DetailSidebar.IsVisible = expanded;
        DetailSidebar.IsHitTestVisible = expanded;
        DetailSidebar.Opacity = expanded ? 1 : 0;
        if (!expanded)
        {
            DetailSplitter.IsVisible = false;
            DetailColumn.MinWidth = 0;
            DetailColumn.MaxWidth = 0;
            DetailColumn.Width = new GridLength(0);
            DetailSplitterColumn.Width = new GridLength(0);
            return;
        }

        DetailColumn.MinWidth = 260;
        DetailColumn.MaxWidth = 520;
        DetailColumn.Width = new GridLength(_expandedDetailWidth);
        DetailSplitterColumn.Width = new GridLength(10);
        DetailSplitter.IsVisible = true;
    }

    private void CurrentUserAvatar_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        SetProfileOpenedFromUsers(false);
        NavigateTo("profile");
    }

    private void NavigateTo(string page)
    {
        ResetPendingDragGesture();
        DiscardHoverDetailSelection();
        if (page == _activePage)
        {
            if (page == "profile" && _currentUser is not null && !_profileOpenedFromUsers)
            {
                Interlocked.Increment(ref _profilePageRequestVersion);
                CancelLatestRefresh(ref _profileContentRefreshCancellation);
                ShowCurrentProfile();
            }

            return;
        }

        if (page != "profile")
        {
            SetProfileOpenedFromUsers(false);
        }

        Interlocked.Increment(ref _profilePageRequestVersion);
        CancelPageRefresh(_activePage);
        if (_activePage == "shared" && page != "shared")
        {
            _cloudAssetOpenCancellation?.Cancel();
        }

        if (_activePage != page && _activePage is "local" or "shared")
        {
            ObserveNavigationTask(
                StopDetailPreviewAsync(savePosition: true),
                "stop-detail-preview");
        }

        if (_activePage == "player" && page != "player")
        {
            PausePlayerWhenLeavingPage();
        }

        if (_activePage == "editor" && page != "editor")
        {
            EditorWorkspace.PauseWhenHidden();
        }

        if (page != "local")
        {
            CloseLocalImagePreview();
        }

        _activePage = page;
        LocalPage.IsVisible = page == "local";
        SharedPage.IsVisible = page == "shared";
        PlayerPage.IsVisible = page == "player";
        EditorPage.IsVisible = page == "editor";
        TransfersPage.IsVisible = page == "transfers";
        UsersPage.IsVisible = page == "users";
        ProfilePage.IsVisible = page == "profile";
        RecycleBinPage.IsVisible = page == "recycle";
        AdminWorkspace.IsVisible = page == "admin";
        SettingsPage.IsVisible = page == "settings";
        UpdateLocalFolderSidebarLayout();
        if (page == "local" && VisibleLocalAssets.Count > 0)
        {
            StartLocalThumbnailLoading();
        }

        var showDetail = page is "local" or "shared";
        UpdateDetailSidebarLayout(showDetail);

        var selectedNavigationPage = page == "profile" && _profileOpenedFromUsers
            ? "users"
            : page;
        foreach (var button in new[]
                 {
                     LocalNav, SharedNav, PlayerNav, EditorNav, TransfersNav, UsersNav,
                     ProfileNav, RecycleBinNav, AdminNav, SettingsNav
                 })
        {
            button.Classes.Set("selected", Equals(button.Tag, selectedNavigationPage));
        }

        if (_currentUser is not null)
        {
            if (page == "shared")
            {
                ObserveNavigationTask(RefreshCloudLibraryAsync(), "refresh-cloud-library");
            }
            else if (page == "users")
            {
                ObserveNavigationTask(RefreshUsersAsync(), "refresh-users");
            }
            else if (page == "admin")
            {
                ObserveNavigationTask(AdminWorkspace.RefreshAsync(), "refresh-admin");
            }
            else if (page == "profile")
            {
                ShowCurrentProfile();
            }
            else if (page == "recycle")
            {
                ObserveNavigationTask(RefreshMyRecycleBinAsync(), "refresh-recycle-bin");
            }
        }
    }

    private async void OpenEditor_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        var assets = _isLocalBatchMode && _selectedLocalAsset is { IsBatchSelected: true }
            ? VisibleLocalAssets.Where(asset => asset.IsBatchSelected)
                .Select(asset => _catalog.Assets.FirstOrDefault(item => item.Id == asset.Id))
                .OfType<LocalAsset>()
                .ToArray()
            : _selectedLocalAsset is { } selected
                ? _catalog.Assets.Where(asset => asset.Id == selected.Id).ToArray()
                : [];
        NavigateTo("editor");
        if (assets.Length == 0)
        {
            return;
        }

        try
        {
            await EditorWorkspace.ImportCatalogAssetsAsync(assets);
        }
        catch (Exception exception)
        {
            ClientDiagnostics.WriteException("open-editor-with-local-assets", exception, isFatal: false);
        }
    }

    private static void ObserveNavigationTask(Task task, string operation) =>
        _ = ObserveNavigationTaskAsync(task, operation);

    private static async Task ObserveNavigationTaskAsync(Task task, string operation)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ClientDiagnostics.WriteException($"navigation-{operation}", exception, isFatal: false);
        }
    }

    private void CancelPageRefresh(string page)
    {
        if (page == "local")
        {
            CancelLocalThumbnailLoading();
        }
        else if (page == "shared")
        {
            CancelLatestRefresh(ref _cloudRefreshCancellation);
            CancelCloudThumbnailLoading();
        }
        else if (page == "users")
        {
            CancelLatestRefresh(ref _usersRefreshCancellation);
        }
        else if (page == "recycle")
        {
            CancelLatestRefresh(ref _recycleBinRefreshCancellation);
        }
        else if (page == "profile")
        {
            CancelLatestRefresh(ref _profileContentRefreshCancellation);
            CancelCloudThumbnailLoading();
        }
    }

    private static CancellationTokenSource StartLatestRefresh(
        ref CancellationTokenSource? activeCancellation)
    {
        var cancellation = new CancellationTokenSource();
        Interlocked.Exchange(ref activeCancellation, cancellation)?.Cancel();
        return cancellation;
    }

    private static void FinishLatestRefresh(
        ref CancellationTokenSource? activeCancellation,
        CancellationTokenSource cancellation)
    {
        Interlocked.CompareExchange(ref activeCancellation, null, cancellation);
        cancellation.Dispose();
    }

    private static void CancelLatestRefresh(ref CancellationTokenSource? activeCancellation) =>
        Interlocked.Exchange(ref activeCancellation, null)?.Cancel();

    private void ConfigureApi()
    {
        PauseTransfers();
        _serverOrigin = new Uri(_settings.ServerAddress, UriKind.Absolute);
        var clientVersion = GetClientVersion();
        _api = new AssetLibraryApiClient(
            _httpClient,
            _serverOrigin,
            _tokenProvider,
            clientVersion: clientVersion);
        _updateService = new ClientUpdateService(_api, clientVersion);
        _transferService = new CloudFileTransferService(
            _api,
            _downloadPathMapper,
            _downloadRegistry,
            _directUploadResumeStore,
            durationProbe: async (path, cancellationToken) =>
            {
                var probe = await _mediaAnalyzer.ProbeAsync(path, cancellationToken);
                return probe.Succeeded ? probe.Information?.Duration?.TotalSeconds : null;
            });
        _cloudUploadPreprocessor = new CloudUploadPreprocessor(
            ResolveRuntimePath("INTERNAL_ASSET_LIBRARY_FFMPEG", AppPaths.FfmpegPath),
            ffprobeExecutable: ResolveRuntimePath("INTERNAL_ASSET_LIBRARY_FFPROBE", AppPaths.FfprobePath));
        _cloudDerivativeService = new CloudMediaDerivativeService(
            _api,
            _mediaThumbnailService,
            AppPaths.CloudDerivativeCacheDirectory,
            ResolveRuntimePath("INTERNAL_ASSET_LIBRARY_FFMPEG", AppPaths.FfmpegPath));
        _cloudTagLibraryService = new CloudTagLibraryService(_api);
        ServerAddressBox.Text = _settings.ServerAddress;
    }

    private async Task TryRestoreSessionAsync(bool serverReachable = false)
    {
        if (_api is null || _serverOrigin is null)
        {
            await SetLoggedOutStateAsync("服务器地址无效");
            return;
        }

        string? token;
        try
        {
            token = await _tokenStore.GetAsync(_serverOrigin);
        }
        catch (Exception exception)
        {
            await SetLoggedOutStateAsync(
                "无法读取系统凭据：{0}",
                VisibleNetworkMessage(exception.Message));
            return;
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            await SetLoggedOutStateAsync(
                serverReachable ? "服务器连接成功，请登录" : "服务器未登录");
            return;
        }

        _tokenProvider.AccessToken = token;
        UiLocalization.SetText(CloudConnectionText, "正在恢复登录...");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var user = await _api.GetCurrentUserAsync(timeout.Token);
            ApplyCurrentUser(user);
            await ReloadLutsAsync();
            await TouchSavedLoginAccountAsync(user);
            await RefreshCloudAndUsersAsync();
            await StartRealtimeAsync();
        }
        catch (AssetLibraryApiException exception) when (exception.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            await DeleteStoredTokenQuietlyAsync();
            await SetLoggedOutStateAsync("登录已失效，请重新登录");
        }
        catch (Exception exception)
        {
            await SetLoggedOutStateAsync(
                "服务器暂时不可用：{0}",
                true,
                UserMessage(exception));
        }
    }

    private async void ConnectServer_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        await ConnectServerSafelyAsync();
    }

    private async Task ConnectServerSafelyAsync()
    {
        try
        {
            await ConnectServerAsync();
        }
        catch (Exception exception)
        {
            ClientDiagnostics.WriteException("connect-server", exception, isFatal: false);
            UiLocalization.SetText(
                CloudSummaryText,
                "服务器连接失败：{0}",
                UserMessage(exception));
        }
    }

    private async Task ConnectServerAsync()
    {
        if (_api is null || _serverOrigin is null)
        {
            return;
        }

        await SuspendTransfersAsync();
        var result = await new LoginWindow(
                _api,
                _tokenProvider,
                _serverOrigin,
                _settings.SavedLoginAccounts,
                _rememberedPasswordStore,
                _currentUser?.Id,
                DeleteSavedLoginAccountAsync)
            .ShowDialog<LoginSessionResult?>(this);
        if (result is null)
        {
            return;
        }

        ApplyCurrentUser(result.User);
        await ReloadLutsAsync();
        await UpdateSavedLoginAccountAsync(result);
        try
        {
            await _tokenStore.SaveAsync(_serverOrigin, result.Token);
        }
        catch (Exception exception)
        {
            UiLocalization.SetText(
                CloudSummaryText,
                "本次已登录，但系统凭据保存失败：{0}",
                VisibleNetworkMessage(exception.Message));
        }

        await RefreshCloudAndUsersAsync();
        await StartRealtimeAsync();
        await CheckForUpdatesAsync(showUpToDateMessage: false);
    }

    private async void SwitchAccount_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        await ConnectServerSafelyAsync();
    }

    private async Task UpdateSavedLoginAccountAsync(LoginSessionResult result)
    {
        if (_serverOrigin is null)
        {
            return;
        }

        var account = new SavedLoginAccount(
            result.User.Id,
            result.User.Username,
            string.IsNullOrWhiteSpace(result.User.DisplayName)
                ? result.User.Username
                : result.User.DisplayName,
            result.RememberPassword,
            DateTimeOffset.UtcNow);
        var accounts = (_settings.SavedLoginAccounts ?? [])
            .Where(item => item.UserId != result.User.Id)
            .ToList();

        if (result.RememberAccount)
        {
            accounts.Add(account);
        }

        _settings = _settings with
        {
            SavedLoginAccounts = accounts
                .OrderByDescending(item => item.LastUsedAt)
                .Take(50)
                .ToArray()
        };

        try
        {
            if (result.RememberPassword)
            {
                await _rememberedPasswordStore.SaveAsync(_serverOrigin, result.User.Id, result.Password);
            }
            else
            {
                await _rememberedPasswordStore.DeleteAsync(_serverOrigin, result.User.Id);
            }
        }
        catch (Exception exception)
        {
            // The account itself may still be remembered; expose the credential failure in the UI.
            UiLocalization.SetText(
                CloudSummaryText,
                "登录成功，但记住密码失败：{0}",
                VisibleNetworkMessage(exception.Message));
            if (result.RememberPassword)
            {
                _settings = _settings with
                {
                    SavedLoginAccounts = _settings.SavedLoginAccounts
                        .Select(item => item.UserId == result.User.Id
                            ? item with { PasswordRemembered = false }
                            : item)
                        .ToArray()
                };
            }
        }

        try
        {
            await SaveSettingsAsync();
        }
        catch (Exception exception)
        {
            UiLocalization.SetText(
                CloudSummaryText,
                "登录成功，但账号记忆保存失败：{0}",
                VisibleNetworkMessage(exception.Message));
        }
    }

    private async Task DeleteSavedLoginAccountAsync(SavedLoginAccount account)
    {
        if (_serverOrigin is null)
        {
            throw new InvalidOperationException(UiLocalization.Text("服务器地址无效。"));
        }

        await _rememberedPasswordStore.DeleteAsync(_serverOrigin, account.UserId);
        var previousSettings = _settings;
        _settings = _settings with
        {
            SavedLoginAccounts = (_settings.SavedLoginAccounts ?? [])
                .Where(item => item.UserId != account.UserId)
                .ToArray()
        };

        try
        {
            await SaveSettingsAsync();
        }
        catch
        {
            _settings = previousSettings;
            throw;
        }
    }

    private async Task TouchSavedLoginAccountAsync(ApiCurrentUser user)
    {
        var existing = (_settings.SavedLoginAccounts ?? [])
            .FirstOrDefault(item => item.UserId == user.Id);
        if (existing is null)
        {
            return;
        }

        var displayName = string.IsNullOrWhiteSpace(user.DisplayName)
            ? user.Username
            : user.DisplayName;
        var updated = existing with
        {
            Username = user.Username,
            DisplayName = displayName,
            LastUsedAt = DateTimeOffset.UtcNow
        };
        _settings = _settings with
        {
            SavedLoginAccounts = (_settings.SavedLoginAccounts ?? [])
                .Select(item => item.UserId == user.Id ? updated : item)
                .ToArray()
        };
        try
        {
            await SaveSettingsAsync();
        }
        catch
        {
            // A stale display name does not invalidate the restored session.
        }
    }

    private async Task RefreshCloudAndUsersAsync()
    {
        var tasks = new List<Task>();
        if (SharedPage.IsVisible)
        {
            tasks.Add(RefreshCloudLibraryAsync());
        }

        if (UsersPage.IsVisible)
        {
            tasks.Add(RefreshUsersAsync());
        }

        await Task.WhenAll(tasks);
    }

    private async Task RefreshMyRecycleBinAsync()
    {
        if (_api is null || _currentUser is null)
        {
            return;
        }

        var requestVersion = Interlocked.Increment(ref _recycleBinRequestVersion);
        var expectedUserId = _currentUser.Id;
        var cancellation = StartLatestRefresh(ref _recycleBinRefreshCancellation);
        var cancellationToken = cancellation.Token;
        ClearMyRecycleBinButton.IsEnabled = false;
        UiLocalization.SetText(MyRecycleBinSummaryText, "正在读取回收站...");
        try
        {
            var assets = await _api.ListAllMyRecycleBinAsync(new ApiRecycleBinListQuery(
                Sort: ApiAssetSort.Name,
                Order: ApiSortOrder.Ascending), cancellationToken);
            if (requestVersion != Volatile.Read(ref _recycleBinRequestVersion) ||
                _currentUser?.Id != expectedUserId ||
                cancellationToken.IsCancellationRequested)
            {
                return;
            }

            MyRecycleBinAssets.Clear();
            foreach (var asset in assets)
            {
                MyRecycleBinAssets.Add(new RecycleBinAssetViewModel(asset));
            }

            MyRecycleBinEmptyText.IsVisible = MyRecycleBinAssets.Count == 0;
            ClearMyRecycleBinButton.IsEnabled = MyRecycleBinAssets.Count > 0;
            UiLocalization.SetText(MyRecycleBinSummaryText, "共 {0:N0} 个已删除素材", assets.Count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (requestVersion != Volatile.Read(ref _recycleBinRequestVersion) ||
                _currentUser?.Id != expectedUserId ||
                cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (await HandleSessionFailureAsync(exception) ||
                requestVersion != Volatile.Read(ref _recycleBinRequestVersion) ||
                _currentUser?.Id != expectedUserId ||
                cancellationToken.IsCancellationRequested)
            {
                return;
            }

            UiLocalization.SetText(
                MyRecycleBinSummaryText,
                "回收站读取失败：{0}",
                UserMessage(exception));
        }
        finally
        {
            FinishLatestRefresh(ref _recycleBinRefreshCancellation, cancellation);
        }
    }

    private async void RefreshMyRecycleBin_OnClick(object? sender, RoutedEventArgs eventArgs) =>
        await RefreshMyRecycleBinAsync();

    private async void RestoreMyRecycleBinAsset_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not Button { DataContext: RecycleBinAssetViewModel item } button || _api is null)
        {
            return;
        }

        button.IsEnabled = false;
        try
        {
            await _api.RestoreMyRecycleBinAssetAsync(item.Id);
        }
        catch (Exception exception)
        {
            UiLocalization.SetText(MyRecycleBinSummaryText, "恢复失败：{0}", UserMessage(exception));
            button.IsEnabled = true;
            return;
        }

        button.IsEnabled = true;
        await Task.WhenAll(RefreshMyRecycleBinAsync(), RefreshCloudAssetsAsync());
        try
        {
            await RefreshDisplayedProfileAssetsAsync();
        }
        catch (Exception exception)
        {
            UiLocalization.SetText(
                MyRecycleBinSummaryText,
                "素材已恢复，但个人资料刷新失败：{0}",
                UserMessage(exception));
        }
    }

    private async void PermanentlyDeleteMyRecycleBinAsset_OnClick(
        object? sender,
        RoutedEventArgs eventArgs)
    {
        if (sender is not Button { DataContext: RecycleBinAssetViewModel item } button || _api is null)
        {
            return;
        }

        var confirmed = await new MessageDialogWindow(
                "永久删除素材",
                UiLocalization.Format("“{0}”的原文件、旧版本和标记将无法恢复。", item.Name),
                "确认删除",
                "取消")
            .ShowDialog<bool>(this);
        if (!confirmed)
        {
            return;
        }

        button.IsEnabled = false;
        try
        {
            await _api.PermanentlyDeleteMyRecycleBinAssetAsync(item.Id);
        }
        catch (Exception exception)
        {
            UiLocalization.SetText(
                MyRecycleBinSummaryText,
                "永久删除失败：{0}",
                UserMessage(exception));
            button.IsEnabled = true;
            return;
        }

        button.IsEnabled = true;
        await RefreshMyRecycleBinAsync();
        try
        {
            await RefreshDisplayedProfileAssetsAsync();
        }
        catch (Exception exception)
        {
            UiLocalization.SetText(
                MyRecycleBinSummaryText,
                "素材已永久删除，但个人资料刷新失败：{0}",
                UserMessage(exception));
        }
    }

    private async void ClearMyRecycleBin_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_api is null || MyRecycleBinAssets.Count == 0)
        {
            return;
        }

        var confirmed = await new MessageDialogWindow(
                "清空我的回收站",
                UiLocalization.Format(
                    "将永久删除回收站中的 {0:N0} 个素材，包括原文件、旧版本和标记。此操作无法恢复。",
                    MyRecycleBinAssets.Count),
                "确认删除",
                "取消")
            .ShowDialog<bool>(this);
        if (!confirmed)
        {
            return;
        }

        ClearMyRecycleBinButton.IsEnabled = false;
        RecycleBinClearResult result;
        try
        {
            result = await _api.ClearMyRecycleBinAsync();
        }
        catch (Exception exception)
        {
            UiLocalization.SetText(MyRecycleBinSummaryText, "清空失败：{0}", UserMessage(exception));
            ClearMyRecycleBinButton.IsEnabled = MyRecycleBinAssets.Count > 0;
            return;
        }

        try
        {
            await Task.WhenAll(RefreshMyRecycleBinAsync(), RefreshCloudAssetsAsync());
            await RefreshDisplayedProfileAssetsAsync();
            UiLocalization.SetText(
                MyRecycleBinSummaryText,
                "已永久删除 {0:N0} 个素材。",
                result.DeletedCount);
        }
        catch (Exception exception)
        {
            UiLocalization.SetText(
                MyRecycleBinSummaryText,
                "已永久删除 {0:N0} 个素材，但界面刷新失败：{1}",
                result.DeletedCount,
                UserMessage(exception));
        }
    }

    private async Task RefreshDisplayedProfileAssetsAsync()
    {
        var api = _api;
        if (!ProfilePage.IsVisible || api is null || _currentUser is null ||
            _displayedProfileUserId is not { } userId)
        {
            return;
        }

        var requestVersion = Interlocked.Increment(ref _profileContentRequestVersion);
        var cancellation = StartLatestRefresh(ref _profileContentRefreshCancellation);
        var cancellationToken = cancellation.Token;
        try
        {
            var profile = await api.GetUserAsync(userId, cancellationToken);
            if (profile.Id != userId ||
                !IsCurrentProfileContentRequest(requestVersion, userId, cancellationToken))
            {
                return;
            }

            await LoadProfileAssetsCoreAsync(
                userId,
                profile.AssetCounts,
                requestVersion,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            FinishLatestRefresh(ref _profileContentRefreshCancellation, cancellation);
        }
    }

    private void ApplyCurrentUser(ApiCurrentUser user, bool refreshOwnProfile = true)
    {
        var userChanged = _currentUser?.Id != user.Id;
        if (userChanged)
        {
            CancelLatestRefresh(ref _cloudRefreshCancellation);
            CancelLatestRefresh(ref _usersRefreshCancellation);
            CancelLatestRefresh(ref _recycleBinRefreshCancellation);
            CancelLatestRefresh(ref _profileContentRefreshCancellation);
            Interlocked.Increment(ref _cloudRequestVersion);
            Interlocked.Increment(ref _userRequestVersion);
            Interlocked.Increment(ref _recycleBinRequestVersion);
            if (_selectedCloudAsset is not null)
            {
                CloseDetail_OnClick(null, new RoutedEventArgs());
            }

            _cloudAssets = [];
            _users = [];
            CancelCloudThumbnailLoading();
            DisposeCloudAssetCards();
            ClearSharedCloudLuts();
            DisposeUserCards();
            MyRecycleBinAssets.Clear();
            ClearMyRecycleBinButton.IsEnabled = false;
            CloudEmptyState.IsVisible = false;
            UsersEmptyState.IsVisible = false;
            ClearCloudFolders();
        }

        _currentUser = user;
        _transferService = AccountTransferService();
        ObserveNavigationTask(RestoreTransferTasksAsync(), "restore-account-transfer-tasks");
        _profileSnapshot = user;
        if (_selectedLocalAsset is { } selectedLocalAsset)
        {
            UploadLocalAssetButton.IsEnabled = selectedLocalAsset.IsAvailable &&
                HasCurrentUserPermission("assets.upload");
        }
        var displayName = string.IsNullOrWhiteSpace(user.DisplayName) ? user.Username : user.DisplayName;
        CurrentDisplayName.Text = displayName;
        CurrentUsername.Text = $"@{user.Username}";
        CurrentUserInitial.Text = displayName[..1].ToUpperInvariant();
        _ = LoadCurrentUserAvatarAsync(user.Id, user.HasAvatar);
        SessionStateDot.Background = ResourceBrush("SuccessBrush");
        UiLocalization.SetText(CloudConnectionText, "已登录：{0}", displayName);
        UiLocalization.SetContent(ServerLoginButton, "切换账号");
        CloudLoginState.IsVisible = false;
        ApplyCloudAssetDisplayMode();
        ObserveNavigationTask(RefreshCloudFoldersAsync(), "refresh-cloud-folders-after-login");
        UsersLoginState.IsVisible = false;
        UsersScroll.IsVisible = true;
        AdminNav.IsVisible = user.IsAdmin;
        RecycleBinNav.IsVisible = HasCurrentUserPermission("assets.browse") &&
            HasCurrentUserPermission("assets.delete-own");
        if (!user.IsAdmin && AdminWorkspace.IsVisible)
        {
            NavigateTo("local");
        }

        if (!RecycleBinNav.IsVisible && RecycleBinPage.IsVisible)
        {
            NavigateTo("local");
        }

        if (ProfilePage.IsVisible &&
            (refreshOwnProfile || _displayedProfileUserId == user.Id))
        {
            ShowCurrentProfile();
        }
    }

    private Task SetLoggedOutStateAsync(string chineseMessageFormat, params object?[] arguments) =>
        SetLoggedOutStateAsync(chineseMessageFormat, false, arguments);

    private async Task SetLoggedOutStateAsync(
        string chineseMessageFormat,
        bool preserveToken,
        params object?[] arguments)
    {
        await SuspendTransfersAsync();
        await StopRealtimeAsync();
        CancelLatestRefresh(ref _cloudRefreshCancellation);
        CancelLatestRefresh(ref _usersRefreshCancellation);
        CancelLatestRefresh(ref _recycleBinRefreshCancellation);
        CancelLatestRefresh(ref _profileContentRefreshCancellation);
        Interlocked.Increment(ref _cloudRequestVersion);
        Interlocked.Increment(ref _userRequestVersion);
        Interlocked.Increment(ref _recycleBinRequestVersion);
        if (_selectedCloudAsset is not null)
        {
            CloseDetail_OnClick(null, new RoutedEventArgs());
        }

        _currentUser = null;
        UploadLocalAssetButton.IsEnabled = false;
        _profileSnapshot = null;
        _publicProfileSnapshot = null;
        _displayedProfileUserId = null;
        if (!preserveToken)
        {
            _tokenProvider.AccessToken = null;
        }

        UiLocalization.SetText(CurrentDisplayName, "本地模式");
        UiLocalization.SetText(CurrentUsername, "云端未登录");
        CurrentUserInitial.Text = "-";
        ClearCurrentUserAvatar();
        SessionStateDot.Background = ResourceBrush("SecondaryTextBrush");
        UiLocalization.SetText(CloudConnectionText, chineseMessageFormat, arguments);
        UiLocalization.SetContent(ServerLoginButton, "登录");
        CloudLoginState.IsVisible = true;
        CloudAssetScroll.IsVisible = false;
        CloudAssetListScroll.IsVisible = false;
        CloudEmptyState.IsVisible = false;
        UsersLoginState.IsVisible = true;
        UsersScroll.IsVisible = false;
        UsersEmptyState.IsVisible = false;
        AdminNav.IsVisible = false;
        RecycleBinNav.IsVisible = false;
        _cloudAssets = [];
        _users = [];
        ClearCloudFolders();
        CancelCloudThumbnailLoading();
        DisposeCloudAssetCards();
        ClearSharedCloudLuts();
        DisposeUserCards();
        MyRecycleBinAssets.Clear();
        ClearMyRecycleBinButton.IsEnabled = false;
        MyRecycleBinEmptyText.IsVisible = true;
        UiLocalization.SetText(MyRecycleBinSummaryText, "仅显示当前账号删除的云端素材");
        if (RecycleBinPage.IsVisible)
        {
            NavigateTo("local");
        }
        UiLocalization.SetText(CloudSummaryText, "团队云端素材");
        UiLocalization.SetText(UsersSummaryText, "团队成员");
        ShowLoggedOutProfile();
        await ReloadLutsAsync();
    }

    private async void Logout_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_api is not null && _currentUser is not null)
        {
            try
            {
                await _api.LogoutAsync();
            }
            catch
            {
                // Local logout still removes the credential when the server is unavailable.
            }
        }

        await DeleteStoredTokenQuietlyAsync();
        await SetLoggedOutStateAsync("已退出登录");
    }

    private async Task DeleteStoredTokenQuietlyAsync()
    {
        if (_serverOrigin is null)
        {
            return;
        }

        try
        {
            await _tokenStore.DeleteAsync(_serverOrigin);
        }
        catch
        {
            // The in-memory token is still removed below.
        }

        _tokenProvider.AccessToken = null;
    }

    private async void SaveServerAddress_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_isSavingServerAddress)
        {
            return;
        }

        _isSavingServerAddress = true;
        if (sender is Button button)
        {
            button.IsEnabled = false;
        }

        try
        {
            await SaveServerAddressAsync();
        }
        catch (Exception exception)
        {
            ClientDiagnostics.WriteException("server-address", exception, isFatal: false);
            ServerAddressStatusText.Foreground = ResourceBrush("ErrorBrush");
            UiLocalization.SetText(
                ServerAddressStatusText,
                "服务器连接失败：{0}",
                ApiErrorLocalization.Message(exception, _serverOrigin));
        }
        finally
        {
            _isSavingServerAddress = false;
            if (sender is Button completedButton)
            {
                completedButton.IsEnabled = true;
            }
        }
    }

    private void ServerAddressVisibility_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        _isServerAddressVisible = !_isServerAddressVisible;
        UpdateServerAddressVisibilityUi();
    }

    private void UpdateServerAddressVisibilityUi()
    {
        ServerAddressBox.PasswordChar = _isServerAddressVisible ? '\0' : '*';
        UiLocalization.SetContent(
            ServerAddressVisibilityButton,
            _isServerAddressVisible ? "隐藏地址" : "显示地址");
        ToolTip.SetTip(
            ServerAddressVisibilityButton,
            UiLocalization.Text(
                _isServerAddressVisible
                    ? "隐藏服务器地址"
                    : "显示服务器地址"));
    }

    private async Task SaveServerAddressAsync()
    {
        await SuspendTransfersAsync();
        ClientSettings updated;
        try
        {
            updated = (_settings with { ServerAddress = ServerAddressBox.Text ?? string.Empty })
                .ValidateAndNormalize();
        }
        catch (Exception exception)
        {
            ServerAddressStatusText.Foreground = ResourceBrush("ErrorBrush");
            UiLocalization.SetText(
                ServerAddressStatusText,
                "地址保存失败：{0}",
                UiLocalization.Text(exception.Message));
            return;
        }

        var previousSettings = _settings;
        var changed = !string.Equals(
            updated.ServerAddress,
            previousSettings.ServerAddress,
            StringComparison.OrdinalIgnoreCase);
        try
        {
            _settings = updated;
            ConfigureApi();
            await SaveSettingsAsync();
        }
        catch (Exception exception)
        {
            _settings = previousSettings;
            ConfigureApi();
            ServerAddressStatusText.Foreground = ResourceBrush("ErrorBrush");
            UiLocalization.SetText(
                ServerAddressStatusText,
                "地址保存失败：{0}",
                VisibleNetworkMessage(exception.Message));
            return;
        }

        if (changed)
        {
            _tokenProvider.AccessToken = null;
            await SetLoggedOutStateAsync("正在检查服务器连接...");
        }

        ServerAddressStatusText.Foreground = ResourceBrush("AccentBrush");
        UiLocalization.SetText(
            ServerAddressStatusText,
            "正在检查服务器连接：{0}",
            _serverOrigin!.GetLeftPart(UriPartial.Authority));
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var health = await _api!.GetAsync<ServerHealthResponse>("healthz", timeout.Token);
            if (!string.Equals(health.Status, "healthy", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidApiResponseException(
                    "The endpoint did not return a healthy service status.");
            }
        }
        catch (Exception exception)
        {
            ServerAddressStatusText.Foreground = ResourceBrush("ErrorBrush");
            UiLocalization.SetText(
                ServerAddressStatusText,
                "服务器连接失败：{0}",
                ApiErrorLocalization.Message(exception, _serverOrigin));
            if (changed)
            {
                await SetLoggedOutStateAsync("服务器连接失败，请查看设置");
            }

            return;
        }

        ServerAddressStatusText.Foreground = ResourceBrush("SuccessBrush");
        UiLocalization.SetText(
            ServerAddressStatusText,
            "服务器连接成功：{0}",
            _serverOrigin!.GetLeftPart(UriPartial.Authority));
        if (changed || _currentUser is null)
        {
            await TryRestoreSessionAsync(serverReachable: true);
        }
    }

    private static IBrush ResourceBrush(string key) =>
        (IBrush)(Application.Current?.Resources[key]
            ?? throw new InvalidOperationException($"Missing brush resource '{key}'."));

    private string UserMessage(Exception exception)
    {
        var failure = NetworkFailureClassifier.Classify(exception);
        return failure.Kind == NetworkFailureKind.Unknown
            ? VisibleNetworkMessage(exception.Message)
            : ApiErrorLocalization.Message(exception);
    }

    private string VisibleNetworkMessage(string? message) =>
        UserVisibleNetworkMessage.RedactLocations(message, _serverOrigin);

    private static DateTimeOffset? StartOfLocalDateUtc(DateTime? value)
    {
        if (value is null)
        {
            return null;
        }

        var localDate = DateTime.SpecifyKind(value.Value.Date, DateTimeKind.Unspecified);
        return new DateTimeOffset(localDate, TimeZoneInfo.Local.GetUtcOffset(localDate)).ToUniversalTime();
    }

    private static DateTimeOffset? EndOfLocalDateUtc(DateTime? value) =>
        StartOfLocalDateUtc(value)?.AddDays(1).AddTicks(-1);

    private void AllLocalFolders_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        _selectedLocalFolderId = null;
        _selectedLocalRelativeDirectoryPath = null;
        _suppressLocalFolderSelectionChanged = true;
        LocalFolderTree.SelectedItem = null;
        _suppressLocalFolderSelectionChanged = false;
        UpdateLocalFolderSelectionState();
        ApplyLocalFilter();
    }

    private void CompactLocalFolder_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not Button { DataContext: LocalFolderNodeViewModel node } ||
            !node.FolderId.HasValue)
        {
            return;
        }

        _selectedLocalFolderId = node.FolderId.Value;
        _selectedLocalRelativeDirectoryPath = string.IsNullOrEmpty(node.RelativePath)
            ? null
            : node.RelativePath;
        _suppressLocalFolderSelectionChanged = true;
        LocalFolderTree.SelectedItem = node;
        _suppressLocalFolderSelectionChanged = false;
        UpdateLocalFolderSelectionState();
        ApplyLocalFilter();
    }

    private void LocalFolderTree_OnSelectionChanged(object? sender, SelectionChangedEventArgs eventArgs)
    {
        if (_suppressLocalFolderSelectionChanged)
        {
            return;
        }

        if (LocalFolderTree.SelectedItem is not LocalFolderNodeViewModel node ||
            !node.FolderId.HasValue)
        {
            _selectedLocalFolderId = null;
            _selectedLocalRelativeDirectoryPath = null;
        }
        else
        {
            _selectedLocalFolderId = node.FolderId.Value;
            _selectedLocalRelativeDirectoryPath = string.IsNullOrEmpty(node.RelativePath)
                ? null
                : node.RelativePath;
        }

        UpdateLocalFolderSelectionState();
        ApplyLocalFilter();
    }

    private void LocalFolderNode_OnContextRequested(
        object? sender,
        ContextRequestedEventArgs eventArgs)
    {
        if (sender is not Control
            {
                DataContext: LocalFolderNodeViewModel node
            } target)
        {
            return;
        }

        SelectLocalFolderNodeForContext(node);
        target.ContextMenu?.Close();
        var menu = CreateLocalFolderContextMenu(node);
        target.ContextMenu = menu;
        menu.Open(target);
        eventArgs.Handled = true;
    }

    private async void OpenLocalFolderLocation_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not MenuItem { Tag: LocalFolderNodeViewModel node })
        {
            return;
        }

        await RunLocalFileSystemMenuActionAsync(
            "open-local-folder-location",
            "无法打开文件夹",
            () =>
            {
                EnsureLocalFolderAvailable(node);
                OpenFolderInManager(node.FullPath);
                return Task.CompletedTask;
            });
    }

    private void RemoveLocalFolderFromCatalog_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not MenuItem { Tag: LocalFolderNodeViewModel { CanRemoveFromCatalog: true } node })
        {
            return;
        }

        _pendingLocalFolderRemoval = node;
        LocalFolderRemovePathText.Text = node.FullPath;
        LocalFolderRemoveMessageText.Text = string.Empty;
        LocalFolderRemoveCancelButton.IsEnabled = true;
        LocalFolderRemoveConfirmButton.IsEnabled = true;
        LocalFolderRemoveConfirmationOverlay.IsVisible = true;
        LocalFolderRemoveCancelButton.Focus();
    }

    private void CancelLocalFolderRemoval_OnClick(object? sender, RoutedEventArgs eventArgs) =>
        HideLocalFolderRemovalConfirmation();

    private async void ConfirmLocalFolderRemoval_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_pendingLocalFolderRemoval is not
            {
                CanRemoveFromCatalog: true,
                FolderId: { } folderId
            } node)
        {
            HideLocalFolderRemovalConfirmation();
            return;
        }

        var removesSelectedAsset = _selectedLocalAsset is { } selected &&
                                   _catalog.Assets.Any(asset =>
                                       asset.Id == selected.Id && asset.FolderId == folderId);
        LocalFolderRemoveCancelButton.IsEnabled = false;
        LocalFolderRemoveConfirmButton.IsEnabled = false;
        UiLocalization.SetText(LocalFolderRemoveMessageText, "正在从目录移除...");
        try
        {
            _catalog = await _indexService.RemoveFolderAsync(folderId);
            CloseLocalImagePreview();
            if (removesSelectedAsset)
            {
                CloseDetail_OnClick(null, new RoutedEventArgs());
            }

            if (_selectedLocalFolderId == folderId)
            {
                _selectedLocalFolderId = null;
                _selectedLocalRelativeDirectoryPath = null;
            }

            HideLocalFolderRemovalConfirmation();
            RebuildLocalFolderTree();
            ApplyLocalFilter();
            UiLocalization.SetText(
                LocalSummaryText,
                "已从目录移除“{0}”；磁盘上的文件夹和素材未作更改。",
                node.DisplayName);
        }
        catch (Exception exception)
        {
            UiLocalization.SetText(LocalFolderRemoveMessageText, "移除失败：{0}", exception.Message);
            LocalFolderRemoveCancelButton.IsEnabled = true;
            LocalFolderRemoveConfirmButton.IsEnabled = true;
        }
    }

    private void HideLocalFolderRemovalConfirmation()
    {
        LocalFolderRemoveConfirmationOverlay.IsVisible = false;
        LocalFolderRemoveCancelButton.IsEnabled = true;
        LocalFolderRemoveConfirmButton.IsEnabled = true;
        LocalFolderRemoveMessageText.Text = string.Empty;
        _pendingLocalFolderRemoval = null;
    }

    private async void MainWindow_OnKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (LocalFolderRemoveConfirmationOverlay.IsVisible &&
            LocalFolderRemoveCancelButton.IsEnabled &&
            eventArgs.Key == Key.Escape)
        {
            HideLocalFolderRemovalConfirmation();
            eventArgs.Handled = true;
            return;
        }

        if (!PlayerPage.IsVisible ||
            IsTextEntryTarget(eventArgs.Source) ||
            !PlaybackShortcutCatalog.TryMatch(_settings.PlaybackShortcuts, eventArgs, out var action))
        {
            return;
        }

        eventArgs.Handled = true;
        try
        {
            await ExecutePlaybackShortcutAsync(action);
        }
        catch (Exception exception)
        {
            ClientDiagnostics.WriteException("playback-shortcut", exception, isFatal: false);
            UiLocalization.SetText(PlayerToolStatusText, "快捷键操作失败：{0}", UserMessage(exception));
        }
    }

    private static bool IsTextEntryTarget(object? source) =>
        source is TextBox ||
        source is Control control && control.FindAncestorOfType<TextBox>() is not null;

    private void OpenBilibiliProfile_OnClick(object? sender, RoutedEventArgs eventArgs) =>
        OpenExternalUri("https://space.bilibili.com/1003434667");

    private void OpenGitHubProfile_OnClick(object? sender, RoutedEventArgs eventArgs) =>
        OpenExternalUri("https://github.com/FengchenWD/");

    private static void OpenExternalUri(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var target) ||
            target.Scheme is not ("https" or "http"))
        {
            return;
        }

        Process.Start(new ProcessStartInfo(target.AbsoluteUri) { UseShellExecute = true });
    }

    private void RebuildLocalFolderTree()
    {
        var selectedFolderId = _selectedLocalFolderId;
        var selectedRelativePath = NormalizeRelativePath(_selectedLocalRelativeDirectoryPath);
        LocalFolderNodeViewModel? selectedNode = null;

        _suppressLocalFolderSelectionChanged = true;
        try
        {
            LocalFolderNodes.Clear();
            CompactLocalFolderNodes.Clear();
            foreach (var folder in _catalog.Folders.OrderBy(item => item.Path, LocalPathComparer))
            {
                var folderAssets = _catalog.Assets
                    .Where(asset => asset.FolderId == folder.Id)
                    .ToArray();
                var root = new LocalFolderNodeViewModel(
                    folder.Id,
                    string.Empty,
                    folder.Path,
                    GetFolderDisplayName(folder.Path),
                    folderAssets.Length,
                    folder.Availability is IndexedFolderAvailability.Offline or
                        IndexedFolderAvailability.Missing);
                LocalFolderNodes.Add(root);

                var nodesByPath = new Dictionary<string, LocalFolderNodeViewModel>(LocalPathComparer);
                foreach (var directory in _catalog.Directories
                             .Where(item => item.FolderId == folder.Id)
                             .OrderBy(item => RelativePathDepth(item.RelativePath))
                             .ThenBy(item => item.RelativePath, LocalPathComparer))
                {
                    var relativePath = NormalizeRelativePath(directory.RelativePath);
                    if (string.IsNullOrEmpty(relativePath))
                    {
                        continue;
                    }

                    var node = new LocalFolderNodeViewModel(
                        folder.Id,
                        relativePath,
                        Path.GetFullPath(Path.Combine(folder.Path, relativePath)),
                        directory.Name,
                        folderAssets.Count(asset => IsAssetWithinDirectory(asset, relativePath)),
                        directory.Availability == IndexedDirectoryAvailability.Offline);
                    nodesByPath[relativePath] = node;

                    var parentPath = NormalizeRelativePath(Path.GetDirectoryName(relativePath));
                    if (string.IsNullOrEmpty(parentPath))
                    {
                        root.Children.Add(node);
                    }
                    else if (nodesByPath.TryGetValue(parentPath, out var parent))
                    {
                        parent.Children.Add(node);
                    }
                    else
                    {
                        root.Children.Add(node);
                    }
                }

                if (selectedFolderId == folder.Id)
                {
                    selectedNode = string.IsNullOrEmpty(selectedRelativePath)
                        ? root
                        : nodesByPath.GetValueOrDefault(selectedRelativePath);
                }


                AddCompactLocalFolderNodes(root);
            }

            if (selectedNode is null)
            {
                _selectedLocalFolderId = null;
                _selectedLocalRelativeDirectoryPath = null;
            }

            LocalFolderTree.SelectedItem = selectedNode;
        }
        finally
        {
            _suppressLocalFolderSelectionChanged = false;
        }

        UpdateLocalFolderSelectionState();
    }

    private void AddCompactLocalFolderNodes(LocalFolderNodeViewModel node)
    {
        CompactLocalFolderNodes.Add(node);
        foreach (var child in node.Children)
        {
            AddCompactLocalFolderNodes(child);
        }
    }

    private void UpdateLocalFolderSelectionState()
    {
        AllLocalFoldersButton.Classes.Set("selected", !_selectedLocalFolderId.HasValue);
        foreach (var node in CompactLocalFolderNodes)
        {
            node.IsCompactSelected = node.FolderId == _selectedLocalFolderId &&
                                     LocalPathComparer.Equals(
                                         NormalizeRelativePath(node.RelativePath),
                                         NormalizeRelativePath(_selectedLocalRelativeDirectoryPath));
        }
        var target = SelectedLocalFileImportTarget();
        var available = target is not null && CanImportIntoLocalFolder(target);
        LocalFileImportDropTarget.IsEnabled = available;
        LocalFileImportDropTarget.Classes.Set("dragActive", false);
        UiLocalization.SetText(
            LocalFileImportDropSubtitle,
            available
                ? "目标：{0}"
                : target is not null
                    ? "当前目录不可用"
                    : "请先选择具体文件夹",
            target?.DisplayName ?? string.Empty);
        ToolTip.SetTip(
            LocalFileImportDropTarget,
            UiLocalization.Text(
                available
                    ? "把素材文件拖到这里，复制到当前选中的文件夹"
                    : "先选择一个具体且可用的文件夹，再把素材文件拖到这里"));
    }

    private LocalFolderNodeViewModel? SelectedLocalFileImportTarget() =>
        LocalFolderTree.SelectedItem as LocalFolderNodeViewModel;

    private static string GetFolderDisplayName(string folderPath)
    {
        var trimmed = folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? folderPath : name;
    }

    private static int RelativePathDepth(string relativePath) =>
        NormalizeRelativePath(relativePath).Count(character => character == Path.DirectorySeparatorChar);

    private static string NormalizeRelativePath(string? relativePath) =>
        string.IsNullOrWhiteSpace(relativePath) || relativePath == "."
            ? string.Empty
            : relativePath
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                .Trim(Path.DirectorySeparatorChar);

    private static bool IsAssetWithinDirectory(LocalAsset asset, string relativeDirectoryPath)
    {
        var assetDirectory = NormalizeRelativePath(Path.GetDirectoryName(asset.RelativePath));
        return LocalPathComparer.Equals(assetDirectory, relativeDirectoryPath) ||
               assetDirectory.StartsWith(
                   relativeDirectoryPath + Path.DirectorySeparatorChar,
                   OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private async void AddFolder_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        var folders = await RunNativePickerAsync(
            "add-local-folder",
            () => StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = UiLocalization.Text("选择本地素材文件夹"),
                AllowMultiple = false
            }),
            LocalSummaryText);

        if (folders is null || folders.Count == 0)
        {
            return;
        }

        var localPath = folders[0].Path.LocalPath;
        if (string.IsNullOrWhiteSpace(localPath))
        {
            UiLocalization.SetText(LocalSummaryText, "该位置不是可索引的本地文件夹。");
            return;
        }

        await IndexFolderAsync(localPath);
    }

    private void UploadDropTarget_OnPointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (!eventArgs.GetCurrentPoint(sender as Control ?? this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _uploadDropPointerStart = eventArgs.GetPosition(sender as Control ?? this);
        _uploadDropPointerMoved = false;
    }

    private void UploadDropTarget_OnPointerMoved(object? sender, PointerEventArgs eventArgs)
    {
        if (_uploadDropPointerStart is not { } start || sender is not Control control ||
            !eventArgs.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var point = eventArgs.GetPosition(control);
        if (Math.Abs(point.X - start.X) > 6 || Math.Abs(point.Y - start.Y) > 6)
        {
            _uploadDropPointerMoved = true;
        }
    }

    private async void LocalFolderDropTarget_OnPointerReleased(object? sender, PointerReleasedEventArgs eventArgs)
    {
        var click = !_uploadDropPointerMoved;
        _uploadDropPointerStart = null;
        _uploadDropPointerMoved = false;
        if (click)
        {
            await AddFolder_OnClickAsync();
        }
    }

    private async Task AddFolder_OnClickAsync()
    {
        var folders = await RunNativePickerAsync(
            "add-local-folder-click",
            () => StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = UiLocalization.Text("选择本地素材文件夹"),
                AllowMultiple = false
            }),
            LocalSummaryText);
        if (folders is { Count: > 0 } && !string.IsNullOrWhiteSpace(folders[0].Path.LocalPath))
        {
            await IndexFolderAsync(folders[0].Path.LocalPath);
        }
    }

    private async void LocalFileImportDropTarget_OnPointerReleased(object? sender, PointerReleasedEventArgs eventArgs)
    {
        var click = !_uploadDropPointerMoved;
        _uploadDropPointerStart = null;
        _uploadDropPointerMoved = false;
        if (!click || SelectedLocalFileImportTarget() is not { } target || !CanImportIntoLocalFolder(target))
        {
            return;
        }

        var files = await RunNativePickerAsync(
            "local-file-import-click",
            () => StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = UiLocalization.Text("选择要添加的素材文件"),
                AllowMultiple = true
            }),
            LocalSummaryText);
        if (files is { Count: > 0 })
        {
            await ImportFilesIntoLocalFolderSafelyAsync(
                target,
                files.Select(file => file.Path.LocalPath).Where(path => !string.IsNullOrWhiteSpace(path)).Cast<string>().ToArray());
        }
    }

    private async void SharedUploadDropTarget_OnPointerReleased(object? sender, PointerReleasedEventArgs eventArgs)
    {
        var click = !_uploadDropPointerMoved;
        _uploadDropPointerStart = null;
        _uploadDropPointerMoved = false;
        if (click)
        {
            await ChooseCloudUpload_OnClickAsync();
        }
    }

    private async Task ChooseCloudUpload_OnClickAsync()
    {
        if (_currentUser is null)
        {
            return;
        }

        var files = await RunNativePickerAsync(
            "choose-cloud-upload-click",
            () => StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = UiLocalization.Text("选择要上传的素材"),
                AllowMultiple = true
            }),
            CloudSummaryText);
        if (files is { Count: > 0 })
        {
            await PrepareCloudUploadAsync(files.Select(file => file.Path.LocalPath).Where(path => !string.IsNullOrWhiteSpace(path)).Cast<string>().ToArray(), _selectedCloudFolderId);
        }
    }

    private async Task IndexFolderAsync(string folderPath)
    {
        UiLocalization.SetText(LocalSummaryText, "正在扫描素材...");
        try
        {
            var result = await _indexService.IndexFolderAsync(folderPath);
            _catalog = result.Catalog;
            RebuildLocalFolderTree();
            ApplyLocalFilter();
            if (result.Issues.Count == 0)
            {
                UiLocalization.SetText(LocalSummaryText, "已索引 {0:N0} 个素材", result.IndexedFileCount);
            }
            else
            {
                UiLocalization.SetText(
                    LocalSummaryText,
                    "已索引 {0:N0} 个素材，{1:N0} 个位置暂不可读",
                    result.IndexedFileCount,
                    result.Issues.Count);
            }
        }
        catch (InvalidOperationException exception)
        {
            UiLocalization.SetText(LocalSummaryText, "无法添加文件夹：{0}", exception.Message);
            await new MessageDialogWindow("无法添加文件夹", exception.Message).ShowDialog(this);
        }
        catch (Exception exception)
        {
            UiLocalization.SetText(LocalSummaryText, "扫描失败：{0}", exception.Message);
        }
    }

    private void LocalFolderDropTarget_OnDragOver(object? sender, DragEventArgs eventArgs)
    {
        var dropped = SnapshotDroppedPaths(eventArgs);
        var folderCount = dropped.Folders.Length;
        var hasPendingFileTransfer = dropped.Files.Length + folderCount == 0 && ContainsFileTransfer(eventArgs);
        var acceptsDrop = folderCount > 0 || hasPendingFileTransfer;
        LocalFolderDropTarget.Classes.Set("dragActive", acceptsDrop);
        eventArgs.DragEffects = acceptsDrop ? DragDropEffects.Copy : DragDropEffects.None;
        if (folderCount > 0)
        {
            UiLocalization.SetText(
                LocalSummaryText,
                "释放鼠标连接 {0:N0} 个素材文件夹",
                folderCount);
        }

        eventArgs.Handled = true;
    }

    private void LocalFolderDropTarget_OnDragLeave(object? sender, DragEventArgs eventArgs)
    {
        LocalFolderDropTarget.Classes.Set("dragActive", false);
        eventArgs.Handled = true;
    }

    private void LocalFolderDropTarget_OnDrop(object? sender, DragEventArgs eventArgs)
    {
        var dropped = SnapshotDroppedPaths(eventArgs);
        var containedFileTransfer = ContainsFileTransfer(eventArgs);
        LocalFolderDropTarget.Classes.Set("dragActive", false);
        eventArgs.DragEffects = dropped.Folders.Length > 0
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        eventArgs.Handled = true;
        if (dropped.Folders.Length == 0 && containedFileTransfer && dropped.Files.Length == 0)
        {
            eventArgs.DragEffects = DragDropEffects.Copy;
            UiLocalization.SetText(
                LocalSummaryText,
                "正在读取拖入的文件...");
            var pendingTransfer = eventArgs.DataTransfer;
            Dispatcher.UIThread.Post(
                () => _ = ResolvePendingLocalFolderDropAsync(pendingTransfer),
                DispatcherPriority.Background);
            return;
        }

        QueueLocalFolderDrop(targetFolder: null, dropped.Folders);
    }

    private async Task ResolvePendingLocalFolderDropAsync(IDataTransfer dataTransfer)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(60 * attempt);
            }

            var folders = SnapshotDroppedPaths(dataTransfer).Folders;
            if (folders.Length == 0)
            {
                continue;
            }

            await HandleLocalFolderDropSafelyAsync(targetFolder: null, folders);
            return;
        }

        UiLocalization.SetText(
            LocalSummaryText,
            "未能读取拖入的文件夹，请重新拖入或使用“添加文件夹”按钮。");
    }

    private void LocalFileImportDropTarget_OnDragOver(object? sender, DragEventArgs eventArgs)
    {
        var dropped = SnapshotDroppedPaths(eventArgs);
        var target = SelectedLocalFileImportTarget();
        var fileCount = dropped.Files.Length;
        var pendingFileTransfer = fileCount + dropped.Folders.Length == 0 && ContainsFileTransfer(eventArgs);
        var acceptsDrop = target is not null &&
                          CanImportIntoLocalFolder(target) &&
                          dropped.Folders.Length == 0 &&
                          (fileCount > 0 || pendingFileTransfer);
        LocalFileImportDropTarget.Classes.Set("dragActive", acceptsDrop);
        eventArgs.DragEffects = acceptsDrop ? DragDropEffects.Copy : DragDropEffects.None;
        if (acceptsDrop && target is not null)
        {
            SetLocalFileDropEffect(eventArgs, target, fileCount > 0 ? fileCount : null);
        }
        else if (dropped.Folders.Length > 0)
        {
            UiLocalization.SetText(LocalSummaryText, "这里只接收素材文件；请把文件夹拖到上方连接区域。");
        }
        eventArgs.Handled = true;
    }

    private void LocalFileImportDropTarget_OnDragLeave(object? sender, DragEventArgs eventArgs)
    {
        LocalFileImportDropTarget.Classes.Set("dragActive", false);
        eventArgs.Handled = true;
    }

    private void LocalFileImportDropTarget_OnDrop(object? sender, DragEventArgs eventArgs)
    {
        var dropped = SnapshotDroppedPaths(eventArgs);
        var containedFileTransfer = ContainsFileTransfer(eventArgs);
        var target = SelectedLocalFileImportTarget();
        LocalFileImportDropTarget.Classes.Set("dragActive", false);
        eventArgs.DragEffects = target is not null &&
                                CanImportIntoLocalFolder(target) &&
                                dropped.Files.Length > 0
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        eventArgs.Handled = true;
        if (target is null || !CanImportIntoLocalFolder(target))
        {
            if (dropped.Folders.Length > 0)
            {
                UiLocalization.SetText(LocalSummaryText, "这里只接收素材文件；请把文件夹拖到上方连接区域。");
            }
            return;
        }

        if (dropped.Files.Length == 0)
        {
            if (containedFileTransfer && dropped.Folders.Length == 0)
            {
                eventArgs.DragEffects = DragDropEffects.Copy;
                UiLocalization.SetText(LocalSummaryText, "正在读取拖入的文件...");
                var pendingTransfer = eventArgs.DataTransfer;
                Dispatcher.UIThread.Post(
                    () => _ = ResolvePendingLocalFileDropAsync(pendingTransfer, target),
                    DispatcherPriority.Background);
            }
            else if (dropped.Folders.Length > 0)
            {
                UiLocalization.SetText(LocalSummaryText, "这里只接收素材文件；请把文件夹拖到上方连接区域。");
            }

            return;
        }

        QueueLocalFileDrop(target, dropped.Files);
    }

    private void CloudDragLocalNavigation_OnDragOver(object? sender, DragEventArgs eventArgs)
    {
        if (!HasActiveCloudFileDrag())
        {
            eventArgs.DragEffects = DragDropEffects.None;
            return;
        }

        eventArgs.DragEffects = DragDropEffects.Copy;
        eventArgs.Handled = true;
        if (_activePage != "local")
        {
            NavigateTo("local");
            UiLocalization.SetText(LocalSummaryText, "请选择具体文件夹并释放鼠标完成下载。" );
        }
    }

    private void CloudDragLocalNavigation_OnDrop(object? sender, DragEventArgs eventArgs)
    {
        eventArgs.DragEffects = DragDropEffects.None;
        eventArgs.Handled = true;
        if (HasActiveCloudFileDrag())
        {
            UiLocalization.SetText(LocalSummaryText, "请将素材拖到文件夹树中的具体目录，不能拖到本地素材入口。" );
        }
    }

    private void LocalFolderNode_OnDragOver(object? sender, DragEventArgs eventArgs)
    {
        if (sender is not Control
            {
                DataContext: LocalFolderNodeViewModel target
            } control)
        {
            eventArgs.DragEffects = DragDropEffects.None;
            return;
        }

        var isCloudDrag = HasActiveCloudAssetDrag();
        var isLocalDrag = HasActiveLocalAssetDrag();
        var acceptsDrop = CanImportIntoLocalFolder(target) && (isCloudDrag || isLocalDrag);
        control.Classes.Set("dragActive", acceptsDrop);
        // Shell drags are intentionally advertised as Copy so Explorer and editing software
        // never move source media. Internal folder drops still execute the catalog move below.
        eventArgs.DragEffects = acceptsDrop ? DragDropEffects.Copy : DragDropEffects.None;
        eventArgs.Handled = true;
        if (acceptsDrop)
        {
            UiLocalization.SetText(
                LocalSummaryText,
                isLocalDrag
                    ? "释放后将 {0:N0} 个本地素材移动到“{1}”"
                    : "释放后将 {0:N0} 个云端素材下载到“{1}”",
                isLocalDrag ? _activeLocalDragAssets!.Length : _activeCloudDragAssets!.Length,
                target.DisplayName);
        }
    }

    private void LocalFolderNode_OnDragLeave(object? sender, DragEventArgs eventArgs)
    {
        if (sender is Control control)
        {
            control.Classes.Set("dragActive", false);
        }

        eventArgs.Handled = true;
    }

    private void LocalFolderNode_OnDrop(object? sender, DragEventArgs eventArgs)
    {
        if (sender is not Control
            {
                DataContext: LocalFolderNodeViewModel target
            } control)
        {
            eventArgs.DragEffects = DragDropEffects.None;
            return;
        }

        control.Classes.Set("dragActive", false);
        var cloudAssets = _activeCloudDragAssets?.ToArray() ?? [];
        var localAssets = _activeLocalDragAssets?.ToArray() ?? [];
        var acceptsDrop = CanImportIntoLocalFolder(target) &&
                          (cloudAssets.Length > 0 || localAssets.Length > 0);
        eventArgs.DragEffects = acceptsDrop ? DragDropEffects.Copy : DragDropEffects.None;
        eventArgs.Handled = true;
        if (acceptsDrop)
        {
            Dispatcher.UIThread.Post(
                () => _ = localAssets.Length > 0
                    ? MoveLocalAssetsToFolderSafelyAsync(target, localAssets)
                    : DownloadCloudAssetsToLocalFolderSafelyAsync(target, cloudAssets),
                DispatcherPriority.Background);
        }
    }

    private bool HasActiveCloudFileDrag() =>
        _isDragging && _activeCloudDragPaths is { Length: > 0 } paths && paths.All(File.Exists);

    private bool HasActiveCloudAssetDrag() =>
        _isDragging && _activeCloudDragAssets is { Length: > 0 };

    private bool HasActiveLocalAssetDrag() =>
        _isDragging && _activeLocalDragAssets is { Length: > 0 } assets &&
        assets.All(asset => asset.IsAvailable && File.Exists(asset.FullPath));

    private async Task DownloadCloudAssetsToLocalFolderSafelyAsync(
        LocalFolderNodeViewModel target,
        IReadOnlyList<ApiAsset> assets)
    {
        if (!CanImportIntoLocalFolder(target) || assets.Count == 0)
        {
            return;
        }

        var succeeded = 0;
        var failed = 0;
        foreach (var asset in assets)
        {
            try
            {
                if (await EnsureCloudAssetDownloadedAsync(asset, target.FullPath) is not null)
                {
                    succeeded++;
                }
            }
            catch (Exception exception)
            {
                failed++;
                ClientDiagnostics.WriteException("cloud-drop-to-local-folder", exception, isFatal: false);
            }
        }

        if (target.FolderId is { } folderId)
        {
            var root = _catalog.Folders.FirstOrDefault(folder => folder.Id == folderId);
            if (root is not null && Directory.Exists(root.Path))
            {
                _catalog = (await _indexService.IndexFolderAsync(root.Path, root.IsExternalStorage)).Catalog;
                RebuildLocalFolderTree();
                ApplyLocalFilter();
            }
        }

        UiLocalization.SetText(
            LocalSummaryText,
            failed == 0
                ? "已下载 {0:N0} 个云端素材到“{1}”。"
                : "下载完成：成功 {0:N0} 个，失败 {2:N0} 个；目标“{1}”。",
            succeeded,
            target.DisplayName,
            failed);
        if (succeeded > 0)
        {
            ShowTransientNotification("下载完成：{0:N0} 个素材", succeeded);
        }
    }

    private async Task MoveLocalAssetsToFolderSafelyAsync(
        LocalFolderNodeViewModel target,
        IReadOnlyList<LocalAsset> assets)
    {
        if (target.FolderId is not { } targetFolderId || assets.Count == 0)
        {
            return;
        }

        try
        {
            _catalog = await _indexService.MoveAssetsAsync(
                assets.Select(asset => asset.Id).ToArray(),
                targetFolderId,
                target.RelativePath);
            _selectedLocalFolderId = targetFolderId;
            _selectedLocalRelativeDirectoryPath = target.RelativePath;
            RebuildLocalFolderTree();
            ApplyLocalFilter();
            UiLocalization.SetText(
                LocalSummaryText,
                "已将 {0:N0} 个素材移动到“{1}”。",
                assets.Count,
                target.DisplayName);
        }
        catch (Exception exception)
        {
            ClientDiagnostics.WriteException("move-local-assets-by-drop", exception, isFatal: false);
            UiLocalization.SetText(LocalSummaryText, "移动素材失败：{0}", UserMessage(exception));
            await new MessageDialogWindow("无法移动素材", UserMessage(exception)).ShowDialog(this);
        }
    }

    private async Task ResolvePendingLocalFileDropAsync(
        IDataTransfer dataTransfer,
        LocalFolderNodeViewModel targetFolder)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(60 * attempt);
            }

            var files = SnapshotDroppedPaths(dataTransfer).Files;
            if (files.Length == 0)
            {
                continue;
            }

            if (!CanImportIntoLocalFolder(targetFolder))
            {
                break;
            }

            await ImportFilesIntoLocalFolderSafelyAsync(targetFolder, files);
            return;
        }

        UiLocalization.SetText(
            LocalSummaryText,
            "未能读取拖入的素材文件，请重新拖入或检查文件是否仍在原位置。");
    }

    private void SetLocalFileDropEffect(
        DragEventArgs eventArgs,
        LocalFolderNodeViewModel targetFolder,
        int? fileCount)
    {
        var available = CanImportIntoLocalFolder(targetFolder);
        eventArgs.DragEffects = available ? DragDropEffects.Copy : DragDropEffects.None;
        if (available)
        {
            if (fileCount.HasValue)
            {
                UiLocalization.SetText(
                    LocalSummaryText,
                    "释放鼠标复制 {0:N0} 个文件到“{1}”",
                    fileCount.Value,
                    targetFolder.DisplayName);
            }
            else
            {
                UiLocalization.SetText(
                    LocalSummaryText,
                    "释放鼠标复制素材文件到“{0}”",
                    targetFolder.DisplayName);
            }
        }
        else
        {
            UiLocalization.SetText(LocalSummaryText, "目标文件夹当前不可用。");
        }
    }

    private static bool CanImportIntoLocalFolder(LocalFolderNodeViewModel targetFolder) =>
        targetFolder.FolderId.HasValue &&
        !targetFolder.IsOffline &&
        Directory.Exists(targetFolder.FullPath);

    private void QueueLocalFileDrop(
        LocalFolderNodeViewModel targetFolder,
        IReadOnlyList<string> filePaths)
    {
        var pathSnapshot = filePaths.ToArray();
        ObserveNavigationTask(
            ImportFilesIntoLocalFolderSafelyAsync(targetFolder, pathSnapshot),
            "local-file-drop");
    }

    private async Task ImportFilesIntoLocalFolderSafelyAsync(
        LocalFolderNodeViewModel targetFolder,
        IReadOnlyList<string> filePaths)
    {
        if (!await _localDropOperationGate.WaitAsync(0))
        {
            UiLocalization.SetText(LocalSummaryText, "已有本地复制或目录扫描正在进行，请完成后再试。");
            return;
        }

        try
        {
            await ImportFilesIntoLocalFolderAsync(targetFolder, filePaths);
        }
        catch (Exception exception)
        {
            ClientDiagnostics.WriteException("local-folder-drop", exception, isFatal: false);
            UiLocalization.SetText(LocalSummaryText, "拖拽添加失败：{0}", UserMessage(exception));
            await new MessageDialogWindow("无法添加素材", UserMessage(exception)).ShowDialog(this);
        }
        finally
        {
            _localDropOperationGate.Release();
        }
    }

    private void QueueLocalFolderDrop(
        LocalFolderNodeViewModel? targetFolder,
        IReadOnlyList<string> folderPaths)
    {
        var pathSnapshot = folderPaths.ToArray();
        ObserveNavigationTask(
            HandleLocalFolderDropSafelyAsync(targetFolder, pathSnapshot),
            "local-folder-drop");
    }

    private async Task HandleLocalFolderDropSafelyAsync(
        LocalFolderNodeViewModel? targetFolder,
        IReadOnlyList<string> folderPaths)
    {
        if (!await _localDropOperationGate.WaitAsync(0))
        {
            UiLocalization.SetText(LocalSummaryText, "已有本地复制或目录扫描正在进行，请完成后再试。");
            return;
        }

        try
        {
            await HandleLocalFolderDropAsync(targetFolder, folderPaths);
        }
        catch (Exception exception)
        {
            ClientDiagnostics.WriteException("local-folder-connect-drop", exception, isFatal: false);
            UiLocalization.SetText(LocalSummaryText, "拖拽连接文件夹失败：{0}", UserMessage(exception));
            await new MessageDialogWindow("无法连接文件夹", UserMessage(exception)).ShowDialog(this);
        }
        finally
        {
            _localDropOperationGate.Release();
        }
    }

    private async Task HandleLocalFolderDropAsync(
        LocalFolderNodeViewModel? targetFolder,
        IReadOnlyList<string> folderPaths)
    {
        if (targetFolder is not null && folderPaths.Count > 0)
        {
            await new MessageDialogWindow(
                    "无法连接目录",
                    "目录只能拖到素材文件夹栏的空白处进行连接，不能拖到已有文件夹节点中。")
                .ShowDialog(this);
            return;
        }

        foreach (var folderPath in folderPaths)
        {
            await IndexFolderAsync(folderPath);
        }
    }

    private async Task ImportFilesIntoLocalFolderAsync(
        LocalFolderNodeViewModel targetFolder,
        IReadOnlyList<string> sourcePaths)
    {
        if (targetFolder.FolderId is not { } folderId ||
            targetFolder.IsOffline ||
            !Directory.Exists(targetFolder.FullPath))
        {
            await new MessageDialogWindow(
                    "无法添加素材",
                    "目标文件夹当前不可用。请重新连接存储设备并刷新素材库后再试。")
                .ShowDialog(this);
            return;
        }

        var supported = sourcePaths
            .Where(File.Exists)
            .Where(path => MediaExtensionClassifier.TryClassify(path, out _))
            .Distinct(LocalPathComparer)
            .ToArray();
        var unsupportedCount = sourcePaths.Count - supported.Length;
        if (supported.Length == 0)
        {
            await new MessageDialogWindow(
                    "无法添加素材",
                    "拖入内容中没有支持的图片、音频或视频文件。")
                .ShowDialog(this);
            return;
        }

        var copyPlans = supported
            .Select(source => (
                Source: Path.GetFullPath(source),
                Destination: Path.GetFullPath(Path.Combine(
                    targetFolder.FullPath,
                    Path.GetFileName(source)))))
            .ToArray();
        var duplicateDestinations = copyPlans
            .GroupBy(plan => plan.Destination, LocalPathComparer)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(LocalPathComparer);
        var conflicts = copyPlans
            .Where(plan =>
                LocalPathComparer.Equals(plan.Source, plan.Destination) ||
                File.Exists(plan.Destination) ||
                duplicateDestinations.Contains(plan.Destination))
            .ToArray();
        var conflictSources = conflicts
            .Select(plan => plan.Source)
            .ToHashSet(LocalPathComparer);

        if (conflicts.Length > 0)
        {
            var confirmed = await new MessageDialogWindow(
                    "跳过同名文件？",
                    UiLocalization.Format(
                        "目标目录中已有同名文件，或本次拖入包含重名文件。将跳过 {0:N0} 个文件，且不会覆盖任何原文件。",
                        conflicts.Length),
                    "继续并跳过",
                    "取消")
                .ShowDialog<bool>(this);
            if (!confirmed)
            {
                UiLocalization.SetText(LocalSummaryText, "已取消添加素材。");
                return;
            }
        }

        var succeeded = 0;
        var failed = 0;
        foreach (var plan in copyPlans.Where(plan => !conflictSources.Contains(plan.Source)))
        {
            UiLocalization.SetText(
                LocalSummaryText,
                "正在复制到“{0}”：{1}",
                targetFolder.DisplayName,
                Path.GetFileName(plan.Source));
            try
            {
                await CopyFileWithoutOverwriteAsync(plan.Source, plan.Destination);
                succeeded++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failed++;
                ClientDiagnostics.WriteException("local-folder-drop-copy", exception, isFatal: false);
            }
        }

        var indexedRoot = _catalog.Folders.FirstOrDefault(folder => folder.Id == folderId);
        if (succeeded > 0 && indexedRoot is not null)
        {
            var selectedAssetId = _selectedLocalAsset?.Id;
            var result = await _indexService.IndexFolderAsync(
                indexedRoot.Path,
                indexedRoot.IsExternalStorage);
            _catalog = result.Catalog;
            if (selectedAssetId.HasValue &&
                !_catalog.Assets.Any(asset => asset.Id == selectedAssetId.Value))
            {
                CloseDetail_OnClick(null, new RoutedEventArgs());
            }

            RebuildLocalFolderTree();
            ApplyLocalFilter();
        }

        UiLocalization.SetText(
            LocalSummaryText,
            "拖拽添加完成：成功 {0:N0} 个，跳过 {1:N0} 个，失败 {2:N0} 个。",
            succeeded,
            conflicts.Length + unsupportedCount,
            failed);
    }

    private static async Task CopyFileWithoutOverwriteAsync(
        string sourcePath,
        string destinationPath)
    {
        var temporaryPath = Path.Combine(
            Path.GetDirectoryName(destinationPath)!,
            $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.ial-importing");
        try
        {
            await using (var source = new FileStream(
                             sourcePath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             1024 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var destination = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             1024 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(destination, 1024 * 1024);
                await destination.FlushAsync();
            }

            File.Move(temporaryPath, destinationPath, overwrite: false);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                ClientDiagnostics.WriteException("local-folder-drop-cleanup", exception, isFatal: false);
            }
        }
    }

    private sealed record DroppedPathSnapshot(string[] Files, string[] Folders);

    private static DroppedPathSnapshot SnapshotDroppedPaths(DragEventArgs eventArgs)
    {
        var paths = DroppedStoragePaths(eventArgs.DataTransfer);
        return new DroppedPathSnapshot(
            paths.Where(File.Exists).ToArray(),
            paths.Where(Directory.Exists).ToArray());
    }

    private static DroppedPathSnapshot SnapshotDroppedPaths(IDataTransfer dataTransfer)
    {
        var paths = DroppedStoragePaths(dataTransfer);
        return new DroppedPathSnapshot(
            paths.Where(File.Exists).ToArray(),
            paths.Where(Directory.Exists).ToArray());
    }

    private static string[] DroppedStoragePaths(IDataTransfer dataTransfer)
    {
        IReadOnlyList<IStorageItem> items;
        try
        {
            items = dataTransfer.TryGetFiles()?.ToArray() ?? [];
        }
        catch (Exception)
        {
            return [];
        }

        var paths = new HashSet<string>(LocalPathComparer);
        foreach (var item in items)
        {
            try
            {
                var path = item.TryGetLocalPath();
                if (!string.IsNullOrWhiteSpace(path))
                {
                    paths.Add(Path.GetFullPath(path));
                }
            }
            catch (Exception exception) when (exception is IOException or
                                              UnauthorizedAccessException or
                                              ArgumentException or
                                              NotSupportedException)
            {
            }
        }

        return paths.ToArray();
    }

    private static bool ContainsFileTransfer(DragEventArgs eventArgs)
    {
        try
        {
            return eventArgs.DataTransfer.Contains(DataFormat.File);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async void RefreshLocal_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        CloseLocalImagePreview();
        await StopDetailPreviewAsync(savePosition: false);
        _previewSession.ResetForLibraryRefresh();
        UiLocalization.SetText(LocalSummaryText, "正在刷新素材库...");
        try
        {
            var selectedAssetId = _selectedLocalAsset?.Id;
            var onlineFolderIds = _catalog.Folders
                .Where(folder => folder.Availability != IndexedFolderAvailability.Offline)
                .Select(folder => folder.Id)
                .ToHashSet();
            var results = await _indexService.RefreshAllAsync();
            _catalog = await _indexService.GetCatalogAsync();
            var disconnected = results
                .Select(result => result.Folder)
                .Where(folder => onlineFolderIds.Contains(folder.Id) &&
                                 folder.Availability == IndexedFolderAvailability.Offline)
                .ToArray();
            var missing = results
                .Select(result => result.Folder)
                .Where(folder => folder.Availability == IndexedFolderAvailability.Missing)
                .ToArray();
            if (selectedAssetId.HasValue &&
                !_catalog.Assets.Any(asset => asset.Id == selectedAssetId.Value))
            {
                CloseDetail_OnClick(null, new RoutedEventArgs());
            }

            RebuildLocalFolderTree();
            ApplyLocalFilter();
            if (disconnected.Length > 0)
            {
                CloseDetailIfDisconnected(disconnected);
                await ShowDisconnectedStorageDialogAsync(disconnected);
            }
            else if (missing.Length > 0)
            {
                ShowMissingFolderStatus(missing);
            }
        }
        catch (Exception exception)
        {
            UiLocalization.SetText(LocalSummaryText, "刷新失败：{0}", exception.Message);
        }
    }

    private async void LocalStorageMonitor_OnTick(object? sender, EventArgs eventArgs) =>
        await CheckLocalStorageAvailabilityAsync();

    private async Task CheckLocalStorageAvailabilityAsync()
    {
        if (_isCheckingLocalStorage || _catalog.Folders.Length == 0)
        {
            return;
        }

        _isCheckingLocalStorage = true;
        try
        {
            var selectedAssetId = _selectedLocalAsset?.Id;
            var probe = await _indexService.ProbeStorageAvailabilityAsync();
            _catalog = probe.Catalog;
            var restoredFolders = new List<IndexedFolder>();
            foreach (var folder in probe.ReconnectedFolders)
            {
                var result = await _indexService.IndexFolderAsync(
                    folder.Path,
                    folder.IsExternalStorage);
                _catalog = result.Catalog;
                if (result.Folder.Availability is not (IndexedFolderAvailability.Offline or
                    IndexedFolderAvailability.Missing))
                {
                    restoredFolders.Add(result.Folder);
                }
            }

            if (probe.DisconnectedFolders.Count == 0 &&
                probe.MissingFolders.Count == 0 &&
                restoredFolders.Count == 0)
            {
                return;
            }

            CloseLocalImagePreview();
            if (selectedAssetId.HasValue &&
                !_catalog.Assets.Any(asset => asset.Id == selectedAssetId.Value))
            {
                CloseDetail_OnClick(null, new RoutedEventArgs());
            }
            CloseDetailIfDisconnected(probe.DisconnectedFolders);
            RebuildLocalFolderTree();
            ApplyLocalFilter();

            if (probe.DisconnectedFolders.Count > 0)
            {
                await ShowDisconnectedStorageDialogAsync(probe.DisconnectedFolders);
            }
            else if (probe.MissingFolders.Count > 0)
            {
                ShowMissingFolderStatus(probe.MissingFolders);
            }
            else
            {
                if (restoredFolders.Count == 1)
                {
                    UiLocalization.SetText(
                        LocalSummaryText,
                        "存储已重新连接：{0}",
                        restoredFolders[0].Path);
                }
                else
                {
                    UiLocalization.SetText(
                        LocalSummaryText,
                        "{0:N0} 个素材存储位置已重新连接",
                        restoredFolders.Count);
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // The window is closing.
        }
        catch (Exception exception)
        {
            if (_activePage == "local")
            {
                UiLocalization.SetText(LocalSummaryText, "检查本地存储失败：{0}", exception.Message);
            }
        }
        finally
        {
            _isCheckingLocalStorage = false;
        }
    }

    private void ShowMissingFolderStatus(IReadOnlyCollection<IndexedFolder> missingFolders)
    {
        if (missingFolders.Count == 1)
        {
            UiLocalization.SetText(
                LocalSummaryText,
                "素材文件夹已不存在，已移除其中失效的索引：{0}",
                missingFolders.First().Path);
            return;
        }

        UiLocalization.SetText(
            LocalSummaryText,
            "{0:N0} 个素材文件夹已不存在，已移除其中失效的索引",
            missingFolders.Count);
    }

    private void CloseDetailIfDisconnected(IEnumerable<IndexedFolder> disconnectedFolders)
    {
        if (_selectedLocalAsset is not { } selected)
        {
            return;
        }

        var disconnectedIds = disconnectedFolders.Select(folder => folder.Id).ToHashSet();
        if (_catalog.Assets.Any(asset => asset.Id == selected.Id &&
                                         disconnectedIds.Contains(asset.FolderId)))
        {
            CloseDetail_OnClick(null, new RoutedEventArgs());
        }
    }

    private async Task ShowDisconnectedStorageDialogAsync(
        IReadOnlyCollection<IndexedFolder> disconnectedFolders)
    {
        var paths = string.Join(Environment.NewLine, disconnectedFolders.Select(folder => folder.Path));
        if (disconnectedFolders.Count == 1)
        {
            UiLocalization.SetText(
                LocalSummaryText,
                "素材存储已断开：{0}",
                disconnectedFolders.First().Path);
        }
        else
        {
            UiLocalization.SetText(
                LocalSummaryText,
                "{0:N0} 个素材存储位置已断开",
                disconnectedFolders.Count);
        }
        await new MessageDialogWindow(
                "本地素材存储已断开",
                UiLocalization.Format(
                    "以下目录当前不可访问，相关素材已标记为离线，拖拽和打开操作已停用。重新连接存储设备后软件会自动恢复：{0}{0}{1}",
                    Environment.NewLine,
                    paths))
            .ShowDialog(this);
    }

    private void LocalSearch_OnTextChanged(object? sender, TextChangedEventArgs eventArgs)
    {
        if (!_isLoading)
        {
            ApplyLocalFilter();
        }
    }

    private void LocalFilter_OnSelectionChanged(object? sender, SelectionChangedEventArgs eventArgs)
    {
        if (!_isLoading && !_isLocalFilterLoading)
        {
            ApplyLocalFilter();
        }
    }

    private void LocalCategoryQuickFilter_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not Button { Tag: string category })
        {
            return;
        }

        _localCategoryFilterIndex = category switch
        {
            "audio" => 1,
            "video" => 2,
            "image" => 3,
            _ => 0
        };
        UpdateLocalCategoryQuickFilters();
        if (!_isLoading)
        {
            ApplyLocalFilter();
        }
    }

    private void UpdateLocalCategoryQuickFilters()
    {
        LocalAllCategoryButton.Classes.Set("selected", _localCategoryFilterIndex == 0);
        LocalAudioCategoryButton.Classes.Set("selected", _localCategoryFilterIndex == 1);
        LocalVideoCategoryButton.Classes.Set("selected", _localCategoryFilterIndex == 2);
        LocalImageCategoryButton.Classes.Set("selected", _localCategoryFilterIndex == 3);
    }

    private void LocalTagMode_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (LocalTagMultiSelectButton.IsChecked == true)
        {
            return;
        }

        var selected = LocalTagFilters.FirstOrDefault(item => !item.IsAll && item.IsSelected);
        SelectLocalTags(selected);
        UpdateLocalTagFilterLabel();
        RefreshLocalAssetsForFilterChange();
    }

    private void LocalTagFilter_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_isLocalFilterLoading ||
            sender is not ToggleButton { DataContext: CloudTagFilterItemViewModel selected })
        {
            return;
        }

        if (selected.IsAll)
        {
            SelectLocalTags(null);
        }
        else if (LocalTagMultiSelectButton.IsChecked != true)
        {
            SelectLocalTags(selected.IsSelected ? selected : null);
        }
        else
        {
            LocalTagFilters.First(item => item.IsAll).IsSelected = false;
            if (!LocalTagFilters.Any(item => !item.IsAll && item.IsSelected))
            {
                SelectLocalTags(null);
            }
        }

        UpdateLocalTagFilterLabel();
        if (LocalTagMultiSelectButton.IsChecked != true &&
            LocalTagFilterButton.Flyout is PopupFlyoutBase flyout)
        {
            flyout.Hide();
        }

        RefreshLocalAssetsForFilterChange();
    }

    private void SelectLocalTags(CloudTagFilterItemViewModel? selected)
    {
        foreach (var item in LocalTagFilters)
        {
            item.IsSelected = selected is null ? item.IsAll : ReferenceEquals(item, selected);
        }
    }

    private void UpdateLocalTagFilterLabel()
    {
        var availableLocalTags = _catalog.TagLibrary.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedTags = LocalTagFilters
            .Where(item => !item.IsAll && item.IsSelected)
            .Select(item => item.Name)
            .Where(availableLocalTags.Contains)
            .ToArray();
        var label = selectedTags.Length switch
        {
            0 => "全部标签",
            1 => selectedTags[0],
            _ => UiLocalization.Format("已选 {0:N0} 个标签", selectedTags.Length)
        };
        if (selectedTags.Length == 1)
        {
            LocalTagFilterLabel.Text = label;
        }
        else
        {
            UiLocalization.SetText(
                LocalTagFilterLabel,
                selectedTags.Length == 0 ? "全部标签" : "已选 {0:N0} 个标签",
                selectedTags.Length);
        }
    }

    private void RefreshLocalAssetsForFilterChange()
    {
        if (!_isLoading)
        {
            ApplyLocalFilter();
        }
    }

    private void LocalDateFilter_OnChanged(object? sender, SelectionChangedEventArgs eventArgs)
    {
        if (!_isLoading && !_isLocalFilterLoading)
        {
            ApplyLocalFilter();
        }
    }

    private void ApplyLocalFilter()
    {
        var selectedAssetIds = VisibleLocalAssets
            .Where(asset => asset.IsBatchSelected)
            .Select(asset => asset.Id)
            .ToHashSet();
        var detailAssetId = _selectedLocalAsset?.Id;
        var selectedMediaTypes = _localCategoryFilterIndex switch
        {
            1 => new[] { LocalMediaType.Audio },
            2 => new[] { LocalMediaType.Video },
            3 => new[] { LocalMediaType.Image },
            _ => []
        };
        var (sortBy, direction) = LocalSortFilter.SelectedIndex switch
        {
            0 => (LocalAssetSortField.AddedAt, LocalSortDirection.Descending),
            1 => (LocalAssetSortField.AddedAt, LocalSortDirection.Ascending),
            2 => (LocalAssetSortField.FileName, LocalSortDirection.Ascending),
            3 => (LocalAssetSortField.FileName, LocalSortDirection.Descending),
            4 => (LocalAssetSortField.Size, LocalSortDirection.Descending),
            _ => (LocalAssetSortField.FileName, LocalSortDirection.Ascending)
        };

        var selectedTags = LocalTagFilters
            .Where(item => !item.IsAll && item.IsSelected)
            .Select(item => item.Name)
            .ToArray();
        var folderScope = LocalAssetSearch.Apply(_catalog.Assets, new LocalAssetQuery
        {
            FolderId = _selectedLocalFolderId,
            RelativeDirectoryPath = _selectedLocalRelativeDirectoryPath
        });
        var result = LocalAssetSearch.Apply(_catalog.Assets, new LocalAssetQuery
        {
            SearchText = LocalSearchBox.Text,
            FolderId = _selectedLocalFolderId,
            RelativeDirectoryPath = _selectedLocalRelativeDirectoryPath,
            MediaTypes = selectedMediaTypes,
            Tags = selectedTags,
            TagMatch = TagMatchMode.Any,
            AddedFromUtc = StartOfLocalDateUtc(LocalFromDateFilter.SelectedDate),
            AddedToUtc = EndOfLocalDateUtc(LocalToDateFilter.SelectedDate),
            SortBy = sortBy,
            SortDirection = direction
        });

        _isLocalFilterLoading = true;
        var previousTags = selectedTags.ToHashSet(StringComparer.OrdinalIgnoreCase);
        LocalTagFilters.Clear();
        LocalTagFilters.Add(new CloudTagFilterItemViewModel(
            "全部标签",
            isAll: true,
            isSelected: previousTags.Count == 0));
        foreach (var tag in _catalog.TagLibrary.Order(StringComparer.OrdinalIgnoreCase))
        {
            LocalTagFilters.Add(new CloudTagFilterItemViewModel(
                tag,
                isAll: false,
                isSelected: previousTags.Contains(tag)));
        }

        if (!LocalTagFilters.Any(item => item.IsSelected))
        {
            LocalTagFilters[0].IsSelected = true;
        }

        UpdateLocalTagFilterLabel();
        _isLocalFilterLoading = false;

        CancelLocalThumbnailLoading();
        DisposeLocalAssetCards();
        foreach (var asset in result)
        {
            var card = AssetCardViewModel.FromLocalAsset(asset);
            card.IsBatchMode = _isLocalBatchMode;
            card.IsBatchSelected = selectedAssetIds.Contains(asset.Id);
            VisibleLocalAssets.Add(card);
        }

        if (detailAssetId is { } selectedDetailId)
        {
            var refreshedDetail = VisibleLocalAssets.FirstOrDefault(asset => asset.Id == selectedDetailId);
            if (refreshedDetail is null)
            {
                CloseDetail_OnClick(null, new RoutedEventArgs());
            }
            else
            {
                SelectLocalAsset(refreshedDetail);
            }
        }

        StartLocalThumbnailLoading();
        ScheduleAdaptiveAssetCardWidthUpdate();
        UpdateLocalSelectionState();

        var hasIndexedFolder = _catalog.Folders.Length > 0;
        EmptyLocalState.IsVisible = !hasIndexedFolder;
        LocalFilteredEmptyState.IsVisible = hasIndexedFolder && VisibleLocalAssets.Count == 0;
        ApplyLocalAssetDisplayMode();
        var unavailableCount = folderScope.Count(asset => !asset.IsAvailable);
        if (!hasIndexedFolder)
        {
            UiLocalization.SetText(LocalSummaryText, "尚未索引文件夹");
        }
        else if (unavailableCount == 0)
        {
            UiLocalization.SetText(
                LocalSummaryText,
                "{0:N0} / {1:N0} 个素材",
                VisibleLocalAssets.Count,
                folderScope.Count);
        }
        else
        {
            UiLocalization.SetText(
                LocalSummaryText,
                "{0:N0} / {1:N0} 个素材，{2:N0} 个离线",
                VisibleLocalAssets.Count,
                folderScope.Count,
                unavailableCount);
        }
    }

    private void StartLocalThumbnailLoading()
    {
        var generation = ++_localThumbnailGeneration;
        var cancellation = new CancellationTokenSource();
        Interlocked.Exchange(ref _localThumbnailCancellation, cancellation)?.Cancel();
        _ = LoadLocalThumbnailsAsync(generation, cancellation);
    }

    private async Task LoadLocalThumbnailsAsync(
        int generation,
        CancellationTokenSource cancellation)
    {
        try
        {
            foreach (var asset in VisibleLocalAssets
                         .Where(item => item.IsAvailable)
                         .ToArray())
            {
                cancellation.Token.ThrowIfCancellationRequested();
                try
                {
                    await LoadLocalDurationAsync(asset, cancellation.Token);
                    var thumbnail = asset.MediaType == LocalMediaType.Audio
                        ? await _mediaThumbnailService.GetOrCreateWaveformAsync(
                            asset.FullPath,
                            maximumWidth: 320,
                            cancellation.Token)
                        : await _mediaThumbnailService.GetOrCreateAsync(
                            asset.FullPath,
                            maximumEdge: 320,
                            cancellation.Token);
                    if (thumbnail is null || generation != _localThumbnailGeneration ||
                        !VisibleLocalAssets.Contains(asset))
                    {
                        continue;
                    }

                    try
                    {
                        asset.SetThumbnail(new Bitmap(thumbnail.CachePath));
                        if (_selectedLocalAsset?.Id == asset.Id && asset.MediaType != LocalMediaType.Audio)
                        {
                            ResetDetailArtwork(asset.Thumbnail);
                        }
                    }
                    catch (IOException)
                    {
                    }
                    catch (ArgumentException)
                    {
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    // A damaged asset leaves only that item on its placeholder.
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Interlocked.CompareExchange(ref _localThumbnailCancellation, null, cancellation);
            cancellation.Dispose();
        }
    }

    private async Task LoadLocalDurationAsync(
        AssetCardViewModel asset,
        CancellationToken cancellationToken)
    {
        if (!asset.IsTimedMedia)
        {
            return;
        }

        var source = new FileInfo(asset.FullPath);
        var cacheKey = $"{source.FullName}|{source.Length}|{source.LastWriteTimeUtc.Ticks}";
        if (_localDurationCache.TryGetValue(cacheKey, out var cached))
        {
            asset.SetDuration(cached);
            return;
        }

        var probe = await _mediaAnalyzer.ProbeAsync(asset.FullPath, cancellationToken);
        if (probe.Succeeded && probe.Information?.Duration is { } duration)
        {
            _localDurationCache[cacheKey] = duration;
            asset.SetDuration(duration);
        }
    }

    private void CancelLocalThumbnailLoading()
    {
        _localThumbnailGeneration++;
        Interlocked.Exchange(ref _localThumbnailCancellation, null)?.Cancel();
    }

    private void DisposeLocalAssetCards()
    {
        var cards = VisibleLocalAssets.ToArray();
        foreach (var card in cards)
        {
            card.Dispose();
        }
        VisibleLocalAssets.Clear();
    }

    private void StartCloudThumbnailLoading()
    {
        var generation = ++_cloudThumbnailGeneration;
        var cancellation = new CancellationTokenSource();
        Interlocked.Exchange(ref _cloudThumbnailCancellation, cancellation)?.Cancel();
        _ = LoadCloudThumbnailsAsync(generation, cancellation);
    }

    private async Task LoadCloudThumbnailsAsync(
        int generation,
        CancellationTokenSource cancellation)
    {
        try
        {
            if (_cloudDerivativeService is null)
            {
                return;
            }

            var targets = VisibleCloudAssets
                .Concat(ProfileAssets)
                .Where(item => item.Asset.HasOriginal)
                .GroupBy(item => (item.Id, item.Asset.CurrentVersionId))
                .Select(group => group.ToArray())
                .ToArray();
            foreach (var cards in targets)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                try
                {
                    var cachePath = await _cloudDerivativeService.GetCachedDerivativeAsync(
                        cards[0].Asset,
                        AssetDerivativeKind.Thumbnail,
                        cancellation.Token);
                    if (cachePath is null || generation != _cloudThumbnailGeneration)
                    {
                        continue;
                    }

                    foreach (var card in cards)
                    {
                        TrySetCloudThumbnailFromCache(card, cachePath, generation);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    // A failed derivative leaves only that asset on its placeholder.
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Interlocked.CompareExchange(ref _cloudThumbnailCancellation, null, cancellation);
            cancellation.Dispose();
        }
    }

    private bool TrySetCloudThumbnailFromCache(
        CloudAssetCardViewModel asset,
        string cachePath,
        int generation)
    {
        if (!File.Exists(cachePath) || generation != _cloudThumbnailGeneration ||
            !VisibleCloudAssets.Contains(asset) && !ProfileAssets.Contains(asset))
        {
            return false;
        }

        try
        {
            asset.SetThumbnail(new Bitmap(cachePath));
            if (_selectedCloudAsset?.Id == asset.Id &&
                asset.Asset.Category is not (ApiAssetCategory.Bgm or ApiAssetCategory.SoundEffect))
            {
                ResetDetailArtwork(asset.Thumbnail);
            }
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private void CancelCloudThumbnailLoading()
    {
        _cloudThumbnailGeneration++;
        Interlocked.Exchange(ref _cloudThumbnailCancellation, null)?.Cancel();
    }

    private void DisposeCloudAssetCards()
    {
        var cards = VisibleCloudAssets.ToArray();
        foreach (var card in cards)
        {
            card.Dispose();
        }
        VisibleCloudAssets.Clear();

        UpdateCloudSelectionState();
    }

    private void DisposeProfileAssetCards()
    {
        var cards = ProfileAssets.ToArray();
        foreach (var card in cards)
        {
            card.Dispose();
        }
        ProfileAssets.Clear();
    }

    private void DisposeUserCards()
    {
        var cards = VisibleUsers.ToArray();
        foreach (var card in cards)
        {
            card.Dispose();
        }
        VisibleUsers.Clear();
    }

    private async void LocalViewMode_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not Button { Tag: string value } ||
            !Enum.TryParse<AssetDisplayMode>(value, ignoreCase: true, out var mode))
        {
            return;
        }

        _settings = _settings with { LocalAssetDisplayMode = mode };
        ApplyLocalAssetDisplayMode();
        try
        {
            await SaveSettingsAsync();
        }
        catch (Exception exception)
        {
            UiLocalization.SetText(LocalSummaryText, "视图设置保存失败：{0}", exception.Message);
        }
    }

    private void ApplyLocalAssetDisplayMode()
    {
        var isGrid = _settings.LocalAssetDisplayMode == AssetDisplayMode.Grid;
        var hasResults = _catalog.Folders.Length > 0 && VisibleLocalAssets.Count > 0;
        LocalGridViewButton.Classes.Set("selected", isGrid);
        LocalListViewButton.Classes.Set("selected", !isGrid);
        LocalAssetScroll.IsVisible = hasResults && isGrid;
        LocalAssetListScroll.IsVisible = hasResults && !isGrid;
    }

    private async void CloudViewMode_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not Button { Tag: string value } ||
            !Enum.TryParse<AssetDisplayMode>(value, ignoreCase: true, out var mode))
        {
            return;
        }

        _settings = _settings with { CloudAssetDisplayMode = mode };
        ApplyCloudAssetDisplayMode();
        try
        {
            await SaveSettingsAsync();
        }
        catch (Exception exception)
        {
            UiLocalization.SetText(CloudSummaryText, "视图设置保存失败：{0}", exception.Message);
        }
    }

    private void ApplyCloudAssetDisplayMode()
    {
        var showsLuts = _cloudCategoryFilterIndex == 5;
        var isGrid = _settings.CloudAssetDisplayMode == AssetDisplayMode.Grid;
        var hasResults = _currentUser is not null && VisibleCloudAssets.Count > 0;
        CloudGridViewButton.Classes.Set("selected", isGrid);
        CloudListViewButton.Classes.Set("selected", !isGrid);
        CloudLutGridViewButton.Classes.Set("selected", isGrid);
        CloudLutListViewButton.Classes.Set("selected", !isGrid);
        CloudAssetFilterPanel.IsVisible = !showsLuts && _cloudFiltersExpanded;
        CloudDirectoryHeader.IsVisible = !showsLuts && _currentUser is not null;
        ApplyCloudFolderHeight();
        CloudAssetSectionHeader.IsVisible = !showsLuts && _currentUser is not null;
        CloudLutViewModePanel.IsVisible = showsLuts;
        CloudAssetScroll.IsVisible = !showsLuts && hasResults && isGrid;
        CloudAssetListScroll.IsVisible = !showsLuts && hasResults && !isGrid;
        CloudEmptyState.IsVisible = !showsLuts && _currentUser is not null && !hasResults;
        CloudBatchToolbar.IsVisible = !showsLuts && _currentUser is not null;
        CloudLutScroll.IsVisible = showsLuts && _currentUser is not null && SharedCloudLuts.Count > 0 && isGrid;
        CloudLutListScroll.IsVisible = showsLuts && _currentUser is not null && SharedCloudLuts.Count > 0 && !isGrid;
        CloudLutEmptyState.IsVisible = showsLuts && _currentUser is not null && SharedCloudLuts.Count == 0;
        UpdateCloudSelectionState();
        UpdateCloudLutSelectionState();
    }

    private void ApplyCloudFolderHeight()
    {
        var visible = CloudDirectoryHeader.IsVisible;
        CloudFolderHeightSplitter.IsVisible = visible;
        var row = CloudContentsGrid.RowDefinitions[1];
        // Header + breadcrumb + one complete 58px folder card, including its spacing.
        var minimum = Math.Max(156, CloudDirectoryHeader.RowDefinitions[0].ActualHeight +
            CloudDirectoryHeader.RowDefinitions[1].ActualHeight + 16 + 66 + 8);
        row.MinHeight = visible ? minimum : 0;
        row.MaxHeight = visible ? Math.Max(minimum, CloudContentsGrid.Bounds.Height -
            CloudContentsGrid.RowDefinitions[0].ActualHeight - 12 - 120) : double.PositiveInfinity;
        row.Height = new GridLength(visible ? Math.Clamp(_settings.CloudFolderHeight, minimum, row.MaxHeight) : 0);
        CloudContentsGrid.RowDefinitions[2].Height = new GridLength(visible ? 12 : 0);
    }

    private async void CloudFolderHeight_OnReleased(object? sender, PointerReleasedEventArgs args)
    {
        if (_isLoading || !CloudDirectoryHeader.IsVisible) return;
        _settings = _settings with { CloudFolderHeight = Math.Max(156, CloudContentsGrid.RowDefinitions[1].ActualHeight) };
        try { await SaveSettingsAsync(); }
        catch (Exception exception) { ClientDiagnostics.WriteException("cloud-folder-height-save", exception, isFatal: false); }
    }

    private void CloudAssetSelection_OnChanged(object? sender, RoutedEventArgs eventArgs)
    {
        if (_isUpdatingCloudSelection)
        {
            return;
        }

        if (sender is CheckBox
            {
                DataContext: CloudAssetCardViewModel asset,
                IsChecked: { } isChecked
            })
        {
            asset.IsSelected = isChecked;
        }

        UpdateCloudSelectionState();
    }

    private void CloudSelectAll_OnChanged(object? sender, RoutedEventArgs eventArgs)
    {
        if (_isUpdatingCloudSelection || CloudSelectAllCheckBox.IsChecked is not { } isChecked)
        {
            return;
        }

        _isUpdatingCloudSelection = true;
        try
        {
            foreach (var asset in VisibleCloudAssets)
            {
                asset.IsSelected = isChecked;
            }
        }
        finally
        {
            _isUpdatingCloudSelection = false;
        }

        UpdateCloudSelectionState();
    }

    private void UpdateCloudSelectionState()
    {
        var selected = VisibleCloudAssets.Where(asset => asset.IsSelected).ToArray();
        var selectedOwnCount = _currentUser is null
            ? 0
            : selected.Count(asset => asset.Asset.UploadedBy.Id == _currentUser.Id);
        var allOwn = selected.Length > 0 && selectedOwnCount == selected.Length;
        var allDownloadable = selected.Length > 0 && selected.All(asset => asset.Asset.HasOriginal);
        var isAdmin = _currentUser?.IsAdmin == true;

        _isUpdatingCloudSelection = true;
        try
        {
            CloudSelectAllCheckBox.IsEnabled = !_isCloudBatchOperationRunning && VisibleCloudAssets.Count > 0;
            CloudSelectAllCheckBox.IsChecked = selected.Length switch
            {
                0 => false,
                _ when selected.Length == VisibleCloudAssets.Count => true,
                _ => null
            };
        }
        finally
        {
            _isUpdatingCloudSelection = false;
        }

        var summaryFormat = selected.Length switch
        {
            0 => "未选择素材",
            _ when !allOwn && !isAdmin => "已选 {0:N0} 项（含非本人素材，仅可批量下载）",
            _ when !allDownloadable => "已选 {0:N0} 项（含等待上传的素材）",
            _ => "已选 {0:N0} 项"
        };
        UiLocalization.SetText(CloudSelectionSummaryText, summaryFormat, selected.Length);
        BatchDownloadCloudAssetsButton.IsEnabled = !_isCloudBatchOperationRunning && allDownloadable &&
            HasCurrentUserPermission("assets.download");
        BatchTagCloudAssetsButton.IsEnabled = !_isCloudBatchOperationRunning && allOwn &&
            HasCurrentUserPermission("assets.tags");
        BatchMoveCloudAssetsButton.IsEnabled = !_isCloudBatchOperationRunning && selected.Length > 0 &&
            (isAdmin || allOwn && HasCurrentUserPermission("assets.edit-own"));
        BatchRecycleCloudAssetsButton.IsEnabled = !_isCloudBatchOperationRunning && allOwn &&
            HasCurrentUserPermission("assets.delete-own");
    }

    private void SetCloudBatchOperationRunning(bool isRunning)
    {
        _isCloudBatchOperationRunning = isRunning;
        UpdateCloudSelectionState();
    }

    private async void BatchDownloadCloudAssets_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_transferService is null)
        {
            return;
        }

        var selected = VisibleCloudAssets
            .Where(asset => asset.IsSelected)
            .Select(asset => asset.Asset)
            .ToArray();
        if (selected.Length == 0 || selected.Any(asset => !asset.HasOriginal))
        {
            return;
        }

        var destination = await new CloudDownloadDestinationWindow(LocalFolderNodes, _settings.DownloadToDefaultDirectory)
            .ShowDialog<CloudDownloadDestination?>(this);
        if (destination is null)
        {
            UiLocalization.SetText(CloudSummaryText, "已取消批量下载。");
            return;
        }

        var operationDirectory = destination.DirectoryPath;
        _settings = _settings with
        {
            PersistentDownloadDirectory = destination.SetAsDefault
                ? destination.DirectoryPath
                : _settings.PersistentDownloadDirectory,
            DownloadToDefaultDirectory = destination.UseDefaultForFuture
        };
        if (destination.SetAsDefault)
        {
            DownloadDirectoryText.Text = destination.DirectoryPath;
        }
        await SaveSettingsAsync();

        SetCloudBatchOperationRunning(true);
        try
        {
            var succeeded = 0;
            var failed = 0;
            foreach (var asset in selected)
            {
                try
                {
                    if (await EnsureCloudAssetDownloadedAsync(asset, operationDirectory) is not null)
                    {
                        succeeded++;
                    }
                }
                catch (OperationCanceledException)
                {
                    UiLocalization.SetText(CloudSummaryText, "已取消批量下载。");
                    return;
                }
                catch
                {
                    failed++;
                }
            }

            if (failed == 0)
            {
                UiLocalization.SetText(CloudSummaryText, "批量下载完成：{0:N0} 个素材。", succeeded);
            }
            else
            {
                UiLocalization.SetText(
                    CloudSummaryText,
                    "批量下载完成：成功 {0:N0} 个，失败 {1:N0} 个；可在传输任务中查看详情。",
                    succeeded,
                    failed);
            }

            if (succeeded > 0)
            {
                ShowTransientNotification("下载完成：{0:N0} 个素材", succeeded);
            }
        }
        finally
        {
            SetCloudBatchOperationRunning(false);
        }
    }

    private async void BatchTagCloudAssets_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_cloudTagLibraryService is null || _currentUser is null)
        {
            return;
        }

        var selected = VisibleCloudAssets.Where(asset => asset.IsSelected).ToArray();
        if (selected.Length == 0 || selected.Any(asset => asset.Asset.UploadedBy.Id != _currentUser.Id))
        {
            return;
        }

        var batchOperationStarted = false;
        try
        {
            var availableTags = await _cloudTagLibraryService.ListAsync();
            var deletedTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var tagsChanged = false;
            var result = await new TagPickerWindow(
                    "批量添加标签",
                    UiLocalization.Format(
                        "为已选的 {0:N0} 个本人素材追加标签，原有标签会保留。",
                        selected.Length),
                    availableTags.Select(tag => new TagPickerEntry(tag.Id, tag.Name, tag.UsageCount)),
                    selectedTags: [],
                    async name =>
                    {
                        var created = await _cloudTagLibraryService.CreateAsync(name);
                        tagsChanged = true;
                        return new TagPickerEntry(created.Id, created.Name, created.UsageCount);
                    },
                    _currentUser.IsAdmin
                        ? async entry =>
                        {
                            if (entry.Id is not { } tagId)
                            {
                                throw new InvalidOperationException(
                                    UiLocalization.Text("云端标签缺少有效 ID。"));
                            }

                            await _cloudTagLibraryService.DeleteAsync(tagId);
                            deletedTags.Add(entry.Name);
                            tagsChanged = true;
                        }
                        : null)
                .ShowDialog<TagPickerResult?>(this);
            if (result is null)
            {
                if (tagsChanged)
                {
                    await RefreshCloudAssetsAsync();
                }

                return;
            }

            if (result.Tags.Count == 0)
            {
                UiLocalization.SetText(CloudSummaryText, "未选择要追加的标签。");
                if (tagsChanged)
                {
                    await RefreshCloudAssetsAsync();
                }

                return;
            }

            var assignments = selected.ToDictionary(
                asset => asset.Id,
                asset => TagRules.NormalizeSelection(asset.Asset.Tags
                    .Where(tag => !deletedTags.Contains(tag))
                    .Concat(result.Tags)));
            batchOperationStarted = true;
            SetCloudBatchOperationRunning(true);
            var succeeded = 0;
            var failedIds = new HashSet<Guid>();
            foreach (var asset in selected)
            {
                try
                {
                    await _cloudTagLibraryService.AssignExistingAsync(asset.Id, assignments[asset.Id]);
                    succeeded++;
                }
                catch
                {
                    failedIds.Add(asset.Id);
                }
            }

            foreach (var asset in selected)
            {
                asset.IsSelected = failedIds.Contains(asset.Id);
            }

            await RefreshCloudAssetsAsync();
            foreach (var asset in VisibleCloudAssets.Where(asset => failedIds.Contains(asset.Id)))
            {
                asset.IsSelected = true;
            }
            UpdateCloudSelectionState();
            if (failedIds.Count == 0)
            {
                UiLocalization.SetText(CloudSummaryText, "已为 {0:N0} 个素材追加标签。", succeeded);
            }
            else
            {
                UiLocalization.SetText(
                    CloudSummaryText,
                    "批量标签完成：成功 {0:N0} 个，失败 {1:N0} 个；失败素材已保留选中。",
                    succeeded,
                    failedIds.Count);
            }
        }
        catch (Exception exception)
        {
            var message = UserMessage(exception);
            try
            {
                await RefreshCloudAssetsAsync();
            }
            catch
            {
                // Preserve the original batch failure as the user-facing result.
            }

            UiLocalization.SetText(CloudSummaryText, "批量标签保存失败：{0}", message);
            UpdateCloudSelectionState();
        }
        finally
        {
            if (batchOperationStarted)
            {
                SetCloudBatchOperationRunning(false);
            }
        }
    }

    private async void BatchRecycleCloudAssets_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_api is null || _currentUser is null)
        {
            return;
        }

        var selected = VisibleCloudAssets.Where(asset => asset.IsSelected).ToArray();
        if (selected.Length == 0 || selected.Any(asset => asset.Asset.UploadedBy.Id != _currentUser.Id))
        {
            return;
        }

        var confirmed = await new MessageDialogWindow(
                "批量移至回收站",
                UiLocalization.Format(
                    "将选中的 {0:N0} 个素材移至个人回收站，并保留 15 天。",
                    selected.Length),
                "确认移入",
                "取消")
            .ShowDialog<bool>(this);
        if (!confirmed)
        {
            return;
        }

        SetCloudBatchOperationRunning(true);
        try
        {
            var succeeded = 0;
            var failedIds = new HashSet<Guid>();
            foreach (var asset in selected)
            {
                try
                {
                    await _api.RecycleAssetAsync(asset.Id);
                    succeeded++;
                }
                catch
                {
                    failedIds.Add(asset.Id);
                }
            }

            await Task.WhenAll(RefreshCloudAssetsAsync(), RefreshMyRecycleBinAsync());
            foreach (var asset in VisibleCloudAssets.Where(asset => failedIds.Contains(asset.Id)))
            {
                asset.IsSelected = true;
            }
            UpdateCloudSelectionState();
            var profileRefreshFailed = false;
            try
            {
                await RefreshDisplayedProfileAssetsAsync();
            }
            catch
            {
                profileRefreshFailed = true;
            }

            if (failedIds.Count == 0 && !profileRefreshFailed)
            {
                UiLocalization.SetText(CloudSummaryText, "已将 {0:N0} 个素材移至回收站。", succeeded);
            }
            else
            {
                UiLocalization.SetText(
                    CloudSummaryText,
                    profileRefreshFailed
                        ? "批量移入完成：成功 {0:N0} 个，失败 {1:N0} 个，个人主页刷新失败。"
                        : "批量移入完成：成功 {0:N0} 个，失败 {1:N0} 个；失败素材已保留选中。",
                    succeeded,
                    failedIds.Count);
            }
        }
        finally
        {
            SetCloudBatchOperationRunning(false);
        }
    }

    private void CloudFilter_OnChanged(object? sender, RoutedEventArgs eventArgs)
    {
        if (!_isLoading && !_isCloudFilterLoading && _currentUser is not null)
        {
            _ = RefreshCloudAssetsAsync();
        }
    }

    private void CloudCategoryQuickFilter_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not Button { Tag: string category })
        {
            return;
        }

        var selectedIndex = category switch
        {
            "bgm" => 1,
            "sound" => 2,
            "video" => 3,
            "image" => 4,
            "lut" => 5,
            _ => 0
        };
        _cloudCategoryFilterIndex = selectedIndex;
        UpdateCloudCategoryQuickFilters();
        RefreshCloudAssetsForFilterChange();
    }

    private void UpdateCloudCategoryQuickFilters()
    {
        var selectedIndex = _cloudCategoryFilterIndex;
        CloudAllCategoryButton.Classes.Set("selected", selectedIndex <= 0);
        CloudBgmCategoryButton.Classes.Set("selected", selectedIndex == 1);
        CloudSoundCategoryButton.Classes.Set("selected", selectedIndex == 2);
        CloudVideoCategoryButton.Classes.Set("selected", selectedIndex == 3);
        CloudImageCategoryButton.Classes.Set("selected", selectedIndex == 4);
        CloudLutCategoryButton.Classes.Set("selected", selectedIndex == 5);
        ApplyCloudAssetDisplayMode();
    }

    private void CloudTagMode_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (CloudTagMultiSelectButton.IsChecked == true)
        {
            return;
        }

        var selected = CloudTagFilters.FirstOrDefault(item => !item.IsAll && item.IsSelected);
        SelectCloudTags(selected);
        UpdateCloudTagFilterLabel();
        RefreshCloudAssetsForFilterChange();
    }

    private void CloudTagFilter_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_isCloudFilterLoading ||
            sender is not ToggleButton { DataContext: CloudTagFilterItemViewModel selected })
        {
            return;
        }

        if (selected.IsAll)
        {
            SelectCloudTags(null);
        }
        else if (CloudTagMultiSelectButton.IsChecked != true)
        {
            SelectCloudTags(selected.IsSelected ? selected : null);
        }
        else
        {
            CloudTagFilters.First(item => item.IsAll).IsSelected = false;
            if (!CloudTagFilters.Any(item => !item.IsAll && item.IsSelected))
            {
                SelectCloudTags(null);
            }
        }

        UpdateCloudTagFilterLabel();
        if (CloudTagMultiSelectButton.IsChecked != true &&
            CloudTagFilterButton.Flyout is PopupFlyoutBase flyout)
        {
            flyout.Hide();
        }

        RefreshCloudAssetsForFilterChange();
    }

    private void SelectCloudTags(CloudTagFilterItemViewModel? selected)
    {
        foreach (var item in CloudTagFilters)
        {
            item.IsSelected = selected is null ? item.IsAll : ReferenceEquals(item, selected);
        }
    }

    private void UpdateCloudTagFilterLabel()
    {
        var selectedTags = CloudTagFilters
            .Where(item => !item.IsAll && item.IsSelected)
            .Select(item => item.Name)
            .ToArray();
        var label = selectedTags.Length switch
        {
            0 => "全部标签",
            1 => selectedTags[0],
            _ => UiLocalization.Format("已选 {0:N0} 个标签", selectedTags.Length)
        };
        if (selectedTags.Length == 1)
        {
            CloudTagFilterLabel.Text = label;
        }
        else
        {
            UiLocalization.SetText(
                CloudTagFilterLabel,
                selectedTags.Length == 0 ? "全部标签" : "已选 {0:N0} 个标签",
                selectedTags.Length);
        }
    }

    private void RefreshCloudAssetsForFilterChange()
    {
        if (!_isLoading && _currentUser is not null)
        {
            _ = RefreshCloudAssetsAsync();
        }
    }

    private void CloudDateFilter_OnChanged(object? sender, SelectionChangedEventArgs eventArgs)
    {
        if (!_isLoading && !_isCloudFilterLoading && _currentUser is not null)
        {
            _ = RefreshCloudAssetsAsync();
        }
    }

    private async void RefreshCloud_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        await StopDetailPreviewAsync(savePosition: false);
        _previewSession.ResetForLibraryRefresh();
        await RefreshCloudLibraryAsync();
    }

    private async Task RefreshCloudAssetsAsync()
    {
        if (_api is null || _currentUser is null)
        {
            return;
        }

        var requestVersion = Interlocked.Increment(ref _cloudRequestVersion);
        var expectedUserId = _currentUser.Id;
        var cancellation = StartLatestRefresh(ref _cloudRefreshCancellation);
        var cancellationToken = cancellation.Token;
        var category = _cloudCategoryFilterIndex switch
        {
            1 => ApiAssetCategory.Bgm,
            2 => ApiAssetCategory.SoundEffect,
            3 => ApiAssetCategory.Video,
            4 => ApiAssetCategory.Image,
            _ => (ApiAssetCategory?)null
        };
        var (sort, order) = CloudSortFilter.SelectedIndex switch
        {
            0 => (ApiAssetSort.UploadedAt, ApiSortOrder.Descending),
            1 => (ApiAssetSort.UploadedAt, ApiSortOrder.Ascending),
            2 => (ApiAssetSort.Name, ApiSortOrder.Ascending),
            3 => (ApiAssetSort.Name, ApiSortOrder.Descending),
            4 => (ApiAssetSort.Size, ApiSortOrder.Descending),
            _ => (ApiAssetSort.Name, ApiSortOrder.Ascending)
        };
        var requestedTags = CloudTagFilters
            .Where(item => !item.IsAll && item.IsSelected)
            .Select(item => item.Name)
            .ToArray();
        var serverTag = requestedTags.Length == 1 ? requestedTags[0] : null;

        UiLocalization.SetText(CloudSummaryText, "正在读取共享素材...");
        try
        {
            if (_cloudCategoryFilterIndex == 5)
            {
                var luts = await ListAllSharedCloudLutsAsync(cancellationToken);
                if (requestVersion != Volatile.Read(ref _cloudRequestVersion) ||
                    _currentUser?.Id != expectedUserId ||
                    cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                RebuildSharedCloudLuts(luts);
                ApplyCloudAssetDisplayMode();
                UiLocalization.SetText(CloudSummaryText, "共 {0:N0} 个团队 LUT", luts.Count);
                UiLocalization.SetText(CloudConnectionText, "已登录：{0}", _currentUser.DisplayName);
                return;
            }

            var query = new ApiAssetListQuery(
                Search: CloudSearchBox.Text,
                Category: category,
                Tag: serverTag,
                Sort: sort,
                Order: order,
                UploadedFrom: StartOfLocalDateUtc(CloudFromDateFilter.SelectedDate),
                UploadedTo: EndOfLocalDateUtc(CloudToDateFilter.SelectedDate),
                FolderId: _selectedCloudFolderId,
                IncludeDescendantFolders: CloudRecursiveSearchToggle.IsChecked == true &&
                                          !string.IsNullOrWhiteSpace(CloudSearchBox.Text),
                RootOnly: _selectedCloudFolderId is null &&
                          !(CloudRecursiveSearchToggle.IsChecked == true &&
                            !string.IsNullOrWhiteSpace(CloudSearchBox.Text)));
            var assetsTask = ListAllAssetsAsync(query, cancellationToken);
            var tagsTask = _api.ListTagsAsync(cancellationToken);
            await Task.WhenAll(assetsTask, tagsTask);
            if (requestVersion != Volatile.Read(ref _cloudRequestVersion) ||
                _currentUser?.Id != expectedUserId ||
                cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var availableTags = await tagsTask;
            var availableTagNames = availableTags
                .Select(tag => tag.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var selectedTags = requestedTags.Where(availableTagNames.Contains).ToArray();
            var assets = await assetsTask;
            if (requestedTags.Length == 1 && selectedTags.Length == 0)
            {
                assets = await ListAllAssetsAsync(query with { Tag = null }, cancellationToken);
            }
            else if (requestedTags.Length > 1 && selectedTags.Length > 0)
            {
                var selectedTagSet = selectedTags.ToHashSet(StringComparer.OrdinalIgnoreCase);
                assets = assets
                    .Where(asset => asset.Tags.Any(selectedTagSet.Contains))
                    .ToArray();
            }

            if (requestVersion != Volatile.Read(ref _cloudRequestVersion) ||
                _currentUser?.Id != expectedUserId ||
                cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var selectedAssetIds = VisibleCloudAssets
                .Where(asset => asset.IsSelected)
                .Select(asset => asset.Id)
                .ToHashSet();
            _cloudAssets = assets;
            ClearSharedCloudLuts();
            var detailAssetId = _selectedCloudAsset?.Id;
            CancelCloudThumbnailLoading();
            DisposeCloudAssetCards();
            foreach (var asset in _cloudAssets)
            {
                VisibleCloudAssets.Add(new CloudAssetCardViewModel(asset)
                {
                    IsSelected = selectedAssetIds.Contains(asset.Id)
                });
            }
            StartCloudThumbnailLoading();
            ScheduleAdaptiveAssetCardWidthUpdate();
            if (detailAssetId is { } selectedDetailId)
            {
                var refreshedDetail = VisibleCloudAssets.FirstOrDefault(asset => asset.Id == selectedDetailId);
                if (refreshedDetail is null)
                {
                    CloseDetail_OnClick(null, new RoutedEventArgs());
                }
                else
                {
                    SelectCloudAsset(refreshedDetail);
                }
            }

            _isCloudFilterLoading = true;
            var previousTags = selectedTags.ToHashSet(StringComparer.OrdinalIgnoreCase);
            CloudTagFilters.Clear();
            CloudTagFilters.Add(new CloudTagFilterItemViewModel(
                "全部标签",
                isAll: true,
                isSelected: previousTags.Count == 0));
            foreach (var tag in availableTags.OrderBy(tag => tag.Name, StringComparer.OrdinalIgnoreCase))
            {
                CloudTagFilters.Add(new CloudTagFilterItemViewModel(
                    tag.Name,
                    isAll: false,
                    isSelected: previousTags.Contains(tag.Name)));
            }

            if (!CloudTagFilters.Any(item => item.IsSelected))
            {
                CloudTagFilters[0].IsSelected = true;
            }

            UpdateCloudTagFilterLabel();
            _isCloudFilterLoading = false;
            ApplyCloudAssetDisplayMode();
            UiLocalization.SetText(CloudSummaryText, "共 {0:N0} 个素材", assets.Count);
            UiLocalization.SetText(CloudConnectionText, "已登录：{0}", _currentUser.DisplayName);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (requestVersion != Volatile.Read(ref _cloudRequestVersion) ||
                _currentUser?.Id != expectedUserId ||
                cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (await HandleSessionFailureAsync(exception) ||
                requestVersion != Volatile.Read(ref _cloudRequestVersion) ||
                _currentUser?.Id != expectedUserId ||
                cancellationToken.IsCancellationRequested)
            {
                return;
            }

            _isCloudFilterLoading = false;
            UiLocalization.SetText(CloudSummaryText, "刷新失败：{0}", UserMessage(exception));
        }
        finally
        {
            FinishLatestRefresh(ref _cloudRefreshCancellation, cancellation);
        }
    }

    private void UpdateAdaptiveAssetCardWidths()
    {
        ApplyAdaptiveCardWidth(VisibleLocalAssets, LocalAssetScroll.Bounds.Width);
        ApplyAdaptiveCardWidth(VisibleCloudAssets, CloudAssetScroll.Bounds.Width);
    }

    private void ScheduleAdaptiveAssetCardWidthUpdate()
    {
        if (_adaptiveCardWidthUpdatePending || _shutdownStarted)
        {
            return;
        }

        _adaptiveCardWidthUpdatePending = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                _adaptiveCardWidthUpdatePending = false;
                if (!_shutdownStarted)
                {
                    UpdateAdaptiveAssetCardWidths();
                }
            },
            DispatcherPriority.Background);
    }

    private static void ApplyAdaptiveCardWidth<T>(IEnumerable<T> items, double viewportWidth)
        where T : class
    {
        const double gap = 12;
        const double minimumWidth = 190;
        const double preferredWidth = 218;
        const double maximumWidth = 300;
        var available = viewportWidth - 14;
        if (available < minimumWidth)
        {
            return;
        }

        var minimumColumns = Math.Max(1, (int)Math.Ceiling((available + gap) / (maximumWidth + gap)));
        var maximumColumns = Math.Max(1, (int)Math.Floor((available + gap) / (minimumWidth + gap)));
        var preferredColumns = Math.Max(1, (int)Math.Floor((available + gap) / (preferredWidth + gap)));
        var columns = Math.Clamp(preferredColumns, minimumColumns, maximumColumns);
        var width = Math.Clamp((available - gap * (columns - 1)) / columns, minimumWidth, maximumWidth);
        foreach (var item in items)
        {
            switch (item)
            {
                case AssetCardViewModel local:
                    local.CardWidth = width;
                    break;
                case CloudAssetCardViewModel cloud:
                    cloud.CardWidth = width;
                    break;
            }
        }
    }

    private async void RefreshUsers_OnClick(object? sender, RoutedEventArgs eventArgs) =>
        await RefreshUsersAsync();

    private void UserSearch_OnTextChanged(object? sender, TextChangedEventArgs eventArgs)
    {
        if (!_isLoading && _currentUser is not null)
        {
            _ = RefreshUsersAsync();
        }
    }

    private async Task RefreshUsersAsync()
    {
        if (!UsersPage.IsVisible || _api is null || _currentUser is null)
        {
            return;
        }

        var requestVersion = Interlocked.Increment(ref _userRequestVersion);
        var expectedUserId = _currentUser.Id;
        var cancellation = StartLatestRefresh(ref _usersRefreshCancellation);
        var cancellationToken = cancellation.Token;
        UiLocalization.SetText(UsersSummaryText, "正在读取团队成员...");
        try
        {
            var page = await _api.ListUsersAsync(
                new ApiUserListQuery(UserSearchBox.Text, PageSize: 100),
                cancellationToken);
            if (requestVersion != Volatile.Read(ref _userRequestVersion) ||
                _currentUser?.Id != expectedUserId ||
                cancellationToken.IsCancellationRequested)
            {
                return;
            }

            _users = page.Items;
            DisposeUserCards();
            var avatarCards = new List<UserCardViewModel>();
            foreach (var user in _users)
            {
                var card = new UserCardViewModel(user, _currentUser?.Id);
                VisibleUsers.Add(card);
                if (user.HasAvatar)
                {
                    avatarCards.Add(card);
                }
            }

            foreach (var card in avatarCards)
            {
                await LoadUserCardAvatarAsync(card, requestVersion, cancellationToken);
            }

            UsersEmptyState.IsVisible = page.Items.Count == 0;
            if (page.Total > page.Items.Count)
            {
                UiLocalization.SetText(
                    UsersSummaryText,
                    "显示前 {0:N0} / {1:N0} 位成员",
                    page.Items.Count,
                    page.Total);
            }
            else
            {
                UiLocalization.SetText(
                    UsersSummaryText,
                    "共 {0:N0} 位成员（含已停用账号）",
                    page.Total);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (requestVersion != Volatile.Read(ref _userRequestVersion) ||
                _currentUser?.Id != expectedUserId ||
                cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (await HandleSessionFailureAsync(exception) ||
                requestVersion != Volatile.Read(ref _userRequestVersion) ||
                _currentUser?.Id != expectedUserId ||
                cancellationToken.IsCancellationRequested)
            {
                return;
            }

            UiLocalization.SetText(UsersSummaryText, "刷新失败：{0}", UserMessage(exception));
        }
        finally
        {
            FinishLatestRefresh(ref _usersRefreshCancellation, cancellation);
        }
    }

    private async Task LoadUserCardAvatarAsync(
        UserCardViewModel card,
        int requestVersion,
        CancellationToken cancellationToken)
    {
        var api = _api;
        if (!UsersPage.IsVisible || api is null || !card.Profile.HasAvatar)
        {
            return;
        }

        try
        {
            using var memory = new MemoryStream();
            await api.DownloadUserAvatarAsync(card.Id, memory, cancellationToken);
            if (requestVersion != Volatile.Read(ref _userRequestVersion) ||
                cancellationToken.IsCancellationRequested ||
                !UsersPage.IsVisible ||
                !VisibleUsers.Contains(card))
            {
                return;
            }

            memory.Position = 0;
            var bitmap = new Bitmap(memory);
            if (requestVersion != Volatile.Read(ref _userRequestVersion) ||
                cancellationToken.IsCancellationRequested ||
                !UsersPage.IsVisible ||
                !VisibleUsers.Contains(card))
            {
                bitmap.Dispose();
                return;
            }

            card.SetAvatar(bitmap);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // A missing or damaged avatar falls back to the user's initial.
        }
    }

    private void ShowCurrentProfile()
    {
        if (_profileSnapshot is not { } user)
        {
            ShowLoggedOutProfile();
            return;
        }

        _publicProfileSnapshot = null;
        _displayedProfileUserId = user.Id;
        ProfileTitleText.Text = string.IsNullOrWhiteSpace(user.DisplayName) ? user.Username : user.DisplayName;
        ProfileSubtitleText.Text = $"@{user.Username}";
        if (string.IsNullOrWhiteSpace(user.Bio))
        {
            UiLocalization.SetText(ProfileHeaderBioText, "这个人还没有写个人简介。");
        }
        else
        {
            ProfileHeaderBioText.Text = user.Bio;
        }
        UiLocalization.SetText(
            ProfileHeaderRoleText,
            user.IsAdmin ? "管理员 · 当前账号" : "团队成员 · 当前账号");
        PopulateProfileFields(
            user.Username,
            user.DisplayName,
            user.Bio,
            user.Birthday,
            user.Gender,
            user.CustomGender,
            user.Contact);
        _ = LoadProfileAvatarAsync(user.Id, user.HasAvatar);
        ProfileEmailBox.Text = user.Email;
        BirthdayVisibilityPicker.SelectedIndex = user.BirthdayVisibility == ApiProfileVisibility.Team ? 1 : 0;
        GenderVisibilityPicker.SelectedIndex = user.GenderVisibility == ApiProfileVisibility.Team ? 1 : 0;
        ContactVisibilityPicker.SelectedIndex = user.ContactVisibility == ApiProfileVisibility.Team ? 1 : 0;
        SetProfileEditable(false);
        UiLocalization.SetText(ProfileStatusText, user.IsAdmin ? "管理员账号" : "团队成员");
        _ = LoadCurrentPublicProfileAsync(user.Id);
    }

    private async Task LoadCurrentPublicProfileAsync(Guid userId)
    {
        var requestVersion = Volatile.Read(ref _profilePageRequestVersion);
        if (!ProfilePage.IsVisible || _api is null || _currentUser?.Id != userId)
        {
            return;
        }

        try
        {
            if (ProfilePage.IsVisible &&
                requestVersion == Volatile.Read(ref _profilePageRequestVersion) &&
                _currentUser?.Id == userId &&
                _displayedProfileUserId == userId &&
                _publicProfileSnapshot is null)
            {
                await RefreshDisplayedProfileAssetsAsync();
            }
        }
        catch (Exception exception)
        {
            if (ProfilePage.IsVisible &&
                requestVersion == Volatile.Read(ref _profilePageRequestVersion) &&
                _displayedProfileUserId == userId)
            {
                UiLocalization.SetText(ProfileStatusText, "素材列表读取失败：{0}", UserMessage(exception));
            }
        }
    }

    private async void UserCard_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_api is null || sender is not Button { DataContext: UserCardViewModel card })
        {
            return;
        }

        SetProfileOpenedFromUsers(_currentUser?.Id != card.Id);
        NavigateTo("profile");
        var requestVersion = Interlocked.Increment(ref _profilePageRequestVersion);
        var requestedUserId = card.Id;
        UiLocalization.SetText(ProfileStatusText, "正在读取个人主页...");
        try
        {
            var profile = await _api.GetUserAsync(requestedUserId);
            if (requestVersion != Volatile.Read(ref _profilePageRequestVersion) ||
                profile.Id != requestedUserId)
            {
                return;
            }

            if (_currentUser?.Id == profile.Id)
            {
                ShowCurrentProfile();
                return;
            }

            _publicProfileSnapshot = profile;
            _displayedProfileUserId = profile.Id;
            ProfileTitleText.Text = string.IsNullOrWhiteSpace(profile.DisplayName) ? profile.Username : profile.DisplayName;
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
            if (requestVersion != Volatile.Read(ref _profilePageRequestVersion) ||
                _displayedProfileUserId != profile.Id)
            {
                return;
            }

            BirthdayVisibilityPicker.SelectedIndex = 0;
            GenderVisibilityPicker.SelectedIndex = 0;
            ContactVisibilityPicker.SelectedIndex = 0;
            SetProfileEditable(false);
            UiLocalization.SetText(
                ProfileStatusText,
                profile.IsEnabled ? "" : "该账号已停用，历史素材和标记归属仍保留。");
            await LoadProfileAssetsAsync(profile.Id, profile.AssetCounts);
        }
        catch (Exception exception)
        {
            if (requestVersion == Volatile.Read(ref _profilePageRequestVersion))
            {
                UiLocalization.SetText(ProfileStatusText, "读取失败：{0}", UserMessage(exception));
            }
        }
    }

    private void BackToUsers_OnClick(object? sender, RoutedEventArgs eventArgs) =>
        NavigateTo("users");

    private void SetProfileOpenedFromUsers(bool value)
    {
        _profileOpenedFromUsers = value;
        if (ProfileBackToUsersButton is not null)
        {
            ProfileBackToUsersButton.IsVisible = value;
        }

        if (_activePage == "profile")
        {
            UsersNav.Classes.Set("selected", value);
            ProfileNav.Classes.Set("selected", !value);
        }
    }

    private void PopulateProfileFields(
        string username,
        string? displayName,
        string? bio,
        DateOnly? birthday,
        ApiProfileGender? gender,
        string? customGender,
        string? contact)
    {
        var titleName = string.IsNullOrWhiteSpace(displayName) ? username : displayName;
        ProfileAvatarInitial.Text = titleName[..1].ToUpperInvariant();
        ProfileDisplayNameBox.Text = displayName;
        ProfileUsernameBox.Text = username;
        ProfileBioBox.Text = bio;
        ProfileBirthdayPicker.SelectedDate = birthday is null
            ? null
            : new DateTime(birthday.Value.Year, birthday.Value.Month, birthday.Value.Day);
        ProfileGenderPicker.SelectedIndex = gender switch
        {
            ApiProfileGender.Male => 1,
            ApiProfileGender.Female => 2,
            ApiProfileGender.Custom => 3,
            _ => 0
        };
        ProfileCustomGenderBox.Text = customGender;
        ProfileContactBox.Text = contact;
        if (birthday is null)
        {
            UiLocalization.SetText(ProfileBirthdayDisplayText, "未填写");
        }
        else
        {
            ProfileBirthdayDisplayText.Text = birthday.Value.ToString("yyyy-MM-dd");
        }
        var genderLabel = gender switch
        {
            ApiProfileGender.Male => "男",
            ApiProfileGender.Female => "女",
            ApiProfileGender.Custom => string.IsNullOrWhiteSpace(customGender) ? "自定义" : customGender,
            _ => "未填写"
        };
        if (gender == ApiProfileGender.Custom && !string.IsNullOrWhiteSpace(customGender))
        {
            ProfileGenderDisplayText.Text = genderLabel;
        }
        else
        {
            UiLocalization.SetText(ProfileGenderDisplayText, genderLabel);
        }
        if (string.IsNullOrWhiteSpace(contact))
        {
            UiLocalization.SetText(ProfileContactDisplayText, "未填写");
        }
        else
        {
            ProfileContactDisplayText.Text = contact;
        }
    }

    private void SetProfileEditable(bool editable)
    {
        _isProfileEditing = editable;
        ProfileDisplayNameBox.IsReadOnly = !editable;
        ProfileBioBox.IsReadOnly = !editable;
        ProfileBirthdayPicker.IsEnabled = editable;
        ProfileGenderPicker.IsEnabled = editable;
        ProfileCustomGenderBox.IsReadOnly = !editable;
        ProfileContactBox.IsReadOnly = !editable;
        BirthdayVisibilityPicker.IsEnabled = editable;
        GenderVisibilityPicker.IsEnabled = editable;
        ContactVisibilityPicker.IsEnabled = editable;
        ProfileAvatarActions.IsVisible = editable;
        ProfileEmailSection.IsVisible = editable;
        ProfileSaveButton.IsVisible = editable;
        ProfileCancelButton.IsVisible = editable;
        ProfileEditPanel.IsVisible = editable;
        ProfileOverviewPanel.IsVisible = !editable;
        ProfileEditButton.IsVisible = editable == false && _publicProfileSnapshot is null && _currentUser is not null;
        ProfileLoginButton.IsVisible = editable == false && _publicProfileSnapshot is null && _currentUser is null;
        SwitchAccountButton.IsVisible = editable == false && _publicProfileSnapshot is null && _currentUser is not null;
        LogoutButton.IsVisible = editable == false && _publicProfileSnapshot is null && _currentUser is not null;
    }

    private void EditProfile_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_currentUser is null || _publicProfileSnapshot is not null)
        {
            return;
        }

        SetProfileEditable(true);
        UiLocalization.SetText(ProfileStatusText, "正在编辑个人资料。");
    }

    private void ShowLoggedOutProfile()
    {
        Interlocked.Increment(ref _profilePageRequestVersion);
        CancelLatestRefresh(ref _profileContentRefreshCancellation);
        Interlocked.Increment(ref _profileContentRequestVersion);
        UiLocalization.SetText(ProfileTitleText, "个人资料");
        _displayedProfileUserId = null;
        UiLocalization.SetText(ProfileSubtitleText, "登录后查看和编辑云端个人资料");
        UiLocalization.SetText(ProfileHeaderBioText, "登录后完善你的团队主页。");
        UiLocalization.SetText(ProfileHeaderRoleText, "本地模式");
        PopulateProfileFields("-", null, null, null, null, null, null);
        ClearProfileAvatar();
        ProfileEmailBox.Text = string.Empty;
        SetProfileEditable(false);
        UiLocalization.SetText(ProfileStatusText, "当前处于本地模式。");
        _profileAssetSource = [];
        _profileLutSource = [];
        _profileAssetCategory = null;
        _profileShowsLuts = false;
        UpdateProfileAssetCounts(new Dictionary<string, int>());
        ApplyProfileAssetFilter();
    }

    private async void SaveProfile_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_api is null || _currentUser is null)
        {
            return;
        }

        var gender = ProfileGenderPicker.SelectedIndex switch
        {
            1 => ApiProfileGender.Male,
            2 => ApiProfileGender.Female,
            3 => ApiProfileGender.Custom,
            _ => (ApiProfileGender?)null
        };
        if (gender == ApiProfileGender.Custom && string.IsNullOrWhiteSpace(ProfileCustomGenderBox.Text))
        {
            UiLocalization.SetText(ProfileStatusText, "选择“自定义”性别时需要填写自定义内容。");
            return;
        }

        UiLocalization.SetText(ProfileStatusText, "正在保存资料...");
        try
        {
            var date = ProfileBirthdayPicker.SelectedDate;
            var updated = await _api.UpdateCurrentProfileAsync(new ApiUpdateProfileRequest(
                EmptyToNull(ProfileDisplayNameBox.Text),
                EmptyToNull(ProfileBioBox.Text),
                date is null ? null : DateOnly.FromDateTime(date.Value.Date),
                gender,
                gender == ApiProfileGender.Custom ? EmptyToNull(ProfileCustomGenderBox.Text) : null,
                EmptyToNull(ProfileContactBox.Text),
                BirthdayVisibilityPicker.SelectedIndex == 1 ? ApiProfileVisibility.Team : ApiProfileVisibility.Private,
                GenderVisibilityPicker.SelectedIndex == 1 ? ApiProfileVisibility.Team : ApiProfileVisibility.Private,
                ContactVisibilityPicker.SelectedIndex == 1 ? ApiProfileVisibility.Team : ApiProfileVisibility.Private));
            _currentUser = updated;
            _profileSnapshot = updated;
            ApplyCurrentUser(updated);
            await TouchSavedLoginAccountAsync(updated);
            UiLocalization.SetText(ProfileStatusText, "资料已保存。");
            await RefreshUsersAsync();
        }
        catch (Exception exception)
        {
            UiLocalization.SetText(ProfileStatusText, "保存失败：{0}", UserMessage(exception));
        }
    }

    private void CancelProfileEdit_OnClick(object? sender, RoutedEventArgs eventArgs) => ShowCurrentProfile();

    private async void ChangePassword_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_api is null || _currentUser is null || _serverOrigin is null)
        {
            UiLocalization.SetText(ProfileStatusText, "请先登录。");
            return;
        }

        var input = await new ChangePasswordWindow().ShowDialog<ChangePasswordInput?>(this);
        if (input is null)
        {
            return;
        }

        UiLocalization.SetText(ProfileStatusText, "正在修改密码...");
        try
        {
            var userId = _currentUser.Id;
            _currentUser = await _api.ChangePasswordAsync(new ApiChangePasswordRequest(
                input.CurrentPassword,
                input.NewPassword));
            _profileSnapshot = _currentUser;

            string? credentialWarning = null;
            var savedAccount = _settings.SavedLoginAccounts.FirstOrDefault(account => account.UserId == userId);
            if (savedAccount?.PasswordRemembered == true)
            {
                try
                {
                    await _rememberedPasswordStore.SaveAsync(_serverOrigin, userId, input.NewPassword);
                }
                catch (Exception exception)
                {
                    try
                    {
                        await _rememberedPasswordStore.DeleteAsync(_serverOrigin, userId);
                    }
                    catch (Exception deleteException)
                    {
                        ClientDiagnostics.WriteException(
                            "change-password-delete-stale-credential",
                            deleteException,
                            isFatal: false);
                    }

                    _settings = _settings with
                    {
                        SavedLoginAccounts = _settings.SavedLoginAccounts
                            .Select(account => account.UserId == userId
                                ? account with { PasswordRemembered = false }
                                : account)
                            .ToArray()
                    };
                    try
                    {
                        await SaveSettingsAsync();
                    }
                    catch (Exception settingsException)
                    {
                        ClientDiagnostics.WriteException(
                            "change-password-save-account-state",
                            settingsException,
                            isFatal: false);
                    }
                    credentialWarning = UiLocalization.Format(
                        "密码已修改，但无法更新本机记住的密码：{0}。下次登录时需要重新输入。",
                        UserMessage(exception));
                }
            }

            var message = credentialWarning ?? UiLocalization.Text("密码已成功修改。");
            ProfileStatusText.Text = message;
            await new MessageDialogWindow("修改密码", message).ShowDialog(this);
        }
        catch (Exception exception)
        {
            var message = UserMessage(exception);
            UiLocalization.SetText(ProfileStatusText, "密码修改失败：{0}", message);
            await new MessageDialogWindow("无法修改密码", message).ShowDialog(this);
        }
    }

    private async void BindEmail_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_api is null || _currentUser is null || string.IsNullOrWhiteSpace(ProfileEmailBox.Text))
        {
            UiLocalization.SetText(ProfileStatusText, "请先填写要绑定的邮箱。");
            return;
        }

        var password = await new TextPromptWindow(
                "确认当前密码",
                "绑定或换绑邮箱需要验证当前密码；邮箱不会显示给其他成员。",
                isPassword: true)
            .ShowDialog<string?>(this);
        if (password is null)
        {
            return;
        }

        try
        {
            var updated = await _api.BindEmailAsync(new ApiBindEmailRequest(ProfileEmailBox.Text.Trim(), password));
            _currentUser = updated;
            _profileSnapshot = updated;
            ShowCurrentProfile();
            UiLocalization.SetText(ProfileStatusText, "邮箱已绑定，可作为附属登录名使用。");
        }
        catch (Exception exception)
        {
            UiLocalization.SetText(ProfileStatusText, "绑定失败：{0}", UserMessage(exception));
        }
    }

    private async void UnbindEmail_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_api is null || _currentUser?.Email is null)
        {
            UiLocalization.SetText(ProfileStatusText, "当前账号没有绑定邮箱。");
            return;
        }

        var password = await new TextPromptWindow(
                "解绑邮箱",
                "输入当前密码确认解绑；用户名和密码登录不受影响。",
                isPassword: true)
            .ShowDialog<string?>(this);
        if (password is null)
        {
            return;
        }

        try
        {
            var updated = await _api.UnbindEmailAsync(new ApiPasswordConfirmationRequest(password));
            _currentUser = updated;
            _profileSnapshot = updated;
            ShowCurrentProfile();
            UiLocalization.SetText(ProfileStatusText, "邮箱已解绑。");
        }
        catch (Exception exception)
        {
            UiLocalization.SetText(ProfileStatusText, "解绑失败：{0}", UserMessage(exception));
        }
    }

    private async Task LoadProfileAssetsAsync(
        Guid userId,
        IReadOnlyDictionary<string, int>? assetCounts = null)
    {
        if (!ProfilePage.IsVisible || _api is null || _currentUser is null ||
            _displayedProfileUserId != userId)
        {
            return;
        }

        var requestVersion = Interlocked.Increment(ref _profileContentRequestVersion);
        var cancellation = StartLatestRefresh(ref _profileContentRefreshCancellation);
        var cancellationToken = cancellation.Token;
        try
        {
            await LoadProfileAssetsCoreAsync(
                userId,
                assetCounts,
                requestVersion,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (IsCurrentProfileContentRequest(requestVersion, userId, cancellationToken))
            {
                UiLocalization.SetText(ProfileStatusText, "上传内容读取失败：{0}", UserMessage(exception));
            }
        }
        finally
        {
            FinishLatestRefresh(ref _profileContentRefreshCancellation, cancellation);
        }
    }

    private async Task LoadProfileAssetsCoreAsync(
        Guid userId,
        IReadOnlyDictionary<string, int>? assetCounts,
        int requestVersion,
        CancellationToken cancellationToken)
    {
        if (!IsCurrentProfileContentRequest(requestVersion, userId, cancellationToken))
        {
            return;
        }

        _profileAssetSource = [];
        _profileLutSource = [];
        UpdateProfileUploadCountLabels();
        ApplyProfileAssetFilter();
        var assetsTask = ListAllAssetsAsync(new ApiAssetListQuery(
            UploaderId: userId,
            Sort: ApiAssetSort.Name,
            Order: ApiSortOrder.Ascending), cancellationToken);
        var lutsTask = ListAllProfileLutsAsync(userId, cancellationToken);
        await Task.WhenAll(assetsTask, lutsTask);
        if (!IsCurrentProfileContentRequest(requestVersion, userId, cancellationToken))
        {
            return;
        }

        if (assetCounts is not null)
        {
            UpdateProfileAssetCounts(assetCounts);
        }

        _profileAssetSource = await assetsTask;
        _profileLutSource = await lutsTask;
        _profileAssetCategory = null;
        _profileShowsLuts = false;
        UpdateProfileUploadCountLabels();
        ApplyProfileAssetFilter();
    }

    private bool IsCurrentProfileContentRequest(
        int requestVersion,
        Guid userId,
        CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested &&
        ProfilePage.IsVisible &&
        requestVersion == Volatile.Read(ref _profileContentRequestVersion) &&
        _displayedProfileUserId == userId;

    private async Task<IReadOnlyList<ApiAsset>> ListAllAssetsAsync(
        ApiAssetListQuery query,
        CancellationToken cancellationToken = default)
    {
        if (_api is null)
        {
            return [];
        }

        const int pageSize = 100;
        var assets = new List<ApiAsset>();
        for (var pageNumber = 1; ; pageNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await _api.ListAssetsAsync(query with
            {
                Page = pageNumber,
                PageSize = pageSize
            }, cancellationToken);
            assets.AddRange(page.Items);
            if (page.Items.Count == 0 || assets.Count >= page.Total)
            {
                return assets;
            }
        }
    }

    private void UpdateProfileAssetCounts(IReadOnlyDictionary<string, int> counts)
    {
        _profileAssetCountSnapshot = counts;
        UpdateProfileUploadCountLabels();
    }

    private void ProfileAssetCategory_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not Button { Tag: string category })
        {
            return;
        }

        _profileShowsLuts = category == "lut";
        var selected = category switch
        {
            "bgm" => ApiAssetCategory.Bgm,
            "sound" => ApiAssetCategory.SoundEffect,
            "image" => ApiAssetCategory.Image,
            "video" => ApiAssetCategory.Video,
            _ => (ApiAssetCategory?)null
        };
        _profileAssetCategory = selected;
        ApplyProfileAssetFilter();
    }

    private void ApplyProfileAssetFilter()
    {
        CancelCloudThumbnailLoading();
        DisposeProfileAssetCards();
        if (!_profileShowsLuts)
        {
            foreach (var asset in _profileAssetSource.Where(asset =>
                         _profileAssetCategory is null || asset.Category == _profileAssetCategory))
            {
                ProfileAssets.Add(new CloudAssetCardViewModel(asset));
            }
        }

        StartCloudThumbnailLoading();

        ProfileAssetItems.IsVisible = !_profileShowsLuts;
        ProfileAssetsEmptyText.IsVisible = !_profileShowsLuts && ProfileAssets.Count == 0;
        ProfileAllCount.Classes.Set("selected", !_profileShowsLuts && _profileAssetCategory is null);
        ProfileBgmCount.Classes.Set("selected", !_profileShowsLuts && _profileAssetCategory == ApiAssetCategory.Bgm);
        ProfileSoundCount.Classes.Set("selected", !_profileShowsLuts && _profileAssetCategory == ApiAssetCategory.SoundEffect);
        ProfileVideoCount.Classes.Set("selected", !_profileShowsLuts && _profileAssetCategory == ApiAssetCategory.Video);
        ProfileImageCount.Classes.Set("selected", !_profileShowsLuts && _profileAssetCategory == ApiAssetCategory.Image);
        RebuildProfileLutCards();
    }

    private void ProfileAsset_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not Button { DataContext: CloudAssetCardViewModel asset })
        {
            return;
        }

        CommitHoverDetailSelection();
        SelectCloudAsset(asset);
        NavigateTo("shared");
    }

    private async Task<bool> HandleSessionFailureAsync(Exception exception)
    {
        if (exception is not AssetLibraryApiException { StatusCode: System.Net.HttpStatusCode.Unauthorized })
        {
            return false;
        }

        await DeleteStoredTokenQuietlyAsync();
        await SetLoggedOutStateAsync("登录已失效，请重新登录");
        return true;
    }

    private void SharedUploadDropTarget_OnDragOver(object? sender, DragEventArgs eventArgs)
    {
        CancelSharedDropTargetDeactivation();
        var dropped = SnapshotDroppedPaths(eventArgs);
        var paths = dropped.Files;
        var supportedCount = paths.Count(IsSupportedCloudUploadPath);
        var canUpload = _currentUser is not null && HasCurrentUserPermission("assets.upload");
        var hasPendingFileTransfer = paths.Length + dropped.Folders.Length == 0 && ContainsFileTransfer(eventArgs);
        var acceptsDrop = canUpload &&
                          (supportedCount > 0 || dropped.Folders.Length > 0 || hasPendingFileTransfer);
        eventArgs.DragEffects = acceptsDrop
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        if (_currentUser is null)
        {
            UiLocalization.SetText(CloudSummaryText, "请先登录，再拖拽上传素材。");
        }
        else if (!canUpload)
        {
            UiLocalization.SetText(CloudSummaryText, "当前账号的上传权限已锁闭。");
        }
        else if (paths.Length > 0 && supportedCount == 0)
        {
            UiLocalization.SetText(CloudSummaryText, "拖入内容中没有支持的素材或 .cube LUT 文件。");
        }
        else if (dropped.Folders.Length > 0)
        {
            UiLocalization.SetText(
                CloudSummaryText,
                "释放鼠标上传 {0:N0} 个完整文件夹到当前云端目录",
                dropped.Folders.Length);
        }
        else if (supportedCount > 0)
        {
            UiLocalization.SetText(
                CloudSummaryText,
                "释放鼠标上传 {0:N0} 个文件",
                supportedCount);
        }
        else if (hasPendingFileTransfer)
        {
            UiLocalization.SetText(CloudSummaryText, "释放鼠标后检查并上传文件");
        }

        SharedUploadDropTarget.Classes.Set("dragActive", acceptsDrop);

        eventArgs.Handled = true;
    }

    private void SharedUploadDropTarget_OnDragLeave(object? sender, DragEventArgs eventArgs)
    {
        ScheduleSharedDropTargetDeactivation();
        eventArgs.Handled = true;
    }

    private void SharedUploadDropTarget_OnDrop(object? sender, DragEventArgs eventArgs)
    {
        var dropped = SnapshotDroppedPaths(eventArgs);
        var containedFileTransfer = ContainsFileTransfer(eventArgs);
        var targetFolderId = _selectedCloudFolderId;
        var paths = dropped.Files
            .Where(IsSupportedCloudUploadPath)
            .ToArray();
        var folders = dropped.Folders.Where(Directory.Exists).ToArray();
        CancelSharedDropTargetDeactivation();
        SharedUploadDropTarget.Classes.Set("dragActive", false);
        eventArgs.Handled = true;
        if (_currentUser is null || !HasCurrentUserPermission("assets.upload"))
        {
            eventArgs.DragEffects = DragDropEffects.None;
            RestoreSharedSummary();
            return;
        }

        if (paths.Length == 0 && folders.Length == 0)
        {
            eventArgs.DragEffects = DragDropEffects.None;
            if (dropped.Files.Length > 0)
            {
                UiLocalization.SetText(CloudSummaryText, "拖入内容中没有支持的素材或 .cube LUT 文件。");
            }
            else if (containedFileTransfer)
            {
                eventArgs.DragEffects = DragDropEffects.Copy;
                UiLocalization.SetText(CloudSummaryText, "正在读取拖入的文件...");
                var pendingTransfer = eventArgs.DataTransfer;
                Dispatcher.UIThread.Post(
                    () => _ = ResolvePendingCloudDropAsync(pendingTransfer, targetFolderId),
                    DispatcherPriority.Background);
            }
            else
            {
                RestoreSharedSummary();
            }
            return;
        }

        eventArgs.DragEffects = DragDropEffects.Copy;
        UiLocalization.SetText(CloudSummaryText, "正在准备拖拽上传...");
        var pathSnapshot = paths.ToArray();
        var folderSnapshot = folders.ToArray();
        Dispatcher.UIThread.Post(
            () => _ = folderSnapshot.Length > 0
                ? PrepareCloudFolderUploadSafelyAsync(folderSnapshot, pathSnapshot, targetFolderId)
                : PrepareDroppedCloudUploadSafelyAsync(pathSnapshot, targetFolderId),
            DispatcherPriority.Background);
    }

    private async Task ResolvePendingCloudDropAsync(
        IDataTransfer dataTransfer,
        Guid? targetFolderId)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(60 * attempt);
            }

            var dropped = SnapshotDroppedPaths(dataTransfer);
            var paths = dropped.Files.Where(IsSupportedCloudUploadPath).ToArray();
            var folders = dropped.Folders.Where(Directory.Exists).ToArray();
            if (paths.Length == 0 && folders.Length == 0)
            {
                continue;
            }

            if (folders.Length > 0)
            {
                await PrepareCloudFolderUploadSafelyAsync(folders, paths, targetFolderId);
            }
            else
            {
                await PrepareDroppedCloudUploadSafelyAsync(paths, targetFolderId);
            }
            return;
        }

        UiLocalization.SetText(
            CloudSummaryText,
            "未能读取拖入的文件，请重新拖入或使用“上传素材”按钮。");
    }

    private void CancelSharedDropTargetDeactivation()
    {
        Interlocked.Exchange(ref _sharedUploadDragLeaveCancellation, null)?.Cancel();
    }

    private void ScheduleSharedDropTargetDeactivation()
    {
        var cancellation = new CancellationTokenSource();
        Interlocked.Exchange(ref _sharedUploadDragLeaveCancellation, cancellation)?.Cancel();
        _ = DeactivateSharedDropTargetAfterDelayAsync(cancellation);
    }

    private async Task DeactivateSharedDropTargetAfterDelayAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(140, cancellation.Token);
            if (_shutdownStarted)
            {
                return;
            }

            SharedUploadDropTarget.Classes.Set("dragActive", false);
            RestoreSharedSummary();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Interlocked.CompareExchange(ref _sharedUploadDragLeaveCancellation, null, cancellation);
            cancellation.Dispose();
        }
    }

    private async Task PrepareDroppedCloudUploadSafelyAsync(
        IReadOnlyList<string> paths,
        Guid? targetFolderId)
    {
        try
        {
            await PrepareCloudUploadAsync(paths, targetFolderId);
        }
        catch (Exception exception)
        {
            ClientDiagnostics.WriteException("shared-library-drop", exception, isFatal: false);
            if (_shutdownStarted)
            {
                return;
            }

            UiLocalization.SetText(CloudSummaryText, "上传准备失败：{0}", UserMessage(exception));
            try
            {
                await new MessageDialogWindow("无法上传素材", UserMessage(exception)).ShowDialog(this);
            }
            catch (Exception dialogException)
            {
                ClientDiagnostics.WriteException(
                    "shared-library-drop-dialog",
                    dialogException,
                    isFatal: false);
            }
        }
    }

    private static bool IsSupportedCloudUploadPath(string path) =>
        File.Exists(path) &&
        (MediaExtensionClassifier.TryClassify(path, out _) ||
         Path.GetExtension(path).Equals(".cube", StringComparison.OrdinalIgnoreCase));

    private void RestoreSharedSummary()
    {
        if (_currentUser is null)
        {
            UiLocalization.SetText(CloudSummaryText, "团队云端素材");
        }
        else if (_cloudCategoryFilterIndex == 5)
        {
            UiLocalization.SetText(CloudSummaryText, "共 {0:N0} 个团队 LUT", SharedCloudLuts.Count);
        }
        else
        {
            UiLocalization.SetText(CloudSummaryText, "共 {0:N0} 个素材", _cloudAssets.Count);
        }
    }

    private async void ChooseCloudUpload_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_currentUser is null)
        {
            ConnectServer_OnClick(sender, eventArgs);
            return;
        }

        try
        {
            var files = await RunNativePickerAsync(
                "choose-cloud-upload",
                () => StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = UiLocalization.Text("选择要上传的素材"),
                    AllowMultiple = true
                }),
                CloudSummaryText);
            if (files is null)
            {
                return;
            }

            await PrepareCloudUploadAsync(files
                .Select(file => file.Path.LocalPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Cast<string>()
                .ToArray());
        }
        catch (Exception exception)
        {
            ClientDiagnostics.WriteException("choose-cloud-upload", exception, isFatal: false);
            UiLocalization.SetText(CloudSummaryText, "上传准备失败：{0}", UserMessage(exception));
        }
    }

    private async void ChooseCloudFolderUpload_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_currentUser is null)
        {
            ConnectServer_OnClick(sender, eventArgs);
            return;
        }

        var folders = await RunNativePickerAsync(
            "choose-cloud-folder-upload",
            () => StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = UiLocalization.Text("选择要完整上传的文件夹"),
                AllowMultiple = true
            }),
            CloudSummaryText);
        if (folders is null)
        {
            return;
        }

        await PrepareCloudFolderUploadSafelyAsync(
            folders.Select(folder => folder.Path.LocalPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Cast<string>()
                .ToArray(),
            [],
            _selectedCloudFolderId);
    }

    private async void UploadLocalFolderToCloud_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not MenuItem { Tag: LocalFolderNodeViewModel node } ||
            node.IsOffline || !Directory.Exists(node.FullPath))
        {
            return;
        }

        await PrepareCloudFolderUploadSafelyAsync([node.FullPath], [], _selectedCloudFolderId);
    }

    private async Task PrepareCloudFolderUploadSafelyAsync(
        IReadOnlyList<string> folderPaths,
        IReadOnlyList<string> looseFilePaths,
        Guid? targetFolderId)
    {
        try
        {
            await PrepareCloudFolderUploadAsync(folderPaths, looseFilePaths, targetFolderId);
        }
        catch (Exception exception)
        {
            ClientDiagnostics.WriteException("cloud-folder-upload", exception, isFatal: false);
            UiLocalization.SetText(CloudSummaryText, "文件夹上传失败：{0}", UserMessage(exception));
            await new MessageDialogWindow("无法上传文件夹", UserMessage(exception)).ShowDialog(this);
        }
    }

    private async Task PrepareCloudFolderUploadAsync(
        IReadOnlyList<string> folderPaths,
        IReadOnlyList<string> looseFilePaths,
        Guid? targetFolderId)
    {
        if (_currentUser is null || !HasCurrentUserPermission("assets.upload"))
        {
            throw new InvalidOperationException(UiLocalization.Text("当前账号没有上传素材的权限。"));
        }

        UiLocalization.SetText(CloudSummaryText, "正在扫描文件夹...");
            var roots = await Task.Run(() => folderPaths
                .Where(Directory.Exists)
                .Select(SnapshotCloudUploadFolder)
                .ToArray());
            var looseMedia = looseFilePaths
                .Where(path => File.Exists(path) && MediaExtensionClassifier.TryClassify(path, out _))
                .Distinct(LocalPathComparer)
                .ToArray();
            var allMedia = roots.SelectMany(root => root.MediaFiles.Select(file => file.FullPath))
                .Concat(looseMedia)
                .ToArray();
            var skipped = roots.Sum(root => root.UnsupportedFileCount) +
                          looseFilePaths.Count(path => File.Exists(path) &&
                              !MediaExtensionClassifier.TryClassify(path, out _));
            if (allMedia.Length == 0)
            {
                UiLocalization.SetText(
                    CloudSummaryText,
                    "所选文件夹中没有支持的图片、音频或视频；跳过 {0:N0} 个文件。",
                    skipped);
                return;
            }

            if (_cloudTagLibraryService is null || _api is null)
            {
                throw new InvalidOperationException(UiLocalization.Text("云端上传服务尚未初始化。"));
            }

            var availableTags = await _cloudTagLibraryService.ListAsync();
            var options = await new UploadOptionsWindow(
                    allMedia,
                    availableTags: availableTags.Select(tag => tag.Name).ToArray(),
                    createTagAsync: HasCurrentUserPermission("assets.tags")
                        ? CreateCloudTagFromPickerAsync
                        : null)
                .ShowDialog<UploadBatchOptions?>(this);
            if (options is null)
            {
                UiLocalization.SetText(CloudSummaryText, "已取消文件夹上传。");
                return;
            }

            var canonicalTags = await _cloudTagLibraryService.EnsureAsync(options.Tags);
            options = options with { Tags = canonicalTags };
            var workingFolders = _cloudFolders.ToList();
            var total = default(CloudMediaBatchUploadResult);
            foreach (var root in roots)
            {
                var rootFolderId = await EnsureCloudFolderAsync(
                    workingFolders,
                    targetFolderId,
                    root.Name);
                var destinations = new Dictionary<string, Guid?>(LocalPathComparer)
                {
                    [string.Empty] = rootFolderId
                };
                foreach (var relativeDirectory in root.RelativeDirectories
                             .OrderBy(RelativePathDepth)
                             .ThenBy(path => path, LocalPathComparer))
                {
                    var parentRelative = NormalizeRelativePath(Path.GetDirectoryName(relativeDirectory));
                    var parentId = destinations[parentRelative];
                    destinations[relativeDirectory] = await EnsureCloudFolderAsync(
                        workingFolders,
                        parentId,
                        Path.GetFileName(relativeDirectory));
                }

                foreach (var group in root.MediaFiles.GroupBy(file => file.RelativeDirectory, LocalPathComparer))
                {
                    var result = await UploadCloudFilesAsync(
                        group.Select(file => file.FullPath).ToArray(),
                        options,
                        destinations[group.Key]);
                    total = AddCloudUploadResults(total, result);
                }
            }

            if (looseMedia.Length > 0)
            {
                total = AddCloudUploadResults(
                    total,
                    await UploadCloudFilesAsync(looseMedia, options, targetFolderId));
            }

            await RefreshCloudFoldersAsync();
            await RefreshCloudAssetsAsync();
            UiLocalization.SetText(
                CloudSummaryText,
                "文件夹上传完成：成功 {0:N0} 个，重复 {1:N0} 个，失败 {2:N0} 个，跳过不支持文件 {3:N0} 个，预览警告 {4:N0} 个。",
                total.Succeeded,
                total.Skipped,
                total.Failed,
                skipped,
                total.DerivativeWarnings);
    }

    private async Task<Guid> EnsureCloudFolderAsync(
        List<ApiAssetFolder> folders,
        Guid? parentId,
        string name)
    {
        var existing = folders.FirstOrDefault(folder =>
            folder.ParentId == parentId && folder.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return existing.Id;
        }

        var created = await _api!.CreateAssetFolderAsync(new ApiCreateAssetFolderRequest(name, parentId));
        folders.Add(created);
        return created.Id;
    }

    private static CloudFolderUploadRoot SnapshotCloudUploadFolder(string folderPath)
    {
        var rootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderPath));
        var directories = Directory.EnumerateDirectories(rootPath, "*", SearchOption.AllDirectories)
            .Select(path => NormalizeRelativePath(Path.GetRelativePath(rootPath, path)))
            .Where(path => path.Length > 0)
            .Distinct(LocalPathComparer)
            .ToArray();
        var files = Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories).ToArray();
        var mediaFiles = files
            .Where(path => MediaExtensionClassifier.TryClassify(path, out _))
            .Select(path => new CloudFolderUploadFile(
                path,
                NormalizeRelativePath(Path.GetDirectoryName(Path.GetRelativePath(rootPath, path)))))
            .ToArray();
        return new CloudFolderUploadRoot(
            GetFolderDisplayName(rootPath),
            directories,
            mediaFiles,
            files.Length - mediaFiles.Length);
    }

    private static CloudMediaBatchUploadResult AddCloudUploadResults(
        CloudMediaBatchUploadResult left,
        CloudMediaBatchUploadResult right) => new(
        left.Succeeded + right.Succeeded,
        left.Skipped + right.Skipped,
        left.Failed + right.Failed,
        left.DerivativeWarnings + right.DerivativeWarnings);

    private Task PrepareCloudUploadAsync(IReadOnlyList<string> paths) =>
        PrepareCloudUploadAsync(paths, _selectedCloudFolderId);

    private async Task PrepareCloudUploadAsync(
        IReadOnlyList<string> paths,
        Guid? targetFolderId)
    {
        if (_currentUser is null || paths.Count == 0)
        {
            return;
        }

        await PrepareCloudUploadCoreAsync(paths, targetFolderId);
    }

    private async Task PrepareCloudUploadCoreAsync(
        IReadOnlyList<string> paths,
        Guid? targetFolderId)
    {

        var existing = paths
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var supportedMedia = existing
            .Where(path => MediaExtensionClassifier.TryClassify(path, out _))
            .ToArray();
        var supportedLuts = existing
            .Where(path => Path.GetExtension(path).Equals(".cube", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (supportedMedia.Length == 0 && supportedLuts.Length == 0)
        {
            UiLocalization.SetText(
                CloudSummaryText,
                "所选文件中没有支持的图片、音频、视频或 .cube LUT 文件。");
            return;
        }

        UploadBatchOptions? options = null;
        if (supportedMedia.Length > 0)
        {
            if (_cloudTagLibraryService is null)
            {
                UiLocalization.SetText(CloudSummaryText, "云端标签服务尚未初始化。");
                return;
            }

            IReadOnlyList<ApiTag> availableTags;
            try
            {
                availableTags = await _cloudTagLibraryService.ListAsync();
            }
            catch (Exception exception)
            {
                UiLocalization.SetText(CloudSummaryText, "标签库读取失败：{0}", UserMessage(exception));
                return;
            }

            options = await new UploadOptionsWindow(
                    supportedMedia,
                    availableTags: availableTags.Select(tag => tag.Name).ToArray(),
                    createTagAsync: HasCurrentUserPermission("assets.tags")
                        ? CreateCloudTagFromPickerAsync
                        : null)
                .ShowDialog<UploadBatchOptions?>(this);
            if (options is null)
            {
                return;
            }
        }

        try
        {
            var mediaResult = default(CloudMediaBatchUploadResult);
            if (options is not null)
            {
                var canonicalTags = await _cloudTagLibraryService!.EnsureAsync(options.Tags);
                mediaResult = await UploadCloudFilesAsync(
                    supportedMedia,
                    options with { Tags = canonicalTags },
                    targetFolderId);
            }

            var lutResult = await UploadCloudLutsAsync(supportedLuts);
            await RefreshCloudAssetsAsync();
            ReportCloudUploadResult(mediaResult, lutResult, supportedMedia.Length > 0, supportedLuts.Length > 0);
        }
        catch (Exception exception)
        {
            UiLocalization.SetText(CloudSummaryText, "上传准备失败：{0}", UserMessage(exception));
        }
    }

    private async Task<CloudMediaBatchUploadResult> UploadCloudFilesAsync(
        IReadOnlyList<string> paths,
        UploadBatchOptions options,
        Guid? targetFolderId)
    {
        if (_transferService is null)
        {
            return default;
        }

        EmptyTransfersText.IsVisible = false;
        var succeeded = 0;
        var skipped = 0;
        var failed = 0;
        var derivativeWarnings = 0;
        var jobs = paths.Where(path => MediaExtensionClassifier.TryClassify(path, out _)).Select(path =>
        {
            MediaExtensionClassifier.TryClassify(path, out var mediaType);
            var category = mediaType switch
            {
                LocalMediaType.Audio => options.AudioCategory,
                LocalMediaType.Image => ApiAssetCategory.Image,
                LocalMediaType.Video => ApiAssetCategory.Video,
                _ => throw new ArgumentOutOfRangeException()
            };
            return QueueUpload(path, category, options.Notes, options.Tags, targetFolderId);
        }).ToArray();
        foreach (var job in jobs)
        {
            try
            {
                var result = await job.Work;
                if (result.Duplicate) skipped++;
                else succeeded++;
                if (result.PreviewWarning) derivativeWarnings++;
            }
            catch (OperationCanceledException) { failed++; }
            catch (Exception) { failed++; }
        }

        return new CloudMediaBatchUploadResult(succeeded, skipped, failed, derivativeWarnings);
    }

    private void ReportCloudUploadResult(
        CloudMediaBatchUploadResult media,
        CloudLutBatchUploadResult luts,
        bool includedMedia,
        bool includedLuts)
    {
        if (!includedLuts)
        {
            UiLocalization.SetText(
                CloudSummaryText,
                "素材上传完成：成功 {0:N0} 个，重复跳过 {1:N0} 个，失败 {2:N0} 个，预览警告 {3:N0} 个。",
                media.Succeeded,
                media.Skipped,
                media.Failed,
                media.DerivativeWarnings);
            return;
        }

        if (!includedMedia)
        {
            UiLocalization.SetText(
                CloudSummaryText,
                "LUT 上传完成：成功 {0:N0} 个，失败 {1:N0} 个。",
                luts.Succeeded,
                luts.Failed);
            return;
        }

        UiLocalization.SetText(
            CloudSummaryText,
            "混合上传完成：素材成功 {0:N0} 个、重复 {1:N0} 个、失败 {2:N0} 个、预览警告 {3:N0} 个；LUT 成功 {4:N0} 个、失败 {5:N0} 个。",
            media.Succeeded,
            media.Skipped,
            media.Failed,
            media.DerivativeWarnings,
            luts.Succeeded,
            luts.Failed);
    }

    private async Task<CloudDerivativeProcessingResult> ProcessUploadedDerivativesAsync(
        string sourcePath,
        ApiAsset uploaded)
    {
        if (_cloudDerivativeService is null)
        {
            var unavailable = new CloudDerivativeProcessingItem(
                AssetDerivativeKind.Thumbnail,
                CloudDerivativeProcessingState.Unavailable,
                UiLocalization.Text("云端派生服务尚未初始化。"));
            return new CloudDerivativeProcessingResult(
                unavailable,
                unavailable with { Kind = AssetDerivativeKind.Proxy });
        }

        try
        {
            return await _cloudDerivativeService.ProcessAfterOriginalUploadAsync(sourcePath, uploaded);
        }
        catch (Exception exception)
        {
            var diagnostic = UserMessage(exception);
            return new CloudDerivativeProcessingResult(
                new CloudDerivativeProcessingItem(
                    AssetDerivativeKind.Thumbnail,
                    CloudDerivativeProcessingState.Failed,
                    diagnostic),
                new CloudDerivativeProcessingItem(
                    AssetDerivativeKind.Proxy,
                    CloudDerivativeProcessingState.Failed,
                    diagnostic));
        }
    }

    private static void ApplyTransferProgress(TransferItemViewModel item, CloudTransferProgress progress)
    {
        var ratio = progress.TotalBytes <= 0
            ? 0
            : Math.Clamp(progress.BytesProcessed * 100d / progress.TotalBytes, 0, 100);
        switch (progress.Stage)
        {
            case CloudTransferStage.Hashing:
                item.Status = "正在校验";
                item.Progress = ratio * 0.2;
                break;
            case CloudTransferStage.Uploading:
                item.Status = "正在上传";
                item.Progress = 20 + ratio * 0.8;
                break;
            case CloudTransferStage.Downloading:
                item.Status = "正在下载";
                item.Progress = ratio;
                break;
            case CloudTransferStage.Completed:
                item.Status = "已完成";
                item.Progress = 100;
                break;
        }
    }

    private void CloudAssetCard_OnPointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (sender is not Border { DataContext: CloudAssetCardViewModel asset })
        {
            return;
        }

        if (OriginatesFromCheckBox(eventArgs))
        {
            eventArgs.Handled = true;
            ResetPendingDragGesture();
            return;
        }

        SelectCloudAsset(asset);
        if (eventArgs.GetCurrentPoint(this).Properties.IsLeftButtonPressed &&
            _settings.SingleClickPreviewEnabled &&
            asset.Asset.HasOriginal &&
            (asset.Asset.Category is ApiAssetCategory.Bgm or ApiAssetCategory.SoundEffect or ApiAssetCategory.Video ||
             asset.Asset.Category == ApiAssetCategory.Image && IsAnimatedImage(asset.Asset.Extension)))
        {
            ObserveNavigationTask(BeginSelectedCloudPreviewAsync(asset), "start-single-click-cloud-preview");
        }
        else
        {
            ObserveNavigationTask(StopDetailPreviewAsync(savePosition: true), "stop-selected-cloud-preview");
        }
        if (eventArgs.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginDragGesture(eventArgs.GetPosition(this));
        }
    }

    private static bool OriginatesFromCheckBox(RoutedEventArgs eventArgs)
    {
        if (eventArgs.Source is CheckBox)
        {
            return true;
        }

        return eventArgs.Source is Visual visual &&
               visual.GetVisualAncestors().Any(ancestor => ancestor is CheckBox);
    }

    private async void CloudAssetCard_OnPointerMoved(object? sender, PointerEventArgs eventArgs)
    {
        if (_isDragging || _isDragStartPending ||
            sender is not Border { DataContext: CloudAssetCardViewModel asset })
        {
            return;
        }

        if (!eventArgs.GetCurrentPoint(this).Properties.IsLeftButtonPressed || _dragStart is not { } dragStart)
        {
            ResetPendingDragGesture();
            return;
        }

        var currentPosition = eventArgs.GetPosition(this);
        if (Math.Abs(currentPosition.X - dragStart.X) < 4 && Math.Abs(currentPosition.Y - dragStart.Y) < 4)
        {
            return;
        }

        if (!TryBeginDragStartOperation())
        {
            return;
        }

        var gestureVersion = Volatile.Read(ref _dragGestureVersion);
        try
        {
            _dragStart = null;
            await DragCloudAssetAsync(eventArgs, asset, gestureVersion);
        }
        finally
        {
            EndDragStartOperation();
        }
    }

    private async Task DragCloudAssetAsync(
        PointerEventArgs eventArgs,
        CloudAssetCardViewModel asset,
        long gestureVersion)
    {
        try
        {
            var assets = asset.IsSelected
                ? VisibleCloudAssets.Where(item => item.IsSelected).Select(item => item.Asset).ToArray()
                : [asset.Asset];
            if (assets.Any(item => !item.HasOriginal))
            {
                throw new InvalidOperationException(UiLocalization.Text("所选素材中有原文件尚未上传完成的项目。"));
            }

            var downloadDirectory = await PersistentCloudDragDirectoryAsync();
            var paths = new List<string>(assets.Length);
            foreach (var item in assets)
            {
                var path = await EnsureCloudAssetDownloadedAsync(item, downloadDirectory);
                if (path is null)
                {
                    throw new IOException(UiLocalization.Text("无法准备云端素材文件。"));
                }

                paths.Add(path);
            }

            _activeCloudDragPaths = paths.ToArray();
            _activeCloudDragAssets = assets;
            try
            {
                var started = await BeginFilesDragAsync(eventArgs, paths, gestureVersion);
                if (!started && paths.All(File.Exists))
                {
                    UiLocalization.SetText(CloudSummaryText, "素材已准备好，请重新拖动一次。" );
                }
            }
            finally
            {
                _activeCloudDragPaths = null;
                _activeCloudDragAssets = null;
            }
        }
        catch (OperationCanceledException)
        {
            UiLocalization.SetText(CloudSummaryText, "已取消下载。");
        }
        catch (Exception exception)
        {
            UiLocalization.SetText(CloudSummaryText, "下载失败：{0}", UserMessage(exception));
        }
    }

    private async Task<string> PersistentCloudDragDirectoryAsync()
    {
        if (_settings.DownloadToDefaultDirectory &&
            !string.IsNullOrWhiteSpace(_settings.PersistentDownloadDirectory) &&
            Directory.Exists(_settings.PersistentDownloadDirectory))
        {
            return _settings.PersistentDownloadDirectory;
        }

        var selectedDirectory = await ChooseDownloadDirectoryAsync(
            UiLocalization.Text("首次拖出云端素材前选择素材默认保存目录"));
        if (selectedDirectory is null)
        {
            throw new OperationCanceledException();
        }

        _settings = _settings with { PersistentDownloadDirectory = selectedDirectory };
        DownloadDirectoryText.Text = selectedDirectory;
        await SaveSettingsAsync();
        return selectedDirectory;
    }

    private async Task<string?> ResolveCloudPreviewPathAsync(
        ApiAsset asset,
        bool allowOriginalDownload,
        CancellationToken cancellationToken = default)
    {
        if (_cloudDerivativeService is not null &&
            asset.Category is ApiAssetCategory.Bgm or ApiAssetCategory.SoundEffect or ApiAssetCategory.Video)
        {
            try
            {
                var proxyPath = await _cloudDerivativeService.GetCachedDerivativeAsync(
                    asset,
                    AssetDerivativeKind.Proxy,
                    cancellationToken);
                if (proxyPath is not null)
                {
                    return proxyPath;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch when (allowOriginalDownload)
            {
                // A damaged or temporarily unreachable proxy may fall back to the verified original.
            }
        }

        if (_transferService is not null)
        {
            var existing = await _transferService.FindDownloadedAsync(
                asset,
                cancellationToken: cancellationToken);
            if (existing is not null)
            {
                return existing.FullPath;
            }
        }

        return allowOriginalDownload
            ? await EnsureCloudAssetDownloadedAsync(asset, cancellationToken: cancellationToken)
            : null;
    }

    private async Task<string?> EnsureCloudAssetDownloadedAsync(
        ApiAsset asset,
        string? operationDirectory = null,
        CancellationToken cancellationToken = default)
    {
        if (_transferService is null)
        {
            return null;
        }

        var existing = await _transferService.FindDownloadedAsync(
            asset,
            cancellationToken: cancellationToken);
        if (existing is not null &&
            (string.IsNullOrWhiteSpace(operationDirectory) ||
             LocalPathComparer.Equals(
                 Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetDirectoryName(existing.FullPath)!)),
                 Path.TrimEndingDirectorySeparator(Path.GetFullPath(operationDirectory)))))
        {
            return existing.FullPath;
        }

        if (!asset.HasOriginal)
        {
            throw new InvalidOperationException(UiLocalization.Text("素材原文件尚未上传完成。"));
        }

        var targetDirectory = operationDirectory;
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(targetDirectory) && _settings.DownloadToDefaultDirectory)
        {
            if (string.IsNullOrWhiteSpace(_settings.PersistentDownloadDirectory))
            {
                var selectedDirectory = await ChooseDownloadDirectoryAsync(
                    UiLocalization.Text("首次下载前选择素材默认保存目录"));
                if (selectedDirectory is null)
                {
                    throw new OperationCanceledException();
                }

                cancellationToken.ThrowIfCancellationRequested();

                _settings = _settings with { PersistentDownloadDirectory = selectedDirectory };
                DownloadDirectoryText.Text = selectedDirectory;
                await SaveSettingsAsync();
            }

            targetDirectory = _settings.PersistentDownloadDirectory;
        }
        else if (string.IsNullOrWhiteSpace(targetDirectory))
        {
            targetDirectory = await ChooseDownloadDirectoryAsync(
                UiLocalization.Text("选择本次下载的保存目录"));
            if (targetDirectory is null)
            {
                throw new OperationCanceledException();
            }

            cancellationToken.ThrowIfCancellationRequested();
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);

        var job = StartTransfer(new TransferTaskRecord(Guid.NewGuid(), _api!.BaseAddress.AbsoluteUri,
            _currentUser!.Id, asset.OriginalFileName, false) { Asset = asset, TargetDirectory = targetDirectory });
        using var registration = cancellationToken.Register(job.Control.Cancel);
        return (await job.Work).Path;
    }

    private async Task<string?> ChooseDownloadDirectoryAsync(string title)
    {
        var destinationWindow = new CloudDownloadDestinationWindow(
            LocalFolderNodes,
            _settings.DownloadToDefaultDirectory)
        {
            Title = UiLocalization.Text(title)
        };
        var destination = await destinationWindow.ShowDialog<CloudDownloadDestination?>(this);
        if (destination is null)
        {
            return null;
        }

        _settings = _settings with
        {
            PersistentDownloadDirectory = destination.SetAsDefault
                ? destination.DirectoryPath
                : _settings.PersistentDownloadDirectory,
            DownloadToDefaultDirectory = destination.UseDefaultForFuture
        };
        if (destination.SetAsDefault)
        {
            DownloadDirectoryText.Text = destination.DirectoryPath;
        }

        await SaveSettingsAsync();
        return destination.DirectoryPath;
    }

    private async void UploadAvatar_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_api is null || _currentUser is null)
        {
            return;
        }

        var files = await RunNativePickerAsync(
            "upload-avatar",
            () => StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = UiLocalization.Text("选择头像"),
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType(UiLocalization.Text("头像图片"))
                    {
                        Patterns = ["*.jpg", "*.jpeg", "*.png", "*.webp"],
                        MimeTypes = ["image/jpeg", "image/png", "image/webp"]
                    }
                ]
            }),
            ProfileStatusText);
        if (files is null || files.Count == 0 ||
            string.IsNullOrWhiteSpace(files[0].Path.LocalPath))
        {
            return;
        }

        UiLocalization.SetText(ProfileStatusText, "正在裁剪并净化头像...");
        try
        {
            var normalized = await AvatarNormalizer.NormalizeFileAsync(files[0].Path.LocalPath);
            await using var source = normalized.OpenRead();
            var updated = await _api.UploadAvatarAsync(source, normalized.SizeBytes);
            _currentUser = updated;
            _profileSnapshot = updated;
            await LoadProfileAvatarAsync(updated.Id, hasAvatar: true);
            await LoadCurrentUserAvatarAsync(updated.Id, hasAvatar: true);
            UiLocalization.SetText(ProfileStatusText, "头像已更新为 512 x 512 静态 WebP。");
        }
        catch (Exception exception)
        {
            UiLocalization.SetText(ProfileStatusText, "头像上传失败：{0}", UserMessage(exception));
        }
    }

    private async void DeleteAvatar_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_api is null || _currentUser is null)
        {
            return;
        }

        var confirmed = await new MessageDialogWindow(
                "移除头像",
                "确定删除当前头像吗？删除后将显示默认头像，以后仍可重新上传。",
                "确认删除",
                "取消")
            .ShowDialog<bool>(this);
        if (!confirmed)
        {
            return;
        }

        try
        {
            var updated = await _api.DeleteAvatarAsync();
            _currentUser = updated;
            _profileSnapshot = updated;
            ClearProfileAvatar();
            ClearCurrentUserAvatar();
            UiLocalization.SetText(ProfileStatusText, "头像已移除。");
        }
        catch (Exception exception)
        {
            UiLocalization.SetText(ProfileStatusText, "移除失败：{0}", UserMessage(exception));
        }
    }

    private async Task LoadProfileAvatarAsync(Guid userId, bool hasAvatar)
    {
        var requestVersion = Interlocked.Increment(ref _profileAvatarRequestVersion);
        if (!ProfilePage.IsVisible)
        {
            return;
        }

        if (!hasAvatar || _api is null)
        {
            ClearProfileAvatar();
            return;
        }

        try
        {
            using var memory = new MemoryStream();
            await _api.DownloadUserAvatarAsync(userId, memory);
            memory.Position = 0;
            var bitmap = new Bitmap(memory);
            if (!ProfilePage.IsVisible ||
                requestVersion != Volatile.Read(ref _profileAvatarRequestVersion) ||
                _displayedProfileUserId != userId)
            {
                bitmap.Dispose();
                return;
            }
            DeferredUiResourceDisposer.Dispose(_profileAvatarBitmap);
            _profileAvatarBitmap = bitmap;
            ProfileAvatarImage.Source = bitmap;
            ProfileAvatarImage.IsVisible = true;
            ProfileAvatarInitial.IsVisible = false;
        }
        catch
        {
            if (ProfilePage.IsVisible &&
                requestVersion == Volatile.Read(ref _profileAvatarRequestVersion) &&
                _displayedProfileUserId == userId)
            {
                ClearProfileAvatar();
            }
        }
    }

    private void ClearProfileAvatar()
    {
        Interlocked.Increment(ref _profileAvatarRequestVersion);
        ProfileAvatarImage.Source = null;
        ProfileAvatarImage.IsVisible = false;
        ProfileAvatarInitial.IsVisible = true;
        DeferredUiResourceDisposer.Dispose(_profileAvatarBitmap);
        _profileAvatarBitmap = null;
    }

    private async Task LoadCurrentUserAvatarAsync(Guid userId, bool hasAvatar)
    {
        if (!hasAvatar || _api is null)
        {
            ClearCurrentUserAvatar();
            return;
        }

        try
        {
            using var memory = new MemoryStream();
            await _api.DownloadUserAvatarAsync(userId, memory);
            if (_currentUser?.Id != userId)
            {
                return;
            }

            memory.Position = 0;
            var bitmap = new Bitmap(memory);
            if (_currentUser?.Id != userId)
            {
                bitmap.Dispose();
                return;
            }

            DeferredUiResourceDisposer.Dispose(_currentUserAvatarBitmap);
            _currentUserAvatarBitmap = bitmap;
            CurrentUserAvatarImage.Source = bitmap;
            CurrentUserAvatarImage.IsVisible = true;
            CurrentUserInitial.IsVisible = false;
        }
        catch
        {
            if (_currentUser?.Id == userId)
            {
                ClearCurrentUserAvatar();
            }
        }
    }

    private void ClearCurrentUserAvatar()
    {
        CurrentUserAvatarImage.Source = null;
        CurrentUserAvatarImage.IsVisible = false;
        CurrentUserInitial.IsVisible = true;
        DeferredUiResourceDisposer.Dispose(_currentUserAvatarBitmap);
        _currentUserAvatarBitmap = null;
    }

    private async void AssetCard_OnPointerEntered(object? sender, PointerEventArgs eventArgs)
    {
        if (LocalHoverToggle.IsChecked != true || _isDragging ||
            sender is not Border { DataContext: AssetCardViewModel asset } card ||
            !asset.IsAvailable ||
            eventArgs.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (CanUsePlaybackPreview(asset.MediaType, asset.FullPath))
        {
            CloseLocalImagePreview();
            await BeginHoverLocalPreviewAsync(asset);
            return;
        }

        if (asset.MediaType != LocalMediaType.Image)
        {
            return;
        }

        CloseLocalImagePreview();
        _hoveredLocalAssetId = asset.Id;
        var generation = ++_localHoverPreviewGeneration;
        var cancellation = new CancellationTokenSource();
        Interlocked.Exchange(ref _localHoverPreviewCancellation, cancellation)?.Cancel();
        try
        {
            var thumbnail = await _mediaThumbnailService.GetOrCreateAsync(
                asset.FullPath,
                maximumEdge: 960,
                cancellation.Token);
            if (thumbnail is null || cancellation.IsCancellationRequested ||
                generation != _localHoverPreviewGeneration ||
                _hoveredLocalAssetId != asset.Id || !card.IsPointerOver || _isDragging)
            {
                return;
            }

            var bitmap = new Bitmap(thumbnail.CachePath);
            if (generation != _localHoverPreviewGeneration || !card.IsPointerOver || _isDragging)
            {
                bitmap.Dispose();
                return;
            }

            _localHoverPreviewBitmap = bitmap;
            LocalImagePreviewImage.Source = bitmap;
            LocalImagePreviewName.Text = asset.Name;
            LocalImagePreviewPopup.PlacementTarget = card;
            LocalImagePreviewPopup.IsOpen = true;
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (ArgumentException)
        {
        }
        catch (Exception exception)
        {
            ClientDiagnostics.WriteException("local-hover-image-preview", exception, isFatal: false);
        }
        finally
        {
            Interlocked.CompareExchange(ref _localHoverPreviewCancellation, null, cancellation);
            cancellation.Dispose();
        }
    }

    private async void AssetCard_OnPointerExited(object? sender, PointerEventArgs eventArgs)
    {
        if (sender is not Border { DataContext: AssetCardViewModel asset })
        {
            return;
        }

        if (_hoveredLocalAssetId == asset.Id)
        {
            CloseLocalImagePreview();
        }

        var key = $"local:{asset.Id:N}";
        await EndHoverPreviewAsync(key);
        CommitHoverDetailSelection(key);
    }

    private void AssetCard_OnPointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (sender is not Border { DataContext: AssetCardViewModel asset })
        {
            return;
        }

        if (OriginatesFromCheckBox(eventArgs))
        {
            eventArgs.Handled = true;
            ResetPendingDragGesture();
            return;
        }

        CloseLocalImagePreview();
        CommitHoverDetailSelection();
        SelectLocalAsset(asset);
        if (eventArgs.GetCurrentPoint(this).Properties.IsLeftButtonPressed &&
            _settings.SingleClickPreviewEnabled &&
            asset.IsAvailable &&
            CanUsePlaybackPreview(asset.MediaType, asset.FullPath))
        {
            ObserveNavigationTask(BeginSelectedLocalPreviewAsync(asset), "start-single-click-local-preview");
        }
        else
        {
            ObserveNavigationTask(StopDetailPreviewAsync(savePosition: true), "stop-selected-local-preview");
        }

        if (eventArgs.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginDragGesture(eventArgs.GetPosition(this), asset.FullPath);
        }
    }

    private void AssetCard_OnPointerReleased(object? sender, PointerReleasedEventArgs eventArgs)
    {
        ResetPendingDragGesture();
    }

    private void LocalAsset_OnContextRequested(
        object? sender,
        ContextRequestedEventArgs eventArgs)
    {
        if (sender is not Control { DataContext: AssetCardViewModel asset } target)
        {
            return;
        }

        SelectLocalAssetForContextMenu(asset);
        target.ContextMenu?.Close();
        var menu = CreateLocalAssetContextMenu(asset);
        target.ContextMenu = menu;
        menu.Open(target);
        eventArgs.Handled = true;
    }

    private async void OpenLocalAssetLocation_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not MenuItem { Tag: AssetCardViewModel asset })
        {
            return;
        }

        await RunLocalFileSystemMenuActionAsync(
            "open-local-asset-location",
            "无法打开文件",
            () =>
            {
                EnsureLocalAssetFileAvailable(asset);
                OpenFileInManager(asset.FullPath);
                return Task.CompletedTask;
            });
    }

    private async void AssetCard_OnPointerMoved(object? sender, PointerEventArgs eventArgs)
    {
        if (_isDragging || _isDragStartPending ||
            sender is not Border { DataContext: AssetCardViewModel asset } || !asset.IsAvailable)
        {
            return;
        }

        if (!eventArgs.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            ResetPendingDragGesture();
            return;
        }

        if (_dragStart is not { } dragStart)
        {
            return;
        }

        var currentPosition = eventArgs.GetPosition(this);
        const double dragThreshold = 4;
        if (Math.Abs(currentPosition.X - dragStart.X) < dragThreshold &&
            Math.Abs(currentPosition.Y - dragStart.Y) < dragThreshold)
        {
            return;
        }

        var gestureVersion = Volatile.Read(ref _dragGestureVersion);
        var preparedFile = PreparedDragFile(asset.FullPath);
        if (!TryBeginDragStartOperation())
        {
            return;
        }

        try
        {
            CloseLocalImagePreview();
            _dragStart = null;
            await DragLocalAssetsAsync(eventArgs, asset, gestureVersion, preparedFile);
        }
        catch (Exception exception)
        {
            ClientDiagnostics.WriteException("local-asset-drag", exception, isFatal: false);
            UiLocalization.SetText(LocalSummaryText, "拖入失败：{0}", UserMessage(exception));
        }
        finally
        {
            EndDragStartOperation();
        }
    }

    private void CloseLocalImagePreview()
    {
        _localHoverPreviewGeneration++;
        _hoveredLocalAssetId = null;
        Interlocked.Exchange(ref _localHoverPreviewCancellation, null)?.Cancel();
        LocalImagePreviewPopup.IsOpen = false;
        LocalImagePreviewPopup.PlacementTarget = null;
        LocalImagePreviewImage.Source = null;
        LocalImagePreviewName.Text = string.Empty;
        DeferredUiResourceDisposer.Dispose(_localHoverPreviewBitmap);
        _localHoverPreviewBitmap = null;
    }

    private void SelectLocalAsset(AssetCardViewModel asset)
    {
        ClearDetailSelectionState();
        _selectedLocalAsset = asset;
        _selectedCloudAsset = null;
        asset.IsDetailSelected = true;
        DetailPreviewBorder.DataContext = asset;
        DetailInitial.Text = asset.Initial;
        DetailExtension.Text = asset.Extension;
        DetailName.Text = asset.Name;
        UiLocalization.SetText(
            DetailMeta,
            asset.MediaType switch
            {
                LocalMediaType.Audio => "音频  ·  {0}",
                LocalMediaType.Image => "图片  ·  {0}",
                LocalMediaType.Video => "视频  ·  {0}",
                _ => "文件  ·  {0}"
            },
            asset.SizeLabel);
        DetailAdded.Text = asset.AddedLabel;
        if (asset.Tags.Count == 0)
        {
            UiLocalization.SetText(DetailTags, "未添加");
        }
        else
        {
            DetailTags.Text = string.Join("、", asset.Tags);
        }
        UiLocalization.SetText(
            DetailStatus,
            asset.Availability switch
            {
                LocalAssetAvailability.Available => "可用",
                LocalAssetAvailability.OfflineStorage => "磁盘离线",
                _ => "暂不可用"
            });
        DetailNotes.Text = "-";
        UiLocalization.SetText(DetailMarkerSummary, "正在读取标记...");
        EditTagsButton.IsEnabled = true;
        ManageMarkersButton.IsEnabled = true;
        UiLocalization.SetContent(OpenAssetLocationButton, "打开位置");
        UploadLocalAssetButton.IsVisible = true;
        UploadLocalAssetButton.IsEnabled = asset.IsAvailable && HasCurrentUserPermission("assets.upload");
        DownloadCloudAssetButton.IsVisible = false;
        DownloadCloudAssetButton.IsEnabled = false;
        SetCloudAssetOwnerActions(null);
        UiLocalization.SetContent(DragAssetButton, "在编辑器中打开");
        DetailPlaybackControls.IsVisible = asset.IsAvailable &&
                                           CanUsePlaybackPreview(asset.MediaType, asset.FullPath);
        BeginLocalDetailArtworkLoad(asset);
        _ = RefreshSelectedMarkerSummaryAsync(asset.Id);
    }

    private void SelectCloudAsset(CloudAssetCardViewModel asset)
    {
        ClearDetailSelectionState();
        _selectedCloudAsset = asset;
        _selectedLocalAsset = null;
        asset.IsDetailSelected = true;
        DetailPreviewBorder.DataContext = asset;
        DetailInitial.Text = asset.Initial;
        DetailExtension.Text = asset.Extension;
        DetailName.Text = asset.Name;
        UiLocalization.SetText(
            DetailMeta,
            asset.Asset.Category switch
            {
                ApiAssetCategory.Bgm => "BGM  ·  {0}",
                ApiAssetCategory.SoundEffect => "音效  ·  {0}",
                ApiAssetCategory.Image => "图片  ·  {0}",
                ApiAssetCategory.Video => "视频  ·  {0}",
                _ => "素材  ·  {0}"
            },
            asset.SizeLabel);
        DetailAdded.Text = asset.Asset.UploadedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        if (asset.Asset.Tags.Count == 0)
        {
            UiLocalization.SetText(DetailTags, "未添加");
        }
        else
        {
            DetailTags.Text = string.Join("、", asset.Asset.Tags);
        }
        UiLocalization.SetText(
            DetailStatus,
            asset.Asset.HasOriginal
                ? "可下载  ·  上传者 @{0}"
                : "等待上传  ·  上传者 @{0}",
            asset.Asset.UploadedBy.Username);
        if (string.IsNullOrWhiteSpace(asset.Asset.Notes))
        {
            UiLocalization.SetText(DetailNotes, "未填写");
        }
        else
        {
            DetailNotes.Text = asset.Asset.Notes;
        }
        UiLocalization.SetText(DetailMarkerSummary, "正在读取云端标记...");
        EditTagsButton.IsEnabled = _currentUser?.IsAdmin == true ||
            _currentUser?.Permissions.Contains("assets.tags", StringComparer.OrdinalIgnoreCase) == true;
        ManageMarkersButton.IsEnabled = true;
        UiLocalization.SetContent(OpenAssetLocationButton, "打开下载位置");
        UploadLocalAssetButton.IsVisible = false;
        DownloadCloudAssetButton.IsVisible = true;
        DownloadCloudAssetButton.IsEnabled = asset.Asset.HasOriginal;
        SetCloudAssetOwnerActions(asset.Asset);
        UiLocalization.SetContent(
            DragAssetButton,
            asset.Asset.HasOriginal ? "下载并拖入" : "等待原文件");
        DetailPlaybackControls.IsVisible = asset.Asset.HasOriginal &&
                                           asset.Asset.Category is ApiAssetCategory.Bgm or
                                               ApiAssetCategory.SoundEffect or ApiAssetCategory.Video ||
                                           asset.Asset.HasOriginal && asset.Asset.Category == ApiAssetCategory.Image &&
                                           IsAnimatedImage(asset.Asset.Extension);
        BeginCloudDetailArtworkLoad(asset);
        _ = RefreshCloudMarkerSummaryAsync(asset.Asset);
    }

    private void ClearDetailSelectionState()
    {
        if (_selectedLocalAsset is not null)
        {
            _selectedLocalAsset.IsDetailSelected = false;
        }

        if (_selectedCloudAsset is not null)
        {
            _selectedCloudAsset.IsDetailSelected = false;
        }
    }

    private void BeginLocalDetailArtworkLoad(AssetCardViewModel asset)
    {
        ResetDetailArtwork(asset.Thumbnail);
        if (!asset.IsAvailable || asset.MediaType != LocalMediaType.Audio)
        {
            return;
        }

        var generation = _detailArtworkGeneration;
        var cancellation = BeginDetailArtworkOperation();
        _ = LoadOwnedDetailWaveformAsync(
            asset.FullPath,
            generation,
            cancellation,
            () => _selectedLocalAsset?.Id == asset.Id);
    }

    private void BeginCloudDetailArtworkLoad(CloudAssetCardViewModel asset)
    {
        ResetDetailArtwork(asset.Thumbnail);
        if (!asset.Asset.HasOriginal ||
            asset.Asset.Category is not (ApiAssetCategory.Bgm or ApiAssetCategory.SoundEffect))
        {
            return;
        }

        var generation = _detailArtworkGeneration;
        var cancellation = BeginDetailArtworkOperation();
        _ = LoadCloudDetailWaveformAsync(asset, generation, cancellation);
    }

    private async Task LoadCloudDetailWaveformAsync(
        CloudAssetCardViewModel asset,
        int generation,
        CancellationTokenSource cancellation)
    {
        try
        {
            var path = await TryGetCloudPreviewPathAsync(
                asset.Asset,
                allowOriginalDownload: false,
                cancellation.Token);
            if (path is null)
            {
                return;
            }

            await LoadDetailWaveformAsync(
                path,
                generation,
                cancellation.Token,
                () => _selectedCloudAsset?.Id == asset.Id);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ClientDiagnostics.WriteException("cloud-detail-waveform", exception, isFatal: false);
        }
        finally
        {
            FinishDetailArtworkOperation(cancellation);
        }
    }

    private async Task LoadOwnedDetailWaveformAsync(
        string sourcePath,
        int generation,
        CancellationTokenSource cancellation,
        Func<bool> isStillSelected)
    {
        try
        {
            await LoadDetailWaveformAsync(
                sourcePath,
                generation,
                cancellation.Token,
                isStillSelected);
        }
        finally
        {
            FinishDetailArtworkOperation(cancellation);
        }
    }

    private async Task LoadDetailWaveformAsync(
        string sourcePath,
        int generation,
        CancellationToken cancellationToken,
        Func<bool> isStillSelected)
    {
        try
        {
            var waveform = await _mediaThumbnailService.GetOrCreateWaveformAsync(
                sourcePath,
                maximumWidth: 960,
                cancellationToken);
            if (waveform is null || cancellationToken.IsCancellationRequested ||
                generation != _detailArtworkGeneration || !isStillSelected())
            {
                return;
            }

            var bitmap = new Bitmap(waveform.CachePath);
            if (generation != _detailArtworkGeneration || !isStillSelected())
            {
                bitmap.Dispose();
                return;
            }

            var previous = _detailArtworkBitmap;
            _detailArtworkBitmap = bitmap;
            DetailPreviewImage.Source = bitmap;
            DetailPreviewImage.IsVisible = true;
            DetailInitial.IsVisible = false;
            DeferredUiResourceDisposer.Dispose(previous);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException)
        {
            ClientDiagnostics.WriteException("detail-waveform", exception, isFatal: false);
        }
    }

    private void ResetDetailArtwork(Bitmap? fallback)
    {
        _detailArtworkGeneration++;
        Interlocked.Exchange(ref _detailArtworkCancellation, null)?.Cancel();
        DetailPreviewImage.Source = fallback;
        DetailPreviewImage.IsVisible = fallback is not null;
        DetailInitial.IsVisible = fallback is null;
        DeferredUiResourceDisposer.Dispose(_detailArtworkBitmap);
        _detailArtworkBitmap = null;
    }

    private CancellationTokenSource BeginDetailArtworkOperation()
    {
        var cancellation = new CancellationTokenSource();
        Interlocked.Exchange(ref _detailArtworkCancellation, cancellation)?.Cancel();
        return cancellation;
    }

    private void FinishDetailArtworkOperation(CancellationTokenSource cancellation)
    {
        Interlocked.CompareExchange(ref _detailArtworkCancellation, null, cancellation);
        cancellation.Dispose();
    }

    private void ClearDetailArtwork()
    {
        _detailArtworkGeneration++;
        Interlocked.Exchange(ref _detailArtworkCancellation, null)?.Cancel();
        DetailPreviewImage.Source = null;
        DetailPreviewImage.IsVisible = false;
        DetailInitial.IsVisible = true;
        DeferredUiResourceDisposer.Dispose(_detailArtworkBitmap);
        _detailArtworkBitmap = null;
    }

    private async Task RefreshCloudMarkerSummaryAsync(ApiAsset asset)
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            var sets = await _api.ListMarkerSetsAsync(asset.Id);
            if (_selectedCloudAsset?.Id == asset.Id)
            {
                if (sets.Count == 0)
                {
                    UiLocalization.SetText(DetailMarkerSummary, "暂无标记");
                }
                else
                {
                    UiLocalization.SetText(
                        DetailMarkerSummary,
                        "{0:N0} 个标记集，{1:N0} 个标记",
                        sets.Count,
                        sets.Sum(set => set.MarkerCount));
                }
            }
        }
        catch (Exception exception)
        {
            if (_selectedCloudAsset?.Id == asset.Id)
            {
                UiLocalization.SetText(
                    DetailMarkerSummary,
                    "读取失败：{0}",
                    UserMessage(exception));
            }
        }
    }

    private async Task RefreshSelectedMarkerSummaryAsync(Guid assetId)
    {
        try
        {
            var sets = await _localMarkerService.ListAsync(assetId);
            if (_selectedLocalAsset?.Id == assetId)
            {
                var markerCount = sets.Sum(set => set.MarkerCount);
                if (sets.Count == 0)
                {
                    UiLocalization.SetText(DetailMarkerSummary, "暂无标记");
                }
                else
                {
                    UiLocalization.SetText(
                        DetailMarkerSummary,
                        "{0:N0} 个标记集，{1:N0} 个标记",
                        sets.Count,
                        markerCount);
                }
            }
        }
        catch (Exception exception)
        {
            if (_selectedLocalAsset?.Id == assetId)
            {
                UiLocalization.SetText(DetailMarkerSummary, "读取失败：{0}", exception.Message);
            }
        }
    }

    private async void EditLocalTags_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_selectedLocalAsset is { } selected)
        {
            var localTagLibraryChanged = false;
            try
            {
                var result = await new TagPickerWindow(
                        "编辑本地标签",
                        "先添加到本地标签库，再勾选要用于当前素材的标签。",
                        _catalog.TagLibrary.Select(name => new TagPickerEntry(
                            null,
                            name,
                            _catalog.Assets.Count(asset => asset.Tags.Contains(
                                name,
                                StringComparer.OrdinalIgnoreCase)))),
                        selected.Tags,
                        async name =>
                        {
                            _catalog = await _indexService.CreateTagAsync(name);
                            localTagLibraryChanged = true;
                            var persistedName = _catalog.TagLibrary.Single(tag =>
                                tag.Equals(name, StringComparison.OrdinalIgnoreCase));
                            return new TagPickerEntry(null, persistedName, 0);
                        },
                        async entry =>
                        {
                            _catalog = await _indexService.DeleteTagAsync(entry.Name);
                            localTagLibraryChanged = true;
                        })
                    .ShowDialog<TagPickerResult?>(this);
                if (result is null)
                {
                    if (localTagLibraryChanged)
                    {
                        RefreshLocalAssetAfterTagChange(selected.Id);
                    }

                    return;
                }

                _catalog = await _indexService.SetTagsAsync(selected.Id, result.Tags);
                RefreshLocalAssetAfterTagChange(selected.Id);
            }
            catch (Exception exception)
            {
                if (localTagLibraryChanged)
                {
                    RefreshLocalAssetAfterTagChange(selected.Id);
                }

                UiLocalization.SetText(LocalSummaryText, "标签保存失败：{0}", exception.Message);
            }

            return;
        }

        if (_selectedCloudAsset is not { } cloud || _cloudTagLibraryService is null)
        {
            return;
        }

        var cloudTagLibraryChanged = false;
        try
        {
            var availableTags = await _cloudTagLibraryService.ListAsync();
            var result = await new TagPickerWindow(
                    "编辑共享标签",
                    "先添加到云端团队标签库，再勾选要用于当前素材的标签。",
                    availableTags.Select(tag => new TagPickerEntry(tag.Id, tag.Name, tag.UsageCount)),
                    cloud.Asset.Tags,
                    async name =>
                    {
                        cloudTagLibraryChanged = true;
                        return await CreateCloudTagEntryFromPickerAsync(name);
                    },
                    _currentUser?.IsAdmin == true
                        ? async entry =>
                        {
                            if (entry.Id is not { } tagId)
                            {
                                throw new InvalidOperationException(
                                    UiLocalization.Text("云端标签缺少有效 ID。"));
                            }

                            await _cloudTagLibraryService.DeleteAsync(tagId);
                            cloudTagLibraryChanged = true;
                        }
                        : null)
                .ShowDialog<TagPickerResult?>(this);
            if (result is null)
            {
                if (cloudTagLibraryChanged)
                {
                    await RefreshCloudAssetAndReselectAsync(cloud.Id);
                }

                return;
            }

            var updated = await _cloudTagLibraryService.AssignExistingAsync(cloud.Id, result.Tags);
            await RefreshCloudAssetAndReselectAsync(updated.Id);
        }
        catch (Exception exception)
        {
            var message = UserMessage(exception);
            if (cloudTagLibraryChanged)
            {
                try
                {
                    await RefreshCloudAssetAndReselectAsync(cloud.Id);
                }
                catch
                {
                    // Keep the original save failure visible.
                }
            }

            UiLocalization.SetText(CloudSummaryText, "标签保存失败：{0}", message);
        }
    }

    private void RefreshLocalAssetAfterTagChange(Guid assetId)
    {
        ApplyLocalFilter();
        var updatedCard = VisibleLocalAssets.FirstOrDefault(asset => asset.Id == assetId);
        if (updatedCard is null)
        {
            CloseDetail_OnClick(null, new RoutedEventArgs());
        }
        else
        {
            SelectLocalAsset(updatedCard);
        }
    }

    private async Task RefreshCloudAssetAndReselectAsync(Guid assetId)
    {
        await RefreshCloudAssetsAsync();
        var updatedCard = VisibleCloudAssets.FirstOrDefault(asset => asset.Id == assetId);
        if (updatedCard is null)
        {
            CloseDetail_OnClick(null, new RoutedEventArgs());
        }
        else
        {
            SelectCloudAsset(updatedCard);
        }
    }

    private async Task<string> CreateCloudTagFromPickerAsync(string name)
    {
        if (_cloudTagLibraryService is null)
        {
            throw new InvalidOperationException(UiLocalization.Text("云端标签库尚未连接。"));
        }

        try
        {
            return (await _cloudTagLibraryService.CreateAsync(name)).Name;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(UserMessage(exception), exception);
        }
    }

    private async Task<TagPickerEntry> CreateCloudTagEntryFromPickerAsync(string name)
    {
        if (_cloudTagLibraryService is null)
        {
            throw new InvalidOperationException(UiLocalization.Text("云端标签库尚未连接。"));
        }

        try
        {
            var tag = await _cloudTagLibraryService.CreateAsync(name);
            return new TagPickerEntry(tag.Id, tag.Name, tag.UsageCount);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(UserMessage(exception), exception);
        }
    }

    private async void ManageLocalMarkers_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_selectedLocalAsset is { } selected)
        {
            var window = new LocalMarkerWindow(
                _localMarkerService,
                selected.Id,
                selected.Name,
                selected.FullPath);
            await window.ShowDialog(this);
            await RefreshSelectedMarkerSummaryAsync(selected.Id);
            return;
        }

        if (_selectedCloudAsset is not { } cloud || _api is null || _currentUser is null)
        {
            return;
        }

        await new CloudMarkerWindow(
                _api,
                cloud.Asset,
                _currentUser,
                () => EnsureCloudAssetDownloadedAsync(cloud.Asset))
            .ShowDialog(this);
        await RefreshCloudMarkerSummaryAsync(cloud.Asset);
    }

    private async void OpenSelectedLocalAsset_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_selectedLocalAsset is { } selected)
        {
            if (!selected.IsAvailable || !File.Exists(selected.FullPath))
            {
                UiLocalization.SetText(DetailStatus, "文件当前不可用，重新连接磁盘后刷新素材库。");
                return;
            }

            try
            {
                OpenFileInManager(selected.FullPath);
            }
            catch (Exception exception)
            {
                ClientDiagnostics.WriteException("open-local-asset-location", exception, isFatal: false);
                UiLocalization.SetText(DetailStatus, "打开位置失败：{0}", UserMessage(exception));
            }

            return;
        }

        if (_selectedCloudAsset is not { } cloud)
        {
            return;
        }

        try
        {
            var path = await EnsureCloudAssetDownloadedAsync(cloud.Asset);
            if (path is not null)
            {
                OpenFileInManager(path);
                UiLocalization.SetContent(DragAssetButton, "在编辑器中打开");
            }
        }
        catch (OperationCanceledException)
        {
            UiLocalization.SetText(DetailStatus, "已取消下载。");
        }
        catch (Exception exception)
        {
            UiLocalization.SetText(DetailStatus, "下载失败：{0}", UserMessage(exception));
        }
    }

    private async void DownloadSelectedCloudAsset_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_selectedCloudAsset is not { } selected || !selected.Asset.HasOriginal)
        {
            return;
        }

        DownloadCloudAssetButton.IsEnabled = false;
        UiLocalization.SetText(DetailStatus, "正在下载素材…");
        try
        {
            var path = await EnsureCloudAssetDownloadedAsync(selected.Asset);
            if (path is not null && _selectedCloudAsset?.Id == selected.Id)
            {
                UiLocalization.SetText(DetailStatus, "已下载到：{0}", path);
                ShowTransientNotification("下载完成：{0}", Path.GetFileName(path));
            }
        }
        catch (OperationCanceledException)
        {
            if (_selectedCloudAsset?.Id == selected.Id)
            {
                UiLocalization.SetText(DetailStatus, "已取消下载。");
            }
        }
        catch (Exception exception)
        {
            if (_selectedCloudAsset?.Id == selected.Id)
            {
                UiLocalization.SetText(DetailStatus, "下载失败：{0}", UserMessage(exception));
            }
        }
        finally
        {
            if (_selectedCloudAsset?.Id == selected.Id)
            {
                DownloadCloudAssetButton.IsEnabled = selected.Asset.HasOriginal;
            }
        }
    }

    private async void UploadSelectedLocalAsset_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_selectedLocalAsset is not { IsAvailable: true } selected ||
            !HasCurrentUserPermission("assets.upload") || _api is null || _transferService is null ||
            _cloudTagLibraryService is null)
        {
            UiLocalization.SetText(
                DetailStatus,
                _currentUser is null
                    ? "请先连接并登录服务器。"
                    : _selectedLocalAsset is not { IsAvailable: true }
                        ? "当前本地文件不可用。"
                        : "当前账号的上传权限已锁闭。");
            return;
        }

        var targetFolderId = _selectedCloudFolderId;
        if (!File.Exists(selected.FullPath))
        {
            UiLocalization.SetText(DetailStatus, "本地文件不存在，请刷新素材库。");
            return;
        }

        IReadOnlyList<ApiTag> availableTags;
        try
        {
            availableTags = await _cloudTagLibraryService.ListAsync();
        }
        catch (Exception exception)
        {
            UiLocalization.SetText(DetailStatus, "云端标签库读取失败：{0}", UserMessage(exception));
            return;
        }

        var options = await new UploadOptionsWindow(
                [selected.FullPath],
                selected.Tags,
                availableTags.Select(tag => tag.Name).ToArray(),
                HasCurrentUserPermission("assets.tags") ? CreateCloudTagFromPickerAsync : null)
            .ShowDialog<UploadBatchOptions?>(this);
        if (options is null)
        {
            return;
        }

        IReadOnlyList<string> canonicalTags;
        try
        {
            canonicalTags = await _cloudTagLibraryService.EnsureAsync(options.Tags);
        }
        catch (Exception exception)
        {
            UiLocalization.SetText(DetailStatus, "云端标签准备失败：{0}", UserMessage(exception));
            return;
        }

        var category = selected.MediaType switch
        {
            LocalMediaType.Audio => options.AudioCategory,
            LocalMediaType.Image => ApiAssetCategory.Image,
            LocalMediaType.Video => ApiAssetCategory.Video,
            _ => throw new ArgumentOutOfRangeException()
        };
        var job = QueueUpload(selected.FullPath, category, options.Notes, canonicalTags, targetFolderId);
        var transfer = job.Item;
        UploadLocalAssetButton.IsEnabled = false;
        try
        {
            var result = await job.Work;
            if (result.Duplicate)
            {
                UiLocalization.SetText(DetailStatus, "云端已存在相同内容，未重复上传。");
                return;
            }
            var uploaded = result.Asset!;
            var previewWarning = result.PreviewWarning;
            int copiedMarkerCount;
            try
            {
                copiedMarkerCount = await CopyLocalMarkersToCloudAsync(selected.Id, uploaded);
            }
            catch (Exception markerException)
            {
                transfer.Status = previewWarning
                    ? "文件已上传，预览与标记均不完整"
                    : "文件已上传，标记复制失败";
                transfer.Progress = 100;
                UiLocalization.SetText(
                    DetailStatus,
                    previewWarning
                        ? "文件已上传，但标记未完整复制：{0}；云端预览也未完整生成。"
                        : "文件已上传，但标记未完整复制：{0}",
                    UserMessage(markerException));
                await RefreshCloudAssetsAsync();
                return;
            }
            transfer.Status = previewWarning ? "原文件已上传，预览生成不完整" : "已完成";
            transfer.Progress = 100;
            if (copiedMarkerCount == 0)
            {
                UiLocalization.SetText(
                    DetailStatus,
                    previewWarning
                        ? "已上传到共享素材库。云端预览未完整生成，原文件不受影响。"
                        : "已上传到共享素材库。");
            }
            else
            {
                UiLocalization.SetText(
                    DetailStatus,
                    previewWarning
                        ? "已上传到共享素材库，并复制 {0:N0} 个标记。云端预览未完整生成，原文件不受影响。"
                        : "已上传到共享素材库，并复制 {0:N0} 个标记。",
                    copiedMarkerCount);
            }
            await RefreshCloudAssetsAsync();
        }
        catch (AssetLibraryApiException exception) when (exception.Code == "duplicate_content")
        {
            transfer.Status = "内容已存在";
            transfer.Progress = 100;
            UiLocalization.SetText(DetailStatus, "云端已存在相同内容，未重复上传。");
        }
        catch (Exception exception)
        {
            transfer.Status = UiLocalization.Format("失败：{0}", UserMessage(exception));
            UiLocalization.SetText(DetailStatus, "上传失败：{0}", UserMessage(exception));
        }
        finally
        {
            UploadLocalAssetButton.IsEnabled = _selectedLocalAsset?.Id == selected.Id &&
                selected.IsAvailable &&
                HasCurrentUserPermission("assets.upload");
        }
    }

    private async Task<int> CopyLocalMarkersToCloudAsync(Guid localAssetId, ApiAsset uploaded)
    {
        if (_api is null)
        {
            return 0;
        }

        var markerCount = 0;
        foreach (var summary in await _localMarkerService.ListAsync(localAssetId))
        {
            var localSet = await _localMarkerService.GetAsync(localAssetId, summary.Id);
            var cloudSet = await _api.CreateMarkerSetAsync(new CreateMarkerSetRequest(
                uploaded.Id,
                uploaded.CurrentVersionId,
                localSet.Name));
            foreach (var marker in localSet.Markers)
            {
                await _api.AddMarkerAsync(
                    cloudSet.Id,
                    new UpsertMarkerRequest(marker.Time, marker.Name, marker.Note));
                markerCount++;
            }
        }

        return markerCount;
    }

    private bool HasCurrentUserPermission(string permission) =>
        _currentUser?.IsAdmin == true ||
        _currentUser?.Permissions.Contains(permission, StringComparer.OrdinalIgnoreCase) == true;

    private void SetCloudAssetOwnerActions(ApiAsset? asset)
    {
        var ownsAsset = asset is not null &&
            asset.State == ApiAssetState.Active &&
            (_currentUser?.IsAdmin == true || _currentUser?.Id == asset.UploadedBy.Id);
        EditCloudAssetButton.IsVisible = ownsAsset && HasCurrentUserPermission("assets.edit-own");
        ReplaceCloudAssetButton.IsVisible = ownsAsset && HasCurrentUserPermission("assets.edit-own");
        RecycleCloudAssetButton.IsVisible = ownsAsset && HasCurrentUserPermission("assets.delete-own");
    }

    private async void EditSelectedCloudAsset_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_selectedCloudAsset is not { } selected || _api is null)
        {
            return;
        }

        var name = await new TextPromptWindow(
                "修改素材名称",
                "修改只影响共享素材库中的显示名称。",
                selected.Asset.Name)
            .ShowDialog<string?>(this);
        if (name is null)
        {
            return;
        }

        var notes = await new TextPromptWindow(
                "修改素材备注",
                "可留空；最多 2000 个字符。",
                selected.Asset.Notes)
            .ShowDialog<string?>(this);
        if (notes is null)
        {
            return;
        }

        try
        {
            var updated = await _api.UpdateAssetAsync(
                selected.Id,
                new ApiUpdateAssetRequest(name, string.IsNullOrWhiteSpace(notes) ? null : notes));
            await RefreshCloudAssetsAsync();
            var updatedCard = VisibleCloudAssets.FirstOrDefault(asset => asset.Id == updated.Id);
            if (updatedCard is null)
            {
                CloseDetail_OnClick(null, new RoutedEventArgs());
            }
            else
            {
                SelectCloudAsset(updatedCard);
            }
        }
        catch (Exception exception)
        {
            UiLocalization.SetText(DetailStatus, "保存失败：{0}", UserMessage(exception));
        }
    }

    private async void ReplaceSelectedCloudAsset_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_selectedCloudAsset is not { } selected || _api is null)
        {
            return;
        }

        var files = await RunNativePickerAsync(
            "replace-cloud-asset",
            () => StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = UiLocalization.Text("选择新的原文件"),
                AllowMultiple = false
            }),
            DetailStatus);
        if (files is null)
        {
            return;
        }

        var path = files.FirstOrDefault()?.Path.LocalPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        ReplaceCloudAssetButton.IsEnabled = false;
        try
        {
            var result = await QueueReplacement(path, selected.Asset).Work;
            var updated = result.Asset!;
            await RefreshCloudAssetsAsync();
            var refreshed = VisibleCloudAssets.FirstOrDefault(asset => asset.Id == updated.Id);
            if (refreshed is not null)
            {
                SelectCloudAsset(refreshed);
                if (result.PreviewWarning)
                {
                    UiLocalization.SetText(
                        DetailStatus,
                        "换源已完成，但云端预览未完整生成；原文件不受影响。");
                }
            }
        }
        catch (Exception exception)
        {
            UiLocalization.SetText(DetailStatus, "换源失败：{0}", UserMessage(exception));
        }
        finally
        {
            ReplaceCloudAssetButton.IsEnabled = true;
        }
    }

    private async void RecycleSelectedCloudAsset_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_selectedCloudAsset is not { } selected || _api is null)
        {
            return;
        }

        var confirmed = await new MessageDialogWindow(
                "将素材移至回收站",
                UiLocalization.Format("“{0}”将在个人回收站保留 15 天。", selected.Name),
                "确认移入",
                "取消")
            .ShowDialog<bool>(this);
        if (!confirmed)
        {
            return;
        }

        try
        {
            await _api.RecycleAssetAsync(selected.Id);
            CloseDetail_OnClick(null, new RoutedEventArgs());
            await RefreshCloudAssetsAsync();
            if (_displayedProfileUserId is not null)
            {
                await RefreshDisplayedProfileAssetsAsync();
            }
        }
        catch (Exception exception)
        {
            UiLocalization.SetText(DetailStatus, "删除失败：{0}", UserMessage(exception));
        }
    }

    private static void OpenFileInManager(string fullPath)
    {
        var normalizedPath = Path.GetFullPath(fullPath);
        if (OperatingSystem.IsWindows())
        {
            var start = new ProcessStartInfo("explorer.exe")
            {
                UseShellExecute = true,
                Arguments = $"/select,\"{normalizedPath}\""
            };
            Process.Start(start);
            return;
        }

        Process.Start(new ProcessStartInfo(Path.GetDirectoryName(normalizedPath)!) { UseShellExecute = true });
    }

    private static void OpenFolderInManager(string fullPath)
    {
        var normalizedPath = Path.GetFullPath(fullPath);
        if (OperatingSystem.IsWindows())
        {
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            start.ArgumentList.Add(normalizedPath);
            Process.Start(start);
            return;
        }

        Process.Start(new ProcessStartInfo(normalizedPath) { UseShellExecute = true });
    }

    private void SelectedAssetDrag_OnPointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if ((_selectedLocalAsset is { IsAvailable: true } || _selectedCloudAsset?.Asset.HasOriginal == true) &&
            eventArgs.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginDragGesture(
                eventArgs.GetPosition(this),
                _selectedLocalAsset is { IsAvailable: true } local ? local.FullPath : null);
        }
    }

    private async void SelectedAssetDrag_OnPointerMoved(object? sender, PointerEventArgs eventArgs)
    {
        if (_isDragging || _isDragStartPending ||
            _selectedLocalAsset is not { IsAvailable: true } && _selectedCloudAsset?.Asset.HasOriginal != true)
        {
            return;
        }

        if (!eventArgs.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            ResetPendingDragGesture();
            return;
        }

        if (_dragStart is not { } dragStart)
        {
            return;
        }

        var currentPosition = eventArgs.GetPosition(this);
        if (Math.Abs(currentPosition.X - dragStart.X) < 4 &&
            Math.Abs(currentPosition.Y - dragStart.Y) < 4)
        {
            return;
        }

        var gestureVersion = Volatile.Read(ref _dragGestureVersion);
        var localPath = _selectedLocalAsset is { IsAvailable: true } localAsset
            ? localAsset.FullPath
            : null;
        var preparedFile = localPath is null ? null : PreparedDragFile(localPath);
        if (!TryBeginDragStartOperation())
        {
            return;
        }

        try
        {
            _dragStart = null;
            if (_selectedLocalAsset is { IsAvailable: true } selected)
            {
                await DragLocalAssetsAsync(eventArgs, selected, gestureVersion, preparedFile);
            }
            else if (_selectedCloudAsset is { } cloud)
            {
                await DragCloudAssetAsync(eventArgs, cloud, gestureVersion);
            }
        }
        catch (Exception exception)
        {
            ClientDiagnostics.WriteException("selected-asset-drag", exception, isFatal: false);
            UiLocalization.SetText(DetailStatus, "拖入失败：{0}", UserMessage(exception));
        }
        finally
        {
            EndDragStartOperation();
        }
    }

    private void BeginDragGesture(Point start, string? localPath = null)
    {
        if (_shutdownStarted || !IsActive || Volatile.Read(ref _systemDragActive) != 0)
        {
            return;
        }

        ResetPendingDragGesture();
        _dragStart = start;
        if (!string.IsNullOrWhiteSpace(localPath) && File.Exists(localPath))
        {
            _preparedDragPath = Path.GetFullPath(localPath);
            _dragPreparationCancellation = new CancellationTokenSource();
            _preparedDragFile = ResolveDragFileAsync(
                _preparedDragPath,
                _dragPreparationCancellation.Token);
        }
    }

    private Task<IStorageFile?>? PreparedDragFile(string fullPath) =>
        _preparedDragPath is not null &&
        LocalPathComparer.Equals(_preparedDragPath, Path.GetFullPath(fullPath))
            ? _preparedDragFile
            : null;

    private void ResetPendingDragGesture()
    {
        Interlocked.Increment(ref _dragGestureVersion);
        _dragStart = null;
        _preparedDragPath = null;
        _preparedDragFile = null;
        var preparationCancellation = Interlocked.Exchange(ref _dragPreparationCancellation, null);
        if (preparationCancellation is not null)
        {
            preparationCancellation.Cancel();
            preparationCancellation.Dispose();
        }
        _isDragStartPending = false;

        // Keep the payload alive while the native drag loop is active. Avalonia
        // may deactivate the window as soon as the pointer leaves it, but the
        // drop target still needs these arrays until DoDragDropAsync returns.
        if (Volatile.Read(ref _systemDragActive) == 0)
        {
            _activeCloudDragPaths = null;
            _activeCloudDragAssets = null;
            _activeLocalDragAssets = null;
            _isDragging = false;
        }
    }

    private bool TryBeginDragStartOperation()
    {
        if (Interlocked.CompareExchange(ref _dragStartOperationActive, 1, 0) != 0)
        {
            return false;
        }

        _isDragStartPending = true;
        return true;
    }

    private void EndDragStartOperation()
    {
        _isDragStartPending = false;
        Volatile.Write(ref _dragStartOperationActive, 0);
    }

    private async Task<IStorageFile?> ResolveDragFileAsync(
        string fullPath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await StorageProvider
                .TryGetFileFromPathAsync(new Uri(Path.GetFullPath(fullPath)))
                .WaitAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            ClientDiagnostics.WriteException("prepare-file-drag", exception, isFatal: false);
            return null;
        }
    }

    private async Task<bool> BeginFileDragAsync(
        PointerEventArgs eventArgs,
        string fullPath,
        long gestureVersion,
        Task<IStorageFile?>? preparedFile = null)
    {
        if (!File.Exists(fullPath))
        {
            return false;
        }

        var file = await (preparedFile ?? ResolveDragFileAsync(fullPath));
        return file is not null && await BeginFilesDragAsync(
            eventArgs,
            [fullPath],
            gestureVersion,
            [file]);
    }

    private async Task<bool> DragLocalAssetsAsync(
        PointerEventArgs eventArgs,
        AssetCardViewModel anchor,
        long gestureVersion,
        Task<IStorageFile?>? preparedFile = null)
    {
        var ids = _isLocalBatchMode && anchor.IsBatchSelected
            ? VisibleLocalAssets
                .Where(asset => asset.IsBatchSelected && asset.IsAvailable)
                .Select(asset => asset.Id)
                .ToHashSet()
            : new HashSet<Guid> { anchor.Id };
        var assets = _catalog.Assets
            .Where(asset => ids.Contains(asset.Id) && asset.IsAvailable && File.Exists(asset.FullPath))
            .ToArray();
        if (assets.Length == 0)
        {
            return false;
        }

        _activeLocalDragAssets = assets;
        try
        {
            return assets.Length == 1
                ? await BeginFileDragAsync(
                    eventArgs,
                    assets[0].FullPath,
                    gestureVersion,
                    assets[0].Id == anchor.Id ? preparedFile : null)
                : await BeginFilesDragAsync(
                    eventArgs,
                    assets.Select(asset => asset.FullPath).ToArray(),
                    gestureVersion);
        }
        finally
        {
            _activeLocalDragAssets = null;
        }
    }

    private async Task<bool> BeginFilesDragAsync(
        PointerEventArgs eventArgs,
        IReadOnlyList<string> fullPaths,
        long gestureVersion,
        IReadOnlyList<IStorageFile>? preparedFiles = null)
    {
        if (fullPaths.Count == 0 ||
            fullPaths.Any(path => !File.Exists(path)) ||
            _shutdownStarted ||
            !IsActive ||
            Volatile.Read(ref _systemDragActive) != 0)
        {
            return false;
        }

        IReadOnlyList<IStorageFile> files;
        if (preparedFiles is not null)
        {
            files = preparedFiles;
        }
        else
        {
            var resolvedFiles = new List<IStorageFile>(fullPaths.Count);
            foreach (var path in fullPaths)
            {
                if (await ResolveDragFileAsync(path) is not { } resolved)
                {
                    return false;
                }

                resolvedFiles.Add(resolved);
            }

            files = resolvedFiles;
        }

        if (files.Count != fullPaths.Count ||
            gestureVersion != Volatile.Read(ref _dragGestureVersion) ||
            _shutdownStarted ||
            !IsActive ||
            Volatile.Read(ref _systemDragActive) != 0)
        {
            return false;
        }

        try
        {
            if (!eventArgs.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                return false;
            }
        }
        catch (Exception)
        {
            return false;
        }

        var data = new DataTransfer();
        foreach (var file in files)
        {
            data.Add(DataTransferItem.CreateFile(file));
        }
        _isDragging = true;
        if (Interlocked.CompareExchange(ref _systemDragActive, 1, 0) != 0)
        {
            _isDragging = false;
            return false;
        }
        try
        {
            await DragDrop.DoDragDropAsync(eventArgs, data, DragDropEffects.Copy);
            return true;
        }
        finally
        {
            Interlocked.Exchange(ref _systemDragActive, 0);
            _isDragging = false;
            _activeCloudDragPaths = null;
            _activeCloudDragAssets = null;
            _activeLocalDragAssets = null;
            _preparedDragPath = null;
            _preparedDragFile = null;
        }
    }

    private void CloseDetail_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        DiscardHoverDetailSelection();
        _ = StopDetailPreviewAsync(savePosition: true);
        ClearDetailSelectionState();
        _selectedLocalAsset = null;
        _selectedCloudAsset = null;
        ClearDetailArtwork();
        DetailPreviewBorder.DataContext = null;
        DetailInitial.Text = "?";
        DetailExtension.Text = "FILE";
        UiLocalization.SetText(DetailName, "未选择素材");
        UiLocalization.SetText(DetailMeta, "从素材列表中选择一项");
        DetailAdded.Text = "-";
        DetailTags.Text = "-";
        DetailStatus.Text = "-";
        DetailNotes.Text = "-";
        UiLocalization.SetText(DetailMarkerSummary, "选择素材后查看");
        EditTagsButton.IsEnabled = false;
        ManageMarkersButton.IsEnabled = false;
        UiLocalization.SetContent(OpenAssetLocationButton, "打开位置");
        UploadLocalAssetButton.IsVisible = false;
        UploadLocalAssetButton.IsEnabled = false;
        DownloadCloudAssetButton.IsVisible = false;
        DownloadCloudAssetButton.IsEnabled = false;
        SetCloudAssetOwnerActions(null);
        UiLocalization.SetContent(DragAssetButton, "在编辑器中打开");
    }

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async void ThemePicker_OnSelectionChanged(object? sender, SelectionChangedEventArgs eventArgs)
    {
        if (_isLoading)
        {
            return;
        }

        var preference = ThemePicker.SelectedIndex switch
        {
            1 => ThemePreference.Light,
            2 => ThemePreference.System,
            _ => ThemePreference.Dark
        };
        _settings = _settings with { Theme = preference };
        ApplyTheme(preference);
        try
        {
            await SaveSettingsAsync();
        }
        catch (Exception exception)
        {
            UiLocalization.SetText(ThemeColorStatusText, "保存失败：{0}", exception.Message);
        }
    }

    private async void AccentSourcePicker_OnSelectionChanged(object? sender, SelectionChangedEventArgs eventArgs)
    {
        if (_isLoading)
        {
            return;
        }

        _settings = _settings with { FollowSystemAccent = AccentSourcePicker.SelectedIndex == 0 };
        ApplyTheme(_settings.Theme);
        try
        {
            await SaveSettingsAsync();
            UiLocalization.SetText(
                ThemeColorStatusText,
                _settings.FollowSystemAccent
                    ? "强调色正在跟随系统；可随时编辑自定义颜色"
                    : "正在使用自定义强调色");
        }
        catch (Exception exception)
        {
            UiLocalization.SetText(ThemeColorStatusText, "保存失败：{0}", exception.Message);
        }
    }

    private void ApplyTheme(ThemePreference preference)
    {
        Application.Current!.RequestedThemeVariant = preference switch
        {
            ThemePreference.Light => ThemeVariant.Light,
            ThemePreference.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };

        var isDark = preference == ThemePreference.Dark ||
                     preference == ThemePreference.System && ActualThemeVariant == ThemeVariant.Dark;
        var colors = isDark ? _settings.DarkColors : _settings.LightColors;
        if (_settings.FollowSystemAccent && TryGetSystemAccent(out var systemAccent))
        {
            colors = colors with { Accent = systemAccent.ToString() };
        }
        var selection = Blend(colors.Surface, colors.Accent, isDark ? 0.42 : 0.30);
        var targets = new Dictionary<string, Color>(StringComparer.Ordinal)
        {
            ["PageBackgroundBrush"] = LimitAlpha(Color.Parse(colors.PageBackground), 0xF0),
            ["NavigationBrush"] = LimitAlpha(Color.Parse(colors.PageBackground), 0xE8),
            ["SurfaceBrush"] = LimitAlpha(Color.Parse(colors.Surface), 0xF0),
            ["SurfaceRaisedBrush"] = LimitAlpha(Color.Parse(Blend(colors.Surface, colors.PrimaryText, 0.06)), 0xF5),
            ["AccentBrush"] = Color.Parse(colors.Accent),
            ["AccentSoftBrush"] = LimitAlpha(Color.Parse(Blend(colors.Surface, colors.Accent, 0.16)), 0xE8),
            ["SelectionBrush"] = Color.Parse(selection),
            ["SelectionContentBrush"] = Color.Parse(ChooseHighContrastContent(selection, colors.PrimaryText, colors.PageBackground)),
            ["SelectionBorderBrush"] = Color.Parse(colors.Accent),
            ["PrimaryTextBrush"] = Color.Parse(colors.PrimaryText),
            ["SecondaryTextBrush"] = Color.Parse(colors.SecondaryText),
            ["BorderBrush"] = Color.Parse(colors.Border),
            ["SuccessBrush"] = Color.Parse(colors.Success),
            ["WarningBrush"] = Color.Parse(colors.Warning),
            ["ErrorBrush"] = Color.Parse(colors.Error),
            ["AccentContentBrush"] = Color.Parse(ChooseAccentContent(colors.Accent, colors.PageBackground, colors.PrimaryText))
        };

        Interlocked.Exchange(ref _themeTransitionCancellation, null)?.Cancel();
        if (_isLoading || _settings.ReduceMotion)
        {
            SetThemeColors(targets);
            return;
        }

        var cancellation = new CancellationTokenSource();
        Interlocked.Exchange(ref _themeTransitionCancellation, cancellation)?.Cancel();
        _ = AnimateThemeColorsAsync(targets, cancellation);
    }

    private static bool TryGetSystemAccent(out Color accent)
    {
        accent = default;
        try
        {
            var values = Application.Current?.PlatformSettings?.GetColorValues();
            if (values is null)
            {
                return false;
            }

            accent = values.AccentColor1;
            return accent.A > 0;
        }
        catch
        {
            return false;
        }
    }

    private static Color LimitAlpha(Color color, byte maximumAlpha) =>
        Color.FromArgb(Math.Min(color.A, maximumAlpha), color.R, color.G, color.B);

    private static string ChooseAccentContent(string accent, string pageBackground, string primaryText) =>
        ChooseHighContrastContent(accent, primaryText, pageBackground);

    private static string ChooseHighContrastContent(string background, string primaryText, string pageBackground)
    {
        var backgroundColor = Color.Parse(background);
        var candidates = new[] { primaryText, pageBackground, "#FFFFFF", "#000000" };
        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(candidate => ContrastRatio(backgroundColor, Color.Parse(candidate)))
            .First();
    }

    private static double ContrastRatio(Color first, Color second)
    {
        static double RelativeLuminance(Color color)
        {
            static double Linearize(byte channel)
            {
                var value = channel / 255d;
                return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
            }

            return 0.2126 * Linearize(color.R) +
                   0.7152 * Linearize(color.G) +
                   0.0722 * Linearize(color.B);
        }

        var lighter = Math.Max(RelativeLuminance(first), RelativeLuminance(second));
        var darker = Math.Min(RelativeLuminance(first), RelativeLuminance(second));
        return (lighter + 0.05) / (darker + 0.05);
    }

    private async Task AnimateThemeColorsAsync(
        IReadOnlyDictionary<string, Color> targets,
        CancellationTokenSource cancellation)
    {
        var brushes = new Dictionary<string, SolidColorBrush>(StringComparer.Ordinal);
        var starts = new Dictionary<string, Color>(StringComparer.Ordinal);
        foreach (var (key, target) in targets)
        {
            var start = Application.Current!.Resources[key] is SolidColorBrush existing
                ? existing.Color
                : target;
            var brush = new SolidColorBrush(start);
            Application.Current.Resources[key] = brush;
            brushes[key] = brush;
            starts[key] = start;
        }

        var stopwatch = Stopwatch.StartNew();
        const double durationMilliseconds = 180;
        try
        {
            while (stopwatch.Elapsed.TotalMilliseconds < durationMilliseconds)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var progress = stopwatch.Elapsed.TotalMilliseconds / durationMilliseconds;
                var eased = progress * progress * (3 - 2 * progress);
                foreach (var (key, target) in targets)
                {
                    brushes[key].Color = Interpolate(starts[key], target, eased);
                }

                await Task.Delay(16, cancellation.Token);
            }

            foreach (var (key, target) in targets)
            {
                brushes[key].Color = target;
            }
        }
        catch (OperationCanceledException)
        {
            // A newer theme selection owns the same resources now.
        }
        finally
        {
            Interlocked.CompareExchange(ref _themeTransitionCancellation, null, cancellation);
            cancellation.Dispose();
        }
    }

    private static Color Interpolate(Color start, Color end, double progress)
    {
        static byte Mix(byte first, byte second, double amount) =>
            (byte)Math.Clamp(Math.Round(first + (second - first) * amount), 0, 255);

        return Color.FromArgb(
            Mix(start.A, end.A, progress),
            Mix(start.R, end.R, progress),
            Mix(start.G, end.G, progress),
            Mix(start.B, end.B, progress));
    }

    private static void SetThemeColors(IReadOnlyDictionary<string, Color> colors)
    {
        foreach (var (key, color) in colors)
        {
            Application.Current!.Resources[key] = new SolidColorBrush(color);
        }
    }

    private async void EditThemeColors_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        var original = _settings;
        var updated = await new SemanticColorEditorWindow(_settings)
            .ShowDialog<ClientSettings?>(this);
        if (updated is null)
        {
            _settings = original;
            ApplyTheme(_settings.Theme);
            UiLocalization.SetText(ThemeColorStatusText, "已取消，颜色未保存");
            return;
        }

        try
        {
            _settings = (updated with { FollowSystemAccent = false }).ValidateAndNormalize();
            var previousLoadingState = _isLoading;
            try
            {
                _isLoading = true;
                AccentSourcePicker.SelectedIndex = 1;
            }
            finally
            {
                _isLoading = previousLoadingState;
            }
            ApplyTheme(_settings.Theme);
            await SaveSettingsAsync();
            UiLocalization.SetText(ThemeColorStatusText, "自定义颜色已保存");
        }
        catch (Exception exception)
        {
            _settings = original;
            ApplyTheme(_settings.Theme);
            UiLocalization.SetText(ThemeColorStatusText, "保存失败：{0}", exception.Message);
        }
    }

    private async void ReduceMotionToggle_OnChanged(object? sender, RoutedEventArgs eventArgs)
    {
        if (_isLoading)
        {
            return;
        }

        _settings = _settings with { ReduceMotion = ReduceMotionToggle.IsChecked == true };
        ApplyMotionPreference();
        ApplyTheme(_settings.Theme);
        try
        {
            await SaveSettingsAsync();
            UiLocalization.SetText(
                ThemeColorStatusText,
                _settings.ReduceMotion ? "已减少界面动画" : "已恢复界面过渡动画");
        }
        catch (Exception exception)
        {
            UiLocalization.SetText(ThemeColorStatusText, "保存失败：{0}", exception.Message);
        }
    }

    private async void LocalHoverToggle_OnChanged(object? sender, RoutedEventArgs eventArgs)
    {
        if (LocalHoverToggle.IsChecked != true)
        {
            CloseLocalImagePreview();
        }

        if (_isLoading)
        {
            return;
        }

        try
        {
            await SaveSettingsAsync();
        }
        catch (Exception exception)
        {
            UiLocalization.SetText(LocalSummaryText, "预览设置保存失败：{0}", exception.Message);
        }
    }

    private async void CloudHoverToggle_OnChanged(object? sender, RoutedEventArgs eventArgs)
    {
        if (_isLoading)
        {
            return;
        }

        _settings = _settings with
        {
            CloudHoverPreviewEnabled = CloudHoverToggle.IsChecked == true
        };
        try
        {
            if (CloudHoverToggle.IsChecked != true && _detailPreviewMode == DetailPreviewMode.Hover)
            {
                await StopDetailPreviewAsync(savePosition: true);
            }

            await SaveSettingsAsync();
        }
        catch (Exception exception)
        {
            UiLocalization.SetText(CloudSummaryText, "预览设置保存失败：{0}", exception.Message);
        }
    }

    private async void SingleClickPreviewToggle_OnChanged(object? sender, RoutedEventArgs eventArgs)
    {
        if (_isLoading)
        {
            return;
        }

        _settings = _settings with
        {
            SingleClickPreviewEnabled = SingleClickPreviewToggle.IsChecked == true
        };
        try
        {
            await SaveSettingsAsync();
        }
        catch (Exception exception)
        {
            UiLocalization.SetText(LocalSummaryText, "预览设置保存失败：{0}", exception.Message);
        }
    }

    private async void VideoMutedToggle_OnChanged(object? sender, RoutedEventArgs eventArgs)
    {
        if (_isLoading)
        {
            return;
        }

        _settings = _settings with
        {
            VideoPreviewMuted = VideoMutedToggle.IsChecked == true
        };
        try
        {
            var current = _detailPlayback.CurrentItem;
            ShowPlaybackResult(
                await _detailPlayback.SetMutedAsync(
                    _settings.VideoPreviewMuted && current?.ShouldRenderVideo == true),
                DetailStatus);
            await SaveSettingsAsync();
        }
        catch (Exception exception)
        {
            UiLocalization.SetText(DetailStatus, "预览设置保存失败：{0}", exception.Message);
        }
    }

    private void ApplySettingsToControls()
    {
        ThemePicker.SelectedIndex = _settings.Theme switch
        {
            ThemePreference.Light => 1,
            ThemePreference.System => 2,
            _ => 0
        };
        AccentSourcePicker.SelectedIndex = _settings.FollowSystemAccent ? 0 : 1;
        LanguagePicker.SelectedIndex = _settings.Language == UiLanguage.English ? 1 : 0;
        UiLocalization.Apply(this, _settings.Language);
        LocalHoverToggle.IsChecked = _settings.LocalHoverPreviewEnabled;
        CloudHoverToggle.IsChecked = _settings.CloudHoverPreviewEnabled;
        SingleClickPreviewToggle.IsChecked = _settings.SingleClickPreviewEnabled;
        VideoMutedToggle.IsChecked = _settings.VideoPreviewMuted;
        ReduceMotionToggle.IsChecked = _settings.ReduceMotion;
        _suppressVolumeChange = true;
        SettingsVolumeSlider.Value = _settings.MasterVolume;
        SettingsVolumeText.Text = $"{_settings.MasterVolume:0}%";
        _suppressVolumeChange = false;
        DownloadToDefaultDirectoryToggle.IsChecked = _settings.DownloadToDefaultDirectory;
        ApplyCloseBehaviorSettingsToControls();
        if (_settings.PersistentDownloadDirectory is null)
        {
            UiLocalization.SetText(DownloadDirectoryText, "尚未设置");
        }
        else
        {
            DownloadDirectoryText.Text = _settings.PersistentDownloadDirectory;
        }
        ApplyLocalAssetDisplayMode();
        ApplyCloudAssetDisplayMode();
        UpdateLocalFolderSidebarLayout();
        UpdateServerAddressVisibilityUi();
        Dispatcher.UIThread.Post(ApplyMotionPreference, DispatcherPriority.Loaded);
    }

    private void ApplyMotionPreference()
    {
        foreach (var visual in this.GetVisualDescendants().Append(this))
        {
            if (_settings.ReduceMotion)
            {
                visual.Transitions = null;
            }
            else
            {
                visual.ClearValue(Animatable.TransitionsProperty);
            }
        }
    }

    private async void LanguagePicker_OnSelectionChanged(object? sender, SelectionChangedEventArgs eventArgs)
    {
        if (_isLoading)
        {
            return;
        }

        var language = LanguagePicker.SelectedIndex == 1
            ? UiLanguage.English
            : UiLanguage.ChineseSimplified;
        _settings = _settings with { Language = language };
        UiLocalization.Apply(this, language);
        UpdateTrayLocalization();
        try
        {
            await SaveSettingsAsync();
        }
        catch (Exception exception)
        {
            UiLocalization.SetText(LocalSummaryText, "语言设置保存失败：{0}", exception.Message);
        }
    }

    private async void ChooseDownloadDirectory_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        try
        {
            var folders = await RunNativePickerAsync(
                "settings-download-directory",
                () => StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = UiLocalization.Text("选择素材默认保存目录"),
                    AllowMultiple = false
                }),
                LocalSummaryText);
            if (folders is null || folders.Count == 0 ||
                string.IsNullOrWhiteSpace(folders[0].Path.LocalPath))
            {
                return;
            }

            _settings = _settings with { PersistentDownloadDirectory = folders[0].Path.LocalPath };
            DownloadDirectoryText.Text = _settings.PersistentDownloadDirectory;
            await SaveSettingsAsync();
        }
        catch (Exception exception)
        {
            UiLocalization.SetText(LocalSummaryText, "下载设置保存失败：{0}", exception.Message);
        }
    }

    private async void DownloadToDefaultDirectoryToggle_OnChanged(
        object? sender,
        RoutedEventArgs eventArgs)
    {
        if (_isLoading)
        {
            return;
        }

        _settings = _settings with
        {
            DownloadToDefaultDirectory = DownloadToDefaultDirectoryToggle.IsChecked == true
        };
        try
        {
            await SaveSettingsAsync();
        }
        catch (Exception exception)
        {
            UiLocalization.SetText(LocalSummaryText, "下载设置保存失败：{0}", exception.Message);
        }
    }

    private void ApplyCloseBehaviorSettingsToControls()
    {
        _isApplyingCloseBehaviorSettings = true;
        try
        {
            CloseActionPicker.SelectedIndex =
                _settings.CloseAction == ApplicationCloseAction.ExitApplication ? 1 : 0;
            AskOnCloseToggle.IsChecked = _settings.AskOnClose;
        }
        finally
        {
            _isApplyingCloseBehaviorSettings = false;
        }
    }

    private async void CloseBehaviorSettings_OnChanged(object? sender, RoutedEventArgs eventArgs)
    {
        if (_isLoading || _isApplyingCloseBehaviorSettings)
        {
            return;
        }

        var previousSettings = _settings;
        _settings = _settings with
        {
            CloseAction = CloseActionPicker.SelectedIndex == 1
                ? ApplicationCloseAction.ExitApplication
                : ApplicationCloseAction.MinimizeToTray,
            AskOnClose = AskOnCloseToggle.IsChecked == true
        };
        try
        {
            await _settingsStore.SaveAsync(_settings);
            UiLocalization.SetText(CloseBehaviorStatusText, "关闭与托盘设置已保存");
        }
        catch (Exception exception)
        {
            _settings = previousSettings;
            ApplyCloseBehaviorSettingsToControls();
            ClientDiagnostics.WriteException("save-close-behavior", exception, isFatal: false);
            UiLocalization.SetText(
                CloseBehaviorStatusText,
                "关闭与托盘设置保存失败：{0}",
                exception.Message);
        }
    }

    private async void RestoreDefaultSettings_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        var previousSettings = _settings;
        var previousLoadingState = _isLoading;
        InvalidatePendingPlaybackPreferenceWrites();
        try
        {
            _isLoading = true;
            _settings = _settings.RestorePreferenceDefaults();
            ApplySettingsToControls();
            ApplyTheme(_settings.Theme);
        }
        catch (Exception exception)
        {
            _settings = previousSettings;
            ApplySettingsToControls();
            ApplyTheme(_settings.Theme);
            UiLocalization.SetText(
                RestoreDefaultSettingsStatusText,
                "设置恢复失败：{0}",
                exception.Message);
            return;
        }
        finally
        {
            _isLoading = previousLoadingState;
        }

        try
        {
            await SaveSettingsAsync();
        }
        catch (Exception exception)
        {
            previousLoadingState = _isLoading;
            try
            {
                _isLoading = true;
                _settings = previousSettings;
                ApplySettingsToControls();
                ApplyTheme(_settings.Theme);
            }
            finally
            {
                _isLoading = previousLoadingState;
            }

            ClientDiagnostics.WriteException("restore-default-settings", exception, isFatal: false);
            UiLocalization.SetText(
                RestoreDefaultSettingsStatusText,
                "设置恢复失败：{0}",
                exception.Message);
            return;
        }

        try
        {
            CloseLocalImagePreview();
            if (_detailPreviewMode == DetailPreviewMode.Hover)
            {
                await StopDetailPreviewAsync(savePosition: true);
            }

            _suppressVolumeChange = true;
            MasterVolumeSlider.Value = _settings.MasterVolume;
            DetailVolumeSlider.Value = _settings.MasterVolume;
            SettingsVolumeSlider.Value = _settings.MasterVolume;
            MasterVolumeText.Text = $"{_settings.MasterVolume:0}%";
            SettingsVolumeText.Text = $"{_settings.MasterVolume:0}%";
            _suppressVolumeChange = false;
            await _playerPlayback.SetVolumeAsync(_settings.MasterVolume);
            await _playerPlayback.SetSpeedAsync(_settings.PlaybackSpeed);
            var detailItem = _detailPlayback.CurrentItem;
            await _detailPlayback.SetMutedAsync(
                _settings.VideoPreviewMuted && detailItem?.ShouldRenderVideo == true);
            _playerPlayback.RepeatMode = PlaybackRepeatMode.Off;
            _playerPlayback.OrderMode = PlaybackOrderMode.Sequential;
            UpdatePlaybackModeButtons();
            previousLoadingState = _isLoading;
            try
            {
                _isLoading = true;
                SelectPlaybackSpeed(_settings.PlaybackSpeed);
            }
            finally
            {
                _isLoading = previousLoadingState;
            }
            UiLocalization.SetText(
                RestoreDefaultSettingsStatusText,
                "设置已恢复为默认值；服务器地址、素材默认保存目录和已保存账号已保留。");
        }
        catch (Exception exception)
        {
            ClientDiagnostics.WriteException("restore-default-settings", exception, isFatal: false);
            UiLocalization.SetText(
                RestoreDefaultSettingsStatusText,
                "设置已恢复，但当前播放状态更新失败：{0}",
                exception.Message);
        }
    }

    private Task SaveSettingsAsync()
    {
        if (_isLoading)
        {
            return Task.CompletedTask;
        }

        _settings = _settings with
        {
            Language = LanguagePicker.SelectedIndex == 1
                ? UiLanguage.English
                : UiLanguage.ChineseSimplified,
            LocalHoverPreviewEnabled = LocalHoverToggle.IsChecked == true,
            CloudHoverPreviewEnabled = CloudHoverToggle.IsChecked == true,
            SingleClickPreviewEnabled = SingleClickPreviewToggle.IsChecked == true,
            VideoPreviewMuted = VideoMutedToggle.IsChecked == true,
            DownloadToDefaultDirectory = DownloadToDefaultDirectoryToggle.IsChecked == true,
            CloseAction = CloseActionPicker.SelectedIndex == 1
                ? ApplicationCloseAction.ExitApplication
                : ApplicationCloseAction.MinimizeToTray,
            AskOnClose = AskOnCloseToggle.IsChecked == true
        };
        return _settingsStore.SaveAsync(_settings);
    }

    private async void CheckUpdates_OnClick(object? sender, RoutedEventArgs eventArgs) =>
        await CheckForUpdatesAsync(showUpToDateMessage: true);

    private async void ViewReleaseNotes_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        try
        {
            var notice = await ClientUpdateCoordinator.GetCurrentReleaseNotesAsync(GetClientVersion());
            if (notice is null)
            {
                await new MessageDialogWindow(
                    "更新日志",
                    "当前安装没有可显示的更新日志。").ShowDialog(this);
                return;
            }

            await ShowReleaseNotesAsync(notice, markAsShown: false);
        }
        catch (Exception exception)
        {
            ClientDiagnostics.WriteException("view-release-notes", exception, isFatal: false);
            UiLocalization.SetText(ClientUpdateStatusText, "读取更新日志失败：{0}", UserMessage(exception));
        }
    }

    private async Task HandleUpdateStartupAsync()
    {
        try
        {
            var currentVersion = GetClientVersion();
            if (Program.PostUpdateTransactionId is { } transactionId)
            {
                await ClientUpdateCoordinator.AcknowledgePostUpdateAsync(transactionId, currentVersion);
            }
            else
            {
                await ClientUpdateCoordinator.EnsureCurrentPackageStateAsync(currentVersion);
            }

            var notice = await ClientUpdateCoordinator.GetPendingReleaseNotesAsync(currentVersion);
            if (notice is not null)
            {
                await ShowReleaseNotesAsync(notice, markAsShown: true);
            }
        }
        catch (Exception exception)
        {
            ClientDiagnostics.WriteException("post-update-startup", exception, isFatal: false);
            UiLocalization.SetText(ClientUpdateStatusText, "更新启动确认失败：{0}", UserMessage(exception));
        }
    }

    private async Task ShowReleaseNotesAsync(ClientReleaseNotesNotice notice, bool markAsShown)
    {
        await new MessageDialogWindow(
            UiLocalization.Format("更新日志 · {0}", notice.Version),
            notice.ReleaseNotes)
            .ShowDialog(this);
        if (markAsShown)
        {
            await ClientUpdateCoordinator.MarkReleaseNotesShownAsync(notice.TransactionId);
        }
    }

    private async Task CheckForUpdatesAsync(bool showUpToDateMessage)
    {
        if (_updateService is null || _isCheckingForUpdates || _shutdownStarted)
        {
            return;
        }

        if (_currentUser is null)
        {
            UiLocalization.SetText(ClientUpdateStatusText, "请先登录后检查更新");
            return;
        }

        _isCheckingForUpdates = true;
        CheckUpdatesButton.IsEnabled = false;
        ClientUpdateProgressPanel.IsVisible = false;
        ClientUpdateProgressBar.Value = 0;
        ClientUpdateProgressText.Text = string.Empty;
        UiLocalization.SetText(ClientUpdateStatusText, "正在检查更新...");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var check = await _updateService.CheckAsync(timeout.Token);
            if (check.Release is null)
            {
                UiLocalization.SetText(ClientUpdateStatusText, "服务器尚未发布可下载版本");
                return;
            }

            if (!check.IsUpdateAvailable)
            {
                UiLocalization.SetText(ClientUpdateStatusText, "当前已是最新版本");
                if (showUpToDateMessage)
                {
                    await new MessageDialogWindow("检查更新", "当前已是最新版本。").ShowDialog(this);
                }
                return;
            }

            UiLocalization.SetText(ClientUpdateStatusText, "发现新版本 {0}", check.Release.Version);
            var useSilentInstaller = ClientUpdateInstallationPolicy.ShouldUseSilentInstaller(
                GetClientVersion(),
                check.Release.Version);
            var notes = string.IsNullOrWhiteSpace(check.Release.ReleaseNotes)
                ? string.Empty
                : $"\n\n{check.Release.ReleaseNotes}";
            var confirmed = await new MessageDialogWindow(
                "发现新版本",
                UiLocalization.Format(
                    useSilentInstaller
                        ? "可更新到 {0}。安装包将通过腾讯云 COS 直连下载并校验 SHA-256。更新会停止当前播放和导出，随后静默覆盖安装并自动重启，是否立即更新？{1}"
                        : "可更新到 {0}。安装包将通过腾讯云 COS 直连下载并校验 SHA-256，是否下载后打开安装程序？{1}",
                    check.Release.Version,
                    notes),
                useSilentInstaller ? "立即更新" : "下载并安装",
                "稍后")
                .ShowDialog<bool>(this);
            if (!confirmed)
            {
                return;
            }

            UiLocalization.SetText(ClientUpdateStatusText, "正在下载安装包...");
            ClientUpdateProgressPanel.IsVisible = true;
            var progress = new Progress<ClientUpdateDownloadProgress>(download =>
            {
                ClientUpdateProgressBar.Value = download.Percentage;
                ClientUpdateProgressText.Text =
                    $"{download.Percentage}% · {CloudAssetCardViewModel.FormatSize(download.BytesDownloaded)} / " +
                    CloudAssetCardViewModel.FormatSize(download.TotalBytes);
                UiLocalization.SetText(
                    ClientUpdateStatusText,
                    download.BytesDownloaded >= download.TotalBytes
                        ? "安装包下载完成，正在校验 SHA-256..."
                        : "正在下载安装包：{0:P0}",
                    download.Fraction);
            });
            var installerPath = await _updateService.DownloadVerifiedInstallerWithProgressAsync(
                check.Release,
                Path.Combine(AppPaths.UpdateDownloadDirectory, check.Release.Version),
                progress);
            ClientUpdateProgressBar.Value = 100;
            if (!useSilentInstaller)
            {
                UiLocalization.SetText(ClientUpdateStatusText, "安装包校验完成，正在打开安装程序");
                Process.Start(new ProcessStartInfo(installerPath) { UseShellExecute = true });
                return;
            }

            UiLocalization.SetText(ClientUpdateStatusText, "安装包校验完成，正在启动一键更新");
            var launch = await ClientUpdateCoordinator.PrepareAsync(
                check.Release,
                installerPath,
                GetClientVersion());
            using var updater = ClientUpdateCoordinator.Launch(launch);
            UiLocalization.SetText(ClientUpdateStatusText, "更新助手已启动，正在安全退出客户端");
            RequestApplicationExit();
        }
        catch (OperationCanceledException) when (!_shutdownStarted)
        {
            ClientUpdateProgressPanel.IsVisible = false;
            UiLocalization.SetText(ClientUpdateStatusText, "检查更新超时，请稍后重试");
        }
        catch (Exception exception)
        {
            ClientUpdateProgressPanel.IsVisible = false;
            ClientDiagnostics.WriteException("client-update", exception, isFatal: false);
            UiLocalization.SetText(ClientUpdateStatusText, "检查更新失败：{0}", UserMessage(exception));
        }
        finally
        {
            _isCheckingForUpdates = false;
            if (!_shutdownStarted)
            {
                CheckUpdatesButton.IsEnabled = true;
            }
        }
    }

    private void RefreshMainWindowLocalization()
    {
        SetSidebarExpanded(_isSidebarExpanded);
        UpdateLocalFolderSidebarLayout();
        UpdateServerAddressVisibilityUi();
        UpdateTrayLocalization();
    }

    private static string Blend(string background, string foreground, double foregroundWeight)
    {
        var back = Color.Parse(background);
        var front = Color.Parse(foreground);
        static byte Mix(byte left, byte right, double weight) =>
            (byte)Math.Clamp(Math.Round(left * (1 - weight) + right * weight), 0, 255);
        return Color.FromRgb(
            Mix(back.R, front.R, foregroundWeight),
            Mix(back.G, front.G, foregroundWeight),
            Mix(back.B, front.B, foregroundWeight)).ToString();
    }

    private void OpenAdmin_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (_serverOrigin is not { IsAbsoluteUri: true } origin ||
            (origin.Scheme != Uri.UriSchemeHttps &&
             (origin.Scheme != Uri.UriSchemeHttp || !ClientSettings.IsAllowedPlainHttpServerAddress(origin))) ||
            !string.IsNullOrEmpty(origin.UserInfo))
        {
            return;
        }

        var adminUri = new Uri(origin.GetLeftPart(UriPartial.Authority) + "/admin", UriKind.Absolute);
        Process.Start(new ProcessStartInfo(adminUri.AbsoluteUri) { UseShellExecute = true });
    }

    private void NavigateFromAdmin(string module)
    {
        if (module == "luts")
        {
            _cloudCategoryFilterIndex = 5;
            UpdateCloudCategoryQuickFilters();
            NavigateTo("shared");
            return;
        }

        NavigateTo(module);
    }
}
