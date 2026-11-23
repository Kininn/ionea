# e2: a standalone emulator that sizes its own window after Ionea took it in (a Qt "-f" going full screen, mGBA)
# is fitted back over the launcher's window (Embed.cs Fits). Fake mGBA = TestGame.exe that grows
# 100 px out of place 1.5 s after it shows (IONEA_TESTGAME_GROW), kept open 20 s. The window is wider than 16:9, so the
# Home menu's still also goes on the window's background (no bars beside the stage; stillbehind). Windowed, off-screen, muted, no real input.
# Usage: e2-embed-refit.ps1 [-Exe <Launcher.exe>] [-Root <dir>]   (wipes <dir>)
param([string]$Exe = "$PSScriptRoot\..\bin\Release\net10.0-windows\Launcher.exe", [string]$Root = "$env:TEMP\Ionea-tests\refit")
$ErrorActionPreference = 'Stop'
$game = Join-Path $PSScriptRoot 'bin\TestGame.exe'
if (Test-Path $Root) { Remove-Item $Root -Recurse -Force }
foreach ($d in 'data', 'emus', 'pick\mGBA', 'roms') { New-Item -ItemType Directory -Force "$Root\$d" | Out-Null }
Copy-Item $game "$Root\pick\mGBA\mGBA.exe"; Set-Content "$Root\pick\mGBA\portable.ini" ''
Set-Content -LiteralPath "$Root\roms\Pocket Test (USA).gba" 'not a real game'
$s = "$Root\e2.txt"
Set-Content $s -Encoding utf8 @(
  'wait 2500', 'type Tester', 'key accept', 'wait 900', 'key accept', 'wait 2500',
  "addemu $Root\pick\mGBA\mGBA.exe", 'wait 800', 'expect emus 1', 'closebutton', 'wait 700', 'expect screen HomeScreen',
  "addrom $Root\roms\Pocket Test (USA).gba", 'wait 800', 'cardrow Add', 'key accept', 'wait 900', 'expect games 1', 'expect screen HomeScreen',
  'select Pocket Test', 'wait 500', 'key accept', 'waitshown 30', 'wait 500', 'expect embedded yes', 'expect embedfits yes',
  'wait 3000', 'expect embedfits yes', 'shot e2-ingame',
  'homebutton', 'wait 800', 'expect screen HomeMenuCard', 'expect paused yes', 'expect stillbehind yes', 'shot e2-menu',
  'key back', 'wait 800', 'expect paused no', 'expect stillbehind no', 'expect embedfits yes',
  'homebutton', 'wait 800', 'cardrow Close game', 'key accept', 'waitlaunch 30', 'wait 800',
  'quit')
$env:IONEA_TESTGAME_SECS = '20'; $env:IONEA_TESTGAME_GROW = '1500'
$l = Start-Process $Exe -ArgumentList "--data `"$Root\data`" --no-import --emulators `"$Root\emus`" --windowed --mute --size 1200x620 --pos -30000,-30000 --script `"$s`" --shots `"$Root\shots`"" -PassThru
$env:IONEA_TESTGAME_GROW = $null
$kids = @{}; $t0 = Get-Date
while (-not $l.HasExited -and ((Get-Date) - $t0).TotalSeconds -lt 120) {
  Get-CimInstance Win32_Process -Filter "ParentProcessId=$($l.Id) and Name<>'conhost.exe'" | ForEach-Object { $kids[$_.ProcessId] = $_.Name }
  Start-Sleep -Milliseconds 200
}
$fails = 0
if (-not $l.HasExited) { "launcher still running after 120 s (hung?); stopping PID $($l.Id)"; $l.Kill(); $fails++ }
foreach ($id in $kids.Keys) { Get-Process -Id $id -ErrorAction SilentlyContinue | ForEach-Object { "LEFTOVER $($kids[$id]) $id"; $_.Kill() } }
$log = Get-Content "$Root\data\launcher.log"
$log | Where-Object { $_ -match 'SCRIPT (FAIL|done)|embed:' } | Out-Host
if (($log -match 'SCRIPT done, 0 failed').Count -ne 1) { $fails++ }
if (($log -match 'sized its own window').Count -lt 1) { "the game's own resize was never seen"; $fails++ }
"e2: $fails failed"
