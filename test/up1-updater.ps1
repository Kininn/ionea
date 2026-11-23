# up1: version and updates (Updater.cs). No network: a fake newer source folder only.
#  Builds two copies with Install-Launcher.ps1 -BuildOnly: "installed" 1.0.0.5 and a "newer" 9.9.9.1.
#  The fake source folder says 9.9.9 (build 1); its Install-Launcher.ps1 just hands over the 9.9.9.1 copy.
#  Run 1 (1.0.0.5): repo kind with its switch off goes nowhere (0 requests); the folder check finds 9.9.9.1 and its
#         changelog; Update now is refused while a "game" runs; then Update now swaps and restarts (outro on).
#         The restarted 9.9.9.1 checks its own version and the previous one, then quits.
#  Run 2 (9.9.9.1): Go back to the previous version swaps them back and restarts as 1.0.0.5.
#  Each time: the data folder's sentinel is unchanged, definitions.previous is there.
# Windowed, muted, off screen. Usage: up1-updater.ps1 [-Root <dir>]   (wipes <dir>)
param([string]$Root = "$env:TEMP\Ionea-tests\updater")
$ErrorActionPreference = 'Stop'
$fails = 0
$install = Join-Path $PSScriptRoot '..\Install-Launcher.ps1'

if (Test-Path $Root) { Remove-Item $Root -Recurse -Force }
$inst = "$Root\inst"; $payload = "$Root\payload"; $src = "$Root\src"; $data = "$inst\data"
& powershell -NoProfile -ExecutionPolicy Bypass -File $install -BuildOnly $inst -BuildNumber 5 | Out-Host
& powershell -NoProfile -ExecutionPolicy Bypass -File $install -BuildOnly $payload -BuildNumber 1 -Version 9.9.9 | Out-Host
$exe = "$inst\Launcher.exe"
if ((Get-Item $exe).VersionInfo.FileVersion -ne '1.0.0.5') { throw "installed copy is $((Get-Item $exe).VersionInfo.FileVersion)" }
if ((Get-Item "$payload\Launcher.exe").VersionInfo.FileVersion -ne '9.9.9.1') { throw "payload is $((Get-Item "$payload\Launcher.exe").VersionInfo.FileVersion)" }

# the fake source folder
New-Item -ItemType Directory -Force $src, $data | Out-Null
Set-Content "$src\Launcher.csproj" -Encoding utf8 '<Project><PropertyGroup><VersionPrefix>9.9.9</VersionPrefix></PropertyGroup></Project>'
Set-Content "$src\build.txt" -Encoding ascii '1'
Set-Content "$src\CHANGELOG.md" -Encoding utf8 "# Ionea changelog`n`n## 9.9.9`n- test notes 9.9.9"
Set-Content "$src\Install-Launcher.ps1" -Encoding utf8 @'
param([string]$BuildOnly)
New-Item -ItemType Directory -Force $BuildOnly | Out-Null
Copy-Item (Join-Path $PSScriptRoot '..\payload\*') $BuildOnly -Recurse -Force
Write-Host "fake build staged in $BuildOnly"
'@
Set-Content "$data\sentinel.txt" -Encoding ascii 'keep me'

