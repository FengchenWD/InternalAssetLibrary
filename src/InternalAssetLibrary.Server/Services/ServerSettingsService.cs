using System.Globalization;
using InternalAssetLibrary.Server.Api;
using InternalAssetLibrary.Server.Data;
using InternalAssetLibrary.Server.Security;

namespace InternalAssetLibrary.Server.Services;

internal sealed record ServerSettingsUpdate(
    long OriginalQuotaBytes,
    long AudioMaxBytes,
    long ImageMaxBytes,
    long VideoMaxBytes,
    long ThumbnailMaxBytes,
    long ProxyMaxBytes,
    long LutMaxBytes,
    int RecycleRetentionDays,
    int AuditRetentionDays,
    int DownloadLogRetentionDays,
    bool BackupsEnabled,
    int BackupIntervalHours,
    int BackupRetentionDays,
    string BackupLocalPath,
    string BackupObjectPrefix,
    double CapacityWarningRatio,
    double CapacityCriticalRatio,
    bool DerivativesEnabled,
    int DerivativePollIntervalSeconds,
    int DerivativeProcessTimeoutSeconds,
    int ThumbnailMaxEdge,
    int SessionLifetimeDays,
    int LoginFailureLimit,
    int LoginFailureWindowMinutes,
    int LoginBlockMinutes,
    int MultipartPartSizeBytes,
    int UploadSessionLifetimeHours,
    int SignedUrlLifetimeMinutes);

internal sealed record ServerSettingsSnapshot(
    long OriginalQuotaBytes,
    long AudioMaxBytes,
    long ImageMaxBytes,
    long VideoMaxBytes,
    long ThumbnailMaxBytes,
    long ProxyMaxBytes,
    long LutMaxBytes,
    int RecycleRetentionDays,
    int AuditRetentionDays,
    int DownloadLogRetentionDays,
    bool BackupsEnabled,
    int BackupIntervalHours,
    int BackupRetentionDays,
    string BackupLocalPath,
    string BackupObjectPrefix,
    double CapacityWarningRatio,
    double CapacityCriticalRatio,
    bool DerivativesEnabled,
    int DerivativePollIntervalSeconds,
    int DerivativeProcessTimeoutSeconds,
    int ThumbnailMaxEdge,
    int SessionLifetimeDays,
    int LoginFailureLimit,
    int LoginFailureWindowMinutes,
    int LoginBlockMinutes,
    int MultipartPartSizeBytes,
    int UploadSessionLifetimeHours,
    int SignedUrlLifetimeMinutes)
{
    public object ToResponse() => new
    {
        originalQuotaBytes = OriginalQuotaBytes,
        audioMaxBytes = AudioMaxBytes,
        imageMaxBytes = ImageMaxBytes,
        videoMaxBytes = VideoMaxBytes,
        thumbnailMaxBytes = ThumbnailMaxBytes,
        proxyMaxBytes = ProxyMaxBytes,
        lutMaxBytes = LutMaxBytes,
        recycleRetentionDays = RecycleRetentionDays,
        auditRetentionDays = AuditRetentionDays,
        downloadLogRetentionDays = DownloadLogRetentionDays,
        backupsEnabled = BackupsEnabled,
        backupIntervalHours = BackupIntervalHours,
        backupRetentionDays = BackupRetentionDays,
        backupLocalPath = BackupLocalPath,
        backupObjectPrefix = BackupObjectPrefix,
        capacityWarningRatio = CapacityWarningRatio,
        capacityCriticalRatio = CapacityCriticalRatio,
        derivativesEnabled = DerivativesEnabled,
        derivativePollIntervalSeconds = DerivativePollIntervalSeconds,
        derivativeProcessTimeoutSeconds = DerivativeProcessTimeoutSeconds,
        thumbnailMaxEdge = ThumbnailMaxEdge,
        sessionLifetimeDays = SessionLifetimeDays,
        loginFailureLimit = LoginFailureLimit,
        loginFailureWindowMinutes = LoginFailureWindowMinutes,
        loginBlockMinutes = LoginBlockMinutes,
        multipartPartSizeBytes = MultipartPartSizeBytes,
        uploadSessionLifetimeHours = UploadSessionLifetimeHours,
        signedUrlLifetimeMinutes = SignedUrlLifetimeMinutes
    };
}

