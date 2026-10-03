using System.Globalization;

namespace InternalAssetLibrary.Client.Core.LocalAssets;

public sealed record LocalBatchRenamePlanItem(
    int Sequence,
    string OriginalFileName,
    string NewFileName);

public sealed record LocalBatchRenamePlan(
    string Prefix,
    string Suffix,
    int SequenceDigits,
    IReadOnlyList<LocalBatchRenamePlanItem> Items);

public static class LocalBatchRenamePlanner
{
    private static readonly char[] InvalidFileNameCharacters =
        ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    public static LocalBatchRenamePlan Create(
        IEnumerable<string> originalFileNames,
        string? prefix,
        string? suffix)
    {
        ArgumentNullException.ThrowIfNull(originalFileNames);
        var originals = originalFileNames.ToArray();
        if (originals.Length == 0)
        {
            throw new ArgumentException("请选择至少一个要重命名的素材。", nameof(originalFileNames));
        }

        var normalizedPrefix = prefix ?? string.Empty;
        var normalizedSuffix = suffix ?? string.Empty;
        var sequenceDigits = Math.Max(
            2,
            originals.Length.ToString(CultureInfo.InvariantCulture).Length);
        var items = new LocalBatchRenamePlanItem[originals.Length];
        for (var index = 0; index < originals.Length; index++)
        {
            var original = NormalizeOriginalFileName(originals[index]);
            var sequence = index + 1;
            var baseName = string.Concat(
                normalizedPrefix,
                sequence.ToString($"D{sequenceDigits}", CultureInfo.InvariantCulture),
                normalizedSuffix);
            ValidateGeneratedBaseName(baseName);
            items[index] = new LocalBatchRenamePlanItem(
                sequence,
                original,
                baseName + Path.GetExtension(original));
        }

        return new LocalBatchRenamePlan(
            normalizedPrefix,
            normalizedSuffix,
            sequenceDigits,
            items);
    }

    private static string NormalizeOriginalFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(
                "原文件名不能为空。",
                nameof(value));
        }

        if (!string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "原文件名必须是不含路径的文件名。",
                nameof(value));
        }

        return value;
    }

    private static void ValidateGeneratedBaseName(string baseName)
    {
        if (string.IsNullOrWhiteSpace(baseName))
        {
            throw new ArgumentException("生成的文件基本名不能为空。", nameof(baseName));
        }

        if (baseName.IndexOfAny(InvalidFileNameCharacters) >= 0 ||
            baseName.Any(character => character < ' '))
        {
            throw new ArgumentException("前缀或后缀包含文件名不允许使用的字符。", nameof(baseName));
        }
    }
}
