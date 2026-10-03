using System.Text.Json;
using InternalAssetLibrary.Server.Data;
using InternalAssetLibrary.Server.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

internal static class ServerSettingsSelfTests
{
    public static void SettingsPersistAndApplyWithoutReturningSecrets() =>
        SettingsPersistAndApplyWithoutReturningSecretsAsync().GetAwaiter().GetResult();

    public static void SettingsRejectUnsafeValuesAndAdminRoutesStayProtected()
    {
        var root = RepositoryRoot();
        var endpoints = File.ReadAllText(Path.Combine(root, "src", "InternalAssetLibrary.Server", "Api", "AdminAssetEndpoints.cs"));
        Contains("app.MapGet(\"/api/admin/settings\", GetServerSettingsAsync)", endpoints);
        Contains("app.MapPut(\"/api/admin/settings\", UpdateServerSettingsAsync)", endpoints);
        Contains("AccessControl.RequireAdmin(context);", endpoints);
        var editor = File.ReadAllText(Path.Combine(root, "src", "InternalAssetLibrary.Client", "Controls", "EditorWorkspaceView.axaml.cs"));
        var editorView = File.ReadAllText(Path.Combine(root, "src", "InternalAssetLibrary.Client", "Controls", "EditorWorkspaceView.axaml"));
        var adminView = File.ReadAllText(Path.Combine(root, "src", "InternalAssetLibrary.Client", "Controls", "AdminWorkspaceView.axaml"));
        var adminCode = File.ReadAllText(Path.Combine(root, "src", "InternalAssetLibrary.Client", "Controls", "AdminWorkspaceView.axaml.cs"));
        var mainWindow = File.ReadAllText(Path.Combine(root, "src", "InternalAssetLibrary.Client", "MainWindow.axaml.cs"));
        var serverProgram = File.ReadAllText(Path.Combine(root, "src", "InternalAssetLibrary.Server", "Program.cs"));
        var apiClient = File.ReadAllText(Path.Combine(root, "src", "InternalAssetLibrary.Client.Core", "Http", "AssetLibraryApiClient.Endpoints.cs"));
        var browserAdmin = File.ReadAllText(Path.Combine(root, "src", "InternalAssetLibrary.Server", "wwwroot", "app.js"));
        Contains("await _playback.PauseAsync();", editor);
        Contains("await _playback.SeekAsync(TimeSpan.Zero);", editor);
        Contains("x:Name=\"VolumeSlider\"", editorView);
        Contains("x:Name=\"VolumeInput\"", editorView);
        Contains("x:Name=\"AdminTabs\"", adminView);
        Contains("x:Name=\"OriginalQuotaBox\"", adminView);
        Contains("api.ListAdminUsersAsync()", adminCode);
        Contains("api.UpdateAdminServerSettingsAsync(request)", adminCode);
        Contains("GetLeftPart(UriPartial.Authority) + \"/admin\"", mainWindow);
        Contains("app.MapGet(\"/admin\"", serverProgram);
        False(serverProgram.Contains("app.UseDefaultFiles()", StringComparison.Ordinal));
        Contains("GetAsync<IReadOnlyList<ApiAdminUser>>(\"api/admin/users\"", apiClient);
        Contains("PutAsync<ApiServerSettings, ApiServerSettings>(\"api/admin/settings\"", apiClient);
        Contains("function isAdminRoute", browserAdmin);
        Contains("function parseAdminRoute", browserAdmin);
        Contains("function pathForView", browserAdmin);
        Contains("return '/admin';", browserAdmin);
        Contains("return '/admin/shared';", browserAdmin);
        Contains("return '/admin/member';", browserAdmin);
        Contains("/admin/menber", browserAdmin);
        Contains("/admin/management/settings", browserAdmin);
        Contains("window.addEventListener('popstate'", browserAdmin);
        Contains("signOut(false);", browserAdmin);
        Contains("当前账号没有管理后台权限", browserAdmin);
        Contains("if (error?.message) $('#loginError').textContent = error.message;", browserAdmin);
        False(browserAdmin.Contains("history.replaceState(null, '', '/')", StringComparison.Ordinal));
        False(browserAdmin.Contains("name === 'admin' ? '/admin' : '/'", StringComparison.Ordinal));
    }

