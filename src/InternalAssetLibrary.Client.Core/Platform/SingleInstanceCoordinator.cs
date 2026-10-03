using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace InternalAssetLibrary.Client.Core.Platform;

public sealed class SingleInstanceCoordinator : IDisposable
{
    private const int ProtocolVersion = 1;
    private const int MaximumPayloadBytes = 1024 * 1024;
    private readonly string _mutexName;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _listenerCancellation = new();
    private Mutex? _mutex;
    private Task? _listenerTask;
    private bool _ownsMutex;
    private bool _disposed;

    public SingleInstanceCoordinator(string applicationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);
        var safeApplicationId = string.Concat(applicationId.Where(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '-'));
        if (safeApplicationId.Length == 0)
        {
            throw new ArgumentException("Application ID must contain a letter or digit.", nameof(applicationId));
        }

        var scope = CreateScopeToken();
        _mutexName = OperatingSystem.IsWindows()
            ? $"Local\\{safeApplicationId}.{scope}"
            : $"{safeApplicationId}.{scope}";
        _pipeName = $"{safeApplicationId}.{scope}";
    }

    public event Action<FileOpenActivationRequest>? ActivationReceived;

    public bool IsPrimary => _ownsMutex;

    public bool TryBecomePrimary()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_ownsMutex)
        {
            return true;
        }

        _mutex?.Dispose();
        _mutex = new Mutex(initiallyOwned: true, _mutexName, out var createdNew);
        if (createdNew)
        {
            _ownsMutex = true;
            return true;
        }

        _mutex.Dispose();
        _mutex = null;
        return false;
    }

    public void StartListening()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_ownsMutex)
        {
            throw new InvalidOperationException("Only the primary instance can listen for activations.");
        }

        _listenerTask ??= Task.Run(() => ListenLoopAsync(_listenerCancellation.Token));
    }

    public async Task<bool> ForwardAsync(
        FileOpenActivationRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var payload = Serialize(request);
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = timeout - stopwatch.Elapsed;
            var attemptMilliseconds = Math.Clamp((int)remaining.TotalMilliseconds, 1, 300);
            using var pipe = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.Out,
                CurrentUserPipeOptions);

            try
            {
                await pipe.ConnectAsync(attemptMilliseconds, cancellationToken).ConfigureAwait(false);
                var header = new byte[sizeof(int)];
                BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
                await pipe.WriteAsync(header, cancellationToken).ConfigureAwait(false);
                await pipe.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (Exception exception) when (
                exception is TimeoutException or IOException && !cancellationToken.IsCancellationRequested)
            {
                var delay = timeout - stopwatch.Elapsed;
                if (delay <= TimeSpan.Zero)
                {
                    break;
                }

                await Task.Delay(delay < TimeSpan.FromMilliseconds(50) ? delay : TimeSpan.FromMilliseconds(50), cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _listenerCancellation.Cancel();
        try
        {
            _listenerTask?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _listenerCancellation.Dispose();
        }

        if (_ownsMutex && _mutex is not null)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // A different thread may dispose test or host infrastructure.
            }
        }

        _mutex?.Dispose();
        _mutex = null;
        _ownsMutex = false;
    }

    private async Task ListenLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    CurrentUserPipeOptions);
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                var request = await ReadRequestAsync(pipe, cancellationToken).ConfigureAwait(false);
                RaiseActivation(request);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (
                exception is IOException or InvalidDataException or JsonException)
            {
                // A malformed or disconnected sender must not stop later activations.
            }
        }
    }

    private void RaiseActivation(FileOpenActivationRequest request)
    {
        var handlers = ActivationReceived;
        if (handlers is null)
        {
            return;
        }

        foreach (Action<FileOpenActivationRequest> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(request);
            }
            catch
            {
                // One UI subscriber must not disable the process activation channel.
            }
        }
    }

    private static async Task<FileOpenActivationRequest> ReadRequestAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (payloadLength <= 0 || payloadLength > MaximumPayloadBytes)
        {
            throw new InvalidDataException("The activation payload length is invalid.");
        }

        var payload = new byte[payloadLength];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        var envelope = JsonSerializer.Deserialize<ActivationEnvelope>(payload)
            ?? throw new InvalidDataException("The activation payload is empty.");
        if (envelope.ProtocolVersion != ProtocolVersion ||
            envelope.FilePaths is null ||
            envelope.FilePaths.Length > FileOpenCommandLine.MaximumFileCount ||
            envelope.FilePaths.Any(path =>
                string.IsNullOrWhiteSpace(path) ||
                !Path.IsPathFullyQualified(path) ||
                !DefaultPlayerFileAssociations.SupportsPath(path)))
        {
            throw new InvalidDataException("The activation payload is incompatible.");
        }

        return new FileOpenActivationRequest(envelope.FilePaths.Distinct(PathComparer));
    }

    private static byte[] Serialize(FileOpenActivationRequest request)
    {
        if (request.FilePaths.Count > FileOpenCommandLine.MaximumFileCount ||
            request.FilePaths.Any(path =>
                string.IsNullOrWhiteSpace(path) ||
                !Path.IsPathFullyQualified(path) ||
                !DefaultPlayerFileAssociations.SupportsPath(path)))
        {
            throw new InvalidDataException("The activation request is invalid.");
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new ActivationEnvelope(ProtocolVersion, request.FilePaths.ToArray()));
        if (payload.Length > MaximumPayloadBytes)
        {
            throw new InvalidDataException("The activation request is too large.");
        }

        return payload;
    }

    private static string CreateScopeToken()
    {
        var sessionId = OperatingSystem.IsWindows()
            ? Process.GetCurrentProcess().SessionId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : Environment.GetEnvironmentVariable("XDG_SESSION_ID") ??
              Environment.GetEnvironmentVariable("SECURITYSESSIONID") ??
              "default";
        var identity = $"{Environment.UserDomainName}\\{Environment.UserName}|{sessionId}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return Convert.ToHexString(hash.AsSpan(0, 12));
    }

    private static PipeOptions CurrentUserPipeOptions => OperatingSystem.IsWindows()
        ? PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly
        : PipeOptions.Asynchronous;

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private sealed record ActivationEnvelope(int ProtocolVersion, string[] FilePaths);
}
