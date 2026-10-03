internal static class DeletionConfirmationSourceSelfTests
{
    public static void DestructiveActionsUseClickConfirmation()
    {
        var root = RepositoryRoot();
        var client = Path.Combine(root, "src", "InternalAssetLibrary.Client");
        var localMarkers = File.ReadAllText(Path.Combine(client, "LocalMarkerWindow.axaml.cs"));
        var cloudMarkers = File.ReadAllText(Path.Combine(client, "CloudMarkerWindow.axaml.cs"));
        var mainWindow = File.ReadAllText(Path.Combine(client, "MainWindow.axaml.cs"));
        var tags = File.ReadAllText(Path.Combine(client, "TagPickerWindow.axaml.cs"));
        var login = File.ReadAllText(Path.Combine(client, "LoginWindow.axaml.cs"));
        var luts = File.ReadAllText(Path.Combine(client, "LutLibraryWindow.axaml.cs"));

        AssertConfirmation(localMarkers, "DeleteSet_OnClick");
        AssertConfirmation(localMarkers, "DeleteMarker_OnClick");
        AssertConfirmation(cloudMarkers, "DeleteSet_OnClick");
        AssertConfirmation(cloudMarkers, "DeleteMarker_OnClick");
        AssertConfirmation(mainWindow, "PermanentlyDeleteMyRecycleBinAsset_OnClick");
        AssertConfirmation(mainWindow, "ClearMyRecycleBin_OnClick");
        AssertConfirmation(mainWindow, "BatchRecycleCloudAssets_OnClick");
        AssertConfirmation(mainWindow, "DeleteAvatar_OnClick");
        AssertConfirmation(mainWindow, "RecycleSelectedCloudAsset_OnClick");
        AssertConfirmation(tags, "DeleteTag_OnClick");
        AssertConfirmation(login, "DeleteSavedAccount_OnClick");
        AssertConfirmation(luts, "RecycleCloud_OnClick");

        DoesNotContain("输入标记集名称“{0}”以确认删除。", localMarkers);
        DoesNotContain("输入标记集名称“{0}”以确认删除。", cloudMarkers);

        var adminWeb = File.ReadAllText(Path.Combine(
            root,
            "src",
            "InternalAssetLibrary.Server",
            "wwwroot",
            "app.js"));
        Contains("if (!confirm('确定移除当前头像吗？", adminWeb);
        Contains("if (!confirm(`将永久删除账号 @${button.dataset.username}", adminWeb);
    }

    private static void AssertConfirmation(string source, string methodName)
    {
        var body = MethodSource(source, methodName);
        Contains("new MessageDialogWindow(", body);
        Contains(".ShowDialog<bool>(this)", body);
        DoesNotContain("new TextPromptWindow(", body);
    }

    private static string MethodSource(string source, string methodName)
    {
        var start = source.IndexOf(methodName, StringComparison.Ordinal);
        if (start < 0)
        {
            throw new InvalidOperationException($"Could not find method {methodName}.");
        }

        var end = source.IndexOf("\n    private ", start + methodName.Length, StringComparison.Ordinal);
        return end < 0 ? source[start..] : source[start..end];
    }

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

    private static void Contains(string expected, string actual)
    {
        if (!actual.Contains(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected source to contain '{expected}'.");
        }
    }

    private static void DoesNotContain(string unexpected, string actual)
    {
        if (actual.Contains(unexpected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Did not expect source to contain '{unexpected}'.");
        }
    }
}
