[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$')]
    [string]$Version,
    [switch]$RebuildExisting
)

function Assert-Amd64PeFile {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $reader = [IO.BinaryReader]::new($stream)
    try {
        if ($stream.Length -lt 64 -or $reader.ReadUInt16() -ne 0x5A4D) {
            throw "运行库不是有效的 Windows PE 文件：$Path"
        }

        $stream.Position = 0x3C
        $peOffset = [int]$reader.ReadUInt32()
        if ($peOffset -lt 64 -or $peOffset -gt ($stream.Length - 6)) {
            throw "运行库 PE 头偏移无效：$Path"
        }

        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) {
            throw "运行库缺少 PE 签名：$Path"
        }

        $machine = $reader.ReadUInt16()
        if ($machine -ne 0x8664) {
            throw ('运行库必须为 Windows x64 (AMD64)，检测到 Machine=0x{0:X4}：{1}' -f $machine, $Path)
        }
    } finally {
        $reader.Dispose()
    }
}

function Get-LibMpvClientApiVersion {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    if (-not ('LibMpvRuntimeProbe' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class LibMpvRuntimeProbe
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint ClientApiVersionDelegate();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryW(string path);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr module);

    public static uint GetClientApiVersion(string path)
    {
        IntPtr handle = LoadLibraryW(path);
        if (handle == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

        try
        {
            IntPtr export = GetProcAddress(handle, "mpv_client_api_version");
            if (export == IntPtr.Zero)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

            return Marshal.GetDelegateForFunctionPointer<ClientApiVersionDelegate>(export)();
        }
        finally
        {
            FreeLibrary(handle);
        }
    }
}
'@
    }

    return [LibMpvRuntimeProbe]::GetClientApiVersion($Path)
}

