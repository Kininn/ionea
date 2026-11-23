# Emulator definitions: makes the test folder, runs t1-definitions.txt
# and checks the log. Fake emulators are copies of the harmless TestGame.exe; "Old Emu" is a copy of
# where.exe (only for its version number; it is never started). Game id files are synthetic headers.
# A second run (t1-definitions-cores.txt) on the same data folder after two core files were dropped in: a hand-made
# core takes its games, an imported one is only listed. The fake cores are text files (never loaded as a core).
# Usage: t1-definitions.ps1 [-Exe <Launcher.exe>] [-Root <dir>]   (wipes <dir>)
param([string]$Exe = "$PSScriptRoot\..\bin\Release\net10.0-windows\Launcher.exe", [string]$Root = "$env:TEMP\Ionea-tests\t1-definitions")
$ErrorActionPreference = 'Stop'
$game = Join-Path $PSScriptRoot 'bin\TestGame.exe'
if (Test-Path $Root) { Remove-Item $Root -Recurse -Force }
$T = "$Root\T"; $data = "$Root\data"
foreach ($d in 'emus\Dolphin', 'emus\Zeta', 'emus\Stella', 'emus\OldEmu', 'emus\OldSaves\Saves', 'emus\NullSys', 'emus\NesEmu', 'roms', 'ids\wiiu\code', 'ids\wiiu\meta', 'ids\Ionea Test [CUSA12345]\sce_sys', 'ids\Ionea Two\sce_sys') {
    New-Item -ItemType Directory -Force (Join-Path $T $d) | Out-Null
}
New-Item -ItemType Directory -Force "$data\definitions\more" | Out-Null
Copy-Item $game "$T\emus\Dolphin\Dolphin.exe"
Copy-Item $game "$T\emus\Zeta\zeta-emu.exe"
Copy-Item $game "$T\emus\Stella\Stella.exe"        # known only from the imported Playnite definition
Copy-Item "$env:SystemRoot\System32\where.exe" "$T\emus\OldEmu\oldemu.exe"
Copy-Item "$env:SystemRoot\System32\where.exe" "$T\emus\OldSaves\oldsave.exe"
Copy-Item $game "$T\emus\NullSys\nullsys.exe"
Copy-Item $game "$T\emus\NesEmu\nesemu.exe"
Set-Content -LiteralPath "$T\emus\OldSaves\Saves\old.sav" 'before'
Set-Content -LiteralPath "$T\roms\NES Test.nes" 'not a real game'
Set-Content -LiteralPath "$T\roms\Zeta Test.nds" 'not a real game'
$utf8 = New-Object System.Text.UTF8Encoding($false)
function Put($p, $text) { [IO.File]::WriteAllText($p, $text, $utf8) }

# The user's own definitions (data\definitions, subfolders too).
Put "$data\definitions\zeta.json" '{ "id": "zeta", "name": "Zeta", "detect": { "programs": ["zeta-emu.exe"] }, "start": { "args": "\"{rom}\"" }, "systems": ["nds"], "saves": { "beside": true } }'
Put "$data\definitions\more\dolphin.json" '{ "id": "dolphin", "name": "Dolphin (user)" }   // only the name changes'
Put "$data\definitions\more\oldemu.json" '{ "id": "oldemu", "name": "Old Emu", "tested": { "min": "99" }, "detect": { "programs": ["oldemu.exe"] }, "start": { "args": "\"{rom}\"" }, "systems": ["psp"], "controls": { "format": "mgba", "file": "config.ini" }, }'
Put "$data\definitions\broken.json" '{ "id": "broken", "name": '
# version outside tested, with save folders and a seed file: saves still per profile, the seed file not written
Put "$data\definitions\more\oldsaves.json" '{ "id": "oldsaves", "name": "Old Saves", "tested": { "min": "99" }, "detect": { "programs": ["oldsave.exe"] }, "start": { "args": "\"{rom}\"", "seed": [{ "file": "seeded.ini", "text": "[x]\na=1\n" }] }, "systems": ["psp"], "saves": { "folders": ["Saves"] } }'
# null where a list or group belongs: loaded empty, or skipped when it has no programs
Put "$data\definitions\nullsys.json" '{ "id": "nullsys", "name": "Null Systems", "detect": { "programs": ["nullsys.exe"] }, "start": null, "systems": null, "extraExts": null, "saves": { "folders": null } }'
Put "$data\definitions\nulldetect.json" '{ "id": "nulldetect", "name": "Null Detect", "detect": null, "start": null, "systems": null }'
# a standalone NES emulator for the cores run
Put "$data\definitions\nesemu.json" '{ "id": "nesemu", "name": "Nes Emu", "detect": { "programs": ["nesemu.exe"] }, "start": { "args": "\"{rom}\"" }, "systems": ["nes"] }'

