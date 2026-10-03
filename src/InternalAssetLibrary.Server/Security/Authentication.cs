using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InternalAssetLibrary.Contracts;
using InternalAssetLibrary.Server.Data;

namespace InternalAssetLibrary.Server.Security;

internal sealed record RequestIdentity(
    Guid UserId,
    Guid SessionId,
    string Username,
    bool IsAdmin,
    HashSet<string> Permissions)
{
    public bool HasPermission(string permission) => IsAdmin || Permissions.Contains(permission);
}

internal static class TokenService
{
    public static string CreateToken() => Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    public static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

internal sealed class BearerSessionMiddleware(RequestDelegate next)
{
    public const string IdentityKey = "InternalAssetLibrary.Identity";

    public async Task InvokeAsync(HttpContext context, IAppDataStore store)
    {
        var token = GetAccessToken(context);
        if (token is not null)
        {
            if (token.Length is >= 32 and <= 256)
            {
                var now = DateTimeOffset.UtcNow;
                var tokenHash = TokenService.HashToken(token);
                var identity = await store.ReadAsync(state =>
                {
                    var session = state.Sessions.FirstOrDefault(item =>
                        item.TokenHash.Equals(tokenHash, StringComparison.Ordinal) && item.ExpiresAt > now);
                    if (session is null)
                    {
                        return null;
                    }

                    var user = state.Users.FirstOrDefault(item => item.Id == session.UserId && item.IsEnabled);
                    return user is null
                        ? null
                        : new RequestIdentity(
                            user.Id,
                            session.Id,
                            user.Username,
                            user.IsAdmin,
                            new HashSet<string>(user.Permissions, StringComparer.OrdinalIgnoreCase));
                }, context.RequestAborted);

                if (identity is not null)
                {
                    context.Items[IdentityKey] = identity;
                }
            }
        }

        await next(context);
    }

    private static string? GetAccessToken(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return header[7..].Trim();
        }

        if (context.Request.Path.StartsWithSegments(LibraryRealtimeProtocol.HubPath))
        {
            var queryToken = context.Request.Query["access_token"].ToString().Trim();
            return queryToken.Length == 0 ? null : queryToken;
        }

        return null;
    }
}

internal static class AccessControl
{
    public static RequestIdentity RequireUser(HttpContext context)
    {
        if (context.Items[BearerSessionMiddleware.IdentityKey] is not RequestIdentity identity)
        {
            throw new ApiException(StatusCodes.Status401Unauthorized, "authentication_required", "请先登录。", "Bearer");
        }

        return identity;
    }

    public static RequestIdentity RequirePermission(HttpContext context, string permission)
    {
        var identity = RequireUser(context);
        if (!identity.HasPermission(permission))
        {
            throw new ApiException(StatusCodes.Status403Forbidden, "permission_denied", "当前账号没有执行此操作的权限。");
        }

        return identity;
    }

    public static RequestIdentity RequireAdmin(HttpContext context)
    {
        var identity = RequireUser(context);
        if (!identity.IsAdmin)
        {
            throw new ApiException(StatusCodes.Status403Forbidden, "administrator_required", "此操作仅管理员可用。");
        }

        return identity;
    }
}

internal sealed class ApiException(
    int statusCode,
    string code,
    string message,
    string? challenge = null) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
    public string? Challenge { get; } = challenge;
}

internal sealed class ApiExceptionMiddleware(RequestDelegate next, ILogger<ApiExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (ApiException exception) when (!context.Response.HasStarted)
        {
            if (exception.Challenge is not null)
            {
                context.Response.Headers.WWWAuthenticate = exception.Challenge;
            }

            await WriteProblemAsync(context, exception.StatusCode, exception.Code, exception.Message);
        }
        catch (BadHttpRequestException exception) when (!context.Response.HasStarted)
        {
            logger.LogInformation(exception, "Invalid HTTP request for {Method} {Path}.", context.Request.Method, context.Request.Path);
            await WriteProblemAsync(
                context,
                StatusCodes.Status400BadRequest,
                "invalid_request",
                "请求正文或参数格式无效。");
        }
        catch (Exception exception) when (!context.Response.HasStarted)
        {
            logger.LogError(exception, "Unhandled API error for {Method} {Path}.", context.Request.Method, context.Request.Path);
            await WriteProblemAsync(
                context,
                StatusCodes.Status500InternalServerError,
                "server_error",
                "服务器处理请求时发生错误。");
        }
    }

    private static async Task WriteProblemAsync(HttpContext context, int status, string code, string detail)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json; charset=utf-8";
        var problem = new
        {
            type = "about:blank",
            title = status switch
            {
                StatusCodes.Status400BadRequest => "请求无效",
                StatusCodes.Status401Unauthorized => "身份验证失败",
                StatusCodes.Status403Forbidden => "没有操作权限",
                StatusCodes.Status404NotFound => "资源不存在",
                StatusCodes.Status409Conflict => "请求发生冲突",
                StatusCodes.Status413PayloadTooLarge => "请求内容过大",
                StatusCodes.Status415UnsupportedMediaType => "不支持的媒体类型",
                StatusCodes.Status426UpgradeRequired => "需要更新客户端",
                StatusCodes.Status429TooManyRequests => "请求过于频繁",
                _ => "服务器错误"
            },
            status,
            detail,
            code,
            traceId = context.TraceIdentifier
        };
        await JsonSerializer.SerializeAsync(context.Response.Body, problem, cancellationToken: context.RequestAborted);
    }
}
