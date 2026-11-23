#nullable disable
// Home menu while an emulator game runs (Switch-style). View + Start together (or the Guide button)
// on a pad, or the Home menu key (Alt by default; Settings > Input > Launcher keys), pauses the game (its process is suspended, so it freezes and goes
// quiet) and brings the launcher up with: Resume, Close game, Home screen (the game stays paused).
// Pressing the Home combo again, or B, resumes. Steam and PC games are never paused (anti-cheat).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using static Ionea.Loc;

namespace Ionea;

public sealed class GameMenu
{
    readonly MainWindow w;
    Game game;
    Process proc;
    Process gameProc;     // a port's game program, when the started one runs it (Ports.GameProgram): paused with it
    CoreSession core;     // a game running inside the launcher (libretro core) instead of a process
    IntPtr gameWindow;
    IntPtr lastFg;   // the window in front at the last check (logged when not the launcher or the game window)
    HomeMenuCard card;
    int[] homeVks = System.Array.Empty<int>();   // the Home menu key's keys claimed now (HomeKeys)

    // An emulator window drawn inside the launcher (Embed.cs), the screen that keeps launcher input
    // away from it, and the still of its last frame shown while it is paused.
    Embedded emb;
    readonly EmbedScreen embScreen = new();
    System.Windows.Controls.Border still;
    EmbedWatch embWatch;
    DateTime lastToggle;
    // shadPS4's mouse camera as it is now in the running game: it starts off, and every F7 flips it.
    bool mouseLook;
    // Lichii / Eden mouse camera: its panning hotkey, posted once the game has settled and checked (SyncPanning).
    bool panWanted, panSent, panChecking, panDone;
    int panTries;            // hotkeys sent so far (at most PanMaxTries; the last one as a real key press)
    DateTime panAfter;       // not before this: the game window taken in (again) and given time to settle
    bool panTimer;           // a later SyncPanning is already scheduled
    int panGen;              // +1 per game start: a timer from an earlier start does nothing
    const int PanMaxTries = 3;
    // Which window is in front changed (the game window over the launcher, another program): the Home menu key follows it.
    readonly WinEventProc fgProc;
    IntPtr fgHook;

    bool paused;
    PauseWay held;           // how the open menu holds a standalone emulator (PauseLogic): what Resume undoes
    public bool Paused { get => paused; private set { if (paused == value) return; paused = value; Changed?.Invoke(); } }
    public event Action Changed;   // a game started or ended, or was paused / resumed (the home screen's "Paused" badge)
    public Game Game => game;
    public bool Closing { get; private set; }   // asked to close, not ended yet: its badge says "Closing…"
    public CoreSession Core => core;
    public bool IsEmbedded => emb != null;
    public bool EmbedOwned => emb != null && emb.Owned;   // test scripts: over the launcher (else a child window, streaming mode)
    public bool EmbedFits => emb != null && emb.Fits;   // test scripts
    // The performance overlay (Overlay.cs): the embedded game's window while it shows on its screen, and the
    // process whose CPU / RAM it shows (a core runs inside Ionea's own; a port's game program, else the emulator).
    public IntPtr EmbedChild => emb != null && emb.Shown && emb.Alive && w.TopScreen == embScreen ? emb.Child : IntPtr.Zero;
    public int PerfPid
    {
        get
        {
            if (core != null) return Environment.ProcessId;
            try { return GameProc() is Process gp ? gp.Id : proc != null && !proc.HasExited ? proc.Id : 0; } catch { return 0; }
        }
    }
    public bool EscClaimed => homeVks.Length > 0;   // the Home menu key is caught now (test scripts: "expect esc" / "homekey")
    // A standalone emulator reads the controls the launcher wrote at its start; changed since then, they
    // need a restart (shadPS4 reloads its own, so not counted).
    string ctlAtStart; ControlScope ctlScope; bool ctlTold;
    public bool ControlsPending => game != null && ctlAtStart != null && EmuControls.Signature(w.Settings.Input, ctlScope) != ctlAtStart;

    public GameMenu(MainWindow win)
    {
        w = win;
        w.Input.HomePressed += Pressed;
        w.Closing += (s, e) => CloseOnQuit();
        w.Closed += (s, e) => { if (Paused) Resume(false); };
        w.StackChanged += () => { if (game != null || homeVks.Length > 0) UpdateEsc(); };
        w.SizeChanged += (s, e) => { if (emb != null && !Paused) emb.Fit(); };
        w.LocationChanged += (s, e) => { if (emb != null && emb.Owned && !Paused) emb.Fit(); };
        fgProc = (hook, ev, hwnd, obj, child, thread, time) => { if (game != null) { UpdateEsc(); w.Overlay?.Update(); } };
        fgHook = SetWinEventHook(0x0003 /* EVENT_SYSTEM_FOREGROUND */, 0x0003, IntPtr.Zero, fgProc, 0, 0, 0 /* WINEVENT_OUTOFCONTEXT */);
        w.Closed += (s, e) => { if (fgHook != IntPtr.Zero) { UnhookWinEvent(fgHook); fgHook = IntPtr.Zero; } };
        // Coming back to the launcher (Alt+Tab, a click): the keyboard goes to the game again.
        w.Activated += (s, e) =>
        {
            if (emb != null && !Paused && emb.Shown) w.Dispatcher.BeginInvoke(emb.Focus, System.Windows.Threading.DispatcherPriority.Input);
            UpdateEsc();
        };
        w.Deactivated += (s, e) => UpdateEsc();
    }

    // An emulator game started / ended (from LaunchFx). embed: take its game window into the launcher.
    public void Begin(Game g, Process p, bool embed = false)
    {
        game = g; proc = p; gameProc = null; Paused = false; gameWindow = IntPtr.Zero; mouseLook = false;
        ctlAtStart = null; ctlTold = false; panSent = false; panChecking = false; panTimer = false; panGen++; panDone = false; panTries = 0;
        panWanted = Ports.EmuOf(w, g) is Emulator pe && EmuControls.MousePanning(pe) && EmuControls.Writes(pe, InputMap.SystemOf(w, g))
            && InputMap.MouseCamOn(w.Settings.Input, InputMap.ScopeFor(w, g));
        // Embedded: counted from when its window is taken in (WatchForWindow). On its own: from now.
        panAfter = embed ? DateTime.MaxValue : DateTime.Now.AddSeconds(PanSettleSecs + 3);
        if (panWanted && !embed) PanLater(panAfter);
        if (Ports.EmuOf(w, g) is Emulator ce && !ce.IsCore && !EmuControls.LiveReload(ce) && EmuControls.Writes(ce, InputMap.SystemOf(w, g)))
        { ctlScope = InputMap.ScopeFor(w, g); ctlAtStart = EmuControls.Signature(w.Settings.Input, ctlScope); }
        Changed?.Invoke();
        UpdateEsc();
        Log.Write($"home menu ready for \"{g.Name}\" (pid {p?.Id}{(embed ? ", inside the launcher" : "")})");
        if (embed && p != null)
        {
            // Until its window is inside, the launcher stays on top: the emulator shows its window a
            // moment before it can be taken in, and this keeps it covered.
            w.Topmost = true;
            if (!Ionea.Embedded.Cover(w.Hwnd, true)) Log.Write("embed: could not put the launcher on top while the game starts");
            Ui.After(20000, () => { if (proc == p && emb == null) w.Topmost = false; });
            WatchForWindow(p);
        }
    }

