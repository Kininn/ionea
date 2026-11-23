# rel1: a self-contained release build starts without an installed .NET.
# Starts <Exe> with dotnet taken off PATH and DOTNET_ROOT pointing nowhere, in an empty data folder, runs
# rel1-start.txt (first run to Home, then quit), and checks that no module came from a shared .NET install
# (Program Files\dotnet or the SDK's folder). Prints the time to "SCRIPT done". Windowed, muted, no real input.
# Usage: rel1-start.ps1 -Exe <Launcher.exe> [-Root <dir>]   (wipes <dir>)
param([Parameter(Mandatory)][string]$Exe, [string]$Root = "$env:TEMP\Ionea-tests\rel1-start")
$ErrorActionPreference = 'Stop'
$Exe = (Resolve-Path $Exe).Path
if (Test-Path $Root) { Remove-Item $Root -Recurse -Force }
$data = "$Root\data"
New-Item -ItemType Directory -Force $data | Out-Null
$shared = @("$env:ProgramFiles\dotnet", "${env:ProgramFiles(x86)}\dotnet")
$dn = Get-Command dotnet -ErrorAction SilentlyContinue
if ($dn) { $shared += Split-Path $dn.Source }

$psi = New-Object Diagnostics.ProcessStartInfo $Exe
$psi.Arguments = '--data "{0}" --no-import --windowed --mute --script "{1}"' -f $data, (Join-Path $PSScriptRoot 'rel1-start.txt')
$psi.UseShellExecute = $false
$psi.WorkingDirectory = $Root
$psi.EnvironmentVariables['PATH'] = (($env:PATH -split ';') | Where-Object { $_ -and -not (Test-Path (Join-Path $_ 'dotnet.exe')) }) -join ';'
$psi.EnvironmentVariables['DOTNET_ROOT'] = "$Root\no-dotnet"
$psi.EnvironmentVariables['DOTNET_MULTILEVEL_LOOKUP'] = '0'
$clock = [Diagnostics.Stopwatch]::StartNew()
$p = [Diagnostics.Process]::Start($psi)
$log = "$data\launcher.log"
$fromShared = @(); $done = $null
try {
    while ($clock.Elapsed.TotalSeconds -lt 60 -and -not $p.HasExited) {
        if (-not $fromShared -and $clock.Elapsed.TotalSeconds -gt 2) {
            try { $p.Refresh(); $fromShared = @($p.Modules | Where-Object { $f = $_.FileName; $shared | Where-Object { $f -like "$_\*" } } | ForEach-Object FileName) } catch { }
        }
        if ((Test-Path $log) -and (Select-String -Path $log -Pattern 'SCRIPT done' -Quiet)) { $done = $clock.Elapsed.TotalSeconds; break }
        Start-Sleep -Milliseconds 100
    }
    if (-not $p.WaitForExit(15000)) { $p.Kill(); "rel1: it did not quit" }
    # it quits right after logging "done", so the loop may end on its exit first
    if (-not $done -and (Test-Path $log) -and (Select-String -Path $log -Pattern 'SCRIPT done' -Quiet)) { $done = $clock.Elapsed.TotalSeconds }
} finally { if (-not $p.HasExited) { $p.Kill() } }

$fails = 0
if (-not $done) { "rel1 FAIL: no 'SCRIPT done' in $log within 60 s (exit code $($p.ExitCode))"; $fails++ }
else {
    "rel1: SCRIPT done after {0:0.0} s" -f $done
    Select-String -Path $log -Pattern 'SCRIPT (FAIL|done)' | ForEach-Object { "  " + $_.Line }
    if (Select-String -Path $log -Pattern 'SCRIPT FAIL' -Quiet) { $fails++ }
}
if ($fromShared) { "rel1 FAIL: modules from a shared .NET install:"; $fromShared | ForEach-Object { "  $_" }; $fails++ }
else { "rel1: no module from a shared .NET install" }
"rel1: $fails failed"
exit $fails
