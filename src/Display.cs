#nullable disable
// How a game shows: display mode, picture size, aspect ratio, window size and
// the emulator's internal resolution. A default for every game under Settings > Look and layout > Games
// display, and a per-game override in Game info and the in-game Game settings page (DisplayCard).
//   Cores (the launcher draws the picture): everything, live. Stretch / Fit / Integer / Custom size and
//     the aspect ratio size the picture on the stage; a windowed game gets a window of 1x, 2x... its own size.
//   Emulators and ports inside the launcher: display mode and window size (the launcher's window, which
//     the embedded window follows); aspect ratio and internal resolution through the game's tweaks.
//   Standalone emulators: Windowed takes the full-screen options off their start command; aspect ratio and
//     internal resolution through the game's tweaks.
//   Steam and PC games: not available (no safe, known way to change them from outside).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using static Ionea.Loc;

namespace Ionea;

// Null fields: use the default (the game's: Settings.Display; the default's: the built-in one).
public sealed class DisplayPrefs
{
    public string Mode { get; set; }     // "fullscreen", "borderless", "windowed"; null: as the launcher is
    public string Size { get; set; }     // "integer", "fit", "stretch", "custom"
    public string Aspect { get; set; }   // "original", "4:3", "16:9", "stretch"
    public int? Custom { get; set; }     // custom size, 0 (native 1x) to 100 (the whole display)
    public string Window { get; set; }   // "1x".."4x" or "custom" (WinW x WinH)
    public int? WinW { get; set; }       // custom window: the inside's size in screen pixels
    public int? WinH { get; set; }
    public int? WinX { get; set; }       // where the game's window was last (screen pixels, its top left)
    public int? WinY { get; set; }
    public string Filter { get; set; }   // picture filter (PictureFilter.cs): "auto", "sharp", "smooth", "crt", "lcd"; a game's falls back to its system's
    public bool Empty => Mode == null && Size == null && Aspect == null && Custom == null && Window == null && WinW == null && WinH == null && WinX == null && WinY == null && Filter == null;
}

public static class DisplayLogic
{
    public static readonly string[] Modes = { "fullscreen", "borderless", "windowed" };
    public static readonly string[] Sizes = { "integer", "fit", "stretch", "custom" };
    public static readonly string[] Aspects = { "original", "4:3", "16:9", "stretch" };
    public static readonly string[] Windows = { "1x", "1.5x", "2x", "2.5x", "3x", "4x", "fit", "custom" };

