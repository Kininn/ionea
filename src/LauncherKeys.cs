#nullable disable
// Ionea launcher: the launcher's own keys, set per profile in Settings > Input > Launcher keys.
// Moving is WASD and the arrows, Select is Enter and Space (held: a game's quick menu), Back is Esc and
// Backspace. The Home menu key opens the Home menu in a game: Alt by default, so Esc can be a game button.
// The Home menu key is never also a game button: a game button bound to it has no key until it moves
// (InputPrefs.KeyFor). In a game the key is caught by HomeKeys, a low-level keyboard hook on its own thread.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using static Ionea.Loc;

namespace Ionea;

public static class LauncherKeys
{
    public sealed record Action_(string Id, string Label, Act? Act, string[] Defaults);

    public static readonly Action_[] Actions =
    {
        new("up", "Up", Act.Up, new[] { "W", "Up" }),
        new("down", "Down", Act.Down, new[] { "S", "Down" }),
        new("left", "Left", Act.Left, new[] { "A", "Left" }),
        new("right", "Right", Act.Right, new[] { "D", "Right" }),
        new("accept", "Select", Act.Accept, new[] { "Return", "Space" }),
        new("back", "Back", Act.Back, new[] { "Escape", "Back" }),
        new("menu", "Settings", Act.Menu, new[] { "F1", "Apps" }),
        new("option", "Game options", Act.Option, new[] { "X", "F2", "Delete" }),
        new("pageleft", "Previous page", Act.PageLeft, new[] { "PageUp" }),
        new("pageright", "Next page", Act.PageRight, new[] { "PageDown" }),
        new("home", "Home menu", null, new[] { "Alt" }),
        // Keys for games played inside the launcher (CoreSession.Poll); F-keys, which are never game buttons.
        new("fastforward", "Fast-forward", null, new[] { "F6" }),
        new("rewind", "Rewind", null, new[] { "F5" }),
        new("screenshot", "Screenshot", null, new[] { "F12" }),
    };
    public const string Home = "home", Fast = "fastforward", Rewind = "rewind", Shot = "screenshot";
    public const int Max = 4;   // keys per action

    public static Action_ Get(string id) => Actions.First(a => a.Id == id);
    public static string Label(string id) => Get(id).Label;
    static bool Nav(string id) => Get(id).Act != null;
    // Keys that do something in a game (the Home menu key, fast-forward ...): never a game button while they are that.
    public static bool InGame(string id) => !Nav(id);
    static bool MustHave(string id) => id is "accept" or "back";
    // The pad buttons held with View for a game key (P1's pad, games inside the launcher), or null.
    public static string PadCombo(string id) => id switch { Fast => "View + RB", Rewind => "View + LB", Shot => "View + Y", _ => null };
    public static ushort PadMask(string id) => id switch { Fast => 0x0200, Rewind => 0x0100, Shot => 0x8000, _ => 0 };

    // ---- key names: a WPF key name ("W", "Escape"), or "Alt" / "Ctrl" / "Shift" for either of the two ----
    public static string NameOf(Key k) => k switch
    {
        Key.LeftAlt or Key.RightAlt => "Alt",
        Key.LeftCtrl or Key.RightCtrl => "Ctrl",
        Key.LeftShift or Key.RightShift => "Shift",
        _ => k.ToString(),
    };
    static Key? Parse(string n) => n is "Alt" or "Ctrl" or "Shift" ? null : Enum.TryParse(n, out Key k) && k != Key.None ? k : null;
    static bool Valid(string n) => n is "Alt" or "Ctrl" or "Shift" || Parse(n) != null;
    public static string Show(string n) => n is "Alt" or "Ctrl" or "Shift" ? n : Parse(n) is Key k ? InputMap.KeyName(k) : n;

    static bool Modifier(Key k) => k is Key.LeftAlt or Key.RightAlt or Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift;

