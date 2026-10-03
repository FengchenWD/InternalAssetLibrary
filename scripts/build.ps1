[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$IncludeDesktop
)

$ErrorActionPreference = 'Stop'
# Avalonia's build-time telemetry writes outside the workspace.  Some managed
# Windows environments deny that cache path, so opt out for deterministic local
# and CI builds.  This affects compilation only; the shipped client has no
# runtime telemetry component.
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
$projectRoot = Split-Path -Parent $PSScriptRoot
$solution = if ($IncludeDesktop) {
    Join-Path $projectRoot 'InternalAssetLibrary.slnx'
} else {
    Join-Path $projectRoot 'InternalAssetLibrary.Foundation.slnx'
}

$restoreArguments = @(
    'restore'
    $solution
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
    $fallbackArguments = @('restore', $solution, '--locked-mode', "--property:Configuration=$Configuration", '--source', 'https://api.nuget.org/v3/index.json')
    & dotnet @fallbackArguments
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$buildArguments = @(
    'build'
    $solution
    '--configuration'
    $Configuration
    '--no-restore'
)

if ($Configuration -eq 'Release') {
    dotnet clean $solution --configuration $Configuration
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $buildArguments += '--no-incremental'
}

& dotnet @buildArguments
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if ($IncludeDesktop -and $Configuration -eq 'Release') {
    $clientOutput = Join-Path $projectRoot 'src\InternalAssetLibrary.Client\bin\Release'
    $diagnosticsAssemblies = @(
        Get-ChildItem -LiteralPath $clientOutput -Filter 'Avalonia.Diagnostics.dll' -File -Recurse -ErrorAction SilentlyContinue
    )

    if ($diagnosticsAssemblies.Count -gt 0) {
        $paths = $diagnosticsAssemblies.FullName -join [Environment]::NewLine
        Write-Error "Release output contains the Debug-only Avalonia.Diagnostics assembly:$([Environment]::NewLine)$paths"
        exit 1
    }
}

exit 0
