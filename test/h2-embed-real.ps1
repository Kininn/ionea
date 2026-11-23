# A real emulator game drawn inside the launcher (shadPS4 + Bloodborne, real launcher data, fullscreen).
# Checks: no console window and no window of the emulator's own ever shows (polled every 50 ms), the
# emulator's process is the launcher's child, Home pause (CPU near 0) / resume / Home screen / close work.
# The emulator is found as the launcher's child process (not by name: it runs as Launcher.exe, EmbedLogic.Alias).
# -Install: the installed launcher's folder (IONEA_INSTALL, else the installer's %LOCALAPPDATA%\Programs\Ionea);
# -Exe defaults to its Launcher.exe.
param([string]$Game = 'Bloodborne', [string]$Tag = 'embed', [string]$Install,
      [string]$Exe, [int]$BootWait = 25000)
if (-not $Install) { $Install = if ($env:IONEA_INSTALL) { $env:IONEA_INSTALL } else { "$env:LOCALAPPDATA\Programs\Ionea" } }
if (-not $Exe) { $Exe = "$Install\Launcher.exe" }
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type @"
using System; using System.Collections.Generic; using System.Runtime.InteropServices; using System.Text;
public static class W3 {
 public delegate bool EnumProc(IntPtr h, IntPtr l);
 [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc f, IntPtr l);
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
 [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
 [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
 [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int i);
 [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint c);
 // Is window a above window b in the z-order?
 public static bool Above(IntPtr a, IntPtr b) { for (IntPtr h = GetWindow(b, 3); h != IntPtr.Zero; h = GetWindow(h, 3)) if (h == a) return true; return false; }
 public static IntPtr Find(uint pid, string cls) { IntPtr r = IntPtr.Zero; EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); var c = new StringBuilder(256); GetClassName(h, c, 256);
   if (p == pid && c.ToString() == cls && IsWindowVisible(h)) { r = h; return false; } return true; }, IntPtr.Zero); return r; }
 [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte sc, uint f, UIntPtr x);
 // Visible top-level windows: "pid|class|title"
 public static List<string> Top() { var r = new List<string>(); EnumWindows((h, l) => { if (IsWindowVisible(h)) { uint p; GetWindowThreadProcessId(h, out p);
   var c = new StringBuilder(256); GetClassName(h, c, 256); var t = new StringBuilder(256); GetWindowText(h, t, 256); r.Add(p + "|" + c + "|" + t); } return true; }, IntPtr.Zero); return r; }
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
function Emu { if ($epid) { Get-Process -Id $epid -ErrorAction SilentlyContinue } }
function Cpu { $p = Emu; if ($p) { $p.TotalProcessorTime.TotalMilliseconds } else { -1 } }
$lines = @(
  'wait 3000', 'key accept', 'wait 2000', "select $Game", 'wait 800', 'key accept', 'waitlaunch 40', "wait $BootWait",
  "expect ingame $Game", 'expect paused no', 'log mark ingame', 'wait 3000', 'log mark keydown', 'wait 2500',
  '# 1: Home menu, then Resume',
  'homebutton', 'wait 800', 'expect paused yes', 'expect screen HomeMenuCard', 'expect front yes', 'log mark paused', 'wait 4000',
  'key accept', 'wait 2000', 'expect paused no', 'log mark resumed', 'wait 3000',
  '# 2: the real Home key',
  'log mark presshome', 'wait 3000', 'expect paused yes', 'expect screen HomeMenuCard',
  'key back', 'wait 2000', 'expect paused no', 'wait 2000',
  '# 3: Home screen keeps it paused, starting it again resumes it',
  'homebutton', 'wait 800', 'cardrow Home screen', 'key accept', 'wait 1500', 'expect screen HomeScreen', 'expect paused yes', 'log mark homescreen', 'wait 2500',
  "select $Game", 'wait 500', 'key accept', 'wait 2500', 'expect paused no', 'log mark back', 'wait 2500',
  '# 4: Close game',
  'homebutton', 'wait 800', 'cardrow Close game', 'key accept', 'waitlaunch 20', 'wait 2000', 'expect ingame none', 'expect front yes', 'log mark closed', 'wait 1500',
  'quit')
