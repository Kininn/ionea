#nullable disable
// Starting a game (port of frontend\launch-effect): the screen fades to the background colour, the
// cover zooms up from its tile, a ring spins bottom-right. The overlay stays at least 1.2 s and
// leaves when the game shows a window or takes the foreground (12 s timeout, up to 60 s while a UAC
// prompt is up). When the game ends, the launcher comes back to the front.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using static Ionea.Loc;

namespace Ionea;

public sealed class LaunchFx
{
    readonly MainWindow w;
    bool busy;
    public bool Busy { get => busy; private set { busy = value; w.UpdateLoops(); } }   // the launch / closing screen keeps loops running
    public bool Running { get; private set; }       // a started game hasn't ended yet
    public Game Playing { get; private set; }       // the started game once it runs (any kind: emulator, core, Steam, PC); Discord status
    public DateTime PlayingSince { get; private set; }
    public bool PlayingNative { get; private set; }  // it was started through a native port (Ports.cs): Discord says PC
    public string LastResult;                       // for test scripts: "shown", "timeout", "cancelled", "failed", "ended"
    bool rebuildOnReturn;
    string returnId;
    int busyGen;          // which start / closing screen set Busy (an older one's fade-out must not clear a newer one's)
    bool closeFading;     // the closing screen is fading away: the game has ended, input goes to home again

    // Input waits only while a game is starting or closing, not while the closing screen fades out over
    // home (a press then was eaten and took a second one).
    public bool Blocks => Busy && !closeFading;

    public LaunchFx(MainWindow win)
    {
        w = win;
        w.Activated += (s, e) => ReturnRebuild();
    }

    // Back from a game: the row is rebuilt once (the game played goes to the front) and home settles in.
    // Never under a press: when a click on a tile is what brings the launcher to the front,
    // Activated comes just before the click, and the rebuild moved or remade the tiles under it, so the
    // click hit nothing (or the next game along). The rebuild then waits until the press has played and
    // keeps the selection. It also runs when the closing screen leaves, instead of at some later click.
    bool returnPoll;
    void ReturnRebuild()
    {
        if (!rebuildOnReturn || Busy || !w.IsActive) return;
        var h = w.Home;
        bool pressing = (h != null && (h.PressLeftMs > 0 || h.Holding)) || w.AcceptHeld || ButtonDown();
        if (pressing)
        {
            deferred = true;
            if (!returnPoll) { returnPoll = true; Ui.After(100, () => { returnPoll = false; ReturnRebuild(); }); }
            return;
        }
        rebuildOnReturn = false;
        h?.Rebuild(deferred ? null : returnId);
        if (!deferred) h?.Welcome();
        Sounds.Play("gameclose");   // back home after a game
        deferred = false;
    }
    bool deferred;
    static bool ButtonDown() => (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0;   // either mouse button (swapped buttons too)
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);

    // "Ready to play": the devices that will work in the game (and, greyed, ones used before but not plugged in).
    public void DevicesToastForTest() => DevicesToast();
    void DevicesToast()
    {
        var s = w.Settings.Input;
        var list = new List<(string kind, string name, bool on)>();
        foreach (var kind in new[] { "keyboard", "pad", "mouse" })
        {
            if (!s.On(kind) || !s.PopupsFor(kind)) continue;
            var (name, on, _) = w.Devs.Show(kind);
            if (name == null) continue;
            list.Add((kind, name, on));
        }
        if (list.Count == 0) return;
        var found = list.Where(d => d.on).Select(d => InputDevices.Noun(d.kind).ToLowerInvariant()).ToList();
        string title = found.Count == 0 ? T("errors.no_input_device_found") : T("errors.detected_your") + string.Join(T("common.and"), found);
        w.Toasts.ShowDevices(title, list);
    }

    readonly HashSet<string> limitsTold = new();   // an emulator's limits are shown once while the launcher runs

