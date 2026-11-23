# Add-ons for a native port: makes the test folder, runs w3-addons.txt and checks the links. The fake
# shadPS4 has made-up add-on folders (DLCA, DLCB) in user\addcont\CUSA00900; after the first sync DLCB is removed and
# DLCC added, so the second sync must follow. The add-on files are never changed or moved, a real folder in the port's
# place is kept. Fake shadPS4 and fake bbport are copies of the harmless TestGame.exe and are never started.
# Usage: w3-addons.ps1 [-Exe <Launcher.exe>] [-Root <dir>] [-Pos X,Y]   (wipes <dir>)
param([string]$Exe = "$PSScriptRoot\..\bin\Release\net10.0-windows\Launcher.exe", [string]$Root = "$env:TEMP\Ionea-tests\w3-addons", [string]$Pos = '-30000,-30000')
$ErrorActionPreference = 'Stop'
$game = Join-Path $PSScriptRoot 'bin\TestGame.exe'
if (Test-Path $Root) { cmd /c rmdir /s /q "$Root" }   # rmdir never follows the junctions inside
$T = "$Root\T"; $data = "$Root\data"; $bb = "$T\esde\ROMs\ps4\Bloodborne [CUSA00900]"
$ac = "$T\emus\shadPS4\user\addcont\CUSA00900"
foreach ($d in 'emus\shadPS4', 'emus\bbport\bin') { New-Item -ItemType Directory -Force (Join-Path $T $d) | Out-Null }
New-Item -ItemType Directory -Force $data, "$bb\sce_sys" | Out-Null
Copy-Item $game "$T\emus\shadPS4\shadPS4.exe"
Copy-Item $game "$T\emus\bbport\bbport-run.exe"
Copy-Item $game "$T\emus\bbport\bin\bb-probe.exe"
$utf8 = New-Object System.Text.UTF8Encoding($false)
foreach ($n in 'DLCA', 'DLCB') { New-Item -ItemType Directory -Force "$ac\$n\sce_sys" | Out-Null; [IO.File]::WriteAllText("$ac\$n\sce_sys\license.dat", "licence $n", $utf8) }
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
$a = "--data `"$data`" --no-import --emulators `"$T\emus`" --windowed --mute --size 1040x620 --pos $Pos --script `"$PSScriptRoot\w3-addons.txt`""
$p = Start-Process $Exe -ArgumentList $a -PassThru
# between the two syncs: DLCB goes away, DLCC comes
$until = (Get-Date).AddSeconds(60); $changed = $false
while ((Get-Date) -lt $until -and -not $p.HasExited) {
    if ((Test-Path "$data\launcher.log") -and (Select-String -Path "$data\launcher.log" -Pattern 'SCRIPT mark first' -Quiet)) {
        cmd /c rmdir /s /q "$ac\DLCB"
        New-Item -ItemType Directory -Force "$ac\DLCC\sce_sys" | Out-Null; [IO.File]::WriteAllText("$ac\DLCC\sce_sys\license.dat", 'licence DLCC', $utf8)
        $changed = $true; break
    }
    Start-Sleep -Milliseconds 200
}
Check $changed 'add-ons changed between the two syncs'
if (-not $p.WaitForExit(120000)) { "launcher still running; stopping PID $($p.Id)"; $p.Kill(); $fails++ }
$log = Get-Content "$data\launcher.log"
$log | Where-Object { $_ -match 'SCRIPT|add-ons' } | Out-Host
Check (($log -match 'SCRIPT done, 0 failed').Count -eq 1) 'script 0 failed'

$pf = @(Get-ChildItem "$data\profiles" -Directory | Where-Object { Test-Path "$($_.FullName)\saves" })[0].FullName
$to = "$pf\saves\bloodborne-bbport\user\addcont\CUSA00900"
function IsLink($p) { (Test-Path -LiteralPath $p) -and ((Get-Item -LiteralPath $p -Force).Attributes -match 'ReparsePoint') }
function Target($p) { (Get-Item -LiteralPath $p -Force).Target | Select-Object -First 1 }
function Text($p) { if (Test-Path -LiteralPath $p) { (Get-Content -LiteralPath $p -Raw).Trim() } else { '(missing)' } }
$i = [array]::IndexOf($log, ($log -match 'SCRIPT mark first')[0])
$first = $log[0..$i] -join "`n"; $second = $log[($i + 1)..($log.Count - 1)] -join "`n"
Check ($first -match 'DLCA: linked to' -and $first -match 'DLCB: linked to') 'first sync: both add-ons linked'
Check ((IsLink "$to\DLCA") -and (Target "$to\DLCA") -like "*\addcont\CUSA00900\DLCA") 'DLCA: a junction to shadPS4''s folder'
Check ((Text "$to\DLCA\sce_sys\license.dat") -eq 'licence DLCA') 'DLCA: the port reads the add-on''s files through the link'
Check ($second -match 'DLCB: an old link removed' -and -not (Test-Path -LiteralPath "$to\DLCB")) 'second sync: the removed add-on''s link is gone'
Check ($second -match 'DLCC: linked to' -and (IsLink "$to\DLCC")) 'second sync: the new add-on is linked'
Check (-not ($second -match 'DLCA:')) 'second sync: DLCA left as it is'
Check ((Text "$to\OWN\own.txt") -eq 'own folder' -and -not (IsLink "$to\OWN")) 'the real folder untouched'
Check ((Text "$ac\DLCA\sce_sys\license.dat") -eq 'licence DLCA' -and (Text "$ac\DLCC\sce_sys\license.dat") -eq 'licence DLCC' -and -not (IsLink "$ac\DLCA")) 'the add-on files themselves unchanged, still real folders'
"$fails failed"
exit $fails
