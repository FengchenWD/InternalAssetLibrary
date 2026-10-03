using System.Text.Json;
using InternalAssetLibrary.Client.Core.Http;
using InternalAssetLibrary.Contracts;

namespace InternalAssetLibrary.Client.Core.Transfers;

public enum TransferTaskState { Queued, Running, Paused, Failed, Canceling, Canceled, Completed }
public enum TransferTaskKind { Media, Lut, Replacement }

// No credentials or signed URLs are persisted. A task belongs to one account on one server.
public sealed record TransferTaskRecord(Guid Id, string ServerOrigin, Guid UserId, string Name, bool Upload)
{
    public string? SourcePath { get; init; }
    public long SourceSize { get; init; }
    public DateTime SourceModifiedUtc { get; init; }
    public string? PreparedPath { get; init; }
    public ApiAssetCategory Category { get; init; }
    public string? Notes { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public Guid? FolderId { get; init; }
    public ApiAsset? Asset { get; init; }
    public string? TargetDirectory { get; init; }
    public TransferTaskState State { get; init; } = TransferTaskState.Queued;
    public string? Error { get; init; }
    public bool CancelRequested { get; init; }
    public TransferTaskKind Kind { get; init; }
    public TeamLutSummary? Lut { get; init; }
    public string? LutDisplayName { get; init; }
    public Guid? ReplacementAssetId { get; init; }
    public string? TargetFilePath { get; init; }
}

public sealed class TransferTaskStore(string path)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<TransferTaskRecord>> ReadAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { return await ReadCoreAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(TransferTaskRecord record)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var records = await ReadCoreAsync().ConfigureAwait(false);
            records.RemoveAll(item => item.Id == record.Id);
            if (record.State is not (TransferTaskState.Completed or TransferTaskState.Canceled)) records.Add(record);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var temp = path + ".tmp";
            await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None,
                             16384, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(output, records, Options).ConfigureAwait(false);
                await output.FlushAsync().ConfigureAwait(false);
                output.Flush(true);
            }
            File.Move(temp, path, true);
        }
        finally { _gate.Release(); }
    }

    private async Task<List<TransferTaskRecord>> ReadCoreAsync()
    {
        if (!File.Exists(path)) return [];
        await using var input = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<List<TransferTaskRecord>>(input, Options).ConfigureAwait(false)
            ?? throw new InvalidDataException("传输任务记录损坏；未覆盖原记录。 ");
    }
}

/// <summary>Pause cancels only the current attempt; resume starts a new attempt using its saved checkpoint.</summary>
public sealed class TransferTask
{
    private readonly object _sync = new();
    private CancellationTokenSource? _attempt;
    private TaskCompletionSource _wake = NewWake();
    private bool _cancel;
    private bool _cleanupPending;
    private TaskCompletionSource _idle = CompletedWake();
    public Task WaitUntilSuspendedAsync() { lock (_sync) return _idle.Task; }
    public TransferTaskState State { get; private set; }
    public string? Error { get; private set; }
    public event Action? Changed;

    public TransferTask(bool paused = false, bool cleanupPending = false)
    { State = paused ? TransferTaskState.Paused : TransferTaskState.Queued; _cleanupPending = cleanupPending; }
    public bool CancelRequested => _cancel || _cleanupPending;

    public void Pause()
    {
        lock (_sync)
        {
            if (State is not (TransferTaskState.Queued or TransferTaskState.Running)) return;
            State = TransferTaskState.Paused;
            _attempt?.Cancel();
        }
        Changed?.Invoke();
    }

    public void Resume()
    {
        lock (_sync)
        {
            if (State is not (TransferTaskState.Paused or TransferTaskState.Failed)) return;
            State = TransferTaskState.Queued;
            _cancel = _cleanupPending;
            Error = null;
            var old = _wake;
            _wake = NewWake();
            old.TrySetResult();
        }
        Changed?.Invoke();
    }

    public void Cancel()
    {
        lock (_sync)
        {
            if (State is TransferTaskState.Completed or TransferTaskState.Canceled) return;
            _cancel = true;
            _cleanupPending = true;
            State = TransferTaskState.Canceling;
            _attempt?.Cancel();
            _wake.TrySetResult();
        }
        Changed?.Invoke();
    }

    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> attempt, Func<Task> cleanup,
        Func<TransferTaskState, string?, Task> checkpoint, CancellationToken shutdown = default)
    {
        using var shutdownRegistration = shutdown.Register(Pause);
        while (true)
        {
            Task? waiting = null;
            CancellationTokenSource? active = null;
            lock (_sync)
            {
                if (!_cancel && State is TransferTaskState.Paused or TransferTaskState.Failed) waiting = _wake.Task;
                else if (!_cancel)
                {
                    State = TransferTaskState.Running;
                    _idle = NewWake();
                    active = _attempt = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
                }
            }
            if (waiting is not null)
            {
                try { await checkpoint(State, Error).ConfigureAwait(false); }
                catch (Exception exception)
                {
                    lock (_sync) { State = TransferTaskState.Failed; Error = "无法保存传输记录：" + exception.Message; }
                    Changed?.Invoke();
                }
                await waiting.WaitAsync(shutdown).ConfigureAwait(false);
                continue;
            }
            if (_cancel)
            {
                lock (_sync) _idle = NewWake();
                try { await cleanup().ConfigureAwait(false); }
                catch (Exception exception)
                {
                    // A failed remote cleanup remains durable and retryable, never silently discarded.
                    lock (_sync) { _cancel = false; State = TransferTaskState.Failed; Error = "取消清理失败：" + exception.Message; _wake = NewWake(); }
                    Changed?.Invoke();
                    continue;
                }
                finally { lock (_sync) _idle.TrySetResult(); }
                lock (_sync) { State = TransferTaskState.Canceled; _cleanupPending = false; _cancel = false; }
                Changed?.Invoke();
                await checkpoint(State, null).ConfigureAwait(false);
                throw new OperationCanceledException("任务已取消");
            }
            Changed?.Invoke();
            try
            {
                await checkpoint(State, null).ConfigureAwait(false);
                var result = await attempt(active!.Token).ConfigureAwait(false);
                // Completion wins over a last-moment pause/cancel: never remove a committed file.
                lock (_sync) { _cancel = false; _cleanupPending = false; State = TransferTaskState.Completed; }
                Changed?.Invoke();
                await checkpoint(State, null).ConfigureAwait(false);
                return result;
            }
            catch (OperationCanceledException) when (active!.IsCancellationRequested)
            {
                if (shutdown.IsCancellationRequested)
                {
                    await checkpoint(TransferTaskState.Paused, null).ConfigureAwait(false);
                    throw;
                }
            }
            catch (Exception exception)
            {
                if (State == TransferTaskState.Completed) throw;
                lock (_sync)
                {
                    if (!_cancel && State != TransferTaskState.Paused)
                    { State = TransferTaskState.Failed; Error = exception.Message; }
                }
                Changed?.Invoke();
            }
            finally
            {
                lock (_sync) { _attempt = null; active!.Dispose(); _idle.TrySetResult(); }
            }
        }
    }

    private static TaskCompletionSource NewWake() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TaskCompletionSource CompletedWake() { var result = NewWake(); result.SetResult(); return result; }
}
