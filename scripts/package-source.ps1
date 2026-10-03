[CmdletBinding()]
param([Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$')][string]$Version)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
[xml]$client = Get-Content (Join-Path $taskRoot 'src/InternalAssetLibrary.Client/InternalAssetLibrary.Client.csproj') -Raw
if ($client.Project.PropertyGroup.Version -ne $Version) { throw '源码包版本与客户端不一致。' }
$artifactRoot = Join-Path $taskRoot 'artifacts'
New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null
$archive = Join-Path $artifactRoot "InternalAssetLibrary-$Version-source.zip"
if (Test-Path -LiteralPath $archive) { throw '源码包已存在，不自动覆盖。' }
$staging = Join-Path $artifactRoot ('source-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $staging | Out-Null
$allowed = @('src','tests','scripts','packaging','deployment','assets','LICENSES','docs',
    '.gitignore','Directory.Build.props','global.json','InternalAssetLibrary.slnx',
    'InternalAssetLibrary.Foundation.slnx','README.md','CHANGELOG.md','THIRD_PARTY_NOTICES.md')
$exclude = '(?i)(^|/)(bin|obj|App_Data|artifacts|\.git|node_modules|TestResults)(/|$)|(^|/)\.env$|\.(db|db-wal|db-shm|pem|pfx|log|exe|dll|zip|7z)$|^deployment/updates/(?!\.gitkeep$)'
foreach ($item in $allowed) {
    $source = Join-Path $taskRoot $item
    $files = if (Test-Path -LiteralPath $source -PathType Container) { Get-ChildItem -LiteralPath $source -File -Recurse -Force } else { Get-Item -LiteralPath $source -Force }
    foreach ($file in $files) {
        $relative = [IO.Path]::GetRelativePath($taskRoot,$file.FullName).Replace('\','/')
        if ($relative -match $exclude) { continue }
        if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw '源码不能包含链接。' }
        $target = Join-Path $staging $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target
    }
}
[IO.Compression.ZipFile]::CreateFromDirectory($staging,$archive)
$sha = (Get-FileHash -LiteralPath $archive).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$archive.sha256","$sha  $([IO.Path]::GetFileName($archive))`n",[Text.UTF8Encoding]::new($false))
# 暂存目录保留用于核对；不删除任何已存在的发布物。
Write-Output $archive
Write-Output "$archive.sha256"