    // Can this key do this action? null: yes. Else why not.
    public static string Refuse(string id, Key k)
    {
        if (k is Key.None or Key.System or Key.ImeProcessed or Key.LWin or Key.RWin or Key.F11 or Key.DeadCharProcessed)
            return $"{InputMap.KeyName(k)} can't be used here";
        if (Nav(id) && Modifier(k)) return $"{Show(NameOf(k))} can't be a launcher key";
        if (id != Home && Modifier(k)) return $"{Show(NameOf(k))} can't be the {Label(id)} key";
        return null;
    }

    // ---- what is set ----
    public static List<string> Keys(InputPrefs p, string id)
    {
        if (p.LauncherKeys != null && p.LauncherKeys.TryGetValue(id, out var own) && own != null)
        {
            var list = own.Where(Valid).Distinct().Take(Max).ToList();
            if (list.Count > 0 || !MustHave(id)) return list;
        }
        return Get(id).Defaults.ToList();
    }
    public static string Names(InputPrefs p, string id)
    {
        var l = Keys(p, id);
        return l.Count == 0 ? T("common.none") : string.Join("  ·  ", l.Select(Show));
    }
    public static string First(InputPrefs p, string id) => Keys(p, id).Select(Show).FirstOrDefault() ?? "";
    public static string Summary(InputPrefs p) => T("settings.home_menu") + Names(p, Home);

    static bool Has(InputPrefs p, string id, Key k) => Keys(p, id).Any(n => n == NameOf(k) || Parse(n) == k);

    // The launcher action for a key, or null.
    public static Act? Map(InputPrefs p, Key k)
    {
        foreach (var a in Actions)
            if (a.Act != null && Has(p, a.Id, k)) return a.Act;
        return null;
    }
    public static bool IsAccept(InputPrefs p, Key k) => Has(p, "accept", k);
    public static bool IsBack(InputPrefs p, Key k) => Has(p, "back", k);
    public static bool IsNav(InputPrefs p, Key k) => Map(p, k) != null;

    // The Home menu key and the other game keys: never a game button while they are that.
    public static bool IsHome(InputPrefs p, Key k) => Has(p, Home, k);
    public static bool TakesBind(InputPrefs p, string bind) => Taker(p, bind) != null;
    // The game key (Home menu, fast-forward ...) a key or mouse button is now, or null.
    public static string Taker(InputPrefs p, string bind)
    {
        if (bind == null || InputMap.IsMouse(bind) || !Enum.TryParse(bind, out Key k)) return null;
        return Actions.FirstOrDefault(a => InGame(a.Id) && Has(p, a.Id, k))?.Id;
    }
    // What a game key does, after its key's name: "opens the Home menu", "is the Fast-forward key".
    public static string Does(string id) => id == Home ? "opens the Home menu" : $"is the {Label(id)} key";

    // The virtual keys of an action's keys (left and right for Alt / Ctrl / Shift), for reading them in a game.
    public static int[] Vks(InputPrefs p, string id)
    {
        var vks = new List<int>();
        foreach (var n in Keys(p, id))
        {
            switch (n)
            {
                case "Alt": vks.AddRange(new[] { 0xA4, 0xA5 }); continue;
                case "Ctrl": vks.AddRange(new[] { 0xA2, 0xA3 }); continue;
                case "Shift": vks.AddRange(new[] { 0xA0, 0xA1 }); continue;
            }
            if (Parse(n) is Key k && KeyInterop.VirtualKeyFromKey(k) is int vk && vk != 0) vks.Add(vk);
        }
        return vks.Distinct().ToArray();
    }

    // The virtual keys of the Home menu key (left and right for Alt / Ctrl / Shift). launcherToo: also the
    // ones that are launcher keys as well (Esc as both Back and Home menu), only while a game is played.
    public static int[] HomeVks(InputPrefs p, bool launcherToo)
    {
        var vks = new List<int>();
        foreach (var n in Keys(p, Home))
        {
            switch (n)
            {
                case "Alt": vks.AddRange(new[] { 0xA4, 0xA5 }); continue;
                case "Ctrl": vks.AddRange(new[] { 0xA2, 0xA3 }); continue;
                case "Shift": vks.AddRange(new[] { 0xA0, 0xA1 }); continue;
            }
            if (Parse(n) is not Key k) continue;
            if (!launcherToo && IsNav(p, k)) continue;
            int vk = KeyInterop.VirtualKeyFromKey(k);
            if (vk != 0) vks.Add(vk);
        }
        return vks.Distinct().ToArray();
    }

