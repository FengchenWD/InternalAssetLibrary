using System.Text.Json;
using InternalAssetLibrary.Server.Api;
using InternalAssetLibrary.Server.Data;
using InternalAssetLibrary.Server.Security;

internal static class AssetFolderSelfTests
{
    public static void HierarchyPersistenceAndPermissionsRemainConsistent()
    {
        var creator = User("creator");
        var other = User("other");
        var admin = User("admin", isAdmin: true);
        var rootFolder = Folder("Root", creator.Id);
        var child = Folder("Child", creator.Id, rootFolder.Id);
        var grandchild = Folder("Grandchild", creator.Id, child.Id);
        var directAsset = Asset(creator.Id, rootFolder.Id);
        var legacyRootAsset = Asset(creator.Id, null);
        var state = new AppState
        {
            Users = [creator, other, admin],
            AssetFolders = [rootFolder, child, grandchild],
            Assets = [directAsset, legacyRootAsset]
        };

        True(AtomicJsonDataStore.Normalize(state));
        Equal<Guid?>(null, legacyRootAsset.FolderId);
        Equal(3, AssetFolderEndpoints.DescendantIds(state, rootFolder.Id).Count);
        Equal("creator", rootFolder.CreatedByUsername);

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(ApiCommon.ToAsset(directAsset, creator), options));
        Equal(rootFolder.Id, json.RootElement.GetProperty("folderId").GetGuid());

        AssetFolderEndpoints.RequireFolderManager(creator, rootFolder);
        AssetFolderEndpoints.RequireFolderManager(admin, rootFolder);
        Rejects("folder_creator_required", () => AssetFolderEndpoints.RequireFolderManager(other, rootFolder));
        AssetFolderEndpoints.RequireAssetManager(creator, directAsset);
        AssetFolderEndpoints.RequireAssetManager(admin, directAsset);
        Rejects("asset_owner_required", () => AssetFolderEndpoints.RequireAssetManager(other, directAsset));
        Rejects("folder_name_in_use", () =>
            AssetFolderEndpoints.EnsureNameAvailable(state, rootFolder.Id, "child"));
        Rejects("invalid_folder_name", () => AssetFolderEndpoints.ValidateName("bad/name"));

        var cycleState = new AppState
        {
            Users = [creator],
            AssetFolders =
            [
                Folder("One", creator.Id, child.Id, rootFolder.Id),
                Folder("Two", creator.Id, rootFolder.Id, child.Id)
            ]
        };
        Throws<InvalidDataException>(() => AtomicJsonDataStore.Normalize(cycleState));
    }

    public static void FolderDeletionPromotesDirectChildrenWithoutDeletingAssets()
    {
        var creator = User("creator");
        var parent = Folder("Parent", creator.Id);
        var removed = Folder("Removed", creator.Id, parent.Id);
        var child = Folder("Child", creator.Id, removed.Id);
        var grandchild = Folder("Grandchild", creator.Id, child.Id);
        var directAsset = Asset(creator.Id, removed.Id);
        var nestedAsset = Asset(creator.Id, child.Id);
        var state = new AppState
        {
            Users = [creator],
            AssetFolders = [parent, removed, child, grandchild],
            Assets = [directAsset, nestedAsset]
        };
        _ = AtomicJsonDataStore.Normalize(state);
        var now = DateTimeOffset.Parse("2026-08-31T12:00:00+00:00");

        var promoted = AssetFolderEndpoints.DeleteAndPromote(state, removed, now);

        Equal(1, promoted.AssetCount);
        Equal(1, promoted.FolderCount);
        False(state.AssetFolders.Contains(removed));
        Equal<Guid?>(parent.Id, child.ParentId);
        Equal<Guid?>(child.Id, grandchild.ParentId);
        Equal<Guid?>(parent.Id, directAsset.FolderId);
        Equal<Guid?>(child.Id, nestedAsset.FolderId);
        Equal(now, directAsset.UpdatedAt);

        var conflictRemoved = Folder("ConflictContainer", creator.Id, parent.Id);
        var conflictChild = Folder("Existing", creator.Id, conflictRemoved.Id);
        var existing = Folder("Existing", creator.Id, parent.Id);
        state.AssetFolders.AddRange([conflictRemoved, conflictChild, existing]);
        Rejects("folder_name_in_use", () =>
            AssetFolderEndpoints.DeleteAndPromote(state, conflictRemoved, now.AddMinutes(1)));
        True(state.AssetFolders.Contains(conflictRemoved));
        Equal<Guid?>(conflictRemoved.Id, conflictChild.ParentId);
    }

    public static void ServerQuotaDefaultsToOneHundredGiB()
    {
        const long expected = 100L * 1024 * 1024 * 1024;
        Equal(expected, new LibraryOptions().OriginalQuotaBytes);
        var root = RepositoryRoot();
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            root,
            "src",
            "InternalAssetLibrary.Server",
            "appsettings.json")));
        Equal(expected, document.RootElement
            .GetProperty("Library")
            .GetProperty("OriginalQuotaBytes")
            .GetInt64());

        var serverFiles = Directory.EnumerateFiles(
                Path.Combine(root, "src", "InternalAssetLibrary.Server"),
                "*.cs",
                SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                           !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
        foreach (var path in serverFiles)
        {
            var source = File.ReadAllText(path);
            False(source.Contains("53_687_091_200", StringComparison.Ordinal));
        }
    }

    private static UserRecord User(string username, bool isAdmin = false) => new()
    {
        Id = Guid.NewGuid(),
        Username = username,
        NormalizedUsername = username,
        DisplayName = username,
        PasswordHash = "hash",
        IsAdmin = isAdmin,
        Permissions = PermissionNames.MemberDefaults(),
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private static AssetFolderRecord Folder(
        string name,
        Guid creatorId,
        Guid? parentId = null,
        Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        ParentId = parentId,
        Name = name,
        CreatedByUserId = creatorId,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private static AssetRecord Asset(Guid creatorId, Guid? folderId) => new()
    {
        Id = Guid.NewGuid(),
        CurrentVersionId = Guid.NewGuid(),
        Name = "Asset",
        Category = AssetCategories.Video,
        OriginalFileName = "asset.mp4",
        Extension = ".mp4",
        SizeBytes = 1,
        ContentHash = new string('A', 64),
        ObjectKey = $"originals/{Guid.NewGuid():N}.mp4",
        FolderId = folderId,
        UploadedByUserId = creatorId,
        UploadedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private static void Rejects(string code, Action action)
    {
        try
        {
            action();
        }
        catch (ApiException exception) when (exception.Code == code)
        {
            return;
        }

        throw new InvalidOperationException($"Expected API error '{code}'.");
    }

    private static void Throws<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private static void True(bool value)
    {
        if (!value)
        {
            throw new InvalidOperationException("Expected true, but was false.");
        }
    }

    private static void False(bool value) => True(!value);

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', but was '{actual}'.");
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
