using InternalAssetLibrary.Client.Core.Transfers;

internal static class TransferControlSelfTests
{
    public static void PauseResumeCancelAndDurability() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var control = new TransferTask();
        var attempts = 0;
        var cleanup = 0;
        var work = control.RunAsync(async token =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            resumed.TrySetResult();
            return 42;
        }, () => { cleanup++; return Task.CompletedTask; }, (_, _) => Task.CompletedTask, timeout.Token);
        await started.Task.WaitAsync(timeout.Token);
        control.Pause();
        if (control.State != TransferTaskState.Paused || work.IsCompleted) throw new Exception("Pause ended the job instead of its attempt.");
        control.Resume();
        if (await work != 42 || attempts != 2 || cleanup != 0) throw new Exception("Resume lost its operation.");
        control.Cancel();
        if (control.State != TransferTaskState.Completed || cleanup != 0) throw new Exception("Cancel removed a committed task.");

        var directory = Directory.CreateTempSubdirectory("ial-task-control-").FullName;
        try
        {
            var store = new TransferTaskStore(Path.Combine(directory, "tasks.json"));
            var record = new TransferTaskRecord(Guid.NewGuid(), "https://server.example/", Guid.NewGuid(), "素材.mp4", true)
                { SourcePath = "video.mp4", PreparedPath = "prepared.mp4", State = TransferTaskState.Paused };
            await store.SaveAsync(record);
            var read = (await store.ReadAsync()).Single();
            if (read.Id != record.Id || read.UserId != record.UserId || read.PreparedPath != record.PreparedPath) throw new Exception("Task scope/checkpoint was lost.");
            var cancel = new TransferTask(true);
            var cancelWork = cancel.RunAsync<int>(_ => throw new Exception("Paused job started automatically."),
                () => { cleanup++; return Task.CompletedTask; }, (state, _) => store.SaveAsync(record with { State = state }), timeout.Token);
            cancel.Cancel();
            try { await cancelWork; throw new Exception("Canceled job returned success."); } catch (OperationCanceledException) { }
            if (cleanup != 1 || (await store.ReadAsync()).Count != 0) throw new Exception("Cancel did not clear its durable task.");

            var retry = new TransferTask(true);
            var failCleanup = 0;
            var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            retry.Changed += () => { if (retry.State == TransferTaskState.Failed) failed.TrySetResult(); };
            var retryWork = retry.RunAsync<int>(_ => throw new Exception("Cleanup retry uploaded again."), () =>
            {
                if (++failCleanup == 1) throw new IOException("offline");
                return Task.CompletedTask;
            }, (_, _) => Task.CompletedTask, timeout.Token);
            retry.Cancel();
            await failed.Task.WaitAsync(timeout.Token);
            if (!retry.CancelRequested || retryWork.IsCompleted) throw new Exception("Failed remote cleanup was discarded.");
            retry.Resume();
            try { await retryWork; throw new Exception("Cleanup retry returned success."); } catch (OperationCanceledException) { }
            if (failCleanup != 2) throw new Exception("Cleanup was not retryable.");
        }
        finally { Directory.Delete(directory, true); }

        var alpha = CloudUploadPreprocessor.ReadPolicy("{\"streams\":[{\"codec_type\":\"video\",\"codec_name\":\"vp9\",\"pix_fmt\":\"yuv420p\",\"tags\":{\"alpha_mode\":\"1\"},\"nb_read_frames\":\"6\"}]}");
        if (!alpha.HasAlpha || !alpha.Animated) throw new Exception("Transparent/animated content was flattened.");
        var opaque = CloudUploadPreprocessor.ReadPolicy("{\"streams\":[{\"codec_type\":\"video\",\"codec_name\":\"mjpeg\",\"pix_fmt\":\"yuvj420p\",\"nb_read_frames\":\"1\"}]}");
        if (opaque.HasAlpha || opaque.Animated) throw new Exception("Opaque single image was misclassified.");
    }
}