# Synthetic headers.
function Bytes($n) { New-Object byte[] $n }
function Text($b, $at, $s) { $x = [Text.Encoding]::ASCII.GetBytes($s); [Array]::Copy($x, 0, $b, $at, $x.Length) }
function U16($b, $at, $v) { $x = [BitConverter]::GetBytes([uint16]$v); [Array]::Copy($x, 0, $b, $at, 2) }
function U32($b, $at, $v) { $x = [BitConverter]::GetBytes([uint32]$v); [Array]::Copy($x, 0, $b, $at, 4) }
function Raw($b, $at, [byte[]]$x) { [Array]::Copy($x, 0, $b, $at, $x.Length) }
function Save($p, $b) { [IO.File]::WriteAllBytes($p, $b) }
# param.sfo with string values
function Sfo([System.Collections.Specialized.OrderedDictionary]$kv) {
    $keys = New-Object System.Collections.Generic.List[byte]; $vals = New-Object System.Collections.Generic.List[byte]; $ents = @()
    foreach ($k in $kv.Keys) {
        $v = [Text.Encoding]::UTF8.GetBytes([string]$kv[$k] + "`0"); $max = [int]([math]::Ceiling($v.Length / 4) * 4)
        $ents += , @($keys.Count, $v.Length, $max, $vals.Count)
        $keys.AddRange([Text.Encoding]::ASCII.GetBytes($k + "`0")); $vals.AddRange($v); $vals.AddRange((Bytes ($max - $v.Length)))
    }
    while ($keys.Count % 4) { $keys.Add(0) }
    $kOff = 20 + 16 * $ents.Count; $dOff = $kOff + $keys.Count
    $b = Bytes ($dOff + $vals.Count)
    U32 $b 0 0x46535000; U32 $b 4 0x101; U32 $b 8 $kOff; U32 $b 12 $dOff; U32 $b 16 $ents.Count
    for ($i = 0; $i -lt $ents.Count; $i++) { $e = 20 + 16 * $i; U16 $b $e $ents[$i][0]; U16 $b ($e + 2) 0x0204; U32 $b ($e + 4) $ents[$i][1]; U32 $b ($e + 8) $ents[$i][2]; U32 $b ($e + 12) $ents[$i][3] }
    Raw $b $kOff $keys.ToArray(); Raw $b $dOff $vals.ToArray()
    , $b
}
$b = Bytes 0x200; Text $b 0 'IONEA TEST'; Text $b 0x0C 'ATSE'; $b[0x1E] = 1; Save "$T\ids\test.nds" $b
$b = Bytes 0xC0; Text $b 0xA0 'IONEA GBA'; Text $b 0xAC 'BTSE'; $b[0xB2] = 0x96; $b[0xBC] = 2; Save "$T\ids\test.gba" $b
$b = Bytes 0x150; Raw $b 0x104 ([byte[]](0xCE, 0xED, 0x66, 0x66)); Text $b 0x134 'IONEAGB'; $b[0x14C] = 3; Save "$T\ids\test.gb" $b
$b = Bytes 0x60; Text $b 0 'GTSE01'; $b[7] = 4; Raw $b 0x1C ([byte[]](0xC2, 0x33, 0x9F, 0x3D)); Text $b 0x20 'Ionea Cube'; Save "$T\ids\test.iso" $b
$b = Bytes 0x260; Text $b 0 'WBFS'; Text $b 0x200 'RTSE01'; Raw $b 0x218 ([byte[]](0x5D, 0x1C, 0x9E, 0xA3)); Text $b 0x220 'Ionea Wii'; Save "$T\ids\test.wbfs" $b
$b = Bytes 0x360; Text $b 0x100 'NCSD'; U32 $b 0x120 1; Text $b 0x300 'NCCH'; U16 $b 0x312 5; Raw $b 0x318 ([BitConverter]::GetBytes([uint64]0x0004000000123400)); Text $b 0x350 'CTR-P-ITSE'; Save "$T\ids\test.3ds" $b
Save "$T\ids\Ionea Test [CUSA12345]\eboot.bin" (Bytes 16)
Save "$T\ids\Ionea Test [CUSA12345]\sce_sys\param.sfo" (Sfo ([ordered]@{ APP_VER = '01.05'; TITLE = 'Ionea PS4'; TITLE_ID = 'CUSA12345' }))
Save "$T\ids\Ionea Two\eboot.bin" (Bytes 16)
Save "$T\ids\Ionea Two\sce_sys\param.sfo" (Sfo ([ordered]@{ APP_VER = '01.00'; TITLE = 'Ionea Two'; TITLE_ID = 'CUSA54321' }))
$sfo = Sfo ([ordered]@{ DISC_ID = 'ULUS12345'; DISC_VERSION = '1.01'; TITLE = 'Ionea PSP' })
$b = Bytes (0x28 + $sfo.Length + 16); U32 $b 0 0x50425000; U32 $b 4 0x10000; U32 $b 8 0x28; U32 $b 0x0C (0x28 + $sfo.Length); Raw $b 0x28 $sfo; Save "$T\ids\test.pbp" $b
Save "$T\ids\wiiu\code\ionea.rpx" (Bytes 16)
Put "$T\ids\wiiu\meta\meta.xml" '<?xml version="1.0" encoding="utf-8"?><menu type="complex" access="777"><title_id type="hexBinary" length="8">0005000010ABCD00</title_id><title_version type="unsignedInt" length="4">32</title_version><longname_en type="string" length="512">Ionea U</longname_en></menu>'
Set-Content "$T\ids\not-a-game.nds" 'hello'