    // ---- changing them ----
    static void Put(InputPrefs p, string id, List<string> keys)
    {
        p.LauncherKeys ??= new();
        if (keys.SequenceEqual(Get(id).Defaults)) p.LauncherKeys.Remove(id); else p.LauncherKeys[id] = keys;
        if (p.LauncherKeys.Count == 0) p.LauncherKeys = null;
    }

    // Can k be added to this action? null: yes (moved: the launcher action it is taken from). Else why not.
    public static string CantAdd(InputPrefs p, string id, Key k, out string movedFrom)
    {
        movedFrom = null;
        if (Refuse(id, k) is string why) return why;
        string name = NameOf(k);
        if (Has(p, id, k)) return $"{Show(name)} is already set";
        if (Keys(p, id).Count >= Max) return T("settings.up_to_keys", Max);
        // A key does one launcher action, and one game key; a game key may also be a launcher key (Esc as Back and Home menu).
        var other = Actions.FirstOrDefault(a => a.Id != id && Nav(a.Id) == Nav(id) && Has(p, a.Id, k));
        if (other == null) return null;
        if (MustHave(other.Id) && Keys(p, other.Id).Count == 1) return $"{Show(name)} is the only {other.Label} key";
        movedFrom = other.Label;
        return null;
    }

    public static void Add(InputPrefs p, string id, Key k)
    {
        string name = NameOf(k);
        foreach (var a in Actions)
            if (a.Id != id && Nav(a.Id) == Nav(id) && Has(p, a.Id, k))
                Put(p, a.Id, Keys(p, a.Id).Where(n => n != name && Parse(n) != k).ToList());
        var l = Keys(p, id); l.Add(name);
        Put(p, id, l);
    }

    public static bool CanRemove(InputPrefs p, string id) => !MustHave(id) || Keys(p, id).Count > 1;
    public static void Remove(InputPrefs p, string id, string name) { if (CanRemove(p, id)) Put(p, id, Keys(p, id).Where(n => n != name).ToList()); }
    public static void Reset(InputPrefs p) => p.LauncherKeys = null;
    public static bool IsDefault(InputPrefs p) => p.LauncherKeys == null || p.LauncherKeys.Count == 0;

    // The game buttons that use a key now ("Game Boy Advance: A"), for the notice before it becomes the Home menu key.
    public static List<string> GameUses(MainWindow w, string name)
    {
        var p = w.Settings.Input;
        bool Same(string bind) => bind != null && !InputMap.IsMouse(bind) && Enum.TryParse(bind, out Key b) && (NameOf(b) == name || b.ToString() == name);
        var scopes = InputMap.ManagedSystems(w).Concat(p.Systems?.Keys ?? Enumerable.Empty<string>()).Distinct().Select(s => new ControlScope(s)).ToList();
        if (p.Games != null)
            foreach (var g in w.Lib.Data.Games.Where(g => !g.Removed && p.Games.ContainsKey(g.Id)))
                scopes.Add(InputMap.ScopeFor(w, g));
        var uses = new List<string>();
        foreach (var sc in scopes)
            foreach (var a in sc.Layout.Actions)
                if (Same(p.KeyFor(sc, a))) uses.Add($"{sc.GameName ?? sc.Name}: {sc.Layout.Label(a)}");
        return uses.Distinct().ToList();
    }

    // Launcher keys changed: save, and everything that shows or catches them follows.
    public static void Changed(MainWindow w)
    {
        w.Settings.Save();
        w.InputChanged();
        DeviceScreen.Current?.Reread();
    }
}

