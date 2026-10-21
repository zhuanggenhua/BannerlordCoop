param([string]$EvidenceRoot = (Join-Path $PSScriptRoot '../../artifacts/listen-demo-chinese-m24'))
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Demo-Common.ps1')
$EvidenceRoot = [IO.Path]::GetFullPath($EvidenceRoot)
New-Item -ItemType Directory -Force -Path $EvidenceRoot | Out-Null
$fixture = Join-Path $EvidenceRoot ('fixture-'+[Guid]::NewGuid().ToString('N'))
$package = Join-Path $fixture 'package'
$game = Join-Path $fixture 'game'
$bin = Join-Path $game 'bin/Win64_Shipping_Client'
New-Item -ItemType Directory -Path $package,$bin,(Join-Path $package 'Modules/Coop') | Out-Null
foreach ($script in Get-ChildItem -LiteralPath $PSScriptRoot -File | Where-Object {$_.Name -ne 'Test-Demo.ps1'}) { Copy-Item -LiteralPath $script.FullName -Destination $package }
[IO.File]::WriteAllText((Join-Path $package 'Modules/Coop/SubModule.xml'),'<Module/>')
[IO.File]::WriteAllText((Join-Path $bin 'Bannerlord.exe'),'fixture never executed')
[IO.File]::WriteAllText((Join-Path $bin 'TaleWorlds.CampaignSystem.dll'),'fixture version')
$files = @(Get-ChildItem -LiteralPath $package -File -Recurse | ForEach-Object { @{path=$_.FullName.Substring($package.Length+1).Replace('\','/');sha256=(Get-CoopSha256 $_.FullName)} })
@{codeCommit='fixture-commit';gameVersion='fixture';requiredCampaignDllSha256=(Get-CoopSha256 (Join-Path $bin 'TaleWorlds.CampaignSystem.dll'));actualModuleIdentities=@(@{file='Coop.dll';mvid='fixture-mvid';sha256='fixture-hash'});files=$files} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $package 'manifest.json') -Encoding UTF8
$passed = @()
foreach ($file in Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1' -File) {
    $tokens=$null; $errors=$null
    [Management.Automation.Language.Parser]::ParseFile($file.FullName,[ref]$tokens,[ref]$errors) | Out-Null
    if ($errors.Count) { throw "Syntax failure $($file.Name): $errors" }
}
$passed += 'PowerShell syntax'
$verified = Get-CoopManifest $package
if ($verified.codeCommit -ne 'fixture-commit') { throw 'Manifest identity mismatch' }
$passed += 'Package hash verification'
$oldModule = Join-Path $game 'Modules/Coop'
New-Item -ItemType Directory -Path $oldModule | Out-Null
[IO.File]::WriteAllText((Join-Path $oldModule 'old.txt'),'retain previous module')
$shell = (Get-Process -Id $PID).Path
$launcher = (Join-Path $package 'Launch-Demo.ps1').Replace("'","''")
$quotedGame = $game.Replace("'","''")
$blockedRunner = Join-Path $fixture 'install-blocked.ps1'
@("function Get-Process { [PSCustomObject]@{Id=9876} }", "& '$launcher' -Action Install -GameRoot '$quotedGame'", 'exit $LASTEXITCODE') | Set-Content -LiteralPath $blockedRunner -Encoding UTF8
& $shell -NoProfile -File $blockedRunner *> (Join-Path $EvidenceRoot 'install-blocked.log')
if ($LASTEXITCODE -ne 1 -or !(Test-Path -LiteralPath (Join-Path $oldModule 'old.txt')) -or (Test-Path -LiteralPath (Join-Path $game 'CoopDemoBackups'))) { throw 'Running-game guard changed fixture installation' }
$passed += 'Running-game guard refuses installation before replacing files'
$installRunner = Join-Path $fixture 'install-fixture.ps1'
@("function Get-Process { }", "& '$launcher' -Action Install -GameRoot '$quotedGame'", 'exit $LASTEXITCODE') | Set-Content -LiteralPath $installRunner -Encoding UTF8
& $shell -NoProfile -File $installRunner *> (Join-Path $EvidenceRoot 'install-fixture.log')
if ($LASTEXITCODE -ne 0) { throw "Fake installation exit $LASTEXITCODE" }
$backups = @(Get-ChildItem -LiteralPath (Join-Path $game 'CoopDemoBackups') -Filter old.txt -Recurse -File)
if ($backups.Count -ne 1 -or [IO.File]::ReadAllText($backups[0].FullName) -ne 'retain previous module' -or (Get-CoopSha256 (Join-Path $oldModule 'SubModule.xml')) -ne (Get-CoopSha256 (Join-Path $package 'Modules/Coop/SubModule.xml'))) { throw 'Installation did not preserve old module outside Modules' }
$passed += 'Fake installation verifies hashes and preserves old module outside Modules'

try { Get-CoopPackagePath $package '../outside' | Out-Null; throw 'Traversal accepted' } catch { if ($_.Exception.Message -eq 'Traversal accepted') { throw } }
$passed += 'Path traversal rejected'
$moduleFile = Join-Path $package 'Modules/Coop/SubModule.xml'
[IO.File]::AppendAllText($moduleFile,'corrupted')
try { Get-CoopManifest $package | Out-Null; throw 'Corrupt package accepted' } catch { if ($_.Exception.Message -eq 'Corrupt package accepted') { throw } }
[IO.File]::WriteAllText($moduleFile,'<Module/>')
$passed += 'Corrupt package rejected'
$raw = Join-Path $bin 'Coop_client.log'
[IO.File]::WriteAllText($raw,"[ERR] 原始异常 System.NullReferenceException`r`n  at Original.Native.Method()`r`nSteam Epoch Freeze`r`n")
[IO.File]::WriteAllText((Join-Path $bin 'BootPatches.log'),'Steam bootstrap 原始失败')
$sourceSha = Get-CoopSha256 $raw
$share = [IO.File]::Open($raw,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::ReadWrite)
try {
    $shell = (Get-Process -Id $PID).Path
    & $shell -NoProfile -File (Join-Path $package 'Export-Diagnostics.ps1') -GameRoot $game -OutputRoot (Join-Path $fixture 'output') *> (Join-Path $EvidenceRoot 'export-fixture.log')
    if ($LASTEXITCODE -ne 0) { throw "Export exit $LASTEXITCODE" }
} finally { $share.Dispose() }
$export = Get-ChildItem -LiteralPath (Join-Path $fixture 'output') -Directory | Select-Object -First 1
if ((Get-CoopSha256 (Join-Path $export.FullName 'raw/Coop_client.log')) -ne $sourceSha -or (Get-CoopSha256 $raw) -ne $sourceSha) { throw 'Raw failure evidence changed' }
$record = Get-Content -LiteralPath (Join-Path $export.FullName 'evidence.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($record.codeCommit -ne 'fixture-commit' -or ($record.logs | Where-Object { $_.source.EndsWith('Coop_client.log') }).sha256 -ne $sourceSha -or !$record.moduleIdentities[0].mvid) { throw 'Evidence identity/hash missing' }
if (!(Test-Path -LiteralPath ($export.FullName+'.zip')) -or !(Test-Path -LiteralPath (Join-Path $export.FullName '中文摘要.txt'))) { throw 'Missing Chinese summary/ZIP' }
$passed += 'Open writable raw log copied without altering source or stack'
if (!(Test-Path -LiteralPath (Join-Path $export.FullName 'raw/BootPatches.log'))) { throw 'Bootstrap log missing' }
$passed += 'ZIP, Chinese summary, BootPatches, source metadata, PID observations, commit and manifest MVID retained'
# Export must refuse corrupted packaged code, not run it and emit a misleading success.
[IO.File]::AppendAllText($moduleFile,'corrupted')
& $shell -NoProfile -File (Join-Path $package 'Export-Diagnostics.ps1') -GameRoot $game -OutputRoot (Join-Path $fixture 'bad-output') *> (Join-Path $EvidenceRoot 'export-reject.log')
if ($LASTEXITCODE -ne 1 -or (Test-Path -LiteralPath (Join-Path $fixture 'bad-output'))) { throw 'Corrupt package export not refused' }
$passed += 'Exporter rejects corrupted package with exit 1'
@{psVersion=$PSVersionTable.PSVersion.ToString();fixture=$fixture;passed=$passed;gameStarted=$false;realInstallationTouched=$false} | ConvertTo-Json -Depth 5 | Tee-Object -FilePath (Join-Path $EvidenceRoot 'script-test-results.json')
