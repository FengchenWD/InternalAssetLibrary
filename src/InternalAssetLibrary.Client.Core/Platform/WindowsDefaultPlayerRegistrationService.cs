using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

namespace InternalAssetLibrary.Client.Core.Platform;

public sealed record RegistryValueRegistration(string SubKey, string ValueName, string ValueData);

public sealed record DefaultPlayerAssociationStatus(int AssociatedCount, int TotalCount)
{
    public bool IsFullyAssociated => TotalCount > 0 && AssociatedCount == TotalCount;
}

public sealed class WindowsFileAssociationRegistrationPlan
{
    public const string ProductSubKey = @"Software\FengchenWD\InternalAssetLibrary";
    public const string RegisteredApplicationName = "FengchenWD.InternalAssetLibrary";
    public const string CapabilitiesSubKey = $@"{ProductSubKey}\Capabilities";
    public const string ExecutableName = "InternalAssetLibrary.Client.exe";

    private WindowsFileAssociationRegistrationPlan(
        string executablePath,
        IReadOnlyList<RegistryValueRegistration> values)
    {
        ExecutablePath = executablePath;
        Values = values;
    }

    public string ExecutablePath { get; }

    public IReadOnlyList<RegistryValueRegistration> Values { get; }

    public static WindowsFileAssociationRegistrationPlan Create(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        var fullPath = Path.GetFullPath(executablePath);
        var quotedExecutable = $"\"{fullPath}\"";
        var openCommand = $"{quotedExecutable} {FileOpenCommandLine.OpenOption} \"%1\"";
        var values = new List<RegistryValueRegistration>
        {
            new(@"Software\RegisteredApplications", RegisteredApplicationName, CapabilitiesSubKey),
            new(CapabilitiesSubKey, "ApplicationName", "内部共享素材库"),
            new(CapabilitiesSubKey, "ApplicationDescription", "团队素材管理与媒体播放器"),
            new(CapabilitiesSubKey, "ApplicationIcon", $"{quotedExecutable},0"),
            new($@"Software\Classes\Applications\{ExecutableName}", "FriendlyAppName", "内部共享素材库"),
            new($@"Software\Classes\Applications\{ExecutableName}\DefaultIcon", string.Empty, $"{quotedExecutable},0"),
            new($@"Software\Classes\Applications\{ExecutableName}\shell\open\command", string.Empty, openCommand)
        };

        foreach (var group in DefaultPlayerFileAssociations.All.GroupBy(association => association.ProgId))
        {
            var association = group.First();
            var progIdSubKey = $@"Software\Classes\{association.ProgId}";
            values.Add(new RegistryValueRegistration(progIdSubKey, string.Empty, association.FriendlyTypeName));
            values.Add(new RegistryValueRegistration($@"{progIdSubKey}\DefaultIcon", string.Empty, $"{quotedExecutable},0"));
            values.Add(new RegistryValueRegistration($@"{progIdSubKey}\shell", string.Empty, "open"));
            values.Add(new RegistryValueRegistration($@"{progIdSubKey}\shell\open\command", string.Empty, openCommand));
        }

        foreach (var association in DefaultPlayerFileAssociations.All)
        {
            values.Add(new RegistryValueRegistration(
                $@"{CapabilitiesSubKey}\FileAssociations",
                association.Extension,
                association.ProgId));
            values.Add(new RegistryValueRegistration(
                $@"Software\Classes\Applications\{ExecutableName}\SupportedTypes",
                association.Extension,
                string.Empty));
        }

        return new WindowsFileAssociationRegistrationPlan(fullPath, values);
    }
}

public sealed class WindowsDefaultPlayerRegistrationService
{
    private const uint AssociationChangedEvent = 0x08000000;
    private const uint IdListNotification = 0x0000;
    private const uint AssociationStringExecutable = 2;
    private readonly WindowsFileAssociationRegistrationPlan _plan;

    public WindowsDefaultPlayerRegistrationService(string? executablePath = null)
    {
        executablePath ??= Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定客户端可执行文件路径。");
        _plan = WindowsFileAssociationRegistrationPlan.Create(executablePath);
    }

