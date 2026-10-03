using System.Text.RegularExpressions;
using System.Xml.Linq;

internal static class UiLocalizationSourceSelfTests
{
    private static readonly Regex MappingPattern = new(
        "\\[\"(?<key>(?:\\\\.|[^\"\\\\])*)\"\\]\\s*=\\s*\"(?<value>(?:\\\\.|[^\"\\\\])*)\"",
        RegexOptions.CultureInvariant);

    private static readonly Regex StringLiteralPattern = new(
        "\"(?<value>(?:\\\\.|[^\"\\\\])*)\"",
        RegexOptions.CultureInvariant);

    public static void FixedXamlTextHasEnglishMappings()
    {
        var root = RepositoryRoot();
        var localizationPath = Path.Combine(
            root,
            "src",
            "InternalAssetLibrary.Client",
            "Services",
            "UiLocalization.cs");
        var mappings = ReadMappings(localizationPath);
        var clientDirectory = Path.Combine(root, "src", "InternalAssetLibrary.Client");
        var fixedChineseText = Directory.EnumerateFiles(clientDirectory, "*.axaml")
            .SelectMany(ReadFixedChineseText)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var missing = fixedChineseText
            .Where(value => !mappings.ContainsKey(value))
            .ToArray();

        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                $"Missing English mappings for fixed XAML text: {string.Join(" | ", missing)}");
        }

        Equal("Yunting Asset Management Tool", mappings["云汀素材管理工具"]);
        Equal("Language", mappings["界面语言"]);
        Equal("Player", mappings["播放器"]);
        Equal("Team markers", mappings["团队标记"]);
        Equal("Shared team LUTs", mappings["团队云端 LUT"]);
        True(mappings.All(pair =>
            !string.IsNullOrWhiteSpace(pair.Value) &&
            (string.Equals(pair.Key, "English", StringComparison.Ordinal) ||
             !string.Equals(pair.Key, pair.Value, StringComparison.Ordinal))));
    }

    public static void RuntimeValuesRemainOutsideAutomaticTranslation()
    {
        var localizationPath = Path.Combine(
            RepositoryRoot(),
            "src",
            "InternalAssetLibrary.Client",
            "Services",
            "UiLocalization.cs");
        var source = File.ReadAllText(localizationPath);
        var mappings = ReadMappings(localizationPath);
        var runtimeExamples = new[]
        {
            "风尘WD",
            "D:\\团队素材\\荒野片段.mov",
            "DJI_D-Log_Rec709.cube",
            "自定义标签名称",
            "用户输入的标记名称"
        };

        foreach (var value in runtimeExamples)
        {
            False(mappings.ContainsKey(value));
        }

        Contains("English.ContainsKey(value)", source);
        Contains("BindingOperations.GetBindingExpressionBase(target, property) is null", source);
        Contains("root.GetLogicalDescendants()", source);
        Contains("root.GetVisualDescendants()", source);
        Contains("ItemsControlObservers.GetValue(", source);
        Contains("new ItemsControlObserver(value)", source);
        Contains("itemsControl.ContainerPrepared += OnContainerPrepared", source);
        Contains("string.Equals(current, LastRendered, StringComparison.Ordinal)", source);
        Contains("_isDetached = true", source);
        Contains("public static string Format(", source);
        Contains("internal static void Register<T>", source);
        DoesNotContain("control.DataContext is not null", source);
    }

    public static void DynamicLocalizationRegistrationsRemainBounded()
    {
        var root = RepositoryRoot();
        var localizationSource = File.ReadAllText(Path.Combine(
            root,
            "src",
            "InternalAssetLibrary.Client",
            "Services",
            "UiLocalization.cs"));
        var viewModelSource = File.ReadAllText(Path.Combine(
            root,
            "src",
            "InternalAssetLibrary.Client",
            "ViewModels",
            "CloudViewModels.cs"));

        Contains("SubscriberCompactionInterval = 128", localizationSource);
        Contains("CompactSubscribers();", localizationSource);
        Contains("bool IsAlive { get; }", localizationSource);
        Contains("nameof(DisplayLabel)", viewModelSource);
    }

    public static void AutomaticMarkerNamesAndStableApiErrorsAreLocalized()
    {
        var root = RepositoryRoot();
        var mappings = ReadMappings(Path.Combine(
            root,
            "src",
            "InternalAssetLibrary.Client",
            "Services",
            "UiLocalization.cs"));
        var playbackSource = File.ReadAllText(Path.Combine(
            root,
            "src",
            "InternalAssetLibrary.Client",
            "MainWindow.Playback.cs"));
        var apiErrorSource = File.ReadAllText(Path.Combine(
            root,
            "src",
            "InternalAssetLibrary.Client",
            "Services",
            "ApiErrorLocalization.cs"));

        Equal("My markers", mappings["我的标记"]);
        Equal(2, Regex.Matches(
            playbackSource,
            "UiLocalization\\.Text\\(\"我的标记\"\\)",
            RegexOptions.CultureInvariant).Count);
        foreach (var code in new[]
                 {
                     "invalid_credentials",
                     "authentication_required",
                     "permission_denied",
                     "weak_password",
                     "incorrect_password",
                     "duplicate_content"
                 })
        {
            Contains($"\"{code}\" =>", apiErrorSource);
        }

        DoesNotContain("exception.Detail.Contains", apiErrorSource);
    }

    public static void ClientRuntimeTextHasEnglishMappings()
    {
        var root = RepositoryRoot();
        var localizationPath = Path.Combine(
            root,
            "src",
            "InternalAssetLibrary.Client",
            "Services",
            "UiLocalization.cs");
        var mappings = ReadMappings(localizationPath);
        var clientDirectory = Path.Combine(root, "src", "InternalAssetLibrary.Client");
        var sourcePaths = Directory.EnumerateFiles(clientDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !string.Equals(path, localizationPath, StringComparison.OrdinalIgnoreCase))
            .Where(path => !Path.GetRelativePath(clientDirectory, path)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment is "bin" or "obj"))
            .ToArray();
        var missing = sourcePaths
            .SelectMany(path => StringLiteralPattern.Matches(File.ReadAllText(path))
                .Select(match => Regex.Unescape(match.Groups["value"].Value))
                .Where(ContainsHan)
                .Where(value => !mappings.ContainsKey(value))
                .Select(value => $"{Path.GetRelativePath(root, path)}: {value}"))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                $"Missing English mappings for client runtime text: {string.Join(" | ", missing)}");
        }

        var mainWindowSource = File.ReadAllText(Path.Combine(
            root,
            "src",
            "InternalAssetLibrary.Client",
            "MainWindow.axaml.cs"));
        var playbackSource = File.ReadAllText(Path.Combine(
            root,
            "src",
            "InternalAssetLibrary.Client",
            "MainWindow.Playback.cs"));

        Contains("UiLocalization.SetText(ClientVersionText", mainWindowSource);
        Contains("UiLocalization.SetContent(ServerLoginButton", mainWindowSource);
        Contains("UiLocalization.SetText(DetailStatus", mainWindowSource);
        Contains("UiLocalization.Text(\"选择要上传的素材\")", mainWindowSource);
        Equal("Could not add the marker: {0}", mappings["标记添加失败：{0}"]);
        Equal(
            "No library asset is currently playing, so a marker cannot be saved.",
            mappings["当前没有可保存标记的素材正在播放。"]);
        Equal("Failed: ", mappings["失败："]);
        Equal(2, Regex.Matches(
            playbackSource,
            Regex.Escape("\"标记添加失败：{0}\""),
            RegexOptions.CultureInvariant).Count);
        Equal(1, Regex.Matches(
            mainWindowSource,
            Regex.Escape("UiLocalization.Format(\"失败：{0}\", UserMessage(exception))"),
            RegexOptions.CultureInvariant).Count);
    }

    public static void WindowsImeCompatibilityStaysGlobalAndShortcutSafe()
    {
        var root = RepositoryRoot();
        var compatibilitySource = File.ReadAllText(Path.Combine(
            root,
            "src",
            "InternalAssetLibrary.Client",
            "Services",
            "WindowsImeCompatibility.cs"));
        var appSource = File.ReadAllText(Path.Combine(
            root,
            "src",
            "InternalAssetLibrary.Client",
            "App.axaml.cs"));
        var editorSource = File.ReadAllText(Path.Combine(
            root,
            "src",
            "InternalAssetLibrary.Client",
            "Controls",
            "EditorWorkspaceView.axaml.cs"));

        Contains("GotFocusEvent.AddClassHandler<TextBox>", compatibilitySource);
        Contains("InputMethod.SetIsInputMethodEnabled(textBox, true)", compatibilitySource);
        Contains("InputLanguageChangedMessage", compatibilitySource);
        Contains("SendMessageW(", compatibilitySource);
        Contains("OperatingSystem.IsWindows()", compatibilitySource);
        Contains("WindowsImeCompatibility.Initialize();", appSource);
        Contains("eventArgs.Source is TextBox", editorSource);
        Contains("control.FindAncestorOfType<TextBox>()", editorSource);
    }

    private static IReadOnlyDictionary<string, string> ReadMappings(string path)
    {
        var source = File.ReadAllText(path);
        var mappings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in MappingPattern.Matches(source))
        {
            var key = Regex.Unescape(match.Groups["key"].Value);
            var value = Regex.Unescape(match.Groups["value"].Value);
            if (!mappings.TryAdd(key, value))
            {
                throw new InvalidOperationException($"Duplicate localization key: {key}");
            }
        }

        return mappings;
    }

    private static IEnumerable<string> ReadFixedChineseText(string path)
    {
        var document = XDocument.Load(path, LoadOptions.PreserveWhitespace);
        return document
            .Descendants()
            .SelectMany(element =>
                element.Attributes().Select(attribute => attribute.Value)
                    .Concat(element.Nodes().OfType<XText>().Select(text => text.Value)))
            .Select(value => value.Trim())
            .Where(value => value.Length > 0 && ContainsHan(value));
    }

    private static bool ContainsHan(string value) =>
        value.Any(character => character is >= '\u3400' and <= '\u9fff');

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "InternalAssetLibrary.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private static void Contains(string expectedSubstring, string actual)
    {
        if (!actual.Contains(expectedSubstring, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected source to contain '{expectedSubstring}'.");
        }
    }

    private static void DoesNotContain(string unexpectedSubstring, string actual)
    {
        if (actual.Contains(unexpectedSubstring, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Did not expect source to contain '{unexpectedSubstring}'.");
        }
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
        }
    }

    private static void True(bool value)
    {
        if (!value)
        {
            throw new InvalidOperationException("Expected true.");
        }
    }

    private static void False(bool value) => True(!value);
}