$script = "$dir\$Tag.txt"; Set-Content $script $lines -Encoding utf8
$log = "$data\launcher.log"; $before = (Get-Content $log).Count
$l = Start-Process $Exe -ArgumentList "--data `"$data`" --no-import --script `"$script`" --fullscreen --mute --shots `"$dir`"" -PassThru
$seen = @{}; $t0 = Get-Date; $bad = @{}; $epid = 0
$baseline = @{}; foreach ($x in [W3]::Top()) { $baseline[$x] = 1 }
while (-not $l.HasExited -and ((Get-Date) - $t0).TotalSeconds -lt 240) {
  Start-Sleep -Milliseconds 50
  if (-not $epid) { $e = Get-CimInstance Win32_Process -Filter "ParentProcessId=$($l.Id) and Name<>'conhost.exe'" -ErrorAction SilentlyContinue | Select-Object -First 1; if ($e) { $epid = [int]$e.ProcessId; "emulator pid $epid ($($e.Name), $($e.ExecutablePath)) is the launcher's child" } }
  # Any new visible top-level window that is not the launcher's: a console, or the emulator's own window.
  foreach ($x in [W3]::Top()) {
    if ($baseline[$x]) { continue }
    $p = [int]($x -split '\|')[0]
    if ($p -eq $l.Id) { continue }
    if (-not $bad[$x]) { $bad[$x] = [pscustomobject]@{ win = $x; first = ((Get-Date) - $t0).TotalSeconds; seen = 0; uncovered = 0 } }
    $bad[$x].seen++
    # Covered = the launcher's window is above it.
    $l.Refresh(); $lw = [IntPtr]$l.MainWindowHandle
    $gw = [W3]::Find([uint32]$p, ($x -split '\|')[1])
    if ($lw -eq [IntPtr]::Zero -or $gw -eq [IntPtr]::Zero -or -not [W3]::Above($lw, $gw)) { $bad[$x].uncovered++ }
  }
  $new = Get-Content $log | Select-Object -Skip $before
  foreach ($m in 'ingame', 'keydown', 'paused', 'resumed', 'presshome', 'homescreen', 'back', 'closed') {
    if ($seen[$m] -or -not ($new -match "SCRIPT mark $m")) { continue }
    $seen[$m] = 1
    switch ($m) {
      'ingame'    { Shot 'ingame'; "in game: front='$([W3]::Fg())'; emulator top-level windows: $(@([W3]::Top() | Where-Object { $_ -like "$epid|*" }).Count)" }
      'paused'    { Shot 'paused'; $a = Cpu; Start-Sleep 2; $b = Cpu; "paused: emulator CPU over 2 s = $([math]::Round($b - $a)) ms; front='$([W3]::Fg())'" }
      'resumed'   { Start-Sleep 1; $a = Cpu; Start-Sleep 1; $b = Cpu; Shot 'resumed'; "resumed: emulator CPU over 1 s = $([math]::Round($b - $a)) ms; front='$([W3]::Fg())'" }
      'presshome' { [W3]::keybd_event(0x24, 0x47, 1, [UIntPtr]::Zero); Start-Sleep -Milliseconds 60; [W3]::keybd_event(0x24, 0x47, 3, [UIntPtr]::Zero); Start-Sleep 1; Shot 'homekey'; "after Home key front='$([W3]::Fg())'" }
      'homescreen'{ Shot 'homescreen'; $a = Cpu; Start-Sleep 1; $b = Cpu; "home screen: emulator CPU over 1 s = $([math]::Round($b - $a)) ms" }
      'back'      { Shot 'back' }
      'keydown'   { [W3]::keybd_event(0x28, 0x50, 1, [UIntPtr]::Zero); Start-Sleep -Milliseconds 80; [W3]::keybd_event(0x28, 0x50, 3, [UIntPtr]::Zero); Start-Sleep 1; Shot 'keydown'; "pressed Down for the game" }
      'closed'    { Shot 'closed'; "closed: emulator running = $([bool](Emu))" }
    }
  }
}
if (-not $l.HasExited) { "launcher still running; stopping it"; $l.Kill() }
Emu | ForEach-Object { "LEFTOVER emulator pid $($_.Id)"; $_.Kill() }
"windows that showed on their own (not the launcher's):"
if ($bad.Count) { $bad.Values | ForEach-Object { "  $($_.win)  first at $([math]::Round($_.first,2)) s, seen $($_.seen) x, not covered by the launcher $($_.uncovered) x" } } else { '  none' }
Get-Content $log | Select-Object -Skip $before | Where-Object { $_ -match 'SCRIPT (ok|FAIL|done)|home menu|launch|ended|embed' }
