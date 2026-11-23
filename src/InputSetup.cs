#nullable disable
// Input setup: which input devices are on, their pop-ups, the game controls, and the mouse options.
// Every game that runs inside the launcher (a libretro core) and every shadPS4 game takes its controls
// from here; Steam / PC games and other emulators read the devices themselves.
// Game buttons are "actions" by position, like a PlayStation pad. Each console has its own set
// (Layouts): only the buttons it has, named the way that console names them, and recommended keys.
// A profile keeps its own keys and controller buttons per console, and per game over that.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Windows.Input;

namespace Ionea;

public sealed class KnownDevice
{
    public string Kind { get; set; }        // "keyboard", "mouse", "pad"
    public string Key { get; set; }         // the physical device (container id), or the pad's HID key
    public string Name { get; set; }
    public DateTime LastSeen { get; set; }
}

// One console's (or one game's) own controls. Missing entries use the console's recommended ones.
public sealed class BindSet
{
    public Dictionary<string, string> Keys { get; set; }   // action -> key name or mouse button ("MouseLeft" ...); "" = none
    public Dictionary<string, string> Pad { get; set; }    // action -> controller button id
    public bool? MouseCamera { get; set; }                 // PS4 / Switch: the mouse moves the right stick (null = on)
    public int? MouseSpeed { get; set; }                   // PS4 / Switch: mouse look sensitivity 1-10 (null = InputMap.MouseSpeedDefault)
    [JsonIgnore] public bool Empty => (Keys == null || Keys.Count == 0) && (Pad == null || Pad.Count == 0) && MouseCamera == null && MouseSpeed == null;
}

// Whose controls: a console, and one game of it when GameId is set.
public sealed record ControlScope(string System, string GameId = null, string GameName = null)
{
    public SysLayout Layout => InputMap.LayoutFor(System);
    public string Name => InputMap.SystemName(System);
}

public sealed class InputPrefs
{
    public bool KeyboardOn { get; set; } = true;
    public bool PadOn { get; set; } = true;
    public bool MouseOn { get; set; } = true;
    public bool Popups { get; set; } = true;            // all input pop-ups
    public bool KeyboardPopups { get; set; } = true;
    public bool PadPopups { get; set; } = true;
    public bool MousePopups { get; set; } = true;
    public int CursorSize { get; set; } = 1;            // 0 small, 1 normal, 2 large
    public bool HoverSounds { get; set; } = true;
    public bool Touch { get; set; } = true;             // the mouse is the DS / 3DS touch screen
    public Dictionary<string, string> Roles { get; set; }   // device key -> "keyboard" / "mouse" / "both", learned from what it sends
    public List<KnownDevice> Seen { get; set; }
    public Dictionary<string, BindSet> Systems { get; set; }   // console id ("gba", "ps4" ...) -> its controls
    public Dictionary<string, BindSet> Games { get; set; }     // game id -> that game's own controls
    public bool SetupDone { get; set; }                 // the first-start controls setup was answered
    public string Main { get; set; }                    // "keyboard" or "pad": what the setup was done for
    public Dictionary<string, List<string>> LauncherKeys { get; set; }   // launcher action -> its keys, when not the defaults (LauncherKeys.cs)

    public bool PopupsFor(string kind) => Popups && kind switch { "keyboard" => KeyboardPopups, "mouse" => MousePopups, _ => PadPopups };
    public bool On(string kind) => kind switch { "keyboard" => KeyboardOn, "mouse" => MouseOn, _ => PadOn };

    BindSet Sys(string sys) => Systems != null && sys != null && Systems.TryGetValue(sys, out var b) ? b : null;
    BindSet Own(ControlScope sc) => sc.GameId != null && Games != null && Games.TryGetValue(sc.GameId, out var b) ? b : null;
    public bool HasOwn(ControlScope sc) => Own(sc) != null;

    // What an edit changes: the game's own controls if it has them, else the console's.
    BindSet Target(ControlScope sc)
    {
        if (Own(sc) is BindSet g) return g;
        Systems ??= new();
        if (!Systems.TryGetValue(sc.System, out var s)) Systems[sc.System] = s = new BindSet();
        return s;
    }

    // A key name ("K"), a mouse button ("MouseLeft"), or null for none. The Home menu key (and the other game
    // keys, LauncherKeys.InGame) is never one: a button bound to it has no key until that key moves.
    public string KeyFor(ControlScope sc, string action)
    {
        string v = null;
        if (!(Own(sc)?.Keys?.TryGetValue(action, out v) == true || Sys(sc.System)?.Keys?.TryGetValue(action, out v) == true))
            v = sc.Layout.DefaultKey(action);
        string b = InputMap.ValidBind(v);
        return Ionea.LauncherKeys.TakesBind(this, b) ? null : b;
    }

    public string PadFor(ControlScope sc, string action)
    {
        var a = InputMap.Get(action);
        if (a.Stick) return null;
        string v = null;
        if ((Own(sc)?.Pad?.TryGetValue(action, out v) == true || Sys(sc.System)?.Pad?.TryGetValue(action, out v) == true)
            && InputMap.PadButtons.Any(b => b.Id == v)) return v;
        return a.DefaultPad;
    }

    public bool MouseCamera(ControlScope sc) => Own(sc)?.MouseCamera ?? Sys(sc.System)?.MouseCamera ?? true;
    public void SetMouseCamera(ControlScope sc, bool on) => Target(sc).MouseCamera = on;

