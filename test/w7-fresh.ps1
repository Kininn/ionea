# w7: a fresh install, the way someone who just downloaded the source gets it (see w7-fresh.txt).
# Builds with Install-Launcher.ps1 -BuildOnly into an empty folder and starts that exe with NO --data (its default
# data folder), an empty home for the scan, no Steam, no ES-DE, no Inbox and no emulators folder. Everything it
# needs is made here: two tiny GBA programs (a cartridge header and a loop), a Nintendo DS file, an emulator
# stand-in (the harmless TestGame.exe named melonDS.exe) and a native port stand-in (TestGame.exe as testport.exe,
# described by a user definition, so it is a port added after the fact). The mGBA core is "downloaded" from a local
# server (http://localhost:47817) that serves a zip of a copy of a real core (-Core). Windowed, muted, off screen,
# no real input, nothing leaves this PC.
# Usage: w7-fresh.ps1 -Core <mgba_libretro.dll> [-Root <dir>] [-NoBuild]   (wipes <dir>)
param([string]$Core = $env:IONEA_TEST_MGBA_CORE, [string]$Root = "$env:TEMP\Ionea-tests\w7-fresh", [switch]$NoBuild)
$ErrorActionPreference = 'Stop'
if (-not $Core -or -not (Test-Path $Core)) { "Give -Core <path to mgba_libretro.dll> (or IONEA_TEST_MGBA_CORE)"; exit 1 }
$port = 47817
$game = Join-Path $PSScriptRoot 'bin\TestGame.exe'
$app = "$Root\app"
if ($NoBuild -and (Test-Path "$app\Launcher.exe")) { Get-ChildItem $Root -Exclude app | Remove-Item -Recurse -Force; Remove-Item "$app\data" -Recurse -Force -ErrorAction SilentlyContinue }
else {
    if (Test-Path $Root) { Remove-Item $Root -Recurse -Force }
    & "$PSScriptRoot\..\Install-Launcher.ps1" -BuildOnly $app -BuildNumber 0 | Out-Host
}
$data = "$app\data"; $T = $Root; $www = "$Root\www"; $served = "$Root\served.txt"; $ready = "$Root\ready.txt"; $shots = "$Root\shots"
New-Item -ItemType Directory -Force "$Root\home", "$Root\nosteam", "$Root\roms", "$Root\emus\melonDS", "$Root\ports\testport", $www, $shots | Out-Null
if (Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue) { "port $port is in use"; exit 1 }
$utf8 = New-Object System.Text.UTF8Encoding($false)