    public static string ModeName(string m) => m switch
    {
        "fullscreen" => T("settings.full_screen"), "borderless" => T("settings.borderless_full_screen"), "windowed" => T("settings.window"), _ => T("settings.same_as_ionea"),
    };
    public static string SizeName(string s) => s switch { "integer" => T("settings.integer_scale"), "stretch" => T("settings.stretch"), "custom" => T("settings.custom_size"), _ => T("settings.fit") };
    public static string AspectName(string a) => a switch { "4:3" => "4:3", "16:9" => "16:9", "stretch" => T("settings.stretch"), _ => T("settings.original") };
    public static string WindowName(string v, DisplayPrefs p) => v switch
    {
        "custom" => p?.WinW > 0 && p?.WinH > 0 ? T("settings.custom_x", p.WinW, p.WinH) : T("settings.custom"),
        "fit" => T("settings.as_large_as_fits"),
        _ => v ?? "2x",
    };
    // A preset's factor of the game's own size ("1.5x" -> 1.5); "fit" and unknown values: 0.
    public static double WindowFactor(string v) =>
        v != null && v.EndsWith("x") && double.TryParse(v[..^1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double n) && n > 0 ? n : v == "fit" ? 0 : 2;
    public static string CustomName(int v) => v <= 0 ? "1x (native)" : v >= 100 ? T("settings.whole_display") : v + "%";

    // What kind of game it is for these settings: "core", "embed", "standalone" or "pc" (Steam, programs).
    public static string Kind(MainWindow w, Game g)
    {
        if (g == null) return "all";
        if (g.Source != "rom") return "pc";
        var e = Ports.EmuOf(w, g);
        if (e == null) return "pc";
        if (e.IsCore) return "core";
        return EmbedLogic.CanEmbed(e) ? "embed" : "standalone";
    }

    // The values a game uses: its own, else the default's, else the built-in ones.
    public static DisplayPrefs Effective(MainWindow w, Game g)
    {
        var d = w.Settings.Display ?? new DisplayPrefs();
        var o = g?.Display ?? new DisplayPrefs();
        return new DisplayPrefs
        {
            Mode = o.Mode ?? d.Mode,
            Size = o.Size ?? d.Size ?? "fit",
            Aspect = o.Aspect ?? d.Aspect ?? "original",
            Custom = o.Custom ?? d.Custom ?? 50,
            Window = o.Window ?? d.Window ?? "2x",
            WinW = o.Window != null ? o.WinW : d.WinW, WinH = o.Window != null ? o.WinH : d.WinH,
            WinX = o.WinX, WinY = o.WinY,
        };
    }

    // DS and 3DS draw both screens in one picture; its shape is the layout's, so the aspect ratio stays.
    public static bool DualScreen(MainWindow w, Game g) => InputMap.SystemOf(w, g) is "nds" or "3ds";

    // One line for a Display row (Game info, the in-game menu, Settings).
    public static string Summary(MainWindow w, Game g)
    {
        string kind = Kind(w, g);
        if (kind == "pc") return T("settings.not_available");
        var p = Effective(w, g);
        string mode = p.Mode == "windowed" ? T("settings.window_2") + WindowName(p.Window, p) : ModeName(p.Mode);
        return kind is "core" or "all" ? mode + ", " + SizeName(p.Size) : mode;
    }

    // The picture's size on an area (stage units). aspect: the shape to show (width / height); nativeH: the
    // core's own height (1x); unit: screen pixels per stage unit (integer scale counts real pixels).
    public static Size Picture(string size, int custom, double aspect, double nativeH, double areaW, double areaH, double unit)
    {
        if (aspect <= 0 || nativeH <= 0 || areaW <= 0 || areaH <= 0) return new Size(areaW, areaH);
        if (unit <= 0) unit = 1;
        double fitH = Math.Min(areaH, areaW / aspect);
        switch (size)
        {
            case "stretch": return new Size(areaW, areaH);
            case "integer":
            {
                double k = Math.Floor(fitH * unit / nativeH + 1e-6);
                if (k < 1) break;   // smaller than 1x: fitted instead
                double h = nativeH * k / unit;
                return new Size(h * aspect, h);
            }
            case "custom":
            {
                double h1 = Math.Min(fitH, nativeH / unit);
                double h = h1 + (fitH - h1) * Math.Clamp(custom, 0, 100) / 100.0;
                return new Size(h * aspect, h);
            }
        }
        return new Size(fitH * aspect, fitH);
    }

    public static double AspectOf(string a, double original, double areaW, double areaH) => a switch
    {
        "4:3" => 4.0 / 3, "16:9" => 16.0 / 9, "stretch" => areaW / areaH, _ => original,
    };

    // A standalone emulator in a window: its full-screen options off ("-f true" -> "-f false", a bare -f,
    // -fullscreen or --fullscreen left out). Text in quotes (the game's path) is left alone.
    public static string NoFullscreen(string args)
    {
        if (string.IsNullOrEmpty(args)) return args;
        var parts = Regex.Split(args, "(\"[^\"]*\")");
        for (int i = 0; i < parts.Length; i += 2)
        {
            string s = Regex.Replace(parts[i], @"(?<=(^|\s)(-f|--fullscreen|-fullscreen)\s+)true\b", "false");
            s = Regex.Replace(s, @"(?<=^|\s)(-f|-fullscreen|--fullscreen)(?!\s+(true|false)\b)(?=\s|$)", "");
            parts[i] = Regex.Replace(s, @"[ ]{2,}", " ");
        }
        return string.Join("", parts).Trim();
    }

    // An emulator's start command for this game (Launch): Windowed takes the full-screen options off.
    public static string Args(MainWindow w, Game g, Emulator e, string args)
    {
        if (e == null || e.IsCore || EmbedLogic.CanEmbed(e)) return args;
        if (Effective(w, g).Mode != "windowed") return args;
        string a = NoFullscreen(args);
        if (a != args) Log.Write($"display: \"{g.Name}\" in a window: {e.Name} starts without its full-screen options");
        return a;
    }

    // An embedded game's own size at 1x (screen pixels), by console; cores report theirs.
    public static Size NativeOf(string system) => system switch
    {
        "gba" => new Size(240, 160), "gb" or "gbc" or "sgb" => new Size(160, 144), "nds" => new Size(256, 384), "3ds" => new Size(400, 480),
        "gc" or "wii" => new Size(640, 480), "ps4" or "ps5" => new Size(1920, 1080), "switch" or "switch2" or "ps3" or "wiiu" => new Size(1280, 720),
        "psp" => new Size(480, 272), "ps2" => new Size(640, 448), "psx" => new Size(320, 240), "nes" or "snes" => new Size(256, 224), "n64" => new Size(320, 240),
        _ => new Size(1280, 720),
    };
}

// The launcher's window while a game plays in it (cores and embedded emulators): full screen or a window of
// the game's own size, then back to how the launcher was when the game ends. Settings.Fullscreen is not
// changed. A window remembers where it was (and its size, when it was resized: Window size becomes Custom).
public static class GameWindow
{
    static Game active;
    static bool wasFull, windowed;
    static Rect wasRect;
    static int wantW, wantH;
    public static string LastResult = "none";
    // The game playing inside the launcher now (cores, embedded emulators), so a change in its Display card
    // shows at once (Apply).
    static Game playing; static string playingKind; static Size playingNative;
    // The Home menu is up: a small game window grows (at the game's shape) so the menu is readable, and
    // goes back when the game resumes. menuFrom is the window before it grew.
    static bool menuUp, grown; static RECT menuFrom;
    const double MenuMinH = 540;   // WPF units: the menu at half its full screen size, never smaller