    public int MouseSpeed(ControlScope sc) => Math.Clamp(Own(sc)?.MouseSpeed ?? Sys(sc.System)?.MouseSpeed ?? InputMap.MouseSpeedDefault, 1, InputMap.MouseSpeedMax);
    public void SetMouseSpeed(ControlScope sc, int level) => Target(sc).MouseSpeed = Math.Clamp(level, 1, InputMap.MouseSpeedMax);

    // Binding a key that another button of this console has swaps the two, so no key does two things.
    public void SetKey(ControlScope sc, string action, string bind)
    {
        var t = Target(sc);
        t.Keys ??= new();
        string old = KeyFor(sc, action);
        foreach (var a in sc.Layout.Actions)
            if (a != action && bind != null && KeyFor(sc, a) == bind) t.Keys[a] = old ?? "";
        t.Keys[action] = bind ?? "";
    }

    public void SetPad(ControlScope sc, string action, string button)
    {
        if (InputMap.Get(action).Stick) return;
        var t = Target(sc);
        t.Pad ??= new();
        string old = PadFor(sc, action);
        foreach (var a in sc.Layout.Actions)
            if (a != action && !InputMap.Get(a).Stick && PadFor(sc, a) == button) t.Pad[a] = old;
        t.Pad[action] = button;
    }

    public void ResetKeys(ControlScope sc) { var t = Target(sc); t.Keys = null; t.MouseCamera = null; t.MouseSpeed = null; Tidy(sc); }
    public void ResetPad(ControlScope sc) { Target(sc).Pad = null; Tidy(sc); }

    // This game gets its own controls (starting as the console's), or goes back to the console's.
    public void SetOwn(ControlScope sc, bool own)
    {
        if (sc.GameId == null) return;
        if (own) { Games ??= new(); if (!Games.ContainsKey(sc.GameId)) Games[sc.GameId] = new BindSet(); }
        else Games?.Remove(sc.GameId);
    }

    // Every console back to the recommended controls (the setup's "Recommended"); games' own ones go too.
    public void UseRecommended() { Systems = null; Games = null; }

    void Tidy(ControlScope sc)
    {
        if (Own(sc) == null && Sys(sc.System) is BindSet s && s.Empty) Systems.Remove(sc.System);
    }

    public bool Changed(string sys) => Sys(sys) is BindSet s && !s.Empty;
}

// One game button. Libretro id (-1 for stick directions), PlayStation name in shadPS4's input file.
public sealed record GameAction(string Id, string Label, int Retro, string Ps4, string DefaultPad, bool Stick = false);
public sealed record PadButton(string Id, string Label, ushort Mask, string Sdl);

// A console's buttons, as that console names them, and the keys recommended for it.
public sealed class SysLayout
{
    public string Id;
    public (string Action, string Label)[] Buttons;    // face buttons, shoulders, start / select (in that order)
    public bool Dpad = true, Stick, RightStick, Touch, Ps4;
    public string StickName = "Left stick", RightStickName = "Right stick";
    public Dictionary<string, string> Keys = new();    // recommended: action -> key name / mouse button

    public static readonly string[] DpadIds = { "up", "down", "left", "right" };
    public static readonly string[] StickIds = { "ls_up", "ls_down", "ls_left", "ls_right" };
    public static readonly string[] RightIds = { "rs_up", "rs_down", "rs_left", "rs_right" };

    public IEnumerable<string> Directions =>
        (Dpad ? DpadIds : Array.Empty<string>()).Concat(Stick ? StickIds : Array.Empty<string>()).Concat(RightStick ? RightIds : Array.Empty<string>());
    public IEnumerable<string> Actions => Buttons.Select(b => b.Action).Concat(Directions);
    public bool Has(string action) => Actions.Contains(action);

    public string DefaultKey(string action) => Keys.TryGetValue(action, out var k) ? k : null;

    public string Label(string action)
    {
        foreach (var (a, l) in Buttons) if (a == action) return l;
        string dir = action[(action.IndexOf('_') + 1)..];
        if (action.StartsWith("ls_")) return $"{StickName} {dir}";
        if (action.StartsWith("rs_")) return $"{RightStickName} {dir}";
        return "D-pad " + action;
    }
}

public static class InputMap
{
    public static readonly GameAction[] Actions =
    {
        new("south", "Bottom button", 0, "cross", "a"),
        new("east", "Right button", 8, "circle", "b"),
        new("west", "Left button", 1, "square", "x"),
        new("north", "Top button", 9, "triangle", "y"),
        new("l1", "Left bumper", 10, "l1", "lb"),
        new("r1", "Right bumper", 11, "r1", "rb"),
        new("l2", "Left trigger", 12, "l2", "lt"),
        new("r2", "Right trigger", 13, "r2", "rt"),
        new("l3", "Left stick click", 14, "l3", "ls"),
        new("r3", "Right stick click", 15, "r3", "rs"),
        new("start", "Start", 3, "options", "start"),
        new("select", "Select", 2, "touchpad_center", "back"),
        new("up", "D-pad up", 4, "pad_up", "dup"),
        new("down", "D-pad down", 5, "pad_down", "ddown"),
        new("left", "D-pad left", 6, "pad_left", "dleft"),
        new("right", "D-pad right", 7, "pad_right", "dright"),
        new("ls_up", "Left stick up", -1, "axis_left_y_minus", null, true),
        new("ls_down", "Left stick down", -1, "axis_left_y_plus", null, true),
        new("ls_left", "Left stick left", -1, "axis_left_x_minus", null, true),
        new("ls_right", "Left stick right", -1, "axis_left_x_plus", null, true),
        new("rs_up", "Right stick up", -1, "axis_right_y_minus", null, true),
        new("rs_down", "Right stick down", -1, "axis_right_y_plus", null, true),
        new("rs_left", "Right stick left", -1, "axis_right_x_minus", null, true),
        new("rs_right", "Right stick right", -1, "axis_right_x_plus", null, true),
    };

