#nullable disable
// Launcher data: settings.json, library.json, shortcuts\ and covers\ inside the data folder.
// Nothing here ever runs a game file; it only reads and writes shortcuts and images.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ionea;

static class Json
{
    public static readonly JsonSerializerOptions Opt = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // A file that can't be read is never written over: it is kept as <file>.broken-<time>, the copy from the save
    // before (<file>.prev) is used when it reads, and the launcher says so once the home screen is up (Broken).
    public static T Load<T>(string file) where T : new()
    {
        if (!File.Exists(file)) return new T();
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(file), Opt) ?? new T(); }
        catch (Exception e)
        {
            Log.Write($"could not read {file}: {e.Message}");
            string kept = null;
            try { kept = file + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"); File.Copy(file, kept, false); }
            catch (Exception x) { Log.Write($"could not keep {file}: {x.Message}"); kept = null; }
            lock (Gate) unreadable.Add(Path.GetFullPath(file));
            T value = new T();
            bool restored = false;
            try
            {
                if (File.Exists(file + ".prev"))
                {
                    value = JsonSerializer.Deserialize<T>(File.ReadAllText(file + ".prev"), Opt) ?? new T();
                    restored = true;
                    Log.Write($"{file}: using the copy from the save before ({file}.prev)");
                }
            }
            catch (Exception x) { Log.Write($"could not read {file}.prev either: {x.Message}"); value = new T(); }
            lock (Gate) broken.Add((Path.GetFileName(file), restored, kept));
            return value;
        }
    }

    static readonly object Gate = new();
    static readonly HashSet<string> unreadable = new(StringComparer.OrdinalIgnoreCase);
    static readonly List<(string Name, bool Restored, string Kept)> broken = new();

    // The files that could not be read since the last call (shown once on the home screen).
    public static List<(string Name, bool Restored, string Kept)> TakeBroken()
    {
        lock (Gate) { var l = broken.ToList(); broken.Clear(); return l; }
    }

    // Write to a temp file first so a crash never leaves half a file. The file before is kept as <file>.prev,
    // except a file that could not be read (its copy is the .broken one; .prev stays the last good one).
    public static void Save<T>(string file, T value)
    {
        string tmp = file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Opt));
        bool bad;
        lock (Gate) bad = unreadable.Remove(Path.GetFullPath(file));
        if (!bad && File.Exists(file))
        {
            try { File.Replace(tmp, file, file + ".prev", true); return; }
            catch (Exception x) { Log.Write($"save {file}: no .prev copy ({x.Message})"); }
        }
        File.Move(tmp, file, true);
    }
}

public enum Zoom { Large = 0, Medium = 1, Small = 2 }