# the emulator and port stand-ins
Copy-Item $game "$Root\emus\melonDS\melonDS.exe"
Copy-Item $game "$Root\ports\testport\testport.exe"
# A GBA cartridge header (logo, title, game code, checksum) and an endless loop: enough for the core to run.
function New-Rom([string]$name, [string]$title, [string]$code) {
  $rom = New-Object byte[] 512
  [byte[]]$logo = 0x24,0xFF,0xAE,0x51,0x69,0x9A,0xA2,0x21,0x3D,0x84,0x82,0x0A,0x84,0xE4,0x09,0xAD,0x11,0x24,0x8B,0x98,0xC0,0x81,0x7F,0x21,0xA3,0x52,0xBE,0x19,0x93,0x09,0xCE,0x20,0x10,0x46,0x4A,0x4A,0xF8,0x27,0x31,0xEC,0x58,0xC7,0xE8,0x33,0x82,0xE3,0xCE,0xBF,0x85,0xF4,0xDF,0x94,0xCE,0x4B,0x09,0xC1,0x94,0x56,0x8A,0xC0,0x13,0x72,0xA7,0xFC,0x9F,0x84,0x4D,0x73,0xA3,0xCA,0x9A,0x61,0x58,0x97,0xA3,0x27,0xFC,0x03,0x98,0x76,0x23,0x1D,0xC7,0x61,0x03,0x04,0xAE,0x56,0xBF,0x38,0x84,0x00,0x40,0xA7,0x0E,0xFD,0xFF,0x52,0xFE,0x03,0x6F,0x95,0x30,0xF1,0x97,0xFB,0xC0,0x85,0x60,0xD6,0x80,0x25,0xA9,0x63,0xBE,0x03,0x01,0x4E,0x38,0xE2,0xF9,0xA2,0x34,0xFF,0xBB,0x3E,0x03,0x44,0x78,0x00,0x90,0xCB,0x88,0x11,0x3A,0x94,0x65,0xC0,0x7C,0x63,0x87,0xF0,0x3C,0xAF,0xD6,0x25,0xE4,0x8B,0x38,0x0A,0xAC,0x72,0x21,0xD4,0xF8,0x07
  [Array]::Copy($logo, 0, $rom, 4, $logo.Length)
  [byte[]]$b = 0x2E,0x00,0x00,0xEA; [Array]::Copy($b, 0, $rom, 0, 4)          # b 0xC0 (past the header)
  [byte[]]$loop = 0xFE,0xFF,0xFF,0xEA; [Array]::Copy($loop, 0, $rom, 0xC0, 4)  # b . (forever)
  $t = [Text.Encoding]::ASCII.GetBytes($title); [Array]::Copy($t, 0, $rom, 0xA0, $t.Length)
  if ($code) { $c4 = [Text.Encoding]::ASCII.GetBytes($code); [Array]::Copy($c4, 0, $rom, 0xAC, 4) }
  $rom[0xB2] = 0x96
  $c = 0; for ($i = 0xA0; $i -le 0xBC; $i++) { $c = ($c - $rom[$i]) -band 0xFF }; $rom[0xBD] = ($c - 0x19) -band 0xFF
  [IO.File]::WriteAllBytes("$Root\roms\$name.gba", $rom)
}
New-Rom "Game One" "GAMEONE     " $null
New-Rom "Port Game" "PORTGAME    " "TPGE"
$nds = New-Object byte[] 512; $nds[0x15C] = 0x56; $nds[0x15D] = 0xCF; [IO.File]::WriteAllBytes("$Root\roms\Small DS.nds", $nds)

