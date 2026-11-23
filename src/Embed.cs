#nullable disable
// A standalone emulator drawn inside the launcher. Its game window loses its border and sits exactly over
// the launcher's window as a window the launcher owns (before, it was a child window). So
// there is one taskbar button, and the game window stays above the launcher. It is not a child because SDL
// only captures and hides the mouse (mouse look, shadPS4's mouse camera) for the window in front, and a
// child window is never the window in front. EmbedLogic.Owned picks the mode. Task
// Manager groups by exe file name, so the emulator is started through a hard link named like the
// launcher (EmbedLogic.Alias) to show inside the launcher's group.
// Child windows always draw over the launcher's own picture, so the launcher hides the game window
// while its Home menu is open and shows a still of the last frame in its place.
// The emulator is started windowed, with no console window, and with SDL's controller input left on
// while its window is a child (SDL otherwise ignores pads when its window isn't the foreground one).
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Ionea;

public static class EmbedLogic
{
    // Emulators whose game window is taken into the launcher: every standalone emulator (catalog or added by
    // hand), unless its "Play inside Ionea" row is off. Cores already run inside; Windows apps are not emulators.
    public static bool CanEmbed(Emulator e) => e != null && !e.IsCore && e.Exe != null && e.Inside != false;

    // Owned window over the launcher (true) or child window inside it (false, the older way). Streaming mode
    // (Settings > Look > In games) takes the child window: window capture of the launcher (Discord, OBS) only
    // sees its own window and its children, never a separate window over it. A child can't capture the mouse.
    public static bool Owned(Emulator e, bool streaming) => CanEmbed(e) && !streaming;

    // Start it windowed: the launcher is the full-screen window. Only "-f true" style switches (SDL emulators
    // such as shadPS4) are turned off; a Qt emulator's bare "-f" stays, since it hides its menu and status bar.
    public static string Windowed(Emulator e, string args) =>
        CanEmbed(e) ? Regex.Replace(args, @"(?<=(^|\s)(-f|--fullscreen)\s+)true\b", "false") : args;