    // Take the game's window in as soon as it exists (EmbedWatch, on its own thread).
    void WatchForWindow(Process p)
    {
        embWatch?.Stop();
        bool owned = EmbedLogic.Owned(Ports.EmuOf(w, game), w.Settings.StreamingMode);   // fixed for this game: the setting applies from the next one
        string gameProgram = Ports.GameProgram(w, game);
        embWatch = EmbedWatch.Start(p, w.Hwnd, owned, () => Paused, (e, ms) => w.Dispatcher.BeginInvoke(() =>
        {
            if (game == null || proc != p) return;
            bool first = emb == null;
            w.Topmost = false;
            emb = e; gameWindow = e.Child;
            if (gameProgram != null && first && Native.GetWindowThreadProcessId(e.Child, out uint gp) != 0 && gp != p.Id)
                try { if (gameProc?.Id != (int)gp) gameProc = Process.GetProcessById((int)gp); Log.Write($"embed: the game runs in {gameProc.ProcessName} (pid {gp})"); } catch (Exception caught) { Log.Debug("home menu: game process", caught); }
            if (!System.Linq.Enumerable.Contains(w.Stack, (IScreen)embScreen)) w.Push(embScreen);
            if (Native.GetForegroundWindow() == w.Hwnd && !Paused) emb.Focus();
            UpdateEsc();
            if (first) Log.Write($"embed: \"{game.Name}\" window 0x{e.Child.ToInt64():X} is {(e.Owned ? "over" : "inside")} the launcher ({ms:0} ms after start; in front: {Front()})");
            // Taken in (or in again after the game reset its window): the mouse camera waits until it settles.
            if (panWanted && !panSent) { panAfter = DateTime.Now.AddSeconds(PanSettleSecs); PanLater(panAfter); }
            if (first) e.WarmCopy();
            if (first) Ui.After(2000, () =>
            {
                if (emb != e) return;
                Log.Write($"embed: 2 s later, in front: {Front()}");
                SyncMouseLook(e.Child);   // the game has read its controls by now
            });
        }), gameProgram);
    }

    // A key press posted to a window (never typed): the key's name ("F7") from a definition's hotkeys.
    static bool PostKey(IntPtr h, string key)
    {
        if (h == IntPtr.Zero || !Enum.TryParse(key, true, out System.Windows.Input.Key k)) return false;
        int vk = System.Windows.Input.KeyInterop.VirtualKeyFromKey(k);
        uint sc = MapVirtualKey((uint)vk, 0);
        PostMessage(h, 0x0100 /* WM_KEYDOWN */, (IntPtr)vk, (IntPtr)(int)(1 | sc << 16));
        PostMessage(h, 0x0101 /* WM_KEYUP */, (IntPtr)vk, unchecked((IntPtr)(int)(0xC0000001 | sc << 16)));
        return true;
    }

    // An emulator with a mouse-look hotkey (shadPS4: hotkey_toggle_mouse_to_joystick = f7) starts every game
    // with its mouse camera off; the hotkey turns it on, and with it the mouse is captured: hidden and kept in
    // the game window. The launcher posts it to the game window when this game's controls use the mouse as the
    // camera, and again when that changes while it runs. Posted while the game is frozen, it takes effect when
    // it runs again.
    void SyncMouseLook(IntPtr h)
    {
        if (game == null || h == IntPtr.Zero) return;
        bool want = InputMap.MouseLookWanted(w, game);
        if (want == mouseLook) return;
        string key = EmuControls.Hotkey(Ports.EmuOf(w, game), x => x.MouseLook);
        if (!PostKey(h, key)) return;
        mouseLook = want;
        Log.Write($"home menu: mouse camera {(want ? "on" : "off")} for \"{game.Name}\" ({key} to {Ports.EmuOf(w, game)?.Name})");
    }

    // An emulator with a mouse-panning hotkey (Lichii / Eden, the yuzu family: written at start by EmuControls)
    // starts with mouse panning off; each press of the hotkey flips it. Qt only takes it while that emulator is
    // the active app. Posted 1 s after start, a Lichii 366 game never got it:
    // focus was still moving and Lichii reset its window 3 s later. So it waits until the game window has been
    // taken in (again) for PanSettleSecs and is in front, then checks that it took (PanCheck) and tries again
    // if not: at most PanMaxTries, the last one a real key press. An odd number of presses, so even if the
    // check missed a press that worked, the camera ends up on.
    const double PanSettleSecs = 4;
    void SyncPanning(IntPtr fg)
    {
        if (!panWanted || panDone || panChecking || game == null || Paused || proc == null) return;
        if (DateTime.Now < panAfter) { PanLater(panAfter); return; }
        if (fg == IntPtr.Zero || !GameInFront(fg)) return;   // back here when the game is in front again
        if (panSent) { PanCheck(fg); return; }                // sent; checked once it has had time to take it
        panSent = true; panTries++;
        string key = EmuControls.Hotkey(Ports.EmuOf(w, game), x => x.MousePanning) ?? EmuControls.YuzuPanKey;
        bool real = panTries == PanMaxTries;
        if (real) TypeKey(key); else PostKey(fg, key);
        Log.Write($"home menu: mouse camera on for \"{game.Name}\" ({key} to the emulator{(real ? " as a key press" : "")}, try {panTries})");
        PanLater(DateTime.Now.AddMilliseconds(700));
    }
    public bool PanningSent => panTries > 0;

