using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using InternalAssetLibrary.Client.Core.Http;
using InternalAssetLibrary.Client.Core.Transfers;
using InternalAssetLibrary.Client.Core.Lut;
using InternalAssetLibrary.Contracts;
using InternalAssetLibrary.Client.Services;
using InternalAssetLibrary.Client.ViewModels;

namespace InternalAssetLibrary.Client;

public sealed partial class MainWindow
{
    private readonly TransferTaskStore _transferTasksStore = new(AppPaths.TransferTasksFile);
    private readonly Dictionary<Guid, TransferExecution> _transferExecutions = [];
    private readonly Dictionary<string, JsonDirectUploadResumeStore> _accountResumeStores = [];
    private readonly CancellationTokenSource _transferShutdown = new();
    public ObservableCollection<TransferItemViewModel> UploadTransferItems { get; } = [];
    public ObservableCollection<TransferItemViewModel> DownloadTransferItems { get; } = [];

    private sealed class TransferExecution(TransferTaskRecord record, TransferItemViewModel item, TransferTask control)
    {
        public TransferTaskRecord Record = record;
        public TransferItemViewModel Item = item;
        public TransferTask Control = control;
        public StaticAccessTokenProvider TokenProvider = new();
        public Task<TransferResult> Work = null!;
    }
    private sealed record TransferResult(string? Path = null, ApiAsset? Asset = null, bool Duplicate = false, bool PreviewWarning = false);

