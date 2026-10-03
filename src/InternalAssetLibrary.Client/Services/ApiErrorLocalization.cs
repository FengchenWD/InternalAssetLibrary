using InternalAssetLibrary.Client.Core.Http;

namespace InternalAssetLibrary.Client.Services;

internal static class ApiErrorLocalization
{
    public static string Message(AssetLibraryApiException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var stableMessage = StableApiMessage(exception.Code);
        return stableMessage ?? HttpStatusMessage(exception, null);
    }

    public static string Message(Exception exception, Uri? serverOrigin = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is AssetLibraryApiException apiException)
        {
            var stableMessage = StableApiMessage(apiException.Code);
            return stableMessage ?? HttpStatusMessage(apiException, serverOrigin);
        }

        var server = ServerLabel(serverOrigin);
        var failure = NetworkFailureClassifier.Classify(exception);
        return failure.Kind switch
        {
            NetworkFailureKind.NameResolution => UiLocalization.Format(
                "无法解析 {0} 的域名。请检查服务器地址，或确认内网穿透域名已经生效。",
                server),
            NetworkFailureKind.ConnectionRefused => UiLocalization.Format(
                "{0} 拒绝连接。请确认服务端已启动，并检查防火墙和内网穿透端口映射。",
                server),
            NetworkFailureKind.Timeout => UiLocalization.Format(
                "连接 {0} 超时。请检查网络、防火墙或内网穿透状态。",
                server),
            NetworkFailureKind.Tls => UiLocalization.Format(
                "{0} 的 HTTPS/TLS 连接失败。请检查证书、域名和内网穿透 HTTPS 配置。",
                server),
            NetworkFailureKind.HttpStatus => UiLocalization.Format(
                "{0} 返回 HTTP {1}。",
                server,
                failure.StatusCode is { } statusCode ? (int)statusCode : 0),
            NetworkFailureKind.InvalidHttpResponse => UiLocalization.Format(
                "{0} 返回了无效的 HTTP 响应。请确认该地址指向本软件服务端，而不是其他网页或 TCP 服务。",
                server),
            NetworkFailureKind.Connection => UiLocalization.Format(
                "无法连接 {0}。请检查服务器是否运行、网络、防火墙和内网穿透配置。",
                server),
            _ => UiLocalization.Format(
                "操作失败：{0}",
                SafeDetail(exception.Message, serverOrigin))
        };
    }

    private static string HttpStatusMessage(AssetLibraryApiException exception, Uri? serverOrigin)
    {
        var server = ServerLabel(serverOrigin);
        var detail = SafeDetail(exception.Detail ?? exception.Title, serverOrigin);
        var statusCode = (int)exception.StatusCode.GetValueOrDefault();
        return string.IsNullOrWhiteSpace(detail)
            ? UiLocalization.Format("{0} 返回 HTTP {1}。", server, statusCode)
            : UiLocalization.Format("{0} 返回 HTTP {1}：{2}", server, statusCode, detail);
    }

    private static string? StableApiMessage(string? code) => code switch
    {
        "invalid_credentials" => UiLocalization.Text("用户名、邮箱或密码不正确。"),
        "authentication_required" => UiLocalization.Text("请先登录。"),
        "permission_denied" => UiLocalization.Text("当前账号没有执行此操作的权限。"),
        "weak_password" => UiLocalization.Text("密码必须为 8 至 128 位，且至少包含大写字母、小写字母、数字、特殊符号中的两种。"),
        "incorrect_password" => UiLocalization.Text("当前密码不正确。"),
        "duplicate_content" => UiLocalization.Text("相同内容已经存在，无需重复上传或换源。"),
        _ => null
    };

    private static string ServerLabel(Uri? serverOrigin)
    {
        if (serverOrigin is not { IsAbsoluteUri: true })
        {
            return UiLocalization.Text("当前服务器");
        }

        var host = serverOrigin.HostNameType == UriHostNameType.IPv6
            ? $"[{serverOrigin.Host}]"
            : serverOrigin.IdnHost;
        return serverOrigin.IsDefaultPort ? host : $"{host}:{serverOrigin.Port}";
    }

    private static string SafeDetail(string? detail, Uri? serverOrigin)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return string.Empty;
        }

        var value = UserVisibleNetworkMessage.RedactLocations(detail, serverOrigin)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        return value.Length <= 240 ? value : value[..240] + "...";
    }
}
