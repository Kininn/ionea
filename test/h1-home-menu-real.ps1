# Home menu test with a real emulator game (real launcher data, fullscreen). The script drives the
# launcher; this harness watches the log for "SCRIPT mark <x>" lines to press the real Home key,
# measure the emulator's CPU while paused, and take screenshots.
# -Install: the installed launcher's folder (IONEA_INSTALL, else the installer's %LOCALAPPDATA%\Programs\Ionea).
param([string]$Game = 'Pokemon - Platinum Version', [string]$Proc = 'melonDS', [string]$Tag = 'home', [string]$Install)
if (-not $Install) { $Install = if ($env:IONEA_INSTALL) { $env:IONEA_INSTALL } else { "$env:LOCALAPPDATA\Programs\Ionea" } }
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type @"
using System; using System.Runtime.InteropServices; using System.Text;
public static class W2 { [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
 [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte sc, uint f, UIntPtr x);
 public static string Fg() { var s = new StringBuilder(256); GetWindowText(GetForegroundWindow(), s, 256); return s.ToString(); } }
"@
$dir = Split-Path $MyInvocation.MyCommand.Path
$data = "$Install\data"
function Shot($n) {
  $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
  $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
  $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
  $small = New-Object System.Drawing.Bitmap $bmp, ([int]($b.Width / 2)), ([int]($b.Height / 2))
  $small.Save("$dir\$Tag-$n.png", [System.Drawing.Imaging.ImageFormat]::Png); $g.Dispose(); $bmp.Dispose(); $small.Dispose()
}
function Cpu { $p = Get-Process $Proc -ErrorAction SilentlyContinue | Select-Object -First 1; if ($p) { $p.TotalProcessorTime.TotalMilliseconds } else { -1 } }
$lines = @(
  'wait 3000', 'key accept', 'wait 2000', "select $Game", 'wait 800', 'key accept', 'waitshown 30', 'wait 6000',
  "expect ingame $Game", 'expect paused no',
  '# 1: Home menu from the script, then Resume',
  'homebutton', 'wait 600', 'expect paused yes', 'expect screen HomeMenuCard', 'expect front yes', 'shot launcher-h-menu', 'log mark paused', 'wait 4000',
  'key accept', 'wait 1500', 'expect paused no', 'log mark resumed', 'wait 3000',
  '# 2: the real Home key (pressed by the harness) opens it while the game is in front',
  'log mark presshome', 'wait 3000', 'expect paused yes', 'expect screen HomeMenuCard',
  '# B resumes',
  'key back', 'wait 1500', 'expect paused no', 'wait 2000',
  '# 3: Home screen keeps the game paused, starting it again resumes',
  'homebutton', 'wait 600', 'cardrow Home screen', 'key accept', 'wait 1200', 'expect screen HomeScreen', 'expect paused yes', 'log mark homescreen', 'wait 2500',
  "select $Game", 'wait 500', 'key accept', 'wait 1500', 'expect paused no', 'expect launch shown', 'wait 2500',
  '# 4: Close game',
  'homebutton', 'wait 600', 'cardrow Close game', 'key accept', 'waitlaunch 15', 'wait 1500', 'expect ingame none', 'expect front yes', 'log mark closed', 'wait 1500',
  'quit')
$script = "$dir\$Tag.txt"; Set-Content $script $lines -Encoding utf8
$log = "$data\launcher.log"; $before = (Get-Content $log).Count
$l = Start-Process "$Install\Launcher.exe" -ArgumentList "--data `"$data`" --no-import --script `"$script`" --fullscreen --mute --shots `"$dir`"" -PassThru
$seen = @{}; $t0 = Get-Date
while (-not $l.HasExited -and ((Get-Date) - $t0).TotalSeconds -lt 150) {
  Start-Sleep -Milliseconds 200
  $new = Get-Content $log | Select-Object -Skip $before
  foreach ($m in 'paused', 'resumed', 'presshome', 'homescreen', 'closed') {
    if ($seen[$m] -or -not ($new -match "SCRIPT mark $m")) { continue }
    $seen[$m] = 1
    switch ($m) {
      'paused'    { Shot 'paused'; $a = Cpu; Start-Sleep 2; $b = Cpu; "paused: emulator CPU over 2 s = $([math]::Round($b - $a)) ms; front='$([W2]::Fg())'" }
      'resumed'   { Start-Sleep 1; $a = Cpu; Start-Sleep 1; $b = Cpu; Shot 'resumed'; "resumed: emulator CPU over 1 s = $([math]::Round($b - $a)) ms; front='$([W2]::Fg())'" }
      'presshome' { "before Home key front='$([W2]::Fg())'"; [W2]::keybd_event(0x24, 0x47, 1, [UIntPtr]::Zero); Start-Sleep -Milliseconds 60; [W2]::keybd_event(0x24, 0x47, 3, [UIntPtr]::Zero); Start-Sleep 1; Shot 'homekey'; "after Home key front='$([W2]::Fg())'" }
      'homescreen'{ Shot 'homescreen'; $a = Cpu; Start-Sleep 1; $b = Cpu; "home screen: emulator CPU over 1 s = $([math]::Round($b - $a)) ms" }
      'closed'    { Shot 'closed'; "closed: emulator running = $([bool](Get-Process $Proc -ErrorAction SilentlyContinue))" }
    }
  }
}
if (-not $l.HasExited) { "launcher still running; stopping it"; $l.Kill() }
Get-Process $Proc -ErrorAction SilentlyContinue | ForEach-Object { "LEFTOVER $Proc pid $($_.Id)"; $_.Kill() }
Get-Content $log | Select-Object -Skip $before | Where-Object { $_ -match 'SCRIPT (ok|FAIL|done)|home menu|launch|ended' }
