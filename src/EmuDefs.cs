#nullable disable
// Emulator definitions: what the launcher knows about each emulator and system lives in JSON
// files, not in code. Shipped ones are in <exe folder>\definitions (systems.json, emulators\*.json,
// cores\*.json); the user's own in <data>\definitions\ (any *.json, any folder) win over shipped ones with
// the same id (merged: objects field by field, lists replaced, null removes).
// A definition may name a "family": it starts as a copy of that definition and changes only what it lists.
// The code here only reads and writes generic formats; nothing is special-cased per emulator.
//
// Support levels: Generic (no definition: start, inside Ionea, Esc menu, pause, play
// time), Known (+ found automatically, portable mode, saves per profile), Managed (+ controls and video from
// Ionea: a "controls" or "video" mapping), Core (libretro core: everything). An emulator whose own version is
// outside its definition's "tested" range drops one level: Ionea then writes nothing into its config (controls,
// video, first-run seed files), but it is still found, made portable and keeps saves per profile (Known, Tested).
// Unknown version: no check.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Ionea;

public enum SupportLevel { Generic = 0, Known = 1, Managed = 2, Core = 3 }

public sealed class EmuSystem
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string[] Exts { get; set; } = Array.Empty<string>();
    public string Reader { get; set; }              // GameId reader for its game files (id, version)
    public string[] Esde { get; set; }              // ES-DE system names that are this system
    public bool More { get; set; }                  // from ES-DE: not in the short lists
    public string Thumbs { get; set; }              // its name at thumbnails.libretro.com and in libretro-database (covers, Covers.cs)
    public string Dat { get; set; }                 // libretro-database's metadat folder for it: "no-intro" or "redump"
    public bool MouseCamera { get; set; }           // its 3D games use the right stick as the camera: the mouse can move it (InputMap.MouseCamSystems)
}

public sealed class DefVersion { public string Min { get; set; } public string Max { get; set; } }   // Max may end in * ("1.1.*")
public sealed class DefSeed { public string File { get; set; } public string Text { get; set; } }
public sealed class DefDetect
{
    public List<string> Programs { get; set; } = new();   // exact file names, or a regex when it starts with ^
    public string Archive { get; set; }                   // a download named with this is most likely it
    public List<string> Product { get; set; }             // the program's own product name is one of these (the scan skips a same-named other program)
}
public sealed class DefStart
{
    public string Args { get; set; }                      // {rom} is the game file
    public List<string> Portable { get; set; }            // created next to the program: a file, or a folder (ends in \)
    public List<DefSeed> Seed { get; set; }               // written once if missing (e.g. its own updater off)
}
public sealed class DefGameFolders                        // where the emulator keeps its own list of game folders (Scan.cs)
{
    public string File { get; set; }                      // {exe} (its folder), {appdata}, {localappdata}, {documents}, {home}
    public string Match { get; set; }                     // a regex; group 1 is a folder (every match counts)
}
public sealed class DefRelease                            // its official download (EmuInstall.cs)
{
    public string Github { get; set; }                    // "owner/repo": the latest release's assets
    public string Asset { get; set; }                     // a regex picking the Windows asset by its file name
    public string Url { get; set; }                       // or one fixed address
}
public sealed class DefSaves
{
    public List<string> Folders { get; set; }             // its save folders, kept per profile
    public bool Beside { get; set; }                      // saves next to the game file, swapped per profile
}
public sealed class DefFile                               // a file the user must supply (firmware, keys, BIOS): RequiredFiles.cs
{
    public string Name { get; set; }
    public string Path { get; set; }                      // relative to the program (a core: its system folder); ends in \ = a folder
    public string Md5 { get; set; }                       // the one right copy
    public List<string> Md5s { get; set; }                // a folder: any one file with one of these hashes
    public string Check { get; set; }                     // no public hash list: checked by structure (ReqFiles.Check)
    public string Share { get; set; }                     // store id; the same id is the same file for every emulator
    public string Install { get; set; }                   // the emulator installs it itself: its start options, {file} is the picked file
    public string Ready { get; set; }                     // with Install: the file that exists once it is installed
    public string Dat { get; set; }                       // a system name in the user's own System.dat (SysDat): its files count
    public bool Optional { get; set; }
    public string Desc { get; set; }
}
public sealed class DefSetting
{
    public string Key { get; set; }
    public string Label { get; set; }
    public string Type { get; set; }
    public List<string> Values { get; set; }
    public List<string> Names { get; set; }
    public string Default { get; set; }
    public string Shared { get; set; }                    // its place in the shared list (Tweaks.SharedList); null: its own name
    public string Group { get; set; }                     // the Game settings box it goes in (SettingGroups: a standard name, or its own); null: by Shared, key and name
    public string File { get; set; }                      // ini: another file for every game (Dolphin's Dolphin.ini)
}
public sealed class DefVideo
{
    public string Format { get; set; }                    // "core" (core options), "json" or "ini" (keys "[Section]key"; no section: top of the file)
    public string File { get; set; }                      // json/ini: every game, relative to the program
    public string PerGame { get; set; }                   // json/ini: one game, with {gameid}
    public string GameId { get; set; }                    // reader for {gameid} (GameId.cs, or "switch": [title id] in the name, else read with the keys)
    public string NoGameId { get; set; }                  // why a game has none, when its id isn't found
    public bool SavesOnExit { get; set; }                 // it writes its file when it closes: no changes while it runs
    public string Style { get; set; }                     // ini: "qt" (key\default lines; per game key\use_global), else plain
    public string Sep { get; set; }                       // ini: between key and value ("=" or " = ")
    public Dictionary<string, string> GameSections { get; set; }   // ini per game: a section's name there (Dolphin: Settings -> Video_Settings)
    public bool IoneaApplies { get; set; }                // per game: Ionea writes the game's values at its start, puts the others back at its end
    public string Summary { get; set; }
    public List<DefSetting> Settings { get; set; } = new();
}
public sealed class DefControls
{
    public string Format { get; set; }                    // a writer in EmuControls: ps4, yuzu, citra, melonds, mgba, dolphin
    public string File { get; set; }                      // relative to the program
    public List<string> Systems { get; set; }             // only these systems' games (null: all)
    public bool LiveReload { get; set; }                  // reads the file again on hotkeys.reloadInputs
}
public sealed class DefHotkeys
{
    public string ReloadInputs { get; set; }
    public string MouseLook { get; set; }
    public string MousePanning { get; set; }
    public string PortMenu { get; set; }                  // a native port: the key that opens its own menu
    public string Pause { get; set; }                     // its own pause / continue toggle: posted when the Home menu opens and closes
}

