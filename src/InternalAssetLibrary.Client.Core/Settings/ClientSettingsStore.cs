using System.Text.Json;
using System.Text.Json.Serialization;

namespace InternalAssetLibrary.Client.Core.Settings;

public interface IClientSettingsStore
{
    Task<ClientSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(ClientSettings settings, CancellationToken cancellationToken = default);
}

public sealed class JsonClientSettingsStore : IClientSettingsStore, IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonClientSettingsStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
    }

    public async Task<ClientSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_filePath))
            {
                return new ClientSettings();
            }

            var json = await File.ReadAllTextAsync(_filePath, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            var explicitlyStoresAccentPreference = document.RootElement.ValueKind == JsonValueKind.Object &&
                                                   document.RootElement.TryGetProperty("followSystemAccent", out _);
            var settings = JsonSerializer.Deserialize<ClientSettings>(json, SerializerOptions)
                           ?? throw new InvalidDataException("The client settings file is empty.");
            if (!explicitlyStoresAccentPreference)
            {
                settings = settings with
                {
                    FollowSystemAccent = settings.DarkColors == DefaultColorPalettes.Dark &&
                                         settings.LightColors == DefaultColorPalettes.Light
                };
            }

            return settings.ValidateAndNormalize();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(ClientSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalized = settings.ValidateAndNormalize();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporaryPath = _filePath + ".tmp";
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
                    normalized,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, _filePath, true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
