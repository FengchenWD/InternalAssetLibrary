internal static class SemanticColorEditorSourceSelfTests
{
    public static void OfficialColorPickerAndTextEditingStaySynchronized()
    {
        var root = RepositoryRoot();
        var project = Read(root, "src", "InternalAssetLibrary.Client", "InternalAssetLibrary.Client.csproj");
        var app = Read(root, "src", "InternalAssetLibrary.Client", "App.axaml");
        var xaml = Read(root, "src", "InternalAssetLibrary.Client", "SemanticColorEditorWindow.axaml");
        var source = Read(root, "src", "InternalAssetLibrary.Client", "SemanticColorEditorWindow.axaml.cs");

        Contains("<PackageReference Include=\"Avalonia.Controls.ColorPicker\" Version=\"11.3.7\" />", project);
        Contains("avares://Avalonia.Controls.ColorPicker/Themes/Fluent/Fluent.xaml", app);
        Contains("<ColorPicker Grid.Column=\"1\"", xaml);
        Contains("Color=\"{Binding SelectedColor, Mode=TwoWay}\"", xaml);
        Contains("HexInputAlphaPosition=\"Leading\"", xaml);
        Contains("IsAlphaEnabled=\"True\"", xaml);
        Contains("IsAlphaVisible=\"True\"", xaml);
        Contains("Text=\"{Binding Value, UpdateSourceTrigger=PropertyChanged}\"", xaml);

        Contains("public Color SelectedColor", source);
        Contains("set => SetSelectedColor(value, updateValue: true);", source);
        Contains("SetSelectedColor(color, updateValue: false);", source);
        Contains("? $\"#{color.R:X2}{color.G:X2}{color.B:X2}\"", source);
        Contains(": $\"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}\"", source);
        Contains("LoadSlots(_loadedPaletteIndex == 1 ? _light : _dark);", source);
        Contains("if (!TryCaptureCurrent(_loadedPaletteIndex))", source);
    }

    private static string Read(string root, params string[] path) =>
        File.ReadAllText(Path.Combine([root, .. path]));

    private static void Contains(string expected, string source)
    {
        if (!source.Contains(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected source to contain '{expected}'.");
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "InternalAssetLibrary.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