    void PanLater(DateTime at)
    {
        if (panTimer || at == DateTime.MaxValue) return;   // MaxValue: its window not taken in yet (WatchForWindow sets it)
        panTimer = true;
        int gen = panGen;
        Ui.After(Math.Max(50, (at - DateTime.Now).TotalMilliseconds + 20), () =>
        {
            if (gen != panGen) return;
            panTimer = false;
            SyncPanning(Native.GetForegroundWindow());
        });
    }

    // Is panning on? With it on, yuzu's render window puts the mouse back at its centre after every move. The
    // mouse is put at two points near the game window's centre in turn: moved back both times, to the same
    // spot, means on. Not settled (paused, the game left the front): checked again when it is in front.
    void PanCheck(IntPtr fg)
    {
        if (!Native.GetWindowRect(fg, out var r) || r.R - r.L < 100 || r.B - r.T < 100) return;
        panChecking = true;
        var g = game; int gen = panGen;
        int cx = (r.L + r.R) / 2, cy = (r.T + r.B) / 2;
        Pt a = new() { X = cx + 9, Y = cy + 6 }, b = new() { X = cx - 9, Y = cy - 6 };
        SetCursorPos(a.X, a.Y);
        Ui.After(200, () =>
        {
            GetCursorPos(out var ra);
            SetCursorPos(b.X, b.Y);
            Ui.After(200, () =>
            {
                GetCursorPos(out var rb);
                if (gen != panGen) return;
                panChecking = false;
                if (game != g || panDone) return;
                if (Paused || !GameInFront(Native.GetForegroundWindow())) { Log.Write("home menu: mouse camera check put off (the game is not in front)"); return; }
                bool on = (ra.X != a.X || ra.Y != a.Y) && (rb.X != b.X || rb.Y != b.Y) && Math.Abs(ra.X - rb.X) <= 2 && Math.Abs(ra.Y - rb.Y) <= 2;
                if (on) { panDone = true; Log.Write($"home menu: mouse camera is on for \"{g.Name}\" (checked: the mouse is held at {ra.X},{ra.Y})"); return; }
                if (panTries >= PanMaxTries) { panDone = true; Log.Write($"home menu: mouse camera for \"{g.Name}\" still looks off after {panTries} tries; left as it is"); return; }
                Log.Write($"home menu: mouse camera for \"{g.Name}\" looks off (the mouse stayed at {ra.X},{ra.Y} / {rb.X},{rb.Y}); again in 1 s");
                panSent = false;
                PanLater(DateTime.Now.AddSeconds(1));
            });
        });
    }

    // A real key press (the window in front gets it), for when a posted one was not taken.
    static void TypeKey(string key)
    {
        if (!Enum.TryParse(key, true, out System.Windows.Input.Key k)) return;
        byte vk = (byte)System.Windows.Input.KeyInterop.VirtualKeyFromKey(k);
        byte sc = (byte)MapVirtualKey(vk, 0);
        Native.keybd_event(vk, sc, 0, UIntPtr.Zero);
        Native.keybd_event(vk, sc, 2 /* KEYEVENTF_KEYUP */, UIntPtr.Zero);
    }

    string Front()
    {
        var fg = Native.GetForegroundWindow();
        if (fg == w.Hwnd) return "the launcher";
        Native.GetWindowThreadProcessId(fg, out uint pid);
        string n; try { n = Process.GetProcessById((int)pid).ProcessName; } catch { n = "?"; }
        return $"{n} (pid {pid})";
    }

    // The embedded game has drawn over the launch screen (LaunchFx): its window is up and shows something
    // other than the launch screen's background where the screen is plain.
    public bool GameDrawn(System.Windows.Media.Color bg) => emb != null && emb.Alive && emb.Shown && emb.Drawn(bg);

    void ShowStill(BitmapSourceOrNull img)
    {
        HideStill();
        // A window of another shape than the 16:9 stage (windowed Ionea) has strips of the launcher's colour beside the
        // stage, which the game's window covered: the whole picture goes on the window's background and the stage gets
        // its middle, so the still looks like the game did, without white bars.
        var src = img.Value;
        var middle = src == null ? null : StageMiddle(src);
        if (middle != null) { w.Background = new System.Windows.Media.ImageBrush(src) { Stretch = System.Windows.Media.Stretch.Fill }; stillBehind = true; src = middle; }
        // No picture: a plain backdrop all the same, so the library doesn't show behind the menu.
        var image = new System.Windows.Controls.Image { Source = src, Stretch = middle != null ? System.Windows.Media.Stretch.Fill : System.Windows.Media.Stretch.UniformToFill };
        System.Windows.Media.RenderOptions.SetBitmapScalingMode(image, System.Windows.Media.BitmapScalingMode.HighQuality);
        still = new System.Windows.Controls.Border { Background = w.P.Bg, Child = img.Value == null ? null : image };
        w.GameLayer.Children.Add(still);
    }
    void HideStill()
    {
        if (still != null) { w.GameLayer.Children.Remove(still); still = null; }
        if (stillBehind) { w.Background = w.P.Bg; stillBehind = false; }
    }
    bool stillBehind;
    public bool StillBehind => stillBehind;   // test scripts

    // The part of a picture of the whole window that the stage covers (the Viewbox centres it), or null when the stage
    // fills the window (full screen on a 16:9 screen).
    System.Windows.Media.Imaging.BitmapSource StageMiddle(System.Windows.Media.Imaging.BitmapSource src)
    {
        // The window's inside: its template's root (the Viewbox itself is only as big as the stage it shows).
        if (VisualTreeHelper.GetChildrenCount(w) == 0 || VisualTreeHelper.GetChild(w, 0) is not FrameworkElement c || c.ActualWidth < 1 || c.ActualHeight < 1) return null;
        double s = Math.Min(c.ActualWidth / 1920, c.ActualHeight / 1080), fx = 1920 * s / c.ActualWidth, fy = 1080 * s / c.ActualHeight;
        int pw = (int)Math.Round(src.PixelWidth * fx), ph = (int)Math.Round(src.PixelHeight * fy);
        if (pw >= src.PixelWidth - 1 && ph >= src.PixelHeight - 1) return null;
        if (pw < 16 || ph < 16) return null;
        var part = new System.Windows.Media.Imaging.CroppedBitmap(src, new Int32Rect((src.PixelWidth - pw) / 2, (src.PixelHeight - ph) / 2, pw, ph));
        part.Freeze();
        return part;
    }
    readonly record struct BitmapSourceOrNull(System.Windows.Media.Imaging.BitmapSource Value);

