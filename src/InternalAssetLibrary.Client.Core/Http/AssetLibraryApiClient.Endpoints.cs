using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using InternalAssetLibrary.Contracts;

namespace InternalAssetLibrary.Client.Core.Http;

public sealed partial class AssetLibraryApiClient
{
    private const long MaximumClientReleaseManifestBytes = 256 * 1024;

    public Task<DirectTransferCapabilities> GetDirectTransferCapabilitiesAsync(
        CancellationToken cancellationToken = default) =>
        GetAsync<DirectTransferCapabilities>("api/transfers/capabilities", cancellationToken);

    public Task<DirectUploadSession> StartAssetDirectUploadAsync(
        Guid assetId,
        CancellationToken cancellationToken = default) =>
        SendAsync<DirectUploadSession>(
            HttpMethod.Post,
            $"api/assets/{assetId:D}/direct-upload",
            cancellationToken: cancellationToken);

    public Task<DirectUploadSession> GetDirectUploadSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default) =>
        GetAsync<DirectUploadSession>($"api/direct-uploads/{sessionId:D}", cancellationToken);

    public Task<DirectUploadPartUrl> GetDirectUploadPartUrlAsync(
        Guid sessionId,
        int partNumber,
        CancellationToken cancellationToken = default) =>
        GetAsync<DirectUploadPartUrl>(
            $"api/direct-uploads/{sessionId:D}/parts/{partNumber}",
            cancellationToken);

    public Task<ApiAsset> CompleteDirectUploadAsync(
        Guid sessionId,
        CompleteDirectUploadRequest request,
        CancellationToken cancellationToken = default) =>
        PostAsync<CompleteDirectUploadRequest, ApiAsset>(
            $"api/direct-uploads/{sessionId:D}/complete",
            request,
            cancellationToken);

    public Task AbortDirectUploadAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default) =>
        DeleteAsync($"api/direct-uploads/{sessionId:D}", cancellationToken);

    public async Task<ClientReleaseInfo?> GetLatestClientReleaseAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await SendCoreAsync(
            HttpMethod.Get,
            "api/client/releases/latest",
            content: null,
            "application/json",
            cancellationToken).ConfigureAwait(false);
        await ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NoContent || response.Content.Headers.ContentLength == 0)
        {
            return null;
        }

        return await ReadJsonAsync<ClientReleaseInfo>(
            response,
            cancellationToken,
            MaximumClientReleaseManifestBytes).ConfigureAwait(false);
    }

    public Task DownloadLatestClientInstallerAsync(
        ClientReleaseInfo release,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        var relativePath = release.DownloadPath.TrimStart('/');
        if (!relativePath.Equals("api/client/releases/latest/download", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The server returned an unsupported client update path.");
        }

        return CopyResponseToAsync(
            relativePath,
            destination,
            "application/vnd.microsoft.portable-executable",
            cancellationToken,
            release.InstallerSizeBytes);
    }

    public Task DownloadLatestClientInstallerRangeAsync(ClientReleaseInfo release, long offset, Stream destination,
        CancellationToken cancellationToken = default)
    {
        if (offset < 0 || offset >= release.InstallerSizeBytes) throw new ArgumentOutOfRangeException(nameof(offset));
        if (release.DownloadPath != "/api/client/releases/latest/download") throw new InvalidDataException("Unsupported update download path.");
        return CopyResponseToAsync(release.DownloadPath.TrimStart('/'), destination,
            "application/vnd.microsoft.portable-executable", cancellationToken, release.InstallerSizeBytes - offset, offset);
    }

    public Task<ApiLoginResponse> LoginAsync(
        ApiLoginRequest request,
        CancellationToken cancellationToken = default) =>
        PostAsync<ApiLoginRequest, ApiLoginResponse>("api/auth/login", request, cancellationToken);

    public Task LogoutAsync(CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "api/auth/logout", cancellationToken: cancellationToken);

    public Task<ApiCurrentUser> ChangePasswordAsync(
        ApiChangePasswordRequest request,
        CancellationToken cancellationToken = default) =>
        PostAsync<ApiChangePasswordRequest, ApiCurrentUser>("api/auth/change-password", request, cancellationToken);

    public Task<ApiCurrentUser> GetCurrentUserAsync(CancellationToken cancellationToken = default) =>
        GetAsync<ApiCurrentUser>("api/me", cancellationToken);

    public Task<IReadOnlyList<ApiAdminUser>> ListAdminUsersAsync(
        CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<ApiAdminUser>>("api/admin/users", cancellationToken);

    public Task<ApiAdminUser> CreateAdminUserAsync(
        ApiCreateAdminUserRequest request,
        CancellationToken cancellationToken = default) =>
        PostAsync<ApiCreateAdminUserRequest, ApiAdminUser>("api/admin/users", request, cancellationToken);

    public Task<ApiAdminUser> SetAdminUserStatusAsync(
        Guid userId,
        ApiAdminAccountStatusRequest request,
        CancellationToken cancellationToken = default) =>
        PutAsync<ApiAdminAccountStatusRequest, ApiAdminUser>(
            $"api/admin/users/{userId:D}/status",
            request,
            cancellationToken);

    public Task<ApiAdminUser> SetAdminUserPermissionsAsync(
        Guid userId,
        ApiAdminPermissionsRequest request,
        CancellationToken cancellationToken = default) =>
        PutAsync<ApiAdminPermissionsRequest, ApiAdminUser>(
            $"api/admin/users/{userId:D}/permissions",
            request,
            cancellationToken);

    public Task<ApiAdminUser> SetAdminUsernameAsync(
        Guid userId,
        ApiAdminUsernameRequest request,
        CancellationToken cancellationToken = default) =>
        PutAsync<ApiAdminUsernameRequest, ApiAdminUser>(
            $"api/admin/users/{userId:D}/username",
            request,
            cancellationToken);

    public Task ResetAdminUserPasswordAsync(
        Guid userId,
        ApiAdminResetPasswordRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync(
            HttpMethod.Post,
            $"api/admin/users/{userId:D}/reset-password",
            request,
            cancellationToken);

    public Task DeleteAdminUserAsync(Guid userId, CancellationToken cancellationToken = default) =>
        DeleteAsync($"api/admin/users/{userId:D}", cancellationToken);

    public Task<IReadOnlyList<ApiAuditRecord>> ListAdminAuditAsync(
        int limit = 200,
        CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<ApiAuditRecord>>(
            BuildUri("api/admin/audit", ("limit", Invariant(Math.Clamp(limit, 1, 500)))),
            cancellationToken);

    public Task<ApiServerSettings> GetAdminServerSettingsAsync(
        CancellationToken cancellationToken = default) =>
        GetAsync<ApiServerSettings>("api/admin/settings", cancellationToken);

    public Task<ApiServerSettings> UpdateAdminServerSettingsAsync(
        ApiServerSettings request,
        CancellationToken cancellationToken = default) =>
        PutAsync<ApiServerSettings, ApiServerSettings>("api/admin/settings", request, cancellationToken);

    public Task<ApiCurrentUser> UpdateCurrentProfileAsync(
        ApiUpdateProfileRequest request,
        CancellationToken cancellationToken = default) =>
        PutAsync<ApiUpdateProfileRequest, ApiCurrentUser>("api/me/profile", request, cancellationToken);

    public Task<ApiCurrentUser> BindEmailAsync(
        ApiBindEmailRequest request,
        CancellationToken cancellationToken = default) =>
        PutAsync<ApiBindEmailRequest, ApiCurrentUser>("api/me/email", request, cancellationToken);

    public Task<ApiCurrentUser> UnbindEmailAsync(
        ApiPasswordConfirmationRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync<ApiCurrentUser>(HttpMethod.Delete, "api/me/email", request, cancellationToken);

    public Task<ApiCurrentUser> UploadAvatarAsync(
        Stream webp,
        long sizeBytes,
        CancellationToken cancellationToken = default) =>
        SendStreamForJsonAsync<ApiCurrentUser>(
            HttpMethod.Post,
            "api/me/avatar",
            webp,
            sizeBytes,
            "image/webp",
            cancellationToken);

    public Task<ApiCurrentUser> DeleteAvatarAsync(CancellationToken cancellationToken = default) =>
        SendAsync<ApiCurrentUser>(HttpMethod.Delete, "api/me/avatar", cancellationToken: cancellationToken);

    public Task<ApiPage<ApiPublicUserProfile>> ListUsersAsync(
        ApiUserListQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        query ??= new ApiUserListQuery();
        return GetAsync<ApiPage<ApiPublicUserProfile>>(BuildUri(
            "api/users",
            ("search", Optional(query.Search)),
            ("page", Invariant(query.Page)),
            ("pageSize", Invariant(query.PageSize))), cancellationToken);
    }

    public Task<ApiPublicUserProfile> GetUserAsync(
        Guid userId,
        CancellationToken cancellationToken = default) =>
        GetAsync<ApiPublicUserProfile>($"api/users/{userId:D}", cancellationToken);

    public Task DownloadUserAvatarAsync(
        Guid userId,
        Stream destination,
        CancellationToken cancellationToken = default) =>
        CopyResponseToAsync($"api/users/{userId:D}/avatar", destination, "image/*", cancellationToken);

    public Task<ApiPage<ApiAsset>> ListAssetsAsync(
        ApiAssetListQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        query ??= new ApiAssetListQuery();
        return GetAsync<ApiPage<ApiAsset>>(BuildUri(
            "api/assets",
            ("search", Optional(query.Search)),
            ("category", query.Category is null ? null : CategoryValue(query.Category.Value)),
            ("tag", Optional(query.Tag)),
            ("uploaderId", query.UploaderId?.ToString("D")),
            ("state", query.State == ApiAssetState.Recycled ? "recycled" : null),
            ("page", Invariant(query.Page)),
            ("pageSize", Invariant(query.PageSize)),
            ("sort", SortValue(query.Sort)),
            ("order", query.Order == ApiSortOrder.Ascending ? "asc" : "desc"),
            ("uploadedFrom", query.UploadedFrom?.ToString("O", CultureInfo.InvariantCulture)),
            ("uploadedTo", query.UploadedTo?.ToString("O", CultureInfo.InvariantCulture)),
            ("folderId", query.FolderId?.ToString("D")),
            ("includeDescendants", query.FolderId is null
                ? null
                : query.IncludeDescendantFolders ? "true" : "false"),
            ("rootOnly", query.RootOnly ? "true" : null)), cancellationToken);
    }

    public Task<ApiPage<ApiAsset>> ListMyRecycleBinAsync(
        ApiRecycleBinListQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        query ??= new ApiRecycleBinListQuery();
        return GetAsync<ApiPage<ApiAsset>>(BuildUri(
            "api/me/recycle-bin",
            ("search", Optional(query.Search)),
            ("category", query.Category is null ? null : CategoryValue(query.Category.Value)),
            ("tag", Optional(query.Tag)),
            ("page", Invariant(query.Page)),
            ("pageSize", Invariant(query.PageSize)),
            ("sort", SortValue(query.Sort)),
            ("order", query.Order == ApiSortOrder.Ascending ? "asc" : "desc"),
            ("uploadedFrom", query.UploadedFrom?.ToString("O", CultureInfo.InvariantCulture)),
            ("uploadedTo", query.UploadedTo?.ToString("O", CultureInfo.InvariantCulture))), cancellationToken);
    }

    public async Task<IReadOnlyList<ApiAsset>> ListAllMyRecycleBinAsync(
        ApiRecycleBinListQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        query ??= new ApiRecycleBinListQuery();
        const int pageSize = 100;
        var assets = new List<ApiAsset>();
        for (var pageNumber = 1; ; pageNumber++)
        {
            var page = await ListMyRecycleBinAsync(query with
            {
                Page = pageNumber,
                PageSize = pageSize
            }, cancellationToken);
            assets.AddRange(page.Items);
            if (page.Items.Count == 0 || assets.Count >= page.Total)
            {
                return assets;
            }
        }
    }

    public Task<ApiAsset> GetAssetAsync(Guid assetId, CancellationToken cancellationToken = default) =>
        GetAsync<ApiAsset>($"api/assets/{assetId:D}", cancellationToken);

    public Task<ApiAsset> CreateAssetAsync(
        ApiCreateAssetRequest request,
        CancellationToken cancellationToken = default) =>
        PostAsync<ApiCreateAssetRequest, ApiAsset>("api/assets", request, cancellationToken);

    public Task<ApiAsset> UpdateAssetAsync(
        Guid assetId,
        ApiUpdateAssetRequest request,
        CancellationToken cancellationToken = default) =>
        PutAsync<ApiUpdateAssetRequest, ApiAsset>($"api/assets/{assetId:D}", request, cancellationToken);

    public Task<ApiAsset> UpdateAssetCategoryAsync(
        Guid assetId,
        ApiUpdateAssetCategoryRequest request,
        CancellationToken cancellationToken = default) =>
        PutAsync<ApiUpdateAssetCategoryRequest, ApiAsset>(
            $"api/assets/{assetId:D}/category",
            request,
            cancellationToken);

    public Task<ApiAsset> UpdateAssetTagsAsync(
        Guid assetId,
        ApiUpdateAssetTagsRequest request,
        CancellationToken cancellationToken = default) =>
        PutAsync<ApiUpdateAssetTagsRequest, ApiAsset>($"api/assets/{assetId:D}/tags", request, cancellationToken);

    public Task<IReadOnlyList<ApiAssetFolder>> ListAssetFoldersAsync(
        CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<ApiAssetFolder>>("api/asset-folders", cancellationToken);

    public Task<ApiAssetFolder> CreateAssetFolderAsync(
        ApiCreateAssetFolderRequest request,
        CancellationToken cancellationToken = default) =>
        PostAsync<ApiCreateAssetFolderRequest, ApiAssetFolder>("api/asset-folders", request, cancellationToken);

    public Task<ApiAssetFolder> RenameAssetFolderAsync(
        Guid folderId,
        ApiRenameAssetFolderRequest request,
        CancellationToken cancellationToken = default) =>
        PutAsync<ApiRenameAssetFolderRequest, ApiAssetFolder>(
            $"api/asset-folders/{folderId:D}",
            request,
            cancellationToken);

    public Task<ApiAssetFolder> MoveAssetFolderAsync(
        Guid folderId,
        ApiMoveAssetFolderRequest request,
        CancellationToken cancellationToken = default) =>
        PutAsync<ApiMoveAssetFolderRequest, ApiAssetFolder>(
            $"api/asset-folders/{folderId:D}/parent",
            request,
            cancellationToken);

    public Task DeleteAssetFolderAsync(
        Guid folderId,
        CancellationToken cancellationToken = default) =>
        DeleteAsync($"api/asset-folders/{folderId:D}", cancellationToken);

    public Task<ApiAsset> MoveAssetToFolderAsync(
        Guid assetId,
        ApiMoveAssetToFolderRequest request,
        CancellationToken cancellationToken = default) =>
        PutAsync<ApiMoveAssetToFolderRequest, ApiAsset>(
            $"api/assets/{assetId:D}/folder",
            request,
            cancellationToken);

    public Task<ApiAsset> RecycleAssetAsync(Guid assetId, CancellationToken cancellationToken = default) =>
        SendAsync<ApiAsset>(HttpMethod.Delete, $"api/assets/{assetId:D}", cancellationToken: cancellationToken);

    public Task<ApiAsset> RestoreAssetAsync(Guid assetId, CancellationToken cancellationToken = default) =>
        SendAsync<ApiAsset>(HttpMethod.Post, $"api/assets/{assetId:D}/restore", cancellationToken: cancellationToken);

    public Task PermanentlyDeleteAssetAsync(Guid assetId, CancellationToken cancellationToken = default) =>
        DeleteAsync($"api/assets/{assetId:D}/permanent", cancellationToken);

    public Task<ApiAsset> RestoreMyRecycleBinAssetAsync(
        Guid assetId,
        CancellationToken cancellationToken = default) =>
        SendAsync<ApiAsset>(
            HttpMethod.Post,
            $"api/me/recycle-bin/{assetId:D}/restore",
            cancellationToken: cancellationToken);

    public Task PermanentlyDeleteMyRecycleBinAssetAsync(
        Guid assetId,
        CancellationToken cancellationToken = default) =>
        DeleteAsync($"api/me/recycle-bin/{assetId:D}", cancellationToken);

    public Task<RecycleBinClearResult> ClearMyRecycleBinAsync(
        CancellationToken cancellationToken = default) =>
        SendAsync<RecycleBinClearResult>(
            HttpMethod.Delete,
            "api/me/recycle-bin",
            cancellationToken: cancellationToken);

    public Task<IReadOnlyList<ApiTag>> ListTagsAsync(CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<ApiTag>>("api/tags", cancellationToken);

    public Task<ApiTag> CreateTagAsync(ApiTagNameRequest request, CancellationToken cancellationToken = default) =>
        PostAsync<ApiTagNameRequest, ApiTag>("api/tags", request, cancellationToken);

    public Task<ApiTag> RenameTagAsync(
        Guid tagId,
        ApiTagNameRequest request,
        CancellationToken cancellationToken = default) =>
        PutAsync<ApiTagNameRequest, ApiTag>($"api/tags/{tagId:D}", request, cancellationToken);

    public Task DeleteTagAsync(Guid tagId, CancellationToken cancellationToken = default) =>
        DeleteAsync($"api/tags/{tagId:D}", cancellationToken);

    public Task<ApiAsset> UploadAssetContentAsync(
        Guid assetId,
        Stream source,
        long sizeBytes,
        CancellationToken cancellationToken = default) =>
        SendStreamForJsonAsync<ApiAsset>(
            HttpMethod.Put,
            $"api/assets/{assetId:D}/content",
            source,
            sizeBytes,
            "application/octet-stream",
            cancellationToken);

    public Task<ApiAsset> UploadAssetReplacementAsync(
        Guid assetId,
        string fileName,
        long sizeBytes,
        string sha256,
        Stream source,
        CancellationToken cancellationToken = default) =>
        SendStreamForJsonAsync<ApiAsset>(
            HttpMethod.Put,
            BuildUri(
                $"api/assets/{assetId:D}/replacement",
                ("fileName", fileName),
                ("sizeBytes", Invariant(sizeBytes)),
                ("sha256", sha256)),
            source,
            sizeBytes,
            "application/octet-stream",
            cancellationToken);

    public Task DownloadAssetAsync(
        Guid assetId,
        Stream destination,
        CancellationToken cancellationToken = default) =>
        CopyResponseToAsync($"api/assets/{assetId:D}/download", destination, "application/octet-stream", cancellationToken);

    public Task DownloadAssetVersionAsync(
        Guid assetId,
        Guid versionId,
        Stream destination,
        CancellationToken cancellationToken = default) =>
        CopyResponseToAsync(
            $"api/assets/{assetId:D}/versions/{versionId:D}/download",
            destination,
            "application/octet-stream",
            cancellationToken);

    public async Task DownloadAssetVersionRangeAsync(
        Guid assetId,
        Guid versionId,
        long offset,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("The destination stream must be writable.", nameof(destination));
        }

        using var response = await SendCoreAsync(
            HttpMethod.Get,
            $"api/assets/{assetId:D}/versions/{versionId:D}/download",
            content: null,
            "application/octet-stream",
            cancellationToken,
            offset == 0 ? null : new RangeHeaderValue(offset, null)).ConfigureAwait(false);
        await ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        if (offset > 0 &&
            (response.StatusCode != HttpStatusCode.PartialContent || response.Content.Headers.ContentRange?.From != offset))
        {
            throw new InvalidDataException("The download source did not honor the requested resume offset.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
    }

    public Task CopyAssetPreviewAsync(
        Guid assetId,
        Stream destination,
        CancellationToken cancellationToken = default) =>
        CopyResponseToAsync($"api/assets/{assetId:D}/content", destination, "*/*", cancellationToken);

    public Task CopyAssetPreviewAsync(
        Guid assetId,
        Stream destination,
        long maximumBytes,
        CancellationToken cancellationToken = default)
    {
        if (maximumBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        return CopyResponseToAsync(
            $"api/assets/{assetId:D}/content",
            destination,
            "*/*",
            cancellationToken,
            maximumBytes);
    }

    public Task<AssetDerivativesInfo> GetAssetDerivativesAsync(
        Guid assetId,
        CancellationToken cancellationToken = default) =>
        GetAsync<AssetDerivativesInfo>($"api/assets/{assetId:D}/derivatives", cancellationToken);

    public Task<AssetDerivativesInfo> UploadAssetThumbnailAsync(
        Guid assetId,
        Guid assetVersionId,
        string sha256,
        Stream source,
        long sizeBytes,
        CancellationToken cancellationToken = default) =>
        UploadAssetDerivativeAsync(
            assetId,
            AssetDerivativeKind.Thumbnail,
            assetVersionId,
            sha256,
            source,
            sizeBytes,
            "image/webp",
            cancellationToken);

    public Task<AssetDerivativesInfo> UploadAssetProxyAsync(
        Guid assetId,
        Guid assetVersionId,
        string sha256,
        Stream source,
        long sizeBytes,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        if (contentType is not ("video/mp4" or "audio/mp4"))
        {
            throw new ArgumentException("Proxy content type must be video/mp4 or audio/mp4.", nameof(contentType));
        }

        return UploadAssetDerivativeAsync(
            assetId,
            AssetDerivativeKind.Proxy,
            assetVersionId,
            sha256,
            source,
            sizeBytes,
            contentType,
            cancellationToken);
    }

    public Task<AssetDerivativesInfo> UpdateAssetDerivativeStatusAsync(
        Guid assetId,
        AssetDerivativeKind kind,
        UpdateAssetDerivativeStatusRequest request,
        CancellationToken cancellationToken = default) =>
        PutAsync<UpdateAssetDerivativeStatusRequest, AssetDerivativesInfo>(
            $"api/assets/{assetId:D}/derivatives/{DerivativeKindValue(kind)}/status",
            request,
            cancellationToken);

    public Task<AssetDerivativesInfo> RetryAssetDerivativeAsync(
        Guid assetId,
        AssetDerivativeKind kind,
        RetryAssetDerivativeRequest request,
        CancellationToken cancellationToken = default) =>
        PostAsync<RetryAssetDerivativeRequest, AssetDerivativesInfo>(
            $"api/assets/{assetId:D}/derivatives/{DerivativeKindValue(kind)}/retry",
            request,
            cancellationToken);

    public Task<ApiObjectDownloadResult> CopyAssetDerivativeAsync(
        Guid assetId,
        AssetDerivativeKind kind,
        Stream destination,
        CancellationToken cancellationToken = default) =>
        CopyResponseWithMetadataAsync(
            $"api/assets/{assetId:D}/derivatives/{DerivativeKindValue(kind)}",
            destination,
            "*/*",
            cancellationToken);

    public Task<ApiPage<TeamLutSummary>> ListTeamLutsAsync(
        ApiTeamLutListQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        query ??= new ApiTeamLutListQuery();
        return GetAsync<ApiPage<TeamLutSummary>>(BuildUri(
            "api/luts",
            ("search", Optional(query.Search)),
            ("state", query.State == TeamLutState.Recycled ? "recycled" : null),
            ("page", Invariant(query.Page)),
            ("pageSize", Invariant(query.PageSize)),
            ("uploaderId", query.UploaderId?.ToString("D"))), cancellationToken);
    }

    public Task<TeamLutSummary> GetTeamLutAsync(
        Guid lutId,
        CancellationToken cancellationToken = default) =>
        GetAsync<TeamLutSummary>($"api/luts/{lutId:D}", cancellationToken);

    public Task<TeamLutSummary> CreateTeamLutAsync(
        CreateTeamLutRequest request,
        CancellationToken cancellationToken = default) =>
        PostAsync<CreateTeamLutRequest, TeamLutSummary>("api/luts", request, cancellationToken);

    public Task<TeamLutSummary> UploadTeamLutContentAsync(
        Guid lutId,
        Stream source,
        long sizeBytes,
        CancellationToken cancellationToken = default) =>
        SendStreamForJsonAsync<TeamLutSummary>(
            HttpMethod.Put,
            $"api/luts/{lutId:D}/content",
            source,
            sizeBytes,
            "application/x-cube",
            cancellationToken);

    public Task<TeamLutSummary> UpdateTeamLutAsync(
        Guid lutId,
        UpdateTeamLutRequest request,
        CancellationToken cancellationToken = default) =>
        PutAsync<UpdateTeamLutRequest, TeamLutSummary>($"api/luts/{lutId:D}", request, cancellationToken);

    public Task<TeamLutSummary> UploadTeamLutReplacementAsync(
        Guid lutId,
        string fileName,
        long sizeBytes,
        string sha256,
        Stream source,
        CancellationToken cancellationToken = default) =>
        SendStreamForJsonAsync<TeamLutSummary>(
            HttpMethod.Put,
            BuildUri(
                $"api/luts/{lutId:D}/replacement",
                ("fileName", fileName),
                ("sizeBytes", Invariant(sizeBytes)),
                ("sha256", sha256)),
            source,
            sizeBytes,
            "application/x-cube",
            cancellationToken);

    public Task<ApiObjectDownloadResult> DownloadTeamLutAsync(
        Guid lutId,
        Stream destination,
        CancellationToken cancellationToken = default) =>
        CopyResponseWithMetadataAsync(
            $"api/luts/{lutId:D}/download",
            destination,
            "application/x-cube",
            cancellationToken);

    public Task DownloadTeamLutRangeAsync(Guid lutId, long offset, long sizeBytes, Stream destination,
        CancellationToken cancellationToken = default) =>
        CopyResponseToAsync($"api/luts/{lutId:D}/download", destination, "application/x-cube", cancellationToken,
            maximumBytes: sizeBytes - offset, offset: offset);

    public Task<TeamLutSummary> RecycleTeamLutAsync(
        Guid lutId,
        CancellationToken cancellationToken = default) =>
        SendAsync<TeamLutSummary>(HttpMethod.Delete, $"api/luts/{lutId:D}", cancellationToken: cancellationToken);

    public Task<TeamLutSummary> RestoreTeamLutAsync(
        Guid lutId,
        CancellationToken cancellationToken = default) =>
        SendAsync<TeamLutSummary>(HttpMethod.Post, $"api/luts/{lutId:D}/restore", cancellationToken: cancellationToken);

    public Task PermanentlyDeleteTeamLutAsync(
        Guid lutId,
        CancellationToken cancellationToken = default) =>
        DeleteAsync($"api/luts/{lutId:D}/permanent", cancellationToken);

    private Task<AssetDerivativesInfo> UploadAssetDerivativeAsync(
        Guid assetId,
        AssetDerivativeKind kind,
        Guid assetVersionId,
        string sha256,
        Stream source,
        long sizeBytes,
        string contentType,
        CancellationToken cancellationToken) =>
        SendStreamForJsonAsync<AssetDerivativesInfo>(
            HttpMethod.Put,
            BuildUri(
                $"api/assets/{assetId:D}/derivatives/{DerivativeKindValue(kind)}",
                ("assetVersionId", assetVersionId.ToString("D")),
                ("sizeBytes", Invariant(sizeBytes)),
                ("sha256", sha256)),
            source,
            sizeBytes,
            contentType,
            cancellationToken);

    public Task<IReadOnlyList<MarkerSetSummary>> ListMarkerSetsAsync(
        Guid assetId,
        CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<MarkerSetSummary>>($"api/assets/{assetId:D}/marker-sets", cancellationToken);

    public Task<MarkerSetDetail> GetMarkerSetAsync(
        Guid markerSetId,
        CancellationToken cancellationToken = default) =>
        GetAsync<MarkerSetDetail>($"api/marker-sets/{markerSetId:D}", cancellationToken);

    public Task<MarkerSetAccess> GetMarkerSetAccessAsync(
        Guid markerSetId,
        CancellationToken cancellationToken = default) =>
        GetAsync<MarkerSetAccess>($"api/marker-sets/{markerSetId:D}/access", cancellationToken);

    public Task<MarkerSetDetail> CreateMarkerSetAsync(
        CreateMarkerSetRequest request,
        CancellationToken cancellationToken = default) =>
        PostAsync<CreateMarkerSetRequest, MarkerSetDetail>("api/marker-sets", request, cancellationToken);

    public Task<MarkerSetDetail> CopyMarkerSetAsync(
        CopyMarkerSetRequest request,
        CancellationToken cancellationToken = default) =>
        PostAsync<CopyMarkerSetRequest, MarkerSetDetail>("api/marker-sets/copy", request, cancellationToken);

    public Task<MarkerSetDetail> RenameMarkerSetAsync(
        Guid markerSetId,
        RenameMarkerSetRequest request,
        CancellationToken cancellationToken = default) =>
        PutAsync<RenameMarkerSetRequest, MarkerSetDetail>($"api/marker-sets/{markerSetId:D}", request, cancellationToken);

    public Task DeleteMarkerSetAsync(Guid markerSetId, CancellationToken cancellationToken = default) =>
        DeleteAsync($"api/marker-sets/{markerSetId:D}", cancellationToken);

    public Task<MarkerItem> AddMarkerAsync(
        Guid markerSetId,
        UpsertMarkerRequest request,
        CancellationToken cancellationToken = default) =>
        PostAsync<UpsertMarkerRequest, MarkerItem>($"api/marker-sets/{markerSetId:D}/markers", request, cancellationToken);

    public Task<MarkerItem> UpdateMarkerAsync(
        Guid markerSetId,
        Guid markerId,
        UpsertMarkerRequest request,
        CancellationToken cancellationToken = default) =>
        PutAsync<UpsertMarkerRequest, MarkerItem>(
            $"api/marker-sets/{markerSetId:D}/markers/{markerId:D}",
            request,
            cancellationToken);

    public Task DeleteMarkerAsync(
        Guid markerSetId,
        Guid markerId,
        CancellationToken cancellationToken = default) =>
        DeleteAsync($"api/marker-sets/{markerSetId:D}/markers/{markerId:D}", cancellationToken);

    public Task<MarkerSetDetail> ImportMarkerCsvAsync(
        ImportMarkerSetRequest request,
        Stream csv,
        long? sizeBytes = null,
        CancellationToken cancellationToken = default) =>
        SendStreamForJsonAsync<MarkerSetDetail>(
            HttpMethod.Post,
            BuildUri(
                "api/marker-sets/import",
                ("assetId", request.AssetId.ToString("D")),
                ("assetVersionId", request.AssetVersionId.ToString("D")),
                ("name", request.Name)),
            csv,
            sizeBytes,
            "text/csv",
            cancellationToken);

    public Task ExportMarkerCsvAsync(
        Guid markerSetId,
        Stream destination,
        MarkerCsvRecordingInfo? recording = null,
        CancellationToken cancellationToken = default)
    {
        string? durationSeconds = null;
        if (recording?.RecordingDuration is { } duration)
        {
            if (duration < TimeSpan.Zero || duration.Ticks % TimeSpan.TicksPerSecond != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(recording), "Recording duration must be a non-negative whole number of seconds.");
            }

            durationSeconds = (duration.Ticks / TimeSpan.TicksPerSecond).ToString(CultureInfo.InvariantCulture);
        }

        return CopyResponseToAsync(BuildUri(
            $"api/marker-sets/{markerSetId:D}/csv",
            ("recordingName", NonEmpty(recording?.RecordingName)),
            ("recordingPath", NonEmpty(recording?.RecordingPath)),
            ("recordingStartedAt", recording?.RecordingStartedAt?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
            ("recordingDurationSeconds", durationSeconds)), destination, "text/csv", cancellationToken);
    }

    private async Task<TResponse> SendStreamForJsonAsync<TResponse>(
        HttpMethod method,
        string relativeUri,
        Stream source,
        long? sizeBytes,
        string contentType,
        CancellationToken cancellationToken)
    {
        ValidateReadableStream(source, nameof(source));
        if (sizeBytes is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeBytes));
        }

        using var content = new NonDisposingStreamContent(source, sizeBytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        using var response = await SendCoreAsync(
            method,
            relativeUri,
            content,
            "application/json",
            cancellationToken).ConfigureAwait(false);
        await ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync<TResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task CopyResponseToAsync(
        string relativeUri,
        Stream destination,
        string accept,
        CancellationToken cancellationToken,
        long? maximumBytes = null, long offset = 0)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("The destination stream must be writable.", nameof(destination));
        }

        using var response = await SendCoreAsync(
            HttpMethod.Get,
            relativeUri,
            content: null,
            accept,
            cancellationToken, offset == 0 ? null : new RangeHeaderValue(offset, null)).ConfigureAwait(false);
        await ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        if (offset > 0 && (response.StatusCode != HttpStatusCode.PartialContent || response.Content.Headers.ContentRange?.From != offset))
            throw new DownloadResumeNotSupportedException();
        if (maximumBytes is { } limit && response.Content.Headers.ContentLength > limit)
        {
            throw new InvalidDataException($"The response exceeds the {limit} byte limit.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        if (maximumBytes is not { } maximum)
        {
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            return;
        }

        var buffer = new byte[81_920];
        long total = 0;
        while (true)
        {
            var count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            total = checked(total + count);
            if (total > maximum)
            {
                throw new InvalidDataException($"The response exceeds the {maximum} byte limit.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<ApiObjectDownloadResult> CopyResponseWithMetadataAsync(
        string relativeUri,
        Stream destination,
        string accept,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("The destination stream must be writable.", nameof(destination));
        }

        using var response = await SendCoreAsync(
            HttpMethod.Get,
            relativeUri,
            content: null,
            accept,
            cancellationToken).ConfigureAwait(false);
        await ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        return new ApiObjectDownloadResult(
            response.Headers.ETag?.Tag.Trim('"'),
            response.Content.Headers.ContentLength,
            response.Content.Headers.ContentType?.MediaType,
            response.Content.Headers.LastModified);
    }

    private async Task<TResponse> ReadJsonAsync<TResponse>(
        HttpResponseMessage response,
        CancellationToken cancellationToken,
        long? maximumBytes = null)
    {
        if (response.StatusCode == HttpStatusCode.NoContent || response.Content.Headers.ContentLength == 0)
        {
            throw new InvalidDataException("The API returned no JSON response body.");
        }

        if (maximumBytes is { } limit && response.Content.Headers.ContentLength > limit)
        {
            throw new InvalidDataException($"The response exceeds the {limit} byte limit.");
        }

        if (maximumBytes is { } boundedLimit)
        {
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (bytes.LongLength > boundedLimit)
            {
                throw new InvalidDataException($"The response exceeds the {boundedLimit} byte limit.");
            }

            var valueFromBytes = JsonSerializer.Deserialize<TResponse>(bytes, _jsonOptions);
            return valueFromBytes ?? throw new InvalidDataException("The API returned a JSON null response.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var value = await JsonSerializer.DeserializeAsync<TResponse>(stream, _jsonOptions, cancellationToken)
            .ConfigureAwait(false);

        return value ?? throw new InvalidDataException("The API returned a JSON null response.");
    }

    private static string BuildUri(string path, params (string Name, string? Value)[] values)
    {
        var builder = new StringBuilder(path);
        var separator = '?';
        foreach (var (name, value) in values)
        {
            if (value is null)
            {
                continue;
            }

            builder.Append(separator);
            separator = '&';
            builder.Append(Uri.EscapeDataString(name));
            builder.Append('=');
            builder.Append(Uri.EscapeDataString(value));
        }

        return builder.ToString();
    }

    private static string? Optional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static string? NonEmpty(string? value) => value?.Length > 0 ? value : null;

    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string CategoryValue(ApiAssetCategory category) => category switch
    {
        ApiAssetCategory.Bgm => "bgm",
        ApiAssetCategory.SoundEffect => "sound-effect",
        ApiAssetCategory.Image => "image",
        ApiAssetCategory.Video => "video",
        _ => throw new ArgumentOutOfRangeException(nameof(category))
    };

    private static string SortValue(ApiAssetSort sort) => sort switch
    {
        ApiAssetSort.UploadedAt => "uploaded",
        ApiAssetSort.Name => "name",
        ApiAssetSort.Size => "size",
        _ => throw new ArgumentOutOfRangeException(nameof(sort))
    };

    private static string DerivativeKindValue(AssetDerivativeKind kind) => kind switch
    {
        AssetDerivativeKind.Thumbnail => "thumbnail",
        AssetDerivativeKind.Proxy => "proxy",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static void ValidateReadableStream(Stream stream, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(stream, parameterName);
        if (!stream.CanRead)
        {
            throw new ArgumentException("The source stream must be readable.", parameterName);
        }
    }

    private sealed class NonDisposingStreamContent(Stream source, long? sizeBytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            source.CopyToAsync(stream);

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken) =>
            source.CopyToAsync(stream, cancellationToken);

        protected override bool TryComputeLength(out long length)
        {
            length = sizeBytes.GetValueOrDefault();
            return sizeBytes.HasValue;
        }
    }
}