function Get-MediaToolIdentity {
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [ValidateSet('ffmpeg', 'ffprobe')]
        [string]$ToolName
    )

    $output = @(& $Path -hide_banner -version 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "$ToolName -version 失败，退出代码：$LASTEXITCODE"
    }

    $text = ($output | Out-String).Trim()
    if ($text -notmatch "(?m)^$ToolName version\s+" ) {
        throw "$Path 没有输出可识别的 $ToolName 版本信息。"
    }
    if ($text -match '(?i)--enable-nonfree') {
        throw "$Path 使用了不可再分发的 FFmpeg nonfree 配置，不能进入 GPL-3.0-only 客户端安装包。"
    }

    [ordered]@{
        firstLine = ($text -split "`r?`n")[0]
        sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

$ErrorActionPreference = 'Stop'
# Publishing may build the Avalonia desktop project.  Disable its build-time
# telemetry so a restricted per-user cache cannot make packaging fail.
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
$projectRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $projectRoot 'src\InternalAssetLibrary.Client\InternalAssetLibrary.Client.csproj'
$updaterProject = Join-Path $projectRoot 'src\InternalAssetLibrary.Updater\InternalAssetLibrary.Updater.csproj'
$releaseRoot = Join-Path $projectRoot 'artifacts\releases'
$releaseDirectory = Join-Path $releaseRoot $Version
$stagingDirectory = Join-Path $releaseRoot ".staging-$([Guid]::NewGuid().ToString('N'))"
$buildArtifactDirectory = Join-Path $stagingDirectory '.build'
$runtimeIdentifier = 'win-x64'
$executableName = 'InternalAssetLibrary.Client.exe'
$updaterExecutableName = 'InternalAssetLibrary.Updater.exe'
$installerName = 'InternalAssetLibrary.Client.Setup.exe'
$installerScript = Join-Path $projectRoot 'packaging\windows\InternalAssetLibrary.Client.iss'
$mediaRuntimeSourceDirectory = Join-Path $projectRoot 'artifacts\media-runtime\win-x64'
$requiredRuntimeFiles = @('libmpv-2.dll', 'ffmpeg.exe', 'ffprobe.exe')
$runtimeLockPath = Join-Path $projectRoot 'packaging\windows\media-runtime.win-x64.lock.json'
$sourcePackageScript = Join-Path $projectRoot 'scripts\package-source.ps1'
$sourceArchiveName = "InternalAssetLibrary-$Version-source.zip"
$sourceArchivePath = Join-Path (Join-Path $projectRoot 'artifacts') $sourceArchiveName
$sourceChecksumPath = "$sourceArchivePath.sha256"

$resolvedReleaseRoot = [IO.Path]::GetFullPath($releaseRoot) + [IO.Path]::DirectorySeparatorChar
$resolvedReleaseDirectory = [IO.Path]::GetFullPath($releaseDirectory) + [IO.Path]::DirectorySeparatorChar
$resolvedStagingDirectory = [IO.Path]::GetFullPath($stagingDirectory) + [IO.Path]::DirectorySeparatorChar

if (-not $resolvedReleaseDirectory.StartsWith($resolvedReleaseRoot, [StringComparison]::OrdinalIgnoreCase) -or
    -not $resolvedStagingDirectory.StartsWith($resolvedReleaseRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw '客户端发布目录必须位于项目 artifacts\releases 目录中。'
}

if (Test-Path -LiteralPath $releaseDirectory) {
    if (-not $RebuildExisting) {
        throw "版本 $Version 已存在，不会覆盖：$releaseDirectory；如需基于最新源码重建，请显式传入 -RebuildExisting。"
    }

    $backupName = ".superseded-$Version-before-rebuild-$([DateTime]::Now.ToString('yyyyMMdd-HHmmss'))"
    $backupDirectory = Join-Path $releaseRoot $backupName
    Move-Item -LiteralPath $releaseDirectory -Destination $backupDirectory
    Write-Warning "旧发布目录已保留在：$backupDirectory"
}

if ((Test-Path -LiteralPath $sourceArchivePath) -or (Test-Path -LiteralPath $sourceChecksumPath)) {
    throw "检测到预存的正式版本源码包；为避免复用过期源码，请先检查并移除：$sourceArchivePath"
}

if (-not (Test-Path -LiteralPath $mediaRuntimeSourceDirectory -PathType Container)) {
    throw "缺少 Windows x64 媒体运行库目录：$mediaRuntimeSourceDirectory"
}

$runtimeLock = Get-Content -LiteralPath $runtimeLockPath -Raw | ConvertFrom-Json
if ($runtimeLock.schemaVersion -ne 1 -or $runtimeLock.runtimeIdentifier -ne $runtimeIdentifier) {
    throw "媒体运行库锁文件版本或 RID 无效：$runtimeLockPath"
}
$runtimeSourcePrefix = [IO.Path]::GetFullPath($mediaRuntimeSourceDirectory) + [IO.Path]::DirectorySeparatorChar
foreach ($requiredFile in $requiredRuntimeFiles) {
    $lockProperty = $runtimeLock.files.PSObject.Properties[$requiredFile]
    if (-not $lockProperty) {
        throw "媒体运行库锁文件缺少条目：$requiredFile"
    }

    $lockedEntry = $lockProperty.Value
    if ([string]$lockedEntry.sha256 -notmatch '^[0-9a-fA-F]{64}$') {
        throw "媒体运行库锁文件包含无效 SHA-256：$requiredFile"
    }

    $requiredPath = Join-Path $mediaRuntimeSourceDirectory $requiredFile
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "媒体运行库缺少必需文件：$requiredPath"
    }
    $actualHash = (Get-FileHash -LiteralPath $requiredPath -Algorithm SHA256).Hash
    if (-not [string]::Equals($actualHash, [string]$lockedEntry.sha256, [StringComparison]::OrdinalIgnoreCase)) {
        throw "媒体运行库与固定 SHA-256 不一致，拒绝执行或打包：$requiredFile"
    }

    $componentLicenses = @($lockedEntry.licensePaths)
    if ($componentLicenses.Count -eq 0) {
        throw "媒体运行库锁文件未声明许可证：$requiredFile"
    }
    foreach ($relativeLicensePath in $componentLicenses) {
        $licensePath = [IO.Path]::GetFullPath(
            (Join-Path $mediaRuntimeSourceDirectory ([string]$relativeLicensePath).Replace('/', '\')))
        if (-not $licensePath.StartsWith($runtimeSourcePrefix, [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $licensePath -PathType Leaf)) {
            throw "媒体运行库许可证路径无效或缺失：$requiredFile -> $relativeLicensePath"
        }
    }
}

$runtimeItems = @(Get-ChildItem -LiteralPath $mediaRuntimeSourceDirectory -Force -Recurse)
$reparsePoints = @($runtimeItems | Where-Object {
    ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
})
if ($reparsePoints.Count -gt 0) {
    throw "媒体运行库目录不能包含符号链接或其他重解析点：$($reparsePoints[0].FullName)"
}

foreach ($requiredFile in $requiredRuntimeFiles) {
    $requiredPath = Join-Path $mediaRuntimeSourceDirectory $requiredFile
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "媒体运行库缺少必需文件：$requiredPath"
    }
    if ((Get-Item -LiteralPath $requiredPath).Length -eq 0) {
        throw "媒体运行库文件为空：$requiredPath"
    }

}

$runtimePeFiles = @(
    $runtimeItems |
        Where-Object {
            -not $_.PSIsContainer -and
            ($_.Extension -eq '.dll' -or $requiredRuntimeFiles -contains $_.Name)
        } |
        Sort-Object FullName -Unique
)
foreach ($runtimePeFile in $runtimePeFiles) {
    if ($runtimePeFile.Length -eq 0) {
        throw "媒体运行库文件为空：$($runtimePeFile.FullName)"
    }

    Assert-Amd64PeFile -Path $runtimePeFile.FullName
}

$unexpectedExecutables = @($runtimeItems | Where-Object {
    -not $_.PSIsContainer -and
    $_.Extension -eq '.exe' -and
    $requiredRuntimeFiles -notcontains $_.Name
})
if ($unexpectedExecutables.Count -gt 0) {
    throw "媒体运行库包含未使用的额外 EXE，请移出后再发布：$($unexpectedExecutables[0].FullName)"
}

if (Test-Path -LiteralPath (Join-Path $mediaRuntimeSourceDirectory 'runtime-manifest.json')) {
    throw 'runtime-manifest.json 由发布脚本生成，不能预先放入媒体运行库输入目录。'
}

$licenseEvidence = @($runtimeItems | Where-Object {
    -not $_.PSIsContainer -and $_.Name -match '(?i)(license|copying|notice|copyright)'
})
if ($licenseEvidence.Count -eq 0) {
    throw '媒体运行库必须保留上游随附的 LICENSE、COPYING、NOTICE 或版权文件。'
}

$ffmpegIdentity = Get-MediaToolIdentity `
    -Path (Join-Path $mediaRuntimeSourceDirectory 'ffmpeg.exe') `
    -ToolName 'ffmpeg'
$ffprobeIdentity = Get-MediaToolIdentity `
    -Path (Join-Path $mediaRuntimeSourceDirectory 'ffprobe.exe') `
    -ToolName 'ffprobe'
$libMpvPath = Join-Path $mediaRuntimeSourceDirectory 'libmpv-2.dll'
$lockedLibMpv = $runtimeLock.files.PSObject.Properties['libmpv-2.dll'].Value
$libMpvFileVersion = (Get-Item -LiteralPath $libMpvPath).VersionInfo.FileVersion
if (-not [string]::Equals($libMpvFileVersion, [string]$lockedLibMpv.fileVersion, [StringComparison]::Ordinal)) {
    throw "libmpv 文件版本与运行库锁不一致：$libMpvFileVersion"
}
$libMpvClientApiVersion = Get-LibMpvClientApiVersion -Path $libMpvPath
if ($libMpvClientApiVersion -ne [uint32]$lockedLibMpv.clientApiVersion) {
    throw "libmpv client API 与运行库锁不一致：$libMpvClientApiVersion"
}
$libMpvIdentity = [ordered]@{
    fileVersion = $libMpvFileVersion
    clientApiVersion = $libMpvClientApiVersion
    sha256 = (Get-FileHash -LiteralPath $libMpvPath -Algorithm SHA256).Hash.ToLowerInvariant()
}

New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
New-Item -ItemType Directory -Path $stagingDirectory | Out-Null

try {
    & $sourcePackageScript -Version $Version
    if (-not (Test-Path -LiteralPath $sourceArchivePath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $sourceChecksumPath -PathType Leaf)) {
        throw '正式发布需要同时生成对应版本的源码 ZIP 和 SHA-256 校验文件。'
    }

    $sourceArchiveHash = (Get-FileHash -LiteralPath $sourceArchivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $sourceChecksumText = (Get-Content -LiteralPath $sourceChecksumPath -Raw).Trim()
    $expectedSourceChecksum = "$sourceArchiveHash  $sourceArchiveName"
    if (-not [string]::Equals($sourceChecksumText, $expectedSourceChecksum, [StringComparison]::OrdinalIgnoreCase)) {
        throw "源码包 SHA-256 校验文件与实际归档不一致：$sourceArchivePath"
    }

    $packageCache = Join-Path $projectRoot 'artifacts\nuget-packages'
    New-Item -ItemType Directory -Path $packageCache -Force | Out-Null

    $restoreArguments = @(
        'restore'
        $project
        '--locked-mode'
        '--runtime', $runtimeIdentifier
        '--artifacts-path', $buildArtifactDirectory
        '--packages', $packageCache
        '--property:Configuration=Release'
        '--property:SelfContained=true'
        '--property:PublishSingleFile=true'
        '--property:IncludeNativeLibrariesForSelfExtract=true'
        '--property:PublishTrimmed=false'
        '--property:DebugType=None'
        '--property:DebugSymbols=false'
        '--property:UsePublishLockFile=true'
        "--property:Version=$Version"
    )

    $offlineFeed = Join-Path $projectRoot 'artifacts\nuget-offline'
    $websiteOfflineFeed = Join-Path $projectRoot 'deployment\nuget-offline'
    if (Test-Path -LiteralPath $offlineFeed -PathType Container) {
        $restoreSources = @('--source', $offlineFeed)
        if (Test-Path -LiteralPath $websiteOfflineFeed -PathType Container) {
            $restoreSources += @('--source', $websiteOfflineFeed)
        }
        & dotnet @restoreArguments @restoreSources
        $restoreExitCode = $LASTEXITCODE

        if ($restoreExitCode -ne 0) {
            Write-Warning '离线源和全局 NuGet 缓存不完整，改用已配置的 NuGet 源重试。'
            & dotnet @restoreArguments --force
            $restoreExitCode = $LASTEXITCODE
        }
    } else {
        & dotnet @restoreArguments
        $restoreExitCode = $LASTEXITCODE
    }

    if ($restoreExitCode -ne 0) {
        throw "dotnet restore 失败，退出代码：$restoreExitCode"
    }

    $publishArguments = @(
        'publish'
        $project
        '--configuration', 'Release'
        '--runtime', $runtimeIdentifier
        '--self-contained', 'true'
        '--output', $stagingDirectory
        '--artifacts-path', $buildArtifactDirectory
        '--no-restore'
        '--property:PublishSingleFile=true'
        '--property:IncludeNativeLibrariesForSelfExtract=true'
        '--property:PublishTrimmed=false'
        '--property:DebugType=None'
        '--property:DebugSymbols=false'
        '--property:UsePublishLockFile=true'
        "--property:Version=$Version"
    )

    & dotnet @publishArguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish 失败，退出代码：$LASTEXITCODE"
    }

    $updaterRestoreArguments = @(
        'restore'
        $updaterProject
        '--locked-mode'
        '--runtime', $runtimeIdentifier
        '--artifacts-path', $buildArtifactDirectory
        '--packages', $packageCache
        '--property:Configuration=Release'
        '--property:SelfContained=true'
        '--property:PublishSingleFile=true'
        '--property:IncludeNativeLibrariesForSelfExtract=true'
        '--property:PublishTrimmed=false'
        '--property:DebugType=None'
        '--property:DebugSymbols=false'
        '--property:UsePublishLockFile=true'
        "--property:Version=$Version"
    )
    if (Test-Path -LiteralPath $offlineFeed -PathType Container) {
        $restoreSources = @('--source', $offlineFeed)
        if (Test-Path -LiteralPath $websiteOfflineFeed -PathType Container) {
            $restoreSources += @('--source', $websiteOfflineFeed)
        }
        & dotnet @updaterRestoreArguments @restoreSources
    } else {
        & dotnet @updaterRestoreArguments
    }
    if ($LASTEXITCODE -ne 0) {
        throw "更新助手 dotnet restore 失败，退出代码：$LASTEXITCODE"
    }

    $updaterStagingDirectory = Join-Path $stagingDirectory '.updater-publish'
    & dotnet publish $updaterProject `
        --configuration Release `
        --runtime $runtimeIdentifier `
        --self-contained true `
        --output $updaterStagingDirectory `
        --artifacts-path $buildArtifactDirectory `
        --no-restore `
        --property:PublishSingleFile=true `
        --property:IncludeNativeLibrariesForSelfExtract=true `
        --property:PublishTrimmed=false `
        --property:DebugType=None `
        --property:DebugSymbols=false `
        --property:UsePublishLockFile=true `
        "--property:Version=$Version"
    if ($LASTEXITCODE -ne 0) {
        throw "更新助手 dotnet publish 失败，退出代码：$LASTEXITCODE"
    }

    $publishedUpdater = Join-Path $updaterStagingDirectory $updaterExecutableName
    if (-not (Test-Path -LiteralPath $publishedUpdater -PathType Leaf)) {
        throw "发布结果中缺少更新助手 EXE：$publishedUpdater"
    }
    Copy-Item -LiteralPath $publishedUpdater -Destination (Join-Path $stagingDirectory $updaterExecutableName)
    Remove-Item -LiteralPath $updaterStagingDirectory -Recurse -Force

    $publishedExecutable = Join-Path $stagingDirectory $executableName
    if (-not (Test-Path -LiteralPath $publishedExecutable -PathType Leaf)) {
        throw "发布结果中缺少客户端 EXE：$publishedExecutable"
    }

    $diagnosticsAssembly = Get-ChildItem -LiteralPath $stagingDirectory -Filter 'Avalonia.Diagnostics.dll' -File -Recurse
    if ($diagnosticsAssembly) {
        throw 'Release 发布结果包含仅供调试使用的 Avalonia.Diagnostics.dll。'
    }

    $stagingRuntimeDirectory = Join-Path $stagingDirectory 'runtime'
    New-Item -ItemType Directory -Path $stagingRuntimeDirectory | Out-Null
    Get-ChildItem -LiteralPath $mediaRuntimeSourceDirectory -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $stagingRuntimeDirectory -Recurse
    }
    Copy-Item -LiteralPath $runtimeLockPath -Destination (Join-Path $stagingRuntimeDirectory 'runtime-lock.json')

    $runtimePrefix = [IO.Path]::GetFullPath($stagingRuntimeDirectory) + [IO.Path]::DirectorySeparatorChar
    $runtimeEntries = @(
        Get-ChildItem -LiteralPath $stagingRuntimeDirectory -File -Recurse |
            Sort-Object FullName |
            ForEach-Object {
                [ordered]@{
                    fileName = $_.FullName.Substring($runtimePrefix.Length).Replace('\', '/')
                    sizeBytes = $_.Length
                    sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                }
            }
    )
    $runtimeSizeBytes = [int64](
        ($runtimeEntries | ForEach-Object { $_['sizeBytes'] } | Measure-Object -Sum).Sum)
    $runtimeManifestPath = Join-Path $stagingRuntimeDirectory 'runtime-manifest.json'
    [ordered]@{
        runtimeIdentifier = $runtimeIdentifier
        architecture = 'x64'
        libmpv = $libMpvIdentity
        ffmpeg = $ffmpegIdentity
        ffprobe = $ffprobeIdentity
        fileCount = $runtimeEntries.Count
        sizeBytes = $runtimeSizeBytes
        files = $runtimeEntries
        validatedAtUtc = [DateTime]::UtcNow.ToString('o')
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $runtimeManifestPath -Encoding utf8NoBOM
    $runtimeManifestHash = (Get-FileHash -LiteralPath $runtimeManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    "$runtimeManifestHash  runtime-manifest.json" |
        Set-Content -LiteralPath (Join-Path $stagingRuntimeDirectory 'runtime-manifest.json.sha256') -Encoding utf8NoBOM
    $runtimePackagedSizeBytes = $runtimeSizeBytes +
        (Get-Item -LiteralPath $runtimeManifestPath).Length +
        (Get-Item -LiteralPath (Join-Path $stagingRuntimeDirectory 'runtime-manifest.json.sha256')).Length

    $stagingLicensesDirectory = Join-Path $stagingDirectory 'Licenses'
    New-Item -ItemType Directory -Path $stagingLicensesDirectory | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $projectRoot 'LICENSES') -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $stagingLicensesDirectory -Recurse
    }
    Copy-Item `
        -LiteralPath (Join-Path $projectRoot 'THIRD_PARTY_NOTICES.md') `
        -Destination (Join-Path $stagingDirectory 'THIRD_PARTY_NOTICES.md')
    $licensePrefix = [IO.Path]::GetFullPath($stagingLicensesDirectory) + [IO.Path]::DirectorySeparatorChar
    $licenseEntries = @(
        Get-ChildItem -LiteralPath $stagingLicensesDirectory -File -Recurse |
            Sort-Object FullName |
            ForEach-Object {
                [ordered]@{
                    fileName = $_.FullName.Substring($licensePrefix.Length).Replace('\', '/')
                    sizeBytes = $_.Length
                    sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                }
            }
    )
    $licenseManifestPath = Join-Path $stagingLicensesDirectory 'license-manifest.json'
    [ordered]@{
        fileCount = $licenseEntries.Count
        files = $licenseEntries
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $licenseManifestPath -Encoding utf8NoBOM
    $licenseManifestHash = (Get-FileHash -LiteralPath $licenseManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    "$licenseManifestHash  license-manifest.json" |
        Set-Content -LiteralPath (Join-Path $stagingLicensesDirectory 'license-manifest.json.sha256') -Encoding utf8NoBOM
    $licenseFiles = @(Get-ChildItem -LiteralPath $stagingLicensesDirectory -File -Recurse)
    $licenseSizeBytes = ($licenseFiles | Measure-Object -Property Length -Sum).Sum
    $thirdPartyNoticePath = Join-Path $stagingDirectory 'THIRD_PARTY_NOTICES.md'
    $thirdPartyNoticeFile = Get-Item -LiteralPath $thirdPartyNoticePath
    $thirdPartyNoticeHash = (Get-FileHash -LiteralPath $thirdPartyNoticePath -Algorithm SHA256).Hash.ToLowerInvariant()

    New-Item -ItemType Directory -Path $releaseDirectory | Out-Null
    $deliveryExecutable = Join-Path $releaseDirectory $executableName
    Copy-Item -LiteralPath $publishedExecutable -Destination $deliveryExecutable
    $deliveryUpdaterExecutable = Join-Path $releaseDirectory $updaterExecutableName
    Copy-Item -LiteralPath (Join-Path $stagingDirectory $updaterExecutableName) -Destination $deliveryUpdaterExecutable
    $deliveryRuntimeDirectory = Join-Path $releaseDirectory 'runtime'
    New-Item -ItemType Directory -Path $deliveryRuntimeDirectory | Out-Null
    Get-ChildItem -LiteralPath $stagingRuntimeDirectory -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $deliveryRuntimeDirectory -Recurse
    }
    $deliveryLicensesDirectory = Join-Path $releaseDirectory 'Licenses'
    New-Item -ItemType Directory -Path $deliveryLicensesDirectory | Out-Null
    Get-ChildItem -LiteralPath $stagingLicensesDirectory -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $deliveryLicensesDirectory -Recurse
    }
    Copy-Item `
        -LiteralPath (Join-Path $stagingDirectory 'THIRD_PARTY_NOTICES.md') `
        -Destination (Join-Path $releaseDirectory 'THIRD_PARTY_NOTICES.md')
    Copy-Item -LiteralPath $sourceArchivePath -Destination (Join-Path $releaseDirectory $sourceArchiveName)
    Copy-Item -LiteralPath $sourceChecksumPath -Destination (Join-Path $releaseDirectory "$sourceArchiveName.sha256")

    $file = Get-Item -LiteralPath $deliveryExecutable
    $sha256 = (Get-FileHash -LiteralPath $deliveryExecutable -Algorithm SHA256).Hash.ToLowerInvariant()
    $updaterFile = Get-Item -LiteralPath $deliveryUpdaterExecutable
    $updaterSha256 = (Get-FileHash -LiteralPath $deliveryUpdaterExecutable -Algorithm SHA256).Hash.ToLowerInvariant()
    "$updaterSha256  $updaterExecutableName" |
        Set-Content -LiteralPath (Join-Path $releaseDirectory "$updaterExecutableName.sha256") -Encoding utf8NoBOM
    $checksumName = "$executableName.sha256"
    "$sha256  $executableName" | Set-Content -LiteralPath (Join-Path $releaseDirectory $checksumName) -Encoding utf8NoBOM
    $publishedFiles = @(
        [ordered]@{
            role = 'client-program'
            fileName = $executableName
            sizeBytes = $file.Length
            sha256 = $sha256
        },
        [ordered]@{
            role = 'updater'
            fileName = $updaterExecutableName
            sizeBytes = $updaterFile.Length
            sha256 = $updaterSha256
        },
        [ordered]@{
            role = 'media-runtime'
            directoryName = 'runtime'
            fileCount = $runtimeEntries.Count + 2
            sizeBytes = $runtimePackagedSizeBytes
            manifestSha256 = $runtimeManifestHash
        },
        [ordered]@{
            role = 'licenses'
            directoryName = 'Licenses'
            fileCount = $licenseFiles.Count
            sizeBytes = $licenseSizeBytes
            manifestSha256 = $licenseManifestHash
        },
        [ordered]@{
            role = 'third-party-notices'
            fileName = 'THIRD_PARTY_NOTICES.md'
            sizeBytes = $thirdPartyNoticeFile.Length
            sha256 = $thirdPartyNoticeHash
        },
        [ordered]@{
            role = 'corresponding-source'
            fileName = $sourceArchiveName
            sizeBytes = (Get-Item -LiteralPath $sourceArchivePath).Length
            sha256 = $sourceArchiveHash
        }
    )
    $installerManifest = $null
    $innoCandidates = @()
    $innoCandidates += Join-Path $projectRoot 'artifacts\tools\inno-setup\ISCC.exe'
    $innoCandidates += Join-Path $projectRoot 'artifacts\tools\Inno Setup 6\ISCC.exe'

    $programFilesX86 = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)
    $programFiles = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
    $localApplicationData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    if ($localApplicationData) {
        $innoCandidates += Join-Path $localApplicationData 'Programs\Inno Setup 6\ISCC.exe'
    }
    if ($programFilesX86) {
        $innoCandidates += Join-Path $programFilesX86 'Inno Setup 6\ISCC.exe'
    }
    if ($programFiles) {
        $innoCandidates += Join-Path $programFiles 'Inno Setup 6\ISCC.exe'
    }
    $innoCommand = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($innoCommand) {
        $innoCandidates += $innoCommand.Source
    }

    $innoCompiler = $innoCandidates |
        Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) } |
        Select-Object -First 1
    if ($innoCompiler) {
        if (-not (Test-Path -LiteralPath $installerScript -PathType Leaf)) {
            throw "缺少 Inno Setup 安装定义：$installerScript"
        }

        $innoUninstaller = Join-Path (Split-Path -Parent $innoCompiler) 'unins000.exe'
        $innoVersion = if (Test-Path -LiteralPath $innoUninstaller -PathType Leaf) {
            (Get-Item -LiteralPath $innoUninstaller).VersionInfo.ProductVersion.Trim()
        } else {
            $null
        }
        if (-not $innoVersion -or $innoVersion -notmatch '^6\.') {
            throw "正式发布只允许经版本验证的 Inno Setup 6 编译器：$innoCompiler"
        }
        $innoSignature = Get-AuthenticodeSignature -LiteralPath $innoCompiler
        if ($innoSignature.Status -ne [Management.Automation.SignatureStatus]::Valid) {
            throw "Inno Setup 6 编译器数字签名无效：$innoCompiler"
        }

        & $innoCompiler "/DAppVersion=$Version" "/DSourceDirectory=$stagingDirectory" "/DOutputDirectory=$releaseDirectory" $installerScript
        if ($LASTEXITCODE -ne 0) {
            throw "Inno Setup 编译失败，退出代码：$LASTEXITCODE"
        }

        $installerPath = Join-Path $releaseDirectory $installerName
        if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf)) {
            throw "Inno Setup 未生成预期安装包：$installerPath"
        }

        $installerFile = Get-Item -LiteralPath $installerPath
        $installerSha256 = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash.ToLowerInvariant()
        "$installerSha256  $installerName" |
            Set-Content -LiteralPath (Join-Path $releaseDirectory "$installerName.sha256") -Encoding utf8NoBOM
        $installerManifest = [ordered]@{
            fileName = $installerName
            sizeBytes = $installerFile.Length
            sha256 = $installerSha256
            compilerVersion = $innoVersion
            compilerSigner = $innoSignature.SignerCertificate.Subject
            compilerSignerThumbprint = $innoSignature.SignerCertificate.Thumbprint
            perUser = $true
            defaultInstallPath = '%LocalAppData%\Programs\FengchenWD\InternalAssetLibrary'
            installDirectorySelectable = $true
            language = 'zh-CN'
        }
        $publishedFiles += [ordered]@{
            role = 'installer'
            fileName = $installerName
            sizeBytes = $installerFile.Length
            sha256 = $installerSha256
        }
    } else {
        throw '未找到 Inno Setup 6 的 ISCC.exe；正式客户端发布必须生成安装包。客户端仅发布安装版，请安装 Inno Setup 后重试。'
    }

    $releaseManifestPath = Join-Path $releaseDirectory 'manifest.json'
    [ordered]@{
        product = '云汀素材管理工具'
        version = $Version
        deliveryType = 'installer'
        runtimeIdentifier = $runtimeIdentifier
        selfContained = $true
        singleFile = $true
        requiresRuntimeDirectory = $true
        fileName = $executableName
        sizeBytes = $file.Length
        sha256 = $sha256
        correspondingSource = [ordered]@{
            fileName = $sourceArchiveName
            sha256 = $sourceArchiveHash
        }
        installer = $installerManifest
        mediaRuntime = [ordered]@{
            directoryName = 'runtime'
            runtimeIdentifier = $runtimeIdentifier
            libmpv = $libMpvIdentity
            ffmpeg = $ffmpegIdentity
            ffprobe = $ffprobeIdentity
            fileCount = $runtimeEntries.Count + 2
            sizeBytes = $runtimePackagedSizeBytes
            manifestSha256 = $runtimeManifestHash
        }
        files = $publishedFiles
        publishedAtUtc = [DateTime]::UtcNow.ToString('o')
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $releaseManifestPath -Encoding utf8NoBOM
    $releaseManifestHash = (Get-FileHash -LiteralPath $releaseManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    "$releaseManifestHash  manifest.json" |
        Set-Content -LiteralPath (Join-Path $releaseDirectory 'manifest.json.sha256') -Encoding utf8NoBOM

    Write-Output $deliveryExecutable
    if ($installerManifest) {
        Write-Output (Join-Path $releaseDirectory $installerName)
    }
    Write-Output (Join-Path $releaseDirectory $sourceArchiveName)
} catch {
    if (Test-Path -LiteralPath $releaseDirectory) {
        Remove-Item -LiteralPath $releaseDirectory -Recurse -Force
    }
    throw
} finally {
    if (Test-Path -LiteralPath $stagingDirectory) {
        Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
    }
    foreach ($temporarySourcePackage in @($sourceArchivePath, $sourceChecksumPath)) {
        if (Test-Path -LiteralPath $temporarySourcePackage) {
            Remove-Item -LiteralPath $temporarySourcePackage -Force
        }
    }
}