public sealed class Settings
{
    public string Name { get; set; }                 // null until the first-run screen is done
    public bool Dark { get; set; }
    public string IconShape { get; set; } = "round";  // tiles, option buttons, profile pictures: round / rounded / square (Art.IconShape); per profile
    public string Colours { get; set; } = "ionea";    // colour preset (ColourPreset.All ids): accents, rings, glows, bars; per profile
    public Zoom Zoom { get; set; } = Zoom.Large;
    public bool Fullscreen { get; set; } = true;
    public int Brightness { get; set; } = 100;       // the whole launcher, 40-100 % (MainWindow.SetBrightness): for a bright display; this PC, not per profile
    public string Order { get; set; } = "recent";    // "recent" (last played first) or "name"
    public bool Sounds { get; set; } = true;
    public bool Intro { get; set; } = true;           // the eyes intro before the start screen (Intro.cs)
    public bool Outro { get; set; } = true;           // the short outro when the launcher closes (Outro.cs)
    public bool NintendoButtons { get; set; }        // games in the launcher: Xbox A/B swapped to the Nintendo positions
    public bool Discord { get; set; } = true;        // "Playing Ionea" + the game on Discord (Discord.cs); per profile
    // Offline mode (Online.cs): no online connection of any kind. Per profile and on for every new one; kept only
    // in the profile's file, so before a profile is chosen Ionea stays offline.
    [JsonIgnore] public bool Offline { get; set; } = true;
    public List<Place> Places { get; set; }          // where game files can be stored (null: Storage.Defaults)
    public InputPrefs Input { get; set; } = new();   // keyboard / controller / mouse: on or off, pop-ups, bindings (InputSetup.cs)
    public SoundPrefs Sound { get; set; } = new();   // volume, and each group of sound effects
    public OnlinePrefs OnlineUse { get; set; } = new();   // Going online switches (Online.cs); this PC's, not a profile's
    public UpdatePrefs Updates { get; set; } = new();     // Settings > About and updates (Updater.cs); this PC's
    public List<string> CoresOffered { get; set; }        // cores CoreOfferCard has offered (definition ids): each asked once
    public bool FilesAsked { get; set; }             // the first-start Required files screen was shown (RequiredFiles.cs); this PC's
    public PathPrefs Paths { get; set; } = new();    // this PC's folders: ES-DE, the Inbox, emulators, game folders (Paths.cs)
    public bool TutorialAsked { get; set; }          // the first-start tutorial was offered (Tutorial.cs); this PC's
    public bool ScanAsked { get; set; }              // the first-start scan's results were shown (Scan.cs); this PC's
    public bool TaskManagerGroup { get; set; }          // emulators and ports show inside Ionea's Task Manager group (EmbedLogic.Group); off by default; this PC's
    public bool GroupDefaultOff { get; set; }           // TaskManagerGroup was moved to off once (it used to default to on, and old files can't tell a choice from the default)
    public string SteamGridKey { get; set; }         // the user's own SteamGridDB key (covers), or null; this PC's
    public DisplayPrefs Display { get; set; } = new();   // how games show by default: mode, picture size, window (Display.cs); this PC's
    public Dictionary<string, string> Filters { get; set; }   // picture filter per system id, for games inside the launcher (PictureFilter.cs); this PC's
    public OverlayPrefs Overlay { get; set; } = new();   // the in-game performance overlay by default: off / simple / detailed, corner, size, opacity (Overlay.cs); this PC's
    public bool FreezeEmulator { get; set; }             // the Home menu freezes a standalone emulator (NtSuspendProcess) instead of its own pause key / letting it run; off by default; this PC's
    public bool StreamingMode { get; set; }              // window capture (Discord, OBS) sees games: standalone emulators as child windows, hosted pictures drawn by WPF (EmbedLogic.Owned, DirectPicture.Off); off by default; this PC's
    public bool SyncToDisplay { get; set; } = true;      // games inside the launcher keep time with the display's refresh when they can (DisplaySync.cs); this PC's
    public PlayPrefs Play { get; set; } = new();         // games inside the launcher: fast-forward (FastForward.cs); this PC's
    public string Language { get; set; } = "auto";       // UI language (Loc.cs): "auto" follows Windows when there is a file for it; this PC's

    // With a profile active, the per-person settings (ProfilePrefs) also go to its own file, so each
    // profile keeps its look, layout, order, folders, sounds and input. The shared file keeps the rest.
    [JsonIgnore] public string File, ProfileFile;
    [JsonIgnore] public Func<Dictionary<string, string>> Folders;   // the active profile's folders: game id -> folder
    public static Settings Load(string dir)
    {
        var s = Json.Load<Settings>(Path.Combine(dir, "settings.json"));
        s.File = Path.Combine(dir, "settings.json");
        s.Sound ??= new(); s.Input ??= new(); s.OnlineUse ??= new(); s.OnlineUse.On ??= new(); s.Updates ??= new();
        s.Paths ??= new(); s.Paths.Emulators ??= new(); s.Paths.Games ??= new();
        s.Display ??= new();
        s.Overlay ??= new();
        s.Play ??= new();
        MoveGroupOff(s);
        return s;
    }
    // Once: Group in Task Manager goes to off, even if it was on (the old default looked the same as a choice).
    public static void MoveGroupOff(Settings s)
    {
        if (s.GroupDefaultOff) return;
        if (s.TaskManagerGroup) Log.Write("settings: Group in Task Manager is now off by default; turn it on in Settings > Library > Advanced");
        s.TaskManagerGroup = false; s.GroupDefaultOff = true;
    }
    public void Save()
    {
        try { Json.Save(File, this); } catch (Exception e) { Log.Write("settings save: " + e.Message); }
        SaveProfile();
    }
    public void SaveProfile()
    {
        if (ProfileFile == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ProfileFile));
            Json.Save(ProfileFile, ProfilePrefs.From(this, Folders?.Invoke()));
        }
        catch (Exception e) { Log.Write("profile settings save: " + e.Message); }
    }
}

