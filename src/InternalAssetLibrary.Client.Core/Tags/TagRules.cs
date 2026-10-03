namespace InternalAssetLibrary.Client.Core.Tags;

public static class TagRules
{
    public const int MaximumTagsPerAsset = 20;
    public const int MaximumNameLength = 32;

    public static string NormalizeName(string? name)
    {
        var normalized = name?.Trim() ?? string.Empty;
        if (normalized.Length is < 1 or > MaximumNameLength)
        {
            throw new ArgumentException(
                $"标签名称必须为 1 至 {MaximumNameLength} 个字符。",
                nameof(name));
        }

        return normalized;
    }

    public static string[] NormalizeSelection(IEnumerable<string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        var normalized = tags
            .Select(NormalizeName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalized.Length > MaximumTagsPerAsset)
        {
            throw new ArgumentException(
                $"每个素材最多可添加 {MaximumTagsPerAsset} 个标签。",
                nameof(tags));
        }

        return normalized;
    }

    public static string[] NormalizeLibrary(IEnumerable<string>? tags) =>
        (tags ?? [])
        .Select(tag => tag?.Trim())
        .Where(tag => !string.IsNullOrEmpty(tag) && tag.Length <= MaximumNameLength)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.OrdinalIgnoreCase)
        .Cast<string>()
        .ToArray();
}
