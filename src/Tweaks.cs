#nullable disable
// Tweaks: an emulator's graphics and performance settings, per game or for all its games.
// Every emulator shows the same shared list, by the same names and in the same order (SharedList):
// resolution, upscaling, aspect ratio, frame pacing, anti-aliasing, texture filtering, renderer, shader
// cache, HDR, speed. A definition maps each to its own key and values ("shared"); what an emulator lacks
// is left out. The Game settings card shows them in boxes (SettingGroups): Display, Image quality,
// Performance, Audio, Controls, boxes a definition names ("group"), and Advanced. A setting with neither a
// shared id nor a group is placed by its key and name, so any emulator's settings land in a fitting box.
//  - Cores (inside the launcher): a chosen few of the core's own settings. Per game they live in
//    Game.Tweaks and win over core-options\<core>.json.
//  - "json" (shadPS4): per game its own user\custom_configs\<CUSA…>.json, for all games user\config.json.
//  - "ini" (yuzu family, Azahar, Dolphin, melonDS, mGBA): keys "[Section]key" in the emulator's own ini /
//    toml. Per game the emulator's own per-game file ({gameid}), or, for an emulator without one
//    (ioneaApplies: melonDS, mGBA), Game.Tweaks that Ionea writes into the file when the game starts and
//    takes back out when it ends (video-applied\<emulator>.json keeps what was there).
// Every tweak takes effect when the game starts, so a running game is offered "Restart now" or "Apply on
// next launch".
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using static Ionea.Loc;

namespace Ionea;

public sealed class TweakDef
{
    public string Key;                  // core option key, "GPU.fsr_enabled" (json section.key) or "[Renderer]backend" (ini)
    public string Label;
    public List<string> Values = new();
    public List<string> Names;          // on-screen names of Values (null = the value itself)
    public string Default;              // the emulator's own value when nothing is set
    public string Type = "string";      // json type: bool, int, string (ini values are written as listed)
    public string Shared;               // its place in the shared list, null: its own name
    public string Group;                // its box on the Game settings card (the definition's, else by Shared)
    public string File;                 // ini: its every-game file when not the video file
    // "mod:<folder>": one game's mod in the emulator's mod folder ({gameid} in it), on or off (Tweaks.ModState).
    public bool IsMod => Key != null && Key.StartsWith("mod:");
    public string Show(string v)
    {
        int i = Values.IndexOf(v);
        string s = Names != null && i >= 0 && i < Names.Count && !string.IsNullOrEmpty(Names[i]) ? Names[i] : v;
        return string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];   // "disabled" -> "Disabled"
    }

    // The listed value a file's text stands for ("True" for "true", "1.00000000" for "1"), or null.
    public string Match(string raw)
    {
        if (raw == null) return null;
        raw = raw.Trim().Trim('"');
        if (Values.Contains(raw)) return raw;
        foreach (var v in Values) if (Tweaks.Same(v, raw)) return v;
        return null;
    }
}

public sealed class TweakSet
{
    public string Kind;                 // "core" (core options), "json" or "ini" (the emulator's own files)
    public Emulator Emu;
    public DefVideo Video;              // the definition's video mapping
    public string Serial;               // per game in the emulator's own file: the game's id ({gameid})
    public List<TweakDef> Defs = new();
    // Per game the values are Ionea's own (Game.Tweaks), not in a file of the emulator's.
    public bool InLibrary => Kind == "core" || Video?.IoneaApplies == true;
    public IEnumerable<TweakDef> Main => Defs.Where(d => d.Shared != null);
    public IEnumerable<TweakDef> More => Defs.Where(d => d.Shared == null);

    // The settings in their boxes (SettingGroups): the standard boxes in their order, a definition's own boxes
    // before Advanced. Empty boxes are left out.
    public List<(string Title, List<TweakDef> Defs)> Boxes() =>
        SettingGroups.Arrange(Defs, d => SettingGroups.Of(d.Group, d.Shared, d.Key, d.Label));
}

// The boxes every emulator's settings go in: Game settings, an emulator's settings for every
// game, and a core's own settings during a game. A setting goes by: its definition's "group" (a standard name or a
// common other name for one maps to that box; any other name is a box of its own), its shared id, words in its key
// and name, a core's own category, else Advanced.
public static class SettingGroups
{
    public const string Display = "Display", Quality = "Image quality", Performance = "Performance",
        Audio = "Audio", Controls = "Controls", Advanced = "Advanced";
    public static readonly string[] Standard = { Display, Quality, Performance, Audio, Controls, Advanced };

    static readonly Dictionary<string, string> SharedBox = new()
    {
        ["resolution"] = Display, ["aspect"] = Display, ["hdr"] = Display,
        ["upscaling"] = Quality, ["antialiasing"] = Quality, ["filtering"] = Quality,
        ["pacing"] = Performance, ["renderer"] = Performance, ["shadercache"] = Performance, ["speed"] = Performance,
    };

    // Other names for the standard boxes (a definition's group, a core's category).
    static readonly Dictionary<string, string> Alias = new(StringComparer.OrdinalIgnoreCase)
    {
        ["display"] = Display, ["screen"] = Display, ["screens"] = Display, ["layout"] = Display, ["window"] = Display, ["output"] = Display,
        ["image quality"] = Quality, ["quality"] = Quality, ["graphics"] = Quality, ["video"] = Quality, ["rendering"] = Quality,
        ["enhancements"] = Quality, ["enhancement"] = Quality, ["image"] = Quality, ["visual"] = Quality, ["visuals"] = Quality,
        ["performance"] = Performance, ["speed"] = Performance, ["timing"] = Performance, ["frame rate"] = Performance, ["cpu"] = Performance,
        ["audio"] = Audio, ["sound"] = Audio,
        ["controls"] = Controls, ["input"] = Controls, ["controller"] = Controls, ["controllers"] = Controls, ["joypad"] = Controls, ["pad"] = Controls,
        ["advanced"] = Advanced, ["more"] = Advanced, ["more settings"] = Advanced, ["other"] = Advanced, ["misc"] = Advanced,
        ["system"] = Advanced, ["hacks"] = Advanced, ["emulation"] = Advanced, ["debug"] = Advanced,
    };

