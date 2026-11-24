#nullable disable
// The one window. Everything is drawn on a 1920x1080 stage inside a Viewbox, so every size and
// every screenshot looks the same. Screens and cards form a stack; input goes to the top one.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using static Ionea.Loc;

namespace Ionea;

public interface IScreen
{
    void OnAct(Act a);
    bool WantsText => false;   // text entry: only Enter / Esc / Up / Down are taken from the keyboard
    bool TakeKey(Key k) => false;                  // a key as it is (binding a key): true = used
    bool TakeMouse(MouseButton b) => false;        // a mouse button as it is (binding it): true = used
    bool OnMouse(MouseAct m, Point p) => false;    // stage coordinates; true = used (on Move: over something clickable)
}

public sealed class MainWindow : Window
{
    public readonly Options Opt;
    public readonly Settings Settings;
    public readonly Library Lib;
    public readonly Profiles Profiles;
    public readonly Palette P = Palette.I;
    public readonly Grid Stage = new() { Width = 1920, Height = 1080, ClipToBounds = true };
    public readonly Grid ScreenLayer = new(), GameLayer = new(), CardLayer = new(), LaunchLayer = new(), ToastLayer = new();
    public readonly InputHub Input = new();
    public InputDevices Devs;
    public Pointer Mouse;
    public Toasts Toasts;
    public HomeScreen Home;
    public StartScreen StartScr;
    public LaunchFx Launch;
    public GameMenu GameMenu;
    public readonly Grid OskLayer = new();
    public Osk Osk { get; private set; }
    public readonly Canvas CoachLayer = new() { IsHitTestVisible = false };   // the tutorial's ring and bubble (Tutorial.cs)
    public Tutorial Tutor;
    public PerfOverlay Overlay;   // Ionea's in-game performance overlay (Overlay.cs)
    public readonly Grid OverlayLayer = new() { IsHitTestVisible = false };   // over a core's picture, under cards and the Home menu
    public HomeKeys HomeKeys;   // the Home menu key in a game (LauncherKeys.cs)
    public EmuList Emus;
    public IntPtr Hwnd;
    public bool PadLast { get; private set; }   // the last action came from a controller (hints show pad buttons, else keys)
    public readonly DispatcherTimer Clock = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly List<IScreen> stack = new();
    Dictionary<string, string> pads = new();
    readonly List<IntPtr> devNotify = new();
    bool devRefreshQueued;

