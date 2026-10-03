using InternalAssetLibrary.Server.Data;
using InternalAssetLibrary.Server.Realtime;
using InternalAssetLibrary.Server.Security;

namespace InternalAssetLibrary.Server.Api;

internal static class AssetFolderEndpoints
{
    public static void MapAssetFolderEndpoints(this WebApplication app)
    {
        app.MapGet("/api/asset-folders", ListAsync);
        app.MapPost("/api/asset-folders", CreateAsync);
        app.MapPut("/api/asset-folders/{folderId:guid}", RenameAsync);
        app.MapPut("/api/asset-folders/{folderId:guid}/parent", MoveAsync);
        app.MapDelete("/api/asset-folders/{folderId:guid}", DeleteAsync);
        app.MapPut("/api/assets/{assetId:guid}/folder", MoveAssetAsync);
    }

    private static async Task<IResult> ListAsync(HttpContext context, IAppDataStore store)
    {
        var identity = AccessControl.RequirePermission(context, PermissionNames.BrowseAssets);
        var folders = await store.ReadAsync(state =>
        {
            var current = ApiCommon.CurrentUser(state, identity);
            ApiCommon.RequirePermission(current, PermissionNames.BrowseAssets);
            return state.AssetFolders
                .OrderBy(folder => folder.Name, StringComparer.OrdinalIgnoreCase)
                .Select(folder => ApiCommon.ToAssetFolder(
                    folder,
                    state.Users.FirstOrDefault(user => user.Id == folder.CreatedByUserId)))
                .ToList();
        }, context.RequestAborted);
        return Results.Ok(folders);
    }

    private static async Task<IResult> CreateAsync(
        CreateAssetFolderApiRequest request,
        HttpContext context,
        IAppDataStore store,
        ILibraryChangeNotifier changes)
    {
        var identity = AccessControl.RequirePermission(context, PermissionNames.BrowseAssets);
        var name = ValidateName(request.Name);
        var now = DateTimeOffset.UtcNow;
        var result = await store.UpdateAsync(state =>
        {
            var current = ApiCommon.CurrentUser(state, identity);
            ApiCommon.RequirePermission(current, PermissionNames.BrowseAssets);
            _ = RequireExistingFolder(state, request.ParentId);
            EnsureNameAvailable(state, request.ParentId, name);
            var folder = new AssetFolderRecord
            {
                Id = Guid.NewGuid(),
                ParentId = request.ParentId,
                Name = name,
                CreatedByUserId = current.Id,
                CreatedByUsername = current.Username,
                CreatedByDisplayName = current.DisplayName,
                CreatedAt = now,
                UpdatedAt = now
            };
            state.AssetFolders.Add(folder);
            ApiCommon.Audit(
                state,
                current.Id,
                "asset-folder.created",
                "asset-folder",
                folder.Id.ToString(),
                $"name={folder.Name}; parent={FolderValue(folder.ParentId)}");
            return new { Folder = folder, Creator = current };
        }, context.RequestAborted);
        await changes.NotifyAsync(context.RequestAborted, LibraryChangeTarget.Assets(result.Folder.Id));
        return Results.Created(
            $"/api/asset-folders/{result.Folder.Id:D}",
            ApiCommon.ToAssetFolder(result.Folder, result.Creator));
    }

    private static async Task<IResult> RenameAsync(
        Guid folderId,
        RenameAssetFolderApiRequest request,
        HttpContext context,
        IAppDataStore store,
        ILibraryChangeNotifier changes)
    {
        var identity = AccessControl.RequirePermission(context, PermissionNames.BrowseAssets);
        var name = ValidateName(request.Name);
        var result = await store.UpdateAsync(state =>
        {
            var current = ApiCommon.CurrentUser(state, identity);
            ApiCommon.RequirePermission(current, PermissionNames.BrowseAssets);
            var folder = RequireFolder(state, folderId);
            EnsureNameAvailable(state, folder.ParentId, name, folder.Id);
            var priorName = folder.Name;
            folder.Name = name;
            folder.UpdatedAt = DateTimeOffset.UtcNow;
            ApiCommon.Audit(
                state,
                current.Id,
                "asset-folder.renamed",
                "asset-folder",
                folder.Id.ToString(),
                $"from={priorName}; to={folder.Name}");
            return new
            {
                Folder = folder,
                Creator = state.Users.FirstOrDefault(user => user.Id == folder.CreatedByUserId)
            };
        }, context.RequestAborted);
        await changes.NotifyAsync(context.RequestAborted, LibraryChangeTarget.Assets(result.Folder.Id));
        return Results.Ok(ApiCommon.ToAssetFolder(result.Folder, result.Creator));
    }