    // Words in a setting's key or name, checked in this order (the first box with a match wins). Words of three
    // letters or fewer must stand alone ("fps", "hz"); longer ones may be part of a word ("upscal" in "upscaling").
    static readonly (string Box, string[] Words)[] Rules =
    {
        (Audio, new[] { "audio latency", "audio buffer", "audio backend", "audio driver", "audio interpolation", "audio sync" }),
        (Performance, new[] { "shader cache", "compil", "background", "async", "sync", "jit", "recompil", "thread", "cpu", "frameskip",
            "frame skip", "skip duplicate", "fps", "frame rate", "framerate", "speed", "overclock", "underclock", "latency", "pacing", "limit",
            "renderer", "backend", "vulkan", "opengl", "directx", "d3d", "accura", "dynarec", "interpreter" }),
        (Controls, new[] { "input", "controller", "gamepad", "joypad", "joystick", "rumble", "vibration", "mouse", "keyboard", "touch",
            "stick", "deadzone", "dead zone", "turbo", "button", "gyro", "analog", "lightgun", "light gun", "pointer", "hotkey", "pad" }),
        (Audio, new[] { "audio", "sound", "volume", "mute", "dsp", "reverb", "sample rate", "samplerate", "music", "sfx", "microphone", "mic" }),
        (Quality, new[] { "upscal", "sharpen", "fsr", "alias", "msaa", "ssaa", "fxaa", "smaa", "taa", "supersampl", "anisotrop", "texture",
            "filter", "bloom", "blur", "shadow", "occlusion", "ssao", "ssr", "reflection", "depth of field", "dof", "detail", "lod", "polygon",
            "quality", "colour", "color", "gamma", "brightness", "contrast", "saturation", "scanline", "crt", "interlac", "dither", "blend",
            "shader", "effect", "lighting", "fog", "enhance", "chromatic", "precision", "smooth", "mipmap", "hd" }),
        (Display, new[] { "resolution", "aspect", "screen", "layout", "widescreen", "hdr", "fullscreen", "full screen", "refresh", "vsync",
            "v-sync", "vblank", "crop", "overscan", "integer", "rotat", "display", "window", "scale", "scaling", "hz", "border", "gap", "orientation" }),
    };

    // The box a setting goes in. group: the definition's; shared: its shared id; hint: a core's own category.
    // The name speaks first, then the key; an ini key's [Section] or a json key's "Section." is only a hint.
    public static string Of(string group, string shared = null, string key = null, string label = null, string hint = null)
    {
        if (!string.IsNullOrWhiteSpace(group)) return Alias.TryGetValue(group.Trim(), out var a) ? a : group.Trim();
        if (shared != null && SharedBox.TryGetValue(shared, out var sb)) return sb;
        string section = null;
        if (key != null && Regex.Match(key, @"^\[([^\]]*)\](.*)$|^([^.\[]+)\.(.+)$") is { Success: true } m)
        {
            section = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[3].Value;
            key = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[4].Value;
        }
        if ((ByWords(label) ?? ByWords(key)) is string w) return w;
        foreach (var x in new[] { section, hint })
            if (!string.IsNullOrWhiteSpace(x) && (Alias.TryGetValue(x.Trim(), out var h) || (h = ByWords(x)) != null)) return h;
        return Advanced;
    }

    static string ByWords(string phrase)
    {
        if (string.IsNullOrWhiteSpace(phrase)) return null;
        // "fsr_enabled", "melonds_render_mode", "FrameSkip", "Audio low-pass filter" -> " fsr enabled ", " frame skip ", ...
        string text = Regex.Replace(phrase, "([a-z])([A-Z])", "$1 $2");
        text = " " + Regex.Replace(text, "[^A-Za-z0-9-]+", " ").ToLowerInvariant() + " ";
        foreach (var (box, words) in Rules)
            foreach (var wd in words)
                if (wd.Length <= 3 ? text.Contains(" " + wd + " ") : text.Contains(wd)) return box;
        return null;
    }

    // Items in their boxes: the standard boxes in their order, any other (a definition's own) before Advanced in
    // the order they first come. Empty boxes are left out.
    public static List<(string Title, List<T> Items)> Arrange<T>(IEnumerable<T> items, Func<T, string> boxOf)
    {
        var list = items.Select(i => (i, b: boxOf(i))).ToList();
        var order = Standard.Where(b => b != Advanced).ToList();
        foreach (var (_, b) in list) if (!Standard.Contains(b) && !order.Contains(b)) order.Add(b);
        order.Add(Advanced);
        return order.Select(t => (t, list.Where(x => x.b == t).Select(x => x.i).ToList())).Where(x => x.Item2.Count > 0).ToList();
    }
}

// What each core offers, learned from the core itself: loaded for a moment with no game, or (melonDS DS
// lists its settings only with a game) from the last game it played. Kept in <data>\core-catalog.
public static class CoreCatalog
{
    public sealed class Saved
    {
        public string Stamp { get; set; }
        public List<SavedOption> Options { get; set; } = new();
    }
    public sealed class SavedOption
    {
        public string Key { get; set; }
        public string Desc { get; set; }
        public string Default { get; set; }
        public List<string> Values { get; set; }
        public List<string> Labels { get; set; }
    }

    static string Stamp(string dll) { var f = new FileInfo(dll); return $"{f.Length}-{f.LastWriteTimeUtc.Ticks}"; }
    static string DiskPath(string dll) => Tweaks.DataDir == null ? null : Path.Combine(Tweaks.DataDir, "core-catalog", Path.GetFileNameWithoutExtension(dll) + ".json");

    static List<CoreOption> FromDisk(string dll)
    {
        try
        {
            string f = DiskPath(dll);
            if (f == null || !File.Exists(f)) return null;
            var s = Json.Load<Saved>(f);
            if (s.Stamp != Stamp(dll) || s.Options.Count == 0) return null;   // a different build of the core
            return s.Options.Select(o => new CoreOption { Key = o.Key, Desc = o.Desc, Values = o.Values ?? new(), Labels = o.Labels, Default = o.Default, Value = o.Default }).ToList();
        }
        catch { return null; }
    }