    // Puts an emulator in a job owned by the launcher: if the launcher ends in any way (even a crash), the
    // emulator ends with it, and it never runs on as a stray process. Returns the job (close it after the
    // emulator has exited), or zero.
    public static IntPtr Hold(Process p)
    {
        try
        {
            IntPtr job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return IntPtr.Zero;
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = 0x2000;   // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            int size = Marshal.SizeOf(info);
            IntPtr mem = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, mem, false);
                if (!SetInformationJobObject(job, 9 /* ExtendedLimitInformation */, mem, (uint)size) || !AssignProcessToJobObject(job, p.Handle))
                {
                    Log.Write($"embed: could not put the emulator in the launcher's job (error {Marshal.GetLastWin32Error()})");
                    CloseHandle(job);
                    return IntPtr.Zero;
                }
            }
            finally { Marshal.FreeHGlobal(mem); }
            return job;
        }
        catch (Exception x) { Log.Write("embed job: " + x.Message); return IntPtr.Zero; }
    }

    public static void Release(IntPtr job) { if (job != IntPtr.Zero) CloseHandle(job); }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS { public ulong a, b, c, d, e, f; }
    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr attr, string name);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetInformationJobObject(IntPtr job, int cls, IntPtr info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr proc);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    // Task Manager groups its Processes list by exe FILE NAME (not by parent process, window or job), so an
    // emulator only shows inside the launcher's row when its exe is named like the launcher. For embedded
    // emulators this returns a hard link named like the launcher ("Launcher.exe") next to the emulator's own
    // exe (same file, no extra space; its folder, so portable `user\` and DLLs are found the same way).
    // An updated emulator exe is a new file: the link is then made again. Any failure: the emulator's own exe.
    // Settings > Library > "Group in Task Manager" (Settings.TaskManagerGroup, off by default; set by MainWindow).
    // Off: every program starts from its own exe, no link is made, and the links Ionea made are removed (RemoveLinks).
    // Off by default because GPU driver profiles, Windows graphics settings and firewall rules go by exe name and path.
    public static bool Group;

    // Removes the links in made-links.txt that are still a second name of some file (never a program's only copy).
    // A link in use (the emulator is running) stays listed and goes next time.
    public static void RemoveLinks() => MadeLinks.RemoveAll(l => Links(l) > 1);

    static uint Links(string p)
    {
        using var h = CreateFile(p, 0x80, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
        return !h.IsInvalid && GetFileInformationByHandle(h, out var i) ? i.NumberOfLinks : 0;
    }

    public static string Alias(Emulator e)
    {
        string exe = e?.Exe;
        if (!CanEmbed(e) || exe == null) return exe;
        return AliasFile(exe);
    }

    // The same for any program file: a port's game program that its starter runs (Ports.GameLink, bb-probe.exe).
    public static string AliasFile(string exe)
    {
        if (!Group || exe == null || !File.Exists(exe)) return exe;
        string name = Path.GetFileName(Environment.ProcessPath ?? "Launcher.exe");
        if (string.Equals(Path.GetFileName(exe), name, StringComparison.OrdinalIgnoreCase)) return exe;
        string link = Path.Combine(Path.GetDirectoryName(exe), name);
        try
        {
            if (File.Exists(link))
            {
                if (SameFile(link, exe)) { MadeLinks.Add(link); return link; }   // also notes links from before the list
                // Only a link Ionea made itself is replaced (the emulator was updated). Any other file with
                // this name belongs to the program: it is left alone and the program's own exe is started.
                if (!MadeLinks.Contains(link))
                {
                    Log.Write($"embed: {link} is not a link Ionea made; starting {Path.GetFileName(exe)}");
                    return exe;
                }
                File.Delete(link);
                Log.Write($"embed: {link} was not {Path.GetFileName(exe)} any more; making the link again");
            }
            if (!CreateHardLink(link, exe, IntPtr.Zero))
            {
                Log.Write($"embed: could not link {link} to {exe} (error {Marshal.GetLastWin32Error()}); starting {Path.GetFileName(exe)}");
                return exe;
            }
            MadeLinks.Add(link);
            Log.Write($"embed: linked {link} to {exe}");
            return link;
        }
        catch (Exception x) { Log.Write($"embed: link {link}: {x.Message}; starting {Path.GetFileName(exe)}"); return exe; }
    }

    // The links Ionea has made (full paths, one per line in <data>\made-links.txt). Without the list no file is replaced.
    public static class MadeLinks
    {
        static readonly object Gate = new();
        static string File_ => Tweaks.DataDir == null ? null : Path.Combine(Tweaks.DataDir, "made-links.txt");

        public static bool Contains(string link)
        {
            lock (Gate)
            {
                try
                {
                    string f = File_;
                    return f != null && File.Exists(f) && File.ReadAllLines(f).Any(l => string.Equals(l.Trim(), Path.GetFullPath(link), StringComparison.OrdinalIgnoreCase));
                }
                catch { return false; }
            }
        }

        public static void RemoveAll(Func<string, bool> safe)
        {
            lock (Gate)
            {
                try
                {
                    string f = File_;
                    if (f == null || !File.Exists(f)) return;
                    var keep = new System.Collections.Generic.List<string>();
                    foreach (var l in File.ReadAllLines(f).Select(l => l.Trim()).Where(l => l.Length > 0))
                    {
                        if (!File.Exists(l)) continue;
                        if (!safe(l)) { Log.Write($"embed: {l} is not a second name of a file any more; left alone"); continue; }
                        try { File.Delete(l); Log.Write($"embed: removed the link {l}"); }
                        catch (Exception x) { keep.Add(l); Log.Write($"embed: could not remove {l} yet ({x.Message})"); }
                    }
                    if (keep.Count == 0) File.Delete(f); else File.WriteAllLines(f, keep);
                }
                catch (Exception x) { Log.Write("embed: could not remove links: " + x.Message); }
            }
        }

        public static void Add(string link)
        {
            lock (Gate)
            {
                try
                {
                    string f = File_;
                    if (f == null || Contains(link)) return;
                    File.AppendAllLines(f, new[] { Path.GetFullPath(link) });
                }
                catch (Exception x) { Log.Write("embed: could not note the link: " + x.Message); }
            }
        }
    }

    // Two paths name the same file (hard links): same volume and file index.
    static bool SameFile(string a, string b)
    {
        static (uint, ulong)? Id(string p)
        {
            using var h = CreateFile(p, 0x80 /* FILE_READ_ATTRIBUTES */, 7 /* share all */, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);
            if (h.IsInvalid || !GetFileInformationByHandle(h, out var i)) return null;
            return (i.VolumeSerialNumber, ((ulong)i.FileIndexHigh << 32) | i.FileIndexLow);
        }
        var x = Id(a); var y = Id(b);
        return x != null && x == y;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool CreateHardLink(string link, string existing, IntPtr sa);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle h, out BY_HANDLE_FILE_INFORMATION info);

    public static void Prepare(Emulator e, ProcessStartInfo si)
    {
        si.CreateNoWindow = true;   // console emulators (shadPS4) get no console window; GUI ones are unaffected
        if (!CanEmbed(e)) return;
        si.Environment["SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS"] = "1";
    }
}

