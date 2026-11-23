# Test folder for n2-ps4.txt: a fake ES-DE with one PS4 game folder laid out the way a moved-in PS4 game
# is installed (game folder + "-patch" update folder next to it) and a fake shadPS4 (copy of TestGame.exe).
# Usage: make-fake-ps4.ps1 <dir>   (wipes <dir>)
param([Parameter(Mandatory)][string]$Dir)
$ErrorActionPreference = 'Stop'
$game = Join-Path $PSScriptRoot 'bin\TestGame.exe'
if (-not (Test-Path $game)) { throw "Build test\bin\TestGame.exe first (see README)." }
if (Test-Path $Dir) { Remove-Item $Dir -Recurse -Force }
$g = Join-Path $Dir 'esde\ROMs\ps4\Bloodborne [CUSA00900]'
foreach ($d in "$g\sce_sys", "$g-patch\sce_sys", "$Dir\emus\shadPS4") { New-Item -ItemType Directory -Force $d | Out-Null }
Copy-Item $game "$Dir\emus\shadPS4\shadPS4.exe"
Set-Content -LiteralPath "$g\eboot.bin" 'not a real game'
Set-Content -LiteralPath "$g-patch\eboot.bin" 'not a real update'
Add-Type -AssemblyName System.Drawing
$bmp = New-Object System.Drawing.Bitmap 512, 512
$gr = [System.Drawing.Graphics]::FromImage($bmp); $gr.Clear([System.Drawing.Color]::FromArgb(140, 30, 30)); $gr.Dispose()
$bmp.Save("$g\sce_sys\icon0.png", [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
Write-Host "Fake PS4 test folder: $Dir"