    // "Home screen" from the menu: the paused game steps aside so the launcher can be used.
    public void StepAside()
    {
        HideStill();
        w.Pop(embScreen);
        UpdateEsc();
    }

    // Quitting the launcher during an emulator game closes the game first (it lives in the launcher's window).
    void CloseOnQuit()
    {
        embWatch?.Stop();
        if (emb == null || proc == null) return;
        var p = proc;
        try
        {
            if (Paused) { if (held == PauseWay.Freeze) ResumeProcs(); Paused = false; }
            emb.Close(true);
            Log.Write($"embed: closing \"{game?.Name}\" because the launcher is quitting");
            if (!p.WaitForExit(8000)) { Log.Write("embed: the game did not close in 8 s; ending it"); p.Kill(true); }
        }
        catch (Exception e) { Log.Write("embed close on quit: " + e.Message); }
    }

    // A game running inside the launcher: pausing stops the core between frames (no process tricks).
    public void BeginCore(Game g, CoreSession s)
    {
        game = g; core = s; proc = null; Paused = false; gameWindow = IntPtr.Zero;
        Changed?.Invoke();
        UpdateEsc();
        w.Dispatcher.BeginInvoke(UpdateEsc);   // its screen goes in front just after
        Log.Write($"home menu ready for \"{g.Name}\" (inside the launcher)");
    }

    public void End()
    {
        if (game == null) return;
        Log.Write($"home menu: \"{game.Name}\" ended");
        game = null; proc = null; gameProc = null; core = null; Paused = false; Closing = false;
        Changed?.Invoke();
        w.Launch.HideClosing();
        embWatch?.Stop(); embWatch = null;
        if (emb != null) { emb = null; HideStill(); w.Pop(embScreen); }
        if (card != null) { var c = card; card = null; c.CloseQuiet(); }
        w.Topmost = false;
        UpdateEsc();
    }

    public void Pressed()
    {
        if (game == null || (proc == null && core == null)) return;
        if (w.Launch.Busy) return;
        // One press, one toggle: a second Home within 300 ms (a key bounce, Home + Guide together) is ignored.
        if ((DateTime.Now - lastToggle).TotalMilliseconds < 300) return;
        lastToggle = DateTime.Now;
        if (Paused) Resume(true); else Pause();
    }

    void Pause()
    {
        w.Overlay?.HideNow();   // before any still of the game is taken: the overlay is not in it
        w.Profiles.PausePlay();   // play time doesn't count while the Home menu is up
        if (core != null || (emb != null && emb.Alive)) GameWindow.Menu(w, true);   // a small game window grows for the menu
        if (core != null)
        {
            core.Pause(); Paused = true;
            Log.Write($"home menu: paused \"{game.Name}\" (core)");
            Sounds.Play("menuopen");
            ShowCard();
            return;
        }
        try { proc.Refresh(); if (proc.HasExited) return; } catch { return; }
        if (emb != null && emb.Alive)
        {
            // Inside the launcher: keep a still of the game and open the menu over it, and only once the launcher
            // has drawn that (hiding first showed the home screen for a moment), hide the game's window
            // (while it can still answer) and freeze it.
            // A game presenting straight to the display (bbport, Lichii) leaves the launcher's picture stale under it:
            // a GDI cover with the still goes over the game first, the switch happens under it, and it goes once the
            // launcher has drawn the menu (Cover.cs).
            // With the cover, the menu opens once it is gone, so its opening is seen whole (under the
            // cover it was half over when it showed, and felt slow and jumpy).
            Sounds.Play("menuopen");
            var shot = emb.Snapshot();
            var cover = Cover.Show(w.Hwnd, emb.Child, shot);
            ShowStill(new(shot));
            if (cover != null) PauseEmbedded(); else ShowCard(quiet: true);
            var e = emb; int frames = 0; var since = System.Diagnostics.Stopwatch.StartNew();
            void Rendered(object s, EventArgs a)
            {
                // Rendering comes before a frame: the third means two frames are drawn; with the cover, also 80 ms
                if (++frames < 3 || (cover != null && since.ElapsedMilliseconds < 80)) return;
                System.Windows.Media.CompositionTarget.Rendering -= Rendered;
                if (cover != null)
                {
                    cover.Close();
                    if (emb == e && game != null && !e.Shown) ShowCard(quiet: true);   // not resumed or ended meanwhile
                    return;
                }
                if (emb != e || game == null || !e.Alive || card == null) return;   // ended, or resumed in between
                PauseEmbedded();
            }
            System.Windows.Media.CompositionTarget.Rendering += Rendered;
            return;
        }
        PauseOutside();
    }

    // The embedded game's second half of Pause, after the menu is on screen.
    void PauseEmbedded()
    {
        try { proc.Refresh(); if (proc.HasExited) return; } catch { return; }
        {
            var way = Way();
            if (way == PauseWay.Key && !PostKey(emb.Child, PauseKey())) way = PauseWay.Unfocus;   // while its window still shows
            emb.Hide();
            if (emb.Owned && Native.GetForegroundWindow() != w.Hwnd) w.BringToFront();   // Windows normally hands the front to the owner by itself
            w.FocusSelf();
            ClipCursor(IntPtr.Zero);   // the mouse camera kept the mouse in the game window: free it for the menu
            if (way != PauseWay.Freeze) { Hold(way, "inside the launcher"); return; }
            emb.Unown();               // a frozen owned window hangs the launcher on Alt+Tab / minimize
            int rr = SuspendGame();
            if (rr != 0) emb.Reown();
            held = PauseWay.Freeze;
            Paused = rr == 0;
            Log.Write($"home menu: paused \"{game.Name}\" (inside the launcher, {(Paused ? "suspended" : "suspend failed 0x" + rr.ToString("X"))})");
        }
    }

    void PauseOutside()
    {
        var fg = Native.GetForegroundWindow();
        gameWindow = GameInFront(fg) ? fg : proc.MainWindowHandle;
        var way = Way();
        if (way == PauseWay.Key && !PostKey(gameWindow, PauseKey())) way = PauseWay.Unfocus;
        // Come to the front while the game still runs (it may need to process the focus change), then freeze it.
        w.Topmost = true;
        w.BringToFront();
        bool front = Native.GetForegroundWindow() == w.Hwnd;
        if (!front) Log.Write("home menu: Windows kept the game in front");
        if (way != PauseWay.Freeze) Hold(way, "own window");
        else
        {
            int r = SuspendGame();
            held = PauseWay.Freeze;
            Paused = r == 0;
            Log.Write($"home menu: paused \"{game.Name}\" ({(Paused ? "suspended" : "suspend failed 0x" + r.ToString("X"))})");
        }
        Sounds.Play("menuopen");
        ShowCard();
    }

