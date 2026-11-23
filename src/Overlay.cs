#nullable disable
// Ionea's own in-game performance overlay (a slim bar). Drawn by
// Ionea, never by an emulator (their own overlays stay off, Quiet.cs). Off / Simple (FPS) / Detailed (FPS,
// frame time with a small graph, emulation speed, CPU and RAM of the game's process, GPU and VRAM when
// Windows has them cheaply, play time this session), as a bar along the top (or bottom) edge of the game:
// FPS at the far left, the rest after it. Simple is a short tab in the corner with the FPS only. A size and
// an opacity. A default under Settings > Look and layout > Performance overlay and each game's own from the
// Home menu (the "Performance overlay" row: Left / Right switches Off / Simple / Detailed, A opens position,
// size and opacity).
//   Where the frame rate comes from (FrameSource.cs):
//   Cores (Ionea draws the picture): exact, from the frames the core runs (CoreSession.Perf). Drawn on the
//     stage's OverlayLayer over the game.
//   A port that writes a per-frame log (its definition's "perf", bbport's logs\*.frames.csv): exact, from it.
//   Every other emulator or port inside Ionea: the frames its window shows, counted by Windows' window capture
//     (at most the monitor's refresh rate).
//   Emulators and ports are drawn in a click-through window over the game's window.
//   Standalone emulators (Play inside Ionea off), Steam and PC games: not available.
// Cost: the numbers change twice a second (a DispatcherTimer); a frame only writes a time into a fixed ring
// (no allocations per frame); the bar is bitmap-cached so the game's frames under it don't redraw it; GPU
// counters are read on a low-priority thread every 2 s, Detailed only. It never takes focus or input, and it
// is hidden while the Home menu is open, before its still of the game is taken, and while the game is paused,
// closing, or not in front.
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using static Ionea.Loc;

namespace Ionea;

// Null fields: use the default (a game's: Settings.Overlay; the default's: the built-in one).
public sealed class OverlayPrefs
{
    public string Mode { get; set; }     // "off", "simple", "detailed"
    public string Corner { get; set; }   // the position: "top", "bottom" (kept under its old name; "tl"/"tr" read as top, "bl"/"br" as bottom)
    public string Size { get; set; }     // "small", "medium", "large"
    public int? Opacity { get; set; }    // percent, 40-100
    public bool Empty => Mode == null && Corner == null && Size == null && Opacity == null;
}

public static class OverlayLogic
{
    public static readonly string[] Modes = { "off", "simple", "detailed" };
    public static readonly string[] Positions = { "top", "bottom" };
    public static readonly string[] Sizes = { "small", "medium", "large" };
    public static readonly int[] Opacities = { 100, 90, 80, 70, 60, 50, 40 };

    public static string ModeName(string m) => m switch { "simple" => T("settings.simple"), "detailed" => T("settings.detailed"), _ => T("common.off") };
    public static string Pos(string c) => c is "bottom" or "bl" or "br" ? "bottom" : "top";
    public static string PosName(string c) => Pos(c) == "bottom" ? T("settings.bottom") : T("settings.top");
    public static string SizeName(string s) => s switch { "medium" => T("home.medium"), "large" => T("home.large"), _ => T("home.small") };
    public static string OpacityName(int v) => v + "%";
    public static double Scale(string s) => s switch { "medium" => 1.25, "large" => 1.6, _ => 1.0 };

    // "core", "embed", "standalone", "pc" or "all" (the default), as Display has them.
    public static string Kind(MainWindow w, Game g) => DisplayLogic.Kind(w, g);
    public static bool Available(string kind) => kind is "core" or "embed" or "all";

    public static OverlayPrefs Effective(MainWindow w, Game g)
    {
        var d = w.Settings.Overlay ?? new OverlayPrefs();
        var o = g?.Overlay ?? new OverlayPrefs();
        return new OverlayPrefs
        {
            Mode = o.Mode ?? d.Mode ?? "off",
            Corner = Pos(o.Corner ?? d.Corner),
            Size = o.Size ?? d.Size ?? "small",
            Opacity = Math.Clamp(o.Opacity ?? d.Opacity ?? 90, 40, 100),
        };
    }

    // The Home menu row and Settings: the mode, or why there is none.
    public static string Summary(MainWindow w, Game g) => Available(Kind(w, g)) ? ModeName(Effective(w, g).Mode) : T("settings.not_available");

    // The Home menu row's Left / Right: Off, Simple, Detailed for this game (kept as its own).
    public static void Step(MainWindow w, Game g, int dir)
    {
        int i = Array.IndexOf(Modes, Effective(w, g).Mode);
        (g.Overlay ??= new OverlayPrefs()).Mode = Modes[((i < 0 ? 0 : i) + dir + Modes.Length) % Modes.Length];
        w.Lib.Save();
        Log.Write($"overlay: \"{g.Name}\" set to {g.Overlay.Mode}");
        w.Overlay?.Update();
    }
}

