# The scan, header identification, covers and Download an emulator.
# Builds a fake home (every place the scan looks is under it, --scan-home), serves http://localhost:47812
# (LibRetro thumbnails and .dat, SteamGridDB, a GitHub release), then runs s7-scan.txt and s7-scan-first.txt.
# The fake emulators are copies of the harmless TestGame.exe; "Dolphin.exe" is a copy of Windows' where.exe
# so its product name is not Dolphin's (the scan skips it). Every game file is made-up bytes. Nothing leaves this PC.
# Usage: s7-scan.ps1 [-Exe <Launcher.exe>] [-Root <dir>]   (wipes <dir>)
param([string]$Exe = "$PSScriptRoot\..\bin\Release\net10.0-windows\Launcher.exe", [string]$Root = "$env:TEMP\Ionea-tests\s7-scan")
$ErrorActionPreference = 'Stop'
$port = 47812
$game = Join-Path $PSScriptRoot 'bin\TestGame.exe'
if (Test-Path $Root) { Remove-Item $Root -Recurse -Force }
$T = "$Root\T"; $H = "$T\home"; $data = "$Root\data"; $data2 = "$Root\data-first"; $www = "$Root\www"; $served = "$Root\served.txt"; $ready = "$Root\ready.txt"
New-Item -ItemType Directory -Force $T, $H, $data, $data2, $www, "$T\empty" | Out-Null
if (Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue) { "port $port is in use"; exit 1 }
$utf8 = New-Object System.Text.UTF8Encoding($false)
function Put($p, $text) { New-Item -ItemType Directory -Force (Split-Path $p) | Out-Null; [IO.File]::WriteAllText($p, $text, $utf8) }
function Bytes($p, [byte[]]$b) { New-Item -ItemType Directory -Force (Split-Path $p) | Out-Null; [IO.File]::WriteAllBytes($p, $b) }
function Exe($p) { New-Item -ItemType Directory -Force (Split-Path $p) | Out-Null; Copy-Item $game $p }
function Png($p, $r, $g, $b) {
    Add-Type -AssemblyName System.Drawing
    New-Item -ItemType Directory -Force (Split-Path $p) | Out-Null
    $bmp = New-Object Drawing.Bitmap 256, 256; $gr = [Drawing.Graphics]::FromImage($bmp)
    $gr.Clear([Drawing.Color]::FromArgb($r, $g, $b)); $gr.Dispose(); $bmp.Save($p, [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
}
function Crc([byte[]]$b) {
    $c = [uint32]::MaxValue
    foreach ($x in $b) { $c = $c -bxor $x; for ($k = 0; $k -lt 8; $k++) { if ($c -band 1) { $c = [uint32](($c -shr 1) -bxor 0xEDB88320) } else { $c = [uint32]($c -shr 1) } } }
    return ('{0:X8}' -f ($c -bxor [uint32]::MaxValue))
}
function Shortcut($lnk, $target) {
    New-Item -ItemType Directory -Force (Split-Path $lnk) | Out-Null
    $s = (New-Object -ComObject WScript.Shell).CreateShortcut($lnk); $s.TargetPath = $target; $s.Save()
}
# A GBA cartridge header: game code at 0xAC, the fixed 0x96 at 0xB2.
function Gba([string]$code, [int]$fill) { $b = [byte[]]::new(0x400); for ($i = 0xC0; $i -lt $b.Length; $i++) { $b[$i] = [byte](($i * $fill) % 251) }; [Text.Encoding]::ASCII.GetBytes($code).CopyTo($b, 0xAC); $b[0xB2] = 0x96; return , $b }

# ---- emulators, found five ways ----
Exe "$H\Apps\PPSSPP\PPSSPPWindows64.exe"; Shortcut "$H\Desktop\PPSSPP.lnk" "$H\Apps\PPSSPP\PPSSPPWindows64.exe"      # desktop shortcut
Exe "$H\Apps\Eden\eden.exe"                                                                                         # installed programs
Put "$H\uninstall.json" (ConvertTo-Json -InputObject @(@{ InstallLocation = "$H\Apps\Eden"; DisplayIcon = "$H\Apps\Eden\eden.exe,0" }))
Exe "$H\Program Files\DuckStation\duckstation-qt-x64-ReleaseLTCG.exe"                                               # Program Files
New-Item -ItemType Directory -Force "$H\Program Files\Dolphin" | Out-Null
Copy-Item "$env:SystemRoot\System32\where.exe" "$H\Program Files\Dolphin\Dolphin.exe"                                # another product: skipped
Exe "$H\Emulators\melonDS\melonDS.exe"                                                                              # an Emulators folder
New-Item -ItemType Directory -Force "$H\RetroArch\cores" | Out-Null
Copy-Item $game "$H\RetroArch\cores\mgba_libretro.dll"                                                              # a RetroArch core

# ---- game folders: an emulator's own settings, ES-DE, RetroArch's playlist, Playnite ----
Put "$H\Apps\Eden\user\config\qt-config.ini" ("[UI]`r`nPaths\gamedirs\1\path=" + ($H -replace '\\', '/') + "/Games/Switch`r`nPaths\gamedirs\size=1`r`n")
Bytes "$H\Games\Switch\Island Trip.nro" ([Text.Encoding]::ASCII.GetBytes('not a real game'))
Put "$H\Program Files\DuckStation\settings.ini" "[GameList]`r`nRecursivePaths = $H\Games\PS1`r`n"
Put "$H\Games\PS1\Disc Runner (USA).cue" "FILE `"Disc Runner (USA).bin`" BINARY`r`n  TRACK 01 MODE2/2352`r`n    INDEX 01 00:00:00`r`n"
Bytes "$H\Games\PS1\Disc Runner (USA).bin" ([byte[]]::new(4096))
Exe "$H\ES-DE\ES-DE.exe"
Put "$H\ES-DE\ES-DE\settings\es_settings.xml" "<?xml version=`"1.0`"?>`r`n<string name=`"ROMDirectory`" value=`"`" />`r`n"
$nds = [byte[]]::new(0x400); [Text.Encoding]::ASCII.GetBytes('STARRACER').CopyTo($nds, 0); $nds[0x15C] = 0x56; $nds[0x15D] = 0xCF
Bytes "$H\ES-DE\ROMs\nds\Star Racer (Europe).nds" $nds
New-Item -ItemType Directory -Force "$H\ES-DE\ROMs\gc" | Out-Null                                                   # empty: not offered
$pq = Gba 'AQTE' 3; Bytes "$H\Games\GBA\Pocket Quest (USA).gba" $pq
Put "$H\RetroArch\playlists\Nintendo - Game Boy Advance.lpl" (ConvertTo-Json -Depth 4 @{ version = '1.5'; items = @(@{ path = "$H\Games\GBA\Pocket Quest (USA).gba"; label = 'Pocket Quest (USA)'; db_name = 'Nintendo - Game Boy Advance.lpl' }) })
$my = Gba 'AMYE' 7; Bytes "$H\Games\Mixed\Mystery.bin" $my                                                          # a GBA game named .bin: its header says so
Bytes "$H\Games\Mixed\Unknown.iso" ([Text.Encoding]::ASCII.GetBytes('nothing to see'))                              # not clearly anything: left for New game files
Put "$H\AppData\Roaming\Playnite\library\games.db" ("LiteDB`0`0junk`0" + "$H\Games\Mixed\Mystery.bin" + "`0more`0" + "$H\Games\Mixed\Unknown.iso" + "`0")

# ---- what the server has ----
$myCrc = Crc $my
Put "$www\dat\no-intro\Nintendo - Game Boy Advance.dat" "clrmamepro (`n`tname `"Nintendo - Game Boy Advance`"`n)`n`ngame (`n`tname `"Pocket Quest (USA)`"`n`tserial `"AGB-AQTE-USA`"`n`trom ( name `"Pocket Quest (USA).gba`" size 1024 crc 00000000 )`n)`n`ngame (`n`tname `"Mystery Quest (Europe)`"`n`trom ( name `"Mystery Quest (Europe).gba`" size 1024 crc $myCrc )`n)`n"
Png "$www\thumbs\Nintendo - Game Boy Advance\Named_Boxarts\Pocket Quest (USA).png" 200 40 40
Png "$www\thumbs\Nintendo - Game Boy Advance\Named_Boxarts\Mystery Quest (Europe).png" 40 200 40
Png "$www\sgdbimg.png" 40 40 200
Put "$www\sgdb\search\autocomplete\Star Racer" '{"success":true,"data":[{"id":4242,"name":"Star Racer"}]}'
Put "$www\sgdb\grids\game\4242" "{`"success`":true,`"data`":[{`"id`":1,`"url`":`"http://localhost:$port/sgdbimg.png`"}]}"
New-Item -ItemType Directory -Force "$www\stage\melonDS-1.1" | Out-Null
Copy-Item $game "$www\stage\melonDS-1.1\melonDS.exe"
New-Item -ItemType Directory -Force "$www\dl" | Out-Null
& "$env:SystemRoot\System32\tar.exe" -a -cf "$www\dl\melonDS-1.1-windows-x86_64.zip" -C "$www\stage" 'melonDS-1.1'
Put "$www\github\repos\melonDS-emu\melonDS\releases\latest" "{`"tag_name`":`"1.1`",`"assets`":[{`"name`":`"melonDS-1.1-macOS-universal.zip`",`"browser_download_url`":`"http://localhost:$port/dl/mac.zip`"},{`"name`":`"melonDS-1.1-windows-x86_64.zip`",`"browser_download_url`":`"http://localhost:$port/dl/melonDS-1.1-windows-x86_64.zip`"}]}"

$job = Start-Job -ArgumentList $port, $served, $ready, $www -ScriptBlock {
    param($port, $served, $ready, $www)
    $l = New-Object System.Net.HttpListener
    $l.Prefixes.Add("http://localhost:$port/")
    $l.Start()
    Set-Content $ready 'ready'
    while ($l.IsListening) {
        $c = $l.GetContext()
        $p = [Uri]::UnescapeDataString($c.Request.Url.AbsolutePath)
        $auth = $c.Request.Headers['Authorization']
        Add-Content $served ($p + $(if ($auth) { "  [$auth]" } else { '' }))
        $r = $c.Response
        $f = Join-Path $www ($p.TrimStart('/') -replace '/', '\')
        if ($p -like '/sgdb/*' -and $auth -ne 'Bearer s7-test-key') { $r.StatusCode = 401; $b = @() }
        elseif (Test-Path -LiteralPath $f -PathType Leaf) { $b = [IO.File]::ReadAllBytes($f) }
        else { $r.StatusCode = 404; $b = @() }
        if ($b.Length) { $r.OutputStream.Write($b, 0, $b.Length) }
        $r.Close()
    }
}
$fails = 0
function Check($ok, $what) { if ($ok) { "ok   $what" } else { "FAIL $what"; $script:fails++ } }
try {
    $until = (Get-Date).AddSeconds(20)
    while (-not (Test-Path $ready) -and (Get-Date) -lt $until -and $job.State -eq 'Running') { Start-Sleep -Milliseconds 200 }
    if (-not (Test-Path $ready)) { "server did not start: $($job.State)"; Receive-Job $job -ErrorAction SilentlyContinue | Out-Host; exit 1 }
    $env:IONEA_TESTROOT = $T
    $runs = @(
        @{ Name = 's7-scan'; Data = $data; Args = "--scan-home `"$H`" --no-import --mute" },
        @{ Name = 's7-scan-first'; Data = $data2; Args = "--scan-home `"$T\empty`" --scan-prompt --no-import --mute" }
    )
    foreach ($run in $runs) {
        $d = $run.Data
        $a = "--data `"$d`" --windowed --size 1040x620 --pos -30000,-30000 --shots `"$d\shots`" --online-test-host localhost:$port $($run.Args) --script `"$PSScriptRoot\$($run.Name).txt`""
        $p = Start-Process $Exe -ArgumentList $a -PassThru
        if (-not $p.WaitForExit(240000)) { "launcher still running; stopping it"; $p.Kill(); $fails++ }
        $log = Get-Content "$d\launcher.log"
        $log | Where-Object { $_ -match 'SCRIPT (FAIL|done|scan:)|^.{0,40}(scan|covers|emulator (installed|download))' } | Out-Host
        Check (($log -match 'SCRIPT done, 0 failed').Count -eq 1) "$($run.Name): script 0 failed"
    }
    $got = @(if (Test-Path $served) { Get-Content $served })
    "served:"; $got | ForEach-Object { "   $_" }
    Check (@($got | Where-Object { $_ -like '/sgdb/*' -and $_ -notlike '*`[Bearer s7-test-key`]' }).Count -eq 0) 'SteamGridDB always got the key'
    Check (@($got | Where-Object { $_ -like '/thumbs/*' -and $_ -like '*Bearer*' }).Count -eq 0) 'the key went nowhere else'
    Check (@($got | Where-Object { $_ -like '*Mystery Quest (Europe).png' }).Count -eq 1) 'the .dat named the game by its CRC'
    Check (@($got | Where-Object { $_ -like '*Pocket Quest (USA).png' }).Count -eq 1) 'the .dat named the game by its game code'
    Check (@($got | Where-Object { $_ -like '/thumbs/*Star Racer*' }).Count -eq 0) 'never by the file name'
    Check (@($got | Where-Object { $_ -eq '/dl/mac.zip' }).Count -eq 0) 'only the Windows file was downloaded'
    $s = Get-Content "$data\settings.json" -Raw | ConvertFrom-Json
    Check ($s.ScanAsked -eq $true) 'scan: ScanAsked saved'
    Check ($s.Paths.Esde -eq "$H\ES-DE") "scan: ES-DE saved ($($s.Paths.Esde))"
    Check (@($s.Paths.Games).Count -eq 5) "scan: 5 game folders saved ($(@($s.Paths.Games).Count))"
    $s2 = Get-Content "$data2\settings.json" -Raw | ConvertFrom-Json
    Check ($s2.ScanAsked -eq $true) 'first start: ScanAsked saved'
    Check (-not (Test-Path "$data2\downloads\melonDS-1.1-windows-x86_64.zip")) 'first start: the download was removed after installing'
}
finally {
    Stop-Job $job -ErrorAction SilentlyContinue; Remove-Job $job -Force -ErrorAction SilentlyContinue
    $env:IONEA_TESTROOT = $null
}
"s7: $fails failed"
