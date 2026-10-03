using System.Text.Json;

namespace InternalAssetLibrary.Client.Core.Transfers;

public sealed record DirectUploadResumeRecord(
    string ServerOrigin,
    string LocalPath,
    long SizeBytes,
    string Sha256,
    Guid AssetId,
    Guid SessionId,
    IReadOnlyDictionary<int, string> CompletedParts,
    DateTimeOffset UpdatedAt);

public interface IDirectUploadResumeStore
{
    Task<DirectUploadResumeRecord?> FindByPathAsync(Uri origin, string path, CancellationToken cancellationToken = default) =>
        Task.FromResult<DirectUploadResumeRecord?>(null);
    Task<DirectUploadResumeRecord?> FindAsync(
        Uri serverOrigin,
        string localPath,
        long sizeBytes,
        string sha256,
        CancellationToken cancellationToken = default);

    Task UpsertAsync(DirectUploadResumeRecord record, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid sessionId, CancellationToken cancellationToken = default);
}

public sealed class JsonDirectUploadResumeStore : IDirectUploadResumeStore, IDisposable
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public JsonDirectUploadResumeStore(string path) => _path = Path.GetFullPath(path);

    public async Task<DirectUploadResumeRecord?> FindByPathAsync(Uri origin, string path, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return (await LoadAsync(cancellationToken).ConfigureAwait(false)).Records.FirstOrDefault(record =>
                record.ServerOrigin.Equals(NormalizeOrigin(origin), StringComparison.OrdinalIgnoreCase) && PathEquals(record.LocalPath, path));
        }
        finally { _gate.Release(); }
    }

    public async Task<DirectUploadResumeRecord?> FindAsync(
        Uri serverOrigin,
        string localPath,
        long sizeBytes,
        string sha256,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serverOrigin);
        var normalizedPath = Path.GetFullPath(localPath);
        var normalizedOrigin = NormalizeOrigin(serverOrigin);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await LoadAsync(cancellationToken).ConfigureAwait(false);
            return state.Records.FirstOrDefault(record =>
                record.ServerOrigin.Equals(normalizedOrigin, StringComparison.OrdinalIgnoreCase) &&
                PathEquals(record.LocalPath, normalizedPath) &&
                record.SizeBytes == sizeBytes &&
                record.Sha256.Equals(sha256, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpsertAsync(DirectUploadResumeRecord record, CancellationToken cancellationToken = default)
    {
        Validate(record);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await LoadAsync(cancellationToken).ConfigureAwait(false);
            state.Records.RemoveAll(item => item.SessionId == record.SessionId ||
                item.ServerOrigin.Equals(record.ServerOrigin, StringComparison.OrdinalIgnoreCase) &&
                PathEquals(item.LocalPath, record.LocalPath) &&
                item.SizeBytes == record.SizeBytes &&
                item.Sha256.Equals(record.Sha256, StringComparison.OrdinalIgnoreCase));
            state.Records.Add(Normalize(record));
            await SaveAsync(state, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (state.Records.RemoveAll(record => record.SessionId == sessionId) > 0)
            {
                await SaveAsync(state, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ResumeState> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return new ResumeState();
        }

        await using var stream = new FileStream(
            _path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            32 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var state = await JsonSerializer.DeserializeAsync<ResumeState>(stream, _jsonOptions, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidDataException("Direct upload resume file is empty.");
        if (state.SchemaVersion != 1)
        {
            throw new InvalidDataException("Direct upload resume file has an unsupported schema.");
        }

        state.Records ??= [];
        state.Records = state.Records.Where(IsValid).Select(Normalize).ToList();
        return state;
    }

    private async Task SaveAsync(ResumeState state, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = _path + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                32 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, state, _jsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _path, overwrite: true);
        }
        catch
        {
            File.Delete(temporaryPath);
            throw;
        }
    }

    private static DirectUploadResumeRecord Normalize(DirectUploadResumeRecord record) => record with
    {
        ServerOrigin = record.ServerOrigin.TrimEnd('/') + "/",
        LocalPath = Path.GetFullPath(record.LocalPath),
        Sha256 = record.Sha256.ToUpperInvariant(),
        CompletedParts = new Dictionary<int, string>(record.CompletedParts ?? new Dictionary<int, string>())
    };

    private static bool IsValid(DirectUploadResumeRecord record)
    {
        try
        {
            Validate(record);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void Validate(DirectUploadResumeRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!Uri.TryCreate(record.ServerOrigin, UriKind.Absolute, out var origin) ||
            (!origin.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !origin.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) ||
            !string.IsNullOrEmpty(origin.UserInfo) || record.SizeBytes <= 0 ||
            record.Sha256.Length != 64 || !record.Sha256.All(char.IsAsciiHexDigit) ||
            record.AssetId == Guid.Empty || record.SessionId == Guid.Empty ||
            record.CompletedParts is null ||
            record.CompletedParts.Any(part => part.Key <= 0 || string.IsNullOrWhiteSpace(part.Value)))
        {
            throw new InvalidDataException("Direct upload resume record is invalid.");
        }

        _ = Path.GetFullPath(record.LocalPath);
    }

    private static string NormalizeOrigin(Uri origin) => origin.GetLeftPart(UriPartial.Authority).TrimEnd('/') + "/";

    private static bool PathEquals(string left, string right) => string.Equals(
        Path.GetFullPath(left),
        Path.GetFullPath(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public void Dispose() => _gate.Dispose();

    private sealed class ResumeState
    {
        public int SchemaVersion { get; set; } = 1;
        public List<DirectUploadResumeRecord> Records { get; set; } = [];
    }
}