    // How this emulator is held while the menu is open (PauseLogic): frozen only with Settings > Freeze the emulator.
    PauseWay Way() => PauseLogic.Decide(w.Settings.FreezeEmulator, PauseKey());
    string PauseKey() => EmuDefs.At(Ports.EmuOf(w, game), SupportLevel.Known)?.Hotkeys?.Pause;

    // Held without freezing: its own pause key was posted, or it keeps running behind the menu, out of focus.
    void Hold(PauseWay way, string where)
    {
        held = way;
        Paused = true;
        Log.Write($"home menu: paused \"{game.Name}\" ({where}, {(way == PauseWay.Key ? "its pause key " + PauseKey() : "not frozen, out of focus")})");
    }

    // A card opened from the menu (Controls, Tweak settings...) brings the menu back on the same page.
    string backPage; int backFocus;
    public void ReturnTo(string page, int focus) { backPage = page; backFocus = focus; }

    public void ShowCard(bool quiet = false)
    {
        if (card != null || game == null) return;
        card = new HomeMenuCard(w, this, game, backPage, backFocus) { QuietOpen = quiet };
        backPage = null; backFocus = 0;
        card.Open();
        UpdateEsc();
    }

    internal void CardClosed(HomeMenuCard c) { if (card == c) card = null; w.Topmost = false; UpdateEsc(); }

    public void Resume(bool front) { ResumeGame(front); UpdateEsc(); }

    void ResumeGame(bool front)
    {
        if (game == null) return;
        if (card != null) { var c = card; card = null; c.CloseQuiet(); }
        backPage = null;
        w.Topmost = false;
        w.Profiles.ResumePlay();
        GameWindow.Menu(w, false);
        if (core != null)
        {
            core.Resume(); Paused = false;
            Log.Write($"home menu: resumed \"{game.Name}\" (core)");
            w.FocusSelf();
            return;
        }
        bool unpauseKey = false;   // its own pause key again (a toggle: continue), once its window shows
        if (Paused)
        {
            unpauseKey = held == PauseWay.Key;
            if (held == PauseWay.Freeze)
            {
                ResumeProcs();
                emb?.Reown();   // running again: it can be the launcher's owned window again
            }
            Paused = false;
            Log.Write($"home menu: resumed \"{game.Name}\"");
        }
        if (front && !ctlTold && ControlsPending)
        {
            ctlTold = true;
            Log.Write($"home menu: \"{game.Name}\" controls changed while it runs; they apply from its next start");
            w.Toasts.Show(T("menu.new_controls_saved"), T("menu.restart_the_game_to_use_them"), "info");
        }
        if (emb != null)
        {
            if (!front) { if (unpauseKey) PostKey(emb.Child, PauseKey()); return; }
            HideStill();
            if (!System.Linq.Enumerable.Contains(w.Stack, (IScreen)embScreen)) w.Push(embScreen);
            w.BringToFront();
            emb.Show();
            emb.Focus();
            if (unpauseKey) PostKey(emb.Child, PauseKey());
            UpdateEsc();
            return;
        }
        IntPtr h = gameWindow != IntPtr.Zero && Native.IsWindowVisible(gameWindow) ? gameWindow : SafeMainWindow();
        if (unpauseKey) PostKey(h, PauseKey());
        if (!front) return;
        if (h != IntPtr.Zero) Native.ForceForeground(h);
    }

    // An emulator with a reload hotkey (shadPS4: hotkey_reload_inputs = f8) reads its input file again on it.
    // Posted to its window (never typed), so it is read once the game runs again. False when there is no window.
    public bool ReloadInputs()
    {
        IntPtr h = emb?.Child ?? IntPtr.Zero;
        if (h == IntPtr.Zero) h = gameWindow != IntPtr.Zero ? gameWindow : proc != null ? Embedded.FindWindow(proc.Id) : IntPtr.Zero;
        if (h == IntPtr.Zero) return false;
        var e = game == null ? null : Ports.EmuOf(w, game);
        if (!PostKey(h, EmuControls.Hotkey(e, x => x.ReloadInputs))) return false;
        Log.Write($"home menu: asked {e?.Name} to reload its controls for \"{game?.Name}\"");
        SyncMouseLook(h);   // the mouse camera turned on or off: after the reload
        return true;
    }

    // Close the game the way its own close button would; kill it if it is still there after 6 s.
    public void CloseGame()
    {
        if (game == null) return;
        if (core != null)
        {
            if (card != null) { var c = card; card = null; c.CloseQuiet(); }
            Log.Write($"home menu: closing \"{game.Name}\" (core)");
            core.Stop(); Paused = false;
            return;
        }
        var p = proc; var g = game; string name = game.Name;
        Resume(false);
        Log.Write($"home menu: closing \"{name}\"");
        // An emulator can take a while: the start's screen says "Closing <name>" over it until it has ended.
        int pid = 0; try { pid = p.Id; } catch { /* already ended */ }
        w.Launch.ShowClosing(g, w.Home?.CoverOf(g), pid);
        Closing = true; Changed?.Invoke();
        if (emb != null && emb.Alive) { embWatch?.Stop(); HideStill(); emb.Close(true); }
        else try { p.Refresh(); if (!p.CloseMainWindow()) Log.Write("home menu: no main window to close"); } catch { /* already ended */ }
        Task.Run(() =>
        {
            try
            {
                if (!p.WaitForExit(6000)) { Log.Write($"home menu: \"{name}\" did not close in 6 s; ending it"); p.Kill(true); }
            }
            catch (Exception caught) { Log.Debug("home menu: close wait", caught); }
        });
    }