    // port: a native start made by Ports.Route (Ports.cs); null for every other start.
    public void Start(Game g, Rect from, BitmapSource cover, PortStart port = null)
    {
        if (w.GameMenu.Game != null)
        {
            // A game is open (paused in its Home menu): the same game resumes; another one asks to switch, then
            // the open game closes and the picked one starts (GameMenu.SwitchTo).
            if (w.GameMenu.Game.Id == g.Id) { w.GameMenu.Resume(true); return; }
            if (w.GameMenu.Closing || Busy) return;
            new SwitchGameCard(w, w.GameMenu.Game, g, () => w.GameMenu.SwitchTo(g, from, cover, port)).Open();
            return;
        }
        string lnk = w.Lib.ShortcutPath(g), steamDir = null;
        bool steam = g.Source == "steam", rom = g.Source == "rom";
        ProcessStartInfo emuStart = null;
        string coreDll = null;
        Emulator emu = null;
        string videoToken = null;   // this start's own video values, written by Ionea (Tweaks.ApplyGame)
        // Emulator or native port: Ports asks, refuses or prepares first, then calls back with the port's start.
        if (rom && port == null && Ports.Route(w, g, from, cover)) return;
        if (rom && port != null)
        {
            emu = port.Emu;
            emuStart = port.Psi;
            EmbedLogic.Prepare(emu, emuStart);
            Quiet.Apply(emu, emuStart);   // nothing of the port's own on top of the game (Quiet.cs)
            videoToken = Tweaks.ApplyGame(emu, g);
            if (ProfileSaves.Prepare(w.Profiles, emu, null, out bool pshared) is string perr)
            {
                new MessageCard(w, T("errors.can_t_start_this_game"), T("errors.couldn_t_set_up_s_saves", w.Profiles.Active?.Name, emu.Name, perr)).Open();
                if (videoToken != null) Tweaks.Restore(emu, videoToken);
                return;
            }
            if (pshared) w.Toasts.Show(T("errors.saves_are_shared"), T("errors.can_t_keep_profiles_apart", emu.Name), "info");
            Ports.SyncSaves(w, g, "start");   // one save with its emulator, unless Game info > Saves is Separate (Ports.cs)
            Ports.SyncAddons(w, g, Ports.Native(w, g), "start");   // the game's DLC from its emulator, linked where the port reads them
            lnk = emuStart.FileName;
            Log.Write($"start: \"{g.Name}\" natively with {emu.Name} from {port.GameDir}");
        }
        else if (rom)
        {
            emu = w.Emus.Get(g.Emulator);
            // No emulator yet (added before one for its system was there): one added since takes it now.
            if (emu == null && g.System != null && w.Emus.Adopt(w.Lib, w.Emus.Data.Emulators, g) > 0) emu = w.Emus.Get(g.Emulator);
            if (emu == null && g.Emulator == null) { new NoEmulatorCard(w, g).Open(); return; }
            if (emu == null) { new MessageCard(w, T("errors.can_t_start_this_game"), T("errors.its_emulator_was_removed_from_ionea")).Open(); return; }
            if (!emu.Present) { new MessageCard(w, T("errors.can_t_start_this_game"), T("errors.s_program_is_missing_choose_it", emu.Name)).Open(); return; }
            if (g.Rom == null || !File.Exists(g.Rom))
            {
                var place = Storage.PlaceOf(w, g.Rom);
                if (place != null && !Storage.Reachable(place, 1500))
                    new MessageCard(w, T("settings.isn_t_connected", place.Name), T("settings.can_t_reach_right_now", place.Path)).Open();
                else new MessageCard(w, T("errors.can_t_start_this_game"), T("settings.the_game_file_is_missing") + g.Rom).Open();
                return;
            }
            if (emu.IsCore) coreDll = emu.Exe;
            else
            {
                emuStart = new ProcessStartInfo(EmbedLogic.Alias(emu), DisplayLogic.Args(w, g, emu, EmbedLogic.Windowed(emu, RomLogic.Args(emu, g.Rom)))) { UseShellExecute = false, WorkingDirectory = System.IO.Path.GetDirectoryName(emu.Exe) };
                EmbedLogic.Prepare(emu, emuStart);
                Quiet.Apply(emu, emuStart);   // its own overlays, OSD and popups kept off (Quiet.cs)
                if (EmuControls.Writes(emu, InputMap.SystemOf(w, g)))
                {
                    EmuControls.Choose(w);   // started from a controller: it gets the buttons; which controller
                    EmuControls.Write(emu, w.Settings.Input, InputMap.ScopeFor(w, g));   // this game's controls (EmuControls.cs)
                }
                videoToken = Tweaks.ApplyGame(emu, g);   // its own video values, for an emulator without per-game files (Tweaks.cs)
                // The active profile's saves, never someone else's.
                if (ProfileSaves.Prepare(w.Profiles, emu, g.Rom, out bool shared) is string perr)
                {
                    new MessageCard(w, T("errors.can_t_start_this_game"), T("errors.couldn_t_set_up_s_saves", w.Profiles.Active?.Name, emu.Name, perr)).Open();
                    if (videoToken != null) Tweaks.Restore(emu, videoToken);
                    return;
                }
                if (shared) w.Toasts.Show(T("errors.saves_are_shared"), T("errors.can_t_keep_profiles_apart", emu.Name), "info");
                Ports.SyncSaves(w, g, "start");   // a game with a native port too: one save for both (Ports.cs)
                // Its other limits, plainly, once per emulator while the launcher runs.
                var lim = EmuDefs.Limits(emu).Where(x => x is not ("saves are shared" or "its own controls")).ToList();
                if (!EmuControls.Writes(emu, InputMap.SystemOf(w, g))) lim.Insert(0, "its own controls");
                if (lim.Count > 0 && limitsTold.Add(emu.Id + "|" + string.Join(",", lim)))
                {
                    w.Toasts.Show(char.ToUpper(lim[0][0]) + lim[0][1..], emu.Name + string.Concat(lim.Skip(1).Select(x => " · " + x)), "info");
                    Log.Write($"start: {emu.Name} limits: {string.Join(", ", lim)}");
                }
            }
            lnk = emuStart?.FileName ?? emu.Exe;
        }
        else if (steam)
        {
            if (SteamLib.Root() == null) { new MessageCard(w, T("errors.can_t_start_this_game"), T("settings.steam_is_not_installed_on_this")).Open(); return; }
            steamDir = SteamLib.Find(g.AppId)?.InstallDir;
            if (steamDir == null) { new MessageCard(w, T("errors.can_t_start_this_game"), T("errors.steam_doesn_t_list_this_game")).Open(); return; }
            lnk = "steam://rungameid/" + g.AppId;
        }
        else if (lnk == null || !File.Exists(lnk))
        {
            new MessageCard(w, T("errors.can_t_start_this_game"), T("errors.its_shortcut_is_missing_from_ionea")).Open();
            return;
        }
        Busy = true; closeFading = false;
        int gen = ++busyGen;
        LastResult = null;
        Sounds.Play("launch");
        DevicesToast();
        Log.Write($"launch \"{g.Name}\" ({lnk}{(emuStart != null ? " " + emuStart.Arguments : "")})");

        var layer = Overlay(g, cover, g.Name, from);

        // ---- start ----
        var before = Native.BigWindows();
        var started = DateTime.Now;
        RunBase run = coreDll != null ? new CoreRun(w, g, coreDll) : steam ? new SteamRun(g, steamDir) : emuStart != null ? new GameRun(g, emuStart) { Embed = EmbedLogic.CanEmbed(emu), Helper = rom && g.Helper != null, OwnExe = emu.Exe } : new GameRun(g, lnk);
        bool helped = run is GameRun { Helper: true };   // its helper prepares first (Helper.cs): more time before the emulator starts
        var srun = run as SteamRun;
        bool closing = false;
        Running = true;
        DateTime? shownAt = null;

        void Finish(string result)
        {
            if (closing) return;
            closing = true;
            LastResult = result;
            Log.Write($"launch \"{g.Name}\": {result}");
            Anim.To(layer, UIElement.OpacityProperty, 0, 400, Anim.InOut, done: () =>
            {
                w.LaunchLayer.Children.Remove(layer);
                if (gen != busyGen) return;   // a newer start or closing screen holds Busy now
                Busy = false;
                if (!Running) ReturnRebuild();
            });
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (s, e) =>
        {
            double t = (DateTime.Now - started).TotalSeconds;
            if (run.Failed != null)
            {
                timer.Stop();
                bool cancelled = run.Failed == "cancelled";
                if (videoToken != null) Tweaks.Restore(emu, videoToken);
                Finish(cancelled ? "cancelled" : "failed");
                Running = false;
                if (cancelled) w.Toasts.Show(T("errors.start_cancelled"), g.Name, "info");
                else Ui.After(450, () => new MessageCard(w, T("errors.couldn_t_start_the_game"), run.Failed).Open());
                return;
            }
            if (run is CoreRun)
            {
                // Inside the launcher: the effect leaves once the first picture is drawn (at least 1.2 s).
                if (run.Ended || (run.Started && t >= 1.2) || t >= 30) { timer.Stop(); Finish(run.Ended ? "ended" : run.Started ? "shown" : "timeout"); }
                return;
            }
            if (!run.Started) { if (t > (helped ? HelperRun.ReadySeconds + 30 : 60)) { timer.Stop(); Finish("timeout"); } return; }
            if (srun != null)
            {
                // Steam: the effect stays until the game's own window exists (then fades out at once),
                // at most 25 s (Steam may show its own dialogs; we are never topmost over them).
                if (run.Ended || srun.Shown || t >= 25)
                {
                    timer.Stop();
                    Finish(srun.Shown ? "shown" : run.Ended ? (srun.NeverStarted ? "not started" : "ended") : "waiting");
                }
                return;
            }
            if (run is GameRun { Embed: true })
            {
                // Inside the launcher: the game window exists long before it draws (Lichii, bbport), and
                // until then the home screen showed through it. The cover stays until the game has drawn its first
                // picture over it, the Home menu opens, or it ends (at most 2 min, 10 for a port that prepares first).
                bool drawn = t >= 1.2 && w.GameMenu.GameDrawn(((SolidColorBrush)w.P.Bg).Color);
                if (run.Ended || drawn || w.GameMenu.Paused || t >= (port != null ? 600 : 120))
                {
                    timer.Stop();
                    Finish(run.Ended ? "ended" : drawn || w.GameMenu.Paused ? "shown" : "timeout");
                }
                return;
            }
            if (shownAt == null)
            {
                var fg = Native.GetForegroundWindow();
                bool other = fg != IntPtr.Zero && fg != w.Hwnd && Native.GetWindowThreadProcessId(fg, out uint pid) != 0 && pid != Environment.ProcessId;
                var fresh = Native.BigWindows(); fresh.ExceptWith(before);
                if (fresh.Count > 0 || (other && t > 0.5) || w.GameMenu.IsEmbedded) { shownAt = DateTime.Now; run.Watch(fresh); }
            }
            double limit = Native.SecureDesktop() ? 60 : 12;
            if (run.Ended || (shownAt != null && t >= 1.2) || t >= limit)
            {
                timer.Stop();
                Finish(run.Ended && shownAt == null ? "ended" : shownAt != null ? "shown" : "timeout");
            }
        };
        timer.Start();

        run.OnStarted = () => w.Dispatcher.BeginInvoke(() =>
        {
            g.LastPlayed = DateTime.Now;
            w.Lib.Save();
            w.Profiles.BeginPlay(g);
            rebuildOnReturn = true; returnId = g.Id;
            Playing = g; PlayingSince = DateTime.Now; PlayingNative = rom && port != null;
            if (rom && run is GameRun gr && gr.Proc != null) w.GameMenu.Begin(g, gr.Proc, EmbedLogic.CanEmbed(emu));
            if (run is CoreRun cr) w.GameMenu.BeginCore(g, cr.Session);
            // Display mode (Display.cs): full screen or a window of the game's size, for games inside the launcher.
            if (run is CoreRun cr3) GameWindow.Begin(w, g, "core", cr3.Session.NativeSize);
            else if (rom && emu != null && EmbedLogic.CanEmbed(emu)) GameWindow.Begin(w, g, "embed", DisplayLogic.NativeOf(InputMap.SystemOf(w, g)));
            w.RefreshDiscord();
        });
        run.OnEnded = () => w.Dispatcher.BeginInvoke(() =>
        {
            Running = false;
            if (Playing?.Id == g.Id) Playing = null;
            Log.Write($"\"{g.Name}\" ended");
            if (videoToken != null) Tweaks.Restore(emu, videoToken);
            if (rom) Ports.SyncSaves(w, g, "end");   // a save folder the port made during play joins the shared save
            w.Profiles.EndPlay(g);
            GameWindow.End(w, g);   // the launcher's own display mode again (Display.cs)
            if (w.GameMenu.Game?.Id == g.Id) w.GameMenu.End();
            w.RefreshDiscord();
            if (srun?.NeverStarted == true)
            {
                LastResult = "not started";
                w.Toasts.Show(T("errors.the_game_didn_t_start"), T("errors.steam_didn_t_start_within_s", g.Name, srun.WaitSeconds), "error");
            }
            if (run is CoreRun cr2 && cr2.Session.Failed != null && cr2.Started) w.Toasts.Show(T("errors.the_game_stopped"), cr2.Session.Failed, "error");
            if (!Busy) w.BringToFront();
            else Ui.After(500, w.BringToFront);
            ReturnRebuild();   // already in front (a game inside the launcher): home is rebuilt now, not at a later click
        });
        run.Begin();
    }

    // ---- closing ----
    // Closing an emulator game (GameMenu.CloseGame): the start's screen again, "Closing <name>", in front of
    // the emulator's own closing (its windows are kept hidden, the launcher stays on top) until the game has
    // ended (GameMenu.End), at least 1 s, at most 12 s.
    Grid closeLayer;
    DispatcherTimer closeTimer;
    DateTime closeShown;
    bool closeLeaving;
    public string ClosingName { get; private set; }     // test scripts: the game whose closing screen is up
    bool TestRun => w.Opt.Windowed == true && !string.IsNullOrEmpty(w.Opt.Script);   // never on top in a test run

    public void ShowClosing(Game g, BitmapSource cover, int pid)
    {
        if (closeLayer != null || Busy) return;
        Busy = true; ClosingName = g.Name; closeShown = DateTime.Now; closeLeaving = false; closeFading = false;
        ++busyGen;
        Log.Write($"closing \"{g.Name}\": closing screen");
        double s = 0.9 * 520;
        closeLayer = Overlay(g, cover, T("errors.closing") + g.Name, new Rect((1920 - s) / 2, (1080 - s) / 2 - 40, s, s));
        if (!TestRun) w.Topmost = true;
        closeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        closeTimer.Tick += (_, _) =>
        {
            if (pid != 0) Native.HideWindowsOf(pid);
            if ((DateTime.Now - closeShown).TotalSeconds >= 12) { Log.Write($"closing \"{g.Name}\": not ended after 12 s"); HideClosing(); }
        };
        closeTimer.Start();
    }

    public void HideClosing()
    {
        if (closeLayer == null || closeLeaving) return;
        closeLeaving = true;
        double left = 1000 - (DateTime.Now - closeShown).TotalMilliseconds;
        Ui.After(Math.Max(1, left), () =>
        {
            closeTimer?.Stop(); closeTimer = null;
            var layer = closeLayer; closeLayer = null; string was = ClosingName; ClosingName = null;
            w.Topmost = false;
            // The launcher is closing (MainWindow.BeginQuit): the closing screen stays and the outro plays over it.
            if (w.Quitting) { Log.Write($"closing \"{was}\": ended; the outro follows"); return; }
            int gen = busyGen;
            closeFading = true;   // the game has ended: home takes input while this fades
            Anim.To(layer, UIElement.OpacityProperty, 0, 400, Anim.InOut, done: () =>
            {
                w.LaunchLayer.Children.Remove(layer);
                if (gen != busyGen) return;   // a game started from home meanwhile: its own screen holds Busy
                closeFading = false;
                Busy = false;
                ReturnRebuild();
            });
        });
    }

    // The screen of a game starting or closing: the background colour fades in, the cover grows from
    // `from` to the middle, the words under it, a ring spins bottom-right.
    Grid Overlay(Game g, BitmapSource cover, string words, Rect from)
    {
        var P = w.P;
        var layer = new Grid();
        var bg = new Rectangle { Fill = P.Bg, Opacity = 0 };
        layer.Children.Add(bg);
        var cv = new Canvas();
        layer.Children.Add(cv);
        const double big = 520;
        var img = new Border
        {
            Width = big, Height = big, CornerRadius = new CornerRadius(16.28 * big / 297),
            Background = cover != null ? CoverView.Brush(cover, g) : CoverColour.Fill(g), BorderBrush = P.TileEdge, BorderThickness = new Thickness(1.5),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 50, ShadowDepth = 14, Direction = 270, Opacity = 0.22 },
        };
        if (cover == null)
        {
            var t = Fonts.Text(g.Name, 44, CoverColour.Ink(g), FontWeights.Normal, Fonts.Display);
            t.TextWrapping = TextWrapping.Wrap; t.TextAlignment = TextAlignment.Center; t.VerticalAlignment = VerticalAlignment.Center; t.Margin = new Thickness(30);
            img.Child = t;
        }
        var sc = new ScaleTransform(from.Width / big, from.Height / big);
        var tr = new TranslateTransform(from.X, from.Y);
        img.RenderTransform = new TransformGroup { Children = { sc, tr } };
        cv.Children.Add(img);
        var name = Fonts.Text(words, 44, P.Text, FontWeights.Light, Fonts.Display);
        var nameBox = new Grid { Width = 1920, Opacity = 0 };
        name.HorizontalAlignment = HorizontalAlignment.Center;
        nameBox.Children.Add(name);
        Canvas.SetTop(nameBox, 540 + big / 2 + 36 - 30);
        cv.Children.Add(nameBox);
        var spin = Spinner(64);
        Canvas.SetLeft(spin, 1920 - 64 - 80); Canvas.SetTop(spin, 1080 - 64 - 72);
        spin.Opacity = 0;
        cv.Children.Add(spin);
        w.LaunchLayer.Children.Add(layer);

        Anim.To(bg, UIElement.OpacityProperty, 1, 220, Anim.Out);
        double ease = 320;
        Anim.To(sc, ScaleTransform.ScaleXProperty, 1, ease, Anim.Out); Anim.To(sc, ScaleTransform.ScaleYProperty, 1, ease, Anim.Out);
        Anim.To(tr, TranslateTransform.XProperty, (1920 - big) / 2, ease, Anim.Out); Anim.To(tr, TranslateTransform.YProperty, (1080 - big) / 2 - 40, ease, Anim.Out);
        Anim.To(nameBox, UIElement.OpacityProperty, 1, 260, delay: 160);
        Anim.To(spin, UIElement.OpacityProperty, 1, 260, delay: 200);
        return layer;
    }