public sealed class Embedded
{
    public readonly IntPtr Child;
    public readonly bool Owned;   // a top-level window owned by the launcher, over it (else a child window)
    readonly IntPtr parent;
    public bool Shown { get; private set; }

    Embedded(IntPtr parent, IntPtr child, bool owned) { this.parent = parent; Child = child; Owned = owned; }

    public bool Alive => IsWindow(Child);

    // The process's game window: an SDL game window, even before it is shown (so it can be taken in
    // before it ever appears on its own), else its biggest visible top-level window (not a tool window).
    public static IntPtr FindWindow(int pid) => FindWindow(p => p == pid);

    // The same over several processes (a port's starter and the program it runs: ProcTree.Named).
    public static IntPtr FindWindow(Func<uint, bool> ofGame)
    {
        IntPtr best = IntPtr.Zero, sdl = IntPtr.Zero; long area = 0;
        var cls = new System.Text.StringBuilder(64);
        Native.EnumWindows((h, l) =>
        {
            Native.GetWindowThreadProcessId(h, out uint p);
            if (!ofGame(p) || (Native.GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return true;
            cls.Clear(); GetClassName(h, cls, 64);
            if (cls.ToString() == "SDL_app") { sdl = h; return false; }
            if (!Native.IsWindowVisible(h) || !Native.GetWindowRect(h, out var r)) return true;
            long a = (long)(r.R - r.L) * (r.B - r.T);
            if (r.R - r.L >= 200 && r.B - r.T >= 150 && a > area) { area = a; best = h; }
            return true;
        }, IntPtr.Zero);
        return sdl != IntPtr.Zero ? sdl : best;
    }

    // Still borderless and the launcher's (owned or child)? (A program may reset its window's style while it starts up.)
    public bool Intact => Owned
        ? GetWindow(Child, GW_OWNER) == parent && (GetWindowLongPtr(Child, GWL_STYLE).ToInt64() & (WS_CHILD | WS_POPUP | WS_CAPTION | WS_THICKFRAME)) == WS_POPUP
        : GetParent(Child) == parent && (GetWindowLongPtr(Child, GWL_STYLE).ToInt64() & (WS_CHILD | WS_CAPTION | WS_THICKFRAME)) == WS_CHILD;

    public static Embedded Take(IntPtr parent, IntPtr child, bool owned)
    {
        if (owned) return TakeOwned(parent, child);
        // If the game window has the foreground, the launcher takes it first: a foreground window that
        // becomes a child would otherwise hand the foreground to some other program's window.
        IntPtr fg = Native.GetForegroundWindow();
        bool ours = fg == child || fg == parent;
        if (fg == child) Native.ForceForeground(parent);
        // The parent clips its children, so its own drawing never covers the game.
        SetWindowLongPtr(parent, GWL_STYLE, (IntPtr)(GetWindowLongPtr(parent, GWL_STYLE).ToInt64() | WS_CLIPCHILDREN));
        long st = GetWindowLongPtr(child, GWL_STYLE).ToInt64();
        st &= ~(WS_POPUP | WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_MAXIMIZE | WS_MINIMIZE);
        st |= WS_CHILD | WS_CLIPSIBLINGS;
        long ex = GetWindowLongPtr(child, GWL_EXSTYLE).ToInt64();
        Log.Write($"embed: taking window 0x{child.ToInt64():X} (style 0x{st:X}, ex 0x{ex:X}, visible {Native.IsWindowVisible(child)}; launcher ex 0x{GetWindowLongPtr(parent, GWL_EXSTYLE).ToInt64():X})");
        ex &= ~(WS_EX_APPWINDOW | WS_EX_WINDOWEDGE | WS_EX_CLIENTEDGE | WS_EX_DLGMODALFRAME | WS_EX_STATICEDGE | WS_EX_TOPMOST);
        ex |= WS_EX_NOPARENTNOTIFY;   // its end must not wait on the launcher
        SetWindowLongPtr(child, GWL_STYLE, (IntPtr)st);
        SetWindowLongPtr(child, GWL_EXSTYLE, (IntPtr)ex);
        if (GetParent(child) != parent && SetParent(child, parent) == IntPtr.Zero && Marshal.GetLastWin32Error() != 0) return null;
        var e = new Embedded(parent, child, false);
        e.Fit(true);
        e.Shown = true;
        if (ours && Native.GetForegroundWindow() != parent) Native.ForceForeground(parent);
        return e;
    }

    // The game window as a borderless window owned by the launcher, over its client area. Owned windows
    // stay above their owner, have no taskbar button of their own (no WS_EX_APPWINDOW), and minimize and
    // come back with it. It can be the window in front, so SDL gives it the keyboard focus and can
    // capture the mouse.
    static Embedded TakeOwned(IntPtr parent, IntPtr child)
    {
        long st0 = GetWindowLongPtr(child, GWL_STYLE).ToInt64(), st = st0;
        st &= ~(WS_CHILD | WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_MAXIMIZE | WS_MINIMIZE);
        st |= WS_POPUP | WS_CLIPSIBLINGS;
        long ex = GetWindowLongPtr(child, GWL_EXSTYLE).ToInt64();
        Log.Write($"embed: taking window 0x{child.ToInt64():X} over the launcher (style 0x{st:X}, ex 0x{ex:X}, visible {Native.IsWindowVisible(child)})");
        ex &= ~(WS_EX_APPWINDOW | WS_EX_WINDOWEDGE | WS_EX_CLIENTEDGE | WS_EX_DLGMODALFRAME | WS_EX_STATICEDGE | WS_EX_TOPMOST);
        SetWindowLongPtr(child, GWL_STYLE, (IntPtr)st);
        SetWindowLongPtr(child, GWL_EXSTYLE, (IntPtr)ex);
        if ((st0 & WS_CHILD) != 0 && SetParent(child, IntPtr.Zero) == IntPtr.Zero && Marshal.GetLastWin32Error() != 0) return null;
        if (GetWindow(child, GW_OWNER) != parent) SetWindowLongPtr(child, GWLP_HWNDPARENT, parent);   // the owner
        if (GetWindow(child, GW_OWNER) != parent) { Log.Write("embed: could not make the launcher the game window's owner"); return null; }
        var e = new Embedded(parent, child, true);
        e.Fit(true);
        e.Shown = true;
        return e;
    }

    // The launcher over every normal window (or not), set on its window directly. Returns whether it is now topmost.
    public static bool Cover(IntPtr parent, bool on)
    {
        SetWindowPos(parent, on ? new IntPtr(-1) : new IntPtr(-2), 0, 0, 0, 0, 0x0001 | 0x0002 | SWP_NOACTIVATE);   // NOSIZE | NOMOVE
        return (GetWindowLongPtr(parent, GWL_EXSTYLE).ToInt64() & WS_EX_TOPMOST) != 0;
    }

    // Fill the launcher's window. Asynchronous, so a frozen (paused) game can never hold the launcher up.
    public void Fit(bool show = false)
    {
        if (Owned && Native.IsIconic(parent)) return;   // minimized: it went with the launcher
        GetClientRect(parent, out var r);
        var pt = new POINT();
        if (Owned) ClientToScreen(parent, ref pt);       // owned: screen place; a child: inside the launcher
        uint f = SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS | SWP_FRAMECHANGED | (show ? SWP_SHOWWINDOW : 0);
        SetWindowPos(Child, IntPtr.Zero, pt.X, pt.Y, r.R - r.L, r.B - r.T, f);
    }

    // Still exactly over the launcher's window? A Qt emulator's "-f" goes full screen on its monitor a moment after it
    // was taken in (mGBA in a windowed Ionea filled the monitor, and the Home menu's still and
    // cover came out that size). True when there is nothing to fit (a child, hidden, the launcher minimized).
    public bool Fits
    {
        get
        {
            if (!Owned || !Shown || Native.IsIconic(parent)) return true;
            GetClientRect(parent, out var r);
            var pt = new POINT();
            ClientToScreen(parent, ref pt);
            if (!Native.GetWindowRect(Child, out var c)) return true;
            return c.L == pt.X && c.T == pt.Y && c.R - c.L == r.R - r.L && c.B - c.T == r.B - r.T;
        }
    }

    // The Home menu's picture copy, made ready now so the menu opens without waiting for it (DisplayCopy).
    public void WarmCopy()
    {
        GetClientRect(parent, out var r);
        var pt = new POINT();
        ClientToScreen(parent, ref pt);
        DisplayCopy.Warm(pt.X, pt.Y, r.R - r.L, r.B - r.T);
    }

    // Hide/show: only while the game runs (not suspended), because these wait for the game to answer.
    public void Hide() { if (!Shown) return; Native.ShowWindow(Child, 0); Shown = false; }
    public void Show() { if (Shown) return; Fit(); Native.ShowWindow(Child, 5); Shown = true; Focus(); }

    // Owned only: Windows keeps owned windows stacked above their owner, so activating, re-stacking or
    // minimizing the launcher sends messages to the game window and waits for an answer. A frozen (paused)
    // game never answers, and the launcher hung (Alt+Tab during the Home menu). So the owner link
    // is dropped before the game is frozen and put back after it runs again. Call both only while it runs.
    public void Unown() { if (Owned && Alive) SetWindowLongPtr(Child, GWLP_HWNDPARENT, IntPtr.Zero); }
    public void Reown() { if (Owned && Alive && GetWindow(Child, GW_OWNER) != parent) SetWindowLongPtr(Child, GWLP_HWNDPARENT, parent); }

    // Keyboard focus to the game. Owned: the game window comes in front (the launcher is in front when this
    // is called, so Windows allows it). A child: parent and child share input state, so SetFocus works across processes.
    public void Focus()
    {
        if (!Shown || !Alive) return;
        if (!Owned) { SetFocus(Child); return; }
        if (Native.GetForegroundWindow() != Child && !Native.SetForegroundWindow(Child)) Log.Write("embed: Windows did not bring the game window in front");
    }

    // Ask the game to close, the way its close button would. It is taken out of the launcher first, so
    // closing never waits on the launcher's window.
    public void Close(bool detach)
    {
        if (!Alive) return;
        if (detach) { Hide(); if (Owned) SetWindowLongPtr(Child, GWLP_HWNDPARENT, IntPtr.Zero); else SetParent(Child, IntPtr.Zero); }
        PostMessage(Child, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
    }

    // What the game shows right now, for the paused backdrop. First what the display shows (DXGI desktop
    // duplication, as screen recorders do): a game that presents straight to the display (Vulkan / flip model
    // filling the screen: bbport, Lichii) isn't in the screen copy, which then holds an old frame (the
    // library flashed before the Home menu). The screen copy is the fallback.
    public BitmapSource Snapshot()
    {
        try
        {
            GetClientRect(Child, out var r);
            int wd = r.R - r.L, ht = r.B - r.T;
            if (wd < 16 || ht < 16) return null;
            var pt = new POINT();
            ClientToScreen(Child, ref pt);
            var dup = DisplayCopy.Grab(pt.X, pt.Y, wd, ht, out string why);
            if (dup != null) return dup;
            Log.Write("embed snapshot: display copy failed (" + why + "), screen copy instead");
            IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen), bmp = CreateCompatibleBitmap(screen, wd, ht);
            try
            {
                IntPtr old = SelectObject(mem, bmp);
                BitBlt(mem, 0, 0, wd, ht, screen, pt.X, pt.Y, 0x00CC0020 | 0x40000000 /* SRCCOPY | CAPTUREBLT */);
                SelectObject(mem, old);
                var src = Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            finally { DeleteObject(bmp); DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen); }
        }
        catch (Exception e) { Log.Write("embed snapshot: " + e.Message); return null; }
    }

    // Has the game drawn yet? A window that hasn't presented a picture shows what is under it (the launch
    // screen). Reads the screen at seven places the launch screen leaves plain (not the cover, its name or the
    // ring): drawn once two of them differ from the launch screen's background.
    public bool Drawn(System.Windows.Media.Color bg)
    {
        if (!Native.IsWindowVisible(Child) || !Native.GetWindowRect(Child, out var r) || r.R - r.L < 64 || r.B - r.T < 64) return false;
        IntPtr dc = GetDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) return false;
        try
        {
            int differ = 0, read = 0;
            foreach (var (fx, fy) in new[] { (0.08, 0.08), (0.5, 0.08), (0.92, 0.08), (0.08, 0.5), (0.92, 0.45), (0.08, 0.92), (0.5, 0.95) })
            {
                uint c = GetPixel(dc, r.L + (int)((r.R - r.L) * fx), r.T + (int)((r.B - r.T) * fy));
                if (c == 0xFFFFFFFF) continue;   // CLR_INVALID: off every screen
                read++;
                int R = (int)(c & 0xFF), G = (int)((c >> 8) & 0xFF), B = (int)((c >> 16) & 0xFF);
                if (Math.Abs(R - bg.R) > 6 || Math.Abs(G - bg.G) > 6 || Math.Abs(B - bg.B) > 6) differ++;
            }
            return differ >= 2 || read == 0;   // nothing readable (off screen, a test run): don't keep the launch screen up
        }
        finally { ReleaseDC(IntPtr.Zero, dc); }
    }
    [DllImport("gdi32.dll")] static extern uint GetPixel(IntPtr dc, int x, int y);

    const int GWL_STYLE = -16, GWL_EXSTYLE = -20, GWLP_HWNDPARENT = -8;
    const uint GW_OWNER = 4;
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    const long WS_POPUP = 0x80000000, WS_CHILD = 0x40000000, WS_MINIMIZE = 0x20000000, WS_CLIPSIBLINGS = 0x04000000, WS_CLIPCHILDREN = 0x02000000,
        WS_MAXIMIZE = 0x01000000, WS_CAPTION = 0x00C00000, WS_SYSMENU = 0x00080000, WS_THICKFRAME = 0x00040000,
        WS_MINIMIZEBOX = 0x00020000, WS_MAXIMIZEBOX = 0x00010000;
    const long WS_EX_DLGMODALFRAME = 0x1, WS_EX_NOPARENTNOTIFY = 0x4, WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_WINDOWEDGE = 0x100,
        WS_EX_CLIENTEDGE = 0x200, WS_EX_STATICEDGE = 0x20000, WS_EX_APPWINDOW = 0x40000;
    const uint SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20, SWP_SHOWWINDOW = 0x40, SWP_ASYNCWINDOWPOS = 0x4000;

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out Native.RECT r);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] static extern IntPtr SetFocus(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, int msg, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint op);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
}

