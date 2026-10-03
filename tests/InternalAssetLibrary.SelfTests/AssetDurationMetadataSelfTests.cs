using System.Text.Json;
using InternalAssetLibrary.Server.Api;
using InternalAssetLibrary.Server.Data;
using InternalAssetLibrary.Server.Security;

internal static class AssetDurationMetadataSelfTests
{
    public static void ValidationAndPersistenceRoundTrip()
    {
        Equal<double?>(null, ApiCommon.ValidateAssetDuration(null));
        Equal<double?>(0, ApiCommon.ValidateAssetDuration(0));
        Equal<double?>(73.25, ApiCommon.ValidateAssetDuration(73.25));
        Rejects(-0.01);
        Rejects(double.PositiveInfinity);
        Rejects(double.NaN);
        Rejects(TimeSpan.MaxValue.TotalSeconds * 2);

        var now = DateTimeOffset.Parse("2026-08-31T00:00:00+00:00");
        var asset = new AssetRecord
        {
            Id = Guid.NewGuid(),
            CurrentVersionId = Guid.NewGuid(),
            Name = "duration",
            Category = "video",
            OriginalFileName = "duration.mp4",
            Extension = ".mp4",
            SizeBytes = 4,
            DurationSeconds = 73.25,
            ContentHash = new string('A', 64),
            ObjectKey = "originals/duration.mp4",
            UploadedByUserId = Guid.NewGuid(),
            UploadedAt = now,
            UpdatedAt = now
        };
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var json = JsonSerializer.Serialize(new AppState { Assets = [asset] }, options);
        var restored = JsonSerializer.Deserialize<AppState>(json, options)!;

        Equal<double?>(73.25, restored.Assets.Single().DurationSeconds);
        using var response = JsonDocument.Parse(JsonSerializer.Serialize(
            ApiCommon.ToAsset(restored.Assets.Single(), null),
            options));
        Equal(73.25, response.RootElement.GetProperty("durationSeconds").GetDouble());
    }

    private static void Rejects(double value)
    {
        try
        {
            _ = ApiCommon.ValidateAssetDuration(value);
        }
        catch (ApiException exception) when (exception.Code == "invalid_duration")
        {
            return;
        }

        throw new InvalidOperationException($"Expected duration '{value}' to be rejected.");
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', but was '{actual}'.");
        }
    }
}