    static void ToDisk(string dll, List<CoreOption> l)
    {
        try
        {
            string f = DiskPath(dll);
            if (f == null || l.Count == 0) return;
            Directory.CreateDirectory(Path.GetDirectoryName(f));
            Json.Save(f, new Saved { Stamp = Stamp(dll), Options = l.Select(o => new SavedOption { Key = o.Key, Desc = o.Desc, Default = o.Default, Values = o.Values, Labels = o.Labels }).ToList() });
        }
        catch (Exception e) { Log.Write("core settings: " + e.Message); }
    }

    public static readonly object Gate = new();
    static readonly Dictionary<string, List<CoreOption>> cache = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, int> inUse = new(StringComparer.OrdinalIgnoreCase);

    // A running game's core must not be loaded again (it would get a second environment callback).
    public static void InUse(string dll, int d) { lock (Gate) inUse[dll] = Math.Max(0, (inUse.TryGetValue(dll, out int n) ? n : 0) + d); }

    public static void Remember(string dll, List<CoreOption> opts)
    {
        var l = opts.Select(o => new CoreOption { Key = o.Key, Desc = o.Desc, Values = o.Values.ToList(), Labels = o.Labels?.ToList(), Default = o.Default ?? o.Values.FirstOrDefault(), Value = o.Default ?? o.Values.FirstOrDefault() }).ToList();
        lock (Gate)
        {
            if (l.Count == 0 && cache.TryGetValue(dll, out var had) && had.Count > 0) return;   // keep what a game showed
            cache[dll] = l;
            ToDisk(dll, l);
        }
    }

    public static List<CoreOption> Get(string dll)
    {
        if (dll == null || !File.Exists(dll)) return new();
        lock (Gate)
        {
            if (cache.TryGetValue(dll, out var l)) return l;
            if (FromDisk(dll) is List<CoreOption> disk) { cache[dll] = disk; return disk; }
            if (inUse.TryGetValue(dll, out int n) && n > 0) return new();
            try
            {
                string dir = Path.GetDirectoryName(dll);
                l = RetroCore.Probe(dll, Path.Combine(dir, "system"), Path.Combine(DataDirOrTemp(), "core-probe"));
                Log.Write($"core settings: {Path.GetFileName(dll)} lists {l.Count}");
            }
            catch (Exception e) { Log.Write($"core settings: {Path.GetFileName(dll)}: {e.Message}"); l = new(); }
            Remember(dll, l);
            return cache[dll];
        }
    }

    static string DataDirOrTemp() => Tweaks.DataDir ?? Path.GetTempPath();

    // Learn the cores' settings in the background, so the menus open at once.
    public static void Warm(IEnumerable<string> dlls)
    {
        var list = dlls.Where(d => d != null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count > 0) Task.Run(() => { foreach (var d in list) Get(d); });
    }
}

public static class Tweaks
{
    // The shared list: one set of names in one order for every emulator.
    public static readonly (string Id, string Label)[] SharedList =
    {
        ("resolution", "Resolution"), ("upscaling", "Upscaling"), ("aspect", "Aspect ratio"), ("pacing", "Frame pacing"),
        ("antialiasing", "Anti-aliasing"), ("filtering", "Texture filtering"), ("renderer", "Renderer"),
        ("shadercache", "Shader cache"), ("hdr", "HDR"), ("speed", "Speed"),
    };


    // The settings worth having at hand come from the emulator's definition (video.settings). Core: their
    // key, name and place; the values are the core's own (CoreCatalog), or the definition's while the core
    // lists none yet (melonDS DS lists its settings only with a game).
    static DefVideo VideoOf(Emulator e) =>
        e == null ? null : EmuDefs.At(e, e.IsCore ? SupportLevel.Core : SupportLevel.Managed)?.Video is DefVideo v && v.Settings?.Count > 0 ? v : null;

    static TweakDef FromSetting(DefSetting d) => new()
    {
        Key = d.Key, Label = d.Label ?? d.Key, Type = d.Type ?? "string", Values = d.Values?.ToList() ?? new(), Names = d.Names?.ToList(), Default = d.Default,
        Shared = d.Shared, Group = d.Group, File = d.File,
    };

    // Shared rows take the shared name and go first, in the shared order; the rest keep their own (More).
    static List<TweakDef> Ordered(List<TweakDef> l)
    {
        foreach (var d in l)
        {
            int i = Array.FindIndex(SharedList, s => s.Id == d.Shared);
            if (i >= 0) d.Label = SharedList[i].Label; else d.Shared = null;
        }
        return l.Where(d => d.Shared != null).OrderBy(d => Array.FindIndex(SharedList, s => s.Id == d.Shared))
            .Concat(l.Where(d => d.Shared == null)).ToList();
    }

    // The tweaks a game has, or null (then Why says why not).
    public static TweakSet For(MainWindow w, Game g)
    {
        if (g == null || g.Source != "rom") return null;
        return ForEmu(Ports.EmuOf(w, g), g);
    }

    public static TweakSet ForEmu(Emulator e, Game g = null)
    {
        if (e == null || !e.Present) return null;
        var v = VideoOf(e);
        if (v == null) return null;
        if (e.IsCore)
        {
            var opts = CoreCatalog.Get(e.Exe);
            var set = new TweakSet { Kind = "core", Emu = e, Video = v };
            foreach (var d in v.Settings)
                if (opts.FirstOrDefault(o => o.Key == d.Key) is CoreOption o && o.Values.Count > 1)
                    set.Defs.Add(new TweakDef { Key = d.Key, Label = d.Label ?? o.Desc, Values = o.Values.ToList(), Names = o.Labels?.ToList(), Default = o.Default, Shared = d.Shared, Group = d.Group });
                else if (opts.Count == 0 && d.Values?.Count > 1)
                    set.Defs.Add(FromSetting(d));
            set.Defs = Ordered(set.Defs);
            return set.Defs.Count > 0 ? set : null;
        }
        if ((v.Format == "json" || v.Format == "ini") && v.File != null)
        {
            var set = new TweakSet { Kind = v.Format, Emu = e, Video = v, Defs = Ordered(v.Settings.Select(FromSetting).ToList()) };
            if (g != null && !v.IoneaApplies)
            {
                if (v.PerGame == null) return null;
                set.Serial = GameIdOf(v, e, g);
                if (set.Serial == null) return null;
            }
            // A mod row only for a game that has that mod.
            set.Defs.RemoveAll(d => d.IsMod && (set.Serial == null || ModState(set, d) == null));
            return set;
        }
        return null;
    }

