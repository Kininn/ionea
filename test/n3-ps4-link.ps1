# shadPS4 starts through a hard link named like the launcher (EmbedLogic.Alias), so Task Manager groups it
# under the launcher. Checks with the fake shadPS4 (TestGame.exe, closes after 3 s):
#  run 1: the link is made, the emulator runs as Launcher.exe, the game counts as ended when it closes
#         (the old name check would have matched the launcher itself and never ended);
#  run 2: after shadPS4.exe is replaced (an update), the link is made again from the new file;
#  run 3: when the link can't be made (a folder is in the way), shadPS4.exe itself is started.
# Usage: n3-ps4-link.ps1 [-Exe <Launcher.exe>] [-Root <dir>]   (wipes <dir>)
param([string]$Exe = "$PSScriptRoot\..\bin\Release\net10.0-windows\Launcher.exe", [string]$Root = "$env:TEMP\Ionea-tests\ps4-link")
$ErrorActionPreference = 'Stop'
$env:IONEA_TESTGAME_SECS = $null   # its fake game closes by itself (3 s); a value left by another test would keep it open
& (Join-Path $PSScriptRoot 'make-fake-ps4.ps1') $Root | Out-Null
$emu = "$Root\emus\shadPS4"; $data = "$Root\data"; $log = "$data\launcher.log"
New-Item -ItemType Directory -Force $data | Out-Null
$fails = 0
function Check($ok, $what) { if ($ok) { "ok   $what" } else { "FAIL $what"; $script:fails++ } }
function Run($name, $lines) {
  $s = "$Root\$name.txt"; Set-Content $s $lines -Encoding utf8
  $before = if (Test-Path $log) { (Get-Content $log).Count } else { 0 }
  $l = Start-Process $Exe -ArgumentList "--data `"$data`" --no-import --esde `"$Root\esde`" --emulators `"$Root\emus`" --windowed --mute --script `"$s`"" -PassThru
  $child = $null; $t0 = Get-Date
  while (-not $l.HasExited -and ((Get-Date) - $t0).TotalSeconds -lt 120) {
    if (-not $child) { $child = Get-CimInstance Win32_Process -Filter "ParentProcessId=$($l.Id) and Name<>'conhost.exe'" | Select-Object -First 1 }
    Start-Sleep -Milliseconds 100
  }
  if (-not $l.HasExited) { "launcher still running; stopping it"; $l.Kill(); $script:fails++ }
  if ($child) { Get-Process -Id $child.ProcessId -ErrorAction SilentlyContinue | ForEach-Object { "LEFTOVER emulator $($_.Id)"; $_.Kill() } }
  $new = Get-Content $log | Select-Object -Skip $before
  $new | Where-Object { $_ -match 'SCRIPT (FAIL|done)|embed: (linked|could not link|.*making the link)|launch "|ended' } | Out-Host
  [pscustomobject]@{ Child = $child; Log = $new }
}
function FileId($p) { (fsutil file queryfileid $p) -replace '.*: ', '' }

$launch = @('select Bloodborne', 'wait 500', 'key accept', 'waitlaunch 30', 'expect ingame none', 'quit')
$r1 = Run 'n3-run1' (@('wait 900', 'type Tester', 'key accept', 'wait 900', 'key accept', 'wait 2500', 'addchoice', 'wait 500', 'key accept',
  'wait 700', 'key accept', 'wait 800', 'key accept', 'wait 1200', 'expect games 1', 'expect emu shadPS4') + $launch)
Check ($r1.Child -and $r1.Child.Name -eq 'Launcher.exe' -and $r1.Child.ExecutablePath -eq "$emu\Launcher.exe") "run 1: emulator ran as $($r1.Child.ExecutablePath)"
Check (($r1.Log -match 'SCRIPT done, 0 failed').Count -eq 1) 'run 1: script 0 failed (the game ended)'
Check (($r1.Log -match 'embed: linked').Count -eq 1) 'run 1: link made'
Check ((FileId "$emu\Launcher.exe") -eq (FileId "$emu\shadPS4.exe")) 'run 1: Launcher.exe is shadPS4.exe'

# An update replaces shadPS4.exe with a new file.
$old = FileId "$emu\shadPS4.exe"
Remove-Item "$emu\shadPS4.exe"; Copy-Item (Join-Path $PSScriptRoot 'bin\TestGame.exe') "$emu\shadPS4.exe"
Check ((FileId "$emu\Launcher.exe") -ne (FileId "$emu\shadPS4.exe")) 'update: the link now differs from shadPS4.exe'
$r2 = Run 'n3-run2' (@('wait 1500', 'key accept', 'wait 2000') + $launch)
Check ($r2.Child -and $r2.Child.ExecutablePath -eq "$emu\Launcher.exe") "run 2: emulator ran as $($r2.Child.ExecutablePath)"
Check (($r2.Log -match 'making the link again').Count -eq 1 -and ($r2.Log -match 'embed: linked').Count -eq 1) 'run 2: link made again'
Check ((FileId "$emu\Launcher.exe") -eq (FileId "$emu\shadPS4.exe") -and (FileId "$emu\shadPS4.exe") -ne $old) 'run 2: Launcher.exe is the new shadPS4.exe'
Check (($r2.Log -match 'SCRIPT done, 0 failed').Count -eq 1) 'run 2: script 0 failed'

# The link can't be made: the emulator's own exe is started.
Remove-Item "$emu\Launcher.exe"; New-Item -ItemType Directory "$emu\Launcher.exe" | Out-Null
$r3 = Run 'n3-run3' (@('wait 1500', 'key accept', 'wait 2000') + $launch)
Check ($r3.Child -and $r3.Child.ExecutablePath -eq "$emu\shadPS4.exe") "run 3: emulator ran as $($r3.Child.ExecutablePath)"
Check (($r3.Log -match 'could not link').Count -eq 1) 'run 3: link failure logged'
Check (($r3.Log -match 'SCRIPT done, 0 failed').Count -eq 1) 'run 3: script 0 failed'
"n3: $fails failed"
