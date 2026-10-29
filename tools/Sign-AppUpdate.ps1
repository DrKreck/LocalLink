#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$WindowsArchive,
    [Parameter(Mandatory=$true)][string]$Version,
    [string]$PrivateKeyPath = '',
    [switch]$AllowPlaintextXml,
    [Parameter(Mandatory=$true)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security
if (-not $PrivateKeyPath) { $PrivateKeyPath = Join-Path $env:LOCALAPPDATA 'LocalLink-Maintainer\update-signing-key.dpapi' }
# Provision the signing key separately and pin only its PUBLIC XML in
# AppUpdateClient.TrustedManifestPublicKeyXml before building signed-only clients.
# This command never generates keys, changes the trust root or uploads files.
if ($Version -notmatch '^(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})$') { throw 'Use a stable X.Y.Z version.' }
$archive = [IO.Path]::GetFullPath($WindowsArchive)
$key = [IO.Path]::GetFullPath($PrivateKeyPath)
$out = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
$source = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\')
if ($key -eq $source -or $key.StartsWith($source+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Keep the private key outside the source/repository.' }
if ($key -eq $out -or $key.StartsWith($out+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Keep the private key outside the release/output directory.' }
if ([IO.Path]::GetFileName($archive) -cne "LocalLink-$Version-windows-x64.zip") { throw 'Archive name must match the release version.' }
foreach ($path in @($archive,$key,$out)) {
    if ($path -notmatch '^[A-Za-z]:[\\/]' -or $path.StartsWith('\\') -or $path.Substring(2).Contains(':')) { throw 'Use ordinary local paths.' }
    $cursor = $path
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Reparse points are not allowed.' }
        }
        $cursor = Split-Path -Parent $cursor
    }
}
$size = (Get-Item -LiteralPath $archive).Length
if ($size -lt 1 -or $size -gt 67108864) { throw 'Archive size is outside the supported limit.' }
if ((Get-Item -LiteralPath $key).Length -lt 1 -or (Get-Item -LiteralPath $key).Length -gt 32768) { throw 'Private key file has an invalid size.' }
$manifestPath = Join-Path $out 'update-manifest.json'
$signaturePath = Join-Path $out 'update-manifest.sig'
if ((Test-Path -LiteralPath $manifestPath) -or (Test-Path -LiteralPath $signaturePath)) { throw 'Manifest/signature already exist; use a fresh release directory.' }
$value = [ordered]@{Schema='locallink-update-v1';Version=$Version;Package=[IO.Path]::GetFileName($archive);Sha256=(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant();Bytes=$size}
$utf8 = New-Object Text.UTF8Encoding($false)
$bytes = $utf8.GetBytes(($value | ConvertTo-Json -Compress))
$rsa = New-Object Security.Cryptography.RSACryptoServiceProvider
$rsa.PersistKeyInCsp = $false
$plain = $null
try {
    $encoding = New-Object Text.UTF8Encoding($false,$true)
    try {
        if ([IO.Path]::GetExtension($key) -ieq '.dpapi') {
            $entropy = $encoding.GetBytes('LocalLink/update-signing/v1')
            $plain = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($key),$entropy,[Security.Cryptography.DataProtectionScope]::CurrentUser)
            $rsa.FromXmlString($encoding.GetString($plain))
        } elseif ($AllowPlaintextXml -and [IO.Path]::GetExtension($key) -ieq '.xml') {
            $plain = [IO.File]::ReadAllBytes($key)
            $rsa.FromXmlString($encoding.GetString($plain))
        } else { throw 'Use a .dpapi key; legacy XML requires -AllowPlaintextXml.' }
    } catch { throw 'The signing key could not be opened. Use this Windows account and a valid .dpapi key; plaintext XML is opt-in only.' }
    if ($rsa.KeySize -lt 2048) { throw 'Use an RSA key of at least 2048 bits.' }
    if ($rsa.PublicOnly) { throw 'Private key required.' }
    $signature = $rsa.SignData($bytes,[Security.Cryptography.CryptoConfig]::MapNameToOID('SHA256'))
    if (-not $rsa.VerifyData($bytes,[Security.Cryptography.CryptoConfig]::MapNameToOID('SHA256'),$signature)) { throw 'Signature self-check failed.' }
    New-Item -ItemType Directory -Path $out -Force | Out-Null
    foreach ($item in @(@($manifestPath,$bytes),@($signaturePath,$utf8.GetBytes([Convert]::ToBase64String($signature))))) {
        $stream = New-Object IO.FileStream($item[0],[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try { $stream.Write($item[1],0,$item[1].Length); $stream.Flush($true) } finally { $stream.Dispose() }
    }
} finally { if ($plain) { [Array]::Clear($plain,0,$plain.Length) }; $rsa.Dispose() }
Write-Host 'Created update-manifest.json and update-manifest.sig. Upload both beside the Windows archive.'
