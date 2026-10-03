using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using InternalAssetLibrary.Contracts;
using InternalAssetLibrary.Core;
using Microsoft.Win32;

namespace InternalAssetLibrary.Client.Services;

internal sealed record ClientUpdateLaunch(string RunnerPath, string TransactionPath);

internal sealed record ClientReleaseNotesNotice(Guid TransactionId, string Version, string ReleaseNotes);

internal static class ClientUpdateCoordinator
{
    private const int SchemaVersion = 1;
    private const string ProductRegistrySubKey = "Software\\FengchenWD\\InternalAssetLibrary";
    private const string UpdaterExecutableName = "InternalAssetLibrary.Updater.exe";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static async Task<ClientUpdateLaunch> PrepareAsync(
        ClientReleaseInfo release,
        string verifiedInstallerPath,
        string currentVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("一键更新目前只支持 Windows 安装版。");
        }

        var executablePath = Path.GetFullPath(Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定当前客户端程序路径。"));
        var installDirectory = Path.GetDirectoryName(executablePath)
            ?? throw new InvalidOperationException("无法确定当前客户端安装目录。");
        ValidateInstalledApplication(executablePath, installDirectory);

        var updaterSource = Path.Combine(AppContext.BaseDirectory, UpdaterExecutableName);
        if (!File.Exists(updaterSource))
        {
            throw new FileNotFoundException("安装目录缺少独立更新助手，请使用完整安装包修复安装。", updaterSource);
        }

        var targetPackage = new ClientUpdatePackageInfo(
            release.Version,
            Path.GetFullPath(verifiedInstallerPath),
            release.InstallerSizeBytes,
            release.InstallerSha256.ToUpperInvariant());
        await VerifyPackageAsync(targetPackage, cancellationToken);
        EnsurePathIsWithin(targetPackage.InstallerPath, AppPaths.UpdateDownloadDirectory, "更新安装包");

        var rollbackPackage = await TryLoadRollbackPackageAsync(currentVersion, cancellationToken);
        var transactionId = Guid.NewGuid();
        var transactionDirectory = Path.Combine(
            AppPaths.UpdateTransactionsDirectory,
            transactionId.ToString("N"));
        Directory.CreateDirectory(transactionDirectory);
        string? recoveryDirectory = null;
        if (rollbackPackage is null)
        {
            recoveryDirectory = Path.Combine(transactionDirectory, "previous-client");
            await Task.Run(() => ClientRecoverySnapshot.CreateAsync(installDirectory, recoveryDirectory, cancellationToken), cancellationToken);
        }
        var runnerPath = Path.Combine(transactionDirectory, UpdaterExecutableName);
        File.Copy(updaterSource, runnerPath, overwrite: false);
        var transactionPath = Path.Combine(transactionDirectory, "transaction.json");
        var healthPath = Path.Combine(transactionDirectory, "healthy.txt");
        using var process = Process.GetCurrentProcess();
        var transaction = new ClientUpdateTransaction(
            SchemaVersion,
            transactionId,
            DateTimeOffset.UtcNow,
            Environment.ProcessId,
            process.StartTime.ToUniversalTime(),
            installDirectory,
            executablePath,
            healthPath,
            targetPackage,
            rollbackPackage,
            release.ReleaseNotes) { RecoverySnapshotDirectory = recoveryDirectory };
        await WriteJsonAtomicallyAsync(transactionPath, transaction, cancellationToken);
        return new ClientUpdateLaunch(runnerPath, transactionPath);
    }