    // Switch games: the open game closes as Close game does, and once it has ended (its closing
    // screen gone too) the picked game starts, from the tile it was picked on. Every kind of game: a core, an
    // emulator inside the launcher or in its own window, a native port.
    public void SwitchTo(Game next, Rect from, System.Windows.Media.Imaging.BitmapSource cover, PortStart port = null)
    {
        var old = game;
        if (old == null) { w.Launch.Start(next, from, cover, port); return; }
        Log.Write($"home menu: switching from \"{old.Name}\" to \"{next.Name}\"");
        Switching = next.Name;
        CloseGame();
        var t0 = DateTime.Now;
        void Wait()
        {
            double ms = (DateTime.Now - t0).TotalMilliseconds;
            bool gone = game == null && w.Launch.ClosingName == null && !w.Launch.Busy;
            if (!gone && ms < 20000) { Ui.After(100, Wait); return; }
            Switching = null;
            if (!gone)
            {
                Log.Write($"home menu: \"{old.Name}\" still not closed after 20 s; \"{next.Name}\" not started");
                w.Toasts.Show(T("menu.is_still_closing", old.Name), T("menu.start_again_in_a_moment", next.Name), "info");
                return;
            }
            Log.Write($"home menu: \"{old.Name}\" closed after {ms:0} ms; starting \"{next.Name}\"");
            w.Launch.Start(next, from, cover, port);
        }
        Ui.After(100, Wait);
    }
    public string Switching { get; private set; }   // the game waiting to start (tests)

    // The launcher is closing with its outro (MainWindow.BeginQuit): the game closes as Close game does, and a
    // game inside the launcher (a core) gets the same closing screen too, so the screen covers the whole wait.
    // A paused core is stopped where it is; its saves are written as it unloads, as on any close.
    public void CloseForQuit()
    {
        if (game == null) return;
        if (core != null && !w.Launch.Busy)
        {
            w.Launch.ShowClosing(game, w.Home?.CoverOf(game), 0);
            Closing = true; Changed?.Invoke();
        }
        CloseGame();
    }

    // "Open the emulator" from the menu: close the game, then open its emulator's own window (EmuOpen.cs).
    // Waits up to 20 s for the close, as Restart does.
    public void OpenEmulator()
    {
        var g = game;
        var e = Ports.EmuOf(w, g);
        if (g == null || e == null || e.IsCore) return;
        Log.Write($"home menu: closing \"{g.Name}\" to open {e.Name}");
        CloseGame();
        var until = DateTime.Now.AddSeconds(20);
        void Wait()
        {
            if ((game != null || w.Launch.Running || w.Launch.Busy) && DateTime.Now < until) { Ui.After(200, Wait); return; }
            if (game != null) { Log.Write($"home menu: \"{g.Name}\" did not close; not opening {e.Name}"); return; }
            EmuOpen.Start(w, e, null);
        }
        Ui.After(200, Wait);
    }

    // Close the game and start it again (tweaks that need a fresh start). Waits up to 20 s for the close.
    public void Restart()
    {
        var g = game;
        if (g == null) return;
        Log.Write($"home menu: restarting \"{g.Name}\"");
        CloseGame();
        var until = DateTime.Now.AddSeconds(20);
        void Wait()
        {
            if ((game != null || w.Launch.Running || w.Launch.Busy) && DateTime.Now < until) { Ui.After(200, Wait); return; }
            if (game != null) { Log.Write($"home menu: \"{g.Name}\" did not close; not restarting"); return; }
            Ui.After(300, () => { Ports.SkipAsk = g.Id; w.Home?.StartGame(g); });   // the same program again, not asked
        }
        Ui.After(200, Wait);
    }

    IntPtr SafeMainWindow() { try { proc.Refresh(); return proc.MainWindowHandle; } catch { return IntPtr.Zero; } }

    // ---- keyboard: the Home menu key (LauncherKeys; Alt by default) ----
    // Caught by HomeKeys while the game is played and in front (the launcher with the game inside it, or
    // the emulator's own window), so the game never gets it. With the menu up or the paused game on the Home
    // screen (the launcher in front) it resumes the game; then a key that is also a launcher key (Esc as
    // both Back and Home menu) is left to the launcher. Any other time (another program in front) it is free.
    public void KeysChanged() { homeVks = System.Array.Empty<int>(); UpdateEsc(); }

    public static bool? TestFront;   // test scripts: true = the launcher is in front, false = nothing is, null = the real front
    void UpdateEsc()
    {
        if (w.Hwnd == IntPtr.Zero) return;
        var fg = TestFront switch { true => w.Hwnd, false => IntPtr.Zero, _ => Native.GetForegroundWindow() };
        SyncPanning(fg);
        bool on = game != null && (proc != null || core != null);
        if (on && fg != lastFg) { lastFg = fg; if (fg != w.Hwnd && (emb == null || fg != emb.Child)) Log.Write($"home menu: in front: {Front()}"); }
        // Embedded: the launcher, the game window, or any other window of the emulator. With a game on
        // Lichii 366 the Home menu key was never caught while it played; the window in front
        // (logged above) was likely another of Lichii's windows.
        bool playing = on && !Paused && card == null && (emb != null
            ? w.TopScreen == embScreen && (fg == w.Hwnd || fg == emb.Child || GameInFront(fg))
            : core != null ? w.TopScreen is CoreScreen && fg == w.Hwnd
            : GameInFront(fg));   // an emulator played on its own (its "Play inside Ionea" is off): while its window is in front
        // the menu itself, or the paused game's Home screen (not a card over them: one may want the key)
        bool menu = on && !playing && fg == w.Hwnd && ((card != null && w.TopScreen == card) || (Paused && w.TopScreen == w.Home));
        var vks = playing ? LauncherKeys.HomeVks(w.Settings.Input, true) : menu ? LauncherKeys.HomeVks(w.Settings.Input, false) : System.Array.Empty<int>();
        if (System.Linq.Enumerable.SequenceEqual(vks, homeVks)) return;
        homeVks = vks;
        w.HomeKeys.Set(this, vks, null, Pressed);
        string name = LauncherKeys.Names(w.Settings.Input, LauncherKeys.Home);
        Log.Write(vks.Length == 0 ? "home menu: Home menu key released"
            : playing ? $"home menu: {name} opens the menu (game in front)" : $"home menu: {name} resumes the game (menu up)");
    }

    bool GameInFront(IntPtr fg)
    {
        if (fg == IntPtr.Zero || Native.GetWindowThreadProcessId(fg, out uint pid) == 0) return false;
        try { return pid == proc.Id || (GameProc() is Process gp && pid == gp.Id); } catch { return false; }
    }


    // The port's game program (found once it runs; not embedded ones are looked up here).
    Process GameProc()
    {
        if (gameProc != null) { try { if (!gameProc.HasExited) return gameProc; } catch { /* already ended */ } gameProc = null; }
        if (proc == null || Ports.GameProgram(w, game) is not string name) return null;
        try { gameProc = ProcTree.First(proc.Id, name); } catch (Exception caught) { Log.Debug("home menu: find game process", caught); }
        return gameProc;
    }

