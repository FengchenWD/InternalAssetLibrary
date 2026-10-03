[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$serverRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
$projectPath = Join-Path $serverRoot 'InternalAssetLibrary.Server.csproj'
$runId = '{0}-{1}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), ([guid]::NewGuid().ToString('N'))
$artifactRoot = Join-Path $serverRoot (Join-Path 'artifacts/smoke' $runId)
$dataRoot = Join-Path $artifactRoot 'data'
$standardOutput = Join-Path $artifactRoot 'server.stdout.log'
$standardError = Join-Path $artifactRoot 'server.stderr.log'
$serverProcess = $null

New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null

function Get-FreeLoopbackPort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    try {
        $listener.Start()
        return ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
    }
    finally {
        $listener.Stop()
    }
}

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) {
        throw "Assertion failed: $Message"
    }
}

function Send-Json([string]$Method, [string]$Path, $Body, [string]$Token) {
    $headers = @{}
    if ($Token) {
        $headers.Authorization = "Bearer $Token"
    }

    $arguments = @{
        Method = $Method
        Uri = "$script:baseUrl$Path"
        Headers = $headers
    }
    if ($null -ne $Body) {
        $arguments.ContentType = 'application/json'
        $arguments.Body = $Body | ConvertTo-Json -Depth 10
    }

    return Invoke-RestMethod @arguments
}

function Send-ExpectStatus([string]$Method, [string]$Path, $Body, [string]$Token, [int]$ExpectedStatus) {
    $headers = @{}
    if ($Token) {
        $headers.Authorization = "Bearer $Token"
    }

    $arguments = @{
        Method = $Method
        Uri = "$script:baseUrl$Path"
        Headers = $headers
        SkipHttpErrorCheck = $true
    }
    if ($null -ne $Body) {
        $arguments.ContentType = 'application/json'
        $arguments.Body = $Body | ConvertTo-Json -Depth 10
    }

    $response = Invoke-WebRequest @arguments
    Assert-True ($response.StatusCode -eq $ExpectedStatus) "$Method $Path returned $($response.StatusCode), expected $ExpectedStatus."
    return $response
}