    public static void Begin(MainWindow w, Game g, string kind, Size native)
    {
        if (g == null || kind is not ("core" or "embed")) return;
        playing = g; playingKind = kind; playingNative = native;
        Start(w, g, native);
    }

    static void Start(MainWindow w, Game g, Size native)
    {
        var p = DisplayLogic.Effective(w, g);
        if (p.Mode == null) { LastResult = "as launcher"; return; }
        bool full = p.Mode != "windowed";
        if (full && w.Settings.Fullscreen) { LastResult = "full screen"; return; }
        if (full && w.Opt.Windowed == true) { LastResult = "full screen skipped"; Log.Write("display: full screen skipped (this run is windowed)"); return; }
        if (active == null)
        {
            wasFull = w.Settings.Fullscreen;
            wasRect = new Rect(w.Left, w.Top, w.ActualWidth, w.ActualHeight);
        }
        active = g; windowed = !full;
        if (full)
        {
            w.WindowState = WindowState.Normal; w.WindowStyle = WindowStyle.None; w.ResizeMode = ResizeMode.NoResize; w.WindowState = WindowState.Maximized;
            LastResult = "full screen"; Log.Write($"display: \"{g.Name}\" full screen");
            return;
        }
        w.WindowState = WindowState.Normal; w.WindowStyle = WindowStyle.SingleBorderWindow; w.ResizeMode = ResizeMode.CanResize;
        // The window's inside has the game's shape, so no black bars: the game's size times the preset, as large
        // as fits ("fit"), or the remembered custom size; never larger than the work area.
        double dpi = VisualTreeHelper.GetDpi(w).DpiScaleX;
        double maxW = SystemParameters.WorkArea.Width * dpi * 0.96, maxH = SystemParameters.WorkArea.Height * dpi * 0.9;
        double k = DisplayLogic.WindowFactor(p.Window);
        if (k <= 0) k = Math.Min(maxW / native.Width, maxH / native.Height);
        double cw = native.Width * k, ch = native.Height * k;
        if (p.Window == "custom" && p.WinW > 0 && p.WinH > 0) { cw = p.WinW.Value; ch = p.WinH.Value; }
        if (cw > maxW || ch > maxH) { double f = Math.Min(maxW / cw, maxH / ch); cw *= f; ch *= f; }
        wantW = (int)Math.Round(cw); wantH = (int)Math.Round(ch);
        int? x = p.WinX, y = p.WinY;
        LastResult = $"window {wantW}x{wantH}";
        Log.Write($"display: \"{g.Name}\" in a window {wantW}x{wantH} ({p.Window})");
        grown = false;
        w.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { Place(w, wantW, wantH, x, y); if (menuUp) Grow(w); });
    }

