#nullable disable
// A game played inside the launcher through a libretro core (see Libretro.cs). The core emulates on
// its own thread; the launcher draws the picture on its GameLayer, plays the sound, reads the pads and
// keyboard, and owns pausing (Home menu), save states and the core's settings.
// Battery saves are <game file>.sav next to the game file, the same file the standalone emulator uses.
// Cores that keep their own save files (DS, 3DS, GameCube/Wii) write them under Emulators\Cores\saves
// (melonDS DS: saves\melonDS DS\<game>.sav, Azahar: saves\Azahar\sdmc, ...).
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static Ionea.Loc;

namespace Ionea;

public sealed class CoreSession
{
    readonly MainWindow w;
    public readonly Game Game;
    public bool TouchMouse => InputMap.TouchMouse(w, Game);
    readonly string dll, rom;
    readonly Grid layer = new() { Background = Brushes.Black };
    readonly Image image = new() { Stretch = Stretch.Fill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    readonly CoreScreen screen;
    readonly ConcurrentQueue<Action<RetroCore>> jobs = new();
    readonly object frameLock = new();
    byte[] frame = Array.Empty<byte>();
    int fw, fh, fmt;
    bool dirty;
    WriteableBitmap bmp;
    int bmpW, bmpH, bmpFmt;
    Thread thread;
    volatile bool stop, paused;
    RetroCore core;
    bool used;                                              // counted in CoreCatalog.InUse
    Dictionary<string, string> baseSaved = new();           // the core-wide settings file, without this game's tweaks
    public string CoreName = "core";
    public List<CoreOption> Options => core?.Options ?? new List<CoreOption>();
    public string Failed;
    public bool Shown { get; private set; }     // the first picture is on screen
    public bool Ended { get; private set; }
    public Action OnShown, OnEnded;
    public bool Paused => paused;
    // Frames the core has run, for the performance overlay (Overlay.cs): written on the core thread, no allocations.
    public readonly FrameStats Perf = new();
    public double TargetFps { get; private set; }

    public CoreSession(MainWindow win, Game g, string coreDll)
    {
        w = win; Game = g; dll = coreDll; rom = g.Rom;
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        layer.Children.Add(image);
        layer.Children.Add(veneer);   // the picture filter's pattern (PictureFilter.cs)
        screen = new CoreScreen(this);
        Nintendo = w.Settings.NintendoButtons;
        Scope = InputMap.ScopeFor(w, g);
        Map = InputMap.Snap(w.Settings.Input, Scope);
    }

    public InputMap.Snapshot Map;   // this game's controls (InputSetup.cs), read on the emulation thread
    public ControlScope Scope;      // whose controls: this game's console, or the game's own

    string SavePath() => w.Profiles.Active != null ? ProfileSaves.CoreSave(w.Profiles, rom) : Path.ChangeExtension(rom, ".sav");
    string StatePath => Path.Combine(w.Profiles.Active != null ? w.Profiles.StatesDir : Path.Combine(w.Lib.Dir, "states"), Path.GetFileNameWithoutExtension(rom) + ".state");
    string OptionsPath => Path.Combine(w.Lib.Dir, "core-options", Path.GetFileNameWithoutExtension(dll) + ".json");
    public bool HasState => File.Exists(StatePath);

    // Everything a core saves goes to the active profile's own folder (Profiles.cs).
    string SaveDir => w.Profiles.Active != null ? w.Profiles.CoreSaves : Path.Combine(Path.GetDirectoryName(dll), "saves");
    string BackupDir => w.Profiles.Active != null ? w.Profiles.BackupsDir : Path.Combine(w.Lib.Dir, "save-backups");

    public void Start()
    {
        w.PreviewMouseMove += Mouse; w.PreviewMouseDown += Mouse; w.PreviewMouseUp += Mouse;
        layer.Opacity = 0;
        w.GameLayer.Children.Add(layer);
        w.Push(screen);
        w.SizeChanged += Resized;
        CompositionTarget.Rendering += Draw;
        if (DirectPicture.Off(w.Settings.StreamingMode) == null && w.Hwnd != IntPtr.Zero) { direct = new DirectPicture(w.Hwnd); direct.Repaint = Redraw; }
        thread = new Thread(Loop, 16 * 1024 * 1024) { IsBackground = true, Name = "core", Priority = ThreadPriority.AboveNormal };
        thread.Start();
    }

    // ---- emulation thread ----
    void Loop()
    {
        WaveOut wave = null;      // the old way, only when the low-latency output can't open
        CoreSound sound = null;
        byte[] lastSave = null;
        string savePath = SavePath();
        // Paths are taken now: switching profile is blocked while a game is open anyway.
        string saveDir = SaveDir, backupDir = BackupDir;
        if (w.Profiles.Active != null) { Directory.CreateDirectory(saveDir); ProfileSaves.TakeBesideSave(w.Profiles, rom, savePath); }
        try
        {
            // Direct2D made while the game loads (it takes a while), not when its first frame is drawn straight.
            if (direct != null) { var pt = Stopwatch.StartNew(); direct.Prepare(); Log.Write($"core picture: Direct2D ready in {pt.ElapsedMilliseconds} ms{(direct.Broken ? " (failed)" : "")}"); }
            lock (CoreCatalog.Gate) { CoreCatalog.InUse(dll, +1); used = true; core = new RetroCore(dll, Path.Combine(Path.GetDirectoryName(dll), "system"), saveDir); }
            CoreName = core.Name;
            baseSaved = Json.Load<Dictionary<string, string>>(OptionsPath);
            core.Saved = new Dictionary<string, string>(baseSaved);
            Quiet.Core(dll, core.Saved);   // the core's own on-screen messages off (Quiet.cs); a game's tweaks still win
            // This game's own tweaks (Tweaks.cs) win over the core's settings, for this game only.
            if (Game.Tweaks != null) foreach (var kv in Game.Tweaks) core.Saved[kv.Key] = kv.Value;
            core.OnVideo = Video;
            core.OnInput = (port, id) => port >= 0 && port < PlayerSlots.Max && id < 16 && (btn[port] & (1 << id)) != 0 ? (short)1 : (short)0;   // port N: player N (PlayerSlots.cs)
            core.OnAnalog = Analog;
            core.OnPointer = id => id switch { 0 => (short)px, 1 => (short)py, 2 => (short)(touching ? 1 : 0), _ => (short)0 };
            core.OnPoll = Poll;
            // A core's on-screen messages go to the log only, never on top of the game.
            core.OnMessage = m => { if (!string.IsNullOrWhiteSpace(m)) Log.Write($"core {CoreName}: {m}"); };
            core.Init();
            core.LoadGame(rom);
            Log.Write($"core {core.Name} {core.Version}: \"{Game.Name}\" {core.Av.Geometry.BaseWidth}x{core.Av.Geometry.BaseHeight} {core.Av.Timing.Fps:0.##} fps, sound {core.Av.Timing.SampleRate:0} Hz{(core.Hardware ? $", drawn with {core.GpuApi}" : "")}");
            CoreCatalog.Remember(dll, core.Options);
            if (Game.Tweaks?.Count > 0) Log.Write($"core: this game's tweaks: {string.Join(", ", Game.Tweaks.Select(kv => kv.Key + "=" + kv.Value))}");
            if (core.Options.Count > 0) Log.Write($"core {core.Name}: {core.Options.Count} settings ({string.Join(", ", core.Options.Take(40).Select(o => o.Key + "=" + o.Value))})");
            smooth = PictureFilters.Smooth(PictureFilters.For(w, Game, core.Hardware));   // until Relayout applies the whole filter

            // Battery save: copy the file into the game's memory; keep a dated backup of it first.
            var ram = core.SaveRam;
            if (File.Exists(savePath) && ram.Length > 0)
            {
                var b = File.ReadAllBytes(savePath);
                b.AsSpan(0, Math.Min(b.Length, ram.Length)).CopyTo(ram);
                lastSave = ram.ToArray();
                try
                {
                    string bak = backupDir;
                    Directory.CreateDirectory(bak);
                    string to = Path.Combine(bak, $"{Path.GetFileNameWithoutExtension(rom)} {DateTime.Now:yyyy-MM-dd}.sav");
                    if (!File.Exists(to)) File.Copy(savePath, to);
                }
                catch (Exception e) { Log.Write("save backup: " + e.Message); }
                Log.Write($"core: loaded save {Path.GetFileName(savePath)} ({b.Length} bytes)");
            }
            else lastSave = ram.ToArray();

            double fps = core.Av.Timing.Fps > 1 ? core.Av.Timing.Fps : 60;
            TargetFps = fps;
            if ((!w.Opt.Mute || w.Opt.GameSound) && core.Av.Timing.SampleRate > 1000)
            {
                try { sound = new CoreSound(core.Av.Timing.SampleRate, fps); core.OnAudio = sound.Write; }
                catch (Exception e)
                {
                    Log.Write("core sound: no low-latency output (" + e.Message + "), using waveOut");
                    try { wave = new WaveOut((int)Math.Round(core.Av.Timing.SampleRate)); core.OnAudio = wave.Write; }
                    catch (Exception e2) { Log.Write("core sound: " + e2.Message); }
                }
            }
            timeBeginPeriod(1);
            timer = CreateWaitableTimerExW(IntPtr.Zero, IntPtr.Zero, 2, 0x1F0003);   // CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, all access
            var clock = Stopwatch.StartNew();
            long frames = 0, total = 0, synced = 0, fastFrames = 0, backSteps = 0; double t0 = 0, played = 0, since = 0; bool wasPaused = false, onDisplay = false;
            int wasMode = 0;   // 0 playing, 1 fast-forward, 2 rewinding
            string paceNow = null;
            var audio = core.OnAudio;
            // Rewind (Rewind.cs): the states kept, or why there are none.
            var play = w.Settings.Play;
            string noRewind = !play.Rewind ? "turned off" : core.Hardware && !play.Rewind3D ? "a game drawn with the GPU (Rewind in 3D games is off)"
                : (core.Quirks & 1) != 0 ? "the core says its states are incomplete" : null;
            if (noRewind != null) Log.Write("core rewind: off, " + noRewind);
            RewindBuffer rewind = null;
            byte[] state = null;
            int every = 1, noState = 0, failed = 0, slow = 0;
            // A state for rewind, every few frames. Some cores have no state for the first frames: they get two seconds.
            // made: the state run-ahead just made of this frame (RunAhead.cs), kept instead of making another.
            void Keep(int scale, byte[] made = null)
            {
                if (noRewind != null || total % (every * scale) != 0) return;
                if (made != null && rewind != null && state?.Length == made.Length) { rewind.Push(made); return; }
                int n = core.StateSize();
                if (n <= 0)
                {
                    if (++noState >= 120) { noRewind = "the core has no state"; rewind = null; Log.Write("core rewind: off, " + noRewind); }
                    return;
                }
                if (state == null || state.Length != n) { state = new byte[n]; every = RewindBuffer.Interval(n); }
                long t = Stopwatch.GetTimestamp();
                if (!core.SaveStateInto(state))
                {
                    if (++failed >= 3) { noRewind = "the core's state failed"; rewind = null; Log.Write("core rewind: off, " + noRewind); }
                    return;
                }
                failed = 0;
                if (rewind == null)
                {
                    rewind = new RewindBuffer(play.Mb << 20);
                    Log.Write($"core rewind: states of {n / 1024} KB every {every} frame{(every == 1 ? "" : "s")}, {play.Mb} MB");
                }
                rewind.Push(state);
                double ms = (Stopwatch.GetTimestamp() - t) * 1000.0 / Stopwatch.Frequency;
                if (ms > 4 && ++slow >= 10 && every < 8) { every *= 2; slow = 0; Log.Write($"core rewind: states take {ms:0.#} ms, now every {every} frames"); }
            }
            // Run-ahead (RunAhead.cs): set in Settings (read each frame, so a change shows at once); off for this game
            // when it can't, or gave up.
            string noAhead = RunAhead.Unfit(core.Hardware, core.Quirks), aheadNow = null;   // aheadNow: what was last logged
            byte[] ahead = null; bool aheadMade = false;
            int aheadNoState = 0;
            var budget = new AheadBudget();
            void GiveUp(string why, bool tell)
            {
                noAhead = why;   // logged by the loop
                if (tell) w.Dispatcher.BeginInvoke(() => w.Toasts.Show(T("menu.run_ahead_is_off_for_this"), why, "info"));
            }
            // One frame shown, n frames ahead: the real frame with its sound and no picture, its state kept, n more
            // frames with the same buttons (no sound, only the last one's picture), then the real frame's state back.
            void RunAheadFrame(int n, double frameMs)
            {
                int size = core.StateSize();
                if (size <= 0)
                {
                    core.Run();
                    if (++aheadNoState >= 120) GiveUp("the core has no state", false);
                    return;
                }
                long t = Stopwatch.GetTimestamp();
                core.AvEnable = 2 | 4; hideVideo = true;
                core.Run();
                size = core.StateSize();
                if (ahead == null || ahead.Length != size) ahead = new byte[size];
                if (size <= 0 || !core.SaveStateInto(ahead))
                {
                    core.AvEnable = 3; hideVideo = false;
                    GiveUp("the core's state failed", true);
                    return;
                }
                holdInput = true; core.OnAudio = null;
                try
                {
                    for (int i = 1; i <= n; i++)
                    {
                        bool last = i == n;
                        core.AvEnable = last ? 1 | 4 : 4; hideVideo = !last;
                        core.Run();
                    }
                }
                finally { holdInput = false; hideVideo = false; core.OnAudio = audio; }
                bool back = core.LoadState(ahead);
                core.AvEnable = 3;
                if (!back) { GiveUp("a state did not load", true); return; }
                aheadMade = true;
                double ms = (Stopwatch.GetTimestamp() - t) * 1000.0 / Stopwatch.Frequency;
                if (budget.Note(ms, frameMs)) GiveUp($"too slow on this PC ({ms:0.#} ms of a {frameMs:0.#} ms frame)", true);
            }
            // Waits until the clock reaches due (ms). A high-resolution timer when Windows has one: Sleep(1) can take
            // a whole 15.6 ms tick when Windows doesn't keep the 1 ms timer for the launcher, and the short sound
            // queue notices that.
            void Until(double due)
            {
                double ahead = due - clock.Elapsed.TotalMilliseconds;
                if (ahead > 0.3 && timer != IntPtr.Zero)
                {
                    long rel = -(long)(ahead * 10_000);   // relative, in 100 ns
                    if (SetWaitableTimer(timer, ref rel, 0, IntPtr.Zero, IntPtr.Zero, false)) WaitForSingleObject(timer, 1000);
                }
                while (clock.Elapsed.TotalMilliseconds < due - 1) Thread.Sleep(1);
                if (clock.Elapsed.TotalMilliseconds - due > 250) { t0 = clock.Elapsed.TotalMilliseconds; frames = 0; }   // fell behind: don't race
            }
            while (!stop)
            {
                while (jobs.TryDequeue(out var job)) { try { job(core); } catch (Exception e) { Log.Write("core job: " + e.Message); } }
                if (paused)
                {
                    if (!wasPaused)
                    {
                        wasPaused = true; fast = rewinding = false; Perf.Break(); played += clock.Elapsed.TotalSeconds - since; wave?.Pause(); sound?.Pause(); lastSave = WriteSave(savePath, lastSave);
                        if (wasMode != 0) { wasMode = 0; core.OnAudio = audio; }   // the Home menu ends fast-forward and rewind
                    }
                    Thread.Sleep(10);
                    continue;
                }
                if (wasPaused) { wasPaused = false; wave?.Restart(); sound?.Restart(); frames = 0; t0 = clock.Elapsed.TotalMilliseconds; since = clock.Elapsed.TotalSeconds; vsync?.Resync(); budget.Clear(); }
                // Rewinding: back one state (at the oldest, that one again), then one frame from it.
                if (wasMode == 2 && rewind != null)
                {
                    if (rewind.StepBack()) backSteps++;
                    if (!core.LoadState(rewind.Current)) { noRewind = "a state did not load"; rewind = null; rewinding = false; Log.Write("core rewind: off, " + noRewind); }
                }
                // Run-ahead only while playing: fast-forward and rewind win (RunAhead.cs).
                int aheadBy = RunAhead.Clean(play.RunAhead);
                bool aheadOn = RunAhead.Now(aheadBy, noAhead, wasMode != 0 || fast || rewinding ? 1 : 0);
                string an = aheadBy == 0 ? "0" : noAhead != null ? "no" : aheadBy.ToString();
                if (an != aheadNow)
                {
                    if (aheadNow != null || an != "0")
                        Log.Write(an == "0" ? "core run-ahead: turned off" : an == "no" ? "core run-ahead: off for this game, " + noAhead : $"core run-ahead: {RunAhead.Name(aheadBy)}");
                    aheadNow = an;
                }
                aheadMade = false;
                if (aheadOn) RunAheadFrame(aheadBy, 1000.0 / fps); else core.Run();
                Perf.Frame();
                if (shotWanted) { shotWanted = false; Screenshot(); }
                frames++; total++;
                if (frames % 300 == 0) lastSave = WriteSave(savePath, lastSave);
                if (core.Av.Timing.Fps > 1 && core.Av.Timing.Fps != fps) { fps = core.Av.Timing.Fps; TargetFps = fps; t0 = clock.Elapsed.TotalMilliseconds; frames = 0; }   // the core changed its rate
                // Fast-forward (FastForward.cs) and rewind (Rewind.cs), their keys read in Poll; rewind wins. Both
                // without sound, and the display's timing steps aside; both come back as after a pause.
                int mode = rewinding && rewind?.Current != null ? 2 : fast ? 1 : 0;
                if (mode != wasMode)
                {
                    if (wasMode == 0)
                    {
                        wave?.Pause(); sound?.Pause(); core.OnAudio = null;
                        if (onDisplay) { onDisplay = false; sound?.SetSkew(1); }
                    }
                    if (wasMode == 1) Log.Write($"core: fast-forward off after {fastFrames} frames");
                    if (wasMode == 2) Log.Write($"core: rewound {backSteps} states");
                    if (mode == 1) { fastFrames = 0; lastShown = double.MinValue; Log.Write($"core: fast-forward on ({FastForward.SpeedName(FastForward.Clean(play.FastSpeed))})"); }
                    if (mode == 2) backSteps = 0;
                    if (mode == 0) { core.OnAudio = audio; wave?.Restart(); sound?.Restart(); vsync?.Resync(); }
                    wasMode = mode;
                    frames = 0; t0 = clock.Elapsed.TotalMilliseconds;
                }
                if (mode == 1)
                {
                    fastFrames++;
                    int speed = FastForward.Clean(play.FastSpeed);
                    Keep(speed > 0 ? speed : 8);   // about as many states a second as when playing
                    double fm = FastForward.FrameMs(fps, speed);
                    if (fm > 0) Until(t0 + frames * fm);
                    continue;
                }
                if (mode == 2) { Until(t0 + frames * 1000.0 / fps); continue; }
                Keep(1, aheadMade ? ahead : null);

                // Keep time with the display when the core's rate is close to its refresh (DisplaySync.cs), else with
                // the clock. The low-latency sound follows the game either way (CoreSound.cs); only the old waveOut
                // way holds the game back by itself.
                bool displayed = false;
                if (wave == null && w.Settings.SyncToDisplay && w.Hwnd != IntPtr.Zero)
                {
                    vsync ??= new VBlankClock(w.Hwnd);
                    var plan = vsync.Ok ? FrameTiming.Decide(fps, vsync.Hz) : PacePlan.ClockAt(fps);
                    if (plan.Mode == Pace.Display)
                    {
                        if (!onDisplay) vsync.Resync();
                        onDisplay = true;
                        sound?.SetSkew(plan.Skew);
                        displayed = vsync.Wait(plan.Refreshes);   // false: no refresh in time, the clock this frame
                        if (displayed) { synced++; frames = 0; t0 = clock.Elapsed.TotalMilliseconds; }
                    }
                    else { onDisplay = false; sound?.SetSkew(1); }
                    Paced(ref paceNow, plan, fps, vsync);
                }
                else if (wave == null) { if (onDisplay) { onDisplay = false; sound?.SetSkew(1); } Paced(ref paceNow, PacePlan.ClockAt(fps), fps, null); }
                if (wave == null && !displayed) Until(t0 + frames * 1000.0 / fps);
            }
            lastSave = WriteSave(savePath, lastSave);
            if (!wasPaused) played += clock.Elapsed.TotalSeconds - since;
            Log.Write($"core: ran {total} frames in {played:0.0} s of play ({(played > 0 ? total / played : 0):0.0} fps, {(wave != null ? "paced by sound" : synced > 0 ? $"paced by the display for {synced * 100.0 / Math.Max(1, total):0} %, the rest by clock" : "paced by clock")})");
        }
        catch (Exception e)
        {
            Failed = e.Message;
            Log.Write($"core \"{Game.Name}\": {e}");
        }
        finally
        {
            try { wave?.Dispose(); } catch { /* best-effort cleanup */ }
            try { sound?.Dispose(); } catch { /* best-effort cleanup */ }
            try { vsync?.Dispose(); } catch { /* best-effort cleanup */ }
            try { core?.Dispose(); } catch (Exception e) { Log.Write("core close: " + e.Message); }
            if (used) CoreCatalog.InUse(dll, -1);
            timeEndPeriod(1);
            if (timer != IntPtr.Zero) { CloseHandle(timer); timer = IntPtr.Zero; }
            w.Dispatcher.BeginInvoke(Finished);
        }
    }

    VBlankClock vsync;   // the refreshes of the monitor the launcher is on (DisplaySync.cs), made when first needed

    // Logs the way the core keeps time when it changes.
    static void Paced(ref string now, PacePlan plan, double fps, VBlankClock vs)
    {
        string key = plan.Mode == Pace.Display ? $"d{plan.Refreshes}:{plan.Rate:0.###}" : vs == null ? "off" : vs.Ok ? $"c{fps:0.###}:{vs.Hz:0.###}" : "none";
        if (key == now) return;
        now = key;
        Log.Write(plan.Mode == Pace.Display
            ? $"core timing: matched to the display, {plan.Rate:0.###} fps ({fps:0.###} fps game on {vs.Hz:0.###} Hz, {plan.Refreshes} refresh{(plan.Refreshes == 1 ? "" : "es")} a frame, game and sound {(plan.Skew - 1) * 100:+0.##;-0.##;0} %)"
            : vs == null ? "core timing: by the clock (Match the display is off)"
            : vs.Ok ? $"core timing: by the clock ({fps:0.###} fps game, {vs.Hz:0.###} Hz display: not close enough to match)"
            : "core timing: by the clock (the display's refresh timing is not known or not steady yet)");
    }

    // Writes the battery save when the game changed it (to a temporary file first, then swapped in).
    byte[] WriteSave(string path, byte[] last)
    {
        var ram = core.SaveRam;
        if (ram.Length == 0 || (last != null && ram.SequenceEqual(last))) return last;
        var now = ram.ToArray();
        try
        {
            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, now);
            File.Move(tmp, path, true);
            Log.Write($"core: saved {Path.GetFileName(path)}");
            return now;
        }
        catch (Exception e) { Log.Write("core save: " + e.Message); return last; }
    }