public sealed class PerfOverlay
{
    readonly MainWindow w;
    readonly DispatcherTimer timer;
    OverlayView view;
    OverlayWindow win;
    string host;          // "layer" (cores), "window" (embedded), or null (hidden)
    string styleKey;      // what the view was built for (mode, shape, size, opacity, theme-free)
    Game game;            // the game the session's numbers are for
    readonly Stopwatch play = new();
    // the frames of an emulator or port (a core has its own): its frame log or its window's frames
    IDisposable source; FrameStats sourceStats; FrameLog frameLog; string sourceKey;
    // samples
    long lastStamp, lastFrames; int lastHead; long lastCpu; int cpuPid = -1; IntPtr cpuHandle;
    double keptFps = -1, keptMs = -1;
    readonly GpuSampler gpu = new();
    public string Shown { get; private set; } = "hidden";   // test scripts: hidden / simple / detailed
    public string LastFps => view?.FpsText ?? "none";
    public string LastText => host == null || view == null ? "hidden" : view.Text();
    public string LastSpeed => view?.SpeedText ?? "none";
    public string CornerNow { get; private set; } = "none";   // test scripts: top / bottom
    public string SourceNow => w.GameMenu.Core != null ? "core" : frameLog != null ? "log" : source is WindowFrames ? "window" : "none";   // test scripts

    public PerfOverlay(MainWindow win)
    {
        w = win;
        timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (s, e) => Tick();
        w.GameMenu.Changed += Update;
        w.StackChanged += Update;
        w.Closed += (s, e) => { timer.Stop(); gpu.Dispose(); DropSource(); CloseHandleIf(); win?.Close(); };
    }

    // Hide at once (the Home menu is opening; its still of the game must not show the overlay). For the
    // window, waits for the desktop to draw a frame without it.
    public void HideNow()
    {
        if (host == null) return;
        bool wasWindow = host == "window";
        Hide();
        if (wasWindow) try { DwmFlush(); } catch { /* desktop composition off */ }
    }

    // Shows, hides or restyles the overlay for the game's state. Called on every change the Home menu sees
    // (start, end, pause, resume), when the screen in front or the window in front changes, and each tick.
    public void Update()
    {
        var gm = w.GameMenu;
        var g = gm.Game;
        if (g != game)
        {
            game = g;
            play.Reset();
            DropSource();
            if (g != null) Log.Write($"overlay: \"{g.Name}\" {OverlayLogic.Effective(w, g).Mode} ({OverlayLogic.Kind(w, g)})");
        }
        if (g != null && !gm.Paused) play.Start(); else play.Stop();

        // Twice a second while a game plays with the overlay on (also to see the core's first picture and the
        // emulator's window come); stopped while paused, with the overlay off, or with no game.
        bool on = g != null && !gm.Paused && OverlayLogic.Effective(w, g).Mode != "off";
        if (on != timer.IsEnabled) { if (on) timer.Start(); else timer.Stop(); }
        if (g == null || OverlayLogic.Effective(w, g).Mode == "off") DropSource();
        string want = on ? Want(gm, g) : null;
        if (want == null) { Hide(); return; }
        var p = OverlayLogic.Effective(w, g);
        string key = $"{p.Mode}|{Art.IconShape}|{p.Size}|{p.Opacity}|{p.Corner}|{want}|{(gm.Core != null ? "core" : "emb")}";
        if (want != host || key != styleKey) Show(want, p, key, gm.Core != null);
    }

    // Where it should show now: "layer", "window", or null.
    string Want(GameMenu gm, Game g)
    {
        if (g == null || gm.Paused || gm.Closing || w.Quitting || w.Launch.Busy) return null;
        if (OverlayLogic.Effective(w, g).Mode == "off") return null;
        if (gm.Core is CoreSession s) return s.Shown && !s.Ended && w.TopScreen is CoreScreen ? "layer" : null;
        IntPtr child = gm.EmbedChild;   // shown, on its screen
        if (child == IntPtr.Zero) return null;
        var fg = GameMenu.TestFront switch { true => w.Hwnd, false => IntPtr.Zero, _ => Native.GetForegroundWindow() };
        return fg == w.Hwnd || fg == child || GetAncestor(fg, 3 /* GA_ROOTOWNER */) == child ? "window" : null;
    }

