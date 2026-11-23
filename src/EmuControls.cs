#nullable disable
// Ionea launcher: the launcher's game controls written into standalone emulators' own control settings
// when a game starts, so the Controls screens set them for every emulator.
//
//   shadPS4                       user\input_config\default.ini (InputMap.WritePs4: keyboard, mouse, controller)
//   Lichii, Eden (yuzu family)    user\config\qt-config.ini [Controls] player_0_*: keyboard and mouse, mouse camera
//   Azahar (Citra family)         user\config\qt-config.ini [Controls] the selected profile: keyboard
//   melonDS                       melonDS.toml [Instance0.Keyboard]: keyboard
//   mGBA                          config.ini [gba.input.QT_K]: keyboard
//   Dolphin                       User\Config\GCPadNew.ini [GCPad1]: keyboard, mouse and the first XInput controller
//
// Only the bound buttons are changed; every other line of the file stays as it was. The first time, the
// emulator's own file is kept beside it as <file>.before-launcher. The file is written whole through a
// .tmp file. An emulator that was never started (no settings file yet) is left alone: it writes its own
// defaults first. Set up for the controller, Lichii / Eden, Azahar and melonDS get the XInput controller's
// buttons by SDL's XInput layout (values to verify); mGBA keeps the controller set in it.
// An emulator whose "Ionea manages controls" switch is off is never written (Emulator.Controls).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Input;

namespace Ionea;

public static class EmuControls
{
    // The definition's controls mapping (format, file, systems), or null when the launcher doesn't write them:
    // no mapping, a core, or below support level Managed (e.g. a version outside the tested range).
    // Off with its "Ionea manages controls" switch (Emulator.Controls = false): it keeps its own.
    public static DefControls Mapping(Emulator e) => e?.Controls == false ? null : Defined(e);

    // The mapping Ionea could use, the switch aside: the emulator's page shows the switch only when there is one.
    public static DefControls Defined(Emulator e) =>
        e == null || e.IsCore || e.Exe == null ? null : EmuDefs.At(e, SupportLevel.Managed)?.Controls is DefControls c && c.Format != null ? c : null;

    // Its controls file from the definition, the switch aside (to see what changed while it was open).
    public static string DefinedFile(Emulator e) =>
        e?.Exe != null && !e.IsCore && EmuDefs.Of(e)?.Controls?.File is string f ? Path.Combine(Path.GetDirectoryName(e.Exe), f) : null;

    // How the launcher writes this emulator's controls (a writer below), or null when it doesn't.
    public static string Format(Emulator e) => Mapping(e)?.Format;

    // The launcher sets this game's controls in this emulator (the mapping may name systems: Dolphin's are
    // GameCube games only, its Wii controls stay its own).
    public static bool Writes(Emulator e, string system) =>
        Mapping(e) is DefControls c && (c.Systems == null || c.Systems.Count == 0 || c.Systems.Contains(system));

    // Changes made while the game runs reach it now (it reads its file again on a hotkey) or from the next start.
    public static bool LiveReload(Emulator e) => Mapping(e)?.LiveReload == true && Hotkey(e, h => h.ReloadInputs) != null;

    // One of its hotkeys from the definition (posted to its window, never typed), or null.
    public static string Hotkey(Emulator e, Func<DefHotkeys, string> which) =>
        e == null || e.IsCore || e.Controls == false ? null : EmuDefs.At(e, SupportLevel.Managed)?.Hotkeys is DefHotkeys h ? which(h) : null;

    // The emulator's controls file, from the definition.
    public static string FileOf(Emulator e) => Mapping(e)?.File is string f ? Path.Combine(Path.GetDirectoryName(e.Exe), f) : null;

    // The part of its controls file Ionea writes, or null (no file): a change there while the emulator was open
    // is a controls change; its other settings may share the file (Eden's qt-config.ini has its video too).
    public static string ControlsText(Emulator e)
    {
        string f = DefinedFile(e);
        try
        {
            if (f == null || !File.Exists(f)) return null;
            string text = File.ReadAllText(f);
            string[] secs = EmuDefs.Of(e)?.Controls?.Format switch
            {
                "yuzu" or "citra" => new[] { "Controls" },
                "melonds" => new[] { "Instance0.Keyboard", "Instance0.Joystick" },
                "mgba" => new[] { "gba.input.QT_K" },
                "dolphin" => new[] { "GCPad1" },
                _ => null,   // shadPS4: the whole file is controls
            };
            if (secs == null) return text;
            var ini = new IniText(text, "=");
            return string.Join("\n", secs.Select(ini.SectionText));
        }
        catch { return null; }
    }

