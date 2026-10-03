using System.Text.Json;
using InternalAssetLibrary.Client.Core.Markers;
using InternalAssetLibrary.Contracts;

internal static class LocalMarkerPersistenceSelfTests
{
    public static void CrudPersistsByLocalAsset() =>
        CrudPersistsByLocalAssetAsync().GetAwaiter().GetResult();

    public static void CsvSamplesRemainByteStable() =>
        CsvSamplesRemainByteStableAsync().GetAwaiter().GetResult();

    private static async Task CrudPersistsByLocalAssetAsync()
    {
        var directory = Directory.CreateTempSubdirectory("ial-local-markers-").FullName;
        var filePath = Path.Combine(directory, "markers.json");
        var assetId = Guid.NewGuid();
        var otherAssetId = Guid.NewGuid();
        Guid firstSetId;
        Guid secondSetId;
        Guid retainedMarkerId;

        try
        {
            using (var service = new LocalMarkerService(filePath))
            {
                var firstSet = await service.CreateAsync(assetId, "第一套", TimeSpan.FromSeconds(10));
                var secondSet = await service.CreateAsync(assetId, "第二套");
                _ = await service.CreateAsync(otherAssetId, "另一个素材");
                var recordingLimitedSet = await service.CreateAsync(
                    otherAssetId,
                    "录制时长限制",
                    TimeSpan.FromSeconds(20),
                    new MarkerCsvRecordingInfo(null, null, null, TimeSpan.FromSeconds(10)));
                firstSetId = firstSet.Id;
                secondSetId = secondSet.Id;

                Equal(2, (await service.ListAsync(assetId)).Count);
                Equal(2, (await service.ListAsync(otherAssetId)).Count);
                Equal(0, (await service.ListAsync(Guid.NewGuid())).Count);

                await ThrowsAsync<ArgumentOutOfRangeException>(() => service.AddMarkerAsync(
                    otherAssetId,
                    recordingLimitedSet.Id,
                    new UpsertMarkerRequest(TimeSpan.FromSeconds(11), null, null)));

                var later = await service.AddMarkerAsync(
                    assetId,
                    firstSetId,
                    new UpsertMarkerRequest(TimeSpan.FromSeconds(8), "稍后", null));
                var earlier = await service.AddMarkerAsync(
                    assetId,
                    firstSetId,
                    new UpsertMarkerRequest(TimeSpan.FromMilliseconds(2123), "较早", "备注"));

                var ordered = await service.GetAsync(assetId, firstSetId);
                SequenceEqual(new[] { earlier.Id, later.Id }, ordered.Markers.Select(marker => marker.Id));

                retainedMarkerId = later.Id;
                _ = await service.UpdateMarkerAsync(
                    assetId,
                    firstSetId,
                    later.Id,
                    new UpsertMarkerRequest(TimeSpan.FromSeconds(1), "已更新", "更新后的备注"));
                ordered = await service.GetAsync(assetId, firstSetId);
                SequenceEqual(new[] { later.Id, earlier.Id }, ordered.Markers.Select(marker => marker.Id));

                await ThrowsAsync<ArgumentOutOfRangeException>(() => service.AddMarkerAsync(
                    assetId,
                    firstSetId,
                    new UpsertMarkerRequest(TimeSpan.FromSeconds(-1), null, null)));
                await ThrowsAsync<ArgumentOutOfRangeException>(() => service.AddMarkerAsync(
                    assetId,
                    firstSetId,
                    new UpsertMarkerRequest(TimeSpan.FromTicks(TimeSpan.TicksPerMillisecond + 1), null, null)));
                await ThrowsAsync<ArgumentOutOfRangeException>(() => service.AddMarkerAsync(
                    assetId,
                    firstSetId,
                    new UpsertMarkerRequest(TimeSpan.FromSeconds(11), null, null)));

                var renamed = await service.RenameAsync(assetId, firstSetId, "  常用标记  ");
                Equal("常用标记", renamed.Name);
                await service.DeleteMarkerAsync(assetId, firstSetId, earlier.Id);

                using var json = JsonDocument.Parse(await File.ReadAllBytesAsync(filePath));
                Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
                False(File.Exists(filePath + ".tmp"));
            }

            using (var reopened = new LocalMarkerService(filePath))
            {
                var persisted = await reopened.GetAsync(assetId, firstSetId);
                Equal("常用标记", persisted.Name);
                Equal(TimeSpan.FromSeconds(10), persisted.AssetDuration);
                Equal(1, persisted.Markers.Count);
                Equal(retainedMarkerId, persisted.Markers[0].Id);
                Equal(TimeSpan.FromSeconds(1), persisted.Markers[0].Time);
                Equal("已更新", persisted.Markers[0].Name);

                await reopened.DeleteMarkerAsync(assetId, firstSetId, retainedMarkerId);
                Equal(0, (await reopened.GetAsync(assetId, firstSetId)).Markers.Count);
                await reopened.DeleteMarkerSetAsync(assetId, secondSetId);
                Equal(1, (await reopened.ListAsync(assetId)).Count);
                await reopened.DeleteMarkerSetAsync(assetId, firstSetId);
                Equal(0, (await reopened.ListAsync(assetId)).Count);
                Equal(2, (await reopened.ListAsync(otherAssetId)).Count);
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static async Task CsvSamplesRemainByteStableAsync()
    {
        var directory = Directory.CreateTempSubdirectory("ial-local-marker-csv-").FullName;
        var filePath = Path.Combine(directory, "markers.json");
        var assetId = Guid.NewGuid();
        var imported = new List<(Guid SetId, byte[] Bytes)>();

        try
        {
            using (var service = new LocalMarkerService(filePath))
            {
                foreach (var sampleName in new[] { "空白.csv", "超一小时的markers.csv" })
                {
                    var bytes = SyntheticMarkerFixtures.Bytes(sampleName);
                    var set = await service.ImportCsvAsync(assetId, sampleName, bytes);
                    SequenceEqual(bytes, await service.ExportCsvAsync(assetId, set.Id));
                    imported.Add((set.Id, bytes));
                }
            }

            using (var reopened = new LocalMarkerService(filePath))
            {
                Equal(2, (await reopened.ListAsync(assetId)).Count);
                foreach (var sample in imported)
                {
                    SequenceEqual(sample.Bytes, await reopened.ExportCsvAsync(assetId, sample.SetId));
                }
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static string SamplePath(string fileName) =>
        Path.Combine(RepositoryRoot(), "测试", fileName);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "InternalAssetLibrary.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private static async Task ThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name} to be thrown.");
    }

    private static void True(bool condition)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Expected true, but was false.");
        }
    }

    private static void False(bool condition) => True(!condition);

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', but was '{actual}'.");
        }
    }

    private static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException("Sequences are not equal.");
        }
    }
}
