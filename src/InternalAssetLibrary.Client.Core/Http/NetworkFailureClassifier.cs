using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;

namespace InternalAssetLibrary.Client.Core.Http;

public enum NetworkFailureKind
{
    Unknown,
    NameResolution,
    ConnectionRefused,
    Connection,
    Timeout,
    Tls,
    HttpStatus,
    InvalidHttpResponse
}

public sealed record NetworkFailure(
    NetworkFailureKind Kind,
    HttpStatusCode? StatusCode = null);

public static class NetworkFailureClassifier
{
    public static NetworkFailure Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is AssetLibraryApiException apiException)
        {
            return new NetworkFailure(NetworkFailureKind.HttpStatus, apiException.StatusCode);
        }

        var fallback = new NetworkFailure(NetworkFailureKind.Unknown);
        foreach (var current in Enumerate(exception))
        {
            if (current is InvalidApiResponseException)
            {
                return new NetworkFailure(NetworkFailureKind.InvalidHttpResponse);
            }

            if (current is OperationCanceledException)
            {
                return new NetworkFailure(NetworkFailureKind.Timeout);
            }

            if (current is AuthenticationException)
            {
                return new NetworkFailure(NetworkFailureKind.Tls);
            }

            if (current is SocketException socketException)
            {
                var socketFailure = Classify(socketException.SocketErrorCode);
                if (socketFailure.Kind != NetworkFailureKind.Connection)
                {
                    return socketFailure;
                }

                fallback = socketFailure;
            }

            if (current is not HttpRequestException httpException)
            {
                continue;
            }

            if (httpException.StatusCode is { } statusCode)
            {
                return new NetworkFailure(NetworkFailureKind.HttpStatus, statusCode);
            }

            var failure = httpException.HttpRequestError switch
            {
                HttpRequestError.NameResolutionError => NetworkFailureKind.NameResolution,
                HttpRequestError.SecureConnectionError or
                    HttpRequestError.UserAuthenticationError => NetworkFailureKind.Tls,
                HttpRequestError.HttpProtocolError or
                    HttpRequestError.InvalidResponse or
                    HttpRequestError.ResponseEnded or
                    HttpRequestError.ProxyTunnelError or
                    HttpRequestError.VersionNegotiationError => NetworkFailureKind.InvalidHttpResponse,
                HttpRequestError.ConnectionError => NetworkFailureKind.Connection,
                _ => NetworkFailureKind.Unknown
            };
            if (failure == NetworkFailureKind.Connection)
            {
                fallback = new NetworkFailure(failure);
            }
            else if (failure != NetworkFailureKind.Unknown)
            {
                return new NetworkFailure(failure);
            }
        }

        return fallback;
    }

    private static IEnumerable<Exception> Enumerate(Exception exception)
    {
        if (exception is AggregateException aggregateException)
        {
            foreach (var innerException in aggregateException.Flatten().InnerExceptions)
            {
                foreach (var current in Enumerate(innerException))
                {
                    yield return current;
                }
            }

            yield break;
        }

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            yield return current;
        }
    }

    private static NetworkFailure Classify(SocketError socketError) => socketError switch
    {
        SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain =>
            new NetworkFailure(NetworkFailureKind.NameResolution),
        SocketError.ConnectionRefused =>
            new NetworkFailure(NetworkFailureKind.ConnectionRefused),
        SocketError.TimedOut =>
            new NetworkFailure(NetworkFailureKind.Timeout),
        _ => new NetworkFailure(NetworkFailureKind.Connection)
    };
}
