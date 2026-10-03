using InternalAssetLibrary.Contracts;

namespace InternalAssetLibrary.Core;

public sealed record MarkerSetPrincipal(
    Guid UserId,
    string DisplayName,
    UserRole Role,
    UserPermission Permissions);

public sealed class InMemoryMarkerSetService
{
    public const int MarkerSetNameMaxLength = 100;

    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<Guid, StoredMarkerSet> _sets = [];
    private readonly Dictionary<Guid, Guid> _currentVersions = [];
    private readonly Dictionary<(Guid AssetId, Guid VersionId), TimeSpan?> _durations = [];

    public InMemoryMarkerSetService(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public void SetCurrentAssetVersion(Guid assetId, Guid versionId, TimeSpan? duration = null)
    {
        RequireId(assetId, nameof(assetId));
        RequireId(versionId, nameof(versionId));
        ValidateDuration(duration);

        lock (_sync)
        {
            _currentVersions[assetId] = versionId;
            _durations[(assetId, versionId)] = duration;
        }
    }

    public IReadOnlyList<MarkerSetSummary> ListForAsset(MarkerSetPrincipal viewer, Guid assetId)
    {
        EnsureCanView(viewer);
        RequireId(assetId, nameof(assetId));

        lock (_sync)
        {
            return _sets.Values
                .Where(set => set.AssetId == assetId)
                .OrderByDescending(set => set.UpdatedAt)
                .ThenBy(set => set.Name, StringComparer.OrdinalIgnoreCase)
                .Select(ToSummary)
                .ToArray();
        }
    }

    public MarkerSetDetail Get(MarkerSetPrincipal viewer, Guid markerSetId)
    {
        EnsureCanView(viewer);
        lock (_sync)
        {
            return ToDetail(GetStored(markerSetId));
        }
    }

    public MarkerSetAccess GetAccess(MarkerSetPrincipal viewer, Guid markerSetId)
    {
        ValidatePrincipal(viewer);
        lock (_sync)
        {
            var set = GetStored(markerSetId);
            var canView = Has(viewer, UserPermission.Browse);
            var ownsSet = set.OwnerUserId == viewer.UserId;
            return new MarkerSetAccess(
                canView,
                canView && ownsSet && Has(viewer, UserPermission.EditOwnMarkers),
                canView && CanDelete(viewer, set),
                canView && Has(viewer, UserPermission.EditOwnMarkers));
        }
    }

    public MarkerSetDetail Create(MarkerSetPrincipal owner, CreateMarkerSetRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureCanCreate(owner);
        RequireId(request.AssetId, nameof(request.AssetId));
        RequireId(request.AssetVersionId, nameof(request.AssetVersionId));
        var name = ValidateName(request.Name);

        lock (_sync)
        {
            var now = _timeProvider.GetUtcNow();
            var set = new StoredMarkerSet(
                Guid.NewGuid(),
                request.AssetId,
                request.AssetVersionId,
                name,
                owner.UserId,
                NormalizeDisplayName(owner),
                now,
                now,
                []);
            _sets.Add(set.Id, set);
            return ToDetail(set);
        }
    }

    public MarkerSetDetail Rename(
        MarkerSetPrincipal actor,
        Guid markerSetId,
        RenameMarkerSetRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = ValidateName(request.Name);

        lock (_sync)
        {
            var set = GetStored(markerSetId);
            EnsureCanEdit(actor, set);
            set = set with { Name = name, UpdatedAt = _timeProvider.GetUtcNow() };
            _sets[set.Id] = set;
            return ToDetail(set);
        }
    }

    public MarkerSetDetail CopyToOwn(MarkerSetPrincipal actor, CopyMarkerSetRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureCanCreate(actor);
        var name = ValidateName(request.Name);

        lock (_sync)
        {
            var source = GetStored(request.SourceMarkerSetId);
            var now = _timeProvider.GetUtcNow();
            var copy = new StoredMarkerSet(
                Guid.NewGuid(),
                source.AssetId,
                source.AssetVersionId,
                name,
                actor.UserId,
                NormalizeDisplayName(actor),
                now,
                now,
                source.Markers
                    .Select(marker => marker with { Id = Guid.NewGuid() })
                    .ToArray());
            _sets.Add(copy.Id, copy);
            return ToDetail(copy);
        }
    }

    public MarkerSetDetail ImportCsv(
        MarkerSetPrincipal actor,
        ImportMarkerSetRequest request,
        ReadOnlySpan<byte> csv)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureCanCreate(actor);
        var document = MarkersCsv.Read(csv);
        RequireId(request.AssetId, nameof(request.AssetId));
        RequireId(request.AssetVersionId, nameof(request.AssetVersionId));
        var name = ValidateName(request.Name);

        lock (_sync)
        {
            var registeredDuration = GetDuration(request.AssetId, request.AssetVersionId);
            foreach (var marker in document.Markers)
            {
                ValidateMarkerTime(marker.Time, registeredDuration);
            }

            var now = _timeProvider.GetUtcNow();
            var set = new StoredMarkerSet(
                Guid.NewGuid(),
                request.AssetId,
                request.AssetVersionId,
                name,
                actor.UserId,
                NormalizeDisplayName(actor),
                now,
                now,
                document.Markers
                    .Select(marker => new MarkerItem(Guid.NewGuid(), marker.Time, marker.Name, marker.Note))
                    .ToArray());
            _sets.Add(set.Id, set);
            return ToDetail(set);
        }
    }

