# Builds a fake Steam folder for test\c1-steam.txt: two libraries, games, tools, a half-installed game,
# and covers in the old flat and the new per-app (also nested hash) cache layouts.
# Usage: powershell -File make-fake-steam.ps1 <dir>   (the dir is wiped first; it must be a test folder)
param([Parameter(Mandatory)][string]$Root)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
if (Test-Path $Root) { Remove-Item $Root -Recurse -Force }
$lib2 = Join-Path $Root 'lib2'
$game = Join-Path $PSScriptRoot 'bin\TestGame.exe'
function MkD($p) { New-Item -ItemType Directory -Force $p | Out-Null; $p }
function Acf($lib, $id, $name, $dir, $flags) {
    Set-Content -Encoding UTF8 (Join-Path (MkD "$lib\steamapps") "appmanifest_$id.acf") @"
"AppState"
{
	"appid"		"$id"
	"name"		"$name"
	"StateFlags"		"$flags"
	"installdir"		"$dir"
}
"@
}
function Exe($lib, $dir, $exe) { Copy-Item $game (Join-Path (MkD "$lib\steamapps\common\$dir") $exe) }
function Img($file, $w, $h, $color, $text, [switch]$Clear) {
    MkD (Split-Path $file) | Out-Null
    $b = New-Object Drawing.Bitmap $w, $h; $g = [Drawing.Graphics]::FromImage($b)
    if ($Clear) { $g.Clear([Drawing.Color]::Transparent) } else { $g.Clear([Drawing.Color]::FromName($color)) }
    $f = New-Object Drawing.Font 'Segoe UI', ([int]($h / 5)), ([Drawing.FontStyle]::Bold)
    $g.DrawString($text, $f, [Drawing.Brushes]::White, 10, $h / 3)
    $fmt = if ($file -like '*.png') { [Drawing.Imaging.ImageFormat]::Png } else { [Drawing.Imaging.ImageFormat]::Jpeg }
    $b.Save($file, $fmt); $g.Dispose(); $b.Dispose()
}
MkD "$Root\steamapps" | Out-Null
$esc = { param($p) $p.Replace('\', '\\') }   # VDF strings escape backslashes
Set-Content -Encoding UTF8 "$Root\steamapps\libraryfolders.vdf" @"
"libraryfolders"
{
	"contentstatsid"		"123"
	"0"
	{
		"path"		"$(& $esc $Root)"
		"apps" { "100" "1" }
	}
	"1"
	{
		"path"		"$(& $esc $lib2)"
	}
}
"@
Acf $Root 100 'Neon Racer' 'Neon Racer' 4;            Exe $Root 'Neon Racer' 'NeonRacer.exe'
Acf $Root 228980 'Steamworks Common Redistributables' 'Steamworks Shared' 4; MkD "$Root\steamapps\common\Steamworks Shared" | Out-Null
Acf $Root 1493710 'Proton Experimental' 'Proton - Experimental' 4;          MkD "$Root\steamapps\common\Proton - Experimental" | Out-Null
Acf $Root 200 'Half Done' 'Half Done' 1026;            MkD "$Root\steamapps\common\Half Done" | Out-Null
Acf $Root 300 'Missing Folder Game' 'Missing' 4
Acf $lib2 400 "Sky Odyssey$([char]0x2122)" 'Sky Odyssey' 4; Exe $lib2 'Sky Odyssey' 'Sky.exe'
Acf $lib2 500 'Paper Tactics' 'Paper Tactics' 4;      Exe $lib2 'Paper Tactics' 'Paper.exe'
Acf $lib2 600 'Deep Garden' 'Deep Garden' 4;          Exe $lib2 'Deep Garden' 'DeepGarden.exe'
$c = "$Root\appcache\librarycache"
Img "$c\100\1a2b\library_hero.jpg" 1920 620 'SteelBlue' 'NEON HERO'
Img "$c\100\3c4d\logo.png" 640 360 '' 'NEON LOGO' -Clear
Img "$c\400_library_600x900.jpg" 600 900 'DarkOrange' 'SKY'
Img "$c\600\header.jpg" 460 215 'SeaGreen' 'DEEP GARDEN'
Write-Host "fake Steam at $Root"