    // The game's Display card changed (or the default for every game): the game playing inside the launcher
    // takes the new display mode and window size at once.
    public static void Apply(MainWindow w, Game changed)
    {
        if (playing == null || (changed != null && changed.Id != playing.Id)) return;
        var p = DisplayLogic.Effective(w, playing);
        if (active != null && (p.Mode == null || (p.Mode != "windowed" && w.Settings.Fullscreen)))
        {
            Restore(w);   // back to the launcher's own mode
            LastResult = p.Mode == null ? "as launcher" : "full screen";
            Log.Write($"display: \"{playing.Name}\" now {(p.Mode == null ? "as the launcher" : "full screen")}");
            return;
        }
        Start(w, playing, playingNative);
    }

    // The Home menu opens (true) or the game resumes (false).
    public static void Menu(MainWindow w, bool up)
    {
        if (menuUp == up) return;
        menuUp = up;
        if (active == null || !windowed) return;
        if (up) Grow(w);
        else if (grown)
        {
            grown = false;
            SetWindowPos(w.Hwnd, IntPtr.Zero, menuFrom.L, menuFrom.T, menuFrom.R - menuFrom.L, menuFrom.B - menuFrom.T, 0x0004 | 0x0010);
            Log.Write($"display: game window back to {wantW}x{wantH}");
        }
    }

    // A window too small for the menu grows around its middle to MenuMinH high, keeping its shape.
    static void Grow(MainWindow w)
    {
        if (grown || wantW <= 0 || wantH <= 0) return;
        double dpi = VisualTreeHelper.GetDpi(w).DpiScaleY;
        double maxW = SystemParameters.WorkArea.Width * dpi * 0.96, maxH = SystemParameters.WorkArea.Height * dpi * 0.9;
        double ch = Math.Min(MenuMinH * dpi, maxH), cw = ch * wantW / wantH;
        if (cw > maxW) { ch *= maxW / cw; cw = maxW; }
        if (ch <= wantH + 1 || !GetWindowRect(w.Hwnd, out menuFrom)) return;
        var r = new RECT { L = 0, T = 0, R = (int)Math.Round(cw), B = (int)Math.Round(ch) };
        int style = GetWindowLong(w.Hwnd, -16), ex = GetWindowLong(w.Hwnd, -20);
        try { AdjustWindowRectExForDpi(ref r, style, false, ex, GetDpiForWindow(w.Hwnd)); } catch { AdjustWindowRectEx(ref r, style, false, ex); }
        int ww = r.R - r.L, wh = r.B - r.T;
        int px = (menuFrom.L + menuFrom.R - ww) / 2, py = (menuFrom.T + menuFrom.B - wh) / 2;
        var wa = SystemParameters.WorkArea;   // kept on the work area
        px = (int)Math.Max(wa.Left * dpi, Math.Min(px, wa.Right * dpi - ww)); py = (int)Math.Max(wa.Top * dpi, Math.Min(py, wa.Bottom * dpi - wh));
        if (w.Opt.Windowed == true && !string.IsNullOrEmpty(w.Opt.Script)) { px = menuFrom.L; py = menuFrom.T; }   // a test stays off screen
        grown = true;
        SetWindowPos(w.Hwnd, IntPtr.Zero, px, py, ww, wh, 0x0004 | 0x0010);
        Log.Write($"display: game window {(int)Math.Round(cw)}x{(int)Math.Round(ch)} while the Home menu is up");
    }

