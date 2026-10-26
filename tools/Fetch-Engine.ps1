#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$ProjectRoot = '',
    [string]$EngineDirectory = '',
    [switch]$VerifyOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $ProjectRoot) { $ProjectRoot = Split-Path -Parent $PSScriptRoot }

# Download/verify only: never executes an engine, installs a driver, changes
# services, relaxes execution policy, disables security, or deletes a file.
function Assert-NoReparsePoint([string]$Path) {
    $cursor = $Path
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Reparse points are not accepted: $cursor"
            }
        }
        $parent = Split-Path -Path $cursor -Parent
        if ($parent -eq $cursor) { break }
        $cursor = $parent
    }
}

function Get-GitBlobHash([string]$Path) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    $header = [Text.Encoding]::ASCII.GetBytes("blob $($bytes.Length)`0")
    $data = New-Object byte[] ($header.Length + $bytes.Length)
    [Array]::Copy($header, 0, $data, 0, $header.Length)
    [Array]::Copy($bytes, 0, $data, $header.Length, $bytes.Length)
    $hasher = [Security.Cryptography.SHA1]::Create()
    try { return ([BitConverter]::ToString($hasher.ComputeHash($data))).Replace('-', '').ToLowerInvariant() }
    finally { $hasher.Dispose() }
}

function Assert-LockedFile([string]$Path, $Entry) {
    Assert-NoReparsePoint $Path
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Missing locked file: $($Entry.path)" }
    if ((Get-Item -LiteralPath $Path).Length -ne [long]$Entry.size) { throw "Size mismatch; file left untouched: $($Entry.path)" }
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $Entry.sha256) {
        throw "SHA256 mismatch; file left untouched: $($Entry.path)"
    }
    if ((Get-GitBlobHash $Path) -ne $Entry.gitBlobSha1) { throw "Git blob hash mismatch; file left untouched: $($Entry.path)" }
}

function Assert-DriverSignature([string]$Path, $Policy) {
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ([string]$signature.Status -ne $Policy.requiredStatus -or
        $null -eq $signature.SignerCertificate -or
        $signature.SignerCertificate.Thumbprint -ne $Policy.signerThumbprint) {
        throw "Driver signature rejected: status=$($signature.Status). Do not disable antivirus, signature enforcement, or Secure Boot."
    }
    Write-Host "Driver Authenticode: $($signature.Status); signer: $($signature.SignerCertificate.Subject)"
}

