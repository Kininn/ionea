# Shared video settings: makes the test folder, runs v2-video-settings.txt
# and checks the emulators' own files. Fake emulators are copies of the harmless TestGame.exe under the shipped
# definitions' program names (Eden, Lichii, Azahar, Dolphin, melonDS, mGBA) with settings files shaped like the
# real ones; a made-up melonDS DS core (not a real library: it lists no settings). Every game here is made up:
# a few header bytes, nothing real.
# Usage: v2-video-settings.ps1 [-Exe <Launcher.exe>] [-Root <dir>]   (wipes <dir>)
param([string]$Exe = "$PSScriptRoot\..\bin\Release\net10.0-windows\Launcher.exe", [string]$Root = "$env:TEMP\Ionea-tests\v2-video-settings")
$ErrorActionPreference = 'Stop'
$game = Join-Path $PSScriptRoot 'bin\TestGame.exe'
if (Test-Path $Root) { Remove-Item $Root -Recurse -Force }
$T = "$Root\T"; $data = "$Root\data"
foreach ($d in 'emus\Eden', 'emus\Lichii', 'emus\Azahar', 'emus\Dolphin', 'emus\melonDS', 'emus\mGBA', 'emus\Cores', 'games') {
    New-Item -ItemType Directory -Force (Join-Path $T $d) | Out-Null
}
New-Item -ItemType Directory -Force $data | Out-Null
Copy-Item $game "$T\emus\Eden\eden.exe"
Copy-Item $game "$T\emus\Lichii\lichii.exe"
Copy-Item $game "$T\emus\Azahar\azahar.exe"
Copy-Item $game "$T\emus\Dolphin\Dolphin.exe"
Copy-Item $game "$T\emus\melonDS\melonDS.exe"
Copy-Item $game "$T\emus\mGBA\mGBA.exe"
[IO.File]::WriteAllBytes("$T\emus\Cores\melondsds_libretro.dll", [byte[]]::new(4096))   # not a library: the core lists nothing
$utf8 = New-Object System.Text.UTF8Encoding($false)
function Put($p, $text) { New-Item -ItemType Directory -Force (Split-Path $p) | Out-Null; [IO.File]::WriteAllText($p, ($text -replace "`r`n", "`n"), $utf8) }

# ---- settings files as the emulators write them ----
$edenQt = @"
[Controls]
player_0_button_a="engine:keyboard,code:67,toggle:0"

[Core]
use_speed_limit\default=true
use_speed_limit=true
speed_limit\default=true
speed_limit=100

[Renderer]
backend\default=true
backend=1
resolution_setup\default=true
resolution_setup=3
scaling_filter\default=true
scaling_filter=SCALE
use_vsync\default=true
use_vsync=2
anti_aliasing\default=true
anti_aliasing=AA
"@
Put "$T\emus\Eden\user\config\qt-config.ini" ($edenQt -replace 'SCALE', '1' -replace 'AA', '0')
Put "$T\emus\Lichii\user\config\qt-config.ini" ($edenQt -replace 'SCALE', '5' -replace 'AA', '1')
Put "$T\emus\Azahar\user\config\qt-config.ini" @"
[Core]
cpu_clock_percentage\default=true
cpu_clock_percentage=100

[Renderer]
graphics_api\default=false
graphics_api=2
resolution_factor\default=true
resolution_factor=1
frame_limit\default=true
frame_limit=100
"@
Put "$T\emus\Dolphin\User\Config\GFX.ini" @"
[Settings]
InternalResolution = 2
ShaderCache = True
[Hardware]
VSync = False
[Enhancements]
MaxAnisotropy = 0
"@
Put "$T\emus\Dolphin\User\Config\Dolphin.ini" @"
[General]
ISOPaths = 0
[Core]
GFXBackend = D3D12
EmulationSpeed = 1.00000000
"@
Put "$T\emus\melonDS\melonDS.toml" @"
LimitFPS = true
TargetFPS = 60.0

[3D]
Renderer = 0

[Instance0.Window0]
ScreenFilter = true
"@
Put "$T\emus\mGBA\config.ini" @"
[ports.qt]
fpsTarget=60
videoSync=0
"@

