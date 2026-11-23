# u1: the first-start tutorial (Tutorial.cs): offered once on Home, Settings > Library > Emulators > + Add emulator,
# then + > Add game > Game file > Add, then play it and open the Home menu, then the done card. Replayed from
# Settings > Look and layout with an emulator already there; Back on Home asks to leave.
# Fake melonDS = TestGame.exe kept open 20 s. Windowed, off-screen, muted, no real input.
# Usage: u1-tutorial.ps1 [-Exe <Launcher.exe>] [-Root <dir>]   (wipes <dir>)
param([string]$Exe = "$PSScriptRoot\..\bin\Release\net10.0-windows\Launcher.exe", [string]$Root = "$env:TEMP\Ionea-tests\tutorial")
$ErrorActionPreference = 'Stop'
$game = Join-Path $PSScriptRoot 'bin\TestGame.exe'
if (Test-Path $Root) { Remove-Item $Root -Recurse -Force }
foreach ($d in 'data', 'emus', 'pick\melonDS', 'roms') { New-Item -ItemType Directory -Force "$Root\$d" | Out-Null }
Copy-Item $game "$Root\pick\melonDS\melonDS.exe"
Set-Content -LiteralPath "$Root\roms\Pocket Test (USA).nds" 'not a real game'
$s = "$Root\u1.txt"
Set-Content $s -Encoding utf8 @(
  'wait 2500', 'type Tester', 'key accept', 'wait 900', 'key accept', 'wait 3000',
  'expect screen TutorialCard', 'expect tutorialasked yes', 'shot u1-welcome', 'key accept', 'wait 500',
  'expect tutorial emulator', 'expect coach gear', 'shot u1-gear',
  'key menu', 'wait 600', 'expect screen SettingsCard', 'tutorial tick', 'expect coach rail:Library', 'shot u1-rail',
  'closebutton', 'wait 500', 'settings Library', 'wait 600', 'tutorial tick', 'expect coach row:Emulators and ports', 'shot u1-library',
  'key right', 'wait 300', 'cardrow Emulators and ports', 'key accept', 'wait 600', 'expect screen EmulatorsCard', 'tutorial tick', 'expect coach row:+ Add emulator', 'shot u1-emulators',
  'cardrow + Add emulator', 'key accept', 'wait 600', 'expect screen AddEmulatorCard', 'tutorial tick', 'expect coach none', 'shot u1-add-emulator',
  "addemu $Root\pick\melonDS\melonDS.exe", 'wait 800', 'tutorial tick', 'expect emus 1', 'expect tutorial game', 'expect coach close', 'shot u1-back-home',
  'closebutton', 'wait 700', 'expect screen HomeScreen', 'tutorial tick', 'expect coach add', 'shot u1-plus',
  'key accept', 'wait 600', 'expect screen AddMenuCard', 'tutorial tick', 'expect coach row:Game',
  'cardrow Game', 'key accept', 'wait 600', 'expect screen AddChoiceCard', 'tutorial tick', 'expect coach row:Game file', 'shot u1-game-file',
  'key back', 'wait 500', "addrom $Root\roms\Pocket Test (USA).nds", 'wait 800', 'expect screen AddRomCard', 'tutorial tick', 'expect coach row:Add', 'shot u1-add-game',
  'cardrow Add', 'key accept', 'wait 900', 'expect games 1', 'expect screen HomeScreen', 'tutorial tick', 'expect tutorial play',
  'expect coach game:Pocket Test', 'shot u1-play',
  'key accept', 'waitshown 30', 'wait 1500', 'tutorial tick', 'expect tutorial ingame',
  'homebutton', 'wait 800', 'expect screen HomeMenuCard', 'tutorial tick', 'expect coach none', 'shot u1-home-menu',
  'cardrow Close game', 'key accept', 'waitlaunch 30', 'wait 1500', 'expect tutorial off', 'expect screen TutorialDoneCard', 'shot u1-done',
  'key accept', 'wait 500', 'expect screen HomeScreen',
  # again from Settings, with melonDS already there
  'settings about and updates', 'wait 500', 'key right', 'wait 300', 'cardrow Watch the tutorial', 'key accept', 'wait 500', 'expect tutorial emulator',
  'settings Library', 'wait 600', 'key right', 'wait 300', 'cardrow Emulators and ports', 'key accept', 'wait 600', 'tutorial tick', 'expect coach row:+ Add emulator', 'shot u1-have',
  'closebutton', 'wait 700', 'tutorial tick', 'expect tutorial game', 'expect coach add',
  'key back', 'wait 500', 'expect screen TutorialLeaveCard', 'cardrow Leave', 'key accept', 'wait 400', 'expect tutorial off', 'expect screen HomeScreen',
  'quit')
$env:IONEA_TESTGAME_SECS = '20'
$l = Start-Process $Exe -ArgumentList "--data `"$Root\data`" --no-import --emulators `"$Root\emus`" --tutorial --windowed --mute --size 1040x620 --pos -30000,-30000 --script `"$s`" --shots `"$Root\shots`"" -PassThru
$kids = @{}; $t0 = Get-Date
while (-not $l.HasExited -and ((Get-Date) - $t0).TotalSeconds -lt 150) {
  Get-CimInstance Win32_Process -Filter "ParentProcessId=$($l.Id) and Name<>'conhost.exe'" | ForEach-Object { $kids[$_.ProcessId] = $_.Name }
  Start-Sleep -Milliseconds 200
}
$fails = 0
if (-not $l.HasExited) { "launcher still running after 150 s (hung?); stopping PID $($l.Id)"; $l.Kill(); $fails++ }
foreach ($id in $kids.Keys) { Get-Process -Id $id -ErrorAction SilentlyContinue | ForEach-Object { "LEFTOVER $($kids[$id]) $id"; $_.Kill() } }
$log = Get-Content "$Root\data\launcher.log"
$log | Where-Object { $_ -match 'SCRIPT (FAIL|done)|tutorial' } | Out-Host
if (($log -match 'SCRIPT done, 0 failed').Count -ne 1) { $fails++ }
"u1: $fails failed"