    // The id the emulator names a game's own file by. "switch": the [title id] in the file name (as most
    // dumps are named), else read from the game with the emulator's keys.
    static string GameIdOf(DefVideo v, Emulator e, Game g)
    {
        if (v.GameId != "switch") return GameId.Read(v.GameId, g.Rom)?.Id;
        var m = Regex.Match(Path.GetFileName(g.Rom ?? ""), @"\[(0[0-9A-Fa-f]{15})\]");
        if (m.Success) return m.Groups[1].Value.ToUpperInvariant();
        if (g.Rom == null || !File.Exists(g.Rom)) return null;
        if (NpdmId(g.Rom) is string npdm) return npdm;
        var keys = GameContent.Keys(new GameFacts { Emu = e });
        return keys == null ? null : SwitchRead.Read(g.Rom, keys).FirstOrDefault(t => t.Type == SwitchRead.Game)?.IdText;
    }

    // An unpacked game (The Duskbloods Network Test: its rom is exefs\main): the title id is in
    // main.npdm beside it. META header; the u32 at 0x70 is where ACI0 starts; the program id is the u64 at
    // ACI0 + 0x10. Only read, for the game's own settings file; how the game starts is not touched.
    public static string NpdmId(string rom)
    {
        try
        {
            var npdm = Path.Combine(Path.GetDirectoryName(rom) ?? "", "main.npdm");
            if (!File.Exists(npdm) || new FileInfo(npdm).Length > 1 << 20) return null;
            var b = File.ReadAllBytes(npdm);
            if (b.Length < 0x80 || Encoding.ASCII.GetString(b, 0, 4) != "META") return null;
            int aci = BitConverter.ToInt32(b, 0x70);
            if (aci < 0x80 || aci > b.Length - 0x18 || Encoding.ASCII.GetString(b, aci, 4) != "ACI0") return null;
            ulong id = BitConverter.ToUInt64(b, aci + 0x10);
            return id == 0 ? null : id.ToString("X16");
        }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException) { return null; }
    }

    // It has graphics tweaks, and what they are (for the menu row).
    public static bool Has(Emulator e) => VideoOf(e) != null;
    public static string Summary(Emulator e) => VideoOf(e)?.Summary ?? "graphics";

    public static string Why(MainWindow w, Game g)
    {
        if (g == null) return "";
        if (g.Source == "steam") return T("menu.steam_games_use_their_own_settings");
        if (g.Source != "rom") return T("menu.pc_games_use_their_own_settings");
        return WhyFor(Ports.EmuOf(w, g));
    }

    // Why one program has no settings for a game.
    public static string WhyFor(Emulator e)
    {
        if (e == null) return T("menu.no_emulator_is_chosen_for_it");
        if (!e.Present) return T("menu.is_missing", e.Name);
        var v = VideoOf(e);
        if (v != null && !e.IsCore && v.PerGame != null && !v.IoneaApplies) return v.NoGameId ?? T("menu.its_game_id_wasn_t_found");
        if (v != null && e.IsCore) return T("menu.play_it_once_then_its_settings");
        return $"{e.Name} has no graphics settings to change";
    }

    // Two values are the same: as text (any case) or as numbers ("60" and "60.0").
    public static bool Same(string a, string b)
    {
        if (a == null || b == null) return a == b;
        a = a.Trim().Trim('"'); b = b.Trim().Trim('"');
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        return double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
            && double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out double y) && Math.Abs(x - y) < 1e-6;
    }

    // ---- values ----

    // The value every game of this emulator uses (the core-wide file, or the emulator's own file).
    public static string BaseValue(TweakSet set, TweakDef d)
    {
        if (d.IsMod) return d.Default;
        if (set.Kind == "core")
            return CoreFile(set.Emu).TryGetValue(d.Key, out var v) && d.Values.Contains(v) ? v : d.Default;
        if (set.Kind == "ini")
        {
            // While a game's own values are written in, the every-game value is the one kept aside.
            string file = BaseFile(set, d);
            if (Applied(set.Emu)?.Items.FirstOrDefault(i => i.Key == d.Key && SameFile(i.File, file)) is AppliedItem a)
                return d.Match(a.Orig) ?? d.Default;
            var (sec, key) = SplitKey(d.Key);
            return d.Match(ReadIni(file, set).Get(sec, key)) ?? d.Default;
        }
        var root = ReadJson(JsonFile(set));
        return d.Match(ReadValue(root, d.Key)) ?? d.Default;
    }

    // This game's own value, or null (uses the emulator's).
    public static string GameValue(Game g, TweakSet set, TweakDef d)
    {
        if (d.IsMod) return ModState(set, d);
        if (set.InLibrary)
            return g.Tweaks != null && g.Tweaks.TryGetValue(d.Key, out var gv) ? d.Match(gv) : null;
        // The emulator's file is the truth (it may have been made in the emulator itself).
        if (set.Kind == "ini")
        {
            var ini = ReadIni(GameFile(set), set);
            var (sec, key) = GameKey(set, d.Key);
            if (set.Video.Style == "qt" && ini.Get(sec, key + "\\use_global") != "false") return null;
            return d.Match(ini.Get(sec, key));
        }
        var root = ReadJson(GameFile(set));
        return d.Match(ReadValue(root, d.Key));
    }

    // Saves a game's tweaks (null value = use the emulator's). Returns false when it couldn't be written.
    public static bool SaveGame(MainWindow w, Game g, TweakSet set, Dictionary<string, string> values)
    {
        try
        {
            if (set.Kind == "json")
            {
                string file = GameFile(set);
                var root = ReadJson(file) ?? new JsonObject();
                foreach (var d in set.Defs.Where(d => !d.IsMod)) SetJson(root, d, values.GetValueOrDefault(d.Key));
                foreach (var sec in root.Select(kv => kv.Key).ToList())
                    if (root[sec] is JsonObject o && o.Count == 0) root.Remove(sec);
                if (root.Count == 0) { if (File.Exists(file)) File.Delete(file); }
                else WriteJson(file, root);
                Log.Write($"tweaks: \"{g.Name}\" -> {file} ({(root.Count == 0 ? "removed" : root.ToJsonString())})");
            }
            else if (set.Kind == "ini" && !set.InLibrary) SaveGameIni(g, set, values);
            foreach (var d in set.Defs.Where(d => d.IsMod)) SetMod(g, set, d, values.GetValueOrDefault(d.Key) ?? d.Default);
            var mine = values.Where(kv => kv.Value != null && !set.Defs.Any(d => d.IsMod && d.Key == kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
            if (set.InLibrary)
            {
                // Keep what this game had for settings that aren't tweaks here.
                if (g.Tweaks != null) foreach (var kv in g.Tweaks) if (!set.Defs.Any(d => d.Key == kv.Key)) mine[kv.Key] = kv.Value;
                g.Tweaks = mine.Count > 0 ? mine : null;
                Log.Write($"tweaks: \"{g.Name}\": {(mine.Count == 0 ? "none" : string.Join(", ", mine.Select(kv => kv.Key + "=" + kv.Value)))}");
            }
            else if (g.Tweaks != null)
            {
                // The emulator keeps them in its own file; another program's (a native port's) stay.
                foreach (var d in set.Defs) g.Tweaks.Remove(d.Key);
                if (g.Tweaks.Count == 0) g.Tweaks = null;
            }
            w.Lib.Save();
            return true;
        }
        catch (Exception e) { Log.Write($"tweaks: \"{g.Name}\": {e.Message}"); return false; }
    }

    // Saves the values every game of this emulator uses.
    public static bool SaveBase(TweakSet set, Dictionary<string, string> values)
    {
        try
        {
            if (set.Kind == "core")
            {
                string file = CoreFilePath(set.Emu);
                var d = CoreFile(set.Emu);
                foreach (var kv in values) d[kv.Key] = kv.Value;
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                Json.Save(file, d);
                Log.Write($"tweaks: {set.Emu.Name} for every game: {string.Join(", ", values.Select(kv => kv.Key + "=" + kv.Value))}");
                return true;
            }
            if (set.Kind == "ini")
            {
                foreach (var file in set.Defs.Where(d => values.ContainsKey(d.Key)).GroupBy(d => BaseFile(set, d), StringComparer.OrdinalIgnoreCase))
                {
                    var ini = ReadIni(file.Key, set);
                    foreach (var d in file)
                    {
                        var (sec, key) = SplitKey(d.Key);
                        string v = values[d.Key];
                        if (set.Video.Style == "qt") ini.Set(sec, key + "\\default", Same(v, d.Default) ? "true" : "false");
                        ini.Set(sec, key, v);
                    }
                    WriteText(file.Key, ini.Text);
                    Log.Write($"tweaks: {set.Emu.Name} for every game -> {file.Key}: {string.Join(", ", file.Select(d => d.Key + "=" + values[d.Key]))}");
                }
                return true;
            }
            string cfg = JsonFile(set);
            var root = ReadJson(cfg) ?? new JsonObject();
            foreach (var d in set.Defs) if (values.TryGetValue(d.Key, out var v)) SetJson(root, d, v);
            WriteJson(cfg, root);
            Log.Write($"tweaks: {set.Emu.Name} for every game -> {cfg}: {string.Join(", ", values.Select(kv => kv.Key + "=" + kv.Value))}");
            return true;
        }
        catch (Exception e) { Log.Write($"tweaks: {set.Emu.Name}: {e.Message}"); return false; }
    }

    // A game's own ini (yuzu family, Azahar: key\use_global=false, key\default, key; Dolphin: the key in its
    // per-game section). A setting back to the emulator's value: use_global=true, or the key taken out.
    static void SaveGameIni(Game g, TweakSet set, Dictionary<string, string> values)
    {
        string file = GameFile(set);
        bool had = File.Exists(file);
        var defs = set.Defs.Where(d => !d.IsMod).ToList();
        if (!had && defs.All(d => values.GetValueOrDefault(d.Key) == null)) return;
        var ini = ReadIni(file, set);
        bool qt = set.Video.Style == "qt";
        foreach (var d in defs)
        {
            var (sec, key) = GameKey(set, d.Key);
            string v = values.GetValueOrDefault(d.Key);
            if (v == null)
            {
                ini.Remove(sec, key);
                if (qt) { ini.Remove(sec, key + "\\default"); if (had) ini.Set(sec, key + "\\use_global", "true"); }
                continue;
            }
            if (qt) { ini.Set(sec, key + "\\use_global", "false"); ini.Set(sec, key + "\\default", Same(v, d.Default) ? "true" : "false"); }
            ini.Set(sec, key, v);
        }
        WriteText(file, ini.Text);
        Log.Write($"tweaks: \"{g.Name}\" -> {file} ({string.Join(", ", defs.Select(d => d.Key + "=" + (values.GetValueOrDefault(d.Key) ?? "default")))})");
    }

    // ---- a game's mod (Duskbloods' 60 fps patch): "mod:user\load\{gameid}\<name>" ----
    // On: its exefs / romfs / cheats folders as the emulator reads them. Off: renamed <folder>.off, so the
    // emulator skips them; its own settings file is not touched (Lichii writes that when it closes).
    static readonly string[] ModParts = { "exefs", "romfs", "cheats" };
    static string ModDir(TweakSet set, TweakDef d) => Path.Combine(Dir(set.Emu), d.Key[4..].Replace("{gameid}", set.Serial ?? ""));

    // "on", "off", or null (the game has no such mod).
    public static string ModState(TweakSet set, TweakDef d)
    {
        string dir = ModDir(set, d);
        if (!Directory.Exists(dir)) return null;
        if (ModParts.Any(p => Directory.Exists(Path.Combine(dir, p)))) return "on";
        if (ModParts.Any(p => Directory.Exists(Path.Combine(dir, p + ".off")))) return "off";
        return null;
    }

    static void SetMod(Game g, TweakSet set, TweakDef d, string value)
    {
        string now = ModState(set, d);
        if (now == null || now == value) return;
        string dir = ModDir(set, d);
        foreach (var p in ModParts)
        {
            string on = Path.Combine(dir, p), off = on + ".off";
            if (value == "on" && Directory.Exists(off) && !Directory.Exists(on)) Directory.Move(off, on);
            if (value == "off" && Directory.Exists(on) && !Directory.Exists(off)) Directory.Move(on, off);
        }
        Log.Write($"tweaks: \"{g.Name}\" mod {Path.GetFileName(dir)} -> {value}");
    }

    // ---- cores: core-options\<core>.json (the same file the core's Settings card writes) ----
    public static string DataDir;   // set by MainWindow
    static string CoreFilePath(Emulator e) => Path.Combine(DataDir, "core-options", Path.GetFileNameWithoutExtension(e.Exe) + ".json");
    static Dictionary<string, string> CoreFile(Emulator e) => Json.Load<Dictionary<string, string>>(CoreFilePath(e));

    // ---- the emulator's own files (video.file for every game, video.perGame with {gameid}) ----
    static string Dir(Emulator e) => Path.GetDirectoryName(e.Exe);
    static string JsonFile(TweakSet set) => Path.Combine(Dir(set.Emu), set.Video.File);
    static string BaseFile(TweakSet set, TweakDef d) => Path.Combine(Dir(set.Emu), d.File ?? set.Video.File);
    static string GameFile(TweakSet set) => Path.Combine(Dir(set.Emu), set.Video.PerGame.Replace("{gameid}", set.Serial));
    static bool SameFile(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    // "[Renderer]backend" -> (Renderer, backend); "TargetFPS" -> ("", TargetFPS), the top of the file.
    public static (string sec, string key) SplitKey(string k)
    {
        int i = k.StartsWith("[") ? k.IndexOf(']') : -1;
        return i > 0 ? (k[1..i], k[(i + 1)..]) : ("", k);
    }

    // The same key in a game's own file, whose sections may be named differently (Dolphin's Video_Settings).
    static (string sec, string key) GameKey(TweakSet set, string k)
    {
        var (sec, key) = SplitKey(k);
        return (set.Video.GameSections != null && set.Video.GameSections.TryGetValue(sec, out var gs) ? gs : sec, key);
    }

    static IniText ReadIni(string file, TweakSet set) => ReadIni(file, set.Video.Sep);
    static IniText ReadIni(string file, string sep)
    {
        try { return new IniText(File.Exists(file) ? File.ReadAllText(file) : "", sep ?? "="); }
        catch (Exception e) { Log.Write($"tweaks: could not read {file}: {e.Message}"); return new IniText("", sep ?? "="); }
    }

    // Through a .tmp file; the emulator's own file is kept once as <file>.before-launcher (as EmuControls does).
    static void WriteText(string file, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file));
        string keep = file + ".before-launcher";
        if (File.Exists(file) && !File.Exists(keep)) File.Copy(file, keep);
        string tmp = file + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, file, true);
    }

    static JsonObject ReadJson(string file)
    {
        try { return File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file)) as JsonObject : null; }
        catch (Exception e) { Log.Write($"tweaks: could not read {file}: {e.Message}"); return null; }
    }

    static void WriteJson(string file, JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file));
        string tmp = file + ".tmp";
        File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, file, true);
    }

    static string ReadValue(JsonObject root, string key)
    {
        var p = key.Split('.', 2);
        if (root?[p[0]] is not JsonObject sec || sec[p[1]] is not System.Text.Json.Nodes.JsonValue v) return null;
        return v.GetValueKind() switch
        {
            JsonValueKind.True => "true", JsonValueKind.False => "false",
            JsonValueKind.Number => v.ToJsonString(), JsonValueKind.String => v.GetValue<string>(), _ => null,
        };
    }

    static void SetJson(JsonObject root, TweakDef d, string value)
    {
        var p = d.Key.Split('.', 2);
        if (value == null) { if (root[p[0]] is JsonObject s0) s0.Remove(p[1]); return; }
        if (root[p[0]] is not JsonObject sec) { sec = new JsonObject(); root[p[0]] = sec; }
        sec[p[1]] = d.Type switch
        {
            "bool" => System.Text.Json.Nodes.JsonValue.Create(value == "true"),
            "int" => System.Text.Json.Nodes.JsonValue.Create(int.Parse(value)),
            _ => System.Text.Json.Nodes.JsonValue.Create(value),
        };
    }

    // ---- per game by Ionea (ioneaApplies): written at the game's start, taken back at its end ----
    public sealed class AppliedItem
    {
        public string File { get; set; }
        public string Key { get; set; }
        public string Orig { get; set; }     // what the file had (null: no such line)
        public string Wrote { get; set; }    // the game's value Ionea wrote
    }
    public sealed class AppliedRecord
    {
        public string Token { get; set; }    // this start's (a restart's end must not undo the next start)
        public string Game { get; set; }
        public string Exe { get; set; }
        public string Sep { get; set; }
        public List<AppliedItem> Items { get; set; } = new();
    }

    static string AppliedPath(Emulator e) => DataDir == null ? null : Path.Combine(DataDir, "video-applied", e.Id + ".json");
    static AppliedRecord Applied(Emulator e) => AppliedPath(e) is string p && File.Exists(p) ? Json.Load<AppliedRecord>(p) : null;

    // At a game's start: writes its own values into the emulator's file. Returns this start's token (null: nothing written).
    public static string ApplyGame(Emulator e, Game g)
    {
        try
        {
            var set = ForEmu(e, g);
            if (set == null || set.Kind != "ini" || !set.InLibrary || g == null) return null;
            Restore(e, null);   // left from a start that never ended here
            var mine = set.Defs.Select(d => (d, v: GameValue(g, set, d))).Where(x => x.v != null).ToList();
            if (mine.Count == 0) return null;
            var rec = new AppliedRecord { Token = Guid.NewGuid().ToString("N"), Game = g.Name, Exe = e.Exe, Sep = set.Video.Sep };
            var files = mine.GroupBy(x => BaseFile(set, x.d), StringComparer.OrdinalIgnoreCase).Select(f => (file: f.Key, ini: ReadIni(f.Key, set), items: f.ToList())).ToList();
            foreach (var (file, ini, items) in files)
                foreach (var (d, v) in items)
                {
                    var (sec, key) = SplitKey(d.Key);
                    rec.Items.Add(new AppliedItem { File = file, Key = d.Key, Orig = ini.Get(sec, key), Wrote = v });
                    ini.Set(sec, key, v);
                }
            // The record first: a crash halfway still finds what to put back.
            string p = AppliedPath(e);
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            Json.Save(p, rec);
            foreach (var (file, ini, _) in files) WriteText(file, ini.Text);
            Log.Write($"tweaks: \"{g.Name}\" applied in {e.Name}: {string.Join(", ", rec.Items.Select(i => i.Key + "=" + i.Wrote))}");
            return rec.Token;
        }
        catch (Exception ex) { Log.Write($"tweaks: \"{g?.Name}\" not applied: {ex.Message}"); return null; }
    }

    // At its end (or a failed start): the every-game values go back, where the file still holds what Ionea
    // wrote (a value changed in the emulator meanwhile is kept). token null: whatever is there.
    public static void Restore(Emulator e, string token)
    {
        try
        {
            var rec = Applied(e);
            if (rec == null || (token != null && rec.Token != token)) return;
            var kept = new List<string>();
            foreach (var f in rec.Items.GroupBy(i => i.File, StringComparer.OrdinalIgnoreCase))
            {
                var ini = ReadIni(f.Key, rec.Sep);
                foreach (var i in f)
                {
                    var (sec, key) = SplitKey(i.Key);
                    if (!Same(ini.Get(sec, key), i.Wrote)) { kept.Add(i.Key); continue; }
                    if (i.Orig == null) { ini.Remove(sec, key); ini.RemoveIfEmpty(sec); }   // a section only Ionea's value was in goes again
                    else ini.Set(sec, key, i.Orig);
                }
                if (File.Exists(f.Key)) WriteText(f.Key, ini.Text);
            }
            File.Delete(AppliedPath(e));
            Log.Write($"tweaks: {e.Name}'s every-game values back after \"{rec.Game}\"{(kept.Count > 0 ? " (changed in the emulator, kept: " + string.Join(", ", kept) + ")" : "")}");
        }
        catch (Exception ex) { Log.Write($"tweaks: {e.Name}: values not put back: {ex.Message}"); }
    }

    // At the launcher's start: put back what a start left (the launcher closed while the game ran), unless
    // that emulator is still running.
    public static void RestoreLeft(IEnumerable<Emulator> emus)
    {
        if (DataDir == null || !Directory.Exists(Path.Combine(DataDir, "video-applied"))) return;
        foreach (var e in emus)
        {
            if (Applied(e) is not AppliedRecord rec) continue;
            var ps = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(rec.Exe ?? e.Exe ?? ""));
            bool running = ps.Length > 0;
            foreach (var p in ps) p.Dispose();
            if (running) { Log.Write($"tweaks: {e.Name} still runs, \"{rec.Game}\"'s values stay for now"); continue; }
            Restore(e, null);
        }
    }
}