    // Everything a writer reads from the launcher's controls for one game: when it differs from the value at
    // the game's start, the running emulator still has the old controls (HomeMenu: restart to use them).
    public static string Signature(InputPrefs p, ControlScope sc) =>
        $"{p.Main}|{p.KeyboardOn}|{p.PadOn}|{p.MouseOn}|{InputMap.MouseCamOn(p, sc)}|{p.MouseSpeed(sc)}|" + string.Join(",", InputMap.Binds(p, sc).Select(x => x.action + "=" + x.bind))
        + "|" + string.Join(",", sc.Layout.Actions.Select(a => a + "=" + p.PadFor(sc, a)));

    // Write this game's controls into the emulator's settings. True when a file changed.
    public static bool Write(Emulator e, InputPrefs p, ControlScope sc)
    {
        string fmt = Format(e);
        if (fmt == null) return false;
        if (!Writes(e, sc.System)) return false;
        if (fmt == "ps4") return InputMap.WritePs4(e, p, sc);
        string file = FileOf(e);
        try
        {
            if (file == null || !File.Exists(file)) { Log.Write($"{e.Name} controls: no settings file yet ({file}); it keeps its own until it has run once"); return false; }
            var ini = new IniText(File.ReadAllText(file), fmt == "melonds" || fmt == "dolphin" ? " = " : "=");
            string what = fmt switch
            {
                "yuzu" => Yuzu(ini, p, sc, Hotkey(e, h => h.MousePanning) ?? YuzuPanKey),
                "citra" => Citra(ini, p, sc),
                "melonds" => Melon(ini, p, sc),
                "mgba" => Mgba(ini, p, sc),
                "dolphin" => Dolphin(ini, p, sc),
                _ => null,
            };
            if (what == null) return false;
            string text = ini.Text;
            if (File.ReadAllText(file) == text) return false;
            string keep = file + ".before-launcher";
            if (!File.Exists(keep)) File.Copy(file, keep);
            File.WriteAllText(file + ".tmp", text, new UTF8Encoding(false));
            File.Move(file + ".tmp", file, true);
            Log.Write($"{e.Name} controls: wrote {file} for {(p.HasOwn(sc) ? sc.GameName : sc.Name)} ({what})");
            return true;
        }
        catch (Exception x) { Log.Write($"{e.Name} controls: {x.Message}"); return false; }
    }

    // ---- the launcher's binds ----

    // The one key / mouse button for an action (emulators take one per button), or null: none, or the
    // keyboard / mouse is turned off in the launcher.
    static string Bind(InputPrefs p, ControlScope sc, string action)
    {
        if (!sc.Layout.Has(action)) return null;
        string b = p.KeyFor(sc, action);
        if (b == null) return null;
        return InputMap.IsMouse(b) ? (p.MouseOn ? b : null) : (p.KeyboardOn ? b : null);
    }

    // A key's Qt key code (Qt::Key), as the Qt emulators store keys. 0 for none or a mouse button.
    public static int QtKey(string b)
    {
        if (b == null || InputMap.IsMouse(b) || !Enum.TryParse(b, out Key k)) return 0;
        return k switch
        {
            >= Key.A and <= Key.Z => 'A' + (k - Key.A),
            >= Key.D0 and <= Key.D9 => '0' + (k - Key.D0),
            >= Key.NumPad0 and <= Key.NumPad9 => '0' + (k - Key.NumPad0),
            Key.Left => 0x01000012, Key.Up => 0x01000013, Key.Right => 0x01000014, Key.Down => 0x01000015,
            Key.Return => 0x01000004, Key.Tab => 0x01000001, Key.Back => 0x01000003, Key.Space => 0x20,
            Key.LeftShift or Key.RightShift => 0x01000020, Key.LeftCtrl or Key.RightCtrl => 0x01000021,
            Key.OemComma => ',', Key.OemPeriod => '.', Key.OemQuestion => '/', Key.OemSemicolon => ';', Key.OemQuotes => '\'',
            Key.OemOpenBrackets => '[', Key.OemCloseBrackets => ']', Key.OemMinus => '-',
            Key.Escape => 0x01000000, Key.Home => 0x01000010, Key.End => 0x01000011, Key.PageUp => 0x01000016, Key.PageDown => 0x01000017,
            Key.LeftAlt or Key.RightAlt => 0x01000023,
            _ => 0,
        };
    }

