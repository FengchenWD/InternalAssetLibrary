using System.Text.Json;
using System.Text.Json.Serialization;

namespace InternalAssetLibrary.Client.Core.Lut;

public sealed record LocalLutEntry(
    Guid Id,
    string DisplayName,
    string FullPath,
    DateTimeOffset ImportedAtUtc,
    bool IsAvailable,
    CubeLutDescriptor? Descriptor,
    string? Diagnostic);

public interface ILocalLutLibrary
{
    Task<IReadOnlyList<LocalLutEntry>> LoadAsync(CancellationToken cancellationToken = default);

    Task<LocalLutEntry> ImportAsync(
        string filePath,
        string? displayName = null,
        CancellationToken cancellationToken = default);

    Task<LocalLutEntry> RefreshAsync(Guid id, CancellationToken cancellationToken = default);

    Task<bool> RemoveAsync(Guid id, CancellationToken cancellationToken = default);
}

public sealed class JsonLocalLutLibrary : ILocalLutLibrary, IDisposable
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _filePath;
    private readonly CubeLutValidator _validator;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonLocalLutLibrary(string filePath, CubeLutValidator? validator = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
        _validator = validator ?? new CubeLutValidator();
    }

    public async Task<IReadOnlyList<LocalLutEntry>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = await LoadDocumentAsync(cancellationToken).ConfigureAwait(false);
            var entries = new List<LocalLutEntry>(document.Entries.Length);
            foreach (var entry in document.Entries.OrderBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                entries.Add(await HydrateAsync(entry, cancellationToken).ConfigureAwait(false));
            }

            return entries;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LocalLutEntry> ImportAsync(
        string filePath,
        string? displayName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var fullPath = Path.GetFullPath(filePath);
        var validation = await _validator.ValidateFileAsync(fullPath, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid || validation.Descriptor is null)
        {
            throw new InvalidDataException(validation.Diagnostic);
        }

        var normalizedName = NormalizeName(displayName, validation.Descriptor.Title, fullPath);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = await LoadDocumentAsync(cancellationToken).ConfigureAwait(false);
            var existing = document.Entries.FirstOrDefault(entry => PathsEqual(entry.FullPath, fullPath));
            var persisted = existing is null
                ? new PersistedLutEntry(Guid.NewGuid(), normalizedName, fullPath, DateTimeOffset.UtcNow)
                : existing with { DisplayName = normalizedName, FullPath = fullPath };
            var entries = document.Entries
                .Where(entry => entry.Id != persisted.Id)
                .Append(persisted)
                .OrderBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            await SaveDocumentAsync(document with { Entries = entries }, cancellationToken).ConfigureAwait(false);
            return ToAvailableEntry(persisted, validation.Descriptor);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LocalLutEntry> RefreshAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("LUT identifier must not be empty.", nameof(id));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = await LoadDocumentAsync(cancellationToken).ConfigureAwait(false);
            var persisted = document.Entries.SingleOrDefault(entry => entry.Id == id)
                ?? throw new KeyNotFoundException("The LUT library entry does not exist.");
            return await HydrateAsync(persisted, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> RemoveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
        {
            return false;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = await LoadDocumentAsync(cancellationToken).ConfigureAwait(false);
            var entries = document.Entries.Where(entry => entry.Id != id).ToArray();
            if (entries.Length == document.Entries.Length)
            {
                return false;
            }

            await SaveDocumentAsync(document with { Entries = entries }, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<LocalLutEntry> HydrateAsync(
        PersistedLutEntry persisted,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(persisted.FullPath))
        {
            return new LocalLutEntry(
                persisted.Id,
                persisted.DisplayName,
                persisted.FullPath,
                persisted.ImportedAtUtc,
                false,
                null,
                "The LUT source file is unavailable.");
        }

        var validation = await _validator.ValidateFileAsync(persisted.FullPath, cancellationToken).ConfigureAwait(false);
        return validation.IsValid && validation.Descriptor is not null
            ? ToAvailableEntry(persisted, validation.Descriptor)
            : new LocalLutEntry(
                persisted.Id,
                persisted.DisplayName,
                persisted.FullPath,
                persisted.ImportedAtUtc,
                false,
                null,
                validation.Diagnostic);
    }

    private async Task<LutLibraryDocument> LoadDocumentAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
        {
            return LutLibraryDocument.Empty;
        }

        await using var stream = new FileStream(
            _filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var document = await JsonSerializer.DeserializeAsync<LutLibraryDocument>(
            stream,
            SerializerOptions,
            cancellationToken).ConfigureAwait(false);
        if (document is null || document.SchemaVersion != SchemaVersion)
        {
            throw new InvalidDataException("The LUT library has an unsupported schema.");
        }

        var entries = document.Entries ?? [];
        if (entries.Any(entry =>
                entry.Id == Guid.Empty ||
                string.IsNullOrWhiteSpace(entry.DisplayName) ||
                !Path.IsPathFullyQualified(entry.FullPath)))
        {
            throw new InvalidDataException("The LUT library contains an invalid entry.");
        }

        return document with { Entries = entries };
    }

    private async Task SaveDocumentAsync(
        LutLibraryDocument document,
        CancellationToken cancellationToken)
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
                16 * 1024,
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
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static LocalLutEntry ToAvailableEntry(PersistedLutEntry persisted, CubeLutDescriptor descriptor) =>
        new(
            persisted.Id,
            persisted.DisplayName,
            persisted.FullPath,
            persisted.ImportedAtUtc,
            true,
            descriptor,
            null);

    private static string NormalizeName(string? requested, string? title, string fullPath)
    {
        var value = string.IsNullOrWhiteSpace(requested)
            ? string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(fullPath) : title
            : requested.Trim();
        if (value.Length is < 1 or > 128 || value.IndexOfAny(['\r', '\n']) >= 0)
        {
            throw new ArgumentException("LUT display names must contain 1 to 128 characters.", nameof(requested));
        }

        return value;
    }

    private static bool PathsEqual(string first, string second) =>
        string.Equals(
            Path.GetFullPath(first),
            Path.GetFullPath(second),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public void Dispose() => _gate.Dispose();

    private sealed record PersistedLutEntry(
        Guid Id,
        string DisplayName,
        string FullPath,
        DateTimeOffset ImportedAtUtc);

    private sealed record LutLibraryDocument(int SchemaVersion, PersistedLutEntry[] Entries)
    {
        public static LutLibraryDocument Empty { get; } = new(JsonLocalLutLibrary.SchemaVersion, []);
    }
}