// A native PC port of a console game ("kind": "port"). It is found and
// installed like an emulator but has no systems of its own, so it never plays a game as an emulator: a game
// plays natively when its id is listed here. Every path, key and version here is from public notes: verify.
public sealed class DefPort
{
    public string Kind { get; set; }                      // "recomp" | "decomp" (shown only)
    public string Repo { get; set; }                      // where it is released, e.g. "github.com/owner/repo"
    public List<DefPortGame> Games { get; set; } = new();
    public DefPrepare Prepare { get; set; }
    public List<string> GameDirEnv { get; set; }          // environment variables that get the prepared game folder
    public string GameProgram { get; set; }               // the program whose window is the game, when the started one runs it (bb-probe.exe)
    public string GameProgramEnv { get; set; }            // environment variable the starter reads that program's path from (BB_PROBE):
                                                          // Ionea points it at a link named like itself, so it shows in Ionea's Task Manager group
    public List<string> TurnOff { get; set; }             // its own online parts and updater: kept off (shown on its page)
    public DefSharedSaves SharedSaves { get; set; }       // its saves are the same files as an emulator's: one save for both (Ports.SyncSaves)
    public DefPortAddons Addons { get; set; }             // where it reads the game's add-ons (DLC): linked from the emulator's (Ports.SyncAddons)
}
// A port that reads the game's add-ons (DLC) from a folder of its own (bbport: user\addcont\<id>\<label>, as
// shadPS4). At every native start each add-on folder of the game's emulator (its definition's gameData "addons", e.g.
// shadPS4's user\addcont\{gameid}\*) gets a junction there with the same name. Nothing is copied, moved or changed.
public sealed class DefPortAddons
{
    public string To { get; set; }                        // relative to the port's program, with {gameid}; inside a per-profile save
                                                          // folder it is the active profile's copy (as sharedSaves)
}
// A port and an emulator that write the same save files (Bloodborne: bbport and shadPS4). By default the game
// keeps ONE save: each save folder of the game in the port's place is a link to the emulator's (per profile), so either
// one continues it. Game info > Saves: Shared / Separate. Paths are relative to each program, with {gameid}.
public sealed class DefSharedSaves
{
    public List<string> Emulators { get; set; } = new();  // emulator definition ids with the same save files
    public string Port { get; set; }                      // the port's folder holding the game's save folders
    public string Emulator { get; set; }                  // the emulator's
    public List<string> Skip { get; set; }                // names in the port's folder that are its own, never linked ("*.sce_sys")
    public string Default { get; set; }                   // "shared" (when missing) or "separate"
}
public sealed class DefPortGame
{
    public string System { get; set; }                    // catalog system id, e.g. "ps4"
    public List<string> Ids { get; set; } = new();        // game ids it plays (GameId readers)
    public List<string> Versions { get; set; }           // accepted versions after the update (null: any)
    public string Name { get; set; }                      // shown in reasons, e.g. "Bloodborne"
}
public sealed class DefPrepare
{
    public string Kind { get; set; }                      // "ps4-merge": base game with its update laid over it (hard links)
    public string Proof { get; set; }                     // a file inside the prepared folder that shows it is done
}