    // Freezes the game: the started process and the port's game program. 0 = done (NTSTATUS of the first failure).
    int SuspendGame()
    {
        int r = NtSuspendProcess(proc.Handle);
        if (r == 0 && GameProc() is Process gp)
            try { int r2 = NtSuspendProcess(gp.Handle); if (r2 != 0) { NtResumeProcess(proc.Handle); return r2; } Log.Write($"pause: {gp.ProcessName} ({gp.Id}) paused too"); }
            catch (Exception e) { Log.Write("pause game program: " + e.Message); }
        return r;
    }

    void ResumeProcs()
    {
        try { if (gameProc != null && !gameProc.HasExited) NtResumeProcess(gameProc.Handle); } catch (Exception e) { Log.Write("resume game program: " + e.Message); }
        try { NtResumeProcess(proc.Handle); } catch (Exception e) { Log.Write("resume: " + e.Message); }
    }

    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, int msg, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint type);
    [DllImport("user32.dll")] static extern bool ClipCursor(IntPtr rect);
    struct Pt { public int X, Y; }
    [DllImport("user32.dll")] static extern bool GetCursorPos(out Pt p);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time);
    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("ntdll.dll")] static extern int NtSuspendProcess(IntPtr h);
    [DllImport("ntdll.dll")] static extern int NtResumeProcess(IntPtr h);
}

// How the Home menu holds a standalone emulator. Freezing the whole program (NtSuspendProcess,
// undocumented) can upset sound and controller drivers mid-game, so it is only done when the user turns on
// Settings > Look > In games > Freeze the emulator. Otherwise the emulator's own pause key from its definition
// (hotkeys.pause, a toggle posted on open and again on resume), else it keeps running behind the menu, out of
// focus. Hosted cores pause inside Ionea and never come here.
public enum PauseWay { Freeze, Key, Unfocus }
public static class PauseLogic
{
    public static PauseWay Decide(bool freeze, string pauseKey) =>
        freeze ? PauseWay.Freeze : !string.IsNullOrWhiteSpace(pauseKey) ? PauseWay.Key : PauseWay.Unfocus;
}

// The in-game menu (Esc / Home / View + Start). A header with the game, then three groups: Resume;
// Save states and Game settings, each a page of its own; Home screen, Restart and Close game. A page
// slides in over the main list and B goes back to it. A group with only one thing in it (a game whose
// only setting is its controls) shows that thing straight on the main list.
public sealed class HomeMenuCard : MenuCard
{
    readonly GameMenu m;
    readonly Game g;
    readonly StackPanel list = new();
    readonly TextBlock crumb;
    readonly TranslateTransform slide = new();
    string page = "main";
    public string Page => page;

