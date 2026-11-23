#nullable disable
// Test helpers: --script runs simple commands (one per line or separated by ';') against the live UI,
// and --dummies N fills a test library with generated games. Results go to launcher.log as "SCRIPT".
//   wait 500 | key right 3 | type Text | shot name | screen name | theme dark | zoom small
//   toast Title | addgame C:\path.exe | photo C:\img.png | launch | expect sel 5 | expect bar 0
//   expect screen HomeScreen | expect launch shown | fullscreen on|off | log text | quit
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Ionea;

public sealed class TestScript
{
    readonly MainWindow w;
    readonly Queue<string> lines;
    int fails;
    public static string Dir;   // the script file's folder: "HERE\..." in an argument starts there
    string lastVideoToken;   // applyvideo / restorevideo

    public TestScript(MainWindow win, string script)
    {
        w = win;
        Esde = w.Opt.EsdeDir;
        if (Environment.GetEnvironmentVariable("IONEA_KEYLOG") == "1") { w.PreviewKeyDown += (s, e) => Log.Write($"KEYLOG down {e.Key} sys={e.SystemKey} rep={e.IsRepeat}"); w.PreviewTextInput += (s, e) => Log.Write($"KEYLOG char '{e.Text}'"); }
        lines = new Queue<string>(script.Split('\n', ';').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("#")));
    }

    public void Run() => Ui.After(400, Step);

    void Step()
    {
        if (lines.Count == 0) { Log.Write($"SCRIPT done, {fails} failed"); return; }
        string line = lines.Dequeue();
        string cmd = line.Split(' ')[0].ToLowerInvariant();
        string arg = line.Length > cmd.Length ? line[(cmd.Length + 1)..].Trim() : "";
        if (Dir != null && arg.StartsWith("HERE\\")) arg = Path.Combine(Dir, arg[5..]);   // paths next to the script (test\bin\TestGame.exe is HERE\bin\TestGame.exe)
        if (SteamLib.RootOverride != null && arg.StartsWith("FAKE\\")) arg = Path.Combine(SteamLib.RootOverride, arg[5..]);   // test paths inside the fake Steam folder
        arg = TP(arg);   // test paths inside the setup script's folders (T\..., T2\...)
        double next = 60;
        try
        {
            switch (cmd)
            {
                case "wait": next = double.Parse(arg); break;
                case "key":
                {
                    var p = arg.Split(' ');
                    var act = Enum.Parse<Act>(p[0], true);
                    int n = p.Length > 1 ? int.Parse(p[1]) : 1;
                    // Accept goes through like a real Enter / A: held while the press is handled, let go right after.
                    for (int i = 0; i < n; i++)
                    {
                        bool held = w.ScriptHeld;
                        if (act == Act.Accept) w.ScriptHeld = true;
                        w.Dispatch(act);
                        w.ScriptHeld = held;
                    }
                    next = 120 + (w.Home?.PressLeftMs ?? 0);   // a press effect holds input until it has played
                    break;
                }
                case "pad":   // pad <act> [n]: as a controller's button (the on-screen keyboard gets it first)
                {
                    var p = arg.Split(' ');
                    var act = Enum.Parse<Act>(p[0], true);
                    int n = p.Length > 1 ? int.Parse(p[1]) : 1;
                    for (int i = 0; i < n; i++) w.PadAct(act);
                    next = 120;
                    break;
                }
                case "oskkey":   // oskkey <label>: press one key of the on-screen keyboard (Space, Done, ⌫ / back, ⇧ / shift)
                {
                    string k = arg.ToLowerInvariant() switch { "back" => Osk.Back, "shift" => Osk.Shift, "left" => Osk.Left, "right" => Osk.Right, _ => arg };
                    if (!w.Osk.IsOpen) Fail("oskkey: the keyboard is not open");
                    else if (!w.Osk.PressKey(k)) Fail("oskkey: no key " + arg);
                    break;
                }
                // ---- the launcher's file browser (Picker.cs) ----
                case "pick":   // pick <file|folder> <start folder> [.ext,.ext]: open the browser as a screen asking for one would
                {
                    var q = arg.Split(' ', 3);
                    string start = TP(q[1]);
                    string[] exts = q.Length > 2 ? q[2].Split(',') : null;
                    Picker.ForceBrowser = true;
                    picked = "none";
                    if (q[0] == "folder") Picker.Folder(w, "Choose a folder", start, p => picked = p);
                    else Picker.File(w, "Choose a file", null, exts, start, p => picked = p);
                    Picker.ForceBrowser = false;
                    next = 400; break;
                }
                case "browse":   // browse <row label>: choose a row of the file browser
                    if (FileBrowserCard.Current == null) Fail("browse: no file browser");
                    else if (!FileBrowserCard.Current.Choose(arg)) Fail("browse: no row " + arg);
                    next = 300; break;
                case "tap":   // tap <act>: one action without waiting for a press effect (to test that input is held)
                    w.Dispatch(Enum.Parse<Act>(arg, true)); next = 20; break;
                case "type":
                    if (Keyboard() is TextBox tb) { tb.Text = arg; tb.CaretIndex = arg.Length; } else Fail("no text box for: " + arg);
                    break;
                case "shot": Shot(arg); break;
                case "screen": ScreenShot(arg); break;
                case "coreshot":   // coreshot <name>: the running core's own picture (fails when there's none or it's all black)
                {
                    var cs = w.GameMenu.Core;
                    int lit = cs == null ? -1 : cs.SaveFrame(ShotPath(arg));
                    Log.Write($"SCRIPT coreshot {arg}: {(cs == null ? "no core" : lit < 0 ? "no picture" : $"{lit} lit pixels")}");
                    if (lit <= 0) Fail("coreshot: no picture");
                    break;
                }
                case "theme": w.SetDark(arg == "dark"); next = 700; break;
                case "brightness": w.SetBrightness(int.Parse(arg)); next = 100; break;   // brightness <40-100>
                case "shape": w.SetIconShape(arg); next = 700; break;
                case "colours": w.SetColours(arg); next = 700; break;   // colours <id>: Settings > Colours (ionea, aurora, sunset, blossom, meadow, twilight, older one-colour ids move to the nearest)   // shape <round|rounded|square>: Settings > Icon shape
                case "intro": w.ReplayIntro(); next = arg.Length > 0 ? int.Parse(arg) : (int)(IntroScreen.OutAt + IntroScreen.OutMs) + 200; break;   // intro [ms]: the start-up intro again (Settings > Watch the intro), next line after ms (default: when it has ended)
                case "zoom": w.Home.SetZoom(Enum.Parse<Zoom>(arg, true)); next = 700; break;
                case "toast": w.Toasts.Show(arg, "Test", "pad-on"); break;
                case "addgame": new AddGameFlow(w).Start(arg); next = 400; break;
                case "photo":
                    if (w.TopScreen is AddGameCard ac) ac.SetPhotoForTest(arg);
                    else if (w.TopScreen is NewProfileScreen nps) nps.SetPicture(arg);
                    else if (w.TopScreen is ProfileCard pc) pc.SetPicture(arg);
                    else Fail("photo: no add-game or profile screen");
                    break;
                // ---- profiles ----
                case "newprofile": new NewProfileScreen(w, false, np => w.SwitchProfile(np)).Open(); next = 400; break;
                case "switchprofile": { var sp = w.Profiles.ByName(arg); if (sp == null) Fail("no profile " + arg); else w.SwitchProfile(sp); next = 300; break; }
                case "profilecard": new ProfileCard(w).Open(); next = 400; break;
                case "listcard": new ListTestCard(w).Open(); next = 400; break;   // a long list (k5, x1)
                case "whocard": new WhoCard(w, np => w.SwitchProfile(np)).Open(); next = 400; break;
                case "helper":   // helper <game name>|<program>|<args>: the game's helper (Helper.cs); "helper <game>|none" removes it
                {
                    var hp = arg.Split('|');
                    var hg = w.Lib.Data.Games.FirstOrDefault(g => g.Name == hp[0]);
                    if (hg == null) { Fail("helper: no game " + hp[0]); break; }
                    hg.Helper = hp.Length < 2 || hp[1] == "none" ? null : new GameHelper { Program = TP(hp[1]), Args = hp.Length > 2 ? string.Join("|", hp[2..]).Replace("T\\", (Environment.GetEnvironmentVariable("IONEA_TESTROOT") ?? "") + "\\") : null };
                    w.Lib.Save();
                    break;
                }
                case "play":   // play <game name>|<seconds>[|<days ago>[|<hour>]]: as if the active profile had played it
                {
                    var pp = arg.Split('|');
                    var pg = w.Lib.Data.Games.FirstOrDefault(g => g.Name == pp[0]);
                    var at = DateTime.Now;
                    if (pp.Length > 2) at = DateTime.Today.AddDays(-int.Parse(pp[2])).Add(pp.Length > 3 ? TimeSpan.FromHours(double.Parse(pp[3], System.Globalization.CultureInfo.InvariantCulture)) : DateTime.Now.TimeOfDay);
                    if (pg == null) Fail("play: no game " + pp[0]); else w.Profiles.Record(pg, at, double.Parse(pp[1], System.Globalization.CultureInfo.InvariantCulture));
                    break;
                }
                case "statscard": new ProfileStatsCard(w).Open(); next = 400; break;   // Play stats
                case "romgame":   // romgame <name>|<game file>|<emulator name>: straight into the library
                {
                    var q = arg.Split('|');
                    var emu = w.Emus.Data.Emulators.FirstOrDefault(e => e.Name == q[2]);
                    if (emu == null) { Fail("romgame: no emulator " + q[2]); break; }
                    w.Lib.Data.Games.Add(new Game { Id = Library.NewId(), Name = q[0], Source = "rom", Rom = TP(q[1]), Emulator = emu.Id, Added = DateTime.Now });
                    w.Lib.Save(); w.Home?.Rebuild(null); next = 300;
                    break;
                }
                case "coverurl":   // coverurl <game>|<address>|<cover from>: the game's cover has this public address (Covers.PublicUrl)
                {
                    var q = arg.Split('|');
                    var cg = w.Lib.Data.Games.FirstOrDefault(g => g.Name == q[0]);
                    if (cg == null) { Fail("coverurl: no game " + q[0]); break; }
                    cg.CoverUrl = q[1];
                    if (q.Length > 2) cg.CoverFrom = q[2];
                    w.Lib.Save();
                    break;
                }
                case "findurl":   // findurl <game>: work out a cover's public address now (Covers.FindUrl), as a game start does
                {
                    var fg = w.Lib.Data.Games.FirstOrDefault(g => g.Name == arg);
                    if (fg == null) { Fail("findurl: no game " + arg); break; }
                    Covers.FindUrl(w, fg, null); next = 1500;
                    break;
                }
                case "playwith":   // playwith <game>|emulator / ask / <port name>: the active profile's answer (Ports.cs)
                {
                    var q = arg.Split('|');
                    var pg = w.Lib.Data.Games.FirstOrDefault(g => g.Name == q[0]);
                    if (pg == null) { Fail("playwith: no game " + q[0]); break; }
                    string c = q[1] == "ask" ? null : q[1] == "emulator" ? Ports.EmulatorChoice
                        : w.Emus.Data.Emulators.FirstOrDefault(e => e.Name == q[1])?.Id;
                    if (c == null && q[1] != "ask") { Fail("playwith: no port " + q[1]); break; }
                    Ports.SetChoice(w, pg, c);
                    break;
                }
                case "profilewrite":   // profilewrite <path under the active profile's folder>|<text>|<minutes old>: a made-up save file
                {
                    var q = arg.Split('|');
                    string f = Path.Combine(w.Profiles.Folder(w.Profiles.Active), q[0]);
                    Directory.CreateDirectory(Path.GetDirectoryName(f));
                    File.WriteAllText(f, q[1]);
                    if (q.Length > 2) File.SetLastWriteTime(f, DateTime.Now.AddMinutes(-double.Parse(q[2])));
                    break;
                }
                case "gamesaves":   // gamesaves <game>|shared/separate: Game info > Saves (syncs at once, as the row does)
                {
                    var q = arg.Split('|');
                    var sg = w.Lib.Data.Games.FirstOrDefault(g => g.Name == q[0]);
                    if (sg == null || Ports.ShareOf(w, sg) == null) { Fail("gamesaves: no shared-save game " + q[0]); break; }
                    Ports.SetSaves(w, sg, q[1] == "shared");
                    break;
                }
                case "syncsaves":   // syncsaves <game>: the port's and the emulator's saves synced, as a start or end does
                {
                    var sg = w.Lib.Data.Games.FirstOrDefault(g => g.Name == arg);
                    if (sg == null) { Fail("syncsaves: no game " + arg); break; }
                    Ports.SyncSaves(w, sg, "test");
                    break;
                }
                case "syncaddons":   // syncaddons <game>: its emulator's add-ons linked for its first port, as a native start does
                {
                    var ag = w.Lib.Data.Games.FirstOrDefault(g => g.Name == arg);
                    var am = ag == null ? null : Ports.For(w, ag).FirstOrDefault();
                    if (am == null) { Fail("syncaddons: no game or port " + arg); break; }
                    Ports.SyncAddons(w, ag, am, "test");
                    break;
                }
                case "prepare":   // prepare <game>|<emulator name>: put the active profile's saves in place, as a start would
                {
                    var q = arg.Split('|');
                    var pg = w.Lib.Data.Games.FirstOrDefault(g => g.Name == q[0]);
                    var emu = w.Emus.Data.Emulators.FirstOrDefault(e => e.Name == q[1]);
                    if (pg == null || emu == null) { Fail("prepare: no game or emulator: " + arg); break; }
                    string err = ProfileSaves.Prepare(w.Profiles, emu, pg.Rom, out bool shared);
                    if (err != null) Fail("prepare: " + err); else Log.Write($"SCRIPT prepared {q[0]} for {w.Profiles.Active?.Name}{(shared ? " (shared)" : "")}");
                    break;
                }
                case "iniset":   // iniset <file>|<[Section]key>|<value>: as the emulator changing its own setting while it runs
                {
                    var q = arg.Split('|');
                    string f = TP(q[0]), text = File.ReadAllText(f);
                    var ini = new IniText(text, text.Contains(" = ") ? " = " : "=");
                    var (sec, key) = Tweaks.SplitKey(q[1]);
                    ini.Set(sec, key, q[2]);
                    File.WriteAllText(f, ini.Text);
                    break;
                }
                case "quiet":   // quiet <definition id>|<program>|<args>: its quiet switches written, as its start does (Quiet.cs); expect quiethas
                {
                    var q = arg.Split('|', 3);
                    var qe = new Emulator { Id = "test-" + q[0], Name = q[0], Preset = q[0], Exe = TP(q[1]) };
                    var qpsi = new System.Diagnostics.ProcessStartInfo(qe.Exe, q.Length > 2 ? q[2] : "") { UseShellExecute = false };
                    qpsi.Environment.Clear();
                    var qcore = new Dictionary<string, string>();
                    if (qe.IsCore) Quiet.Core(qe.Exe, qcore); else Quiet.Apply(qe, qpsi);
                    lastQuiet = $"args: {qpsi.Arguments} env: {string.Join(" ", qpsi.Environment.Select(kv => kv.Key + "=" + kv.Value))} core: {string.Join(" ", qcore.Select(kv => kv.Key + "=" + kv.Value))}";
                    Log.Write("SCRIPT quiet " + q[0] + ": " + lastQuiet);
                    break;
                }
                case "writefile": { var q = arg.Split('|', 2); Directory.CreateDirectory(Path.GetDirectoryName(TP(q[0]))); File.WriteAllText(TP(q[0]), q[1]); break; }
                case "profilelog":
                    Log.Write($"SCRIPT profiles: {string.Join(" | ", w.Profiles.List.Select(x => $"{x.Name} ({x.Id}{(x.Id == w.Profiles.Active?.Id ? ", active" : "")}{(x.Picture ? ", picture" : "")})"))} beside: {string.Join(", ", w.Profiles.Data.Beside.Select(kv => kv.Key + "=" + kv.Value))}");
                    break;
                case "mem":   // mem <note>: memory in use and home's cover reads, for big-library timing (perf-home)
                {
                    long managed = GC.GetTotalMemory(true) >> 20;
                    using var pr = System.Diagnostics.Process.GetCurrentProcess();
                    Log.Write($"SCRIPT mem {arg}: {managed} MB managed, {pr.PrivateMemorySize64 >> 20} MB private, {pr.WorkingSet64 >> 20} MB working set, home tiles {w.Home?.Count}, covers read {HomeScreen.CoverReads}, cached {HomeScreen.CoverCached}");
                    break;
                }
                case "select": w.Home.Rebuild(w.Lib.Data.Games.First(g => g.Name == arg).Id); next = 400; break;
                case "folderfill":   // folderfill <folder>|<n>: the first n games on home (A to Z) go into that folder
                {
                    var q = arg.Split('|', 2);
                    foreach (var g in w.Lib.HomeGames("name").Take(int.Parse(q[1]))) g.Folder = q[0];
                    w.Lib.Save(); w.Home.Rebuild(null); next = 400; break;
                }
                case "steampick": new SteamPickCard(w).Open(); next = 400; break;
                case "tutorial":   // tutorial start|stop|tick: the tutorial (Tutorial.cs) without its welcome card; tick = look again now
                    if (arg == "start") w.Tutor.Begin(); else if (arg == "stop") w.Tutor.Stop("script"); else w.Tutor.Update();
                    next = 300; break;
                case "addrom": new AddRomFlow(w).Start(arg); next = 400; break;
                case "addchoice": new AddChoiceCard(w).Open(); next = 400; break;
                case "addemu": EmuPick.AddExe(w, arg); next = 400; break;
                case "installemu": EmuPick.Install(w, arg); next = 800; break;
                case "emulators": new EmulatorsCard(w).Open(); next = 400; break;
                case "discover": EmuPick.Announce(w, w.Emus.Discover()); next = 400; break;
                case "emulog": Log.Write("SCRIPT emulators: " + string.Join(" | ", w.Emus.Sorted().Select(e => $"{e.Name} [{string.Join(",", e.Systems)}] {e.Exe} {e.Args}"))); break;
                case "realkeys":   // real key events through Windows (keybd_event), not direct typing
                    if (Native.GetForegroundWindow() != w.Hwnd) Native.ForceForeground(w.Hwnd);
                    if (Native.GetForegroundWindow() != w.Hwnd) Fail("realkeys: launcher is not in front");
                    foreach (char c in arg.ToUpperInvariant())
                    {
                        byte vk = (byte)(c == ' ' ? 0x20 : c);
                        byte sc = (byte)MapVirtualKey(vk, 0);
                        keybd_event(vk, sc, 0, UIntPtr.Zero); keybd_event(vk, sc, 2, UIntPtr.Zero);
                    }
                    next = 500;
                    break;
                case "realesc":   // a real Esc key press through Windows
                    if (Native.GetForegroundWindow() != w.Hwnd) Native.ForceForeground(w.Hwnd);
                    keybd_event(0x1B, (byte)MapVirtualKey(0x1B, 0), 0, UIntPtr.Zero); keybd_event(0x1B, (byte)MapVirtualKey(0x1B, 0), 2, UIntPtr.Zero);
                    next = 900;
                    break;
                case "logtext": Log.Write($"SCRIPT text '{(Keyboard() as TextBox)?.Text}' in {w.TopScreen?.GetType().Name}"); break;
                case "search": w.Home.OpenSearch(); next = 600; break;
                case "steamlist": Log.Write("SCRIPT steam games: " + string.Join(" | ", SteamLib.Installed().Select(g => $"{g.AppId} {g.Name}"))); break;
                case "waitlaunch":   // waitlaunch <seconds>: until the launch effect is gone and the game has ended
                {
                    var until = DateTime.Now.AddSeconds(double.Parse(arg));
                    void Poll() { if ((w.Launch.Busy || w.Launch.Running) && DateTime.Now < until) Ui.After(250, Poll); else Ui.After(300, Step); }
                    Poll(); return;
                }
                case "waitshown":    // waitshown <seconds>: until the launch effect is gone (the game keeps running)
                {
                    var until = DateTime.Now.AddSeconds(double.Parse(arg));
                    void Poll() { if (w.Launch.Busy && DateTime.Now < until) Ui.After(250, Poll); else Ui.After(300, Step); }
                    Poll(); return;
                }
                case "closegame": w.GameMenu.CloseGame(); next = 500; break;
                case "closewin": w.Close(); next = 60; break;   // as Alt+F4 / the close button; the script goes on while the outro holds the close
                case "waitoutro":    // waitoutro <seconds>: until the outro plays (a game open first closes behind its closing screen)
                {
                    var until = DateTime.Now.AddSeconds(double.Parse(arg, CultureInfo.InvariantCulture));
                    void Poll() { if (!OutroScreen.Playing && DateTime.Now < until) Ui.After(20, Poll); else Step(); }
                    Poll(); return;
                }
                case "hold":   // hold <ms>: A held down that long on the selected game (no real keys)
                {
                    int ms = int.Parse(arg);
                    w.ScriptHeld = true;
                    w.Dispatch(Act.Accept);
                    Ui.After(ms, () => { w.ScriptHeld = false; Ui.After(HomeScreen.HoldMs + 200 > ms ? 300 : 150, Step); });
                    return;
                }
                case "latency":   // latency <presses>: A pressed in the running hosted game that often (LatencyProbe.cs), each held 250 ms
                {
                    int left = int.Parse(arg);
                    var rnd = new Random(1);
                    LatencyProbe.Reset(); LatencyProbe.On = true;
                    string F(double x) => double.IsNaN(x) ? "-" : x.ToString("0.0", CultureInfo.InvariantCulture);
                    void Next()
                    {
                        if (left-- <= 0)
                        {
                            Log.Write("SCRIPT latency: " + LatencyProbe.Summary());
                            LatencyProbe.On = false; Ui.After(100, Step); return;
                        }
                        Ui.After(300 + rnd.Next(300), () =>
                        {
                            LatencyProbe.Press(8);   // libretro A
                            Ui.After(250, () =>
                            {
                                var r = LatencyProbe.Release(8);
                                Log.Write($"latency: pressed at {LatencyProbe.PressedAt}: read {F(r[0])}, frame {F(r[1])}, picture {F(r[2])}, sound made {F(r[3])}, sound out {F(r[4])} ms");
                                Next();
                            });
                        });
                    }
                    Next(); return;
                }
                case "directpicture": DirectPicture.Allowed = arg != "off"; next = 300; break;   // directpicture on|off: a hosted game's picture straight to the screen, or WPF only (DirectPicture.cs)
                case "streaming": w.Settings.StreamingMode = arg == "on"; w.Settings.Save(); break;   // streaming on|off: Settings > Look > In games > Streaming mode (from the next game started)
                case "holdmenu": w.Home.OpenHoldMenuForTest(); next = 500; break;
                // ---- input devices (no real keys or mouse: everything is called directly) ----
                case "devices":   // devices none: ignore the real keyboards / mice, use fakedevice ones
                    w.Devs.TestOnly = arg == "none"; w.Devs.Fake.Clear(); w.Devs.Refresh(false); next = 200; break;
                case "fakedevice":   // fakedevice <keyboard|mouse|pad> <on|off> <name>
                {
                    var q = arg.Split(' ', 3);
                    w.Devs.Fake.RemoveAll(d => d.Kind == q[0] && d.Name == q[2]);
                    if (q[1] == "on") w.Devs.Fake.Add(new InputDevices.Dev(q[0], "fake:" + q[0] + ":" + q[2], q[2]));
                    if (q[0] == "pad") w.PadEvent(q[1] == "on", q[2]);   // as a controller arriving / leaving
                    else w.Devs.Refresh(true);
                    next = 300; break;
                }
                case "rawkey":   // rawkey <WPF key name>: a key as the screen in front takes it (binding), else as a launcher key
                {
                    var k = Enum.Parse<System.Windows.Input.Key>(arg, true);
                    if (!(w.TopScreen?.TakeKey(k) ?? false)) Log.Write("SCRIPT rawkey not taken: " + arg);
                    next = 200; break;
                }
                case "input": new InputCard(w).Open(); next = 400; break;
                case "devicescreen": DeviceScreen.For(w, arg, null, Scope).Open(); next = 400; break;   // devicescreen <keyboard|pad|mouse>, for "controls"
                case "focusitem": if (!(DeviceScreen.Current?.FocusOn(arg) ?? false)) Fail("focusitem: no " + arg); next = 200; break;
                case "padpress":   // padpress <button id>: as if a controller button were pressed while a binding waits
                {
                    var pb = InputMap.Button(arg);
                    var cap = w.Input.Capture;
                    if (cap == null) { Fail("padpress: nothing waiting for a button"); break; }
                    cap(0, 0, 0);
                    w.Input.Capture?.Invoke(arg == "lt" || arg == "rt" ? (ushort)0 : pb.Mask, (byte)(arg == "lt" ? 255 : 0), (byte)(arg == "rt" ? 255 : 0));
                    next = 200; break;
                }
                case "mouse":   // mouse <move|down|up|right|wheelup|wheeldown> <x> <y>: stage coordinates, as the pointer routes them
                {
                    var q = arg.Split(' ');
                    var m = Enum.Parse<MouseAct>(q[0], true);
                    var pt = q.Length > 2 ? new Point(double.Parse(q[1], CultureInfo.InvariantCulture), double.Parse(q[2], CultureInfo.InvariantCulture)) : new Point(-1, -1);
                    if (m == MouseAct.Down) w.Mouse.Held = true;
                    if (m == MouseAct.Up) w.Mouse.Held = false;
                    bool used = w.Mouse.Route(m, pt);
                    if (m == MouseAct.Move) w.Mouse.Tips.At(pt); else w.Mouse.Tips.Hide();   // as the pointer does
                    if (!used && m == MouseAct.Right) w.Dispatch(Act.Back);
                    if (!used && m is MouseAct.WheelUp or MouseAct.WheelDown) w.Dispatch(m == MouseAct.WheelUp ? Act.Up : Act.Down);
                    Log.Write($"SCRIPT mouse {arg}: {(used ? "used" : "not used")}");
                    next = 250 + (w.Home?.PressLeftMs ?? 0); break;
                }
                case "mousepart":   // mousepart <move|down> <part>: at the middle of a key / button on a device screen's drawing ("MouseMiddle", "K", "a", "wheel")
                {
                    var q = arg.Split(' ');
                    if (DeviceScreen.Current?.PartOnStage(q[1]) is not Point pt) { Fail("mousepart: no " + q[1]); break; }
                    var m = Enum.Parse<MouseAct>(q[0], true);
                    if (m == MouseAct.Down) w.Mouse.Held = true;
                    bool used = w.Mouse.Route(m, pt);
                    if (m == MouseAct.Down) Ui.After(60, () => w.Mouse.Held = false);
                    Log.Write($"SCRIPT mousepart {arg} at {pt.X:0},{pt.Y:0}: {(used ? "used" : "not used")}");
                    next = 250; break;
                }
                case "mouseitem":   // mouseitem <move|down|up|click> <id>: at the middle of a device screen's tab / chip / row; click = down, then up as a real click sends
                {
                    var q = arg.Split(' ');
                    if (DeviceScreen.Current?.ItemOnStage(q[1]) is not Point pt) { Fail("mouseitem: no " + q[1]); break; }
                    bool click = q[0].Equals("click", StringComparison.OrdinalIgnoreCase);
                    var m = click ? MouseAct.Down : Enum.Parse<MouseAct>(q[0], true);
                    if (m == MouseAct.Down) w.Mouse.Held = true;
                    if (m == MouseAct.Up) w.Mouse.Held = false;
                    bool used = w.Mouse.Route(m, pt);
                    if (click) { w.Mouse.Held = false; used |= w.Mouse.Route(MouseAct.Up, pt); }
                    Log.Write($"SCRIPT mouseitem {arg} at {pt.X:0},{pt.Y:0}: {(used ? "used" : "not used")}");
                    next = 250; break;
                }
                case "mouserow":   // mouserow <move|down|wheeldown|wheelup> <label>: at the middle of a menu card's row, or of a choice in its open list
                {
                    var q = arg.Split(' ', 2);
                    if ((w.TopScreen as MenuCard)?.CentreOf(q[1]) is not Point pt) { Fail("mouserow: no " + q[1]); break; }
                    var m = Enum.Parse<MouseAct>(q[0], true);
                    if (m == MouseAct.Down) w.Mouse.Held = true;
                    bool used = w.Mouse.Route(m, pt);
                    if (m == MouseAct.Down) Ui.After(60, () => w.Mouse.Held = false);
                    Log.Write($"SCRIPT mouserow {arg} at {pt.X:0},{pt.Y:0}: {(used ? "used" : "not used")}");
                    next = 300; break;
                }
                case "realmouse":   // realmouse <move|wheelup|wheeldown|down> <dx> <dy> <label>: as a real mouse sends it (wheel lock included), at a menu row / list choice's middle moved by dx, dy px
                {
                    var q = arg.Split(' ', 4);
                    if ((w.TopScreen as MenuCard)?.CentreOf(q[3]) is not Point c) { Fail("realmouse: no " + q[3]); break; }
                    var pt = new Point(c.X + double.Parse(q[1], CultureInfo.InvariantCulture), c.Y + double.Parse(q[2], CultureInfo.InvariantCulture));
                    var m = Enum.Parse<MouseAct>(q[0], true);
                    bool used = false;
                    if (m == MouseAct.Move) { if (w.Mouse.HoverAllowed(pt)) used = w.Mouse.Route(m, pt); else Log.Write("SCRIPT realmouse: hover held by the wheel"); }
                    else
                    {
                        if (m is MouseAct.WheelUp or MouseAct.WheelDown) w.Mouse.NoteWheel(pt);
                        if (m == MouseAct.Down) { w.Mouse.EndWheelLock(); w.Mouse.Held = true; }
                        used = w.Mouse.Route(m, pt);
                        if (!used && m is MouseAct.WheelUp or MouseAct.WheelDown) w.Dispatch(m == MouseAct.WheelUp ? Act.Up : Act.Down);
                        if (m == MouseAct.Down) Ui.After(60, () => w.Mouse.Held = false);
                    }
                    Log.Write($"SCRIPT realmouse {arg} at {pt.X:0},{pt.Y:0}: {(used ? "used" : "not used")}");
                    next = 40; break;
                }
                case "mousepick":   // mousepick <move|down> <row|done>: at the middle of a shown game's row (or the button) in Add container's game list
                {
                    var q = arg.Split(' ');
                    if ((w.TopScreen as ContainerPickCard)?.CentreOf(q[1]) is not Point pt) { Fail("mousepick: no " + q[1]); break; }
                    var m = Enum.Parse<MouseAct>(q[0], true);
                    if (m == MouseAct.Down) w.Mouse.Held = true;
                    bool used = w.Mouse.Route(m, pt);
                    if (m == MouseAct.Down) Ui.After(60, () => w.Mouse.Held = false);
                    Log.Write($"SCRIPT mousepick {arg} at {pt.X:0},{pt.Y:0}: {(used ? "used" : "not used")}");
                    next = 250; break;
                }
                case "mousetile":   // mousetile <move|down|right> <index>: at the centre of a home tile
                {
                    var q = arg.Split(' ');
                    var pt = w.Home.TileCentre(int.Parse(q[1]));
                    var m = Enum.Parse<MouseAct>(q[0], true);
                    if (m == MouseAct.Down) w.Mouse.Held = true;
                    w.Mouse.Route(m, pt);
                    if (m == MouseAct.Down) Ui.After(60, () => w.Mouse.Held = false);   // a click, not a hold
                    next = 300 + (w.Home?.PressLeftMs ?? 0); break;
                }
                case "mousetilefresh":   // mousetilefresh <index>: the row rebuilt, then a click at that tile's place at once, before a layout pass (a click that brings the launcher back to the front)
                {
                    var pt = w.Home.TileCentre(int.Parse(arg));
                    w.Home.Rebuild(null);
                    w.Mouse.Held = true;
                    bool used = w.Mouse.Route(MouseAct.Down, pt);
                    Ui.After(60, () => w.Mouse.Held = false);
                    Log.Write($"SCRIPT mousetilefresh {arg}: {(used ? "used" : "not used")}, pressing {(w.Home.PressLeftMs > 0 || w.Home.Holding ? "yes" : "no")}");
                    next = 300 + (w.Home?.PressLeftMs ?? 0); break;
                }
                case "rebuildin":   // rebuildin <ms>: the home row rebuilt after that long (as a game ending or the launcher coming back does), the script goes on at once
                {
                    Ui.After(double.Parse(arg, CultureInfo.InvariantCulture), () => { Log.Write("SCRIPT rebuild now"); w.Home?.Rebuild(null); });
                    next = 1; break;
                }
                case "mousebar":   // mousebar <move|down> <index>: at the centre of an options-bar button
                {
                    var q = arg.Split(' ');
                    var pt = w.Home.BarCentre(int.Parse(q[1]));
                    var m = Enum.Parse<MouseAct>(q[0], true);
                    if (m == MouseAct.Down) w.Mouse.Held = true;
                    w.Mouse.Route(m, pt);
                    if (m == MouseAct.Down) Ui.After(60, () => w.Mouse.Held = false);
                    next = 300 + (w.Home?.PressLeftMs ?? 0); break;
                }
                case "mousechip":   // mousechip <move|down>: at the middle of the profile picture, top left of home
                {
                    var pt = w.Home.ChipCentre();
                    var m = Enum.Parse<MouseAct>(arg.Trim(), true);
                    if (m == MouseAct.Down) w.Mouse.Held = true;
                    w.Mouse.Route(m, pt);
                    if (m == MouseAct.Down) Ui.After(60, () => w.Mouse.Held = false);
                    next = 300; break;
                }
                case "mousehold":   // mousehold <index> <ms>: left button held on a home tile
                {
                    var q = arg.Split(' ');
                    var pt = w.Home.TileCentre(int.Parse(q[0]));
                    w.Mouse.Held = true;
                    w.Mouse.Route(MouseAct.Down, pt);
                    int ms = int.Parse(q[1]);
                    Ui.After(ms, () => { w.Mouse.Held = false; w.Mouse.Route(MouseAct.Up, pt); Ui.After(400, Step); });
                    return;
                }
                case "inputset":   // inputset <prop> <value>: a Settings > Input value (true/false/number)
                {
                    var q = arg.Split(' ');
                    var pr = typeof(InputPrefs).GetProperty(q[0]);
                    pr.SetValue(w.Settings.Input, pr.PropertyType == typeof(bool) ? bool.Parse(q[1]) : pr.PropertyType == typeof(string) ? q[1] : (object)int.Parse(q[1]));
                    w.Settings.Save(); w.InputChanged(); next = 150; break;
                }
                case "sounds": new SoundCard(w).Open(); next = 400; break;
                case "listpick": if (!((w.TopScreen as MenuCard)?.PickForTest(arg) ?? false)) Fail("listpick: no " + arg); next = 300; break;   // listpick <choice>: as if clicked in the open list
                case "cardrow": if (!((w.TopScreen as MenuCard)?.FocusRow(arg) ?? false)) Fail("cardrow: no " + arg); next = 200; break;   // cardrow <label>: focus that row of the card in front
                case "closebutton":   // closebutton: a click on the X corner button of the card in front
                {
                    if (w.TopScreen is not Card xcard) { Fail("closebutton: no card in front"); break; }
                    var cp = xcard.CloseButtonForTest;
                    w.Mouse.Route(MouseAct.Move, cp); w.Mouse.Held = true; w.Mouse.Route(MouseAct.Down, cp); w.Mouse.Held = false; w.Mouse.Route(MouseAct.Up, cp);
                    next = 400; break;
                }
                case "settings": new SettingsCard(w, string.IsNullOrWhiteSpace(arg) ? null : arg).Open(); next = 400; break;   // settings [category]
                case "soundvol": w.Settings.Sound.Volume = int.Parse(arg); w.Settings.Save(); break;
                case "soundgroup": { var q = arg.Split(' '); w.Settings.Sound.SetGroup(q[0], int.Parse(q[1])); w.Settings.Save(); break; }
                // sound: soundkind nav|act <0-100>, soundtheme <id>, reduce on|off, soundmark (forget the clips heard so far)
                case "soundkind": { var q = arg.Split(' '); w.Settings.Sound.SetKind(q[0], int.Parse(q[1])); w.Settings.Save(); break; }
                case "soundtheme": w.Settings.Sound.Theme = arg; w.Settings.Save(); Sounds.Preload(); next = 200; break;
                case "reduce": w.Settings.Sound.ReduceEffects = arg == "on"; w.Settings.Save(); Anim.SetReduced(arg == "on"); break;
                case "soundmark": lock (Sounds.Played) Sounds.Played.Clear(); break;
                case "order": w.Settings.Order = arg; w.Settings.Save(); w.Home?.Rebuild(null); next = 300; break;
                case "infolder":   // infolder <game>|<folder> (no folder: back on the home row)
                {
                    var q = arg.Split('|', 2);
                    var g = w.Lib.Data.Games.First(x => x.Name == q[0]);
                    g.Folder = q.Length > 1 && q[1].Length > 0 ? q[1] : null; w.Lib.Save(); w.Home?.Rebuild(null); next = 300; break;
                }
                case "setmousespeed": w.Settings.Input.SetMouseSpeed(Scope, int.Parse(arg)); w.Settings.Save(); w.InputChanged(); break;
                case "setmousecam": w.Settings.Input.SetMouseCamera(Scope, arg == "on"); w.Settings.Save(); w.InputChanged(); break;   // setmousecam on|off
                case "setkey": { var q = arg.Split(' '); w.Settings.Input.SetKey(Scope, q[0], InputMap.ValidBind(q[1])); w.Settings.Save(); w.InputChanged(); break; }
                case "controls":
                {
                    var q = arg.Split(' ', 2);
                    var cg = q.Length > 1 ? w.Lib.Data.Games.FirstOrDefault(x => x.Name == q[1]) : null;
                    Scope = new ControlScope(q[0], cg?.Id, cg?.Name);
                    break;
                }
                case "mousebind":   // mousebind <Left|Right|Middle|XButton1|XButton2>: as if that mouse button were pressed while a key binding waits
                    if (!(w.TopScreen?.TakeMouse(Enum.Parse<System.Windows.Input.MouseButton>(arg)) ?? false)) Fail("mousebind: not taken: " + arg);
                    next = 200; break;
                case "gamesystem":   // gamesystem <game name>|<system id>
                {
                    var q = arg.Split('|', 2);
                    w.Lib.Data.Games.First(x => x.Name == q[0]).System = q[1]; w.Lib.Save(); break;
                }
                case "controlsfor":   // controlsfor <game name>: the in-game Controls row, as the Esc menu opens it
                    ControlsCard.OpenFor(w, w.Lib.Data.Games.First(x => x.Name == arg), false); next = 400; break;
                case "setup": new SetupPromptCard(w).Open(); next = 400; break;   // the first-start controls setup
                case "setpad": { var q = arg.Split(' '); w.Settings.Input.SetPad(Scope, q[0], q[1]); w.Settings.Save(); w.InputChanged(); break; }
                case "launchtoast":   // the "detected your …" pop-up, as a launch shows it
                    w.Launch.DevicesToastForTest(); next = 300; break;
                case "writeps4":   // writeps4 <emulator name>
                    InputMap.WritePs4(w.Emus.Data.Emulators.First(x => x.Name == arg), w.Settings.Input, Scope); break;
                case "writecontrols":   // writecontrols <emulator name>: as a game start writes them, for the "controls" scope
                    Log.Write("SCRIPT writecontrols " + arg + ": " + (EmuControls.Write(w.Emus.Data.Emulators.First(x => x.Name == arg), w.Settings.Input, Scope) ? "changed" : "unchanged")); break;
                case "inside":   // inside <emulator name>|on|off: its "Play inside Ionea" row
                {
                    var q = arg.Split('|');
                    w.Emus.Data.Emulators.First(x => x.Name == q[0]).Inside = q[1] == "on" ? null : false; w.Emus.Save(); break;
                }
                case "probecore":   // probecore <dll>: what the core lists as its settings
                {
                    var opts = CoreCatalog.Get(arg);
                    Log.Write($"SCRIPT probe {Path.GetFileName(arg)}: {opts.Count} options: {string.Join(", ", opts.Select(o => o.Key + "=" + o.Default + "[" + o.Values.Count + "]"))}");
                    break;
                }
                case "tweakset":   // tweakset <key>|<value or default>: on the open tweaks card
                {
                    var p = arg.Split('|');
                    if (w.TopScreen is TweaksCard tc) tc.SetForTest(p[0], p[1]);
                    else Fail("tweakset: no tweaks card, top is " + w.TopScreen?.GetType().Name);
                    break;
                }
                case "tweakmore":   // (no More page any more, every setting is on the card in its box)
                    if (w.TopScreen is not TweaksCard) Fail("tweakmore: no tweaks card, top is " + w.TopScreen?.GetType().Name);
                    break;
                case "applyvideo":   // applyvideo <game>: its own video values into the emulator's file, as its start does
                {
                    var g = w.Lib.Data.Games.First(x => x.Name == arg);
                    lastVideoToken = Tweaks.ApplyGame(w.Emus.Get(g.Emulator), g);
                    Log.Write("SCRIPT applyvideo " + arg + ": " + (lastVideoToken != null ? "applied" : "nothing"));
                    break;
                }
                case "restorevideo":   // restorevideo <emulator name>: as the last applyvideo's game ending
                    Tweaks.Restore(w.Emus.Data.Emulators.First(x => x.Name == arg), lastVideoToken); break;
                case "restoreleft": Tweaks.RestoreLeft(w.Emus.Data.Emulators.Where(e => e.Present)); break;   // as the launcher's start does
                case "displaycard":   // displaycard <game> or global: the Display card (a running game's: as its in-game menu opens it)
                {
                    var dg = arg == "global" ? null : w.Lib.Data.Games.FirstOrDefault(x => x.Name == arg);
                    if (arg != "global" && dg == null) { Fail("displaycard: no game " + arg); break; }
                    new DisplayCard(w, dg, dg != null && w.GameMenu.Game?.Id == dg.Id).Open(); next = 400; break;
                }
                case "changelog": new ChangelogCard(w).Open(); next = 400; break;   // the Changelog card (Settings > About)
                case "addmenu": new AddMenuCard(w).Open(); next = 400; break;   // Add ("+" on Home)
                case "toastmark": Toasts.Sounded = Toasts.Hushed = 0; break;
                case "sleep": w.Sleep.Set(arg == "on"); if (arg != "on") Anim.PauseLoops(false); next = 300; break;   // sleep on|off: as when the launcher loses / gets the front (Sleep.cs)
                case "displayset":   // displayset <row label>|<shown choice>: on the open Display card, as picking it from the row's list
                {
                    var q = arg.Split('|', 2);
                    if (w.TopScreen is not DisplayCard dc) Fail("displayset: no Display card, top is " + w.TopScreen?.GetType().Name);
                    else if (!dc.SetForTest(q[0], q[1])) Fail($"displayset: no choice {q[1]} in {q[0]}");
                    next = 300; break;
                }
                case "overlaycard":   // overlaycard <game> or global: the Performance overlay card (a running game's: as its Home menu opens it)
                {
                    var og = arg == "global" ? null : w.Lib.Data.Games.FirstOrDefault(x => x.Name == arg);
                    if (arg != "global" && og == null) { Fail("overlaycard: no game " + arg); break; }
                    new OverlayCard(w, og, og != null && w.GameMenu.Game?.Id == og.Id).Open(); next = 400; break;
                }
                case "overlayset":   // overlayset <row label>|<shown choice>: on the open Performance overlay card, as picking it from the row's list
                {
                    var q = arg.Split('|', 2);
                    if (w.TopScreen is not OverlayCard oc) Fail("overlayset: no Performance overlay card, top is " + w.TopScreen?.GetType().Name);
                    else if (!oc.SetForTest(q[0], q[1])) Fail($"overlayset: no choice {q[1]} in {q[0]}");
                    next = 300; break;
                }
                case "overlaylog": Log.Write("SCRIPT overlay: " + (w.Overlay?.LastText ?? "none")); break;   // every value the performance overlay shows now
                case "gametweaks":   // gametweaks <game>: the tweaks card for it, as the quick menu opens it
                {
                    var g = w.Lib.Data.Games.First(x => x.Name == arg);
                    if (Tweaks.For(w, g) is TweakSet ts) new TweaksCard(w, g, ts).Open(); else Fail($"gametweaks: none for {arg}: {Tweaks.Why(w, g)}");
                    next = 400; break;
                }
                case "gamesettings":   // gamesettings <game>: Game settings as the hold menu opens it (a choice first when both are installed)
                {
                    var g = w.Lib.Data.Games.First(x => x.Name == arg);
                    if (GameSettings.Any(w, g)) GameSettings.Open(w, g); else Fail($"gamesettings: none for {arg}: {GameSettings.Why(w, g)}");
                    next = 400; break;
                }
                case "setemu":   // setemu <game>|<emulator name>: which emulator plays it (as the game menu's Emulator row)
                {
                    var p = arg.Split('|');
                    w.Lib.Data.Games.First(x => x.Name == p[0]).Emulator = w.Emus.Data.Emulators.First(x => x.Name == p[1]).Id;
                    w.Lib.Save();
                    break;
                }
                case "emutweaks":   // emutweaks <emulator name>
                {
                    var e = w.Emus.Data.Emulators.First(x => x.Name == arg);
                    if (Tweaks.ForEmu(e) is TweakSet ts) new TweaksCard(w, null, ts).Open(); else Fail("emutweaks: none for " + arg);
                    next = 400; break;
                }
                case "move":   // move <game>|<place name>: move a game's files, as Stored on does
                {
                    var q = arg.Split('|');
                    var g = w.Lib.Data.Games.First(x => x.Name == q[0]);
                    var pl = Storage.Places(w).First(x => x.Name == q[1]);
                    new MoveCard(w, new List<Game> { g }, pl).Open(); next = 300; break;
                }
                case "moveall":   // moveall <from place>|<to place>
                {
                    var q = arg.Split('|');
                    var from = Storage.Places(w).First(x => x.Name == q[0]);
                    var pl = Storage.Places(w).First(x => x.Name == q[1]);
                    var list = w.Lib.Data.Games.Where(x => !x.Removed && Storage.CanMove(x) && Storage.PlaceOf(w, x.Rom) == from).ToList();
                    new MoveCard(w, list, pl).Open(); next = 300; break;
                }
                case "cancelmove": (w.TopScreen as MoveCard)?.OnAct(Act.Back); next = 300; break;
                case "storage": new StorageCard(w).Open(); next = 500; break;
                case "waitmove":   // waitmove <seconds>
                {
                    var until = DateTime.Now.AddSeconds(double.Parse(arg));
                    void Poll() { if (MoveCard.Busy && DateTime.Now < until) Ui.After(100, Poll); else Ui.After(300, Step); }
                    Poll(); return;
                }
                case "homebutton": w.GameMenu.Pressed(); next = 900; break;   // same as the Home key / View + Start
                // ---- launcher keys (LauncherKeys.cs); no real keys ----
                case "homekey":   // the Home menu key pressed, as HomeKeys would pass it on (only while something claims it)
                    if (!w.HomeKeys.Press()) Log.Write("SCRIPT homekey: not claimed now");
                    next = 900; break;
                case "hooksim":   // hooksim <down|up> <WPF key>: a key event through the Home menu key's logic (expect hooksim ...)
                {
                    var q = arg.Split(' ');
                    var hk = Enum.Parse<System.Windows.Input.Key>(q[1], true);
                    Log.Write($"SCRIPT hooksim {arg}: {w.HomeKeys.Simulate(System.Windows.Input.KeyInterop.VirtualKeyFromKey(hk), q[0] == "down")}");
                    next = 150; break;
                }
                case "launcherkey":   // launcherkey <action> <key,key|default>
                {
                    var q = arg.Split(' ', 2);
                    var lp = w.Settings.Input;
                    lp.LauncherKeys ??= new();
                    if (q[1] == "default") lp.LauncherKeys.Remove(q[0]); else lp.LauncherKeys[q[0]] = q[1].Split(',').ToList();
                    LauncherKeys.Changed(w);
                    next = 200; break;
                }
                case "keydown": w.KeyPressed(Enum.Parse<System.Windows.Input.Key>(arg, true), false); next = 200; break;   // keydown <WPF key>: as the launcher's window gets it (no real keys)
                case "keyup": w.KeyReleased(Enum.Parse<System.Windows.Input.Key>(arg, true)); next = 200; break;
                case "pretendfront": GameMenu.TestFront = arg switch { "on" => true, "off" => false, _ => null }; w.GameMenu.KeysChanged(); next = 200; break;   // pretendfront <on|off|real>: the launcher is in front / nothing is / the real front (the Home menu key's claim)
                case "homeclaim":   // homeclaim <on|off>: the test claims the Home menu key (as a game in front would), for "hooksim"
                    w.HomeKeys.Set(this, arg == "on" ? LauncherKeys.HomeVks(w.Settings.Input, true) : null, null, () => { homeFires++; Log.Write("SCRIPT home key fired"); });
                    next = 150; break;
                case "launcherkeysreset": LauncherKeys.Reset(w.Settings.Input); LauncherKeys.Changed(w); next = 200; break;
                case "launcherkeys": new LauncherKeysCard(w, arg == "" ? null : arg).Open(); next = 400; break;   // launcherkeys [action to focus]
                case "launcherkeycard": new LauncherKeyCard(w, arg).Open(); next = 400; break;
                case "keyheld": EscHold.ScriptHeld = arg == "none" ? null : Enum.Parse<System.Windows.Input.Key>(arg, true); next = 100; break;   // keyheld <key|none>
                case "restack":   // the launcher's window re-stacked on top without taking the front (as Alt+Tab back does); fails if it blocks
                {
                    var done = new System.Threading.ManualResetEventSlim();
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    System.Threading.ThreadPool.QueueUserWorkItem(_ => { if (!done.Wait(3000)) Log.Write("SCRIPT FAIL restack: the launcher's window blocked for over 3 s"); });
                    SetWindowPos(w.Hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);   // NOSIZE | NOMOVE | NOACTIVATE
                    done.Set();
                    if (sw.ElapsedMilliseconds > 3000) fails++;
                    Log.Write($"SCRIPT restack took {sw.ElapsedMilliseconds} ms");
                    next = 300; break;
                }
                // Going online (Online.cs)
                case "online":   // online <item> on|off: its switch, as Settings > Going online changes it
                { var q = arg.Split(' '); Online.Set(q[0], q[1] == "on"); break; }
                case "onlineforget": Online.Forget(arg); break;
                case "offline": Online.SetOffline(arg == "on"); break;   // offline on|off: Offline mode, as Settings > Network changes it
                // updates (Updater.cs): updatefolder <dir|default> | updatekind folder|repo | updaterepo <url|none>
                // | updatecheck (waits for it) | updatenow (no question; waits; logs SCRIPT done before a restart)
                // | updateback (Go back to the previous version, restarts) | updategame on|off (as if a game were running)
                case "updatefolder": w.Settings.Updates.Folder = arg == "default" ? null : TP(arg); w.Settings.Save(); Updater.Forget(); break;
                case "updatekind": w.Settings.Updates.Kind = arg; w.Settings.Save(); Updater.Forget(); break;
                case "updaterepo": w.Settings.Updates.Repo = arg == "none" ? null : arg; w.Settings.Save(); Updater.Forget(); break;
                // updatetoken env:<NAME> | none: the access token from an environment variable (never written in a script)
                case "updatetoken":
                    if (arg == "none") Updater.SetToken(null);
                    else if (arg.StartsWith("env:") && Environment.GetEnvironmentVariable(arg[4..]) is string tv && tv.Length > 0) Updater.SetToken(tv);
                    else Fail("updatetoken: give env:<NAME> (set) or none");
                    break;
                case "updategame": Updater.TestGameRunning = arg == "on"; break;
                case "updatecheck":
                {
                    _ = Updater.Check(w);
                    var until = DateTime.Now.AddSeconds(60);
                    void Poll() { if (Updater.Busy && DateTime.Now < until) Ui.After(100, Poll); else Ui.After(200, Step); }
                    Ui.After(100, Poll); return;
                }
                case "updatenow":
                {
                    Updater.BeforeRestart = () => Log.Write($"SCRIPT done, {fails} failed");
                    Updater.UpdateNow(w, confirmed: true);
                    var until = DateTime.Now.AddMinutes(11);
                    void Poll()
                    {
                        if (Program.RestartExe != null) return;   // restarting: this run's script ends here
                        if (UpdatingCard.Working && DateTime.Now < until) Ui.After(250, Poll); else Ui.After(1000, () => { if (Program.RestartExe == null) Step(); });
                    }
                    Ui.After(250, Poll); return;
                }
                case "updateback":
                {
                    if (Updater.GameRunning(w)) { Log.Write("update: go back refused, a game is running"); break; }
                    string why = Updater.SwapBack();
                    if (why != null) { Fail("updateback: " + why); break; }
                    Log.Write($"SCRIPT done, {fails} failed");
                    Updater.Restart(w); return;
                }   // onlineforget <item>: as if that item were new in this version
                case "onlinecard": new OnlineCard(w).Open(); next = 400; break;
                // required files: reqfiles <emulator name> | reqpick <emulator name>|<path>[|<entry name>] | reqoverview
                case "reqfiles": new RequiredFilesCard(w, EmuNamed(arg)).Open(); next = 400; break;
                case "reqpick": { var q = arg.Split('|'); RequiredFilesCard.PickForTest(w, EmuNamed(q[0]), TP(q[1]), q.Length > 2 ? q[2] : null); next = 600; break; }
                case "reqoverview": new RequiredOverviewCard(w).Open(); next = 400; break;
                // game info: gameinfo <game> | addonpick <path> | coverpick <image> | waitaddon <seconds>
                case "gameinfo":
                {
                    var g = w.Lib.Data.Games.First(x => x.Name == arg);
                    new GameInfoCard(w, g).Open();
                    var until = DateTime.Now.AddSeconds(20);
                    void Poll() { if ((GameInfoCard.Current?.Loading ?? false) && DateTime.Now < until) Ui.After(100, Poll); else Ui.After(400, Step); }
                    Ui.After(200, Poll); return;
                }
                case "addonpick": if (GameInfoCard.Current is GameInfoCard gc) gc.Add(TP(arg)); else Fail("addonpick: no Game info"); next = 300; break;
                case "coverpick": if (GameInfoCard.Current is GameInfoCard cc) cc.Cover(TP(arg)); else Fail("coverpick: no Game info"); next = 500; break;
                // cover zoom: coveradjust <game> opens Adjust cover; covermouse <act> <dx> <dy> at the preview's middle + (dx, dy);
                // coverpad <lt> <rt> <rx> <ry> <ms>: a pad's triggers and right stick held that long (0..1, -1..1)
                case "coveradjust": new CoverAdjustCard(w, w.Lib.Data.Games.First(x => x.Name == arg)).Open(); next = 400; break;
                case "covermouse":
                {
                    if (CoverAdjustCard.Current is not CoverAdjustCard ca) { Fail("covermouse: no Adjust cover"); break; }
                    var q = arg.Split(' ');
                    var m = Enum.Parse<MouseAct>(q[0], true);
                    var c = ca.PreviewCentre;
                    var pt = new Point(c.X + double.Parse(q[1], CultureInfo.InvariantCulture), c.Y + double.Parse(q[2], CultureInfo.InvariantCulture));
                    if (m == MouseAct.Down) w.Mouse.Held = true;
                    if (m == MouseAct.Up) w.Mouse.Held = false;
                    bool used = w.Mouse.Route(m, pt);
                    Log.Write($"SCRIPT covermouse {arg}: {(used ? "used" : "not used")} -> {ca.View}");
                    next = 150; break;
                }
                case "coverpad":
                {
                    if (CoverAdjustCard.Current is not CoverAdjustCard ca) { Fail("coverpad: no Adjust cover"); break; }
                    var q = arg.Split(' ').Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
                    ca.TestPad = (q[0], q[1], q[2], q[3]);
                    Ui.After(q[4], () => { ca.TestPad = null; Log.Write("SCRIPT coverpad done -> " + ca.View); Ui.After(100, Step); });
                    return;
                }
                case "waitaddon":
                {
                    var until = DateTime.Now.AddSeconds(double.Parse(arg));
                    void Poll() { if (AddonCard.Busy && DateTime.Now < until) Ui.After(100, Poll); else Ui.After(400, Step); }
                    Ui.After(200, Poll); return;
                }
                case "emucard": new EmulatorCard(w, EmuNamed(arg)).Open(); next = 400; break;   // emucard <emulator name>
                // the "Ionea manages controls" switch, "Open the emulator" and its close
                case "emucontrols":   // emucontrols <emulator name>|on|off|unset: as the switch (unset: an entry from before it existed)
                {
                    var q = arg.Split('|');
                    var ce = EmuNamed(q[0]);
                    ce.Controls = q[1] == "on" ? true : q[1] == "off" ? false : null;
                    w.Emus.Save();
                    break;
                }
                case "openemu":   // openemu <emulator name>: as its page's "Open the emulator" (the page comes back when it closes)
                {
                    var oe = EmuNamed(arg);
                    (w.TopScreen as MenuCard)?.Close(false);
                    EmuOpen.Start(w, oe, () => new EmulatorCard(w, oe, "Open the emulator").Open());
                    next = 600; break;
                }
                case "closeemu": EmuOpen.CloseNow(); next = 300; break;   // as the Home key while it is open
                case "waitemu":   // waitemu <seconds>: until the emulator opened with openemu has closed
                {
                    var until = DateTime.Now.AddSeconds(double.Parse(arg));
                    void Poll() { if (EmuOpen.OpenName != null && DateTime.Now < until) Ui.After(250, Poll); else Ui.After(500, Step); }
                    Poll(); return;
                }
                case "waitemuin":   // waitemuin <seconds>: until its window is inside the launcher
                {
                    var until = DateTime.Now.AddSeconds(double.Parse(arg));
                    void Poll() { if (EmuOpen.Window == null && DateTime.Now < until) Ui.After(200, Poll); else Ui.After(300, Step); }
                    Poll(); return;
                }
                case "reqsupply": ReqFiles.SupplyAll(); break;
                case "onlinelog": new OnlineLogCard(w).Open(); next = 400; break;
                case "download":   // download <item>|<address>|<file>[|<name>]: through the one download path; waitdownload waits for it
                {
                    var q = arg.Split('|');
                    _ = Online.Download(w, q[0], q[1], TP(q[2]), q.Length > 3 ? q[3] : null);
                    next = 400; break;
                }
                case "waitdownload":   // waitdownload <seconds>
                {
                    var until = DateTime.Now.AddSeconds(double.Parse(arg, CultureInfo.InvariantCulture));
                    void Poll() { if (Online.Pending > 0 && DateTime.Now < until) Ui.After(100, Poll); else Ui.After(200, Step); }
                    Poll(); return;
                }
                case "fullscreen": w.SetFullscreen(arg == "on"); next = 600; break;
                // the scan, covers, the emulator download
                case "scan": ScanCard.Run(w, arg == "first", null, arg == "deep"); next = 300; break;   // scan [first|deep]
                case "waitscan":   // waitscan <seconds>: until the results are shown
                {
                    var until = DateTime.Now.AddSeconds(double.Parse(arg, CultureInfo.InvariantCulture));
                    void Poll() { if (ScanCard.Current?.Ready != true && DateTime.Now < until) Ui.After(100, Poll); else Ui.After(300, Step); }
                    Poll(); return;
                }
                case "scantoggle": if (!(ScanCard.Current?.Toggle(arg) ?? false)) Fail("scantoggle: no " + arg); next = 200; break;
                case "scanconfirm": if (ScanCard.Current is ScanCard scc) scc.Confirm(); else Fail("scanconfirm: no results"); next = 600; break;
                case "scanlog": Log.Write("SCRIPT scan: " + (ScanCard.Current?.Summary ?? "none")); break;
                case "coverfetch": Covers.Queue(w); next = 300; break;   // the background fetch now
                case "waitcovers":   // waitcovers <seconds>
                {
                    var until = DateTime.Now.AddSeconds(double.Parse(arg, CultureInfo.InvariantCulture));
                    void Poll() { if ((Covers.Busy || Online.Pending > 0) && DateTime.Now < until) Ui.After(100, Poll); else Ui.After(300, Step); }
                    Ui.After(200, Poll); return;
                }
                case "covercard": { var cg = w.Lib.Data.Games.First(g => g.Name == arg); new CoverPickCard(w, cg, null).Open(); next = 400; break; }
                case "coverpicksrc": if (!(CoverPickCard.Current?.PickSource(arg) ?? false)) Fail("coverpicksrc: no " + arg); next = 500; break;
                case "waitcovercard":
                {
                    var until = DateTime.Now.AddSeconds(double.Parse(arg, CultureInfo.InvariantCulture));
                    void Poll() { if (CoverPickCard.Current?.Fetching == true && DateTime.Now < until) Ui.After(100, Poll); else Ui.After(300, Step); }
                    Ui.After(200, Poll); return;
                }
                case "sgdbkey": w.Settings.SteamGridKey = string.IsNullOrWhiteSpace(arg) ? null : arg; w.Settings.Save(); break;
                case "emuinstall": new EmuInstallCard(w).Get(EmuDefs.ById(arg)); next = 400; break;   // emuinstall <definition id>
                case "waitemuinstall":
                {
                    var until = DateTime.Now.AddSeconds(double.Parse(arg, CultureInfo.InvariantCulture));
                    void Poll() { if (EmuInstall.Busy && DateTime.Now < until) Ui.After(100, Poll); else Ui.After(300, Step); }
                    Ui.After(200, Poll); return;
                }
                case "log": Log.Write("SCRIPT " + arg); break;
                case "expect": Expect(arg); break;
                case "quit": Log.Write($"SCRIPT done, {fails} failed"); w.Close(); return;
                default: Fail("unknown command: " + line); break;
            }
        }
        catch (Exception e) { Fail($"{line}: {e.Message}"); }
        Ui.After(next, Step);
    }

    // Test paths: T\... is inside the setup script's folder (IONEA_TESTROOT), T2\... inside a second one,
    // for example on another drive (IONEA_TESTROOT2). ESDE\... is inside ES-DE's folder (IONEA_ESDE, else the
    // run's --esde / Settings.Paths.Esde) and INBOX\... inside the Inbox (IONEA_INBOX, else --inbox / the setting).
    static string TP(string s) =>
        Environment.GetEnvironmentVariable("IONEA_TESTROOT2") is string root2 && s.StartsWith("T2\\") ? Path.Combine(root2, s[3..]) :
        Environment.GetEnvironmentVariable("IONEA_TESTROOT") is string root && s.StartsWith("T\\") ? Path.Combine(root, s[2..]) :
        s.StartsWith("ESDE\\") && Root("IONEA_ESDE", Esde) is string esde ? Path.Combine(esde, s[5..]) :
        s.StartsWith("INBOX\\") && Root("IONEA_INBOX", AddGameLogic.Inbox) is string inbox ? Path.Combine(inbox, s[6..]) : s;

    public static string Esde;   // this run's ES-DE folder (Options.EsdeDir), for ESDE\... paths
    static string Root(string env, string fallback) => Environment.GetEnvironmentVariable(env) is string v && v.Length > 0 ? v : fallback;

    static object Keyboard() => System.Windows.Input.Keyboard.FocusedElement;
    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint type);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    string lastQuiet = "";
    void Fail(string s) { fails++; Log.Write("SCRIPT FAIL " + s); }
    string picked = "none";
    Emulator EmuNamed(string name) => w.Emus.Data.Emulators.FirstOrDefault(e => e.Name == name) ?? throw new InvalidOperationException("no emulator " + name);
    // reqstate <emulator>|<entry>: Ready / Missing / Optional; reqmissing <emulator>: how many needed ones are missing
    string ReqStateOf(string want)
    {
        var q = want.Split('|');
        var e = w.Emus.Data.Emulators.FirstOrDefault(x => x.Name == q[0]);
        if (e == null) return "no emulator";
        var f = ReqFiles.Of(e).FirstOrDefault(x => x.Name == q[1]);
        return f == null ? "no entry" : q[0] + "|" + q[1] + "|" + ReqFiles.StateName(ReqFiles.State(e, f));
    }
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);

    // controls <system> [game name]: whose controls setkey / setpad / binding / devicescreen use (default: Game Boy Advance)
    public static ControlScope Scope = new("gba");
    int homeFires;
    string InputBinding(string id) => id + " " + (w.Settings.Input.KeyFor(Scope, id) ?? "none");

    // ps4ini <emulator name>|<text>: the shadPS4 key file contains the text (or "none" when there is no file)
    string Ps4IniHas(string want)
    {
        var q = want.Split('|', 2);
        var e = w.Emus.Data.Emulators.FirstOrDefault(x => x.Name == q[0]);
        if (e == null) return "no emulator";
        string f = Path.Combine(Path.GetDirectoryName(e.Exe), "user", "input_config", "default.ini");
        if (!File.Exists(f)) return "no file";
        return File.ReadAllText(f).Contains(q[1]) ? want : "not in file";
    }

    void Expect(string arg)
    {
        var p = arg.Split(' ', 2);
        string what = p[0], want = p.Length > 1 ? p[1] : "";
        // test paths inside the setup script's folder (want stays as written, so the log shows it)
        string T(string s) => TP(s);
        string got = what switch
        {
            "sel" => w.Home?.Selected.ToString(),
            "hinttext" => w.Home?.HintText ?? "none",
            "selname" => w.Home?.SelectedName ?? "none",
            "bar" => w.Home?.BarFocus.ToString(),
            "name" => w.Home?.SelectedName,
            "screen" => w.TopScreen?.GetType().Name,
            "launch" => w.Launch.LastResult,
            "count" => w.Home?.Count.ToString(),
            "games" => w.Lib.Data.Games.Count(g => !g.Removed).ToString(),
            "removed" => w.Lib.Data.Games.Count(g => g.Removed).ToString(),
            "view" => w.Home?.View,
            "folderpage" => $"{w.Home?.FolderPage}/{w.Home?.FolderPages}",
            "folderlook" => w.Home?.FolderLook,
            "closefx" => w.Home?.CloseFx,
            "closeto" => w.Home?.CloseTarget,
            "setting" => w.Profiles.Active?.Name,
            "profile" => w.Profiles.Active?.Name,
            "profilecount" => w.Profiles.Data.Profiles.Count.ToString(),
            "profilepic" => w.Profiles.Active?.Picture == true ? "yes" : "no",
            "playtime" => Math.Round(w.Profiles.Stats.Games.Values.Sum(x => x.Seconds)).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "mostplayed" => w.Profiles.Summarize(w.Lib).Most?.Name,
            "monthcount" => w.Profiles.Summarize(w.Lib).MonthCount.ToString(),
            "pstat" => want.Split('|')[0] + "|" + (ProfileStatsCard.Current?.Stat(want.Split('|')[0]) ?? "no card"),   // pstat <label>|<value>[ | <line under>]
            "chip" => w.Home?.OnChip == true ? "yes" : "no",
            "whofocus" => (w.TopScreen as WhoCard)?.FocusName,
            "coresave" => w.Profiles.Active == null ? null : ProfileSaves.CoreSave(w.Profiles, w.Lib.Data.Games.FirstOrDefault(g => g.Name == w.Home?.SelectedName)?.Rom ?? "x"),
            "text" => (Keyboard() as TextBox)?.Text,
            "tip" => w.Mouse.Tips.Shown ?? "none",   // tip <whole text>: the text shown over text cut short under the mouse (TextTip.cs)
            "quiethas" => lastQuiet.Contains(want) ? want : "has: " + lastQuiet,
            "filehas" => want.Split('|', 2) is var fh && File.Exists(T(fh[0])) ? (File.ReadAllText(T(fh[0])).Contains(fh[1]) ? want : "has: " + File.ReadAllText(T(fh[0])).Trim()) : "missing",
            "filelacks" => want.Split('|', 2) is var fl && File.Exists(T(fl[0])) ? (File.ReadAllText(T(fl[0])).Contains(fl[1]) ? "has: " + File.ReadAllText(T(fl[0])).Trim() : want) : "missing",   // filelacks <file>|<text>: the file is there without the text
            "link" => Directory.Exists(T(want)) && new DirectoryInfo(T(want)).Attributes.HasFlag(FileAttributes.ReparsePoint) ? want : "not a link",
            "pickcount" => (w.TopScreen as ContainerPickCard)?.Count.ToString(),
            "picked" => (w.TopScreen as ContainerPickCard)?.Picked.ToString(),
            "pickfocus" => (w.TopScreen as ContainerPickCard)?.FocusName,
            "steamcount" => (w.TopScreen as SteamPickCard)?.Count.ToString(),
            "steammarked" => (w.TopScreen as SteamPickCard)?.Marked.ToString(),
            "steamfocus" => (w.TopScreen as SteamPickCard)?.FocusName,
            "appid" => w.Lib.Data.Games.FirstOrDefault(g => g.Name == w.Home?.SelectedName)?.AppId,
            "source" => w.Lib.Data.Games.FirstOrDefault(g => g.Name == w.Home?.SelectedName)?.Source,
            "cover" => w.Lib.Data.Games.FirstOrDefault(g => g.Name == w.Home?.SelectedName)?.Cover != null ? "yes" : "no",
            // coverview <game>: the saved zoom|x|y; adjustview: what the open Adjust cover card shows; tileview <game>: the home tile's shown part
            "coverview" => want.Split('|')[0] + "|" + CoverView.Describe(w.Lib.Data.Games.FirstOrDefault(g => g.Name == want.Split('|')[0])),
            "adjustview" => CoverAdjustCard.Current?.View ?? "none",
            "tileview" => w.Home?.TileView(want.Split('|')[0]) is string tv ? want.Split('|')[0] + "|" + tv : "no tile",
            "played" => w.Lib.Data.Games.FirstOrDefault(g => g.Name == w.Home?.SelectedName)?.LastPlayed != null ? "yes" : "no",
            "soundopens" => SoundMixer.Opens.ToString(),
            "soundunderruns" => SoundMixer.Underruns.ToString(),
            "soundqueue" => SoundMixer.Queued.ToString(),
            "mousespeed" => w.Settings.Input.MouseSpeed(Scope).ToString(),
            "mousecurve" => InputMap.CheckMouseCurve(),
            "pressing" => (w.Home?.PressLeftMs > 0) ? "yes" : "no",
            "emus" => w.Emus.Data.Emulators.Count.ToString(),
            // the scan, covers, the emulator download
            "scanrows" => ScanCard.Current?.Summary ?? "none",
            "scannot" => ScanCard.Current?.Summary is string sn && !sn.Contains(want) ? want : "found in: " + (ScanCard.Current?.Summary ?? "none"),
            "scanhas" => ScanCard.Current?.Summary is string ss && ss.Contains(want) ? want : "not in: " + (ScanCard.Current?.Summary ?? "none"),
            "requests" => Online.Requests.ToString(),
            "updatestate" => Updater.TestState,                  // notchecked | uptodate | available <version> | failed | refused
            "updatedetail" => (Updater.Detail ?? "").Contains(want) ? want : Updater.Detail ?? "none",   // updatedetail <part of the sentence>
            "version" => AppVersion.Full.ToString(),
            "previousversion" => Updater.PreviousVersion?.ToString() ?? "none",
            "changelog" => Updater.Changelog().text is string cl && cl.Contains(want) ? want : "not in the changelog",
            "coverfrom" => w.Lib.Data.Games.FirstOrDefault(g => g.Name == want.Split('|')[0]) is Game cfg ? cfg.Name + "|" + (cfg.CoverFrom ?? "none") + "|" + (cfg.Cover != null && File.Exists(w.Lib.CoverPath(cfg) ?? "") ? "cover" : "no cover") : "no game",
            "gamesystem" => w.Lib.Data.Games.FirstOrDefault(g => g.Name == want.Split('|')[0]) is Game gsg ? gsg.Name + "|" + (gsg.System ?? "none") : "no game",
            "gamefolder" => w.Settings.Paths.Games.Any(f => string.Equals(f.Path, T(want).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) ? want : "not a game folder",
            "esde" => w.Settings.Paths.Esde is string ed ? (string.Equals(ed, T(want), StringComparison.OrdinalIgnoreCase) ? want : ed) : "none",
            "inbox" => AddGameLogic.Inbox ?? "none",
            "emufolders" => w.Emus.Folders.Count + "|" + w.Emus.InstallDir,
            "coversources" => CoverPickCard.Current?.Sources ?? "no card",
            "emuinstall" => EmuInstallCard.LastResult ?? "none",
            "coreoffer" => CoreOfferCard.LastResult ?? "none",
            "systemof" => want.Split('|')[0] + "|" + (GameId.SystemOf(T(want.Split('|')[0])) ?? "none"),
            "reqstate" => ReqStateOf(want),
            "norow" => (w.TopScreen as MenuCard)?.ValueOf(want) == null ? want : "has a row",
            "reqmissing" => w.Emus.Data.Emulators.FirstOrDefault(x => x.Name == want.Split('|')[0]) is Emulator rm ? rm.Name + "|" + ReqFiles.Missing(rm) : "no emulator",
            "newroms" => NewRoms.Find(w).Count.ToString(),
            "emu" => w.Emus.Get(w.Lib.Data.Games.FirstOrDefault(g => g.Name == w.Home?.SelectedName)?.Emulator)?.Name,
            // definitions: an emulator's support level, a definition's merged name, a game file's id
            "emulevel" => w.Emus.Data.Emulators.FirstOrDefault(e => e.Name == want.Split('|')[0]) is Emulator el ? el.Name + "|" + EmuDefs.LevelName(EmuDefs.Level(el)) : "no emulator",
            // <emulator>|on/off/unset, <emulator>|<Support row>, the emulator open now, its window inside
            "emucontrols" => w.Emus.Data.Emulators.FirstOrDefault(e => e.Name == want.Split('|')[0]) is Emulator ec ? ec.Name + "|" + (ec.Controls == true ? "on" : ec.Controls == false ? "off" : "unset") : "no emulator",
            "support" => w.Emus.Data.Emulators.FirstOrDefault(e => e.Name == want.Split('|')[0]) is Emulator es ? es.Name + "|" + EmuDefs.Support(es) : "no emulator",
            "emuopen" => EmuOpen.OpenName ?? "none",
            "emuinside" => EmuOpen.Window != null ? "yes" : "no",
            "defname" => EmuDefs.ById(want.Split('|')[0]) is EmuDef dn ? dn.Id + "|" + dn.Name : "no definition",
            // id|<required files>|<with an md5>|<source field set>
            "deffiles" => EmuDefs.ById(want.Split('|')[0]) is EmuDef df ? df.Id + "|" + (df.RequiredFiles?.Count ?? 0) + "|" + (df.RequiredFiles?.Count(f => f.Md5 != null) ?? 0) + "|" + (df.Source != null ? "source" : "hand") : "no definition",
            "defforexe" => want.Split('|')[0] + "|" + (EmuDefs.ForExe(want.Split('|')[0])?.Id ?? "none"),
            "gameid" => want.Split('|', 3) is var gi && gi.Length == 3 ? gi[0] + "|" + gi[1] + "|" + (GameId.Read(gi[0], T(gi[1]))?.ToString() ?? "none") : "bad gameid",
            "front" => (Native.GetForegroundWindow() == w.Hwnd) ? "yes" : "no",
            "paused" => w.GameMenu.Paused ? "yes" : "no",
            "runbadge" => w.Home?.RunningBadge ?? "none",   // <game>|Paused or Playing, or none
            "ingame" => w.GameMenu.Game?.Name ?? "none",
            // native ports: <game>|ask / emulator / <port name>; <game>|ok or the reason
            "playwith" => w.Lib.Data.Games.FirstOrDefault(g => g.Name == want.Split('|')[0]) is Game pwg
                ? pwg.Name + "|" + (Ports.MustAsk(w, pwg) ? "ask" : Ports.Native(w, pwg)?.Def.Name ?? "emulator") : "no game",
            "portwhy" => w.Lib.Data.Games.FirstOrDefault(g => g.Name == want.Split('|')[0]) is Game pyg
                ? pyg.Name + "|" + (Ports.For(w, pyg).FirstOrDefault() is PortMatch pym ? Ports.Why(w, pym, pyg) ?? "ok" : "no port") : "no game",
            // shared saves: <game>|Shared / Separate / none (no port that shares with its emulator)
            "savesmode" => w.Lib.Data.Games.FirstOrDefault(g => g.Name == want.Split('|')[0]) is Game smg
                ? smg.Name + "|" + (Ports.ShareOf(w, smg) is Ports.SaveShare sms ? (Ports.SavesShared(smg, sms) ? "Shared" : "Separate") : "none") : "no game",
            "discord" => w.Discord.Status + "|" + w.Discord.Shown,   // connected|home, connected|<game>|<line 2>, waiting|none
            "discordon" => w.Settings.Discord ? "on" : "off",
            "discordwish" => w.Discord.Wanted,     // what the launcher asks Discord for, also with no Discord: home, <game>|<line 2>
            "coverurl" => w.Lib.Data.Games.FirstOrDefault(g => g.Name == want.Split('|')[0]) is Game cug ? cug.Name + "|" + (cug.CoverUrl ?? "unknown") + "|" + (Covers.PublicUrl(cug) ?? "none") : "no game",   // <game>|<stored>|<used>
            "discordpic" => w.Discord.WantedPic,   // its small picture's address, or none
            "outro" => OutroScreen.Playing ? "yes" : "no",
            "brightness" => w.Settings.Brightness.ToString(),
            "quitting" => w.Quitting ? "yes" : "no",
            "closing" => w.Launch.ClosingName ?? "none",   // the game whose closing screen is up
            "move" => MoveCard.LastResult,
            "rom" => w.Lib.Data.Games.FirstOrDefault(g => g.Name == want.Split('|')[0])?.Rom is string r ? want.Split('|')[0] + "|" + (want.Split('|', 2) is { Length: 2 } rw && string.Equals(r, T(rw[1]), StringComparison.OrdinalIgnoreCase) ? rw[1] : r) : "none",
            "exists" => System.IO.File.Exists(T(want)) || Directory.Exists(T(want)) ? want : "missing",
            "gone" => System.IO.File.Exists(T(want)) || Directory.Exists(T(want)) ? "still there" : want,
            "dir" => Directory.Exists(T(want)) ? want : "missing",
            "holdrows" => HoldMenu.Current?.Rows ?? "none",
            "slidein" => w.Home == null ? "none" : w.Home.SlideCount + "|" + w.Home.SlideState,
            "holdfocus" => HoldMenu.Current?.Focused ?? "none",
            "cardfocus" => (w.TopScreen as MenuCard)?.FocusedLabel ?? "none",
            "settingspane" => SettingsCard.Current?.Category ?? "none",
            "settingsrail" => SettingsCard.Current == null ? "none" : SettingsCard.Current.InRail ? "yes" : "no",
            "settingstext" => SettingsCard.Current == null ? "none" : $"{want.Split('|')[0]}|{SettingsCard.Current.TextCountForTest(want.Split('|')[0])}",   // settingstext <phrase>|<count>
            "settingsheight" => SettingsCard.Current == null ? "none" : Math.Round(SettingsCard.Current.BodyHeightForTest).ToString(CultureInfo.InvariantCulture),
            "menupage" => (w.TopScreen as HomeMenuCard)?.Page ?? "none",
            "cardrows" => (w.TopScreen as MenuCard)?.Labels ?? "none",
            "cardboxes" => (w.TopScreen as MenuCard)?.BoxesForTest ?? "none",   // the box headings, in order
            "settinggroup" => want.Split('|') is var sg && sg.Length >= 2 ? $"{sg[0]}|{sg[1]}|{SettingGroups.Of(null, null, sg[0], sg[1])}" : "bad",   // settinggroup <key>|<name>|<box>
            "list" => (w.TopScreen as MenuCard)?.ListItems ?? "none",
            "listfocus" => (w.TopScreen as MenuCard)?.ListFocused ?? "none",
            "listshown" => (w.TopScreen as MenuCard)?.ListShown ?? "none",
            "tweaks" => TweaksCard.LastResult,
            "uninstall" => UninstallCard.LastResult,
            "inlist" => w.Lib.Data.Games.Any(g => g.Name == want) ? want : "not in list",
            "notinlist" => w.Lib.Data.Games.Any(g => g.Name == want) ? "still in list" : want,
            "gametweak" => GameTweak(want),
            "corelayout" => w.GameMenu.Core?.LayoutForTest() ?? "no core",   // stretch / fit / integer N / other (Display.cs)
            "saveddisplay" => SavedDisplay(want),
            "cardscrolled" => (w.TopScreen as MenuCard)?.ScrollForTest is double cs ? (cs > 0.5 ? "yes" : cs < 0 ? "no scroller" : "no") : "none",   // the top card's text scrolled down
            "dips" => (w.TopScreen as MenuCard)?.DipsForTest.ToString() ?? "none",
            "asleep" => (w.Sleep.Asleep ? "yes" : "no") + "|" + w.Sleep.Layer.Opacity.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),   // and the dim's opacity
            "effects" => Anim.EffectsForTest(),
            "ringplain" => Palette.I.RingA.Color == Palette.I.RingB.Color && Palette.I.RingC.Color == Palette.I.RingB.Color ? "yes" : "no",   // Reduce effects: one colour
            "glass" => Palette.I.GlassA.Color is var gc ? $"#{gc.A:X2}{gc.R:X2}{gc.G:X2}{gc.B:X2}" : "",   // the open folder's glass, top (stained white / near-black)
            "toastsounds" => $"{Toasts.Sounded} sounded, {Toasts.Hushed} quiet",   // pop-up sounds: one per burst (Toasts.cs)   // glows, running lights and shines shown / hidden (Reduce effects)
            "emuports" => string.Join(" | ", w.Emus.Sorted().Where(EmulatorsCard.IsPort).Select(e => e.Name)) is { Length: > 0 } ep ? ep : "none",   // under Native ports
            "covercolour" => w.Lib.Data.Games.FirstOrDefault(x => x.Name == want.Split('|')[0]) is Game ccg ? ccg.Name + "|" + CoverColour.Of(ccg).Name : "none",   // times a full level dipped (Cards.cs)
            "gamewindow" => GameWindow.LastResult,   // full screen / window WxH / as launcher (Display.cs)
            "gameclient" => GameWindow.ClientForTest(w),   // the window's inside now, WxH screen pixels
            "overlay" => w.Overlay?.Shown ?? "none",   // hidden / simple / detailed: what the performance overlay shows now (Overlay.cs)
            "overlayfps" => w.Overlay == null ? "none" : double.TryParse(w.Overlay.LastFps, out var ofps) ? (ofps >= 30 ? "ok" : "low " + ofps) : w.Overlay.LastFps,   // ok: a core's frame rate is shown and 30 or more
            "overlaycorner" => w.Overlay?.CornerNow ?? "none",   // top / bottom
            "overlaysource" => w.Overlay?.SourceNow ?? "none",   // core / log / window / none: where the frame rate comes from
            "overlayfpsany" => w.Overlay == null ? "none" : double.TryParse(w.Overlay.LastFps, out var oany) && oany > 0 ? "ok" : w.Overlay.LastFps,   // ok: a frame rate above 0 is shown
            "overlayspeed" => w.Overlay == null ? "none" : double.TryParse(w.Overlay.LastSpeed.TrimEnd('%'), out var osp) ? (osp >= 90 && osp <= 110 ? "ok" : "off " + osp) : w.Overlay.LastSpeed,   // ok: a core's speed shows 90-110 %
            "savedoverlay" => SavedOverlay(want),
            "json" => JsonAt(want, T),
            "ini" => IniAt(want, T),
            "applied" => File.Exists(Path.Combine(Tweaks.DataDir, "video-applied", (w.Emus.Data.Emulators.FirstOrDefault(e => e.Name == want.Split('|')[0])?.Id ?? "none") + ".json")) ? want.Split('|')[0] + "|yes" : want.Split('|')[0] + "|no",
            "tweakrows" => (w.TopScreen as TweaksCard)?.Keys ?? "none",
            "tweakboxes" => (w.TopScreen as TweaksCard)?.BoxTitles ?? "none",
            "carddim" => (w.TopScreen as MenuCard)?.DimLabels ?? "none",
            "toast" => Toasts.Last ?? "none",
            "toasthas" => Toasts.Last?.Contains(want) == true ? want : Toasts.Last ?? "none",   // the last pop-up contains this
            "tutorial" => w.Tutor.StepName,   // off, emulator, game, play, ingame
            "coach" => w.Tutor.Target,        // what the ring is on: gear, rail:Library, row:<label>, add, game:<name>, close, none, hidden
            "tutorialasked" => w.Settings.TutorialAsked ? "yes" : "no",
            // game info: info <label>|<value> (a fact as shown), infoaddons, infosection, infokind, addon (the last install's result)
            "info" => want.Split('|')[0] + "|" + (GameInfoCard.Current?.Fact(want.Split('|')[0]) ?? "no card"),
            "infoaddons" => GameInfoCard.Current?.AddonList ?? "no card",
            "infomods" => GameInfoCard.Current?.ModList ?? "no card",   // the emulator's mods, "(off)" when turned off
            "infosection" => GameInfoCard.Current is GameInfoCard ic ? (ic.HasSection ? "yes" : "no") : "no card",
            "infokind" => GameInfoCard.Current?.Kind ?? "no card",
            "addon" => AddonCard.LastResult,
            "inputcaption" => w.Home?.InputText,
            "binding" => InputBinding(want.Split(' ')[0]),
            "padbinding" => w.Settings.Input.PadFor(Scope, want.Split(' ')[0]) is string pbid ? want.Split(' ')[0] + " " + pbid : "none",
            "devmsg" => DeviceScreen.Current?.Message ?? "none",
            "devfocus" => DeviceScreen.Current?.FocusedId ?? "none",
            "capturing" => DeviceScreen.Current?.IsCapturing == true ? "yes" : "no",
            "devassigning" => DeviceScreen.Current?.AssigningPart ?? "none",
            "devtitle" => DeviceScreen.Current?.TitleText ?? "none",
            "devitems" => DeviceScreen.Current?.ItemList ?? "none",
            "mousecams" => string.Join(",", InputMap.MouseCamSystems.Select(s => new ControlScope(s)).Select(c => $"{c.System}:{(w.Settings.Input.MouseCamera(c) ? "on" : "off")}:{w.Settings.Input.MouseSpeed(c)}")),
            "devdrop" => DeviceScreen.Current?.DropList ?? "none",
            "setupdone" => w.Settings.Input.SetupDone ? "yes" : "no",
            "maininput" => w.Settings.Input.Main ?? "none",
            "ownctl" => w.Settings.Input.HasOwn(Scope) ? "yes" : "no",
            "inputpref" => typeof(InputPrefs).GetProperty(want.Split(' ')[0])?.GetValue(w.Settings.Input) is object ipv ? want.Split(' ')[0] + " " + ipv.ToString().ToLowerInvariant() : "none",
            "kbon" => w.KeyboardOn ? "yes" : "no",
            "cursor" => w.Cursor == null ? "windows" : w.Cursor == System.Windows.Input.Cursors.None ? "hidden" : "custom",
            "mouseroute" => Pointer.LastRoute ?? "none",
            "devstatus" => InputCard.Status(w, want.Split('|')[0]) is string dst ? want.Split('|')[0] + "|" + dst : "none",
            "ps4ini" => Ps4IniHas(want),
            "embedded" => w.GameMenu.IsEmbedded ? "yes" : "no",
            "embedowned" => w.GameMenu.EmbedOwned ? "yes" : "no",
            "embedfits" => w.GameMenu.EmbedFits ? "yes" : "no",
            "stillbehind" => w.GameMenu.StillBehind ? "yes" : "no",
            "canembed" => w.Emus.Data.Emulators.FirstOrDefault(x => x.Name == want.Split('|')[0]) is Emulator ce ? ce.Name + "|" + (EmbedLogic.CanEmbed(ce) ? "yes" : "no") : "none",
            "managed" => w.Lib.Data.Games.FirstOrDefault(g => g.Name == want.Split('|')[0]) is Game mgd ? mgd.Name + "|" + (InputMap.Managed(w, mgd) ? "yes" : "no") : "none",
            "esc" => w.GameMenu.EscClaimed ? "yes" : "no",
            "homekey" => w.GameMenu.EscClaimed || EmuOpen.HomeClaimed ? "yes" : "no",
            "hooksim" => w.HomeKeys.LastSim,
            "homefires" => homeFires.ToString(),
            "launcherkey" => want.Split('|')[0] + "|" + LauncherKeys.Names(w.Settings.Input, want.Split('|')[0]),
            "keymap" => want.Split('|')[0] + "|" + (LauncherKeys.Map(w.Settings.Input, Enum.Parse<System.Windows.Input.Key>(want.Split('|')[0], true))?.ToString() ?? "none"),
            "lkmsg" => LauncherKeyCard.Current?.Message ?? "none",
            "lkcapturing" => LauncherKeyCard.Current?.Capturing == true ? "yes" : "no",
            "gameuses" => want.Split('|')[0] + "|" + LauncherKeys.GameUses(w, want.Split('|')[0]).Count,
            "ctlpending" => w.GameMenu.ControlsPending ? "yes" : "no",
            "panning" => w.GameMenu.PanningSent ? "yes" : "no",
            // mouselook <game>|yes|no: shadPS4 is told to take the mouse as the camera. gamecursor <game>|shown|hidden: the cursor in that game.
            "mouselook" => w.Lib.Data.Games.FirstOrDefault(g => g.Name == want.Split('|')[0]) is Game mg ? mg.Name + "|" + (InputMap.MouseLookWanted(w, mg) ? "yes" : "no") : "none",
            "gamecursor" => w.Lib.Data.Games.FirstOrDefault(g => g.Name == want.Split('|')[0]) is Game cg ? cg.Name + "|" + (InputMap.TouchMouse(w, cg) ? "shown" : "hidden") : "none",
            "nintendo" => w.Settings.NintendoButtons ? "yes" : "no",
            "volume" => w.Settings.Sound.Volume.ToString(),
            "soundson" => w.Settings.Sounds ? "yes" : "no",
            // heard <clip>: "<clip> yes" when it played since the last soundmark; lastsound: the last clip played
            "heard" => want.Split(' ')[0] + " " + (Heard(want.Split(' ')[0]) ? "yes" : "no"),
            "lastsound" => Sounds.Last ?? "none",
            "kind" => want.Split(' ')[0] + " " + w.Settings.Sound.Kind(want.Split(' ')[0]),
            "soundtheme" => Sounds.Theme,
            "reduce" => (w.Settings.Sound.ReduceEffects ? "on" : "off") + "|" + (Anim.Reduced ? "on" : "off"),
            // savedsound: the sound settings as written in the active profile's file: theme nav act reduce volume
            "savedsound" => SavedSound(),
            "group" => want.Split(' ')[0] + " " + w.Settings.Sound.Group(want.Split(' ')[0]),
            "gain" => want.Split(' ')[0] + " " + Sounds.Gain(want.Split(' ')[0]).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
            "zoom" => w.Settings.Zoom.ToString(),
            "order" => w.Settings.Order,
            "dark" => w.Settings.Dark ? "yes" : "no",
            "folderof" => w.Lib.Data.Games.FirstOrDefault(g => g.Name == want.Split('|')[0]) is Game fg ? fg.Name + "|" + (fg.Folder ?? "none") : "none",
            "tile" => w.Home?.NameAt(int.Parse(want.Split(' ')[0])) is string tn ? want.Split(' ')[0] + " " + tn : "none",
            "loading" => (w.TopScreen as MenuCard)?.Loading == true ? "yes" : "no",
            "rowvalue" => (w.TopScreen as MenuCard)?.ValueOf(want.Split('|')[0]) is string rv ? want.Split('|')[0] + "|" + rv : "none",
            // Going online: online <item>|on|off, onlineasked yes|no, onlinelog <entries>, onlinelast <item>|<result>,
            // onlinerequests <sent since start>, download <how the latest ended or running>, downloadaddress <the question's address>
            "online" => Online.Find(want.Split('|')[0]) is OnlineItem oi ? oi.Id + "|" + (Online.Allowed(oi.Id) ? "on" : "off") : "no item",
            "onlineasked" => Online.AllAsked ? "yes" : "no",
            "keyboard" => w.Osk.IsOpen ? "open" : "closed",   // the on-screen keyboard (Osk.cs)
            "oskfocus" => w.Osk.FocusedKey ?? "none",
            "browserpicked" => picked == "none" ? "none" : Path.GetFileName(picked.TrimEnd('\\')),   // what the file browser gave (its name)
            "browserdir" => FileBrowserCard.Current == null ? "closed" : FileBrowserCard.Current.Dir == null ? "This PC" : Path.GetFileName(FileBrowserCard.Current.Dir.TrimEnd('\\')) is string bn && bn.Length > 0 ? bn : FileBrowserCard.Current.Dir,
            "browserrows" => FileBrowserCard.Current?.Labels ?? "closed",
            "offline" => Online.Offline ? "on" : "off",   // Offline mode
            "hosts" => Online.Find(want.Split('|')[0]) is OnlineItem hi ? hi.Id + "|" + Online.HostsText(hi) : "no item",   // hosts <item>|<shown hosts>
            "onlinelog" => Online.Entries.Count.ToString(),
            "onlinelast" => Online.Entries.LastOrDefault() is OnlineLogEntry ol ? ol.Item + "|" + ol.Result : "none",
            "onlinerequests" => Online.Requests.ToString(),
            "download" => Online.Pending > 0 ? "running" : Online.Last is OnlineResult lr ? Online.Word(lr) : "none",
            "downloadaddress" => (w.TopScreen as DownloadCard)?.Address ?? "none",
            _ => "?",
        };
        if (got == want) Log.Write($"SCRIPT ok {what} = {want}");
        else Fail($"expect {what}: want '{want}', got '{got}'");
    }

    // saveddisplay <game or global>|<field>|<value>: a display setting as written in the file (none = not set)
    static bool Heard(string clip) { lock (Sounds.Played) return Sounds.Played.Contains(clip); }

    // The active profile's sound settings as kept on disk (data\profiles\<id>\settings.json).
    string SavedSound()
    {
        try
        {
            var f = w.Settings.ProfileFile;
            var sp = f != null ? Json.Load<ProfilePrefs>(f).Sound : Json.Load<Settings>(w.Settings.File).Sound;
            if (sp == null) return "none";
            return $"{sp.Theme} {sp.Navigation} {sp.Actions} {(sp.ReduceEffects ? "on" : "off")} {sp.Volume}";
        }
        catch (Exception e) { return "unreadable: " + e.Message; }
    }

    string SavedDisplay(string want)
    {
        var p = want.Split('|');
        DisplayPrefs d;
        try
        {
            if (p[0] == "global") d = Json.Load<Settings>(w.Settings.File).Display;
            else d = Json.Load<LibraryFile>(Path.Combine(w.Lib.Dir, "library.json")).Games.FirstOrDefault(x => x.Name == p[0])?.Display;
        }
        catch (Exception e) { return "unreadable: " + e.Message; }
        var prop = typeof(DisplayPrefs).GetProperty(p[1]);
        string v = d == null || prop == null ? null : prop.GetValue(d)?.ToString();
        return $"{p[0]}|{p[1]}|{v ?? "none"}";
    }

    // savedoverlay global|<prop>|<value> or <game>|<prop>|<value>: the performance overlay's kept value (none = not set)
    string SavedOverlay(string want)
    {
        var p = want.Split('|');
        OverlayPrefs d;
        try
        {
            if (p[0] == "global") d = Json.Load<Settings>(w.Settings.File).Overlay;
            else d = Json.Load<LibraryFile>(Path.Combine(w.Lib.Dir, "library.json")).Games.FirstOrDefault(x => x.Name == p[0])?.Overlay;
        }
        catch (Exception e) { return "unreadable: " + e.Message; }
        var prop = typeof(OverlayPrefs).GetProperty(p[1]);
        string v = d == null || prop == null ? null : prop.GetValue(d)?.ToString();
        return $"{p[0]}|{p[1]}|{v ?? "none"}";
    }

    // gametweak <game>|<key>|<value>: the game's own value (none = uses the emulator's)
    string GameTweak(string want)
    {
        var p = want.Split('|');
        var g = w.Lib.Data.Games.FirstOrDefault(x => x.Name == p[0]);
        string v = g?.Tweaks != null && g.Tweaks.TryGetValue(p[1], out var t) ? t : "none";
        return $"{p[0]}|{p[1]}|{v}";
    }

    // ini <file>|<[Section]key>|<value>: a value in an ini / toml file (missing = no file or no such key)
    static string IniAt(string want, Func<string, string> T)
    {
        var p = want.Split('|');
        string file = T(p[0]);
        var (sec, key) = Tweaks.SplitKey(p[1]);
        string v = File.Exists(file) ? new IniText(File.ReadAllText(file), "=").Get(sec, key) ?? "missing" : "missing";
        return $"{p[0]}|{p[1]}|{v}";
    }

    // json <file>|<section.key>|<value>: a value in a JSON file (missing = no file or no such key)
    static string JsonAt(string want, Func<string, string> T)
    {
        var p = want.Split('|');
        string v = "missing", file = T(p[0]);
        try
        {
            if (File.Exists(file) && System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file)) is System.Text.Json.Nodes.JsonObject o)
            {
                var k = p[1].Split('.', 2);
                if (o[k[0]]?[k[1]] is System.Text.Json.Nodes.JsonNode n) v = n.ToJsonString().Trim('"');
            }
        }
        catch (Exception e) { v = "error " + e.Message; }
        return $"{p[0]}|{p[1]}|{v}";
    }

    string ShotPath(string name)
    {
        string dir = w.Opt.ShotsDir ?? Path.Combine(w.Opt.DataDir, "shots");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, name.EndsWith(".png") ? name : name + ".png");
    }

    // The stage as drawn (1920x1080), independent of the window size.
    void Shot(string name)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new VisualBrush(w.Stage) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, 1920, 1080));
            if (w.Dim > 0)   // the brightness veil sits over the stage, outside it
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb((byte)Math.Round(255 * w.Dim), 0, 0, 0)), null, new Rect(0, 0, 1920, 1080));
        }
        var rtb = new RenderTargetBitmap(1920, 1080, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        Art.SavePng(rtb, ShotPath(name));
        Log.Write("SCRIPT shot " + name);
    }

    // A real capture of the window's area on screen (shows what is actually in front).
    void ScreenShot(string name)
    {
        GetWindowRect(w.Hwnd, out var r);
        int width = r.R - r.L, height = r.B - r.T;
        IntPtr src = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(src), bmp = CreateCompatibleBitmap(src, width, height);
        IntPtr old = SelectObject(mem, bmp);
        BitBlt(mem, 0, 0, width, height, src, r.L, r.T, 0x00CC0020);
        SelectObject(mem, old);
        var bs = Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        DeleteObject(bmp); DeleteDC(mem); ReleaseDC(IntPtr.Zero, src);
        Art.SavePng(bs, ShotPath(name));
        Log.Write($"SCRIPT screen {name} {width}x{height}");
    }

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, int rop);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
}