// Game settings: a game's settings in each program that can play it. With its emulator and a
// native port both installed, a choice of the two comes first ("Bloodborne – shadPS4 settings", "Bloodborne –
// Native bbport settings"); a program with nothing to change is shown faded.
public static class GameSettings
{
    public sealed record Option(string Label, Emulator Emu, TweakSet Set, string Why);

    public static List<Option> Options(MainWindow w, Game g)
    {
        var l = new List<Option>();
        if (g == null || g.Source != "rom") return l;
        if (!Ports.Both(w, g))
        {
            var ts = Tweaks.For(w, g);
            l.Add(new Option(null, Ports.EmuOf(w, g), ts, ts == null ? Tweaks.Why(w, g) : null));
            return l;
        }
        var emu = w.Emus.Get(g.Emulator);
        var es = Tweaks.ForEmu(emu, g);
        l.Add(new Option($"{g.Name} – {emu.Name} settings", emu, es, es == null ? Tweaks.WhyFor(emu) : null));
        foreach (var m in Ports.For(w, g))
        {
            var ps = Tweaks.ForEmu(m.Emu, g);
            l.Add(new Option($"{g.Name} – Native {Ports.ShortName(m)} settings", m.Emu, ps, ps == null ? Tweaks.WhyFor(m.Emu) : null));
        }
        return l;
    }

