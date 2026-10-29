#requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$OutputDirectory,[switch]$LiveDesktop)
$ErrorActionPreference='Stop'
$source=Split-Path -Parent $PSScriptRoot
$out=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $out){throw 'Use a fresh isolated fixture directory.'}
if($out.StartsWith([IO.Path]::GetFullPath($source).TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Fixture must stay outside source.'}
New-Item -ItemType Directory -Path $out | Out-Null
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$references=@('/r:System.dll','/r:System.Core.dll','/r:System.Security.dll','/r:System.Windows.Forms.dll')
$common=@((Join-Path $source 'DesktopProcess.cs'),(Join-Path $source 'diagnostics\DesktopProcessTests.cs'))
$test=Join-Path $out 'DesktopProcess.Tests.exe'
& $compiler /nologo /langversion:5 /platform:x64 /target:exe /main:LocalLink.DesktopProcessTests @references "/out:$test" @common
if($LASTEXITCODE-ne 0){throw 'Desktop process tests compilation failed.'}
& $test
if($LASTEXITCODE-ne 0){throw 'Desktop process offline tests failed.'}
if($LiveDesktop){
    $fixture=Join-Path $out 'live';New-Item -ItemType Directory -Path $fixture | Out-Null
    & $compiler /nologo /langversion:5 /platform:x64 /target:winexe /main:LocalLink.DesktopProcessFixture @references ("/out:"+(Join-Path $fixture 'LocalLink.UpdateHelper.exe')) @common (Join-Path $PSScriptRoot 'DesktopProcessFixture.cs')
    if($LASTEXITCODE-ne 0){throw 'Desktop broker fixture compilation failed.'}
    [IO.File]::WriteAllText((Join-Path $fixture 'plan.json'),'{}',(New-Object Text.UTF8Encoding($false)))
    & $test '--launch-fixture' $fixture
    if($LASTEXITCODE-ne 0){throw 'Desktop broker launch fixture failed.'}
}
