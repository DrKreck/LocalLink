#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$KeyDirectory = '',
    [ValidateSet(2048,3072,4096)][int]$KeySize = 4096
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security
# .NET Framework exposes static File/Directory ACL methods; modern .NET uses
# FileSystemAclExtensions. Resolve the supported API before creating a folder.
$script:UseAclExtensions = -not [bool]([IO.Directory].GetMethods() | Where-Object Name -eq 'SetAccessControl')
if ($script:UseAclExtensions) { Add-Type -AssemblyName System.IO.FileSystem.AccessControl }
if (-not $KeyDirectory) { $KeyDirectory = Join-Path $env:LOCALAPPDATA 'LocalLink-Maintainer' }
$source = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\')
$utf8 = New-Object Text.UTF8Encoding($false,$true)
$entropy = $utf8.GetBytes('LocalLink/update-signing/v1')

function Assert-LocalPath([string]$Path) {
    if ($Path -notmatch '^[A-Za-z]:[\\/]' -or $Path.StartsWith('\\') -or $Path.IndexOf(':',2) -ge 0) { throw 'Use an ordinary local key directory.' }
    $absolute = [IO.Path]::GetFullPath($Path).TrimEnd('\','/')
    if ($absolute.Length -lt 4) { throw 'Use a dedicated key directory, not a volume root.' }
    $cursor = $absolute
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            if (((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Reparse points are not allowed in the key path.' }
        }
        $cursor = Split-Path -Parent $cursor
    }
    return $absolute
}
function Set-KeyDirectoryAcl([string]$Path) {
    $security = New-Object Security.AccessControl.DirectorySecurity
    $security.SetAccessRuleProtection($true,$false)
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $security.SetOwner($sid)
    foreach ($identity in @($sid,(New-Object Security.Principal.SecurityIdentifier('S-1-5-18')))) {
        $rule = New-Object Security.AccessControl.FileSystemAccessRule($identity,[Security.AccessControl.FileSystemRights]::FullControl,
            ([Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [Security.AccessControl.InheritanceFlags]::ObjectInherit),
            [Security.AccessControl.PropagationFlags]::None,[Security.AccessControl.AccessControlType]::Allow)
        $security.AddAccessRule($rule)
    }
    if ($script:UseAclExtensions) { [IO.FileSystemAclExtensions]::SetAccessControl((New-Object IO.DirectoryInfo($Path)),$security) }
    else { [IO.Directory]::SetAccessControl($Path,$security) }
}
function Set-KeyFileAcl([string]$Path) {
    $security = New-Object Security.AccessControl.FileSecurity
    $security.SetAccessRuleProtection($true,$false)
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $security.SetOwner($sid)
    foreach ($identity in @($sid,(New-Object Security.Principal.SecurityIdentifier('S-1-5-18')))) {
        $security.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($identity,[Security.AccessControl.FileSystemRights]::FullControl,[Security.AccessControl.AccessControlType]::Allow)))
    }
    if ($script:UseAclExtensions) { [IO.FileSystemAclExtensions]::SetAccessControl((New-Object IO.FileInfo($Path)),$security) }
    else { [IO.File]::SetAccessControl($Path,$security) }
}
function Write-New([string]$Path,[byte[]]$Bytes) {
    $null = Assert-LocalPath $Path
    $stream = New-Object IO.FileStream($Path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try { $stream.Write($Bytes,0,$Bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
    Set-KeyFileAcl $Path
}

$directory = Assert-LocalPath $KeyDirectory
if ($directory -eq $source -or $directory.StartsWith($source+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Keep the signing key outside the source/repository.' }
$keyPath = Join-Path $directory 'update-signing-key.dpapi'
$publicPath = Join-Path $directory 'update-signing-public.xml'
if ((Test-Path -LiteralPath $keyPath -PathType Container) -or (Test-Path -LiteralPath $publicPath -PathType Container)) { throw 'A key path is a directory.' }
$reused = Test-Path -LiteralPath $keyPath -PathType Leaf
$rsa = New-Object Security.Cryptography.RSACryptoServiceProvider($KeySize)
$rsa.PersistKeyInCsp = $false
$plain = $null
try {
    if ($reused) {
        $length = (Get-Item -LiteralPath $keyPath).Length
        if ($length -lt 1 -or $length -gt 32768) { throw 'The encrypted key file has an invalid size. It was not replaced.' }
        try {
            $protected = [IO.File]::ReadAllBytes($keyPath)
            $plain = [Security.Cryptography.ProtectedData]::Unprotect($protected,$entropy,[Security.Cryptography.DataProtectionScope]::CurrentUser)
            $rsa.FromXmlString($utf8.GetString($plain))
        } catch { throw 'The existing key cannot be opened by this Windows account. It was not replaced.' }
        if ($rsa.PublicOnly -or $rsa.KeySize -lt $KeySize) { throw 'The existing key is not a suitable private key. It was not replaced or rotated.' }
    }
    $publicXml = $rsa.ToXmlString($false)
    $publicBytes = $utf8.GetBytes($publicXml)
    if (Test-Path -LiteralPath $publicPath -PathType Leaf) {
        if ((Get-Item -LiteralPath $publicPath).Length -gt 16384 -or [IO.File]::ReadAllText($publicPath,$utf8) -cne $publicXml) { throw 'The public key does not match the private key. Nothing was rotated.' }
    }
    # Verify the public/private pair before persisting anything. The challenge
    # and private XML exist in memory only; they are never sent or printed.
    $challenge = New-Object byte[] 32
    $random = New-Object Security.Cryptography.RNGCryptoServiceProvider
    try { $random.GetBytes($challenge) } finally { $random.Dispose() }
    $signature = $rsa.SignData($challenge,[Security.Cryptography.CryptoConfig]::MapNameToOID('SHA256'))
    $verifier = New-Object Security.Cryptography.RSACryptoServiceProvider
    $verifier.PersistKeyInCsp = $false
    try {
        $verifier.FromXmlString($publicXml)
        if (-not $verifier.VerifyData($challenge,[Security.Cryptography.CryptoConfig]::MapNameToOID('SHA256'),$signature)) { throw 'Signing-key self-check failed.' }
    } finally { $verifier.Dispose() }
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $null = Assert-LocalPath $directory
    Set-KeyDirectoryAcl $directory
    if (-not $reused) {
        $plain = $utf8.GetBytes($rsa.ToXmlString($true))
        $protected = [Security.Cryptography.ProtectedData]::Protect($plain,$entropy,[Security.Cryptography.DataProtectionScope]::CurrentUser)
        Write-New $keyPath $protected
    } else { Set-KeyFileAcl $keyPath }
    if (-not (Test-Path -LiteralPath $publicPath)) { Write-New $publicPath $publicBytes }
    else { Set-KeyFileAcl $publicPath }
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $fingerprint = [BitConverter]::ToString($sha.ComputeHash($publicBytes)).Replace('-','').ToLowerInvariant() } finally { $sha.Dispose() }
    [pscustomobject]@{KeyDirectory=$directory;PrivateKeyPath=$keyPath;PublicKeyPath=$publicPath;PublicKeySha256=$fingerprint;KeyBits=$rsa.KeySize;Reused=[bool]$reused}
} finally {
    if ($plain) { [Array]::Clear($plain,0,$plain.Length) }
    $rsa.Dispose()
}
