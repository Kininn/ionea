# Boot test: launcher (fullscreen, real data) starts one game, screenshots during boot, then the
# emulator is closed and we check the launcher comes back. Usage: boot-test.ps1 "<game name>" <emu process name> <tag>
# -Install: the installed launcher's folder (IONEA_INSTALL, else the installer's %LOCALAPPDATA%\Programs\Ionea).
param([string]$Game, [string]$Proc, [string]$Tag, [string]$Install)
if (-not $Install) { $Install = if ($env:IONEA_INSTALL) { $env:IONEA_INSTALL } else { "$env:LOCALAPPDATA\Programs\Ionea" } }
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type @"
using System; using System.Runtime.InteropServices; using System.Text;
public static class W { [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
 public static string Fg() { var h = GetForegroundWindow(); var s = new StringBuilder(256); GetWindowText(h, s, 256); uint p; GetWindowThreadProcessId(h, out p); return p + " '" + s + "'"; } }
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
$script = "$dir\$Tag.txt"
Set-Content $script -Encoding utf8 @("wait 3000", "expect screen StartScreen", "key accept", "wait 2000", "select $Game", "wait 800", "key accept", "waitlaunch 120", "wait 1500", "expect front yes", "quit")
$before = if (Test-Path "$data\launcher.log") { (Get-Content "$data\launcher.log").Count } else { 0 }
$l = Start-Process "$Install\Launcher.exe" -ArgumentList "--data `"$data`" --no-import --script `"$script`" --fullscreen --mute" -PassThru
$t0 = Get-Date; [System.Windows.Forms.SendKeys]::SendWait("{ESC}")
foreach ($s in 6, 9, 13, 20, 30) { while (((Get-Date) - $t0).TotalSeconds -lt $s) { Start-Sleep -Milliseconds 100 }; Shot "t$s"; "t=$s fg=$([W]::Fg())" }
$e = Get-Process $Proc -ErrorAction SilentlyContinue | Select-Object -First 1
if ($e) {
  "emulator pid $($e.Id) window '$($e.MainWindowTitle)' started $([math]::Round(($e.StartTime - $t0).TotalSeconds,1)) s after the launcher"
  $e.CloseMainWindow() | Out-Null
  if (-not $e.WaitForExit(8000)) { "emulator did not close on its own; killing"; $e.Kill() }
} else { "EMULATOR NOT RUNNING" }
Start-Sleep -Seconds 3; Shot "back"; "after close fg=$([W]::Fg()) launcher pid $($l.Id)"
if (-not $l.WaitForExit(20000)) { "launcher did not quit"; $l.Kill() }
Get-Content "$data\launcher.log" | Select-Object -Skip $before | Where-Object { $_ -match 'launch|ended|SCRIPT|error' }