    private static async Task<IResult> MoveAsync(
        Guid folderId,
        MoveAssetFolderApiRequest request,
        HttpContext context,
        IAppDataStore store,
        ILibraryChangeNotifier changes)
    {
        var identity = AccessControl.RequirePermission(context, PermissionNames.BrowseAssets);
        var result = await store.UpdateAsync(state =>
        {
            var current = ApiCommon.CurrentUser(state, identity);
            ApiCommon.RequirePermission(current, PermissionNames.BrowseAssets);
            var folder = RequireFolder(state, folderId);
            RequireFolderManager(current, folder);
            _ = RequireExistingFolder(state, request.ParentId);
            if (request.ParentId == folder.Id ||
                request.ParentId is { } parentId && DescendantIds(state, folder.Id).Contains(parentId))
            {
                throw new ApiException(
                    StatusCodes.Status409Conflict,
                    "folder_ancestry_cycle",
                    "不能将文件夹移动到自身或其子文件夹中。");
            }

            EnsureNameAvailable(state, request.ParentId, folder.Name, folder.Id);
            var previousParentId = folder.ParentId;
            folder.ParentId = request.ParentId;
            folder.UpdatedAt = DateTimeOffset.UtcNow;
            ApiCommon.Audit(
                state,
                current.Id,
                "asset-folder.moved",
                "asset-folder",
                folder.Id.ToString(),
                $"from={FolderValue(previousParentId)}; to={FolderValue(folder.ParentId)}");
            return new
            {
                Folder = folder,
                Creator = state.Users.FirstOrDefault(user => user.Id == folder.CreatedByUserId)
            };
        }, context.RequestAborted);
        await changes.NotifyAsync(context.RequestAborted, LibraryChangeTarget.Assets(result.Folder.Id));
        return Results.Ok(ApiCommon.ToAssetFolder(result.Folder, result.Creator));
    }

    private static async Task<IResult> DeleteAsync(
        Guid folderId,
        HttpContext context,
        IAppDataStore store,
        ILibraryChangeNotifier changes)
    {
        var identity = AccessControl.RequirePermission(context, PermissionNames.BrowseAssets);
        await store.UpdateAsync(state =>
        {
            var current = ApiCommon.CurrentUser(state, identity);
            ApiCommon.RequirePermission(current, PermissionNames.BrowseAssets);
            var folder = RequireFolder(state, folderId);
            RequireFolderManager(current, folder);
            var now = DateTimeOffset.UtcNow;
            var promoted = DeleteAndPromote(state, folder, now);
            ApiCommon.Audit(
                state,
                current.Id,
                "asset-folder.deleted",
                "asset-folder",
                folder.Id.ToString(),
                $"parent={FolderValue(folder.ParentId)}; promotedAssets={promoted.AssetCount}; promotedFolders={promoted.FolderCount}");
            return true;
        }, context.RequestAborted);
        await changes.NotifyAsync(context.RequestAborted, LibraryChangeTarget.Assets(folderId));
        return Results.NoContent();
    }

    private static async Task<IResult> MoveAssetAsync(
        Guid assetId,
        MoveAssetToFolderApiRequest request,
        HttpContext context,
        IAppDataStore store,
        ILibraryChangeNotifier changes)
    {
        var identity = AccessControl.RequirePermission(context, PermissionNames.BrowseAssets);
        var result = await store.UpdateAsync(state =>
        {
            var current = ApiCommon.CurrentUser(state, identity);
            ApiCommon.RequirePermission(current, PermissionNames.BrowseAssets);
            var asset = state.Assets.FirstOrDefault(item => item.Id == assetId)
                ?? throw new ApiException(StatusCodes.Status404NotFound, "asset_not_found", "素材不存在。");
            RequireAssetManager(current, asset);

            if (!current.IsAdmin)
            {
                ApiCommon.RequirePermission(current, PermissionNames.EditOwnAssets);
            }

            ApiCommon.RequireCategory(current, asset.Category);
            if (asset.State != AssetState.Active)
            {
                throw new ApiException(
                    StatusCodes.Status409Conflict,
                    "asset_recycled",
                    "回收站中的素材不能执行此操作。");
            }

            _ = RequireExistingFolder(state, request.FolderId);
            var previousFolderId = asset.FolderId;
            asset.FolderId = request.FolderId;
            asset.UpdatedAt = DateTimeOffset.UtcNow;
            ApiCommon.Audit(
                state,
                current.Id,
                "asset.folder.moved",
                "asset",
                asset.Id.ToString(),
                $"from={FolderValue(previousFolderId)}; to={FolderValue(asset.FolderId)}");
            return new
            {
                Asset = asset,
                Uploader = state.Users.FirstOrDefault(user => user.Id == asset.UploadedByUserId)
            };
        }, context.RequestAborted);
        await changes.NotifyAsync(context.RequestAborted, LibraryChangeTarget.Assets(result.Asset.Id));
        return Results.Ok(ApiCommon.ToAsset(result.Asset, result.Uploader));
    }

