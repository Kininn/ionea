# st1: Streaming mode (Settings > Look > In games). Window capture of the launcher's own window (PrintWindow with
# PW_RENDERFULLCONTENT, as window capture in streaming and recording programs does) must show the game:
#  - a standalone emulator (stand-in: TestGame.exe as mGBA.exe) taken in as a child window, not an owned window over
#    the launcher; it is fitted again when the launcher's window is resized, its Home menu pauses and resumes it, and
#    it is let go and closed when the game is closed;
#  - a hosted core (the real mGBA core, -Core) drawn by WPF, not the Direct2D child window. The test program fills
#    the screen red (backdrop colour, display on, then a loop).
# Runs each with streaming mode off and on; with it off the result is only reported (the owned window is expected
# to be missing from the capture). Windowed, muted, no real input, off-screen; but WPF draws nothing in a window that
# is on no screen, so the hosted core's capture needs -OnScreen: its runs then show at the screen's top left, sent to
# the bottom of the window order and never activated (without it, the capture is only reported).
# Usage: st1-streaming.ps1 [-Exe <Launcher.exe>] [-Core <mgba_libretro.dll>] [-Root <dir>] [-OnScreen]   (wipes <dir>)
param([string]$Exe = "$PSScriptRoot\..\bin\Release\net10.0-windows\Launcher.exe", [string]$Core = $env:IONEA_TEST_MGBA_CORE,
      [string]$Root = "$env:TEMP\Ionea-tests\st1", [switch]$OnScreen)
$ErrorActionPreference = 'Stop'
$game = Join-Path $PSScriptRoot 'bin\TestGame.exe'
if (Test-Path $Root) { Remove-Item $Root -Recurse -Force }
New-Item -ItemType Directory -Force $Root | Out-Null

Add-Type -ReferencedAssemblies System.Drawing @'
using System; using System.Drawing; using System.Drawing.Imaging; using System.Runtime.InteropServices;
public static class St1
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr p, EnumProc f, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);

    // The process's biggest visible top-level window (the launcher's).
    public static IntPtr Launcher(int pid)
    {
        IntPtr best = IntPtr.Zero; long area = 0;
        EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); RECT r;
            if (p == pid && IsWindowVisible(h) && GetWindowRect(h, out r) && (long)(r.R - r.L) * (r.B - r.T) > area) { area = (long)(r.R - r.L) * (r.B - r.T); best = h; }
            return true; }, IntPtr.Zero);
        return best;
    }
    // A visible window of another process: a child of the launcher's window (mode "child") or owned by it ("owned").
    public static IntPtr Game(IntPtr launcher, int launcherPid, out string mode)
    {
        IntPtr child = IntPtr.Zero, owned = IntPtr.Zero;
        EnumChildWindows(launcher, (h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p != launcherPid && IsWindowVisible(h) && child == IntPtr.Zero) child = h; return true; }, IntPtr.Zero);
        EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p != launcherPid && IsWindowVisible(h) && GetWindow(h, 4 /* GW_OWNER */) == launcher) owned = h; return true; }, IntPtr.Zero);
        mode = child != IntPtr.Zero ? "child" : owned != IntPtr.Zero ? "owned" : "none";
        return child != IntPtr.Zero ? child : owned;
    }
    // What a pixel of the game looks like: TestGame's background, or the red test program's.
    static bool Match(string kind, Color c)
    {
        return kind == "red" ? c.R >= 150 && c.G <= 90 && c.B <= 90
            : c.B >= 36 && c.B <= 44 && c.G >= 26 && c.G <= 34 && c.R >= 26 && c.R <= 94;
    }
    // The window's client area as window capture sees it; returns the share of pixels that look like the game.
    public static double Capture(IntPtr h, string png, string kind)
    {
        RECT r; GetClientRect(h, out r);
        int w = r.R - r.L, ht = r.B - r.T;
        if (w <= 0 || ht <= 0) return -1;
        using (var bmp = new Bitmap(w, ht, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(bmp)) { IntPtr dc = g.GetHdc(); PrintWindow(h, dc, 1 | 2); g.ReleaseHdc(dc); }   // PW_CLIENTONLY | PW_RENDERFULLCONTENT
            bmp.Save(png, ImageFormat.Png);
            long hit = 0, all = 0;
            for (int y = 0; y < ht; y += 4) for (int x = 0; x < w; x += 4) { var c = bmp.GetPixel(x, y); all++; if (Match(kind, c)) hit++; }
            return all == 0 ? 0 : (double)hit / all;
        }
    }
}
'@

