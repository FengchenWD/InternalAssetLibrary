using InternalAssetLibrary.Client.Core.Settings;

internal static class SettingsDefaultsSelfTests
{
    public static void PreferenceDefaultsStaySafeAndUiRemainsWired()
    {
        var account = new SavedLoginAccount(
            Guid.NewGuid(),
            "member",
            "Team Member",
            PasswordRemembered: true,
            DateTimeOffset.UtcNow);
        var customized = new ClientSettings
        {
            Language = UiLanguage.English,
            Theme = ThemePreference.Light,
            FollowSystemAccent = false,
            ReduceMotion = true,
            LocalAssetDisplayMode = AssetDisplayMode.List,
            CloudAssetDisplayMode = AssetDisplayMode.List,
            LocalFolderSidebarExpanded = false,
            LocalHoverPreviewEnabled = true,
            CloudHoverPreviewEnabled = true,
            SingleClickPreviewEnabled = true,
            VideoPreviewMuted = true,
            MasterVolume = 35,
            PlaybackSpeed = 2,
            PlaybackLoopEnabled = true,
            PlaybackShuffleEnabled = true,
            ServerAddress = "https://assets.example.com/",
            PersistentDownloadDirectory = Path.Combine(Path.GetTempPath(), "asset-downloads"),
            DownloadToDefaultDirectory = false,
            SavedLoginAccounts = [account]
        }.ValidateAndNormalize();

        var restored = customized.RestorePreferenceDefaults();
        var defaults = new ClientSettings().ValidateAndNormalize();
        Equal(156d, defaults.CloudFolderHeight);
        Equal(156d, (new ClientSettings { CloudFolderHeight = 0 }).ValidateAndNormalize().CloudFolderHeight);
        Equal(156d, (new ClientSettings { CloudFolderHeight = double.NaN }).ValidateAndNormalize().CloudFolderHeight);
        Equal(220d, (new ClientSettings { CloudFolderHeight = 220 }).ValidateAndNormalize().CloudFolderHeight);

        False(defaults.LocalHoverPreviewEnabled);
        False(defaults.CloudHoverPreviewEnabled);
        False(defaults.SingleClickPreviewEnabled);
        Equal(ThemePreference.System, defaults.Theme);
        Equal(true, defaults.FollowSystemAccent);
        False(defaults.ReduceMotion);
        Equal(40d, defaults.MasterVolume);
        False(restored.LocalHoverPreviewEnabled);
        False(restored.CloudHoverPreviewEnabled);
        False(restored.SingleClickPreviewEnabled);
        False(restored.ReduceMotion);
        Equal(true, customized.SingleClickPreviewEnabled);
        Equal(defaults.Language, restored.Language);
        Equal(defaults.Theme, restored.Theme);
        Equal(defaults.FollowSystemAccent, restored.FollowSystemAccent);
        False(customized.FollowSystemAccent);
        Equal(defaults.LocalAssetDisplayMode, restored.LocalAssetDisplayMode);
        Equal(defaults.CloudAssetDisplayMode, restored.CloudAssetDisplayMode);
        Equal(defaults.LocalFolderSidebarExpanded, restored.LocalFolderSidebarExpanded);
        Equal(defaults.VideoPreviewMuted, restored.VideoPreviewMuted);
        Equal(defaults.MasterVolume, restored.MasterVolume);
        Equal(defaults.PlaybackSpeed, restored.PlaybackSpeed);
        Equal(defaults.PlaybackLoopEnabled, restored.PlaybackLoopEnabled);
        Equal(defaults.PlaybackShuffleEnabled, restored.PlaybackShuffleEnabled);
        Equal(defaults.DownloadToDefaultDirectory, restored.DownloadToDefaultDirectory);
        Equal(customized.ServerAddress, restored.ServerAddress);
        Equal(customized.PersistentDownloadDirectory, restored.PersistentDownloadDirectory);
        Equal(1, restored.SavedLoginAccounts.Count);
        Equal(account.UserId, restored.SavedLoginAccounts[0].UserId);

        var root = RepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(
            root,
            "src",
            "InternalAssetLibrary.Client",
            "MainWindow.axaml"));
        var source = File.ReadAllText(Path.Combine(
            root,
            "src",
            "InternalAssetLibrary.Client",
            "MainWindow.axaml.cs"));
        var appXaml = File.ReadAllText(Path.Combine(
            root,
            "src",
            "InternalAssetLibrary.Client",
            "App.axaml"));
        var playbackSource = File.ReadAllText(Path.Combine(
            root,
            "src",
            "InternalAssetLibrary.Client",
            "MainWindow.Playback.cs"));
        var restoreHandler = Section(
            source,
            "private async void RestoreDefaultSettings_OnClick",
            "private Task SaveSettingsAsync");