    void Video(IntPtr data, int width, int height, int pitch)
    {
        if (data == IntPtr.Zero || width <= 0 || height <= 0 || hideVideo) return;   // same picture as before; a frame run-ahead doesn't show
        if (fast && !rewinding)   // fast-forward: about 60 pictures a second, the other frames are not copied
        {
            double now = Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
            if (!FastForward.Show(now, lastShown)) return;
            lastShown = now;
        }
        int bpp = core.PixelFormat == 1 ? 4 : 2, row = width * bpp;
        lock (frameLock)
        {
            if (frame.Length != row * height) frame = new byte[row * height];
            FrameRows.Copy(data, pitch, frame, row, height);   // a negative pitch (OpenGL, GlHost.cs) turns it the right way up here
            fw = width; fh = height; fmt = core.PixelFormat; dirty = true;
            if (LatencyProbe.On && LatencyProbe.Frame(frame[0] != 0 || frame[1] != 0)) probeFrame = true;
            // Straight to the screen while nothing is over the picture (DirectPicture.cs); WPF's copy is still made.
            var d = direct;
            if (d != null && d.Live) { d.Submit(frame, width, height, fmt, smooth, probeFrame); probeFrame = false; }
        }
    }

    // ---- input (emulation thread, once per frame) ----
    // Each player's pad goes to its own port (PlayerSlots.cs); the keyboard and the mouse are Player 1's too.
    public readonly PlayerSlots Players = new();
    public IReadOnlyList<string> PadKeys { get; private set; } = Array.Empty<string>();   // pads plugged in at the last poll
    readonly int[] btn = new int[PlayerSlots.Max];             // buttons held, per port
    readonly int[] axis = new int[PlayerSlots.Max * 6];        // per port: sticks (libretro range, y down) lx ly rx ry, triggers lt rt
    readonly int[] nextBtn = new int[PlayerSlots.Max], nextAxis = new int[PlayerSlots.Max * 6];
    bool probeFrame;   // tests: the frame waiting to be drawn is the first with the press (LatencyProbe)
    // Fast-forward (FastForward.cs), emulation thread: on, its key's state at the last poll, when a picture was last
    // shown and when the pads were last read while fast (ms).
    bool fast, fastDown; double lastShown = double.MinValue, lastPadRead;
    bool rewinding;   // rewind's key is held (Rewind.cs), emulation thread
    bool shotDown, shotWanted;   // the screenshot key: held at the last poll, newly pressed
    bool hideVideo, holdInput;   // run-ahead (RunAhead.cs), emulation thread: a frame whose picture isn't shown; the buttons kept as polled
    volatile int px, py; volatile bool touching;  // mouse on the picture, as a touch screen
    volatile bool smooth;                         // scale smoothly, not in whole steps (the picture filter, PictureFilter.cs)