$fails = 0
function Check($ok, $what) { if ($ok) { Write-Host "ok   $what" } else { Write-Host "FAIL $what"; $script:fails++ } }
function Note($what) { Write-Host "     $what" }

# The launcher's log has the text (read while the launcher may be writing it).
function LogHas([string]$file, [string]$text) {
  try {
    $fs = [IO.File]::Open($file, 'Open', 'Read', 'ReadWrite, Delete')
    try { return (New-Object IO.StreamReader($fs)).ReadToEnd().Contains($text) } finally { $fs.Dispose() }
  } catch { return $false }
}

# Starts the launcher on a script; for each "log <MARK>" line the script reaches (SCRIPT <MARK> in its log) calls
# $hooks[<MARK>] with the launcher's process; waits for it to end. Returns the log.
function Run([string]$dir, [string[]]$lines, [hashtable]$hooks, [switch]$Shown) {
  $s = "$dir\script.txt"
  Set-Content $s -Encoding utf8 $lines
  $pos = if ($Shown) { '40,40' } else { '-30000,-30000' }
  $a = "--data `"$dir\data`" --no-import --emulators `"$dir\emus`" --windowed --mute --size 1040x620 --pos $pos --script `"$s`" --shots `"$dir\shots`""
  $l = Start-Process $Exe -ArgumentList $a -PassThru
  $kids = @{}; $t0 = Get-Date; $done = @{}
  while (-not $l.HasExited -and ((Get-Date) - $t0).TotalSeconds -lt 150) {
    Get-CimInstance Win32_Process -Filter "ParentProcessId=$($l.Id) and Name<>'conhost.exe'" | ForEach-Object { $kids[$_.ProcessId] = $_.Name }
    if ($Shown) { $h = [St1]::Launcher($l.Id); if ($h -ne [IntPtr]::Zero) { [void][St1]::SetWindowPos($h, [IntPtr]1, 0, 0, 0, 0, 0x13) } }   # HWND_BOTTOM; NOSIZE | NOMOVE | NOACTIVATE
    foreach ($k in @($hooks.Keys)) {
      if (-not $done[$k] -and (LogHas "$dir\data\launcher.log" "SCRIPT $k")) { $done[$k] = $true; & $hooks[$k] $l }
    }
    Start-Sleep -Milliseconds 200
  }
  if (-not $l.HasExited) { Check $false "launcher ended within 150 s (stopping PID $($l.Id))"; $l.Kill() }
  foreach ($id in $kids.Keys) { Get-Process -Id $id -ErrorAction SilentlyContinue | ForEach-Object { Check $false "no leftover $($kids[$id]) $id"; $_.Kill() } }
  foreach ($k in $hooks.Keys) { if (-not $done[$k]) { Check $false "the script reached $k" } }
  $log = Get-Content "$dir\data\launcher.log"
  $log | Select-String 'SCRIPT FAIL' | ForEach-Object { Write-Host "    $_" }
  Check (@($log | Select-String 'SCRIPT done, 0 failed').Count -eq 1) "the script ran clean ($dir)"
  return ,$log
}

# ---- a standalone emulator ----
foreach ($mode in 'off', 'on') {
  $dir = "$Root\emu-$mode"
  foreach ($d in 'data', 'emus', 'pick\mGBA', 'roms', 'shots') { New-Item -ItemType Directory -Force "$dir\$d" | Out-Null }
  Copy-Item $game "$dir\pick\mGBA\mGBA.exe"; Set-Content "$dir\pick\mGBA\portable.ini" ''
  Set-Content -LiteralPath "$dir\roms\Pocket Test (USA).gba" 'not a real game'
  $env:IONEA_TESTGAME_SECS = '40'
  $script:cap = $null; $script:fit = $null; $script:gm = $null; $script:g = [IntPtr]::Zero; $script:menuHidden = $null
  $log = Run $dir @(
    'wait 2500', 'type Tester', 'key accept', 'wait 900', 'key accept', 'wait 2500', "streaming $mode",
    'settings look and layout', 'key accept', 'cardrow Streaming mode', 'wait 600', 'shot st1-settings', 'key back 2', 'wait 800', 'expect screen HomeScreen',
    "addemu $dir\pick\mGBA\mGBA.exe", 'wait 800', 'expect emus 1', 'closebutton', 'wait 700', 'expect screen HomeScreen',
    "addrom $dir\roms\Pocket Test (USA).gba", 'wait 800', 'cardrow Add', 'key accept', 'wait 900', 'expect games 1',
    'select Pocket Test', 'wait 500', 'key accept', 'waitshown 30', 'wait 1500', 'expect embedded yes',
    "expect embedowned $(if ($mode -eq 'on') { 'no' } else { 'yes' })",
    'log CAPTURE', 'wait 5000',
    'homebutton', 'wait 900', 'expect screen HomeMenuCard', 'log MENU', 'wait 2500', 'shot st1-menu',
    'key back', 'wait 900', 'expect embedded yes',
    'homebutton', 'wait 900', 'cardrow Close game', 'key accept', 'waitlaunch 30', 'wait 800', 'expect ingame none',
    'quit') @{ CAPTURE = {
      param($l)
      Start-Sleep -Milliseconds 800
      $h = [St1]::Launcher($l.Id)
      $m = $null; $g = [St1]::Game($h, $l.Id, [ref]$m); $script:gm = $m; $script:g = $g
      $script:cap = [St1]::Capture($h, "$dir\capture.png", 'testgame')
      # Resize the launcher's window (no activation, still off-screen): the game must fill the new client area.
      [St1+RECT]$wr = New-Object St1+RECT; [void][St1]::GetWindowRect($h, [ref]$wr)
      [void][St1]::SetWindowPos($h, [IntPtr]::Zero, $wr.L, $wr.T, 900, 560, 0x0014)   # NOZORDER | NOACTIVATE
      Start-Sleep -Milliseconds 1200
      [St1+RECT]$cr = New-Object St1+RECT; [void][St1]::GetClientRect($h, [ref]$cr)
      [St1+RECT]$gr = New-Object St1+RECT; [void][St1]::GetWindowRect($g, [ref]$gr)
      $script:fit = "launcher client $($cr.R)x$($cr.B), game $($gr.R - $gr.L)x$($gr.B - $gr.T)"
      $script:fitOk = $g -ne [IntPtr]::Zero -and ($gr.R - $gr.L) -eq $cr.R -and ($gr.B - $gr.T) -eq $cr.B
      $script:capResized = [St1]::Capture($h, "$dir\capture-resized.png", 'testgame')
    }; MENU = {
      param($l)
      Start-Sleep -Milliseconds 800
      $script:menuHidden = $script:g -ne [IntPtr]::Zero -and [St1]::IsWindowVisible($script:g) -eq $false   # (a capture would show nothing new: WPF draws nothing off-screen)
    } }
  $env:IONEA_TESTGAME_SECS = $null
  $pct = if ($null -ne $script:cap) { '{0:P0}' -f $script:cap } else { 'none' }
  if ($mode -eq 'on') {
    Check ($script:gm -eq 'child') "streaming on: the emulator's window is a child of the launcher's ($script:gm)"
    Check ($script:cap -gt 0.5) "streaming on: the launcher's window capture shows the emulator ($pct of the picture)"
    Check $script:fitOk "streaming on: fitted again after the launcher's window was resized ($script:fit)"
    Check ($script:capResized -gt 0.5) ("streaming on: still captured after the resize ({0:P0})" -f $script:capResized)
    Check (@($log | Select-String 'is inside the launcher').Count -ge 1) 'streaming on: the log says "inside the launcher"'
    Check ($script:menuHidden -eq $true) "streaming on: the game's window is hidden while the Home menu is open"
  } else {
    Note "streaming off: the emulator's window is $script:gm; the launcher's window capture shows it in $pct of the picture (an owned window is expected to be missing)"
  }
}

# ---- a hosted core ----
if (-not $Core -or -not (Test-Path $Core)) { Note 'no -Core (mgba_libretro.dll): the hosted core part was not run' }
else {
  foreach ($mode in 'off', 'on') {
    $dir = "$Root\core-$mode"
    New-Item -ItemType Directory -Force "$dir\roms", "$dir\emus\Cores", "$dir\data", "$dir\shots" | Out-Null
    Copy-Item $Core "$dir\emus\Cores\mgba_libretro.dll"
    # A GBA cartridge header and: palette 0 (the backdrop) = red, display on with nothing in it, then a loop.
    $rom = New-Object byte[] 512
    [byte[]]$logo = 0x24,0xFF,0xAE,0x51,0x69,0x9A,0xA2,0x21,0x3D,0x84,0x82,0x0A,0x84,0xE4,0x09,0xAD,0x11,0x24,0x8B,0x98,0xC0,0x81,0x7F,0x21,0xA3,0x52,0xBE,0x19,0x93,0x09,0xCE,0x20,0x10,0x46,0x4A,0x4A,0xF8,0x27,0x31,0xEC,0x58,0xC7,0xE8,0x33,0x82,0xE3,0xCE,0xBF,0x85,0xF4,0xDF,0x94,0xCE,0x4B,0x09,0xC1,0x94,0x56,0x8A,0xC0,0x13,0x72,0xA7,0xFC,0x9F,0x84,0x4D,0x73,0xA3,0xCA,0x9A,0x61,0x58,0x97,0xA3,0x27,0xFC,0x03,0x98,0x76,0x23,0x1D,0xC7,0x61,0x03,0x04,0xAE,0x56,0xBF,0x38,0x84,0x00,0x40,0xA7,0x0E,0xFD,0xFF,0x52,0xFE,0x03,0x6F,0x95,0x30,0xF1,0x97,0xFB,0xC0,0x85,0x60,0xD6,0x80,0x25,0xA9,0x63,0xBE,0x03,0x01,0x4E,0x38,0xE2,0xF9,0xA2,0x34,0xFF,0xBB,0x3E,0x03,0x44,0x78,0x00,0x90,0xCB,0x88,0x11,0x3A,0x94,0x65,0xC0,0x7C,0x63,0x87,0xF0,0x3C,0xAF,0xD6,0x25,0xE4,0x8B,0x38,0x0A,0xAC,0x72,0x21,0xD4,0xF8,0x07
    [Array]::Copy($logo, 0, $rom, 4, $logo.Length)
    [byte[]]$b = 0x2E,0x00,0x00,0xEA; [Array]::Copy($b, 0, $rom, 0, 4)   # b 0xC0 (past the header)
    [byte[]]$code = 0x05,0x04,0xA0,0xE3,  0x1F,0x10,0xA0,0xE3,  0xB0,0x10,0xC0,0xE1,   # mov r0,#0x05000000; mov r1,#0x1F; strh r1,[r0]
                    0x01,0x23,0xA0,0xE3,  0x00,0x30,0xA0,0xE3,  0xB0,0x30,0xC2,0xE1,   # mov r2,#0x04000000; mov r3,#0; strh r3,[r2]
                    0xFE,0xFF,0xFF,0xEA                                                 # b .
    [Array]::Copy($code, 0, $rom, 0xC0, $code.Length)
    $t = [Text.Encoding]::ASCII.GetBytes('REDSCREEN   '); [Array]::Copy($t, 0, $rom, 0xA0, $t.Length)
    $rom[0xB2] = 0x96
    $c = 0; for ($i = 0xA0; $i -le 0xBC; $i++) { $c = ($c - $rom[$i]) -band 0xFF }; $rom[0xBD] = ($c - 0x19) -band 0xFF
    [IO.File]::WriteAllBytes("$dir\roms\Red Test.gba", $rom)
    $script:cap = $null
    $log = Run $dir @(
      'wait 2500', 'type Tester', 'key accept', 'wait 900', 'key accept', 'wait 1100', "streaming $mode",
      "addrom $dir\roms\Red Test.gba", 'wait 3000', 'expect screen AddRomCard', 'key accept', 'wait 2500',
      'select Red Test', 'key accept', 'waitshown 30', 'wait 3000', 'expect ingame Red Test',
      'log CAPTURE', 'wait 4000', 'streaming on', 'wait 1500',
      'homebutton', 'wait 1500', 'expect paused yes', 'homebutton', 'wait 1500', 'expect paused no',
      'closegame', 'wait 3000', 'expect ingame none', 'quit') -Shown:$OnScreen @{ CAPTURE = {
        param($l)
        Start-Sleep -Milliseconds 800
        $script:cap = [St1]::Capture([St1]::Launcher($l.Id), "$dir\capture.png", 'red')
      } }
    $pct = if ($null -ne $script:cap) { '{0:P0}' -f $script:cap } else { 'none' }
    $how = ($log | Select-String 'core picture: (straight to the screen|drawn by WPF.*)' | ForEach-Object { $_.Matches[0].Value }) -join '; '
    if ($mode -eq 'off') {
      Check (@($log | Select-String 'core picture: drawn by WPF \(streaming mode\)').Count -eq 1) 'streaming mode turned on mid-game: the picture moved to WPF at once'
    }
    if ($mode -eq 'on') {
      Check (@($log | Select-String 'core picture: Direct2D ready').Count -eq 0) 'streaming on: no Direct2D child window was made'
      Check (@($log | Select-String 'core picture: straight to the screen').Count -eq 0) 'streaming on: the picture never went straight to the screen'
      if ($OnScreen) { Check ($script:cap -gt 0.5) "streaming on: the launcher's window capture shows the core's picture ($pct of the picture red)" }
      else { Note "streaming on: the capture is not checked off-screen ($pct red; WPF draws nothing there): run with -OnScreen" }
    } else {
      Note "streaming off: $how; the launcher's window capture shows the core's picture in $pct of the picture"
    }
  }
}
Write-Host "st1: $fails failed (captures in $Root)"
exit $fails
