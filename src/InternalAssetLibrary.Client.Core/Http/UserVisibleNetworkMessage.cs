using System.Text.RegularExpressions;

namespace InternalAssetLibrary.Client.Core.Http;

public static class UserVisibleNetworkMessage
{
    private const string HiddenAddress = "[服务器地址已隐藏]";
    private const string HiddenCredential = "[凭据已隐藏]";

    private static readonly Regex AuthorizationCredentialPattern = new(
        @"(?<prefix>\b(?:Bearer|Basic)\s+)[A-Za-z0-9._~+/=-]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex NamedCredentialPattern = new(
        @"(?<prefix>\b(?:password|passwd|access[_-]?token|refresh[_-]?token|token|api[_-]?key)\b[\""']?\s*[:=]\s*)(?:\""[^\""\r\n]*\""|'[^'\r\n]*'|[^\s,;&]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex AbsoluteNetworkUriPattern = new(
        @"\b(?:https?|wss?)://[^\s\""'<>，。；！？）】]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Ipv4EndpointPattern = new(
        @"(?<![\d.])(?:\d{1,3}\.){3}\d{1,3}(?::\d{1,5})?(?![\d.])",
        RegexOptions.CultureInvariant);

    private static readonly Regex BracketedIpv6EndpointPattern = new(
        @"\[[0-9a-f:]+\](?::\d{1,5})?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string RedactLocations(string? message, Uri? serverOrigin = null)
    {
        if (string.IsNullOrEmpty(message))
        {
            return string.Empty;
        }

        var redacted = AuthorizationCredentialPattern.Replace(
            message,
            match => match.Groups["prefix"].Value + HiddenCredential);
        redacted = NamedCredentialPattern.Replace(
            redacted,
            match => match.Groups["prefix"].Value + HiddenCredential);
        redacted = AbsoluteNetworkUriPattern.Replace(redacted, HideAbsoluteNetworkUri);
        redacted = Ipv4EndpointPattern.Replace(redacted, HiddenAddress);
        redacted = BracketedIpv6EndpointPattern.Replace(redacted, HiddenAddress);

        if (serverOrigin is { IsAbsoluteUri: true })
        {
            redacted = RedactEndpoint(redacted, serverOrigin.Authority);
            redacted = RedactEndpoint(redacted, serverOrigin.Host);
        }

        return redacted;
    }

    private static string HideAbsoluteNetworkUri(Match match)
    {
        var addressLength = match.Value.AsSpan().TrimEnd(".,;:!?)]}".AsSpan()).Length;
        return HiddenAddress + match.Value[addressLength..];
    }

    private static string RedactEndpoint(string message, string endpoint)
    {
        if (string.IsNullOrEmpty(endpoint))
        {
            return message;
        }

        return Regex.Replace(
            message,
            $@"(?<![\p{{L}}\p{{N}}_.-]){Regex.Escape(endpoint)}(?![\p{{L}}\p{{N}}_.-])",
            HiddenAddress,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