try {
    if ($env:OS -ne 'Windows_NT') { throw 'This verifier requires Windows Authenticode support.' }
    $root = [IO.Path]::GetFullPath($ProjectRoot).TrimEnd('\', '/')
    if (-not $EngineDirectory) { $EngineDirectory = Join-Path $root 'vendor\engine' }
    $engineRoot = [IO.Path]::GetFullPath($EngineDirectory).TrimEnd('\', '/')
    Assert-NoReparsePoint $engineRoot
    Assert-NoReparsePoint $root
    if (-not (Test-Path -LiteralPath $root -PathType Container)) { throw "Project root does not exist: $root" }
    $manifest = Join-Path $root 'engine.lock.json'
    Assert-NoReparsePoint $manifest
    $lock = Get-Content -LiteralPath $manifest -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($lock.schemaVersion -ne 1) { throw 'Unsupported engine lock schema.' }
    if ($lock.upstream.repository -ne 'bol-van/zapret-win-bundle' -or
        $lock.upstream.commit -ne '6eb463a6758fb48cd101bc55dfd057e6e9d98af1') { throw 'Unexpected engine source pin.' }
    if ($lock.signaturePolicy.path -ne 'vendor/engine/WinDivert64.sys' -or
        $lock.signaturePolicy.requiredStatus -ne 'Valid' -or
        $lock.signaturePolicy.signerThumbprint -ne '043589F75FCE2795E7F2CC3E526D46784D5DDAB3') { throw 'Unexpected driver signature policy.' }

    $sourcePins = @{
        'bol-van/zapret-win-bundle' = '6eb463a6758fb48cd101bc55dfd057e6e9d98af1'
        'bol-van/zapret2' = '00f5aaa36de500971faf6db440dd7d8b25672281'
        'basil00/WinDivert' = '1789526ecfb9ff5397c94f9f54c1a3dc2fb60440'
        'cygwin/cygwin' = '89a1e4a573d40b233011402f42630b970ff71291'
    }
    $seen = @{}
    # Validate the entire manifest and every existing destination before writes.
    foreach ($entry in $lock.files) {
        if ($entry.path -notmatch '^vendor/engine/[A-Za-z0-9_.\-/]+$' -or
            $entry.path -match '(^|/)\.\.?(/|$)' -or $entry.path.Contains('//')) { throw 'Unsafe manifest path.' }
        if ($seen.ContainsKey($entry.path)) { throw "Duplicate manifest path: $($entry.path)" }
        $seen[$entry.path] = $true
        if (-not $sourcePins.ContainsKey($entry.upstreamRepository) -or
            $entry.upstreamCommit -ne $sourcePins[$entry.upstreamRepository]) { throw 'Unapproved source pin.' }
        if ($entry.upstreamPath -notmatch '^[A-Za-z0-9_.\-/]+$' -or
            $entry.upstreamPath -match '(^|/)\.\.?(/|$)') { throw 'Unsafe upstream path.' }
        $expectedUrl = "https://raw.githubusercontent.com/$($entry.upstreamRepository)/$($entry.upstreamCommit)/$($entry.upstreamPath)"
        if ($entry.url -cne $expectedUrl) { throw 'Download URL must match the pinned official raw source.' }
        if ($entry.sha256 -notmatch '^[a-f0-9]{64}$' -or $entry.gitBlobSha1 -notmatch '^[a-f0-9]{40}$' -or
            [long]$entry.size -le 0 -or [long]$entry.size -gt 16777216) { throw 'Invalid locked hash or size.' }
        $destination = [IO.Path]::GetFullPath((Join-Path $engineRoot $entry.path.Substring('vendor/engine/'.Length)))
        if (-not $destination.StartsWith($engineRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Destination escapes engine directory.' }
        Assert-NoReparsePoint $destination
        if (Test-Path -LiteralPath $destination) { Assert-LockedFile $destination $entry }
        elseif ($VerifyOnly) { throw "Missing locked file: $($entry.path)" }
    }
    foreach ($required in @('vendor/engine/winws2.exe', 'vendor/engine/WinDivert.dll',
        'vendor/engine/WinDivert64.sys', 'vendor/engine/cygwin1.dll',
        'vendor/engine/lua/zapret-lib.lua', 'vendor/engine/lua/zapret-antidpi.lua')) {
        if (-not $seen.ContainsKey($required)) { throw "Missing required manifest entry: $required" }
    }

    foreach ($entry in $lock.files) {
        $destination = Join-Path $engineRoot $entry.path.Substring('vendor/engine/'.Length)
        if (-not (Test-Path -LiteralPath $destination)) {
            $directory = Split-Path -Parent $destination
            $null = New-Item -ItemType Directory -Force -Path $directory
            Assert-NoReparsePoint $directory
            # Keep the original extension so Authenticode identifies the driver.
            $stageName = '.download-' + [Guid]::NewGuid().ToString('N') + [IO.Path]::GetExtension($destination)
            $staged = Join-Path $directory $stageName
            if (Test-Path -LiteralPath $staged) { throw 'Unexpected staging collision.' }
            Write-Host "Downloading pinned file: $($entry.path)"
            Invoke-WebRequest -Uri $entry.url -UseBasicParsing -MaximumRedirection 0 -OutFile $staged -ErrorAction Stop
            Assert-LockedFile $staged $entry
            if ($entry.path -eq $lock.signaturePolicy.path) { Assert-DriverSignature $staged $lock.signaturePolicy }
            # Two-argument File.Move refuses to overwrite an existing destination.
            [IO.File]::Move($staged, $destination)
        }
        Assert-LockedFile $destination $entry
        Write-Host "Verified SHA256 and Git blob: $($entry.path)"
    }
    Assert-DriverSignature (Join-Path $engineRoot 'WinDivert64.sys') $lock.signaturePolicy
    Write-Host "Verified $(@($lock.files).Count) locked files. No engine or driver was executed."
    Write-Host 'Provenance and a valid signature are not a malware-free guarantee.'
}
catch {
    Write-Error ("Engine preparation failed: " + $_.Exception.Message +
        ' No security settings were changed. Existing mismatched files are never overwritten. Any partial download is left in place for inspection.') -ErrorAction Continue
    exit 1
}
