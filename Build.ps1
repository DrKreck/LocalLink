#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$OutputDirectory = '',
    [string]$EngineDirectory = '',
    [string]$TelegramRuntimeDirectory = ''
)
$ErrorActionPreference = 'Stop'
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot 'artifacts\build' }
if (-not $EngineDirectory) { $EngineDirectory = Join-Path $PSScriptRoot 'vendor\engine' }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework x64 compiler not found.' }
$out = [IO.Path]::GetFullPath($OutputDirectory)
$source = [IO.Path]::GetFullPath($PSScriptRoot)
if ($out.TrimEnd('\') -eq $source.TrimEnd('\') -or $source.StartsWith($out.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Choose a separate build directory.' }
if (@(Get-CimInstance Win32_Process -Filter "Name LIKE 'LocalLink%'" | Where-Object {
    $_.ExecutablePath -and $_.ExecutablePath.StartsWith($out.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)
}).Count) { throw 'Close the application in the build directory first.' }
$lockPath = Join-Path $PSScriptRoot 'engine.lock.json'
$lock = Get-Content -LiteralPath $lockPath -Raw -Encoding UTF8 | ConvertFrom-Json
$engine = [IO.Path]::GetFullPath($EngineDirectory)
foreach ($entry in $lock.files) {
    if ($entry.path -notmatch '^vendor/engine/[A-Za-z0-9_./-]+$' -or $entry.path -match '(^|/)\.\.?(/|$)') { throw 'Unsafe engine lock path.' }
    $relative = $entry.path.Substring('vendor/engine/'.Length)
    $file = Join-Path $engine $relative
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw 'Engine files are missing. Run tools\Setup-Engine.ps1 first.' }
    if ((Get-Item -LiteralPath $file).Length -ne [long]$entry.size -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.sha256) { throw "Engine checksum mismatch: $relative" }
}
New-Item -ItemType Directory -Path $out -Force | Out-Null
foreach ($entry in $lock.files) {
    $relative = $entry.path.Substring('vendor/engine/'.Length)
    $target = Join-Path (Join-Path $out 'engine') $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $engine $relative) -Destination $target -Force
}
$sources = @('Engine.cs','ProfileEvidence.cs','Profiles.cs','Network.cs','Services.cs','AppSettings.cs','Tuning.cs','TuningReport.cs','PublicLists.cs','StartupRegistration.cs','DiagnosticHistory.cs','BugReport.cs','ContactLink.cs','VersionInfo.cs','diagnostics\DiscordDesktopDiagnostics.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$references = @('/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Net.Http.dll','/r:System.Web.Extensions.dll','/r:System.Security.dll','/r:System.Management.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll')
$options = @('/nologo','/langversion:5','/platform:x64','/optimize+',"/resource:$lockPath,LocalLink.EngineManifest") + $references
& $compiler @options '/target:winexe' "/win32manifest:$PSScriptRoot\app.manifest" "/out:$out\LocalLink.exe" @sources "$PSScriptRoot\Theme.cs" "$PSScriptRoot\Program.cs"
if ($LASTEXITCODE -ne 0) { throw 'GUI compilation failed.' }
foreach ($tool in @(@('LocalLink.Check','LocalLink.DiagnosticsCli','DiagnosticsCli.cs'),@('LocalLink.Lists','LocalLink.ListsCli','ListsCli.cs'))) {
    & $compiler @options '/target:exe' ("/main:"+$tool[1]) ("/out:"+(Join-Path $out ($tool[0]+'.exe'))) @sources (Join-Path $PSScriptRoot $tool[2])
    if ($LASTEXITCODE -ne 0) { throw "Tool compilation failed: $($tool[0])" }
}
$telegram = Join-Path $out 'telegram'
New-Item -ItemType Directory -Path $telegram -Force | Out-Null
& $compiler @options '/target:winexe' "/out:$telegram\LocalLink.Telegram.exe" "$PSScriptRoot\telegram_bridge\Launcher.cs" "$PSScriptRoot\VersionInfo.cs"
if ($LASTEXITCODE -ne 0) { throw 'Telegram launcher compilation failed.' }
foreach ($name in @('bridge.py','README.md')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot ('telegram_bridge\'+$name)) -Destination $telegram -Force }
if ($TelegramRuntimeDirectory) {
    $runtimeSource = [IO.Path]::GetFullPath($TelegramRuntimeDirectory)
    $runtimeLock = Get-Content -LiteralPath (Join-Path $runtimeSource 'runtime-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $runtimeOut = Join-Path $telegram 'runtime'
    foreach ($entry in $runtimeLock.Files) {
        if ($entry.Path -notmatch '^[A-Za-z0-9_./-]+$' -or $entry.Path -match '(^|/)\.\.?(/|$)') { throw 'Unsafe runtime manifest path.' }
        $file = Join-Path $runtimeSource $entry.Path
        if ((Get-Item -LiteralPath $file).Length -ne $entry.Bytes -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.SHA256) { throw 'Runtime checksum mismatch.' }
    }
    foreach ($entry in $runtimeLock.Files) {
        $target = Join-Path $runtimeOut $entry.Path
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $runtimeSource $entry.Path) -Destination $target -Force
    }
    Copy-Item -LiteralPath (Join-Path $runtimeSource 'runtime-manifest.json') -Destination $runtimeOut -Force
}
foreach ($exe in @('LocalLink.exe','LocalLink.Check.exe','LocalLink.Lists.exe','telegram\LocalLink.Telegram.exe')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LocalLink.exe.config') -Destination (Join-Path $out ($exe+'.config')) -Force }
foreach ($name in @('engine.lock.json','README.md','LICENSE','THIRD-PARTY.md','CHANGELOG.md')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $out -Force }
$testDirectory = Join-Path $out 'tests'
New-Item -ItemType Directory -Path $testDirectory -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $out 'engine') -Destination $testDirectory -Recurse -Force
$lists = Join-Path $testDirectory 'lists'
New-Item -ItemType Directory -Path $lists -Force | Out-Null
$fixture = Join-Path $lists 'sites-fixture.lst'
[IO.File]::WriteAllText($fixture,("example.com"+[char]10+"example.org"+[char]10),(New-Object Text.UTF8Encoding($false)))
@{FileName='sites-fixture.lst';Sha256=(Get-FileHash -LiteralPath $fixture -Algorithm SHA256).Hash.ToLowerInvariant();DomainCount=2;SourceCount=0;Sources=@();RejectedLines=0;UpdatedUtc='2026-10-24T00:00:00Z'} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $lists 'current.json') -Encoding UTF8
Copy-Item -LiteralPath (Join-Path $lists 'current.json') -Destination ($fixture+'.json') -Force
$cases = @(
    @('LocalLink.Tests','LocalLink.TestMain',@('ProfileTests.cs','ServiceTests.cs','PublicListTests.cs','TestMain.cs')),
    @('LocalLink.TlsCoverage.Tests','LocalLink.TlsCoverageTests',@('diagnostics\TlsCoverageTests.cs')),
    @('LocalLink.TuningSelection.Tests','LocalLink.TuningSelectionTests',@('diagnostics\TuningSelectionTests.cs')),
    @('LocalLink.TuningReport.Tests','TuningReportTests',@('diagnostics\TuningReportTests.cs')),
    @('LocalLink.ProfileEvidence.Tests','LocalLink.ProfileEvidenceTests',@('diagnostics\ProfileEvidenceTests.cs')),
    @('LocalLink.StartupRegistration.Tests','LocalLink.StartupRegistrationTests',@('diagnostics\StartupRegistrationTests.cs')),
    @('LocalLink.HostListPath.Tests','LocalLink.HostListPathTests',@('diagnostics\HostListPathTests.cs')),
    @('LocalLink.DiscordScope.Tests','LocalLink.DiscordScopeTests',@('diagnostics\DiscordScopeTests.cs')),
    @('LocalLink.DiscordDesktop.Tests','DiscordDesktopDiagnosticsTests',@('diagnostics\DiscordDesktopDiagnosticsTests.cs')),
    @('LocalLink.DiscordTuning.Tests','LocalLink.DiscordTuningTests',@('diagnostics\DiscordTuningTests.cs')),
    @('LocalLink.DiscordReport.Tests','DiscordReportTests',@('diagnostics\DiscordReportTests.cs')),
    @('LocalLink.VerifiedListCount.Tests','LocalLink.VerifiedListCountTests',@('diagnostics\VerifiedListCountTests.cs')),
    @('LocalLink.SettingsStorage.Tests','SettingsStorageTests',@('diagnostics\SettingsStorageTests.cs')),
    @('LocalLink.EnginePreflight.Tests','LocalLink.EnginePreflightTests',@('diagnostics\EnginePreflightTests.cs')),
    @('LocalLink.PublicListSave.Tests','LocalLink.PublicListSaveTests',@('diagnostics\PublicListSaveTests.cs')),
    @('LocalLink.BugReport.Tests','BugReportTests',@('diagnostics\BugReportTests.cs')),
    @('LocalLink.ContactLink.Tests','ContactLinkTests',@('diagnostics\ContactLinkTests.cs'))
)
foreach ($case in $cases) {
    $test = Join-Path $testDirectory ($case[0]+'.exe')
    $testSources = @($case[2] | ForEach-Object { Join-Path $PSScriptRoot $_ })
    & $compiler @options '/target:exe' ("/main:"+$case[1]) "/out:$test" @sources @testSources
    if ($LASTEXITCODE -ne 0) { throw "Test compilation failed: $($case[0])" }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LocalLink.exe.config') -Destination ($test+'.config') -Force
    & $test 2>&1 | Tee-Object -FilePath (Join-Path $testDirectory ($case[0]+'.log'))
    if ($LASTEXITCODE -ne 0) { throw "Offline regression failed: $($case[0])" }
}
$launcher = Start-Process -FilePath (Join-Path $telegram 'LocalLink.Telegram.exe') -ArgumentList '--self-test' -WindowStyle Hidden -Wait -PassThru
if ($launcher.ExitCode -ne 0) { throw 'Telegram self-test failed.' }
@{Version='0.8.1';Edition='OpenSource';ExecutableSHA256=(Get-FileHash -LiteralPath (Join-Path $out 'LocalLink.exe') -Algorithm SHA256).Hash;OfflineTestExecutables=$cases.Count} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $out 'build-info.json') -Encoding UTF8
Write-Host "Built: $out\LocalLink.exe"