    public static GameAction Get(string id) => Actions.First(a => a.Id == id);

    public static readonly PadButton[] PadButtons =
    {
        new("a", "A", 0x1000, "cross"), new("b", "B", 0x2000, "circle"), new("x", "X", 0x4000, "square"), new("y", "Y", 0x8000, "triangle"),
        new("lb", "LB", 0x0100, "l1"), new("rb", "RB", 0x0200, "r1"), new("lt", "LT", 0, "l2"), new("rt", "RT", 0, "r2"),
        new("ls", "Left stick click", 0x0040, "l3"), new("rs", "Right stick click", 0x0080, "r3"),
        new("start", "Menu", 0x0010, "options"), new("back", "View", 0x0020, "back"),
        new("dup", "D-pad up", 0x0001, "pad_up"), new("ddown", "D-pad down", 0x0002, "pad_down"),
        new("dleft", "D-pad left", 0x0004, "pad_left"), new("dright", "D-pad right", 0x0008, "pad_right"),
    };
    public static PadButton Button(string id) => PadButtons.FirstOrDefault(b => b.Id == id);

    // ---- the consoles ----

    static Dictionary<string, string> K(params (string a, string k)[] list) => list.ToDictionary(x => x.a, x => x.k);
    // Recommended keys: the usual PC-game layout for a player with one hand on the mouse. All of
    // it is in reach of the left hand around WASD: the console's confirm / interact button on E, its other main
    // button (back, jump, dodge) on Space, the other two face buttons on R and F, the shoulders on Q and Shift,
    // Start Enter, Select Tab. Consoles whose right stick is the camera (mouse camera) put the triggers on the
    // clicks (right trigger left click, left trigger right click, right stick click middle click), the d-pad on
    // 1 2 3 4 and the keyboard camera on the arrows. No Ctrl: Lichii / Eden and Azahar use it for their own
    // hotkeys (Ctrl+S, Ctrl+F, Ctrl+Z), which a held shoulder would set off.
    static readonly (string, string)[] Wasd = { ("up", "W"), ("down", "S"), ("left", "A"), ("right", "D") };
    static readonly (string, string)[] WasdStick = { ("ls_up", "W"), ("ls_down", "S"), ("ls_left", "A"), ("ls_right", "D") };
    static readonly (string, string)[] Arrows = { ("up", "Up"), ("down", "Down"), ("left", "Left"), ("right", "Right") };
    static readonly (string, string)[] ArrowStick = { ("rs_up", "Up"), ("rs_down", "Down"), ("rs_left", "Left"), ("rs_right", "Right") };
    static readonly (string, string)[] NumberPad = { ("up", "D1"), ("down", "D2"), ("left", "D3"), ("right", "D4") };
    // Nintendo's places: A (right) E, B (bottom) Space, X (top) R, Y (left) F, L Q, R Shift.
    static readonly (string, string)[] NintendoFace = { ("east", "E"), ("south", "Space"), ("north", "R"), ("west", "F"), ("l1", "Q"), ("r1", "LeftShift"), ("start", "Return"), ("select", "Tab") };
    // PlayStation / Xbox places: ✕ / A (bottom) E, ○ / B (right) Space, □ / X (left) F, △ / Y (top) R, L1 Q, R1 Shift.
    static readonly (string, string)[] PadFace = { ("south", "E"), ("east", "Space"), ("west", "F"), ("north", "R"), ("l1", "Q"), ("r1", "LeftShift"), ("start", "Return"), ("select", "Tab") };
    static readonly (string, string)[] MouseTriggers = { ("r2", "MouseLeft"), ("l2", "MouseRight"), ("l3", "C"), ("r3", "MouseMiddle") };
    static Dictionary<string, string> KeySet(params (string, string)[][] parts) => K(parts.SelectMany(x => x).ToArray());