// ---- the Home menu key in a game: a low-level keyboard hook on its own thread ----
// What GameMenu (a game in front) or EmuOpen (an emulator open) claims, per owner. A plain key is taken
// whole (down and up never reach the game) and fires on its press. Alt, Ctrl or Shift fire only tapped on
// their own (no other key with them, let go within HomeKeyTap.TapMs), so Alt+Tab and Ctrl+C still work;
// a tapped Alt gets a key between its press and release so the game's menu bar doesn't open.
public sealed class HomeKeys
{
    sealed record Claim(object Owner, int[] Vks, uint[] Pids, Action Fire);
    volatile Claim[] claims = Array.Empty<Claim>();
    readonly MainWindow w;
    readonly HomeKeyTap tap = new();
    Thread thread;
    uint threadId;
    IntPtr hook;
    LowLevelProc proc;
    public string LastSim = "none";   // test scripts: what the last simulated key did

    public HomeKeys(MainWindow w) { this.w = w; }

    // owner claims these keys (none: lets go). pids: only while a window of one of them is in front (null: any).
    public void Set(object owner, int[] vks, uint[] pids, Action fire)
    {
        var list = claims.Where(c => c.Owner != owner).ToList();
        if (vks != null && vks.Length > 0) list.Add(new Claim(owner, vks, pids, fire));
        claims = list.ToArray();
        if (list.Count > 0) Start();
    }
    public bool Claimed(object owner) => claims.Any(c => c.Owner == owner);
    public bool Hooked => hook != IntPtr.Zero;

    Claim Find(int vk, bool front)
    {
        foreach (var c in claims)
        {
            if (Array.IndexOf(c.Vks, vk) < 0) continue;
            if (!front || c.Pids == null) return c;
            var fg = Native.GetForegroundWindow();
            if (fg != IntPtr.Zero && Native.GetWindowThreadProcessId(fg, out uint pid) != 0 && Array.IndexOf(c.Pids, pid) >= 0) return c;
        }
        return null;
    }

    void Start()
    {
        if (thread != null) return;
        if (!string.IsNullOrEmpty(w.Opt.Script)) return;   // test runs never watch the real keyboard (they use "homekey" / "hooksim")
        var ready = new ManualResetEventSlim();
        thread = new Thread(() =>
        {
            threadId = GetCurrentThreadId();
            proc = Proc;
            hook = SetWindowsHookEx(13 /* WH_KEYBOARD_LL */, proc, GetModuleHandle(null), 0);
            Log.Write(hook != IntPtr.Zero ? "home key: keyboard watch on" : "home key: keyboard watch failed (" + Marshal.GetLastWin32Error() + ")");
            ready.Set();
            while (GetMessage(out var m, IntPtr.Zero, 0, 0) > 0) { }
            if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
            hook = IntPtr.Zero;
        }) { IsBackground = true, Name = "home key" };
        thread.Start();
        ready.Wait(2000);
    }

    public void Stop() { if (threadId != 0) PostThreadMessage(threadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero); }

    IntPtr Proc(int code, IntPtr wp, IntPtr lp)
    {
        try
        {
            if (code >= 0)
            {
                var k = Marshal.PtrToStructure<KbdLL>(lp);
                int msg = wp.ToInt32();
                bool down = msg is 0x0100 or 0x0104, up = msg is 0x0101 or 0x0105;
                if ((k.Flags & 0x10) == 0 /* not injected */ && (down || up))
                {
                    Claim hit = null;
                    HomeKeyTap.Result r;
                    lock (tap) r = tap.Event((int)k.Vk, down, Environment.TickCount64, vk => (hit = Find(vk, true)) != null, vk => (GetAsyncKeyState(vk) & 0x8000) != 0);
                    if (r.Fire && hit != null) { var f = hit.Fire; w.Dispatcher.BeginInvoke(f); }
                    if (r.Quiet) Quiet((byte)k.Vk, (byte)k.Scan, (k.Flags & 1) != 0);
                    if (r.Swallow) return (IntPtr)1;
                }
            }
        }
        catch (Exception caught) { Log.Debug("launcher keys: key hook", caught); }
        return CallNextHookEx(hook, code, wp, lp);
    }

    // A tapped Alt: an unused key (0xE8) between its press and release, so no menu bar takes the keyboard.
    static void Quiet(byte vk, byte scan, bool ext)
    {
        Native.keybd_event(0xE8, 0, 0, UIntPtr.Zero);
        Native.keybd_event(0xE8, 0, 2, UIntPtr.Zero);
        Native.keybd_event(vk, scan, 2u | (ext ? 1u : 0u), UIntPtr.Zero);
    }