    internal static HashSet<Guid> DescendantIds(AppState state, Guid folderId)
    {
        _ = RequireFolder(state, folderId);
        var result = new HashSet<Guid> { folderId };
        var pending = new Queue<Guid>();
        pending.Enqueue(folderId);
        while (pending.TryDequeue(out var parentId))
        {
            foreach (var child in state.AssetFolders.Where(folder => folder.ParentId == parentId))
            {
                if (result.Add(child.Id))
                {
                    pending.Enqueue(child.Id);
                }
            }
        }

        return result;
    }

    internal static AssetFolderRecord? RequireExistingFolder(AppState state, Guid? folderId) =>
        folderId is null ? null : RequireFolder(state, folderId.Value);

    internal static AssetFolderRecord RequireFolder(AppState state, Guid folderId) =>
        state.AssetFolders.FirstOrDefault(folder => folder.Id == folderId)
        ?? throw new ApiException(StatusCodes.Status404NotFound, "folder_not_found", "素材文件夹不存在。");

    internal static string ValidateName(string? rawName)
    {
        var name = rawName?.Trim() ?? string.Empty;
        if (!AssetFolderNameRules.IsValid(name))
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "invalid_folder_name",
                "文件夹名称须为 1 至 100 个字符，且不能包含控制字符、斜杠或反斜杠。");
        }

        return name;
    }

    internal static void EnsureNameAvailable(
        AppState state,
        Guid? parentId,
        string name,
        params Guid[] excludedFolderIds)
    {
        var excluded = excludedFolderIds.ToHashSet();
        if (state.AssetFolders.Any(folder =>
                folder.ParentId == parentId &&
                !excluded.Contains(folder.Id) &&
                folder.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ApiException(
                StatusCodes.Status409Conflict,
                "folder_name_in_use",
                "同一层级中已存在同名文件夹。");
        }
    }

    internal static (int AssetCount, int FolderCount) DeleteAndPromote(
        AppState state,
        AssetFolderRecord folder,
        DateTimeOffset now)
    {
        var children = state.AssetFolders.Where(item => item.ParentId == folder.Id).ToList();
        foreach (var child in children)
        {
            EnsureNameAvailable(state, folder.ParentId, child.Name, folder.Id, child.Id);
        }

        var assets = state.Assets.Where(asset => asset.FolderId == folder.Id).ToList();
        foreach (var asset in assets)
        {
            asset.FolderId = folder.ParentId;
            asset.UpdatedAt = now;
        }

        foreach (var child in children)
        {
            child.ParentId = folder.ParentId;
            child.UpdatedAt = now;
        }

        state.AssetFolders.Remove(folder);
        return (assets.Count, children.Count);
    }

    internal static void RequireFolderManager(UserRecord current, AssetFolderRecord folder)
    {
        if (!current.IsAdmin && folder.CreatedByUserId != current.Id)
        {
            throw new ApiException(
                StatusCodes.Status403Forbidden,
                "folder_creator_required",
                "只有文件夹创建者或管理员可以移动或删除此文件夹。");
        }
    }

    internal static void RequireAssetManager(UserRecord current, AssetRecord asset)
    {
        if (!current.IsAdmin && asset.UploadedByUserId != current.Id)
        {
            throw new ApiException(
                StatusCodes.Status403Forbidden,
                "asset_owner_required",
                "只有上传者或管理员可以移动此素材。");
        }
    }

    private static string FolderValue(Guid? folderId) => folderId?.ToString("D") ?? "root";
}
