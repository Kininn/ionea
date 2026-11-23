# Builds the launcher and installs it to <system drive>\Ionea\Launcher (or -Target), plus a desktop shortcut "Ionea".
# The data folder (<target>\data: name, settings, games, covers) is never touched.
# Version: every build gets the next build number and today's date. build.txt (next to this script, tracked) is the last
# release's number; this folder's own builds count on in bin\build.txt (not tracked), so building from a clone never
# changes its files;
# the version itself (1.0.0) is VersionPrefix in Launcher.csproj. The installed exe before this one is kept as
# Launcher.previous.exe (definitions.previous\), so Settings > About and updates > Go back to the previous version works.
#   -BuildOnly <dir>   build into <dir> only (Launcher.exe, definitions\, LICENSE, THIRD-PARTY.md, CHANGELOG.md), install nothing.
#                      Ionea's own Update now uses this, then swaps the exe itself and restarts (Updater.cs).
#   -BuildNumber <n>   use this build number and leave both counters alone (tests).
#   -Release           write the number to build.txt (Make-Release.ps1 does this; commit it).
#   -Version <x.y.z>   build as this version instead of Launcher.csproj's (tests).
#   -Target <dir>      where to install (default <system drive>\Ionea\Launcher); any folder the user can write to.
#   -FrameworkDependent  a small exe that needs the .NET 10 Desktop Runtime on the PC (the default carries its own).
# The exe is self-contained: one Launcher.exe with the .NET runtime inside, ReadyToRun, not trimmed
# (WPF and reflection), so the PC it runs on needs no .NET. The layout is unchanged: Launcher.exe, definitions\,
# LICENSE, THIRD-PARTY.md, CHANGELOG.md.
# Building needs the .NET 10 SDK (dotnet) on PATH; nothing else on the PC is assumed.
param([string]$Target = (Join-Path $env:SystemDrive 'Ionea\Launcher'), [switch]$NoShortcut, [string]$BuildOnly, [int]$BuildNumber = -1, [string]$Version, [switch]$FrameworkDependent, [switch]$Release)
$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'The .NET 10 SDK is needed to build Ionea (https://dotnet.microsoft.com/download). Install it, then run this again.' }
$exe = Join-Path $Target 'Launcher.exe'

if (-not $BuildOnly) {
    $running = Get-CimInstance Win32_Process -Filter "Name = 'Launcher.exe'" | Where-Object { $_.ExecutablePath -eq $exe }
    if ($running) { throw "The launcher is running (PID $($running.ProcessId -join ', ')). Close it first." }
}

# the build number: one past the higher of the two counters, written back only after a good build
$released = Join-Path $src 'build.txt'
$local = Join-Path $src 'bin\build.txt'
$counter = if ($Release) { $released } else { $local }
$bump = $BuildNumber -lt 0
if ($bump) {
    $last = 0
    foreach ($c in $released, $local) {
        $n = 0
        if (Test-Path $c) { [int]::TryParse((Get-Content $c -Raw).Trim(), [ref]$n) | Out-Null }
        $last = [Math]::Max($last, $n)
    }
    $BuildNumber = $last + 1
}
$built = (Get-Date).ToString('yyyy-MM-ddTHH:mm:ss')