    // ---- test scripts (no real keys) ----
    // A key event through the same logic as the hook; nothing is sent (an Alt tap's quieting is only noted).
    public string Simulate(int vk, bool down)
    {
        Claim hit = null;
        HomeKeyTap.Result r;
        lock (tap) r = tap.Event(vk, down, Environment.TickCount64, v => (hit = Find(v, false)) != null);
        if (r.Fire && hit != null) w.Dispatcher.BeginInvoke(hit.Fire);
        LastSim = r.Fire ? (r.Quiet ? "fire+quiet" : r.Swallow ? "fire+swallow" : "fire") : r.Swallow ? "swallow" : "pass";
        return LastSim;
    }
    // The Home menu key pressed (its first key), if something claims it now.
    public bool Press()
    {
        var c = claims.FirstOrDefault();
        if (c == null) return false;
        w.Dispatcher.BeginInvoke(c.Fire);
        return true;
    }

    [StructLayout(LayoutKind.Sequential)] struct KbdLL { public uint Vk, Scan, Flags, Time; public IntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] struct Msg { public IntPtr Hwnd; public uint Message; public IntPtr W, L; public uint Time; public int X, Y; }
    delegate IntPtr LowLevelProc(int code, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, LowLevelProc proc, IntPtr mod, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll")] static extern int GetMessage(out Msg m, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint thread, uint msg, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string name);
}

// The Home menu key's press logic, apart from the hook so test scripts can run it.
public sealed class HomeKeyTap
{
    public const int TapMs = 700;      // a modifier let go later than this was held, not tapped
    const int RepeatGap = 1000;        // a taken key "repeating" after this long was let go unseen: a new press
    public readonly record struct Result(bool Swallow, bool Fire, bool Quiet);

    readonly HashSet<int> down = new();
    readonly Dictionary<int, long> taken = new();   // plain keys taken whole: vk -> last seen
    int modVk; long modAt; bool modClean;

    public static bool IsMod(int vk) => vk is >= 0xA0 and <= 0xA5;
    public static bool IsAlt(int vk) => vk is 0xA4 or 0xA5;

    // claimed: is this key the Home menu key now. stillDown: the key's real state (drops key-ups missed).
    public Result Event(int vk, bool isDown, long ms, Func<int, bool> claimed, Func<int, bool> stillDown = null)
    {
        if (isDown)
        {
            if (taken.TryGetValue(vk, out long last))
            {
                taken[vk] = ms;
                if (ms - last < RepeatGap) return new(true, false, false);   // its repeats
                return new(true, claimed(vk), false);
            }
            if (down.Contains(vk)) return new(false, false, false);        // a repeat
            if (stillDown != null) down.RemoveWhere(k => !stillDown(k));
            bool alone = down.Count == 0;
            if (modVk != 0) modClean = false;   // another key with the modifier: not a tap
            down.Add(vk);
            if (!claimed(vk)) return new(false, false, false);
            if (IsMod(vk)) { if (alone) { modVk = vk; modAt = ms; modClean = true; } return new(false, false, false); }
            taken[vk] = ms;
            return new(true, true, false);
        }
        down.Remove(vk);
        if (taken.Remove(vk)) return new(true, false, false);
        if (vk != modVk) return new(false, false, false);
        modVk = 0;
        if (!modClean || ms - modAt > TapMs || !claimed(vk)) return new(false, false, false);
        return IsAlt(vk) ? new(true, true, true) : new(false, true, false);
    }
}

// Esc while a key is wanted: a tap cancels, held it is the key (Esc can be a game button or a launcher key).
public sealed class EscHold
{
    public const int Ms = 450;
    public static Key? ScriptHeld;   // test scripts: a key held down (no real keys)
    bool waiting;
    public bool Waiting => waiting;

