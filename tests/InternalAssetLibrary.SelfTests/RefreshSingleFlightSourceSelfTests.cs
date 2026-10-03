internal static class RefreshSingleFlightSourceSelfTests
{
    public static void LatestPageRefreshCancelsStaleNetworkAndUiWork()
    {
        var root = RepositoryRoot();
        var window = File.ReadAllText(Path.Combine(
            root,
            "src",
            "InternalAssetLibrary.Client",
            "MainWindow.axaml.cs"));
        var profileLuts = File.ReadAllText(Path.Combine(
            root,
            "src",
            "InternalAssetLibrary.Client",
            "MainWindow.ProfileLuts.cs"));

        Contains("if (page == _activePage)", window);
        Contains(
            "if (page == \"profile\" && _currentUser is not null && !_profileOpenedFromUsers)",
            window);
        Contains("CancelPageRefresh(_activePage);", window);
        Contains("StartLatestRefresh(ref _cloudRefreshCancellation)", window);
        Contains("StartLatestRefresh(ref _usersRefreshCancellation)", window);
        Contains("StartLatestRefresh(ref _recycleBinRefreshCancellation)", window);
        Contains("StartLatestRefresh(ref _profileContentRefreshCancellation)", window);
        Contains("ListAllAssetsAsync(query, cancellationToken)", window);
        Contains("_api.ListTagsAsync(cancellationToken)", window);
        Contains("ListAllProfileLutsAsync(userId, cancellationToken)", window);
        Contains("api.GetUserAsync(userId, cancellationToken)", window);
        Contains("IsCurrentProfileContentRequest(requestVersion, userId, cancellationToken)", window);
        Contains("if (!ProfilePage.IsVisible || api is null", window);
        Contains("if (!UsersPage.IsVisible || _api is null", window);
        Contains("LoadUserCardAvatarAsync(card, requestVersion, cancellationToken)", window);
        Contains("cancellationToken.ThrowIfCancellationRequested();", profileLuts);
        Contains("UploaderId: uploaderId), cancellationToken", profileLuts);

        var profileCore = Slice(
            window,
            "private async Task LoadProfileAssetsCoreAsync(",
            "private bool IsCurrentProfileContentRequest(");
        Before(
            "IsCurrentProfileContentRequest(requestVersion, userId, cancellationToken)",
            "UpdateProfileAssetCounts(assetCounts)",
            profileCore);

        var profileRequestGuard = Slice(
            window,
            "private bool IsCurrentProfileContentRequest(",
            "private async Task<IReadOnlyList<ApiAsset>> ListAllAssetsAsync(");
        Contains("ProfilePage.IsVisible", profileRequestGuard);

        foreach (var method in new[]
                 {
                     "private async Task RefreshCloudAssetsAsync()",
                     "private async Task RefreshUsersAsync()",
                     "private async Task RefreshMyRecycleBinAsync()"
                 })
        {
            var body = Slice(window, method, "\n    private ", method.Length);
            Before("_currentUser?.Id != expectedUserId", "HandleSessionFailureAsync(exception)", body);
        }
    }

    private static string Slice(
        string source,
        string startMarker,
        string endMarker,
        int endSearchOffset = 0)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        if (start < 0)
        {
            throw new InvalidOperationException($"Expected source to contain '{startMarker}'.");
        }

        var end = source.IndexOf(endMarker, start + endSearchOffset, StringComparison.Ordinal);
        if (end < 0)
        {
            throw new InvalidOperationException($"Expected source after '{startMarker}' to contain '{endMarker}'.");
        }

        return source[start..end];
    }

    private static void Before(string first, string second, string source)
    {
        var firstIndex = source.IndexOf(first, StringComparison.Ordinal);
        var secondIndex = source.IndexOf(second, StringComparison.Ordinal);
        if (firstIndex < 0 || secondIndex < 0 || firstIndex >= secondIndex)
        {
            throw new InvalidOperationException($"Expected '{first}' before '{second}'.");
        }
    }

    private static void Contains(string expectedSubstring, string actual)
    {
        if (!actual.Contains(expectedSubstring, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected source to contain '{expectedSubstring}'.");
        }
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
}