    public HomeMenuCard(MainWindow w, GameMenu menu, Game game, string page = null, int focus = 0) : base(w, null, 940)
    {
        m = menu; g = game;
        Box.Padding = new Thickness(40, 36, 40, 34);

        // header: the cover, the name, "Paused"
        var h = new Grid { Margin = new Thickness(0, 0, 0, 22) };
        h.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        h.ColumnDefinitions.Add(new ColumnDefinition());
        var cover = new Border { Width = 112, Height = 112, CornerRadius = new CornerRadius(20), Background = CoverColour.Fill(g), Margin = new Thickness(0, 0, 26, 0), ClipToBounds = true };
        string file = w.Lib.CoverPath(g);
        if (file != null && System.IO.File.Exists(file) && Art.LoadCover(file, CoverView.Zoom(g) > 1.001 ? 0 : 224) is System.Windows.Media.Imaging.BitmapSource bmp) cover.Background = CoverView.Brush(bmp, g);
        else cover.Child = new Viewbox { Margin = new Thickness(26), Child = Art.Icon("play", 48, CoverColour.Ink2(g)) };
        h.Children.Add(cover);
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var name = Fonts.Text(g.Name, 42, P.Text, FontWeights.Normal, Fonts.Display);
        names.Children.Add(name);
        var status = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 6, 0, 0) };
        status.Children.Add(new Border
        {
            Background = P.RowFocus, CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 3, 14, 5),
            Child = Fonts.Text(T("home.paused"), 22, P.Accent, FontWeights.SemiBold),
        });
        crumb = Fonts.Text("", 24, P.Text2);
        crumb.VerticalAlignment = VerticalAlignment.Center; crumb.Margin = new Thickness(16, 0, 0, 2);
        status.Children.Add(crumb);
        names.Children.Add(status);
        Grid.SetColumn(names, 1); h.Children.Add(names);
        Body.Children.Add(h);

        list.RenderTransform = slide;
        Body.Children.Add(list);
        RowHost = list;
        Go(page ?? "main", 0, focus);
    }

    // What the game has in each group.
    List<(string label, string icon, Func<string> value, Action act)> SettingsItems()
    {
        var l = new List<(string, string, Func<string>, Action)>();
        if (InputMap.Managed(w, g))
            l.Add((T("settings.controls"), "controller", null, () => Leave("settings", () => ControlsCard.OpenFor(w, g, true))));
        if (Tweaks.For(w, g) is TweakSet ts)
            l.Add((T("settings.game_settings"), "tune", null, () => Leave("settings", () => new TweaksCard(w, g, ts, true).Open())));
        if (g.Source == "rom")
            l.Add((T("settings.display"), "screen", () => DisplayLogic.Summary(w, g), () => Leave("settings", () => new DisplayCard(w, g, true).Open())));   // Display.cs
        if (m.Core is CoreSession s)
            l.Add((T("menu.emulator_settings"), "gear", () => s.CoreName, () => Leave("settings", () => new CoreOptionsCard(w, s).Open())));
        // Last: the game closes the usual way, then the emulator's own window opens inside Ionea's (EmuOpen).
        else if (Ports.Native(w, g) == null && w.Emus.Get(g.Emulator) is Emulator oe && !oe.IsCore && oe.Present)
            l.Add((T("settings.open_the_emulator"), "gear", () => oe.Name, () => { Close(false); m.OpenEmulator(); }));
        return l;
    }

    // Open a card over the game (the menu closes; that card brings it back on this page when it closes).
    void Leave(string back, Action open)
    {
        m.ReturnTo(page == "main" ? "main" : back, Focus);
        CloseQuiet(); m.CardClosed(this);
        open();
    }

    void Go(string to, int dir, int focus = 0)
    {
        page = to;
        ClearRows();
        var set = SettingsItems();
        switch (to)
        {
            case "states":
                crumb.Text = T("menu.save_states");
                if (m.Core is CoreSession s)
                {
                    WithIcon(AddRow(T("menu.save_state"), activate: () => s.SaveState(ok =>
                    {
                        w.Toasts.Show(ok ? T("menu.state_saved") : T("menu.couldn_t_save_the_state"), g.Name, ok ? "info" : "error");
                        Refresh();
                    })), "save");
                    WithIcon(AddRow(T("menu.load_state"), () => s.HasState ? "" : T("menu.none_yet"), activate: () =>
                    {
                        if (!s.HasState) { w.Toasts.Show(T("menu.no_saved_state_yet"), g.Name, "info"); return; }
                        s.LoadState(ok =>
                        {
                            w.Toasts.Show(ok ? T("menu.state_loaded") : T("menu.couldn_t_load_the_state"), g.Name, ok ? "info" : "error");
                            if (ok) m.Resume(true);
                        });
                    }), "load");
                }
                break;
            case "players":
                // Which controller plays as which player (PlayerSlots.cs): Left / Right pick another pad; a pad that
                // had another player swaps with it. The keyboard always plays as Player 1 too.
                crumb.Text = T("menu.players");
                if (m.Core is CoreSession ps)
                {
                    ps.RefreshPads();
                    for (int i = 0; i < PlayerSlots.Max; i++)
                    {
                        int slot = i;
                        WithIcon(AddRow(T("menu.player", slot + 1), () => PlayerPad(ps, slot), d => { ps.RefreshPads(); ps.Players.Move(slot, d, ps.PadKeys); Refresh(); }), "controller");
                    }
                }
                break;
            case "settings":
                crumb.Text = T("settings.game_settings");
                foreach (var (label, icon, value, act) in set) WithIcon(AddRow(label, value, activate: act), icon);
                break;
            default:
                page = "main";
                crumb.Text = "";
                WithIcon(AddRow(T("home.resume"), activate: () => m.Resume(true)), "play");
                bool states = m.Core != null, settings = set.Count > 0;
                Divider();
                if (states) Opens(WithIcon(AddRow(T("menu.save_states"), activate: () => Go("states", 1)), "states"));
                if (m.Core is CoreSession pc) { pc.RefreshPads(); if (pc.PadKeys.Count > 0) Opens(WithIcon(AddRow(T("menu.players"), () => PlayerCount(pc), activate: () => Go("players", 1)), "controller")); }
                if (set.Count == 1) WithIcon(AddRow(set[0].label, set[0].value, activate: set[0].act), set[0].icon);
                else if (settings) Opens(WithIcon(AddRow(T("settings.game_settings"), activate: () => Go("settings", 1)), "tune"));
                Opens(WithIcon(AddRow(T("settings.game_info"), activate: () => Leave("main", () => new GameInfoCard(w, g, true).Open())), "info"));
                // Ionea's own performance overlay (Overlay.cs): Left / Right switch Off / Simple / Detailed for this
                // game; A opens its corner, size and opacity.
                if (OverlayLogic.Available(OverlayLogic.Kind(w, g)))
                    Opens(WithIcon(AddRow(T("settings.performance_overlay"), () => OverlayLogic.Summary(w, g), d => { OverlayLogic.Step(w, g, d); Refresh(); },
                        activate: () => Leave("main", () => new OverlayCard(w, g, true).Open())), "gauge"));
                Divider();
                WithIcon(AddRow(T("settings.home_screen"), activate: () =>
                {
                    Close(false);
                    m.Core?.Hide();
                    m.StepAside();
                    w.Toasts.Show(T("menu.is_paused", g.Name), T("menu.pick_another_game_to_switch_to"), "info");
                }), "home");
                if (m.Core is CoreSession cs) WithIcon(AddRow(T("menu.restart_the_game"), activate: () => { cs.Reset(); m.Resume(true); }), "restart");
                // Emulators and ports (shadPS4, bbport, Lichii...): closed and started again, the same program, not asked.
                else WithIcon(AddRow(m.ControlsPending ? T("menu.restart_for_the_new_controls") : T("menu.restart_the_game"), activate: () => { Close(false); m.Restart(); }), "restart");
                WithIcon(AddRow(T("menu.close_game"), activate: () => { Close(false); m.CloseGame(); }), "close");
                break;
        }
        Focus = Math.Clamp(focus, 0, Math.Max(0, Rows.Count - 1));
        if (dir != 0)
        {
            Anim.FromTo(slide, TranslateTransform.XProperty, 46 * dir, 0, 240);
            Anim.FromTo(list, UIElement.OpacityProperty, 0, 1, 200);
        }
        Refresh();
    }

    // A player's controller by name (numbered when two plugged-in pads share a name); Player 1 with no pad is the keyboard.
    string PlayerPad(CoreSession s, int slot)
    {
        string key = s.Players[slot];
        if (key == null) return slot == 0 ? T("menu.keyboard") : T("common.none");
        string name = InputHub.PadName(key, w.Pads);
        var same = s.PadKeys.Where(k => InputHub.PadName(k, w.Pads) == name).ToList();
        return same.Count > 1 ? $"{name} {same.IndexOf(key) + 1}" : name;
    }

    static string PlayerCount(CoreSession s)
    {
        int n = s.Players.Snapshot().Count(k => k != null);
        return n == 1 ? "1 controller" : $"{n} controllers";
    }

    // The main list's row for a page (to land on it when coming back).
    int RowOf(string label) => Math.Max(0, Rows.FindIndex(r => ((r.El.Child as Grid)?.Children[0] as TextBlock)?.Text is string t && (t == label || Loc.ToEnglish(t) == label)));

    public override void OnAct(Act a)
    {
        if (a == Act.Back || (a == Act.Left && page != "main" && page != "players"))   // Left on Players picks a pad
        {
            if (page == "main") { m.Resume(true); return; }
            string from = page == "states" ? T("menu.save_states") : page == "players" ? T("menu.players") : T("settings.game_settings");
            Sounds.Play("back");
            Go("main", -1);
            Focus = RowOf(from); Refresh();
            return;
        }
        if (a == Act.Right && Rows.Count > 0 && page == "main" && Rows[Focus].Change == null
            && Loc.ToEnglish(((Rows[Focus].El.Child as Grid)?.Children[0] as TextBlock)?.Text) is "Save states" or "Game settings" or "Players")
            a = Act.Accept;
        base.OnAct(a);
    }

    public override void Close(bool sound = true) { base.Close(sound); m.CardClosed(this); }
    public void CloseQuiet() => base.Close(false);
}
