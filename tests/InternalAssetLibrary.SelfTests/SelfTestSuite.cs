using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using InternalAssetLibrary.Contracts;
using InternalAssetLibrary.Client.Core.Http;
using InternalAssetLibrary.Client.Core.LocalAssets;
using InternalAssetLibrary.Client.Core.Playback;
using InternalAssetLibrary.Client.Core.Settings;
using InternalAssetLibrary.Core;
using InternalAssetLibrary.Server.Security;
using InternalAssetLibrary.Server.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

internal static class SelfTestSuite
{
    public static int Run()
    {
        var tests = new (string Name, Action Run)[]
        {
            ("collapsed local folder viewport does not cover compact buttons", Release120WiringSelfTests.CollapsedFolderListHasNoOverlay),
            ("empty CSV sample is byte-for-byte stable", EmptyCsvSampleIsStable),
            ("over-one-hour CSV sample is byte-for-byte stable", LongCsvSampleIsStable),
            ("CSV special fields use RFC 4180 escaping", CsvSpecialFieldsRoundTrip),
            ("CSV rejects incompatible formats and invalid marker rules", CsvRejectsInvalidInput),
            ("marker sets enforce owner and administrator rules", MarkerSetPermissionsWork),
            ("marker sets import and export the fixed CSV", MarkerSetCsvImportExportWorks),
            ("password hashing verifies without exposing plaintext", PasswordHashingWorks),
            ("passwords require two of four character classes", PasswordLoginPolicySelfTests.PasswordRequiresTwoOfFourCharacterClasses),
            ("first login recommends a password change once without blocking access", PasswordLoginPolicySelfTests.FirstLoginRecommendationIsConsumedOnceWithoutBlockingAccess),
            ("desktop and web login recommend rather than force password changes", PasswordLoginPolicySelfTests.ClientAndWebLoginOnlyRecommendPasswordChange),
            ("input rules enforce confirmed limits", InputRulesWork),
            ("pagination and permission contracts behave consistently", ContractsBehaveConsistently),
            ("real sample folders are indexed recursively", () => LocalSampleFoldersAreIndexed().GetAwaiter().GetResult()),
            ("local directory snapshots include empty and non-media folders", () => DirectorySnapshotsIncludeAllFolders().GetAwaiter().GetResult()),
            ("offline local roots retain their directory snapshots", () => OfflineDirectorySnapshotsAreRetained().GetAwaiter().GetResult()),
            ("schema 1 local catalogs migrate without losing asset metadata", () => SchemaOneCatalogMigratesLosslessly().GetAwaiter().GetResult()),
            ("local tag libraries persist unused tags and validate assignments", TagLibrarySelfTests.LocalLibraryPersistsUnusedTagsAndValidatesAssignments),
            ("schema 2 local catalogs recover a persistent tag library", TagLibrarySelfTests.SchemaTwoCatalogsRecoverTheirTagLibrary),
            ("cloud tag libraries reuse names and create missing tags", TagLibrarySelfTests.CloudLibraryReusesNamesAndCreatesMissingTags),
            ("cloud downloads default to the configured directory", DownloadSettingDefaultsToConfiguredDirectory),
            ("COS signed URLs use the configured private bucket", CosObjectStoreSelfTest),
            ("COS uploads keep seekable sources streaming and satisfy SDK validation", () => CosObjectStoreUploadSelfTests.SeekableSourcesRemainStreamingAndPassSdkValidation().GetAwaiter().GetResult()),
            ("COS uploads spool non-seekable sources with bounded cleanup", () => CosObjectStoreUploadSelfTests.NonSeekableSourcesUseBoundedTemporaryFilesAndCleanUp().GetAwaiter().GetResult()),
            ("preference defaults disable hover previews and preserve connection data", SettingsDefaultsSelfTests.PreferenceDefaultsStaySafeAndUiRemainsWired),
            ("close defaults, remembered choices, and legacy settings stay safe", ClientCloseBehaviorSelfTests.DefaultsPromptForMinimizeAndSelectionsAreDeterministic),
            ("close preferences round-trip while legacy JSON uses safe defaults", () => ClientCloseBehaviorSelfTests.ClosePreferencesRoundTripAndLegacyJsonUsesSafeDefaults().GetAwaiter().GetResult()),
            ("tray prompt settings and safe shutdown stay wired", ClientCloseBehaviorSelfTests.TrayPromptSettingsAndSafeShutdownRemainWired),
            ("server addresses allow secure tunnels and reject public plaintext HTTP", ServerAddressRulesSupportSecureTunnels),
            ("asset list queries default to name A-Z", AssetQueriesDefaultToNameAscending),
            ("adding a parent root merges child roots without changing asset identity", () => ParentRootMergePreservesAssetIdentity().GetAwaiter().GetResult()),
            ("adding a child root below an existing parent is rejected", () => ChildRootBelowParentIsRejected().GetAwaiter().GetResult()),
            ("local directory filters enforce path segment boundaries", DirectoryFilterUsesStrictPathBoundaries),
            ("storage probes mark disconnected roots offline and detect reconnects", () => StorageProbeTracksDisconnectAndReconnect().GetAwaiter().GetResult()),
            ("offline storage reconnect preserves local identity", () => OfflineStorageReconnectPreservesIdentity().GetAwaiter().GetResult()),
            ("missing roots on connected storage discard stale assets", () => MissingConnectedRootDiscardsStaleAssets().GetAwaiter().GetResult()),
            ("deleted local files are removed from connected roots", () => DeletedLocalFileIsRemovedFromConnectedRoot().GetAwaiter().GetResult()),
            ("moved local files replace the prior path identity", () => MovedLocalFileReplacesPriorPathIdentity().GetAwaiter().GetResult()),
            ("indexed file and directory renames preserve identity, tags, and markers", LocalAssetFileSystemMutationSelfTests.RenamesPreserveIdentityTagsAndMarkers),
            ("local asset moves preserve identity, tags, and batch atomicity", LocalAssetFileSystemMutationSelfTests.MovesPreserveIdentityTagsAndBatchAtomicity),
            ("failed local asset move saves roll back every file", LocalAssetFileSystemMutationSelfTests.FailedMoveCatalogSavesRollBackEveryFile),
            ("failed local catalog saves roll back physical filesystem changes", LocalAssetFileSystemMutationSelfTests.FailedCatalogSavesRollBackPhysicalMoves),
            ("local delete operations stay behind the recycle-bin boundary", LocalAssetFileSystemMutationSelfTests.RecycleBinOperationsStayAtomic),
            ("local filesystem mutations reject unsafe names and collisions", LocalAssetFileSystemMutationSelfTests.NamesAndCollisionsAreValidated),
            ("local batch tags and two-phase renames stay atomic", LocalAssetFileSystemMutationSelfTests.BatchTagsAndRenamesStayAtomic),
            ("failed local batch renames roll back every file", LocalAssetFileSystemMutationSelfTests.FailedBatchRenameRollsBackEveryFile),
            ("recycled assets reconcile catalog save failures", LocalAssetFileSystemMutationSelfTests.RecycledAssetsReconcileCatalogSaveFailures),
            ("atomic local copies stay flat and never overwrite", LocalAssetFileSystemMutationSelfTests.AtomicCopiesNeverExposePartialOrOverwriteExistingFiles),
            ("batch rename rollback diagnostics expose recovery paths", LocalAssetFileSystemMutationSelfTests.BatchRenameRollbackDiagnosticsExposeRecoveryPaths),
            ("batch rename plans follow visible order and preserve extensions", LocalBatchRenamePlannerSelfTests.NamesFollowVisibleOrderAndPreserveExtensions),
            ("batch rename sequence width expands beyond ninety-nine", LocalBatchRenamePlannerSelfTests.SequenceWidthExpandsBeyondNinetyNine),
            ("batch rename rejects empty and invalid name inputs", LocalBatchRenamePlannerSelfTests.EmptySelectionsAndInvalidNamePartsAreRejected),
            ("local and shared drag targets use dedicated drop zones", LocalDragAndShellWiringStaysScoped),
            ("local marker sets persist CRUD by local asset", LocalMarkerPersistenceSelfTests.CrudPersistsByLocalAsset),
            ("local marker CSV samples remain byte-for-byte stable after persistence", LocalMarkerPersistenceSelfTests.CsvSamplesRemainByteStable),
            ("session tokens use origin-scoped secure storage", SessionTokenStoreSelfTests.OriginScopedStorageWorks),
            ("remembered login accounts isolate passwords by server and user", RememberedLoginPasswordStoreSelfTests.AccountsAndPasswordsRemainIsolated),
            ("deleting a remembered login credential is scoped and idempotent", RememberedLoginPasswordStoreSelfTests.DeletingCredentialIsIdempotentAndScoped),
            ("uninstall cleanup removes only managed data and credentials", ApplicationDataPurgeSelfTests.ManagedDataAndCredentialsArePurgedWithoutTouchingMedia),
            ("uninstall cleanup rejects paths outside application data", ApplicationDataPurgeSelfTests.UnsafeTargetsAreRejectedBeforeCleanup),
            ("uninstall credential cleanup targets are strictly scoped", ApplicationDataPurgeSelfTests.CredentialTargetsAreStrictlyScoped),
            ("installer asks before invoking explicit uninstall cleanup", ApplicationDataPurgeSelfTests.InstallerUsesExplicitOptInCleanupCommand),
            ("API client sends bearer tokens and rejects cross-host addresses", ApiClientSelfTests.AuthenticationAndUriRules),
            ("COS update manifests do not require server installer copies", ClientUpdateDeliverySelfTests.CosManifestsDoNotRequireServerInstallerCopies),
            ("published signatures preserve timestamp precision", ClientUpdateDeliverySelfTests.PublishedSignaturesPreserveTimestampPrecision),
            ("COS update object keys follow the fixed private convention", ClientUpdateDeliverySelfTests.CosManifestObjectKeysFollowTheFixedConvention),
            ("p.6.8 update delivery gates only p.6.6 and p.6.7", ClientUpdateDeliverySelfTests.ManualTransitionBlocksOnlyBrokenUpdaterVersions),
            ("COS update downloads return a signed redirect without opening installer bytes", ClientUpdateDeliverySelfTests.CosDownloadEndpointReturnsSignedRedirect),
            ("client updates report bounded byte and percentage progress", ClientUpdateDeliverySelfTests.ClientDownloadsReportBoundedProgressAndVerifyBytes),
            ("cancelled client updates preserve and resume partial downloads", ClientUpdateDeliverySelfTests.CancelledClientDownloadsPreserveAndResumePartialFiles),
            ("release signatures reject tampering and untrusted signers", Release111SelfTests.ReleaseSignaturesRejectTampering),
            ("first update recovery preserves user data and rejects damaged snapshots", () => Release111SelfTests.FirstUpdateRecoveryPreservesUserData().GetAwaiter().GetResult()),
            ("real upload transcoding preserves WebM alpha, source files and compatibility", () => Release111SelfTests.RealUploadTranscodingPreservesAlphaAndOriginals().GetAwaiter().GetResult()),
            ("oversized client release manifests are rejected before parsing", ClientUpdateDeliverySelfTests.OversizedClientReleaseManifestsAreRejected),
            ("client update delivery redirects installer bytes to private COS", ClientUpdateDeliverySelfTests.ServerAndPublisherKeepInstallerBytesOffTheAppServer),
            ("semantic versions order preview releases correctly", SemanticVersionsOrderPreviewReleases),
            ("API client DTOs match server enum and TimeSpan JSON", ApiClientSelfTests.DtoFormatsMatchServer),
            ("asset duration metadata validates and survives server persistence", AssetDurationMetadataSelfTests.ValidationAndPersistenceRoundTrip),
            ("cloud folder hierarchy persists and enforces creator ownership", AssetFolderSelfTests.HierarchyPersistenceAndPermissionsRemainConsistent),
            ("cloud folder deletion promotes direct contents without deleting assets", AssetFolderSelfTests.FolderDeletionPromotesDirectChildrenWithoutDeletingAssets),
            ("server original asset quota defaults to one hundred GiB", AssetFolderSelfTests.ServerQuotaDefaultsToOneHundredGiB),
            ("API client personal recycle-bin routes stay owner scoped", ApiClientSelfTests.PersonalRecycleBinRoutesStayScoped),
            ("API client loads every personal recycle-bin page", ApiClientSelfTests.PersonalRecycleBinLoadsEveryPage),
            ("API client cloud-folder routes preserve recursive hierarchy scope", ApiClientSelfTests.AssetFolderRoutesPreserveHierarchy),
            ("API client direct-folder queries and audio category routes stay exact", ApiClientSelfTests.DirectFolderQueriesAndAudioCategoryRoutes),
            ("object deletion failures remain queued and retry successfully", ObjectDeletionOutboxSelfTests.FailedDeletesRemainQueuedAndRetry),
            ("schema 1 server data normalizes the object deletion outbox", ObjectDeletionOutboxSelfTests.SchemaOneDataNormalizesTheOutbox),
            ("SQLite serializes concurrent writes and survives restart", SqliteAppDataStoreSelfTests.ConcurrentWritesSurviveRestart),
            ("SQLite online backups verify and restore application state", SqliteAppDataStoreSelfTests.VerifiedBackupCanBeRestored),
            ("SQLite schema migrations preserve state, WAL, snapshots and audit across upgrade/downgrade", SqliteMigrationSelfTests.UpgradeDowngradeAndAudit),
            ("SQLite failed and canceled migrations roll back and remain retryable", SqliteMigrationSelfTests.FailureRollsBack),
            ("Transfer pause/resume/cancel preserves checkpoints, scope and cleanup retries", TransferControlSelfTests.PauseResumeCancelAndDurability),
            ("server settings persist, apply at runtime, and never return secrets", ServerSettingsSelfTests.SettingsPersistAndApplyWithoutReturningSecrets),
            ("server settings reject unsafe paths and editor opens paused", ServerSettingsSelfTests.SettingsRejectUnsafeValuesAndAdminRoutesStayProtected),
            ("API client uploads and downloads without whole-file buffering", ApiClientSelfTests.TransfersRemainStreaming),
            ("API client exposes RFC 7807 error fields", ApiClientSelfTests.ProblemDetailsAreExposed),
            ("API client streams derivative and team LUT routes", ApiClientSelfTests.DerivativeAndLutRoutesStreamContent),
            ("team LUT list queries preserve uploader scope", ApiClientSelfTests.TeamLutListsPreserveUploaderScope),
            ("visible network errors hide server addresses", ApiClientSelfTests.VisibleNetworkMessagesHideAddresses),
            ("network failures distinguish DNS, refusal, timeout, TLS, and HTTP status", ApiClientSelfTests.NetworkFailuresAreClassified),
            ("successful non-JSON API responses are reported as invalid services", ApiClientSelfTests.InvalidSuccessBodiesAreClassified),
            ("cloud upload hashes and streams local files", CloudFileTransferServiceSelfTests.UploadHashesAndStreams),
            ("failed initial upload cleans created metadata and preserves the original error", CloudFileTransferServiceSelfTests.FailedUploadCleansMetadata),
            ("cloud download verifies content and registers the persistent path", CloudFileTransferServiceSelfTests.DownloadVerifiesAndRegisters),
            ("user-visible cloud downloads stay flat and preserve name collisions", CloudFileTransferServiceSelfTests.UserVisibleDownloadsStayFlatAndPreserveNameCollisions),
            ("explicit download destinations reuse verified cached bytes", CloudFileTransferServiceSelfTests.ExplicitDestinationsReuseVerifiedCachedBytes),
            ("user-visible interrupted downloads resume with HTTP Range", CloudFileTransferServiceSelfTests.UserVisibleInterruptedDownloadResumesWithRange),
            ("failed cloud download preserves an existing target", CloudFileTransferServiceSelfTests.FailedDownloadPreservesExistingFile),
            ("interrupted cloud downloads resume with HTTP Range", CloudFileTransferServiceSelfTests.InterruptedDownloadResumesWithRange),
            ("interrupted direct uploads resume remaining multipart parts", CloudFileTransferServiceSelfTests.InterruptedDirectUploadResumesRemainingParts),
            ("completed direct uploads reconcile a lost completion response", CloudFileTransferServiceSelfTests.CompletedDirectUploadReconcilesLostResponse),
            ("avatar JPG, PNG, and WebP inputs become sanitized square WebP", AvatarNormalizerSelfTests.SupportedInputsBecomeSanitizedSquareWebp),
            ("avatar normalization enforces the 10 MiB input limit", AvatarNormalizerSelfTests.InputSizeLimitIsEnforced),
            ("avatar normalization rejects invalid and animated inputs", AvatarNormalizerSelfTests.InvalidAndAnimatedInputsAreRejected),
            ("server accepts normalized static WebP avatars", ServerAvatarValidationSelfTests.NormalizedStaticWebpIsAccepted),
            ("server rejects forged avatar MIME and malformed WebP bodies", ServerAvatarValidationSelfTests.ForgedAvatarUploadsAreRejected),
            ("SignalR publisher preserves revision, scope, and entity", LibraryRealtimeSelfTests.PublisherPreservesScopeEntityAndRevision),
            ("SignalR mappings survive an individual publish failure", LibraryRealtimeSelfTests.NotificationMappingsAndFailureIsolationWork),
            ("desktop SignalR reconnects only after a successful handshake and compensates state", LibraryRealtimeSelfTests.DesktopReconnectContractRemainsWired),
            ("old assets queue versioned thumbnail and proxy derivatives", MediaDerivativeSelfTests.OldAssetsQueueVersionedDerivativeBackfill),
            ("COS entity tags do not replace trusted derivative SHA-256 metadata", MediaDerivativeSelfTests.CosEntityTagsDoNotReplaceTrustedDerivativeHashes),
            ("stale derivative snapshots refresh before download", MediaDerivativeSelfTests.StaleDerivativeSnapshotsRefreshBeforeDownload),
            ("CUBE LUT validation enforces dimensions and declared rows", MediaDerivativeSelfTests.CubeValidationAcceptsDeclaredRowsAndRejectsUnsafeShapes),
            ("real DJI LUT and bounded WebP thumbnail pass validation", MediaDerivativeSelfTests.RealDjiCubeAndThumbnailEnvelopeAreAccepted),
            ("legacy server assets persist derivative migration", MediaDerivativeSelfTests.LegacyServerAssetsPersistDerivativeMigration),
            ("static image thumbnails are bounded and reuse cache", StaticImageThumbnailServiceSelfTests.OutputIsBoundedAndCacheIsReused),
            ("static image thumbnail cache invalidates and remains bounded", StaticImageThumbnailServiceSelfTests.SourceChangesInvalidateAndCacheStaysBounded),
            ("static image thumbnails reject unsafe input and allow in-flight disposal", StaticImageThumbnailServiceSelfTests.UnsafeInputsDowngradeAndDisposeAllowsInFlightWork),
            ("Windows shell thumbnails release GDI handles", StaticImageThumbnailServiceSelfTests.WindowsShellThumbnailsReleaseGdiHandles),
            ("file-open arguments and activation broker remain bounded", PlatformIntegrationSelfTests.FileOpenArgumentsAndBrokerRemainBounded),
            ("single-instance activation forwards multiple files", () => PlatformIntegrationSelfTests.SingleInstanceForwardsMultipleFiles().GetAwaiter().GetResult()),
            ("Windows default-player registration avoids UserChoice", PlatformIntegrationSelfTests.WindowsRegistrationPlanIsUserChoiceSafe),
            ("UI dispatcher diagnostics wait for platform initialization", ClientStartupSourceSelfTests.UiDispatcherDiagnosticsWaitForPlatformInitialization),
            ("main-window shutdown stays asynchronous and time bounded", ClientShutdownSourceSelfTests.MainWindowShutdownRemainsResponsiveAndBounded),
            ("profile password changes update remembered credentials", ClientP58RegressionSourceSelfTests.ProfilePasswordChangeRemainsWired),
            ("cloud preview, download, upload, and drag races stay guarded", ClientP58RegressionSourceSelfTests.CloudAndDragRaceGuardsRemainWired),
            ("p.6.1 layout, editor, and click selection stay wired", ClientP61RegressionSourceSelfTests.ResizableLayoutEditorAndSelectionRemainWired),
            ("p.6.1 audio details use visible independent waveforms", ClientP61RegressionSourceSelfTests.AudioDetailsUseVisibleIndependentWaveforms),
            ("p.6.2 waveform lists and cloud selection stay wired", ClientP62RegressionSourceSelfTests.WaveformListsAndCloudSelectionStayWired),
            ("p.6.2 detail collapse, editor, and COS update progress stay wired", ClientP62RegressionSourceSelfTests.DetailCollapseEditorAndCosProgressStayWired),
            ("p.6.3 local batch selection and operations stay wired", ClientP63RegressionSourceSelfTests.LocalBatchSelectionAndOperationsStayWired),
            ("p.6.4 cloud folders, waveforms, and download notifications stay wired", ClientP64RegressionSourceSelfTests.CloudFoldersWaveformsAndNotificationsStayWired),
            ("p.6.5 selection, batch move, install path, and cloud drag stay wired", ClientP65RegressionSourceSelfTests.SelectionBatchMoveInstallPathAndCloudDragStayWired),
            ("p.6.6 one-click updater and post-update notes stay wired", ClientP66RegressionSourceSelfTests.OneClickUpdaterAndReleaseNotesStayWired),
            ("p.6.6 hover selection and resume behavior stay wired", ClientP66RegressionSourceSelfTests.HoverSelectionAndResumeBehaviorStayWired),
            ("p.6.7 folder browsing, drag downloads, and uploads stay wired", ClientP67RegressionSourceSelfTests.FolderBrowsingDragDownloadsAndUploadsStayWired),
            ("p.6.7 library refresh still resets preview playback", ClientP67RegressionSourceSelfTests.LibraryRefreshStillResetsPreviewPlayback),
            ("p.6.8 image zoom and pan stay wired", ClientP68RegressionSourceSelfTests.ImageZoomAndPanStayWired),
            ("p.6.8 uses a visible transition setup before silent updates", UpdaterSelfTests.TransitionVersionUsesVisibleSetupBeforeSilentUpdates),
            ("updater reads client camelCase transactions", () => UpdaterSelfTests.CamelCaseClientTransactionsDeserializeInUpdater().GetAwaiter().GetResult()),
            ("updater invokes Inno silently while preserving the install directory", UpdaterSelfTests.SilentInstallerArgumentsPreserveInstallDirectory),
            ("official semantic color picker and text editing stay synchronized", SemanticColorEditorSourceSelfTests.OfficialColorPickerAndTextEditingStaySynchronized),
            ("native pickers and drag starts stay single-flight", ClientInteractionSafetySourceSelfTests.NativePickersAndDragStartsRemainSingleFlight),
            ("modal native pickers stay single-flight and close-safe", ClientInteractionSafetySourceSelfTests.ModalNativePickersRemainSingleFlightAndCloseSafe),
            ("missing libmpv returns a structured unavailable state", MediaCoreSelfTests.MissingLibMpvReturnsUnavailableState),
            ("libmpv loadfile resume options use the options argument", MediaCoreSelfTests.LibMpvLoadFileCommandsKeepResumeOptionsInTheOptionsSlot),
            ("video hosts stay hidden until a frame can render", MediaCoreSelfTests.VideoHostsOnlyShowAfterAFrameCanRender),
            ("libmpv LUT filters persist and validate canonical readback", MediaCoreSelfTests.LibMpvLutFiltersValidateCanonicalReadback),
            ("playback controllers model queues and share global volume", MediaCoreSelfTests.PlaybackControllersShareVolumeAndModelQueue),
            ("playback disposal cancels and drains concurrent engine operations", PlaybackControllerLifecycleSelfTests.ConcurrentDisposeCancelsAndDrainsPlaybackOperations),
            ("playback LUT selections remain isolated per queue item", MediaCoreSelfTests.PlaybackLutsAreRememberedPerItem),
            ("playback folder scans recurse, filter, and sort media", PlaybackFeatureSelfTests.FolderScannerRecursesFiltersAndSorts),
            ("playback shortcut defaults remain valid and unique", PlaybackFeatureSelfTests.PlaybackShortcutDefaultsStayValidAndUnique),
            ("queue replacement and removal discard stale LUT state", PlaybackFeatureSelfTests.QueueReplacementAndRemovalDiscardLutState),
            ("player folder, marker, LUT, trim, export, and audio UI stays wired", PlaybackFeatureSelfTests.PlayerFeatureSourceWiringRemainsComplete),
            ("playback lifecycle and preview race guards remain wired", MediaCoreSelfTests.PlaybackReliabilityGuardsRemainWired),
            ("deferred UI resources batch reference-unique disposal and isolate failures", MediaCoreSelfTests.DeferredUiResourceDisposalRemainsBatchedAndIsolated),
            ("ffprobe JSON detects multiple audio tracks", MediaCoreSelfTests.FfprobeJsonDetectsMultipleAudioTracks),
            ("ffmpeg commands keep paths atomic and protect originals", MediaCoreSelfTests.FfmpegCommandsKeepPathsAtomicAndProtectOriginals),
            ("cube LUT validation accepts Log ranges and rejects bad shapes", MediaCoreSelfTests.CubeValidatorAcceptsLogValuesAndRejectsBadShape),
            ("local LUT libraries persist paths without deleting sources", MediaCoreSelfTests.LutLibraryPersistsPathsWithoutDeletingSources),
            ("fixed XAML text has complete English mappings", UiLocalizationSourceSelfTests.FixedXamlTextHasEnglishMappings),
            ("client runtime text has complete English mappings", UiLocalizationSourceSelfTests.ClientRuntimeTextHasEnglishMappings),
            ("Windows IME stays enabled across dialogs and editor shortcuts", UiLocalizationSourceSelfTests.WindowsImeCompatibilityStaysGlobalAndShortcutSafe),
            ("destructive UI actions use click confirmation without typed names", DeletionConfirmationSourceSelfTests.DestructiveActionsUseClickConfirmation),
            ("automatic UI translation preserves bound runtime values", UiLocalizationSourceSelfTests.RuntimeValuesRemainOutsideAutomaticTranslation),
            ("dynamic localization registrations compact dead weak subscribers", UiLocalizationSourceSelfTests.DynamicLocalizationRegistrationsRemainBounded),
            ("automatic markers and stable API errors localize without matching server prose", UiLocalizationSourceSelfTests.AutomaticMarkerNamesAndStableApiErrorsAreLocalized),
            ("user profiles expose only owned active team LUTs", ProfileLutSourceSelfTests.UserProfilesExposeOnlyOwnedActiveTeamLuts),
            ("shared library routes cube uploads and exposes team LUTs", ProfileLutSourceSelfTests.SharedLibraryRoutesCubeUploadsAndExposesTeamLuts),
            ("latest page refresh cancels stale network and UI work", RefreshSingleFlightSourceSelfTests.LatestPageRefreshCancelsStaleNetworkAndUiWork),
            ("library refresh clears preview progress", LibraryRefreshClearsPreviewProgress),
            ("temporary editor exports one video with trim and volume", EditorTemporaryVideoSelfTests.ExportCommandKeepsSingleVideoWorkflow),
            ("temporary editor exports audio with trim and gain", EditorTemporaryVideoSelfTests.AudioExportCommandKeepsTrimAndGain),
            ("editor export options ignore initialization selection events", EditorTemporaryVideoSelfTests.ExportOptionsIgnoreXamlSelectionEventsDuringInitialization)
        };

        var failures = 0;
        foreach (var test in tests)
        {
            try
            {
                test.Run();
                Console.WriteLine($"PASS  {test.Name}");
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine($"FAIL  {test.Name}");
                Console.Error.WriteLine(exception);
            }
        }

        Console.WriteLine($"{tests.Length - failures}/{tests.Length} self-tests passed.");
        return failures == 0 ? 0 : 1;
    }

