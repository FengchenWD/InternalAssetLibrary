using System.Text.Json;
using System.Text.Json.Serialization;

namespace InternalAssetLibrary.Server.Data;

internal interface IAppDataStore
{
    string StorageKind { get; }
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<TResult> ReadAsync<TResult>(Func<AppState, TResult> read, CancellationToken cancellationToken = default);
    Task<TResult> UpdateAsync<TResult>(Func<AppState, TResult> update, CancellationToken cancellationToken = default);
}

internal sealed class AtomicJsonDataStore : IAppDataStore, IDisposable
{
    private readonly string _path;
    private readonly ILogger<AtomicJsonDataStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private AppState? _state;

    public string StorageKind => "development-atomic-json";

    public AtomicJsonDataStore(
        IHostEnvironment environment,
        IConfiguration configuration,
        ILogger<AtomicJsonDataStore> logger)
    {
        var options = configuration.GetSection("DevelopmentStorage").Get<DevelopmentStorageOptions>() ?? new();
        if (!environment.IsDevelopment() && !options.AllowInProduction)
        {
            throw new InvalidOperationException(
                "The atomic JSON store is a development implementation. Configure SQLite before production, " +
                "or explicitly set DevelopmentStorage:AllowInProduction=true for a temporary internal deployment.");
        }

        _path = Path.GetFullPath(options.DataPath, environment.ContentRootPath);
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_state is not null)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            if (!File.Exists(_path))
            {
                _state = new AppState();
                await PersistAsync(_state, cancellationToken);
                return;
            }

            await using (var stream = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                _state = await JsonSerializer.DeserializeAsync<AppState>(stream, _jsonOptions, cancellationToken)
                    ?? throw new InvalidDataException($"Development data file '{_path}' is empty.");
            }