// Watches an emulator for its game window on its own thread (every 5 ms until the first one), takes it
// into the launcher at once, and takes it in again if the emulator remakes it or resets its style.
public sealed class EmbedWatch
{
    volatile bool stopped;
    public void Stop() => stopped = true;

    // gameProgram: a port whose starter runs the game as another program (bbport-run.exe -> bb-probe.exe):
    // only that program's windows are the game's, and it may take minutes to appear (the port prepares first).
    public static EmbedWatch Start(Process p, IntPtr parent, bool owned, Func<bool> paused, Action<Embedded, double> taken, string gameProgram = null)
    {
        var wch = new EmbedWatch();
        new System.Threading.Thread(() =>
        {
            var t0 = DateTime.Now; Embedded cur = null; int refits = 0; DateTime fitted = DateTime.MinValue;
            double limit = gameProgram == null ? 90 : 600;
            DateTime treeAt = DateTime.MinValue; var pids = new System.Collections.Generic.HashSet<uint>();
            try
            {
                while (!wch.stopped)
                {
                    bool exited; try { exited = p.HasExited; } catch (Exception x) { Log.Write("embed watch: cannot see the started program: " + x.Message); exited = true; }
                    if (exited) { if (cur == null) Log.Write("embed watch: the started program ended before a game window came"); return; }
                    if (paused()) { System.Threading.Thread.Sleep(200); continue; }
                    if (cur != null && cur.Alive && cur.Intact)
                    {
                        // The game moved or sized its own window (a Qt "-f" going full screen): back over the launcher.
                        // (Fit is asynchronous: not looked at again until it has had time.)
                        if ((DateTime.Now - fitted).TotalMilliseconds >= 500 && !cur.Fits)
                        { if (++refits <= 3) Log.Write($"embed: the game sized its own window; fitted again ({refits})"); cur.Fit(); fitted = DateTime.Now; }
                        System.Threading.Thread.Sleep(200); continue;
                    }
                    if (gameProgram != null && (DateTime.Now - treeAt).TotalMilliseconds >= 100)
                    {
                        int before = pids.Count; pids = ProcTree.Named(p.Id, gameProgram); treeAt = DateTime.Now;
                        if (pids.Count != before) Log.Write($"embed watch: {pids.Count} {gameProgram} under pid {p.Id}");
                    }
                    IntPtr h = cur != null && cur.Alive ? cur.Child
                        : gameProgram == null ? Embedded.FindWindow(p.Id) : pids.Count == 0 ? IntPtr.Zero : Embedded.FindWindow(pids.Contains);
                    if (h == IntPtr.Zero)
                    {
                        if (cur == null && (DateTime.Now - t0).TotalSeconds > limit) { Log.Write($"embed: no game window within {limit:0} s"); return; }
                        System.Threading.Thread.Sleep(cur == null ? 5 : 100);
                        continue;
                    }
                    var e = Embedded.Take(parent, h, owned);
                    if (e == null) { Log.Write("embed: could not take the game window; it stays on its own"); return; }
                    if (cur != null && cur.Child == h) Log.Write("embed: the game reset its window; taken in again");
                    cur = e; fitted = DateTime.Now;
                    if (!wch.stopped) taken(e, (DateTime.Now - t0).TotalMilliseconds);
                }
            }
            catch (Exception x) { Log.Write("embed watch: " + x.Message); }
        }) { IsBackground = true, Name = "embed watch" }.Start();
        return wch;
    }
}

