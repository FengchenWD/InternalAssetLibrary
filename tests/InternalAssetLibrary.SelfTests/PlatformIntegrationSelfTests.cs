using InternalAssetLibrary.Client.Core.Platform;
using System.Text.Json;

internal static class PlatformIntegrationSelfTests
{
    public static void FileOpenArgumentsAndBrokerRemainBounded()
    {
        var first = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "素材 A.mp4"));
        var second = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "封面.png"));
        True(FileOpenCommandLine.TryParse(
            [FileOpenCommandLine.OpenOption, first, second, first],
            out var request,
            out var error));
        Null(error);
        Equal(2, request.FilePaths.Count);
        Equal(first, request.FilePaths[0]);
        Equal(second, request.FilePaths[1]);

        False(FileOpenCommandLine.TryParse(
            [FileOpenCommandLine.OpenOption, Path.Combine(Path.GetTempPath(), "project.psd")],
            out _,
            out _));
        False(FileOpenCommandLine.TryParse(["--unknown"], out _, out _));

        var broker = new FileOpenActivationBroker();
        var eventCount = 0;
        broker.ActivationAvailable += () => eventCount++;
        broker.Publish(request);
        Equal(1, eventCount);
        True(broker.TryTake(out var queued));
        Equal(2, queued!.FilePaths.Count);
        False(broker.TryTake(out _));
    }

    public static async Task SingleInstanceForwardsMultipleFiles()
    {
        var applicationId = $"FengchenWD.InternalAssetLibrary.SelfTest.{Guid.NewGuid():N}";
        using var primary = new SingleInstanceCoordinator(applicationId);
        using var secondary = new SingleInstanceCoordinator(applicationId);
        True(primary.TryBecomePrimary());
        False(secondary.TryBecomePrimary());

        var received = new TaskCompletionSource<FileOpenActivationRequest>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        primary.ActivationReceived += request => received.TrySetResult(request);
        primary.StartListening();

        var expected = new FileOpenActivationRequest(
        [
            Path.GetFullPath(Path.Combine(Path.GetTempPath(), "视频 1.mp4")),
            Path.GetFullPath(Path.Combine(Path.GetTempPath(), "音频 2.flac"))
        ]);
        True(await secondary.ForwardAsync(expected, TimeSpan.FromSeconds(3)));
        var actual = await received.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Equal(expected.FilePaths.Count, actual.FilePaths.Count);
        Equal(expected.FilePaths[0], actual.FilePaths[0]);
        Equal(expected.FilePaths[1], actual.FilePaths[1]);
    }

    public static void WindowsRegistrationPlanIsUserChoiceSafe()
    {
        var executable = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "播放器 目录", "InternalAssetLibrary.Client.exe"));
        var plan = WindowsFileAssociationRegistrationPlan.Create(executable);
        Equal(executable, plan.ExecutablePath);
        True(plan.Values.Any(value =>
            value.SubKey == @"Software\RegisteredApplications" &&
            value.ValueName == WindowsFileAssociationRegistrationPlan.RegisteredApplicationName &&
            value.ValueData == WindowsFileAssociationRegistrationPlan.CapabilitiesSubKey));
        var openCommands = plan.Values
            .Where(value => value.SubKey.EndsWith(@"shell\open\command", StringComparison.Ordinal))
            .ToArray();
        Equal(4, openCommands.Length);
        True(openCommands.All(value => value.ValueData == $"\"{executable}\" --open \"%1\""));
        False(plan.Values.Any(value => value.SubKey.Contains("UserChoice", StringComparison.OrdinalIgnoreCase)));
        Equal(
            DefaultPlayerFileAssociations.All.Count,
            DefaultPlayerFileAssociations.All.Select(value => value.Extension).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        True(DefaultPlayerFileAssociations.All.Any(value => value.Extension == ".mp4"));
        True(DefaultPlayerFileAssociations.All.Any(value => value.Extension == ".svg"));
        False(DefaultPlayerFileAssociations.All.Any(value =>
            value.Extension is ".psd" or ".ai" or ".eps"));
        foreach (var association in DefaultPlayerFileAssociations.All)
        {
            True(plan.Values.Any(value =>
                value.SubKey == $@"{WindowsFileAssociationRegistrationPlan.CapabilitiesSubKey}\FileAssociations" &&
                value.ValueName == association.Extension &&
                value.ValueData == association.ProgId));
            True(plan.Values.Any(value =>
                value.SubKey == $@"Software\Classes\Applications\{WindowsFileAssociationRegistrationPlan.ExecutableName}\SupportedTypes" &&
                value.ValueName == association.Extension));
        }

        True(new WindowsDefaultPlayerRegistrationService(
            WindowsDefaultPlayerRegistrationService.StableExecutablePath).UsesStableInstallPath);
        Equal(
            executable,
            WindowsDefaultPlayerRegistrationService.ExtractExecutablePath($"\"{executable}\",0"));
        Equal(
            executable,
            WindowsDefaultPlayerRegistrationService.ExtractExecutablePath(executable));
        Null(WindowsDefaultPlayerRegistrationService.ExtractExecutablePath("\"unterminated"));
        True(WindowsDefaultPlayerRegistrationService.MatchesInstalledExecutablePath(
            executable,
            executable.ToUpperInvariant()));
        False(WindowsDefaultPlayerRegistrationService.MatchesInstalledExecutablePath(
            executable,
            Path.Combine(Path.GetTempPath(), "other", "InternalAssetLibrary.Client.exe")));
        False(WindowsDefaultPlayerRegistrationService.MatchesInstalledExecutablePath(executable, null));

        Equal(
            "ms-settings:defaultapps",
            WindowsDefaultPlayerRegistrationService.GetSettingsUri(new Version(10, 0, 19045)).AbsoluteUri);
        Equal(
            "ms-settings:defaultapps?registeredAppUser=FengchenWD.InternalAssetLibrary",
            WindowsDefaultPlayerRegistrationService.GetSettingsUri(new Version(10, 0, 22621)).AbsoluteUri);

        var repositoryRoot = RepositoryRoot();
        var appSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "InternalAssetLibrary.Client",
            "App.axaml.cs"));
        Contains("ShutdownMode.OnMainWindowClose", appSource);
        var installer = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "packaging",
            "windows",
            "InternalAssetLibrary.Client.iss"));
        foreach (var association in DefaultPlayerFileAssociations.All)
        {
            Contains(
                $"Subkey: \"{{#CapabilitiesKey}}\\FileAssociations\"; ValueType: string; ValueName: \"{association.Extension}\"; ValueData: \"{association.ProgId}\"",
                installer);
            Contains(
                $"Subkey: \"Software\\Classes\\Applications\\{{#AppExeName}}\\SupportedTypes\"; ValueType: string; ValueName: \"{association.Extension}\"; ValueData: \"\"",
                installer);
            Equal(2, CountOccurrences(installer, $"ValueName: \"{association.Extension}\""));
        }

        Contains("AppId={{D1B775BE-9677-4C86-B128-211433AF9F38}", installer);
        Contains("DefaultDirName={localappdata}\\Programs\\FengchenWD\\InternalAssetLibrary", installer);
        Contains("DisableDirPage=no", installer);
        False(installer.Contains("DisableDirPage=yes", StringComparison.Ordinal));
        Contains("ShowLanguageDialog=no", installer);
        Contains("UsePreviousLanguage=no", installer);
        Contains("[Languages]", installer);
        Contains(
            "Name: \"chinesesimplified\"; MessagesFile: \"languages\\ChineseSimplified.isl\"",
            installer);
        Contains("PrivilegesRequired=lowest", installer);
        Contains("ArchitecturesAllowed=x64compatible", installer);
        Contains("MinVersion=10.0.19045", installer);
        Contains("UsePreviousAppDir=yes", installer);
        Contains("ValueName: \"InstallLocation\"; ValueData: \"{app}\"", installer);
        Contains("ValueName: \"ExecutablePath\"; ValueData: \"{app}\\{#AppExeName}\"", installer);
        Contains("CloseApplications=yes", installer);
        Contains("ChangesAssociations=yes", installer);
        Contains("ValueType: none; Flags: deletekey", installer);
        Contains("Flags: uninsdeletevalue", installer);
        Contains("Flags: uninsdeletekey", installer);
        Contains("[InstallDelete]", installer);
        Contains("[UninstallDelete]", installer);
        Contains("Name: \"{app}\\runtime\"", installer);
        Contains("Source: \"{#SourceDirectory}\\runtime\\*\"", installer);
        Contains("Source: \"{#SourceDirectory}\\Licenses\\*\"", installer);
        Contains("Source: \"{#SourceDirectory}\\THIRD_PARTY_NOTICES.md\"", installer);
        False(installer.Contains("ValueName: \".psd\"", StringComparison.OrdinalIgnoreCase));
        False(installer.Contains("ValueName: \".ai\"", StringComparison.OrdinalIgnoreCase));
        False(installer.Contains("ValueName: \".eps\"", StringComparison.OrdinalIgnoreCase));
        False(installer.Contains("Subkey: \"Software\\Classes\\.mp3\"", StringComparison.OrdinalIgnoreCase));
        False(installer.Contains("UserChoice", StringComparison.OrdinalIgnoreCase));

        var installerChinese = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "packaging",
            "windows",
            "languages",
            "ChineseSimplified.isl"));
        Contains("Modified from Inno Setup 6.7.3 Default.isl", installerChinese);
        Contains("42a5f6f7dbbddf26cc278f67db5d894235ce1d126a6856702e31bd02023a1316", installerChinese);
        Contains("LanguageName=简体中文", installerChinese);
        Contains("LanguageID=$0804", installerChinese);
        Contains("SetupAppTitle=安装", installerChinese);
        Contains("WizardSelectDir=选择安装位置", installerChinese);
        Contains("ButtonInstall=安装(&I)", installerChinese);
        Contains("ConfirmUninstall=确定要从计算机中完整移除 %1 及其所有组件吗？", installerChinese);

        var publishScript = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "publish-client.ps1"));
        Contains("deliveryType = 'installer'", publishScript);
        False(publishScript.Contains("PortableOnly", StringComparison.Ordinal));
        False(publishScript.Contains("Portable.zip", StringComparison.Ordinal));
        Contains("正式客户端发布必须生成安装包", publishScript);
        Contains("--self-contained", publishScript);
        Contains("--property:PublishSingleFile=true", publishScript);
        Contains("--property:PublishTrimmed=false", publishScript);
        Equal(4, CountOccurrences(publishScript, "--property:UsePublishLockFile=true"));
        Contains("Avalonia.Diagnostics.dll", publishScript);
        Contains("artifacts\\media-runtime\\win-x64", publishScript);
        Contains("Assert-Amd64PeFile", publishScript);
        Contains("$_.Extension -eq '.dll'", publishScript);
        Contains("artifacts\\tools\\inno-setup\\ISCC.exe", publishScript);
        Contains("[Environment+SpecialFolder]::LocalApplicationData", publishScript);
        Contains("VersionInfo.ProductVersion.Trim()", publishScript);
        Contains("--enable-nonfree", publishScript);
        Contains("runtime-manifest.json", publishScript);
        Contains("$runtimeEntries | ForEach-Object { $_['sizeBytes'] } | Measure-Object -Sum", publishScript);
        Contains("requiresRuntimeDirectory = $true", publishScript);
        Contains("THIRD_PARTY_NOTICES.md", publishScript);
        Contains("检测到预存的正式版本源码包", publishScript);
        Contains("$temporarySourcePackage", publishScript);
        Contains("defaultInstallPath = '%LocalAppData%\\Programs\\FengchenWD\\InternalAssetLibrary'", publishScript);
        Contains("installDirectorySelectable = $true", publishScript);
        Contains("language = 'zh-CN'", publishScript);
        False(publishScript.Contains("stableInstallPath", StringComparison.Ordinal));

        var directoryBuildProps = File.ReadAllText(Path.Combine(repositoryRoot, "Directory.Build.props"));
        Equal(1, CountOccurrences(directoryBuildProps, "<NuGetLockFilePath"));
        Contains(
            "<NuGetLockFilePath Condition=\"'$(UsePublishLockFile)' == 'true'\">$(MSBuildProjectDirectory)\\packages.publish-win-x64.lock.json</NuGetLockFilePath>",
            directoryBuildProps);

        string[] publishGraphProjects =
        [
            "InternalAssetLibrary.Client",
            "InternalAssetLibrary.Client.Core",
            "InternalAssetLibrary.Contracts",
            "InternalAssetLibrary.Core"
        ];
        foreach (var projectName in publishGraphProjects)
        {
            var projectDirectory = Path.Combine(repositoryRoot, "src", projectName);
            AssertLockFileContext(Path.Combine(projectDirectory, "packages.lock.json"), isPublishLock: false);
            AssertLockFileContext(
                Path.Combine(projectDirectory, "packages.publish-win-x64.lock.json"),
                isPublishLock: true);
        }

        var sourcePackageScript = File.ReadAllText(Path.Combine(RepositoryRoot(), "scripts", "package-source.ps1"));
        Contains("'packaging'", sourcePackageScript);
        Contains("'deployment'", sourcePackageScript);
        Contains("'assets'", sourcePackageScript);
        Contains("'CHANGELOG.md'", sourcePackageScript);
        Contains("deployment/updates/(?!\\.gitkeep$)", sourcePackageScript);

        var updatePublishScript = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "deployment",
            "scripts",
            "publish-client-update.ps1"));
        Contains("manifest.json", updatePublishScript);
        Contains("correspondingSource", updatePublishScript);
        Contains("-source.zip", updatePublishScript);
        Contains("$versionedInstallerName", updatePublishScript);
        Contains("$manifestBackup = Join-Path $updatesDirectory", updatePublishScript);
        Contains("[IO.File]::Replace($incomingManifest, $manifestPath, $manifestBackup)", updatePublishScript);
        Contains("@($incomingInstaller, $incomingManifest, $manifestBackup)", updatePublishScript);
        Contains("installerSha256 = $installerHash", updatePublishScript);

        using var developmentSettings = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "src",
            "InternalAssetLibrary.Server",
            "appsettings.Development.json")));
        Equal(
            "0.2.0-preview.6.3",
            developmentSettings.RootElement
                .GetProperty("ClientUpdates")
                .GetProperty("MinimumCompatibleVersion")
                .GetString());
    }

    private static void AssertLockFileContext(string path, bool isPublishLock)
    {
        True(File.Exists(path));
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var dependencies = document.RootElement.GetProperty("dependencies");
        True(dependencies.TryGetProperty("net10.0", out var baseTarget));

        var hasWinX64Target = dependencies.TryGetProperty("net10.0/win-x64", out _);
        var hasDirectIlLink =
            baseTarget.TryGetProperty("Microsoft.NET.ILLink.Tasks", out var ilLink) &&
            ilLink.TryGetProperty("type", out var type) &&
            string.Equals(type.GetString(), "Direct", StringComparison.Ordinal);

        Equal(isPublishLock, hasWinX64Target);
        Equal(isPublishLock, hasDirectIlLink);
    }

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(search, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += search.Length;
        }

        return count;
    }

    private static void Contains(string expectedSubstring, string actual)
    {
        if (!actual.Contains(expectedSubstring, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected text to contain '{expectedSubstring}'.");
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

    private static void Null(object? value)
    {
        if (value is not null)
        {
            throw new InvalidOperationException($"Expected null, got '{value}'.");
        }
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "InternalAssetLibrary.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