    // Sizes the window so its inside is cw x ch screen pixels; at the remembered place, else in the middle.
    // A test run keeps the window where it is (off screen).
    static void Place(MainWindow w, int cw, int ch, int? x, int? y)
    {
        var r = new RECT { L = 0, T = 0, R = cw, B = ch };
        int style = GetWindowLong(w.Hwnd, -16), ex = GetWindowLong(w.Hwnd, -20);
        try { AdjustWindowRectExForDpi(ref r, style, false, ex, GetDpiForWindow(w.Hwnd)); } catch { AdjustWindowRectEx(ref r, style, false, ex); }
        int ww = r.R - r.L, wh = r.B - r.T;
        double dpi = VisualTreeHelper.GetDpi(w).DpiScaleX;
        var wa = SystemParameters.WorkArea;
        int px = (int)(wa.Left * dpi + (wa.Width * dpi - ww) / 2), py = (int)(wa.Top * dpi + (wa.Height * dpi - wh) / 2);
        if (x is int rx && y is int ry && MonitorFromPoint(new POINT { X = rx + 40, Y = ry + 20 }, 0) != IntPtr.Zero) { px = rx; py = ry; }
        if (w.Opt.Windowed == true && !string.IsNullOrEmpty(w.Opt.Script) && GetWindowRect(w.Hwnd, out var now)) { px = now.L; py = now.T; }
        SetWindowPos(w.Hwnd, IntPtr.Zero, px, py, ww, wh, 0x0004 | 0x0010);   // no z-order change, not activated
    }

    // The game ended: remember the window, then the launcher as it was.
    public static void End(MainWindow w, Game g)
    {
        if (playing != null && g != null && playing.Id == g.Id) { playing = null; menuUp = false; }
        if (active == null || g == null || active.Id != g.Id) return;
        if (windowed && GetWindowRect(w.Hwnd, out var r) && GetClientRect(w.Hwnd, out var c))
        {
            var d = g.Display ??= new DisplayPrefs();
            if (grown) r = menuFrom;   // closed from the Home menu: the window as the game had it
            d.WinX = r.L; d.WinY = r.T;
            int cw = c.R - c.L, ch = c.B - c.T;
            if (!grown && cw > 0 && ch > 0 && (Math.Abs(cw - wantW) > 4 || Math.Abs(ch - wantH) > 4)) { d.Window = "custom"; d.WinW = cw; d.WinH = ch; }
            w.Lib.Save();
            Log.Write($"display: \"{g.Name}\" window kept at {r.L},{r.T} ({cw}x{ch})");
        }
        Restore(w);
    }

    static void Restore(MainWindow w)
    {
        active = null; grown = false;
        w.ApplyWindowMode(false);
        if (!w.Settings.Fullscreen && !wasFull) { w.Left = wasRect.Left; w.Top = wasRect.Top; w.Width = wasRect.Width; w.Height = wasRect.Height; }
    }

    // Test scripts: the game window's inside now, in screen pixels.
    public static string ClientForTest(MainWindow w) => GetClientRect(w.Hwnd, out var c) ? $"{c.R - c.L}x{c.B - c.T}" : "?";

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern bool AdjustWindowRectEx(ref RECT r, int style, bool menu, int ex);
    [DllImport("user32.dll")] static extern bool AdjustWindowRectExForDpi(ref RECT r, int style, bool menu, int ex, uint dpi);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT p, uint flags);
}

// Settings > Look and layout > Games display (game == null: the default for every game), and a game's own
// Display card (Game info, the in-game Game settings page). Each change is kept at once; a core game
// that is running takes the picture changes live, the rest on its next start.
public sealed class DisplayCard : MenuCard
{
    readonly Game g;
    readonly bool fromGameMenu;
    readonly string kind;
    readonly TweakSet set;
    readonly Dictionary<string, string> tweakNow = new();
    bool tweaksChanged, screen;

