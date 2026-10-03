using System.Security.Cryptography;
using COSXML.Model.Object;
using InternalAssetLibrary.Server.Services;

internal static class CosObjectStoreUploadSelfTests
{
    public static async Task SeekableSourcesRemainStreamingAndPassSdkValidation()
    {
        var payload = Enumerable.Range(0, 300_000)
            .Select(index => (byte)(index % 251))
            .ToArray();
        var sourceBytes = new byte[payload.Length + 3];
        payload.CopyTo(sourceBytes, 3);
        using var source = new MemoryStream(sourceBytes);
        source.Position = 3;

        await using var upload = await CosObjectStore.PrepareUploadAsync(source, payload.LongLength + 100);

        Same(source, upload.Stream);
        Equal(3L, upload.Offset);
        Equal(payload.LongLength, upload.Length);
        Equal(Convert.ToHexString(SHA256.HashData(payload)), upload.Sha256);
        Equal(3L, source.Position);
        Null(upload.TemporaryPath);

        var request = new PutObjectRequest(
            "test-bucket-123456",
            "test/object.bin",
            upload.Stream,
            upload.Offset,
            upload.Length);
        request.CheckParameters();
    }

    public static async Task NonSeekableSourcesUseBoundedTemporaryFilesAndCleanUp()
    {
        var payload = Enumerable.Range(0, 300_000)
            .Select(index => (byte)(index % 239))
            .ToArray();
        using var source = new NonSeekableReadStream(payload);
        string temporaryPath;

        await using (var upload = await CosObjectStore.PrepareUploadAsync(source, payload.LongLength + 100))
        {
            True(upload.Stream.CanSeek);
            Equal(0L, upload.Offset);
            Equal(payload.LongLength, upload.Length);
            Equal(Convert.ToHexString(SHA256.HashData(payload)), upload.Sha256);
            True(source.MaximumRequestedBytes <= 128 * 1024);
            temporaryPath = upload.TemporaryPath
                ?? throw new InvalidOperationException("Expected a temporary upload path.");
            True(File.Exists(temporaryPath));

            var request = new PutObjectRequest(
                "test-bucket-123456",
                "test/object.bin",
                upload.Stream,
                upload.Offset,
                upload.Length);
            request.CheckParameters();

            using var copy = new MemoryStream();
            await upload.Stream.CopyToAsync(copy);
            SequenceEqual(payload, copy.ToArray());
        }

        False(File.Exists(temporaryPath));

        var before = TemporaryUploadFiles();
        await ThrowsAsync<InvalidDataException>(() => CosObjectStore.PrepareUploadAsync(
            new NonSeekableReadStream(payload),
            payload.LongLength - 1));
        SequenceEqual(before, TemporaryUploadFiles());
    }

    private static string[] TemporaryUploadFiles() =>
        Directory.EnumerateFiles(Path.GetTempPath(), "ial-cos-upload-*.tmp")
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

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

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private static void True(bool condition)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Expected true, but was false.");
        }
    }

    private static void False(bool condition) => True(!condition);

    private static void Null(object? value)
    {
        if (value is not null)
        {
            throw new InvalidOperationException("Expected null.");
        }
    }

    private static void Same(object expected, object actual)
    {
        if (!ReferenceEquals(expected, actual))
        {
            throw new InvalidOperationException("Expected references to be identical.");
        }
    }

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

    private sealed class NonSeekableReadStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes, writable: false);

        public int MaximumRequestedBytes { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            MaximumRequestedBytes = Math.Max(MaximumRequestedBytes, count);
            return _inner.Read(buffer, offset, count);
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            MaximumRequestedBytes = Math.Max(MaximumRequestedBytes, buffer.Length);
            return _inner.ReadAsync(buffer, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
