#requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$source = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\')
$out = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
if ($out -eq $source -or $out.StartsWith($source+'\',[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $out)) { throw 'Use a fresh fixture directory outside the source.' }
New-Item -ItemType Directory -Path $out | Out-Null
$checks = 0
function Check([bool]$Value,[string]$Message) { if(-not $Value){throw "FAIL: $Message"}; $script:checks++; Write-Host "PASS $Message" }
function Expect-Failure([scriptblock]$Action,[string]$Message) { $failed=$false;try{& $Action | Out-Null}catch{$failed=$true};Check $failed $Message }
$keys = Join-Path $out 'fixture-keys'
$newTool = Join-Path $PSScriptRoot 'New-UpdateSigningKey.ps1'
$signTool = Join-Path $PSScriptRoot 'Sign-AppUpdate.ps1'
$key = & $newTool -KeyDirectory $keys -KeySize 2048
Check ((Test-Path -LiteralPath $key.PrivateKeyPath) -and $key.KeyBits -eq 2048 -and -not $key.Reused) 'isolated encrypted fixture key created'
Check ((Test-Path -LiteralPath $key.PublicKeyPath) -and (Get-Item -LiteralPath $key.PrivateKeyPath).Length -lt 32768) 'bounded encrypted and public key files exist'
$before = (Get-FileHash -LiteralPath $key.PrivateKeyPath -Algorithm SHA256).Hash
$again = & $newTool -KeyDirectory $keys -KeySize 2048
Check ($again.Reused -and $again.PublicKeySha256 -eq $key.PublicKeySha256 -and (Get-FileHash -LiteralPath $key.PrivateKeyPath).Hash -eq $before) 'key provisioning is idempotent and never silently rotates'
$allowed = @([Security.Principal.WindowsIdentity]::GetCurrent().User.Value,'S-1-5-18')
foreach($path in @($keys,$key.PrivateKeyPath,$key.PublicKeyPath)) {
    $acl = Get-Acl -LiteralPath $path
    $rules = @($acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier]))
    Check ($acl.AreAccessRulesProtected -and $rules.Count -eq 2 -and @($rules | Where-Object {$_.IdentityReference.Value -notin $allowed -or $_.AccessControlType -ne 'Allow'}).Count -eq 0) 'fixture key ACL permits only current account and SYSTEM'
}
$publicXml = [IO.File]::ReadAllText($key.PublicKeyPath)
Check ($publicXml.Contains('<Modulus>') -and -not $publicXml.Contains('<D>') -and -not $publicXml.Contains('<P>')) 'public XML contains no private parameters'
Check (@(Get-ChildItem -LiteralPath $keys -File).Count -eq 2 -and @(Get-ChildItem -LiteralPath $keys -Filter '*private*.xml' -File).Count -eq 0) 'no plaintext private key file created'
$public = New-Object Security.Cryptography.RSACryptoServiceProvider
$public.PersistKeyInCsp = $false
$wrong = New-Object Security.Cryptography.RSACryptoServiceProvider(2048)
$wrong.PersistKeyInCsp = $false
try {
    $public.FromXmlString($publicXml)
    Check $public.PublicOnly 'public key can verify but has no signing private material'
    $archive = Join-Path $out 'LocalLink-1.2.3-windows-x64.zip'
    $zip = [IO.Compression.ZipFile]::Open($archive,[IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach($name in @('LocalLink.exe','LocalLink.exe.config','LICENSE','THIRD-PARTY.md')) {
            $entry = $zip.CreateEntry($name)
            $bytes = [Text.Encoding]::UTF8.GetBytes('synthetic fixture, never executed: '+$name)
            $stream = $entry.Open();try{$stream.Write($bytes,0,$bytes.Length)}finally{$stream.Dispose()}
        }
    } finally { $zip.Dispose() }
    $release = Join-Path $out 'signed-release'
    & $signTool -WindowsArchive $archive -Version '1.2.3' -PrivateKeyPath $key.PrivateKeyPath -OutputDirectory $release
    $manifestPath = Join-Path $release 'update-manifest.json'
    $signaturePath = Join-Path $release 'update-manifest.sig'
    $manifestBytes = [IO.File]::ReadAllBytes($manifestPath)
    $signature = [Convert]::FromBase64String([IO.File]::ReadAllText($signaturePath))
    $oid = [Security.Cryptography.CryptoConfig]::MapNameToOID('SHA256')
    Check ($public.VerifyData($manifestBytes,$oid,$signature)) 'DPAPI private key signs exact manifest bytes verified by public key'
    $value = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
    Check ($value.Schema -eq 'locallink-update-v1' -and $value.Version -eq '1.2.3' -and $value.Package -eq 'LocalLink-1.2.3-windows-x64.zip') 'client-compatible manifest schema/version/name'
    Check ($value.Bytes -eq (Get-Item -LiteralPath $archive).Length -and $value.Sha256 -eq (Get-FileHash -LiteralPath $archive).Hash.ToLowerInvariant()) 'manifest binds package byte length and SHA256'
    Check (-not $wrong.VerifyData($manifestBytes,$oid,$signature)) 'different private key cannot impersonate pinned publisher'
    $modified = [byte[]]$manifestBytes.Clone();$modified[0] = $modified[0] -bxor 1
    Check (-not $public.VerifyData($modified,$oid,$signature)) 'modified manifest fails signature verification'
    $badSig = [byte[]]$signature.Clone();$badSig[0] = $badSig[0] -bxor 1
    Check (-not $public.VerifyData($manifestBytes,$oid,$badSig)) 'modified signature fails verification'
    $manifestHash = (Get-FileHash -LiteralPath $manifestPath).Hash
    Expect-Failure { & $signTool -WindowsArchive $archive -Version '1.2.3' -PrivateKeyPath $key.PrivateKeyPath -OutputDirectory $release } 'existing release signature is never overwritten'
    Check ((Get-FileHash -LiteralPath $manifestPath).Hash -eq $manifestHash) 'refused overwrite preserves original signed manifest'
    Expect-Failure { & $signTool -WindowsArchive $archive -Version '9.9.9' -PrivateKeyPath $key.PrivateKeyPath -OutputDirectory (Join-Path $out 'wrong-version') } 'archive/version mismatch rejected'
    Expect-Failure { & $signTool -WindowsArchive $archive -Version '1.2.3' -PrivateKeyPath $key.PublicKeyPath -OutputDirectory (Join-Path $out 'public-only') } 'plaintext key loading requires explicit legacy opt-in'
    Expect-Failure { & $signTool -WindowsArchive $archive -Version '1.2.3' -PrivateKeyPath $key.PublicKeyPath -AllowPlaintextXml -OutputDirectory (Join-Path $out 'public-only-optin') } 'public XML cannot sign even with legacy opt-in'
    Expect-Failure { & $signTool -WindowsArchive $archive -Version '1.2.3' -PrivateKeyPath $signTool -OutputDirectory (Join-Path $out 'source-key') } 'private key path inside repository refused'
    Expect-Failure { & $signTool -WindowsArchive $archive -Version '1.2.3' -PrivateKeyPath $key.PrivateKeyPath -OutputDirectory $keys } 'private key cannot be inside signed release directory'
    $badKey = Join-Path $out 'corrupt.dpapi';[IO.File]::WriteAllBytes($badKey,(New-Object byte[] 400))
    Expect-Failure { & $signTool -WindowsArchive $archive -Version '1.2.3' -PrivateKeyPath $badKey -OutputDirectory (Join-Path $out 'bad-key') } 'unreadable DPAPI key fails closed'
    $largeKey = Join-Path $out 'oversized.dpapi';[IO.File]::WriteAllBytes($largeKey,(New-Object byte[] 32769))
    Expect-Failure { & $signTool -WindowsArchive $archive -Version '1.2.3' -PrivateKeyPath $largeKey -OutputDirectory (Join-Path $out 'large-key') } 'oversized encrypted key rejected before decryption'
    $foreign = Join-Path $out 'foreign-public-key';New-Item -ItemType Directory -Path $foreign | Out-Null
    [IO.File]::WriteAllText((Join-Path $foreign 'update-signing-public.xml'),$wrong.ToXmlString($false))
    Expect-Failure { & $newTool -KeyDirectory $foreign -KeySize 2048 } 'mismatching existing public key is not replaced'
    Check (-not(Test-Path -LiteralPath (Join-Path $foreign 'update-signing-key.dpapi'))) 'public mismatch does not create a new private key'
    [IO.File]::WriteAllBytes($archive,([IO.File]::ReadAllBytes($archive)+[byte]1))
    Check ((Get-FileHash -LiteralPath $archive).Hash.ToLowerInvariant() -ne $value.Sha256) 'archive modified after signing no longer matches signed digest'
    Check (@(Get-ChildItem -LiteralPath $release -File).Count -eq 2) 'signed release contains only public manifest and signature'
    Check ((Get-FileHash -LiteralPath $key.PrivateKeyPath).Hash -eq $before) 'signing never rewrites the fixture private key'
} finally { $public.Dispose();$wrong.Dispose() }
Write-Host "PASS update signing tools: $checks checks; isolated DPAPI fixture only, no owner key, no network or publishing."
