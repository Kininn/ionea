# Native ports: makes the test folder, runs w1-ports.txt and
# checks what was passed to the fake port, the prepared folder and that the game's own files are unchanged.
# Fake shadPS4 and fake Bloodborne port are copies of the harmless TestGame.exe; IONEA_TESTGAME_REPORT makes
# each start write its program, arguments and BB_GAME_DIR. Every game here is made up: a few bytes and a
# param.sfo. Windowed, muted, second monitor, no real input.
# -OtherDrive: the test home is reached through a spare drive letter (subst, removed at the end), so the games
# are on another drive than the data folder and must be prepared in <drive>\Ionea\Ports.
# Usage: w1-ports.ps1 [-Exe <Launcher.exe>] [-Root <dir>] [-Pos X,Y] [-OtherDrive]   (wipes <dir>)
param([string]$Exe = "$PSScriptRoot\..\bin\Release\net10.0-windows\Launcher.exe", [string]$Root = "$env:TEMP\Ionea-tests\w1-ports", [string]$Pos = '-30000,-30000', [switch]$OtherDrive)
$ErrorActionPreference = 'Stop'
$letter = $null
trap { if ($letter) { subst "$($letter):" /D | Out-Null }; break }
$game = Join-Path $PSScriptRoot 'bin\TestGame.exe'
if (Test-Path $Root) { Remove-Item $Root -Recurse -Force }
$T = "$Root\T"; $data = "$Root\data"; $ps4 = "$T\esde\ROMs\ps4"
foreach ($d in 'emus\shadPS4', 'emus\bbport\bin') { New-Item -ItemType Directory -Force (Join-Path $T $d) | Out-Null }
New-Item -ItemType Directory -Force $data | Out-Null
Copy-Item $game "$T\emus\shadPS4\shadPS4.exe"
Copy-Item $game "$T\emus\bbport\bbport-run.exe"
# as bbport: the started program has no window, it starts bin\bb-probe.exe (or BB_PROBE), whose window is the game (gameProgram)
Copy-Item $game "$T\emus\bbport\bin\bb-probe.exe"
$utf8 = New-Object System.Text.UTF8Encoding($false)
[IO.File]::WriteAllText("$T\emus\bbport\testgame-child.txt", 'bin\bb-probe.exe', $utf8)
function Put($p, $text) { New-Item -ItemType Directory -Force (Split-Path $p) | Out-Null; [IO.File]::WriteAllText($p, $text, $utf8) }

