# m2: the in-game menu's main page on a fake shadPS4 game. Made from n5:
# Pausing an owned game window (shadPS4) must not hang the launcher. Before a fix, the frozen
# game window stayed owned by the launcher, and re-stacking / Alt+Tab / minimizing the launcher waited for
# it forever ("Launcher.exe stopped interacting with Windows" while Bloodborne sat in the Home menu).
# Fake shadPS4 = TestGame.exe kept open 40 s. Windowed, muted, no real input, never takes the front by itself.
# Usage: n5-pause-restack.ps1 [-Exe <Launcher.exe>] [-Root <dir>]   (wipes <dir>)
param([string]$Exe = "$PSScriptRoot\..\bin\Release\net10.0-windows\Launcher.exe", [string]$Root = "$env:TEMP\Ionea-tests\ingame-menu")
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'make-fake-ps4.ps1') $Root | Out-Null
$data = "$Root\data"; New-Item -ItemType Directory -Force $data | Out-Null
$s = "$Root\m2.txt"
Set-Content $s -Encoding utf8 @(
  'wait 2500', 'type Tester', 'key accept', 'wait 900', 'key accept', 'wait 2500', 'addchoice', 'wait 500', 'key accept',
  'wait 700', 'key accept', 'wait 800', 'key accept', 'wait 1200', 'expect games 1',
  'select Bloodborne', 'wait 500', 'key accept', 'waitshown 30', 'wait 3000', 'expect ingame Bloodborne',
  'restack', 'expect paused no',
  'homebutton', 'wait 800', 'expect screen HomeMenuCard', 'expect menupage main', 'expect cardfocus Resume',
  'shot m2-menu-main', 'cardrow Game settings', 'key accept', 'wait 400', 'expect menupage settings', 'shot m2-menu-settings',
  'key back', 'expect menupage main', 'expect cardfocus Game settings', 'key down 9', 'key back', 'wait 600', 'expect paused no',
  'homebutton', 'wait 800', 'cardrow Close game', 'key accept', 'waitlaunch 20', 'expect ingame none', 'quit')
$env:IONEA_TESTGAME_SECS = '40'
$l = Start-Process $Exe -ArgumentList "--data `"$data`" --no-import --esde `"$Root\esde`" --emulators `"$Root\emus`" --windowed --mute --size 1040x620 --pos -30000,-30000 --script `"$s`" --shots `"$Root\shots`"" -PassThru
$kids = @{}; $t0 = Get-Date
while (-not $l.HasExited -and ((Get-Date) - $t0).TotalSeconds -lt 120) {
  Get-CimInstance Win32_Process -Filter "ParentProcessId=$($l.Id) and Name<>'conhost.exe'" | ForEach-Object { $kids[$_.ProcessId] = $_.Name }
  Start-Sleep -Milliseconds 200
}
$fails = 0
if (-not $l.HasExited) { "launcher still running after 120 s (hung?); stopping PID $($l.Id)"; $l.Kill(); $fails++ }
foreach ($id in $kids.Keys) { Get-Process -Id $id -ErrorAction SilentlyContinue | ForEach-Object { "LEFTOVER $($kids[$id]) $id"; $_.Kill() } }
$log = Get-Content "$data\launcher.log"
$log | Where-Object { $_ -match 'SCRIPT (FAIL|done|restack)|paused|resumed|embed: "|ended' } | Out-Host
if (($log -match 'SCRIPT done, 0 failed').Count -ne 1) { $fails++ }
"m2: $fails failed"
