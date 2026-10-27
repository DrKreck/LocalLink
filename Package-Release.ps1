#requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$BuildDirectory,[Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$build = [IO.Path]::GetFullPath($BuildDirectory).TrimEnd('\')
$out = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
if (Test-Path -LiteralPath $out) { throw 'Package destination must not already exist.' }
if ($build -eq $out -or $build.StartsWith($out+'\',[StringComparison]::OrdinalIgnoreCase) -or $out.StartsWith($build+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Use separate build and package directories.' }
$info = Get-Content -LiteralPath (Join-Path $build 'build-info.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($info.Version -ne '0.8.3' -or $info.Edition -ne 'OpenSource') { throw 'Expected a tested LocalLink 0.8.3 build.' }
if ((Get-FileHash -LiteralPath (Join-Path $build 'LocalLink.exe') -Algorithm SHA256).Hash -ne $info.ExecutableSHA256) { throw 'Build checksum mismatch.' }
if ([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $build 'LocalLink.exe')).FileVersion -ne '0.8.3.0') { throw 'Executable version does not match release 0.8.3.' }
$binaries = @('LocalLink.exe','LocalLink.exe.config')
$documentation = @('LICENSE','THIRD-PARTY.md')
foreach ($file in $binaries) { if (-not (Test-Path -LiteralPath (Join-Path $build $file) -PathType Leaf)) { throw "Missing build file: $file" } }
foreach ($file in $documentation) { if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot $file) -PathType Leaf)) { throw "Missing documentation: $file" } }
New-Item -ItemType Directory -Path $out | Out-Null
foreach ($file in $binaries) {
    $target = Join-Path $out $file
    New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $build $file) -Destination $target
}
foreach ($file in $documentation) {
    $target = Join-Path $out $file
    New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $target
}
if (@(Get-ChildItem -LiteralPath $out -File -Recurse).Count -ne 4) { throw 'Unexpected files in the Windows package.' }
Write-Host "Packaged LocalLink (4 files): $out"