# A PS4 param.sfo with text values only (the layout GameId.Sfo reads).
function Sfo($path, $id, $ver) {
    $vals = [ordered]@{ APP_VER = $ver; CONTENT_ID = "UP9000-$($id)_00-0000000000000000"; TITLE = 'Test Port Game'; TITLE_ID = $id }
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
function Ps4Game($dir, $id, $ver) {
    Put "$dir\eboot.bin" 'not a real game'
    Put "$dir\dvdroot_ps4\base-only.dat" 'base only'
    Put "$dir\dvdroot_ps4\shared.dat" 'base copy'
    Sfo "$dir\sce_sys\param.sfo" $id $ver
}
$bb = "$ps4\Bloodborne [CUSA00900]"
Ps4Game $bb 'CUSA00900' '01.00'
Put "$bb-patch\eboot.bin" 'not a real update'
Put "$bb-patch\dvdroot_ps4\shared.dat" 'update copy'
Sfo "$bb-patch\sce_sys\param.sfo" 'CUSA00900' '01.09'
Ps4Game "$ps4\Bloodborne [CUSA00207]" 'CUSA00207' '01.00'
Ps4Game "$ps4\Bloodborne [CUSA03173]" 'CUSA03173' '01.09'

# Run: each fake stays open 40 s unless closed.
$report = "$Root\starts.txt"
$TR = $T; $prep = "$data\ports\bloodborne-bbport"; $letter = $null
if ($OtherDrive) {
    $used = (Get-PSDrive -PSProvider FileSystem).Name
    $letter = 'Z','Y','X','W','V','U','T','S','R' | Where-Object { $used -notcontains $_ } | Select-Object -First 1
    subst "$($letter):" $T; if ($LASTEXITCODE) { throw "subst $($letter): failed" }
    $TR = "$($letter):"; $prep = "$TR\Ionea\Ports\bloodborne-bbport"; "games on $TR (= $T)"
}
$env:IONEA_TESTROOT = "$TR\"; $env:IONEA_TESTGAME_SECS = '40'; $env:IONEA_TESTGAME_OFFSCREEN = '1'; $env:IONEA_TESTGAME_REPORT = $report
$fails = 0
function Check($ok, $what) { if ($ok) { "ok   $what" } else { "FAIL $what"; $script:fails++ } }
$a = "--data `"$data`" --no-import --emulators `"$TR\emus`" --windowed --mute --size 1040x620 --pos $Pos --shots `"$data\shots`" --script `"$PSScriptRoot\w1-ports.txt`""
$p = Start-Process $Exe -ArgumentList $a -PassThru
$kids = @{}; $t0 = Get-Date
while (-not $p.HasExited -and ((Get-Date) - $t0).TotalSeconds -lt 300) {
    Get-CimInstance Win32_Process -Filter "ParentProcessId=$($p.Id) and Name<>'conhost.exe'" | ForEach-Object { $kids[$_.ProcessId] = $_.Name }
    Start-Sleep -Milliseconds 250
}
if (-not $p.HasExited) { "launcher still running; stopping PID $($p.Id)"; $p.Kill(); $fails++ }
foreach ($id in $kids.Keys) { Get-Process -Id $id -ErrorAction SilentlyContinue | ForEach-Object { "LEFTOVER $($kids[$id]) $id"; $_.Kill(); $script:fails++ } }
$env:IONEA_TESTGAME_SECS = $null; $env:IONEA_TESTGAME_OFFSCREEN = $null; $env:IONEA_TESTGAME_REPORT = $null
$log = Get-Content "$data\launcher.log"
$log | Where-Object { $_ -match 'SCRIPT (FAIL|done|mark)|ports:|natively|definitions:|embed:|pause|tweaks:' } | Out-Host
Check (($log -match 'SCRIPT done, 0 failed').Count -eq 1) 'script 0 failed'
Check (($log -match 'definitions: .* 1 ports').Count -ge 1) 'the port definition is loaded'

# what each start was given
$starts = @(Get-Content $report -ErrorAction SilentlyContinue)
$starts | Out-Host
Check ($starts.Count -eq 4) 'four starts'
Check ($starts[0] -eq "Launcher.exe|--play|$prep\CUSA00900") 'native: --play and BB_GAME_DIR = the prepared folder'
Check ($starts[1] -eq $starts[0]) 'native again: the same'
Check ($starts[2] -like 'Launcher.exe|-f false -g *Bloodborne `[CUSA00900`]\eboot.bin|') 'emulator: shadPS4 gets the game itself, no BB_GAME_DIR'
Check ($starts[3] -eq "Launcher.exe|--play|$prep\CUSA03173") 'only the port: started natively'
Check (($log -match 'ports: preparing').Count -eq 2) 'prepared once per game'
Check (($log -match 'ports: .* refused "BB Old"').Count -eq 1) 'wrong version refused'
# the game program's window: taken in, paused with the starter, the game's own port settings written and put back
Check (($log -match 'ports: bb-probe.exe runs as .*bbport.bin.Launcher\.exe').Count -ge 1) 'native: bb-probe runs through its link named like Ionea (BB_PROBE)'
Check (($log -match 'embed: the game runs in Launcher').Count -ge 1) 'native: bb-probe''s window taken in'
Check (($log -match 'pause: Launcher \(\d+\) paused too').Count -ge 1) 'native: the Home menu pauses bb-probe too'
Check (($log -match 'tweaks: "Bloodborne" applied in Bloodborne \(bbport\): .*upscaler=fsr4').Count -eq 2) 'native: the game''s port settings written at both native starts'
Check (($log -match 'tweaks: Bloodborne \(bbport\)''s every-game values back after "Bloodborne"').Count -eq 2) 'native: the port''s every-game values put back'
Check (-not (Test-Path "$data\video-applied\*")) 'native: nothing left applied'

# the prepared folder: the update over the base game, hard links, stamped
$pd = "$prep\CUSA00900"
Check ((Get-Content "$pd\eboot.bin") -eq 'not a real update') 'prepared: the update''s eboot'
Check ((Get-Content "$pd\dvdroot_ps4\shared.dat") -eq 'update copy') 'prepared: the update''s copy of a shared file'
Check ((Get-Content "$pd\dvdroot_ps4\base-only.dat") -eq 'base only') 'prepared: the base game''s own files'
Check ((Get-Item "$pd\dvdroot_ps4\base-only.dat").LinkType -eq 'HardLink') 'prepared: hard links, not copies'
Check (Test-Path "$pd\ionea-prepared.txt") 'prepared: stamped'
Check (-not (Test-Path "$pd.part")) 'prepared: no half-made folder left'
# the game's own files are unchanged
Check ((Get-Content -LiteralPath "$bb\eboot.bin") -eq 'not a real game') 'the base game''s eboot is unchanged'
Check ((Get-Content -LiteralPath "$bb\dvdroot_ps4\shared.dat") -eq 'base copy') 'the base game''s shared file is unchanged'
# saves: the profile's own folder through a link
$sv = Get-Item "$T\emus\bbport\user" -Force -ErrorAction SilentlyContinue
Check ($sv -and $sv.Attributes -match 'ReparsePoint') 'the port''s saves are the profile''s (link)'
if ($OtherDrive) { Check (-not (Test-Path "$data\ports")) 'other drive: nothing prepared on the data drive' }
if ($letter) { subst "$($letter):" /D | Out-Null }
"$fails failed"
exit $fails
