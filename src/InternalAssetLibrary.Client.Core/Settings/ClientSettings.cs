using System.Net;
using System.Net.Sockets;

namespace InternalAssetLibrary.Client.Core.Settings;

public enum UiLanguage
{
    ChineseSimplified,
    English
}

public enum ThemePreference
{
    Dark,
    Light,
    System
}

public enum AssetDisplayMode
{
    Grid,
    List
}

public enum ApplicationCloseAction
{
    MinimizeToTray,
    ExitApplication
}

public sealed record SemanticColorPalette(
    string PageBackground,
    string Surface,
    string Accent,
    string PrimaryText,
    string SecondaryText,
    string Border,
    string Success,
    string Warning,
    string Error);

public sealed record SavedLoginAccount(
    Guid UserId,
    string Username,
    string DisplayName,
    bool PasswordRemembered,
    DateTimeOffset LastUsedAt);

public sealed record PlaybackShortcutSettings
{
    public string PlayPause { get; init; } = "Space";

    public string SeekBackwardFiveSeconds { get; init; } = "Left";

    public string SeekForwardFiveSeconds { get; init; } = "Right";

    public string SeekBackwardOneSecond { get; init; } = "Shift+Left";

    public string SeekForwardOneSecond { get; init; } = "Shift+Right";

    public string SetInPoint { get; init; } = "I";

    public string SetOutPoint { get; init; } = "O";

    public string AddMarker { get; init; } = "M";

    public string OpenFiles { get; init; } = "Ctrl+O";

    public string OpenFolder { get; init; } = "Ctrl+Shift+O";

    public string Export { get; init; } = "Ctrl+E";

    public string VolumeUp { get; init; } = "Up";

    public string VolumeDown { get; init; } = "Down";

    public string ToggleFullscreen { get; init; } = "F";

    public string ExitFullscreen { get; init; } = "Escape";

    public PlaybackShortcutSettings ValidateAndNormalize()
    {
        var normalized = this with
        {
            PlayPause = Normalize(PlayPause),
            SeekBackwardFiveSeconds = Normalize(SeekBackwardFiveSeconds),
            SeekForwardFiveSeconds = Normalize(SeekForwardFiveSeconds),
            SeekBackwardOneSecond = Normalize(SeekBackwardOneSecond),
            SeekForwardOneSecond = Normalize(SeekForwardOneSecond),
            SetInPoint = Normalize(SetInPoint),
            SetOutPoint = Normalize(SetOutPoint),
            AddMarker = Normalize(AddMarker),
            OpenFiles = Normalize(OpenFiles),
            OpenFolder = Normalize(OpenFolder),
            Export = Normalize(Export),
            VolumeUp = Normalize(VolumeUp),
            VolumeDown = Normalize(VolumeDown),
            ToggleFullscreen = Normalize(ToggleFullscreen),
            ExitFullscreen = Normalize(ExitFullscreen)
        };

        var duplicate = normalized.AllGestures()
            .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidDataException($"Playback shortcut '{duplicate.Key}' is assigned more than once.");
        }

        return normalized;
    }

    private IEnumerable<string> AllGestures()
    {
        yield return PlayPause;
        yield return SeekBackwardFiveSeconds;
        yield return SeekForwardFiveSeconds;
        yield return SeekBackwardOneSecond;
        yield return SeekForwardOneSecond;
        yield return SetInPoint;
        yield return SetOutPoint;
        yield return AddMarker;
        yield return OpenFiles;
        yield return OpenFolder;
        yield return Export;
        yield return VolumeUp;
        yield return VolumeDown;
        yield return ToggleFullscreen;
        yield return ExitFullscreen;
    }

    private static string Normalize(string? gesture)
    {
        var normalized = gesture?.Trim();
        if (string.IsNullOrEmpty(normalized) || normalized.Length > 64)
        {
            throw new InvalidDataException("Playback shortcuts must contain 1 to 64 characters.");
        }

        return normalized;
    }
}

public static class DefaultColorPalettes
{
    public static SemanticColorPalette Dark { get; } = new(
        "#111318",
        "#1B1E24",
        "#4CC2FF",
        "#F8FAFC",
        "#B5BFCC",
        "#343A46",
        "#34D399",
        "#FBBF24",
        "#FB7185");

    public static SemanticColorPalette Light { get; } = new(
        "#F6F8FB",
        "#FFFFFF",
        "#0078D4",
        "#111827",
        "#536171",
        "#D5DCE5",
        "#059669",
        "#D97706",
        "#DC2626");
}