    void Show(string where, OverlayPrefs p, string key, bool core)
    {
        bool restyle = key != styleKey;
        if (restyle || view == null)
        {
            Detach();
            view = new OverlayView(p, core);
            styleKey = key;
        }
        if (where == "layer")
        {
            if (win != null) { win.Hide(); win.Content = null; }
            if (view.Root.Parent == null) w.OverlayLayer.Children.Add(view.Root);
            view.Place(1);
        }
        else
        {
            if (view.Root.Parent is Panel old) old.Children.Remove(view.Root);
            win ??= new OverlayWindow();
            if (win.Content != view.Root) win.Content = view.Root;
            PlaceWindow();
            if (!win.IsVisible) win.Show();
        }
        if (host == null) { ResetSamples(); Log.Write($"overlay: showing {p.Mode} ({(where == "layer" ? "over the core's picture" : "over the emulator's window")}, {p.Corner})"); }
        host = where;
        Shown = p.Mode;
        CornerNow = p.Corner;
        gpu.Active = p.Mode == "detailed";
        if (restyle) Sample();   // fill it in at once (it shows "-" until two samples exist)
    }

    void Hide()
    {
        if (host == null) return;
        host = null; Shown = "hidden";
        gpu.Active = false;
        if (view?.Root.Parent is Panel pnl) pnl.Children.Remove(view.Root);
        win?.Hide();
        Log.Write("overlay: hidden");
    }

    void Detach()
    {
        if (view == null) return;
        if (view.Root.Parent is Panel pnl) pnl.Children.Remove(view.Root);
        if (win != null && win.Content == view.Root) win.Content = null;
        view = null;
    }

    void Tick()
    {
        Update();
        if (host == null) return;
        Sample();
        if (host == "window") PlaceWindow();
    }

    void ResetSamples() { lastStamp = 0; lastHead = -1; lastCpu = -1; keptFps = keptMs = -1; }

    // The frames of the emulator or port now: its definition's frame log, else its window's frames. Kept while
    // the same game runs with the overlay on (also while the Home menu hides its window); a new window, a
    // new source.
    FrameStats Frames(GameMenu gm)
    {
        var emu = Ports.EmuOf(w, game);
        var perf = EmuDefs.Of(emu)?.Perf;
        IntPtr child = gm.EmbedChild;
        string key = perf?.FrameLog != null && perf.Time != null && emu?.Exe != null ? "log|" + emu.Exe
            : child != IntPtr.Zero ? "window|" + child.ToInt64() : sourceKey;
        if (key != sourceKey)
        {
            DropSource();
            sourceKey = key;
            if (key == null) return null;
            if (key.StartsWith("log|"))
            {
                // files written since the game's program started (or since the overlay last saw it start)
                DateTime since = DateTime.Now - play.Elapsed - TimeSpan.FromSeconds(30);
                try { if (gm.PerfPid is int pid && pid > 0) since = Process.GetProcessById(pid).StartTime.AddSeconds(-10); } catch { /* the process has ended */ }
                frameLog = new FrameLog(System.IO.Path.GetDirectoryName(emu.Exe), perf.FrameLog, perf.Time, since);
                source = frameLog; sourceStats = frameLog.Stats;
            }
            else { var wf = new WindowFrames(child); source = wf; sourceStats = wf.Stats; }
            lastHead = -1; keptFps = keptMs = -1;
        }
        frameLog?.Poll();
        return sourceStats;
    }

    void DropSource()
    {
        if (source == null && sourceKey == null) return;
        try { source?.Dispose(); } catch { /* best-effort cleanup */ }
        source = null; sourceStats = null; frameLog = null; sourceKey = null;
    }