    // UI thread (the Home menu): the pads plugged in now, also while the game is paused and not polling.
    public void RefreshPads()
    {
        if (!Map.PadOn) return;
        var keys = w.Input.KeyedStates().Select(p => p.Key).ToArray();
        Players.Update(keys);
        PadKeys = keys;
    }

    short Analog(int port, int index, int id)
    {
        if (port < 0 || port >= PlayerSlots.Max) return 0;
        int o = port * 6;
        return (index, id) switch
        {
            (0, 0) => (short)axis[o], (0, 1) => (short)axis[o + 1],
            (1, 0) => (short)axis[o + 2], (1, 1) => (short)axis[o + 3],
            (2, 12) => (short)axis[o + 4], (2, 13) => (short)axis[o + 5],
            (2, _) => (short)(id < 16 && (btn[port] & (1 << id)) != 0 ? 0x7fff : 0),
            _ => 0,
        };
    }

    static int Stick(short v, bool flip)
    {
        int x = flip ? -Math.Max((int)v, -32767) : Math.Max((int)v, -32767);
        return Math.Abs(x) < 7849 ? 0 : x;   // XInput's usual dead zone
    }

    // UI thread: the mouse over the picture is the touch screen (DS lower screen, 3DS lower screen).
    void Mouse(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (image.ActualWidth <= 0 || image.ActualHeight <= 0 || layer.Visibility != Visibility.Visible) return;
        if (!w.Settings.Input.MouseOn || !w.Settings.Input.Touch) { touching = false; return; }
        var pos = e.GetPosition(image);
        double fx = pos.X / image.ActualWidth, fy = pos.Y / image.ActualHeight;
        bool inside = fx >= 0 && fx <= 1 && fy >= 0 && fy <= 1;
        px = (int)Math.Round(Math.Clamp(fx, 0, 1) * 0xfffe - 0x7fff);
        py = (int)Math.Round(Math.Clamp(fy, 0, 1) * 0xfffe - 0x7fff);
        touching = inside && e.LeftButton == System.Windows.Input.MouseButtonState.Pressed;
    }
    public bool Nintendo;   // Xbox A/B swapped to match the Nintendo positions

