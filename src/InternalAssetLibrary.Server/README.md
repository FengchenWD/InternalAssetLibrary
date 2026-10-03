# InternalAssetLibrary.Server

`InternalAssetLibrary.Server` is the first runnable server slice for the internal team asset library. It uses only the .NET 10 shared framework and does not require external NuGet packages.

## Development administrator

The bootstrap account is deliberately disabled in committed settings. It is only honored when `ASPNETCORE_ENVIRONMENT=Development`, and only when the user store is empty.

PowerShell example:

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:DevelopmentBootstrap__Enabled = "true"
$env:DevelopmentBootstrap__Username = "admin"
$env:DevelopmentBootstrap__TemporaryPassword = "replace-with-a-local-temporary-password"
$env:DevelopmentBootstrap__DisplayName = "风尘WD"
dotnet run --project src/InternalAssetLibrary.Server
```

The temporary password must be 8 to 128 characters long. The account is forced through `POST /api/auth/change-password` before it can use other features. Do not commit a real password to `appsettings.Development.json`.

## Development data

The current implementation is an atomic JSON development store because a SQLite NuGet dependency was unavailable during this bootstrap phase. It is not the final production database.

- State: `App_Data/development-data.json`
- Previous committed state: `App_Data/development-data.json.bak`
- Avatars: `App_Data/avatars/`
- Original asset files: `App_Data/objects/`
- Storage interface: `Data/IAppDataStore`
- Implementation: `Data/AtomicJsonDataStore`

Each update clones the last in-memory state, writes a same-directory temporary file, flushes it to disk, and atomically replaces the current file while retaining one backup. A process-local semaphore serializes reads and writes. This protects against concurrent in-process requests and interrupted writes, but it is intentionally single-process. SQLite should replace this implementation before normal production deployment. The server refuses to start outside Development unless `DevelopmentStorage:AllowInProduction=true` is explicitly set.

The cleanup service removes expired sessions, audit entries older than the configured retention, and recycled metadata after its retention date. Permanent deletion writes every current and retained-version object key to a durable outbox in the same metadata transaction. Object cleanup is attempted after commit and again when the cleanup service starts and every six hours; failed tasks retain their creation time, last-attempt time, and failure count until deletion succeeds. The same outbox drives either the development filesystem store or the production Tencent COS store selected by `Storage:ObjectProvider`.

## Authentication

1. `POST /api/auth/login` accepts a username or bound email plus password.
2. A successful login returns a random 256-bit bearer token. Only its SHA-256 hash is persisted.
3. Send `Authorization: Bearer <token>` on API calls.
4. A temporary-password session may only read `/api/me`, change its password, or log out.
5. Disabling an account or resetting its password revokes all sessions immediately.
6. Passwords use PBKDF2-HMAC-SHA256 with a per-password random salt and a configurable iteration count.

The bundled web console keeps its token in `sessionStorage`, not a persistent cookie or local storage.

## Realtime foundation

The authenticated SignalR hub is mapped at `/hubs/library`. It accepts the same bearer session as the REST API; the `access_token` query fallback is restricted to this hub path for transports that cannot set an authorization header. The shared `libraryChanged` notification carries `revision`, `scope`, and optional `entityId`.

Successful persistent mutations publish through a best-effort notifier:

- Account administration and private account-state changes publish `users/{userId}`.
- Public profile and avatar changes publish `profiles/{userId}`; changes also represented in the administrator summary publish both scopes.
- Asset changes publish `assets/{assetId}`. Initial original upload and active/recycled transitions additionally publish `profiles/{uploaderUserId}` when they change public profile counts.
- Tag changes publish `tags/{tagId}`. Rename and deletion additionally publish `assets/null`; asset operations that change tag usage publish `tags/null`.
- Marker-set and marker changes publish `markers/{markerSetId}`. Asset replacement or permanent deletion also publishes for affected marker sets.

Notification delivery starts only after the data-store update succeeds. A SignalR failure is logged as a warning and does not turn the already-committed API operation into an error; remaining notifications are still attempted. Both the desktop client and web console subscribe after an authenticated handshake, send a 15-second keepalive, reconnect after interruption, and perform a compensating REST refresh after reconnect. Notifications remain best-effort invalidation hints; REST responses and persisted state are authoritative.

## API routes

Authentication and profile:

- `POST /api/auth/login`
- `POST /api/auth/logout`
- `POST /api/auth/change-password`
- `GET /api/me`
- `PUT /api/me/profile`
- `PUT /api/me/email`
- `DELETE /api/me/email`
- `POST /api/me/avatar` (normalized static 512 x 512 WebP body, maximum 10 MB)
- `DELETE /api/me/avatar`

Team directory:

- `GET /api/users`
- `GET /api/users/{userId}`
- `GET /api/users/{userId}/avatar`

Asset metadata:

- `GET /api/assets`
- `GET /api/assets/{assetId}`
- `POST /api/assets`
- `PUT /api/assets/{assetId}`
- `PUT /api/assets/{assetId}/tags` (`createMissing` defaults to `true`; `false` returns `409 tag_catalog_changed` if any requested tag no longer exists)
- `DELETE /api/assets/{assetId}` (move to recycle bin)
- `POST /api/assets/{assetId}/restore`
- `DELETE /api/assets/{assetId}/permanent`
- `GET /api/me/recycle-bin` (current uploader only, including for administrators)
- `DELETE /api/me/recycle-bin` (permanently clear recycled assets uploaded by the current user in categories the user can currently access; requires browse and delete-own permissions)
- `POST /api/me/recycle-bin/{assetId}/restore` (current uploader only)
- `DELETE /api/me/recycle-bin/{assetId}` (permanently delete a recycled current-uploader asset)
- `GET /api/library/status`

Asset files:

- `PUT /api/assets/{assetId}/content`
- `PUT /api/assets/{assetId}/replacement`
- `GET /api/assets/{assetId}/content`
- `GET /api/assets/{assetId}/download`
- `GET /api/assets/{assetId}/versions/{versionId}/download`

Tags and marker sets:

- `GET|POST /api/tags`
- `PUT|DELETE /api/tags/{tagId}`
- `GET /api/assets/{assetId}/marker-sets`
- Marker-set create, copy, rename, delete, CSV import/export, and marker CRUD routes under `/api/marker-sets`

Administration:

- `GET /api/admin/users`
- `POST /api/admin/users`
- `PUT /api/admin/users/{userId}/status`
- `PUT /api/admin/users/{userId}/permissions`
- `PUT /api/admin/users/{userId}/username`
- `POST /api/admin/users/{userId}/reset-password`
- `DELETE /api/admin/users/{userId}/email`
- `DELETE /api/admin/users/{userId}/avatar`
- `POST /api/admin/users/{userId}/public-profile/clear`
- `GET /api/admin/audit`

Operations:

- `GET /healthz`

## Web console

Open the server root URL, normally `http://localhost:5019/` in the development HTTP profile. The page can log in, complete the forced password change, browse/filter asset metadata, view the recycle bin and team profiles, edit the current profile and avatar, create accounts, enable/disable accounts, reset temporary passwords, correct usernames, edit the administrator role and all 12 permission bits, and clear an account's email, avatar, or selected public profile fields without exposing private field values.

