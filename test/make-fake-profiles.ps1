# Test folder for u1-profiles.txt: a fake Dolphin (save folders) and a fake mGBA (saves next to the game),
# each with a save made "before profiles", two empty game files and a picture.
# Usage: make-fake-profiles.ps1 <dir>   (wipes <dir>)
param([Parameter(Mandatory)][string]$Dir)
$ErrorActionPreference = 'Stop'
$game = Join-Path $PSScriptRoot 'bin\TestGame.exe'
if (-not (Test-Path $game)) { throw "Build test\bin\TestGame.exe first (see README)." }
if (Test-Path $Dir) { Remove-Item $Dir -Recurse -Force }
foreach ($d in 'emus\Dolphin\User\GC', 'emus\mGBA', 'roms', 'recycle') { New-Item -ItemType Directory -Force (Join-Path $Dir $d) | Out-Null }
Copy-Item $game (Join-Path $Dir 'emus\Dolphin\Dolphin.exe')
Set-Content (Join-Path $Dir 'emus\Dolphin\portable.txt') ''
Set-Content (Join-Path $Dir 'emus\Dolphin\User\GC\old.sav') 'before'
Copy-Item $game (Join-Path $Dir 'emus\mGBA\mGBA.exe')
Set-Content (Join-Path $Dir 'emus\mGBA\portable.ini') ''
Set-Content -LiteralPath (Join-Path $Dir 'roms\Pocket Quest (USA).gba') 'not a real game'
Set-Content -LiteralPath (Join-Path $Dir 'roms\Pocket Quest (USA).sav') 'owner save'
Set-Content -LiteralPath (Join-Path $Dir 'roms\Test Island.rvz') 'not a real game'
Add-Type -AssemblyName System.Drawing
$b = New-Object System.Drawing.Bitmap 300, 200
$g = [System.Drawing.Graphics]::FromImage($b); $g.Clear([System.Drawing.Color]::FromArgb(255, 40, 160, 220)); $g.Dispose()
$b.Save((Join-Path $Dir 'pic.png'), [System.Drawing.Imaging.ImageFormat]::Png); $b.Dispose()
Write-Host "Fake profiles test folder: $Dir"