    static readonly SysLayout GameBoy = new()
    {
        Id = "gb", Buttons = new[] { ("east", "A"), ("south", "B"), ("start", "Start"), ("select", "Select") }, Keys = KeySet(NintendoFace, Wasd),
    };
    static readonly SysLayout Gba = new()
    {
        Id = "gba", Buttons = new[] { ("east", "A"), ("south", "B"), ("l1", "L"), ("r1", "R"), ("start", "Start"), ("select", "Select") }, Keys = KeySet(NintendoFace, Wasd),
    };
    static readonly SysLayout Snes = new()
    {
        Id = "snes", Buttons = new[] { ("east", "A"), ("south", "B"), ("north", "X"), ("west", "Y"), ("l1", "L"), ("r1", "R"), ("start", "Start"), ("select", "Select") }, Keys = KeySet(NintendoFace, Wasd),
    };
    // DS and 3DS: the mouse is the touch screen, so no clicks are used for buttons.
    static readonly SysLayout Ds = new()
    {
        Id = "nds", Buttons = Snes.Buttons, Keys = KeySet(NintendoFace, Wasd), Touch = true,
    };
    static readonly SysLayout N3ds = new()
    {
        Id = "3ds",
        Buttons = new[] { ("east", "A"), ("south", "B"), ("north", "X"), ("west", "Y"), ("l1", "L"), ("r1", "R"), ("l2", "ZL"), ("r2", "ZR"), ("start", "Start"), ("select", "Select") },
        Stick = true, StickName = "Circle Pad", RightStick = true, RightStickName = "C-Stick", Touch = true,
        Keys = KeySet(NintendoFace, new[] { ("l2", "Z"), ("r2", "C") }, WasdStick, NumberPad, ArrowStick),
    };
    // Dolphin's core puts GameCube A on the bottom button, B on the left, X on the right and Y on top.
    // A (the main button) E, B Space, X F, Y R, Z Q, the R trigger left click, the L trigger right click.
    static readonly SysLayout GameCube = new()
    {
        Id = "gc",
        Buttons = new[] { ("south", "A"), ("west", "B"), ("east", "X"), ("north", "Y"), ("r1", "Z"), ("l2", "L"), ("r2", "R"), ("start", "Start") },
        Stick = true, StickName = "Control Stick", RightStick = true, RightStickName = "C-Stick",
        Keys = KeySet(new[] { ("south", "E"), ("west", "Space"), ("east", "F"), ("north", "R"), ("r1", "Q"), ("l2", "MouseRight"), ("r2", "MouseLeft"), ("start", "Return") }, WasdStick, NumberPad, ArrowStick),
    };
    // "Souls-style": WASD moves, the mouse is the camera, clicks attack and guard.
    static readonly SysLayout Ps4 = new()
    {
        Id = "ps4", Ps4 = true,
        Buttons = new[] { ("south", "✕"), ("east", "○"), ("west", "□"), ("north", "△"), ("l1", "L1"), ("r1", "R1"), ("l2", "L2"), ("r2", "R2"), ("l3", "L3"), ("r3", "R3"), ("start", "Options"), ("select", "Touch pad") },
        Stick = true, RightStick = true,
        Keys = K(new[]
        {
            ("r1", "MouseLeft"), ("r2", "LeftShift"), ("l1", "MouseRight"), ("l2", "LeftCtrl"),
            ("east", "Space"), ("south", "E"), ("north", "R"), ("west", "F"),
            ("start", "Return"), ("select", "Tab"), ("r3", "MouseMiddle"), ("l3", "Q"),
        }.Concat(WasdStick).Concat(Arrows).ToArray()),
    };
    // Switch and Switch 2 (Lichii, Eden): Nintendo's places, A right, B bottom, X top, Y left; + and − are Start and Select.
    // ZR left click, ZL right click (shoot and aim), the left stick click C (crouch), the right one middle click.
    static readonly SysLayout Switch = new()
    {
        Id = "switch",
        Buttons = new[] { ("east", "A"), ("south", "B"), ("north", "X"), ("west", "Y"), ("l1", "L"), ("r1", "R"), ("l2", "ZL"), ("r2", "ZR"), ("l3", "Left stick click"), ("r3", "Right stick click"), ("start", "+"), ("select", "−") },
        Stick = true, RightStick = true,
        Keys = KeySet(NintendoFace, MouseTriggers, WasdStick, NumberPad, ArrowStick),
    };
    // PS1 / PS2 / PS3: the PlayStation face buttons on the PS4's places (libretro B = ✕, A = ○,
    // Y = □, X = △), Start and Select, both sticks and their clicks; the PS4 keys as recommended.
    static readonly SysLayout PlayStation = new()
    {
        Id = "psx",
        Buttons = new[] { ("south", "✕"), ("east", "○"), ("west", "□"), ("north", "△"), ("l1", "L1"), ("r1", "R1"), ("l2", "L2"), ("r2", "R2"), ("l3", "L3"), ("r3", "R3"), ("start", "Start"), ("select", "Select") },
        Stick = true, RightStick = true, Keys = Ps4.Keys,
    };
    static readonly SysLayout Psp = new()
    {
        Id = "psp",
        Buttons = new[] { ("south", "✕"), ("east", "○"), ("west", "□"), ("north", "△"), ("l1", "L"), ("r1", "R"), ("start", "Start"), ("select", "Select") },
        Stick = true, StickName = "Analog stick",
        Keys = KeySet(PadFace, WasdStick, NumberPad),
    };
    // PS5: the PS4's buttons (Options, touch pad) and keys, without shadPS4's mouse row (Ps4 = false).
    static readonly SysLayout Ps5 = new()
    {
        Id = "ps5", Buttons = Ps4.Buttons, Stick = true, RightStick = true, Keys = Ps4.Keys,
    };
    // Wii U GamePad: Nintendo's places like the Switch; + and − are Start and Select.
    static readonly SysLayout WiiU = new()
    {
        Id = "wiiu", Buttons = Switch.Buttons, Stick = true, RightStick = true, Keys = Switch.Keys,
    };
    // Any other console: every button, by position, on the Xbox / PlayStation places.
    static readonly SysLayout Generic = new()
    {
        Id = "",
        Buttons = new[] { ("south", "Bottom button"), ("east", "Right button"), ("west", "Left button"), ("north", "Top button"), ("l1", "Left bumper"), ("r1", "Right bumper"), ("l2", "Left trigger"), ("r2", "Right trigger"), ("l3", "Left stick click"), ("r3", "Right stick click"), ("start", "Start"), ("select", "Select") },
        Stick = true, RightStick = true,
        Keys = KeySet(PadFace, MouseTriggers, WasdStick, NumberPad, ArrowStick),
    };