    void Poll()
    {
        if (holdInput) return;   // run-ahead's frames ahead: the same buttons as the real frame
        // While fast-forwarding the core runs many frames a second: the pads (XInput is slow on empty slots) are
        // read every 4 ms at most, and the buttons held stay as they were in between.
        double nowMs = Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
        if (fast && nowMs - lastPadRead < 4) return;
        lastPadRead = nowMs;
        bool fastKey = false, backKey = false, shotKey = false;
        Array.Clear(nextBtn); Array.Clear(nextAxis);
        // A console without a stick, or a core that hasn't read one yet: the left stick moves the d-pad.
        var map = Map;
        bool stickIsPad = !map.HasStick || core == null || !core.UsesAnalog;
        bool front = Native.GetForegroundWindow() == w.Hwnd;
        // Pads are counted in front or not, so a pad unplugged behind another window frees its slot too.
        var pads = map.PadOn ? w.Input.KeyedStates() : new List<(string Key, Native.XINPUT_GAMEPAD State)>();
        var keys = new string[pads.Count];
        for (int i = 0; i < pads.Count; i++) keys[i] = pads[i].Key;
        if (Players.Update(keys)) Log.Write("core players: " + string.Join(", ", Players.Snapshot().Select((k, i) => $"P{i + 1} {(k == null ? "none" : InputHub.PadName(k, w.Pads))}")));
        PadKeys = keys;
        if (front)
        {
            foreach (var (key, p) in pads)
            {
                int port = Players.SlotOf(key);
                if (port < 0) continue;   // a fifth pad: no player for it
                int m = 0, o = port * 6;
                ushort b = p.wButtons;
                bool home = (b & 0x30) == 0x30;
                nextAxis[o] = Stick(p.sThumbLX, false); nextAxis[o + 1] = Stick(p.sThumbLY, true);
                nextAxis[o + 2] = Stick(p.sThumbRX, false); nextAxis[o + 3] = Stick(p.sThumbRY, true);
                nextAxis[o + 4] = p.bLeftTrigger * 0x7fff / 255; nextAxis[o + 5] = p.bRightTrigger * 0x7fff / 255;
                if (stickIsPad && p.sThumbLY > 16000) m |= 1 << 4;
                if (stickIsPad && p.sThumbLY < -16000) m |= 1 << 5;
                if (stickIsPad && p.sThumbLX < -16000) m |= 1 << 6;
                if (stickIsPad && p.sThumbLX > 16000) m |= 1 << 7;
                if (home) b &= unchecked((ushort)~0x30);   // View + Start together is the Home menu, not the game's
                // Player 1's View + RB is fast-forward (LauncherKeys.PadCombo), not the game's.
                ushort ff = LauncherKeys.PadMask(LauncherKeys.Fast);
                ushort rw = LauncherKeys.PadMask(LauncherKeys.Rewind);   // and View + LB rewind
                bool view = port == 0 && (b & 0x20) != 0;
                if (view && (b & ff) != 0) { fastKey = true; b &= unchecked((ushort)~(0x20 | ff)); }
                if (view && (b & rw) != 0) { backKey = true; b &= unchecked((ushort)~(0x20 | rw)); }
                ushort sh = LauncherKeys.PadMask(LauncherKeys.Shot);     // and View + Y a screenshot
                if (view && (b & sh) != 0) { shotKey = true; b &= unchecked((ushort)~(0x20 | sh)); }
                // Settings > Input > Controller: each game button reads the pad button bound to it.
                foreach (var (mask, trig, retro) in map.Pad)
                {
                    bool down = trig == 1 ? p.bLeftTrigger > 100 : trig == 2 ? p.bRightTrigger > 100 : (b & mask) != 0;
                    if (!down) continue;
                    int id = retro;
                    if (Nintendo) id = id switch { 0 => 8, 8 => 0, 1 => 9, 9 => 1, _ => id };   // A/B and X/Y swapped to the Nintendo positions
                    m |= 1 << id;
                }
                nextBtn[port] |= m;
            }
            // Keys and mouse buttons (InputMap.Binds: already only what is turned on): Player 1.
            int k1 = 0;
            foreach (var (vk, id) in map.Buttons) if ((GetAsyncKeyState(vk) & 0x8000) != 0) k1 |= 1 << id;
            // Stick keys: full tilt, or the d-pad while the core doesn't read sticks.
            bool K(int i) => map.StickKeys[i] != 0 && (GetAsyncKeyState(map.StickKeys[i]) & 0x8000) != 0;
            int kx = (K(3) ? 1 : 0) - (K(2) ? 1 : 0), ky = (K(1) ? 1 : 0) - (K(0) ? 1 : 0);
            int rkx = (K(7) ? 1 : 0) - (K(6) ? 1 : 0), rky = (K(5) ? 1 : 0) - (K(4) ? 1 : 0);
            if (stickIsPad)
            {
                if (ky < 0) k1 |= 1 << 4; if (ky > 0) k1 |= 1 << 5; if (kx < 0) k1 |= 1 << 6; if (kx > 0) k1 |= 1 << 7;
            }
            else
            {
                if (kx != 0) nextAxis[0] = kx * 0x7fff; if (ky != 0) nextAxis[1] = ky * 0x7fff;
            }
            int arx = nextAxis[2], ary = nextAxis[3];
            if (rkx != 0) arx = rkx * 0x7fff; if (rky != 0) ary = rky * 0x7fff;
            if (map.MouseCam > 0) MouseLook(map.MouseCam, ref arx, ref ary); else camLive = false;
            nextAxis[2] = arx; nextAxis[3] = ary;
            nextBtn[0] |= k1;
            foreach (int vk in map.FastKeys) if ((GetAsyncKeyState(vk) & 0x8000) != 0) fastKey = true;
            foreach (int vk in map.RewindKeys) if ((GetAsyncKeyState(vk) & 0x8000) != 0) backKey = true;
            foreach (int vk in map.ShotKeys) if ((GetAsyncKeyState(vk) & 0x8000) != 0) shotKey = true;
        }
        else camLive = false;
        fast = FastForward.Next(fast, fastKey, fastDown, w.Settings.Play.FastToggle);
        fastDown = fastKey;
        rewinding = backKey;
        if (shotKey && !shotDown) shotWanted = true;
        shotDown = shotKey;


        int tb = LatencyProbe.Buttons;   // tests: a press from the script, in front or not
        if (tb != 0) LatencyProbe.Polled();
        nextBtn[0] |= tb;
        Array.Copy(nextBtn, btn, btn.Length);
        Array.Copy(nextAxis, axis, axis.Length);
    }


