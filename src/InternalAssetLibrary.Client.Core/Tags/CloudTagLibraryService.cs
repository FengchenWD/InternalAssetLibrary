using InternalAssetLibrary.Client.Core.Http;

namespace InternalAssetLibrary.Client.Core.Tags;

public sealed class CloudTagLibraryService
{
    private readonly AssetLibraryApiClient _apiClient;

    public CloudTagLibraryService(AssetLibraryApiClient apiClient)
    {
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
    }

    public async Task<IReadOnlyList<ApiTag>> ListAsync(CancellationToken cancellationToken = default) =>
        (await _apiClient.ListTagsAsync(cancellationToken).ConfigureAwait(false))
        .OrderBy(tag => tag.Name, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public async Task<ApiTag> CreateAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = TagRules.NormalizeName(name);
        try
        {
            return await _apiClient.CreateTagAsync(
                new ApiTagNameRequest(normalizedName),
                cancellationToken).ConfigureAwait(false);
        }
        catch (AssetLibraryApiException exception) when (exception.Code == "tag_name_in_use")
        {
            var existing = (await ListAsync(cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(tag => tag.Name.Equals(normalizedName, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                return existing;
            }

            throw;
        }
    }

    public Task DeleteAsync(Guid tagId, CancellationToken cancellationToken = default)
    {
        if (tagId == Guid.Empty)
        {
            throw new ArgumentException("标签 ID 不能为空。", nameof(tagId));
        }

        return _apiClient.DeleteTagAsync(tagId, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> EnsureAsync(
        IEnumerable<string> names,
        CancellationToken cancellationToken = default)
    {
        var normalizedNames = TagRules.NormalizeSelection(names);
        var tagsByName = (await ListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(tag => tag.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var name in normalizedNames)
        {
            if (tagsByName.ContainsKey(name))
            {
                continue;
            }

            var created = await CreateAsync(name, cancellationToken).ConfigureAwait(false);
            tagsByName[created.Name] = created;
        }

        return normalizedNames.Select(name => tagsByName[name].Name).ToArray();
    }

    public async Task<ApiAsset> EnsureAndAssignAsync(
        Guid assetId,
        IEnumerable<string> names,
        CancellationToken cancellationToken = default)
    {
        if (assetId == Guid.Empty)
        {
            throw new ArgumentException("素材 ID 不能为空。", nameof(assetId));
        }

        var tags = await EnsureAsync(names, cancellationToken).ConfigureAwait(false);
        return await _apiClient.UpdateAssetTagsAsync(
            assetId,
            new ApiUpdateAssetTagsRequest(tags),
            cancellationToken).ConfigureAwait(false);
    }

    public Task<ApiAsset> AssignExistingAsync(
        Guid assetId,
        IEnumerable<string> names,
        CancellationToken cancellationToken = default)
    {
        if (assetId == Guid.Empty)
        {
            throw new ArgumentException("素材 ID 不能为空。", nameof(assetId));
        }

        var tags = TagRules.NormalizeSelection(names);
        return _apiClient.UpdateAssetTagsAsync(
            assetId,
            new ApiUpdateAssetTagsRequest(tags, CreateMissing: false),
            cancellationToken);
    }
}
