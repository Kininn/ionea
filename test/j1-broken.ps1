# A broken data file (Data.cs Json.Load): runs j1-broken.txt, breaks the profile's library.json, runs j1-broken-again.txt.
# The launcher must start, keep the broken file as library.json.broken-<time> and never write over it.
# Usage: j1-broken.ps1 [-Exe <Launcher.exe>] [-Root <dir>]   (wipes <dir>)
param([string]$Exe = "$PSScriptRoot\..\bin\Release\net10.0-windows\Launcher.exe", [string]$Root = "$env:TEMP\Ionea-tests\j1-broken")
$ErrorActionPreference = 'Stop'
if (Test-Path $Root) { Remove-Item $Root -Recurse -Force }
$data = "$Root\data"
New-Item -ItemType Directory -Force $data | Out-Null
$fails = 0
function Check($ok, $what) { if ($ok) { "ok   $what" } else { "FAIL $what"; $script:fails++ } }
function Run($name) {
    $before = if (Test-Path "$data\launcher.log") { (Get-Content "$data\launcher.log").Count } else { 0 }
    $a = "--data `"$data`" --no-import --dummies 6 --dummy-target `"$PSScriptRoot\bin\TestGame.exe`" --windowed --mute --size 1040x620 --pos -30000,-30000 --shots `"$data\shots`" --online-test-host localhost:47899 --script `"$PSScriptRoot\$name.txt`""
    $p = Start-Process $Exe -ArgumentList $a -PassThru
    if (-not $p.WaitForExit(120000)) { "launcher still running; stopping it"; $p.Kill(); $script:fails++ }
    $log = Get-Content "$data\launcher.log" | Select-Object -Skip $before
    $log | Where-Object { $_ -match 'SCRIPT (FAIL|done)|could not read|could not keep|copy from the save before' } | Out-Host
    Check (($log -match 'SCRIPT done, 0 failed').Count -eq 1) "${name}: script 0 failed"
}
Run 'j1-broken'
$lib = Get-ChildItem $data -Recurse -Filter library.json | Select-Object -First 1
Check ($null -ne $lib) 'library.json made'
if ($lib) {
    Set-Content $lib.FullName '{ "Games": [ broken'
    Run 'j1-broken-again'
    $kept = @(Get-ChildItem $lib.DirectoryName -Filter 'library.json.broken-*')
    Check ($kept.Count -eq 1) "the broken file is kept ($($kept.Count))"
    if ($kept.Count -eq 1) { Check ((Get-Content $kept[0].FullName -Raw).Trim() -eq '{ "Games": [ broken') 'kept as it was' }
}
if ($fails -eq 0) { "ok   j1-broken" } else { "FAIL j1-broken ($fails)" }
exit $fails