    public static bool Any(MainWindow w, Game g) => Options(w, g).Any(o => o.Set != null);
    public static string Why(MainWindow w, Game g) => Options(w, g).FirstOrDefault()?.Why ?? Tweaks.Why(w, g);
    public static string Summary(MainWindow w, Game g)
    {
        var l = Options(w, g).Where(o => o.Set != null).ToList();
        return l.Count == 1 ? Tweaks.Summary(l[0].Emu) : string.Join(", ", l.Select(o => o.Emu.Name));
    }

    public static void Open(MainWindow w, Game g, bool fromGameMenu = false)
    {
        var l = Options(w, g);
        if (l.Count == 1) { if (l[0].Set != null) new TweaksCard(w, g, l[0].Set, fromGameMenu).Open(); return; }
        if (l.Count > 1) new GameSettingsPickCard(w, g, l).Open();
    }
}

// Whose settings: the emulator's or the native port's (both installed).
public sealed class GameSettingsPickCard : MenuCard
{
    public GameSettingsPickCard(MainWindow w, Game g, List<GameSettings.Option> l) : base(w, T("settings.game_settings"), 1060)
    {
        foreach (var o in l)
        {
            var r = AddRow(o.Label, () => o.Set == null ? T("menu.nothing_to_change") : "", activate: () =>
            {
                if (o.Set == null) { Sounds.Play("edge"); return; }
                Close(false);
                new TweaksCard(w, g, o.Set).Open();
            });
            r.Dim = () => o.Set == null;
            WithIcon(r, EmuDefs.Of(o.Emu)?.IsPort == true ? "play" : "chip");
        }
        Focus = Math.Max(0, l.FindIndex(o => o.Set != null));
    }
}