// Generated test games: covers in varied colours (about one in five has none), shortcuts that point
// to a harmless test program (or nowhere).
public static class Dummies
{
    static readonly string[] A = { "Neon", "Crystal", "Silent", "Iron", "Solar", "Lunar", "Hidden", "Frozen", "Golden", "Wild", "Echo", "Rapid", "Sky", "Deep", "Pixel", "Turbo", "Velvet", "Storm", "Paper", "Quantum" };
    static readonly string[] B = { "Drift", "Garden", "Harbor", "Quest", "Racers", "Tactics", "Kingdom", "Runner", "Islands", "Signal", "Valley", "Arena", "Frontier", "Golf", "Puzzle", "Rally", "Heist", "Odyssey", "Circuit", "Tales" };

    public static void Ensure(Library lib, int n, string target)
    {
        int have = lib.Data.Games.Count(g => g.Source == "dummy");
        if (have >= n) return;
        var rnd = new Random(42 + have);
        for (int i = have; i < n; i++)
        {
            string name = $"{A[i % A.Length]} {B[(i / A.Length + i * 3) % B.Length]}";   // unique for 400 games
            while (lib.Data.Games.Any(g => g.Name == name)) name += "+";
            var g = new Game { Id = Library.NewId(), Name = name, Source = "dummy", Added = DateTime.Now.AddMinutes(-i), Shortcut = $"dummy-{i:000}.lnk" };
            if (target != null && File.Exists(target))
            {
                Shortcut.Create(Path.Combine(lib.ShortcutsDir, g.Shortcut), target, "3", Path.GetDirectoryName(target), null);
                g.Target = target; g.Args = $"#{i}";   // unique args: dummies are not duplicates of each other
            }
            if (i % 5 != 3)
            {
                g.Cover = g.Id + ".png";
                Art.SavePng(Cover(name, i, rnd), Path.Combine(lib.CoversDir, g.Cover));
            }
            lib.Data.Games.Add(g);
        }
        lib.Save();
    }

