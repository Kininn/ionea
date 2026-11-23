# Test folder for d1-emulators.txt: fake emulators (copies of the harmless TestGame.exe), a fake Inbox
# with a zipped "melonDS", and empty "game files". Usage: make-fake-emus.ps1 <dir>   (wipes <dir>)
param([Parameter(Mandatory)][string]$Dir)
$ErrorActionPreference = 'Stop'
$game = Join-Path $PSScriptRoot 'bin\TestGame.exe'
if (-not (Test-Path $game)) { throw "Build test\bin\TestGame.exe first (see README)." }
if (Test-Path $Dir) { Remove-Item $Dir -Recurse -Force }
foreach ($d in 'emus\Dolphin', 'inbox', 'roms', 'custom', 'stage\melonDS-1.1') { New-Item -ItemType Directory -Force (Join-Path $Dir $d) | Out-Null }
Copy-Item $game (Join-Path $Dir 'emus\Dolphin\Dolphin.exe')
Copy-Item $game (Join-Path $Dir 'custom\MyEmu.exe')
Copy-Item $game (Join-Path $Dir 'stage\melonDS-1.1\melonDS.exe')
Set-Content (Join-Path $Dir 'stage\melonDS-1.1\readme.txt') 'test'
& "$env:SystemRoot\System32\tar.exe" -a -cf (Join-Path $Dir 'inbox\melonDS-1.1-windows-x86_64.zip') -C (Join-Path $Dir 'stage') 'melonDS-1.1'
& "$env:SystemRoot\System32\tar.exe" --format 7zip -cf (Join-Path $Dir 'inbox\no-emulator-test.7z') -C (Join-Path $Dir 'stage\melonDS-1.1') 'readme.txt'
foreach ($f in 'Pokemon - White Version 2 (USA, Europe) (NDSi Enhanced).nds', 'Test Island (Europe) (En,Fr,De).rvz', 'Mystery Disc.iso', 'Pocket Quest (USA).gba') {
    Set-Content -LiteralPath (Join-Path $Dir "roms\$f") 'not a real game'
}
Write-Host "Fake emulator test folder: $Dir"
