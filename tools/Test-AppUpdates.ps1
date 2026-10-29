#requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$source = Split-Path -Parent $PSScriptRoot
$out = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
if (Test-Path -LiteralPath $out) { throw 'Use a fresh isolated fixture directory.' }
if ($out.StartsWith([IO.Path]::GetFullPath($source).TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Keep fixtures outside source.' }
New-Item -ItemType Directory -Path $out | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$references = @('/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Net.Http.dll','/r:System.Web.Extensions.dll','/r:System.Security.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll')
$helper = Join-Path $out 'fixture-helper.exe'
& $compiler /nologo /langversion:5 /platform:x64 /target:winexe /define:LOCALLINK_UPDATER_TESTS @references "/out:$helper" (Join-Path $source 'AppUpdates.cs') (Join-Path $source 'DesktopProcess.cs') (Join-Path $source 'UpdateHelper.cs')
if ($LASTEXITCODE -ne 0) { throw 'Fixture helper compilation failed.' }
$resources = @("/resource:$helper,LocalLink.UpdateHelper",("/resource:"+(Join-Path $source 'LocalLink.exe.config')+',LocalLink.UpdateHelperConfig'))
foreach ($variant in @('old','healthy','bad')) {
    $directory = Join-Path $out $variant
    New-Item -ItemType Directory -Path $directory | Out-Null
    $defines = 'LOCALLINK_UPDATER_TESTS'
    if ($variant -eq 'old') { $defines += ';OLD_UPDATE_FIXTURE' }
    if ($variant -eq 'bad') { $defines += ';BAD_UPDATE_FIXTURE' }
    & $compiler /nologo /langversion:5 /platform:x64 /target:winexe ("/define:$defines") @references @resources "/out:$directory\LocalLink.exe" (Join-Path $source 'AppUpdates.cs') (Join-Path $source 'DesktopProcess.cs') (Join-Path $PSScriptRoot 'AppUpdateFixture.cs')
    if ($LASTEXITCODE -ne 0) { throw "Fixture app compilation failed: $variant" }
    Copy-Item -LiteralPath (Join-Path $source 'LocalLink.exe.config') -Destination (Join-Path $directory 'LocalLink.exe.config')
    [IO.File]::WriteAllText((Join-Path $directory 'LICENSE'),"$variant fixture license")
    [IO.File]::WriteAllText((Join-Path $directory 'THIRD-PARTY.md'),"$variant fixture attribution")
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$checks = 0
function Check([bool]$passed,[string]$message) { if(-not $passed){throw "FAIL: $message"}; $script:checks++; Write-Host "PASS $message" }
function Fixture-Process([string]$file,[string]$arguments,[string]$root,[bool]$interrupt=$false) {
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName=$file; $start.Arguments=$arguments; $start.WorkingDirectory=Split-Path -Parent $file
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true; $start.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
    $start.EnvironmentVariables['LOCALLINK_UPDATE_FIXTURE_ROOT']=$root
    $start.EnvironmentVariables['LOCALLINK_UPDATE_TEST_INTERRUPT']=$(if($interrupt){'1'}else{'0'})
    return [Diagnostics.Process]::Start($start)
}
function Wait-Fixture([string]$file,[int]$seconds=65) {
    $timer=[Diagnostics.Stopwatch]::StartNew()
    while(-not(Test-Path -LiteralPath $file)) { if($timer.Elapsed.TotalSeconds -gt $seconds){throw "Fixture timed out: $file"}; Start-Sleep -Milliseconds 200 }
}
function Close-Fixture([string]$directory) {
    $absolute=[IO.Path]::GetFullPath($directory).TrimEnd('\')+'\'
    if(-not $absolute.StartsWith($out+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Fixture cleanup target escaped output.'}
    $running=@(Get-CimInstance Win32_Process -Filter "Name LIKE 'LocalLink%'" | Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($absolute,[StringComparison]::OrdinalIgnoreCase) })
    foreach($item in $running) {
        $process=Get-Process -Id $item.ProcessId -ErrorAction SilentlyContinue
        if($process) { $null=$process.CloseMainWindow(); if(-not $process.WaitForExit(5000)){ $process.Kill(); $null=$process.WaitForExit(5000) } }
    }
}
function Create-Case([string]$name,[string]$variant,[string]$initial='old') {
    $case=Join-Path $out $name; New-Item -ItemType Directory -Path $case | Out-Null
    $target=Join-Path $case 'installed'; Copy-Item -LiteralPath (Join-Path $out $initial) -Destination $target -Recurse
    [IO.File]::WriteAllText((Join-Path $target 'user-settings.json'),'untouched')
    $root=Join-Path $case 'updates'; New-Item -ItemType Directory -Path $root | Out-Null
    $transaction=Join-Path $root ([Guid]::NewGuid().ToString('N')); New-Item -ItemType Directory -Path $transaction | Out-Null
    $payload=Join-Path $transaction 'payload'; Copy-Item -LiteralPath (Join-Path $out $variant) -Destination $payload -Recurse
    $archive=Join-Path $transaction 'package.zip'; [IO.Compression.ZipFile]::CreateFromDirectory($payload,$archive)
    return @{Case=$case;Target=$target;Root=$root;Transaction=$transaction;Hash=(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()}
}
try {
    $success=Create-Case 'success-case' 'healthy'
    $process=Fixture-Process (Join-Path $success.Target 'LocalLink.exe') ('--fixture-update "'+$success.Transaction+'" '+$success.Hash) $success.Root
    Check ($process.WaitForExit(15000) -and $process.ExitCode -eq 0) 'old app gracefully hands off updater'
    Wait-Fixture (Join-Path $success.Transaction 'status.committed')
    Check ((Get-FileHash -LiteralPath (Join-Path $success.Target 'LocalLink.exe')).Hash -eq (Get-FileHash -LiteralPath (Join-Path $out 'healthy\LocalLink.exe')).Hash) 'helper installs exact new executable and receives health acknowledgment'
    Check ((Get-Content -LiteralPath (Join-Path $success.Target 'user-settings.json') -Raw) -eq 'untouched') 'settings survive real helper upgrade'
    Check (Test-Path -LiteralPath (Join-Path $success.Root 'last-update.json')) 'only successful update creates rollback pointer'
    Close-Fixture $success.Case
    $rollback=Fixture-Process (Join-Path $success.Target 'LocalLink.exe') '--fixture-rollback' $success.Root
    Check ($rollback.WaitForExit(15000) -and $rollback.ExitCode -eq 0) 'new app hands off user-requested rollback'
    $rollbackTimer=[Diagnostics.Stopwatch]::StartNew()
    do { $rollbackPlans=@(Get-ChildItem -LiteralPath $success.Root -Directory | Where-Object {Test-Path -LiteralPath (Join-Path $_.FullName 'status.committed')}); if($rollbackPlans.Count -ge 2){break}; if($rollbackTimer.Elapsed.TotalSeconds -gt 65){throw 'Rollback helper timed out'}; Start-Sleep -Milliseconds 200 } while($true)
    Check ((Get-FileHash -LiteralPath (Join-Path $success.Target 'LocalLink.exe')).Hash -eq (Get-FileHash -LiteralPath (Join-Path $out 'old\LocalLink.exe')).Hash) 'manual rollback restores exact prior executable and legacy visible-window startup'
    Close-Fixture $success.Case

    $modern=Create-Case 'modern-rollback-case' 'healthy' 'healthy'
    $modernBefore='Modern previous fixture license; restore this exact content.'
    [IO.File]::WriteAllText((Join-Path $modern.Target 'LICENSE'),$modernBefore)
    $process=Fixture-Process (Join-Path $modern.Target 'LocalLink.exe') ('--fixture-update "'+$modern.Transaction+'" '+$modern.Hash) $modern.Root
    Check ($process.WaitForExit(15000) -and $process.ExitCode -eq 0) 'modern app hands off a health-aware same-version fixture update'
    Wait-Fixture (Join-Path $modern.Transaction 'status.committed')
    Check ((Get-Content -LiteralPath (Join-Path $modern.Target 'LICENSE') -Raw) -eq 'healthy fixture license') 'modern fixture update replaces the exact non-executable payload too'
    Close-Fixture $modern.Case
    $rollback=Fixture-Process (Join-Path $modern.Target 'LocalLink.exe') '--fixture-rollback' $modern.Root
    Check ($rollback.WaitForExit(15000) -and $rollback.ExitCode -eq 0) 'modern app hands off user-requested rollback'
    $clock=[Diagnostics.Stopwatch]::StartNew()
    do { $completed=@(Get-ChildItem -LiteralPath $modern.Root -Directory | Where-Object {$_.FullName -ne $modern.Transaction -and (Test-Path -LiteralPath (Join-Path $_.FullName 'status.committed'))}); if($completed.Count -eq 1){break}; if($clock.Elapsed.TotalSeconds -gt 65){throw 'Modern rollback helper timed out'}; Start-Sleep -Milliseconds 200 } while($true)
    $modernRollbackPlan=Get-Content -LiteralPath (Join-Path $completed[0].FullName 'plan.json') -Raw | ConvertFrom-Json
    Check ((Get-Content -LiteralPath (Join-Path $completed[0].FullName 'healthy') -Raw) -eq $modernRollbackPlan.HealthToken) 'modern rollback uses its own health token instead of visible-window fallback'
    Check ((Get-Content -LiteralPath (Join-Path $modern.Target 'LICENSE') -Raw) -eq $modernBefore) 'modern rollback restores the exact prior payload'
    Check ((Get-Content -LiteralPath (Join-Path $modern.Target 'user-settings.json') -Raw) -eq 'untouched') 'modern rollback preserves unrelated user settings'
    Close-Fixture $modern.Case

    $failure=Create-Case 'bad-startup-case' 'bad'
    $process=Fixture-Process (Join-Path $failure.Target 'LocalLink.exe') ('--fixture-update "'+$failure.Transaction+'" '+$failure.Hash) $failure.Root
    Check ($process.WaitForExit(15000) -and $process.ExitCode -eq 0) 'bad startup fixture handed off'
    Wait-Fixture (Join-Path $failure.Transaction 'status.rolled-back')
    Check ((Get-FileHash -LiteralPath (Join-Path $failure.Target 'LocalLink.exe')).Hash -eq (Get-FileHash -LiteralPath (Join-Path $out 'old\LocalLink.exe')).Hash) 'failed new startup automatically restores prior executable'
    Check (-not(Test-Path -LiteralPath (Join-Path $failure.Root 'last-update.json'))) 'failed startup never becomes successful rollback baseline'
    Close-Fixture $failure.Case

    $interrupted=Create-Case 'interrupted-case' 'healthy'
    $process=Fixture-Process (Join-Path $interrupted.Target 'LocalLink.exe') ('--fixture-update "'+$interrupted.Transaction+'" '+$interrupted.Hash) $interrupted.Root $true
    Check ($process.WaitForExit(15000) -and $process.ExitCode -eq 0) 'interruption fixture handed off'
    Wait-Fixture (Join-Path $interrupted.Transaction 'fixture-after-first')
    $expectedHelper=Join-Path $interrupted.Transaction 'LocalLink.UpdateHelper.exe'
    $helpers=@(Get-CimInstance Win32_Process -Filter "Name='LocalLink.UpdateHelper.exe'" | Where-Object {$_.ExecutablePath -and $_.ExecutablePath.Equals($expectedHelper,[StringComparison]::OrdinalIgnoreCase)})
    Check ($helpers.Count -eq 1) 'only exact isolated fixture helper selected for interruption'
    $helperProcess=Get-Process -Id $helpers[0].ProcessId; $helperProcess.Kill(); Check ($helperProcess.WaitForExit(5000)) 'test interrupts helper after first atomic replacement'
    $recovery=Fixture-Process (Join-Path $interrupted.Target 'LocalLink.exe') '--fixture-rollback' $interrupted.Root
    Check ($recovery.WaitForExit(15000) -and $recovery.ExitCode -eq 0) 'next launch recognizes pending journal and requests recovery'
    $recoveryTimer=[Diagnostics.Stopwatch]::StartNew()
    do { $committed=@(Get-ChildItem -LiteralPath $interrupted.Root -Directory | Where-Object {Test-Path -LiteralPath (Join-Path $_.FullName 'status.committed')}); if($committed.Count -ge 1){break}; if($recoveryTimer.Elapsed.TotalSeconds -gt 65){throw 'Recovery helper timed out'}; Start-Sleep -Milliseconds 200 } while($true)
    Check ((Get-FileHash -LiteralPath (Join-Path $interrupted.Target 'LocalLink.exe')).Hash -eq (Get-FileHash -LiteralPath (Join-Path $out 'old\LocalLink.exe')).Hash) 'interrupted update recovers exact prior application'
    Check ((Get-Content -LiteralPath (Join-Path $interrupted.Target 'user-settings.json') -Raw) -eq 'untouched') 'interruption recovery preserves unrelated settings'
    Close-Fixture $interrupted.Case
    $metadata=Create-Case 'locked-pointer-case' 'healthy'
    $pointer=Join-Path $metadata.Root 'last-update.json'
    $locked=[IO.File]::Open($pointer,[IO.FileMode]::CreateNew,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    try {
        $process=Fixture-Process (Join-Path $metadata.Target 'LocalLink.exe') ('--fixture-update "'+$metadata.Transaction+'" '+$metadata.Hash) $metadata.Root
        Check ($process.WaitForExit(15000) -and $process.ExitCode -eq 0) 'locked metadata fixture hands off update'
        Wait-Fixture (Join-Path $metadata.Transaction 'status.committed')
        Check ((Get-FileHash -LiteralPath (Join-Path $metadata.Target 'LocalLink.exe')).Hash -eq (Get-FileHash -LiteralPath (Join-Path $out 'healthy\LocalLink.exe')).Hash) 'locked rollback pointer leaves healthy new executable installed'
        Check (-not(Test-Path -LiteralPath (Join-Path $metadata.Transaction 'status.rolled-back'))) 'metadata failure does not roll back a running new version'
        Close-Fixture $metadata.Case
        $rollback=Fixture-Process (Join-Path $metadata.Target 'LocalLink.exe') '--fixture-rollback' $metadata.Root
        Check ($rollback.WaitForExit(15000) -and $rollback.ExitCode -eq 0) 'rollback finds committed pending journal while last-update pointer is locked'
        $clock=[Diagnostics.Stopwatch]::StartNew()
        do { $committed=@(Get-ChildItem -LiteralPath $metadata.Root -Directory | Where-Object {Test-Path -LiteralPath (Join-Path $_.FullName 'status.committed')}); if($committed.Count -ge 2){break}; if($clock.Elapsed.TotalSeconds -gt 65){throw 'Metadata fallback rollback timed out'}; Start-Sleep -Milliseconds 200 } while($true)
        Check ((Get-FileHash -LiteralPath (Join-Path $metadata.Target 'LocalLink.exe')).Hash -eq (Get-FileHash -LiteralPath (Join-Path $out 'old\LocalLink.exe')).Hash) 'metadata fallback restores exact previous executable'
        Close-Fixture $metadata.Case
    } finally { $locked.Dispose() }

    foreach($mode in @('cancel','dispose','unconfirmed','revoke')) {
        $cancelled=Create-Case ($mode+'-handoff-case') 'healthy'
        $process=Fixture-Process (Join-Path $cancelled.Target 'LocalLink.exe') ('--fixture-'+$mode+'-update "'+$cancelled.Transaction+'" '+$cancelled.Hash) $cancelled.Root
        Check ($process.WaitForExit(15000) -and $process.ExitCode -eq 0) ($mode+' fixture completes ready handshake before parent exits')
        Wait-Fixture (Join-Path $cancelled.Transaction 'helper-ready.json') 10
        Start-Sleep -Milliseconds 700
        Check ((Get-FileHash -LiteralPath (Join-Path $cancelled.Target 'LocalLink.exe')).Hash -eq (Get-FileHash -LiteralPath (Join-Path $out 'old\LocalLink.exe')).Hash) ($mode+' handoff never replaces target after later parent exit')
        Check (-not(Test-Path -LiteralPath (Join-Path $cancelled.Root 'pending-update.json'))) ($mode+' handoff never starts a cancelled update journal')
        if($mode -ne 'revoke') { Check (-not(Test-Path -LiteralPath (Join-Path $cancelled.Transaction 'exit-confirmed'))) ($mode+' handoff never authorizes exit') }
        if($mode -ne 'unconfirmed') { Check (Test-Path -LiteralPath (Join-Path $cancelled.Transaction 'abort')) ($mode+' handoff persists hash-bound abort marker') }
        Close-Fixture $cancelled.Case
    }

    $desktop=Create-Case 'desktop-broker-case' 'healthy'
    $process=Fixture-Process (Join-Path $desktop.Target 'LocalLink.exe') ('--fixture-desktop-update "'+$desktop.Transaction+'" '+$desktop.Hash) $desktop.Root
    Check ($process.WaitForExit(20000) -and $process.ExitCode -eq 0) 'same-user desktop broker hands off exact helper without UAC'
    Wait-Fixture (Join-Path $desktop.Transaction 'status.committed')
    Check ((Get-FileHash -LiteralPath (Join-Path $desktop.Target 'LocalLink.exe')).Hash -eq (Get-FileHash -LiteralPath (Join-Path $out 'healthy\LocalLink.exe')).Hash) 'desktop broker update receives readiness and health acknowledgments'
    Close-Fixture $desktop.Case
    Write-Host "PASS updater lifecycle: $checks checks"
} finally {
    foreach($case in @('success-case','modern-rollback-case','bad-startup-case','interrupted-case','locked-pointer-case','cancel-handoff-case','dispose-handoff-case','unconfirmed-handoff-case','revoke-handoff-case','desktop-broker-case')) { $directory=Join-Path $out $case; if(Test-Path -LiteralPath $directory){Close-Fixture $directory} }
}
