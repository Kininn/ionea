# The scan with multi-disc games (Scan.cs Identify): builds a fake home with DuckStation and a PS1 folder, runs s8-discs.txt.
#   Saga: two discs (cue + bin each) and Saga.m3u naming both -> one game, the playlist
#   Solo: one cue + bin -> one game, the cue
#   Twin: two discs and no playlist -> two games that keep their disc numbers
#   A\B\C\Deep Find: three levels down -> found;  A\B\C\D\Too Deep: four -> not looked at
#   saves\Lost: a save folder -> skipped
# Every file is made-up bytes. Usage: s8-discs.ps1 [-Exe <Launcher.exe>] [-Root <dir>]   (wipes <dir>)
param([string]$Exe = "$PSScriptRoot\..\bin\Release\net10.0-windows\Launcher.exe", [string]$Root = "$env:TEMP\Ionea-tests\s8-discs")
$ErrorActionPreference = 'Stop'
$game = Join-Path $PSScriptRoot 'bin\TestGame.exe'
if (Test-Path $Root) { Remove-Item $Root -Recurse -Force }
$T = "$Root\T"; $H = "$T\home"; $data = "$Root\data"; $P = "$H\Games\PS1"
New-Item -ItemType Directory -Force $T, $H, $data | Out-Null
$utf8 = New-Object System.Text.UTF8Encoding($false)
function Put($p, $text) { New-Item -ItemType Directory -Force (Split-Path $p) | Out-Null; [IO.File]::WriteAllText($p, $text, $utf8) }
function Disc($dir, $name) {
    Put "$dir\$name.cue" "FILE `"$name.bin`" BINARY`r`n  TRACK 01 MODE2/2352`r`n    INDEX 01 00:00:00`r`n"
    [IO.File]::WriteAllBytes("$dir\$name.bin", [byte[]]::new(4096))
}
New-Item -ItemType Directory -Force "$H\Program Files\DuckStation" | Out-Null
Copy-Item $game "$H\Program Files\DuckStation\duckstation-qt-x64-ReleaseLTCG.exe"
Put "$H\Program Files\DuckStation\settings.ini" "[GameList]`r`nRecursivePaths = $P`r`n"
Disc $P 'Saga (USA) (Disc 1)'; Disc $P 'Saga (USA) (Disc 2)'
Put "$P\Saga (USA).m3u" "Saga (USA) (Disc 1).cue`r`nSaga (USA) (Disc 2).cue`r`n"
Disc $P 'Solo (USA)'
Disc $P 'Twin (Europe) (Disc 1)'; Disc $P 'Twin (Europe) (Disc 2)'
Disc "$P\A\B\C" 'Deep Find (USA)'
Disc "$P\A\B\C\D" 'Too Deep (USA)'
Disc "$P\saves" 'Lost (USA)'
$env:IONEA_TESTROOT = $T
$a = "--data `"$data`" --windowed --size 1040x620 --pos -30000,-30000 --shots `"$data\shots`" --online-test-host localhost:47899 --scan-home `"$H`" --no-import --mute --script `"$PSScriptRoot\s8-discs.txt`""
$p = Start-Process $Exe -ArgumentList $a -PassThru
if (-not $p.WaitForExit(180000)) { "launcher still running; stopping it"; $p.Kill(); exit 1 }
$log = Get-Content "$data\launcher.log"
$log | Where-Object { $_ -match 'SCRIPT (FAIL|done|scan:)|^.{0,40}scan' } | Out-Host
if (($log -match 'SCRIPT done, 0 failed').Count -eq 1) { "ok   s8-discs"; exit 0 } else { "FAIL s8-discs"; exit 1 }