    public void Begin(MainWindow w, Action take, Action cancel)
    {
        if (waiting) return;
        waiting = true;
        KeyEventHandler up = null;
        void Done(bool held)
        {
            if (!waiting) return;
            waiting = false;
            w.PreviewKeyUp -= up;
            (held ? take : cancel)();
        }
        up = (s, e) => { if (e.Key == Key.Escape) Done(false); };
        w.PreviewKeyUp += up;
        Ui.After(Ms, () => Done(Keyboard.IsKeyDown(Key.Escape) || ScriptHeld == Key.Escape));
    }
    public void Stop() => waiting = false;
}

// ---- cards ----

// Settings > Input > Launcher keys: every launcher action and its keys.
public sealed class LauncherKeysCard : MenuCard
{
    public static LauncherKeysCard Current;   // test scripts

    public LauncherKeysCard(MainWindow w, string focus = null) : base(w, T("settings.launcher_keys"))
    {
        var p = w.Settings.Input;
        foreach (var a in LauncherKeys.Actions)
        {
            if (a.Id == LauncherKeys.Home) Divider();
            string id = a.Id;
            var r = Opens(AddRow(a.Label, () => LauncherKeys.Names(p, id), activate: () => new LauncherKeyCard(w, id).Open()));
            if (LauncherKeys.PadCombo(id) is string combo) Aside(r, T("settings.controller") + combo);
        }
        Divider();
        AddRow(T("settings.back_to_the_defaults"), () => LauncherKeys.IsDefault(p) ? T("settings.in_use") : "", activate: () =>
        {
            if (LauncherKeys.IsDefault(p)) return;
            void Do() { LauncherKeys.Reset(p); LauncherKeys.Changed(w); Sounds.Play("favorite"); Refresh(); }
            var uses = LauncherKeys.Get(LauncherKeys.Home).Defaults.SelectMany(n => LauncherKeys.GameUses(w, n)).ToList();
            if (uses.Count > 0) new HomeKeyTakeCard(w, LauncherKeys.Show(LauncherKeys.Get(LauncherKeys.Home).Defaults[0]), uses, Do).Open();
            else Do();
        });
        AddRow(T("common.done"), activate: () => Close());
        Compact();
        if (focus != null) FocusRow(LauncherKeys.Label(focus));
    }

    public override void Open() { base.Open(); Current = this; }
    public override void Close(bool sound = true) { if (Current == this) Current = null; base.Close(sound); }
}

// One launcher action: its keys (each can be removed), and a new one.
public sealed class LauncherKeyCard : MenuCard, IScreen
{
    public static LauncherKeyCard Current;   // test scripts
    readonly string id;
    readonly StackPanel list = new();
    readonly TextBlock msg;
    readonly EscHold esc = new();
    bool capturing;
    public bool Capturing => capturing;
    public string Message => msg.Text;

    public LauncherKeyCard(MainWindow w, string id) : base(w, LauncherKeys.Label(id), 860)
    {
        this.id = id;
        RowHost = list;
        Body.Children.Add(list);
        msg = Note("", 24, new Thickness(4, 14, 0, 0));
        Build(0);
    }

    InputPrefs S => w.Settings.Input;

    void Build(int focus)
    {
        ClearRows();
        foreach (var n in LauncherKeys.Keys(S, id))
        {
            string name = n;
            AddRow(LauncherKeys.Show(name), () => LauncherKeys.CanRemove(S, id) ? T("common.remove") : "", activate: () => Remove(name));
        }
        if (LauncherKeys.Keys(S, id).Count < LauncherKeys.Max)
            AddRow(T("settings.add_a_key"), () => capturing ? T("settings.press_a_key") : "", activate: StartCapture);
        AddRow(T("common.done"), activate: () => Close());
        Focus = Math.Clamp(focus, 0, Rows.Count - 1);
        Refresh();
    }

    void Say(string text) => msg.Text = text;

    void Remove(string name)
    {
        if (!LauncherKeys.CanRemove(S, id)) { Sounds.Play("back"); Say(T("settings.needs_a_key", LauncherKeys.Label(id))); return; }
        LauncherKeys.Remove(S, id, name);
        LauncherKeys.Changed(w);
        Sounds.Play("select");
        Say($"{LauncherKeys.Show(name)} removed");
        Build(Focus);
    }