    public static SysLayout LayoutFor(string sys) => sys switch
    {
        "gb" or "gbc" or "nes" => GameBoy,
        "gba" => Gba,
        "snes" => Snes,
        "nds" => Ds,
        "3ds" => N3ds,
        "gc" => GameCube,
        "ps4" => Ps4,
        "ps5" => Ps5,
        "psx" or "ps2" or "ps3" => PlayStation,
        "psp" => Psp,
        "switch" or "switch2" => Switch,
        "wiiu" => WiiU,
        _ => Generic,
    };

    public static string SystemName(string sys) => EmuCatalog.System(sys)?.Name ?? (string.IsNullOrEmpty(sys) ? "Games" : sys);

    // The console a game is played as: its own system, else the first its emulator plays.
    public static string SystemOf(MainWindow w, Game g) => g.System ?? Ports.EmuOf(w, g)?.Systems?.FirstOrDefault() ?? "";
    public static ControlScope ScopeFor(MainWindow w, Game g) => new(SystemOf(w, g), g.Id, g.Name);

    // The launcher sets this game's controls: it runs in a core, or in an emulator whose controls file it writes (EmuControls).
    public static bool Managed(MainWindow w, Game g) => Ports.EmuOf(w, g) is Emulator e && (e.IsCore || EmuControls.Writes(e, SystemOf(w, g)));

    // Consoles whose right stick is the camera in most 3D games: there the mouse moves it unless turned off
    // (shadPS4: its mouse-to-joystick; Lichii / Eden: mouse panning, EmuControls.Yuzu; Dolphin: its relative
    // mouse axes on the C-stick, EmuControls.Dolphin; the Dolphin core: CoreSession.MouseLook).
    // Not here: consoles whose emulators' controls the launcher doesn't write (PS2, PS3, Wii U), and the
    // 3DS, where the mouse is the touch screen.
    // Which consoles these are is the definitions' "mouseCamera": true in systems.json (was a
    // fixed list here), so a user's own systems.json can add one; the shipped four if no definition says so.
    static readonly string[] DefaultMouseCam = { "ps4", "switch", "switch2", "gc" };
    public static string[] MouseCamSystems
    {
        get
        {
            var on = EmuCatalog.Systems.Where(s => s.MouseCamera).Select(s => s.Id).ToList();
            if (on.Count == 0) return DefaultMouseCam;
            return DefaultMouseCam.Where(on.Contains).Concat(on.Where(id => !DefaultMouseCam.Contains(id))).ToArray();   // the shipped order first
        }
    }
    public static bool MouseCamSystem(string system) => MouseCamSystems.Contains(system);
    public static bool MouseCamOn(InputPrefs p, ControlScope sc) => MouseCamSystem(sc.System) && p.MouseOn && p.MouseCamera(sc);

    // A game played with the mouse as its camera in an emulator with a mouse-look hotkey (shadPS4): the
    // emulator must capture the mouse (hidden, kept in the window).
    public static bool MouseLookWanted(MainWindow w, Game g) =>
        g != null && EmuControls.Hotkey(Ports.EmuOf(w, g), h => h.MouseLook) != null && w.Settings.Input is var p && p.MouseOn && p.MouseCamera(ScopeFor(w, g));

    // A core game where the mouse is the touch screen (DS, 3DS): the cursor shows. Anywhere else in a game it stays hidden.
    public static bool TouchMouse(MainWindow w, Game g) =>
        g != null && w.Settings.Input is var p && p.MouseOn && p.Touch && LayoutFor(SystemOf(w, g)).Touch;

    // Every console the user has added, in the catalog's order: one with a game in the library, or an emulator,
    // core or native port for it. With none yet: the usual ones.
    // (this listed only consoles with games whose controls the launcher writes, so PS4 vanished once
    // Bloodborne played through bbport, a port whose controls are its own. A console's binds now show and are
    // kept whichever program plays it; they reach the program where the launcher writes its controls.)
    static readonly HashSet<string> NotConsoles = new() { "pc", "windows", "windows3x", "windows9x", "steam", "dos" };
    public static List<string> ManagedSystems(MainWindow w)
    {
        var have = w.Lib.Data.Games.Where(g => !g.Removed).Select(g => SystemOf(w, g)).ToHashSet();
        foreach (var e in w.Emus.Data.Emulators.Where(e => e.Present))
        {
            foreach (var s in e.Systems ?? new()) have.Add(s);
            foreach (var pg in EmuDefs.Of(e)?.Port?.Games ?? new()) have.Add(pg.System);
        }
        have.RemoveWhere(s => string.IsNullOrEmpty(s) || NotConsoles.Contains(s));
        if (have.Count == 0) have = new HashSet<string> { "gba", "nds", "3ds", "gc", "ps4" };
        var order = EmuCatalog.Systems.Select(s => s.Id).ToList();
        return have.OrderBy(s => order.IndexOf(s) < 0 ? 999 : order.IndexOf(s)).ToList();
    }

    // ---- binds: a WPF key name, or a mouse button ----