    public static Process Launch(ClientUpdateLaunch launch)
    {
        var startInfo = new ProcessStartInfo(launch.RunnerPath)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(launch.RunnerPath) ?? Environment.CurrentDirectory
        };
        startInfo.ArgumentList.Add("--transaction");
        startInfo.ArgumentList.Add(launch.TransactionPath);
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动独立更新助手。");
    }

    public static async Task AcknowledgePostUpdateAsync(
        Guid transactionId,
        string currentVersion,
        CancellationToken cancellationToken = default)
    {
        var transactionPath = TransactionPath(transactionId);
        var transaction = await ReadJsonAsync<ClientUpdateTransaction>(transactionPath, cancellationToken)
            ?? throw new InvalidDataException("更新事务不存在或无法读取。");
        if (transaction.SchemaVersion != SchemaVersion ||
            transaction.TransactionId != transactionId ||
            !string.Equals(transaction.TargetPackage.Version, currentVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException("更新后的客户端版本与更新事务不一致。");
        }

        var expectedHealthPath = Path.Combine(
            Path.GetDirectoryName(transactionPath)!,
            "healthy.txt");
        if (!PathsEqual(expectedHealthPath, transaction.HealthConfirmationPath))
        {
            throw new InvalidDataException("更新健康确认路径无效。");
        }

        var state = new ClientInstalledUpdateState(
            SchemaVersion,
            transactionId,
            DateTimeOffset.UtcNow,
            transaction.TargetPackage,
            transaction.ReleaseNotes,
            LastShownTransactionId: null);
        await WriteJsonAtomicallyAsync(AppPaths.InstalledUpdateStateFile, state, cancellationToken);
        await WriteTextAtomicallyAsync(
            expectedHealthPath,
            transactionId.ToString("N"),
            cancellationToken);
    }

    public static async Task EnsureCurrentPackageStateAsync(
        string currentVersion,
        CancellationToken cancellationToken = default)
    {
        CleanupCompletedTransactionDirectories(activeTransactionId: null);
        var state = await ReadJsonAsync<ClientInstalledUpdateState>(
            AppPaths.InstalledUpdateStateFile,
            cancellationToken);
        if (state is not null &&
            state.SchemaVersion == SchemaVersion &&
            string.Equals(state.Package.Version, currentVersion, StringComparison.Ordinal) &&
            File.Exists(state.Package.InstallerPath))
        {
            return;
        }

        if (!Directory.Exists(AppPaths.UpdateDownloadDirectory))
        {
            return;
        }

        var candidate = Directory.EnumerateFiles(
                AppPaths.UpdateDownloadDirectory,
                "InternalAssetLibrary.Client.Setup*.exe",
                SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .Where(file => file.Name.Contains(currentVersion, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .FirstOrDefault();
        if (candidate is null)
        {
            return;
        }

        await using var input = candidate.OpenRead();
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken));
        var seeded = new ClientInstalledUpdateState(
            SchemaVersion,
            Guid.Empty,
            DateTimeOffset.UtcNow,
            new ClientUpdatePackageInfo(currentVersion, candidate.FullName, candidate.Length, hash),
            ReleaseNotes: null,
            LastShownTransactionId: Guid.Empty);
        await WriteJsonAtomicallyAsync(AppPaths.InstalledUpdateStateFile, seeded, cancellationToken);
    }

    public static async Task<ClientReleaseNotesNotice?> GetPendingReleaseNotesAsync(
        string currentVersion,
        CancellationToken cancellationToken = default)
    {
        var state = await ReadJsonAsync<ClientInstalledUpdateState>(
            AppPaths.InstalledUpdateStateFile,
            cancellationToken);
        if (state is null ||
            state.SchemaVersion != SchemaVersion ||
            state.TransactionId == Guid.Empty ||
            state.LastShownTransactionId == state.TransactionId ||
            !string.Equals(state.Package.Version, currentVersion, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(state.ReleaseNotes))
        {
            return null;
        }

        return new ClientReleaseNotesNotice(
            state.TransactionId,
            state.Package.Version,
            state.ReleaseNotes.Trim());
    }

    public static async Task<ClientReleaseNotesNotice?> GetCurrentReleaseNotesAsync(
        string currentVersion,
        CancellationToken cancellationToken = default)
    {
        var state = await ReadJsonAsync<ClientInstalledUpdateState>(
            AppPaths.InstalledUpdateStateFile,
            cancellationToken);
        return state is not null &&
               state.SchemaVersion == SchemaVersion &&
               string.Equals(state.Package.Version, currentVersion, StringComparison.Ordinal) &&
               !string.IsNullOrWhiteSpace(state.ReleaseNotes)
            ? new ClientReleaseNotesNotice(
                state.TransactionId,
                state.Package.Version,
                state.ReleaseNotes.Trim())
            : null;
    }

    public static async Task MarkReleaseNotesShownAsync(
        Guid transactionId,
        CancellationToken cancellationToken = default)
    {
        var state = await ReadJsonAsync<ClientInstalledUpdateState>(
            AppPaths.InstalledUpdateStateFile,
            cancellationToken);
        if (state is null || state.TransactionId != transactionId)
        {
            return;
        }

        await WriteJsonAtomicallyAsync(
            AppPaths.InstalledUpdateStateFile,
            state with { LastShownTransactionId = transactionId },
            cancellationToken);
    }

    private static async Task<ClientUpdatePackageInfo?> TryLoadRollbackPackageAsync(
        string currentVersion,
        CancellationToken cancellationToken)
    {
        var state = await ReadJsonAsync<ClientInstalledUpdateState>(
            AppPaths.InstalledUpdateStateFile,
            cancellationToken);
        if (state is null ||
            state.SchemaVersion != SchemaVersion ||
            !string.Equals(state.Package.Version, currentVersion, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            EnsurePathIsWithin(state.Package.InstallerPath, AppPaths.UpdateDownloadDirectory, "回滚安装包");
            await VerifyPackageAsync(state.Package, cancellationToken);
            return state.Package;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ValidateInstalledApplication(string executablePath, string installDirectory)
    {
        using var productKey = Registry.CurrentUser.OpenSubKey(ProductRegistrySubKey, writable: false);
        var registeredDirectory = productKey?.GetValue("InstallLocation") as string;
        var registeredExecutable = productKey?.GetValue("ExecutablePath") as string;
        if (!PathsEqual(registeredDirectory, installDirectory) ||
            !PathsEqual(registeredExecutable, executablePath))
        {
            throw new InvalidOperationException("一键更新仅支持通过安装包装入的客户端；便携版请下载完整安装包更新。");
        }
    }

    private static async Task VerifyPackageAsync(
        ClientUpdatePackageInfo package,
        CancellationToken cancellationToken)
    {
        var file = new FileInfo(package.InstallerPath);
        if (!file.Exists || file.Length != package.InstallerSizeBytes)
        {
            throw new InvalidDataException("安装包大小与更新清单不一致。");
        }

        await using var input = new FileStream(
            file.FullName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken));
        if (!hash.Equals(package.InstallerSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("安装包 SHA-256 与更新清单不一致。");
        }
    }

    private static async Task<T?> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        await using var input = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            32 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<T>(input, JsonOptions, cancellationToken);
    }

    private static async Task WriteJsonAtomicallyAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("更新状态文件缺少父目录。");
        Directory.CreateDirectory(directory);
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var output = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             32 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(output, value, JsonOptions, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        catch
        {
            File.Delete(temporaryPath);
            throw;
        }
    }

    private static async Task WriteTextAtomicallyAsync(
        string path,
        string value,
        CancellationToken cancellationToken)
    {
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(temporaryPath, value, cancellationToken);
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static string TransactionPath(Guid transactionId) => Path.Combine(
        AppPaths.UpdateTransactionsDirectory,
        transactionId.ToString("N"),
        "transaction.json");

    private static void CleanupCompletedTransactionDirectories(Guid? activeTransactionId)
    {
        if (!Directory.Exists(AppPaths.UpdateTransactionsDirectory))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(AppPaths.UpdateTransactionsDirectory))
        {
            if (activeTransactionId.HasValue &&
                string.Equals(
                    Path.GetFileName(directory),
                    activeTransactionId.Value.ToString("N"),
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static void EnsurePathIsWithin(string candidate, string root, string field)
    {
        var fullCandidate = Path.GetFullPath(candidate);
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!fullCandidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{field}不在受管理的更新目录中。");
        }
    }

    private static bool PathsEqual(string? first, string? second) =>
        !string.IsNullOrWhiteSpace(first) &&
        !string.IsNullOrWhiteSpace(second) &&
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
            StringComparison.OrdinalIgnoreCase);
}
