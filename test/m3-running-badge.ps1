# m3: the open game on the home screen (first on the row, a Paused / Playing badge, A says Resume). Made from n5:
# Pausing an owned game window (shadPS4) must not hang the launcher. Before a fix, the frozen
# game window stayed owned by the launcher, and re-stacking / Alt+Tab / minimizing the launcher waited for
# it forever ("Launcher.exe stopped interacting with Windows" while Bloodborne sat in the Home menu).
# Fake shadPS4 = TestGame.exe kept open 40 s. Windowed, muted, no real input, never takes the front by itself.
# Usage: n5-pause-restack.ps1 [-Exe <Launcher.exe>] [-Root <dir>]   (wipes <dir>)
param([string]$Exe = "$PSScriptRoot\..\bin\Release\net10.0-windows\Launcher.exe", [string]$Root = "$env:TEMP\Ionea-tests\running-badge")
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'make-fake-ps4.ps1') $Root | Out-Null
$data = "$Root\data"; New-Item -ItemType Directory -Force $data | Out-Null
$s = "$Root\m3.txt"
Set-Content $s -Encoding utf8 @(
  'wait 2500', 'type Tester', 'key accept', 'wait 900', 'key accept', 'wait 2500', 'addchoice', 'wait 500', 'key accept',
  'wait 700', 'key accept', 'wait 800', 'key accept', 'wait 1200', 'expect games 6',
  'select Bloodborne', 'wait 500', 'key accept', 'waitshown 30', 'wait 3000', 'expect ingame Bloodborne',
  'restack', 'expect paused no',
  'homebutton', 'wait 800', 'expect screen HomeMenuCard', 'cardrow Home screen', 'key accept', 'wait 1200',
  'expect screen HomeScreen', 'expect paused yes', 'expect runbadge Bloodborne|Paused', 'expect selname Bloodborne', 'expect sel 0',
  'expect hinttext X Options  Enter Resume', 'shot m3-home-paused', 'key right', 'wait 300', 'expect hinttext X Options  Enter Start',
  'shot m3-home-other', 'key left', 'wait 300', 'key accept', 'wait 1500', 'expect paused no',
  'homebutton', 'wait 800', 'cardrow Close game', 'key accept', 'wait 600', 'expect closing Bloodborne', 'expect ingame Bloodborne',
  'expect runbadge Bloodborne|Closing…', 'shot m3-closing', 'waitlaunch 20', 'expect ingame none', 'expect closing none', 'wait 500', 'expect runbadge none', 'quit')
$env:IONEA_TESTGAME_SECS = '40'
$env:IONEA_TESTGAME_CLOSEMS = '2500'   # the fake emulator takes a while to close: the closing screen
$l = Start-Process $Exe -ArgumentList "--data `"$data`" --no-import --dummies 5 --esde `"$Root\esde`" --emulators `"$Root\emus`" --windowed --mute --size 1040x620 --pos -30000,-30000 --script `"$s`" --shots `"$Root\shots`"" -PassThru
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
"m3: $fails failed"