The web console accepts JPG, PNG, or static WebP avatar selections, crops them to a square, renders a 512 x 512 WebP, and removes source metadata before upload. The API rejects other formats, dimensions, animation chunks, EXIF, XMP, and embedded color profiles so a client cannot bypass that normalization step.

The web asset form registers validated metadata and calculates SHA-256 in the browser for files up to 64 MB. It does not upload the selected file. The desktop client streams real files through the compatible API, and production deployments use Tencent COS for signed downloads and resumable multipart uploads.

Run the isolated end-to-end smoke test with:

```powershell
pwsh -File src/InternalAssetLibrary.Server/scripts/server-smoke.ps1
```

The script builds the server, chooses an unused loopback port, starts a development administrator against isolated JSON data, exercises authentication/profile/admin/asset/recycle flows and seven concurrent reads, then stops only the process it launched. Its logs and data are written under `artifacts/smoke/`; failed runs are intentionally retained for diagnosis.

Permanent-delete responses mean the metadata removal and durable object-cleanup tasks were committed. A transient object-store failure does not restore deleted metadata; it leaves the task in the outbox for a later retry and is logged with the object key.

## Current boundary

Implemented here: atomic development persistence, authentication and sessions, first-login password change, account creation/status/permission APIs, profile privacy and avatars, team directory/profile data, asset metadata search/filter/order, ownership enforcement, collaborative tags, cloud marker-set REST APIs, quota checks, recycle/restore/permanent deletion, audit retention, local-development object upload/replacement/version download with Range support, download logging, health check, an authenticated SignalR hub with post-commit best-effort business notifications, and the working web console.

Not implemented in this server slice: SMTP and SQLite migrations. Tencent COS multipart transfer, signed URLs, resumable upload, client update packages, and production deployment/backup scripts are implemented for the configured deployment profile. Thumbnail and low-bitrate proxy derivatives can be uploaded by the desktop client, while the server also runs a single-process FFmpeg thumbnail backfill worker for eligible assets; this is not a production distributed media-processing pipeline. The JSON/local-object stores must not be presented as final production persistence.
