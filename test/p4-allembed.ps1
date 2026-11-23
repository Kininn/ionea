# p4: every standalone emulator plays inside the launcher, and the launcher writes its controls.
# Fake Lichii (TestGame.exe named lichii.exe, kept open 40 s) with a copy of the real Lichii qt-config.ini,
# a fake Switch 2 game; fake Dolphin / melonDS / mGBA / Azahar with copies of their real control files.
# Checks: the Lichii game is taken into the launcher, its Home menu opens and closes the game, its controls
# land in its qt-config.ini (and change with the launcher's), the other writers, and the "Play inside
# Ionea" switch. Windowed, muted, no real input, never takes the front by itself. The real emulators'
# files are only read (copied).
# Usage: p4-allembed.ps1 [-Exe <Launcher.exe>] [-Root <dir>] [-Esde <ES-DE folder>]   (wipes <dir>; -Esde defaults to IONEA_ESDE)
param([string]$Exe = "$PSScriptRoot\..\bin\Release\net10.0-windows\Launcher.exe", [string]$Root = "$env:TEMP\Ionea-tests\allembed",
      [string]$Esde = $env:IONEA_ESDE)
$ErrorActionPreference = 'Stop'
$game = Join-Path $PSScriptRoot 'bin\TestGame.exe'
if (-not (Test-Path $game)) { throw "Build test\bin\TestGame.exe first (see README)." }
if (-not $Esde) { throw 'Give -Esde <ES-DE folder> or set IONEA_ESDE (its Emulators folder holds the real control files).' }
$real = Join-Path $Esde 'Emulators'
if (Test-Path $Root) { Remove-Item $Root -Recurse -Force }
$e = "$Root\emus"
foreach ($d in "$Root\esde\ROMs\switch2", "$Root\data", "$e\Lichii\user\config", "$e\Dolphin\User\Config", "$e\melonDS", "$e\mGBA", "$e\Azahar\user\config") { New-Item -ItemType Directory -Force $d | Out-Null }
Copy-Item $game "$e\Lichii\lichii.exe"; Copy-Item "$real\Lichii\user\config\qt-config.ini" "$e\Lichii\user\config\"
Copy-Item $game "$e\Dolphin\Dolphin.exe"; Copy-Item "$real\Dolphin\User\Config\GCPadNew.ini" "$e\Dolphin\User\Config\"; Set-Content "$e\Dolphin\portable.txt" ''
Copy-Item $game "$e\melonDS\melonDS.exe"; Copy-Item "$real\melonDS\melonDS.toml" "$e\melonDS\"
Copy-Item $game "$e\mGBA\mGBA.exe"; Copy-Item "$real\mGBA\config.ini" "$e\mGBA\"; Set-Content "$e\mGBA\portable.ini" ''
Copy-Item $game "$e\Azahar\azahar.exe"; Copy-Item "$real\Azahar\user\config\qt-config.ini" "$e\Azahar\user\config\"
Set-Content -LiteralPath "$Root\esde\ROMs\switch2\Test Zelda.nup" 'not a real game'

