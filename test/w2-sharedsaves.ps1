# Shared saves: makes the test folder, runs w2-sharedsaves.txt and checks the made-up save folders:
# the shared one is picked, the newer save always wins (an older one never overwrites it), every replaced copy is in
# a backup, the port's own metadata is untouched, and Separate gives the port its own copy. Fake shadPS4 and fake
# bbport are copies of the harmless TestGame.exe and are never started. Windowed, muted, off-screen, no real input.
# Usage: w2-sharedsaves.ps1 [-Exe <Launcher.exe>] [-Root <dir>] [-Pos X,Y]   (wipes <dir>)
param([string]$Exe = "$PSScriptRoot\..\bin\Release\net10.0-windows\Launcher.exe", [string]$Root = "$env:TEMP\Ionea-tests\w2-sharedsaves", [string]$Pos = '-30000,-30000')
$ErrorActionPreference = 'Stop'
$game = Join-Path $PSScriptRoot 'bin\TestGame.exe'
if (Test-Path $Root) { Remove-Item $Root -Recurse -Force }
$T = "$Root\T"; $data = "$Root\data"; $bb = "$T\esde\ROMs\ps4\Bloodborne [CUSA00900]"
foreach ($d in 'emus\shadPS4', 'emus\bbport\bin') { New-Item -ItemType Directory -Force (Join-Path $T $d) | Out-Null }
New-Item -ItemType Directory -Force $data, "$bb\sce_sys" | Out-Null
Copy-Item $game "$T\emus\shadPS4\shadPS4.exe"
Copy-Item $game "$T\emus\bbport\bbport-run.exe"
Copy-Item $game "$T\emus\bbport\bin\bb-probe.exe"
$utf8 = New-Object System.Text.UTF8Encoding($false)
[IO.File]::WriteAllText("$bb\eboot.bin", 'not a real game', $utf8)
# a PS4 param.sfo with text values only (the layout GameId.Sfo reads), as in w1-ports.ps1
$vals = [ordered]@{ APP_VER = '01.09'; CONTENT_ID = 'UP9000-CUSA00900_00-0000000000000000'; TITLE = 'Test Port Game'; TITLE_ID = 'CUSA00900' }
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
[IO.File]::WriteAllBytes("$bb\sce_sys\param.sfo", $out.ToArray())

$env:IONEA_TESTROOT = "$T\"
$fails = 0
function Check($ok, $what) { if ($ok) { "ok   $what" } else { "FAIL $what"; $script:fails++ } }
$a = "--data `"$data`" --no-import --emulators `"$T\emus`" --windowed --mute --size 1040x620 --pos $Pos --script `"$PSScriptRoot\w2-sharedsaves.txt`""
$p = Start-Process $Exe -ArgumentList $a -PassThru
if (-not $p.WaitForExit(120000)) { "launcher still running; stopping PID $($p.Id)"; $p.Kill(); $fails++ }
$log = Get-Content "$data\launcher.log"
$log | Where-Object { $_ -match 'SCRIPT|saves' } | Out-Host
Check (($log -match 'SCRIPT done, 0 failed').Count -eq 1) 'script 0 failed'

$pf = @(Get-ChildItem "$data\profiles" -Directory | Where-Object { Test-Path "$($_.FullName)\saves" })[0].FullName
$emu = "$pf\saves\shadps4\user\home\1000\savedata\CUSA00900"; $port = "$pf\saves\bloodborne-bbport\user\savedata\1\CUSA00900"; $bk = "$pf\save-backups"
function Text($p) { if (Test-Path -LiteralPath $p) { (Get-Content -LiteralPath $p -Raw).Trim() } else { '(missing)' } }
function BackupWith($pattern, $text) { @(Get-ChildItem $bk -Directory -Filter $pattern -ErrorAction SilentlyContinue | Where-Object { (Text "$($_.FullName)\userdata0000") -eq $text }).Count -ge 1 }
$i = [array]::IndexOf($log, ($log -match 'SCRIPT mark shared')[0]); $j = [array]::IndexOf($log, ($log -match 'SCRIPT mark again')[0])
$shared = $log[0..$i] -join "`n"
# shared (first sync)
Check ($shared -match 'SPRJ0005: linked to' -and $shared -match 'SPRJ0005: the emulator''s save is newer') 'SPRJ0005: the emulator''s newer save picked and linked'
Check (BackupWith 'Bloodborne SPRJ0005 port *' 'port older') 'SPRJ0005: the port''s older copy kept in a backup'
Check ($shared -match 'SPRJ0006: the port''s save is newer') 'SPRJ0006: the port''s newer save picked'
Check (BackupWith 'Bloodborne SPRJ0006 emulator *' 'emu older') 'SPRJ0006: the emulator''s older copy kept in a backup'
Check ($shared -match 'SPRJ0007: the port''s save is the shared one now') 'SPRJ0007: only on the port: moved to the shared place'
Check ($shared -match 'SPRJ0008: linked to') 'SPRJ0008: only on the emulator: linked for the port'
Check (BackupWith "Bloodborne SPRJ0005 $(Get-Date -Format yyyy-MM-dd)" 'emu newer') 'today''s backup of the shared save'
Check (($log[($i + 1)..$j] -match 'saves \(test\)').Count -eq 0) 'second sync: nothing to do'
# separate (final state)
Check ((Text "$emu\SPRJ0005\userdata0000") -eq 'emu newer') 'the emulator''s save never overwritten by an older one'
Check ((Text "$emu\SPRJ0006\userdata0000") -eq 'port newer') 'SPRJ0006: the shared save is the newer one'
Check ((Text "$emu\SPRJ0007\userdata0000") -eq 'port only') 'SPRJ0007: in the emulator''s place'
Check ((Text "$emu\SPRJ0005\sce_sys\param.sfo") -eq 'emu meta') 'the emulator''s metadata kept'
Check ((Text "$port\SPRJ0005.sce_sys\param.bin") -eq 'port meta' -and -not ((Get-Item "$port\SPRJ0005.sce_sys").Attributes -match 'ReparsePoint')) 'the port''s own metadata untouched'
$real = @('SPRJ0005', 'SPRJ0006', 'SPRJ0007', 'SPRJ0008' | Where-Object { (Test-Path "$port\$_") -and -not ((Get-Item "$port\$_").Attributes -match 'ReparsePoint') }).Count
Check ($real -eq 4) 'separate: the port has its own four save folders (no links)'
Check ((Text "$port\SPRJ0005\userdata0000") -eq 'port separate') 'separate: a change on the port side stays there'
Check ((Text "$port\SPRJ0008\userdata0000") -eq 'emu only') 'separate: the port''s copy holds the shared save'
"$fails failed"
exit $fails