    public bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19045);

    public bool UsesStableInstallPath =>
        PathsEqual(_plan.ExecutablePath, StableExecutablePath) ||
        OperatingSystem.IsWindows() &&
        MatchesInstalledExecutablePath(_plan.ExecutablePath, ReadInstalledExecutablePathWindows());

    public static string StableExecutablePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs",
        "FengchenWD",
        "InternalAssetLibrary",
        WindowsFileAssociationRegistrationPlan.ExecutableName);

    public Uri SettingsUri => GetSettingsUri(Environment.OSVersion.Version);

    public void Register()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19045))
        {
            throw CreatePlatformException();
        }
        if (!UsesStableInstallPath)
        {
            throw new InvalidOperationException("便携版路径可能变化，请安装到固定目录后再配置默认播放器。");
        }

        RegisterWindows();
    }

    public void RegisterAndOpenDefaultAppsSettings()
    {
        Register();
        Process.Start(new ProcessStartInfo(SettingsUri.AbsoluteUri)
        {
            UseShellExecute = true
        });
    }

    public DefaultPlayerAssociationStatus GetStatus()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19045))
        {
            throw CreatePlatformException();
        }

        return GetStatusWindows();
    }

    public void Unregister()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19045))
        {
            throw CreatePlatformException();
        }
        if (!UsesStableInstallPath)
        {
            throw new InvalidOperationException("只有固定目录中的安装版可以移除播放器注册。");
        }

        UnregisterWindows();
    }

    public static Uri GetSettingsUri(Version windowsVersion)
    {
        ArgumentNullException.ThrowIfNull(windowsVersion);
        return windowsVersion.Major >= 10 && windowsVersion.Build >= 22000
            ? new Uri($"ms-settings:defaultapps?registeredAppUser={Uri.EscapeDataString(WindowsFileAssociationRegistrationPlan.RegisteredApplicationName)}")
            : new Uri("ms-settings:defaultapps");
    }

    public static string? ExtractExecutablePath(string? registryValue)
    {
        if (string.IsNullOrWhiteSpace(registryValue))
        {
            return null;
        }

        var value = registryValue.Trim();
        if (value[0] == '"')
        {
            var closingQuote = value.IndexOf('"', 1);
            return closingQuote > 1 ? value[1..closingQuote] : null;
        }

        var comma = value.LastIndexOf(',');
        return comma > 0 ? value[..comma].Trim() : value;
    }

    public static bool MatchesInstalledExecutablePath(
        string currentExecutablePath,
        string? installedExecutablePath) =>
        !string.IsNullOrWhiteSpace(installedExecutablePath) &&
        PathsEqual(currentExecutablePath, installedExecutablePath);

    [SupportedOSPlatform("windows")]
    private void RegisterWindows()
    {
        Registry.CurrentUser.DeleteSubKeyTree(
            $@"{WindowsFileAssociationRegistrationPlan.CapabilitiesSubKey}\FileAssociations",
            throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(
            $@"Software\Classes\Applications\{WindowsFileAssociationRegistrationPlan.ExecutableName}\SupportedTypes",
            throwOnMissingSubKey: false);

        foreach (var value in _plan.Values)
        {
            using var key = Registry.CurrentUser.CreateSubKey(value.SubKey, writable: true)
                ?? throw new InvalidOperationException($"无法写入注册表：HKCU\\{value.SubKey}");
            key.SetValue(value.ValueName, value.ValueData, RegistryValueKind.String);
        }

        SHChangeNotify(AssociationChangedEvent, IdListNotification, IntPtr.Zero, IntPtr.Zero);
    }

    [SupportedOSPlatform("windows")]
    private DefaultPlayerAssociationStatus GetStatusWindows()
    {
        var associatedCount = DefaultPlayerFileAssociations.All.Count(association =>
            QueryAssociatedExecutable(association.Extension) is { Length: > 0 } currentExecutable &&
            PathsEqual(currentExecutable, _plan.ExecutablePath));

        return new DefaultPlayerAssociationStatus(associatedCount, DefaultPlayerFileAssociations.All.Count);
    }

    [SupportedOSPlatform("windows")]
    private static void UnregisterWindows()
    {
        using (var registeredApplications = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", writable: true))
        {
            registeredApplications?.DeleteValue(
                WindowsFileAssociationRegistrationPlan.RegisteredApplicationName,
                throwOnMissingValue: false);
        }

        Registry.CurrentUser.DeleteSubKeyTree(@"Software\FengchenWD\InternalAssetLibrary", throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(
            $@"Software\Classes\Applications\{WindowsFileAssociationRegistrationPlan.ExecutableName}",
            throwOnMissingSubKey: false);
        foreach (var progId in DefaultPlayerFileAssociations.All.Select(item => item.ProgId).Distinct())
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{progId}", throwOnMissingSubKey: false);
        }

        SHChangeNotify(AssociationChangedEvent, IdListNotification, IntPtr.Zero, IntPtr.Zero);
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadInstalledExecutablePathWindows()
    {
        using var productKey = Registry.CurrentUser.OpenSubKey(
            WindowsFileAssociationRegistrationPlan.ProductSubKey,
            writable: false);
        if (productKey?.GetValue("ExecutablePath") is string executablePath &&
            !string.IsNullOrWhiteSpace(executablePath))
        {
            return executablePath;
        }

        using var capabilitiesKey = Registry.CurrentUser.OpenSubKey(
            WindowsFileAssociationRegistrationPlan.CapabilitiesSubKey,
            writable: false);
        return ExtractExecutablePath(capabilitiesKey?.GetValue("ApplicationIcon") as string);
    }

    [SupportedOSPlatform("windows")]
    private static string? QueryAssociatedExecutable(string extension)
    {
        uint length = 0;
        _ = AssocQueryString(0, AssociationStringExecutable, extension, null, null, ref length);
        if (length == 0 || length > 32768)
        {
            return null;
        }

        var buffer = new StringBuilder((int)length);
        return AssocQueryString(0, AssociationStringExecutable, extension, null, buffer, ref length) == 0
            ? buffer.ToString()
            : null;
    }

    private static bool PathsEqual(string first, string second)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(first),
                Path.GetFullPath(second),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static PlatformNotSupportedException CreatePlatformException() =>
        new("默认播放器注册仅支持 Windows 10 22H2 及更高版本。");

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(
        uint eventId,
        uint flags,
        IntPtr item1,
        IntPtr item2);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern uint AssocQueryString(
        uint flags,
        uint associationString,
        string association,
        string? extra,
        StringBuilder? output,
        ref uint outputLength);
}