$data = "$Root\data"
$s = "$Root\p4.txt"
Set-Content $s -Encoding utf8 @(
  'wait 2500', 'type Tester', 'key accept', 'wait 900', 'key accept', 'wait 2500', 'emulog',
  'emucontrols Lichii|on', 'emucontrols Dolphin|on', 'emucontrols melonDS|on', 'emucontrols mGBA|on', 'emucontrols Azahar|on',
  'expect canembed Lichii|yes', 'expect canembed Dolphin|yes', 'expect canembed mGBA|yes',
  'addchoice', 'wait 500', 'key accept', 'wait 700', 'key accept', 'wait 800', 'key accept', 'wait 1200', 'expect games 1',
  'expect managed Test Zelda|yes',
  'select Test Zelda', 'wait 500', 'key accept', 'waitshown 30', 'wait 3000', 'expect ingame Test Zelda', 'expect embedded yes',
  'expect filehas T\emus\Lichii\user\config\qt-config.ini|player_0_button_a="engine:keyboard,code:69,toggle:0"',
  'expect filehas T\emus\Lichii\user\config\qt-config.ini|player_0_button_b="engine:keyboard,code:32,toggle:0"',
  'expect filehas T\emus\Lichii\user\config\qt-config.ini|player_0_button_plus="engine:keyboard,code:16777220,toggle:0"',
  'expect filehas T\emus\Lichii\user\config\qt-config.ini|up:engine$0keyboard$1code$087$1toggle$00',
  'expect filehas T\emus\Lichii\user\config\qt-config.ini|player_0_button_dup="engine:keyboard,code:49,toggle:0"',
  'expect filehas T\emus\Lichii\user\config\qt-config.ini|check_for_updates=false',
  'expect filehas T\emus\Lichii\user\config\qt-config.ini|player_0_rstick="engine:mouse,axis_x:0,axis_y:1,threshold:0.500000,range:0.666667,deadzone:0.000000,port:0"',
  'expect filehas T\emus\Lichii\user\config\qt-config.ini|mouse_panning_min_decay=30', 'expect filehas T\emus\Lichii\user\config\qt-config.ini|mouse_panning_decay_strength=0',
  'expect filehas T\emus\Lichii\user\config\qt-config.ini|mouse_panning_deadzone_counterweight=13',
  'expect filehas T\emus\Lichii\user\config\qt-config.ini|mouse_panning_x_sensitivity=50', 'expect filehas T\emus\Lichii\user\config\qt-config.ini|mouse_enabled=false',
  'expect filehas T\emus\Lichii\user\config\qt-config.ini|Shortcuts\Main%20Window\Toggle%20Mouse%20Panning\KeySeq=F3',
  'expect panning no',
  'shot p4-ingame',
  'homebutton', 'wait 800', 'expect screen HomeMenuCard', 'expect paused yes', 'shot p4-menu',
  'key back', 'wait 800', 'expect paused no', 'expect embedded yes',
  'homebutton', 'wait 800', 'cardrow Restart the game', 'key accept', 'wait 4000', 'waitshown 30', 'wait 3000',
  'expect ingame Test Zelda', 'expect embedded yes', 'expect paused no',
  'controls switch2 Test Zelda', 'expect ctlpending no', 'setkey east L', 'setkey ls_up MouseLeft', 'setmousecam off', 'expect ctlpending yes',
  'homebutton', 'wait 800', 'cardrow Restart for the new controls', 'key accept', 'wait 4000', 'waitshown 30', 'wait 3000',
  'expect ingame Test Zelda', 'expect embedded yes', 'expect ctlpending no',
  'expect filehas T\emus\Lichii\user\config\qt-config.ini|player_0_rstick="engine:analog_from_button,up:engine$0keyboard$1code$016777235',
  'expect filehas T\emus\Lichii\user\config\qt-config.ini|player_0_button_a="engine:keyboard,code:76,toggle:0"',
  'expect filehas T\emus\Lichii\user\config\qt-config.ini|up:engine$0mouse$1port$00$1button$00',
  'homebutton', 'wait 800', 'cardrow Close game', 'key accept', 'waitlaunch 20', 'expect ingame none',
  'controls gc', 'writecontrols Dolphin',
  'expect filehas T\emus\Dolphin\User\Config\GCPadNew.ini|Buttons/A = `E` | `XInput/0/Gamepad:Button A`',
  'expect filehas T\emus\Dolphin\User\Config\GCPadNew.ini|Main Stick/Up = `W` | `XInput/0/Gamepad:Left Y+`',
  'expect filehas T\emus\Dolphin\User\Config\GCPadNew.ini|C-Stick/Left = `LEFT` | `XInput/0/Gamepad:Right X-` | (`Axis X-` * 1.30)',
  'expect filehas T\emus\Dolphin\User\Config\GCPadNew.ini|C-Stick/Up = `UP` | `XInput/0/Gamepad:Right Y+` | (`Axis Y-` * 1.30)',
  'expect filehas T\emus\Dolphin\User\Config\GCPadNew.ini|Triforce/Coin = `3`',
  'controls nds', 'writecontrols melonDS',
  'expect filehas T\emus\melonDS\melonDS.toml|A = 69',
  'expect filehas T\emus\melonDS\melonDS.toml|Up = 87',
  'controls gba', 'writecontrols mGBA',
  'expect filehas T\emus\mGBA\config.ini|keyA=69',
  'expect filehas T\emus\mGBA\config.ini|fullscreen=1',
  'controls 3ds', 'writecontrols Azahar',
  'expect filehas T\emus\Azahar\user\config\qt-config.ini|profiles\1\button_a="code:69,engine:keyboard"',
  'expect filehas T\emus\Azahar\user\config\qt-config.ini|modifier:code$0-1$1engine$0keyboard',
  'inside Lichii|off', 'expect canembed Lichii|no', 'inside Lichii|on', 'expect canembed Lichii|yes',
  'quit')
$env:IONEA_TESTGAME_SECS = '40'
$env:IONEA_TESTROOT = $Root
$l = Start-Process $Exe -ArgumentList "--data `"$data`" --no-import --esde `"$Root\esde`" --emulators `"$e`" --windowed --mute --size 1040x620 --pos -30000,-30000 --script `"$s`" --shots `"$Root\shots`"" -PassThru
$kids = @{}; $t0 = Get-Date; $grouped = $false
while (-not $l.HasExited -and ((Get-Date) - $t0).TotalSeconds -lt 200) {
  Get-CimInstance Win32_Process -Filter "ParentProcessId=$($l.Id) and Name<>'conhost.exe'" | ForEach-Object {
    $kids[$_.ProcessId] = $_.Name
    if ($_.ExecutablePath -like "$e\Lichii\*" -and $_.Name -eq 'Launcher.exe') { $grouped = $true }
  }
  Start-Sleep -Milliseconds 200
}
$fails = 0
if (-not $l.HasExited) { "launcher still running after 200 s (hung?); stopping PID $($l.Id)"; $l.Kill(); $fails++ }
foreach ($id in $kids.Keys) { Get-Process -Id $id -ErrorAction SilentlyContinue | ForEach-Object { "LEFTOVER $($kids[$id]) $id"; $_.Kill() } }
if (-not $grouped) { "FAIL: the Lichii game did not run as Launcher.exe (Task Manager grouping)"; $fails++ } else { "ok: the Lichii game ran as Launcher.exe from its own folder" }
$log = Get-Content "$data\launcher.log"
$log | Where-Object { $_ -match 'SCRIPT (FAIL|done|writecontrols)|controls: wrote|paused|resumed|embed: "|ended|Esc' } | Out-Host
if (($log -match 'SCRIPT done, 0 failed').Count -ne 1) { $fails++ }
"p4: $fails failed"