    private static async Task SettingsPersistAndApplyWithoutReturningSecretsAsync()
    {
        var directory = Directory.CreateTempSubdirectory("ial-server-settings-").FullName;
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DevelopmentStorage:DataPath"] = Path.Combine(directory, "state.json"),
                    ["Library:OriginalQuotaBytes"] = (100L * 1024 * 1024 * 1024).ToString(),
                    ["Monitoring:MetricsToken"] = "do-not-return-this-token",
                    ["Cos:SecretId"] = "do-not-return-secret-id",
                    ["Cos:SecretKey"] = "do-not-return-secret-key"
                })
                .Build();
            using var store = new AtomicJsonDataStore(
                new TestHostEnvironment(directory), configuration, NullLogger<AtomicJsonDataStore>.Instance);
            await store.InitializeAsync();
            var service = new ServerSettingsService(store, configuration, NullLogger<ServerSettingsService>.Instance);
            await service.InitializeAsync();

            Equal(100L * 1024 * 1024 * 1024, service.Current.OriginalQuotaBytes);
            var updated = service.Current with { RecycleRetentionDays = 45, BackupsEnabled = false };
            var result = await service.UpdateAsync(ToUpdate(updated), Guid.NewGuid());
            Equal(45, result.RecycleRetentionDays);
            Equal(false, configuration.GetValue<bool>("Backups:Enabled"));
            Equal(45, configuration.GetValue<int>("Library:RecycleRetentionDays"));

            var persisted = await store.ReadAsync(state => state.ServerSettings);
            Equal(45, persisted!.RecycleRetentionDays);
            Equal(false, persisted.BackupsEnabled);
            var json = JsonSerializer.Serialize(result.ToResponse());
            False(json.Contains("SecretId", StringComparison.OrdinalIgnoreCase));
            False(json.Contains("SecretKey", StringComparison.OrdinalIgnoreCase));
            False(json.Contains("MetricsToken", StringComparison.OrdinalIgnoreCase));
            False(json.Contains("do-not-return", StringComparison.Ordinal));

            var invalid = ToUpdate(result) with { BackupLocalPath = "../../outside" };
            try
            {
                await service.UpdateAsync(invalid, Guid.NewGuid());
                throw new InvalidOperationException("Unsafe server setting was accepted.");
            }
            catch (InternalAssetLibrary.Server.Security.ApiException exception)
            {
                Equal("invalid_server_settings", exception.Code);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ServerSettingsUpdate ToUpdate(ServerSettingsSnapshot value) => new(
        value.OriginalQuotaBytes, value.AudioMaxBytes, value.ImageMaxBytes, value.VideoMaxBytes,
        value.ThumbnailMaxBytes, value.ProxyMaxBytes, value.LutMaxBytes, value.RecycleRetentionDays,
        value.AuditRetentionDays, value.DownloadLogRetentionDays, value.BackupsEnabled,
        value.BackupIntervalHours, value.BackupRetentionDays, value.BackupLocalPath,
        value.BackupObjectPrefix, value.CapacityWarningRatio, value.CapacityCriticalRatio,
        value.DerivativesEnabled, value.DerivativePollIntervalSeconds,
        value.DerivativeProcessTimeoutSeconds, value.ThumbnailMaxEdge, value.SessionLifetimeDays,
        value.LoginFailureLimit, value.LoginFailureWindowMinutes, value.LoginBlockMinutes,
        value.MultipartPartSizeBytes, value.UploadSessionLifetimeHours, value.SignedUrlLifetimeMinutes);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "InternalAssetLibrary.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static void Contains(string expected, string value)
    {
        if (!value.Contains(expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"Expected source to contain '{expected}'.");
    }

    private static void False(bool value)
    {
        if (value) throw new InvalidOperationException("Expected false.");
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected '{expected}', but was '{actual}'.");
    }

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "InternalAssetLibrary.SelfTests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
