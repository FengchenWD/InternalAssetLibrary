[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
# Keep test runs independent of Avalonia's machine-wide build-services cache.
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
$projectRoot = Split-Path -Parent $PSScriptRoot
$testProject = Join-Path $projectRoot 'tests\InternalAssetLibrary.SelfTests\InternalAssetLibrary.SelfTests.csproj'

$restoreArguments = @(
    'restore'
    $testProject
    '--locked-mode'
    "--property:Configuration=$Configuration"
)

$offlineFeed = Join-Path $projectRoot 'artifacts\nuget-offline'
$websiteOfflineFeed = Join-Path $projectRoot 'deployment\nuget-offline'
if (Test-Path -LiteralPath $offlineFeed -PathType Container) {
    $packageCache = Join-Path $projectRoot 'artifacts\nuget-packages'
    New-Item -ItemType Directory -Path $packageCache -Force | Out-Null
    $restoreArguments += @('--source', $offlineFeed, '--packages', $packageCache)
    if (Test-Path -LiteralPath $websiteOfflineFeed -PathType Container) {
        $restoreArguments += @('--source', $websiteOfflineFeed)
    }
} else {
    $restoreArguments += '--ignore-failed-sources'
}

& dotnet @restoreArguments
if ($LASTEXITCODE -ne 0) {
    Write-Warning '离线源不完整，使用官方 NuGet 源重试还原。'
    $fallbackArguments = @('restore', $testProject, '--locked-mode', "--property:Configuration=$Configuration", '--source', 'https://api.nuget.org/v3/index.json')
    & dotnet @fallbackArguments
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

dotnet run --project $testProject --configuration $Configuration --no-restore
exit $LASTEXITCODE
