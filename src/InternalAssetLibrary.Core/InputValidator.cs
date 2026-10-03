using System.Globalization;
using System.Net.Mail;
using InternalAssetLibrary.Contracts;

namespace InternalAssetLibrary.Core;

public sealed record ValidationError(string Field, string Code);

public sealed record ValidationResult(IReadOnlyList<ValidationError> Errors)
{
    public bool IsValid => Errors.Count == 0;

    public static ValidationResult Valid { get; } = new(Array.Empty<ValidationError>());
}

public static class InputValidator
{
    public const int UsernameMinLength = 3;
    public const int UsernameMaxLength = 32;
    public const int PasswordMinLength = 8;
    public const int PasswordMaxLength = 128;
    public const int DisplayNameMaxLength = 40;
    public const int BiographyMaxLength = 500;
    public const int ContactMaxLength = 200;
    public const int CustomGenderMaxLength = 40;
    public const int AssetNameMaxLength = 255;
    public const int AssetNoteMaxLength = 2_000;
    public const int TagNameMaxLength = 40;
    public const int MaximumPageSize = 200;
    public const long AvatarMaxBytes = 10L * 1024 * 1024;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".tiff", ".tif",
        ".svg", ".ai", ".eps", ".psd"
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".aac", ".ogg", ".m4a", ".flac", ".ape", ".wav", ".caf"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".flv", ".webm"
    };

    private static readonly HashSet<string> AvatarContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/png", "image/webp"
    };

    public static ValidationResult ValidateUsername(string? username)
    {
        var errors = new List<ValidationError>();
        if (string.IsNullOrWhiteSpace(username))
        {
            errors.Add(new("username", "required"));
            return new(errors);
        }

        var normalized = username.Trim();
        if (normalized.Length is < UsernameMinLength or > UsernameMaxLength)
        {
            errors.Add(new("username", "length"));
        }

        if (normalized.Length > 0 && !IsAsciiLetterOrDigit(normalized[0]))
        {
            errors.Add(new("username", "invalid_start"));
        }

        if (normalized.Any(character =>
                !IsAsciiLetterOrDigit(character) && character is not '_' and not '-' and not '.'))
        {
            errors.Add(new("username", "invalid_character"));
        }

        return new(errors);
    }

    public static ValidationResult ValidatePassword(string? password)
    {
        if (password is null)
        {
            return new([new("password", "required")]);
        }

        if (password.Length is < PasswordMinLength or > PasswordMaxLength)
        {
            return new([new("password", "length")]);
        }

        var characterClassCount = 0;
        if (password.Any(character => character is >= 'A' and <= 'Z'))
        {
            characterClassCount++;
        }

        if (password.Any(character => character is >= 'a' and <= 'z'))
        {
            characterClassCount++;
        }

        if (password.Any(character => character is >= '0' and <= '9'))
        {
            characterClassCount++;
        }

        if (password.Any(character =>
                !char.IsWhiteSpace(character) && !IsAsciiLetterOrDigit(character)))
        {
            characterClassCount++;
        }

        if (characterClassCount < 2)
        {
            return new([new("password", "complexity")]);
        }

        return ValidationResult.Valid;
    }

    public static ValidationResult ValidateEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return ValidationResult.Valid;
        }

        if (email.Length > 254 || !string.Equals(email, email.Trim(), StringComparison.Ordinal))
        {
            return new([new("email", "invalid")]);
        }

        try
        {
            var parsed = new MailAddress(email);
            return string.Equals(parsed.Address, email, StringComparison.OrdinalIgnoreCase)
                ? ValidationResult.Valid
                : new([new("email", "invalid")]);
        }
        catch (FormatException)
        {
            return new([new("email", "invalid")]);
        }
    }

    public static ValidationResult ValidateProfile(UpdateUserProfileRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new List<ValidationError>();
        ValidateOptionalText(errors, "displayName", request.DisplayName, DisplayNameMaxLength);
        ValidateOptionalText(errors, "biography", request.Biography, BiographyMaxLength);
        ValidateOptionalText(errors, "contact", request.Contact, ContactMaxLength);
        ValidateOptionalText(errors, "customGender", request.CustomGender, CustomGenderMaxLength);

        if (request.Gender == ProfileGender.Custom && string.IsNullOrWhiteSpace(request.CustomGender))
        {
            errors.Add(new("customGender", "required"));
        }
        else if (request.Gender != ProfileGender.Custom && !string.IsNullOrEmpty(request.CustomGender))
        {
            errors.Add(new("customGender", "not_applicable"));
        }

        return new(errors);
    }

    public static ValidationResult ValidateAvatar(long byteLength, string? contentType)
    {
        var errors = new List<ValidationError>();
        if (byteLength is <= 0 or > AvatarMaxBytes)
        {
            errors.Add(new("avatar", "size"));
        }

        if (contentType is null || !AvatarContentTypes.Contains(contentType))
        {
            errors.Add(new("avatar", "content_type"));
        }

        return new(errors);
    }

    public static ValidationResult ValidateTagName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return new([new("name", "required")]);
        }

        return CountTextElements(name.Trim()) <= TagNameMaxLength
            ? ValidationResult.Valid
            : new([new("name", "length")]);
    }

    public static ValidationResult ValidatePage(int page, int pageSize)
    {
        var errors = new List<ValidationError>();
        if (page < 1)
        {
            errors.Add(new("page", "range"));
        }

        if (pageSize is < 1 or > MaximumPageSize)
        {
            errors.Add(new("pageSize", "range"));
        }

        return new(errors);
    }

    public static ValidationResult ValidateUpload(BeginUploadRequest request, AssetUploadLimits limits)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(limits);
        var errors = new List<ValidationError>();

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            errors.Add(new("name", "required"));
        }
        else if (CountTextElements(request.Name.Trim()) > AssetNameMaxLength)
        {
            errors.Add(new("name", "length"));
        }

        if (string.IsNullOrWhiteSpace(request.OriginalFileName) ||
            CountTextElements(request.OriginalFileName) > AssetNameMaxLength ||
            request.OriginalFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            errors.Add(new("originalFileName", "invalid"));
        }
        else if (!IsExtensionAllowed(request.OriginalFileName, request.Category))
        {
            errors.Add(new("originalFileName", "category_mismatch"));
        }

        if (request.FileSize <= 0 || request.FileSize > limits.For(request.Category))
        {
            errors.Add(new("fileSize", "range"));
        }

        if (request.Sha256 is null ||
            request.Sha256.Length != 64 ||
            request.Sha256.Any(character => !Uri.IsHexDigit(character)))
        {
            errors.Add(new("sha256", "invalid"));
        }

        ValidateOptionalText(errors, "note", request.Note, AssetNoteMaxLength);

        if (request.TagIds is null)
        {
            errors.Add(new("tagIds", "required"));
        }
        else if (request.TagIds.Count != request.TagIds.Distinct().Count())
        {
            errors.Add(new("tagIds", "duplicate"));
        }

        return new(errors);
    }

    public static ValidationResult ValidateMarker(MarkerCsvEntry marker, TimeSpan? duration = null)
    {
        ArgumentNullException.ThrowIfNull(marker);
        var errors = new List<ValidationError>();
        if (marker.Time < TimeSpan.Zero ||
            marker.Time.Ticks % TimeSpan.TicksPerMillisecond != 0 ||
            duration is not null && marker.Time > duration.Value)
        {
            errors.Add(new("time", "range"));
        }

        return new(errors);
    }

    public static bool IsExtensionAllowed(string fileName, AssetCategory category)
    {
        var extension = Path.GetExtension(fileName);
        return category switch
        {
            AssetCategory.Image => ImageExtensions.Contains(extension),
            AssetCategory.Bgm or AssetCategory.SoundEffect => AudioExtensions.Contains(extension),
            AssetCategory.Video => VideoExtensions.Contains(extension),
            _ => false
        };
    }

    public static AssetCategory? InferUnambiguousCategory(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        if (ImageExtensions.Contains(extension))
        {
            return AssetCategory.Image;
        }

        if (VideoExtensions.Contains(extension))
        {
            return AssetCategory.Video;
        }

        return null;
    }

    private static void ValidateOptionalText(
        ICollection<ValidationError> errors,
        string field,
        string? value,
        int maximumLength)
    {
        if (value is not null && CountTextElements(value) > maximumLength)
        {
            errors.Add(new(field, "length"));
        }
    }

    private static int CountTextElements(string value) => new StringInfo(value).LengthInTextElements;

    private static bool IsAsciiLetterOrDigit(char character) =>
        character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9';
}