    static BitmapSource Cover(string name, int i, Random rnd)
    {
        const int n = 420;
        double hue = (i * 47) % 360;
        Color c1 = Hsv(hue, 0.55, 0.92), c2 = Hsv((hue + 40) % 360, 0.75, 0.55);
        return Art.Render(n, n, dc =>
        {
            dc.DrawRectangle(new LinearGradientBrush(c1, c2, 55), null, new Rect(0, 0, n, n));
            for (int k = 0; k < 4; k++)
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), null,
                    new Point(rnd.Next(n), rnd.Next(n)), 40 + rnd.Next(120), 40 + rnd.Next(120));
            var ft = new FormattedText(name.ToUpperInvariant(), System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(Fonts.Display, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), 44, Brushes.White, 1.0)
            { MaxTextWidth = n - 60, TextAlignment = TextAlignment.Left };
            dc.DrawText(ft, new Point(30, n - 40 - ft.Height));
        });
    }

    static Color Hsv(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        var (r, g, b) = h < 60 ? (c, x, 0.0) : h < 120 ? (x, c, 0.0) : h < 180 ? (0.0, c, x) : h < 240 ? (0.0, x, c) : h < 300 ? (x, 0.0, c) : (c, 0.0, x);
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}

// Tests only: one long list, None and every game by name, so k5 and x1 can check a list of
// more than eight choices (paging, the wheel, the wheel winning over hover). It stood in the profile's
// Favourite game row until that was removed. The pick is kept on the card, nowhere else.
public sealed class ListTestCard : MenuCard
{
    int picked = -1;   // -1 = None
    public ListTestCard(MainWindow w) : base(w, "List test")
    {
        var games = w.Lib.Data.Games.Where(g => !g.Removed).OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        WithList(AddRow("Game", () => picked < 0 ? "None" : games[picked].Name, d =>
        {
            if (games.Count == 0) return;
            int n = picked + d;
            picked = n < -1 ? games.Count - 1 : n >= games.Count ? -1 : n;
        }), () => games.Select(g => g.Name).Prepend("None").ToList(), () => picked + 1, i => picked = i - 1);
        AddRow("Close", activate: () => Close());
    }
}
