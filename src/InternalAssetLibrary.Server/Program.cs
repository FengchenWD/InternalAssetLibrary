using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using InternalAssetLibrary.Contracts;
using InternalAssetLibrary.Server.Api;
using InternalAssetLibrary.Server.Data;
using InternalAssetLibrary.Server.Realtime;
using InternalAssetLibrary.Server.Security;
using InternalAssetLibrary.Server.Services;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

// Offline maintenance is intentionally separate from serving requests and never loads COS credentials.
if (args.Contains("--database-status") || args.Contains("--database-migrate"))
{
    var path = Path.GetFullPath(builder.Configuration["database-path"] ??
        builder.Configuration["Sqlite:DatabasePath"] ?? "App_Data/library.db");
    var migrate = args.Contains("--database-migrate");
    if (migrate && !args.Contains("--offline-confirm"))
        throw new InvalidOperationException("Stop the server first, then pass --offline-confirm for database maintenance.");
    using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
    {
        DataSource = path, Pooling = false,
        Mode = migrate ? Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite : Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly
    }.ToString());
    await connection.OpenAsync();
    if (migrate)
    {
        var target = builder.Configuration.GetValue("target-version", SqliteSchemaMigrator.CurrentVersion);
        await SqliteSchemaMigrator.MigrateAsync(connection, path, target);
    }
    await SqliteSchemaMigrator.CheckIntegrityAsync(connection);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
    {
        schemaVersion = await SqliteSchemaMigrator.VersionAsync(connection),
        supportedVersion = SqliteSchemaMigrator.CurrentVersion, integrity = "ok"
    }));
    return;
}

if (!builder.Environment.IsDevelopment())
{
    var metricsToken = builder.Configuration["Monitoring:MetricsToken"]?.Trim();
    if (string.IsNullOrEmpty(metricsToken) || metricsToken.Length < 32 ||
        metricsToken.Contains("REPLACE_WITH", StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException(
            "Monitoring:MetricsToken must be a non-placeholder secret containing at least 32 characters in production.");
    }
}

builder.Logging.ClearProviders();
builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));
builder.Logging.AddConsole();
builder.Logging.AddDebug();
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 16 * 1024 * 1024);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
var metadataProvider = builder.Configuration["Storage:MetadataProvider"]?.Trim().ToLowerInvariant()
    ?? (builder.Environment.IsDevelopment() ? "json" : "sqlite");
switch (metadataProvider)
{
    case "json":
        builder.Services.AddSingleton<IAppDataStore, AtomicJsonDataStore>();
        break;
    case "sqlite":
        builder.Services.AddSingleton<SqliteAppDataStore>();
        builder.Services.AddSingleton<IAppDataStore>(services => services.GetRequiredService<SqliteAppDataStore>());
        builder.Services.AddSingleton<ISqliteBackupSource>(services => services.GetRequiredService<SqliteAppDataStore>());
        break;
    default:
        throw new InvalidOperationException(
            $"Unsupported Storage:MetadataProvider '{metadataProvider}'. Use 'json' or 'sqlite'.");
}
builder.Services.AddSingleton<PasswordService>();
builder.Services.AddSingleton<AvatarFileStore>();
var objectProvider = builder.Configuration["Storage:ObjectProvider"]?.Trim().ToLowerInvariant()
    ?? (builder.Environment.IsDevelopment() ? "filesystem" : "cos");
switch (objectProvider)
{
    case "filesystem":
    case "development":
        builder.Services.AddSingleton<IObjectStore, DevelopmentObjectStore>();
        break;
    case "cos":
        builder.Services.AddSingleton<IObjectStore, CosObjectStore>();
        break;
    default:
        throw new InvalidOperationException(
            $"Unsupported Storage:ObjectProvider '{objectProvider}'. Use 'filesystem' or 'cos'.");
}
builder.Services.AddSingleton<ObjectDeletionOutbox>();
builder.Services.AddSingleton<ClientReleaseProvider>();
builder.Services.AddSingleton<ServerSettingsService>();
builder.Services.AddSingleton<ILibraryChangePublisher, SignalRLibraryChangePublisher>();
builder.Services.AddSingleton<ILibraryChangeNotifier, BestEffortLibraryChangeNotifier>();
builder.Services.AddHostedService<DevelopmentDataCleanupService>();
builder.Services.AddHostedService<DerivativeBackfillService>();
builder.Services.AddHostedService<CapacityMonitorService>();
builder.Services.AddHostedService<DirectUploadCleanupService>();
if (metadataProvider == "sqlite")
{
    builder.Services.AddHostedService<SqliteBackupService>();
}
builder.Services.AddResponseCompression();
builder.Services.AddSignalR();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor |
                               ForwardedHeaders.XForwardedHost |
                               ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    var knownProxy = builder.Configuration["Http:KnownProxy"]?.Trim();
    if (!string.IsNullOrEmpty(knownProxy))
    {
        if (!System.Net.IPAddress.TryParse(knownProxy, out var address))
        {
            throw new InvalidOperationException("Http:KnownProxy must be a valid IP address.");
        }

        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        options.KnownProxies.Add(address);
    }
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            Window = TimeSpan.FromMinutes(1),
            PermitLimit = 8,
            QueueLimit = 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true
        }));
});