    public byte[] ExportCsv(
        MarkerSetPrincipal viewer,
        Guid markerSetId,
        MarkerCsvRecordingInfo recording)
    {
        ArgumentNullException.ThrowIfNull(recording);
        EnsureCanView(viewer);

        lock (_sync)
        {
            var set = GetStored(markerSetId);
            var duration = recording.RecordingDuration ?? GetDuration(set.AssetId, set.AssetVersionId);
            return MarkersCsv.Write(new MarkerCsvDocument(
                recording.RecordingName,
                recording.RecordingPath,
                recording.RecordingStartedAt,
                duration,
                set.Markers
                    .OrderBy(marker => marker.Time)
                    .Select(marker => new MarkerCsvEntry(marker.Time, marker.Name, marker.Note))
                    .ToArray()));
        }
    }

    public MarkerItem AddMarker(
        MarkerSetPrincipal actor,
        Guid markerSetId,
        UpsertMarkerRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (_sync)
        {
            var set = GetStored(markerSetId);
            EnsureCanEdit(actor, set);
            ValidateMarkerTime(request.Time, GetDuration(set.AssetId, set.AssetVersionId));
            var marker = new MarkerItem(Guid.NewGuid(), request.Time, request.Name, request.Note);
            set = set with
            {
                Markers = set.Markers.Append(marker).OrderBy(item => item.Time).ToArray(),
                UpdatedAt = _timeProvider.GetUtcNow()
            };
            _sets[set.Id] = set;
            return marker;
        }
    }

    public MarkerItem UpdateMarker(
        MarkerSetPrincipal actor,
        Guid markerSetId,
        Guid markerId,
        UpsertMarkerRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireId(markerId, nameof(markerId));

        lock (_sync)
        {
            var set = GetStored(markerSetId);
            EnsureCanEdit(actor, set);
            ValidateMarkerTime(request.Time, GetDuration(set.AssetId, set.AssetVersionId));
            if (!set.Markers.Any(marker => marker.Id == markerId))
            {
                throw new KeyNotFoundException($"Marker '{markerId}' does not exist.");
            }

            var updated = new MarkerItem(markerId, request.Time, request.Name, request.Note);
            set = set with
            {
                Markers = set.Markers
                    .Select(marker => marker.Id == markerId ? updated : marker)
                    .OrderBy(marker => marker.Time)
                    .ToArray(),
                UpdatedAt = _timeProvider.GetUtcNow()
            };
            _sets[set.Id] = set;
            return updated;
        }
    }

    public void DeleteMarker(MarkerSetPrincipal actor, Guid markerSetId, Guid markerId)
    {
        RequireId(markerId, nameof(markerId));

        lock (_sync)
        {
            var set = GetStored(markerSetId);
            EnsureCanDelete(actor, set);
            var markers = set.Markers.Where(marker => marker.Id != markerId).ToArray();
            if (markers.Length == set.Markers.Count)
            {
                throw new KeyNotFoundException($"Marker '{markerId}' does not exist.");
            }

            _sets[set.Id] = set with
            {
                Markers = markers,
                UpdatedAt = _timeProvider.GetUtcNow()
            };
        }
    }

    public void DeleteSet(MarkerSetPrincipal actor, Guid markerSetId)
    {
        lock (_sync)
        {
            var set = GetStored(markerSetId);
            EnsureCanDelete(actor, set);
            _sets.Remove(set.Id);
        }
    }

