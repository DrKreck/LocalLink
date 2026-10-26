#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$OutputDirectory = '',
    [switch]$VerifyOnly
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) {
    $OutputDirectory = if (Test-Path -LiteralPath (Join-Path $root 'LocalLink.exe')) { Join-Path $root 'engine' } else { Join-Path $root 'vendor\engine' }
}
& (Join-Path $PSScriptRoot 'Fetch-Engine.ps1') -ProjectRoot $root -EngineDirectory $OutputDirectory -VerifyOnly:$VerifyOnly
