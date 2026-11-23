# x1: Discord status (Discord.cs) against a fake Discord: a named pipe server in a background job that speaks
# Discord's local IPC (handshake -> READY, SET_ACTIVITY -> answer) and writes every frame it gets to a file.
# The launcher only uses it through IONEA_DISCORD_PIPE, so the real Discord never sees a test game.
# Flow: home (just Ionea) -> Bloodborne on the fake shadPS4 (game | emulator, timer) -> paused -> resumed
# -> closed (home again) -> Settings > Profile > Discord status Off (status cleared).
# Ionea's icon stays the big picture; the game's cover address (coverurl) is the small one, game only.
# Windowed, muted, second monitor, no real input, never takes the front by itself.
# Usage: x1-discord.ps1 [-Exe <Launcher.exe>] [-Root <dir>]   (wipes <dir>)
param([string]$Exe = "$PSScriptRoot\..\bin\Release\net10.0-windows\Launcher.exe", [string]$Root = "$env:TEMP\Ionea-tests\discord")
$ErrorActionPreference = 'Stop'
$dot = [string][char]0x00B7   # the middle dot, kept out of this file (Windows PowerShell reads it as ANSI)
& (Join-Path $PSScriptRoot 'make-fake-ps4.ps1') $Root | Out-Null
$data = "$Root\data"; New-Item -ItemType Directory -Force $data | Out-Null
$frames = "$Root\discord-frames.txt"
$pipeName = 'ionea-test-discord-' + [guid]::NewGuid().ToString('N').Substring(0, 8)

# ---- the fake Discord ----
$server = Start-Job -ArgumentList $pipeName, $frames -ScriptBlock {
  param($name, $out)
  function ReadN($s, $n) { $b = New-Object byte[] $n; $o = 0; while ($o -lt $n) { $r = $s.Read($b, $o, $n - $o); if ($r -le 0) { return $null }; $o += $r }; $b }
  function Send($s, $op, $json) {
    $j = [Text.Encoding]::UTF8.GetBytes($json); $h = New-Object byte[] 8
    [BitConverter]::GetBytes([int]$op).CopyTo($h, 0); [BitConverter]::GetBytes([int]$j.Length).CopyTo($h, 4)
    $s.Write($h, 0, 8); $s.Write($j, 0, $j.Length); $s.Flush()
  }
  while ($true) {
    $p = New-Object System.IO.Pipes.NamedPipeServerStream($name, 'InOut', 1)
    $p.WaitForConnection()
    Add-Content $out 'CONNECT'
    while ($p.IsConnected) {
      $h = ReadN $p 8; if ($h -eq $null) { break }
      $op = [BitConverter]::ToInt32($h, 0); $len = [BitConverter]::ToInt32($h, 4)
      $b = ReadN $p $len; if ($b -eq $null) { break }
      $json = [Text.Encoding]::UTF8.GetString($b)
      Add-Content $out -Encoding utf8 "$op $json"
      if ($op -eq 0) { Send $p 1 '{"cmd":"DISPATCH","evt":"READY","data":{"v":1}}' }
      else { $nonce = ($json | ConvertFrom-Json).nonce; Send $p 1 ('{"cmd":"SET_ACTIVITY","data":{},"evt":null,"nonce":"' + $nonce + '"}') }
    }
    Add-Content $out 'DISCONNECT'
    $p.Dispose()
  }
}

$s = "$Root\x1.txt"
Set-Content $s -Encoding utf8 @(
  'wait 2500', 'type Tester', 'key accept', 'wait 900', 'key accept', 'wait 2500', 'addchoice', 'wait 500', 'key accept',
  'wait 700', 'key accept', 'wait 800', 'key accept', 'wait 1200', 'expect games 6',
  'expect discordon on', 'wait 4500', 'expect discord connected|home', 'expect discordpic none',
  'coverurl Bloodborne|https://thumbnails.libretro.com/test/bloodborne.png|libretro',
  'select Bloodborne', 'wait 500', 'key accept', 'waitshown 30', 'wait 5000', 'expect ingame Bloodborne',
  'expect discord connected|Bloodborne|shadPS4', 'expect discordpic https://thumbnails.libretro.com/test/bloodborne.png',
  'homebutton', 'wait 800', 'expect paused yes', 'wait 5000', "expect discord connected|Bloodborne|shadPS4 $dot Paused",
  'cardrow Resume', 'key accept', 'wait 800', 'expect paused no', 'wait 5000', 'expect discord connected|Bloodborne|shadPS4',
  'homebutton', 'wait 800', 'cardrow Close game', 'key accept', 'waitlaunch 20', 'expect ingame none', 'wait 5000',
  'expect discord connected|home', 'expect discordpic none',
  'settings Network', 'wait 600', 'key accept', 'wait 300', 'cardrow Discord status', 'key accept', 'wait 400', 'expect discordon off',
  'wait 5000', 'expect discord connected|off', 'key back', 'wait 400', 'key back', 'wait 400', 'quit')