# a native port the user describes in their own definitions folder (data\definitions): plays TPGE natively,
# keeps its saves in user\ per profile. Nothing in Ionea knows it.
$defs = "$data\definitions\ports"; New-Item -ItemType Directory -Force $defs | Out-Null
[IO.File]::WriteAllText("$defs\test-port.json", @'
{
  "id": "test-port",
  "name": "Port Game (testport)",
  "kind": "port",
  "detect": { "programs": [ "testport.exe" ] },
  "start": { "args": "\"{rom}\"", "portable": [ "user\\" ] },
  "saves": { "folders": [ "user" ] },
  "port": { "kind": "decomp", "games": [ { "system": "gba", "name": "Port Game", "ids": [ "TPGE" ] } ] }
}
'@, $utf8)

# the user's own systems.json: PS3 games get the mouse camera too (a definition field, no code list)
[IO.File]::WriteAllText("$data\definitions\systems.json", '[ { "id": "ps3", "mouseCamera": true } ]', $utf8)

# the local "buildbot": the mGBA core's fixed release address, under /release/<host><path>
$rel = "$www\release\buildbot.libretro.com\nightly\windows\x86_64\latest"
New-Item -ItemType Directory -Force "$Root\stage", $rel | Out-Null
Copy-Item $Core "$Root\stage\mgba_libretro.dll"
& "$env:SystemRoot\System32\tar.exe" -a -cf "$rel\mgba_libretro.dll.zip" -C "$Root\stage" 'mgba_libretro.dll'

$job = Start-Job -ArgumentList $port, $served, $ready, $www -ScriptBlock {
    param($port, $served, $ready, $www)
    $l = New-Object System.Net.HttpListener
    $l.Prefixes.Add("http://localhost:$port/")
    $l.Start()
    Set-Content $ready 'ready'
    while ($l.IsListening) {
        $c = $l.GetContext()
        $p = [Uri]::UnescapeDataString($c.Request.Url.AbsolutePath)
        Add-Content $served $p
        $r = $c.Response
        $f = Join-Path $www ($p.TrimStart('/') -replace '/', '\')
        if (Test-Path -LiteralPath $f -PathType Leaf) { $b = [IO.File]::ReadAllBytes($f) } else { $r.StatusCode = 404; $b = @() }
        if ($b.Length) { $r.OutputStream.Write($b, 0, $b.Length) }
        $r.Close()
    }
}
$fails = 0
function Check($ok, $what) { if ($ok) { "ok   $what" } else { "FAIL $what"; $script:fails++ } }
try {
    $until = (Get-Date).AddSeconds(20)
    while (-not (Test-Path $ready) -and (Get-Date) -lt $until -and $job.State -eq 'Running') { Start-Sleep -Milliseconds 200 }
    if (-not (Test-Path $ready)) { "server did not start: $($job.State)"; exit 1 }
    $env:IONEA_TESTROOT = $T
    $env:IONEA_TESTGAME_SECS = '60'; $env:IONEA_TESTGAME_OFFSCREEN = '1'; $env:IONEA_TESTGAME_REPORT = "$Root\starts.txt"
    # no --data, --emulators, --inbox, --esde or --no-import: the defaults a new user gets
    $a = "--windowed --mute --size 1040x620 --pos -30000,-30000 --shots `"$shots`" --scan-home `"$Root\home`" --steam `"$Root\nosteam`"" +
         " --online-prompt --scan-prompt --setup --online-test-host localhost:$port --script `"$PSScriptRoot\w7-fresh.txt`""
    $p = Start-Process "$app\Launcher.exe" -ArgumentList $a -WorkingDirectory $app -PassThru
    if (-not $p.WaitForExit(300000)) { "launcher still running; stopping it"; $p.Kill(); $fails++ }
    $log = Get-Content "$data\launcher.log"
    $log | Where-Object { $_ -match 'SCRIPT (FAIL|done)|start |paths:|scan:|emulator (installed|download)|ports:|switch|home menu: (closing|switching)|pause|saves|error' } | Out-Host
    Check (($log -match 'SCRIPT done, 0 failed').Count -eq 1) 'script 0 failed'
    Check (($log -match ' error: ').Count -eq 0) 'no unhandled errors'
    Check (Test-Path "$data\settings.json") 'data folder next to the program'
    Check (Test-Path "$data\emulators") "Ionea's own emulators folder was made"
    Check ((Get-ChildItem "$data\emulators" -Recurse -Filter mgba_libretro.dll).Count -eq 1) 'the downloaded core is in it'
    $starts = @(if (Test-Path "$Root\starts.txt") { Get-Content "$Root\starts.txt" })
    # (each program runs through a link named like Ionea, so the report names Launcher.exe)
    "starts:"; $starts | ForEach-Object { "   $_" }
    Check (@($starts | Where-Object { $_ -like 'Launcher.exe|*Small DS.nds|' }).Count -ge 1) 'the emulator got its game'
    Check (@($starts | Where-Object { $_ -like 'Launcher.exe|*Port Game.gba*|' }).Count -ge 1) 'the port got its game'
    $u = Get-Item "$Root\ports\testport\user" -ErrorAction SilentlyContinue
    Check ($u -and $u.LinkType -eq 'Junction' -and $u.Target -like "$data\*") "the port's saves are the profile's ($($u.LinkType) -> $($u.Target))"
    $got = @(if (Test-Path $served) { Get-Content $served })
    "served:"; $got | ForEach-Object { "   $_" }
    Check (@($got | Where-Object { $_ -like '/release/buildbot.libretro.com/*mgba_libretro.dll.zip' }).Count -eq 1) 'the core came from its release address'
}
finally {
    Stop-Job $job -ErrorAction SilentlyContinue; Remove-Job $job -Force -ErrorAction SilentlyContinue
    Get-Process TestGame, melonDS, testport -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$Root\*" } | Stop-Process -Force -ErrorAction SilentlyContinue
}
"w7-fresh: $fails failed"
exit $fails
