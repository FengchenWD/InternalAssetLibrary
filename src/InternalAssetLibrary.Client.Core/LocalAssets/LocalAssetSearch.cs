namespace InternalAssetLibrary.Client.Core.LocalAssets;

public enum LocalAssetSortField
{
    FileName,
    AddedAt,
    LastModified,
    Size,
    Extension
}

public enum LocalSortDirection
{
    Ascending,
    Descending
}

public enum TagMatchMode
{
    Any,
    All
}

public sealed record LocalAssetQuery
{
    public string? SearchText { get; init; }

    public Guid? FolderId { get; init; }

    public string? RelativeDirectoryPath { get; init; }

    public IReadOnlyCollection<LocalMediaType> MediaTypes { get; init; } = [];

    public IReadOnlyCollection<string> Extensions { get; init; } = [];

    public IReadOnlyCollection<string> Tags { get; init; } = [];

    public TagMatchMode TagMatch { get; init; } = TagMatchMode.All;

    public DateTimeOffset? AddedFromUtc { get; init; }

    public DateTimeOffset? AddedToUtc { get; init; }

    public bool IncludeUnavailable { get; init; } = true;

    public LocalAssetSortField SortBy { get; init; } = LocalAssetSortField.FileName;

    public LocalSortDirection SortDirection { get; init; } = LocalSortDirection.Ascending;
}

public static class LocalAssetSearch
{
    public static IReadOnlyList<LocalAsset> Apply(
        IEnumerable<LocalAsset> source,
        LocalAssetQuery? query = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        query ??= new LocalAssetQuery();

        var mediaTypes = query.MediaTypes.ToHashSet();
        var extensions = query.Extensions
            .Where(extension => !string.IsNullOrWhiteSpace(extension))
            .Select(MediaExtensionClassifier.NormalizeExtension)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tags = query.Tags
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim())
            .ToArray();
        var searchTerms = (query.SearchText ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var relativeDirectoryPath = NormalizeRelativeDirectoryPath(query.RelativeDirectoryPath);

        var filtered = source.Where(asset =>
            MatchesDirectory(asset, query.FolderId, relativeDirectoryPath) &&
            (query.IncludeUnavailable || asset.IsAvailable) &&
            (mediaTypes.Count == 0 || mediaTypes.Contains(asset.MediaType)) &&
            (extensions.Count == 0 || extensions.Contains(asset.Extension)) &&
            (!query.AddedFromUtc.HasValue || asset.AddedAtUtc >= query.AddedFromUtc.Value) &&
            (!query.AddedToUtc.HasValue || asset.AddedAtUtc <= query.AddedToUtc.Value) &&
            MatchesTags(asset, tags, query.TagMatch) &&
            MatchesSearch(asset, searchTerms));

        return Order(filtered, query.SortBy, query.SortDirection).ToArray();
    }

    private static bool MatchesDirectory(
        LocalAsset asset,
        Guid? folderId,
        string relativeDirectoryPath)
    {
        if (folderId.HasValue && asset.FolderId != folderId.Value)
        {
            return false;
        }

        if (relativeDirectoryPath.Length == 0)
        {
            return true;
        }

        var assetDirectory = NormalizeRelativeDirectoryPath(Path.GetDirectoryName(asset.RelativePath));
        if (PathComparer.Equals(assetDirectory, relativeDirectoryPath))
        {
            return true;
        }

        return assetDirectory.Length > relativeDirectoryPath.Length &&
               assetDirectory.StartsWith(relativeDirectoryPath, PathComparison) &&
               assetDirectory[relativeDirectoryPath.Length] == Path.DirectorySeparatorChar;
    }

    private static string NormalizeRelativeDirectoryPath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path == ".")
        {
            return string.Empty;
        }

        return Path.TrimEndingDirectorySeparator(
            path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar));
    }

    private static bool MatchesTags(
        LocalAsset asset,
        IReadOnlyCollection<string> tags,
        TagMatchMode matchMode)
    {
        if (tags.Count == 0)
        {
            return true;
        }

        var assetTags = asset.Tags.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return matchMode == TagMatchMode.All
            ? tags.All(assetTags.Contains)
            : tags.Any(assetTags.Contains);
    }

    private static bool MatchesSearch(LocalAsset asset, IReadOnlyCollection<string> terms)
    {
        if (terms.Count == 0)
        {
            return true;
        }

        return terms.All(term =>
            asset.FileName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            asset.Extension.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            asset.MediaType.ToString().Contains(term, StringComparison.OrdinalIgnoreCase) ||
            asset.Tags.Any(tag => tag.Contains(term, StringComparison.OrdinalIgnoreCase)));
    }

    private static IOrderedEnumerable<LocalAsset> Order(
        IEnumerable<LocalAsset> source,
        LocalAssetSortField field,
        LocalSortDirection direction)
    {
        var descending = direction == LocalSortDirection.Descending;
        IOrderedEnumerable<LocalAsset> ordered = field switch
        {
            LocalAssetSortField.FileName => descending
                ? source.OrderByDescending(asset => asset.FileName, StringComparer.OrdinalIgnoreCase)
                : source.OrderBy(asset => asset.FileName, StringComparer.OrdinalIgnoreCase),
            LocalAssetSortField.AddedAt => descending
                ? source.OrderByDescending(asset => asset.AddedAtUtc)
                : source.OrderBy(asset => asset.AddedAtUtc),
            LocalAssetSortField.LastModified => descending
                ? source.OrderByDescending(asset => asset.LastWriteTimeUtc)
                : source.OrderBy(asset => asset.LastWriteTimeUtc),
            LocalAssetSortField.Size => descending
                ? source.OrderByDescending(asset => asset.SizeBytes)
                : source.OrderBy(asset => asset.SizeBytes),
            LocalAssetSortField.Extension => descending
                ? source.OrderByDescending(asset => asset.Extension, StringComparer.OrdinalIgnoreCase)
                : source.OrderBy(asset => asset.Extension, StringComparer.OrdinalIgnoreCase),
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, null)
        };

        return ordered.ThenBy(asset => asset.FullPath, StringComparer.OrdinalIgnoreCase);
    }

    private static StringComparer PathComparer { get; } = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static StringComparison PathComparison { get; } = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
