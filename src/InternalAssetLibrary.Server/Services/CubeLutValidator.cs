using System.Globalization;
using System.Text;
using InternalAssetLibrary.Server.Security;

namespace InternalAssetLibrary.Server.Services;

internal sealed record CubeLutInfo(int? OneDimensionalSize, int? ThreeDimensionalSize, int DataRows);

internal static class CubeLutValidator
{
    public const int MaximumDimension = 65;
    private const int MaximumLineLength = 4096;

    public static async Task<CubeLutInfo> ValidateAsync(
        Stream source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
        {
            throw new ArgumentException("The LUT source stream must be readable.", nameof(source));
        }

        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        using var reader = new StreamReader(
            source,
            utf8,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 16 * 1024,
            leaveOpen: true);
        int? oneDimensionalSize = null;
        int? threeDimensionalSize = null;
        var dataRows = 0;
        var titleSeen = false;
        (double X, double Y, double Z)? domainMinimum = null;
        (double X, double Y, double Z)? domainMaximum = null;
        try
        {
            while (await reader.ReadLineAsync(cancellationToken) is { } rawLine)
            {
                if (rawLine.Length > MaximumLineLength)
                {
                    throw Invalid("CUBE 文件包含过长的文本行。");
                }

                var line = StripComment(rawLine).Trim();
                if (line.Length > 0 && line[0] == '\uFEFF')
                {
                    line = line[1..].TrimStart();
                }
                if (line.Length == 0)
                {
                    continue;
                }

                if (line.StartsWith("TITLE", StringComparison.OrdinalIgnoreCase))
                {
                    var value = line.Length > 5 ? line[5..].Trim() : string.Empty;
                    if (titleSeen || value.Length < 2 || value[0] != '"' || value[^1] != '"' ||
                        string.IsNullOrWhiteSpace(value[1..^1]) || value[1..^1].Contains('"'))
                    {
                        throw Invalid("CUBE TITLE 行格式无效。");
                    }

                    titleSeen = true;
                    continue;
                }

                var values = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (values.Length == 0)
                {
                    continue;
                }

                if (values[0].Equals("LUT_1D_SIZE", StringComparison.OrdinalIgnoreCase))
                {
                    if (oneDimensionalSize is not null || values.Length != 2)
                    {
                        throw Invalid("CUBE 文件的 LUT_1D_SIZE 无效或重复。");
                    }

                    oneDimensionalSize = ParseDimension(values[1], "LUT_1D_SIZE");
                    continue;
                }

                if (values[0].Equals("LUT_3D_SIZE", StringComparison.OrdinalIgnoreCase))
                {
                    if (threeDimensionalSize is not null || values.Length != 2)
                    {
                        throw Invalid("CUBE 文件的 LUT_3D_SIZE 无效或重复。");
                    }

                    threeDimensionalSize = ParseDimension(values[1], "LUT_3D_SIZE");
                    continue;
                }

                if (values[0].Equals("DOMAIN_MIN", StringComparison.OrdinalIgnoreCase) ||
                    values[0].Equals("DOMAIN_MAX", StringComparison.OrdinalIgnoreCase))
                {
                    var parsed = ParseVector(values, values[0]);
                    if (values[0].Equals("DOMAIN_MIN", StringComparison.OrdinalIgnoreCase))
                    {
                        domainMinimum = parsed;
                    }
                    else
                    {
                        domainMaximum = parsed;
                    }
                    continue;
                }

                if (values[0].Equals("LUT_1D_INPUT_RANGE", StringComparison.OrdinalIgnoreCase) ||
                    values[0].Equals("LUT_3D_INPUT_RANGE", StringComparison.OrdinalIgnoreCase))
                {
                    ValidateFloatTuple(values, 2, values[0]);
                    continue;
                }

                if (values.Length != 3 || !values.All(IsFiniteInvariantDouble))
                {
                    if (char.IsLetter(values[0][0]) || values[0][0] == '_')
                    {
                        continue;
                    }

                    throw Invalid("CUBE 文件包含无效的数据行。");
                }
                dataRows++;
                if (dataRows > MaximumDimension * MaximumDimension * MaximumDimension + MaximumDimension)
                {
                    throw Invalid("CUBE 文件包含过多 LUT 数据行。");
                }
            }
        }
        catch (DecoderFallbackException)
        {
            throw Invalid("CUBE 文件必须使用有效的 UTF-8 或 ASCII 文本编码。");
        }

        if (oneDimensionalSize is null && threeDimensionalSize is null)
        {
            throw Invalid("CUBE 文件缺少 LUT_1D_SIZE 或 LUT_3D_SIZE。");
        }

        var minimum = domainMinimum ?? (0d, 0d, 0d);
        var maximum = domainMaximum ?? (1d, 1d, 1d);
        if (minimum.Item1 >= maximum.Item1 || minimum.Item2 >= maximum.Item2 || minimum.Item3 >= maximum.Item3)
        {
            throw Invalid("CUBE 文件的每个 DOMAIN_MIN 分量都必须小于 DOMAIN_MAX。");
        }

        var expectedRows = MaximumExpectedRows(oneDimensionalSize, threeDimensionalSize);
        if (dataRows != expectedRows)
        {
            throw Invalid($"CUBE 文件声明需要 {expectedRows} 行数据，实际为 {dataRows} 行。");
        }

        return new CubeLutInfo(oneDimensionalSize, threeDimensionalSize, dataRows);
    }

    private static int ParseDimension(string value, string field)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result) ||
            result is < 2 or > MaximumDimension)
        {
            throw Invalid($"{field} 必须为 2 至 {MaximumDimension}。");
        }

        return result;
    }

    private static void ValidateFloatTuple(string[] values, int count, string field)
    {
        if (values.Length != count + 1 || values.Skip(1).Any(value => !IsFiniteInvariantDouble(value)))
        {
            throw Invalid($"CUBE 文件的 {field} 行无效。");
        }
    }

    private static (double X, double Y, double Z) ParseVector(string[] values, string field)
    {
        if (values.Length != 4 ||
            !double.TryParse(values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
            !double.TryParse(values[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) ||
            !double.TryParse(values[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var z) ||
            !double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z))
        {
            throw Invalid($"CUBE 文件的 {field} 行无效。");
        }

        return (x, y, z);
    }

    private static bool IsFiniteInvariantDouble(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) &&
        double.IsFinite(result);

    private static int MaximumExpectedRows(int? oneDimensionalSize, int? threeDimensionalSize) => checked(
        oneDimensionalSize.GetValueOrDefault() +
        (threeDimensionalSize is { } size ? size * size * size : 0));

    private static string StripComment(string value)
    {
        var insideQuotes = false;
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '"')
            {
                insideQuotes = !insideQuotes;
            }
            else if (value[index] == '#' && !insideQuotes)
            {
                return value[..index];
            }
        }

        return value;
    }

    private static ApiException Invalid(string message) => new(
        StatusCodes.Status415UnsupportedMediaType,
        "invalid_cube_lut",
        message);
}