public sealed record ClientSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public UiLanguage Language { get; init; } = UiLanguage.ChineseSimplified;

    public ThemePreference Theme { get; init; } = ThemePreference.System;

    public bool FollowSystemAccent { get; init; } = true;

    public bool ReduceMotion { get; init; }

    public AssetDisplayMode LocalAssetDisplayMode { get; init; } = AssetDisplayMode.Grid;

    public AssetDisplayMode CloudAssetDisplayMode { get; init; } = AssetDisplayMode.Grid;

    public bool LocalFolderSidebarExpanded { get; init; } = true;

    public double CloudFolderHeight { get; init; } = 156;

    public SemanticColorPalette DarkColors { get; init; } = DefaultColorPalettes.Dark;

    public SemanticColorPalette LightColors { get; init; } = DefaultColorPalettes.Light;

    public bool LocalHoverPreviewEnabled { get; init; }

    public bool CloudHoverPreviewEnabled { get; init; }

    public bool SingleClickPreviewEnabled { get; init; }

    public bool VideoPreviewMuted { get; init; }

    public double MasterVolume { get; init; } = 40;

    public double PlaybackSpeed { get; init; } = 1;

    public bool PlaybackLoopEnabled { get; init; }

    public bool PlaybackShuffleEnabled { get; init; }

    public PlaybackShortcutSettings PlaybackShortcuts { get; init; } = new();

    public string ServerAddress { get; init; } = "http://127.0.0.1:5019/";

    public IReadOnlyList<SavedLoginAccount> SavedLoginAccounts { get; init; } = [];

    public string? PersistentDownloadDirectory { get; init; }

    public bool DownloadToDefaultDirectory { get; init; } = true;

    public ApplicationCloseAction CloseAction { get; init; } = ApplicationCloseAction.MinimizeToTray;

    public bool AskOnClose { get; init; } = true;

    public SemanticColorPalette GetActiveColors(bool systemUsesDarkTheme) => Theme switch
    {
        ThemePreference.Dark => DarkColors,
        ThemePreference.Light => LightColors,
        ThemePreference.System => systemUsesDarkTheme ? DarkColors : LightColors,
        _ => throw new ArgumentOutOfRangeException(nameof(Theme), Theme, null)
    };

    public ClientSettings RestorePreferenceDefaults()
    {
        var defaults = new ClientSettings();
        return (defaults with
        {
            ServerAddress = ServerAddress,
            PersistentDownloadDirectory = PersistentDownloadDirectory,
            SavedLoginAccounts = SavedLoginAccounts ?? []
        }).ValidateAndNormalize();
    }

    public ClientSettings ValidateAndNormalize()
    {
        if (SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException("The client settings have an unsupported schema.");
        }

        if (!Enum.IsDefined(Language) ||
            !Enum.IsDefined(Theme) ||
            !Enum.IsDefined(LocalAssetDisplayMode) ||
            !Enum.IsDefined(CloudAssetDisplayMode) ||
            !Enum.IsDefined(CloseAction))
        {
            throw new InvalidDataException("The client settings contain an unknown option.");
        }

        if (!double.IsFinite(MasterVolume) || MasterVolume is < 0 or > 100 ||
            !double.IsFinite(PlaybackSpeed) || PlaybackSpeed is < 0.25 or > 4)
        {
            throw new InvalidDataException("The client settings contain invalid playback values.");
        }

        var savedAccounts = (SavedLoginAccounts ?? [])
            .Where(account => account is not null &&
                              account.UserId != Guid.Empty &&
                              !string.IsNullOrWhiteSpace(account.Username))
            .GroupBy(account => account.UserId)
            .Select(group => group.OrderByDescending(account => account.LastUsedAt).First())
            .Select(account => account with
            {
                Username = account.Username.Trim(),
                DisplayName = account.DisplayName?.Trim() ?? string.Empty
            })
            .OrderByDescending(account => account.LastUsedAt)
            .Take(50)
            .ToArray();

        return this with
        {
            CloudFolderHeight = double.IsFinite(CloudFolderHeight) ? Math.Clamp(CloudFolderHeight, 156, 2000) : 156,
            DarkColors = NormalizePalette(DarkColors),
            LightColors = NormalizePalette(LightColors),
            PlaybackShortcuts = (PlaybackShortcuts ?? new PlaybackShortcutSettings()).ValidateAndNormalize(),
            ServerAddress = NormalizeServerAddress(ServerAddress),
            SavedLoginAccounts = savedAccounts,
            PersistentDownloadDirectory = string.IsNullOrWhiteSpace(PersistentDownloadDirectory)
                ? null
                : Path.GetFullPath(PersistentDownloadDirectory.Trim())
        };
    }

    private static string NormalizeServerAddress(string address)
    {
        if (!Uri.TryCreate(address?.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps &&
             (uri.Scheme != Uri.UriSchemeHttp || !IsAllowedPlainHttpServerAddress(uri))) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            uri.AbsolutePath != "/")
        {
            throw new InvalidDataException(
                "服务器地址格式不正确。请输入完整的 HTTPS 地址；本机回环或 WireGuard 私网 IP 可使用 HTTP。地址不能包含账号、密码、路径、查询参数或片段。");
        }

        return uri.GetLeftPart(UriPartial.Authority) + "/";
    }

    public static bool IsAllowedPlainHttpServerAddress(Uri uri)
    {
        if (uri.IsLoopback)
        {
            return true;
        }

        if (!IPAddress.TryParse(uri.Host, out var address))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 10 ||
                   bytes[0] == 127 ||
                   (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                   (bytes[0] == 192 && bytes[1] == 168);
        }

        return address.AddressFamily == AddressFamily.InterNetworkV6 &&
               (IPAddress.IsLoopback(address) || (address.GetAddressBytes()[0] & 0xFE) == 0xFC);
    }

    private static SemanticColorPalette NormalizePalette(SemanticColorPalette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        return new SemanticColorPalette(
            NormalizeColor(palette.PageBackground),
            NormalizeColor(palette.Surface),
            NormalizeColor(palette.Accent),
            NormalizeColor(palette.PrimaryText),
            NormalizeColor(palette.SecondaryText),
            NormalizeColor(palette.Border),
            NormalizeColor(palette.Success),
            NormalizeColor(palette.Warning),
            NormalizeColor(palette.Error));
    }

    private static string NormalizeColor(string color)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(color);
        var value = color.Trim();
        if (value[0] != '#' || (value.Length != 7 && value.Length != 9) ||
            !value.AsSpan(1).ContainsOnlyHexDigits())
        {
            throw new InvalidDataException($"'{color}' is not a #RRGGBB or #AARRGGBB color.");
        }

        return value.ToUpperInvariant();
    }
}