// What each profile keeps for itself (data\profiles\<id>\settings.json). Facts about this PC stay shared:
// display mode, storage places, and the devices seen (InputPrefs.Seen / Roles).
public sealed class ProfilePrefs
{
    public int Version { get; set; } = 1;
    public bool Dark { get; set; }
    public string IconShape { get; set; } = "round";
    public string Colours { get; set; } = "ionea";
    public Zoom Zoom { get; set; } = Zoom.Large;
    public string Order { get; set; } = "recent";
    public bool Sounds { get; set; } = true;
    public SoundPrefs Sound { get; set; } = new();
    public bool NintendoButtons { get; set; }
    public bool Discord { get; set; } = true;
    public bool Offline { get; set; } = true;   // a profile file from an older version starts offline too
    public InputPrefs Input { get; set; } = new();
    public Dictionary<string, string> Folders { get; set; } = new();

    static T Copy<T>(T v) where T : new() => v == null ? new T() : JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(v, Json.Opt), Json.Opt) ?? new T();

    public static ProfilePrefs From(Settings s, Dictionary<string, string> folders)
    {
        var input = Copy(s.Input);
        input.Seen = null; input.Roles = null;
        return new ProfilePrefs
        {
            Dark = s.Dark, IconShape = s.IconShape ?? "round", Colours = s.Colours ?? "ionea", Zoom = s.Zoom, Order = s.Order ?? "recent", Sounds = s.Sounds, Sound = Copy(s.Sound),
            NintendoButtons = s.NintendoButtons, Discord = s.Discord, Offline = s.Offline, Input = input, Folders = folders ?? new(),
        };
    }

    // Into the live settings and library; the PC's own device list is kept.
    public void ApplyTo(Settings s, Library lib)
    {
        var input = Copy(Input);
        input.Seen = s.Input?.Seen; input.Roles = s.Input?.Roles;
        s.Dark = Dark; s.IconShape = IconShape ?? "round"; s.Colours = Colours ?? "ionea"; s.Zoom = Zoom; s.Order = Order == "name" ? "name" : "recent"; s.Sounds = Sounds;
        s.Sound = Copy(Sound); s.NintendoButtons = NintendoButtons; s.Discord = Discord; s.Offline = Offline; s.Input = input;
        var f = Folders ?? new();
        foreach (var g in lib.Data.Games) g.Folder = f.TryGetValue(g.Id, out string name) && !string.IsNullOrWhiteSpace(name) ? name : null;
    }

    public static Dictionary<string, string> FoldersOf(Library lib) =>
        lib.Data.Games.Where(g => g.Folder != null).ToDictionary(g => g.Id, g => g.Folder);
}

// Launcher sounds: one volume for all of them, and one per group of effects (Sounds.Groups), 0-100.
public sealed class SoundPrefs
{
    public int Volume { get; set; } = 100;
    public Dictionary<string, int> Groups { get; set; }   // group id -> 0-100 (missing = 100)
    public int Group(string id) => Groups != null && Groups.TryGetValue(id, out int v) ? Math.Clamp(v, 0, 100) : 100;
    public void SetGroup(string id, int v) { Groups ??= new(); Groups[id] = Math.Clamp(v, 0, 100); }
    // One level per kind of sound (Sounds.Kinds: "nav" Navigation, "act" Actions), the sound
    // theme (Sounds.Themes) and Reduce effects (fewer and shorter animations, Anim.Reduced).
    public int Navigation { get; set; } = 100;
    public int Actions { get; set; } = 100;
    public string Theme { get; set; } = "ionea";
    public bool ReduceEffects { get; set; }
    public int Kind(string id) => Math.Clamp(id == "act" ? Actions : Navigation, 0, 100);
    public void SetKind(string id, int v) { if (id == "act") Actions = Math.Clamp(v, 0, 100); else Navigation = Math.Clamp(v, 0, 100); }
}