# ---- made-up games ----
[IO.File]::WriteAllBytes("$T\games\Test Quest [0100ABCD00010000][v0].nsp", [byte[]]::new(512))
[IO.File]::WriteAllBytes("$T\games\Next Quest [0400ABCD00020000][v0].nsp", [byte[]]::new(512))
function Disc([int]$at, [string]$id) {   # a GameCube disc header at $at: id, the GameCube magic at 0x1C, a title
    $b = [byte[]]::new($at + 0x440)
    [Text.Encoding]::ASCII.GetBytes($id).CopyTo($b, $at)
    [byte[]](0xC2, 0x33, 0x9F, 0x3D) | ForEach-Object -Begin { $i = 0 } -Process { $b[$at + 0x1C + $i++] = $_ }
    [Text.Encoding]::ASCII.GetBytes('Test Disc').CopyTo($b, $at + 0x20)
    , $b
}
[IO.File]::WriteAllBytes("$T\games\Test Disc.iso", (Disc 0 'GTQE01'))
$rvz = Disc 0x58 'GRVP01'; [byte[]](0x52, 0x56, 0x5A, 0x01) | ForEach-Object -Begin { $i = 0 } -Process { $rvz[$i++] = $_ }
[IO.File]::WriteAllBytes("$T\games\Test Wave.rvz", $rvz)
# 3DS: NCSD header (magic at 0x100, partition 0 at 0x120 in 0x200 units), NCCH at 0x200 with the program id.
$c = [byte[]]::new(0x600)
[Text.Encoding]::ASCII.GetBytes('NCSD').CopyTo($c, 0x100); [BitConverter]::GetBytes([uint32]1).CopyTo($c, 0x120)
[Text.Encoding]::ASCII.GetBytes('NCCH').CopyTo($c, 0x300)
[BitConverter]::GetBytes([uint64]0x0004000000ABCD00).CopyTo($c, 0x318)
[Text.Encoding]::ASCII.GetBytes('CTR-P-ABCE').CopyTo($c, 0x350)
[IO.File]::WriteAllBytes("$T\games\Test Pocket.3ds", $c)
# An unpacked game (exefs\main, no [title id] in the name): main.npdm beside it, META then ACI0 at 0x80 with the
# program id at ACI0 + 0x10.
New-Item -ItemType Directory -Force "$T\games\Test Dusk\base\exefs" | Out-Null
[IO.File]::WriteAllBytes("$T\games\Test Dusk\base\exefs\main", [byte[]]::new(512))
$n = [byte[]]::new(0x200)
[Text.Encoding]::ASCII.GetBytes('META').CopyTo($n, 0); [BitConverter]::GetBytes([uint32]0x80).CopyTo($n, 0x70)
[Text.Encoding]::ASCII.GetBytes('ACI0').CopyTo($n, 0x80); [BitConverter]::GetBytes([uint64]0x0400ABCD00030000).CopyTo($n, 0x90)
[IO.File]::WriteAllBytes("$T\games\Test Dusk\base\exefs\main.npdm", $n)
# Its 60 fps mod in Lichii's mod folder (a row on its own Game settings card only).
New-Item -ItemType Directory -Force "$T\emus\Lichii\user\load\0400ABCD00030000\60 FPS (Ionea)\exefs" | Out-Null
[IO.File]::WriteAllBytes("$T\emus\Lichii\user\load\0400ABCD00030000\60 FPS (Ionea)\exefs\TEST.ips", [Text.Encoding]::ASCII.GetBytes('IPS32EEOF'))
[IO.File]::WriteAllBytes("$T\games\Test DS.nds", [byte[]]::new(512))
[IO.File]::WriteAllBytes("$T\games\Test DS Two.nds", [byte[]]::new(512))
[IO.File]::WriteAllBytes("$T\games\Test Adventure.gba", [byte[]]::new(512))

# Run (nothing takes the foreground: the fake melonDS opens a window of its own for 6 s, as other launch tests do).
$env:IONEA_TESTROOT = $T; $env:IONEA_TESTGAME_SECS = '6'
$fails = 0
function Check($ok, $what) { if ($ok) { "ok   $what" } else { "FAIL $what"; $script:fails++ } }
$a = "--data `"$data`" --no-import --emulators `"$T\emus`" --windowed --mute --size 1040x620 --pos -30000,-30000 --shots `"$data\shots`" --script `"$PSScriptRoot\v2-video-settings.txt`""
$p = Start-Process $Exe -ArgumentList $a -PassThru
if (-not $p.WaitForExit(240000)) { "launcher still running; stopping it"; $p.Kill(); $fails++ }
Get-Process melonDS -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$T\*" } | ForEach-Object { "LEFTOVER melonDS $($_.Id)"; $_.Kill(); $fails++ }
$env:IONEA_TESTGAME_SECS = $null
$log = Get-Content "$data\launcher.log"
$log | Where-Object { $_ -match 'SCRIPT (FAIL|done|applyvideo)|tweaks:' } | Out-Host
Check (($log -match 'SCRIPT done, 0 failed').Count -eq 1) 'script 0 failed'
$eden = Get-Content -Raw "$T\emus\Eden\user\config\qt-config.ini"
Check ($eden.Contains("resolution_setup\default=false`nresolution_setup=6`n")) 'Eden: every game, \default then the value'
Check ($eden.Contains("[Controls]`nplayer_0_button_a=")) 'Eden: other sections untouched'
Check (Test-Path "$T\emus\Eden\user\config\qt-config.ini.before-launcher") 'Eden: the file as it was, kept once'
$custom = Get-Content -Raw "$T\emus\Eden\user\config\custom\0100ABCD00010000.ini"
Check ($custom.Contains("resolution_setup\use_global=false`nresolution_setup\default=false`nresolution_setup=8`n")) 'Eden: per game, use_global, \default, the value'
$gfx = Get-Content -Raw "$T\emus\Dolphin\User\Config\GFX.ini"
Check ($gfx.Contains("InternalResolution = 3`n")) 'Dolphin: written as " = "'
$gs = Get-Content -Raw "$T\emus\Dolphin\User\GameSettings\GTQE01.ini"
Check ($gs.Contains("[Video_Settings]`nInternalResolution = 4`n")) 'Dolphin: per game in [Video_Settings]'
$toml = Get-Content -Raw "$T\emus\melonDS\melonDS.toml"
Check ($toml.StartsWith("LimitFPS = true`nTargetFPS = 120.0`n")) 'melonDS: top-level keys stay at the top'
Check (-not (Test-Path "$data\video-applied\melonds.json")) 'melonDS: nothing left applied'
Check (-not $toml.Contains('[3D.GL]') -and $toml.EndsWith("ScreenFilter = true`n")) 'melonDS: the section only Ionea''s value was in is gone again'
"v2: $fails failed"
