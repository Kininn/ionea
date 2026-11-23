# gh1: a game's helper (Helper.cs). Fake Lichii (TestGame.exe named lichii.exe, kept open 40 s by the
# test's environment) and a fake Switch 2 game, with a PowerShell script as its helper:
#   ok   says "env" lines then "ready", waits for its standard input to close, then writes helper-stopped.txt.
#        The env lines give the emulator IONEA_TESTGAME_SECS=8 (it closes itself after 8 s instead of 40) and
#        IONEA_TESTGAME_REPORT (it writes how it was started), so both prove the emulator got them.
#   fail says "fail <text>": the start is refused with that text.
#   die  writes to its error output and exits before "ready": the start is refused.
#   own  as ok, but says "own name" first: the emulator starts as lichii.exe, not through the link named like Ionea.
#   slow never says ready (HelperRun.ReadySeconds lowered to 3 by IONEA_TEST_HELPER_READY): refused, and it is ended.
# Windowed, muted, no real input, never takes the front by itself.
# Usage: gh1-helper.ps1 [-Exe <Launcher.exe>] [-Root <dir>]   (wipes <dir>)
param([string]$Exe = "$PSScriptRoot\..\bin\Release\net10.0-windows\Launcher.exe", [string]$Root = "$env:TEMP\Ionea-tests\helper")
$ErrorActionPreference = 'Stop'
$game = Join-Path $PSScriptRoot 'bin\TestGame.exe'
if (-not (Test-Path $game)) { throw "Build test\bin\TestGame.exe first (see README)." }
if (Test-Path $Root) { Remove-Item $Root -Recurse -Force }
$e = "$Root\emus"
foreach ($d in "$Root\esde\ROMs\switch2", "$Root\data", "$e\Lichii\user\config") { New-Item -ItemType Directory -Force $d | Out-Null }
Copy-Item $game "$e\Lichii\lichii.exe"
Set-Content "$e\Lichii\user\config\qt-config.ini" "[UI]`r`ncheck_for_updates\default=false`r`ncheck_for_updates=false"
Set-Content -LiteralPath "$Root\esde\ROMs\switch2\Test Zelda.nup" 'not a real game'
Set-Content "$Root\helper.ps1" -Encoding utf8 @'
param([string]$Mode = 'ok')
function Say([string]$t) { [Console]::Out.WriteLine($t); [Console]::Out.Flush() }
Say "say starting ($Mode)"
if ($Mode -eq 'fail') { Say 'fail The stand-in server did not start.'; exit 1 }
if ($Mode -eq 'die') { [Console]::Error.WriteLine('boom from the helper'); exit 3 }
if ($Mode -eq 'slow') { Start-Sleep 60; exit 0 }
if ($Mode -eq 'own') { Say 'own name' }
Say "env IONEA_TESTGAME_SECS=8"
Say "env IONEA_TESTGAME_REPORT=$PSScriptRoot\report.txt"
Say 'not a command, only logged'
Say 'ready'
Say 'say after ready'
while ($null -ne [Console]::In.ReadLine()) { }
Set-Content "$PSScriptRoot\helper-stopped.txt" 'stopped'
'@
$ps = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
$h = "$ps|-NoProfile -ExecutionPolicy Bypass -File `"T\helper.ps1`""

$data = "$Root\data"
$s = "$Root\gh1.txt"
Set-Content $s -Encoding utf8 @(
  'wait 2500', 'type Tester', 'key accept', 'wait 900', 'key accept', 'wait 2500',
  'addchoice', 'wait 500', 'key accept', 'wait 700', 'key accept', 'wait 800', 'key accept', 'wait 1200', 'expect games 1',
  # ok: the emulator gets the helper's settings, the helper is told to stop after the game
  "helper Test Zelda|$h ok",
  'select Test Zelda', 'wait 500', 'key accept', 'wait 3000', 'waitshown 30', 'expect ingame Test Zelda', 'expect exists T\report.txt',
  'waitlaunch 30', 'expect ingame none', 'wait 1500', 'expect exists T\helper-stopped.txt',
  # own: the same, under the emulator's own program name
  "helper Test Zelda|$h own",
  'select Test Zelda', 'wait 500', 'key accept', 'wait 3000', 'waitshown 30', 'expect ingame Test Zelda',
  'waitlaunch 30', 'expect ingame none', 'wait 1500',
  # fail: refused with the helper's text
  "helper Test Zelda|$h fail",
  'select Test Zelda', 'wait 500', 'key accept', 'wait 1500', 'waitlaunch 30', 'wait 800', 'expect launch failed', 'expect screen MessageCard', 'shot gh1-fail', 'key back', 'wait 600',
  # die: ended before ready
  "helper Test Zelda|$h die",
  'select Test Zelda', 'wait 500', 'key accept', 'wait 1500', 'waitlaunch 30', 'wait 800', 'expect launch failed', 'expect screen MessageCard', 'key back', 'wait 600',
  # slow: never ready, ended
  "helper Test Zelda|$h slow",
  'select Test Zelda', 'wait 500', 'key accept', 'wait 1500', 'waitlaunch 40', 'wait 800', 'expect launch failed', 'expect screen MessageCard', 'key back', 'wait 600',
  # a missing helper program
  'helper Test Zelda|T\missing.exe|',
  'select Test Zelda', 'wait 500', 'key accept', 'wait 1500', 'waitlaunch 20', 'wait 800', 'expect launch failed', 'expect screen MessageCard', 'key back', 'wait 600',
  # none: starts as before
  'helper Test Zelda|none',
  'select Test Zelda', 'wait 500', 'key accept', 'wait 1500', 'waitshown 30', 'wait 1500', 'expect ingame Test Zelda',
  'homebutton', 'wait 800', 'cardrow Close game', 'key accept', 'wait 1500', 'waitlaunch 20', 'expect ingame none',
  'quit')
$env:IONEA_TESTGAME_SECS = '40'
$env:IONEA_TESTROOT = $Root
$env:IONEA_TEST_HELPER_READY = '3'
$l = Start-Process $Exe -ArgumentList "--data `"$data`" --no-import --esde `"$Root\esde`" --emulators `"$e`" --windowed --mute --size 1040x620 --pos -30000,-30000 --script `"$s`" --shots `"$Root\shots`"" -PassThru
$t0 = Get-Date
while (-not $l.HasExited -and ((Get-Date) - $t0).TotalSeconds -lt 240) { Start-Sleep -Milliseconds 500 }
$fails = 0
if (-not $l.HasExited) { "launcher still running after 240 s (hung?); stopping PID $($l.Id)"; $l.Kill(); $fails++ }
# No helper left running (every one of them was a powershell.exe running helper.ps1).
$left = Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | Where-Object { $_.CommandLine -like "*$Root\helper.ps1*" }
foreach ($x in $left) { "LEFTOVER helper $($x.ProcessId)"; Stop-Process -Id $x.ProcessId -Force; $fails++ }
$log = Get-Content "$data\launcher.log"
$log | Where-Object { $_ -match 'SCRIPT (FAIL|done)|helper: |ended"?$' } | Out-Host
if (($log -match 'SCRIPT done, 0 failed').Count -ne 1) { $fails++ }
foreach ($want in 'ready, 2 setting\(s\)', "asked for the emulator's own name: lichii\.exe", 'refused: The stand-in server did not start\.', 'err: boom from the helper', 'ended before ready', 'not ready in time', 'missing .*missing\.exe', 'powershell\.exe: after ready', 'powershell\.exe: not a command, only logged') {
  if (-not ($log -match "helper: .*$want")) { "MISSING log line: $want"; $fails++ }
}
$rep = if (Test-Path "$Root\report.txt") { @(Get-Content "$Root\report.txt") } else { @() }
# The ok start ran through the link named like Ionea, the own start as lichii.exe.
if ($rep.Count -ne 2) { "report.txt has $($rep.Count) line(s), want 2 (the two helped starts)"; $fails++ }
elseif ($rep[0] -notlike 'Launcher.exe|*' -or $rep[1] -notlike 'lichii.exe|*') { "report.txt: want Launcher.exe then lichii.exe, got: $($rep -join ' / ')"; $fails++ }
"gh1: $fails failed"
