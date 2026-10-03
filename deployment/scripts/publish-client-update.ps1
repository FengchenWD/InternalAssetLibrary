[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $InstallerPath,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$')]
    [string] $Version,

    [ValidatePattern('^\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$')]
    [string] $MinimumCompatibleVersion = $Version,

    [string] $ReleaseNotes = "",

    [string] $SigningKeyPath = ""
)

$ErrorActionPreference = 'Stop'
if ([version]($Version.Split('-')[0]) -ge [version]'1.1.1' -and [string]::IsNullOrWhiteSpace($SigningKeyPath)) {
    throw 'Version 1.1.1 and later require -SigningKeyPath. The private key stays on the release machine and must not be uploaded.'
}
if (-not [string]::IsNullOrWhiteSpace($SigningKeyPath) -and -not (Test-Path -LiteralPath $SigningKeyPath -PathType Leaf)) {
    throw 'The release signing private key was not found.'
}
$installer = Get-Item -LiteralPath $InstallerPath -ErrorAction Stop
if ($installer.PSIsContainer -or $installer.Extension -ne '.exe' -or $installer.Length -le 0) {
    throw 'InstallerPath must point to an .exe installer.'
}

$releaseDirectory = $installer.DirectoryName
$releaseManifestPath = Join-Path $releaseDirectory 'manifest.json'
if (-not (Test-Path -LiteralPath $releaseManifestPath -PathType Leaf)) {
    throw "The installer release directory is missing manifest.json: $releaseDirectory"
}

$releaseManifest = [IO.File]::ReadAllText(
    $releaseManifestPath,
    [Text.UTF8Encoding]::new($false)) | ConvertFrom-Json
if (-not [string]::Equals([string]$releaseManifest.version, $Version, [StringComparison]::Ordinal)) {
    throw "The release manifest version does not match -Version: $($releaseManifest.version)"
}
if (-not $releaseManifest.installer -or
    -not [string]::Equals([string]$releaseManifest.installer.fileName, $installer.Name, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The release manifest does not describe the selected installer.'
}

$sourceFileName = [string]$releaseManifest.correspondingSource.fileName
if ([string]::IsNullOrWhiteSpace($sourceFileName) -or
    [IO.Path]::GetFileName($sourceFileName) -ne $sourceFileName -or
    -not $sourceFileName.EndsWith('-source.zip', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The release manifest does not declare a valid corresponding source archive.'
}
$sourceArchivePath = Join-Path $releaseDirectory $sourceFileName
$sourceChecksumPath = "$sourceArchivePath.sha256"
if (-not (Test-Path -LiteralPath $sourceArchivePath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $sourceChecksumPath -PathType Leaf)) {
    throw 'The corresponding source archive or its SHA-256 file is missing.'
}

$installerHash = (Get-FileHash -LiteralPath $installer.FullName -Algorithm SHA256).Hash.ToUpperInvariant()
if ($installer.Length -ne [long]$releaseManifest.installer.sizeBytes -or
    -not [string]::Equals($installerHash, [string]$releaseManifest.installer.sha256, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The selected installer does not match the release manifest.'
}

$sourceHash = (Get-FileHash -LiteralPath $sourceArchivePath -Algorithm SHA256).Hash.ToLowerInvariant()
$expectedSourceChecksum = "$sourceHash  $sourceFileName"
$actualSourceChecksum = [IO.File]::ReadAllText(
    $sourceChecksumPath,
    [Text.UTF8Encoding]::new($false)).Trim()
if (-not [string]::Equals($sourceHash, [string]$releaseManifest.correspondingSource.sha256, [StringComparison]::OrdinalIgnoreCase) -or
    -not [string]::Equals($actualSourceChecksum, $expectedSourceChecksum, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The corresponding source archive does not match its release metadata.'
}

$updatesDirectory = Join-Path $PSScriptRoot '..\updates'
New-Item -ItemType Directory -Path $updatesDirectory -Force | Out-Null
$installerBaseName = [IO.Path]::GetFileNameWithoutExtension($installer.Name)
$installerExtension = $installer.Extension.ToLowerInvariant()
$versionedInstallerName = "$installerBaseName-$Version$installerExtension"
$installerObjectKey = "client-updates/$Version/$versionedInstallerName"
$destination = Join-Path $updatesDirectory $versionedInstallerName
$incomingInstaller = Join-Path $updatesDirectory ".incoming-$([Guid]::NewGuid().ToString('N')).tmp"
$manifestPath = Join-Path $updatesDirectory 'latest.json'
$incomingManifest = Join-Path $updatesDirectory ".latest-$([Guid]::NewGuid().ToString('N')).tmp"
$manifestBackup = Join-Path $updatesDirectory ".latest-backup-$([Guid]::NewGuid().ToString('N')).tmp"

try {
    if (Test-Path -LiteralPath $destination -PathType Leaf) {
        $existingHash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
        if (-not [string]::Equals($existingHash, $installerHash, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Version $Version is already published with different installer bytes: $destination"
        }
    } else {
        Copy-Item -LiteralPath $installer.FullName -Destination $incomingInstaller
        $incomingHash = (Get-FileHash -LiteralPath $incomingInstaller -Algorithm SHA256).Hash
        if (-not [string]::Equals($incomingHash, $installerHash, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The copied installer failed SHA-256 verification.'
        }
        [IO.File]::Move($incomingInstaller, $destination)
    }

    $copied = Get-Item -LiteralPath $destination

    $manifest = [ordered]@{
        version = $Version
        minimumCompatibleVersion = $MinimumCompatibleVersion
        publishedAt = [DateTimeOffset]::UtcNow.ToString('O')
        installerFileName = $copied.Name
        installerSizeBytes = $copied.Length
        installerSha256 = $installerHash
        installerObjectKey = $installerObjectKey
        releaseNotes = $ReleaseNotes
    }
    $manifestJson = $manifest | ConvertTo-Json
    [IO.File]::WriteAllText($incomingManifest, $manifestJson, [Text.UTF8Encoding]::new($false))
    if (-not [string]::IsNullOrWhiteSpace($SigningKeyPath)) {
        $signer = Join-Path $PSScriptRoot '../../scripts/release-signature.ps1'
        & $signer -Mode Sign -PrivateKeyPath $SigningKeyPath -ManifestPath $incomingManifest
        & $signer -Mode Verify -ManifestPath $incomingManifest -PublicKey 'MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE1JskSJck7qN7Qw/USkbWw6qCJFyCzzLfHpEZ6tRaq5j0anG1GpI84u0rv8kNEVL47NFqi1yFMRJeXZwnT42gag=='
    }
    $null = [IO.File]::ReadAllText(
        $incomingManifest,
        [Text.UTF8Encoding]::new($false)) | ConvertFrom-Json
    if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
        [IO.File]::Replace($incomingManifest, $manifestPath, $manifestBackup)
    } else {
        [IO.File]::Move($incomingManifest, $manifestPath)
    }

    Write-Host "Published $Version ($($copied.Length) bytes, SHA-256 $installerHash) to $updatesDirectory"
    Write-Host "Private COS object key: $installerObjectKey"
    Write-Host "Upload $destination to that exact private COS key before deploying latest.json."
    Write-Host "Verified corresponding source: $sourceFileName ($sourceHash)"
}
finally {
    foreach ($temporaryPath in @($incomingInstaller, $incomingManifest, $manifestBackup)) {
        if (Test-Path -LiteralPath $temporaryPath) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
    }
}