    public static readonly string[] MouseBinds = { "MouseLeft", "MouseRight", "MouseMiddle", "MouseBack", "MouseForward" };
    public static bool IsMouse(string b) => b != null && b.StartsWith("Mouse");

    public static string MouseBind(MouseButton m) => m switch
    {
        MouseButton.Left => "MouseLeft", MouseButton.Right => "MouseRight", MouseButton.Middle => "MouseMiddle",
        MouseButton.XButton1 => "MouseBack", MouseButton.XButton2 => "MouseForward", _ => null,
    };

    public static string ValidBind(string b)
    {
        if (string.IsNullOrEmpty(b)) return null;
        if (MouseBinds.Contains(b)) return b;
        return Enum.TryParse(b, out Key k) && Bindable(k) ? k.ToString() : null;
    }

    public static int Vk(string b) => b switch
    {
        null => 0,
        "MouseLeft" => 0x01, "MouseRight" => 0x02, "MouseMiddle" => 0x04, "MouseBack" => 0x05, "MouseForward" => 0x06,
        _ => Enum.TryParse(b, out Key k) ? KeyInterop.VirtualKeyFromKey(k) : 0,
    };

    public static string BindName(string b) => b switch
    {
        null => "—",
        "MouseLeft" => "Left click", "MouseRight" => "Right click", "MouseMiddle" => "Middle click",
        "MouseBack" => "Mouse back", "MouseForward" => "Mouse forward",
        _ => Enum.TryParse(b, out Key k) ? KeyName(k) : b,
    };

    // Keys that can be bound. F-keys and Delete (emulator hotkeys) and the Windows keys are kept free, and
    // the Home menu key while it is that (InputPrefs.KeyFor; Alt by default).
    public static bool Bindable(Key k) =>
        k is >= Key.A and <= Key.Z or >= Key.D0 and <= Key.D9 or >= Key.NumPad0 and <= Key.NumPad9
        or Key.Up or Key.Down or Key.Left or Key.Right or Key.Return or Key.Space or Key.Tab or Key.Back
        or Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl
        or Key.Escape or Key.LeftAlt or Key.RightAlt or Key.Home or Key.End or Key.PageUp or Key.PageDown
        or Key.OemComma or Key.OemPeriod or Key.OemQuestion or Key.OemSemicolon or Key.OemQuotes
        or Key.OemOpenBrackets or Key.OemCloseBrackets or Key.OemMinus;

    // Short name on a callout and on the drawn key.
    public static string KeyName(Key k) => k switch
    {
        >= Key.D0 and <= Key.D9 => ((int)(k - Key.D0)).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "Num " + (int)(k - Key.NumPad0),
        Key.Up => "↑", Key.Down => "↓", Key.Left => "←", Key.Right => "→",
        Key.Return => "Enter", Key.Space => "Space", Key.Tab => "Tab", Key.Back => "Backspace",
        Key.LeftShift => "L Shift", Key.RightShift => "R Shift", Key.LeftCtrl => "L Ctrl", Key.RightCtrl => "R Ctrl",
        Key.OemComma => ",", Key.OemPeriod => ".", Key.OemQuestion => "/", Key.OemSemicolon => ";", Key.OemQuotes => "'",
        Key.OemOpenBrackets => "[", Key.OemCloseBrackets => "]", Key.OemMinus => "-",
        Key.Escape => "Esc", Key.LeftAlt => "L Alt", Key.RightAlt => "R Alt", Key.PageUp => "Page Up", Key.PageDown => "Page Down",
        Key.Apps => "Menu key", Key.CapsLock => "Caps Lock",
        _ => k.ToString(),
    };

    // shadPS4's name for a key or mouse button (input_config/*.ini).
    public static string Ps4Name(string b)
    {
        switch (b)
        {
            case null: return null;
            case "MouseLeft": return "leftbutton";
            case "MouseRight": return "rightbutton";
            case "MouseMiddle": return "middlebutton";
            case "MouseBack": return "sidebuttonback";
            case "MouseForward": return "sidebuttonforward";
        }
        if (!Enum.TryParse(b, out Key k)) return null;
        return k switch
        {
            >= Key.A and <= Key.Z => k.ToString().ToLowerInvariant(),
            >= Key.D0 and <= Key.D9 => ((int)(k - Key.D0)).ToString(),
            >= Key.NumPad0 and <= Key.NumPad9 => "kp" + (int)(k - Key.NumPad0),
            Key.Up => "up", Key.Down => "down", Key.Left => "left", Key.Right => "right",
            Key.Return => "enter", Key.Space => "space", Key.Tab => "tab", Key.Back => "backspace",
            Key.LeftShift => "lshift", Key.RightShift => "rshift", Key.LeftCtrl => "lctrl", Key.RightCtrl => "rctrl",
            Key.OemComma => "comma", Key.OemPeriod => "period", Key.OemQuestion => "slash", Key.OemSemicolon => "semicolon",
            Key.OemQuotes => "apostrophe", Key.OemOpenBrackets => "lbracket", Key.OemCloseBrackets => "rbracket", Key.OemMinus => "minus",
            Key.Escape => "escape", Key.LeftAlt => "lalt", Key.RightAlt => "ralt", Key.Home => "home", Key.End => "end",
            Key.PageUp => "pgup", Key.PageDown => "pgdown",
            _ => null,
        };
    }

