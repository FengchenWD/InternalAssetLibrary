param(
    [ValidateSet('NewKey','Sign','Verify')][string]$Mode,
    [string]$PrivateKeyPath,
    [string]$ManifestPath,
    [string]$PublicKey
)
$ErrorActionPreference = 'Stop'
$ecdsa = [Security.Cryptography.ECDsa]::Create([Security.Cryptography.ECCurve+NamedCurves]::nistP256)
try {
    if ($Mode -eq 'NewKey') {
        if (Test-Path -LiteralPath $PrivateKeyPath) { throw '签名私钥已存在，禁止覆盖。' }
        $parent = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($PrivateKeyPath))
        [IO.Directory]::CreateDirectory($parent) | Out-Null
        [IO.File]::WriteAllText($PrivateKeyPath, $ecdsa.ExportPkcs8PrivateKeyPem(), [Text.UTF8Encoding]::new($false))
        [Convert]::ToBase64String($ecdsa.ExportSubjectPublicKeyInfo())
        return
    }
    $json = [IO.File]::ReadAllText($ManifestPath)
    $manifest = $json | ConvertFrom-Json
    # PowerShell 7.5 自动把 JSON 日期转换为 DateTime；再转字符串会丢失小数秒。
    # 必须保留 JSON 原始 ISO 时间戳，使签名与 C# 的 UtcTicks 完全一致。
    $document = [Text.Json.JsonDocument]::Parse($json)
    try { $manifest.publishedAt = $document.RootElement.GetProperty('publishedAt').GetString() }
    finally { $document.Dispose() }
    $buffer = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new($buffer, [Text.UTF8Encoding]::new($false), $true)
    $writer.Write([string]$manifest.version)
    $writer.Write([string]$manifest.minimumCompatibleVersion)
    $writer.Write([long]([DateTimeOffset]::Parse($manifest.publishedAt, [Globalization.CultureInfo]::InvariantCulture).UtcTicks))
    $writer.Write([string]$manifest.installerFileName)
    $writer.Write([long]$manifest.installerSizeBytes)
    $writer.Write([string]$manifest.installerSha256.ToUpperInvariant())
    $writer.Write([string]'/api/client/releases/latest/download')
    $writer.Write([string]([string]$manifest.releaseNotes).Trim())
    $writer.Flush(); $payload = $buffer.ToArray(); $writer.Dispose(); $buffer.Dispose()
    if ($Mode -eq 'Sign') {
        $ecdsa.ImportFromPem([IO.File]::ReadAllText($PrivateKeyPath))
        $signature = [Convert]::ToBase64String($ecdsa.SignData($payload, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation))
        $manifest | Add-Member -NotePropertyName signature -NotePropertyValue $signature -Force
        [IO.File]::WriteAllText($ManifestPath, ($manifest | ConvertTo-Json -Depth 12) + "`n", [Text.UTF8Encoding]::new($false))
    } else {
        $read = 0
        $ecdsa.ImportSubjectPublicKeyInfo([Convert]::FromBase64String($PublicKey), [ref]$read)
        if (!$ecdsa.VerifyData($payload, [Convert]::FromBase64String($manifest.signature), [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)) { throw '更新清单签名无效。' }
        '更新清单 ECDSA P-256/SHA-256 签名有效。'
    }
} finally { if ($null -ne $ecdsa) { $ecdsa.Dispose() } }
