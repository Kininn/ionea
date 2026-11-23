# w9: final UI audit (see w9-ui-audit.txt). A fresh data folder, 12 dummy games,
# windowed, muted, off screen, no real input. Nothing goes online.
# Usage: w9-ui-audit.ps1 [-Exe <Launcher.exe>] [-Root <dir>]   (wipes <dir>)
param([string]$Exe = "$PSScriptRoot\..\bin\Release\net10.0-windows\Launcher.exe", [string]$Root = "$env:TEMP\Ionea-tests\w9")
$ErrorActionPreference = 'Stop'
if (Test-Path $Root) { Remove-Item -Recurse -Force $Root }
$data = "$Root\data"; $shots = "$Root\shots"
New-Item -ItemType Directory -Force $data, $shots, "$Root\dl" | Out-Null
$env:IONEA_TESTROOT = $Root
$p = Start-Process -FilePath $Exe -WorkingDirectory "$PSScriptRoot\.." -PassThru -Wait -ArgumentList @(
  "--data", "`"$data`"", "--no-import", "--dummies", "12", "--script", "`"$PSScriptRoot\w9-ui-audit.txt`"", "--shots", "`"$shots`"",
  "--windowed", "--mute", "--size", "1040x620", "--pos", "-30000,-30000")
$log = "$data\launcher.log"
"exit " + $p.ExitCode
if (Test-Path $log) { Select-String -Path $log -Pattern "FAIL|SCRIPT done|Exception" | ForEach-Object { $_.Line } } else { "no log" }
