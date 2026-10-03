using System.Text.Json;
using InternalAssetLibrary.Contracts;
using InternalAssetLibrary.Core;

namespace InternalAssetLibrary.Client.Core.Markers;

public sealed class LocalMarkerService : IDisposable
{
    public const int MarkerSetNameMaxLength = 100;

    private const int CurrentSchemaVersion = 1;
    private static readonly MarkerCsvRecordingInfo EmptyRecordingInfo = new(null, null, null, null);
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _timeProvider;

    public LocalMarkerService(string filePath, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<IReadOnlyList<LocalMarkerSetSummary>> ListAsync(
        Guid localAssetId,
        CancellationToken cancellationToken = default)
    {
        RequireId(localAssetId, nameof(localAssetId));
        return ReadDocumentAsync<IReadOnlyList<LocalMarkerSetSummary>>(document =>
        {
            var asset = FindAsset(document, localAssetId);
            return asset is null
                ? []
                : asset.MarkerSets
                    .OrderByDescending(set => set.UpdatedAtUtc)
                    .ThenBy(set => set.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(set => ToSummary(set, localAssetId))
                    .ToArray();
        }, cancellationToken);
    }

    public Task<LocalMarkerSetDetail> GetAsync(
        Guid localAssetId,
        Guid markerSetId,
        CancellationToken cancellationToken = default)
    {
        RequireIds(localAssetId, markerSetId);
        return ReadDocumentAsync(
            document => ToDetail(GetSet(document, localAssetId, markerSetId), localAssetId),
            cancellationToken);
    }

    public Task<LocalMarkerSetDetail> CreateAsync(
        Guid localAssetId,
        string name,
        TimeSpan? assetDuration = null,
        MarkerCsvRecordingInfo? recordingInfo = null,
        CancellationToken cancellationToken = default)
    {
        RequireId(localAssetId, nameof(localAssetId));
        var normalizedName = ValidateName(name);
        ValidateDuration(assetDuration);
        ValidateRecordingInfo(recordingInfo);

        return MutateDocumentAsync(document =>
        {
            var now = _timeProvider.GetUtcNow();
            var set = new StoredMarkerSet
            {
                Id = Guid.NewGuid(),
                Name = normalizedName,
                AssetDuration = assetDuration ?? recordingInfo?.RecordingDuration,
                RecordingInfo = recordingInfo ?? EmptyRecordingInfo,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            GetOrCreateAsset(document, localAssetId).MarkerSets.Add(set);
            return ToDetail(set, localAssetId);
        }, cancellationToken);
    }

    public Task<LocalMarkerSetDetail> RenameAsync(
        Guid localAssetId,
        Guid markerSetId,
        string name,
        CancellationToken cancellationToken = default)
    {
        RequireIds(localAssetId, markerSetId);
        var normalizedName = ValidateName(name);
        return MutateDocumentAsync(document =>
        {
            var set = GetSet(document, localAssetId, markerSetId);
            set.Name = normalizedName;
            set.UpdatedAtUtc = _timeProvider.GetUtcNow();
            return ToDetail(set, localAssetId);
        }, cancellationToken);
    }

    public Task<MarkerItem> AddMarkerAsync(
        Guid localAssetId,
        Guid markerSetId,
        UpsertMarkerRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireIds(localAssetId, markerSetId);
        return MutateDocumentAsync(document =>
        {
            var set = GetSet(document, localAssetId, markerSetId);
            ValidateMarkerTime(request.Time, GetMarkerTimeLimit(set));
            var marker = new MarkerItem(Guid.NewGuid(), request.Time, request.Name, request.Note);
            set.Markers.Add(marker);
            SortMarkers(set.Markers);
            set.UpdatedAtUtc = _timeProvider.GetUtcNow();
            return marker;
        }, cancellationToken);
    }

    public Task<MarkerItem> UpdateMarkerAsync(
        Guid localAssetId,
        Guid markerSetId,
        Guid markerId,
        UpsertMarkerRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireIds(localAssetId, markerSetId);
        RequireId(markerId, nameof(markerId));
        return MutateDocumentAsync(document =>
        {
            var set = GetSet(document, localAssetId, markerSetId);
            ValidateMarkerTime(request.Time, GetMarkerTimeLimit(set));
            var index = set.Markers.FindIndex(marker => marker.Id == markerId);
            if (index < 0)
            {
                throw new KeyNotFoundException($"Marker '{markerId}' does not exist.");
            }

            var marker = new MarkerItem(markerId, request.Time, request.Name, request.Note);
            set.Markers[index] = marker;
            SortMarkers(set.Markers);
            set.UpdatedAtUtc = _timeProvider.GetUtcNow();
            return marker;
        }, cancellationToken);
    }

    public Task DeleteMarkerAsync(
        Guid localAssetId,
        Guid markerSetId,
        Guid markerId,
        CancellationToken cancellationToken = default)
    {
        RequireIds(localAssetId, markerSetId);
        RequireId(markerId, nameof(markerId));
        return MutateDocumentAsync(document =>
        {
            var set = GetSet(document, localAssetId, markerSetId);
            if (set.Markers.RemoveAll(marker => marker.Id == markerId) == 0)
            {
                throw new KeyNotFoundException($"Marker '{markerId}' does not exist.");
            }

            set.UpdatedAtUtc = _timeProvider.GetUtcNow();
        }, cancellationToken);
    }

    public Task DeleteMarkerSetAsync(
        Guid localAssetId,
        Guid markerSetId,
        CancellationToken cancellationToken = default)
    {
        RequireIds(localAssetId, markerSetId);
        return MutateDocumentAsync(document =>
        {
            var asset = FindAsset(document, localAssetId);
            if (asset is null || asset.MarkerSets.RemoveAll(set => set.Id == markerSetId) == 0)
            {
                throw new KeyNotFoundException($"Marker set '{markerSetId}' does not exist for this local asset.");
            }

            if (asset.MarkerSets.Count == 0)
            {
                document.Assets.Remove(asset);
            }
        }, cancellationToken);
    }

    public Task<LocalMarkerSetDetail> ImportCsvAsync(
        Guid localAssetId,
        string name,
        ReadOnlyMemory<byte> csv,
        TimeSpan? assetDuration = null,
        CancellationToken cancellationToken = default)
    {
        RequireId(localAssetId, nameof(localAssetId));
        var normalizedName = ValidateName(name);
        ValidateDuration(assetDuration);
        var csvDocument = MarkersCsv.Read(csv.Span);
        var markerTimeLimit = assetDuration ?? csvDocument.RecordingDuration;
        foreach (var marker in csvDocument.Markers)
        {
            ValidateMarkerTime(marker.Time, markerTimeLimit);
        }

        return MutateDocumentAsync(document =>
        {
            var now = _timeProvider.GetUtcNow();
            var set = new StoredMarkerSet
            {
                Id = Guid.NewGuid(),
                Name = normalizedName,
                AssetDuration = markerTimeLimit,
                RecordingInfo = new MarkerCsvRecordingInfo(
                    csvDocument.RecordingName,
                    csvDocument.RecordingPath,
                    csvDocument.RecordingStartedAt,
                    csvDocument.RecordingDuration),
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                Markers = csvDocument.Markers
                    .Select(marker => new MarkerItem(Guid.NewGuid(), marker.Time, marker.Name, marker.Note))
                    .ToList()
            };
            GetOrCreateAsset(document, localAssetId).MarkerSets.Add(set);
            return ToDetail(set, localAssetId);
        }, cancellationToken);
    }

    public Task<byte[]> ExportCsvAsync(
        Guid localAssetId,
        Guid markerSetId,
        MarkerCsvRecordingInfo? recordingInfo = null,
        CancellationToken cancellationToken = default)
    {
        RequireIds(localAssetId, markerSetId);
        ValidateRecordingInfo(recordingInfo);
        return ReadDocumentAsync(document =>
        {
            var set = GetSet(document, localAssetId, markerSetId);
            var recording = recordingInfo ?? set.RecordingInfo;
            return MarkersCsv.Write(new MarkerCsvDocument(
                recording.RecordingName,
                recording.RecordingPath,
                recording.RecordingStartedAt,
                recording.RecordingDuration ?? set.AssetDuration,
                set.Markers
                    .Select(marker => new MarkerCsvEntry(marker.Time, marker.Name, marker.Note))
                    .ToArray()));
        }, cancellationToken);
    }

    private async Task<T> ReadDocumentAsync<T>(
        Func<StoreDocument, T> read,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return read(await LoadAsync(cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<T> MutateDocumentAsync<T>(
        Func<StoreDocument, T> mutate,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = await LoadAsync(cancellationToken).ConfigureAwait(false);
            var result = mutate(document);
            await SaveAsync(document, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task MutateDocumentAsync(
        Action<StoreDocument> mutate,
        CancellationToken cancellationToken)
    {
        await MutateDocumentAsync(document =>
        {
            mutate(document);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<StoreDocument> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
        {
            return new StoreDocument();
        }

        try
        {
            await using var stream = new FileStream(
                _filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var document = await JsonSerializer.DeserializeAsync<StoreDocument>(
                stream,
                SerializerOptions,
                cancellationToken).ConfigureAwait(false);
            if (document is null || document.SchemaVersion != CurrentSchemaVersion)
            {
                throw new InvalidDataException("The local marker file has an unsupported schema.");
            }

            ValidateStoredDocument(document);
            return document;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The local marker file is not valid JSON.", exception);
        }
    }

    private async Task SaveAsync(StoreDocument document, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = _filePath + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    document,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, _filePath, true);
        }
        catch
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            throw;
        }
    }

    private static void ValidateStoredDocument(StoreDocument document)
    {
        if (document.Assets is null)
        {
            throw new InvalidDataException("The local marker file does not contain an asset collection.");
        }

        var assetIds = new HashSet<Guid>();
        var setIds = new HashSet<Guid>();
        foreach (var asset in document.Assets)
        {
            if (asset is null || asset.LocalAssetId == Guid.Empty || !assetIds.Add(asset.LocalAssetId) ||
                asset.MarkerSets is null)
            {
                throw new InvalidDataException("The local marker file contains an invalid asset entry.");
            }

            foreach (var set in asset.MarkerSets)
            {
                if (set is null || set.Id == Guid.Empty || !setIds.Add(set.Id) || set.RecordingInfo is null ||
                    set.Markers is null)
                {
                    throw new InvalidDataException("The local marker file contains an invalid marker set.");
                }

                try
                {
                    _ = ValidateName(set.Name);
                    ValidateDuration(set.AssetDuration);
                    ValidateRecordingInfo(set.RecordingInfo);
                }
                catch (ArgumentException exception)
                {
                    throw new InvalidDataException("The local marker file contains invalid marker set metadata.", exception);
                }

                var markerIds = new HashSet<Guid>();
                TimeSpan? previousTime = null;
                foreach (var marker in set.Markers)
                {
                    if (marker is null || marker.Id == Guid.Empty || !markerIds.Add(marker.Id))
                    {
                        throw new InvalidDataException("The local marker file contains an invalid marker.");
                    }

                    try
                    {
                        ValidateMarkerTime(marker.Time, GetMarkerTimeLimit(set));
                    }
                    catch (ArgumentOutOfRangeException exception)
                    {
                        throw new InvalidDataException("The local marker file contains an invalid marker time.", exception);
                    }

                    if (previousTime is not null && marker.Time < previousTime.Value)
                    {
                        throw new InvalidDataException("Markers in the local marker file are not ordered by time.");
                    }

                    previousTime = marker.Time;
                }
            }
        }
    }

    private static StoredAssetMarkers? FindAsset(StoreDocument document, Guid localAssetId) =>
        document.Assets.SingleOrDefault(asset => asset.LocalAssetId == localAssetId);

    private static StoredAssetMarkers GetOrCreateAsset(StoreDocument document, Guid localAssetId)
    {
        var asset = FindAsset(document, localAssetId);
        if (asset is not null)
        {
            return asset;
        }

        asset = new StoredAssetMarkers { LocalAssetId = localAssetId };
        document.Assets.Add(asset);
        return asset;
    }

    private static StoredMarkerSet GetSet(StoreDocument document, Guid localAssetId, Guid markerSetId) =>
        FindAsset(document, localAssetId)?.MarkerSets.SingleOrDefault(set => set.Id == markerSetId)
        ?? throw new KeyNotFoundException(
            $"Marker set '{markerSetId}' does not exist for local asset '{localAssetId}'.");

    private static LocalMarkerSetSummary ToSummary(StoredMarkerSet set, Guid localAssetId) => new(
        set.Id,
        localAssetId,
        set.Name,
        set.Markers.Count,
        set.CreatedAtUtc,
        set.UpdatedAtUtc);

    private static LocalMarkerSetDetail ToDetail(StoredMarkerSet set, Guid localAssetId) => new(
        set.Id,
        localAssetId,
        set.Name,
        set.AssetDuration,
        set.RecordingInfo,
        set.CreatedAtUtc,
        set.UpdatedAtUtc,
        set.Markers.ToArray());

    private static string ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Marker set name is required.", nameof(name));
        }

        var normalized = name.Trim();
        if (normalized.Length > MarkerSetNameMaxLength)
        {
            throw new ArgumentException(
                $"Marker set name cannot exceed {MarkerSetNameMaxLength} characters.",
                nameof(name));
        }

        return normalized;
    }

    private static void ValidateMarkerTime(TimeSpan time, TimeSpan? limit)
    {
        if (time < TimeSpan.Zero ||
            time.Ticks % TimeSpan.TicksPerMillisecond != 0 ||
            limit is not null && time > limit.Value)
        {
            throw new ArgumentOutOfRangeException(
                nameof(time),
                "Marker time must be non-negative, use millisecond precision, and not exceed the asset duration.");
        }
    }

    private static void ValidateDuration(TimeSpan? duration)
    {
        if (duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Asset duration cannot be negative.");
        }
    }

    private static void ValidateRecordingInfo(MarkerCsvRecordingInfo? recordingInfo)
    {
        if (recordingInfo?.RecordingDuration < TimeSpan.Zero ||
            recordingInfo?.RecordingDuration is { } duration &&
            duration.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(recordingInfo),
                "CSV recording duration must be non-negative and use whole-second precision.");
        }
    }

    private static TimeSpan? GetMarkerTimeLimit(StoredMarkerSet set)
    {
        if (set.AssetDuration is null)
        {
            return set.RecordingInfo.RecordingDuration;
        }

        if (set.RecordingInfo.RecordingDuration is null)
        {
            return set.AssetDuration;
        }

        return set.AssetDuration < set.RecordingInfo.RecordingDuration
            ? set.AssetDuration
            : set.RecordingInfo.RecordingDuration;
    }

    private static void SortMarkers(List<MarkerItem> markers)
    {
        var ordered = markers.OrderBy(marker => marker.Time).ToArray();
        markers.Clear();
        markers.AddRange(ordered);
    }

    private static void RequireIds(Guid localAssetId, Guid markerSetId)
    {
        RequireId(localAssetId, nameof(localAssetId));
        RequireId(markerSetId, nameof(markerSetId));
    }

    private static void RequireId(Guid id, string parameterName)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Identifier cannot be empty.", parameterName);
        }
    }

    public void Dispose() => _gate.Dispose();

    private sealed class StoreDocument
    {
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        public List<StoredAssetMarkers> Assets { get; set; } = [];
    }

    private sealed class StoredAssetMarkers
    {
        public Guid LocalAssetId { get; set; }

        public List<StoredMarkerSet> MarkerSets { get; set; } = [];
    }

    private sealed class StoredMarkerSet
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public TimeSpan? AssetDuration { get; set; }

        public MarkerCsvRecordingInfo RecordingInfo { get; set; } = EmptyRecordingInfo;

        public DateTimeOffset CreatedAtUtc { get; set; }

        public DateTimeOffset UpdatedAtUtc { get; set; }

        public List<MarkerItem> Markers { get; set; } = [];
    }
}