    void StartCapture()
    {
        capturing = true;
        Sounds.Play("select");
        Say(T("settings.press_a_key_hold_esc_for"));
        Refresh();
    }

    void EndCapture(string say)
    {
        capturing = false;
        esc.Stop();
        Say(say);
        Refresh();
    }

    public override void OnAct(Act a)
    {
        if (capturing) { if (a == Act.Back) { Sounds.Play("back"); EndCapture(T("settings.cancelled")); } return; }
        base.OnAct(a);
    }

    public override bool OnMouse(MouseAct m, Point p) => capturing ? (m == MouseAct.Down || m == MouseAct.Move) : base.OnMouse(m, p);

    bool IScreen.TakeKey(Key k)
    {
        if (!capturing) return false;
        if (esc.Waiting) return true;
        if (k is Key.ImeProcessed or Key.None or Key.System) return true;
        if (k == Key.Escape)
        {
            esc.Begin(w, () => { if (capturing) Take(Key.Escape); }, () => { if (capturing) { Sounds.Play("back"); EndCapture(T("settings.cancelled")); } });
            return true;
        }
        Take(k);
        return true;
    }

    // A right click while waiting cancels; other clicks do nothing.
    bool IScreen.TakeMouse(MouseButton b)
    {
        if (!capturing) return false;
        if (b == MouseButton.Right) { Sounds.Play("back"); EndCapture(T("settings.cancelled")); }
        return true;
    }

    void Take(Key k)
    {
        if (LauncherKeys.CantAdd(S, id, k, out string moved) is string why) { Sounds.Play("back"); Say(why + T("settings.pick_another_key_or_esc_2")); return; }
        string name = LauncherKeys.NameOf(k);
        void Do()
        {
            LauncherKeys.Add(S, id, k);
            LauncherKeys.Changed(w);
            capturing = false;
            Sounds.Play("favorite");
            Build(Focus);
            Say(moved != null ? T("settings.set_taken_from", LauncherKeys.Show(name), moved) : $"{LauncherKeys.Show(name)} set");
        }
        if (LauncherKeys.InGame(id) && LauncherKeys.GameUses(w, name) is { Count: > 0 } uses)
        {
            EndCapture("");
            new HomeKeyTakeCard(w, LauncherKeys.Show(name), uses, Do, id).Open();
            return;
        }
        Do();
    }

    public override void Open() { base.Open(); Current = this; }
    public override void Close(bool sound = true) { if (Current == this) Current = null; capturing = false; esc.Stop(); base.Close(sound); }
}

// A key games use is about to open the Home menu (or be another game key): what loses it.
public sealed class HomeKeyTakeCard : MenuCard
{
    public HomeKeyTakeCard(MainWindow w, string name, List<string> uses, Action yes, string id = LauncherKeys.Home) : base(w, T("settings.games_use", name), 960)
    {
        string list = string.Join("\n", uses.Take(4)) + (uses.Count > 4 ? $"\n+{uses.Count - 4} more" : "");
        Note(T("settings.these_get_no_key_while", list, name, LauncherKeys.Does(id)), 28, new Thickness(4, 0, 0, 26));
        AddRow(T("settings.pick_another_key"), activate: () => Close());
        AddRow(T("settings.use_anyway", name), activate: () => { Close(false); yes(); });
    }
}

// A game button can't have the Home menu key (or another game key): say so, and offer to move that key.
public sealed class HomeKeyNoticeCard : MenuCard
{
    public HomeKeyNoticeCard(MainWindow w, string name, string id = LauncherKeys.Home) : base(w, $"{name} {LauncherKeys.Does(id)}", 960)
    {
        string what = id == LauncherKeys.Home ? "the Home menu" : LauncherKeys.Label(id);
        Note(T("settings.to_use_in_games_give_another", name, what), 30, new Thickness(4, 0, 0, 26));
        Opens(AddRow(T("settings.change_the_key", what), activate: () => { Close(false); new LauncherKeyCard(w, id).Open(); }));
        AddRow(T("common.ok"), activate: () => Close());
    }
}