    private static void DownloadSettingDefaultsToConfiguredDirectory()
    {
        True(new ClientSettings().DownloadToDefaultDirectory);
        var migrated = JsonSerializer.Deserialize<ClientSettings>("{}")!;
        True(migrated.DownloadToDefaultDirectory);
        False((migrated with { DownloadToDefaultDirectory = false })
            .ValidateAndNormalize()
            .DownloadToDefaultDirectory);
    }

    private static void CosObjectStoreSelfTest()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cos:Region"] = "ap-shanghai",
                ["Cos:AppId"] = "1309349835",
                ["Cos:Bucket"] = "ytwqsck-1309349835",
                ["Cos:SecretId"] = "test-secret-id",
                ["Cos:SecretKey"] = "test-secret-key",
                ["Cos:UseHttps"] = "true",
                ["Cos:SignedUrlLifetimeMinutes"] = "15"
            })
            .Build();
        var store = new CosObjectStore(configuration, NullLogger<CosObjectStore>.Instance);
        var signed = store.CreateDownloadUrlAsync("originals/2026/08/sample.mp4", "示例.mp4")
            .AsTask()
            .GetAwaiter()
            .GetResult();
        True(signed is not null);
        Equal("https", signed!.Url.Scheme);
        Equal("ytwqsck-1309349835.cos.ap-shanghai.myqcloud.com", signed.Url.Host);
        Contains("q-signature=", signed.Url.Query);
        Contains("response-content-disposition=", signed.Url.Query);
        Contains("%E7%A4%BA%E4%BE%8B.mp4", signed.Url.AbsoluteUri);
        False(signed.Url.AbsoluteUri.Contains("%25E7", StringComparison.OrdinalIgnoreCase));
        True(signed.ExpiresAt > DateTimeOffset.UtcNow);
        Throws<ArgumentException>(() => store.CreateDownloadUrlAsync("../unsafe", cancellationToken: CancellationToken.None));
    }

    private static void ServerAddressRulesSupportSecureTunnels()
    {
        Equal(
            "https://tunnel.example/",
            (new ClientSettings { ServerAddress = "https://tunnel.example" })
            .ValidateAndNormalize()
            .ServerAddress);
        Equal(
            "http://127.0.0.1:5019/",
            (new ClientSettings { ServerAddress = "http://127.0.0.1:5019" })
            .ValidateAndNormalize()
            .ServerAddress);
        Equal(
            "http://10.66.66.1:5019/",
            (new ClientSettings { ServerAddress = "http://10.66.66.1:5019" })
            .ValidateAndNormalize()
            .ServerAddress);

        try
        {
            _ = (new ClientSettings { ServerAddress = "http://tunnel.example" })
                .ValidateAndNormalize();
            throw new InvalidOperationException("Expected public plaintext HTTP to be rejected.");
        }
        catch (InvalidDataException exception)
        {
            Contains("HTTPS", exception.Message);
        }
    }

    private static void SemanticVersionsOrderPreviewReleases()
    {
        var p59 = SemanticVersion.Parse("0.2.0-preview.5.9");
        var p6 = SemanticVersion.Parse("0.2.0-preview.6");
        var p61 = SemanticVersion.Parse("0.2.0-preview.6.1");
        var release = SemanticVersion.Parse("0.2.0");

        True(p6.CompareTo(p59) > 0);
        True(p61.CompareTo(p6) > 0);
        True(release.CompareTo(p61) > 0);
        Equal(0, SemanticVersion.Parse("1.2.3-alpha.1").CompareTo(SemanticVersion.Parse("1.2.3-alpha.1")));
        False(SemanticVersion.TryParse("1.2", out _));
        False(SemanticVersion.TryParse("1.2.3-preview.01", out _));
    }

    private static void EmptyCsvSampleIsStable()
    {
        var bytes = SyntheticMarkerFixtures.Bytes("空白.csv");
        AssertCsvEnvelope(bytes);

        var document = MarkersCsv.Read(bytes);
        Equal(0, document.Markers.Count);
        SequenceEqual(bytes, MarkersCsv.Write(document));

        var expectedBody = MarkersCsv.Header + "\r\n1,,,,,,,,\r\n";
        Equal(expectedBody, Encoding.UTF8.GetString(bytes.AsSpan(3)));
    }

    private static void LongCsvSampleIsStable()
    {
        var bytes = SyntheticMarkerFixtures.Bytes("超一小时的markers.csv");
        AssertCsvEnvelope(bytes);

        var document = MarkersCsv.Read(bytes);
        Equal(3, document.Markers.Count);
        Equal(
            TimeSpan.FromHours(1) + TimeSpan.FromMinutes(25) + TimeSpan.FromSeconds(34) + TimeSpan.FromMilliseconds(554),
            document.Markers[2].Time);
        Equal("标记 3", document.Markers[2].Name);
        Equal(TimeSpan.FromHours(2) + TimeSpan.FromMinutes(20) + TimeSpan.FromSeconds(41), document.RecordingDuration);
        SequenceEqual(bytes, MarkersCsv.Write(document));
    }

    private static void CsvSpecialFieldsRoundTrip()
    {
        var document = new MarkerCsvDocument(
            "录像,\"甲\"\r\n第二行.mp4",
            "F:\\素材,测试\\\"片段\".mp4",
            new DateTime(2026, 8, 27, 9, 8, 7),
            TimeSpan.FromHours(30),
            [
                new MarkerCsvEntry(
                    TimeSpan.FromHours(25) + TimeSpan.FromMilliseconds(9),
                    "名称,\"一\"\r\n名称二",
                    "备注,\"引号\"\r\n下一行")
            ]);

        var bytes = MarkersCsv.Write(document);
        AssertCsvEnvelope(bytes);
        var text = Encoding.UTF8.GetString(bytes.AsSpan(3));
        Contains("25:00:00.009", text);
        Contains("\"备注,\"\"引号\"\"\r\n下一行\"", text);
        Contains("\"名称,\"\"一\"\"\r\n名称二\"", text);

        var parsed = MarkersCsv.Read(bytes);
        Equal(document.RecordingName, parsed.RecordingName);
        Equal(document.RecordingPath, parsed.RecordingPath);
        Equal(document.Markers[0], parsed.Markers[0]);
        SequenceEqual(bytes, MarkersCsv.Write(parsed));
    }

    private static void CsvRejectsInvalidInput()
    {
        var valid = MarkersCsv.Write(new MarkerCsvDocument(
            "sample.mp4",
            "C:\\sample.mp4",
            null,
            TimeSpan.FromSeconds(10),
            [new MarkerCsvEntry(TimeSpan.FromSeconds(5), "marker", null)]));

        Throws<MarkerCsvFormatException>(() => MarkersCsv.Read(valid.AsSpan(3)));
        Throws<MarkerCsvFormatException>(() => MarkersCsv.Read(valid.AsSpan(0, valid.Length - 2)));

        var bareLf = Encoding.UTF8.GetPreamble()
            .Concat(Encoding.UTF8.GetBytes(MarkersCsv.Header + "\n1,,,,,,,,\n"))
            .ToArray();
        Throws<MarkerCsvFormatException>(() => MarkersCsv.Read(bareLf));

        var eightColumns = Encoding.UTF8.GetPreamble()
            .Concat(Encoding.UTF8.GetBytes("a,b,c,d,e,f,g,h\r\n1,,,,,,,\r\n"))
            .ToArray();
        Throws<MarkerCsvFormatException>(() => MarkersCsv.Read(eightColumns));

        Throws<ArgumentException>(() => MarkersCsv.Write(new MarkerCsvDocument(
            null,
            null,
            null,
            null,
            [
                new MarkerCsvEntry(TimeSpan.FromSeconds(2)),
                new MarkerCsvEntry(TimeSpan.FromSeconds(1))
            ])));

        Throws<ArgumentException>(() => MarkersCsv.Write(new MarkerCsvDocument(
            null,
            null,
            null,
            TimeSpan.FromSeconds(1),
            [new MarkerCsvEntry(TimeSpan.FromSeconds(2))])));

        Throws<ArgumentException>(() => MarkersCsv.Write(new MarkerCsvDocument(
            null,
            null,
            null,
            null,
            [new MarkerCsvEntry(TimeSpan.FromTicks(1))])));
    }

    private static void MarkerSetPermissionsWork()
    {
        var service = new InMemoryMarkerSetService();
        var assetId = Guid.NewGuid();
        var firstVersionId = Guid.NewGuid();
        var secondVersionId = Guid.NewGuid();
        var owner = new MarkerSetPrincipal(
            Guid.NewGuid(),
            "Owner",
            UserRole.Member,
            UserPermission.MemberDefault);
        var other = new MarkerSetPrincipal(
            Guid.NewGuid(),
            "Other",
            UserRole.Member,
            UserPermission.MemberDefault);
        var administrator = new MarkerSetPrincipal(
            Guid.NewGuid(),
            "Administrator",
            UserRole.Administrator,
            UserPermission.AdministratorDefault);
        var noBrowse = new MarkerSetPrincipal(
            Guid.NewGuid(),
            "No browse",
            UserRole.Member,
            UserPermission.EditOwnMarkers);

        service.SetCurrentAssetVersion(assetId, firstVersionId, TimeSpan.FromMinutes(10));
        var first = service.Create(owner, new CreateMarkerSetRequest(assetId, firstVersionId, "第一套"));
        _ = service.Create(owner, new CreateMarkerSetRequest(assetId, firstVersionId, "第二套"));
        var firstMarker = service.AddMarker(
            owner,
            first.Id,
            new UpsertMarkerRequest(TimeSpan.FromSeconds(5), "开始", "备注"));
        var secondMarker = service.AddMarker(
            owner,
            first.Id,
            new UpsertMarkerRequest(TimeSpan.FromSeconds(8), "继续", null));

        Equal(2, service.ListForAsset(owner, assetId).Count(summary => summary.OwnerUserId == owner.UserId));
        Equal(2, service.Get(other, first.Id).Markers.Count);
        var otherAccess = service.GetAccess(other, first.Id);
        True(otherAccess.CanView);
        False(otherAccess.CanEdit);
        False(otherAccess.CanDelete);
        True(otherAccess.CanCopy);

        Throws<UnauthorizedAccessException>(() => service.Rename(
            other,
            first.Id,
            new RenameMarkerSetRequest("不允许")));
        Throws<UnauthorizedAccessException>(() => service.UpdateMarker(
            other,
            first.Id,
            firstMarker.Id,
            new UpsertMarkerRequest(TimeSpan.FromSeconds(6), null, null)));
        Throws<UnauthorizedAccessException>(() => service.DeleteMarker(other, first.Id, firstMarker.Id));
        Throws<UnauthorizedAccessException>(() => service.DeleteSet(other, first.Id));
        Throws<UnauthorizedAccessException>(() => service.Get(noBrowse, first.Id));
        Throws<UnauthorizedAccessException>(() => service.CopyToOwn(
            noBrowse,
            new CopyMarkerSetRequest(first.Id, "无浏览权限")));

        var copy = service.CopyToOwn(other, new CopyMarkerSetRequest(first.Id, "复制后可编辑"));
        Equal(other.UserId, copy.OwnerUserId);
        Equal(first.AssetVersionId, copy.AssetVersionId);
        Equal(2, copy.Markers.Count);
        False(copy.Markers.Select(marker => marker.Id).Intersect(
            service.Get(owner, first.Id).Markers.Select(marker => marker.Id)).Any());
        _ = service.UpdateMarker(
            other,
            copy.Id,
            copy.Markers[0].Id,
            new UpsertMarkerRequest(TimeSpan.FromSeconds(7), "自己的副本", null));

        var administratorAccess = service.GetAccess(administrator, first.Id);
        False(administratorAccess.CanEdit);
        True(administratorAccess.CanDelete);
        Throws<UnauthorizedAccessException>(() => service.Rename(
            administrator,
            first.Id,
            new RenameMarkerSetRequest("管理员不能修改")));
        Throws<UnauthorizedAccessException>(() => service.UpdateMarker(
            administrator,
            first.Id,
            secondMarker.Id,
            new UpsertMarkerRequest(TimeSpan.FromSeconds(9), null, null)));
        service.DeleteMarker(administrator, first.Id, firstMarker.Id);
        Equal(1, service.Get(owner, first.Id).Markers.Count);

        service.SetCurrentAssetVersion(assetId, secondVersionId, TimeSpan.FromMinutes(12));
        True(service.Get(owner, first.Id).IsBasedOnOldVersion);
        True(service.Get(other, copy.Id).IsBasedOnOldVersion);
        var current = service.Create(owner, new CreateMarkerSetRequest(assetId, secondVersionId, "新版本标记"));
        False(current.IsBasedOnOldVersion);
        Equal(firstVersionId, service.Get(owner, first.Id).AssetVersionId);

        service.DeleteSet(administrator, first.Id);
        Throws<KeyNotFoundException>(() => service.Get(owner, first.Id));
    }

    private static void MarkerSetCsvImportExportWorks()
    {
        var bytes = SyntheticMarkerFixtures.Bytes("超一小时的markers.csv");
        var source = MarkersCsv.Read(bytes);
        var service = new InMemoryMarkerSetService();
        var assetId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var owner = new MarkerSetPrincipal(
            Guid.NewGuid(),
            "Owner",
            UserRole.Member,
            UserPermission.MemberDefault);
        var viewer = new MarkerSetPrincipal(
            Guid.NewGuid(),
            "Viewer",
            UserRole.Member,
            UserPermission.MemberDefault);

        service.SetCurrentAssetVersion(assetId, versionId, source.RecordingDuration);
        var request = new ImportMarkerSetRequest(assetId, versionId, "播放器导入");
        var imported = service.ImportCsv(owner, request, bytes);
        var importedAgain = service.ImportCsv(owner, request with { Name = "再次导入" }, bytes);
        NotEqual(imported.Id, importedAgain.Id);
        Equal(3, imported.Markers.Count);
        Equal(source.Markers[2], new MarkerCsvEntry(
            imported.Markers[2].Time,
            imported.Markers[2].Name,
            imported.Markers[2].Note));

        var exported = service.ExportCsv(
            viewer,
            imported.Id,
            new MarkerCsvRecordingInfo(
                source.RecordingName,
                source.RecordingPath,
                source.RecordingStartedAt,
                source.RecordingDuration));
        SequenceEqual(bytes, exported);

        var emptyBytes = SyntheticMarkerFixtures.Bytes("空白.csv");
        var emptySet = service.ImportCsv(owner, request with { Name = "空标记" }, emptyBytes);
        Equal(0, emptySet.Markers.Count);
        SequenceEqual(
            emptyBytes,
            service.ExportCsv(viewer, emptySet.Id, new MarkerCsvRecordingInfo(null, null, null, null)));
    }

    private static void PasswordHashingWorks()
    {
        const string password = "正确 horse battery 2026!";
        var hasher = new PasswordHasher();
        var first = hasher.Hash(password);
        var second = hasher.Hash(password);

        NotEqual(first, second);
        False(first.Contains(password, StringComparison.Ordinal));
        Equal(PasswordVerificationResult.Success, hasher.Verify(first, password));
        Equal(PasswordVerificationResult.Failed, hasher.Verify(first, "wrong password"));
        Equal(PasswordVerificationResult.Failed, hasher.Verify("not-a-password-hash", password));

        var legacyHash = new PasswordHasher(100_000).Hash(password);
        Equal(PasswordVerificationResult.SuccessRehashNeeded, hasher.Verify(legacyHash, password));
    }

    private static void InputRulesWork()
    {
        True(InputValidator.ValidateUsername("Fengchen.WD").IsValid);
        True(InputValidator.ValidateUsername("  admin  ").IsValid);
        False(InputValidator.ValidateUsername("ab").IsValid);
        False(InputValidator.ValidateUsername("风尘WD").IsValid);
        False(InputValidator.ValidateUsername("bad name").IsValid);
        True(InputValidator.ValidatePassword("Password1").IsValid);
        True(InputValidator.ValidatePassword("abcdefgh1").IsValid);
        True(InputValidator.ValidatePassword("!@#$%^&*A").IsValid);
        False(InputValidator.ValidatePassword("1234567").IsValid);
        True(InputValidator.ValidatePassword("A" + new string('x', 127)).IsValid);
        False(InputValidator.ValidatePassword(new string('x', 129)).IsValid);
        True(PasswordService.IsAcceptable("!!!!!!!!A", out _));
        True(PasswordService.IsAcceptable("1234567A", out _));
        False(PasswordService.IsAcceptable("12345678", out _));
        False(PasswordService.IsAcceptable("1234567", out _));
        False(PasswordService.IsAcceptable(new string('x', 129), out _));
        True(InputValidator.ValidateEmail(null).IsValid);
        True(InputValidator.ValidateEmail("member@example.com").IsValid);
        False(InputValidator.ValidateEmail("member @example.com").IsValid);

        var blankProfile = new UpdateUserProfileRequest(
            null,
            null,
            null,
            ProfileGender.Unspecified,
            null,
            null,
            ProfileFieldVisibility.SelfOnly,
            ProfileFieldVisibility.SelfOnly,
            ProfileFieldVisibility.SelfOnly);
        True(InputValidator.ValidateProfile(blankProfile).IsValid);
        False(InputValidator.ValidateProfile(blankProfile with { Gender = ProfileGender.Custom }).IsValid);
        True(InputValidator.ValidateAvatar(InputValidator.AvatarMaxBytes, "image/webp").IsValid);
        False(InputValidator.ValidateAvatar(InputValidator.AvatarMaxBytes + 1, "image/webp").IsValid);
        False(InputValidator.ValidateAvatar(1, "image/gif").IsValid);

        var limits = new AssetUploadLimits(ImageBytes: 100, AudioBytes: 200, VideoBytes: 300);
        var upload = new BeginUploadRequest(
            "测试素材",
            "sample.png",
            AssetCategory.Image,
            100,
            new string('a', 64),
            null,
            Array.Empty<Guid>());
        True(InputValidator.ValidateUpload(upload, limits).IsValid);
        False(InputValidator.ValidateUpload(upload with { OriginalFileName = "sample.mp3" }, limits).IsValid);
        False(InputValidator.ValidateUpload(upload with { OriginalFileName = "stream.m3u8" }, limits).IsValid);
        False(InputValidator.ValidateUpload(upload with { FileSize = 101 }, limits).IsValid);
        Equal(AssetCategory.Image, InputValidator.InferUnambiguousCategory("image.PSD"));
        Equal(AssetCategory.Video, InputValidator.InferUnambiguousCategory("clip.MOV"));
        Equal<AssetCategory?>(null, InputValidator.InferUnambiguousCategory("sound.mp3"));
        True(InputValidator.IsExtensionAllowed("lossless.caf", AssetCategory.Bgm));
        False(InputValidator.IsExtensionAllowed("unsupported.alac", AssetCategory.Bgm));

        True(InputValidator.ValidateMarker(new MarkerCsvEntry(TimeSpan.Zero), TimeSpan.Zero).IsValid);
        False(InputValidator.ValidateMarker(new MarkerCsvEntry(TimeSpan.FromSeconds(-1))).IsValid);
        False(InputValidator.ValidateMarker(new MarkerCsvEntry(TimeSpan.FromSeconds(2)), TimeSpan.FromSeconds(1)).IsValid);
    }

    private static void ContractsBehaveConsistently()
    {
        var page = new PageResult<int>([1, 2], 2, 2, 5);
        Equal(3, page.TotalPages);
        Equal(0, new PageResult<int>([], 1, 50, 0).TotalPages);
        True(InputValidator.ValidatePage(1, 200).IsValid);
        False(InputValidator.ValidatePage(0, 201).IsValid);

        True(UserPermission.MemberDefault.HasFlag(UserPermission.Upload));
        False(UserPermission.MemberDefault.HasFlag(UserPermission.ManageUsers));
        True(UserPermission.AdministratorDefault.HasFlag(UserPermission.ManageUsers));
        Equal(
            AssetCategoryAccess.All,
            AssetCategoryAccess.Bgm | AssetCategoryAccess.SoundEffect | AssetCategoryAccess.Image | AssetCategoryAccess.Video);
    }

    private static async Task LocalSampleFoldersAreIndexed()
    {
        var root = Directory.CreateTempSubdirectory("ial-synthetic-index-").FullName;
        try
        {
            var nested = Directory.CreateDirectory(Path.Combine(root, "nested")).FullName;
            foreach (var name in new[] { "audio.mp3", "video.mp4", "picture.png", "ignored.csv" })
                await File.WriteAllTextAsync(Path.Combine(nested, name), "synthetic");
            using var service = new LocalAssetIndexService(new InMemoryLocalAssetCatalogStore());
            var result = await service.IndexFolderAsync(root, false);
            Equal(3, result.IndexedFileCount);
            Equal(0, result.Issues.Count);
            False(result.Catalog.Assets.Any(asset => asset.Extension == ".csv"));
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task DirectorySnapshotsIncludeAllFolders()
    {
        var root = Directory.CreateTempSubdirectory("ial-directory-snapshot-").FullName;
        var empty = Path.Combine(root, "empty");
        var documents = Path.Combine(root, "documents");
        var nested = Path.Combine(documents, "nested");

        try
        {
            Directory.CreateDirectory(empty);
            Directory.CreateDirectory(nested);
            await File.WriteAllTextAsync(Path.Combine(nested, "notes.txt"), "not a media asset");

            var store = new InMemoryLocalAssetCatalogStore();
            using var service = new LocalAssetIndexService(store);
            var result = await service.IndexFolderAsync(root, false);

            Equal(0, result.IndexedFileCount);
            SequenceEqual(
                new[]
                {
                    "documents",
                    Path.Combine("documents", "nested"),
                    "empty"
                },
                result.Catalog.Directories.Select(directory => directory.RelativePath));
            True(result.Catalog.Directories.All(directory =>
                directory.FolderId == result.Folder.Id &&
                directory.Availability == IndexedDirectoryAvailability.Available));

            var removed = await service.RemoveFolderAsync(result.Folder.Id);
            Equal(0, removed.Directories.Length);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static async Task OfflineDirectorySnapshotsAreRetained()
    {
        var root = Directory.CreateTempSubdirectory("ial-offline-tree-").FullName;
        Directory.CreateDirectory(Path.Combine(root, "first", "second"));

        try
        {
            var store = new InMemoryLocalAssetCatalogStore();
            var storageAvailable = true;
            using var service = new LocalAssetIndexService(
                store,
                isContainingStorageAvailable: _ => storageAvailable);
            var online = await service.IndexFolderAsync(root, false);
            Equal(2, online.Catalog.Directories.Length);

            storageAvailable = false;
            Directory.Delete(root, true);
            var offline = await service.IndexFolderAsync(root, false);

            Equal(IndexedFolderAvailability.Offline, offline.Folder.Availability);
            Equal(2, offline.Catalog.Directories.Length);
            True(offline.Catalog.Directories.All(directory =>
                directory.Availability == IndexedDirectoryAvailability.Offline));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static async Task SchemaOneCatalogMigratesLosslessly()
    {
        var directory = Directory.CreateTempSubdirectory("ial-schema-migration-").FullName;
        var catalogPath = Path.Combine(directory, "local-assets.json");
        var root = Path.Combine(directory, "library");
        var folderId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var addedAt = new DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);
        var relativePath = Path.Combine("alpha", "beta", "sample.mp3");
        var fullPath = Path.Combine(root, relativePath);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };
        var legacyCatalog = new
        {
            schemaVersion = 1,
            folders = new[]
            {
                new
                {
                    id = folderId,
                    path = root,
                    isExternalStorage = false,
                    availability = IndexedFolderAvailability.Online,
                    addedAtUtc = addedAt,
                    lastScanAtUtc = (DateTimeOffset?)addedAt
                }
            },
            assets = new[]
            {
                new
                {
                    id = assetId,
                    folderId,
                    fullPath,
                    relativePath,
                    fileName = "sample.mp3",
                    extension = ".mp3",
                    mediaType = LocalMediaType.Audio,
                    sizeBytes = 123L,
                    lastWriteTimeUtc = addedAt,
                    addedAtUtc = addedAt,
                    availability = LocalAssetAvailability.Available,
                    tags = new[] { "常用", "配乐" }
                }
            }
        };

        try
        {
            await File.WriteAllTextAsync(
                catalogPath,
                JsonSerializer.Serialize(legacyCatalog, options));

            using var store = new JsonLocalAssetCatalogStore(catalogPath);
            var migrated = await store.LoadAsync();

            Equal(LocalAssetCatalog.CurrentSchemaVersion, migrated.SchemaVersion);
            Equal(folderId, migrated.Folders.Single().Id);
            var asset = migrated.Assets.Single();
            Equal(assetId, asset.Id);
            Equal(addedAt, asset.AddedAtUtc);
            SequenceEqual(new[] { "常用", "配乐" }, asset.Tags);
            SequenceEqual(
                new[] { "alpha", Path.Combine("alpha", "beta") },
                migrated.Directories.Select(item => item.RelativePath));

            using var persisted = JsonDocument.Parse(await File.ReadAllBytesAsync(catalogPath));
            Equal(
                LocalAssetCatalog.CurrentSchemaVersion,
                persisted.RootElement.GetProperty("schemaVersion").GetInt32());
            Equal(2, persisted.RootElement.GetProperty("directories").GetArrayLength());
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static async Task ParentRootMergePreservesAssetIdentity()
    {
        var root = Directory.CreateTempSubdirectory("ial-parent-merge-").FullName;
        var child = Path.Combine(root, "child");
        var file = Path.Combine(child, "sample.mp3");

        try
        {
            Directory.CreateDirectory(child);
            await File.WriteAllBytesAsync(file, [1, 2, 3]);

            var store = new InMemoryLocalAssetCatalogStore();
            using var service = new LocalAssetIndexService(store);
            var childResult = await service.IndexFolderAsync(child, false);
            var original = childResult.Catalog.Assets.Single();
            var taggedCatalog = await service.SetTagsAsync(original.Id, ["常用", "父根合并"]);
            var tagged = taggedCatalog.Assets.Single();

            var parentResult = await service.IndexFolderAsync(root, false);
            var merged = parentResult.Catalog.Assets.Single();

            Equal(1, parentResult.Catalog.Folders.Length);
            Equal(root, parentResult.Catalog.Folders.Single().Path);
            NotEqual(childResult.Folder.Id, parentResult.Folder.Id);
            Equal(tagged.Id, merged.Id);
            Equal(tagged.AddedAtUtc, merged.AddedAtUtc);
            Equal(parentResult.Folder.Id, merged.FolderId);
            Equal(Path.Combine("child", "sample.mp3"), merged.RelativePath);
            SequenceEqual(tagged.Tags, merged.Tags);
            True(parentResult.Catalog.Directories.Any(directory =>
                directory.FolderId == parentResult.Folder.Id && directory.RelativePath == "child"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static async Task ChildRootBelowParentIsRejected()
    {
        var root = Directory.CreateTempSubdirectory("ial-child-rejection-").FullName;
        var child = Path.Combine(root, "child");
        Directory.CreateDirectory(child);

        try
        {
            var store = new InMemoryLocalAssetCatalogStore();
            using var service = new LocalAssetIndexService(store);
            await service.IndexFolderAsync(root, false);

            var exception = await ThrowsAsync<InvalidOperationException>(
                async () => await service.IndexFolderAsync(child, false));
            Contains("已经包含在已连接的文件夹", exception.Message);
            Equal(1, (await service.GetCatalogAsync()).Folders.Length);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void DirectoryFilterUsesStrictPathBoundaries()
    {
        var firstFolderId = Guid.NewGuid();
        var secondFolderId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var assets = new[]
        {
            CreateAsset(firstFolderId, Path.Combine("A", "one.mp3")),
            CreateAsset(firstFolderId, Path.Combine("A", "Nested", "two.mp3")),
            CreateAsset(firstFolderId, Path.Combine("AB", "three.mp3")),
            CreateAsset(firstFolderId, "root.mp3"),
            CreateAsset(secondFolderId, Path.Combine("A", "other.mp3"))
        };

        var subtree = LocalAssetSearch.Apply(assets, new LocalAssetQuery
        {
            FolderId = firstFolderId,
            RelativeDirectoryPath = "A",
            SortBy = LocalAssetSortField.FileName,
            SortDirection = LocalSortDirection.Ascending
        });
        SequenceEqual(new[] { "one.mp3", "two.mp3" }, subtree.Select(asset => asset.FileName));

        var wholeRoot = LocalAssetSearch.Apply(assets, new LocalAssetQuery
        {
            FolderId = firstFolderId
        });
        Equal(4, wholeRoot.Count);

        LocalAsset CreateAsset(Guid folderId, string relativePath) => new(
            Guid.NewGuid(),
            folderId,
            Path.Combine(Path.GetTempPath(), folderId.ToString("N"), relativePath),
            relativePath,
            Path.GetFileName(relativePath),
            ".mp3",
            LocalMediaType.Audio,
            1,
            now,
            now,
            LocalAssetAvailability.Available,
            []);
    }

    private static void AssetQueriesDefaultToNameAscending()
    {
        var local = new LocalAssetQuery();
        Equal(LocalAssetSortField.FileName, local.SortBy);
        Equal(LocalSortDirection.Ascending, local.SortDirection);

        var cloud = new ApiAssetListQuery();
        Equal(ApiAssetSort.Name, cloud.Sort);
        Equal(ApiSortOrder.Ascending, cloud.Order);

        var recycleBin = new ApiRecycleBinListQuery();
        Equal(ApiAssetSort.Name, recycleBin.Sort);
        Equal(ApiSortOrder.Ascending, recycleBin.Order);
    }

    private static async Task OfflineStorageReconnectPreservesIdentity()
    {
        var root = Directory.CreateTempSubdirectory("ial-selftest-").FullName;
        var nested = Path.Combine(root, "nested");
        var file = Path.Combine(nested, "sample.MP3");

        try
        {
            Directory.CreateDirectory(nested);
            await File.WriteAllBytesAsync(file, [1, 2, 3]);

            var store = new InMemoryLocalAssetCatalogStore();
            var storageAvailable = true;
            using var service = new LocalAssetIndexService(
                store,
                isContainingStorageAvailable: _ => storageAvailable);
            var first = await service.IndexFolderAsync(root, false);
            var original = first.Catalog.Assets.Single();

            storageAvailable = false;
            Directory.Delete(root, true);
            var offline = await service.IndexFolderAsync(root, false);
            Equal(LocalAssetAvailability.OfflineStorage, offline.Catalog.Assets.Single().Availability);

            storageAvailable = true;
            Directory.CreateDirectory(nested);
            await File.WriteAllBytesAsync(file, [4, 5, 6]);
            var reconnected = await service.IndexFolderAsync(root, false);
            var restored = reconnected.Catalog.Assets.Single();
            Equal(LocalAssetAvailability.Available, restored.Availability);
            Equal(original.Id, restored.Id);
            Equal(original.AddedAtUtc, restored.AddedAtUtc);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static async Task StorageProbeTracksDisconnectAndReconnect()
    {
        var root = Directory.CreateTempSubdirectory("ial-storage-probe-").FullName;
        var nested = Path.Combine(root, "nested");
        var file = Path.Combine(nested, "sample.mp3");

        try
        {
            Directory.CreateDirectory(nested);
            await File.WriteAllBytesAsync(file, [1, 2, 3]);

            var store = new InMemoryLocalAssetCatalogStore();
            var storageAvailable = true;
            using var service = new LocalAssetIndexService(
                store,
                isContainingStorageAvailable: _ => storageAvailable);
            var online = await service.IndexFolderAsync(root, true);

            storageAvailable = false;
            Directory.Delete(root, true);
            var disconnected = await service.ProbeStorageAvailabilityAsync();
            Equal(1, disconnected.DisconnectedFolders.Count);
            Equal(0, disconnected.ReconnectedFolders.Count);
            Equal(online.Folder.Id, disconnected.DisconnectedFolders[0].Id);
            Equal(IndexedFolderAvailability.Offline, disconnected.Catalog.Folders.Single().Availability);
            Equal(LocalAssetAvailability.OfflineStorage, disconnected.Catalog.Assets.Single().Availability);
            True(disconnected.Catalog.Directories.All(directory =>
                directory.Availability == IndexedDirectoryAvailability.Offline));

            var unchanged = await service.ProbeStorageAvailabilityAsync();
            Equal(0, unchanged.DisconnectedFolders.Count);
            Equal(0, unchanged.ReconnectedFolders.Count);

            storageAvailable = true;
            Directory.CreateDirectory(nested);
            await File.WriteAllBytesAsync(file, [4, 5, 6]);
            var reconnected = await service.ProbeStorageAvailabilityAsync();
            Equal(0, reconnected.DisconnectedFolders.Count);
            Equal(1, reconnected.ReconnectedFolders.Count);
            Equal(online.Folder.Id, reconnected.ReconnectedFolders[0].Id);

            var restored = await service.IndexFolderAsync(root, true);
            Equal(LocalAssetAvailability.Available, restored.Catalog.Assets.Single().Availability);
            Equal(online.Catalog.Assets.Single().Id, restored.Catalog.Assets.Single().Id);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static async Task MissingConnectedRootDiscardsStaleAssets()
    {
        var root = Directory.CreateTempSubdirectory("ial-missing-root-").FullName;
        var nested = Path.Combine(root, "nested");
        var file = Path.Combine(nested, "sample.mp3");

        try
        {
            Directory.CreateDirectory(nested);
            await File.WriteAllBytesAsync(file, [1, 2, 3]);

            var store = new InMemoryLocalAssetCatalogStore();
            using var service = new LocalAssetIndexService(
                store,
                isContainingStorageAvailable: _ => true);
            var online = await service.IndexFolderAsync(root, true);
            var originalAssetId = online.Catalog.Assets.Single().Id;

            Directory.Delete(root, true);
            var missing = await service.ProbeStorageAvailabilityAsync();
            Equal(1, missing.MissingFolders.Count);
            Equal(0, missing.DisconnectedFolders.Count);
            Equal(IndexedFolderAvailability.Missing, missing.Catalog.Folders.Single().Availability);
            Equal(0, missing.Catalog.Assets.Length);
            Equal(0, missing.Catalog.Directories.Length);

            var unchanged = await service.ProbeStorageAvailabilityAsync();
            Equal(0, unchanged.MissingFolders.Count);

            Directory.CreateDirectory(nested);
            await File.WriteAllBytesAsync(file, [4, 5, 6]);
            var reconnected = await service.ProbeStorageAvailabilityAsync();
            Equal(1, reconnected.ReconnectedFolders.Count);

            var restored = await service.IndexFolderAsync(root, true);
            Equal(IndexedFolderAvailability.Online, restored.Folder.Availability);
            False(restored.Catalog.Assets.Single().Id == originalAssetId);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static async Task DeletedLocalFileIsRemovedFromConnectedRoot()
    {
        var root = Directory.CreateTempSubdirectory("ial-selftest-").FullName;
        var file = Path.Combine(root, "sample.mp3");

        try
        {
            await File.WriteAllBytesAsync(file, [1, 2, 3]);

            var store = new InMemoryLocalAssetCatalogStore();
            using var service = new LocalAssetIndexService(store);
            var first = await service.IndexFolderAsync(root, false);
            var original = first.Catalog.Assets.Single();
            await service.SetTagsAsync(original.Id, ["常用", "配乐"]);

            File.Delete(file);
            var missing = await service.IndexFolderAsync(root, false);
            Equal(IndexedFolderAvailability.Online, missing.Folder.Availability);
            Equal(0, missing.IndexedFileCount);
            Equal(0, missing.Issues.Count);
            Equal(0, missing.Catalog.Assets.Length);

            await File.WriteAllBytesAsync(file, [4, 5, 6]);
            var restoredCatalog = await service.IndexFolderAsync(root, false);
            var restored = restoredCatalog.Catalog.Assets.Single();
            Equal(LocalAssetAvailability.Available, restored.Availability);
            NotEqual(original.Id, restored.Id);
            Equal(0, restored.Tags.Length);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static async Task MovedLocalFileReplacesPriorPathIdentity()
    {
        var root = Directory.CreateTempSubdirectory("ial-selftest-").FullName;
        var originalPath = Path.Combine(root, "original.wav");
        var movedPath = Path.Combine(root, "moved.wav");

        try
        {
            await File.WriteAllBytesAsync(originalPath, [1, 2, 3]);

            var store = new InMemoryLocalAssetCatalogStore();
            using var service = new LocalAssetIndexService(store);
            var first = await service.IndexFolderAsync(root, false);
            var original = first.Catalog.Assets.Single();
            await service.SetTagsAsync(original.Id, ["原路径标签"]);

            File.Move(originalPath, movedPath);
            var movedCatalog = await service.IndexFolderAsync(root, false);
            Equal(1, movedCatalog.Catalog.Assets.Length);

            var moved = movedCatalog.Catalog.Assets.Single();
            Equal(movedPath, moved.FullPath);
            Equal(LocalAssetAvailability.Available, moved.Availability);
            NotEqual(original.Id, moved.Id);
            Equal(0, moved.Tags.Length);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static void LocalDragAndShellWiringStaysScoped()
    {
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

        Contains("x:Name=\"LocalFolderDropTarget\"", xaml);
        Contains("DragDrop.DragOver=\"LocalFolderDropTarget_OnDragOver\"", xaml);
        Contains("DragDrop.Drop=\"LocalFolderDropTarget_OnDrop\"", xaml);
        Contains("x:Name=\"LocalFileImportDropTarget\"", xaml);
        Contains("DragDrop.DragOver=\"LocalFileImportDropTarget_OnDragOver\"", xaml);
        Contains("DragDrop.Drop=\"LocalFileImportDropTarget_OnDrop\"", xaml);
        Contains("ContextRequested=\"LocalAsset_OnContextRequested\"", xaml);
        Contains("x:Name=\"SharedUploadDropTarget\"", xaml);
        Contains("DragDrop.DragOver=\"SharedUploadDropTarget_OnDragOver\"", xaml);
        Contains("DragDrop.Drop=\"SharedUploadDropTarget_OnDrop\"", xaml);
        False(xaml.Contains("LocalFolderSidebar_OnDragOver", StringComparison.Ordinal));
        False(xaml.Contains("AllLocalFolders_OnDragOver", StringComparison.Ordinal));
        Contains("DragDrop.DragOver=\"LocalFolderNode_OnDragOver\"", xaml);
        Contains("DragDrop.Drop=\"LocalFolderNode_OnDrop\"", xaml);
        False(xaml.Contains("SharedDropOverlay", StringComparison.Ordinal));
        False(source.Contains("SharedPage.AddHandler(", StringComparison.Ordinal));
        // Only page-wide drag interception is forbidden; a splitter may observe its own handled release.
        False(source.Contains("SharedPage.AddHandler(DragDrop", StringComparison.Ordinal));
        Contains("CloudFolderHeightSplitter.AddHandler(InputElement.PointerReleasedEvent", source);
        Contains("SharedUploadDropTarget_OnDragOver", source);
        Contains("SharedUploadDropTarget_OnDrop", source);
        False(xaml.Contains("LocalPage_OnDragOver", StringComparison.Ordinal));

        Contains("private sealed record DroppedPathSnapshot", source);
        Contains("private static DroppedPathSnapshot SnapshotDroppedPaths", source);
        Contains("private static string[] DroppedStoragePaths", source);
        Contains("dataTransfer.TryGetFiles()", source);
        Contains("eventArgs.DataTransfer.Contains(DataFormat.File)", source);
        Contains("ContainsFileTransfer(eventArgs)", source);
        Contains("new HashSet<string>(LocalPathComparer)", source);
        Contains("item.TryGetLocalPath()", source);
        Contains("paths.Add(Path.GetFullPath(path))", source);
        False(source.Contains("DroppedFolderPaths(eventArgs)", StringComparison.Ordinal));
        False(source.Contains("DroppedFilePaths(eventArgs)", StringComparison.Ordinal));
        foreach (var dropHandler in new[]
                 {
                     "LocalFolderDropTarget_OnDrop",
                     "LocalFileImportDropTarget_OnDrop",
                     "SharedUploadDropTarget_OnDrop"
                 })
        {
            var methodStart = source.IndexOf(
                $"private void {dropHandler}(",
                StringComparison.Ordinal);
            True(methodStart >= 0);
            var methodEnd = source.IndexOf("\n    private ", methodStart + 1, StringComparison.Ordinal);
            var methodSource = source[methodStart..(methodEnd < 0 ? source.Length : methodEnd)];
            Equal(
                1,
                methodSource.Split(
                    "SnapshotDroppedPaths(eventArgs)",
                    StringSplitOptions.None).Length - 1);
        }

        Contains("SelectedLocalFileImportTarget()", source);
        Contains("LocalFileImportDropTarget.IsEnabled = available", source);
        Contains("QueueLocalFileDrop(target, dropped.Files)", source);
        Contains("ResolvePendingLocalFolderDropAsync(pendingTransfer)", source);
        Contains("ResolvePendingLocalFileDropAsync(pendingTransfer, target)", source);
        Contains("PrepareDroppedCloudUploadSafelyAsync(pathSnapshot, targetFolderId)", source);
        Contains("_cloudUploadTransferGate.WaitAsync(token)", File.ReadAllText(Path.Combine(root, "src", "InternalAssetLibrary.Client", "MainWindow.Transfers.cs")));
        var taskSource = File.ReadAllText(Path.Combine(root, "src", "InternalAssetLibrary.Client", "MainWindow.Transfers.cs"));
        Contains("existing.Record.UserId == userId && existing.Record.ServerOrigin == origin", taskSource);
        Contains("existing.TokenProvider.AccessToken = _tokenProvider.AccessToken;", taskSource);
        Contains("job.TokenProvider, clientVersion: GetClientVersion()", taskSource);
        Contains("eventArgs.DragEffects = DragDropEffects.Copy;", source);
        Contains("CopyFileWithoutOverwriteAsync", source);
        Contains("FileMode.CreateNew", source);
        Contains("File.Move(temporaryPath, destinationPath, overwrite: false)", source);
        Contains("Arguments = $\"/select,\\\"{normalizedPath}\\\"\"", source);
    }

    private static void LibraryRefreshClearsPreviewProgress()
    {
        var session = new PreviewPlaybackSession();
        Equal(TimeSpan.Zero, session.Begin("asset-1"));
        session.End("asset-1", TimeSpan.FromSeconds(12));
        Equal(TimeSpan.FromSeconds(12), session.Begin("asset-1"));
        session.End("asset-1", TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(12));
        Equal(TimeSpan.Zero, session.Begin("asset-1"));
        session.End("asset-1", TimeSpan.FromSeconds(11.9), TimeSpan.FromSeconds(12));
        Equal(TimeSpan.Zero, session.Begin("asset-1"));

        var generation = session.Generation;
        session.ResetForLibraryRefresh();
        Equal(TimeSpan.Zero, session.GetPosition("asset-1"));
        Equal<string?>(null, session.ActiveAssetKey);
        Equal(generation + 1, session.Generation);
    }

    private static void AssertCsvEnvelope(byte[] bytes)
    {
        True(bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
        True(bytes.AsSpan().EndsWith("\r\n"u8));

        var text = Encoding.UTF8.GetString(bytes.AsSpan(3));
        True(text.StartsWith(MarkersCsv.Header + "\r\n", StringComparison.Ordinal));
        Equal(9, MarkersCsv.Header.Split(',').Length);

        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\n')
            {
                True(index > 0 && text[index - 1] == '\r');
            }
        }
    }

    private static string SamplePath(string fileName)
        => Path.Combine(RepositoryRoot(), "测试", fileName);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "InternalAssetLibrary.slnx")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new DirectoryNotFoundException("Could not locate the repository root.");
        }

        return directory.FullName;
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

    private static void NotEqual<T>(T expected, T actual)
    {
        if (EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected values to differ, but both were '{actual}'.");
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

    private static async Task<TException> ThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException exception)
        {
            return exception;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name} to be thrown.");
    }
}