function Run-Chain([string]$scriptFile, [string]$afterFile, [int]$limit) {
  $env:IONEA_TEST_RESTART_SCRIPT = $afterFile
  $argsLine = "--data `"$data`" --no-import --dummies 12 --windowed --mute --outro on --size 1040x620 --pos -30000,-30000 --script `"$scriptFile`""
  $l = Start-Process $exe -ArgumentList $argsLine -PassThru -WorkingDirectory $inst
  $t0 = Get-Date; $seen = @{}
  # the first one, then the one it restarted: wait until no Launcher.exe from $inst is left
  while (((Get-Date) - $t0).TotalSeconds -lt $limit) {
    $live = @(Get-CimInstance Win32_Process -Filter "Name = 'Launcher.exe'" | Where-Object { $_.ExecutablePath -like "$inst\*" })
    $live | ForEach-Object { $seen[$_.ProcessId] = $_.ExecutablePath }
    if ($live.Count -eq 0 -and $l.HasExited) { break }
    Start-Sleep -Milliseconds 300
  }
  $env:IONEA_TEST_RESTART_SCRIPT = $null
  $bad = 0
  Get-CimInstance Win32_Process -Filter "Name = 'Launcher.exe'" | Where-Object { $_.ExecutablePath -like "$inst\*" } | ForEach-Object { Write-Host "still running after $limit s: $($_.ProcessId)"; Stop-Process -Id $_.ProcessId -Force; $bad++ }
  Write-Host "processes seen: $($seen.Count)"
  if ($seen.Count -lt 2) { Write-Host 'no restarted process seen'; $bad++ }
  return $bad
}
function Check([string]$name, [string[]]$must) {
  $log = Get-Content "$data\launcher.log"
  $bad = 0
  foreach ($p in $must) { if (-not ($log -match $p)) { Write-Host "$name MISSING: $p"; $bad++ } }
  $f = @($log -match 'SCRIPT FAIL'); if ($f.Count) { $f | Out-Host; $bad += $f.Count }
  if ((Get-Content "$data\sentinel.txt" -Raw).Trim() -ne 'keep me') { Write-Host "$name data sentinel changed"; $bad++ }
  if (-not (Test-Path "$inst\definitions.previous")) { Write-Host "$name no definitions.previous"; $bad++ }
  return $bad
}
function Versions([string]$name, [string]$now, [string]$prev) {
  $a = (Get-Item $exe).VersionInfo.FileVersion; $b = (Get-Item "$inst\Launcher.previous.exe").VersionInfo.FileVersion
  Write-Host "$name Launcher.exe $a, Launcher.previous.exe $b"
  if ($a -ne $now -or $b -ne $prev) { Write-Host "$name versions off (want $now / $prev)"; return 1 }
  return 0
}

# ---- run 1: check, refusals, update ----
$s1 = "$Root\run1.txt"; $a1 = "$Root\after1.txt"
Set-Content $s1 -Encoding utf8 @(
  'wait 1500', 'expect version 1.0.0.5', 'expect updatestate notchecked', 'expect previousversion none',
  'updatekind repo', 'updaterepo https://github.com/example/ionea', 'online updates off',
  'updatecheck', 'expect updatestate refused', 'expect requests 0',
  'updatekind folder', "updatefolder $src", 'updatecheck', 'expect updatestate available 9.9.9.1', 'expect changelog test notes 9.9.9',
  'settings About and updates', 'wait 600', 'expect screen SettingsCard',
  'updategame on', 'updatenow', 'wait 500', 'expect version 1.0.0.5', 'key back', 'wait 400', 'updategame off',
  'updatenow')
Set-Content $a1 -Encoding utf8 @('wait 1500', 'expect version 9.9.9.1', 'expect previousversion 1.0.0.5', 'log after-update', 'quit')
$fails += Run-Chain $s1 $a1 180
$fails += Check 'update' @('start 1\.0\.0\.5', 'update: refused: ', 'update: refused, a game is running', 'update: available: ', 'update: swapped in 9\.9\.9\.1',
  'outro: start', 'restart: started', 'start 9\.9\.9\.1', 'update: started 9\.9\.9\.1 after updating from 1\.0\.0\.5', 'SCRIPT after-update')
$fails += Versions 'update' '9.9.9.1' '1.0.0.5'

# ---- run 2: Go back to the previous version ----
$s2 = "$Root\run2.txt"; $a2 = "$Root\after2.txt"
Set-Content $s2 -Encoding utf8 @('wait 1500', 'expect version 9.9.9.1', 'updateback')
Set-Content $a2 -Encoding utf8 @('wait 1500', 'expect version 1.0.0.5', 'expect previousversion 9.9.9.1', 'log after-back', 'quit')
$fails += Run-Chain $s2 $a2 90
$fails += Check 'back' @('update: back to 1\.0\.0\.5', 'update: started 1\.0\.0\.5 after going back from 9\.9\.9\.1', 'SCRIPT after-back')
$fails += Versions 'back' '1.0.0.5' '9.9.9.1'

Get-Content "$data\launcher.log" | Where-Object { $_ -match 'SCRIPT|update:|restart|start |exit' } | Out-Host
Write-Host "up1: $fails failed"
exit $fails
