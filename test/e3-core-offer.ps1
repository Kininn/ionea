# e3: a standalone emulator added for systems a downloadable core also plays offers that core once (
# coreoffer). Fake mGBA and melonDS = TestGame.exe; the "mGBA core" download is a zip with a copy of TestGame.exe
# named mgba_libretro.dll, served by http://localhost:47815 (--online-test-host: the buildbot address goes to
# /release/<host><path>). The core is installed, never run. Then "Which emulator?" lists the core first.
# melonDS's offer is declined (Keep melonDS only). Windowed, off-screen, muted, no real input; nothing leaves this PC.
# Usage: e3-core-offer.ps1 [-Exe <Launcher.exe>] [-Root <dir>]   (wipes <dir>)
param([string]$Exe = "$PSScriptRoot\..\bin\Release\net10.0-windows\Launcher.exe", [string]$Root = "$env:TEMP\Ionea-tests\coreoffer")
$ErrorActionPreference = 'Stop'
$port = 47815
$game = Join-Path $PSScriptRoot 'bin\TestGame.exe'
if (Test-Path $Root) { Remove-Item $Root -Recurse -Force }
$www = "$Root\www"; $served = "$Root\served.txt"; $ready = "$Root\ready.txt"
foreach ($d in 'data', 'emus', 'pick\mGBA', 'pick\melonDS', 'roms', 'stage', 'www\release\buildbot.libretro.com\nightly\windows\x86_64\latest') { New-Item -ItemType Directory -Force "$Root\$d" | Out-Null }
if (Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue) { "port $port is in use"; exit 1 }
Copy-Item $game "$Root\pick\mGBA\mGBA.exe"; Set-Content "$Root\pick\mGBA\portable.ini" ''
Copy-Item $game "$Root\pick\melonDS\melonDS.exe"
Set-Content -LiteralPath "$Root\roms\Pocket Test (USA).gba" 'not a real game'
Copy-Item $game "$Root\stage\mgba_libretro.dll"
& "$env:SystemRoot\System32\tar.exe" -a -cf "$www\release\buildbot.libretro.com\nightly\windows\x86_64\latest\mgba_libretro.dll.zip" -C "$Root\stage" 'mgba_libretro.dll'

$job = Start-Job -ArgumentList $port, $served, $ready, $www -ScriptBlock {
    param($port, $served, $ready, $www)
    $l = New-Object System.Net.HttpListener
    $l.Prefixes.Add("http://localhost:$port/")
    $l.Start()
    Set-Content $ready 'ready'
    while ($l.IsListening) {
        $c = $l.GetContext()
        $p = [Uri]::UnescapeDataString($c.Request.Url.AbsolutePath)
        Add-Content $served $p
        $r = $c.Response
        $f = Join-Path $www ($p.TrimStart('/') -replace '/', '\')
        if (Test-Path -LiteralPath $f -PathType Leaf) { $b = [IO.File]::ReadAllBytes($f) } else { $r.StatusCode = 404; $b = @() }
        if ($b.Length) { $r.OutputStream.Write($b, 0, $b.Length) }
        $r.Close()
    }
}
$fails = 0
try {
    $until = (Get-Date).AddSeconds(20)
    while (-not (Test-Path $ready) -and (Get-Date) -lt $until -and $job.State -eq 'Running') { Start-Sleep -Milliseconds 200 }
    if (-not (Test-Path $ready)) { "server did not start: $($job.State)"; exit 1 }
    $s = "$Root\e3.txt"
    Set-Content $s -Encoding utf8 @(
      'wait 2500', 'type Tester', 'key accept', 'wait 900', 'key accept', 'wait 2500',
      'online cores on',
      "addemu $Root\pick\mGBA\mGBA.exe", 'wait 800', 'expect emus 1', 'expect screen CoreOfferCard', 'expect cardfocus Get the mGBA core', 'shot e3-offer',
      'key accept', 'wait 1500', 'expect screen DownloadCard',
      "expect downloadaddress http://localhost:$port/release/buildbot.libretro.com/nightly/windows/x86_64/latest/mgba_libretro.dll.zip",
      'key down', 'expect cardfocus Download', 'key accept', 'waitemuinstall 20', 'wait 600',
      'expect coreoffer installed mGBA core', 'expect emus 2', 'expect screen EmulatorsCard',
      "addemu $Root\pick\melonDS\melonDS.exe", 'wait 800', 'expect emus 3', 'expect screen CoreOfferCard',
      'cardrow Keep melonDS only', 'key accept', 'wait 600', 'expect coreoffer kept', 'expect screen EmulatorsCard', 'expect emus 3',
      'closebutton', 'wait 700',
      "addrom $Root\roms\Pocket Test (USA).gba", 'wait 800', 'expect screen EmuChoiceCard', 'expect cardfocus mGBA core', 'shot e3-choice',
      'quit')
    $l = Start-Process $Exe -ArgumentList "--data `"$Root\data`" --no-import --emulators `"$Root\emus`" --windowed --mute --size 1040x620 --pos -30000,-30000 --online-test-host localhost:$port --script `"$s`" --shots `"$Root\shots`"" -PassThru
    if (-not $l.WaitForExit(120000)) { "launcher still running after 120 s; stopping it"; $l.Kill(); $fails++ }
    $log = Get-Content "$Root\data\launcher.log"
    $log | Where-Object { $_ -match 'SCRIPT (FAIL|done)|core offer|emulator (installed|added)' } | Out-Host
    if (($log -match 'SCRIPT done, 0 failed').Count -ne 1) { $fails++ }
    if (-not (Test-Path "$Root\emus\Cores\mgba_libretro.dll")) { "the core was not installed in emus\Cores"; $fails++ }
    if (($log -match 'core offer: ').Count -ne 2) { "want 2 core offers"; $fails++ }
} finally {
    Stop-Job $job -ErrorAction SilentlyContinue; Remove-Job $job -Force -ErrorAction SilentlyContinue
}
"e3: $fails failed"