            if (Normalize(_state))
            {
                await PersistAsync(_state, cancellationToken);
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Development data file '{_path}' is invalid JSON. Restore the .bak file instead of overwriting it.",
                exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TResult> ReadAsync<TResult>(
        Func<AppState, TResult> read,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(read);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureInitialized();
            var snapshot = Clone(_state!);
            return read(snapshot);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TResult> UpdateAsync<TResult>(
        Func<AppState, TResult> update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureInitialized();
            var working = Clone(_state!);
            var result = update(working);
            Normalize(working);
            await PersistAsync(working, cancellationToken);
            _state = working;
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task PersistAsync(AppState state, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path)!;
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        var backupPath = _path + ".bak";

        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, state, _jsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_path))
            {
                File.Replace(temporaryPath, _path, backupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, _path);
            }
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    private AppState Clone(AppState state)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, _jsonOptions);
        var clone = JsonSerializer.Deserialize<AppState>(bytes, _jsonOptions)
            ?? throw new InvalidOperationException("Unable to clone the development data store state.");
        _ = Normalize(clone);
        return clone;
    }

    internal static bool Normalize(AppState state)
    {
        if (state.SchemaVersion != 1)
        {
            throw new InvalidDataException($"Unsupported development data schema version {state.SchemaVersion}.");
        }

        var changed = false;
        var now = DateTimeOffset.UtcNow;
        state.Users ??= [];
        state.Sessions ??= [];
        state.Assets ??= [];
        if (state.AssetFolders is null)
        {
            state.AssetFolders = [];
            changed = true;
        }
        state.Tags ??= [];
        state.MarkerSets ??= [];
        state.DownloadLog ??= [];
        state.AuditLog ??= [];
        state.LoginFailures ??= [];
        state.DirectUploadSessions ??= [];
        if (state.TeamLuts is null)
        {
            state.TeamLuts = [];
            changed = true;
        }
        if (state.PendingObjectDeletions is null)
        {
            state.PendingObjectDeletions = [];
            changed = true;
        }
        if (state.ServerSettings is { Initialized: false })
        {
            state.ServerSettings = null;
            changed = true;
        }

        state.LoginFailures.RemoveAll(failure =>
            string.IsNullOrWhiteSpace(failure.IdentifierHash) ||
            string.IsNullOrWhiteSpace(failure.RemoteAddress) ||
            failure.FailureCount <= 0 ||
            failure.FirstFailureAt == default ||
            failure.LastFailureAt == default);
        var uploadSessionIds = new HashSet<Guid>();
        foreach (var session in state.DirectUploadSessions)
        {
            if (session.Id == Guid.Empty || !uploadSessionIds.Add(session.Id) ||
                session.UserId == Guid.Empty || session.AssetId == Guid.Empty ||
                session.AssetVersionId == Guid.Empty || string.IsNullOrWhiteSpace(session.ObjectKey) ||
                string.IsNullOrWhiteSpace(session.ProviderUploadId) || session.SizeBytes <= 0 ||
                session.Sha256.Length != 64 || !session.Sha256.All(char.IsAsciiHexDigit) ||
                session.PartSizeBytes < 5 * 1024 * 1024 ||
                session.CreatedAt == default || session.ExpiresAt <= session.CreatedAt)
            {
                throw new InvalidDataException("The application state contains an invalid direct upload session.");
            }
        }

        var pendingDeletionKeys = new HashSet<string>(StringComparer.Ordinal);
        var normalizedPendingDeletions = new List<PendingObjectDeletionRecord>();
        foreach (var pending in state.PendingObjectDeletions)
        {
            if (string.IsNullOrWhiteSpace(pending.ObjectKey) ||
                !string.Equals(pending.ObjectKey, pending.ObjectKey.Trim(), StringComparison.Ordinal) ||
                pending.ObjectKey.Length > 1_024)
            {
                throw new InvalidDataException("The application state contains an invalid pending object deletion key.");
            }

            if (!pendingDeletionKeys.Add(pending.ObjectKey + "\n" + pending.ProviderUploadId))
            {
                changed = true;
                continue;
            }

            if (pending.CreatedAt == default)
            {
                pending.CreatedAt = now;
                changed = true;
            }

            if (pending.FailureCount < 0)
            {
                pending.FailureCount = 0;
                changed = true;
            }

            normalizedPendingDeletions.Add(pending);
        }

        if (normalizedPendingDeletions.Count != state.PendingObjectDeletions.Count)
        {
            changed = true;
        }
        state.PendingObjectDeletions = normalizedPendingDeletions;
        foreach (var user in state.Users)
        {
            user.Permissions = new HashSet<string>(user.Permissions ?? [], StringComparer.OrdinalIgnoreCase);
        }

        changed |= NormalizeAssetFolders(state, now);
        var folderIds = state.AssetFolders.Select(folder => folder.Id).ToHashSet();

        var tagIds = new HashSet<Guid>();
        var tagsByName = new Dictionary<string, TagRecord>(StringComparer.OrdinalIgnoreCase);
        var normalizedTags = new List<TagRecord>();
        foreach (var tag in state.Tags)
        {
            var name = tag.Name?.Trim() ?? string.Empty;
            if (name.Length is < 1 or > 32)
            {
                throw new InvalidDataException("The application state contains an invalid global tag name.");
            }

            if (tagsByName.ContainsKey(name))
            {
                changed = true;
                continue;
            }

            if (tag.Id == Guid.Empty || !tagIds.Add(tag.Id))
            {
                tag.Id = Guid.NewGuid();
                tagIds.Add(tag.Id);
                changed = true;
            }

            if (!string.Equals(tag.Name, name, StringComparison.Ordinal))
            {
                tag.Name = name;
                changed = true;
            }

            if (tag.CreatedAt == default)
            {
                tag.CreatedAt = now;
                changed = true;
            }

            if (tag.UpdatedAt == default)
            {
                tag.UpdatedAt = tag.CreatedAt;
                changed = true;
            }

            tagsByName.Add(name, tag);
            normalizedTags.Add(tag);
        }

        if (normalizedTags.Count != state.Tags.Count)
        {
            changed = true;
        }
        state.Tags = normalizedTags;

        foreach (var asset in state.Assets)
        {
            if (asset.FolderId is { } folderId && !folderIds.Contains(folderId))
            {
                throw new InvalidDataException("The application state contains an asset assigned to a missing folder.");
            }

            var uploader = state.Users.FirstOrDefault(user => user.Id == asset.UploadedByUserId);
            if (uploader is not null)
            {
                if (string.IsNullOrWhiteSpace(asset.UploadedByUsername))
                {
                    asset.UploadedByUsername = uploader.Username;
                    changed = true;
                }

                if (string.IsNullOrWhiteSpace(asset.UploadedByDisplayName))
                {
                    asset.UploadedByDisplayName = uploader.DisplayName;
                    changed = true;
                }
            }

            if (asset.CurrentVersionId == Guid.Empty)
            {
                asset.CurrentVersionId = asset.Id;
            }

            var normalizedAssetTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rawTag in asset.Tags ?? [])
            {
                var name = rawTag?.Trim() ?? string.Empty;
                if (name.Length is < 1 or > 32)
                {
                    throw new InvalidDataException("The application state contains an invalid asset tag name.");
                }

                if (!tagsByName.TryGetValue(name, out var catalogTag))
                {
                    catalogTag = new TagRecord
                    {
                        Id = Guid.NewGuid(),
                        Name = name,
                        CreatedByUserId = asset.UploadedByUserId,
                        CreatedAt = asset.UploadedAt == default ? now : asset.UploadedAt,
                        UpdatedAt = asset.UploadedAt == default ? now : asset.UploadedAt
                    };
                    tagsByName.Add(name, catalogTag);
                    normalizedTags.Add(catalogTag);
                    state.AuditLog.Add(new AuditRecord
                    {
                        Id = Guid.NewGuid(),
                        ActorUserId = null,
                        Action = "tag.migrated",
                        TargetType = "tag",
                        TargetId = catalogTag.Id.ToString(),
                        Detail = $"Imported from asset {asset.Id}.",
                        OccurredAt = now
                    });
                    changed = true;
                }

                normalizedAssetTags.Add(catalogTag.Name);
                if (!string.Equals(rawTag, catalogTag.Name, StringComparison.Ordinal))
                {
                    changed = true;
                }
            }

            if (normalizedAssetTags.Count != (asset.Tags?.Count ?? 0))
            {
                changed = true;
            }
            asset.Tags = normalizedAssetTags;
            asset.PreviousVersions ??= [];
            changed |= AssetDerivativeRules.Normalize(asset, now);
        }

        var lutIds = new HashSet<Guid>();
        foreach (var lut in state.TeamLuts)
        {
            if (lut.Id == Guid.Empty || !lutIds.Add(lut.Id))
            {
                throw new InvalidDataException("The application state contains an invalid or duplicate LUT identifier.");
            }

            if (lut.CurrentVersionId == Guid.Empty)
            {
                lut.CurrentVersionId = lut.Id;
                changed = true;
            }

            lut.Name = lut.Name?.Trim() ?? string.Empty;
            lut.OriginalFileName = Path.GetFileName(lut.OriginalFileName?.Trim() ?? string.Empty);
            if (lut.Name.Length is < 1 or > 200 ||
                lut.OriginalFileName.Length is < 1 or > 255 ||
                !Path.GetExtension(lut.OriginalFileName).Equals(".cube", StringComparison.OrdinalIgnoreCase) ||
                lut.SizeBytes < 0 ||
                lut.Version < 1 ||
                string.IsNullOrWhiteSpace(lut.ObjectKey))
            {
                throw new InvalidDataException("The application state contains invalid team LUT metadata.");
            }

            lut.Notes = string.IsNullOrWhiteSpace(lut.Notes) ? null : lut.Notes.Trim();
            var uploader = state.Users.FirstOrDefault(user => user.Id == lut.UploadedByUserId);
            if (uploader is not null)
            {
                if (string.IsNullOrWhiteSpace(lut.UploadedByUsername))
                {
                    lut.UploadedByUsername = uploader.Username;
                    changed = true;
                }

                if (string.IsNullOrWhiteSpace(lut.UploadedByDisplayName))
                {
                    lut.UploadedByDisplayName = uploader.DisplayName;
                    changed = true;
                }
            }
        }

        foreach (var markerSet in state.MarkerSets)
        {
            markerSet.Markers ??= [];
            if (string.IsNullOrWhiteSpace(markerSet.OwnerDisplayName))
            {
                var owner = state.Users.FirstOrDefault(user => user.Id == markerSet.OwnerUserId);
                if (owner is not null)
                {
                    markerSet.OwnerDisplayName = string.IsNullOrWhiteSpace(owner.DisplayName)
                        ? owner.Username
                        : owner.DisplayName;
                    changed = true;
                }
            }
        }

        return changed;
    }

    private static bool NormalizeAssetFolders(AppState state, DateTimeOffset now)
    {
        var changed = false;
        var foldersById = new Dictionary<Guid, AssetFolderRecord>();
        var siblingNames = new Dictionary<Guid, HashSet<string>>();
        foreach (var folder in state.AssetFolders)
        {
            if (folder.Id == Guid.Empty || !foldersById.TryAdd(folder.Id, folder))
            {
                throw new InvalidDataException("The application state contains an invalid or duplicate asset folder identifier.");
            }

            var name = folder.Name?.Trim() ?? string.Empty;
            if (!AssetFolderNameRules.IsValid(name))
            {
                throw new InvalidDataException("The application state contains an invalid asset folder name.");
            }

            if (!string.Equals(folder.Name, name, StringComparison.Ordinal))
            {
                folder.Name = name;
                changed = true;
            }

            if (folder.CreatedByUserId == Guid.Empty || folder.ParentId == folder.Id)
            {
                throw new InvalidDataException("The application state contains invalid asset folder ownership or ancestry.");
            }

            var creator = state.Users.FirstOrDefault(user => user.Id == folder.CreatedByUserId);
            if (creator is not null)
            {
                if (string.IsNullOrWhiteSpace(folder.CreatedByUsername))
                {
                    folder.CreatedByUsername = creator.Username;
                    changed = true;
                }

                if (string.IsNullOrWhiteSpace(folder.CreatedByDisplayName))
                {
                    folder.CreatedByDisplayName = creator.DisplayName;
                    changed = true;
                }
            }

            if (folder.CreatedAt == default)
            {
                folder.CreatedAt = now;
                changed = true;
            }

            if (folder.UpdatedAt == default)
            {
                folder.UpdatedAt = folder.CreatedAt;
                changed = true;
            }

            var siblingKey = folder.ParentId ?? Guid.Empty;
            if (!siblingNames.TryGetValue(siblingKey, out var names))
            {
                names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                siblingNames.Add(siblingKey, names);
            }

            if (!names.Add(name))
            {
                throw new InvalidDataException("The application state contains duplicate asset folder names under one parent.");
            }
        }

        foreach (var folder in state.AssetFolders)
        {
            var visited = new HashSet<Guid> { folder.Id };
            var parentId = folder.ParentId;
            while (parentId is { } currentId)
            {
                if (!foldersById.TryGetValue(currentId, out var parent))
                {
                    throw new InvalidDataException("The application state contains an asset folder with a missing parent.");
                }

                if (!visited.Add(currentId))
                {
                    throw new InvalidDataException("The application state contains an asset folder ancestry cycle.");
                }

                parentId = parent.ParentId;
            }
        }

        return changed;
    }

    private void EnsureInitialized()
    {
        if (_state is null)
        {
            throw new InvalidOperationException("The development data store has not been initialized.");
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not remove temporary data file {Path}.", path);
        }
    }

    public void Dispose() => _gate.Dispose();
}

internal static class AssetFolderNameRules
{
    public static bool IsValid(string name) =>
        name.Length is >= 1 and <= 100 &&
        name is not "." and not ".." &&
        !name.Any(character => char.IsControl(character) || character is '/' or '\\');
}