    void Sample()
    {
        if (view == null) return;
        var gm = w.GameMenu;
        long now = Stopwatch.GetTimestamp();
        double dt = lastStamp == 0 ? 0 : (now - lastStamp) / (double)Stopwatch.Frequency;
        lastStamp = now;

        double fps = -1, ms = -1, speed = -1;
        var core = gm.Core;
        FrameStats frames;
        if (core != null)
        {
            // a core: every frame it ran, counted against the clock
            frames = core.Perf;
            long count = frames.Count; int head = frames.Head;
            if (dt > 0.05 && lastHead >= 0)
            {
                fps = (count - lastFrames) / dt;
                ms = Average(frames, head);
                if (core.TargetFps > 1) speed = fps / core.TargetFps * 100;
            }
            lastFrames = count; lastHead = head;
        }
        else
        {
            // an emulator or port: from the frames' own times (a log comes in pieces). Nothing new for a
            // while: the last numbers stay, then 0 (the game shows no new frames).
            frames = Frames(gm);
            if (frames != null)
            {
                int head = frames.Head;
                if (lastHead >= 0)
                {
                    double a = Average(frames, head);
                    if (a > 0) { keptMs = ms = a; keptFps = fps = 1000 / a; }
                    else if (frames.Quiet < 2.5) { fps = keptFps; ms = keptMs; }
                    else if (frames.Count > 0) fps = 0;
                }
                lastHead = head;
            }
        }

        // CPU and RAM of the game's process (a core runs inside Ionea's own)
        int pid = gm.PerfPid;
        double cpu = -1; long ram = -1;
        if (pid > 0)
        {
            if (pid != cpuPid) { CloseHandleIf(); cpuPid = pid; cpuHandle = pid == Environment.ProcessId ? GetCurrentProcess() : OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid); lastCpu = -1; }
            if (cpuHandle != IntPtr.Zero)
            {
                if (GetProcessTimes(cpuHandle, out _, out _, out long k, out long u))
                {
                    if (lastCpu >= 0 && dt > 0.05) cpu = Math.Clamp((k + u - lastCpu) / (dt * 1e7 * Environment.ProcessorCount) * 100, 0, 100);
                    lastCpu = k + u;
                }
                var mc = new PROCESS_MEMORY_COUNTERS_EX2 { cb = (uint)Marshal.SizeOf<PROCESS_MEMORY_COUNTERS_EX2>() };
                if (K32GetProcessMemoryInfo(cpuHandle, ref mc, mc.cb)) ram = (long)(mc.PrivateWorkingSetSize != UIntPtr.Zero ? mc.PrivateWorkingSetSize : mc.WorkingSetSize);
            }
        }
        if (gpu.Active) gpu.Pid = pid;
        view.Set(fps, ms, speed, cpu, ram, gpu.Active ? gpu.Gpu : -1, gpu.Active ? gpu.Vram : -1, play.Elapsed, frames, core?.TargetFps ?? 0);
    }

    // The mean frame time of the frames since the last sample (at most the ring's), or -1 when none came.
    double Average(FrameStats f, int head)
    {
        int n = Math.Min(head - lastHead, FrameStats.N);
        if (lastHead < 0 || n <= 0) return -1;
        double sum = 0;
        for (int i = head - n; i < head; i++) sum += f.At(i);
        return sum / n;
    }

    // The window over an embedded game: along its top or bottom edge (the bar as wide as the game), sized to
    // the game's height.
    void PlaceWindow()
    {
        IntPtr child = w.GameMenu.EmbedChild;
        if (win == null || view == null || child == IntPtr.Zero || !GetClientRect(child, out var r)) return;
        var pt = new POINT();
        if (!ClientToScreen(child, ref pt)) return;
        double dpi = VisualTreeHelper.GetDpi(w).DpiScaleX;
        double gw = (r.R - r.L) / dpi, gh = (r.B - r.T) / dpi;
        if (gw < 50 || gh < 50) return;
        view.Place(gh / 1080);   // stage units to the game window's DIPs
        var stc = view.Wide ? SizeToContent.Height : SizeToContent.WidthAndHeight;
        if (win.SizeToContent != stc) win.SizeToContent = stc;
        if (view.Wide && Math.Abs(win.Width - gw) > 0.5) win.Width = gw;
        win.UpdateLayout();
        double x = pt.X / dpi, y = pt.Y / dpi + (view.Bottom ? gh - win.ActualHeight : 0);
        if (Math.Abs(win.Left - x) > 0.5) win.Left = x;
        if (Math.Abs(win.Top - y) > 0.5) win.Top = y;
    }

    void CloseHandleIf()
    {
        if (cpuHandle != IntPtr.Zero && cpuPid != Environment.ProcessId) CloseHandle(cpuHandle);
        cpuHandle = IntPtr.Zero; cpuPid = -1;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_MEMORY_COUNTERS_EX2
    {
        public uint cb, PageFaultCount;
        public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage, QuotaPeakNonPagedPoolUsage,
            QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage, PrivateWorkingSetSize;
        public ulong SharedCommitUsage;
    }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool GetProcessTimes(IntPtr h, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll")] static extern bool K32GetProcessMemoryInfo(IntPtr h, ref PROCESS_MEMORY_COUNTERS_EX2 c, uint cb);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("dwmapi.dll")] static extern int DwmFlush();
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
}

// The numbers along the game's top (or bottom) edge, straight on the game: no bar, box or line behind them
//. Light text in fixed colours (the game is under it, not a card, so not the light / dark
// theme's), the FPS in the preset's dark-theme accent, each with a soft dark halo so it reads on bright and
// busy pictures. Detailed runs along the edge; Simple is the FPS alone in the corner. Built once per style;
// Set only changes text and the graph.
public sealed class OverlayView
{
    public readonly Grid Root = new() { IsHitTestVisible = false, Focusable = false };
    readonly ScaleTransform scale = new();
    readonly double baseScale;
    readonly bool detailed;
    public bool Wide => detailed;
    public bool Bottom { get; }
    readonly TextBlock fps;
    readonly TextBlock ftV, spV, cpuV, ramV, gpuV, vramV, timeV;
    readonly Path graph; readonly Line target;
    const double H = 34, GW = 64, GH = 16, Pad = 12;   // row height, graph size, room for the halo (stage units)
    static readonly Brush Ink = Frozen(Color.FromRgb(0xF4, 0xF5, 0xF7)), Ink2 = Frozen(Color.FromRgb(0xC9, 0xCD, 0xD4));
    static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    string fpsNow = "";
    public string FpsText => fpsNow;

    public OverlayView(OverlayPrefs p, bool core)
    {
        detailed = p.Mode == "detailed";
        Bottom = OverlayLogic.Pos(p.Corner) == "bottom";
        baseScale = OverlayLogic.Scale(p.Size);
        Root.Opacity = (p.Opacity ?? 90) / 100.0;
        Root.LayoutTransform = scale;
        Root.HorizontalAlignment = detailed ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        Root.VerticalAlignment = Bottom ? VerticalAlignment.Bottom : VerticalAlignment.Top;

        var bar = new Grid { Height = H, Margin = Bottom ? new Thickness(0, Pad, detailed ? 0 : Pad, 0) : new Thickness(0, 0, detailed ? 0 : Pad, Pad) };
        var accent = Frozen(Palette.Hex(Palette.I.Preset.AccD));

        // the halo: a soft dark glow right around the letters (no offset), nothing else behind them
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 16, 0),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 6, ShadowDepth = 0, Opacity = 1, Color = Colors.Black, RenderingBias = System.Windows.Media.Effects.RenderingBias.Quality },
        };
        fps = Num("-", 21, accent, FontWeights.Bold);
        fps.VerticalAlignment = VerticalAlignment.Center;
        var unit = Fonts.Text(T("settings.fps"), 13, Ink2, FontWeights.SemiBold);
        unit.VerticalAlignment = VerticalAlignment.Center; unit.Margin = new Thickness(5, 2, 0, 0);
        row.Children.Add(fps); row.Children.Add(unit);

        if (detailed)
        {
            TextBlock Item(string label)
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                var l = Fonts.Text(label, 13, Ink2, FontWeights.SemiBold);
                l.VerticalAlignment = VerticalAlignment.Center; l.Margin = new Thickness(22, 1, 6, 0);   // a gap, no divider line
                var v = Num("-", 15, Ink, FontWeights.SemiBold);
                v.VerticalAlignment = VerticalAlignment.Center;
                sp.Children.Add(l); sp.Children.Add(v);
                row.Children.Add(sp);
                return v;
            }
            ftV = Item(T("settings.frame"));
            // the last frames' times, small, after the frame time (a line at the core's target)
            var gc = new Canvas { Width = GW, Height = GH, Margin = new Thickness(8, 0, 0, 0), ClipToBounds = true, VerticalAlignment = VerticalAlignment.Center };
            if (core)
            {
                target = new Line { X1 = 0, X2 = GW, Stroke = Ink2, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 2, 2 }, Opacity = 0.6 };
                gc.Children.Add(target);
            }
            graph = new Path { Stroke = accent, StrokeThickness = 1.8, StrokeLineJoin = PenLineJoin.Round };
            gc.Children.Add(graph);
            ((StackPanel)ftV.Parent).Children.Add(gc);
            if (core) spV = Item(T("settings.speed"));
            cpuV = Item("CPU"); ramV = Item("RAM");
            gpuV = Item("GPU"); vramV = Item("VRAM");
            timeV = Item(T("settings.played"));
        }
        bar.Children.Add(row);
        Root.Children.Add(bar);
        // Drawn once into a picture and reused while the game's frames change under it; redrawn only
        // when a number changes (twice a second).
        Root.CacheMode = new BitmapCache { SnapsToDevicePixels = true, RenderAtScale = 1.5 };
    }

    static TextBlock Num(string s, double size, Brush fg, FontWeight wt)
    {
        var t = Fonts.Text(s, size, fg, wt);
        System.Windows.Documents.Typography.SetNumeralAlignment(t, FontNumeralAlignment.Tabular);
        return t;
    }

    // k: stage units to the host's units (1 on the stage).
    public void Place(double k)
    {
        double s = baseScale * k;
        if (scale.ScaleX != s) { scale.ScaleX = s; scale.ScaleY = s; }
    }

    static void Put(TextBlock t, string s) { if (t != null && t.Text != s) t.Text = s; }
    static void Item(TextBlock v, bool on)
    {
        if (v?.Parent is not UIElement sp) return;
        var vis = on ? Visibility.Visible : Visibility.Collapsed;
        if (sp.Visibility != vis) sp.Visibility = vis;
    }

    public void Set(double fpsV, double ms, double speed, double cpu, long ram, double gpuPct, long vram, TimeSpan played, FrameStats frames, double targetFps)
    {
        fpsNow = fpsV < 0 ? "-" : fpsV.ToString("0");
        Put(fps, fpsNow);
        if (!detailed) return;
        Put(ftV, ms < 0 ? "-" : ms.ToString("0.0") + " ms");
        Put(spV, speed < 0 ? "-" : Math.Round(speed).ToString("0") + "%");
        Put(cpuV, cpu < 0 ? "-" : cpu.ToString("0") + "%");
        Put(ramV, ram < 0 ? "-" : Bytes(ram));
        Put(gpuV, gpuPct < 0 ? "-" : Math.Min(100, gpuPct).ToString("0") + "%");
        Put(vramV, vram < 0 ? "-" : Bytes(vram));
        Put(timeV, played.TotalHours >= 1 ? $"{(int)played.TotalHours}:{played.Minutes:00}:{played.Seconds:00}" : $"{played.Minutes}:{played.Seconds:00}");
        Graph(frames, targetFps);
        Item(gpuV, gpuPct >= 0); Item(vramV, vram >= 0);
    }

    // Test scripts: every value shown now, one line (items hidden for lack of data are left out).
    public string Text()
    {
        var sb = new System.Text.StringBuilder("fps " + fpsNow);
        void A(string label, TextBlock v) { if (v?.Parent is UIElement sp && sp.Visibility == Visibility.Visible) sb.Append($" | {label} {v.Text}"); }
        A("frame", ftV); A("speed", spV); A("cpu", cpuV); A("ram", ramV); A("gpu", gpuV); A("vram", vramV); A("played", timeV);
        return sb.ToString();
    }
    public string SpeedText => spV?.Text ?? "none";

    static string Bytes(long b) => b >= 1L << 30 ? (b / (double)(1L << 30)).ToString("0.0") + " GB" : (b >> 20) + " MB";

    // The last 60 frame times, scaled so the target (or the recent mean) sits in the lower half (spikes stand
    // out, clipped at the top).
    void Graph(FrameStats f, double targetFps)
    {
        if (graph == null) return;
        const int n = 60;
        int head = f?.Head ?? 0, count = Math.Min(n, Math.Min(head, FrameStats.N));
        if (count < 2) { graph.Data = null; return; }
        double tms;
        if (targetFps > 1) tms = 1000 / targetFps;
        else { double s = 0; for (int i = head - count; i < head; i++) s += f.At(i); tms = Math.Max(1, s / count); }
        double top = tms * 2.2;
        double Y(double v) => GH - 2 - Math.Min(v, top) / top * (GH - 4);
        if (target != null) target.Y1 = target.Y2 = Y(tms);
        var sg = new StreamGeometry();
        using (var ctx = sg.Open())
        {
            double step = GW / (n - 1);
            double x0 = GW - (count - 1) * step;
            ctx.BeginFigure(new Point(x0, Y(f.At(head - count))), false, false);
            for (int i = 1; i < count; i++) ctx.LineTo(new Point(x0 + i * step, Y(f.At(head - count + i))), true, true);
        }
        sg.Freeze();
        graph.Data = sg;
    }
}

