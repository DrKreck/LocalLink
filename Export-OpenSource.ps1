#requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$out = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
$root = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
if ($out -eq $root -or $root.StartsWith($out+'\',[StringComparison]::OrdinalIgnoreCase) -or $out.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Export to a separate directory outside the working source.' }
if (Test-Path -LiteralPath $out) { throw 'Export directory must not already exist.' }
$files = @(
    'Program.cs','Theme.cs','BootstrapUI.cs','DomainManagerForm.cs','PresetUi.cs','Engine.cs','Dependencies.cs','ProfileEvidence.cs','Profiles.cs','Network.cs','Services.cs','AppSettings.cs','DomainSelection.cs','Presets.cs','Tuning.cs','TuningReport.cs','PublicLists.cs','StartupRegistration.cs','DiagnosticHistory.cs','BugReport.cs','ContactLink.cs','VersionInfo.cs',
    'ApplicationFeatures.cs','NetworkProfiles.cs','RecoveryPolicy.cs','AppUpdates.cs','UpdateHelper.cs','DesktopProcess.cs','UpdatePreferences.cs','UpdateExperience.cs','UpdatePromptForm.cs','Troubleshooting.cs','TroubleshootingForm.cs',
    'AutomaticSetup.cs','AutomaticConnection.cs','SettingsNavigation.cs','PresetExchange.cs','PresetExchangeUi.cs',
    'DiagnosticsCli.cs','ListsCli.cs','ProfileTests.cs','ServiceTests.cs','PublicListTests.cs','TestMain.cs',
    'Build.ps1','Export-OpenSource.ps1','Package-Release.ps1','engine.lock.json','dependencies.lock.json','telegram-runtime.lock.json','app.manifest','LocalLink.exe.config',
    'README.md','LICENSE','THIRD-PARTY.md','CHANGELOG.md','CONTRIBUTING.md','SECURITY.md','.gitignore','.gitattributes',
    'tools\Setup-Engine.ps1','tools\Fetch-Engine.ps1','tools\Build-TelegramRuntime.ps1','tools\Sign-AppUpdate.ps1','tools\Test-AppUpdates.ps1','tools\AppUpdateFixture.cs',
    'tools\New-UpdateSigningKey.ps1','tools\Test-UpdateSigning.ps1','tools\Test-DesktopProcess.ps1','tools\DesktopProcessFixture.cs',
    'telegram_bridge\Launcher.cs','telegram_bridge\bridge.py','telegram_bridge\python312._pth','telegram_bridge\README.md',
    'docs\PRIVACY.md','docs\RELEASE-NOTES-0.8.5.md','docs\RELEASE-NOTES-0.9.0.md','docs\UPDATE-SIGNING.md','docs\images\interface.png','.github\FUNDING.yml'
)
$testNames = @('DiscordDesktopDiagnostics','DependencyInstallerTests','DependencyUiTests','DomainSelectionTests','DomainExcludeTests','PresetTests','DomainManagerUiTests','PresetUiTests','TlsCoverageTests','TuningSelectionTests','TuningReportTests','ProfileEvidenceTests','StartupRegistrationTests','HostListPathTests','DiscordScopeTests','DiscordDesktopDiagnosticsTests','DiscordTuningTests','DiscordReportTests','VerifiedListCountTests','SettingsStorageTests','EnginePreflightTests','PublicListSaveTests','BugReportTests','ContactLinkTests','TypographyLayoutTests','UsabilityFlowTests','TrayLifecycleTests','ControlChromeTests','StartupTrayTests','UsabilityPreview','PublicBuildTests')
$files += @($testNames | ForEach-Object { 'diagnostics\'+$_+'.cs' })
$files += 'diagnostics\Test-UI.ps1'
$files += @('NetworkProfileTests','RecoveryPolicyTests','AppUpdateTests','TroubleshootingTests','ApplicationFeatureUiTests','UpdatePreferenceTests','DesktopProcessTests','UpdatePromptTests' | ForEach-Object { 'diagnostics\'+$_+'.cs' })
$files += @('AutomaticSetupTests','AutomaticConnectionUiTests','SettingsNavigationTests','PresetExchangeTests','PresetExchangeUiTests','TelegramAutoSetupTests' | ForEach-Object { 'diagnostics\'+$_+'.cs' })
$files += 'diagnostics\AutomaticOnboardingTests.cs'
$files += @('diagnostics\UpdateStartupGateTests.cs','diagnostics\UpdateStartupUiTests.cs')
foreach ($relative in $files) {
    $file = Join-Path $root $relative
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Required public source is missing: $relative" }
    $cursor = $file
    while ($cursor) {
        $item = Get-Item -LiteralPath $cursor -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse point not allowed: $relative" }
        if ($item.FullName -eq $root) { break }
        $cursor = Split-Path -Parent $cursor
    }
}
New-Item -ItemType Directory -Path $out | Out-Null
foreach ($relative in $files) {
    $target = Join-Path $out $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $root $relative) -Destination $target
}
$manifest = @($files | ForEach-Object { [ordered]@{Path=$_.Replace('\','/');SHA256=(Get-FileHash -LiteralPath (Join-Path $out $_) -Algorithm SHA256).Hash} })
$manifest | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $out 'SOURCE-MANIFEST.json') -Encoding UTF8
Write-Host "Exported $($files.Count) source files: $out"
