using InternalAssetLibrary.Client.Core.Settings;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class ClientCloseBehaviorSelfTests
{
    public static void DefaultsPromptForMinimizeAndSelectionsAreDeterministic()
    {
        var defaults = new ClientSettings().ValidateAndNormalize();
        Equal(ApplicationCloseAction.MinimizeToTray, defaults.CloseAction);
        True(defaults.AskOnClose);
        Equal(CloseRequestDecision.ShowPrompt, ClientCloseBehavior.Decide(defaults));

        var oneTimeExit = ClientCloseBehavior.ResolvePrompt(
            defaults,
            new ClosePromptSelection(ApplicationCloseAction.ExitApplication, DoNotAskAgain: false));
        Equal(CloseRequestDecision.ExitApplication, oneTimeExit.Decision);
        False(oneTimeExit.ShouldSaveSettings);
        True(ReferenceEquals(defaults, oneTimeExit.Settings));

        var rememberedExit = ClientCloseBehavior.ResolvePrompt(
            defaults,
            new ClosePromptSelection(ApplicationCloseAction.ExitApplication, DoNotAskAgain: true));
        Equal(CloseRequestDecision.ExitApplication, rememberedExit.Decision);
        True(rememberedExit.ShouldSaveSettings);
        False(rememberedExit.Settings.AskOnClose);
        Equal(ApplicationCloseAction.ExitApplication, rememberedExit.Settings.CloseAction);
        Equal(CloseRequestDecision.ExitApplication, ClientCloseBehavior.Decide(rememberedExit.Settings));

        var rememberedMinimize = ClientCloseBehavior.ResolvePrompt(
            defaults,
            new ClosePromptSelection(ApplicationCloseAction.MinimizeToTray, DoNotAskAgain: true));
        Equal(CloseRequestDecision.MinimizeToTray, rememberedMinimize.Decision);
        Equal(CloseRequestDecision.MinimizeToTray, ClientCloseBehavior.Decide(rememberedMinimize.Settings));
    }

    public static async Task ClosePreferencesRoundTripAndLegacyJsonUsesSafeDefaults()
    {
        var root = Directory.CreateTempSubdirectory("ial-close-settings-").FullName;
        var settingsPath = Path.Combine(root, "settings.json");
        try
        {
            var settings = new ClientSettings
            {
                CloseAction = ApplicationCloseAction.ExitApplication,
                AskOnClose = false
            }.ValidateAndNormalize();

            using (var store = new JsonClientSettingsStore(settingsPath))
            {
                await store.SaveAsync(settings);
                var restored = await store.LoadAsync();
                Equal(ApplicationCloseAction.ExitApplication, restored.CloseAction);
                False(restored.AskOnClose);
            }

            await File.WriteAllTextAsync(settingsPath, "{\"schemaVersion\":1}");
            using var legacyStore = new JsonClientSettingsStore(settingsPath);
            var legacy = await legacyStore.LoadAsync();
            Equal(ApplicationCloseAction.MinimizeToTray, legacy.CloseAction);
            True(legacy.AskOnClose);
            True(legacy.FollowSystemAccent);

            var legacyCustomJson = JsonSerializer.Serialize(
                new ClientSettings
                {
                    DarkColors = DefaultColorPalettes.Dark with { Accent = "#FF006E" }
                },
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var legacyCustomObject = JsonNode.Parse(legacyCustomJson)!.AsObject();
            legacyCustomObject.Remove("followSystemAccent");
            await File.WriteAllTextAsync(settingsPath, legacyCustomObject.ToJsonString());
            var legacyCustom = await legacyStore.LoadAsync();
            False(legacyCustom.FollowSystemAccent);

            var restoredDefaults = settings.RestorePreferenceDefaults();
            Equal(ApplicationCloseAction.MinimizeToTray, restoredDefaults.CloseAction);
            True(restoredDefaults.AskOnClose);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    public static void TrayPromptSettingsAndSafeShutdownRemainWired()
    {
        var root = RepositoryRoot();
        var clientDirectory = Path.Combine(root, "src", "InternalAssetLibrary.Client");
        var xaml = File.ReadAllText(Path.Combine(clientDirectory, "MainWindow.axaml"));
        var source = File.ReadAllText(Path.Combine(clientDirectory, "MainWindow.axaml.cs"));
        var traySource = File.ReadAllText(Path.Combine(clientDirectory, "Services", "TrayService.cs"));
        var dialogXaml = File.ReadAllText(Path.Combine(clientDirectory, "CloseActionPromptWindow.axaml"));

        Contains("x:Name=\"CloseActionPicker\"", xaml);
        Contains("x:Name=\"AskOnCloseToggle\"", xaml);
        Contains("CloseBehaviorSettings_OnChanged", xaml);
        Contains("ClientCloseBehavior.Decide(_settings)", source);
        Contains("ShowDialog<ClosePromptSelection?>(this)", source);
        Contains("resolution.ShouldSaveSettings", source);
        Contains("_trayService.MinimizeToTray()", source);
        Contains("await ShutdownAsync();", source);
        Contains("private async Task ShutdownAsync()", source);
        Contains("if (_shutdownStarted || _shutdownCompleted)", source);
        Contains("_trayService.Dispose();", source);
        Contains("RequestApplicationExit();", source);
        Contains("TrayIcon.SetIcons(_application, icons)", traySource);
        Contains("public void UpdateText(", traySource);
        Contains("ExitRequested", traySource);
        Contains("x:Name=\"MinimizeToTrayOption\"", dialogXaml);
        Contains("x:Name=\"ExitApplicationOption\"", dialogXaml);
        Contains("x:Name=\"DoNotAskAgainCheckBox\"", dialogXaml);
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

    private static void True(bool value)
    {
        if (!value)
        {
            throw new InvalidOperationException("Expected true.");
        }
    }

    private static void False(bool value) => True(!value);

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
        }
    }
}