// A click-through window that never takes focus, over an embedded emulator's window.
public sealed class OverlayWindow : Window
{
    public OverlayWindow()
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; ShowActivated = false; Topmost = true; Focusable = false; ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight; Title = T("settings.ionea_overlay");
        SourceInitialized += (s, e) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            // WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED
            SetWindowLongPtr(h, -20, (IntPtr)((long)GetWindowLongPtr(h, -20) | 0x20 | 0x80 | 0x08000000 | 0x80000));
        };
    }
    [DllImport("user32.dll")] static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll")] static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
}

// GPU use and dedicated video memory of one process, from Windows' GPU performance counters (the ones Task
// Manager shows), read on a low-priority thread every 2 s while Active. -1 when Windows doesn't have them.
public sealed class GpuSampler : IDisposable
{
    volatile bool active, stop;
    volatile int pid;
    public double Gpu = -1; public long Vram = -1;
    readonly AutoResetEvent wake = new(false);
    Thread thread;
    public bool Active
    {
        get => active;
        set
        {
            if (active == value) return;
            active = value;
            if (value && thread == null) { thread = new Thread(Loop) { IsBackground = true, Name = "overlay gpu", Priority = ThreadPriority.BelowNormal }; thread.Start(); }
            wake.Set();
        }
    }
    public int Pid { get => pid; set => pid = value; }