$env:IONEA_TESTGAME_SECS = '60'
$env:IONEA_DISCORD_PIPE = $pipeName
$l = Start-Process $Exe -ArgumentList "--data `"$data`" --no-import --dummies 5 --esde `"$Root\esde`" --emulators `"$Root\emus`" --windowed --mute --size 1040x620 --pos -30000,-30000 --script `"$s`" --shots `"$Root\shots`"" -PassThru
Remove-Item Env:IONEA_DISCORD_PIPE, Env:IONEA_TESTGAME_SECS
$kids = @{}; $t0 = Get-Date
while (-not $l.HasExited -and ((Get-Date) - $t0).TotalSeconds -lt 150) {
  Get-CimInstance Win32_Process -Filter "ParentProcessId=$($l.Id) and Name<>'conhost.exe'" | ForEach-Object { $kids[$_.ProcessId] = $_.Name }
  Start-Sleep -Milliseconds 200
}
$fails = 0
if (-not $l.HasExited) { "launcher still running after 150 s (hung?); stopping PID $($l.Id)"; $l.Kill(); $fails++ }
foreach ($id in $kids.Keys) { Get-Process -Id $id -ErrorAction SilentlyContinue | ForEach-Object { "LEFTOVER $($kids[$id]) $id"; $_.Kill() } }
Start-Sleep -Milliseconds 500
Stop-Job $server; Remove-Job $server -Force

$log = Get-Content "$data\launcher.log"
$log | Where-Object { $_ -match 'SCRIPT (FAIL|done)|discord' } | Out-Host
if (($log -match 'SCRIPT done, 0 failed').Count -ne 1) { $fails++ }

# What the fake Discord got.
$got = if (Test-Path $frames) { Get-Content $frames -Encoding utf8 } else { @() }
$got | Out-Host
function Check($what, $ok) { if (-not $ok) { "FRAME CHECK FAILED: $what"; $script:fails++ } }
$acts = $got | Where-Object { $_ -like '1 *' } | ForEach-Object { ($_.Substring(2) | ConvertFrom-Json).args }
Check 'handshake with the Ionea app id' ([bool]($got | Where-Object { $_ -like '0 *"client_id":"1572029408242438235"*' }))
Check 'home: Ionea art, no game' ([bool]($acts | Where-Object { $_.activity -and $_.activity.assets.large_image -eq 'ionea' -and -not $_.activity.details }))
Check 'game: Bloodborne | shadPS4 with a start time' ([bool]($acts | Where-Object { $_.activity.details -eq 'Bloodborne' -and $_.activity.state -eq 'shadPS4' -and $_.activity.timestamps.start -gt 0 }))
Check 'game: Ionea big, the cover small with the game''s name' ([bool]($acts | Where-Object { $_.activity.details -eq 'Bloodborne' -and $_.activity.assets.large_image -eq 'ionea' -and $_.activity.assets.small_image -eq 'https://thumbnails.libretro.com/test/bloodborne.png' -and $_.activity.assets.small_text -eq 'Bloodborne' }))
Check 'home: no small picture' (-not ($acts | Where-Object { $_.activity -and -not $_.activity.details -and $_.activity.assets.small_image }))
Check 'paused line' ([bool]($acts | Where-Object { $_.activity.state -eq "shadPS4 $dot Paused" }))
Check 'off: activity cleared' ([bool]($acts | Where-Object { -not $_.PSObject.Properties['activity'] }))
Check 'disconnects when the launcher quits' ($got[-1] -eq 'DISCONNECT')
"x1: $fails failed"
