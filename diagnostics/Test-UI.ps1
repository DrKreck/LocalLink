#requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$BuildDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$build = [IO.Path]::GetFullPath($BuildDirectory)
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$directory = Join-Path $build 'tests'
New-Item -ItemType Directory -Path $directory -Force | Out-Null
$options = @('/nologo','/langversion:5','/platform:x64','/optimize+','/target:exe','/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll',"/r:$build\LocalLink.exe")
Copy-Item -LiteralPath (Join-Path $build 'LocalLink.exe') -Destination $directory -Force
$cases = @(
    @('TypographyLayoutTests','TypographyLayoutTests'),
    @('UsabilityFlowTests','UsabilityFlowTests'),
    @('ControlChromeTests','LocalLink.ControlChromeTests'),
    @('TrayLifecycleTests','TrayLifecycleTests'),
    @('StartupTrayTests','LocalLink.StartupTrayTests'),
    @('PublicBuildTests','PublicBuildTests'),
    @('DependencyUiTests','DependencyUiTests'),
    @('DomainManagerUiTests','DomainManagerUiTests'),
    @('PresetUiTests','PresetUiTests')
)
foreach ($case in $cases) {
    $test = Join-Path $directory ($case[0]+'.exe')
    & $compiler @options ("/main:"+$case[1]) "/out:$test" (Join-Path $PSScriptRoot ($case[0]+'.cs'))
    if ($LASTEXITCODE -ne 0) { throw "UI test compilation failed: $($case[0])" }
    Copy-Item -LiteralPath (Join-Path $root 'LocalLink.exe.config') -Destination ($test+'.config') -Force
    $arguments = @()
    if ($case[0] -eq 'PublicBuildTests') { $arguments = @($build) }
    & $test @arguments 2>&1 | Tee-Object -FilePath (Join-Path $directory ($case[0]+'.log'))
    if ($LASTEXITCODE -ne 0) { throw "UI regression failed: $($case[0])" }
}
$preview = Join-Path $directory 'UsabilityPreview.exe'
& $compiler @options '/main:UsabilityPreview' "/out:$preview" (Join-Path $PSScriptRoot 'UsabilityPreview.cs')
if ($LASTEXITCODE -ne 0) { throw 'Preview compilation failed.' }
Copy-Item -LiteralPath (Join-Path $root 'LocalLink.exe.config') -Destination ($preview+'.config') -Force
& $preview (Join-Path $build 'screenshots')
if ($LASTEXITCODE -ne 0) { throw 'Preview rendering failed.' }
Write-Host 'UI checks and preview rendering completed.'
