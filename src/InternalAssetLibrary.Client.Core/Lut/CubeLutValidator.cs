using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace InternalAssetLibrary.Client.Core.Lut;

public enum CubeLutKind
{
    OneDimensional,
    ThreeDimensional,
    Combined
}

public readonly record struct LutVector3(double X, double Y, double Z);

public sealed record CubeLutDescriptor(
    string? SourcePath,
    string? Title,
    CubeLutKind Kind,
    int? OneDimensionalSize,
    int? ThreeDimensionalSize,
    int DataRowCount,
    LutVector3 DomainMinimum,
    LutVector3 DomainMaximum,
    long? SizeBytes,
    DateTimeOffset? LastWriteTimeUtc,
    string? Sha256);

public sealed record CubeLutValidationResult(
    bool IsValid,
    CubeLutDescriptor? Descriptor,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings)
{
    public string Diagnostic => string.Join(Environment.NewLine, Errors);
}

public sealed class CubeLutValidator
{
    public const long MaximumFileBytes = 16L * 1024 * 1024;
    public const int MaximumLutSize = 65;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public async Task<CubeLutValidationResult> ValidateFileAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var fullPath = Path.GetFullPath(filePath);
        if (!string.Equals(Path.GetExtension(fullPath), ".cube", StringComparison.OrdinalIgnoreCase))
        {
            return Invalid("LUT files must use the .cube extension.");
        }

        var file = new FileInfo(fullPath);
        if (!file.Exists)
        {
            return Invalid($"LUT file does not exist: {fullPath}");
        }

        if (file.Length == 0 || file.Length > MaximumFileBytes)
        {
            return Invalid($"LUT file size must be between 1 byte and {MaximumFileBytes} bytes.");
        }

        string text;
        try
        {
            text = await File.ReadAllTextAsync(fullPath, StrictUtf8, cancellationToken).ConfigureAwait(false);
        }
        catch (DecoderFallbackException)
        {
            return Invalid("LUT file must contain valid UTF-8 text.");
        }