// While an emulator's window is inside the launcher, launcher actions do nothing: the buttons belong to
// the game. The Home menu comes from the Home key, View + Start or Guide (GameMenu).
public sealed class EmbedScreen : IScreen
{
    public void OnAct(Act a) { }
    public bool OnMouse(MouseAct m, Point p) => true;   // the mouse belongs to the game
}

// The processes a started program runs in turn (a port's starter and its game), from a Toolhelp snapshot.
public static class ProcTree
{
    // Every process under root (not root itself) whose exe is named `name` ("bb-probe.exe").
    public static System.Collections.Generic.HashSet<uint> Named(int root, string name)
    {
        var found = new System.Collections.Generic.HashSet<uint>();
        var all = Snapshot();
        var under = new System.Collections.Generic.HashSet<uint> { (uint)root };
        for (bool more = true; more;)
        {
            more = false;
            foreach (var (pid, parent, exe) in all)
                if (!under.Contains(pid) && under.Contains(parent)) { under.Add(pid); more = true; }
        }
        // A full path (a port's game program started through its link, Ports.GameLink): the same file name and
        // that exact program, since the starter and its helper scripts run under the same name as the link.
        bool path = Path.IsPathRooted(name);
        string file = path ? Path.GetFileName(name) : name;
        foreach (var (pid, _, exe) in all)
            if (pid != root && under.Contains(pid) && string.Equals(exe, file, StringComparison.OrdinalIgnoreCase)
                && (!path || string.Equals(ImagePath(pid), name, StringComparison.OrdinalIgnoreCase))) found.Add(pid);
        return found;
    }

    static string ImagePath(uint pid)
    {
        IntPtr h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new System.Text.StringBuilder(1024); uint n = (uint)sb.Capacity;
            return QueryFullProcessImageNameW(h, 0, sb, ref n) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageNameW(IntPtr h, uint flags, System.Text.StringBuilder name, ref uint size);

    // The first such process, or null.
    public static Process First(int root, string name)
    {
        foreach (uint pid in Named(root, name))
            try { return Process.GetProcessById((int)pid); } catch { /* the process has ended */ }
        return null;
    }

    static System.Collections.Generic.List<(uint pid, uint parent, string exe)> Snapshot()
    {
        var list = new System.Collections.Generic.List<(uint, uint, string)>();
        IntPtr snap = CreateToolhelp32Snapshot(2 /* TH32CS_SNAPPROCESS */, 0);
        if (snap == IntPtr.Zero || snap == (IntPtr)(-1)) return list;
        try
        {
            var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            for (bool ok = Process32FirstW(snap, ref e); ok; ok = Process32NextW(snap, ref e))
                list.Add((e.th32ProcessID, e.th32ParentProcessID, e.szExeFile));
        }
        finally { CloseHandle(snap); }
        return list;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PROCESSENTRY32W
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32W e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32W e);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
}