# Run (the fake emulator closes after 1 s; nothing takes the foreground).
$env:IONEA_TESTROOT = $T; $env:IONEA_TESTGAME_SECS = $null
$fails = 0
function Check($ok, $what) { if ($ok) { "ok   $what" } else { "FAIL $what"; $script:fails++ } }
$a = "--data `"$data`" --no-import --emulators `"$T\emus`" --windowed --mute --size 1040x620 --pos -30000,-30000 --shots `"$data\shots`" --script `"$PSScriptRoot\t1-definitions.txt`""
$p = Start-Process $Exe -ArgumentList $a -PassThru
if (-not $p.WaitForExit(180000)) { "launcher still running; stopping it"; $p.Kill(); $fails++ }
$log = Get-Content "$data\launcher.log"
$log | Where-Object { $_ -match 'SCRIPT (FAIL|done|emulators)|definitions:' } | Out-Host
Check (($log -match 'SCRIPT done, 0 failed').Count -eq 1) 'script 0 failed'
Check (($log -match 'definitions: \d+ systems, \d+ emulators, \d+ cores').Count -ge 1) 'definitions loaded'
Check (($log -match 'definitions: dolphin changed by .*more\\dolphin\.json').Count -ge 1) 'user file changed a shipped definition'
Check (($log -match 'definitions: could not read .*broken\.json').Count -ge 1) 'broken user file skipped'
Check (($log -match 'definitions: Old Emu \S+ is outside its tested versions \(99-\): Known').Count -ge 1) 'out-of-range version logged'
Check (Test-Path "$T\emus\Dolphin\portable.txt") 'Dolphin made portable (Known level)'
Check (-not (Test-Path "$T\emus\OldEmu\config.ini")) 'nothing written for Old Emu'
Check (($log -match 'definitions: Old Saves \S+ is outside its tested versions \(99-\): Generic').Count -ge 1) 'Old Saves dropped to Generic (logged)'
Check (($log -match 'SCRIPT prepared Old Saves Game for (Tester|Sam)$').Count -ge 3) 'Old Saves saves kept per profile (not shared)'
Check (($log -match 'seed files for Old Saves not written').Count -ge 1) 'seed file skip logged'
Check (-not (Test-Path "$T\emus\OldSaves\seeded.ini")) 'no seed file written for Old Saves'
Check (($log -match 'definitions: nulldetect has no name or programs').Count -ge 1) 'user file with null detect skipped'
Check (($log -match 'not added:').Count -eq 0) 'no emulator failed to add'

# Second run: two cores dropped in, one hand-made (melonDS DS), one imported (FCEUmm).
New-Item -ItemType Directory -Force "$T\emus\Cores" | Out-Null
Set-Content -LiteralPath "$T\emus\Cores\melondsds_libretro.dll" 'not a real core'
Set-Content -LiteralPath "$T\emus\Cores\fceumm_libretro.dll" 'not a real core'
$n1 = @(Get-Content "$data\launcher.log").Count
$p = Start-Process $Exe -ArgumentList ($a -replace 't1-definitions\.txt', 't1-definitions-cores.txt') -PassThru
if (-not $p.WaitForExit(120000)) { "launcher still running; stopping it"; $p.Kill(); $fails++ }
$log = @(Get-Content "$data\launcher.log" | Select-Object -Skip $n1)
$log | Where-Object { $_ -match 'SCRIPT (FAIL|done)|core found|now runs inside' } | Out-Host
Check (($log -match 'SCRIPT done, 0 failed').Count -eq 1) 'cores run: script 0 failed'
Check (($log -match '"Zeta Test" now runs inside the launcher with melonDS DS core').Count -eq 1) 'hand-made core took its game'
Check (($log -match 'core found, games not switched to it: FCEUmm core \(imported from libretro-core-info').Count -eq 1) 'imported core found, not switched to (logged)'
Check (($log -match 'now runs inside the launcher with FCEUmm').Count -eq 0) 'no game switched to the imported core'
"t1: $fails failed"
