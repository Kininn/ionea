# Makes a release of Ionea for GitHub (Settings > About and updates > Repo reads it).
#   The build is self-contained (Install-Launcher.ps1): the PC it runs on needs no .NET.
#   Ionea-<version>.zip         the portable copy and what the in-app updater downloads: only what an install needs,
#                               at its top: Launcher.exe, definitions\, THIRD-PARTY.md, CHANGELOG.md. Never a data
#                               folder, the previous version, update leftovers or backups.
#   Ionea-<version>-setup.exe   the installer (installer\Ionea.iss), only when Inno Setup 6's ISCC.exe is found
#                               (-Iscc, PATH, or its usual install folders); otherwise the script says so and goes on.
#   SHA256SUMS                  "<sha256>  <name>" for each of the two (the updater finds the zip's line by its name).
# Usage:
#   Make-Release.ps1 [-Out <dir>] [-Iscc <ISCC.exe>] [-NoInstaller]   builds the next build number (Install-Launcher.ps1 -BuildOnly -Release; commit build.txt)
#   Make-Release.ps1 -From <install dir> [-Out]   packs an installed copy instead (its own version)
# Publish all three files as a release tagged v<major>.<minor>.<patch>.<build> (the tag the script prints).
param([string]$From, [string]$Out = (Join-Path $PSScriptRoot '..\bin\release'), [string]$Iscc, [switch]$NoInstaller)
$ErrorActionPreference = 'Stop'
$work = Join-Path ([IO.Path]::GetTempPath()) ('ionea-release-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$pack = Join-Path $work 'pack'
New-Item -ItemType Directory -Force $pack, $Out | Out-Null
$Out = (Resolve-Path $Out).Path
try {
    if ($From) {
        if (-not (Test-Path (Join-Path $From 'Launcher.exe'))) { throw "There is no Launcher.exe in $From." }
        Copy-Item (Join-Path $From 'Launcher.exe') $pack
        Copy-Item (Join-Path $From 'definitions') (Join-Path $pack 'definitions') -Recurse
        foreach ($f in 'THIRD-PARTY.md', 'CHANGELOG.md') { if (Test-Path (Join-Path $From $f)) { Copy-Item (Join-Path $From $f) $pack } }
    } else {
        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot '..\Install-Launcher.ps1') -BuildOnly $pack -Release | Out-Host
        if ($LASTEXITCODE) { throw 'Build failed.' }
    }
    # nothing but the four items, whatever the source folder held
    Get-ChildItem $pack | Where-Object { $_.Name -notin 'Launcher.exe', 'definitions', 'THIRD-PARTY.md', 'CHANGELOG.md' } | Remove-Item -Recurse -Force
    Get-ChildItem $pack -Recurse -File | Where-Object { $_.Name -like '*.bak*' } | Remove-Item -Force
    $ver = (Get-Item (Join-Path $pack 'Launcher.exe')).VersionInfo.FileVersion
    $made = @()

    # the portable zip
    $name = "Ionea-$ver.zip"
    $zip = Join-Path $Out $name
    if (Test-Path $zip) { Remove-Item $zip -Force }
    # Windows' own tar writes a zip with forward slashes (the updater unpacks it with the same tar)
    $items = @(Get-ChildItem $pack | ForEach-Object Name)
    & (Join-Path $env:SystemRoot 'System32\tar.exe') -a -c -f $zip -C $pack @items
    if ($LASTEXITCODE) { throw 'Zipping failed.' }
    $made += $zip

    # the installer
    $setupNote = $null
    if (-not $NoInstaller) {
        if (-not $Iscc) {
            $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
            $Iscc = @($(if ($cmd) { $cmd.Source }),
                (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
                (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
                (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
        }
        if ($Iscc) {
            $setup = Join-Path $Out "Ionea-$ver-setup.exe"
            if (Test-Path $setup) { Remove-Item $setup -Force }
            & $Iscc /Qp "/DSourceDir=$pack" "/DOutputDir=$Out" "/DAppVersion=$ver" "/DOutputName=Ionea-$ver-setup" (Join-Path $PSScriptRoot '..\installer\Ionea.iss') | Out-Host
            if ($LASTEXITCODE -or -not (Test-Path $setup)) { throw 'The installer could not be built.' }
            $made += $setup
        } else { $setupNote = 'No installer: Inno Setup 6 (ISCC.exe) was not found. Give -Iscc <path> or install it, then run this again.' }
    }

    # one SHA-256 list for everything (one file, so the updater can't pick another file's sum)
    $sums = Join-Path $Out 'SHA256SUMS'
    $made | ForEach-Object { "$((Get-FileHash $_ -Algorithm SHA256).Hash.ToLowerInvariant())  $(Split-Path $_ -Leaf)" } | Set-Content $sums -Encoding ascii
    Write-Host "Release v$ver"
    foreach ($f in $made) { Write-Host ("  {0}  ({1:0.0} MB)" -f $f, ((Get-Item $f).Length / 1MB)) }
    Write-Host "  $sums"
    if ($setupNote) { Write-Warning $setupNote }
} finally { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