public sealed record ColorContrastWarning(string ForegroundSlot, string BackgroundSlot, double Ratio);

public static class ColorContrastAnalyzer
{
    public static IReadOnlyList<ColorContrastWarning> FindTextWarnings(
        SemanticColorPalette palette,
        double minimumRatio = 4.5)
    {
        ArgumentNullException.ThrowIfNull(palette);
        if (minimumRatio <= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumRatio));
        }

        var warnings = new List<ColorContrastWarning>();
        AddWarningIfNeeded(warnings, "PrimaryText", palette.PrimaryText, "PageBackground", palette.PageBackground, minimumRatio);
        AddWarningIfNeeded(warnings, "PrimaryText", palette.PrimaryText, "Surface", palette.Surface, minimumRatio);
        AddWarningIfNeeded(warnings, "SecondaryText", palette.SecondaryText, "PageBackground", palette.PageBackground, minimumRatio);
        AddWarningIfNeeded(warnings, "SecondaryText", palette.SecondaryText, "Surface", palette.Surface, minimumRatio);
        return warnings;
    }

    private static void AddWarningIfNeeded(
        ICollection<ColorContrastWarning> warnings,
        string foregroundSlot,
        string foreground,
        string backgroundSlot,
        string background,
        double minimumRatio)
    {
        var ratio = ContrastRatio(foreground, background);
        if (ratio < minimumRatio)
        {
            warnings.Add(new ColorContrastWarning(foregroundSlot, backgroundSlot, ratio));
        }
    }

    private static double ContrastRatio(string first, string second)
    {
        var firstLuminance = RelativeLuminance(first);
        var secondLuminance = RelativeLuminance(second);
        var lighter = Math.Max(firstLuminance, secondLuminance);
        var darker = Math.Min(firstLuminance, secondLuminance);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double RelativeLuminance(string color)
    {
        var rgbOffset = color.Length == 9 ? 3 : 1;
        var red = Convert.ToByte(color.Substring(rgbOffset, 2), 16) / 255d;
        var green = Convert.ToByte(color.Substring(rgbOffset + 2, 2), 16) / 255d;
        var blue = Convert.ToByte(color.Substring(rgbOffset + 4, 2), 16) / 255d;
        return 0.2126 * Linearize(red) + 0.7152 * Linearize(green) + 0.0722 * Linearize(blue);
    }

    private static double Linearize(double channel) => channel <= 0.04045
        ? channel / 12.92
        : Math.Pow((channel + 0.055) / 1.055, 2.4);
}

file static class HexSpanExtensions
{
    public static bool ContainsOnlyHexDigits(this ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (!char.IsAsciiHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }
}
