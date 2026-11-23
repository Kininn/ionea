# A hosted core's sound for real: a silent GBA test program runs on the mGBA core with the game's sound on (the
# launcher's own sounds stay off), through the low-latency output on the default device. Checks from the log:
# the output opened, the game kept time with the clock at full speed, and no gaps; prints the measured delay.
# Usage: snd1-core-sound.ps1 -Exe <Launcher.exe> [-Core <mgba_libretro.dll>] [-Root <dir>]   (wipes <dir>)
param([string]$Exe = "$PSScriptRoot\..\bin\publish\Launcher.exe", [string]$Core = $env:IONEA_TEST_MGBA_CORE, [string]$Root = "$env:TEMP\Ionea-tests\snd1")
$ErrorActionPreference = 'Stop'
if (-not $Core -or -not (Test-Path $Core)) { "Give -Core <path to mgba_libretro.dll> (or IONEA_TEST_MGBA_CORE)"; exit 1 }
if (Test-Path $Root) { Remove-Item $Root -Recurse -Force }
New-Item -ItemType Directory -Force "$Root\roms", "$Root\emus\Cores", "$Root\data" | Out-Null
Copy-Item $Core "$Root\emus\Cores\mgba_libretro.dll"
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
New-Rom "Sound Test" "SOUNDTEST   " $null

$fails = 0
function Check($ok, $what) { if ($ok) { "ok   $what" } else { "FAIL $what"; $script:fails++ } }
$env:IONEA_TESTROOT = $Root
$a = "--data `"$Root\data`" --no-import --emulators `"$Root\emus`" --windowed --mute --game-sound --size 1040x620 --pos -30000,-30000 --script `"$PSScriptRoot\snd1-core-sound.txt`""
$p = Start-Process $Exe -ArgumentList $a -PassThru
if (-not $p.WaitForExit(180000)) { $p.Kill(); "FAIL the launcher did not finish"; exit 1 }
$log = Get-Content "$Root\data\launcher.log"
$log | Select-String 'SCRIPT FAIL' | ForEach-Object { "    $_" }
Check (@($log | Select-String 'SCRIPT done, 0 failed').Count -eq 1) 'the script ran clean'
$open = $log | Select-String 'core sound: low-latency output' | Select-Object -First 1
Check ($null -ne $open) 'the low-latency output opened'
$sum = $log | Select-String 'core sound: .* ms of sound waiting' | Select-Object -Last 1
Check ($null -ne $sum) 'it reported its delay'
Check ($sum -and $sum.Line -match '; 0 gaps') 'no gaps'
$ran = $log | Select-String 'core: ran .* paced by clock' | Select-Object -Last 1
Check ($null -ne $ran) 'the game kept time with the clock'
if ($ran -and $ran.Line -match '\(([\d.]+) fps') { Check ([double]$Matches[1] -gt 59 -and [double]$Matches[1] -lt 60.5) "full speed ($($Matches[1]) fps)" }
if ($open) { "     " + $open.Line.Substring(24) }
if ($sum) { "     " + $sum.Line.Substring(24) }
"snd1: $fails failed"
exit $fails