    // ---- the XInput controller ----
    // The launcher reads XInput controllers only, so the controller an emulator is given is an Xbox-type one:
    // set up for the controller (Main "pad"), each button gets the controller's button instead of a key.
    // The Qt emulators and melonDS name it by SDL's joystick id and number, with SDL's own XInput layout
    // (no SDL_GameControllerDB). All values below are from memory: verify against a real Eden / Azahar /
    // melonDS with an Xbox controller. Other controllers (DualSense, Switch Pro) come later.
    static bool PadMain(InputPrefs p) => p.Main == "pad" && p.PadOn;

    // SDL's id for an XInput controller (USB, Microsoft 045E:028E, driver 'x', subtype 1). verify
    public const string XInputGuid = "030000005e0400008e02000000007801";
    // The id the emulator already uses for an XInput controller (one set up in its own window), else SDL's.
    static string PadGuid(string text) =>
        System.Text.RegularExpressions.Regex.Matches(text, @"guid:([0-9a-f]{32})").Select(m => m.Groups[1].Value).FirstOrDefault(g => g.EndsWith("7801")) ?? XInputGuid;

    // SDL's XInput layout: buttons, the d-pad as hat 0, triggers as axes 2 and 5, sticks 0/1 and 3/4. verify
    static int SdlButton(string id) => id switch
    {
        "a" => 0, "b" => 1, "x" => 2, "y" => 3, "lb" => 4, "rb" => 5, "back" => 6, "start" => 7, "ls" => 8, "rs" => 9, _ => -1,
    };
    static string SdlHat(string id) => id switch { "dup" => "up", "ddown" => "down", "dleft" => "left", "dright" => "right", _ => null };
    static int SdlTrigger(string id) => id switch { "lt" => 2, "rt" => 5, _ => -1 };
    static readonly (int x, int y)[] SdlSticks = { (0, 1), (3, 4) };

    // One controller button as a yuzu-family / Citra-family parameter, or null for none.
    static string SdlParam(string guid, string id, bool citra)
    {
        if (id == null) return null;
        string dev = $"engine:sdl,port:0,guid:{guid}";
        if (SdlButton(id) is int b && b >= 0) return citra ? $"button:{b},engine:sdl,guid:{guid},port:0" : $"{dev},button:{b}";
        if (SdlHat(id) is string h) return citra ? $"direction:{h},engine:sdl,guid:{guid},hat:0,port:0" : $"{dev},hat:0,direction:{h}";
        if (SdlTrigger(id) is int t && t >= 0) return citra ? $"axis:{t},direction:+,engine:sdl,guid:{guid},port:0,threshold:0.500000" : $"{dev},axis:{t},threshold:0.500000,invert:+";
        return null;
    }
    static string SdlStick(string guid, int which, bool citra)
    {
        var (x, y) = SdlSticks[which];
        return citra ? $"axis_x:{x},axis_y:{y},deadzone:0.100000,engine:sdl,guid:{guid},port:0"
                     : $"engine:sdl,port:0,guid:{guid},axis_x:{x},axis_y:{y},invert_x:+,invert_y:+,deadzone:0.150000,range:1.000000";
    }
    // The controller's button for an action, when set up for the controller and the layout has it.
    static string PadOf(InputPrefs p, ControlScope sc, string action) => PadMain(p) && sc.Layout.Has(action) ? p.PadFor(sc, action) : null;

    // melonDS's joystick numbers: a button, 0x100 | direction (hat 0), or 0x10000 | axis << 24 | 2 << 20 (a trigger). verify
    static int MelonPad(string id) =>
        id == null ? -1
        : SdlButton(id) is int b && b >= 0 ? b
        : SdlHat(id) is string h ? 0x100 | (h switch { "up" => 1, "right" => 2, "down" => 4, _ => 8 })
        : SdlTrigger(id) is int t && t >= 0 ? 0x10000 | (t << 24) | (2 << 20) : -1;

    // ---- Lichii / Eden (yuzu family): player 1 ----
    // Switch buttons by place: A right, B bottom, X top, Y left (InputMap.Switch).
    static readonly (string key, string action)[] YuzuButtons =
    {
        ("a", "east"), ("b", "south"), ("x", "north"), ("y", "west"), ("l", "l1"), ("r", "r1"), ("zl", "l2"), ("zr", "r2"),
        ("lstick", "l3"), ("rstick", "r3"), ("plus", "start"), ("minus", "select"),
        ("dup", "up"), ("ddown", "down"), ("dleft", "left"), ("dright", "right"),
    };

    // yuzu's "Toggle Mouse Panning" hotkey (theirs Ctrl+F9): a plain F3 (free there), so the launcher can post it.
    const string YuzuPanKeySeq = @"Shortcuts\Main%20Window\Toggle%20Mouse%20Panning\KeySeq";
    public const string YuzuPanKey = "F3";   // when the definition names none
    public static bool MousePanning(Emulator e) => Hotkey(e, h => h.MousePanning) != null;

