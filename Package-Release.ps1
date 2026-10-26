#requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$BuildDirectory,[Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$build = [IO.Path]::GetFullPath($BuildDirectory).TrimEnd('\')
$out = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
if (Test-Path -LiteralPath $out) { throw 'Package destination must not already exist.' }
if ($build -eq $out -or $build.StartsWith($out+'\',[StringComparison]::OrdinalIgnoreCase) -or $out.StartsWith($build+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Use separate build and package directories.' }
$info = Get-Content -LiteralPath (Join-Path $build 'build-info.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($info.Version -ne '0.8.1' -or $info.Edition -ne 'OpenSource') { throw 'Expected a tested LocalLink 0.8.1 build.' }
if ((Get-FileHash -LiteralPath (Join-Path $build 'LocalLink.exe') -Algorithm SHA256).Hash -ne $info.ExecutableSHA256) { throw 'Build checksum mismatch.' }
$binaries = @('LocalLink.exe','LocalLink.exe.config','LocalLink.Check.exe','LocalLink.Check.exe.config','LocalLink.Lists.exe','LocalLink.Lists.exe.config','build-info.json','telegram\LocalLink.Telegram.exe','telegram\LocalLink.Telegram.exe.config','telegram\bridge.py','telegram\README.md')
$documentation = @('README.md','LICENSE','THIRD-PARTY.md','CHANGELOG.md','CONTRIBUTING.md','SECURITY.md','engine.lock.json','docs\PRIVACY.md','docs\RELEASE-0.8.1.md','docs\RELEASE-NOTES-0.8.1.md','docs\images\interface.png','telegram_bridge\README.md','telegram_bridge\python312._pth','tools\Setup-Engine.ps1','tools\Fetch-Engine.ps1','tools\Build-TelegramRuntime.ps1')
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
Get-ChildItem -LiteralPath $out -File -Recurse | ForEach-Object {
    [ordered]@{Path=$_.FullName.Substring($out.Length+1).Replace('\','/');SHA256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
} | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $out 'SHA256.json') -Encoding UTF8
Write-Host "Packaged LocalLink without third-party binaries: $out"
