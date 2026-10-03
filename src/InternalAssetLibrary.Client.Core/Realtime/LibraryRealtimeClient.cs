using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using InternalAssetLibrary.Client.Core.Http;
using InternalAssetLibrary.Contracts;

namespace InternalAssetLibrary.Client.Core.Realtime;

public sealed class LibraryChangeReceivedEventArgs(LibraryChangeNotification notification) : EventArgs
{
    public LibraryChangeNotification Notification { get; } = notification;
}

public sealed class LibraryRealtimeConnectedEventArgs(bool isReconnect) : EventArgs
{
    public bool IsReconnect { get; } = isReconnect;
}

public sealed class LibraryRealtimeClient : IAsyncDisposable
{
    private static readonly byte RecordSeparator = 0x1e;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(15)
    ];

    private readonly HttpClient _httpClient;
    private readonly Uri _baseAddress;
    private readonly IAccessTokenProvider _tokenProvider;
    private readonly object _sync = new();
    private CancellationTokenSource? _lifetime;
    private Task? _runTask;
    private ClientWebSocket? _socket;
    private bool _hasConnected;

    public LibraryRealtimeClient(
        HttpClient httpClient,
        Uri baseAddress,
        IAccessTokenProvider tokenProvider)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _baseAddress = baseAddress ?? throw new ArgumentNullException(nameof(baseAddress));
        _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        if (!_baseAddress.IsAbsoluteUri ||
            !string.Equals(_baseAddress.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(_baseAddress.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The realtime base address must be an absolute HTTP(S) URI.", nameof(baseAddress));
        }
    }

    public event EventHandler<LibraryChangeReceivedEventArgs>? ChangeReceived;

    public event EventHandler<LibraryRealtimeConnectedEventArgs>? Connected;

    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return _runTask is { IsCompleted: false };
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_runTask is { IsCompleted: false })
            {
                return Task.CompletedTask;
            }

            _lifetime?.Dispose();
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _hasConnected = false;
            _runTask = RunAsync(_lifetime.Token);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        Task? runTask;
        ClientWebSocket? socket;
        lock (_sync)
        {
            _lifetime?.Cancel();
            runTask = _runTask;
            socket = _socket;
        }

        socket?.Abort();
        if (runTask is not null)
        {
            try
            {
                await runTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var retry = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ConnectAndReceiveAsync(cancellationToken).ConfigureAwait(false);
                retry = 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Reconnection is intentionally silent; ordinary API calls surface authentication errors.
            }

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var delay = RetryDelays[Math.Min(retry++, RetryDelays.Length - 1)];
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ConnectAndReceiveAsync(CancellationToken cancellationToken)
    {
        var token = await _tokenProvider.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("An access token is required for realtime updates.");
        }

        var connectionToken = await NegotiateAsync(token, cancellationToken).ConfigureAwait(false);
        using var socket = new ClientWebSocket();
        lock (_sync)
        {
            _socket = socket;
        }

        try
        {
            await socket.ConnectAsync(BuildWebSocketUri(connectionToken, token), cancellationToken)
                .ConfigureAwait(false);
            using var connectionLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using var sendLock = new SemaphoreSlim(1, 1);
            await SendTextAsync(
                socket,
                "{\"protocol\":\"json\",\"version\":1}\u001e",
                sendLock,
                connectionLifetime.Token).ConfigureAwait(false);

            var receiveTask = ReceiveLoopAsync(socket, connectionLifetime.Token);
            var keepAliveTask = SendKeepAliveLoopAsync(socket, sendLock, connectionLifetime.Token);
            await Task.WhenAny(receiveTask, keepAliveTask).ConfigureAwait(false);
            await connectionLifetime.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(receiveTask, keepAliveTask).ConfigureAwait(false);
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_socket, socket))
                {
                    _socket = null;
                }
            }
        }
    }

    private async Task<string> NegotiateAsync(string accessToken, CancellationToken cancellationToken)
    {
        var path = LibraryRealtimeProtocol.HubPath.TrimEnd('/') + "/negotiate?negotiateVersion=1";
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseAddress, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = new ByteArrayContent([]);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var root = document.RootElement;
        var connectionToken = root.TryGetProperty("connectionToken", out var tokenElement)
            ? tokenElement.GetString()
            : null;
        var supportsWebSockets = root.TryGetProperty("availableTransports", out var transports) &&
            transports.EnumerateArray().Any(item =>
                item.TryGetProperty("transport", out var transport) &&
                string.Equals(transport.GetString(), "WebSockets", StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(connectionToken) || !supportsWebSockets)
        {
            throw new InvalidDataException("The server did not offer a SignalR WebSocket connection.");
        }

        return connectionToken;
    }

    private Uri BuildWebSocketUri(string connectionToken, string accessToken)
    {
        var hub = new Uri(_baseAddress, LibraryRealtimeProtocol.HubPath);
        var builder = new UriBuilder(hub)
        {
            Scheme = hub.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            Port = hub.IsDefaultPort ? -1 : hub.Port,
            Query = $"id={Uri.EscapeDataString(connectionToken)}&access_token={Uri.EscapeDataString(accessToken)}"
        };
        return builder.Uri;
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        var waitingForHandshake = true;
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            using var payload = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                if (result.MessageType == WebSocketMessageType.Text && result.Count > 0)
                {
                    payload.Write(buffer, 0, result.Count);
                }
            } while (!result.EndOfMessage);

            if (payload.Length > 0)
            {
                ProcessPayload(
                    payload.GetBuffer().AsSpan(0, checked((int)payload.Length)),
                    ref waitingForHandshake);
            }
        }
    }

    private static async Task SendKeepAliveLoopAsync(
        ClientWebSocket socket,
        SemaphoreSlim sendLock,
        CancellationToken cancellationToken)
    {
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(KeepAliveInterval, cancellationToken).ConfigureAwait(false);
            await SendTextAsync(socket, "{\"type\":6}\u001e", sendLock, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private void ProcessPayload(ReadOnlySpan<byte> payload, ref bool waitingForHandshake)
    {
        var start = 0;
        while (start < payload.Length)
        {
            var separator = payload[start..].IndexOf(RecordSeparator);
            if (separator < 0)
            {
                break;
            }

            var frame = payload.Slice(start, separator);
            start += separator + 1;
            if (frame.IsEmpty)
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(frame.ToArray());
                var root = document.RootElement;
                if (waitingForHandshake)
                {
                    waitingForHandshake = false;
                    if (root.TryGetProperty("error", out var error))
                    {
                        throw new InvalidDataException(
                            error.GetString() ?? "The SignalR handshake failed.");
                    }

                    RaiseConnected();
                    continue;
                }

                if (!root.TryGetProperty("type", out var type) || type.GetInt32() != 1 ||
                    !root.TryGetProperty("target", out var target) ||
                    !string.Equals(target.GetString(), LibraryRealtimeProtocol.ChangeEvent, StringComparison.Ordinal) ||
                    !root.TryGetProperty("arguments", out var arguments) ||
                    arguments.GetArrayLength() == 0)
                {
                    continue;
                }

                var notification = arguments[0].Deserialize<LibraryChangeNotification>(JsonOptions);
                if (notification is not null)
                {
                    RaiseChangeReceived(notification);
                }
            }
            catch (JsonException)
            {
                if (waitingForHandshake)
                {
                    throw new InvalidDataException("The SignalR handshake response was invalid.");
                }

                // Ignore malformed or unsupported hub frames and keep the connection alive.
            }
        }
    }

    private void RaiseConnected()
    {
        bool isReconnect;
        lock (_sync)
        {
            isReconnect = _hasConnected;
            _hasConnected = true;
        }

        var handlers = Connected;
        if (handlers is null)
        {
            return;
        }

        var args = new LibraryRealtimeConnectedEventArgs(isReconnect);
        foreach (EventHandler<LibraryRealtimeConnectedEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, args);
            }
            catch
            {
                // A UI subscriber must not tear down the realtime connection.
            }
        }
    }

    private void RaiseChangeReceived(LibraryChangeNotification notification)
    {
        var handlers = ChangeReceived;
        if (handlers is null)
        {
            return;
        }

        var args = new LibraryChangeReceivedEventArgs(notification);
        foreach (EventHandler<LibraryChangeReceivedEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, args);
            }
            catch
            {
                // A UI subscriber must not tear down the realtime connection.
            }
        }
    }

    private static async Task SendTextAsync(
        ClientWebSocket socket,
        string text,
        SemaphoreSlim sendLock,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(
                bytes,
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            sendLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        lock (_sync)
        {
            _lifetime?.Dispose();
            _lifetime = null;
            _runTask = null;
            _socket = null;
        }
    }
}