    private MarkerSetSummary ToSummary(StoredMarkerSet set) => new(
        set.Id,
        set.AssetId,
        set.AssetVersionId,
        set.Name,
        set.OwnerUserId,
        set.OwnerDisplayName,
        set.Markers.Count,
        IsOldVersion(set),
        set.UpdatedAt);

    private MarkerSetDetail ToDetail(StoredMarkerSet set) => new(
        set.Id,
        set.AssetId,
        set.AssetVersionId,
        set.Name,
        set.OwnerUserId,
        set.OwnerDisplayName,
        IsOldVersion(set),
        set.CreatedAt,
        set.UpdatedAt,
        set.Markers.ToArray());

    private bool IsOldVersion(StoredMarkerSet set) =>
        _currentVersions.TryGetValue(set.AssetId, out var currentVersion) &&
        currentVersion != set.AssetVersionId;

    private TimeSpan? GetDuration(Guid assetId, Guid versionId) =>
        _durations.GetValueOrDefault((assetId, versionId));

    private StoredMarkerSet GetStored(Guid markerSetId)
    {
        RequireId(markerSetId, nameof(markerSetId));
        return _sets.TryGetValue(markerSetId, out var set)
            ? set
            : throw new KeyNotFoundException($"Marker set '{markerSetId}' does not exist.");
    }

    private static void EnsureCanView(MarkerSetPrincipal principal)
    {
        ValidatePrincipal(principal);
        if (!Has(principal, UserPermission.Browse))
        {
            throw new UnauthorizedAccessException("The user cannot view marker sets.");
        }
    }

    private static void EnsureCanCreate(MarkerSetPrincipal principal)
    {
        ValidatePrincipal(principal);
        if (!Has(principal, UserPermission.Browse) ||
            !Has(principal, UserPermission.EditOwnMarkers))
        {
            throw new UnauthorizedAccessException("The user cannot create or copy marker sets.");
        }
    }

    private static void EnsureCanEdit(MarkerSetPrincipal principal, StoredMarkerSet set)
    {
        ValidatePrincipal(principal);
        if (set.OwnerUserId != principal.UserId ||
            !Has(principal, UserPermission.Browse) ||
            !Has(principal, UserPermission.EditOwnMarkers))
        {
            throw new UnauthorizedAccessException("Only the owner may edit this marker set.");
        }
    }

    private static void EnsureCanDelete(MarkerSetPrincipal principal, StoredMarkerSet set)
    {
        ValidatePrincipal(principal);
        if (!CanDelete(principal, set))
        {
            throw new UnauthorizedAccessException("Only the owner or an authorized administrator may delete markers.");
        }
    }

    private static bool CanDelete(MarkerSetPrincipal principal, StoredMarkerSet set) =>
        Has(principal, UserPermission.Browse) &&
        (set.OwnerUserId == principal.UserId && Has(principal, UserPermission.EditOwnMarkers) ||
         set.OwnerUserId != principal.UserId && principal.Role == UserRole.Administrator &&
         Has(principal, UserPermission.ManageAssets));

    private static bool Has(MarkerSetPrincipal principal, UserPermission permission) =>
        (principal.Permissions & permission) == permission;

    private static void ValidatePrincipal(MarkerSetPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        RequireId(principal.UserId, nameof(principal.UserId));
    }

    private static string NormalizeDisplayName(MarkerSetPrincipal principal) =>
        string.IsNullOrWhiteSpace(principal.DisplayName) ? principal.UserId.ToString() : principal.DisplayName.Trim();

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

    private static void ValidateMarkerTime(TimeSpan time, TimeSpan? duration)
    {
        if (time < TimeSpan.Zero ||
            time.Ticks % TimeSpan.TicksPerMillisecond != 0 ||
            duration is not null && time > duration.Value)
        {
            throw new ArgumentOutOfRangeException(nameof(time), "Marker time is invalid for this asset version.");
        }
    }

    private static void ValidateDuration(TimeSpan? duration)
    {
        if (duration < TimeSpan.Zero ||
            duration is not null && duration.Value.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }
    }

    private static void RequireId(Guid id, string parameterName)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Identifier cannot be empty.", parameterName);
        }
    }

    private sealed record StoredMarkerSet(
        Guid Id,
        Guid AssetId,
        Guid AssetVersionId,
        string Name,
        Guid OwnerUserId,
        string OwnerDisplayName,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        IReadOnlyList<MarkerItem> Markers);
}