        Contains("x:Name=\"ProfileLoginButton\"", xaml);
        Contains("x:Name=\"SingleClickPreviewToggle\"", xaml);
        Contains("x:Name=\"ReduceMotionToggle\"", xaml);
        Contains("x:Name=\"AccentSourcePicker\"", xaml);
        Contains("SelectionChanged=\"AccentSourcePicker_OnSelectionChanged\"", xaml);
        Contains("x:Name=\"CloudAssetSectionHeader\"", xaml);
        Contains("x:Name=\"CloudFolderHeightSplitter\"", xaml);
        Contains("<RowDefinition Height=\"156\" MinHeight=\"156\" />", xaml);
        Contains("CloudDirectoryHeader.RowDefinitions[1].ActualHeight + 16 + 66 + 8", source);
        Contains("ResizeDirection=\"Rows\"", xaml);
        Contains("Cursor=\"SizeNorthSouth\"", xaml);
        Contains("Text=\"文件夹\" Classes=\"sectionTitle\"", xaml);
        Contains("Text=\"素材\" Classes=\"sectionTitle\"", xaml);
        Contains("IsCheckedChanged=\"ReduceMotionToggle_OnChanged\"", xaml);
        Contains("Click=\"ConnectServer_OnClick\">登录", xaml);
        Contains("Click=\"RestoreDefaultSettings_OnClick\">恢复为默认设置", xaml);
        Contains("_settings.RestorePreferenceDefaults()", source);
        Contains("SingleClickPreviewEnabled = SingleClickPreviewToggle.IsChecked == true", source);
        Contains("ReduceMotion = ReduceMotionToggle.IsChecked == true", source);
        Contains("PlatformSettings_OnColorValuesChanged", source);
        Contains("FollowSystemAccent = AccentSourcePicker.SelectedIndex == 0", source);
        Contains("CloudEmptyState.IsVisible = !showsLuts && _currentUser is not null && !hasResults;", source);
        Contains("x:Key=\"SelectionBrush\"", appXaml);
        Contains("x:Key=\"SelectionContentBrush\"", appXaml);
        Contains("x:Key=\"SelectionBorderBrush\"", appXaml);
        Contains("ProfileLoginButton.IsVisible", source);
        Contains("x:Name=\"ServerAddressBox\"", xaml);
        Contains("PasswordChar=\"*\"", xaml);
        Contains("Click=\"ServerAddressVisibility_OnClick\"", xaml);
        Contains("_isServerAddressVisible = !_isServerAddressVisible;", source);
        Contains("ServerAddressBox.PasswordChar = _isServerAddressVisible ? '\\0' : '*'", source);
        Equal(3, xaml.Split("Value=\"40\"", StringSplitOptions.None).Length - 1);
        Contains("MasterVolumeSlider.Value = _settings.MasterVolume;", playbackSource);
        Contains("DetailVolumeSlider.Value = _settings.MasterVolume;", playbackSource);
        Contains("SettingsVolumeSlider.Value = _settings.MasterVolume;", playbackSource);
        Contains("MasterVolumeSlider.Value = normalized;", playbackSource);
        Contains("DetailVolumeSlider.Value = normalized;", playbackSource);
        Contains("SettingsVolumeSlider.Value = normalized;", playbackSource);
        Contains("InvalidatePendingPlaybackPreferenceWrites();", restoreHandler);
        Contains("await _detailPlayback.SetMutedAsync(", restoreHandler);
        Before(
            "InvalidatePendingPlaybackPreferenceWrites();",
            "_settings = _settings.RestorePreferenceDefaults();",
            restoreHandler);
        Contains("private long _playbackPreferenceGeneration;", playbackSource);
        Contains("preferenceGeneration == Volatile.Read(ref _playbackPreferenceGeneration)", playbackSource);
        Contains("Interlocked.Exchange(ref _volumeChangeCancellation, null)", playbackSource);
        Contains("ref _playbackSettingsSaveCancellation", playbackSource);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "InternalAssetLibrary.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private static void Contains(string expectedSubstring, string actual)
    {
        if (!actual.Contains(expectedSubstring, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected source to contain '{expectedSubstring}'.");
        }
    }

    private static void Before(string first, string second, string actual)
    {
        var firstIndex = actual.IndexOf(first, StringComparison.Ordinal);
        var secondIndex = actual.IndexOf(second, StringComparison.Ordinal);
        if (firstIndex < 0 || secondIndex < 0 || firstIndex >= secondIndex)
        {
            throw new InvalidOperationException($"Expected '{first}' to appear before '{second}'.");
        }
    }

    private static string Section(string source, string start, string end)
    {
        var startIndex = source.IndexOf(start, StringComparison.Ordinal);
        if (startIndex < 0)
        {
            throw new InvalidOperationException($"Could not find source section start '{start}'.");
        }

        var endIndex = source.IndexOf(end, startIndex + start.Length, StringComparison.Ordinal);
        if (endIndex < 0)
        {
            throw new InvalidOperationException($"Could not find source section '{start}' to '{end}'.");
        }

        return source[startIndex..endIndex];
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
        }
    }

    private static void False(bool value)
    {
        if (value)
        {
            throw new InvalidOperationException("Expected false.");
        }
    }
}