// The settings of one game (each row can also be "default": the same as every game) or of every game of an
// emulator, in boxes (TweakSet.Boxes). Saved when the card closes.
public sealed class TweaksCard : MenuCard
{
    readonly Game g;
    readonly TweakSet set;
    readonly Dictionary<string, string> start, now;
    readonly Dictionary<string, string> baseVals = new();
    readonly bool fromGameMenu;
    public static string LastResult = "";

    public TweaksCard(MainWindow w, Game game, TweakSet tweaks, bool fromGameMenu = false)
        : base(w, game != null ? T("settings.game_settings") : tweaks.Emu.Name + " settings", 1180)
    {
        g = game; set = tweaks; this.fromGameMenu = fromGameMenu;
        Note(game != null ? $"{game.Name}  ·  {ProgramName()}" : T("menu.every_game"), 26, new Thickness(4, -14, 0, 20));
        start = new(); now = new();
        foreach (var d in set.Defs)
        {
            baseVals[d.Key] = Tweaks.BaseValue(set, d);
            start[d.Key] = g != null ? Tweaks.GameValue(g, set, d) : baseVals[d.Key];
            now[d.Key] = start[d.Key];
        }
        foreach (var (title, defs) in set.Boxes())
        {
            BeginBox(title);
            foreach (var d in defs) AddSettingRow(d);
            EndBox();
        }
        SaveRow(() => set.Defs.Any(d => now[d.Key] != start[d.Key]), () => Close());
    }