    void Loop()
    {
        IntPtr q = IntPtr.Zero, eng = IntPtr.Zero, mem = IntPtr.Zero; int qpid = 0; bool broken = false;
        IntPtr buf = IntPtr.Zero; uint bufSize = 0;
        while (!stop)
        {
            if (!active || pid <= 0 || broken) { Gpu = -1; Vram = -1; wake.WaitOne(active ? 2000 : Timeout.Infinite); continue; }
            try
            {
                if (q == IntPtr.Zero || qpid != pid)
                {
                    if (q != IntPtr.Zero) PdhCloseQuery(q);
                    q = IntPtr.Zero; qpid = pid; Gpu = -1; Vram = -1;
                    if (PdhOpenQueryW(null, IntPtr.Zero, out q) != 0) { broken = true; Log.Write("overlay: no performance counters (GPU skipped)"); continue; }
                    if (PdhAddEnglishCounterW(q, $"\\GPU Engine(pid_{qpid}_*)\\Utilization Percentage", IntPtr.Zero, out eng) != 0) eng = IntPtr.Zero;
                    if (PdhAddEnglishCounterW(q, $"\\GPU Process Memory(pid_{qpid}_*)\\Dedicated Usage", IntPtr.Zero, out mem) != 0) mem = IntPtr.Zero;
                    if (eng == IntPtr.Zero && mem == IntPtr.Zero) { broken = true; Log.Write("overlay: no GPU counters on this PC (GPU skipped)"); continue; }
                    PdhCollectQueryData(q);
                    wake.WaitOne(1000);
                }
                if (PdhCollectQueryData(q) == 0)
                {
                    Gpu = eng != IntPtr.Zero ? EngineMax(eng, ref buf, ref bufSize) : -1;
                    Vram = mem != IntPtr.Zero ? (long)Sum(mem, 0x400 /* LARGE */, ref buf, ref bufSize) : -1;
                    if (Gpu < 0 && Vram < 0) { PdhCloseQuery(q); q = IntPtr.Zero; }   // no instances yet (the game has not used the GPU): build it again
                }
            }
            catch (Exception e) { broken = true; Log.Write("overlay gpu: " + e.Message); }
            wake.WaitOne(2000);
        }
        if (q != IntPtr.Zero) PdhCloseQuery(q);
        if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
    }

