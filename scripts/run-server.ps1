[CmdletBinding()]
param(
    [string]$Urls = 'http://127.0.0.1:5019',
    [string]$AdminUsername = 'admin',
    [string]$AdminDisplayName = '风尘WD'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$serverProject = Join-Path $projectRoot 'src\InternalAssetLibrary.Server\InternalAssetLibrary.Server.csproj'

if ([string]::IsNullOrWhiteSpace($env:IAL_BOOTSTRAP_PASSWORD)) {
    throw '请先在当前 PowerShell 会话设置 IAL_BOOTSTRAP_PASSWORD。该值只在空数据库首次创建管理员时使用。'
}

$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:DevelopmentBootstrap__Enabled = 'true'
$env:DevelopmentBootstrap__Username = $AdminUsername
$env:DevelopmentBootstrap__DisplayName = $AdminDisplayName
$env:DevelopmentBootstrap__TemporaryPassword = $env:IAL_BOOTSTRAP_PASSWORD

dotnet run --project $serverProject --urls $Urls
exit $LASTEXITCODE