    public static Grid Spinner(double d)
    {
        var g = new Grid { Width = d, Height = d };
        g.Children.Add(new Ellipse { Stroke = new SolidColorBrush(Color.FromArgb(40, 0x80, 0x88, 0x99)), StrokeThickness = 6 });
        var arc = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse($"M {d / 2},3 A {d / 2 - 3},{d / 2 - 3} 0 1 1 3,{d / 2}"),
            Stroke = Palette.RingBrush(new Point(0, 0), new Point(1, 0)), StrokeThickness = 6,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        };
        var rot = new RotateTransform(0, d / 2, d / 2);
        arc.RenderTransform = rot;
        Anim.Forever(rot, RotateTransform.AngleProperty, 0, 360, 1000, false);
        g.Children.Add(arc);
        return g;
    }
}

// Runs a shortcut and watches for the end of the game. ShellExecute gives a process handle (also
// through UAC). Launchers that hand over to another exe are covered by also waiting for the
// windows that appeared and for processes named like the shortcut's target.
public abstract class RunBase
{
    public volatile bool Started, Ended;
    public volatile string Failed;
    public Action OnStarted, OnEnded;
    public abstract void Begin();
    public virtual void Watch(IEnumerable<IntPtr> fresh) { }
}

public sealed class GameRun : RunBase
{
    readonly Game g;
    readonly string lnk;
    readonly List<IntPtr> windows = new();
    readonly object gate = new();