    // Mouse camera (InputMap.MouseCamOn): mouse movement tilts the right stick, by the PS4 curve. The cursor
    // (hidden in the game) goes back to the window's middle each frame, so it never stops at a screen edge.
    // Two frames' movement together, about shadPS4's 33 ms poll. Only while the launcher is in front.
    bool camLive; int camPx, camPy;
    void MouseLook(int level, ref int rx, ref int ry)
    {
        if (!Native.GetWindowRect(w.Hwnd, out var r) || !GetCursorPos(out var c)) { camLive = false; return; }
        int cx = (r.L + r.R) / 2, cy = (r.T + r.B) / 2;
        int dx = camLive ? c.X - cx : 0, dy = camLive ? c.Y - cy : 0;
        if (c.X != cx || c.Y != cy) SetCursorPos(cx, cy);
        if (!camLive) camPx = camPy = 0;
        camLive = true;
        int sx = dx + camPx, sy = dy + camPy;
        camPx = dx; camPy = dy;
        if (sx != 0) rx = InputMap.CoreMouseStick(level, sx);
        if (sy != 0) ry = InputMap.CoreMouseStick(level, sy);   // libretro: + is down, as the mouse
    }

    // ---- UI thread ----
    void Draw(object s, EventArgs e)
    {
        if (LatencyProbe.On) LatencyProbe.Tick();
        Steer();
        lock (frameLock)
        {
            if (!dirty) return;
            dirty = false;
            if (bmp == null || bmpW != fw || bmpH != fh || bmpFmt != fmt)
            {
                var pf = fmt == 1 ? PixelFormats.Bgr32 : fmt == 2 ? PixelFormats.Bgr565 : PixelFormats.Bgr555;
                bmp = new WriteableBitmap(fw, fh, 96, 96, pf, null);
                bmpW = fw; bmpH = fh; bmpFmt = fmt;
                image.Source = bmp;
                Relayout();
            }
            bmp.WritePixels(new Int32Rect(0, 0, fw, fh), frame, fw * (fmt == 1 ? 4 : 2), 0);
            if (probeFrame) { probeFrame = false; LatencyProbe.Drawn(); }
        }
        if (!Shown)
        {
            Shown = true;
            Anim.To(layer, UIElement.OpacityProperty, 1, 250);
            OnShown?.Invoke();
        }
    }

