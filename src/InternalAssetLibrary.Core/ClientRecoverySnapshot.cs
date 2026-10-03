using System.Security.Cryptography;
using System.Text.Json;

namespace InternalAssetLibrary.Core;

public static class ClientRecoverySnapshot
{
    private sealed record Entry(string Path, long Size, string Sha256);

    // Only program files are backed up; user settings, original media and unknown files are untouched.
    public static async Task CreateAsync(string installDirectory, string snapshotDirectory, CancellationToken token = default)
    {
        var root = Path.GetFullPath(installDirectory);
        var snapshot = Path.GetFullPath(snapshotDirectory);
        EnsureNoLinks(root, root);
        EnsureNoLinks(snapshot, snapshot);
        if (snapshot.Equals(root, StringComparison.OrdinalIgnoreCase) || snapshot.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("恢复快照不能位于程序目录内。 Recovery snapshot must be outside the installation.");
        Directory.CreateDirectory(snapshot);
        var candidates = Directory.EnumerateFiles(root).Where(path =>
            Path.GetFileName(path).StartsWith("InternalAssetLibrary.", StringComparison.OrdinalIgnoreCase) &&
            Path.GetExtension(path).ToLowerInvariant() is ".exe" or ".dll" or ".json" or ".config" or ".pdb").ToList();
        foreach (var directory in new[] { "runtime", "licenses" })
        {
            var path = Path.Combine(root, directory);
            if (Directory.Exists(path)) candidates.AddRange(SafeFiles(path));
        }
        var entries = new List<Entry>();
        foreach (var path in candidates)
        {
            token.ThrowIfCancellationRequested();
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("不能备份符号链接。 Recovery files must not be links.");
            var relative = Path.GetRelativePath(root, path);
            var target = Resolve(snapshot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous);
            await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous))
            { await input.CopyToAsync(output, token); await output.FlushAsync(token); output.Flush(true); }
            entries.Add(new Entry(relative, input.Length, await HashAsync(target, token)));
        }
        if (!entries.Any(entry => entry.Path.Equals("InternalAssetLibrary.Client.exe", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("恢复快照缺少客户端。 Recovery snapshot contains no client executable.");
        var manifest = Path.Combine(snapshot, "snapshot.json");
        await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(entries), token);
        await VerifyAsync(snapshot, token);
    }

    public static async Task RestoreAsync(string installDirectory, string snapshotDirectory, CancellationToken token = default)
    {
        var entries = await VerifyAsync(snapshotDirectory, token);
        // Verify every file first; a damaged snapshot must not partially overwrite the installation.
        foreach (var entry in entries)
        {
            var destination = Resolve(installDirectory, entry.Path);
            EnsureNoLinks(installDirectory, destination);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(Resolve(snapshotDirectory, entry.Path), destination, true);
        }
    }

    public static async Task ValidateAsync(string snapshotDirectory, CancellationToken token = default) =>
        _ = await VerifyAsync(snapshotDirectory, token);

    private static async Task<List<Entry>> VerifyAsync(string root, CancellationToken token)
    {
        var manifest = Path.Combine(root, "snapshot.json");
        if (new FileInfo(manifest).Length > 4 * 1024 * 1024) throw new InvalidDataException("恢复快照索引过大。 Recovery index is too large.");
        var entries = JsonSerializer.Deserialize<List<Entry>>(await File.ReadAllTextAsync(manifest, token))
            ?? throw new InvalidDataException("恢复快照无效。 Invalid recovery snapshot.");
        if (entries.Count is 0 or > 20000 || entries.Select(item => item.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Count ||
            !entries.Any(item => item.Path.Equals("InternalAssetLibrary.Client.exe", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("恢复快照文件清单无效。 Invalid recovery file list.");
        foreach (var entry in entries)
        {
            var path = Resolve(root, entry.Path);
            EnsureNoLinks(root, path);
            if (!File.Exists(path) || new FileInfo(path).Length != entry.Size || await HashAsync(path, token) != entry.Sha256)
                throw new InvalidDataException("恢复快照校验失败。 Recovery snapshot verification failed.");
        }
        return entries;
    }

    private static IEnumerable<string> SafeFiles(string directory)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("Recovery directories must not be links.");
        foreach (var path in Directory.EnumerateFiles(directory)) yield return path;
        foreach (var child in Directory.EnumerateDirectories(directory)) foreach (var path in SafeFiles(child)) yield return path;
    }
    private static string Resolve(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Split('/', '\\').Any(part => part is ".." or "." or "")) throw new InvalidDataException("Unsafe recovery path.");
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Recovery path escapes its root.");
        return path;
    }
    private static void EnsureNoLinks(string root, string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Recovery path contains a link.");
            if (string.Equals(current, Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)) break;
        }
    }
    private static async Task<string> HashAsync(string path, CancellationToken token)
    { await using var input = File.OpenRead(path); return Convert.ToHexString(await SHA256.HashDataAsync(input, token)); }
}