    readonly ProcessStartInfo direct;   // emulator + game file, started without a shortcut
    public Process Proc;                // the emulator's process (direct starts only), for the Home menu
    public bool Embed;                  // drawn inside the launcher: it lives in the launcher's job (EmbedLogic.Hold)
    public bool Helper;                 // the game's helper runs beside it (Helper.cs; direct starts only)
    public string OwnExe;               // the emulator's real program file, for a helper's "own name" (Helper.cs)
    IntPtr job;

    public GameRun(Game game, string shortcut) { g = game; lnk = shortcut; }
    public GameRun(Game game, ProcessStartInfo start) { g = game; direct = start; lnk = start.FileName; }

    static readonly string OwnName = System.IO.Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? T("errors.launcher"));

    public override void Watch(IEnumerable<IntPtr> fresh) { lock (gate) windows.AddRange(fresh); }

    public override void Begin()
    {
        var th = new Thread(() =>
        {
            Process p = null;
            HelperRun helper = null;
            if (Helper && direct != null)
            {
                helper = HelperRun.Start(g, direct, OwnExe, out string herr);
                if (helper == null) { Failed = herr; return; }
            }
            try
            {
                p = Process.Start(direct ?? new ProcessStartInfo(lnk) { UseShellExecute = true });
            }
            catch (Win32Exception e) when (e.NativeErrorCode == 1223) { helper?.Stop(); Failed = "cancelled"; return; }
            catch (Exception e) { helper?.Stop(); Failed = e.Message; Log.Write($"start {lnk}: {e}"); return; }
            if (direct != null) Proc = p;
            if (Embed && p != null) job = EmbedLogic.Hold(p);
            Started = true;
            OnStarted?.Invoke();
            string exe = null;
            try
            {
                string t = direct != null ? direct.FileName : g.Target;
                if (t == null) Shortcut.Read(lnk, out t, out _, out _, out _);
                exe = t == null ? null : System.IO.Path.GetFileNameWithoutExtension(t);
                // An emulator started through the link named like the launcher (EmbedLogic.Alias): the name
                // would match every launcher, so its process handle alone tells when it ends.
                if (direct != null && string.Equals(exe, OwnName, StringComparison.OrdinalIgnoreCase)) exe = null;
            }
            catch (Exception caught) { Log.Debug("launch: target name", caught); }
            try { p?.WaitForExit(); } catch { /* already ended */ }
            EmbedLogic.Release(job); job = IntPtr.Zero;
            // Wait as long as the game's windows or a process with the target's name are still around.
            var since = DateTime.Now;
            while (true)
            {
                bool alive = false;
                lock (gate) alive = windows.Any(Native.IsWindowVisible);
                if (!alive && exe != null)
                {
                    // Never the launcher itself (a PC game's program may also be called Launcher.exe).
                    var ps = Process.GetProcessesByName(exe);
                    alive = ps.Any(x => x.Id != Environment.ProcessId);
                    foreach (var x in ps) x.Dispose();
                }
                // A launcher may exit before its game window opens: give it a few seconds. An emulator started
                // directly hands over to nothing: it has ended once it and its windows are gone.
                if (!alive && (direct != null || (DateTime.Now - since).TotalSeconds > (p == null ? 15 : 4))) break;
                if (alive) since = DateTime.Now;
                Thread.Sleep(1000);
            }
            p?.Dispose();
            helper?.Stop();   // after the game: it may tidy up what it set up for it (Helper.cs)
            Ended = true;
            OnEnded?.Invoke();
        }) { IsBackground = true, Name = "game-run" };
        th.SetApartmentState(ApartmentState.STA);
        th.Start();
    }
}