    // ---- the picture straight to the screen (DirectPicture.cs) ----
    DirectPicture direct;
    string directNow;
    readonly HashSet<string> directSaid = new();

    // UI thread, each WPF frame: the child window over the picture while nothing of the launcher's is over it,
    // WPF's own picture (always kept up to date) the rest of the time.
    void Steer()
    {
        if (direct == null || direct.Broken) return;
        var r = default((int X, int Y, int W, int H));
        string why = DirectPicture.Off(w.Settings.StreamingMode) ?? Covered(out r);   // streaming mode turned on mid-game: WPF from now on
        if (why != null) { direct.Hide(); Said(why); return; }
        direct.Veil = (float)w.Dim;
        if (direct.Show(r.X, r.Y, r.W, r.H) || !direct.Live) { Redraw(); direct.Live = true; }
        Said(null);
    }

    // The latest frame again (shown, moved or resized): copied first, so the core never waits on this drawing.
    void Redraw()
    {
        int rw, rh, rf;
        lock (frameLock)
        {
            if (redraw.Length != frame.Length) redraw = new byte[frame.Length];
            Buffer.BlockCopy(frame, 0, redraw, 0, frame.Length);
            rw = fw; rh = fh; rf = fmt;
        }
        direct?.Draw(redraw, rw, rh, rf, smooth);
    }
    byte[] redraw = Array.Empty<byte>();

    void Said(string why)
    {
        string k = why ?? "";
        if (k == directNow) return;
        directNow = k;
        if (directSaid.Add(k)) Log.Write(why == null ? "core picture: straight to the screen" : $"core picture: drawn by WPF ({why})");
    }

    // Why the child can't show the picture now (null: it can), and where it goes (device pixels in the window).
    string Covered(out (int X, int Y, int W, int H) r)
    {
        r = default;
        if (!Shown || Ended || fw <= 0 || image.ActualWidth <= 0 || image.ActualHeight <= 0) return "no picture yet";
        if (w.WindowState == WindowState.Minimized || !w.IsVisible) return "the window is hidden";
        // The picture and everything it sits in: all there, not fading, no effect over it.
        for (DependencyObject o = image; o != null; o = VisualTreeHelper.GetParent(o))
            if (o is UIElement u && (u.Visibility != Visibility.Visible || u.Opacity < 0.999 || u.Effect != null || u.OpacityMask != null || u.Clip != null))
                return "it is fading or hidden";
        // Nothing over it: the layers above the games' (menus, the overlay, toasts, ...) and anything after this game's layer.
        bool above = false;
        foreach (UIElement c in w.Stage.Children)
        {
            if (c == w.GameLayer) { above = true; continue; }
            if (above && Shows(c, 0)) return "something is over it: " + LayerName(c);
        }
        above = false;
        foreach (UIElement c in w.GameLayer.Children)
        {
            if (c == layer) { above = true; continue; }
            if (above && Shows(c, 0)) return "something is over it: the Home menu";
        }
        if (PresentationSource.FromVisual(w) is not System.Windows.Interop.HwndSource src || src.CompositionTarget == null) return "the window is hidden";
        if (image.TransformToAncestor(w) is not Transform t || Math.Abs(t.Value.M12) > 1e-9 || Math.Abs(t.Value.M21) > 1e-9) return "it is turned";
        var b = t.TransformBounds(new Rect(0, 0, image.ActualWidth, image.ActualHeight));
        var st = w.Stage.TransformToAncestor(w).TransformBounds(new Rect(0, 0, w.Stage.ActualWidth, w.Stage.ActualHeight));
        if (b.Left < st.Left - 0.5 || b.Top < st.Top - 0.5 || b.Right > st.Right + 0.5 || b.Bottom > st.Bottom + 0.5) return "it is larger than the screen";
        var m = src.CompositionTarget.TransformToDevice;
        int x0 = (int)Math.Round(b.Left * m.M11), y0 = (int)Math.Round(b.Top * m.M22), x1 = (int)Math.Round(b.Right * m.M11), y1 = (int)Math.Round(b.Bottom * m.M22);
        if (x1 - x0 < 2 || y1 - y0 < 2) return "it is too small";
        r = (x0, y0, x1 - x0, y1 - y0);
        return null;
    }

    // Shown at all: there, not see-through, and a panel only when something in it is (or it has a background).
    static bool Shows(UIElement e, int depth)
    {
        if (e.Visibility != Visibility.Visible || e.Opacity <= 0) return false;
        if (e is not Panel p || depth > 5) return true;
        if (p.Background != null && !(p.Background is SolidColorBrush sb && sb.Color.A == 0)) return true;
        foreach (UIElement c in p.Children) if (Shows(c, depth + 1)) return true;
        return false;
    }

    string LayerName(UIElement c) =>
        c == w.OverlayLayer ? "the performance overlay" : c == w.CardLayer ? "a menu" : c == w.CoachLayer ? "the tutorial" : c == w.OskLayer ? "the keyboard"
        : c == w.LaunchLayer ? "the launch effect" : c == w.Sleep?.Layer ? "the sleep dim" : c == w.ToastLayer ? "a message" : c == w.Mouse?.Layer ? "a click ring" : "a layer";

    // The picture's size and shape (Display.cs): Integer scale, Fit, Stretch or a custom size, at the
    // game's aspect ratio. While the game shows, the stage takes the window's shape, so the black around
    // the picture (and Stretch) reaches the window's edges on a screen or window that isn't 16:9.
    // Called when the core's picture size changes, the window is resized, and from the Display card (live).
    public void Relayout()
    {
        if (fh <= 0 || fw <= 0) return;
        var p = DisplayLogic.Effective(w, Game);
        var (cw, ch) = Client();
        FitStage(cw, ch);
        double sw = w.Stage.Width, sh = w.Stage.Height;
        double unit = cw > 0 && ch > 0 ? Math.Min(cw / sw, ch / sh) * VisualTreeHelper.GetDpi(w).DpiScaleY : 1;
        double nativeH = core?.Av.Geometry.BaseHeight > 0 ? core.Av.Geometry.BaseHeight : fh;
        double original = core?.Av.Geometry.AspectRatio > 0 ? core.Av.Geometry.AspectRatio
            : core?.Av.Geometry.BaseWidth > 0 && core.Av.Geometry.BaseHeight > 0 ? (double)core.Av.Geometry.BaseWidth / core.Av.Geometry.BaseHeight : (double)fw / fh;
        // DS / 3DS: both screens are one picture laid out by the core; its shape stays (it may still be stretched).
        double aspect = DisplayLogic.DualScreen(w, Game) ? original : DisplayLogic.AspectOf(p.Aspect, original, sw, sh);
        var size = DisplayLogic.Picture(p.Size, p.Custom ?? 50, aspect, nativeH, sw, sh, unit);
        image.Width = size.Width; image.Height = size.Height;
        Filter(unit);
        layoutNote = $"{p.Size} {aspect:0.###} native {nativeH} unit {unit:0.###} -> {size.Width:0.#}x{size.Height:0.#} of {sw:0}x{sh:0}";
        if (layoutNote != lastLogged) { lastLogged = layoutNote; Log.Write($"core picture: {layoutNote}"); }
    }
    string layoutNote = "", lastLogged;