var app = builder.Build();
var webRootPath = app.Environment.WebRootPath ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot");
static async Task SendFileOrNotFoundAsync(HttpContext context, string path)
{
    if (!File.Exists(path))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    await context.Response.SendFileAsync(path);
}
// Resolve once during startup so an invalid compatibility policy fails fast,
// instead of turning every API request into a runtime 500.
_ = app.Services.GetRequiredService<ClientReleaseProvider>();

await ServerBootstrapper.InitializeAsync(app);
await app.Services.GetRequiredService<ServerSettingsService>().InitializeAsync();

if (builder.Configuration.GetValue("Http:UseForwardedHeaders", true))
{
    app.UseForwardedHeaders();
}

if (builder.Configuration.GetValue("Http:EnableHsts", false))
{
    app.UseHsts();
}
if (builder.Configuration.GetValue("Http:EnableHttpsRedirection", false))
{
    app.UseHttpsRedirection();
}

app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.XFrameOptions = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    var websocketOrigin = $"{(context.Request.IsHttps ? "wss" : "ws")}://{context.Request.Host}";
    context.Response.Headers.ContentSecurityPolicy =
        "default-src 'self'; img-src 'self' blob: data:; style-src 'self'; script-src 'self' 'wasm-unsafe-eval'; " +
        "style-src-attr 'unsafe-inline'; " +
        $"connect-src 'self' {websocketOrigin}; frame-src https://player.bilibili.com https://www.bilibili.com; " +
        "object-src 'none'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    await next();
});
app.UseResponseCompression();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        context.Context.Response.Headers.CacheControl =
            context.File.Name.Equals("index.html", StringComparison.OrdinalIgnoreCase)
                ? "no-cache"
                : "public, max-age=3600";
    }
});
app.MapGet("/admin", async context =>
{
    context.Response.ContentType = "text/html; charset=utf-8";
    context.Response.Headers.CacheControl = "no-cache";
    await SendFileOrNotFoundAsync(context, Path.Combine(webRootPath, "index.html"));
});
app.MapGet("/admin/{**path}", async context =>
{
    context.Response.ContentType = "text/html; charset=utf-8";
    context.Response.Headers.CacheControl = "no-cache";
    await SendFileOrNotFoundAsync(context, Path.Combine(webRootPath, "index.html"));
});
app.UseMiddleware<ApiExceptionMiddleware>();
app.UseMiddleware<ClientCompatibilityMiddleware>();
app.UseRateLimiter();
app.UseMiddleware<BearerSessionMiddleware>();
app.UseMiddleware<LibraryRealtimeAuthorizationMiddleware>();

app.MapGet("/healthz", async (
    IAppDataStore store,
    IObjectStore objects,
    CancellationToken cancellationToken) =>
{
    var objectStorageAvailable = await objects.IsAvailableAsync(cancellationToken);
    var status = await store.ReadAsync(state => new
    {
        status = objectStorageAvailable ? "healthy" : "degraded",
        storage = store.StorageKind,
        objectStorage = objects.StorageKind,
        objectStorageAvailable,
        schemaVersion = state.SchemaVersion,
        utc = DateTimeOffset.UtcNow
    }, cancellationToken);
    return objectStorageAvailable
        ? Results.Ok(status)
        : Results.Json(status, statusCode: StatusCodes.Status503ServiceUnavailable);
});

app.MapAuthProfileEndpoints();
app.MapAdminAssetEndpoints();
app.MapAssetFolderEndpoints();
app.MapMarkerEndpoints();
app.MapAssetContentEndpoints();
app.MapAssetDerivativeEndpoints();
app.MapTeamLutEndpoints();
app.MapClientUpdateEndpoints();
app.MapMonitoringEndpoints();
app.MapDirectTransferEndpoints();
app.MapHub<LibraryRealtimeHub>(LibraryRealtimeProtocol.HubPath);

app.Run();

public partial class Program;
