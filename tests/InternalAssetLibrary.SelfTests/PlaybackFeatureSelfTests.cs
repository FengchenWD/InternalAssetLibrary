using InternalAssetLibrary.Client.Core.LocalAssets;
using InternalAssetLibrary.Client.Core.Playback;
using InternalAssetLibrary.Client.Core.Settings;

internal static class PlaybackFeatureSelfTests
{
    public static void FolderScannerRecursesFiltersAndSorts()
    {
        var root = Directory.CreateTempSubdirectory("ial-playback-folder-").FullName;
        try
        {
            CreateFile(root, "00-root.MP4");
            CreateFile(root, "Alpha", "a-song.FLAC");
            CreateFile(root, "Alpha", "nested", "b-frame.png");
            CreateFile(root, "beta", "c-clip.mov");

            CreateFile(root, "Alpha", "edit.psd");
            CreateFile(root, "design.ai");
            CreateFile(root, "vector.eps");
            CreateFile(root, "look.cube");
            CreateFile(root, "notes.txt");

            var relativePaths = PlaybackFolderScanner.Discover(root)
                .Select(path => Path.GetRelativePath(root, path))
                .ToArray();

            SequenceEqual(
                [
                    "00-root.MP4",
                    Path.Combine("Alpha", "a-song.FLAC"),
                    Path.Combine("Alpha", "nested", "b-frame.png"),
                    Path.Combine("beta", "c-clip.mov")
                ],
                relativePaths);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public static void QueueReplacementAndRemovalDiscardLutState() =>
        QueueReplacementAndRemovalDiscardLutStateAsync().GetAwaiter().GetResult();

    public static void PlaybackShortcutDefaultsStayValidAndUnique()
    {
        var shortcuts = new PlaybackShortcutSettings().ValidateAndNormalize();
        var gestures = new[]
        {
            shortcuts.PlayPause,
            shortcuts.SeekBackwardFiveSeconds,
            shortcuts.SeekForwardFiveSeconds,
            shortcuts.SeekBackwardOneSecond,
            shortcuts.SeekForwardOneSecond,
            shortcuts.SetInPoint,
            shortcuts.SetOutPoint,
            shortcuts.AddMarker,
            shortcuts.OpenFiles,
            shortcuts.OpenFolder,
            shortcuts.Export,
            shortcuts.VolumeUp,
            shortcuts.VolumeDown,
            shortcuts.ToggleFullscreen,
            shortcuts.ExitFullscreen
        };

        Equal("Space", shortcuts.PlayPause);
        Equal("Ctrl+Shift+O", shortcuts.OpenFolder);
        Equal("Escape", shortcuts.ExitFullscreen);
        Equal(gestures.Length, gestures.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        try
        {
            _ = (shortcuts with { Export = shortcuts.PlayPause }).ValidateAndNormalize();
            throw new InvalidOperationException("Expected duplicate playback shortcuts to be rejected.");
        }
        catch (InvalidDataException)
        {
        }
    }

    public static void PlayerFeatureSourceWiringRemainsComplete()
    {
        var root = RepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(
            root,
            "src",
            "InternalAssetLibrary.Client",
            "MainWindow.axaml"));
        var playbackSource = File.ReadAllText(Path.Combine(
            root,
            "src",
            "InternalAssetLibrary.Client",
            "MainWindow.Playback.cs"));
        var mainWindowSource = File.ReadAllText(Path.Combine(
            root,
            "src",
            "InternalAssetLibrary.Client",
            "MainWindow.axaml.cs"));
        var shortcutWindow = File.ReadAllText(Path.Combine(
            root,
            "src",
            "InternalAssetLibrary.Client",
            "PlaybackShortcutSettingsWindow.axaml"));
        var queueWindow = File.ReadAllText(Path.Combine(
            root,
            "src",
            "InternalAssetLibrary.Client",
            "PlayerQueueAddWindow.axaml"));
        var queueWindowSource = File.ReadAllText(Path.Combine(
            root,
            "src",
            "InternalAssetLibrary.Client",
            "PlayerQueueAddWindow.axaml.cs"));
        var appXaml = File.ReadAllText(Path.Combine(
            root,
            "src",
            "InternalAssetLibrary.Client",
            "App.axaml"));

        Contains("Click=\"OpenPlayerFolder_OnClick\">打开文件夹", xaml);
        Contains("PlaybackFolderScanner.Discover", playbackSource);
        Contains("await OpenExternalFilesAsync(paths)", playbackSource);

        Contains("x:Name=\"PlayerMarkerPane\"", xaml);
        Contains("ItemsSource=\"{Binding PlayerMarkers}\"", xaml);
        Contains("DoubleTapped=\"PlayerMarkerList_OnDoubleTapped\"", xaml);
        Contains("_playerPlayback.SeekAsync(marker.Time)", playbackSource);
        Contains("await RefreshPlayerMarkersAsync()", playbackSource);

        Contains("Click=\"ClearPlayerLut_OnClick\">清除 LUT", xaml);
        Contains("ApplyLutSelectionAsync(isPlayer: true, selectedLut: null)", playbackSource);

        Contains("Click=\"PlayerSetInPoint_OnClick\">设为入点", xaml);
        Contains("Click=\"PlayerSetOutPoint_OnClick\">设为出点", xaml);
        Contains("Click=\"PlayerClearRange_OnClick\">清除范围", xaml);
        Contains("x:Name=\"PlayerExportWithLutToggle\" Content=\"导出时套用 LUT\"", xaml);
        Contains("Click=\"ExportPlayerMedia_OnClick\">导出", xaml);
        Contains("PlayerExportWithLutToggle.IsChecked == true", playbackSource);
        Contains("await ExportPlayerOriginalCopyAsync(inputPath)", playbackSource);
        Contains("FfmpegCommandBuilder.BuildAudioTrimExport", playbackSource);
        Contains("FfmpegCommandBuilder.BuildVideoExport", playbackSource);

        Contains("Checked=\"PlayerAudioTrack_OnChanged\" Unchecked=\"PlayerAudioTrack_OnChanged\"", xaml);
        Contains("Checked=\"DetailAudioTrack_OnChanged\" Unchecked=\"DetailAudioTrack_OnChanged\"", xaml);
        Contains("SynchronizeAudioTrackCheckBox(sender)", playbackSource);
        Contains("Interlocked.Exchange(ref _playerAudioTrackCancellation, cancellation)", playbackSource);
        Contains(".Where(item => item.IsSelected)", playbackSource);
        Contains("selectedIds,\n                expectedItemKey,\n                cancellation.Token", playbackSource.Replace("\r\n", "\n"));
        Contains("_temporaryPlayerMarkerSets", playbackSource);
        Contains("GetOrCreateTemporaryMarkerSet(current.Key)", playbackSource);
        Contains("ClearTemporaryMarkerState(item.Key)", playbackSource);
        Contains("IsTemporaryPlaybackOrigin(origin)", playbackSource);
        Contains("item.MediaType is not (LocalMediaType.Audio or LocalMediaType.Video)", playbackSource);

        Contains("Click=\"PlaybackShortcuts_OnClick\">快捷键", xaml);
        Contains("PlaybackShortcutCatalog.TryMatch", mainWindowSource);
        Contains("IsTextEntryTarget(eventArgs.Source)", mainWindowSource);
        Contains("ExecutePlaybackShortcutAsync", playbackSource);
        Contains("KeyDown=\"Window_OnKeyDown\"", shortcutWindow);
        Contains("Click=\"RestoreDefaults_OnClick\">恢复默认", shortcutWindow);
        Contains("x:Name=\"PlayerTimelineMarkersCanvas\"", xaml);
        Contains("MarkPlayerTimelineMarkersDirty()", playbackSource);
        Contains("PlayerTimelineMarkersCanvas.Children.Add(tick)", playbackSource);
        Contains("Selector=\"Border.timelineInPoint\"", appXaml);
        Contains("Selector=\"Border.timelineOutPoint\"", appXaml);
        Contains("x:Key=\"TimelineInPointBrush\" Color=\"#F472B6\"", appXaml);
        Contains("Background\" Value=\"{DynamicResource TimelineInPointBrush}\"", appXaml);
        Contains("AddPlayerTimelineRangeTick(range.InPoint, duration, availableWidth, \"timelineInPoint\")", playbackSource);
        Contains("AddPlayerTimelineRangeTick(range.OutPoint, duration, availableWidth, \"timelineOutPoint\")", playbackSource);
        var trimUi = Section(
            playbackSource,
            "private void UpdatePlayerTrimUi()",
            "private static string FormatTrimTime");
        Contains("MarkPlayerTimelineMarkersDirty();", trimUi);
        Contains("Click=\"AddPlayerQueueItems_OnClick\">加入队列", xaml);
        Contains("ToggleButton Classes=\"queueAssetChoice\"", queueWindow);
        Contains("IsChecked=\"{Binding IsSelected, Mode=TwoWay}\"", queueWindow);
        Contains("_allAssets\n            .Where(item => item.IsSelected)", queueWindowSource.Replace("\r\n", "\n"));
        Contains("AppendPlayerQueueItems", playbackSource);
        Contains("DefaultPlayerFileAssociations.SupportsPath(asset.FullPath)", playbackSource);
        Contains("!DefaultPlayerFileAssociations.SupportsPath(filePath)", playbackSource);
        Contains("PausePlayerWhenLeavingPage();", mainWindowSource);
        Contains("await _playerPlayback.PauseAsync()", playbackSource);

        var queueSync = Section(
            playbackSource,
            "private void SyncPlayerQueue()",
            "private async void PlayerSidePane_OnClick");
        Before("PlayerQueueList.ItemsSource = null;", "PlayerQueueItems.Clear();", queueSync);
        Before("PlayerQueueItems.Clear();", "PlayerQueueList.ItemsSource = PlayerQueueItems;", queueSync);

        var navigation = Section(
            mainWindowSource,
            "private void NavigateTo(string page)",
            "private void CancelPageRefresh");
        Contains("ObserveNavigationTask(\n                StopDetailPreviewAsync", navigation.Replace("\r\n", "\n"));
        Contains("ObserveNavigationTask(RefreshCloudLibraryAsync(), \"refresh-cloud-library\")", navigation);
        Contains("ObserveNavigationTask(RefreshUsersAsync(), \"refresh-users\")", navigation);
        Contains("ObserveNavigationTask(RefreshMyRecycleBinAsync(), \"refresh-recycle-bin\")", navigation);
        Contains("await task;", navigation);
        Contains("ClientDiagnostics.WriteException($\"navigation-{operation}\"", navigation);
    }

    private static async Task QueueReplacementAndRemovalDiscardLutStateAsync()
    {
        var engine = new FakePlaybackEngine();
        await using var controller = new PlaybackController(engine);
        var first = new PlaybackItem("video-a", "C:/media/a.mov", "A", LocalMediaType.Video);
        var second = new PlaybackItem("video-b", "C:/media/b.mov", "B", LocalMediaType.Video);
        var lut = Path.Combine(Path.GetTempPath(), "queue-session-look.cube");
        controller.ReplaceQueue([first, second]);

        True((await controller.PlayIndexAsync(0)).Succeeded);
        True((await controller.SetLutAsync(lut)).Succeeded);
        True((await controller.RemoveFromQueueAsync(first.Key)).Succeeded);
        Equal<string?>(null, controller.GetLutPath(first.Key));
        Equal<string?>(null, engine.LutPath);
        Equal(second.Key, engine.State.CurrentItemKey);

        True((await controller.SetLutAsync(lut)).Succeeded);
        Equal(Path.GetFullPath(lut), controller.GetLutPath(second.Key));

        var replacement = second with
        {
            Source = "C:/media/replacement.mov",
            Title = "Replacement"
        };
        controller.ReplaceQueue([replacement], replacement.Key);

        Equal<string?>(null, controller.GetLutPath(replacement.Key));
        True((await controller.PlayCurrentAsync()).Succeeded);
        Equal<string?>(null, engine.LutPath);
        Equal(replacement.Source, engine.Loaded?.Source);
    }

    private static void CreateFile(string root, params string[] segments)
    {
        var path = segments.Aggregate(root, Path.Combine);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
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

    private sealed class FakePlaybackEngine : IPlaybackEngine
    {
        private PlaybackEngineState _state = PlaybackEngineState.Idle;

        public PlaybackEngineAvailability Availability { get; } =
            PlaybackEngineAvailability.Available("fake");

        public PlaybackEngineState State => _state;

        public PlaybackItem? Loaded { get; private set; }

        public string? LutPath { get; private set; }

        public event EventHandler<PlaybackEngineStateChangedEventArgs>? StateChanged;

        public Task SetVideoHostAsync(nint nativeWindowHandle, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task LoadAsync(
            PlaybackItem item,
            TimeSpan startPosition,
            CancellationToken cancellationToken = default)
        {
            Loaded = item;
            SetState(new PlaybackEngineState(
                PlaybackStatus.Playing,
                item.Key,
                item.Title,
                item.ShouldRenderVideo,
                false,
                startPosition,
                TimeSpan.FromMinutes(2),
                _state.Volume,
                _state.Speed,
                null));
            return Task.CompletedTask;
        }

        public Task PlayAsync(CancellationToken cancellationToken = default)
        {
            SetState(_state with { Status = PlaybackStatus.Playing, IsPaused = false });
            return Task.CompletedTask;
        }

        public Task PauseAsync(CancellationToken cancellationToken = default)
        {
            SetState(_state with { Status = PlaybackStatus.Paused, IsPaused = true });
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            Loaded = null;
            SetState(PlaybackEngineState.Idle);
            return Task.CompletedTask;
        }

        public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
        {
            SetState(_state with { Position = position });
            return Task.CompletedTask;
        }

        public Task SetVolumeAsync(double volume, CancellationToken cancellationToken = default)
        {
            SetState(_state with { Volume = volume });
            return Task.CompletedTask;
        }

        public Task SetMutedAsync(bool muted, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SetSpeedAsync(double speed, CancellationToken cancellationToken = default)
        {
            SetState(_state with { Speed = speed });
            return Task.CompletedTask;
        }

        public Task StepFrameAsync(bool forward, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task TakeScreenshotAsync(string outputPath, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SetLutAsync(string? cubePath, CancellationToken cancellationToken = default)
        {
            LutPath = cubePath is null ? null : Path.GetFullPath(cubePath);
            return Task.CompletedTask;
        }

        public Task AddSubtitleAsync(string subtitlePath, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<PlaybackAudioTrack>> GetAudioTracksAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PlaybackAudioTrack>>([]);

        public Task SetSelectedAudioTracksAsync(
            IReadOnlyCollection<int> trackIds,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private void SetState(PlaybackEngineState state)
        {
            _state = state;
            StateChanged?.Invoke(this, new PlaybackEngineStateChangedEventArgs(state));
        }
    }

    private static void True(bool condition)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Expected true, but was false.");
        }
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', but was '{actual}'.");
        }
    }

    private static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException(
                $"Expected [{string.Join(", ", expected)}], but was [{string.Join(", ", actual)}].");
        }
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
        var endIndex = startIndex < 0
            ? -1
            : source.IndexOf(end, startIndex + start.Length, StringComparison.Ordinal);
        if (startIndex < 0 || endIndex < 0)
        {
            throw new InvalidOperationException($"Could not find source section '{start}' to '{end}'.");
        }

        return source[startIndex..endIndex];
    }
}