    // The picture filter (PictureFilter.cs), UI thread, with the picture's size: how it scales, and the pattern over
    // it, in both paths (Direct2D: DirectPicture.Pattern; WPF: the tiled brush over the image). unit: screen pixels
    // per stage unit.
    void Filter(double unit)
    {
        string f = PictureFilters.For(w, Game, core?.Hardware ?? false);
        smooth = PictureFilters.Smooth(f);
        RenderOptions.SetBitmapScalingMode(image, smooth ? BitmapScalingMode.Linear : BitmapScalingMode.NearestNeighbor);
        int pat = PictureFilters.Pattern(f);
        if (direct != null) direct.Pattern = pat;
        double sx = image.Width * unit / fw, sy = image.Height * unit / fh;
        string key = $"{pat} {sx:0.###} {sy:0.###}";
        if (key != veneerKey)
        {
            veneerKey = key;
            var tile = PictureFilters.Tile(pat, sx, sy, out int tw, out int th);
            if (tile == null) veneer.Fill = null;
            else
            {
                var src = BitmapSource.Create(tw, th, 96, 96, PixelFormats.Pbgra32, null, tile, tw * 4);
                var brush = new ImageBrush(src) { TileMode = TileMode.Tile, Stretch = Stretch.Fill, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, image.Width / fw, image.Height / fh) };
                RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.Linear);
                brush.Freeze();
                veneer.Fill = brush;
            }
        }
        veneer.Width = image.Width; veneer.Height = image.Height;
        veneer.Visibility = veneer.Fill != null ? Visibility.Visible : Visibility.Collapsed;
        if (f != filterLogged) { filterLogged = f; Log.Write($"core picture: filter {PictureFilters.Name(f)}{(pat != 0 && veneer.Fill == null ? " (its pattern left out: the game's pixels are too small on this screen)" : "")}"); }
    }
    // The filter's pattern over the WPF picture (the Direct2D path draws its own), what it was made for, the filter last logged.
    readonly System.Windows.Shapes.Rectangle veneer = new() { IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    string veneerKey, filterLogged;

    void FitStage(double cw, double ch)
    {
        double sw = 1920, sh = 1080;
        if (cw > 0 && ch > 0 && layer.Visibility == Visibility.Visible && !Ended)
        {
            double a = cw / ch;
            if (Math.Abs(a - 16.0 / 9) > 0.005) { sw = Math.Max(1920, Math.Round(1080 * a)); sh = Math.Max(1080, Math.Round(1920 / a)); }
        }
        if (w.Stage.Width != sw || w.Stage.Height != sh) { w.Stage.Width = sw; w.Stage.Height = sh; }
    }
    // The window's inside, in WPF units. Not the Viewbox's own size: a Viewbox is as large as the stage it
    // scales, so it always had the stage's 16:9 shape and the stage never took a window's shape.
    (double, double) Client()
    {
        var vb = w.Content as FrameworkElement;
        if (vb != null && VisualTreeHelper.GetParent(vb) is FrameworkElement host && host.ActualWidth > 0 && host.ActualHeight > 0)
            return (host.ActualWidth, host.ActualHeight);
        return (vb?.ActualWidth ?? 0, vb?.ActualHeight ?? 0);
    }

    static void UnfitStage(MainWindow w) { if (w.Stage.Width != 1920 || w.Stage.Height != 1080) { w.Stage.Width = 1920; w.Stage.Height = 1080; } }

    void Resized(object s, SizeChangedEventArgs e) => w.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, Relayout);

    // The game's own size at 1x, in screen pixels (a window of 1x, 2x... GameWindow).
    public Size NativeSize
    {
        get
        {
            double h = core?.Av.Geometry.BaseHeight > 0 ? core.Av.Geometry.BaseHeight : fh > 0 ? fh : 240;
            double a = core?.Av.Geometry.AspectRatio > 0 ? core.Av.Geometry.AspectRatio : fw > 0 && fh > 0 ? (double)fw / fh : 4.0 / 3;
            return new Size(Math.Round(h * a), h);
        }
    }

    // For tests: which size the picture has on the stage: stretch (fills it), integer (whole screen
    // pixels per game pixel), fit (fills one side at its shape) or other (a custom size).
    public string LayoutForTest()
    {
        if (fh <= 0) return "no picture";
        double sw = w.Stage.Width, sh = w.Stage.Height, iw = image.Width, ih = image.Height;
        Log.Write($"SCRIPT core picture: {layoutNote}");
        if (Math.Abs(iw - sw) < 0.5 && Math.Abs(ih - sh) < 0.5) return "stretch";
        var (cw, ch) = Client();
        double unit = cw > 0 ? Math.Min(cw / sw, ch / sh) * VisualTreeHelper.GetDpi(w).DpiScaleY : 1;
        double nativeH = core?.Av.Geometry.BaseHeight > 0 ? core.Av.Geometry.BaseHeight : fh;
        double k = ih * unit / nativeH;
        bool edge = Math.Abs(iw - sw) < 0.5 || Math.Abs(ih - sh) < 0.5;
        if (Math.Abs(k - Math.Round(k)) < 0.01 && k >= 1 && !edge) { Log.Write($"SCRIPT core picture: integer {Math.Round(k)}x"); return "integer"; }
        return edge ? "fit" : "other";
    }

    // For tests: the last picture the core drew, as a PNG, plus how many pixels aren't black.
    // The screenshot key (emulation thread): the picture on screen now, as the core made it (its own size, the right
    // way up), to Screenshots in the data folder as PNG, with a toast. Saved on a thread of its own, so the game
    // doesn't wait for it.
    void Screenshot()
    {
        byte[] px; int width, height, f;
        lock (frameLock)
        {
            if (fw == 0 || fh == 0) return;
            px = frame.ToArray(); width = fw; height = fh; f = fmt;
        }
        string dir = Path.Combine(w.Lib.Dir, "Screenshots"), name = AddGameLogic.Clean(Game.Name ?? "");
        if (name == "") name = T("home.game");
        var t = new Thread(() =>
        {
            string path = null, why = null;
            try
            {
                var pf = f == 1 ? PixelFormats.Bgr32 : f == 2 ? PixelFormats.Bgr565 : PixelFormats.Bgr555;
                var src = BitmapSource.Create(width, height, 96, 96, pf, null, px, width * (f == 1 ? 4 : 2));
                string stem = $"{name} {DateTime.Now:yyyy-MM-dd HH-mm-ss}";
                path = Path.Combine(dir, stem + ".png");
                for (int n = 2; File.Exists(path); n++) path = Path.Combine(dir, $"{stem} {n}.png");
                Art.SavePng(src, path);
                Log.Write($"core: screenshot {Path.GetFileName(path)} ({width}x{height})");
            }
            catch (Exception e) { Log.Write("core screenshot: " + e.Message); path = null; why = e.Message; }
            w.Dispatcher.BeginInvoke(() => w.Toasts.Show(path != null ? T("menu.screenshot_saved") : T("menu.couldn_t_save_the_screenshot"),
                path != null ? Path.GetFileName(path) : why, path != null ? "picture" : "error"));
        }) { IsBackground = true, Name = "core screenshot" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    public int SaveFrame(string path)
    {
        BitmapSource src; int lit = 0;
        lock (frameLock)
        {
            if (fw == 0 || fh == 0) return -1;
            var pf = fmt == 1 ? PixelFormats.Bgr32 : fmt == 2 ? PixelFormats.Bgr565 : PixelFormats.Bgr555;
            int bpp = fmt == 1 ? 4 : 2;
            for (int i = 0; i < frame.Length; i += bpp) { bool any = false; for (int k = 0; k < Math.Min(bpp, 3); k++) any |= frame[i + k] != 0; if (any) lit++; }
            src = BitmapSource.Create(fw, fh, 96, 96, pf, null, frame, fw * bpp);
        }
        Art.SavePng(src, path);
        return lit;
    }

    public void Pause() { paused = true; }

    public void Resume()
    {
        if (Ended) return;
        layer.Visibility = Visibility.Visible;
        if (!w.Stack.Contains(screen)) w.Push(screen);
        paused = false;
        Relayout();
    }

    // Leave the game paused and show the home screen behind it.
    public void Hide()
    {
        paused = true;
        direct?.Hide();
        layer.Visibility = Visibility.Collapsed;
        UnfitStage(w);
        w.Pop(screen);
    }

    public void Stop() { paused = false; stop = true; }

    // The launcher is quitting: stop the game and let the core thread close the core (and its GPU
    // context) before the process ends. A process that dies with a live OpenGL context can leave a
    // thread stuck in the graphics driver.
    public bool StopAndWait(int ms)
    {
        Stop();
        return thread == null || thread.Join(ms);
    }

    public void SaveState(Action<bool> done) => jobs.Enqueue(c =>
    {
        var b = c.SaveState();
        bool ok = false;
        if (b != null)
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(StatePath)); File.WriteAllBytes(StatePath + ".tmp", b); File.Move(StatePath + ".tmp", StatePath, true); ok = true; }
            catch (Exception e) { Log.Write("save state: " + e.Message); }
        }
        Log.Write($"core: save state {(ok ? "written" : "failed")} ({b?.Length ?? 0} bytes)");
        w.Dispatcher.BeginInvoke(() => done(ok));
    });

    public void LoadState(Action<bool> done) => jobs.Enqueue(c =>
    {
        bool ok = false;
        try { ok = File.Exists(StatePath) && c.LoadState(File.ReadAllBytes(StatePath)); } catch (Exception e) { Log.Write("load state: " + e.Message); }
        Log.Write($"core: load state {(ok ? "done" : "failed")}");
        w.Dispatcher.BeginInvoke(() => done(ok));
    });

    public void Reset() => jobs.Enqueue(c => c.Reset());

    public void SetOption(CoreOption o, string value) => jobs.Enqueue(c => c.SetOption(o.Key, value));

    public void SaveOptions()
    {
        try
        {
            var d = Options.ToDictionary(o => o.Key, o => o.Value);
            // A setting this game overrides (its tweaks) keeps the core-wide value in the file.
            if (Game.Tweaks != null)
                foreach (var k in Game.Tweaks.Keys)
                    if (baseSaved.TryGetValue(k, out var bv)) d[k] = bv; else d.Remove(k);
            Directory.CreateDirectory(Path.GetDirectoryName(OptionsPath));
            Json.Save(OptionsPath, d);
        }
        catch (Exception e) { Log.Write("core options save: " + e.Message); }
    }

    void Finished()
    {
        w.PreviewMouseMove -= Mouse; w.PreviewMouseDown -= Mouse; w.PreviewMouseUp -= Mouse;
        Ended = true;
        w.SizeChanged -= Resized;
        CompositionTarget.Rendering -= Draw;
        if (direct != null) { Log.Write($"core picture: {direct.Frames} frames drawn straight to the screen (slowest {direct.Slowest:0.0} ms, {direct.Slow} over 4 ms)"); direct.Dispose(); direct = null; }
        UnfitStage(w);
        w.Pop(screen);
        Anim.To(layer, UIElement.OpacityProperty, 0, 300, done: () => w.GameLayer.Children.Remove(layer));
        Log.Write($"core: \"{Game.Name}\" closed");
        OnEnded?.Invoke();
    }

    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out CurPt p);
    [StructLayout(LayoutKind.Sequential)] struct CurPt { public int X, Y; }
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);
    [DllImport("kernel32.dll")] static extern IntPtr CreateWaitableTimerExW(IntPtr attrs, IntPtr name, uint flags, uint access);
    [DllImport("kernel32.dll")] static extern bool SetWaitableTimer(IntPtr h, ref long due, int period, IntPtr apc, IntPtr arg, bool resume);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    IntPtr timer;   // the core thread's frame timer
}