    public MainWindow(Options opt)
    {
        Opt = opt;
        Settings = Settings.Load(opt.DataDir);
        if (opt.Windowed.HasValue) Settings.Fullscreen = !opt.Windowed.Value;
        Loc.Use(opt.Language ?? Settings.Language, opt.DataDir);   // UI language (Loc.cs)
        EmbedLogic.Group = Settings.TaskManagerGroup;
        if (!EmbedLogic.Group) EmbedLogic.RemoveLinks();
        Online.Init(Settings, opt.DataDir);   // the network connection, the source switches and the activity log (Online.cs)
        Online.Changed += () => RefreshDiscord();
        Updater.Init(Settings);               // version and updates (Updater.cs)
        if (!string.IsNullOrEmpty(opt.Script)) Online.TestHost = opt.OnlineTestHost;
        EmuDefs.Load(System.IO.Path.Combine(opt.DataDir, "definitions"));   // emulator and system definitions (EmuDefs.cs)
        Paths.Apply(Settings, opt);   // this PC's folders: ES-DE, the Inbox, emulators (Paths.cs)
        Scan.Home = opt.ScanHome;     // tests: the scan looks through a fake home
        Lib = new Library(opt.DataDir);
        if (!opt.NoImport && opt.EsdeDir != null) EsdeImport.Run(Lib, opt.EsdeDir);
        Emus = new EmuList(opt.DataDir, Paths.EmuFolders(Settings, opt));
        ReqFiles.StoreDir = System.IO.Path.Combine(opt.DataDir, "files");   // required files' shared store (RequiredFiles.cs)
        ReqFiles.AllEmus = () => Emus.Data.Emulators;
        RomArt.Emus = () => Emus.Data.Emulators;   // a Switch game's icon is read with a Switch emulator's keys
        RomArt.Backfill(Lib);
        SysDat.Load(System.IO.Path.Combine(opt.DataDir, "definitions"));   // the user's own System.dat, if any
        var foundEmus = Emus.Discover();
        Emus.UseCores(Lib, foundEmus);
        ReqFiles.SupplyAll();   // what the store or another emulator has goes to every emulator lacking it
        Tweaks.DataDir = Lib.Dir;
        Tweaks.RestoreLeft(Emus.Data.Emulators.Where(e => e.Present));   // video values a game start left in an emulator's file
        Profiles = new Profiles(opt.DataDir);
        Lib.PlayedAt = g => Profiles.Active != null ? Profiles.LastPlayed(g) : g.LastPlayed;
        if (opt.Dummies > 0) Dummies.Ensure(Lib, opt.Dummies, opt.DummyTarget);
        Sounds.Prefs = () => Settings.Sound;
        Sounds.HookTyping();
        Settings.Folders = () => ProfilePrefs.FoldersOf(Lib);
        Lib.Saved = Settings.SaveProfile;
        // Profiles made before settings were per profile keep what they had: the shared settings.
        foreach (var pr in Profiles.List)
            if (!System.IO.File.Exists(Profiles.SettingsFile(pr)))
                try { System.IO.Directory.CreateDirectory(Profiles.Folder(pr)); Json.Save(Profiles.SettingsFile(pr), ProfilePrefs.From(Settings, ProfilePrefs.FoldersOf(Lib))); }
                catch (Exception e) { Log.Write("profile settings: " + e.Message); }
        ApplyProfile(true);
        Sounds.On = Settings.Sounds && !opt.Mute;
        P.SetPreset(Settings.Colours, false);
        P.Set(Settings.Dark, false);
        Art.IconShape = Settings.IconShape ?? "round";
        Profiles.Switched += () => ApplyProfile(false);

        Title = "Ionea";
        try { Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/assets/icon/ionea.ico")); } catch (Exception caught) { Log.Debug("window icon", caught); }
        Background = P.Bg;
        Width = opt.Width; Height = opt.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (opt.Left.HasValue) { WindowStartupLocation = WindowStartupLocation.Manual; Left = opt.Left.Value; Top = opt.Top ?? 0; }
        ApplyWindowMode(true);
        UseLayoutRounding = false;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        RenderOptions.SetBitmapScalingMode(Stage, BitmapScalingMode.HighQuality);

        Stage.Children.Add(ScreenLayer);
        Stage.Children.Add(GameLayer);     // games running inside the launcher (libretro cores)
        Stage.Children.Add(OverlayLayer);  // the performance overlay over them
        Stage.Children.Add(CardLayer);
        Stage.Children.Add(CoachLayer);
        Stage.Children.Add(OskLayer);      // the on-screen keyboard over the cards (Osk.cs)
        Tutor = new Tutorial(this, CoachLayer);
        Stage.Children.Add(LaunchLayer);
        Sleep = new SleepLayer(this);
        Stage.Children.Add(Sleep.Layer);   // the dim while the launcher sleeps (Sleep.cs); toasts stay over it
        Stage.Children.Add(ToastLayer);
        // Brightness: a black veil over the whole window (the stage and any border round it), above everything
        Content = new Grid { Children = { new Viewbox { Stretch = Stretch.Uniform, Child = Stage }, dim } };
        SetBrightness(Settings.Brightness, false);

        Toasts = new Toasts(this);
        Devs = new InputDevices(this);
        Devs.Changed += () => Home?.RefreshPads();
        Mouse = new Pointer(this);
        Stage.Children.Add(Mouse.Layer);
        Launch = new LaunchFx(this);
        HomeKeys = new HomeKeys(this);
        Closed += (s, e) => HomeKeys.Stop();
        GameMenu = new GameMenu(this);
        Overlay = new PerfOverlay(this);
        GameMenu.Changed += () => Dispatcher.BeginInvoke(RefreshDiscord);
        Osk = new Osk(this, OskLayer);
        Input.Action += a => { Mouse.Hide(); PadAct(a); };
        Input.CanRead = () => IsActive && Native.GetForegroundWindow() == Hwnd;
        Input.Enabled = () => Settings.Input.PadOn;
        PreviewKeyDown += OnKey;
        PreviewKeyUp += (s, e) => KeyReleased(e.Key == Key.System ? e.SystemKey : e.Key);
        Deactivated += (s, e) => keyAccept = false;
        Activated += (s, e) => UpdateLoops();
        Deactivated += (s, e) => UpdateLoops();
        StateChanged += (s, e) => UpdateLoops();
        SourceInitialized += (s, e) => { Hwnd = new WindowInteropHelper(this).Handle; HookDevices(); Devs.Start(); Input.Start(); if (!Devs.TestOnly) Input.Hid.Start(Hwnd); };
        Loaded += (s, e) =>
        {
            Clock.Start();
            UpdateLoops();   // a window that opens behind others never gets Deactivated
            bool intro = Opt.Intro ?? (Settings.Intro && string.IsNullOrEmpty(Opt.Script));
            if (intro) new IntroScreen(this, () => FirstScreen(true)).Play();
            else FirstScreen(false);
            if (foundEmus.Count > 0) Ui.After(1500 + (intro ? IntroScreen.OutAt : 0), () => EmuPick.Announce(this, foundEmus));
            // Learn what the cores the library uses can tweak, so the quick menu opens at once.
            Ui.After(3000, () => CoreCatalog.Warm(Lib.Data.Games.Where(g => !g.Removed && g.Source == "rom").Select(g => Emus.Get(g.Emulator))
                .Where(e => e != null && e.IsCore && e.Present && Tweaks.Has(e)).Select(e => e.Exe)));
            RefreshDiscord();
            if (string.IsNullOrEmpty(Opt.Script)) Ui.After(6000, () => Covers.Queue(this));   // covers for games with none, when switched on
            Updater.AfterStart(this, !string.IsNullOrEmpty(Opt.Script));   // the toast after an update; Check at start
            if (!string.IsNullOrEmpty(Opt.Script)) new TestScript(this, Opt.Script).Run();
        };
        if (Application.Current != null) Application.Current.SessionEnding += (s, e) => { sessionEnding = true; Log.Write($"quit: Windows is ending the session ({e.ReasonSessionEnding}); no outro"); };
        Closed += (s, e) => Discord.Dispose();
        Closed += (s, e) => Profiles?.Checkpoint();   // a game still running: its time so far is kept (Profiles.cs)
        Closed += (s, e) => { foreach (var h in devNotify) Native.UnregisterDeviceNotification(h); };
    }

    // ---- closing: the outro (Outro.cs) ----
    // Every normal close (Quit, Alt+F4, the close button, a test script's quit) comes here first. With the
    // outro on, the close is held: a game open in the Home menu closes first behind its closing screen (its
    // saves written, as Close game does), then the outro plays and the window closes for real. A second close
    // meanwhile waits for it. With the outro off, a close during a game is held the same way, without the outro.
    // An emulator that is slow to close is never ended on a timer: the StillClosingCard asks. Windows shutdown /
    // logoff never waits: it closes at once, as before.
    bool quitting, quitDone, sessionEnding;
    public bool Quitting => quitting;   // test scripts: "expect quitting"
    bool OutroOn => Opt.Outro ?? (Settings.Outro && string.IsNullOrEmpty(Opt.Script));

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!quitDone && !sessionEnding && (quitting || OutroOn || GameMenu?.Game != null))
        {
            e.Cancel = true;
            if (!quitting) BeginQuit();
            return;
        }
        base.OnClosing(e);
    }

    // After a crash (Crash.cs): close at once, no outro; a game open in the Home menu is still stopped on the way.
    public void CloseForCrash() { quitDone = true; Close(); }

    void BeginQuit()
    {
        quitting = true;
        var outro = new OutroScreen(this);
        outro.Hold();
        void Go()
        {
            if (sessionEnding || quitDone) return;
            if (!OutroOn) { quitDone = true; Close(); return; }
            outro.Play(() => { if (quitDone) return; quitDone = true; Log.Write("outro: done"); Close(); });
        }
        var g = GameMenu?.Game;
        if (g == null) { Log.Write("quit: outro"); Go(); return; }
        // The game first, the way Close game does it, behind its closing screen.
        Log.Write($"quit: closing \"{g.Name}\" first{(GameMenu.Paused ? " (paused)" : "")}");
        GameMenu.CloseForQuit();
        var t0 = DateTime.Now;
        void Wait()
        {
            double ms = (DateTime.Now - t0).TotalMilliseconds;
            bool gone = GameMenu.Game == null && Launch.ClosingName == null;
            // A core gets 20 s. An emulator still closing is waited for as long as it runs: after a while the
            // StillClosingCard asks the user whether to keep waiting or end it.
            if (!gone && (ms < 20000 || GameMenu.EmulatorClosing)) { Ui.After(100, Wait); return; }
            if (!gone) Log.Write($"quit: \"{g.Name}\" still not closed after 20 s; closing anyway");
            else Log.Write($"quit: \"{g.Name}\" closed after {ms:0} ms");
            Go();
        }
        Ui.After(100, Wait);
    }

    // Looping animations only run while someone can be looking at them: the launcher is the active window,
    // or its launch / closing screen is up (a game's window can hold the front then). Behind other windows,
    // minimized or under a game they pause, and WPF stops redrawing the window. Test runs never take the
    // front, so they keep everything running.
    // Sleep: behind other windows with no game open, the screen dims first and the loops pause
    // once it has faded in (Sleep.cs). Minimized, or a game open, they pause at once as before (nothing seen).
    public SleepLayer Sleep { get; private set; }

    public void UpdateLoops()
    {
        bool show = !string.IsNullOrEmpty(Opt.Script) || Launch?.Busy == true
            || (IsActive && WindowState != WindowState.Minimized);
        if (!show && Sleep != null && WindowState != WindowState.Minimized && GameMenu?.Game == null) { Sleep.Set(true); return; }
        Sleep?.Set(false);
        Anim.PauseLoops(!show);
    }

    // The first real screen: making a profile, or the start screen (fading in under the intro's exit).
    void FirstScreen(bool afterIntro)
    {
        if (!Profiles.Any) { new NewProfileScreen(this, true).Open(); return; }
        var s = new StartScreen(this);
        Push(s);
        if (afterIntro) s.FadeIn();
    }

    // Settings > Look and layout > Watch the intro: plays it over whatever is showing, then back.
    public void ReplayIntro()
    {
        if (IntroScreen.Busy) return;
        SoundMixer.Warm(4000);
        new IntroScreen(this, null).Play();
    }

    // ---- screen stack ----
    public IScreen TopScreen => stack.Count > 0 ? stack[^1] : null;
    public void Push(IScreen s) { stack.Add(s); StackChanged?.Invoke(); }
    public void Pop(IScreen s)
    {
        bool wasTop = TopScreen == s;
        stack.Remove(s);
        if (wasTop && TopScreen is MenuCard under) under.OnTop();   // back on a menu: its values may have changed
        StackChanged?.Invoke();
    }
    public void Replace(IScreen old, IScreen s) { int i = stack.IndexOf(old); if (i >= 0) stack[i] = s; else stack.Add(s); StackChanged?.Invoke(); }
    public event Action StackChanged;   // the screen in front may have changed (the Home menu key follows it)
    public IReadOnlyList<IScreen> Stack => stack;

    // A controller's action: the on-screen keyboard first (Osk.cs), then the screen in front.
    // The Home menu just came up over a game, or the game ended: for a moment the controller, the keyboard and the
    // mouse do nothing here, and what was held for the game needs a fresh press (the mouse: to be moved first).
    long keyHushUntil; bool keyStale;
    public void HushInput(int ms)
    {
        keyHushUntil = Environment.TickCount64 + ms; keyStale = true; keyAccept = false;
        Input.Hush(ms);
        Mouse?.Hush(ms);
    }

    public void PadAct(Act a)
    {
        SetPadLast(true);
        if (Launch.Blocks) return;
        try { if (Osk.Handle(a)) return; }
        catch (Exception e) { Log.Write($"keyboard {a}: {e}"); }
        Dispatch(a);
    }

    public void Dispatch(Act a)
    {
        if (Launch.Blocks) return;               // a game is starting or closing: nothing moves
        try
        {
            // X (pad X, keyboard X / F2 / Delete: the "Game options" launcher key) anywhere in Settings, also on a
            // card opened from it: Settings closes, back to Home. Outside Settings X is unchanged.
            if (a == Act.Option && TopScreen is Card top && stack.OfType<SettingsCard>().Any()) { top.CloseAll(); return; }
            if (TopScreen is MenuCard mc && mc.ListOpen) mc.ListAct(a); else TopScreen?.OnAct(a);
        }
        catch (Exception e) { Log.Write($"input {a}: {e}"); }
    }

    // A or a Select key held down right now (a game held on the home screen opens its quick menu).
    bool keyAccept;
    Key acceptKey;
    public bool ScriptHeld;   // test scripts: "hold <ms>"
    // Settings > Input changed: the running game's bindings, the cursor and the Input button follow.
    public void InputChanged()
    {
        if (GameMenu?.Core is CoreSession cs) { cs.Map = InputMap.Snap(Settings.Input, cs.Scope); cs.Nintendo = Settings.NintendoButtons; }
        Mouse?.Apply();
        Home?.RefreshPads();
        Home?.RefreshHints();
        GameMenu?.KeysChanged();
        EmuOpen.KeysChanged();
    }

    public bool AcceptHeld => keyAccept || ScriptHeld || Input.AHeld || Mouse.Held;

    // The keyboard can be turned off only while a controller is plugged in (Settings > Input).
    public bool KeyboardOn => Settings.Input.KeyboardOn || !Settings.Input.PadOn || !Devs.Connected("pad");

    void OnKey(object sender, KeyEventArgs e)
    {
        if (KeyPressed(e.Key == Key.System ? e.SystemKey : e.Key, e.IsRepeat)) e.Handled = true;
    }

    // A key pressed (test scripts: "keydown"): true = the launcher used it.
    public bool KeyPressed(Key k, bool repeat)
    {
        if (k == Key.F4 && Keyboard.Modifiers == ModifierKeys.Alt) return false;   // Alt+F4 always closes
        if (!KeyboardOn) return !(TopScreen is IScreen ws && ws.WantsText);
        // The Home menu just came up over a game: keys pressed or still held for the game don't work the menu.
        if (Environment.TickCount64 < keyHushUntil || (repeat && keyStale)) return true;
        if (!repeat) keyStale = false;
        Mouse.Hide();
        SetPadLast(false);
        if (TopScreen is IScreen cap && !repeat && cap.TakeKey(k)) return true;
        if (k == Key.F11) { SetFullscreen(!Settings.Fullscreen); return true; }
        if (k == Key.F && Keyboard.Modifiers == ModifierKeys.Control && TopScreen == Home && Home != null) { Home.OpenSearch(); return true; }
        // The launcher's own keys (Settings > Input > Launcher keys). In a game the Home menu key is caught by HomeKeys.
        var act = LauncherKeys.Map(Settings.Input, k);
        if (act == Act.Accept)
        {
            // While a game is held, or its quick menu is up, the key's repeats are not more presses.
            if (repeat && (Home?.Holding == true || TopScreen is HoldMenu)) return true;
            if (!repeat) { keyAccept = true; acceptKey = k; }
        }
        // A held Back key (Esc) must not close a menu and the one under it: its repeats are ignored.
        if (act == Act.Back && repeat && !(k == Key.Back && TopScreen is IScreen ts && ts.WantsText)) return true;   // held Backspace in a text box deletes on
        if (act == null) return false;
        if (TopScreen is IScreen s && s.WantsText && k is not (Key.Enter or Key.Escape or Key.Up or Key.Down)) return false;   // let the text box have it
        Dispatch(act.Value);
        return true;
    }
    public void KeyReleased(Key k) { if (k == acceptKey) keyAccept = false; }

    void SetPadLast(bool pad)
    {
        if (PadLast == pad) return;
        PadLast = pad;
        Home?.RefreshHints();
    }

    // ---- home ----
    public void ShowHome(bool fromStart)
    {
        if (Home == null) { Home = new HomeScreen(this); ScreenLayer.Children.Add(Home.Root); }
        Home.Root.Visibility = Visibility.Visible;
        Storage.CheckAll(this);   // a sleeping network share takes ~20 s to answer: ask early, in the background
        Home.Enter(fromStart);
        foreach (var b in Json.TakeBroken())   // a data file that could not be read (Data.cs Json.Load)
            Toasts.Show(b.Name + T("home.could_not_be_read"), b.Restored ? T("home.using_the_copy_from_the_save") : T("home.kept_as") + System.IO.Path.GetFileName(b.Kept ?? b.Name + ".broken"), "error");
        // First start: the Going online screen (Online.cs; again when a kind of online use was never asked),
        // then what the scan found on this PC (Scan.cs; once), then Required files when an emulator lacks one
        // (RequiredFiles.cs; once), then a profile's game controls are set up (InputCards.cs). Test runs ask
        // with --online-prompt / --scan-prompt / --files-prompt / --setup.
        if (Profiles.Active == null) return;
        bool script = !string.IsNullOrEmpty(Opt.Script);
        bool askOnline = !Online.AllAsked && (!script || Opt.OnlinePrompt);
        bool askControls = !Settings.Input.SetupDone && (!script || Opt.Setup);
        bool askFiles = !Settings.FilesAsked && (!script || Opt.FilesPrompt);
        bool askScan = !Settings.ScanAsked && (!script || Opt.ScanPrompt);
        // then the tutorial (Tutorial.cs), once per PC, when this start set up a profile's controls
        bool askTutorial = !Settings.TutorialAsked && (script ? Opt.TutorialPrompt : askControls);
        void Controls()
        {
            if (askControls && TopScreen == Home && !Settings.Input.SetupDone) new SetupPromptCard(this).Open();
            if (askTutorial) Tutor.OfferWhenHome();
        }
        void Files()
        {
            if (askFiles && TopScreen == Home && !Settings.FilesAsked && RequiredOverviewCard.Lacking(this).Count > 0)
                new RequiredOverviewCard(this, () => Ui.After(400, Controls)).Open();
            else Controls();
        }
        void Found()
        {
            if (askScan && TopScreen == Home && !Settings.ScanAsked) ScanCard.Run(this, true, () => Ui.After(400, Files));
            else Files();
        }
        if (askOnline || askControls || askFiles || askScan || askTutorial)
            Ui.After(900, () =>
            {
                if (TopScreen != Home) return;
                if (askOnline && !Online.AllAsked) new OnlineCard(this, () => Ui.After(400, Found)).Open();
                else Found();
            });
    }

    // ---- window ----
    // Brightness (Settings > Look and layout): 100 % shows everything as it is; lower lays black over the whole
    // window at (100 - level) %, so every colour, the intro and outro and games in the launcher dim evenly.
    readonly System.Windows.Shapes.Rectangle dim = new() { Fill = Brushes.Black, IsHitTestVisible = false, Opacity = 0 };
    public const int MinBrightness = 40;
    public double Dim => dim.Opacity;   // test screenshots draw the veil too
    public void SetBrightness(int level, bool save = true)
    {
        level = Math.Clamp(level, MinBrightness, 100);
        dim.Opacity = (100 - level) / 100.0;
        if (Settings.Brightness == level && !save) return;
        Settings.Brightness = level;
        if (save) Settings.Save();
    }

    public void SetFullscreen(bool on)
    {
        Settings.Fullscreen = on; Settings.Save();
        ApplyWindowMode(false);
    }

    internal void ApplyWindowMode(bool initial)   // also GameWindow (Display.cs), back to the launcher's own mode after a game
    {
        if (Settings.Fullscreen)
        {
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            if (!initial)
            {
                Width = Opt.Width; Height = Opt.Height;
                Left = Opt.Left ?? (SystemParameters.WorkArea.Width - Width) / 2; Top = Opt.Top ?? (SystemParameters.WorkArea.Height - Height) / 2;
            }
        }
    }

    // Saves made by the cores before profiles: the first profile takes a copy.
    public IEnumerable<string> CoreSaveDirs() => Emus.Data.Emulators.Where(e => e.IsCore && e.Exe != null)
        .Select(e => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(e.Exe), "saves"));

    // Another profile now plays: refresh what shows whose.
    // Discord status (Discord.cs): what the profile lets Discord show right now.
    // Test runs never reach the real Discord: only a fake one named by IONEA_DISCORD_PIPE.
    DiscordPresence discord;
    public DiscordPresence Discord => discord ??= new DiscordPresence(
        string.IsNullOrEmpty(Opt.Script) || Environment.GetEnvironmentVariable("IONEA_DISCORD_PIPE") != null);
    public void RefreshDiscord()
    {
        if (!Settings.Discord || Online.Offline) { Discord.Set(new(true, null, null, false, null)); return; }   // network connection off: no Discord status either
        var g = Launch?.Playing;
        if (g == null) { Discord.Set(new(false, null, null, false, null)); return; }
        bool paused = GameMenu?.Game?.Id == g.Id && GameMenu.Paused;
        Discord.Set(new(false, g.Name, DiscordLine2(g), paused, Launch.PlayingSince, Covers.PublicUrl(g)));
        if (g.CoverUrl == null) Covers.FindUrl(this, g, RefreshDiscord);   // a cover from an older version: its address, once
    }

    // Line 2: the emulator (without " core"), or Steam / PC for games Ionea doesn't emulate or that play
    // through a native port (Ports.cs) right now.
    string DiscordLine2(Game g)
    {
        if (g.Source == "steam") return "Steam";
        if (g.Source != "rom" || Launch.PlayingNative) return "PC";
        string n = Emus.Get(g.Emulator)?.Name;
        if (string.IsNullOrWhiteSpace(n)) return null;
        return n.EndsWith(" core", StringComparison.OrdinalIgnoreCase) ? n[..^5] : n;
    }

    public void ProfileChanged()
    {
        RefreshDiscord();
        Home?.RefreshProfile();
        StartScr?.RefreshProfile();
    }

    // Each profile keeps its own look, layout, order, folders, sounds and input (data\profiles\<id>\settings.json).
    // A profile made now starts from the defaults; the first one keeps what was set before it.
    void ApplyProfile(bool first)
    {
        var p = Profiles.Active;
        if (p == null) { Settings.ProfileFile = null; return; }
        string file = Profiles.SettingsFile(p);
        bool wasDark = Settings.Dark;
        string wasShape = Art.IconShape, wasColours = P.Preset.Id;
        ProfilePrefs prefs = System.IO.File.Exists(file) ? Json.Load<ProfilePrefs>(file)
            : p.Id == Profiles.Data.Owner ? ProfilePrefs.From(Settings, ProfilePrefs.FoldersOf(Lib))
            : new ProfilePrefs();
        Settings.ProfileFile = null;          // nothing is written while the two are half swapped
        prefs.ApplyTo(Settings, Lib);
        Settings.ProfileFile = file;
        Lib.Save();                           // also writes the profile's file
        Settings.Save();
        Sounds.On = Settings.Sounds && !Opt.Mute;
        Anim.SetReduced(Settings.Sound.ReduceEffects);
        Art.IconShape = Settings.IconShape ?? "round";
        if (first) return;
        if (Settings.Dark != wasDark) P.Set(Settings.Dark, true);
        if (ColourPreset.Of(Settings.Colours).Id != wasColours) { P.SetPreset(Settings.Colours, true); Mouse?.Recolour(); }
        if (Art.IconShape != wasShape) Home?.RefreshShape();
        InputChanged();
    }

    public bool CanSwitchProfile(out string why)
    {
        why = GameMenu?.Game != null || Launch?.Running == true ? T("home.close_the_game_first") : null;
        return why == null;
    }

    public void SwitchProfile(Profile p)
    {
        if (!CanSwitchProfile(out string why)) { Toasts.Show(T("home.can_t_switch_now"), why, "error"); return; }
        string was = Profiles.Active?.Id;
        Profiles.Switch(p);
        ProfileChanged();
        if (Profiles.Active?.Id != was) Sounds.Play("profile");   // in the new profile's own sounds
    }

    public void SetDark(bool dark)
    {
        Settings.Dark = dark; Settings.Save();
        P.Set(dark, true);
        Home?.RefreshTheme();
    }

    // Settings > Icon shape: round / rounded / square, kept per profile, shown at once.
    public void SetIconShape(string shape)
    {
        if (Array.IndexOf(Art.Shapes, shape) < 0) shape = "round";
        Settings.IconShape = shape; Settings.Save();
        if (Art.IconShape == shape) return;
        Art.IconShape = shape;
        Home?.RefreshShape();
    }

    // Settings > Colours: the accent palette (rings, glows, highlights, bars, rim), kept per profile, live.
    // Every coloured thing on screen uses the palette's shared brushes and stops, so they change by themselves.
    public void SetColours(string id)
    {
        id = ColourPreset.Of(id).Id;
        Settings.Colours = id; Settings.Save();
        if (P.Preset.Id == id) return;
        P.SetPreset(id, true);
        Mouse?.Recolour();
    }

    public void FocusSelf() { Activate(); Keyboard.Focus(this); }

    public void BringToFront()
    {
        if (Hwnd == IntPtr.Zero) return;
        if (Opt.Windowed == true && !string.IsNullOrEmpty(Opt.Script)) return;   // a test run never takes the foreground
        Native.ForceForeground(Hwnd);
        Activate();
    }

    // ---- controllers: HID arrival / removal for the toast ----
    void HookDevices()
    {
        pads = Devices.List();
        Log.Write($"game controllers at start: {pads.Count}");
        foreach (var guid in new[] { Devices.HidGuid, InputDevices.KeyboardGuid, InputDevices.MouseGuid })
        {
            var f = new Native.DEV_BROADCAST_DEVICEINTERFACE { dbcc_devicetype = 5, dbcc_classguid = guid };
            f.dbcc_size = System.Runtime.InteropServices.Marshal.SizeOf(f);
            IntPtr h = Native.RegisterDeviceNotification(Hwnd, ref f, 0);
            if (h != IntPtr.Zero) devNotify.Add(h);
        }
        HwndSource.FromHwnd(Hwnd).AddHook(WndProc);
    }

    IntPtr WndProc(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == 0x11) sessionEnding = true;                         // WM_QUERYENDSESSION: shutdown / logoff, no outro
        if (msg == 0x16 && w == IntPtr.Zero) sessionEnding = false;    // WM_ENDSESSION, not ending after all
        if (msg == 0xFF) { try { if (!Input.Hid.OnRawInput(l)) Devs.OnRawInput(l); } catch (Exception e) { Log.Write("raw input: " + e.Message); } return IntPtr.Zero; }   // WM_INPUT
        if (msg != 0x219 || l == IntPtr.Zero) return IntPtr.Zero;   // WM_DEVICECHANGE
        try
        {
            int ev = w.ToInt32();
            if ((ev == 0x8000 || ev == 0x8004) && System.Runtime.InteropServices.Marshal.ReadInt32(l, 4) == 5)
            {
                var cls = System.Runtime.InteropServices.Marshal.PtrToStructure<Guid>(l + 12);
                if (cls == InputDevices.KeyboardGuid || cls == InputDevices.MouseGuid)
                {
                    // A keyboard or mouse brings several parts at once: list them again once they've all arrived.
                    if (!devRefreshQueued) { devRefreshQueued = true; Ui.After(700, () => { devRefreshQueued = false; Devs.Refresh(); }); }
                    return IntPtr.Zero;
                }
                string path = System.Runtime.InteropServices.Marshal.PtrToStringUni(l + 28);
                string key = Devices.Key(path);
                if (ev == 0x8000)
                {
                    // Raw input may list the new device a moment after the arrival message; only game
                    // controllers are opened (never headsets or other HID devices).
                    if (!pads.ContainsKey(key))
                        Ui.After(500, () =>
                        {
                            if (!pads.ContainsKey(key) && Devices.IsGamepad(path) && Devices.Describe(path, out string name)) { pads[key] = name; PadEvent(true, name); }
                        });
                }
                else
                {
                    if (pads.Remove(key, out string name)) PadEvent(false, name);
                    Ui.After(300, Input.Hid.Refresh);
                }
            }
        }
        catch (Exception e) { Log.Write("device change: " + e.Message); }
        return IntPtr.Zero;
    }

    public void PadEvent(bool connected, string name)
    {
        Log.Write($"controller {(connected ? "connected" : "disconnected")}: {name}");
        if (Settings.Input.PopupsFor("pad")) Toasts.Show(connected ? T("home.controller_connected") : T("home.controller_disconnected"), name, connected ? "pad-on" : "pad-off");
        // The keyboard was turned off for the controller: with no controller left, it comes back on.
        if (!connected && !Settings.Input.KeyboardOn && !Devs.Connected("pad"))
        {
            Settings.Input.KeyboardOn = true; Settings.Save();
            Toasts.Show(T("home.keyboard_turned_back_on"), T("home.no_controller_is_plugged_in"), "keyboard-on");
        }
        Devs.PadsChanged();
        Home?.RefreshPads();
    }

    public int PadCount => Math.Max(pads.Count, Input.Connected.Count(c => c));
    public IReadOnlyDictionary<string, string> Pads => pads;
}
