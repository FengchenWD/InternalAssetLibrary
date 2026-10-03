using InternalAssetLibrary.Client.Core.Http;
using InternalAssetLibrary.Client.Core.LocalAssets;
using InternalAssetLibrary.Client.Core.Lut;
using InternalAssetLibrary.Client.Core.MediaAnalysis;
using InternalAssetLibrary.Client.Core.Playback;

internal static class MediaCoreSelfTests
{
    public static void MissingLibMpvReturnsUnavailableState()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"missing-libmpv-{Guid.NewGuid():N}.dll");
        var availability = LibMpvRuntime.Probe(missingPath);
        False(availability.IsAvailable);
        Equal(PlaybackEngineUnavailableReason.NativeLibraryNotFound, availability.Reason);
        Contains(missingPath, availability.Diagnostic!);

        var graph = LibMpvPlaybackEngine.BuildAudioMixGraph([3, 1, 2]);
        Equal("[aid1] [aid2] [aid3] amix=inputs=3:normalize=1:dropout_transition=0 [ao]", graph);
        Throws<ArgumentException>(() => LibMpvPlaybackEngine.BuildAudioMixGraph([1]));
    }

    public static void LibMpvLoadFileCommandsKeepResumeOptionsInTheOptionsSlot()
    {
        SequenceEqual(
            ["loadfile", "D:/media/test.mp4", "replace"],
            LibMpvPlaybackEngine.BuildLoadFileCommand("D:/media/test.mp4", TimeSpan.Zero));
        SequenceEqual(
            ["loadfile", "D:/media/test.mp4", "replace", "-1", "start=62.345"],
            LibMpvPlaybackEngine.BuildLoadFileCommand(
                "D:/media/test.mp4",
                TimeSpan.FromMilliseconds(62_345)));
        Throws<ArgumentOutOfRangeException>(() =>
            LibMpvPlaybackEngine.BuildLoadFileCommand("D:/media/test.mp4", TimeSpan.FromSeconds(-1)));
    }

    public static void VideoHostsOnlyShowAfterAFrameCanRender()
    {
        foreach (var status in new[]
                 {
                     PlaybackStatus.Idle,
                     PlaybackStatus.Loading,
                     PlaybackStatus.Ended,
                     PlaybackStatus.Failed,
                     PlaybackStatus.Unavailable
                 })
        {
            False(State(status, hasVideo: true).HasRenderableVideoFrame);
        }

        True(State(PlaybackStatus.Playing, hasVideo: true).HasRenderableVideoFrame);
        True(State(PlaybackStatus.Paused, hasVideo: true).HasRenderableVideoFrame);
        False(State(PlaybackStatus.Playing, hasVideo: false).HasRenderableVideoFrame);

        static PlaybackEngineState State(PlaybackStatus status, bool hasVideo) =>
            new(status, "test", "Test", hasVideo, status == PlaybackStatus.Paused,
                TimeSpan.Zero, TimeSpan.FromSeconds(1), 100, 1, null);
    }

    public static void LibMpvLutFiltersValidateCanonicalReadback()
    {
        const string token = "lut3d=file='D\\:/media/look.cube'";
        True(LibMpvPlaybackEngine.IsLutFilterReadbackMatch(
            token,
            "lavfi=graph=%128%lut3d=file='D\\:/media/look.cube'"));
        False(LibMpvPlaybackEngine.IsLutFilterReadbackMatch(
            token,
            "lavfi=graph=%128%lut3d=file='D\\:/media/other.cube'"));
        True(LibMpvPlaybackEngine.IsLutFilterReadbackMatch(null, string.Empty));
        False(LibMpvPlaybackEngine.IsLutFilterReadbackMatch(null, "lavfi=graph=%128%null"));
    }

    public static void PlaybackControllersShareVolumeAndModelQueue() =>
        PlaybackControllersShareVolumeAndModelQueueAsync().GetAwaiter().GetResult();

    public static void PlaybackLutsAreRememberedPerItem() =>
        PlaybackLutsAreRememberedPerItemAsync().GetAwaiter().GetResult();

    public static void PlaybackReliabilityGuardsRemainWired()
    {
        var root = RepositoryRoot();
        var engine = File.ReadAllText(Path.Combine(
            root,
            "src",
            "InternalAssetLibrary.Client.Core",
            "Playback",
            "LibMpvPlaybackEngine.cs"));
        var window = File.ReadAllText(Path.Combine(
            root,
            "src",
            "InternalAssetLibrary.Client",
            "MainWindow.Playback.cs"));
        var windowXaml = File.ReadAllText(Path.Combine(
            root,
            "src",
            "InternalAssetLibrary.Client",
            "MainWindow.axaml"));
        var localCard = File.ReadAllText(Path.Combine(
                root,
                "src",
                "InternalAssetLibrary.Client",
                "ViewModels",
                "AssetCardViewModel.cs"))
            .Replace("\r\n", "\n");
        var cloudCards = File.ReadAllText(Path.Combine(
                root,
                "src",
                "InternalAssetLibrary.Client",
                "ViewModels",
                "CloudViewModels.cs"))
            .Replace("\r\n", "\n");

        var constructorStart = engine.IndexOf("internal LibMpvPlaybackEngine(", StringComparison.Ordinal);
        var hostStart = engine.IndexOf("public Task SetVideoHostAsync", constructorStart, StringComparison.Ordinal);
        var constructor = engine[constructorStart..hostStart];
        False(constructor.Contains("_native.Initialize", StringComparison.Ordinal));
        False(constructor.Contains("PumpEventsAsync", StringComparison.Ordinal));
        False(constructor.Contains("audio-display", StringComparison.Ordinal));
        Contains("SetOption(\"hwdec\", options.EnableHardwareDecoding ? \"auto-copy-safe\" : \"no\")", constructor);
        False(constructor.Contains("? \"auto-safe\" : \"no\"", StringComparison.Ordinal));
        var hostMethod = engine[hostStart..engine.IndexOf("public Task LoadAsync", hostStart, StringComparison.Ordinal)];
        True(hostMethod.IndexOf("SetOption(\"wid\"", StringComparison.Ordinal) <
             hostMethod.IndexOf("_native.Initialize", StringComparison.Ordinal));
        Contains("Bind the native video host before loading or controlling media.", engine);
        Contains("SetPropertyCore(\"volume\"", hostMethod);
        Contains("SetPropertyCore(\"speed\"", hostMethod);
        Contains("SetOption(\"image-display-duration\", \"inf\")", constructor);
        Contains("_state = QueryState(_state.Status, _state.Error)", engine);
        Contains(": _state.Duration", engine);
        Contains("_currentLutVideoFilter", engine);
        Contains("RestoreLutFilterIfNeeded();", engine);
        Contains("ApplyCurrentLutFilterCore();", engine);
        Contains("[\"seek\", \"0\", \"relative+exact\"]", engine);
        Contains("_ = _native.Command(_player, [\"seek\", \"0\", \"relative+exact\"]);", engine);
        var loadMethod = engine[engine.IndexOf("public Task LoadAsync", hostStart, StringComparison.Ordinal)..
            engine.IndexOf("public Task PlayAsync", hostStart, StringComparison.Ordinal)];
        Contains("SetPropertyCore(\"pause\", \"no\")", loadMethod);
        var audioSelectionMethod = engine[engine.IndexOf(
                "public Task SetSelectedAudioTracksAsync",
                hostStart,
                StringComparison.Ordinal)..
            engine.IndexOf("public static string BuildAudioMixGraph", hostStart, StringComparison.Ordinal)];
        Contains("lock (_sync)", audioSelectionMethod);
        Contains("SetPropertyCore(\"aid\", \"no\")", audioSelectionMethod);
        Contains("SetPropertyCore(\"lavfi-complex\", string.Empty)", audioSelectionMethod);
        False(audioSelectionMethod.Contains("SetProperty(\"", StringComparison.Ordinal));

        Contains("_detailPreviewGate.WaitAsync", window);
        Contains("IsCurrentDetailPreviewOperation", window);
        Contains("if (DetailVideoHost.IsHostReady)", window);
        Contains("DetailVideoHost.HostHandle", window);
        Contains("_detailPreviewGate.WaitAsync(operation.CancellationToken)", window);
        Contains("RefreshPlayerVisualsForKeyAsync", window);
        Contains("_playerLutOptionKeys.GetValueOrDefault(item.Key)", window);
        Contains("_detailLutOptionKeys.GetValueOrDefault(item.Key)", window);
        Contains("var selections = isPlayer ? _playerLutOptionKeys : _detailLutOptionKeys", window);
        Contains("selections[current.Key] = selectedLut.Key", window);
        Contains("selections.Remove(current.Key)", window);
        Contains("await ApplyLutSelectionAsync(isPlayer: true, selectedLut: null)", window);
        Contains("IsCurrentLutSelection", window);
        Contains("controller.State", window);
        Contains("CurrentItemKey", window);
        Contains("VideoMutedToggle_OnChanged", File.ReadAllText(Path.Combine(
            root,
            "src",
            "InternalAssetLibrary.Client",
            "MainWindow.axaml.cs")));
        Contains("PlayerEndedBackdrop", windowXaml);
        Contains("DetailEndedBackdrop", windowXaml);
        Contains("state.Status != PlaybackStatus.Ended", window);
        Contains("private Task DisposePlaybackAsync()", window);
        Contains("return Task.Run(async () =>", window);
        False(window.Contains("shutdown.Wait(TimeSpan.FromSeconds(3))", StringComparison.Ordinal));
        Contains("_playerVisualGate.WaitAsync", window);
        Contains("_suppressPlayerQueueSelection", window);
        Contains("PlayerQueueList.SelectedIndex = -1", window);
        Contains("DeferredUiResourceDisposer.Dispose(previousArtworkBitmap)", window);
        Contains("DeferredUiResourceDisposer.Dispose(previousTimelineBitmap)", window);
        Contains("_thumbnail = null;\n        OnPropertyChanged(nameof(Thumbnail));", localCard);
        Contains("OnPropertyChanged(nameof(ShowPlaceholder));\n        DeferredUiResourceDisposer.Dispose(thumbnail);", localCard);
        Contains("_thumbnail = null;\n        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));", cloudCards);
        Contains("PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowPlaceholder)));\n        DeferredUiResourceDisposer.Dispose(thumbnail);", cloudCards);
        Contains("_avatar = null;\n        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Avatar)));", cloudCards);
        Contains("PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowPlaceholder)));\n        DeferredUiResourceDisposer.Dispose(avatar);", cloudCards);
        var clearPlayerVisuals = window[window.IndexOf("private void ClearPlayerVisuals()", StringComparison.Ordinal)..
            window.IndexOf("private async void PlayerScreenshot_OnClick", StringComparison.Ordinal)];
        True(clearPlayerVisuals.IndexOf("PlayerArtworkImage.Source = null", StringComparison.Ordinal) <
             clearPlayerVisuals.IndexOf("DeferredUiResourceDisposer.Dispose(_playerArtworkBitmap)", StringComparison.Ordinal));
        True(clearPlayerVisuals.IndexOf("PlayerTimelineImage.Source = null", StringComparison.Ordinal) <
             clearPlayerVisuals.IndexOf("DeferredUiResourceDisposer.Dispose(_playerTimelineBitmap)", StringComparison.Ordinal));
        Contains("QueuePlaybackSettingsSave();", window);
        Contains("TimeSpan.FromMilliseconds(350)", window);
        Contains("cancellationToken: operation.CancellationToken", window);
        Contains("x:Name=\"PlayerLutPicker\" Width=\"180\"", windowXaml);
        False(windowXaml.Contains(
            "ColumnDefinitions=\"Auto,Auto,Auto,180,Auto,180,Auto,Auto,*\"",
            StringComparison.Ordinal));
        Contains("Interlocked.Increment(ref _playerVisualGeneration)", window);
        Contains("Interlocked.Exchange(ref _playerVisualCancellation, cancellation)", window);
        Contains("StringComparer.Ordinal.Equals(_playerPlayback.CurrentItem?.Key, item.Key)", window);
        Contains("ThrowIfPlayerVisualOperationIsStale", window);
        Contains("new FileInfo(path).Length == 0", window);
        Contains("File.Move(temporaryPath, path, overwrite: true)", window);
        Contains("catch when (attempt == 0)", window);
        Contains("catch (Exception exception)\n        {\n            UiLocalization.SetText(status, \"导出失败：{0}\"", window.Replace("\r\n", "\n"));
    }

    public static void DeferredUiResourceDisposalRemainsBatchedAndIsolated()
    {
        var root = RepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "src",
            "InternalAssetLibrary.Client",
            "Services",
            "DeferredUiResourceDisposer.cs"));
        var normalized = source.Replace("\r\n", "\n");

        Contains("TimeSpan.FromMilliseconds(250)", source);
        Contains("new(ReferenceEqualityComparer.Instance)", source);
        Contains("if (!Pending.Add(resource) || _flushScheduled)", source);
        Contains("batch = [.. Pending]", source);
        Equal(1, source.Split("DispatcherTimer.RunOnce", StringSplitOptions.None).Length - 1);
        Contains("foreach (var resource in batch)\n        {\n            try", normalized);
        Contains("catch (Exception exception)", source);
        Contains("Pending.Count == 0", source);
    }

    public static void FfprobeJsonDetectsMultipleAudioTracks()
    {
        const string json = """
            {
              "streams": [
                {
                  "index": 0,
                  "codec_name": "h264",
                  "codec_long_name": "H.264",
                  "profile": "High",
                  "codec_type": "video",
                  "width": 1920,
                  "height": 1080,
                  "pix_fmt": "yuv420p",
                  "avg_frame_rate": "30000/1001",
                  "color_space": "bt709",
                  "color_transfer": "bt709",
                  "color_primaries": "bt709",
                  "disposition": { "default": 1, "attached_pic": 0 }
                },
                {
                  "index": 1,
                  "codec_name": "aac",
                  "codec_type": "audio",
                  "sample_rate": "48000",
                  "channels": 2,
                  "channel_layout": "stereo",
                  "tags": { "language": "zho", "title": "现场声" },
                  "disposition": { "default": 1 }
                },
                {
                  "index": 2,
                  "codec_name": "pcm_s24le",
                  "codec_type": "audio",
                  "sample_rate": "48000",
                  "channels": 1,
                  "channel_layout": "mono",
                  "tags": { "language": "eng", "title": "麦克风" },
                  "disposition": { "default": 0 }
                }
              ],
              "format": {
                "format_name": "matroska,webm",
                "duration": "61.250000",
                "size": "987654",
                "bit_rate": "129000",
                "tags": { "title": "双音轨样本" }
              }
            }
            """;

        var source = Path.Combine(Path.GetTempPath(), "multi track sample.mkv");
        var information = FfprobeJsonParser.Parse(json, source);
        True(information.HasVideo);
        Equal(TimeSpan.FromSeconds(61.25), information.Duration);
        Equal(2, information.AudioTracks.Count);
        Equal(1, information.AudioTracks[0].StreamIndex);
        Equal("现场声", information.AudioTracks[0].Title);
        Equal(2, information.AudioTracks[0].ChannelCount);
        Equal("麦克风", information.AudioTracks[1].Title);
        True(information.AudioTracks[0].IsDefault);
        False(information.AudioTracks[1].IsDefault);
        var video = information.Streams.Single(stream => stream.Kind == MediaStreamKind.Video);
        True(video.FrameRate is > 29.9 and < 30.1);
        Equal("bt709", video.ColorSpace);

        var command = FfprobeMediaAnalyzer.BuildProbeCommand("ffprobe-custom", source);
        Equal("ffprobe-custom", command.Executable);
        Equal(Path.GetFullPath(source), command.Arguments[^1]);
        True(command.Arguments.Contains("-show_entries", StringComparer.Ordinal));
        True(command.Arguments.Contains("stream:format=format_name,format_long_name,duration,size,bit_rate,tags", StringComparer.Ordinal));
        False(MediaToolLocator.Locate(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.exe")).IsAvailable);
    }

    public static void FfmpegCommandsKeepPathsAtomicAndProtectOriginals()
    {
        var root = Path.Combine(Path.GetTempPath(), "media core command paths");
        var input = Path.Combine(root, "input, with spaces.mov");
        var lut = Path.Combine(root, "D-Log look's 01.cube");
        var thumbnail = Path.Combine(root, "thumb.webp");
        var output = Path.Combine(root, "graded output.mov");

        var thumbnailCommand = FfmpegCommandBuilder.BuildThumbnail(input, thumbnail);
        True(thumbnailCommand.Arguments.Contains(Path.GetFullPath(input), StringComparer.Ordinal));
        True(thumbnailCommand.Arguments.Contains(Path.GetFullPath(thumbnail), StringComparer.Ordinal));
        True(thumbnailCommand.Arguments.Contains("-y", StringComparer.Ordinal));

        var waveform = FfmpegCommandBuilder.BuildWaveform(input, Path.Combine(root, "wave.png"));
        var waveformFilter = waveform.Arguments.Single(argument =>
            argument.Contains("showwavespic", StringComparison.Ordinal));
        Contains("volume=-12dB", waveformFilter);
        Contains("scale=sqrt", waveformFilter);
        Contains("filter=average", waveformFilter);

        var filmstrip = FfmpegCommandBuilder.BuildFilmstrip(
            input,
            Path.Combine(root, "filmstrip.webp"),
            TimeSpan.FromSeconds(100));
        Contains("tile=10x1", filmstrip.Arguments.Single(argument => argument.Contains("tile=", StringComparison.Ordinal)));
        Contains("scale=320:180", filmstrip.Arguments.Single(argument => argument.Contains("scale=", StringComparison.Ordinal)));
        Contains("tpad=stop_mode=clone", filmstrip.Arguments.Single(argument => argument.Contains("tpad=", StringComparison.Ordinal)));

        var export = FfmpegCommandBuilder.BuildLutExport(input, lut, output);
        True(export.Arguments.Contains("-n", StringComparer.Ordinal));
        Equal(Path.GetFullPath(output), export.Arguments[^1]);
        var filterIndex = export.Arguments.ToList().IndexOf("-vf");
        var filter = export.Arguments[filterIndex + 1];
        Contains("lut3d=file=", filter);
        if (Path.GetFullPath(lut).Contains(':'))
        {
            Contains("\\:", filter);
        }

        Contains("\\'", filter);

        var trimmedVideo = FfmpegCommandBuilder.BuildVideoExport(
            input,
            Path.Combine(root, "trimmed.mp4"),
            TimeSpan.FromSeconds(1.25),
            TimeSpan.FromSeconds(4.5),
            lut,
            LutExportPreset.HighQualityH264);
        Equal("1.25", ArgumentAfter(trimmedVideo, "-ss"));
        Equal("3.25", ArgumentAfter(trimmedVideo, "-t"));
        True(trimmedVideo.Arguments.Contains("libx264", StringComparer.Ordinal));
        True(trimmedVideo.Arguments.Contains("-vf", StringComparer.Ordinal));

        var ungradedVideo = FfmpegCommandBuilder.BuildVideoExport(
            input,
            Path.Combine(root, "ungraded.mov"),
            outPoint: TimeSpan.FromSeconds(2));
        False(ungradedVideo.Arguments.Contains("-vf", StringComparer.Ordinal));
        Equal("2", ArgumentAfter(ungradedVideo, "-t"));

        var flac = FfmpegCommandBuilder.BuildAudioTrimExport(
            input,
            Path.Combine(root, "trimmed.flac"),
            TimeSpan.Zero,
            TimeSpan.FromSeconds(12.345678),
            AudioExportPreset.Flac);
        Equal("0", ArgumentAfter(flac, "-ss"));
        Equal("12.345678", ArgumentAfter(flac, "-t"));
        Equal("flac", ArgumentAfter(flac, "-c:a"));
        True(flac.Arguments.Contains("-vn", StringComparer.Ordinal));

        var wav = FfmpegCommandBuilder.BuildAudioTrimExport(
            input,
            Path.Combine(root, "trimmed.wav"),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(3),
            AudioExportPreset.Wav);
        Equal("pcm_s24le", ArgumentAfter(wav, "-c:a"));
        Throws<ArgumentException>(() => FfmpegCommandBuilder.BuildVideoExport(
            input,
            output,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5)));
        Throws<ArgumentException>(() => FfmpegCommandBuilder.BuildAudioTrimExport(
            input,
            output,
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(2)));
        Throws<ArgumentException>(() => FfmpegCommandBuilder.BuildThumbnail(input, input));

        var videoProxy = CloudMediaDerivativeService.BuildProxyCommand(
            input,
            Path.Combine(root, "video proxy.mp4"),
            ApiAssetCategory.Video,
            "ffmpeg-custom");
        Equal("ffmpeg-custom", videoProxy.Executable);
        True(videoProxy.Arguments.Contains("0:a?", StringComparer.Ordinal));
        True(videoProxy.Arguments.Contains("-fpsmax", StringComparer.Ordinal));
        Contains("min(540,ih)", videoProxy.Arguments.Single(argument => argument.StartsWith("scale=", StringComparison.Ordinal)));
        True(videoProxy.Arguments.Contains("192k", StringComparer.Ordinal));

        var audioProxy = CloudMediaDerivativeService.BuildProxyCommand(
            input,
            Path.Combine(root, "audio proxy.m4a"),
            ApiAssetCategory.Bgm);
        True(audioProxy.Arguments.Contains("-vn", StringComparer.Ordinal));
        False(audioProxy.Arguments.Contains("-c:v", StringComparer.Ordinal));
        Throws<ArgumentOutOfRangeException>(() => CloudMediaDerivativeService.BuildProxyCommand(
            input,
            Path.Combine(root, "image proxy.mp4"),
            ApiAssetCategory.Image));

        static string ArgumentAfter(MediaToolCommand command, string option)
        {
            var index = command.Arguments.ToList().IndexOf(option);
            True(index >= 0 && index + 1 < command.Arguments.Count);
            return command.Arguments[index + 1];
        }
    }

    public static void CubeValidatorAcceptsLogValuesAndRejectsBadShape()
    {
        var validator = new CubeLutValidator();
        const string valid = """
            # DJI-style conversion values may leave the display range.
            TITLE "D-Log # Rec.709"
            LUT_3D_SIZE 2
            DOMAIN_MIN -0.1 -0.1 -0.1
            DOMAIN_MAX 1.2 1.2 1.2
            -0.2 0.0 0.0
            1.1 0.0 0.0
            0.0 1.1 0.0
            1.1 1.1 0.0
            0.0 0.0 1.1
            1.1 0.0 1.1
            0.0 1.1 1.1
            1.2 1.2 1.2
            """;
        var result = validator.ValidateText(valid);
        True(result.IsValid);
        Equal(CubeLutKind.ThreeDimensional, result.Descriptor!.Kind);
        Equal("D-Log # Rec.709", result.Descriptor.Title);
        Equal(8, result.Descriptor.DataRowCount);
        Equal(-0.1, result.Descriptor.DomainMinimum.X);
        Equal(1.2, result.Descriptor.DomainMaximum.X);

        var invalidRows = validator.ValidateText(valid[..valid.LastIndexOf('\n')]);
        False(invalidRows.IsValid);
        Contains("declares 8 data rows", invalidRows.Diagnostic);
        var invalidFinite = validator.ValidateText("LUT_1D_SIZE 2\n0 0 0\nNaN 1 1\n");
        False(invalidFinite.IsValid);
    }

    public static void LutLibraryPersistsPathsWithoutDeletingSources() =>
        LutLibraryPersistsPathsWithoutDeletingSourcesAsync().GetAwaiter().GetResult();

    private static async Task PlaybackControllersShareVolumeAndModelQueueAsync()
    {
        var sharedVolume = new PlaybackVolumeCoordinator(80);
        var firstEngine = new FakePlaybackEngine();
        var secondEngine = new FakePlaybackEngine();
        await using var first = new PlaybackController(firstEngine, sharedVolume, new Random(1));
        await using var second = new PlaybackController(secondEngine, sharedVolume, new Random(2));
        var audio = new PlaybackItem(
            "audio-1",
            "https://media.invalid/one.flac",
            "One",
            LocalMediaType.Audio,
            [new PlaybackHttpHeader("Authorization", "Bearer test-token")]);
        var video = new PlaybackItem("video-1", "C:/media/two.mkv", "Two", LocalMediaType.Video);
        first.ReplaceQueue([audio, video]);

        True((await first.PlayIndexAsync(0)).Succeeded);
        Equal("audio-1", firstEngine.Loaded!.Key);
        True((await first.NextAsync()).Succeeded);
        Equal("video-1", firstEngine.Loaded!.Key);
        Equal("video-1", first.Recent[0].Item.Key);
        Equal("audio-1", first.Recent[1].Item.Key);
        Equal(0, first.Recent[1].Item.HttpHeaders!.Count);
        False((await first.NextAsync()).Succeeded);
        first.RepeatMode = PlaybackRepeatMode.All;
        True((await first.NextAsync()).Succeeded);
        Equal("audio-1", firstEngine.Loaded!.Key);
        first.OrderMode = PlaybackOrderMode.Shuffle;
        True((await first.NextAsync()).Succeeded);
        Equal("video-1", firstEngine.Loaded!.Key);

        True((await first.SetVolumeAsync(37)).Succeeded);
        Equal(37d, firstEngine.Volume);
        Equal(37d, secondEngine.Volume);
        Equal(37d, second.Volume);
        True((await second.SetVideoHostAsync((nint)1234)).Succeeded);
        Equal((nint)1234, secondEngine.VideoHost);
        True((await first.SetSelectedAudioTracksAsync([1, 2])).Succeeded);
        SequenceEqual([1, 2], firstEngine.SelectedAudioTracks);
        False((await first.SetSelectedAudioTracksAsync([1], audio.Key)).Succeeded);
        SequenceEqual([1, 2], firstEngine.SelectedAudioTracks);
        True((await first.SetSpeedAsync(1.5)).Succeeded);
        True((await first.SeekAsync(TimeSpan.FromMinutes(5))).Succeeded);
        Equal(1.5, first.State.Speed);
        Equal(TimeSpan.FromMinutes(2), first.State.Position);
        True((await first.PauseAsync()).Succeeded);
        True(first.State.IsPaused);
        True((await first.PlayPauseAsync()).Succeeded);
        False(first.State.IsPaused);
        True((await first.SetMutedAsync(true)).Succeeded);
        True(first.IsMuted);
        True(firstEngine.Muted);

        var output = Path.Combine(Path.GetTempPath(), "shot.png");
        True((await first.StepFrameAsync(true)).Succeeded);
        True((await first.TakeScreenshotAsync(output)).Succeeded);
        True((await first.SetLutAsync(null)).Succeeded);
        False((await first.SetLutAsync("C:/media/stale.cube", audio.Key)).Succeeded);
        var subtitle = Path.Combine(Path.GetTempPath(), "captions.srt");
        True((await first.AddSubtitleAsync(subtitle)).Succeeded);
        True(firstEngine.SteppedForward);
        Equal(Path.GetFullPath(output), firstEngine.ScreenshotPath);
        Equal<string?>(null, firstEngine.LutPath);
        Equal(Path.GetFullPath(subtitle), firstEngine.SubtitlePath);

        var animatedImage = new PlaybackItem(
            "animated-image",
            "C:/media/animated.gif",
            "Animated",
            LocalMediaType.Image,
            ForceVideoOutput: true);
        True(animatedImage.ShouldRenderVideo);
        False(new PlaybackItem(
            "static-image",
            "C:/media/static.png",
            "Static",
            LocalMediaType.Image).ShouldRenderVideo);

        var removalEngine = new FakePlaybackEngine();
        await using var removal = new PlaybackController(removalEngine);
        removal.ReplaceQueue([audio, video]);
        True((await removal.PlayIndexAsync(0)).Succeeded);
        True((await removal.RemoveFromQueueAsync(audio.Key)).Succeeded);
        Equal(video.Key, removal.CurrentItem!.Key);
        Equal(video.Key, removalEngine.Loaded!.Key);
        Equal(video.Key, removal.State.CurrentItemKey);
        True((await removal.RemoveFromQueueAsync(video.Key)).Succeeded);
        Equal<PlaybackItem?>(null, removal.CurrentItem);
        Equal(PlaybackStatus.Idle, removal.State.Status);
        Equal<PlaybackItem?>(null, removalEngine.Loaded);

        var eofEngine = new FakePlaybackEngine();
        await using var eof = new PlaybackController(eofEngine);
        eof.ReplaceQueue([audio, video]);
        True((await eof.PlayIndexAsync(0)).Succeeded);
        eofEngine.SimulateEnded();
        True(SpinWait.SpinUntil(() => eofEngine.Loaded?.Key == video.Key, TimeSpan.FromSeconds(1)));
        Equal(video.Key, eof.CurrentItem!.Key);
        Equal(PlaybackStatus.Playing, eof.State.Status);
        eofEngine.SimulateEnded();
        Equal(video.Key, eof.CurrentItem!.Key);
        Equal(PlaybackStatus.Ended, eof.State.Status);
        Equal(eof.State.Duration, eof.State.Position);
        True((await eof.PlayPauseAsync()).Succeeded);
        Equal(PlaybackStatus.Playing, eof.State.Status);
        Equal(TimeSpan.Zero, eof.State.Position);
        eofEngine.SimulatePausedAtEnd();
        True((await eof.PlayPauseAsync()).Succeeded);
        Equal(PlaybackStatus.Playing, eof.State.Status);
        Equal(TimeSpan.Zero, eof.State.Position);
    }

    private static async Task PlaybackLutsAreRememberedPerItemAsync()
    {
        var engine = new FakePlaybackEngine();
        await using var controller = new PlaybackController(engine);
        var first = new PlaybackItem("video-a", "C:/media/a.mov", "A", LocalMediaType.Video);
        var second = new PlaybackItem("video-b", "C:/media/b.mov", "B", LocalMediaType.Video);
        var lut = Path.Combine(Path.GetTempPath(), "camera-look.cube");
        controller.ReplaceQueue([first, second]);

        True((await controller.PlayIndexAsync(0)).Succeeded);
        True((await controller.SetLutAsync(lut)).Succeeded);
        Equal(Path.GetFullPath(lut), controller.CurrentLutPath);
        Equal(Path.GetFullPath(lut), controller.GetLutPath(first.Key));

        engine.OperationLog.Clear();
        True((await controller.PlayIndexAsync(1)).Succeeded);
        SequenceEqual(["lut:<none>", "load:video-b"], engine.OperationLog.Take(2));
        Equal<string?>(null, controller.CurrentLutPath);

        engine.OperationLog.Clear();
        True((await controller.PlayIndexAsync(0)).Succeeded);
        SequenceEqual([$"lut:{Path.GetFullPath(lut)}", "load:video-a"], engine.OperationLog.Take(2));
        Equal(Path.GetFullPath(lut), controller.CurrentLutPath);

        True((await controller.SetLutAsync(null)).Succeeded);
        Equal<string?>(null, controller.GetLutPath(first.Key));
    }

    private static async Task LutLibraryPersistsPathsWithoutDeletingSourcesAsync()
    {
        var root = Directory.CreateTempSubdirectory("ial-lut-library-").FullName;
        try
        {
            var cubePath = Path.Combine(root, "camera conversion.cube");
            var libraryPath = Path.Combine(root, "lut-library.json");
            await File.WriteAllTextAsync(cubePath, """
                TITLE "Camera Rec709"
                LUT_1D_SIZE 2
                0 0 0
                1 1 1
                """);

            Guid id;
            using (var library = new JsonLocalLutLibrary(libraryPath))
            {
                var imported = await library.ImportAsync(cubePath);
                id = imported.Id;
                True(imported.IsAvailable);
                Equal("Camera Rec709", imported.DisplayName);
                True(imported.Descriptor!.Sha256?.Length == 64);
                var repeated = await library.ImportAsync(cubePath, "常用转换");
                Equal(id, repeated.Id);
                Equal("常用转换", repeated.DisplayName);
            }

            using (var reloaded = new JsonLocalLutLibrary(libraryPath))
            {
                var entry = (await reloaded.LoadAsync()).Single();
                Equal(id, entry.Id);
                True(entry.IsAvailable);
                True(await reloaded.RemoveAsync(id));
                True(File.Exists(cubePath));
                var importedAgain = await reloaded.ImportAsync(cubePath);
                File.Delete(cubePath);
                var missing = await reloaded.RefreshAsync(importedAgain.Id);
                False(missing.IsAvailable);
                Contains("unavailable", missing.Diagnostic!);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "InternalAssetLibrary.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private sealed class FakePlaybackEngine : IPlaybackEngine
    {
        private PlaybackEngineState _state = PlaybackEngineState.Idle;

        public PlaybackEngineAvailability Availability { get; } = PlaybackEngineAvailability.Available("fake");
        public PlaybackEngineState State => _state;
        public PlaybackItem? Loaded { get; private set; }
        public double Volume { get; private set; } = 100;
        public bool Muted { get; private set; }
        public nint VideoHost { get; private set; }
        public int[] SelectedAudioTracks { get; private set; } = [];
        public bool SteppedForward { get; private set; }
        public string? ScreenshotPath { get; private set; }
        public string? LutPath { get; private set; }
        public string? SubtitlePath { get; private set; }
        public List<string> OperationLog { get; } = [];
        public event EventHandler<PlaybackEngineStateChangedEventArgs>? StateChanged;

        public Task SetVideoHostAsync(nint nativeWindowHandle, CancellationToken cancellationToken = default)
        {
            VideoHost = nativeWindowHandle;
            return Task.CompletedTask;
        }

        public Task LoadAsync(PlaybackItem item, TimeSpan startPosition, CancellationToken cancellationToken = default)
        {
            OperationLog.Add($"load:{item.Key}");
            Loaded = item;
            SetState(new PlaybackEngineState(
                PlaybackStatus.Playing,
                item.Key,
                item.Title,
                item.ShouldRenderVideo,
                false,
                startPosition,
                TimeSpan.FromMinutes(2),
                Volume,
                1,
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
            Volume = volume;
            SetState(_state with { Volume = volume });
            return Task.CompletedTask;
        }

        public Task SetMutedAsync(bool muted, CancellationToken cancellationToken = default)
        {
            Muted = muted;
            return Task.CompletedTask;
        }

        public Task SetSpeedAsync(double speed, CancellationToken cancellationToken = default)
        {
            SetState(_state with { Speed = speed });
            return Task.CompletedTask;
        }

        public Task StepFrameAsync(bool forward, CancellationToken cancellationToken = default)
        {
            SteppedForward = forward;
            return Task.CompletedTask;
        }

        public Task TakeScreenshotAsync(string outputPath, CancellationToken cancellationToken = default)
        {
            ScreenshotPath = Path.GetFullPath(outputPath);
            return Task.CompletedTask;
        }

        public Task SetLutAsync(string? cubePath, CancellationToken cancellationToken = default)
        {
            LutPath = cubePath is null ? null : Path.GetFullPath(cubePath);
            OperationLog.Add($"lut:{LutPath ?? "<none>"}");
            return Task.CompletedTask;
        }

        public Task AddSubtitleAsync(string subtitlePath, CancellationToken cancellationToken = default)
        {
            SubtitlePath = Path.GetFullPath(subtitlePath);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<PlaybackAudioTrack>> GetAudioTracksAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PlaybackAudioTrack>>([
                new(1, 1, "现场声", "zho", "aac", 2, true, true),
                new(2, 2, "麦克风", "eng", "aac", 1, false, false)
            ]);

        public Task SetSelectedAudioTracksAsync(
            IReadOnlyCollection<int> trackIds,
            CancellationToken cancellationToken = default)
        {
            SelectedAudioTracks = trackIds.ToArray();
            return Task.CompletedTask;
        }

        public void SimulateEnded()
        {
            SetState(_state with
            {
                Status = PlaybackStatus.Ended,
                IsPaused = true,
                Position = _state.Duration ?? _state.Position
            });
        }

        public void SimulatePausedAtEnd()
        {
            SetState(_state with
            {
                Status = PlaybackStatus.Paused,
                IsPaused = true,
                Position = _state.Duration ?? _state.Position
            });
        }

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

    private static void False(bool condition) => True(!condition);

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
            throw new InvalidOperationException("Sequences are not equal.");
        }
    }

    private static void Contains(string expectedSubstring, string actual)
    {
        if (!actual.Contains(expectedSubstring, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected text to contain '{expectedSubstring}'.");
        }
    }

    private static void Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name} to be thrown.");
    }
}