    // Every key / mouse button that works for an action, counting the arrow keys, which also move the
    // d-pad when this console doesn't use them for anything else ("WASD and the arrows").
    public static List<(string action, string bind)> Binds(InputPrefs p, ControlScope sc)
    {
        var L = sc.Layout;
        var list = new List<(string action, string bind)>();
        foreach (var a in L.Actions)
        {
            string b = p.KeyFor(sc, a);
            if (b == null) continue;
            if (IsMouse(b) ? !p.MouseOn || (b == "MouseLeft" && L.Touch && p.Touch) : !p.KeyboardOn) continue;
            list.Add((a, b));
        }
        if (L.Dpad && p.KeyboardOn)
            foreach (var (a, arrow) in Arrows)
                if (!L.Actions.Any(x => p.KeyFor(sc, x) == ValidBind(arrow)) && !Ionea.LauncherKeys.TakesBind(p, ValidBind(arrow))) list.Add((a, ValidBind(arrow)));
        return list;
    }

    // ---- for the emulation thread: a snapshot of one game's controls ----
    public sealed class Snapshot
    {
        public (int vk, int retro)[] Buttons;   // key / mouse button -> libretro button
        public int[] StickKeys;                  // vk for ls up/down/left/right, rs up/down/left/right (0 = none)
        public (ushort mask, int trigger, int retro)[] Pad;   // trigger: 0 none, 1 LT, 2 RT
        public bool PadOn, HasStick;
        public int MouseCam;                     // mouse camera: its sensitivity level (0 = off)
        public int[] FastKeys = Array.Empty<int>(), RewindKeys = Array.Empty<int>(), ShotKeys = Array.Empty<int>();   // vk of the game keys (LauncherKeys): fast-forward, rewind, screenshot
    }

    public static Snapshot Snap(InputPrefs p, ControlScope sc)
    {
        var L = sc.Layout;
        var binds = Binds(p, sc);
        var s = new Snapshot { PadOn = p.PadOn, HasStick = L.Stick, MouseCam = L.RightStick && MouseCamOn(p, sc) ? p.MouseSpeed(sc) : 0 };
        s.Buttons = binds.Where(x => !Get(x.action).Stick).Select(x => (Vk(x.bind), Get(x.action).Retro)).Where(x => x.Item1 != 0).ToArray();
        s.StickKeys = SysLayout.StickIds.Concat(SysLayout.RightIds).Select(id => binds.Where(x => x.action == id).Select(x => Vk(x.bind)).FirstOrDefault()).ToArray();
        s.Pad = L.Actions.Where(a => !Get(a).Stick).Select(a =>
        {
            var b = Button(p.PadFor(sc, a));
            return (b.Mask, b.Id == "lt" ? 1 : b.Id == "rt" ? 2 : 0, Get(a).Retro);
        }).ToArray();
        if (p.KeyboardOn) { s.FastKeys = Ionea.LauncherKeys.Vks(p, Ionea.LauncherKeys.Fast); s.RewindKeys = Ionea.LauncherKeys.Vks(p, Ionea.LauncherKeys.Rewind); s.ShotKeys = Ionea.LauncherKeys.Vks(p, Ionea.LauncherKeys.Shot); }
        return s;
    }

    // ---- PS4 mouse look sensitivity ----
    // shadPS4 v0.21 (src\input\input_mouse.cpp) turns mouse movement into right-stick deflection
    // (0-128) every 33 ms:
    //   deflection = clamp(pixels moved * speed + speed_offset * 128, deadzone_offset * 128, 128)
    // set by "mouse_movement_params = deadzone_offset, speed, speed_offset" (its default 0.5, 1, 0.125).
    // Any movement at all gives at least the floor (deadzone_offset), and the stick is full once
    // (128 - offset*128) / speed pixels arrive in one poll.
    // The first mapping lifted the floor with the level (0.74 at level 9) and used speeds up
    // to 6, so a barely moved mouse already gave 74% stick and ~20 px gave 100%: the camera worked like an
    // on/off switch, jumpy on small moves and capped on big ones. Now the floor is fixed just past a typical
    // game stick deadzone, the offset equals the floor (deflection rises straight from it, no flat part),
    // and the level only sets how many pixels per poll reach full stick: 200 at level 1 down to 15 at
    // level 10, 0.75x per step. The game's own camera speed option is the top limit (full stick); raise
    // that in the game for faster turns.
    public const int MouseSpeedMax = 10, MouseSpeedDefault = 5;
    public const double MouseFloor = 0.35;
    static readonly double[] MouseSpeeds = { 0.4, 0.55, 0.75, 1.0, 1.3, 1.75, 2.35, 3.1, 4.2, 5.5 };
    public static (double Deadzone, double Speed, double Offset) Ps4MouseParams(int level)
    {
        level = Math.Clamp(level, 1, MouseSpeedMax);
        return (MouseFloor, MouseSpeeds[level - 1], MouseFloor);
    }

    // Stick deflection (0-128) shadPS4 gives for this many pixels in one poll, as in input_mouse.cpp.
    public static double Ps4MouseStick(int level, double pixels)
    {
        var (dz, sp, off) = Ps4MouseParams(level);
        return pixels <= 0 ? 0 : Math.Clamp(pixels * sp + off * 128, dz * 128, 128);
    }