try {
    & dotnet build $projectPath --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw 'Server build failed.'
    }

    $port = Get-FreeLoopbackPort
    $script:baseUrl = "http://127.0.0.1:$port"
    $environment = @{
        ASPNETCORE_ENVIRONMENT = 'Development'
        DevelopmentBootstrap__Enabled = 'true'
        DevelopmentBootstrap__Username = 'smokeadmin'
        DevelopmentBootstrap__TemporaryPassword = 'SmokeInitial1234'
        DevelopmentBootstrap__DisplayName = 'Smoke Admin'
        DevelopmentStorage__DataPath = (Join-Path $dataRoot 'state.json')
        DevelopmentStorage__AvatarPath = (Join-Path $dataRoot 'avatars')
        DevelopmentStorage__ObjectPath = (Join-Path $dataRoot 'objects')
    }
    $arguments = @(
        'run',
        '--project', 'InternalAssetLibrary.Server.csproj',
        '--no-build',
        '--no-launch-profile',
        '--urls', $script:baseUrl
    )
    $serverProcess = Start-Process dotnet `
        -ArgumentList $arguments `
        -WorkingDirectory $serverRoot `
        -Environment $environment `
        -RedirectStandardOutput $standardOutput `
        -RedirectStandardError $standardError `
        -WindowStyle Hidden `
        -PassThru

    $healthy = $false
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline) {
        if ($serverProcess.HasExited) {
            throw "Server exited before becoming healthy. See $standardError"
        }

        try {
            $health = Invoke-RestMethod "$script:baseUrl/healthz"
            if ($health.status -eq 'healthy') {
                $healthy = $true
                break
            }
        }
        catch {
        }
        Start-Sleep -Milliseconds 200
    }
    Assert-True $healthy 'Server did not become healthy within 30 seconds.'

    $adminLogin = Send-Json POST '/api/auth/login' @{
        identifier = 'smokeadmin'
        password = 'SmokeInitial1234'
    } $null
    Assert-True $adminLogin.user.mustChangePassword 'Bootstrap account must show the one-time password change recommendation.'
    $libraryStatus = Send-Json GET '/api/library/status' $null $adminLogin.token
    Assert-True ($null -ne $libraryStatus) 'First login recommendation must not block normal API access.'

    $settings = Send-Json GET '/api/admin/settings' $null $adminLogin.token
    Assert-True ($settings.originalQuotaBytes -eq 107374182400) 'Runtime server settings must default to a 100 GiB quota.'
    $settingsFields = @($settings.PSObject.Properties.Name)
    Assert-True (-not ($settingsFields -match '(?i)secret|password|token|credential')) `
        'Runtime server settings exposed a sensitive field.'
    $settings.recycleRetentionDays = 17
    $settings.multipartPartSizeBytes = 33554432
    $updatedSettings = Send-Json PUT '/api/admin/settings' $settings $adminLogin.token
    Assert-True ($updatedSettings.recycleRetentionDays -eq 17) 'Admin settings update did not return the saved value.'
    Assert-True ($updatedSettings.multipartPartSizeBytes -eq 33554432) 'Multipart part-size setting was not applied.'
    $invalidSettings = $settings | ConvertTo-Json -Depth 10 | ConvertFrom-Json
    $invalidSettings.backupLocalPath = '../outside'
    $null = Send-ExpectStatus PUT '/api/admin/settings' $invalidSettings $adminLogin.token 400

    $rootPage = Invoke-WebRequest "$script:baseUrl/" -SkipHttpErrorCheck
    Assert-True ($rootPage.StatusCode -eq 404) `
        'The reserved cloud-library root must not expose the management interface.'

    foreach ($adminPath in @('/admin', '/admin/', '/admin/shared', '/admin/member', '/admin/menber', '/admin/profile', '/admin/management/users', '/admin/management/settings')) {
        $adminPage = Invoke-WebRequest "$script:baseUrl$adminPath" -SkipHttpErrorCheck
        Assert-True ($adminPage.StatusCode -eq 200 -and $adminPage.Content.Contains('adminTabs')) `
            "The $adminPath path did not return the management interface."
    }

    $admin = Send-Json POST '/api/auth/change-password' @{
        currentPassword = 'SmokeInitial1234'
        newPassword = 'SmokeChanged1234'
    } $adminLogin.token
    Assert-True (-not $admin.mustChangePassword) 'Changed password must clear the first-login gate.'

    $null = Send-ExpectStatus POST '/hubs/library/negotiate?negotiateVersion=1' $null $null 401
    $hubNegotiation = Send-Json POST '/hubs/library/negotiate?negotiateVersion=1' $null $adminLogin.token
    Assert-True (-not [string]::IsNullOrWhiteSpace($hubNegotiation.connectionToken)) 'Authenticated SignalR negotiation did not return a connection token.'

    $profile = Send-Json PUT '/api/me/profile' @{
        displayName = 'Smoke Admin'
        bio = 'Server smoke profile'
        birthday = '2000-01-02'
        gender = 'custom'
        customGender = 'private'
        contact = 'team-contact'
        birthdayVisibility = 'private'
        genderVisibility = 'private'
        contactVisibility = 'team'
    } $adminLogin.token
    Assert-True ($profile.bio -eq 'Server smoke profile') 'Profile update was not persisted.'

    $bound = Send-Json PUT '/api/me/email' @{
        email = 'smoke.admin@example.invalid'
        currentPassword = 'SmokeChanged1234'
    } $adminLogin.token
    Assert-True ($bound.email -eq 'smoke.admin@example.invalid') 'Email binding failed.'
    $emailLogin = Send-Json POST '/api/auth/login' @{
        identifier = 'smoke.admin@example.invalid'
        password = 'SmokeChanged1234'
    } $null
    Assert-True ($emailLogin.user.username -eq 'smokeadmin') 'Bound email login failed.'

    $passwordRuleUser = Send-Json POST '/api/admin/users' @{
        username = 'passwordrule'
        temporaryPassword = 'Password1!'
        displayName = 'Password Rule Test'
        isAdmin = $false
        permissions = $null
    } $adminLogin.token
    $passwordRuleLogin = Send-Json POST '/api/auth/login' @{
        identifier = 'passwordrule'
        password = 'Password1!'
    } $null
    $passwordRuleChanged = Send-Json POST '/api/auth/change-password' @{
        currentPassword = 'Password1!'
        newPassword = '1234567!'
    } $passwordRuleLogin.token
    Assert-True (-not $passwordRuleChanged.mustChangePassword) 'Password rule did not accept a valid two-class password.'
    $null = Send-Json PUT "/api/admin/users/$($passwordRuleUser.id)/status" @{ isEnabled = $false } $adminLogin.token
    $null = Send-Json DELETE "/api/admin/users/$($passwordRuleUser.id)" $null $adminLogin.token

    $member = Send-Json POST '/api/admin/users' @{
        username = 'smokemember'
        temporaryPassword = 'MemberInitial1234'
        displayName = 'Smoke Member'
        isAdmin = $false
        permissions = $null
    } $adminLogin.token
    $memberLogin = Send-Json POST '/api/auth/login' @{
        identifier = 'smokemember'
        password = 'MemberInitial1234'
    } $null
    $null = Send-Json POST '/api/auth/change-password' @{
        currentPassword = 'MemberInitial1234'
        newPassword = 'MemberChanged1234'
    } $memberLogin.token
    $null = Send-ExpectStatus GET '/api/admin/settings' $null $memberLogin.token 403

    $memberProfile = Send-Json PUT '/api/me/profile' @{
        displayName = 'Moderated Member'
        bio = 'Public biography to clear'
        birthday = '1998-02-03'
        gender = 'custom'
        customGender = 'Private gender'
        contact = 'Private contact'
        birthdayVisibility = 'private'
        genderVisibility = 'private'
        contactVisibility = 'private'
    } $memberLogin.token
    Assert-True ($memberProfile.displayName -eq 'Moderated Member') 'Member moderation profile setup failed.'
    $null = Send-ExpectStatus POST "/api/admin/users/$($member.id)/public-profile/clear" @{
        fields = @('displayName')
    } $memberLogin.token 403
    $null = Send-ExpectStatus POST "/api/admin/users/$($member.id)/public-profile/clear" @{
        fields = @('email')
    } $adminLogin.token 400

    $privateModeration = Send-Json POST "/api/admin/users/$($member.id)/public-profile/clear" @{
        fields = @('displayName', 'bio', 'birthday', 'gender', 'contact')
        birthday = '2001-01-01'
        password = 'ForgedPassword1234'
        impersonate = $true
    } $adminLogin.token
    Assert-True ($privateModeration.userId -eq $member.id) 'Profile moderation returned the wrong target user.'
    Assert-True (@($privateModeration.clearedFields).Count -eq 2) 'Private profile fields must not be cleared.'
    Assert-True ($privateModeration.clearedFields -contains 'displayName') 'Public display name was not cleared.'
    Assert-True ($privateModeration.clearedFields -contains 'bio') 'Public biography was not cleared.'
    $moderationProperties = @($privateModeration.PSObject.Properties.Name)
    Assert-True ($moderationProperties.Count -eq 2) 'Profile moderation response exposed unexpected data.'
    Assert-True ($moderationProperties -contains 'userId' -and $moderationProperties -contains 'clearedFields') `
        'Profile moderation response must contain only the target and cleared fields.'

    $privateProfileAfterModeration = Send-Json GET '/api/me' $null $memberLogin.token
    Assert-True ([string]::IsNullOrEmpty([string]$privateProfileAfterModeration.displayName)) `
        'Administrator could not clear the public display name.'
    Assert-True ($null -eq $privateProfileAfterModeration.bio) 'Administrator could not clear the public biography.'
    Assert-True ($null -ne $privateProfileAfterModeration.birthday) 'Private birthday was altered by moderation.'
    Assert-True ($privateProfileAfterModeration.birthdayVisibility -eq 'private') 'Private birthday visibility was altered.'
    Assert-True ($privateProfileAfterModeration.gender -eq 'custom') 'Private gender was altered by moderation.'
    Assert-True ($privateProfileAfterModeration.customGender -eq 'Private gender') 'Private custom gender was altered.'
    Assert-True ($privateProfileAfterModeration.genderVisibility -eq 'private') 'Private gender visibility was altered.'
    Assert-True ($privateProfileAfterModeration.contact -eq 'Private contact') 'Private contact was altered by moderation.'
    Assert-True ($privateProfileAfterModeration.contactVisibility -eq 'private') 'Private contact visibility was altered.'

    $memberPasswordCheck = Send-Json POST '/api/auth/login' @{
        identifier = 'smokemember'
        password = 'MemberChanged1234'
    } $null
    Assert-True ($memberPasswordCheck.user.id -eq $member.id) 'Profile moderation changed the member password.'

    $null = Send-Json PUT '/api/me/profile' @{
        displayName = ''
        bio = $null
        birthday = '1998-02-03'
        gender = 'custom'
        customGender = 'Team gender'
        contact = 'Team contact'
        birthdayVisibility = 'team'
        genderVisibility = 'team'
        contactVisibility = 'team'
    } $memberLogin.token
    $teamModeration = Send-Json POST "/api/admin/users/$($member.id)/public-profile/clear" @{
        fields = @('birthday', 'gender', 'contact')
    } $adminLogin.token
    Assert-True (@($teamModeration.clearedFields).Count -eq 3) 'Team-visible profile fields were not all cleared.'
    Assert-True ($teamModeration.clearedFields -contains 'birthday') 'Team-visible birthday was not cleared.'
    Assert-True ($teamModeration.clearedFields -contains 'gender') 'Team-visible gender was not cleared.'
    Assert-True ($teamModeration.clearedFields -contains 'contact') 'Team-visible contact was not cleared.'

    $teamProfileAfterModeration = Send-Json GET '/api/me' $null $memberLogin.token
    Assert-True ($null -eq $teamProfileAfterModeration.birthday) 'Cleared birthday value remained in private storage.'
    Assert-True ($null -eq $teamProfileAfterModeration.gender) 'Cleared gender value remained in private storage.'
    Assert-True ($null -eq $teamProfileAfterModeration.customGender) 'Cleared custom gender value remained in private storage.'
    Assert-True ($null -eq $teamProfileAfterModeration.contact) 'Cleared contact value remained in private storage.'
    Assert-True ($teamProfileAfterModeration.birthdayVisibility -eq 'private') 'Cleared birthday was left team-visible.'
    Assert-True ($teamProfileAfterModeration.genderVisibility -eq 'private') 'Cleared gender was left team-visible.'
    Assert-True ($teamProfileAfterModeration.contactVisibility -eq 'private') 'Cleared contact was left team-visible.'

    $idempotentModeration = Send-Json POST "/api/admin/users/$($member.id)/public-profile/clear" @{
        fields = @('displayName', 'bio', 'birthday', 'gender', 'contact')
    } $adminLogin.token
    Assert-True (@($idempotentModeration.clearedFields).Count -eq 0) 'Repeated profile moderation must be idempotent.'

    $uploadBytes = [System.Text.Encoding]::UTF8.GetBytes('smoke original object bytes')
    $uploadHash = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($uploadBytes))
    $asset = Send-Json POST '/api/assets' @{
        name = 'Smoke BGM'
        category = 'bgm'
        originalFileName = 'smoke.mp3'
        sizeBytes = $uploadBytes.Length
        contentHash = $uploadHash
        notes = 'Smoke asset'
        tags = @('smoke')
    } $adminLogin.token
    $asset = Invoke-RestMethod `
        -Method PUT `
        -Uri "$script:baseUrl/api/assets/$($asset.id)/content" `
        -Headers @{ Authorization = "Bearer $($adminLogin.token)" } `
        -ContentType 'application/octet-stream' `
        -Body $uploadBytes
    Assert-True $asset.hasOriginal 'Initial original file upload did not commit.'
    Assert-True ($asset.derivatives.thumbnail.state -eq 'queued') 'Initial upload did not queue its thumbnail.'
    Assert-True ($asset.derivatives.proxy.state -eq 'queued') 'Initial audio upload did not queue its proxy.'

    $thumbnailBytes = [Convert]::FromBase64String('UklGRiIAAABXRUJQVlA4IBYAAAAwAQCdASoBAAEADsD+JaQAA3AAAAAA')
    $thumbnailHash = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($thumbnailBytes))
    $derivatives = Invoke-RestMethod `
        -Method PUT `
        -Uri "$script:baseUrl/api/assets/$($asset.id)/derivatives/thumbnail?assetVersionId=$($asset.currentVersionId)&sizeBytes=$($thumbnailBytes.Length)&sha256=$thumbnailHash" `
        -Headers @{ Authorization = "Bearer $($adminLogin.token)" } `
        -ContentType 'image/webp' `
        -Body $thumbnailBytes
    Assert-True ($derivatives.thumbnail.state -eq 'ready') 'Thumbnail upload did not become ready.'
    Assert-True ($derivatives.thumbnail.eTag -eq $thumbnailHash) 'Thumbnail ETag did not match its SHA-256.'
    $thumbnailPath = Join-Path $artifactRoot 'smoke-thumbnail.webp'
    $thumbnailResponse = Invoke-WebRequest `
        -Uri "$script:baseUrl/api/assets/$($asset.id)/derivatives/thumbnail" `
        -Headers @{ Authorization = "Bearer $($adminLogin.token)" } `
        -OutFile $thumbnailPath `
        -PassThru
    Assert-True (($thumbnailResponse.Headers.ETag -join '') -match $thumbnailHash) 'Thumbnail response omitted its ETag.'
    Assert-True (
        [Convert]::ToBase64String([System.IO.File]::ReadAllBytes($thumbnailPath)) -eq [Convert]::ToBase64String($thumbnailBytes)
    ) 'Downloaded thumbnail bytes differ from upload.'
    $derivatives = Send-Json PUT "/api/assets/$($asset.id)/derivatives/proxy/status" @{
        assetVersionId = $asset.currentVersionId
        state = 'failed'
        errorCode = 'smoke_failure'
        errorMessage = 'Smoke failure'
    } $adminLogin.token
    Assert-True ($derivatives.proxy.state -eq 'failed') 'Derivative failure state was not persisted.'
    $derivatives = Send-Json POST "/api/assets/$($asset.id)/derivatives/proxy/retry" @{
        assetVersionId = $asset.currentVersionId
        serverBackfill = $false
    } $adminLogin.token
    Assert-True ($derivatives.proxy.state -eq 'queued' -and $derivatives.proxy.retryCount -eq 1) `
        'Derivative retry did not return to queued state or increment its count.'

    $lutBytes = [System.Text.Encoding]::UTF8.GetBytes("LUT_1D_SIZE 2`n0 0 0`n1 1 1`n")
    $lutHash = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($lutBytes))
    $lut = Send-Json POST '/api/luts' @{
        name = 'Smoke D-Log LUT'
        originalFileName = 'smoke-dlog.cube'
        sizeBytes = $lutBytes.Length
        sha256 = $lutHash
        note = 'Smoke LUT'
    } $adminLogin.token
    $lut = Invoke-RestMethod `
        -Method PUT `
        -Uri "$script:baseUrl/api/luts/$($lut.id)/content" `
        -Headers @{ Authorization = "Bearer $($adminLogin.token)" } `
        -ContentType 'application/x-cube' `
        -Body $lutBytes
    Assert-True $lut.hasContent 'Validated team LUT content did not commit.'
    $listedLuts = Send-Json GET '/api/luts?pageSize=20' $null $memberLogin.token
    Assert-True (@($listedLuts.items | Where-Object id -eq $lut.id).Count -eq 1) 'Team LUT was not visible to a member.'
    $lutDownloadPath = Join-Path $artifactRoot 'smoke.cube'
    Invoke-WebRequest `
        -Uri "$script:baseUrl/api/luts/$($lut.id)/download" `
        -Headers @{ Authorization = "Bearer $($memberLogin.token)" } `
        -OutFile $lutDownloadPath | Out-Null
    Assert-True (
        [Convert]::ToBase64String([System.IO.File]::ReadAllBytes($lutDownloadPath)) -eq [Convert]::ToBase64String($lutBytes)
    ) 'Downloaded team LUT bytes differ from upload.'
    $lut = Send-Json PUT "/api/luts/$($lut.id)" @{ name = 'Renamed Smoke LUT'; note = 'Updated' } $adminLogin.token
    Assert-True ($lut.name -eq 'Renamed Smoke LUT') 'LUT uploader could not rename the LUT.'
    $replacementLutBytes = [System.Text.Encoding]::UTF8.GetBytes("LUT_3D_SIZE 2`n0 0 0`n0 0 1`n0 1 0`n0 1 1`n1 0 0`n1 0 1`n1 1 0`n1 1 1`n")
    $replacementLutHash = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($replacementLutBytes))
    $replacementLutName = [uri]::EscapeDataString('smoke-dlog-v2.cube')
    $lut = Invoke-RestMethod `
        -Method PUT `
        -Uri "$script:baseUrl/api/luts/$($lut.id)/replacement?fileName=$replacementLutName&sizeBytes=$($replacementLutBytes.Length)&sha256=$replacementLutHash" `
        -Headers @{ Authorization = "Bearer $($adminLogin.token)" } `
        -ContentType 'application/x-cube' `
        -Body $replacementLutBytes
    Assert-True ($lut.version -eq 2 -and $lut.sha256 -eq $replacementLutHash) 'LUT replacement did not publish a new version.'
    $lutObjects = @(Get-ChildItem -LiteralPath (Join-Path (Join-Path $dataRoot 'objects') 'luts') -File -Recurse |
        Where-Object FullName -match ($lut.id -replace '-', ''))
    Assert-True ($lutObjects.Count -eq 1) 'LUT replacement left its old object behind.'
    $lut = Send-Json DELETE "/api/luts/$($lut.id)" $null $adminLogin.token
    Assert-True ($lut.state -eq 'recycled') 'Team LUT did not enter its recycle bin.'
    $lut = Send-Json POST "/api/luts/$($lut.id)/restore" $null $adminLogin.token
    Assert-True ($lut.state -eq 'active') 'Team LUT restore failed.'
    $lut = Send-Json DELETE "/api/luts/$($lut.id)" $null $adminLogin.token
    $null = Send-Json DELETE "/api/luts/$($lut.id)/permanent" $null $adminLogin.token
    $listedLuts = Send-Json GET '/api/luts?pageSize=20' $null $adminLogin.token
    Assert-True (@($listedLuts.items | Where-Object id -eq $lut.id).Count -eq 0) 'Permanently deleted LUT remained listed.'

    $downloadPath = Join-Path $artifactRoot 'smoke-original.bin'
    Invoke-WebRequest `
        -Uri "$script:baseUrl/api/assets/$($asset.id)/download" `
        -Headers @{ Authorization = "Bearer $($adminLogin.token)" } `
        -OutFile $downloadPath | Out-Null
    Assert-True (
        [Convert]::ToBase64String([System.IO.File]::ReadAllBytes($downloadPath)) -eq [Convert]::ToBase64String($uploadBytes)
    ) 'Downloaded original bytes differ from upload.'

    $rangePath = Join-Path $artifactRoot 'smoke-range.bin'
    $rangeResponse = Invoke-WebRequest `
        -Uri "$script:baseUrl/api/assets/$($asset.id)/content" `
        -Headers @{ Authorization = "Bearer $($adminLogin.token)"; Range = 'bytes=0-4' } `
        -OutFile $rangePath `
        -PassThru `
        -SkipHttpErrorCheck
    Assert-True ($rangeResponse.StatusCode -eq 206) 'Preview endpoint did not honor a byte range request.'
    Assert-True ((Get-Item $rangePath).Length -eq 5) 'Preview byte range returned the wrong length.'
    $video = Send-Json POST '/api/assets' @{
        name = 'Restricted Smoke Video'
        category = 'video'
        originalFileName = 'restricted.mp4'
        sizeBytes = 2048
        contentHash = ('B' * 64)
        notes = 'Category permission smoke asset'
        tags = @('restricted')
    } $adminLogin.token

    $defaultSortedAssets = Send-Json GET '/api/assets?pageSize=100' $null $adminLogin.token
    Assert-True ($defaultSortedAssets.items[0].name -eq 'Restricted Smoke Video') 'Default asset sorting was not name ascending.'
    Assert-True ($defaultSortedAssets.items[1].name -eq 'Smoke BGM') 'Default asset sorting returned the wrong second item.'
    $explicitSortedAssets = Send-Json GET '/api/assets?pageSize=100&sort=uploadedAt&order=desc' $null $adminLogin.token
    Assert-True ($explicitSortedAssets.items[0].name -eq 'Restricted Smoke Video') 'Explicit upload-time sorting changed unexpectedly.'

    $publicUsers = Send-Json GET '/api/users?pageSize=100' $null $adminLogin.token
    $publicAdmin = @($publicUsers.items | Where-Object id -eq $adminLogin.user.id)
    Assert-True ($publicAdmin.Count -eq 1) 'Uploader was not returned by the public user list.'
    Assert-True ($publicAdmin[0].assetCounts.bgm -eq 1) 'Public user-list counts omitted the uploaded BGM.'
    Assert-True ($null -eq $publicAdmin[0].assetCounts.video) 'Public user-list counts exposed metadata without an original file.'

    $publicProfile = Send-Json GET "/api/users/$($adminLogin.user.id)" $null $adminLogin.token
    Assert-True ($publicProfile.assetCounts.bgm -eq 1) 'Public profile counts omitted the uploaded BGM.'
    Assert-True ($null -eq $publicProfile.assetCounts.video) 'Public profile counts exposed metadata without an original file.'

    $publicProfileAssets = Send-Json GET "/api/assets?uploaderId=$($adminLogin.user.id)&pageSize=100" $null $adminLogin.token
    Assert-True ($publicProfileAssets.total -eq 1) 'Public profile asset list must contain only active assets with original files.'
    Assert-True ($publicProfileAssets.items[0].id -eq $asset.id) 'Public profile asset list returned the wrong active asset.'

    $largeBytes = [byte[]]::new(16MB + 1)
    $largeHash = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($largeBytes))
    $largeAsset = Send-Json POST '/api/assets' @{
        name = 'Request limit smoke'
        category = 'bgm'
        originalFileName = 'request-limit.wav'
        sizeBytes = $largeBytes.Length
        contentHash = $largeHash
        notes = $null
        tags = @()
    } $adminLogin.token
    $largeAsset = Invoke-RestMethod `
        -Method PUT `
        -Uri "$script:baseUrl/api/assets/$($largeAsset.id)/content" `
        -Headers @{ Authorization = "Bearer $($adminLogin.token)" } `
        -ContentType 'application/octet-stream' `
        -Body $largeBytes
    Assert-True $largeAsset.hasOriginal 'Per-endpoint request limit did not allow a file larger than the 16MB API default.'
    $largeObjectPath = Join-Path (Join-Path $dataRoot 'objects') ($largeAsset.objectKey -replace '/', [IO.Path]::DirectorySeparatorChar)
    Assert-True (Test-Path -LiteralPath $largeObjectPath) 'Large uploaded object was not stored.'
    $null = Send-Json DELETE "/api/assets/$($largeAsset.id)" $null $adminLogin.token
    $null = Send-Json DELETE "/api/assets/$($largeAsset.id)/permanent" $null $adminLogin.token
    Assert-True (-not (Test-Path -LiteralPath $largeObjectPath)) 'Permanent deletion did not remove the stored object.'
    $largeBytes = $null

    $limitedPermissions = @($member.permissions | Where-Object { $_ -ne 'assets.category.video' })
    $null = Send-Json PUT "/api/admin/users/$($member.id)/permissions" @{
        isAdmin = $false
        permissions = $limitedPermissions
    } $adminLogin.token
    $memberAssets = Send-Json GET '/api/assets?pageSize=10' $null $memberLogin.token
    Assert-True ($memberAssets.total -eq 1) 'Category permission did not hide the restricted video.'
    $null = Send-ExpectStatus GET "/api/assets/$($video.id)" $null $memberLogin.token 404
    $null = Send-ExpectStatus PUT "/api/assets/$($asset.id)" @{ name = 'Denied'; notes = $null } $memberLogin.token 403
    $tagged = Send-Json PUT "/api/assets/$($asset.id)/tags" @{ tags = @('shared-tag') } $memberLogin.token
    Assert-True ($tagged.tags -contains 'shared-tag') 'Collaborative tag update failed.'

    $memberTag = Send-Json POST '/api/tags' @{ name = 'member-tag' } $memberLogin.token
    Assert-True ($memberTag.name -eq 'member-tag') 'Member could not create a global tag.'
    $null = Send-ExpectStatus POST '/api/tags' @{ name = 'MEMBER-TAG' } $memberLogin.token 409
    $null = Send-ExpectStatus PUT "/api/tags/$($memberTag.id)" @{ name = 'member-renamed' } $memberLogin.token 403
    $tagged = Send-Json PUT "/api/assets/$($asset.id)/tags" @{ tags = @($memberTag.name) } $memberLogin.token
    Assert-True ($tagged.tags -contains 'member-tag') 'Global tag was not assigned to the asset.'

    $renamedTag = Send-Json PUT "/api/tags/$($memberTag.id)" @{ name = 'team-renamed' } $adminLogin.token
    Assert-True ($renamedTag.name -eq 'team-renamed') 'Administrator could not rename a global tag.'
    Assert-True ($renamedTag.usageCount -eq 1) 'Renamed global tag reported the wrong usage count.'
    $assetAfterTagRename = Send-Json GET "/api/assets/$($asset.id)" $null $memberLogin.token
    Assert-True ($assetAfterTagRename.tags -contains 'team-renamed') 'Global tag rename was not synchronized to the asset.'
    Assert-True (-not ($assetAfterTagRename.tags -contains 'member-tag')) 'Old tag name remained on the asset after rename.'

    $listedTags = Send-Json GET '/api/tags' $null $memberLogin.token
    $listedRenamedTag = @($listedTags | Where-Object id -eq $memberTag.id)
    Assert-True ($listedRenamedTag.Count -eq 1) 'Renamed global tag was not listed.'
    Assert-True ($listedRenamedTag[0].usageCount -eq 1) 'Listed global tag reported the wrong usage count.'

    $null = Send-Json DELETE "/api/tags/$($memberTag.id)" $null $adminLogin.token
    $assetAfterTagDelete = Send-Json GET "/api/assets/$($asset.id)" $null $memberLogin.token
    Assert-True (-not ($assetAfterTagDelete.tags -contains 'team-renamed')) 'Deleted global tag remained on the asset.'
    $listedTags = Send-Json GET '/api/tags' $null $memberLogin.token
    Assert-True (@($listedTags | Where-Object id -eq $memberTag.id).Count -eq 0) 'Deleted global tag remained in the directory.'
    $strictTagResponse = Send-ExpectStatus PUT "/api/assets/$($asset.id)/tags" @{
        tags = @('team-renamed')
        createMissing = $false
    } $memberLogin.token 409
    $strictTagContent = if ($strictTagResponse.Content -is [byte[]]) {
        [System.Text.Encoding]::UTF8.GetString($strictTagResponse.Content)
    } else {
        [string]$strictTagResponse.Content
    }
    $strictTagProblem = $strictTagContent | ConvertFrom-Json
    Assert-True ($strictTagProblem.code -eq 'tag_catalog_changed') 'Strict tag update returned the wrong conflict code.'
    $listedTags = Send-Json GET '/api/tags' $null $memberLogin.token
    Assert-True (@($listedTags | Where-Object name -eq 'team-renamed').Count -eq 0) 'Strict tag update recreated a deleted tag.'
    $legacyTagUpdate = Send-Json PUT "/api/assets/$($asset.id)/tags" @{
        tags = @('legacy-created-tag')
    } $memberLogin.token
    Assert-True ($legacyTagUpdate.tags -contains 'legacy-created-tag') 'Default tag update no longer creates missing tags.'

    $memberMarkerSet = Send-Json POST '/api/marker-sets' @{
        assetId = $asset.id
        assetVersionId = $asset.currentVersionId
        name = 'Member marker set'
    } $memberLogin.token
    $memberMarker = Send-Json POST "/api/marker-sets/$($memberMarkerSet.id)/markers" @{
        time = '00:00:05'
        name = 'Start'
        note = 'Smoke marker'
    } $memberLogin.token
    Assert-True ($memberMarker.time -eq '00:00:05') 'Member marker was not created.'

    $adminMarkerAccess = Send-Json GET "/api/marker-sets/$($memberMarkerSet.id)/access" $null $adminLogin.token
    Assert-True (-not $adminMarkerAccess.canEdit) 'Administrator must not edit another user marker set.'
    Assert-True $adminMarkerAccess.canDelete 'Administrator must be able to delete another user marker.'
    $null = Send-ExpectStatus PUT "/api/marker-sets/$($memberMarkerSet.id)" @{ name = 'Denied rename' } $adminLogin.token 403

    $adminCopy = Send-Json POST '/api/marker-sets/copy' @{
        sourceMarkerSetId = $memberMarkerSet.id
        name = 'Administrator copy'
    } $adminLogin.token
    Assert-True ($adminCopy.ownerUserId -eq $adminLogin.user.id) 'Copied marker set must belong to the copier.'
    Assert-True ($adminCopy.markers.Count -eq 1) 'Copied marker set did not preserve markers.'

    $csvOutput = Join-Path $artifactRoot 'member.markers.csv'
    $recordingName = [uri]::EscapeDataString('smoke.mp3')
    $recordingPath = [uri]::EscapeDataString('D:\素材\smoke.mp3')
    Invoke-WebRequest `
        -Uri "$script:baseUrl/api/marker-sets/$($memberMarkerSet.id)/csv?recordingName=$recordingName&recordingPath=$recordingPath&recordingDurationSeconds=60" `
        -Headers @{ Authorization = "Bearer $($memberLogin.token)" } `
        -OutFile $csvOutput | Out-Null
    $csvBytes = [System.IO.File]::ReadAllBytes($csvOutput)
    Assert-True ($csvBytes.Length -gt 3) 'Marker CSV export was empty.'
    Assert-True ($csvBytes[0] -eq 0xEF -and $csvBytes[1] -eq 0xBB -and $csvBytes[2] -eq 0xBF) 'Marker CSV export must use a UTF-8 BOM.'

    $projectRoot = Split-Path -Parent (Split-Path -Parent $serverRoot)
    $sampleCsv = Join-Path $projectRoot '测试/超一小时的markers.csv'
    $importName = [uri]::EscapeDataString('Imported player markers')
    $imported = Invoke-RestMethod `
        -Method POST `
        -Uri "$script:baseUrl/api/marker-sets/import?assetId=$($asset.id)&assetVersionId=$($asset.currentVersionId)&name=$importName" `
        -Headers @{ Authorization = "Bearer $($memberLogin.token)" } `
        -ContentType 'text/csv' `
        -InFile $sampleCsv
    Assert-True ($imported.markers.Count -eq 3) 'Nine-column marker CSV import failed.'

    $null = Send-Json DELETE "/api/marker-sets/$($memberMarkerSet.id)/markers/$($memberMarker.id)" $null $adminLogin.token
    $afterAdminDelete = Send-Json GET "/api/marker-sets/$($memberMarkerSet.id)" $null $memberLogin.token
    Assert-True ($afterAdminDelete.markers.Count -eq 0) 'Administrator marker deletion was not persisted.'

    $oldVersionId = $asset.currentVersionId
    $replacementBytes = [System.Text.Encoding]::UTF8.GetBytes('smoke replacement object bytes')
    $replacementHash = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($replacementBytes))
    $replacementFileName = [uri]::EscapeDataString('smoke-v2.mp3')
    $asset = Invoke-RestMethod `
        -Method PUT `
        -Uri "$script:baseUrl/api/assets/$($asset.id)/replacement?fileName=$replacementFileName&sizeBytes=$($replacementBytes.Length)&sha256=$replacementHash" `
        -Headers @{ Authorization = "Bearer $($adminLogin.token)" } `
        -ContentType 'application/octet-stream' `
        -Body $replacementBytes
    Assert-True ($asset.currentVersionId -ne $oldVersionId) 'Source replacement did not create a new version identifier.'
    Assert-True ($asset.previousVersionCount -eq 1) 'Source replacement did not retain the old source version.'
    Assert-True ($asset.derivatives.thumbnail.assetVersionId -eq $asset.currentVersionId) 'Replacement did not reset derivative version binding.'
    Assert-True ($asset.derivatives.thumbnail.state -eq 'queued') 'Replacement did not queue a fresh thumbnail.'
    $oldDerivativeRoot = Join-Path (Join-Path (Join-Path (Join-Path $dataRoot 'objects') 'derivatives') ($asset.id -replace '-', '')) ($oldVersionId -replace '-', '')
    Assert-True (-not (Test-Path -LiteralPath $oldDerivativeRoot) -or @(
        Get-ChildItem -LiteralPath $oldDerivativeRoot -File -ErrorAction SilentlyContinue
    ).Count -eq 0) 'Replacement left an old derivative object behind.'
    $oldMarkers = Send-Json GET "/api/marker-sets/$($memberMarkerSet.id)" $null $memberLogin.token
    Assert-True $oldMarkers.isBasedOnOldVersion 'Markers were not kept bound to the replaced source version.'

    $oldDownloadPath = Join-Path $artifactRoot 'smoke-old-version.bin'
    Invoke-WebRequest `
        -Uri "$script:baseUrl/api/assets/$($asset.id)/versions/$oldVersionId/download" `
        -Headers @{ Authorization = "Bearer $($adminLogin.token)" } `
        -OutFile $oldDownloadPath | Out-Null
    Assert-True (
        [Convert]::ToBase64String([System.IO.File]::ReadAllBytes($oldDownloadPath)) -eq [Convert]::ToBase64String($uploadBytes)
    ) 'Retained old-version download differs from the original.'

    $personalRecycleBytes = [System.Text.Encoding]::UTF8.GetBytes('member personal recycle-bin object')
    $personalRecycleHash = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($personalRecycleBytes))
    $personalRecycleAsset = Send-Json POST '/api/assets' @{
        name = 'Member personal recycle test'
        category = 'bgm'
        originalFileName = 'member-personal-recycle.mp3'
        sizeBytes = $personalRecycleBytes.Length
        contentHash = $personalRecycleHash
        notes = 'Owner-scoped recycle-bin smoke asset'
        tags = @()
    } $memberLogin.token
    $personalRecycleAsset = Invoke-RestMethod `
        -Method PUT `
        -Uri "$script:baseUrl/api/assets/$($personalRecycleAsset.id)/content" `
        -Headers @{ Authorization = "Bearer $($memberLogin.token)" } `
        -ContentType 'application/octet-stream' `
        -Body $personalRecycleBytes
    $personalRecycleOldObjectPath = Join-Path (Join-Path $dataRoot 'objects') ($personalRecycleAsset.objectKey -replace '/', [IO.Path]::DirectorySeparatorChar)
    $personalRecycleReplacementBytes = [System.Text.Encoding]::UTF8.GetBytes('member personal recycle-bin replacement')
    $personalRecycleReplacementHash = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($personalRecycleReplacementBytes))
    $personalRecycleReplacementName = [uri]::EscapeDataString('member-personal-recycle-v2.mp3')
    $personalRecycleAsset = Invoke-RestMethod `
        -Method PUT `
        -Uri "$script:baseUrl/api/assets/$($personalRecycleAsset.id)/replacement?fileName=$personalRecycleReplacementName&sizeBytes=$($personalRecycleReplacementBytes.Length)&sha256=$personalRecycleReplacementHash" `
        -Headers @{ Authorization = "Bearer $($memberLogin.token)" } `
        -ContentType 'application/octet-stream' `
        -Body $personalRecycleReplacementBytes
    Assert-True ($personalRecycleAsset.previousVersionCount -eq 1) 'Personal recycle-bin test did not retain its old source version.'
    $personalRecycleMarkerSet = Send-Json POST '/api/marker-sets' @{
        assetId = $personalRecycleAsset.id
        assetVersionId = $personalRecycleAsset.currentVersionId
        name = 'Personal recycle marker set'
    } $memberLogin.token
    $null = Send-Json POST "/api/marker-sets/$($personalRecycleMarkerSet.id)/markers" @{
        time = '00:00:00'
        name = 'Bulk deletion marker'
        note = $null
    } $memberLogin.token
    $personalRecycleObjectPath = Join-Path (Join-Path $dataRoot 'objects') ($personalRecycleAsset.objectKey -replace '/', [IO.Path]::DirectorySeparatorChar)
    Assert-True (Test-Path -LiteralPath $personalRecycleOldObjectPath) 'Personal recycle-bin old-version object was not stored.'
    Assert-True (Test-Path -LiteralPath $personalRecycleObjectPath) 'Personal recycle-bin current object was not stored.'
    $null = Send-Json DELETE "/api/assets/$($personalRecycleAsset.id)" $null $memberLogin.token
    $memberPersonalTrash = Send-Json GET '/api/me/recycle-bin' $null $memberLogin.token
    Assert-True ($memberPersonalTrash.total -eq 1) 'Member personal recycle bin did not list its own asset.'
    Assert-True ($memberPersonalTrash.items[0].id -eq $personalRecycleAsset.id) 'Member personal recycle bin returned the wrong asset.'
    $adminPersonalTrash = Send-Json GET '/api/me/recycle-bin' $null $adminLogin.token
    Assert-True ($adminPersonalTrash.total -eq 0) 'Administrator personal recycle bin exposed another uploader asset.'
    $adminGlobalTrash = Send-Json GET '/api/assets?state=recycled&pageSize=10' $null $adminLogin.token
    Assert-True (@($adminGlobalTrash.items | Where-Object id -eq $personalRecycleAsset.id).Count -eq 1) 'Existing administrator recycle-bin view lost cross-user visibility.'
    $adminClearResult = Send-Json DELETE '/api/me/recycle-bin' $null $adminLogin.token
    Assert-True ($adminClearResult.deletedCount -eq 0) 'Administrator personal clear deleted another uploader asset.'
    $null = Send-ExpectStatus POST "/api/me/recycle-bin/$($personalRecycleAsset.id)/restore" $null $adminLogin.token 404
    $null = Send-ExpectStatus DELETE "/api/me/recycle-bin/$($personalRecycleAsset.id)" $null $adminLogin.token 404
    $restoredPersonalAsset = Send-Json POST "/api/me/recycle-bin/$($personalRecycleAsset.id)/restore" $null $memberLogin.token
    Assert-True ($restoredPersonalAsset.state -eq 'active') 'Member could not restore its personal recycle-bin asset.'
    $null = Send-Json DELETE "/api/assets/$($personalRecycleAsset.id)" $null $memberLogin.token
    $memberClearResult = Send-Json DELETE '/api/me/recycle-bin' $null $memberLogin.token
    Assert-True ($memberClearResult.deletedCount -eq 1) 'Personal recycle-bin clear returned the wrong deletion count.'
    Assert-True (-not (Test-Path -LiteralPath $personalRecycleOldObjectPath)) 'Personal recycle-bin clear did not remove the old-version object.'
    Assert-True (-not (Test-Path -LiteralPath $personalRecycleObjectPath)) 'Personal recycle-bin clear did not remove the current object.'
    $null = Send-ExpectStatus GET "/api/marker-sets/$($personalRecycleMarkerSet.id)" $null $memberLogin.token 404
    $emptyClearResult = Send-Json DELETE '/api/me/recycle-bin' $null $memberLogin.token
    Assert-True ($emptyClearResult.deletedCount -eq 0) 'Clearing an empty personal recycle bin was not idempotent.'

    $null = Send-Json PUT "/api/admin/users/$($member.id)/permissions" @{
        isAdmin = $false
        permissions = $member.permissions
    } $adminLogin.token
    $hiddenRecycleBytes = [System.Text.Encoding]::UTF8.GetBytes('member hidden category recycle-bin object')
    $hiddenRecycleHash = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($hiddenRecycleBytes))
    $hiddenRecycleAsset = Send-Json POST '/api/assets' @{
        name = 'Member hidden category recycle test'
        category = 'video'
        originalFileName = 'member-hidden.mp4'
        sizeBytes = $hiddenRecycleBytes.Length
        contentHash = $hiddenRecycleHash
        notes = $null
        tags = @()
    } $memberLogin.token
    $hiddenRecycleAsset = Invoke-RestMethod `
        -Method PUT `
        -Uri "$script:baseUrl/api/assets/$($hiddenRecycleAsset.id)/content" `
        -Headers @{ Authorization = "Bearer $($memberLogin.token)" } `
        -ContentType 'application/octet-stream' `
        -Body $hiddenRecycleBytes
    $hiddenRecycleObjectPath = Join-Path (Join-Path $dataRoot 'objects') ($hiddenRecycleAsset.objectKey -replace '/', [IO.Path]::DirectorySeparatorChar)
    $null = Send-Json DELETE "/api/assets/$($hiddenRecycleAsset.id)" $null $memberLogin.token

    $permissionsWithoutBrowse = @($member.permissions | Where-Object { $_ -ne 'assets.browse' })
    $null = Send-Json PUT "/api/admin/users/$($member.id)/permissions" @{
        isAdmin = $false
        permissions = $permissionsWithoutBrowse
    } $adminLogin.token
    $null = Send-ExpectStatus DELETE '/api/me/recycle-bin' $null $memberLogin.token 403

    $null = Send-Json PUT "/api/admin/users/$($member.id)/permissions" @{
        isAdmin = $false
        permissions = $limitedPermissions
    } $adminLogin.token
    $hiddenCategoryTrash = Send-Json GET '/api/me/recycle-bin' $null $memberLogin.token
    Assert-True ($hiddenCategoryTrash.total -eq 0) 'Personal recycle bin exposed a category without access.'
    $hiddenCategoryClear = Send-Json DELETE '/api/me/recycle-bin' $null $memberLogin.token
    Assert-True ($hiddenCategoryClear.deletedCount -eq 0) 'Personal clear deleted a category without access.'
    Assert-True (Test-Path -LiteralPath $hiddenRecycleObjectPath) 'Category-filtered clear removed the hidden object.'
    $adminGlobalTrash = Send-Json GET '/api/assets?state=recycled&pageSize=10' $null $adminLogin.token
    Assert-True (@($adminGlobalTrash.items | Where-Object id -eq $hiddenRecycleAsset.id).Count -eq 1) 'Category-filtered clear removed hidden metadata.'

    $null = Send-Json PUT "/api/admin/users/$($member.id)/permissions" @{
        isAdmin = $false
        permissions = $member.permissions
    } $adminLogin.token
    $visibleCategoryClear = Send-Json DELETE '/api/me/recycle-bin' $null $memberLogin.token
    Assert-True ($visibleCategoryClear.deletedCount -eq 1) 'Visible category clear returned the wrong count.'
    Assert-True (-not (Test-Path -LiteralPath $hiddenRecycleObjectPath)) 'Visible category clear did not remove the object.'

    $recycled = Send-Json DELETE "/api/assets/$($asset.id)" $null $adminLogin.token
    Assert-True ($recycled.state -eq 'recycled') 'Asset was not moved to the recycle bin.'
    $trash = Send-Json GET '/api/assets?state=recycled&pageSize=10' $null $adminLogin.token
    Assert-True ($trash.total -eq 1) 'Recycled asset was not listed.'
    $recycledProfile = Send-Json GET "/api/users/$($adminLogin.user.id)" $null $adminLogin.token
    Assert-True ($null -eq $recycledProfile.assetCounts.bgm) 'Public profile counts exposed a recycled asset.'
    $recycledProfileAssets = Send-Json GET "/api/assets?uploaderId=$($adminLogin.user.id)&pageSize=100" $null $adminLogin.token
    Assert-True ($recycledProfileAssets.total -eq 0) 'Public profile asset list exposed a recycled asset.'
    $memberPersonalTrash = Send-Json GET '/api/me/recycle-bin' $null $memberLogin.token
    Assert-True ($memberPersonalTrash.total -eq 0) 'Member personal recycle bin exposed an administrator asset.'
    $null = Send-ExpectStatus POST "/api/me/recycle-bin/$($asset.id)/restore" $null $memberLogin.token 404
    $restoredAsset = Send-Json POST "/api/me/recycle-bin/$($asset.id)/restore" $null $adminLogin.token
    Assert-True ($restoredAsset.state -eq 'active') 'Asset restore failed.'

    $disabled = Send-Json PUT "/api/admin/users/$($member.id)/status" @{ isEnabled = $false } $adminLogin.token
    Assert-True (-not $disabled.isEnabled) 'Member was not disabled.'
    $null = Send-ExpectStatus GET '/api/me' $null $memberLogin.token 401
    $restoredMember = Send-Json PUT "/api/admin/users/$($member.id)/status" @{ isEnabled = $true } $adminLogin.token
    Assert-True $restoredMember.isEnabled 'Member was not restored.'

    $readUri = "$script:baseUrl/api/assets?pageSize=10"
    $bearer = "Bearer $($adminLogin.token)"
    $parallelReads = 1..7 | ForEach-Object -Parallel {
        Invoke-RestMethod -Uri $using:readUri -Headers @{ Authorization = $using:bearer }
    } -ThrottleLimit 7
    Assert-True ($parallelReads.Count -eq 7) 'Seven-way concurrent read did not complete.'
    Assert-True (($parallelReads | Where-Object total -eq 2).Count -eq 7) 'Concurrent reads returned inconsistent data.'

    $null = Send-ExpectStatus DELETE "/api/admin/users/$($member.id)" $null $adminLogin.token 409
    $memberLogin = Send-Json POST '/api/auth/login' @{
        identifier = 'smokemember'
        password = 'MemberChanged1234'
    } $null
    $memberAssetBytes = [System.Text.Encoding]::UTF8.GetBytes('member attribution survives account deletion')
    $memberAssetHash = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($memberAssetBytes))
    $memberAsset = Send-Json POST '/api/assets' @{
        name = 'Member-owned BGM'
        category = 'bgm'
        originalFileName = 'member-owned.mp3'
        sizeBytes = $memberAssetBytes.Length
        contentHash = $memberAssetHash
        notes = 'Deletion attribution smoke asset'
        tags = @('member-owned')
    } $memberLogin.token
    $memberAsset = Invoke-RestMethod `
        -Method PUT `
        -Uri "$script:baseUrl/api/assets/$($memberAsset.id)/content" `
        -Headers @{ Authorization = "Bearer $($memberLogin.token)" } `
        -ContentType 'application/octet-stream' `
        -Body $memberAssetBytes

    $lockedMemberPermissions = @($member.permissions | Where-Object {
        $_ -ne 'assets.edit-own' -and $_ -ne 'assets.delete-own'
    })
    $null = Send-Json PUT "/api/admin/users/$($member.id)/permissions" @{
        isAdmin = $false
        permissions = $lockedMemberPermissions
    } $adminLogin.token
    $null = Send-ExpectStatus PUT "/api/assets/$($memberAsset.id)" @{
        name = 'Denied member edit'
        notes = $null
    } $memberLogin.token 403
    $null = Send-ExpectStatus DELETE "/api/assets/$($memberAsset.id)" $null $memberLogin.token 403
    $null = Send-ExpectStatus GET '/api/me/recycle-bin' $null $memberLogin.token 403
    $null = Send-ExpectStatus DELETE '/api/me/recycle-bin' $null $memberLogin.token 403
    $memberAsset = Send-Json PUT "/api/assets/$($memberAsset.id)" @{
        name = 'Administrator-maintained member BGM'
        notes = 'Administrator edit remains available'
    } $adminLogin.token
    Assert-True ($memberAsset.name -eq 'Administrator-maintained member BGM') 'Administrator could not maintain another uploader asset.'

    $null = Send-Json PUT "/api/admin/users/$($member.id)/status" @{ isEnabled = $false } $adminLogin.token
    $null = Send-Json DELETE "/api/admin/users/$($member.id)" $null $adminLogin.token
    $adminUsersAfterDelete = Send-Json GET '/api/admin/users' $null $adminLogin.token
    Assert-True (@($adminUsersAfterDelete | Where-Object id -eq $member.id).Count -eq 0) 'Deleted account remained in the administrator user list.'
    $publicUsersAfterDelete = Send-Json GET '/api/users?pageSize=100' $null $adminLogin.token
    Assert-True (@($publicUsersAfterDelete.items | Where-Object id -eq $member.id).Count -eq 0) 'Deleted account remained in the public user list.'
    $memberAssetAfterDelete = Send-Json GET "/api/assets/$($memberAsset.id)" $null $adminLogin.token
    Assert-True ($memberAssetAfterDelete.uploadedBy.id -eq $member.id) 'Deleted uploader identifier was not retained on the asset.'
    Assert-True ($memberAssetAfterDelete.uploadedBy.username -eq 'smokemember') 'Deleted uploader username snapshot was not retained.'
    $memberMarkersAfterDelete = Send-Json GET "/api/marker-sets/$($memberMarkerSet.id)" $null $adminLogin.token
    Assert-True ($memberMarkersAfterDelete.ownerUserId -eq $member.id) 'Deleted marker owner identifier was not retained.'
    Assert-True ($memberMarkersAfterDelete.ownerDisplayName -eq 'smokemember') 'Deleted marker owner display snapshot was not retained.'

    $persistedState = Get-Content -Raw -Encoding UTF8 (Join-Path $dataRoot 'state.json') | ConvertFrom-Json
    Assert-True ($persistedState.markerSets.Count -eq 3) 'Marker sets were not persisted in the development data store.'
    Assert-True ($persistedState.downloadLog.Count -eq 2) 'Download logging did not retain current and old-version downloads.'
    Assert-True (@($persistedState.tags | Where-Object id -eq $memberTag.id).Count -eq 0) 'Deleted global tag was persisted.'
    $tagAuditActions = @($persistedState.auditLog | Where-Object targetId -eq $memberTag.id | Select-Object -ExpandProperty action)
    Assert-True ($tagAuditActions -contains 'tag.created') 'Global tag creation was not audited.'
    Assert-True ($tagAuditActions -contains 'tag.renamed') 'Global tag rename was not audited.'
    Assert-True ($tagAuditActions -contains 'tag.deleted') 'Global tag deletion was not audited.'
    $profileModerationAudit = @($persistedState.auditLog | Where-Object {
        $_.targetId -eq $member.id -and $_.action -eq 'admin.user.public_profile.cleared'
    })
    Assert-True ($profileModerationAudit.Count -eq 3) 'Profile moderation attempts were not fully audited.'
    Assert-True (-not (($profileModerationAudit | ConvertTo-Json -Depth 5) -match 'Private gender|Private contact|ForgedPassword')) `
        'Profile moderation audit leaked profile values or request-only secrets.'

    [pscustomobject]@{
        Status = 'PASS'
        BaseUrl = $script:baseUrl
        ArtifactDirectory = $artifactRoot
        ConcurrentReads = $parallelReads.Count
    }
}
catch {
    Write-Error "Server smoke test failed. Logs and isolated data were retained at '$artifactRoot'. $($_.Exception.Message)"
    throw
}
finally {
    if ($null -ne $serverProcess -and -not $serverProcess.HasExited) {
        Stop-Process -Id $serverProcess.Id -Force
        $serverProcess.WaitForExit(5000) | Out-Null
    }
}
