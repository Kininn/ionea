# o2: the outro when the launcher closes (Outro.cs, MainWindow.BeginQuit).
#  Run 1: no game. A close plays the ~0.5 s outro, presses during it don't stop it, then the launcher exits.
#  Run 2: a dummy game (fake shadPS4 = TestGame.exe, takes 2.5 s to close) paused in the Home menu. A close
#         first shows the game's closing screen, the game closes by itself (not killed), then the outro, then exit.
# Windowed, muted, off screen, --outro on (test runs have no outro unless asked), never takes the front.
# Usage: o2-outro.ps1 [-Exe <Launcher.exe>] [-Root <dir>]   (wipes <dir>)
param([string]$Exe = "$PSScriptRoot\..\bin\Release\net10.0-windows\Launcher.exe", [string]$Root = "$env:TEMP\Ionea-tests\outro")
$ErrorActionPreference = 'Stop'
$fails = 0

function Run-Launcher([string]$argsLine, [string]$data, [int]$limit) {
  $l = Start-Process $Exe -ArgumentList $argsLine -PassThru
  $kids = @{}; $t0 = Get-Date
  while (-not $l.HasExited -and ((Get-Date) - $t0).TotalSeconds -lt $limit) {
    Get-CimInstance Win32_Process -Filter "ParentProcessId=$($l.Id) and Name<>'conhost.exe'" | ForEach-Object { $kids[$_.ProcessId] = $_.Name }
    Start-Sleep -Milliseconds 200
  }
  $bad = 0
  if (-not $l.HasExited) { Write-Host "launcher still running after $limit s; stopping PID $($l.Id)"; $l.Kill(); $bad++ }
  foreach ($id in $kids.Keys) { Get-Process -Id $id -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "LEFTOVER $($kids[$id]) $id"; $_.Kill(); $bad++ } }
  return $bad
}
function At([string[]]$log, [string]$pattern) {
  $m = $log | Where-Object { $_ -match $pattern } | Select-Object -First 1
  if (-not $m) { return $null }
  return [datetime]::ParseExact($m.Substring(0, 23), 'yyyy-MM-dd HH:mm:ss.fff', $null)
}
function Check([string[]]$log, [string]$name, [string[]]$must) {
  $bad = 0
  foreach ($p in $must) { if (-not ($log -match $p)) { Write-Host "$name MISSING: $p"; $bad++ } }
  $f = @($log -match 'SCRIPT FAIL'); if ($f.Count) { $f | Out-Host | Out-Null; $bad += $f.Count }
  $s = At $log 'outro: start'; $d = At $log 'outro: done'
  if ($s -and $d) { $ms = ($d - $s).TotalMilliseconds; Write-Host "$name outro took $ms ms"; if ($ms -lt 450 -or $ms -gt 900) { Write-Host "$name outro length off"; $bad++ } }
  return $bad
}

# ---- run 1: no game ----
if (Test-Path $Root) { Remove-Item $Root -Recurse -Force }
$d1 = "$Root\nogame\data"; New-Item -ItemType Directory -Force $d1 | Out-Null
$s1 = "$Root\o2-nogame.txt"
Set-Content $s1 -Encoding utf8 @(
  'wait 1500', 'expect quitting no', 'closewin', 'expect quitting yes', 'expect outro yes',
  'tap accept', 'tap back', 'closewin', 'expect outro yes', 'log reached-end', 'shot o2-outro-nogame')
$fails += Run-Launcher "--data `"$d1`" --no-import --dummies 12 --windowed --mute --outro on --size 1040x620 --pos -30000,-30000 --script `"$s1`" --shots `"$Root\shots`"" $d1 30
$log1 = Get-Content "$d1\launcher.log"
$log1 | Where-Object { $_ -match 'SCRIPT|quit:|outro|exit' } | Out-Host
$fails += Check $log1 'nogame' @('quit: outro', 'outro: start', 'SCRIPT reached-end', 'outro: done', ' exit$')

# ---- run 2: a dummy game paused in the Home menu ----
$r2 = "$Root\game"
& (Join-Path $PSScriptRoot 'make-fake-ps4.ps1') $r2 | Out-Null
$d2 = "$r2\data"; New-Item -ItemType Directory -Force $d2 | Out-Null
$s2 = "$Root\o2-game.txt"
Set-Content $s2 -Encoding utf8 @(
  'wait 2500', 'type Tester', 'key accept', 'wait 900', 'key accept', 'wait 2500', 'addchoice', 'wait 500', 'key accept',
  'wait 700', 'key accept', 'wait 800', 'key accept', 'wait 1200', 'expect games 6',
  'select Bloodborne', 'wait 500', 'key accept', 'waitshown 30', 'wait 3000', 'expect ingame Bloodborne',
  'homebutton', 'wait 800', 'expect screen HomeMenuCard', 'cardrow Home screen', 'key accept', 'wait 1200',
  'expect screen HomeScreen', 'expect paused yes',
  'closewin', 'wait 400', 'expect quitting yes', 'expect closing Bloodborne', 'expect outro no',
  'tap accept', 'tap back', 'closewin', 'shot o2-quit-closing',
  'waitoutro 20', 'expect ingame none', 'expect outro yes', 'log reached-end', 'shot o2-outro-game')
$env:IONEA_TESTGAME_SECS = '60'
$env:IONEA_TESTGAME_CLOSEMS = '2500'
try {
  $fails += Run-Launcher "--data `"$d2`" --no-import --dummies 5 --esde `"$r2\esde`" --emulators `"$r2\emus`" --windowed --mute --outro on --size 1040x620 --pos -30000,-30000 --script `"$s2`" --shots `"$Root\shots`"" $d2 120
} finally { Remove-Item Env:IONEA_TESTGAME_SECS, Env:IONEA_TESTGAME_CLOSEMS -ErrorAction SilentlyContinue }
$log2 = Get-Content "$d2\launcher.log"
$log2 | Where-Object { $_ -match 'SCRIPT|quit:|outro|closing|ended|exit|did not close' } | Out-Host
$fails += Check $log2 'game' @('quit: closing "Bloodborne" first \(paused\)', 'home menu: "Bloodborne" ended', 'outro follows', 'quit: "Bloodborne" closed after', 'outro: start', 'SCRIPT reached-end', 'outro: done', ' exit$')
if ($log2 -match 'did not close in|ending it') { 'game: the game was killed instead of closing'; $fails++ }
$e = At $log2 'home menu: "Bloodborne" ended'; $o = At $log2 'outro: start'
if ($e -and $o -and $o -lt $e) { 'game: the outro started before the game had ended'; $fails++ }
"o2: $fails failed"
