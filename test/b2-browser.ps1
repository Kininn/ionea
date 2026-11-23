# The launcher's own file browser (Picker.cs): builds a small folder tree and runs b2-browser.txt on it.
# Usage: b2-browser.ps1 [-Exe <Launcher.exe>] [-Root <dir>]   (wipes <dir>)
param([string]$Exe = "$PSScriptRoot\..\bin\Release\net10.0-windows\Launcher.exe", [string]$Root = "$env:TEMP\Ionea-tests\b2-browser")
$ErrorActionPreference = 'Stop'
if (Test-Path $Root) { Remove-Item $Root -Recurse -Force }
$T = "$Root\T"; $data = "$Root\data"; $B = "$T\browse"
New-Item -ItemType Directory -Force $data, "$B\Games\saves", "$B\Empty" | Out-Null
foreach ($f in 'Alpha.iso', 'Beta.chd', 'notes.txt') { Set-Content "$B\Games\$f" 'x' }
$h = New-Item -ItemType Directory -Force "$B\Games\.hidden"; $h.Attributes = 'Hidden'
$env:IONEA_TESTROOT = $T
$a = "--data `"$data`" --no-import --windowed --mute --size 1040x620 --pos -30000,-30000 --shots `"$data\shots`" --online-test-host localhost:47899 --script `"$PSScriptRoot\b2-browser.txt`""
$p = Start-Process $Exe -ArgumentList $a -PassThru
if (-not $p.WaitForExit(120000)) { "launcher still running; stopping it"; $p.Kill(); exit 1 }
$log = Get-Content "$data\launcher.log"
$log | Where-Object { $_ -match 'SCRIPT (FAIL|done)|browse' } | Out-Host
if (($log -match 'SCRIPT done, 0 failed').Count -eq 1) { "ok   b2-browser"; exit 0 } else { "FAIL b2-browser"; exit 1 }