$out = Join-Path $src 'bin\publish'
if ($BuildOnly) { $out = Join-Path ([IO.Path]::GetTempPath()) ('ionea-build-' + [guid]::NewGuid().ToString('N').Substring(0, 8)) }
$props = @("-p:IoneaBuild=$BuildNumber", "-p:IoneaBuilt=$built")
if ($Version) { $props += "-p:VersionPrefix=$Version" }
# A fresh start for the publish's own intermediate files, so changed <Resource> items always make it in
# (build 42 kept stale ones). dotnet publish has no --no-incremental.
$objRid = Join-Path $src 'obj\Release\net10.0-windows\win-x64'
if (Test-Path $objRid) { Remove-Item $objRid -Recurse -Force }
if ($FrameworkDependent) { $props += '--self-contained', 'false' }
else { $props += '--self-contained', 'true', '-p:PublishReadyToRun=true', '-p:PublishTrimmed=false' }
dotnet publish (Join-Path $src 'Launcher.csproj') -c Release -r win-x64 `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true @props -o $out -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
if ($bump) { New-Item -ItemType Directory -Force (Split-Path $counter) | Out-Null; Set-Content $counter $BuildNumber -Encoding ascii }
$ver = (Get-Item (Join-Path $out 'Launcher.exe')).VersionInfo.FileVersion
Write-Host "Built Ionea $ver ($built)"

if ($BuildOnly) {
    New-Item -ItemType Directory -Force $BuildOnly | Out-Null
    Copy-Item (Join-Path $out 'Launcher.exe') (Join-Path $BuildOnly 'Launcher.exe') -Force
    $bdefs = Join-Path $BuildOnly 'definitions'
    if (Test-Path $bdefs) { Remove-Item $bdefs -Recurse -Force }
    Copy-Item (Join-Path $out 'definitions') $bdefs -Recurse
    foreach ($f in 'LICENSE', 'THIRD-PARTY.md', 'CHANGELOG.md') { if (Test-Path (Join-Path $out $f)) { Copy-Item (Join-Path $out $f) (Join-Path $BuildOnly $f) -Force } }
    Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "Staged in $BuildOnly"
    return
}

New-Item -ItemType Directory -Force $Target | Out-Null
# the exe and definitions being replaced become the previous version (Go back to the previous version)
if (Test-Path $exe) { Copy-Item $exe (Join-Path $Target 'Launcher.previous.exe') -Force }
$defs = Join-Path $Target 'definitions'
$pdefs = Join-Path $Target 'definitions.previous'
if (Test-Path $defs) {
    if (Test-Path $pdefs) { Remove-Item $pdefs -Recurse -Force }
    Copy-Item $defs $pdefs -Recurse
}
Copy-Item (Join-Path $out 'Launcher.exe') $exe -Force
Write-Host "Installed $exe ($ver)"
# The shipped definitions are replaced whole (the user's own are in data\definitions and stay).
if (Test-Path $defs) { Remove-Item $defs -Recurse -Force }
Copy-Item (Join-Path $out 'definitions') $defs -Recurse
Copy-Item (Join-Path $out 'THIRD-PARTY.md') (Join-Path $Target 'THIRD-PARTY.md') -Force
foreach ($f in 'LICENSE', 'CHANGELOG.md') { if (Test-Path (Join-Path $out $f)) { Copy-Item (Join-Path $out $f) (Join-Path $Target $f) -Force } }
Write-Host "Installed $defs ($((Get-ChildItem $defs -Recurse -Filter *.json).Count) files), LICENSE and THIRD-PARTY.md"

if (-not $NoShortcut) {
    $desk = [Environment]::GetFolderPath('Desktop')
    $lnk = Join-Path $desk 'Ionea.lnk'
    $sh = (New-Object -ComObject WScript.Shell).CreateShortcut($lnk)
    $sh.TargetPath = $exe
    $sh.WorkingDirectory = $Target
    $sh.IconLocation = "$exe,0"
    $sh.Save()
    Write-Host "Shortcut $lnk"
    # Pinned copies (taskbar, Start) keep their own icon path; point any that open this exe at its icon.
    $pins = Join-Path $env:APPDATA 'Microsoft\Internet Explorer\Quick Launch\User Pinned'
    if (Test-Path $pins) {
        Get-ChildItem $pins -Recurse -Filter *.lnk | ForEach-Object {
            $p = (New-Object -ComObject WScript.Shell).CreateShortcut($_.FullName)
            if ($p.TargetPath -eq $exe -and $p.IconLocation -ne "$exe,0") {
                $p.IconLocation = "$exe,0"; $p.WorkingDirectory = $Target; $p.Save()
                Write-Host "Pinned shortcut icon fixed: $($_.FullName)"
            }
        }
    }
}