    // Items of a wildcard counter: (name, value). PDH_FMT_COUNTERVALUE_ITEM_W is 24 bytes on 64-bit.
    static int Items(IntPtr c, uint fmt, ref IntPtr buf, ref uint size)
    {
        uint need = size, n;
        int r = PdhGetFormattedCounterArrayW(c, fmt, ref need, out n, buf);
        if (r == unchecked((int)0x800007D2) /* PDH_MORE_DATA */)
        {
            if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
            size = need + 4096; buf = Marshal.AllocHGlobal((int)size); need = size;
            r = PdhGetFormattedCounterArrayW(c, fmt, ref need, out n, buf);
        }
        return r == 0 ? (int)n : -1;
    }

    static double Sum(IntPtr c, uint fmt, ref IntPtr buf, ref uint size)
    {
        int n = Items(c, fmt, ref buf, ref size);
        if (n <= 0) return -1;
        double s = 0;
        for (int i = 0; i < n; i++)
        {
            IntPtr it = buf + i * 24;
            if (Marshal.ReadInt32(it, 8) is 0 or 1) s += fmt == 0x400 ? Marshal.ReadInt64(it, 16) : BitConverter.Int64BitsToDouble(Marshal.ReadInt64(it, 16));
        }
        return s;
    }

    // As Task Manager: each engine type's use added up, the busiest type.
    static double EngineMax(IntPtr c, ref IntPtr buf, ref uint size)
    {
        int n = Items(c, 0x200 | 0x8000 /* DOUBLE | NOCAP100 */, ref buf, ref size);
        if (n <= 0) return -1;
        Span<double> sums = stackalloc double[16];
        Span<int> types = stackalloc int[16];
        int kinds = 0;
        for (int i = 0; i < n; i++)
        {
            IntPtr it = buf + i * 24;
            if (Marshal.ReadInt32(it, 8) is not (0 or 1)) continue;
            double v = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(it, 16));
            string name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(it));
            int at = name?.LastIndexOf("engtype_", StringComparison.Ordinal) ?? -1;
            int h = at < 0 ? 0 : string.GetHashCode(name.AsSpan(at));
            int k = 0; while (k < kinds && types[k] != h) k++;
            if (k == kinds) { if (kinds == 16) continue; types[kinds++] = h; sums[k] = 0; }
            sums[k] += v;
        }
        double max = 0; for (int k = 0; k < kinds; k++) max = Math.Max(max, sums[k]);
        return max;
    }

    public void Dispose() { stop = true; active = false; wake.Set(); }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhOpenQueryW(string src, IntPtr user, out IntPtr q);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhAddEnglishCounterW(IntPtr q, string path, IntPtr user, out IntPtr c);
    [DllImport("pdh.dll")] static extern int PdhCollectQueryData(IntPtr q);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhGetFormattedCounterArrayW(IntPtr c, uint fmt, ref uint size, out uint count, IntPtr buf);
    [DllImport("pdh.dll")] static extern int PdhCloseQuery(IntPtr q);
}

