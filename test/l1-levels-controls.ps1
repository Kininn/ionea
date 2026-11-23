# Levels, own controls, re-reading: makes the test folder, runs
# l1-levels-controls.txt and checks the emulators' own files. Fake emulators are copies of the harmless
# TestGame.exe under the shipped definitions' program names (Eden, Azahar, melonDS, DuckStation) with settings
# files shaped like the real ones. "Open the emulator" starts the fake one with no game, its window inside the
# launcher's (never in front by itself); the script changes its file meanwhile, as its own window would.
# Every game here is made up: a few header bytes, nothing real. Windowed, muted, no real input.
# Usage: l1-levels-controls.ps1 [-Exe <Launcher.exe>] [-Root <dir>]   (wipes <dir>)
param([string]$Exe = "$PSScriptRoot\..\bin\Release\net10.0-windows\Launcher.exe", [string]$Root = "$env:TEMP\Ionea-tests\l1-levels-controls")
$ErrorActionPreference = 'Stop'
$game = Join-Path $PSScriptRoot 'bin\TestGame.exe'
if (Test-Path $Root) { Remove-Item $Root -Recurse -Force }
$T = "$Root\T"; $data = "$Root\data"
foreach ($d in 'emus\Eden', 'emus\Azahar', 'emus\melonDS', 'emus\DuckStation', 'games') { New-Item -ItemType Directory -Force (Join-Path $T $d) | Out-Null }
New-Item -ItemType Directory -Force $data | Out-Null
Copy-Item $game "$T\emus\Eden\eden.exe"
Copy-Item $game "$T\emus\Azahar\azahar.exe"
Copy-Item $game "$T\emus\melonDS\melonDS.exe"
Copy-Item $game "$T\emus\DuckStation\duckstation-qt.exe"
$utf8 = New-Object System.Text.UTF8Encoding($false)
function Put($p, $text) { New-Item -ItemType Directory -Force (Split-Path $p) | Out-Null; [IO.File]::WriteAllText($p, ($text -replace "`r`n", "`n"), $utf8) }

# ---- settings files as the emulators write them ----
$edenFile = "$T\emus\Eden\user\config\qt-config.ini"
Put $edenFile @"
[Controls]
player_0_button_a\default=true
player_0_button_a="engine:keyboard,code:67,toggle:0"
player_0_lstick\default=true
player_0_lstick="engine:analog_from_button,up:engine`$0keyboard`$1code`$087`$1toggle`$00,down:engine`$0keyboard`$1code`$083`$1toggle`$00,left:engine`$0keyboard`$1code`$065`$1toggle`$00,right:engine`$0keyboard`$1code`$068`$1toggle`$00,modifier:engine`$0keyboard`$1code`$00`$1toggle`$00,modifier_scale:0.500000"

[Renderer]
resolution_setup\default=true
resolution_setup=3
backend\default=true
backend=1
"@
Put "$T\emus\Azahar\user\config\qt-config.ini" @"
[Controls]
profile\default=true
profile=0
profiles\1\button_a\default=false
profiles\1\button_a="button:1,engine:sdl,guid:030000005e040000ea02000000007801,port:0"
profiles\1\circle_pad\default=true
profiles\1\circle_pad="down:code`$083`$1engine`$0keyboard,engine:analog_from_button,left:code`$065`$1engine`$0keyboard,modifier:code`$068`$1engine`$0keyboard,modifier_scale:0.500000,right:code`$068`$1engine`$0keyboard,up:code`$087`$1engine`$0keyboard"

[Renderer]
resolution_factor\default=true
resolution_factor=1
"@
Put "$T\emus\melonDS\melonDS.toml" @"
LimitFPS = true

[Instance0]
JoystickID = -1

[Instance0.Keyboard]
A = 75
B = 74

[Instance0.Joystick]
A = -1
B = -1
"@
$edenBefore = [IO.File]::ReadAllText($edenFile)

[IO.File]::WriteAllBytes("$T\games\Test Quest [0100ABCD00010000][v0].nsp", [byte[]]::new(512))