public sealed class Game
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string Shortcut { get; set; }   // file name inside data\shortcuts
    public string Cover { get; set; }      // file name inside data\covers, or null
    public string CoverFrom { get; set; }  // "own" (the user chose it: never replaced), "icon", "steam", "libretro", "steamgriddb"; null: older or none
    // The cover's public web address (LibRetro, SteamGridDB, Steam) for the Discord status's small picture
    // (Covers.PublicUrl). "" = the cover has none (a file of the user's, the game's own icon, the name).
    // null = not known yet (a cover set by an older version): Covers.FindUrl works it out once.
    public string CoverUrl { get; set; }
    public string Target { get; set; }     // the program the shortcut starts (for duplicate checks)
    public string Args { get; set; }
    public string Source { get; set; }     // "es-de", "added", "dummy", "steam", "rom"
    public string AppId { get; set; }      // Steam app id (Source "steam"); Target is then the install folder
    public string Rom { get; set; }        // game file (Source "rom"), started through Emulator
    public string Emulator { get; set; }   // emulator id in emulators.json
    public string System { get; set; }     // catalog system id, e.g. "nds"
    public DateTime Added { get; set; }
    public DateTime? LastPlayed { get; set; }
    // Containers and remove-from-list. Kept in the file now so old libraries load later.
    public string Folder { get; set; }     // null = on the home row
    public bool Removed { get; set; }      // hidden from the list; files kept, restorable
    // Graphics / performance tweaks for this game only (Tweaks.cs): setting key -> value. Null = none.
    public Dictionary<string, string> Tweaks { get; set; }
    // Emulator or native port, per profile (Ports.cs): profile id -> "emulator" or the port's emulator id. Null = not asked yet.
    public Dictionary<string, string> PlayWith { get; set; }
    // A game with a native port and an emulator that share saves (Ports.SyncSaves): "shared" / "separate". Null = the port's default (shared).
    public string Saves { get; set; }
    // Cover zoom and position (CoverZoom.cs): 1-4 and the shown part's centre in the picture's 0..1 units. Null = the plain fill.
    public double? CoverZoom { get; set; }
    public double? CoverX { get; set; }
    public double? CoverY { get; set; }
    // The name tile's colour when the game has no picture (CoverColour.cs). Null = plain.
    public string CoverColour { get; set; }
    // How this game shows (Display.cs): its own display mode, picture size, window size and place. Null = the default.
    public DisplayPrefs Display { get; set; }
    // The in-game performance overlay for this game (Overlay.cs): its own mode, corner, size, opacity. Null = the default.
    public OverlayPrefs Overlay { get; set; }
    // A program that runs beside this game while it plays (Helper.cs), e.g. a local stand-in for a service it asks for. Null = none.
    public GameHelper Helper { get; set; }
}

// Game.Helper (Helper.cs): the program, its arguments and the folder it runs in (null = the program's own folder).
public sealed class GameHelper
{
    public string Program { get; set; }
    public string Args { get; set; }
    public string Dir { get; set; }
}

public sealed class LibraryFile
{
    public int Version { get; set; } = 1;
    public bool EsdeImported { get; set; }
    public List<Game> Games { get; set; } = new();
}

public sealed class Library
{
    public readonly string Dir;
    public LibraryFile Data;
    public string ShortcutsDir => Path.Combine(Dir, "shortcuts");
    public string CoversDir => Path.Combine(Dir, "covers");
    string FilePath => Path.Combine(Dir, "library.json");

    public Library(string dir)
    {
        Dir = dir;
        Directory.CreateDirectory(ShortcutsDir);
        Directory.CreateDirectory(CoversDir);
        Data = Json.Load<LibraryFile>(FilePath);
    }

    public void Save()
    {
        try { Json.Save(FilePath, Data); } catch (Exception e) { Log.Write("library save: " + e.Message); }
        Saved?.Invoke();
    }
    public Action Saved;   // folders belong to the active profile: its settings file is written too

    public string ShortcutPath(Game g) => g.Shortcut == null ? null : Path.Combine(ShortcutsDir, g.Shortcut);
    public string CoverPath(Game g) => g.Cover == null ? null : Path.Combine(CoversDir, g.Cover);

    // When a game was last played, for "recent" order: set to the active profile's own times.
    public Func<Game, DateTime?> PlayedAt = g => g.LastPlayed;