    public DisplayCard(MainWindow w, Game game, bool fromGameMenu = false) : base(w, game == null ? T("settings.game_display") : T("settings.display"), 1180)
    {
        g = game; this.fromGameMenu = fromGameMenu;
        kind = DisplayLogic.Kind(w, g);
        Note(g == null ? T("settings.every_game_unless_a_game_has") : $"{g.Name}  ·  {KindName()}", 26, new Thickness(4, -14, 0, 20));
        if (kind == "pc")
        {
            BeginBox(T("settings.screen"));
            AddRow(T("settings.display_mode"), () => T("settings.not_available"));
            AddRow(T("settings.picture_size"), () => T("settings.not_available"));
            EndBox();
            AddRow(T("common.done"), activate: () => Close());
            return;
        }
        set = g != null ? Tweaks.For(w, g) : null;
        if (set != null) foreach (var d in set.Defs) tweakNow[d.Key] = Tweaks.GameValue(g, set, d);
        var mine = () => g == null ? (w.Settings.Display ??= new DisplayPrefs()) : (g.Display ??= new DisplayPrefs());
        DisplayPrefs Eff() => DisplayLogic.Effective(w, g);
        DisplayPrefs Def() => DisplayLogic.Effective(w, null);
        bool core = kind is "core" or "all";

        BeginBox(T("settings.screen"));
        Choice(T("settings.display_mode"), DisplayLogic.Modes, () => mine().Mode, v => { mine().Mode = v; screen = true; }, DisplayLogic.ModeName,
            g == null ? T("settings.same_as_ionea") : T("settings.default", DisplayLogic.ModeName(Def().Mode)));
        var win = Choice(T("settings.window_size"), DisplayLogic.Windows, () => mine().Window, v => { mine().Window = v; screen = true; }, v => DisplayLogic.WindowName(v, mine()),
            g == null ? null : T("settings.default", DisplayLogic.WindowName(Def().Window, Def())), "2x");
        win.Dim = () => Eff().Mode != "windowed";
        EndBox();

        if (core)
        {
            BeginBox(T("settings.picture"));
            Choice(T("settings.picture_size"), DisplayLogic.Sizes, () => mine().Size, v => mine().Size = v, DisplayLogic.SizeName,
                g == null ? null : T("settings.default", DisplayLogic.SizeName(Def().Size)), "fit");
            var cs = AddSlider(T("settings.custom_size"), () => Eff().Custom ?? 50, v => { mine().Custom = v; Changed(); });
            cs.Value = () => DisplayLogic.CustomName(Eff().Custom ?? 50);
            cs.Dim = () => Eff().Size != "custom";
            if (g != null && DisplayLogic.DualScreen(w, g))
                AddRow(T("settings.aspect_ratio"), () => T("settings.original_two_screens")).Dim = () => true;
            else
                Choice(T("settings.aspect_ratio"), DisplayLogic.Aspects, () => mine().Aspect, v => mine().Aspect = v, DisplayLogic.AspectName,
                    g == null ? null : T("settings.default", DisplayLogic.AspectName(Def().Aspect)), "original");
            // The picture filter (PictureFilter.cs): every game's here; in a game's card its own, and its system's.
            if (g == null)
                Choice(T("settings.picture_filter"), PictureFilters.Ids, () => mine().Filter, v => mine().Filter = v, PictureFilters.Name, (Func<string>)null, "auto");
            else
            {
                string sys = InputMap.SystemOf(w, g);
                Choice(T("settings.picture_filter"), PictureFilters.Ids, () => mine().Filter, v => mine().Filter = v, PictureFilters.Name,
                    () => T("settings.default", PictureFilters.Name(PictureFilters.Default(w, sys))));
                if (!string.IsNullOrEmpty(sys))
                    Choice(T("settings.all_games", InputMap.SystemName(sys)), PictureFilters.Ids, () => PictureFilters.SystemChoice(w, sys), v =>
                    {
                        var f = w.Settings.Filters ??= new Dictionary<string, string>();
                        if (v == null) f.Remove(sys); else f[sys] = v;
                        w.Settings.Save();
                        if (w.GameMenu.Core is CoreSession s) s.Relayout();   // another game of this system may be playing
                    }, PictureFilters.Name, () => T("settings.default_filter", PictureFilters.Name(w.Settings.Display?.Filter ?? "auto")));
            }
            EndBox();
        }
        if (set != null)
        {
            // The emulator's own settings that belong here: its aspect ratio (not for cores, the launcher
            // sizes their picture) and its internal resolution. Kept as the game's tweaks (Tweaks.cs).
            var defs = set.Defs.Where(d => d.Shared == "resolution" || (!core && d.Shared == "aspect")).ToList();
            if (defs.Count > 0)
            {
                BeginBox(set.Emu.Name);
                foreach (var d in defs) TweakRow(d.Shared == "resolution" ? T("settings.internal_resolution") : T("settings.aspect_ratio"), d);
                EndBox();
            }
        }
        AddRow(T("common.done"), activate: () => Close());
        Compact(8);
    }

