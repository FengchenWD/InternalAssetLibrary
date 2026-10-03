using InternalAssetLibrary.Client.Core.Http;
using InternalAssetLibrary.Client.Core.Lut;
using InternalAssetLibrary.Contracts;

namespace InternalAssetLibrary.Client.Services;

internal sealed record TeamLutUploadResult(
    TeamLutSummary Lut,
    CubeLutDescriptor Descriptor);

internal sealed class TeamLutUploadService(
    AssetLibraryApiClient api,
    CubeLutValidator? validator = null)
{
    private readonly CubeLutValidator _validator = validator ?? new CubeLutValidator();

    public async Task<TeamLutUploadResult> UploadAsync(
        string sourcePath,
        string? displayName = null,
        string? note = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var fullPath = Path.GetFullPath(sourcePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException(UiLocalization.Text("LUT 原文件当前不可用。"), fullPath);
        }

        var validation = await _validator.ValidateFileAsync(fullPath, cancellationToken);
        if (!validation.IsValid ||
            validation.Descriptor is not { SizeBytes: { } size, Sha256: { } sha256 } descriptor)
        {
            throw new InvalidDataException(validation.Diagnostic);
        }

        var name = string.IsNullOrWhiteSpace(displayName)
            ? string.IsNullOrWhiteSpace(descriptor.Title)
                ? Path.GetFileNameWithoutExtension(fullPath)
                : descriptor.Title
            : displayName.Trim();
        TeamLutSummary? created = null;
        try
        {
            created = await api.CreateTeamLutAsync(new CreateTeamLutRequest(
                name,
                Path.GetFileName(fullPath),
                size,
                sha256,
                note), cancellationToken);
            await using var stream = OpenRead(fullPath);
            var uploaded = await api.UploadTeamLutContentAsync(
                created.Id,
                stream,
                stream.Length,
                cancellationToken);
            return new TeamLutUploadResult(uploaded, descriptor);
        }
        catch
        {
            if (created is not null)
            {
                await TryRemoveIncompleteUploadAsync(api, created.Id);
            }

            throw;
        }
    }

    private static async Task TryRemoveIncompleteUploadAsync(AssetLibraryApiClient api, Guid lutId)
    {
        try
        {
            if ((await api.GetTeamLutAsync(lutId)).HasContent) return;
            await api.RecycleTeamLutAsync(lutId);
            await api.PermanentlyDeleteTeamLutAsync(lutId);
        }
        catch
        {
            // Preserve the original upload failure; cleanup is best effort.
        }
    }

    private static FileStream OpenRead(string path) => new(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        64 * 1024,
        FileOptions.Asynchronous | FileOptions.SequentialScan);
}
