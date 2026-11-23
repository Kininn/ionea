# k4: the Home menu key (Alt by default) in a fake shadPS4 game. While the game is in front a
# tap of Alt opens the Home menu and pauses, and a tap in the menu resumes. With the key changed to J the
# game loses J, and the claim follows. "pretendfront on/off" counts the launcher as in front / nothing as in front,
# "hooksim" runs the key logic only (no keyboard hook in test runs, nothing is sent).
# Fake shadPS4 = TestGame.exe kept open 40 s. Windowed, muted, no real input.
# Usage: k4-home-key-ingame.ps1 [-Exe <Launcher.exe>] [-Root <dir>]   (wipes <dir>)
param([string]$Exe = "$PSScriptRoot\..\bin\Release\net10.0-windows\Launcher.exe", [string]$Root = "$env:TEMP\Ionea-tests\home-key-ingame")
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'make-fake-ps4.ps1') $Root | Out-Null
$data = "$Root\data"; New-Item -ItemType Directory -Force $data | Out-Null
$s = "$Root\k4.txt"
Set-Content $s -Encoding utf8 @(
  'wait 2500', 'type Tester', 'key accept', 'wait 900', 'key accept', 'wait 2500', 'addchoice', 'wait 500', 'key accept',
  'wait 700', 'key accept', 'wait 800', 'key accept', 'wait 1200', 'expect games 1',
  'select Bloodborne', 'wait 500', 'key accept', 'waitshown 30', 'wait 3000', 'expect ingame Bloodborne',
  'pretendfront off', 'wait 300', 'expect paused no', 'expect homekey no', 'pretendfront on', 'wait 300', 'expect homekey yes',
  'hooksim down LeftAlt', 'expect hooksim pass', 'hooksim up LeftAlt', 'expect hooksim fire+quiet', 'wait 900',
  'expect screen HomeMenuCard', 'expect paused yes', 'expect homekey yes', 'shot k4-alt-menu',
  'hooksim down RightAlt', 'hooksim up RightAlt', 'expect hooksim fire+quiet', 'wait 900', 'expect paused no', 'expect homekey yes',
  'hooksim down LeftAlt', 'hooksim down Tab', 'hooksim up Tab', 'hooksim up LeftAlt', 'expect hooksim pass', 'wait 600', 'expect paused no',
  'launcherkey home J', 'wait 300', 'expect homekey yes', 'hooksim down LeftAlt', 'hooksim up LeftAlt', 'expect hooksim pass',
  'hooksim down J', 'expect hooksim fire+swallow', 'hooksim up J', 'expect hooksim swallow', 'wait 900', 'expect paused yes',
  'hooksim down J', 'hooksim up J', 'wait 900', 'expect paused no',
  'launcherkeysreset', 'pretendfront off', 'wait 300', 'expect homekey no',
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
$log | Where-Object { $_ -match 'SCRIPT (FAIL|done|home)|paused|resumed|home menu:|embed: "|ended' } | Out-Host
if (($log -match 'SCRIPT done, 0 failed').Count -ne 1) { $fails++ }
"k4: $fails failed"