    List<Game> Sorted(IEnumerable<Game> list, string order)
    {
        if (order == "name") return list.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        return list.OrderByDescending(g => PlayedAt(g) ?? DateTime.MinValue)
                   .ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // What the home row shows, in display order.
    public List<Game> HomeGames(string order) => Sorted(Data.Games.Where(g => !g.Removed && g.Folder == null), order);
    public List<Game> FolderGames(string folder, string order) =>
        Sorted(Data.Games.Where(g => !g.Removed && string.Equals(g.Folder, folder, StringComparison.CurrentCultureIgnoreCase)), order);
    // Search covers every listed game, also those inside folders.
    public List<Game> Search(string text, string order) =>
        Sorted(Data.Games.Where(g => !g.Removed && g.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase)), order);
    // A folder exists while it holds at least one listed game.
    public List<string> Folders() => Data.Games.Where(g => !g.Removed && g.Folder != null).Select(g => g.Folder)
        .Distinct(StringComparer.CurrentCultureIgnoreCase).OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase).ToList();
    // Each folder's most recent play (MinValue when none of its games was played), in one pass over the library.
    public Dictionary<string, DateTime> FolderPlayedAt()
    {
        var at = new Dictionary<string, DateTime>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var g in Data.Games)
        {
            if (g.Removed || g.Folder == null) continue;
            var t = PlayedAt(g) ?? DateTime.MinValue;
            if (!at.TryGetValue(g.Folder, out var had) || t > had) at[g.Folder] = t;
        }
        return at;
    }
    public List<Game> RemovedGames() => Data.Games.Where(g => g.Removed).OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    public static string NewId() => Guid.NewGuid().ToString("N")[..10];
}

// .lnk files through the Windows Script Host COM object (same as the old add-game helper).
public static class Shortcut
{
    static object Shell() => Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
    static object Get(object o, string p) => o.GetType().InvokeMember(p, System.Reflection.BindingFlags.GetProperty, null, o, null);
    static void Set(object o, string p, object v) => o.GetType().InvokeMember(p, System.Reflection.BindingFlags.SetProperty, null, o, new[] { v });
    static object Open(string lnk)
    {
        object sh = Shell();
        return sh.GetType().InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, sh, new object[] { lnk });
    }

    public static void Read(string lnk, out string target, out string args, out string workDir, out string icon)
    {
        object sc = Open(lnk);
        target = (string)Get(sc, "TargetPath"); args = (string)Get(sc, "Arguments");
        workDir = (string)Get(sc, "WorkingDirectory"); icon = (string)Get(sc, "IconLocation");
    }

    public static void Create(string lnk, string target, string args, string workDir, string icon)
    {
        object sc = Open(lnk);
        Set(sc, "TargetPath", target);
        Set(sc, "Arguments", args ?? "");
        Set(sc, "WorkingDirectory", workDir ?? "");
        if (!string.IsNullOrEmpty(icon) && !icon.StartsWith(",")) Set(sc, "IconLocation", icon);
        sc.GetType().InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, sc, null);
    }
}

// One-time import of the PC games from the ES-DE install. Copies only; ES-DE's files are never changed.
public static class EsdeImport
{
    public static int Run(Library lib, string esdeDir)
    {
        if (lib.Data.EsdeImported) return 0;
        int n = 0;
        string roms = Path.Combine(esdeDir, "ROMs", "windows");
        string covers = Path.Combine(esdeDir, "ES-DE", "downloaded_media", "windows", "covers");
        if (Directory.Exists(roms))
        {
            foreach (string lnk in Directory.GetFiles(roms, "*.lnk").OrderBy(f => f))
            {
                string name = Path.GetFileNameWithoutExtension(lnk);
                if (name.Equals("Add game", StringComparison.OrdinalIgnoreCase)) continue;   // ES-DE's "+" entry
                if (lib.Data.Games.Any(g => g.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
                try
                {
                    var g = new Game { Id = Library.NewId(), Name = name, Source = "es-de", Added = DateTime.Now, Shortcut = name + ".lnk" };
                    File.Copy(lnk, Path.Combine(lib.ShortcutsDir, g.Shortcut), true);
                    Shortcut.Read(lnk, out string t, out string a, out _, out _);
                    g.Target = t; g.Args = string.IsNullOrWhiteSpace(a) ? null : a;
                    foreach (string ext in new[] { ".png", ".jpg", ".jpeg" })
                    {
                        string c = Path.Combine(covers, name + ext);
                        if (!File.Exists(c)) continue;
                        g.Cover = g.Id + ext;
                        File.Copy(c, Path.Combine(lib.CoversDir, g.Cover), true);
                        break;
                    }
                    lib.Data.Games.Add(g); n++;
                }
                catch (Exception e) { Log.Write($"import {name}: {e.Message}"); }
            }
        }
        lib.Data.EsdeImported = true;
        lib.Save();
        Log.Write($"imported {n} game(s) from {roms}");
        return n;
    }
}