    // Mouse camera feel. yuzu-family panning (Lichii/Eden input_common/drivers/mouse.cpp): each
    // pixel adds sensitivity * 0.001 to a stick accumulator, and every update multiplies it by
    // min(1 - min_decay/100, 1 - len^2 * decay_strength/100). Their defaults (6 / 18) leave a long glide after the mouse
    // stops and compress fast moves near full stick, so turn speed no longer follows hand speed. Here: no length-based
    // decay (linear up to full stick) and a 30% leak per update (the glide is gone in a few updates). Steady stick is
    // sens * 0.001 * px / 0.30, a third less than at the old 20 leak, so the right stick's range (input_converter
    // SanitizeStick: value / range) is 1/1.5 to give each Ionea level the same speed as before. The counterweight
    // (smallest stick while the mouse moves) is applied before that range, so 13 gives about 0.2 stick, as yuzu's 20 did.
    const int PanMinDecay = 30, PanDecayStrength = 0, PanCounterweight = 13;
    const string PanRange = "0.666667";
    public const string YuzuMouseStick = "engine:mouse,axis_x:0,axis_y:1,threshold:0.500000,range:" + PanRange + ",deadzone:0.000000,port:0";

    static string YuzuParam(string b)
    {
        int mb = b switch { "MouseLeft" => 0, "MouseRight" => 1, "MouseMiddle" => 2, "MouseBack" => 3, "MouseForward" => 4, _ => -1 };
        if (mb >= 0) return $"engine:mouse,port:0,button:{mb}";
        return $"engine:keyboard,code:{KeyOrNone(b)},toggle:0";
    }
    // No key: -1, as melonDS and mGBA. Never code 0: Qt reports keys it doesn't know (the unassigned 0xE8 that quiets a
    // tapped Alt, HomeKeys.Quiet) as 0, so a button left at 0 (yuzu's own default for Home and Screenshot) was pressed by
    // every Home menu Alt tap: Lichii's Capture Screenshot opened its Save as dialog.
    const string YuzuNone = "engine:keyboard,code:-1,toggle:0";
    static int KeyOrNone(string b) => QtKey(b) is int c && c != 0 ? c : -1;
    // A parameter inside another (yuzu's ParamPackage): ':' -> $0, ',' -> $1, '$' -> $2.
    static string Nest(string s) => s.Replace("$", "$2").Replace(":", "$0").Replace(",", "$1");

