# o4: the performance overlay's frame rate over a native port (its frame log) and an emulator inside Ionea (its
# window's frames). Fake bbport and shadPS4 are copies of the harmless TestGame.exe, drawing
# a frame every 20 ms (IONEA_TESTGAME_ANIM); bb-probe also writes a bbport-style frame log
# (IONEA_TESTGAME_FRAMELOG=..\logs, beside bbport-run.exe). Every game is made up: a few bytes and a param.sfo.
# Windowed, muted, second monitor, no real input.
# Usage: o4-overlay-embed.ps1 [-Exe <Launcher.exe>] [-Root <dir>] [-Pos X,Y]   (wipes <dir>)
param([string]$Exe = "$PSScriptRoot\..\bin\Release\net10.0-windows\Launcher.exe", [string]$Root = "$env:TEMP\Ionea-tests\o4-overlay", [string]$Pos = '-1060,650')
$ErrorActionPreference = 'Stop'
$game = Join-Path $PSScriptRoot 'bin\TestGame.exe'
if (Test-Path $Root) { Remove-Item $Root -Recurse -Force }
$T = "$Root\T"; $data = "$Root\data"; $ps4 = "$T\esde\ROMs\ps4"
foreach ($d in 'emus\shadPS4', 'emus\bbport\bin') { New-Item -ItemType Directory -Force (Join-Path $T $d) | Out-Null }
New-Item -ItemType Directory -Force $data | Out-Null
Copy-Item $game "$T\emus\shadPS4\shadPS4.exe"
Copy-Item $game "$T\emus\bbport\bbport-run.exe"
Copy-Item $game "$T\emus\bbport\bin\bb-probe.exe"
$utf8 = New-Object System.Text.UTF8Encoding($false)
[IO.File]::WriteAllText("$T\emus\bbport\testgame-child.txt", 'bin\bb-probe.exe', $utf8)
function Put($p, $text) { New-Item -ItemType Directory -Force (Split-Path $p) | Out-Null; [IO.File]::WriteAllText($p, $text, $utf8) }
# A PS4 param.sfo with text values only (the layout GameId.Sfo reads), as w1-ports.ps1 makes it.
function Sfo($path, $id, $ver) {
    $vals = [ordered]@{ APP_VER = $ver; CONTENT_ID = "UP9000-$($id)_00-0000000000000000"; TITLE = 'Test Game'; TITLE_ID = $id }
    $keys = New-Object IO.MemoryStream; $dat = New-Object IO.MemoryStream; $ents = New-Object IO.MemoryStream
    $bw = New-Object IO.BinaryWriter($ents)
    foreach ($k in $vals.Keys) {
        $kb = [Text.Encoding]::ASCII.GetBytes($k + "`0"); $vb = [Text.Encoding]::UTF8.GetBytes($vals[$k] + "`0")
        $max = [int]([Math]::Ceiling($vb.Length / 4) * 4)
        $bw.Write([uint16]$keys.Length); $bw.Write([uint16]0x0204); $bw.Write([uint32]$vb.Length); $bw.Write([uint32]$max); $bw.Write([uint32]$dat.Length)
        $keys.Write($kb, 0, $kb.Length); $dat.Write($vb, 0, $vb.Length); $dat.Write((New-Object byte[] ($max - $vb.Length)), 0, $max - $vb.Length)
    }
    while ($keys.Length % 4) { $keys.WriteByte(0) }
    $n = $vals.Count; $keyStart = 20 + 16 * $n; $dataStart = $keyStart + $keys.Length
    $out = New-Object IO.MemoryStream; $w = New-Object IO.BinaryWriter($out)
    $w.Write([uint32]0x46535000); $w.Write([uint32]0x101); $w.Write([uint32]$keyStart); $w.Write([uint32]$dataStart); $w.Write([uint32]$n)
    $w.Write($ents.ToArray()); $w.Write($keys.ToArray()); $w.Write($dat.ToArray())
    New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
    [IO.File]::WriteAllBytes($path, $out.ToArray())
}
foreach ($g in @(@("Bloodborne [CUSA03173]", 'CUSA03173', '01.09'), @("Other [CUSA99999]", 'CUSA99999', '01.00'))) {
    Put "$ps4\$($g[0])\eboot.bin" 'not a real game'
    Sfo "$ps4\$($g[0])\sce_sys\param.sfo" $g[1] $g[2]
}

$env:IONEA_TESTROOT = "$T\"; $env:IONEA_TESTGAME_SECS = '40'; $env:IONEA_TESTGAME_OFFSCREEN = '1'
$env:IONEA_TESTGAME_ANIM = '20'; $env:IONEA_TESTGAME_FRAMELOG = '..\logs'
$fails = 0
function Check($ok, $what) { if ($ok) { "ok   $what" } else { "FAIL $what"; $script:fails++ } }
$a = "--data `"$data`" --no-import --emulators `"$T\emus`" --windowed --mute --size 1040x620 --pos $Pos --shots `"$data\shots`" --script `"$PSScriptRoot\o4-overlay-embed.txt`""
$p = Start-Process $Exe -ArgumentList $a -PassThru
$kids = @{}; $t0 = Get-Date
while (-not $p.HasExited -and ((Get-Date) - $t0).TotalSeconds -lt 180) {
    Get-CimInstance Win32_Process -Filter "ParentProcessId=$($p.Id) and Name<>'conhost.exe'" | ForEach-Object { $kids[$_.ProcessId] = $_.Name }
    Start-Sleep -Milliseconds 250
}
if (-not $p.HasExited) { "launcher still running; stopping PID $($p.Id)"; $p.Kill(); $fails++ }
foreach ($id in $kids.Keys) { Get-Process -Id $id -ErrorAction SilentlyContinue | ForEach-Object { "LEFTOVER $($kids[$id]) $id"; $_.Kill(); $script:fails++ } }
foreach ($v in 'SECS', 'OFFSCREEN', 'ANIM', 'FRAMELOG') { Remove-Item "env:IONEA_TESTGAME_$v" -ErrorAction SilentlyContinue }
$log = Get-Content "$data\launcher.log"
$log | Where-Object { $_ -match 'SCRIPT (FAIL|done|overlay|screen)|overlay:|natively' } | Out-Host
Check (($log -match 'SCRIPT done, 0 failed').Count -eq 1) 'script 0 failed'
Check (@(Get-ChildItem "$T\emus\bbport\logs\*.frames.csv" -ErrorAction SilentlyContinue).Count -ge 1) 'the fake port wrote a frame log'
# the frame rates shown: the fake draws 50 a second (a Windows timer: about 32-64)
foreach ($m in [regex]::Matches(($log -join "`n"), 'SCRIPT overlay: fps (\d+)')) { $f = [int]$m.Groups[1].Value; Check ($f -ge 25 -and $f -le 70) "frame rate shown $f" }
Check (($log -match 'overlay: frame rate from .*\.frames\.csv').Count -ge 1) 'the port: read from its frame log'
Check (($log -match 'overlay: counting frames of window').Count -ge 1) 'the emulator: its window''s frames counted'
Check (($log -match 'overlay: no frame count').Count -eq 0) 'window capture started'
"$fails failed"
exit $fails