    private CloudFileTransferService AccountTransferService(AssetLibraryApiClient? capturedApi = null)
    {
        if (_api is null || _currentUser is null) throw new InvalidOperationException("请先登录服务器。");
        var accountKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_api.BaseAddress.AbsoluteUri))) + "-" + _currentUser.Id.ToString("N");
        if (!_accountResumeStores.TryGetValue(accountKey, out var resume))
        {
            resume = new JsonDirectUploadResumeStore(Path.Combine(AppPaths.AccountTransfersDirectory, accountKey + ".json"));
            _accountResumeStores.Add(accountKey, resume);
        }
        return new CloudFileTransferService(capturedApi ?? _api, _downloadPathMapper, _downloadRegistry, resume,
            durationProbe: async (path, token) =>
            {
                var probe = await _mediaAnalyzer.ProbeAsync(path, token);
                return probe.Succeeded ? probe.Information?.Duration?.TotalSeconds : null;
            });
    }

    private TransferExecution StartTransfer(TransferTaskRecord record, bool restored = false)
    {
        var control = new TransferTask(restored, record.CancelRequested);
        var item = new TransferItemViewModel(record.Name, record.Upload ? "上传" : "下载") { TaskId = record.Id, Control = control };
        var job = new TransferExecution(record, item, control);
        _transferExecutions.Add(record.Id, job);
        (record.Upload ? UploadTransferItems : DownloadTransferItems).Insert(0, item);
        TransferItems.Insert(0, item);
        EmptyTransfersText.IsVisible = false;
        control.Changed += () => Dispatcher.UIThread.Post(() => UpdateTransferState(job));
        job.TokenProvider.AccessToken = _tokenProvider.AccessToken;
        var capturedApi = new AssetLibraryApiClient(_httpClient, _api!.BaseAddress,
            job.TokenProvider, clientVersion: GetClientVersion());
        var service = AccountTransferService(capturedApi);
        var preprocessor = _cloudUploadPreprocessor!;
        var derivatives = new InternalAssetLibrary.Client.Core.MediaAnalysis.CloudMediaDerivativeService(
            capturedApi, _mediaThumbnailService, AppPaths.CloudDerivativeCacheDirectory,
            ResolveRuntimePath("INTERNAL_ASSET_LIBRARY_FFMPEG", AppPaths.FfmpegPath));
        var progress = new Progress<CloudTransferProgress>(value =>
        {
            if (control.State == TransferTaskState.Running) ApplyTransferProgress(item, value);
        });
        UpdateTransferState(job);
        job.Work = control.RunAsync(async token =>
        {
            EnsureTransferAccount(job);
            if (record.Kind == TransferTaskKind.Lut && !record.Upload)
                return await DownloadLutTaskAsync(job, capturedApi, token);
            if (!record.Upload)
            {
                var mapping = await service.DownloadToUserDirectoryAsync(job.Record.Asset!, job.Record.TargetDirectory!, progress, token);
                return new TransferResult(mapping.FullPath);
            }
            await _cloudUploadTransferGate.WaitAsync(token);
            try
            {
                var info = new FileInfo(job.Record.SourcePath!);
                if (!info.Exists || info.Length != record.SourceSize || info.LastWriteTimeUtc != record.SourceModifiedUtc)
                    throw new InvalidDataException("原文件已改变或不可用，请取消该任务后重新选择。 ");
                if (record.Kind == TransferTaskKind.Lut)
                {
                    var validation = await new CubeLutValidator().ValidateFileAsync(info.FullName, token);
                    if (!validation.IsValid || validation.Descriptor is not { SizeBytes: { } size, Sha256: { } hash })
                        throw new InvalidDataException(validation.Diagnostic);
                    var lut = job.Record.Lut;
                    if (lut is not null) lut = await capturedApi.GetTeamLutAsync(lut.Id, token);
                    if (record.ReplacementAssetId is { } replacementLutId)
                    {
                        lut = await capturedApi.GetTeamLutAsync(replacementLutId, token);
                        if (!hash.Equals(lut.Sha256, StringComparison.OrdinalIgnoreCase))
                        {
                            await using var input = File.OpenRead(info.FullName);
                            lut = await capturedApi.UploadTeamLutReplacementAsync(lut.Id, info.Name, size, hash, input, token);
                        }
                        job.Record = job.Record with { Lut = lut };
                        return new TransferResult();
                    }
                    if (lut is null)
                    {
                        lut = await capturedApi.CreateTeamLutAsync(new CreateTeamLutRequest(record.LutDisplayName ?? Path.GetFileNameWithoutExtension(info.Name), info.Name, size, hash), token);
                        job.Record = job.Record with { Lut = lut };
                        await SaveTransferCheckpointAsync(job, TransferTaskState.Running, null);
                    }
                    if (!lut.HasContent)
                    {
                        await using var input = File.OpenRead(info.FullName);
                        lut = await capturedApi.UploadTeamLutContentAsync(lut.Id, input, input.Length, token);
                    }
                    job.Record = job.Record with { Lut = lut };
                    return new TransferResult();
                }
                if (job.Record.PreparedPath is null)
                {
                    await Dispatcher.UIThread.InvokeAsync(() => item.Status = "正在准备上传文件");
                    var prepared = await preprocessor.PrepareAsync(info.FullName, token);
                    job.Record = job.Record with { PreparedPath = prepared.UploadPath };
                    await SaveTransferCheckpointAsync(job, TransferTaskState.Running, null);
                }
                if (!File.Exists(job.Record.PreparedPath)) throw new FileNotFoundException("转码文件已丢失，请取消后重新上传。 ");
                ApiAsset uploaded;
                try
                {
                    if (record.Kind == TransferTaskKind.Replacement && job.Record.Asset is null)
                    {
                        await using var input = File.OpenRead(job.Record.PreparedPath);
                        var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, token));
                        input.Position = 0;
                        var current = await capturedApi.GetAssetAsync(record.ReplacementAssetId!.Value, token);
                        uploaded = current.ContentHash.Equals(hash, StringComparison.OrdinalIgnoreCase) ? current :
                            await capturedApi.UploadAssetReplacementAsync(current.Id, Path.GetFileName(job.Record.PreparedPath), input.Length, hash, input, token);
                    }
                    else uploaded = job.Record.Asset ?? await service.UploadAsync(job.Record.PreparedPath, record.Category,
                        record.Notes, record.Tags, progress, token, record.FolderId);
                }
                catch (AssetLibraryApiException exception) when (exception.Code == "duplicate_content")
                { return new TransferResult(Duplicate: true); }
                job.Record = job.Record with { Asset = uploaded };
                await SaveTransferCheckpointAsync(job, TransferTaskState.Running, null);
                token.ThrowIfCancellationRequested();
                await Dispatcher.UIThread.InvokeAsync(() => item.Status = "正在生成云端预览");
                var warning = derivatives is null || (await derivatives.ProcessAfterOriginalUploadAsync(job.Record.PreparedPath, uploaded, token)).HasWarning;
                return new TransferResult(Asset: uploaded, PreviewWarning: warning);
            }
            finally { _cloudUploadTransferGate.Release(); }
        }, async () =>
        {
            EnsureTransferAccount(job);
            using var cleanupTimeout = CancellationTokenSource.CreateLinkedTokenSource(_transferShutdown.Token);
            cleanupTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            if (record.Kind == TransferTaskKind.Lut && record.Upload && record.ReplacementAssetId is null && job.Record.Lut is { } pendingLut)
            {
                try
                {
                    var current = await capturedApi.GetTeamLutAsync(pendingLut.Id, cleanupTimeout.Token);
                    if (!current.HasContent)
                    {
                        await capturedApi.RecycleTeamLutAsync(current.Id, cleanupTimeout.Token);
                        await capturedApi.PermanentlyDeleteTeamLutAsync(current.Id, cleanupTimeout.Token);
                    }
                }
                catch (AssetLibraryApiException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound) { }
            }
            if (record.Kind == TransferTaskKind.Lut && !record.Upload) File.Delete(record.TargetFilePath + "." + record.Id.ToString("N") + ".part");
            if (record.Kind == TransferTaskKind.Media && record.Upload && job.Record.PreparedPath is { } prepared && job.Record.Asset?.HasOriginal != true)
                await service.CancelPendingUploadAsync(prepared, cleanupTimeout.Token);
            if (!record.Upload && record.Kind == TransferTaskKind.Media) service.CancelPendingDownload(record.Asset!, record.TargetDirectory!);
            DeletePreparedTransferFile(job.Record);
        }, (state, error) => SaveTransferCheckpointAsync(job, state, error), _transferShutdown.Token);
        ObserveNavigationTask(ObserveTransferAsync(job), "transfer-task");
        return job;
    }

    private void EnsureTransferAccount(TransferExecution job)
    {
        if (_currentUser?.Id != job.Record.UserId || _api?.BaseAddress.AbsoluteUri != job.Record.ServerOrigin)
            throw new InvalidOperationException("请登录该任务所属的账号和服务器后继续。 ");
    }

    private async Task SaveTransferCheckpointAsync(TransferExecution job, TransferTaskState state, string? error)
    {
        job.Record = job.Record with { State = state, Error = error, CancelRequested = job.Control.CancelRequested };
        await _transferTasksStore.SaveAsync(job.Record);
        if (state is TransferTaskState.Completed or TransferTaskState.Canceled) DeletePreparedTransferFile(job.Record);
    }

    private static void DeletePreparedTransferFile(TransferTaskRecord record)
    {
        if (record.PreparedPath is { } path && !LocalPathComparer.Equals(path, record.SourcePath))
        {
            var ownedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "InternalAssetLibrary", "cloud-upload")) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(path).StartsWith(ownedRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                CloudUploadPreprocessor.TryDelete(path);
        }
    }

    private void UpdateTransferState(TransferExecution job)
    {
        job.Item.RefreshControl();
        job.Item.Status = job.Control.State switch
        {
            TransferTaskState.Queued => "等待中", TransferTaskState.Paused => "已暂停",
            TransferTaskState.Failed => "失败：" + job.Control.Error, TransferTaskState.Canceling => "正在取消",
            TransferTaskState.Canceled => "已取消", TransferTaskState.Completed => "已完成", _ => job.Item.Status
        };
        if (job.Control.State == TransferTaskState.Completed) job.Item.Progress = 100;
    }

    private async Task ObserveTransferAsync(TransferExecution job)
    {
        try { await job.Work; }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            job.Item.Status = "失败：" + UserMessage(exception);
            ClientDiagnostics.WriteException("transfer-checkpoint", exception, false);
        }
    }

    private async Task RestoreTransferTasksAsync()
    {
        if (_api is null || _currentUser is null) return;
        var origin = _api.BaseAddress.AbsoluteUri;
        var userId = _currentUser.Id;
        var records = await _transferTasksStore.ReadAsync();
        if (_currentUser?.Id != userId || _api?.BaseAddress.AbsoluteUri != origin) return;
        UploadTransferItems.Clear(); DownloadTransferItems.Clear();
        foreach (var existing in _transferExecutions.Values)
            if (existing.Record.UserId == userId && existing.Record.ServerOrigin == origin)
            {
                // A re-login may issue a new token. Never update another account's task.
                existing.TokenProvider.AccessToken = _tokenProvider.AccessToken;
                (existing.Record.Upload ? UploadTransferItems : DownloadTransferItems).Add(existing.Item);
            }
        foreach (var record in records.Where(item => item.UserId == userId && item.ServerOrigin == origin && !_transferExecutions.ContainsKey(item.Id)))
            StartTransfer(record, restored: true);
    }

    private void PauseTransfers()
    { foreach (var job in _transferExecutions.Values) job.Control.Pause(); }

    private async Task SuspendTransfersAsync()
    {
        PauseTransfers();
        await Task.WhenAll(_transferExecutions.Values.Select(job => job.Control.WaitUntilSuspendedAsync()));
    }

    private async Task StopTransfersAsync()
    {
        PauseTransfers();
        _transferShutdown.Cancel();
        await Task.WhenAll(_transferExecutions.Values.Select(async job => { try { await job.Work; } catch (OperationCanceledException) { } }));
    }

    private void TransferPause_OnClick(object? sender, RoutedEventArgs args)
    { if (sender is Button { DataContext: TransferItemViewModel item }) item.Control?.Pause(); }
    private void TransferResume_OnClick(object? sender, RoutedEventArgs args)
    { if (sender is Button { DataContext: TransferItemViewModel item }) item.Control?.Resume(); }
    private void TransferCancel_OnClick(object? sender, RoutedEventArgs args)
    { if (sender is Button { DataContext: TransferItemViewModel item }) item.Control?.Cancel(); }

    private async void TransferBatch_OnClick(object? sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: string action }) return;
        var jobs = (action.StartsWith("upload:") ? UploadTransferItems : DownloadTransferItems).ToArray();
        if (action.EndsWith(":cancel") && !await new MessageDialogWindow("取消传输任务", "是否取消此列表中当前所有未完成的任务？不会删除原文件和已完成素材。", "确认取消", "返回").ShowDialog<bool>(this)) return;
        foreach (var item in jobs)
        {
            if (action.EndsWith(":pause")) item.Control?.Pause();
            else if (action.EndsWith(":resume")) item.Control?.Resume();
            else item.Control?.Cancel();
        }
    }

    private TransferExecution QueueUpload(string path, ApiAssetCategory category, string? notes, IReadOnlyList<string> tags, Guid? folder)
    {
        var info = new FileInfo(path);
        return StartTransfer(new TransferTaskRecord(Guid.NewGuid(), _api!.BaseAddress.AbsoluteUri, _currentUser!.Id, info.Name, true)
        {
            SourcePath = info.FullName, SourceSize = info.Length, SourceModifiedUtc = info.LastWriteTimeUtc,
            Category = category, Notes = notes, Tags = tags.ToArray(), FolderId = folder
        });
    }

    private TransferExecution QueueLutUpload(string path, string? displayName = null, Guid? replacementId = null)
    {
        var info = new FileInfo(path);
        return StartTransfer(new TransferTaskRecord(Guid.NewGuid(), _api!.BaseAddress.AbsoluteUri, _currentUser!.Id, info.Name, true)
        {
            Kind = TransferTaskKind.Lut, SourcePath = info.FullName,
            SourceSize = info.Length, SourceModifiedUtc = info.LastWriteTimeUtc,
            LutDisplayName = displayName, ReplacementAssetId = replacementId
        });
    }

    private TransferExecution QueueLutDownload(TeamLutSummary lut, string directory) =>
        StartTransfer(new TransferTaskRecord(Guid.NewGuid(), _api!.BaseAddress.AbsoluteUri, _currentUser!.Id, lut.Name, false)
        { Kind = TransferTaskKind.Lut, Lut = lut, TargetDirectory = directory, TargetFilePath = UniqueCloudLutPath(directory, lut) });

    private LutLibraryWindow CreateLutLibraryWindow(Guid? initialLutId = null) => new(
        _localLutLibrary, _api, _currentUser, initialLutId,
        async (path, name, replacementId) =>
        {
            var job = QueueLutUpload(path, name, replacementId);
            await job.Work;
            return job.Record.Lut!;
        },
        async (lut, target) =>
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(target))!;
            var job = StartTransfer(new TransferTaskRecord(Guid.NewGuid(), _api!.BaseAddress.AbsoluteUri, _currentUser!.Id, lut.Name, false)
            { Kind = TransferTaskKind.Lut, Lut = lut, TargetDirectory = directory, TargetFilePath = target });
            return (await job.Work).Path!;
        });

    private TransferExecution QueueReplacement(string path, ApiAsset asset)
    {
        var info = new FileInfo(path);
        return StartTransfer(new TransferTaskRecord(Guid.NewGuid(), _api!.BaseAddress.AbsoluteUri, _currentUser!.Id, info.Name, true)
        {
            Kind = TransferTaskKind.Replacement, ReplacementAssetId = asset.Id, Category = asset.Category,
            SourcePath = info.FullName, SourceSize = info.Length, SourceModifiedUtc = info.LastWriteTimeUtc
        });
    }

    private async Task<TransferResult> DownloadLutTaskAsync(TransferExecution job, AssetLibraryApiClient api, CancellationToken token)
    {
        var lut = job.Record.Lut!;
        var current = await api.GetTeamLutAsync(lut.Id, token);
        if (current.Sha256 != lut.Sha256) throw new InvalidDataException("LUT 已更新，请取消任务后重新下载。");
        var target = job.Record.TargetFilePath!;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var partial = target + "." + job.Record.Id.ToString("N") + ".part";
        await using (var output = new FileStream(partial, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.Asynchronous))
        {
            if (output.Length > lut.SizeBytes) output.SetLength(0);
            output.Position = output.Length;
            if (output.Length < lut.SizeBytes)
            {
                try { await api.DownloadTeamLutRangeAsync(lut.Id, output.Length, lut.SizeBytes, output, token); }
                catch (DownloadResumeNotSupportedException)
                {
                    output.SetLength(0); output.Position = 0;
                    await api.DownloadTeamLutRangeAsync(lut.Id, 0, lut.SizeBytes, output, token);
                }
            }
            await output.FlushAsync(token);
            output.Position = 0;
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(output, token));
            if (output.Length != lut.SizeBytes || !hash.Equals(lut.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                output.SetLength(0);
                throw new InvalidDataException("LUT 下载校验失败，请继续任务重新下载。");
            }
        }
        if (File.Exists(target)) target = UniqueCloudLutPath(job.Record.TargetDirectory!, lut);
        File.Move(partial, target);
        return new TransferResult(target);
    }
}