// Settings > Look and layout > Performance overlay (game == null: the default for every game), and a game's
// own, from the Home menu's "Performance overlay" row. Each change is kept at once.
public sealed class OverlayCard : MenuCard
{
    readonly Game g;
    readonly bool fromGameMenu;

    public OverlayCard(MainWindow w, Game game, bool fromGameMenu = false) : base(w, T("settings.performance_overlay"), 1100)
    {
        g = game; this.fromGameMenu = fromGameMenu;
        string kind = OverlayLogic.Kind(w, g);
        Note(g == null ? T("settings.every_game_unless_a_game_has") : g.Name, 26, new Thickness(4, -14, 0, 20));
        if (!OverlayLogic.Available(kind))
        {
            AddRow(T("settings.overlay"), () => kind == "pc" ? T("settings.not_for_pc_games") : T("settings.needs_play_inside_ionea"));
            AddRow(T("common.done"), activate: () => Close());
            return;
        }
        OverlayPrefs Mine() => g == null ? (w.Settings.Overlay ??= new OverlayPrefs()) : (g.Overlay ??= new OverlayPrefs());
        OverlayPrefs Def() => OverlayLogic.Effective(w, null);
        string D(string s) => g == null ? null : T("settings.default", s);

        Choice(T("settings.overlay"), OverlayLogic.Modes, () => Mine().Mode, v => Mine().Mode = v, OverlayLogic.ModeName, D(OverlayLogic.ModeName(Def().Mode)), "off");
        Choice(T("settings.position"), OverlayLogic.Positions, () => Mine().Corner is string c ? OverlayLogic.Pos(c) : null, v => Mine().Corner = v, OverlayLogic.PosName, D(OverlayLogic.PosName(Def().Corner)), "top");
        Choice(T("settings.size"), OverlayLogic.Sizes, () => Mine().Size, v => Mine().Size = v, OverlayLogic.SizeName, D(OverlayLogic.SizeName(Def().Size)), "small");
        var ops = Array.ConvertAll(OverlayLogic.Opacities, x => x.ToString());
        Choice(T("settings.opacity"), ops, () => Mine().Opacity?.ToString(), v => Mine().Opacity = v == null ? null : int.Parse(v), v => OverlayLogic.OpacityName(int.Parse(v)),
            D(OverlayLogic.OpacityName(Def().Opacity ?? 90)), "90");
        AddRow(T("common.done"), activate: () => Close());
    }

    Row Choice(string label, string[] values, Func<string> get, Action<string> put, Func<string, string> name, string nullName, string fallback)
    {
        System.Collections.Generic.List<string> Values() { var l = new System.Collections.Generic.List<string>(); if (nullName != null) l.Add(null); l.AddRange(values); return l; }
        string Cur() => get() ?? (nullName == null ? fallback : null);
        string Show(string v) => v == null && nullName != null ? nullName : name(v);
        void Set(string v) { put(v); Changed(); }
        return WithList(AddRow(label, () => Show(Cur()), d =>
        {
            var l = Values(); int i = l.IndexOf(Cur());
            Set(l[((i < 0 ? 0 : i) + d + l.Count) % l.Count]);
        }), () => Values().ConvertAll(Show), () => Math.Max(0, Values().IndexOf(Cur())), i => Set(Values()[i]));
    }

    void Changed()
    {
        if (g == null) w.Settings.Save();
        else { if (g.Overlay?.Empty == true) g.Overlay = null; w.Lib.Save(); }
        w.Overlay?.Update();
        Refresh();
    }

    // Test scripts: a value by the row's label, as picking it from the row's list.
    public bool SetForTest(string label, string value)
    {
        var r = System.Linq.Enumerable.FirstOrDefault(Rows, x => ((x.El.Child as Grid)?.Children[0] as TextBlock)?.Text == label);
        if (r?.Names == null) return false;
        int i = r.Names().IndexOf(value);
        if (i < 0) return false;
        r.Pick(i); Refresh();
        return true;
    }

    public override void Close(bool sound = true)
    {
        base.Close(sound);
        if (fromGameMenu && w.GameMenu.Game != null) w.GameMenu.ShowCard();
    }
}