# Run: the fake emulator stays open 40 s unless closed (closegame / Home / closeemu close it at once).
$env:IONEA_TESTROOT = $T; $env:IONEA_TESTGAME_SECS = '40'
$fails = 0
function Check($ok, $what) { if ($ok) { "ok   $what" } else { "FAIL $what"; $script:fails++ } }
$a = "--data `"$data`" --no-import --emulators `"$T\emus`" --windowed --mute --size 1040x620 --pos -30000,-30000 --shots `"$data\shots`" --script `"$PSScriptRoot\l1-levels-controls.txt`""
$p = Start-Process $Exe -ArgumentList $a -PassThru
$kids = @{}; $t0 = Get-Date
while (-not $p.HasExited -and ((Get-Date) - $t0).TotalSeconds -lt 300) {
    Get-CimInstance Win32_Process -Filter "ParentProcessId=$($p.Id) and Name<>'conhost.exe'" | ForEach-Object { $kids[$_.ProcessId] = $_.Name }
    Start-Sleep -Milliseconds 250
}
if (-not $p.HasExited) { "launcher still running; stopping PID $($p.Id)"; $p.Kill(); $fails++ }
foreach ($id in $kids.Keys) { Get-Process -Id $id -ErrorAction SilentlyContinue | ForEach-Object { "LEFTOVER $($kids[$id]) $id"; $_.Kill(); $script:fails++ } }
$env:IONEA_TESTGAME_SECS = $null
$log = Get-Content "$data\launcher.log"
$log | Where-Object { $_ -match 'SCRIPT (FAIL|done|mark|writecontrols)|controls: wrote|opened emulator|closed; reading|manages controls|limits:|embed: Eden|to open' } | Out-Host
Check (($log -match 'SCRIPT done, 0 failed').Count -eq 1) 'script 0 failed'

# a start with the switch off wrote nothing; with it on, it did
$off = [array]::IndexOf($log, ($log -match 'SCRIPT mark started-off')[0]); $on = [array]::IndexOf($log, ($log -match 'SCRIPT mark started-on')[0])
Check ($off -gt 0 -and -not ($log[0..$off] -match 'Eden controls: wrote')) 'switch off: the start left Eden''s controls file alone'
Check ($log -match 'start: Eden limits: its own controls') 'switch off: the start says its own controls'
Check ($on -gt $off -and ($log[$off..$on] -match 'Eden controls: wrote')) 'switch on: the start wrote them'
Check (Test-Path "$edenFile.before-launcher") 'switch on: the file as it was, kept once'
Check ([IO.File]::ReadAllText("$edenFile.before-launcher") -eq $edenBefore) 'the kept file is the one from before any start'

# set up for the controller: the XInput controller's buttons (SDL's XInput layout, values to verify on real ones)
$eden = Get-Content -Raw $edenFile
Check ($eden.Contains('player_0_button_a="engine:sdl,port:0,guid:030000005e0400008e02000000007801,button:1"')) 'Eden: A is the controller''s right button (SDL XInput id)'
Check ($eden.Contains('player_0_lstick="engine:sdl,port:0,guid:030000005e0400008e02000000007801,axis_x:0,axis_y:1,')) 'Eden: left stick axes 0 / 1'
Check ($eden.Contains("[Renderer]`nresolution_setup\default=false`nresolution_setup=6")) 'Eden: the value changed while it was open is still there'
# No key is -1, never code 0 (Qt's code for the unassigned key that quiets an Alt tap)
$code0 = '(:|\$0)keyboard(,|\$1)code(:|\$0)0(,|\$1|")|code(:|\$0)0(,|\$1)engine(:|\$0)keyboard'
Check (-not ($eden -match "(?m)^player_0_.*($code0)")) 'Eden: no button or direction of player 1 on code 0'
$az = Get-Content -Raw "$T\emus\Azahar\user\config\qt-config.ini"
Check (-not ($az -match "(?m)^profiles\\1\\.*($code0)")) 'Azahar: no button or direction on code 0'
Check ($az.Contains('profiles\1\button_a="button:1,engine:sdl,guid:030000005e040000ea02000000007801,port:0"')) 'Azahar: the XInput controller id it already used'
Check ($az.Contains('profiles\1\circle_pad="axis_x:0,axis_y:1,deadzone:0.100000,engine:sdl,guid:030000005e040000ea02000000007801,port:0"')) 'Azahar: circle pad on the left stick'
$md = Get-Content -Raw "$T\emus\melonDS\melonDS.toml"
Check ($md.Contains("[Instance0]`nJoystickID = 0")) 'melonDS: joystick 0'
Check ($md.Contains("[Instance0.Joystick]`nA = 1`nB = 0`n")) 'melonDS: A and B on the controller''s right and bottom buttons'
Check ($md.Contains("[Instance0.Keyboard]`nA = ")) 'melonDS: keys stay alongside'
"l1: $fails failed"
