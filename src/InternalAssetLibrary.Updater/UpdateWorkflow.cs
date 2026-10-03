using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Runtime.Versioning;
using InternalAssetLibrary.Contracts;
using InternalAssetLibrary.Core;
using Microsoft.Win32;

namespace InternalAssetLibrary.Updater;

[SupportedOSPlatform("windows")]
internal sealed class UpdateWorkflow
{
    internal const int TransactionSchemaVersion = 1;
    internal const string ProductRegistrySubKey = @"Software\FengchenWD\InternalAssetLibrary";
    internal const string ClientExecutableName = "InternalAssetLibrary.Client.exe";
    private static readonly TimeSpan ClientExitTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan InstallerTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(60);
    private static readonly JsonSerializerOptions TransactionJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _applicationRoot;
    private readonly string _updatesRoot;
    private readonly string _diagnosticsRoot;

    public UpdateWorkflow()
    {
        _applicationRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FengchenWD",
            "InternalAssetLibrary");
        _updatesRoot = Path.Combine(_applicationRoot, "updates");
        _diagnosticsRoot = Path.Combine(_applicationRoot, "diagnostics");
    }

    public async Task RunAsync(string transactionPath, CancellationToken cancellationToken = default)
    {
        var transaction = await LoadAndValidateAsync(transactionPath, cancellationToken).ConfigureAwait(false);
        await AppendDiagnosticAsync($"开始更新事务 {transaction.TransactionId:N}。 Starting update transaction {transaction.TransactionId:N}.").ConfigureAwait(false);
        await WaitForClientExitAsync(transaction, cancellationToken).ConfigureAwait(false);
        await VerifyPackageAsync(transaction.TargetPackage, cancellationToken).ConfigureAwait(false);

        Process? updatedClient = null;
        try
        {
            await RunInstallerAsync(transaction.TargetPackage.InstallerPath, transaction.InstallDirectory, cancellationToken)
                .ConfigureAwait(false);
            updatedClient = StartClient(transaction.ClientExecutablePath, transaction.TransactionId);
            await WaitForHealthConfirmationAsync(transaction, updatedClient, cancellationToken).ConfigureAwait(false);
            CleanupInstallerCache(transaction.TargetPackage.InstallerPath);
            await AppendDiagnosticAsync($"更新事务 {transaction.TransactionId:N} 已完成。 Update transaction {transaction.TransactionId:N} completed.")
                .ConfigureAwait(false);
        }
        catch (Exception updateException)
        {
            await AppendDiagnosticAsync($"更新失败。 Update failed: {updateException}").ConfigureAwait(false);
            await StopOwnedProcessAsync(updatedClient).ConfigureAwait(false);
            var restored = await TryRestoreAsync(transaction, cancellationToken).ConfigureAwait(false);
            if (!restored && File.Exists(transaction.ClientExecutablePath))
            {
                _ = StartClient(transaction.ClientExecutablePath, transactionId: null);
            }

            throw new InvalidOperationException(
                restored
                    ? "更新失败，已恢复原客户端。 The update failed and the previous client version was restored."
                    : "更新失败且自动恢复未成功，请保留诊断日志并手动覆盖安装。 The update failed and verified recovery did not succeed.",
                updateException);
        }
    }

    internal async Task<ClientUpdateTransaction> LoadAndValidateAsync(
        string transactionPath,
        CancellationToken cancellationToken = default)
    {
        var fullTransactionPath = Path.GetFullPath(transactionPath);
        var transactionRoot = Path.Combine(_updatesRoot, "transactions");
        EnsurePathIsWithin(fullTransactionPath, transactionRoot, "transaction file");
        if (!File.Exists(fullTransactionPath))
        {
            throw new FileNotFoundException("The update transaction file does not exist.", fullTransactionPath);
        }

        await using var input = new FileStream(
            fullTransactionPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            32 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var transaction = await DeserializeTransactionAsync(input, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidDataException("The update transaction is empty.");

        if (transaction.SchemaVersion != TransactionSchemaVersion ||
            transaction.TransactionId == Guid.Empty ||
            transaction.ClientProcessId <= 0 ||
            transaction.CreatedAtUtc < DateTimeOffset.UtcNow.AddDays(-2) ||
            transaction.CreatedAtUtc > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            throw new InvalidDataException("The update transaction header is invalid or expired.");
        }

        var expectedTransactionDirectory = Path.Combine(transactionRoot, transaction.TransactionId.ToString("N"));
        EnsurePathsEqual(Path.GetDirectoryName(fullTransactionPath), expectedTransactionDirectory, "transaction directory");
        ValidatePackage(transaction.TargetPackage, requireInsideUpdatesRoot: true);
        if (transaction.RollbackPackage is not null)
        {
            ValidatePackage(transaction.RollbackPackage, requireInsideUpdatesRoot: true);
        }
        if (transaction.RecoverySnapshotDirectory is { } recovery)
        {
            EnsurePathsEqual(recovery, Path.Combine(expectedTransactionDirectory, "previous-client"), "recovery snapshot");
            await ClientRecoverySnapshot.ValidateAsync(recovery, cancellationToken).ConfigureAwait(false);
        }

        ValidateInstallation(transaction);
        EnsurePathIsWithin(transaction.HealthConfirmationPath, expectedTransactionDirectory, "health confirmation");
        return transaction;
    }

    internal static ValueTask<ClientUpdateTransaction?> DeserializeTransactionAsync(
        Stream input,
        CancellationToken cancellationToken = default) =>
        JsonSerializer.DeserializeAsync<ClientUpdateTransaction>(
            input,
            TransactionJsonOptions,
            cancellationToken);

    private async Task WaitForClientExitAsync(
        ClientUpdateTransaction transaction,
        CancellationToken cancellationToken)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(transaction.ClientProcessId);
        }
        catch (ArgumentException)
        {
            return;
        }

        using (process)
        {
            DateTimeOffset actualStart;
            try
            {
                actualStart = process.StartTime.ToUniversalTime();
            }
            catch (InvalidOperationException)
            {
                return;
            }

            if (Math.Abs((actualStart - transaction.ClientProcessStartedAtUtc).TotalSeconds) > 2)
            {
                return;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ClientExitTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("The running client did not exit before the update timeout.");
            }
        }
    }

    private static async Task RunInstallerAsync(
        string installerPath,
        string installDirectory,
        CancellationToken cancellationToken)
    {
        using var installer = Process.Start(CreateInstallerStartInfo(installerPath, installDirectory))
            ?? throw new InvalidOperationException("The update installer could not be started.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(InstallerTimeout);
        try
        {
            await installer.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                installer.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            throw new TimeoutException("The update installer did not finish before the timeout.");
        }

        if (installer.ExitCode != 0)
        {
            throw new InvalidOperationException($"The update installer returned exit code {installer.ExitCode}.");
        }
    }

    internal static ProcessStartInfo CreateInstallerStartInfo(string installerPath, string installDirectory)
    {
        var startInfo = new ProcessStartInfo(installerPath)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(installerPath) ?? Environment.CurrentDirectory
        };
        foreach (var argument in new[]
                 {
                     "/VERYSILENT",
                     "/SUPPRESSMSGBOXES",
                     "/NORESTART",
                     "/CLOSEAPPLICATIONS",
                     "/LANG=chinesesimplified",
                     $"/DIR={installDirectory}"
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static Process StartClient(string executablePath, Guid? transactionId)
    {
        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException("The installed client executable does not exist.", executablePath);
        }

        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? Environment.CurrentDirectory
        };
        if (transactionId.HasValue)
        {
            startInfo.ArgumentList.Add("--post-update");
            startInfo.ArgumentList.Add(transactionId.Value.ToString("N"));
        }

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("The installed client could not be restarted.");
    }

    private static async Task WaitForHealthConfirmationAsync(
        ClientUpdateTransaction transaction,
        Process updatedClient,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + HealthTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(transaction.HealthConfirmationPath))
            {
                var confirmation = (await File.ReadAllTextAsync(
                        transaction.HealthConfirmationPath,
                        cancellationToken)
                    .ConfigureAwait(false)).Trim();
                if (string.Equals(confirmation, transaction.TransactionId.ToString("N"), StringComparison.Ordinal))
                {
                    return;
                }
            }

            if (updatedClient.HasExited)
            {
                throw new InvalidOperationException("The updated client exited before confirming a healthy startup.");
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException("The updated client did not confirm a healthy startup.");
    }

    private async Task<bool> TryRestoreAsync(
        ClientUpdateTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (transaction.RollbackPackage is null)
        {
            if (transaction.RecoverySnapshotDirectory is null) return false;
            try
            {
                await ClientRecoverySnapshot.RestoreAsync(transaction.InstallDirectory, transaction.RecoverySnapshotDirectory, cancellationToken).ConfigureAwait(false);
                _ = StartClient(transaction.ClientExecutablePath, transactionId: null);
                await AppendDiagnosticAsync("已恢复更新前的程序快照。 Restored the pre-update application snapshot.").ConfigureAwait(false);
                return true;
            }
            catch (Exception exception)
            { await AppendDiagnosticAsync($"快照恢复失败。 Snapshot recovery failed: {exception}").ConfigureAwait(false); return false; }
        }

        try
        {
            await VerifyPackageAsync(transaction.RollbackPackage, cancellationToken).ConfigureAwait(false);
            await RunInstallerAsync(
                    transaction.RollbackPackage.InstallerPath,
                    transaction.InstallDirectory,
                    cancellationToken)
                .ConfigureAwait(false);
            _ = StartClient(transaction.ClientExecutablePath, transactionId: null);
            await AppendDiagnosticAsync($"已恢复客户端 {transaction.RollbackPackage.Version}。 Restored client {transaction.RollbackPackage.Version}.")
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception exception)
        {
            await AppendDiagnosticAsync($"回滚失败。 Rollback failed: {exception}").ConfigureAwait(false);
            return false;
        }
    }

    private static async Task StopOwnedProcessAsync(Process? process)
    {
        if (process is null)
        {
            return;
        }

        using (process)
        {
            try
            {
                if (process.HasExited)
                {
                    return;
                }

                _ = process.CloseMainWindow();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync().ConfigureAwait(false);
                    }
                }
                catch
                {
                }
            }
        }
    }

    private void ValidateInstallation(ClientUpdateTransaction transaction)
    {
        var installDirectory = Path.GetFullPath(transaction.InstallDirectory);
        var executablePath = Path.GetFullPath(transaction.ClientExecutablePath);
        EnsurePathIsWithin(executablePath, installDirectory, "client executable");
        if (!string.Equals(Path.GetFileName(executablePath), ClientExecutableName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The client executable name is invalid.");
        }

        using var productKey = Registry.CurrentUser.OpenSubKey(ProductRegistrySubKey, writable: false);
        var registeredDirectory = productKey?.GetValue("InstallLocation") as string;
        var registeredExecutable = productKey?.GetValue("ExecutablePath") as string;
        EnsurePathsEqual(registeredDirectory, installDirectory, "registered install directory");
        EnsurePathsEqual(registeredExecutable, executablePath, "registered client executable");

        var root = Path.GetPathRoot(installDirectory);
        if (string.Equals(root, installDirectory, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
                Path.TrimEndingDirectorySeparator(installDirectory),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The install directory is too broad for an automatic update.");
        }
    }

    private void ValidatePackage(ClientUpdatePackageInfo package, bool requireInsideUpdatesRoot)
    {
        if (string.IsNullOrWhiteSpace(package.Version) ||
            package.InstallerSizeBytes <= 0 ||
            package.InstallerSha256.Length != 64 ||
            !package.InstallerSha256.All(char.IsAsciiHexDigit) ||
            !package.InstallerPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("An update package entry is invalid.");
        }

        if (requireInsideUpdatesRoot)
        {
            EnsurePathIsWithin(package.InstallerPath, _updatesRoot, "update installer");
        }
    }

    private static async Task VerifyPackageAsync(
        ClientUpdatePackageInfo package,
        CancellationToken cancellationToken)
    {
        var file = new FileInfo(package.InstallerPath);
        if (!file.Exists || file.Length != package.InstallerSizeBytes)
        {
            throw new InvalidDataException("The update installer size no longer matches its verified metadata.");
        }

        await using var input = new FileStream(
            file.FullName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false));
        if (!hash.Equals(package.InstallerSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The update installer SHA-256 no longer matches its verified metadata.");
        }
    }

    private void CleanupInstallerCache(string installerToKeep)
    {
        foreach (var installer in Directory.EnumerateFiles(
                     _updatesRoot,
                     "InternalAssetLibrary.Client.Setup*.exe",
                     SearchOption.AllDirectories))
        {
            if (!PathsEqual(installer, installerToKeep))
            {
                try
                {
                    File.Delete(installer);
                }
                catch
                {
                }
            }
        }
    }

    private async Task AppendDiagnosticAsync(string message)
    {
        Directory.CreateDirectory(_diagnosticsRoot);
        var path = Path.Combine(_diagnosticsRoot, "updater.log");
        await File.AppendAllTextAsync(path, $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}")
            .ConfigureAwait(false);
    }

    public static string WriteFatalDiagnostic(Exception exception)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FengchenWD",
            "InternalAssetLibrary",
            "diagnostics");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "updater-fatal.log");
        try
        {
            File.AppendAllText(path, $"[{DateTimeOffset.Now:O}] {exception}{Environment.NewLine}");
        }
        catch
        {
        }

        return path;
    }

    private static void EnsurePathIsWithin(string candidate, string root, string field)
    {
        var fullCandidate = Path.GetFullPath(candidate);
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var prefix = fullRoot + Path.DirectorySeparatorChar;
        if (!fullCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"The {field} path is outside its allowed directory.");
        }
    }

    private static void EnsurePathsEqual(string? first, string? second, string field)
    {
        if (string.IsNullOrWhiteSpace(first) ||
            string.IsNullOrWhiteSpace(second) ||
            !PathsEqual(first, second))
        {
            throw new InvalidDataException($"The {field} does not match the installed application.");
        }
    }

    private static bool PathsEqual(string first, string second) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
            StringComparison.OrdinalIgnoreCase);
}