// While a core game is in front, launcher actions do nothing: the buttons belong to the game.
// The Home menu comes from the Home key or View + Start (GameMenu).
public sealed class CoreScreen : IScreen
{
    readonly CoreSession s;
    public CoreScreen(CoreSession session) { s = session; }
    public void OnAct(Act a) { }
    public bool OnMouse(MouseAct m, Point p) => true;   // the mouse is the game's (the touch screen)
    public bool ShowsCursor => s.TouchMouse;              // only where the mouse is the touch screen
}

// LaunchFx's view of a core game: Started when the core thread runs, shown at the first picture.
public sealed class CoreRun : RunBase
{
    public readonly CoreSession Session;
    public CoreRun(MainWindow w, Game g, string dll)
    {
        Session = new CoreSession(w, g, dll);
        Session.OnEnded = () =>
        {
            if (Session.Failed != null && !Started) Failed = Session.Failed;
            Ended = true;
            OnEnded?.Invoke();
        };
        Session.OnShown = () => { Started = true; OnStarted?.Invoke(); };
    }
    public override void Begin() => Session.Start();
}

// The core's own settings, drawn as a launcher card. Up to nine rows show at a time.
// A core's own settings during a game: in the same boxes as Game settings (SettingGroups),
// the button layout under Controls. The card scrolls; Save is pinned under it.
public sealed class CoreOptionsCard : MenuCard
{
    readonly CoreSession s;
    bool dirty;

    public CoreOptionsCard(MainWindow w, CoreSession session) : base(w, session.CoreName + " settings", 1320)
    {
        s = session;
        var items = s.Options.ToList().Select(o => (opt: o, box: SettingGroups.Of(null, null, o.Key, o.Desc, o.Category))).ToList();
        items.Insert(0, (null, SettingGroups.Controls));   // the button layout
        foreach (var (title, list) in SettingGroups.Arrange(items, x => x.box))
        {
            BeginBox(title);
            foreach (var (opt, _) in list)
            {
                if (opt == null)
                {
                    AddRow(T("menu.button_layout"), () => s.Nintendo ? T("menu.nintendo_b_confirms") : T("settings.as_labelled"), _ => { s.Nintendo = !s.Nintendo; w.Settings.NintendoButtons = s.Nintendo; w.Settings.Save(); dirty = true; });
                    continue;
                }
                var o = opt;
                void Use(string v) { o.Value = v; s.SetOption(o, v); dirty = true; }   // shown at once; the core takes it before its next frame
                WithList(AddRow(Short(o.Desc), () => o.Label(o.Value), d =>
                {
                    int i = o.Values.IndexOf(o.Value);
                    Use(o.Values[((i < 0 ? 0 : i) + d + o.Values.Count) % o.Values.Count]);
                }), () => o.Values.Select(o.Label).ToList(), () => o.Values.IndexOf(o.Value), i => Use(o.Values[i]));
            }
            EndBox();
        }
        SaveRow(() => dirty, () => Close());
    }

    static string Short(string d) => d.Length > 48 ? d[..47] + "…" : d;

    // Test scripts.
    public string BoxTitles => string.Join(",", SettingGroups.Arrange(s.Options.Select(o => SettingGroups.Of(null, null, o.Key, o.Desc, o.Category)).Append(SettingGroups.Controls), b => b).Select(b => b.Title));

    public override void Close(bool sound = true)
    {
        s.SaveOptions();
        base.Close(sound);
        if (w.GameMenu.Game != null) w.GameMenu.ShowCard();
    }
}