    static string Yuzu(IniText ini, InputPrefs p, ControlScope sc, string panKey)
    {
        bool pad = PadMain(p);
        if (!p.KeyboardOn && !p.MouseOn && !pad) return null;
        const string S = "Controls";
        int n = 0, np = 0;
        string guid = pad ? PadGuid(ini.Text) : null;
        var used = new HashSet<int>();
        foreach (var (key, action) in YuzuButtons)
        {
            string name = "player_0_button_" + key;
            if (SdlParam(guid, PadOf(p, sc, action), false) is string pp)
            {
                ini.Set(S, name + "\\default", "false");
                ini.Set(S, name, Q(pp));
                np++;
                continue;
            }
            string b = Bind(p, sc, action);
            ini.Set(S, name + "\\default", "false");
            ini.Set(S, name, Q(b == null ? YuzuNone : YuzuParam(b)));
            if (b != null) { n++; used.Add(QtKey(b)); }
        }
        bool cam = InputMap.MouseCamOn(p, sc);
        foreach (var (stick, ids) in new[] { ("lstick", SysLayout.StickIds), ("rstick", SysLayout.RightIds) })
        {
            string name = "player_0_" + stick, old = Unq(ini.Get(S, name));
            if (pad && !(stick == "rstick" && cam))
            {
                ini.Set(S, name + "\\default", "false");
                ini.Set(S, name, Q(SdlStick(guid, stick == "lstick" ? 0 : 1, false)));
                np++;
                continue;
            }
            if (stick == "rstick" && cam)
            {
                // Mouse camera: the right stick follows the mouse's movement (yuzu's mouse panning, the mouse
                // driver's axes 0/1 on port 0). Panning itself starts off at every launch (not saved); its
                // hotkey, moved to a plain key, is posted once the game is in front (GameMenu.SyncPanning).
                ini.Set(S, name + "\\default", "false");
                ini.Set(S, name, Q(YuzuMouseStick));
                continue;
            }
            // the slow-walk modifier stays (Shift on the left stick), unless a button now uses that key
            foreach (int i in Enumerable.Range(0, 4)) if (Bind(p, sc, ids[i]) is string db) used.Add(QtKey(db));
            string mod = Param(old, "modifier") ?? Nest(YuzuNone);
            if (int.TryParse(Param(mod.Replace("$0", ":").Replace("$1", ","), "code"), out int mc) && (mc == 0 || used.Contains(mc))) mod = Nest(YuzuNone);
            var dirs = new[] { "up", "down", "left", "right" }.Select((d, i) => d + ":" + Nest(Bind(p, sc, ids[i]) is string b ? YuzuParam(b) : YuzuNone));
            ini.Set(S, name + "\\default", "false");
            ini.Set(S, name, Q("engine:analog_from_button," + string.Join(",", dirs) + ",modifier:" + mod + ",modifier_scale:" + (Param(old, "modifier_scale") ?? "0.500000")));
            n += ids.Count(id => Bind(p, sc, id) != null);
        }
        ini.Set(S, "player_0_connected\\default", "false");
        ini.Set(S, "player_0_connected", "true");
        if (cam)
        {
            string sens = (p.MouseSpeed(sc) * 100 / InputMap.MouseSpeedMax).ToString();   // 1-10 -> 10-100 (theirs: 50)
            foreach (var k in new[] { "mouse_panning_x_sensitivity", "mouse_panning_y_sensitivity", "mouse_panning_sensitivity" })
            { ini.Set(S, k + "\\default", "false"); ini.Set(S, k, sens); }
            foreach (var (k, v) in new[] { ("mouse_panning_min_decay", PanMinDecay), ("mouse_panning_decay_strength", PanDecayStrength),
                                           ("mouse_panning_deadzone_counterweight", PanCounterweight) })
            { ini.Set(S, k + "\\default", "false"); ini.Set(S, k, v.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
            ini.Set(S, "mouse_enabled\\default", "false");
            ini.Set(S, "mouse_enabled", "false");   // the emulated Switch mouse turns panning off
            ini.Set("UI", YuzuPanKeySeq + "\\default", "false");
            ini.Set("UI", YuzuPanKeySeq, panKey);
        }
        return $"player 1, {n} keys" + (pad ? $", XInput controller {np} binds" : "") + (cam ? $", mouse camera at {p.MouseSpeed(sc)}" : "");
    }

    // ---- Azahar (Citra family): the selected controls profile ----
    static readonly (string key, string action)[] CitraButtons =
    {
        ("button_a", "east"), ("button_b", "south"), ("button_x", "north"), ("button_y", "west"), ("button_l", "l1"), ("button_r", "r1"),
        ("button_zl", "l2"), ("button_zr", "r2"), ("button_start", "start"), ("button_select", "select"),
        ("button_up", "up"), ("button_down", "down"), ("button_left", "left"), ("button_right", "right"),
    };

    static string Citra(IniText ini, InputPrefs p, ControlScope sc)
    {
        bool pad = PadMain(p);
        if (!p.KeyboardOn && !pad) return null;
        const string S = "Controls";
        string guid = pad ? PadGuid(ini.Text) : null;
        int np = 0;
        int prof = int.TryParse(ini.Get(S, "profile"), out int pi) ? pi + 1 : 1;
        string pre = $"profiles\\{prof}\\";
        if (ini.Get(S, pre + "button_a") == null && ini.Get(S, pre + "circle_pad") == null) return null;   // no such profile
        int n = 0;
        var used = new HashSet<int>();
        string CitraKey(string b) => $"code:{KeyOrNone(b)},engine:keyboard";
        foreach (var (key, action) in CitraButtons)
        {
            if (SdlParam(guid, PadOf(p, sc, action), true) is string pp)
            {
                ini.Set(S, pre + key + "\\default", "false");
                ini.Set(S, pre + key, Q(pp));
                np++;
                continue;
            }
            string b = p.KeyboardOn ? Bind(p, sc, action) : null;
            if (b != null && InputMap.IsMouse(b)) b = null;
            ini.Set(S, pre + key + "\\default", "false");
            ini.Set(S, pre + key, Q(CitraKey(b)));
            if (b != null) { n++; used.Add(QtKey(b)); }
        }
        foreach (var (stick, ids) in new[] { ("circle_pad", SysLayout.StickIds), ("c_stick", SysLayout.RightIds) })
        {
            string old = Unq(ini.Get(S, pre + stick));
            if (pad)
            {
                ini.Set(S, pre + stick + "\\default", "false");
                ini.Set(S, pre + stick, Q(SdlStick(guid, stick == "circle_pad" ? 0 : 1, true)));
                np++;
                continue;
            }
            string K(int i) { var b = Bind(p, sc, ids[i]); return Nest(CitraKey(b != null && !InputMap.IsMouse(b) ? b : null)); }
            // the half-tilt modifier stays, unless a button or direction now uses that key (Azahar's own is D, a WASD key)
            foreach (int i in Enumerable.Range(0, 4)) if (Bind(p, sc, ids[i]) is string db) used.Add(QtKey(db));
            string mod = Param(old, "modifier") ?? Nest("code:-1,engine:keyboard");
            if (int.TryParse(Param(mod.Replace("$0", ":").Replace("$1", ","), "code"), out int mc) && (mc == 0 || used.Contains(mc))) mod = Nest("code:-1,engine:keyboard");
            ini.Set(S, pre + stick + "\\default", "false");
            ini.Set(S, pre + stick, Q($"down:{K(1)},engine:analog_from_button,left:{K(2)},modifier:{mod},modifier_scale:{Param(old, "modifier_scale") ?? "0.500000"},right:{K(3)},up:{K(0)}"));
            n += ids.Count(id => Bind(p, sc, id) is string b && !InputMap.IsMouse(b));
        }
        return $"profile {prof}, {n} keys" + (pad ? $", XInput controller {np} binds" : "");
    }

    // ---- melonDS ----
    static readonly (string key, string action)[] MelonButtons =
    {
        ("A", "east"), ("B", "south"), ("X", "north"), ("Y", "west"), ("L", "l1"), ("R", "r1"), ("Start", "start"), ("Select", "select"),
        ("Up", "up"), ("Down", "down"), ("Left", "left"), ("Right", "right"),
    };

    static string Melon(IniText ini, InputPrefs p, ControlScope sc)
    {
        bool pad = PadMain(p);
        if (!p.KeyboardOn && !pad) return null;
        string S = "Instance0.Keyboard", J = "Instance0.Joystick";
        if (!ini.HasSection(S)) return null;
        int n = 0, np = 0;
        foreach (var (key, action) in MelonButtons)
        {
            int code = p.KeyboardOn ? QtKey(Bind(p, sc, action)) : 0;
            ini.Set(S, key, code == 0 ? "-1" : code.ToString());
            if (code != 0) n++;
            if (pad)
            {
                int j = MelonPad(PadOf(p, sc, action));
                ini.Set(J, key, j.ToString());
                if (j >= 0) np++;
            }
        }
        if (pad) ini.Set("Instance0", "JoystickID", "0");
        return $"{n} keys" + (pad ? $", XInput controller {np} binds" : "");
    }

    // ---- mGBA ----
    static readonly (string key, string action)[] MgbaButtons =
    {
        ("keyA", "east"), ("keyB", "south"), ("keyL", "l1"), ("keyR", "r1"), ("keyStart", "start"), ("keySelect", "select"),
        ("keyUp", "up"), ("keyDown", "down"), ("keyLeft", "left"), ("keyRight", "right"),
    };

    static string Mgba(IniText ini, InputPrefs p, ControlScope sc)
    {
        if (!p.KeyboardOn) return null;
        const string S = "gba.input.QT_K";
        int n = 0;
        foreach (var (key, action) in MgbaButtons)
        {
            int code = QtKey(Bind(p, sc, action));
            ini.Set(S, key, code == 0 ? "-1" : code.ToString());
            if (code != 0) n++;
        }
        return $"{n} keys";
    }

    // ---- Dolphin: GameCube controller 1 (keyboard and mouse, plus the first XInput controller) ----
    // GameCube buttons by place, as its layout (InputMap.GameCube): A bottom, B left, X right, Y top, Z right bumper, L / R the triggers.
    static readonly (string key, string action)[] DolphinButtons =
    {
        ("Buttons/A", "south"), ("Buttons/B", "west"), ("Buttons/X", "east"), ("Buttons/Y", "north"), ("Buttons/Z", "r1"), ("Buttons/Start", "start"),
        ("Triggers/L", "l2"), ("Triggers/R", "r2"),
        ("D-Pad/Up", "up"), ("D-Pad/Down", "down"), ("D-Pad/Left", "left"), ("D-Pad/Right", "right"),
    };
    static readonly (string key, string action, string pad)[] DolphinSticks =
    {
        ("Main Stick/Up", "ls_up", "Left Y+"), ("Main Stick/Down", "ls_down", "Left Y-"), ("Main Stick/Left", "ls_left", "Left X-"), ("Main Stick/Right", "ls_right", "Left X+"),
        ("C-Stick/Up", "rs_up", "Right Y+"), ("C-Stick/Down", "rs_down", "Right Y-"), ("C-Stick/Left", "rs_left", "Right X-"), ("C-Stick/Right", "rs_right", "Right X+"),
    };

    static string DolphinMouseAxis(string action) => action switch
    {
        "rs_up" => "Axis Y-", "rs_down" => "Axis Y+", "rs_left" => "Axis X-", "rs_right" => "Axis X+", _ => null,
    };

    // Dolphin's name for a key or mouse button on its "DInput/0/Keyboard Mouse" device.
    public static string DolphinKey(string b)
    {
        switch (b)
        {
            case null: return null;
            case "MouseLeft": return "Click 0";
            case "MouseRight": return "Click 1";
            case "MouseMiddle": return "Click 2";
            case "MouseBack": return "Click 3";
            case "MouseForward": return "Click 4";
        }
        if (!Enum.TryParse(b, out Key k)) return null;
        return k switch
        {
            >= Key.A and <= Key.Z => k.ToString(),
            >= Key.D0 and <= Key.D9 => ((int)(k - Key.D0)).ToString(),
            >= Key.NumPad0 and <= Key.NumPad9 => "NUMPAD" + (int)(k - Key.NumPad0),
            Key.Up => "UP", Key.Down => "DOWN", Key.Left => "LEFT", Key.Right => "RIGHT",
            Key.Return => "RETURN", Key.Space => "SPACE", Key.Tab => "TAB", Key.Back => "BACK",
            Key.LeftShift => "LSHIFT", Key.RightShift => "RSHIFT", Key.LeftCtrl => "LCONTROL", Key.RightCtrl => "RCONTROL",
            Key.OemComma => "COMMA", Key.OemPeriod => "PERIOD", Key.OemQuestion => "SLASH", Key.OemSemicolon => "SEMICOLON",
            Key.OemQuotes => "APOSTROPHE", Key.OemOpenBrackets => "LBRACKET", Key.OemCloseBrackets => "RBRACKET", Key.OemMinus => "MINUS",
            Key.Escape => "ESCAPE", Key.LeftAlt => "LMENU", Key.RightAlt => "RMENU", Key.Home => "HOME", Key.End => "END",   // DirectInput names, verify
            Key.PageUp => "PRIOR", Key.PageDown => "NEXT",
            _ => null,
        };
    }

    // Dolphin's XInput name for a launcher controller button.
    public static string DolphinPad(string id) => id switch
    {
        "a" => "Button A", "b" => "Button B", "x" => "Button X", "y" => "Button Y",
        "lb" => "Shoulder L", "rb" => "Shoulder R", "lt" => "Trigger L", "rt" => "Trigger R",
        "ls" => "Thumb L", "rs" => "Thumb R", "start" => "Start", "back" => "Back",
        "dup" => "Pad N", "ddown" => "Pad S", "dleft" => "Pad W", "dright" => "Pad E",
        _ => null,
    };
    const string XPad = "XInput/0/Gamepad";

    static string Dolphin(IniText ini, InputPrefs p, ControlScope sc)
    {
        const string S = "GCPad1";
        if (!ini.HasSection(S)) return null;
        ini.Set(S, "Device", "DInput/0/Keyboard Mouse");
        int n = 0;
        string Expr(string bind, string pad)
        {
            var parts = new List<string>();
            if (DolphinKey(bind) is string k) { parts.Add($"`{k}`"); n++; }
            if (p.PadOn && pad != null) parts.Add($"`{XPad}:{pad}`");
            return string.Join(" | ", parts);
        }
        var keys = new HashSet<string>();
        foreach (var (key, action) in DolphinButtons)
        {
            string b = Bind(p, sc, action);
            if (b != null) keys.Add(b);
            string pad = sc.Layout.Has(action) ? DolphinPad(p.PadFor(sc, action)) : null;
            ini.Set(S, key, Expr(b, pad));
            if (key.StartsWith("Triggers/")) ini.Set(S, key + "-Analog", p.PadOn && pad is string ap && ap.StartsWith("Trigger") ? $"`{XPad}:{ap}`" : "");
        }
        // Mouse camera: the C-stick also follows the mouse's movement (its relative "Axis" inputs; "Cursor" is the
        // pointer's place). Its keys still work alongside.
        bool cam = InputMap.MouseCamOn(p, sc);
        string scale = InputMap.DolphinMouseScale(p.MouseSpeed(sc)).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        foreach (var (key, action, pad) in DolphinSticks)
        {
            string b = Bind(p, sc, action);
            if (b != null) keys.Add(b);
            string e = Expr(b, pad);
            if (cam && DolphinMouseAxis(action) is string ax) e = (e.Length > 0 ? e + " | " : "") + $"(`{ax}` * {scale})";
            ini.Set(S, key, e);
        }
        // the half-tilt modifiers stay (Shift, Ctrl), unless a button now uses that key
        if (keys.Any(k => k is "LeftShift" or "RightShift") && ini.Get(S, "Main Stick/Modifier") is string m1 && m1.Contains("Shift")) ini.Set(S, "Main Stick/Modifier", "");
        if (keys.Any(k => k is "LeftCtrl" or "RightCtrl") && ini.Get(S, "C-Stick/Modifier") is string m2 && m2.Contains("Ctrl")) ini.Set(S, "C-Stick/Modifier", "");
        return $"controller 1, {n} keys{(p.PadOn ? ", XInput controller 1" : "")}{(cam ? $", mouse camera at {p.MouseSpeed(sc)}" : "")}";
    }

    // ---- small helpers ----
    static string Q(string v) => "\"" + v + "\"";
    static string Unq(string v) => v != null && v.Length >= 2 && v[0] == '"' && v[^1] == '"' ? v[1..^1] : v;

    // One top-level parameter of a ParamPackage ("a:1,b:2"), nested values left as they are.
    static string Param(string package, string name)
    {
        if (package == null) return null;
        foreach (var part in Unq(package).Split(','))
        {
            int i = part.IndexOf(':');
            if (i > 0 && part[..i] == name) return part[(i + 1)..];
        }
        return null;
    }
}

// An ini / toml text where a few keys of one section are set and every other line stays as it was.
public sealed class IniText
{
    readonly List<string> lines;
    readonly string sep;
    readonly string nl;

    public IniText(string text, string sep)
    {
        nl = text.Contains("\r\n") ? "\r\n" : "\n";
        lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[^1] == "") lines.RemoveAt(lines.Count - 1);
        this.sep = sep;
    }

    public string Text => string.Join(nl, lines) + nl;

    static bool IsSection(string l) => l.TrimStart().StartsWith("[") && l.TrimEnd().EndsWith("]");
    static string SectionName(string l) => l.Trim()[1..^1];

    int SectionStart(string section) => lines.FindIndex(l => IsSection(l) && SectionName(l) == section);
    public bool HasSection(string section) => SectionStart(section) >= 0;

    // The section's lines: from just after its header to the next header. Section "": the top of the file,
    // before any header (melonDS's TargetFPS).
    (int from, int to) Range(string section)
    {
        if (section == "") { int f = lines.FindIndex(IsSection); return (0, f < 0 ? lines.Count : f); }
        int s = SectionStart(section);
        if (s < 0) return (-1, -1);
        int e = s + 1;
        while (e < lines.Count && !IsSection(lines[e])) e++;
        return (s + 1, e);
    }

    int Find(string section, string key)
    {
        var (from, to) = Range(section);
        for (int i = from; i >= 0 && i < to; i++)
        {
            int eq = lines[i].IndexOf('=');
            if (eq > 0 && lines[i][..eq].Trim() == key) return i;
        }
        return -1;
    }

    public string Get(string section, string key)
    {
        int i = Find(section, key);
        return i < 0 ? null : lines[i][(lines[i].IndexOf('=') + 1)..].Trim();
    }

    public void Set(string section, string key, string value)
    {
        int i = Find(section, key);
        string line = key + sep + value;
        if (sep == " = " && value == "") line = key + " =";
        if (i >= 0) { lines[i] = line; return; }
        var (from, to) = Range(section);
        if (from < 0)
        {
            if (lines.Count > 0 && lines[^1].Trim() != "") lines.Add("");
            lines.Add("[" + section + "]");
            lines.Add(line);
            return;
        }
        int at = to;
        while (at > from && lines[at - 1].Trim() == "") at--;   // after the section's last line, before its blank lines
        lines.Insert(at, line);
    }

    // A section's lines as they are ("" when there is none).
    public string SectionText(string section)
    {
        var (from, to) = Range(section);
        return from < 0 ? "" : string.Join("\n", lines.Skip(from).Take(to - from));
    }

    public void Remove(string section, string key)
    {
        int i = Find(section, key);
        if (i >= 0) lines.RemoveAt(i);
    }

    // A section with nothing but blank lines left goes (its header and those lines; at the end of the file the
    // blank line before it too).
    public void RemoveIfEmpty(string section)
    {
        if (section == "") return;
        var (from, to) = Range(section);
        if (from < 0) return;
        for (int i = from; i < to; i++) if (lines[i].Trim() != "") return;
        int start = from - 1;
        if (to == lines.Count && start > 0 && lines[start - 1].Trim() == "") start--;
        lines.RemoveRange(start, to - start);
    }
}