    string KindName() => kind switch
    {
        "core" => (Ports.EmuOf(w, g)?.Name ?? T("settings.core")) + " inside Ionea", "embed" => Ports.EmuOf(w, g)?.Name + " inside Ionea", "standalone" => Ports.EmuOf(w, g)?.Name, _ => "",
    };

    // A row of choices. With nullName the list starts with that (the default, or "As the launcher");
    // without, an unset value shows and steps from the built-in one (fallback).
    Row Choice(string label, string[] values, Func<string> get, Action<string> put, Func<string, string> name, string nullName, string fallback = null) =>
        Choice(label, values, get, put, name, nullName == null ? null : () => nullName, fallback);

    Row Choice(string label, string[] values, Func<string> get, Action<string> put, Func<string, string> name, Func<string> nullName, string fallback = null)
    {
        List<string> Values() { var l = new List<string>(); if (nullName != null) l.Add(null); l.AddRange(values); return l; }
        string Cur() => get() ?? (nullName == null ? fallback : null);
        string Show(string v) => v == null && nullName != null ? nullName() : name(v);
        void Set(string v) { put(v); Changed(); }
        return WithList(AddRow(label, () => Show(Cur()), d =>
        {
            var l = Values(); int i = l.IndexOf(Cur());
            Set(l[((i < 0 ? 0 : i) + d + l.Count) % l.Count]);
        }), () => Values().Select(Show).ToList(), () => Math.Max(0, Values().IndexOf(Cur())), i => Set(Values()[i]));
    }

    void TweakRow(string label, TweakDef d)
    {
        string baseV = Tweaks.BaseValue(set, d);
        List<string> Values() { var l = new List<string> { null }; l.AddRange(d.Values); return l; }
        string Show(string v) => v == null ? T("settings.default", d.Show(baseV)) : d.Show(v);
        void Set(string v) { tweakNow[d.Key] = v; tweaksChanged = true; Refresh(); }
        WithList(AddRow(label, () => Show(tweakNow[d.Key]), dir =>
        {
            var l = Values(); int i = l.IndexOf(tweakNow[d.Key]);
            Set(l[((i < 0 ? 0 : i) + dir + l.Count) % l.Count]);
        }), () => Values().Select(Show).ToList(), () => Math.Max(0, Values().IndexOf(tweakNow[d.Key])), i => Set(Values()[i]));
    }

    void Changed()
    {
        if (g == null) w.Settings.Save();
        else
        {
            if (g.Display?.Empty == true) g.Display = null;
            w.Lib.Save();
        }
        // A core game that's running shows its new picture size at once.
        if (w.GameMenu.Core is CoreSession s && (g == null || s.Game.Id == g.Id)) s.Relayout();
        // A game playing inside the launcher takes a new display mode or window size at once.
        if (screen) { screen = false; GameWindow.Apply(w, g); }
        Refresh();
    }

    // Test scripts: a value by the row's label, as Left / Right would set it.
    public bool SetForTest(string label, string value)
    {
        var r = Rows.FirstOrDefault(x => ((x.El.Child as System.Windows.Controls.Grid)?.Children[0] as System.Windows.Controls.TextBlock)?.Text == label);
        if (r?.Names == null) return false;
        var names = r.Names();
        int i = names.IndexOf(value);
        if (i < 0) return false;
        r.Pick(i); Refresh();
        return true;
    }

    public override void Close(bool sound = true)
    {
        if (tweaksChanged && set != null && g != null)
        {
            var all = set.Defs.ToDictionary(d => d.Key, d => tweakNow.TryGetValue(d.Key, out var v) ? v : Tweaks.GameValue(g, set, d));
            Tweaks.SaveGame(w, g, set, all);
        }
        base.Close(sound);
        if (fromGameMenu && w.GameMenu.Game != null) w.GameMenu.ShowCard();
    }
}