    // A launcher-hosted core's right stick (libretro, -0x7fff..0x7fff) for this many pixels moved: the PS4 curve.
    public static int CoreMouseStick(int level, int pixels) =>
        Math.Sign(pixels) * (int)(Ps4MouseStick(level, Math.Abs(pixels)) / 128 * 0x7fff);

    // Dolphin's multiplier on its mouse axes (a smoothed movement / 8 per poll) for a sensitivity level.
    public static double DolphinMouseScale(int level) => MouseSpeeds[Math.Clamp(level, 1, MouseSpeedMax) - 1];

    // Test check: the curve must not work like a switch. "ok", or what broke.
    public static string CheckMouseCurve()
    {
        for (int l = 1; l <= MouseSpeedMax; l++)
        {
            if (Ps4MouseStick(l, 1) > 0.40 * 128) return $"level {l}: 1 px already gives {Ps4MouseStick(l, 1):0} of 128";
            if (Ps4MouseStick(l, 14) >= 128) return $"level {l}: full stick below 15 px";
            for (int px = 1; px < 200 && Ps4MouseStick(l, px) < 128; px++)
                if (Ps4MouseStick(l, px + 1) <= Ps4MouseStick(l, px)) return $"level {l}: flat at {px} px";
            if (l > 1)
                for (int px = 1; px <= 200; px++)
                    if (Ps4MouseStick(l, px) < Ps4MouseStick(l - 1, px)) return $"level {l} slower than {l - 1} at {px} px";
        }
        return "ok";
    }

    // ---- shadPS4: input_config\default.ini (used for every game while use_unified_input_config is on) ----
    public static string Ps4Ini(InputPrefs p, ControlScope sc)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Written by the launcher from its game controls. Changes made here are replaced when a PS4 game starts.");
        sb.AppendLine("# The first start kept the old file as default.ini.before-launcher.");
        sb.AppendLine($"# Controls of: {(p.HasOwn(sc) ? sc.GameName + " (its own)" : sc.Name)}");
        sb.AppendLine();
        var binds = Binds(p, sc);
        if (binds.Count > 0)
        {
            sb.AppendLine("# Keyboard and mouse bindings");
            foreach (var (a, b) in binds) if (Ps4Name(b) is string k) sb.AppendLine($"{Get(a).Ps4} = {k}");
            sb.AppendLine();
        }
        else sb.AppendLine("# Keyboard and mouse: turned off in the launcher").AppendLine();
        if (p.PadOn)
        {
            sb.AppendLine("# Controller bindings");
            foreach (var a in Actions.Where(a => !a.Stick)) sb.AppendLine($"{a.Ps4} = {Button(p.PadFor(sc, a.Id)).Sdl}");
            foreach (var ax in new[] { "axis_left_x", "axis_left_y", "axis_right_x", "axis_right_y" }) sb.AppendLine($"{ax} = {ax}");
            sb.AppendLine();
        }
        else sb.AppendLine("# Controller: turned off in the launcher").AppendLine();
        if (p.MouseOn && p.MouseCamera(sc))
        {
            sb.AppendLine("# Mouse: moves the right stick (the camera). F7 turns it on and off in the game.");
            sb.AppendLine("mouse_to_joystick = right");
            int lvl = p.MouseSpeed(sc);
            var (dz, sp, off) = Ps4MouseParams(lvl);
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            sb.AppendLine($"# Mouse look sensitivity {lvl} of {MouseSpeedMax}: deadzone offset, speed, speed offset");
            sb.AppendLine(string.Format(inv, "mouse_movement_params = {0:0.###}, {1:0.###}, {2:0.###}", dz, sp, off));
            sb.AppendLine();
        }
        sb.AppendLine("# Range of deadzones: 1 (almost none) to 127 (max)");
        sb.AppendLine("analog_deadzone = leftjoystick, 5, 127");
        sb.AppendLine("analog_deadzone = rightjoystick, 5, 127");
        sb.AppendLine();
        sb.AppendLine("override_controller_color = false, 0, 0, 255");
        return sb.ToString();
    }

    // Called when a shadPS4 game starts, and when its controls change while it runs. The first time,
    // the emulator's own file is kept beside it. True when the file changed.
    public static bool WritePs4(Emulator e, InputPrefs p, ControlScope sc)
    {
        try
        {
            string root = Path.GetDirectoryName(e.Exe);
            string file = EmuControls.FileOf(e) ?? Path.Combine(root, "user", "input_config", "default.ini"), keep = file + ".before-launcher";
            string top = Path.GetRelativePath(root, file).Split('\\', '/')[0];
            if (top != Path.GetFileName(file) && !Directory.Exists(Path.Combine(root, top))) return false;   // never started: it writes its own defaults first
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            string text = Ps4Ini(p, sc);
            if (File.Exists(file) && File.ReadAllText(file) == text) return false;
            if (File.Exists(file) && !File.Exists(keep)) File.Copy(file, keep);
            File.WriteAllText(file + ".tmp", text);
            File.Move(file + ".tmp", file, true);
            Log.Write($"shadPS4 input: wrote {file} for {(p.HasOwn(sc) ? sc.GameName : sc.Name)} (keyboard {(p.KeyboardOn ? "on" : "off")}, controller {(p.PadOn ? "on" : "off")}, mouse camera {(p.MouseOn && p.MouseCamera(sc) ? "on, sensitivity " + p.MouseSpeed(sc) : "off")})");
            return true;
        }
        catch (Exception ex) { Log.Write("shadPS4 input: " + ex.Message); return false; }
    }
}