// Where the performance overlay can read the frame rate exactly (FrameSource.cs). Without it, an emulator or port
// inside Ionea has its window's frames counted.
public sealed class DefPerf
{
    public string FrameLog { get; set; }   // a log with a row per frame, beside the program: "logs\*.frames.csv" (the newest since the start)
    public string Time { get; set; }       // its column with each frame's time in seconds
}

// What Ionea keeps off at every start so nothing of the emulator's own shows on top of the game (Quiet.cs).
public sealed class DefQuiet
{
    public Dictionary<string, DefQuietFile> Files { get; set; }   // by file beside the program
    public Dictionary<string, string> Env { get; set; }            // environment for the started program
    public List<string> Args { get; set; }                         // added to its command line when missing
    public Dictionary<string, string> Core { get; set; }           // a libretro core's own options
}
public sealed class DefQuietFile
{
    public string Format { get; set; } = "ini";           // "ini" (also toml) | "json"
    public string Style { get; set; }                     // "qt": each key with key\default=false
    public string Sep { get; set; }                       // "=" (default) or " = "
    public Dictionary<string, string> Set { get; set; }   // "[Section]key" (ini) or "Section.key" (json) -> value
}

public sealed class EmuDef
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string Family { get; set; }
    public DefVersion Tested { get; set; }
    public DefDetect Detect { get; set; } = new();
    public DefStart Start { get; set; } = new();
    public List<string> Systems { get; set; } = new();
    public List<string> ExtraExts { get; set; }
    public DefSaves Saves { get; set; }
    public List<DefFile> RequiredFiles { get; set; }
    public JsonObject GameData { get; set; }              // later steps (game info)
    public JsonArray Errors { get; set; }                 // later steps (launch failure card)
    public DefVideo Video { get; set; }
    public DefControls Controls { get; set; }
    public DefHotkeys Hotkeys { get; set; }
    public List<DefGameFolders> GameFolders { get; set; }
    public DefRelease Release { get; set; }
    public string Source { get; set; }                    // where it came from (attribution), e.g. "Playnite"
    public string Kind { get; set; }                      // null: an emulator or core; "port": a native port (DefPort)
    public DefPort Port { get; set; }
    public DefQuiet Quiet { get; set; }                   // its own overlays, messages and popups kept off (Quiet.cs)
    public DefPerf Perf { get; set; }                     // where the performance overlay reads its frame rate (FrameSource.cs)

    [JsonIgnore] public string File;                      // the file it was read from (the user's, when overridden)
    [JsonIgnore] public bool IsCore => Programs.Any(p => p.EndsWith("_libretro.dll", StringComparison.OrdinalIgnoreCase));
    [JsonIgnore] public bool IsPort => Kind == "port";
    [JsonIgnore] public List<string> Programs => Detect?.Programs ?? new();
    [JsonIgnore] public string Args => Start?.Args ?? "";

    readonly ConcurrentDictionary<string, Regex> rx = new();
    // Is this file name one of its programs?
    public bool Matches(string fileName)
    {
        foreach (string p in Programs)
        {
            if (p.StartsWith("^"))
            {
                var r = rx.GetOrAdd(p, s => { try { return new Regex(s, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant); } catch { return null; } });
                if (r != null && r.IsMatch(fileName)) return true;
            }
            else if (p.Equals(fileName, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}

public static class EmuDefs
{
    static readonly JsonSerializerOptions Opt = new()
    {
        PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
    };
    static readonly JsonDocumentOptions DocOpt = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static string ShippedDir = Path.Combine(AppContext.BaseDirectory, "definitions");
    public static string UserDir;                          // <data>\definitions
    public static List<EmuSystem> Systems = new();
    public static List<EmuDef> All = new();
    static bool loaded;

    public static void EnsureLoaded() { if (!loaded) Load(UserDir); }

    public static void Load(string userDir)
    {
        loaded = true;
        UserDir = userDir;
        var sys = new List<JsonObject>();
        var raw = new List<(string id, JsonObject o, string file)>();
        foreach (var (dir, user) in new[] { (ShippedDir, false), (UserDir, true) })
        {
            if (dir == null || !Directory.Exists(dir)) continue;
            string sysFile = Path.Combine(dir, "systems.json");
            if (ReadNode(sysFile) is JsonArray arr)
                foreach (var n in arr.OfType<JsonObject>())
                {
                    string id = Text(n["id"]);
                    int i = sys.FindIndex(s => Text(s["id"]) == id);
                    if (i >= 0) sys[i] = Merge(sys[i], n); else sys.Add(n);
                }
            IEnumerable<string> files = user
                ? Directory.GetFiles(dir, "*.json", SearchOption.AllDirectories).Where(f => !SameFile(f, sysFile))
                : new[] { "cores", "emulators", "ports" }.Select(s => Path.Combine(dir, s)).Where(Directory.Exists)
                    .SelectMany(d => Directory.GetFiles(d, "*.json", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
            foreach (string f in files)
            {
                if (ReadNode(f) is not JsonObject o || Text(o["id"]) is null) { Log.Write($"definitions: skipped {f} (no id)"); continue; }
                string id = Text(o["id"]);
                int i = raw.FindIndex(r => r.id == id);
                if (i >= 0) { raw[i] = (id, Merge(raw[i].o, o), f); if (user) Log.Write($"definitions: {id} changed by {f}"); }
                else raw.Add((id, o, f));
            }
        }
        Systems = sys.Select(n => { try { return n.Deserialize<EmuSystem>(Opt); } catch (Exception e) { Log.Write("definitions: system " + n["id"] + ": " + e.Message); return null; } })
                     .Where(s => s?.Id != null && s.Name != null).ToList();
        foreach (var s in Systems) s.Exts = (s.Exts ?? Array.Empty<string>()).Select(x => x.ToLowerInvariant()).ToArray();

        var byId = raw.ToDictionary(r => r.id, r => r.o);
        var defs = new List<EmuDef>();
        foreach (var (id, _, file) in raw)
        {
            try
            {
                var o = Resolve(id, byId, new HashSet<string>());
                if (o == null) continue;
                var d = o.Deserialize<EmuDef>(Opt);
                if (d == null) { Log.Write($"definitions: {id} ({file}) is empty"); continue; }
                Tidy(d);
                if (string.IsNullOrEmpty(d.Name) || d.Programs.Count == 0) { Log.Write($"definitions: {id} has no name or programs"); continue; }
                d.File = file;
                defs.Add(d);
            }
            catch (Exception e) { Log.Write($"definitions: {id} ({file}): {e.Message}"); }
        }
        // Cores first: an archive or file is checked against them before the standalone emulators. Within each,
        // the user's own files, then the hand-made ones, then the imported ones (a broad imported pattern never
        // takes a program a closer definition knows).
        All = defs.OrderBy(d => d.IsCore ? 0 : 1).ThenBy(Rank).ToList();
        Log.Write($"definitions: {Systems.Count} systems, {All.Count(d => !d.IsCore && !d.IsPort)} emulators, {All.Count(d => d.IsCore)} cores, {All.Count(d => d.IsPort)} ports" +
                  (UserDir != null && Directory.Exists(UserDir) ? $" (own: {UserDir})" : ""));
    }

    // A user file may say null (or list nulls) where a list or group belongs ("systems": null, "detect": null,
    // "start": null, or a merge that removed them): read as empty, so nothing later trips over it.
    static void Tidy(EmuDef d)
    {
        static List<string> Clean(List<string> l) => l?.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        d.Detect ??= new();
        d.Detect.Programs = Clean(d.Detect.Programs) ?? new();
        d.Start ??= new();
        d.Start.Portable = Clean(d.Start.Portable);
        d.Start.Seed = d.Start.Seed?.Where(s => !string.IsNullOrWhiteSpace(s?.File)).ToList();
        d.Systems = Clean(d.Systems) ?? new();
        d.ExtraExts = Clean(d.ExtraExts);
        if (d.Saves != null) d.Saves.Folders = Clean(d.Saves.Folders);
        d.RequiredFiles = d.RequiredFiles?.Where(f => !string.IsNullOrWhiteSpace(f?.Path ?? f?.Ready)).ToList();
        foreach (var f in d.RequiredFiles ?? new())
        {
            f.Path ??= f.Ready;
            f.Name ??= System.IO.Path.GetFileName(f.Path.TrimEnd('\\', '/'));
            f.Md5s = Clean(f.Md5s)?.Select(m => m.ToLowerInvariant()).ToList();
            f.Md5 = string.IsNullOrWhiteSpace(f.Md5) ? null : f.Md5.ToLowerInvariant();
        }
        if (d.Video != null) d.Video.Settings = d.Video.Settings?.Where(s => s?.Key != null).ToList() ?? new();
        if (d.Controls != null) d.Controls.Systems = Clean(d.Controls.Systems);
        if (d.Kind == "port")
        {
            d.Port ??= new();
            d.Systems = new();   // a port never plays a system as an emulator
            d.Port.Games = d.Port.Games?.Where(x => x != null && !string.IsNullOrWhiteSpace(x.System)).ToList() ?? new();
            foreach (var x in d.Port.Games) { x.Ids = Clean(x.Ids) ?? new(); x.Versions = Clean(x.Versions); }
            d.Port.GameDirEnv = Clean(d.Port.GameDirEnv);
            d.Port.TurnOff = Clean(d.Port.TurnOff);
        }
        else d.Port = null;
    }

    static string Text(JsonNode n) => n is JsonValue v && v.TryGetValue(out string s) ? s : null;   // a field that should be text, else null

    // 0: the user's own file (also one that changes a shipped definition), 1: hand-made (no "source"), 2: imported.
    static int Rank(EmuDef d) =>
        UserDir != null && d.File != null && Path.GetFullPath(d.File).StartsWith(Path.GetFullPath(UserDir).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) ? 0
        : string.IsNullOrEmpty(d.Source) ? 1 : 2;

    // May a newly found core switch games over by itself (EmuList.UseCores)? Only a hand-made or the user's own
    // definition: an imported core has never run in Ionea's host, so it is listed and can be picked per game,
    // but takes no games on its own.
    public static bool TakesGames(EmuDef d) => d != null && d.IsCore && !Imported(d);
    public static bool Imported(EmuDef d) => d != null && Rank(d) == 2;

    static bool SameFile(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    static JsonNode ReadNode(string file)
    {
        try { return File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file), null, DocOpt) : null; }
        catch (Exception e) { Log.Write($"definitions: could not read {file}: {e.Message}"); return null; }
    }

    // A family's fields first, then the definition's own.
    static JsonObject Resolve(string id, Dictionary<string, JsonObject> byId, HashSet<string> seen)
    {
        if (!byId.TryGetValue(id, out var o)) return null;
        if (!seen.Add(id)) { Log.Write($"definitions: family loop at {id}"); return null; }
        string fam = Text(o["family"]);
        if (fam == null || fam == id) return (JsonObject)o.DeepClone();
        var b = Resolve(fam, byId, seen);
        if (b == null) { Log.Write($"definitions: {id}: family {fam} not found"); return (JsonObject)o.DeepClone(); }
        return Merge(b, o);
    }

    // Objects merge field by field; anything else (lists, text, null) replaces.
    public static JsonObject Merge(JsonObject a, JsonObject b)
    {
        var r = (JsonObject)a.DeepClone();
        foreach (var kv in b)
        {
            if (kv.Value is JsonObject bo && r[kv.Key] is JsonObject ao) r[kv.Key] = Merge(ao, bo);
            else r[kv.Key] = kv.Value?.DeepClone();
        }
        return r;
    }

    // ---- lookups ----
    public static EmuDef ById(string id) { EnsureLoaded(); return id == null ? null : All.FirstOrDefault(d => d.Id == id); }
    public static EmuDef ForExe(string exe) { EnsureLoaded(); string n = Path.GetFileName(exe ?? ""); return n == "" ? null : All.FirstOrDefault(d => d.Matches(n)); }
    // The definition an emulator on the list uses: its own, or the one its program is.
    public static EmuDef Of(Emulator e) => e == null ? null : ById(e.Preset) ?? ForExe(e.Exe);

    // ---- versions and levels ----
    static readonly ConcurrentDictionary<string, string> versions = new(StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> told = new(StringComparer.OrdinalIgnoreCase);

    // Its own version ("1.1", "0.10.5.0", "228"), or null when the program doesn't say.
    public static string VersionOf(string exe)
    {
        if (exe == null || !File.Exists(exe)) return null;
        string key = exe + "|" + File.GetLastWriteTimeUtc(exe).Ticks;
        return versions.GetOrAdd(key, _ =>
        {
            try
            {
                var fv = FileVersionInfo.GetVersionInfo(exe);
                foreach (string s in new[] { fv.FileVersion, fv.ProductVersion })
                {
                    var m = Regex.Match(s ?? "", @"\d+(\.\d+)*");
                    if (m.Success && m.Value.Split('.').Any(p => p.TrimStart('0') != "")) return m.Value;   // 0.0.0.0: says nothing
                }
            }
            catch { /* no version info: version unknown */ }
            return null;
        });
    }

    static int[] Parts(string v) => (v ?? "").Split('.').Select(p => int.TryParse(p, out int n) ? n : 0).ToArray();
    static int Cmp(int[] a, int[] b)
    {
        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            int x = i < a.Length ? a[i] : 0, y = i < b.Length ? b[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    // Is this version inside the tested range? (null range or version: yes)
    public static bool InRange(string version, DefVersion t)
    {
        if (t == null || version == null) return true;
        var v = Parts(version);
        if (t.Min != null && Cmp(v, Parts(t.Min)) < 0) return false;
        if (t.Max == null) return true;
        if (t.Max.Contains('*'))
        {
            var pre = Parts(t.Max.Split('*')[0].TrimEnd('.'));
            return pre.Select((p, i) => i < v.Length ? v[i] == p : p == 0).All(x => x);
        }
        return Cmp(v, Parts(t.Max)) <= 0;
    }

    public static SupportLevel Level(Emulator e)
    {
        if (e == null) return SupportLevel.Generic;
        var d = Of(e);
        if (e.IsCore) return SupportLevel.Core;
        if (d == null) return SupportLevel.Generic;
        var lvl = d.Controls != null || d.Video != null ? SupportLevel.Managed : SupportLevel.Known;
        string v = VersionOf(e.Exe);
        if (!InRange(v, d.Tested))
        {
            lvl--;
            lock (told) if (told.Add(e.Exe + "|" + v))
                Log.Write($"definitions: {e.Name} {v} is outside its tested versions ({d.Tested.Min}-{d.Tested.Max}): {lvl}");
        }
        return lvl;
    }

    public static string LevelName(SupportLevel l) => l switch
    {
        SupportLevel.Core => "Core", SupportLevel.Managed => "Managed", SupportLevel.Known => "Known", _ => "Generic",
    };

    // The definition when the emulator gets at least this level, else null. For what Ionea writes into the
    // emulator's own files (controls, video).
    public static EmuDef At(Emulator e, SupportLevel min) => Level(e) >= min ? Of(e) : null;

    // What Ionea doesn't do for it, in a few words each: its page's Support row and the start's
    // toasts show them as "Saves are shared" did. Empty for a core or an emulator with nothing missing.
    public static List<string> Limits(Emulator e)
    {
        var l = new List<string>();
        if (e == null || e.IsCore || e.Exe == null) return l;
        if (Of(e) == null) { l.Add("not known to Ionea"); l.Add("its own controls"); l.Add("saves are shared"); return l; }
        if (!Tested(e)) l.Add($"version {VersionOf(e.Exe)} not tested");
        if (EmuControls.Mapping(e) == null) l.Add("its own controls");
        if (ProfileSaves.Shared(e)) l.Add("saves are shared");
        return l;
    }

    // "Managed", "Known · its own controls · saves are shared": the level, then its limits.
    public static string Support(Emulator e) => string.Join(" · ", new[] { LevelName(Level(e)) }.Concat(Limits(e)));

    // The definition for what doesn't write into the emulator's own config: found, portable marker, saves kept
    // per profile. A version outside the tested range keeps these (the drop is still logged by Level): saves
    // stop being kept apart only when there is no definition at all.
    public static EmuDef Known(Emulator e) { Level(e); return Of(e); }

    // Is its version inside the tested range (or unknown)? Only then does Ionea write into its config files,
    // first-run seed files included.
    public static bool Tested(Emulator e)
    {
        if (e == null) return false;
        if (e.IsCore) return true;
        Level(e);
        return InRange(VersionOf(e.Exe), Of(e)?.Tested);
    }
}
