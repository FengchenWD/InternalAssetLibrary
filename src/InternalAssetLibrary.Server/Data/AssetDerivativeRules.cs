using InternalAssetLibrary.Contracts;

namespace InternalAssetLibrary.Server.Data;

internal static class AssetDerivativeRules
{
    public const int ThumbnailFormatVersion = 1;
    public const int AudioWaveformThumbnailFormatVersion = 4;
    public const int ProxyFormatVersion = 1;

    public static void InitializePendingOriginal(AssetRecord asset, DateTimeOffset now)
    {
        asset.Derivatives = new AssetDerivativesRecord
        {
            Thumbnail = NewRecord(
                asset.CurrentVersionId,
                DerivativeState.Unavailable,
                ThumbnailFormatVersionFor(asset.Category),
                now),
            Proxy = NewRecord(asset.CurrentVersionId, DerivativeState.Unavailable, ProxyFormatVersion, now)
        };
    }

    public static void ResetForCurrentVersion(
        AssetRecord asset,
        DateTimeOffset now,
        bool serverThumbnailBackfill)
    {
        asset.Derivatives = new AssetDerivativesRecord
        {
            Thumbnail = NewRecord(
                asset.CurrentVersionId,
                DerivativeState.Queued,
                ThumbnailFormatVersionFor(asset.Category),
                now,
                serverThumbnailBackfill),
            Proxy = NewRecord(
                asset.CurrentVersionId,
                SupportsProxy(asset.Category) ? DerivativeState.Queued : DerivativeState.Unavailable,
                ProxyFormatVersion,
                now)
        };
    }

    public static bool Normalize(AssetRecord asset, DateTimeOffset now)
    {
        var changed = false;
        if (asset.Derivatives is null ||
            asset.Derivatives.Thumbnail is null ||
            asset.Derivatives.Proxy is null ||
            asset.Derivatives.Thumbnail.AssetVersionId != asset.CurrentVersionId ||
            asset.Derivatives.Proxy.AssetVersionId != asset.CurrentVersionId)
        {
            if (asset.HasOriginal)
            {
                ResetForCurrentVersion(asset, now, serverThumbnailBackfill: true);
            }
            else
            {
                InitializePendingOriginal(asset, now);
            }

            return true;
        }

        changed |= NormalizeThumbnailRecord(asset, now);
        changed |= NormalizeRecord(asset.Derivatives.Proxy, ProxyFormatVersion, now);
        if (!SupportsProxy(asset.Category) && asset.Derivatives.Proxy.State != DerivativeState.Unavailable)
        {
            ClearObject(asset.Derivatives.Proxy);
            asset.Derivatives.Proxy.State = DerivativeState.Unavailable;
            asset.Derivatives.Proxy.ErrorCode = "proxy_not_applicable";
            asset.Derivatives.Proxy.ErrorMessage = null;
            asset.Derivatives.Proxy.UpdatedAt = now;
            changed = true;
        }

        return changed;
    }

    public static AssetDerivativeRecord Select(AssetRecord asset, AssetDerivativeKind kind) => kind switch
    {
        AssetDerivativeKind.Thumbnail => asset.Derivatives.Thumbnail,
        AssetDerivativeKind.Proxy => asset.Derivatives.Proxy,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static IEnumerable<string> StoredObjectKeys(AssetRecord asset) =>
        new[] { asset.Derivatives.Thumbnail, asset.Derivatives.Proxy }
            .Where(item => !string.IsNullOrWhiteSpace(item.ObjectKey))
            .Select(item => item.ObjectKey!);

    public static bool SupportsProxy(string category) =>
        category is AssetCategories.Bgm or AssetCategories.SoundEffect or AssetCategories.Video;

    public static bool UsesWaveformThumbnail(string category) =>
        category is AssetCategories.Bgm or AssetCategories.SoundEffect;

    public static int ThumbnailFormatVersionFor(string category) =>
        UsesWaveformThumbnail(category) ? AudioWaveformThumbnailFormatVersion : ThumbnailFormatVersion;

    public static string BuildObjectKey(
        AssetRecord asset,
        AssetDerivativeKind kind,
        Guid objectId)
    {
        var formatVersion = kind == AssetDerivativeKind.Thumbnail
            ? ThumbnailFormatVersionFor(asset.Category)
            : ProxyFormatVersion;
        var suffix = kind switch
        {
            AssetDerivativeKind.Thumbnail => ".webp",
            AssetDerivativeKind.Proxy when asset.Category == AssetCategories.Video => ".mp4",
            AssetDerivativeKind.Proxy => ".m4a",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        return $"derivatives/{asset.Id:N}/{asset.CurrentVersionId:N}/{KindValue(kind)}-v{formatVersion}-{objectId:N}{suffix}";
    }

    public static string KindValue(AssetDerivativeKind kind) => kind switch
    {
        AssetDerivativeKind.Thumbnail => "thumbnail",
        AssetDerivativeKind.Proxy => "proxy",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static void ClearObject(AssetDerivativeRecord record)
    {
        record.ObjectKey = null;
        record.ContentType = null;
        record.SizeBytes = 0;
        record.ETag = null;
    }

    private static AssetDerivativeRecord NewRecord(
        Guid versionId,
        DerivativeState state,
        int formatVersion,
        DateTimeOffset now,
        bool serverBackfill = false) => new()
    {
        AssetVersionId = versionId,
        State = state,
        FormatVersion = formatVersion,
        UpdatedAt = now,
        ServerBackfillEligible = serverBackfill
    };

    private static bool NormalizeRecord(
        AssetDerivativeRecord record,
        int formatVersion,
        DateTimeOffset now)
    {
        var changed = false;
        if (record.FormatVersion != formatVersion)
        {
            record.FormatVersion = formatVersion;
            changed = true;
        }

        if (record.RetryCount < 0)
        {
            record.RetryCount = 0;
            changed = true;
        }

        if (record.UpdatedAt == default)
        {
            record.UpdatedAt = now;
            changed = true;
        }

        if (record.State == DerivativeState.Ready &&
            (string.IsNullOrWhiteSpace(record.ObjectKey) ||
             string.IsNullOrWhiteSpace(record.ETag) ||
             string.IsNullOrWhiteSpace(record.ContentType) ||
             record.SizeBytes <= 0))
        {
            ClearObject(record);
            record.State = DerivativeState.Queued;
            record.ErrorCode = "derivative_metadata_incomplete";
            record.ErrorMessage = null;
            record.ServerBackfillEligible = true;
            changed = true;
        }

        return changed;
    }

    private static bool NormalizeThumbnailRecord(AssetRecord asset, DateTimeOffset now)
    {
        var thumbnail = asset.Derivatives.Thumbnail;
        var expectedVersion = ThumbnailFormatVersionFor(asset.Category);
        var changed = false;
        if (thumbnail.FormatVersion != expectedVersion)
        {
            thumbnail.FormatVersion = expectedVersion;
            thumbnail.UpdatedAt = now;
            thumbnail.ErrorCode = null;
            thumbnail.ErrorMessage = null;
            if (asset.HasOriginal)
            {
                // Keep the previous object until its replacement commits. The backfill worker
                // then schedules that stale object through the durable deletion outbox.
                thumbnail.State = DerivativeState.Queued;
                thumbnail.ServerBackfillEligible = true;
            }
            else
            {
                ClearObject(thumbnail);
                thumbnail.State = DerivativeState.Unavailable;
                thumbnail.ServerBackfillEligible = false;
            }

            changed = true;
        }

        return NormalizeRecord(thumbnail, expectedVersion, now) || changed;
    }
}