    string ProgramName() => EmuDefs.Of(set.Emu)?.IsPort == true ? T("menu.native") + set.Emu.Name : set.Emu.Name;

    // A row that cycles a setting's values (per game the list starts with "default", null).
    // Three or more values: A or a click opens the list of them.
    void AddSettingRow(TweakDef d)
    {
        List<string> Values()
        {
            var list = new List<string>();
            if (g != null && !d.IsMod) list.Add(null);   // a mod is on or off, there is no every-game value
            list.AddRange(d.Values);
            return list;
        }
        AddRowFor(d.Label, () => Show(d), dir =>
        {
            var list = Values();
            int i = list.IndexOf(now[d.Key]);
            now[d.Key] = list[((i < 0 ? 0 : i) + dir + list.Count) % list.Count];
        }, () => Values().Select(v => Show(d, v)).ToList(), () => Values().IndexOf(now[d.Key]), i => now[d.Key] = Values()[i]);
    }

    // Test scripts.
    public void SetForTest(string key, string value) { now[key] = value == "default" ? null : value; Refresh(); }
    public string Keys => string.Join(",", set.Defs.Select(d => d.Key));
    public string BoxTitles => string.Join(",", set.Boxes().Select(b => b.Title));

    string Show(TweakDef d) => Show(d, now[d.Key]);
    string Show(TweakDef d, string v) => v == null ? T("settings.default_filter", d.Show(baseVals[d.Key])) : d.Show(v);

    public override void Close(bool sound = true)
    {
        var changed = set.Defs.Where(d => now[d.Key] != start[d.Key]).ToList();
        base.Close(sound);
        bool running = g != null && w.GameMenu.Game?.Id == g.Id;
        if (changed.Count == 0)
        {
            LastResult = "unchanged";
            if (fromGameMenu && w.GameMenu.Game != null) w.GameMenu.ShowCard();
            return;
        }
        if (g == null && set.Video?.SavesOnExit == true && w.GameMenu.Game is Game pg && Ports.EmuOf(w, pg)?.Id == set.Emu.Id)
        {
            // It writes its own settings file when it closes and would undo these.
            LastResult = "refused";
            new MessageCard(w, T("menu.is_running", set.Emu.Name), T("menu.close_it_first_then_change_its")).Open();
            return;
        }
        bool ok = g != null ? Tweaks.SaveGame(w, g, set, now) : Tweaks.SaveBase(set, changed.Where(d => now[d.Key] != null).ToDictionary(d => d.Key, d => now[d.Key]));
        LastResult = ok ? "saved" : "failed";
        if (!ok) { new MessageCard(w, T("menu.couldn_t_save_the_settings"), T("menu.see_launcher_log_for_why")).Open(); return; }
        // A restart only for the program these are for (the game running in its other program isn't affected).
        if (running && Ports.EmuOf(w, g)?.Id == set.Emu.Id) { new RestartAskCard(w, fromGameMenu).Open(); return; }
        bool emuRunning = g == null && w.GameMenu.Game is Game rg && Ports.EmuOf(w, rg)?.Id == set.Emu.Id;
        w.Toasts.Show(T("menu.settings_saved"), emuRunning ? T("menu.restart_the_game_to_use_them") : g != null ? T("menu.they_apply_the_next_time_starts", g.Name) : T("menu.every_game_uses_them_from_its", set.Emu.Name), "info");
        if (fromGameMenu && w.GameMenu.Game != null) w.GameMenu.ShowCard();
    }
}

// After changing a running game's tweaks: they need the game to start again.
public sealed class RestartAskCard : MenuCard
{
    bool restarting;
    readonly bool backToMenu;

    public RestartAskCard(MainWindow w, bool backToGameMenu) : base(w, T("menu.restart_to_apply"), 860)
    {
        backToMenu = backToGameMenu;
        Note(T("menu.progress_since_your_last_save_is"), 28, new Thickness(4, 0, 0, 24));
        AddRow(T("menu.restart_now"), activate: () => { restarting = true; Close(false); w.GameMenu.Restart(); });
        AddRow(T("menu.apply_on_next_launch"), activate: () => Close());
    }

    public override void Close(bool sound = true)
    {
        base.Close(sound);
        if (!restarting) w.Toasts.Show(T("menu.settings_saved"), T("menu.they_apply_the_next_time_the"), "info");
        if (!restarting && backToMenu && w.GameMenu.Game != null) w.GameMenu.ShowCard();
    }
}
