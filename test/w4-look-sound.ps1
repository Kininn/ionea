# w4: look, effects and sound (see w4-look-sound.txt). A fresh data folder, 12 dummy games, the fake emulators of
# make-fake-emus.ps1 (copies of the harmless TestGame.exe, only added, never run), windowed, muted, off screen,
# no real input. Usage: w4-look-sound.ps1 [-Exe <Launcher.exe>] [-Root <dir>]   (wipes <dir>)
param([string]$Exe = "$PSScriptRoot\..\bin\Release\net10.0-windows\Launcher.exe", [string]$Root = "$env:TEMP\Ionea-tests\w4")
$ErrorActionPreference = 'Stop'
& "$PSScriptRoot\make-fake-emus.ps1" $Root
$data = "$Root\data"; $shots = "$Root\shots"
New-Item -ItemType Directory -Force $data, $shots | Out-Null
$env:IONEA_TESTROOT = $Root
$p = Start-Process -FilePath $Exe -WorkingDirectory "$PSScriptRoot\.." -PassThru -Wait -ArgumentList @(
  "--data", "`"$data`"", "--no-import", "--dummies", "12", "--script", "`"$PSScriptRoot\w4-look-sound.txt`"", "--shots", "`"$shots`"",
  "--emulators", "`"$Root\emus`"", "--inbox", "`"$Root\inbox`"", "--windowed", "--mute", "--size", "1040x620", "--pos", "-30000,-30000")
$log = "$data\launcher.log"
"exit " + $p.ExitCode
if (Test-Path $log) { Select-String -Path $log -Pattern "FAIL|SCRIPT done|sleep:|Exception" | ForEach-Object { $_.Line } } else { "no log" }