internal sealed class ServerSettingsService(
    IAppDataStore store,
    IConfiguration configuration,
    ILogger<ServerSettingsService> logger)
{
    private readonly object _sync = new();
    private ServerSettingsSnapshot? _current;
    private TaskCompletionSource _backupChange = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task BackupSettingsChanged { get { lock (_sync) return _backupChange.Task; } }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await store.UpdateAsync(state =>
        {
            if (state.ServerSettings is null || !state.ServerSettings.Initialized)
            {
                state.ServerSettings = FromConfiguration(configuration);
                state.ServerSettings.Initialized = true;
            }

            return ToSnapshot(state.ServerSettings);
        }, cancellationToken);
        ApplyConfiguration(snapshot);
        logger.LogInformation("Runtime server settings loaded from the application state.");
    }

    public ServerSettingsSnapshot Current
    {
        get
        {
            lock (_sync)
            {
                return _current ?? throw new InvalidOperationException("Server settings have not been initialized.");
            }
        }
    }

    public async Task<ServerSettingsSnapshot> UpdateAsync(
        ServerSettingsUpdate update,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        Validate(update);
        var snapshot = await store.UpdateAsync(state =>
        {
            var settings = state.ServerSettings ?? FromConfiguration(configuration);
            Apply(settings, update);
            settings.Initialized = true;
            ApiCommon.Audit(state, actorUserId, "admin.server-settings.updated", "server-settings", null);
            return ToSnapshot(settings);
        }, cancellationToken);
        ApplyConfiguration(snapshot);
        return snapshot;
    }

    private static ServerSettingsRecord FromConfiguration(IConfiguration configuration) => new()
    {
        OriginalQuotaBytes = configuration.GetValue("Library:OriginalQuotaBytes", 100L * 1024 * 1024 * 1024),
        AudioMaxBytes = configuration.GetValue("Library:AudioMaxBytes", 5L * 1024 * 1024 * 1024),
        ImageMaxBytes = configuration.GetValue("Library:ImageMaxBytes", 5L * 1024 * 1024 * 1024),
        VideoMaxBytes = configuration.GetValue("Library:VideoMaxBytes", 15L * 1024 * 1024 * 1024),
        ThumbnailMaxBytes = configuration.GetValue("Library:ThumbnailMaxBytes", 10L * 1024 * 1024),
        ProxyMaxBytes = configuration.GetValue("Library:ProxyMaxBytes", 5L * 1024 * 1024 * 1024),
        LutMaxBytes = configuration.GetValue("Library:LutMaxBytes", 16L * 1024 * 1024),
        RecycleRetentionDays = configuration.GetValue("Library:RecycleRetentionDays", 15),
        AuditRetentionDays = configuration.GetValue("Library:AuditRetentionDays", 30),
        DownloadLogRetentionDays = configuration.GetValue("Library:DownloadLogRetentionDays", 10),
        BackupsEnabled = configuration.GetValue("Backups:Enabled", true),
        BackupIntervalHours = configuration.GetValue("Backups:IntervalHours", 24),
        BackupRetentionDays = configuration.GetValue("Backups:RetentionDays", 30),
        BackupLocalPath = configuration["Backups:LocalPath"] ?? "App_Data/backups",
        BackupObjectPrefix = configuration["Backups:ObjectPrefix"] ?? "backups/sqlite",
        CapacityWarningRatio = configuration.GetValue("Monitoring:CapacityWarningRatio", 0.85),
        CapacityCriticalRatio = configuration.GetValue("Monitoring:CapacityCriticalRatio", 0.95),
        DerivativesEnabled = configuration.GetValue("Derivatives:Enabled", true),
        DerivativePollIntervalSeconds = configuration.GetValue("Derivatives:PollIntervalSeconds", 30),
        DerivativeProcessTimeoutSeconds = configuration.GetValue("Derivatives:ProcessTimeoutSeconds", 300),
        ThumbnailMaxEdge = configuration.GetValue("Derivatives:ThumbnailMaxEdge", 640),
        SessionLifetimeDays = configuration.GetValue("Security:SessionLifetimeDays", 7),
        LoginFailureLimit = configuration.GetValue("Security:LoginFailureLimit", 5),
        LoginFailureWindowMinutes = configuration.GetValue("Security:LoginFailureWindowMinutes", 15),
        LoginBlockMinutes = configuration.GetValue("Security:LoginBlockMinutes", 15),
        MultipartPartSizeBytes = configuration.GetValue("Cos:MultipartPartSizeBytes", 16 * 1024 * 1024),
        UploadSessionLifetimeHours = configuration.GetValue("Cos:UploadSessionLifetimeHours", 24),
        SignedUrlLifetimeMinutes = configuration.GetValue("Cos:SignedUrlLifetimeMinutes", 15)
    };

    private static ServerSettingsSnapshot ToSnapshot(ServerSettingsRecord settings) => new(
        settings.OriginalQuotaBytes,
        settings.AudioMaxBytes,
        settings.ImageMaxBytes,
        settings.VideoMaxBytes,
        settings.ThumbnailMaxBytes,
        settings.ProxyMaxBytes,
        settings.LutMaxBytes,
        settings.RecycleRetentionDays,
        settings.AuditRetentionDays,
        settings.DownloadLogRetentionDays,
        settings.BackupsEnabled,
        settings.BackupIntervalHours,
        settings.BackupRetentionDays,
        settings.BackupLocalPath,
        settings.BackupObjectPrefix,
        settings.CapacityWarningRatio,
        settings.CapacityCriticalRatio,
        settings.DerivativesEnabled,
        settings.DerivativePollIntervalSeconds,
        settings.DerivativeProcessTimeoutSeconds,
        settings.ThumbnailMaxEdge,
        settings.SessionLifetimeDays,
        settings.LoginFailureLimit,
        settings.LoginFailureWindowMinutes,
        settings.LoginBlockMinutes,
        settings.MultipartPartSizeBytes,
        settings.UploadSessionLifetimeHours,
        settings.SignedUrlLifetimeMinutes);

    private void ApplyConfiguration(ServerSettingsSnapshot settings)
    {
        Set("Library:OriginalQuotaBytes", settings.OriginalQuotaBytes);
        Set("Library:AudioMaxBytes", settings.AudioMaxBytes);
        Set("Library:ImageMaxBytes", settings.ImageMaxBytes);
        Set("Library:VideoMaxBytes", settings.VideoMaxBytes);
        Set("Library:ThumbnailMaxBytes", settings.ThumbnailMaxBytes);
        Set("Library:ProxyMaxBytes", settings.ProxyMaxBytes);
        Set("Library:LutMaxBytes", settings.LutMaxBytes);
        Set("Library:RecycleRetentionDays", settings.RecycleRetentionDays);
        Set("Library:AuditRetentionDays", settings.AuditRetentionDays);
        Set("Library:DownloadLogRetentionDays", settings.DownloadLogRetentionDays);
        Set("Backups:Enabled", settings.BackupsEnabled);
        Set("Backups:IntervalHours", settings.BackupIntervalHours);
        Set("Backups:RetentionDays", settings.BackupRetentionDays);
        Set("Backups:LocalPath", settings.BackupLocalPath);
        Set("Backups:ObjectPrefix", settings.BackupObjectPrefix);
        Set("Monitoring:CapacityWarningRatio", settings.CapacityWarningRatio);
        Set("Monitoring:CapacityCriticalRatio", settings.CapacityCriticalRatio);
        Set("Derivatives:Enabled", settings.DerivativesEnabled);
        Set("Derivatives:PollIntervalSeconds", settings.DerivativePollIntervalSeconds);
        Set("Derivatives:ProcessTimeoutSeconds", settings.DerivativeProcessTimeoutSeconds);
        Set("Derivatives:ThumbnailMaxEdge", settings.ThumbnailMaxEdge);
        Set("Security:SessionLifetimeDays", settings.SessionLifetimeDays);
        Set("Security:LoginFailureLimit", settings.LoginFailureLimit);
        Set("Security:LoginFailureWindowMinutes", settings.LoginFailureWindowMinutes);
        Set("Security:LoginBlockMinutes", settings.LoginBlockMinutes);
        Set("Cos:MultipartPartSizeBytes", settings.MultipartPartSizeBytes);
        Set("Cos:UploadSessionLifetimeHours", settings.UploadSessionLifetimeHours);
        Set("Cos:SignedUrlLifetimeMinutes", settings.SignedUrlLifetimeMinutes);
        lock (_sync)
        {
            var old = _current;
            _current = settings;
            if (old is not null && (old.BackupsEnabled != settings.BackupsEnabled ||
                old.BackupIntervalHours != settings.BackupIntervalHours || old.BackupRetentionDays != settings.BackupRetentionDays ||
                old.BackupLocalPath != settings.BackupLocalPath || old.BackupObjectPrefix != settings.BackupObjectPrefix))
            {
                var changed = _backupChange;
                _backupChange = new(TaskCreationOptions.RunContinuationsAsynchronously);
                changed.TrySetResult();
            }
        }
    }

    private void Set(string key, object value) => configuration[key] = Convert.ToString(value, CultureInfo.InvariantCulture);

    private static void Apply(ServerSettingsRecord settings, ServerSettingsUpdate update)
    {
        settings.OriginalQuotaBytes = update.OriginalQuotaBytes;
        settings.AudioMaxBytes = update.AudioMaxBytes;
        settings.ImageMaxBytes = update.ImageMaxBytes;
        settings.VideoMaxBytes = update.VideoMaxBytes;
        settings.ThumbnailMaxBytes = update.ThumbnailMaxBytes;
        settings.ProxyMaxBytes = update.ProxyMaxBytes;
        settings.LutMaxBytes = update.LutMaxBytes;
        settings.RecycleRetentionDays = update.RecycleRetentionDays;
        settings.AuditRetentionDays = update.AuditRetentionDays;
        settings.DownloadLogRetentionDays = update.DownloadLogRetentionDays;
        settings.BackupsEnabled = update.BackupsEnabled;
        settings.BackupIntervalHours = update.BackupIntervalHours;
        settings.BackupRetentionDays = update.BackupRetentionDays;
        settings.BackupLocalPath = update.BackupLocalPath.Trim();
        settings.BackupObjectPrefix = update.BackupObjectPrefix.Trim().Trim('/');
        settings.CapacityWarningRatio = update.CapacityWarningRatio;
        settings.CapacityCriticalRatio = update.CapacityCriticalRatio;
        settings.DerivativesEnabled = update.DerivativesEnabled;
        settings.DerivativePollIntervalSeconds = update.DerivativePollIntervalSeconds;
        settings.DerivativeProcessTimeoutSeconds = update.DerivativeProcessTimeoutSeconds;
        settings.ThumbnailMaxEdge = update.ThumbnailMaxEdge;
        settings.SessionLifetimeDays = update.SessionLifetimeDays;
        settings.LoginFailureLimit = update.LoginFailureLimit;
        settings.LoginFailureWindowMinutes = update.LoginFailureWindowMinutes;
        settings.LoginBlockMinutes = update.LoginBlockMinutes;
        settings.MultipartPartSizeBytes = update.MultipartPartSizeBytes;
        settings.UploadSessionLifetimeHours = update.UploadSessionLifetimeHours;
        settings.SignedUrlLifetimeMinutes = update.SignedUrlLifetimeMinutes;
    }

    private static void Validate(ServerSettingsUpdate update)
    {
        if (update.OriginalQuotaBytes is < 1L * 1024 * 1024 * 1024 or > 100L * 1024 * 1024 * 1024 * 1024)
            throw Invalid("原文件配额必须在 1 GiB 至 100 TiB 之间。");
        ValidateBytes(update.AudioMaxBytes, "音频文件上限");
        ValidateBytes(update.ImageMaxBytes, "图片文件上限");
        ValidateBytes(update.VideoMaxBytes, "视频文件上限");
        ValidateBytes(update.ThumbnailMaxBytes, "缩略图上限");
        ValidateBytes(update.ProxyMaxBytes, "代理文件上限");
        ValidateBytes(update.LutMaxBytes, "LUT 文件上限");
        ValidateRange(update.RecycleRetentionDays, 1, 3650, "回收站保留天数");
        ValidateRange(update.AuditRetentionDays, 1, 3650, "审计日志保留天数");
        ValidateRange(update.DownloadLogRetentionDays, 1, 3650, "下载日志保留天数");
        ValidateRange(update.BackupIntervalHours, 1, 168, "备份间隔小时");
        ValidateRange(update.BackupRetentionDays, 1, 3650, "备份保留天数");
        if (!IsSafeRelativePath(update.BackupLocalPath)) throw Invalid("备份本地路径必须是安全的相对路径。");
        if (string.IsNullOrWhiteSpace(update.BackupObjectPrefix) || update.BackupObjectPrefix.Contains("..", StringComparison.Ordinal))
            throw Invalid("备份对象前缀无效。");
        if (update.CapacityWarningRatio is < 0.01 or > 1 || update.CapacityCriticalRatio is < 0.01 or > 1 ||
            update.CapacityWarningRatio > update.CapacityCriticalRatio)
            throw Invalid("容量告警比例必须在 1% 至 100% 之间，且警告比例不能高于临界比例。");
        ValidateRange(update.DerivativePollIntervalSeconds, 5, 3600, "派生任务轮询间隔");
        ValidateRange(update.DerivativeProcessTimeoutSeconds, 10, 3600, "派生任务超时时间");
        ValidateRange(update.ThumbnailMaxEdge, 64, 640, "缩略图最大边长");
        ValidateRange(update.SessionLifetimeDays, 1, 30, "会话有效期");
        ValidateRange(update.LoginFailureLimit, 2, 100, "登录失败次数上限");
        ValidateRange(update.LoginFailureWindowMinutes, 1, 1440, "登录失败统计窗口");
        ValidateRange(update.LoginBlockMinutes, 1, 1440, "登录封禁时长");
        if (update.MultipartPartSizeBytes is < 5 * 1024 * 1024 or > 512 * 1024 * 1024)
            throw Invalid("上传分片大小必须在 5 MiB 至 512 MiB 之间。");
        ValidateRange(update.UploadSessionLifetimeHours, 1, 168, "上传会话有效期");
        ValidateRange(update.SignedUrlLifetimeMinutes, 1, 60, "COS 临时链接有效期");
    }

    private static void ValidateBytes(long value, string label)
    {
        if (value is < 1L * 1024 * 1024 or > 100L * 1024 * 1024 * 1024)
            throw Invalid($"{label}必须在 1 MiB 至 100 GiB 之间。");
    }

    private static void ValidateRange(int value, int minimum, int maximum, string label)
    {
        if (value < minimum || value > maximum) throw Invalid($"{label}必须在 {minimum} 至 {maximum} 之间。");
    }

    private static bool IsSafeRelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 260 || Path.IsPathRooted(value)) return false;
        return value.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .All(part => part is not "." and not ".." && !part.Contains(':'));
    }

    private static ApiException Invalid(string message) =>
        new(StatusCodes.Status400BadRequest, "invalid_server_settings", message);
}