        var parsed = ValidateText(text, fullPath);
        if (!parsed.IsValid || parsed.Descriptor is null)
        {
            return parsed;
        }

        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        return parsed with
        {
            Descriptor = parsed.Descriptor with
            {
                SourcePath = fullPath,
                SizeBytes = file.Length,
                LastWriteTimeUtc = file.LastWriteTimeUtc,
                Sha256 = hash
            }
        };
    }

    public CubeLutValidationResult ValidateText(string text, string? sourcePath = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var errors = new List<string>();
        var warnings = new List<string>();
        int? size1D = null;
        int? size3D = null;
        string? title = null;
        var domainMinimum = new LutVector3(0, 0, 0);
        var domainMaximum = new LutVector3(1, 1, 1);
        var dataRows = 0;

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var lineNumber = index + 1;
            var line = StripComment(lines[index]).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var separator = line.IndexOfAny([' ', '\t']);
            var firstToken = separator < 0 ? line : line[..separator];
            var remainder = separator < 0 ? string.Empty : line[(separator + 1)..].Trim();
            switch (firstToken.ToUpperInvariant())
            {
                case "TITLE":
                    if (title is not null)
                    {
                        errors.Add($"Line {lineNumber}: TITLE is declared more than once.");
                    }
                    else if (!TryParseTitle(remainder, out title))
                    {
                        errors.Add($"Line {lineNumber}: TITLE must contain a non-empty quoted value.");
                    }

                    break;
                case "LUT_1D_SIZE":
                    ParseSize(remainder, lineNumber, "LUT_1D_SIZE", ref size1D, errors);
                    break;
                case "LUT_3D_SIZE":
                    ParseSize(remainder, lineNumber, "LUT_3D_SIZE", ref size3D, errors);
                    break;
                case "DOMAIN_MIN":
                    ParseDomain(remainder, lineNumber, "DOMAIN_MIN", ref domainMinimum, errors);
                    break;
                case "DOMAIN_MAX":
                    ParseDomain(remainder, lineNumber, "DOMAIN_MAX", ref domainMaximum, errors);
                    break;
                default:
                    if (TryParseVector(line, out _))
                    {
                        dataRows++;
                    }
                    else if (char.IsLetter(firstToken[0]) || firstToken[0] == '_')
                    {
                        warnings.Add($"Line {lineNumber}: unsupported directive '{firstToken}' was ignored.");
                    }
                    else
                    {
                        errors.Add($"Line {lineNumber}: expected three finite numeric LUT values.");
                    }

                    break;
            }
        }

        if (!size1D.HasValue && !size3D.HasValue)
        {
            errors.Add("The LUT must declare LUT_1D_SIZE, LUT_3D_SIZE, or both.");
        }

        if (domainMinimum.X >= domainMaximum.X ||
            domainMinimum.Y >= domainMaximum.Y ||
            domainMinimum.Z >= domainMaximum.Z)
        {
            errors.Add("Every DOMAIN_MIN component must be lower than DOMAIN_MAX.");
        }

        var expectedRows = (size1D ?? 0) + (size3D.HasValue ? checked(size3D.Value * size3D.Value * size3D.Value) : 0);
        if (expectedRows > 0 && dataRows != expectedRows)
        {
            errors.Add($"The LUT declares {expectedRows} data rows but contains {dataRows}.");
        }

        if (errors.Count > 0)
        {
            return new CubeLutValidationResult(false, null, errors, warnings);
        }

        var kind = size1D.HasValue && size3D.HasValue
            ? CubeLutKind.Combined
            : size1D.HasValue
                ? CubeLutKind.OneDimensional
                : CubeLutKind.ThreeDimensional;
        return new CubeLutValidationResult(
            true,
            new CubeLutDescriptor(
                string.IsNullOrWhiteSpace(sourcePath) ? null : Path.GetFullPath(sourcePath),
                title,
                kind,
                size1D,
                size3D,
                dataRows,
                domainMinimum,
                domainMaximum,
                null,
                null,
                null),
            [],
            warnings);
    }

    private static void ParseSize(
        string remainder,
        int lineNumber,
        string directive,
        ref int? target,
        List<string> errors)
    {
        if (target.HasValue)
        {
            errors.Add($"Line {lineNumber}: {directive} is declared more than once.");
            return;
        }

        if (!int.TryParse(remainder, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ||
            value is < 2 or > MaximumLutSize)
        {
            errors.Add($"Line {lineNumber}: {directive} must be between 2 and {MaximumLutSize}.");
            return;
        }

        target = value;
    }

    private static void ParseDomain(
        string remainder,
        int lineNumber,
        string directive,
        ref LutVector3 target,
        List<string> errors)
    {
        if (!TryParseVector(remainder, out var value))
        {
            errors.Add($"Line {lineNumber}: {directive} must contain three finite numbers.");
            return;
        }

        target = value;
    }

    private static bool TryParseVector(string value, out LutVector3 vector)
    {
        var parts = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 3 &&
            TryParseFinite(parts[0], out var x) &&
            TryParseFinite(parts[1], out var y) &&
            TryParseFinite(parts[2], out var z))
        {
            vector = new LutVector3(x, y, z);
            return true;
        }

        vector = default;
        return false;
    }

    private static bool TryParseFinite(string value, out double result) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result) &&
        double.IsFinite(result);

    private static bool TryParseTitle(string value, out string? title)
    {
        title = null;
        if (value.Length < 2 || value[0] != '"' || value[^1] != '"')
        {
            return false;
        }

        var parsed = value[1..^1].Trim();
        if (parsed.Length == 0 || parsed.Contains('"'))
        {
            return false;
        }

        title = parsed;
        return true;
    }

    private static string StripComment(string line)
    {
        var insideQuotes = false;
        for (var index = 0; index < line.Length; index++)
        {
            if (line[index] == '"')
            {
                insideQuotes = !insideQuotes;
            }
            else if (line[index] == '#' && !insideQuotes)
            {
                return line[..index];
            }
        }

        return line;
    }

    private static CubeLutValidationResult Invalid(string error) => new(false, null, [error], []);
}
