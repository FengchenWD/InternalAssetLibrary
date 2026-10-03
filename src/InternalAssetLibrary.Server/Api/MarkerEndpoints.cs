using System.Globalization;
using InternalAssetLibrary.Contracts;
using InternalAssetLibrary.Core;
using InternalAssetLibrary.Server.Data;
using InternalAssetLibrary.Server.Realtime;
using InternalAssetLibrary.Server.Security;

namespace InternalAssetLibrary.Server.Api;

internal static class MarkerEndpoints
{
    private const int CsvMaxBytes = 8 * 1024 * 1024;
    private const int MarkerSetNameMaxLength = 100;
    private const int MarkerNameMaxLength = 200;
    private const int MarkerNoteMaxLength = 2_000;

    public static void MapMarkerEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/assets/{assetId:guid}/marker-sets", ListAsync);
        app.MapGet("/api/marker-sets/{markerSetId:guid}", GetAsync);
        app.MapGet("/api/marker-sets/{markerSetId:guid}/access", GetAccessAsync);
        app.MapGet("/api/marker-sets/{markerSetId:guid}/csv", ExportCsvAsync);
        app.MapPost("/api/marker-sets", CreateAsync);
        app.MapPost("/api/marker-sets/copy", CopyAsync);
        app.MapPost("/api/marker-sets/import", ImportCsvAsync);
        app.MapPut("/api/marker-sets/{markerSetId:guid}", RenameAsync);
        app.MapDelete("/api/marker-sets/{markerSetId:guid}", DeleteSetAsync);
        app.MapPost("/api/marker-sets/{markerSetId:guid}/markers", AddMarkerAsync);
        app.MapPut("/api/marker-sets/{markerSetId:guid}/markers/{markerId:guid}", UpdateMarkerAsync);
        app.MapDelete("/api/marker-sets/{markerSetId:guid}/markers/{markerId:guid}", DeleteMarkerAsync);
    }

    private static async Task<IResult> ListAsync(Guid assetId, HttpContext context, IAppDataStore store)
    {
        var identity = AccessControl.RequirePermission(context, PermissionNames.BrowseAssets);
        var result = await store.ReadAsync(state =>
        {
            var current = CurrentUser(state, identity, PermissionNames.BrowseAssets);
            var asset = VisibleAsset(state, current, assetId);
            return state.MarkerSets
                .Where(item => item.AssetId == asset.Id)
                .OrderByDescending(item => item.UpdatedAt)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .Select(item => ToSummary(state, asset, item))
                .ToArray();
        }, context.RequestAborted);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetAsync(Guid markerSetId, HttpContext context, IAppDataStore store)
    {
        var identity = AccessControl.RequirePermission(context, PermissionNames.BrowseAssets);
        var result = await store.ReadAsync(state =>
        {
            var current = CurrentUser(state, identity, PermissionNames.BrowseAssets);
            var markerSet = FindSet(state, markerSetId);
            var asset = VisibleAsset(state, current, markerSet.AssetId);
            return ToDetail(state, asset, markerSet);
        }, context.RequestAborted);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetAccessAsync(Guid markerSetId, HttpContext context, IAppDataStore store)
    {
        var identity = AccessControl.RequirePermission(context, PermissionNames.BrowseAssets);
        var result = await store.ReadAsync(state =>
        {
            var current = CurrentUser(state, identity, PermissionNames.BrowseAssets);
            var markerSet = FindSet(state, markerSetId);
            _ = VisibleAsset(state, current, markerSet.AssetId);
            var canMaintain = current.IsAdmin || current.Permissions.Contains(PermissionNames.MaintainMarkers);
            var ownsSet = markerSet.OwnerUserId == current.Id;
            return new MarkerSetAccess(
                CanView: true,
                CanEdit: ownsSet && canMaintain,
                CanDelete: ownsSet && canMaintain || current.IsAdmin,
                CanCopy: canMaintain);
        }, context.RequestAborted);
        return Results.Ok(result);
    }

    private static async Task<IResult> CreateAsync(
        CreateMarkerSetRequest request,
        HttpContext context,
        IAppDataStore store,
        ILibraryChangeNotifier changes)
    {
        var identity = AccessControl.RequirePermission(context, PermissionNames.MaintainMarkers);
        var name = ValidateSetName(request.Name);
        var result = await store.UpdateAsync(state =>
        {
            var current = CurrentUser(state, identity, PermissionNames.MaintainMarkers);
            var asset = VisibleAsset(state, current, request.AssetId);
            RequireCurrentVersion(asset, request.AssetVersionId);
            var now = DateTimeOffset.UtcNow;
            var markerSet = new MarkerSetRecord
            {
                Id = Guid.NewGuid(),
                AssetId = asset.Id,
                AssetVersionId = asset.CurrentVersionId,
                Name = name,
                OwnerUserId = current.Id,
                OwnerDisplayName = string.IsNullOrWhiteSpace(current.DisplayName) ? current.Username : current.DisplayName,
                CreatedAt = now,
                UpdatedAt = now
            };
            state.MarkerSets.Add(markerSet);
            ApiCommon.Audit(state, current.Id, "marker_set.created", "marker_set", markerSet.Id.ToString());
            return ToDetail(state, asset, markerSet);
        }, context.RequestAborted);
        await changes.NotifyAsync(context.RequestAborted, LibraryChangeTarget.Markers(result.Id));
        return Results.Created($"/api/marker-sets/{result.Id}", result);
    }

    private static async Task<IResult> CopyAsync(
        CopyMarkerSetRequest request,
        HttpContext context,
        IAppDataStore store,
        ILibraryChangeNotifier changes)
    {
        var identity = AccessControl.RequirePermission(context, PermissionNames.MaintainMarkers);
        var name = ValidateSetName(request.Name);
        var result = await store.UpdateAsync(state =>
        {
            var current = CurrentUser(state, identity, PermissionNames.MaintainMarkers);
            var source = FindSet(state, request.SourceMarkerSetId);
            var asset = VisibleAsset(state, current, source.AssetId);
            var now = DateTimeOffset.UtcNow;
            var copy = new MarkerSetRecord
            {
                Id = Guid.NewGuid(),
                AssetId = source.AssetId,
                AssetVersionId = source.AssetVersionId,
                Name = name,
                OwnerUserId = current.Id,
                OwnerDisplayName = string.IsNullOrWhiteSpace(current.DisplayName) ? current.Username : current.DisplayName,
                CreatedAt = now,
                UpdatedAt = now,
                Markers = source.Markers.Select(marker => new MarkerRecord
                {
                    Id = Guid.NewGuid(),
                    TimeMilliseconds = marker.TimeMilliseconds,
                    Name = marker.Name,
                    Note = marker.Note
                }).ToList()
            };
            state.MarkerSets.Add(copy);
            ApiCommon.Audit(state, current.Id, "marker_set.copied", "marker_set", copy.Id.ToString(), source.Id.ToString());
            return ToDetail(state, asset, copy);
        }, context.RequestAborted);
        await changes.NotifyAsync(context.RequestAborted, LibraryChangeTarget.Markers(result.Id));
        return Results.Created($"/api/marker-sets/{result.Id}", result);
    }

    private static async Task<IResult> RenameAsync(
        Guid markerSetId,
        RenameMarkerSetRequest request,
        HttpContext context,
        IAppDataStore store,
        ILibraryChangeNotifier changes)
    {
        var identity = AccessControl.RequirePermission(context, PermissionNames.MaintainMarkers);
        var name = ValidateSetName(request.Name);
        var result = await store.UpdateAsync(state =>
        {
            var current = CurrentUser(state, identity, PermissionNames.MaintainMarkers);
            var markerSet = FindSet(state, markerSetId);
            var asset = VisibleAsset(state, current, markerSet.AssetId);
            RequireOwner(current, markerSet);
            markerSet.Name = name;
            markerSet.UpdatedAt = DateTimeOffset.UtcNow;
            ApiCommon.Audit(state, current.Id, "marker_set.renamed", "marker_set", markerSet.Id.ToString());
            return ToDetail(state, asset, markerSet);
        }, context.RequestAborted);
        await changes.NotifyAsync(context.RequestAborted, LibraryChangeTarget.Markers(markerSetId));
        return Results.Ok(result);
    }

    private static async Task<IResult> AddMarkerAsync(
        Guid markerSetId,
        UpsertMarkerRequest request,
        HttpContext context,
        IAppDataStore store,
        ILibraryChangeNotifier changes)
    {
        var identity = AccessControl.RequirePermission(context, PermissionNames.MaintainMarkers);
        var marker = NewMarker(request, Guid.NewGuid());
        var result = await store.UpdateAsync(state =>
        {
            var current = CurrentUser(state, identity, PermissionNames.MaintainMarkers);
            var markerSet = FindSet(state, markerSetId);
            _ = VisibleAsset(state, current, markerSet.AssetId);
            RequireOwner(current, markerSet);
            markerSet.Markers.Add(marker);
            markerSet.Markers = markerSet.Markers.OrderBy(item => item.TimeMilliseconds).ToList();
            markerSet.UpdatedAt = DateTimeOffset.UtcNow;
            ApiCommon.Audit(state, current.Id, "marker.created", "marker", marker.Id.ToString(), markerSet.Id.ToString());
            return ToMarker(marker);
        }, context.RequestAborted);
        await changes.NotifyAsync(context.RequestAborted, LibraryChangeTarget.Markers(markerSetId));
        return Results.Created($"/api/marker-sets/{markerSetId}/markers/{result.Id}", result);
    }

    private static async Task<IResult> UpdateMarkerAsync(
        Guid markerSetId,
        Guid markerId,
        UpsertMarkerRequest request,
        HttpContext context,
        IAppDataStore store,
        ILibraryChangeNotifier changes)
    {
        var identity = AccessControl.RequirePermission(context, PermissionNames.MaintainMarkers);
        var replacement = NewMarker(request, markerId);
        var result = await store.UpdateAsync(state =>
        {
            var current = CurrentUser(state, identity, PermissionNames.MaintainMarkers);
            var markerSet = FindSet(state, markerSetId);
            _ = VisibleAsset(state, current, markerSet.AssetId);
            RequireOwner(current, markerSet);
            var index = markerSet.Markers.FindIndex(item => item.Id == markerId);
            if (index < 0)
            {
                throw NotFound("marker_not_found", "标记不存在。");
            }

            markerSet.Markers[index] = replacement;
            markerSet.Markers = markerSet.Markers.OrderBy(item => item.TimeMilliseconds).ToList();
            markerSet.UpdatedAt = DateTimeOffset.UtcNow;
            ApiCommon.Audit(state, current.Id, "marker.updated", "marker", markerId.ToString(), markerSet.Id.ToString());
            return ToMarker(replacement);
        }, context.RequestAborted);
        await changes.NotifyAsync(context.RequestAborted, LibraryChangeTarget.Markers(markerSetId));
        return Results.Ok(result);
    }

    private static async Task<IResult> DeleteMarkerAsync(
        Guid markerSetId,
        Guid markerId,
        HttpContext context,
        IAppDataStore store,
        ILibraryChangeNotifier changes)
    {
        var identity = AccessControl.RequireUser(context);
        await store.UpdateAsync(state =>
        {
            var current = ApiCommon.CurrentUser(state, identity);
            var markerSet = FindSet(state, markerSetId);
            _ = VisibleAsset(state, current, markerSet.AssetId);
            RequireDeleteAccess(current, markerSet);
            if (markerSet.Markers.RemoveAll(item => item.Id == markerId) == 0)
            {
                throw NotFound("marker_not_found", "标记不存在。");
            }

            markerSet.UpdatedAt = DateTimeOffset.UtcNow;
            ApiCommon.Audit(state, current.Id, "marker.deleted", "marker", markerId.ToString(), markerSet.Id.ToString());
            return true;
        }, context.RequestAborted);
        await changes.NotifyAsync(context.RequestAborted, LibraryChangeTarget.Markers(markerSetId));
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteSetAsync(
        Guid markerSetId,
        HttpContext context,
        IAppDataStore store,
        ILibraryChangeNotifier changes)
    {
        var identity = AccessControl.RequireUser(context);
        await store.UpdateAsync(state =>
        {
            var current = ApiCommon.CurrentUser(state, identity);
            var markerSet = FindSet(state, markerSetId);
            _ = VisibleAsset(state, current, markerSet.AssetId);
            RequireDeleteAccess(current, markerSet);
            state.MarkerSets.Remove(markerSet);
            ApiCommon.Audit(state, current.Id, "marker_set.deleted", "marker_set", markerSet.Id.ToString());
            return true;
        }, context.RequestAborted);
        await changes.NotifyAsync(context.RequestAborted, LibraryChangeTarget.Markers(markerSetId));
        return Results.NoContent();
    }

    private static async Task<IResult> ImportCsvAsync(
        HttpContext context,
        IAppDataStore store,
        ILibraryChangeNotifier changes)
    {
        var identity = AccessControl.RequirePermission(context, PermissionNames.MaintainMarkers);
        if (!Guid.TryParse(context.Request.Query["assetId"], out var assetId) ||
            !Guid.TryParse(context.Request.Query["assetVersionId"], out var assetVersionId))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_asset_version", "素材 ID 和素材版本 ID 无效。");
        }

        var name = ValidateSetName(context.Request.Query["name"]);
        var bytes = await ReadLimitedBodyAsync(context.Request, context.RequestAborted);
        MarkerCsvDocument document;
        try
        {
            document = MarkersCsv.Read(bytes);
        }
        catch (MarkerCsvFormatException exception)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_marker_csv", exception.Message);
        }

        foreach (var marker in document.Markers)
        {
            ValidateMarkerText(marker.Name, marker.Note);
        }

        var result = await store.UpdateAsync(state =>
        {
            var current = CurrentUser(state, identity, PermissionNames.MaintainMarkers);
            var asset = VisibleAsset(state, current, assetId);
            RequireCurrentVersion(asset, assetVersionId);
            var now = DateTimeOffset.UtcNow;
            var markerSet = new MarkerSetRecord
            {
                Id = Guid.NewGuid(),
                AssetId = asset.Id,
                AssetVersionId = asset.CurrentVersionId,
                Name = name,
                OwnerUserId = current.Id,
                OwnerDisplayName = string.IsNullOrWhiteSpace(current.DisplayName) ? current.Username : current.DisplayName,
                CreatedAt = now,
                UpdatedAt = now,
                Markers = document.Markers.Select(marker => new MarkerRecord
                {
                    Id = Guid.NewGuid(),
                    TimeMilliseconds = ToMilliseconds(marker.Time),
                    Name = marker.Name,
                    Note = marker.Note
                }).ToList()
            };
            state.MarkerSets.Add(markerSet);
            ApiCommon.Audit(state, current.Id, "marker_set.csv_imported", "marker_set", markerSet.Id.ToString());
            return ToDetail(state, asset, markerSet);
        }, context.RequestAborted);
        await changes.NotifyAsync(context.RequestAborted, LibraryChangeTarget.Markers(result.Id));
        return Results.Created($"/api/marker-sets/{result.Id}", result);
    }

    private static async Task<IResult> ExportCsvAsync(Guid markerSetId, HttpContext context, IAppDataStore store)
    {
        var identity = AccessControl.RequirePermission(context, PermissionNames.BrowseAssets);
        var recording = ParseRecordingInfo(context);
        var result = await store.ReadAsync(state =>
        {
            var current = CurrentUser(state, identity, PermissionNames.BrowseAssets);
            var markerSet = FindSet(state, markerSetId);
            _ = VisibleAsset(state, current, markerSet.AssetId);
            var document = new MarkerCsvDocument(
                recording.RecordingName,
                recording.RecordingPath,
                recording.RecordingStartedAt,
                recording.RecordingDuration,
                markerSet.Markers
                    .OrderBy(item => item.TimeMilliseconds)
                    .Select(item => new MarkerCsvEntry(
                        TimeSpan.FromMilliseconds(item.TimeMilliseconds),
                        item.Name,
                        item.Note))
                    .ToArray());
            try
            {
                return new { Bytes = MarkersCsv.Write(document), markerSet.Name };
            }
            catch (ArgumentException exception)
            {
                throw new ApiException(StatusCodes.Status400BadRequest, "invalid_recording_metadata", exception.Message);
            }
        }, context.RequestAborted);
        return Results.File(result.Bytes, "text/csv; charset=utf-8", SafeCsvFileName(result.Name));
    }

    private static UserRecord CurrentUser(AppState state, RequestIdentity identity, string permission)
    {
        var current = ApiCommon.CurrentUser(state, identity);
        ApiCommon.RequirePermission(current, permission);
        return current;
    }

    private static AssetRecord VisibleAsset(AppState state, UserRecord current, Guid assetId)
    {
        var asset = state.Assets.FirstOrDefault(item => item.Id == assetId);
        if (asset is null || !ApiCommon.CanAccessCategory(current, asset.Category) ||
            asset.State == AssetState.Recycled && !current.IsAdmin && asset.UploadedByUserId != current.Id)
        {
            throw NotFound("asset_not_found", "素材不存在。");
        }

        return asset;
    }

    private static MarkerSetRecord FindSet(AppState state, Guid markerSetId) =>
        state.MarkerSets.FirstOrDefault(item => item.Id == markerSetId)
        ?? throw NotFound("marker_set_not_found", "标记集不存在。");

    private static void RequireCurrentVersion(AssetRecord asset, Guid versionId)
    {
        if (versionId == Guid.Empty || versionId != asset.CurrentVersionId)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "asset_version_not_current", "只能在素材当前版本上新建或导入标记集。");
        }
    }

    private static void RequireOwner(UserRecord current, MarkerSetRecord markerSet)
    {
        if (markerSet.OwnerUserId != current.Id)
        {
            throw new ApiException(StatusCodes.Status403Forbidden, "marker_owner_required", "他人的标记只读；请先复制到自己名下。");
        }
    }

    private static void RequireDeleteAccess(UserRecord current, MarkerSetRecord markerSet)
    {
        if (current.IsAdmin && markerSet.OwnerUserId != current.Id)
        {
            return;
        }

        ApiCommon.RequirePermission(current, PermissionNames.MaintainMarkers);
        RequireOwner(current, markerSet);
    }

    private static MarkerRecord NewMarker(UpsertMarkerRequest request, Guid markerId)
    {
        ValidateMarkerText(request.Name, request.Note);
        return new MarkerRecord
        {
            Id = markerId,
            TimeMilliseconds = ToMilliseconds(request.Time),
            Name = EmptyToNull(request.Name),
            Note = EmptyToNull(request.Note)
        };
    }

    private static long ToMilliseconds(TimeSpan time)
    {
        if (time < TimeSpan.Zero || time.Ticks % TimeSpan.TicksPerMillisecond != 0)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_marker_time", "标记时间不能为负数，且最多精确到毫秒。");
        }

        return time.Ticks / TimeSpan.TicksPerMillisecond;
    }

    private static void ValidateMarkerText(string? name, string? note)
    {
        if (name?.Length > MarkerNameMaxLength || note?.Length > MarkerNoteMaxLength)
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "invalid_marker_text",
                $"标记名称不能超过 {MarkerNameMaxLength} 个字符，备注不能超过 {MarkerNoteMaxLength} 个字符。");
        }
    }

    private static string ValidateSetName(string? value)
    {
        var name = value?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > MarkerSetNameMaxLength)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_marker_set_name", $"标记集名称必须为 1 至 {MarkerSetNameMaxLength} 个字符。");
        }

        return name;
    }

    private static async Task<byte[]> ReadLimitedBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength > CsvMaxBytes)
        {
            throw new ApiException(StatusCodes.Status413PayloadTooLarge, "marker_csv_too_large", "标记 CSV 不能超过 8MB。");
        }

        await using var buffer = new MemoryStream();
        var block = new byte[64 * 1024];
        while (true)
        {
            var read = await request.Body.ReadAsync(block, cancellationToken);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length + read > CsvMaxBytes)
            {
                throw new ApiException(StatusCodes.Status413PayloadTooLarge, "marker_csv_too_large", "标记 CSV 不能超过 8MB。");
            }

            await buffer.WriteAsync(block.AsMemory(0, read), cancellationToken);
        }
    }

    private static MarkerCsvRecordingInfo ParseRecordingInfo(HttpContext context)
    {
        DateTime? startedAt = null;
        var startedText = context.Request.Query["recordingStartedAt"].ToString();
        if (startedText.Length > 0)
        {
            if (!DateTime.TryParseExact(
                    startedText,
                    "yyyy-MM-dd HH:mm:ss",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var parsedStart))
            {
                throw new ApiException(StatusCodes.Status400BadRequest, "invalid_recording_start", "录制开始必须使用 yyyy-MM-dd HH:mm:ss。");
            }

            startedAt = parsedStart;
        }

        TimeSpan? duration = null;
        var durationText = context.Request.Query["recordingDurationSeconds"].ToString();
        if (durationText.Length > 0)
        {
            if (!long.TryParse(durationText, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds < 0)
            {
                throw new ApiException(StatusCodes.Status400BadRequest, "invalid_recording_duration", "录制时长秒数无效。");
            }

            try
            {
                duration = TimeSpan.FromSeconds(seconds);
            }
            catch (OverflowException)
            {
                throw new ApiException(StatusCodes.Status400BadRequest, "invalid_recording_duration", "录制时长超出支持范围。");
            }
        }

        return new MarkerCsvRecordingInfo(
            EmptyToNull(context.Request.Query["recordingName"]),
            EmptyToNull(context.Request.Query["recordingPath"]),
            startedAt,
            duration);
    }

    private static MarkerSetSummary ToSummary(AppState state, AssetRecord asset, MarkerSetRecord markerSet) => new(
        markerSet.Id,
        markerSet.AssetId,
        markerSet.AssetVersionId,
        markerSet.Name,
        markerSet.OwnerUserId,
        OwnerDisplayName(state, markerSet),
        markerSet.Markers.Count,
        markerSet.AssetVersionId != asset.CurrentVersionId,
        markerSet.UpdatedAt);

    private static MarkerSetDetail ToDetail(AppState state, AssetRecord asset, MarkerSetRecord markerSet) => new(
        markerSet.Id,
        markerSet.AssetId,
        markerSet.AssetVersionId,
        markerSet.Name,
        markerSet.OwnerUserId,
        OwnerDisplayName(state, markerSet),
        markerSet.AssetVersionId != asset.CurrentVersionId,
        markerSet.CreatedAt,
        markerSet.UpdatedAt,
        markerSet.Markers.OrderBy(item => item.TimeMilliseconds).Select(ToMarker).ToArray());

    private static MarkerItem ToMarker(MarkerRecord marker) => new(
        marker.Id,
        TimeSpan.FromMilliseconds(marker.TimeMilliseconds),
        marker.Name,
        marker.Note);

    private static string OwnerDisplayName(AppState state, MarkerSetRecord markerSet)
    {
        var owner = state.Users.FirstOrDefault(item => item.Id == markerSet.OwnerUserId);
        return owner is null
            ? string.IsNullOrWhiteSpace(markerSet.OwnerDisplayName)
                ? markerSet.OwnerUserId.ToString()
                : markerSet.OwnerDisplayName
            : string.IsNullOrWhiteSpace(owner.DisplayName) ? owner.Username : owner.DisplayName;
    }

    private static string SafeCsvFileName(string markerSetName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = string.Concat(markerSetName.Select(character => invalid.Contains(character) ? '_' : character));
        return $"{safe}.markers.csv";
    }

    private static string? EmptyToNull(string? value) => value?.Length > 0 ? value : null;

    private static ApiException NotFound(string code, string message) =>
        new(StatusCodes.Status404NotFound, code, message);
}
