#nullable disable
// Emulators: the catalog (systems and emulator definitions, from the definition files: EmuDefs.cs), the user's list (emulators.json),
// finding emulators in the Emulators folders, installing one from a checked archive in the Inbox,
// and adding game files (ROMs) that start through an emulator.
// Nothing here runs an emulator; LaunchFx does that when a game is started.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using static Ionea.Loc;

namespace Ionea;

// The catalog: systems and emulator definitions, read from the definition files (EmuDefs.cs).
public static class EmuCatalog
{
    public static IReadOnlyList<EmuSystem> Systems { get { EmuDefs.EnsureLoaded(); return EmuDefs.Systems; } }
    public static IReadOnlyList<EmuDef> Presets { get { EmuDefs.EnsureLoaded(); return EmuDefs.All; } }

    public static EmuSystem System(string id) => id == null ? null : Systems.FirstOrDefault(s => s.Id == id);
    public static EmuDef Preset(string id) => EmuDefs.ById(id);
    public static string SystemNames(IEnumerable<string> ids) =>
        string.Join(", ", ids.Select(i => System(i)?.Name ?? i));

    public static List<string> ExtsFor(IEnumerable<string> systems, IEnumerable<string> extra = null) =>
        systems.SelectMany(s => System(s)?.Exts ?? Array.Empty<string>()).Concat(extra ?? Array.Empty<string>())
               .Select(e => e.ToLowerInvariant()).Distinct().ToList();

    // Which definition an exe is (by file name), or null.
    public static EmuDef ForExe(string exe) => EmuDefs.ForExe(exe);

    // Which definition a download in the Inbox most likely holds (the longest name hint in its file name), or null.
    public static EmuDef ForArchive(string file)
    {
        string n = Path.GetFileName(file).ToLowerInvariant();
        return Presets.Where(p => !string.IsNullOrEmpty(p.Detect?.Archive) && n.Contains(p.Detect.Archive.ToLowerInvariant()))
                      .OrderByDescending(p => p.Detect.Archive.Length).FirstOrDefault();
    }
}

public sealed class Emulator
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string Preset { get; set; }          // catalog id, or null for one added by hand
    public string Exe { get; set; }             // full path
    public string Args { get; set; }            // with {rom}
    public List<string> Systems { get; set; } = new();
    public List<string> Exts { get; set; } = new();
    public DateTime Added { get; set; }
    public bool? Inside { get; set; }            // its game window inside the launcher (null: yes); false: on its own
    // Ionea writes its controls at every game start (null or true: yes, the default for every emulator however it
    // was added); false: it keeps the controls set in its own window (its page's switch, or "Keep its own").
    public bool? Controls { get; set; }

    [System.Text.Json.Serialization.JsonIgnore] public bool Present => Exe != null && File.Exists(Exe);
    // A libretro core: runs inside the launcher, which draws the game and owns the Home menu.
    [System.Text.Json.Serialization.JsonIgnore] public bool IsCore => Exe != null && Exe.EndsWith("_libretro.dll", StringComparison.OrdinalIgnoreCase);
    [System.Text.Json.Serialization.JsonIgnore] public string SystemText =>
        Systems.Count > 0 ? EmuCatalog.SystemNames(Systems) : string.Join(" ", Exts);
}

public sealed class EmulatorFile
{
    public int Version { get; set; } = 1;
    public List<Emulator> Emulators { get; set; } = new();
    public List<string> Dismissed { get; set; } = new();   // exes the user removed: not found again automatically
}

public sealed class EmuList
{
    static string InboxDir => AddGameLogic.Inbox;
    readonly string file;
    public EmulatorFile Data;
    // Where emulators live (Paths.EmuFolders: this PC's folders, ES-DE's Emulators folder first when set). The first
    // one is where installs go; with none set, Ionea's own folder (data\emulators).
    public readonly List<string> Folders = new();
    public readonly string DownloadsDir;   // emulators Ionea downloaded itself (EmuInstall.cs): installable like the Inbox

    public EmuList(string dataDir, IEnumerable<string> folders)
    {
        file = Path.Combine(dataDir, "emulators.json");
        Data = Json.Load<EmulatorFile>(file);
        DownloadsDir = Path.Combine(dataDir, "downloads");
        foreach (string f in folders ?? Array.Empty<string>())
            if (!string.IsNullOrWhiteSpace(f) && !Folders.Any(x => SamePath(x, f))) Folders.Add(Path.GetFullPath(f));
        if (Folders.Count == 0) Folders.Add(Path.Combine(dataDir, "emulators"));
    }

    public string InstallDir => Folders[0];
    public void Save() { try { Json.Save(file, Data); } catch (Exception e) { Log.Write("emulators save: " + e.Message); } }
    public Emulator Get(string id) => id == null ? null : Data.Emulators.FirstOrDefault(e => e.Id == id);
    public List<Emulator> Sorted() => Data.Emulators.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    static bool SamePath(string a, string b) =>
        a != null && b != null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    public Emulator ByExe(string exe) => Data.Emulators.FirstOrDefault(e => SamePath(e.Exe, exe));

    public Emulator FromPreset(EmuDef p, string exe) => new()
    {
        Id = Library.NewId(), Name = p.Name, Preset = p.Id, Exe = Path.GetFullPath(exe), Args = p.Args,
        Systems = p.Systems.ToList(), Exts = EmuCatalog.ExtsFor(p.Systems, p.ExtraExts), Added = DateTime.Now,
    };

    public void Add(Emulator e)
    {
        Data.Dismissed.RemoveAll(d => SamePath(d, e.Exe));
        Data.Emulators.Add(e);
        if (EmuDefs.Known(e) is EmuDef p) MakePortable(p, e.Exe, EmuDefs.Tested(e));
        Save();
        Log.Write($"emulator added: {e.Name} -> {e.Exe} ({string.Join(",", e.Systems)})");
        ReqFiles.Supply(e);   // required files the store or another emulator already has
    }

    public void Remove(Emulator e)
    {
        Data.Emulators.Remove(e);
        if (e.Exe != null && !Data.Dismissed.Any(d => SamePath(d, e.Exe))) Data.Dismissed.Add(e.Exe);
        Save();
        Log.Write($"emulator removed from the list: {e.Name} ({e.Exe}); files kept");
    }

    // Marker files that make the emulator keep settings and saves next to itself, and its seed files (only for a
    // tested version: a seed is written into its config). Never overwrites.
    public static void MakePortable(EmuDef p, string exe, bool seeds = true)
    {
        string dir = Path.GetDirectoryName(exe);
        if (!seeds && p.Start?.Seed?.Count > 0) Log.Write($"seed files for {p.Name} not written: its version is outside the tested range");
        foreach (var seed in seeds ? p.Start?.Seed ?? new() : new())
        {
            try
            {
                string path = Path.Combine(dir, seed.File);
                if (File.Exists(path)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, (seed.Text ?? "").Replace("\r\n", "\n").Replace("\n", Environment.NewLine));
            }
            catch (Exception ex) { Log.Write($"seed file for {p.Name}: {ex.Message}"); }
        }
        if (p.Start?.Portable == null) return;
        foreach (string m in p.Start.Portable)
        {
            try
            {
                string path = Path.Combine(dir, m.TrimEnd('\\'));
                if (m.EndsWith("\\")) Directory.CreateDirectory(path);
                else if (!File.Exists(path)) File.WriteAllText(path, "");
            }
            catch (Exception ex) { Log.Write($"portable marker {m} for {p.Name}: {ex.Message}"); }
        }
    }

    // Looks through the Emulators folders (two levels deep) for known emulators not on the list yet.
    // Adds them and returns them. Removed ones (Dismissed) are skipped.
    public List<Emulator> Discover()
    {
        var found = new List<Emulator>();
        foreach (string root in Folders.Where(Directory.Exists))
        {
            IEnumerable<string> dirs;
            try { dirs = Directory.GetDirectories(root).SelectMany(d => new[] { d }.Concat(SafeDirs(d))).Prepend(root); }
            catch { continue; }
            foreach (string d in dirs)
                foreach (string exe in Programs(d))
                {
                    if (ByExe(exe) != null || Data.Dismissed.Any(z => SamePath(z, exe))) continue;
                    if (found.Any(f => SamePath(f.Exe, exe))) continue;
                    if (EmuCatalog.ForExe(exe) is not EmuDef p) continue;
                    try
                    {
                        var e = FromPreset(p, exe);
                        // Ionea manages its controls like every other emulator (the same as on a PC set up
                        // earlier). Its own file is kept as .before-launcher; the switch on its page turns this off.
                        e.Controls = true;
                        Add(e);
                        found.Add(e);
                    }
                    catch (Exception ex) { Log.Write($"emulator {exe} ({p.Id}, {p.File}) not added: {ex.Message}"); }   // one bad definition never stops the start
                }
        }
        return found;
    }

    // Rule: games run inside the launcher when a core exists for their system. When a core
    // is found, games of its systems that use a standalone emulator switch to it (the game menu can
    // switch a game back). Only cores with a hand-made or the user's own definition (EmuDefs.TakesGames);
    // an imported one is listed and can be picked per game.
    public void UseCores(Library lib, List<Emulator> found)
    {
        int n = 0;
        foreach (var core in found.Where(e => e.IsCore))
        {
            if (!EmuDefs.TakesGames(EmuDefs.Of(core)))
            {
                int could = lib.Data.Games.Count(g => g.Source == "rom" && g.System != null && core.Systems.Contains(g.System) && Get(g.Emulator)?.IsCore != true);
                Log.Write($"core found, games not switched to it: {core.Name} ({(EmuDefs.Of(core)?.Source is string src ? "imported from " + src : "no definition of its own")}; {could} game(s) of its systems keep their emulator)");
                continue;
            }
            foreach (var g in lib.Data.Games.Where(g => g.Source == "rom" && g.System != null && core.Systems.Contains(g.System)))
            {
                var cur = Get(g.Emulator);
                if (cur != null && cur.IsCore) continue;
                g.Emulator = core.Id; n++;
                Log.Write($"\"{g.Name}\" now runs inside the launcher with {core.Name}");
            }
        }
        if (n > 0) lib.Save();
    }

    static string[] SafeDirs(string d) { try { return Directory.GetDirectories(d); } catch { return Array.Empty<string>(); } }
    static IEnumerable<string> Programs(string d)
    {
        try { return Directory.GetFiles(d).Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList(); }
        catch { return Array.Empty<string>(); }
    }

    // Emulators that can open a file with this extension.
    // The emulators for a game file, by its extension. When they are for different systems (.iso: PSP, PS2, GameCube...)
    // the file's header picks (GameId.SystemOf); a file it can't tell keeps them all.
    public List<Emulator> ForFile(string file)
    {
        string ext = Path.GetExtension(file).ToLowerInvariant();
        var l = Sorted().Where(e => e.Exts.Contains(ext)).ToList();
        if (l.Select(e => string.Join(",", e.Systems)).Distinct().Count() > 1)
        {
            string sys = null;
            try { sys = GameId.SystemOf(file); } catch (Exception caught) { Log.Debug("emulators: system of file", caught); }
            if (sys != null && l.Any(e => e.Systems.Contains(sys))) l = l.Where(e => e.Systems.Contains(sys)).ToList();
        }
        return l;
    }

    public List<string> AllExts() => Data.Emulators.SelectMany(e => e.Exts).Distinct().OrderBy(x => x).ToList();

    // Checked archives in the Inbox: emulator-looking ones first.
    public List<string> InboxArchives()
    {
        if (InboxDir == null || !Directory.Exists(InboxDir)) return new List<string>();
        return Directory.GetFiles(InboxDir).Where(f => Regex.IsMatch(f, @"(?i)\.(zip|7z)$"))
            .OrderByDescending(f => EmuCatalog.ForArchive(f) != null).ThenByDescending(File.GetLastWriteTime).ToList();
    }

    // Unpacks a checked archive into Emulators\<name> with Windows' own tar (zip and 7z). An existing
    // folder is updated in place: program files are replaced, the emulator's settings and saves stay.
    // Returns the new or updated emulator, or throws with a sentence for the user.
    public Emulator Install(string archive, out bool updated)
    {
        updated = false;
        string full = Path.GetFullPath(archive);
        bool Inside(string dir) => dir != null && full.StartsWith(Path.GetFullPath(dir).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
        if (!Inside(InboxDir) && !Inside(DownloadsDir))
            throw new InvalidOperationException(InboxDir != null ? $"Only files in the Inbox ({InboxDir}) or that Ionea downloaded can be installed." : "Only emulators Ionea downloaded can be installed here.");
        var hint = EmuCatalog.ForArchive(full);
        string stage = Path.Combine(Path.GetTempPath(), "ionea-emu-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(stage);
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "tar.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            psi.ArgumentList.Add("-xf"); psi.ArgumentList.Add(full); psi.ArgumentList.Add("-C"); psi.ArgumentList.Add(stage);
            using (var p = Process.Start(psi))
            {
                string err = p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0) throw new InvalidOperationException("The archive could not be unpacked: " + err.Trim());
            }
            // A single top folder inside the archive is the emulator's folder.
            string src = stage;
            var top = Directory.GetFileSystemEntries(stage);
            if (top.Length == 1 && Directory.Exists(top[0])) src = top[0];

            // Find the program inside: the definition the download's name hints at first, then every other.
            EmuDef preset = null; string relExe = null;
            var progs = Directory.GetFiles(src, "*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f.Length).ToList();
            foreach (var p in EmuCatalog.Presets.OrderByDescending(x => x == hint))
            {
                string hit = progs.FirstOrDefault(f => p.Matches(Path.GetFileName(f)));
                if (hit != null) { preset = p; relExe = Path.GetRelativePath(src, hit); break; }
            }
            if (preset == null) throw new InvalidOperationException("No known emulator was found in this archive. Unpack it into the Emulators folder yourself, then use \"Choose a program\".");

            bool isCore = relExe.EndsWith("_libretro.dll", StringComparison.OrdinalIgnoreCase);
            string dest = isCore ? Path.Combine(InstallDir, "Cores") : Path.Combine(InstallDir, preset.Name);
            updated = Directory.Exists(dest);
            Directory.CreateDirectory(dest);
            CopyTree(src, dest);
            string exe = Path.Combine(dest, relExe);
            if (isCore && !File.Exists(exe)) exe = Path.Combine(dest, Path.GetFileName(relExe));
            Log.Write($"emulator {(updated ? "updated" : "installed")}: {preset.Name} from {Path.GetFileName(full)} -> {dest}");

            var e = ByExe(exe);
            if (e == null)
            {
                e = FromPreset(preset, exe);
                e.Controls = true;   // Ionea put it here: Ionea sets its controls
                Add(e);
            }
            else if (EmuDefs.Known(e) is EmuDef pd) MakePortable(pd, exe, EmuDefs.Tested(e));
            return e;
        }
        finally { try { Directory.Delete(stage, true); } catch { /* best-effort cleanup */ } }
    }

    static void CopyTree(string from, string to)
    {
        foreach (string d in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, d)));
        foreach (string f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(f, Path.Combine(to, Path.GetRelativePath(from, f)), true);
    }
}

public static class RomLogic
{
    // "Pokemon - White Version 2 (USA, Europe) (NDSi Enhanced) [!]" -> "Pokemon - White Version 2"
    // PS4 games: "Bloodborne [CUSA00900]" -> "Bloodborne".
    public static string NameFromFile(string file)
    {
        string stem = Stem(file);
        string n = Regex.Replace(stem, @"\s*[\(\[][^\)\]]*[\)\]]", "");
        n = n.Replace('_', ' ');
        n = AddGameLogic.Clean(n);
        if (n.Length == 0) return stem;
        return Disc(stem) is int d ? $"{n} (Disc {d})" : n;   // the discs of one game keep apart
    }

    // "Game (USA) (Disc 2)", "Game (Disk 2 of 3)", "Game [CD2]" -> 2; null when the name has no disc number.
    public static int? Disc(string name)
    {
        var m = Regex.Match(name ?? "", @"[\(\[]\s*(?:disc|disk|cd)\s*(\d{1,2})(?:\s*of\s*\d{1,2})?\s*[\)\]]", RegexOptions.IgnoreCase);
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }

    // A name without its "(Disc n)" (searches for covers and names).
    public static string WithoutDisc(string name) => Regex.Replace(name ?? "", @"\s*[\(\[]\s*(?:disc|disk|cd)\s*\d{1,2}(?:\s*of\s*\d{1,2})?\s*[\)\]]", "", RegexOptions.IgnoreCase).Trim();

    // What names a game file: its file name without extension, or for a PS4 game folder's eboot.bin the folder's name.
    public static string Stem(string file) => IsEboot(file) ? Path.GetFileName(Path.GetDirectoryName(file)) ?? "eboot" : Path.GetFileNameWithoutExtension(file);
    public static bool IsEboot(string file) => string.Equals(Path.GetFileName(file), "eboot.bin", StringComparison.OrdinalIgnoreCase);
    // shadPS4 reads a game's update from "<game folder>-patch" / "-UPDATE" next to it; those are not games.
    public static bool IsPs4Update(string dir) => Regex.IsMatch(Path.GetFileName(dir.TrimEnd('\\', '/')), @"-(patch|update)$", RegexOptions.IgnoreCase);

    // null = fine, otherwise a sentence for the user.
    public static string Check(string rom, Library lib)
    {
        if (!File.Exists(rom)) return T("home.the_file_was_not_found");
        if (AddGameLogic.InInbox(rom)) return T("errors.files_in_are_not_checked_in", AddGameLogic.Inbox);
        var dup = lib.Data.Games.FirstOrDefault(g => g.Source == "rom" && g.Rom != null &&
            string.Equals(Path.GetFullPath(g.Rom), Path.GetFullPath(rom), StringComparison.OrdinalIgnoreCase));
        if (dup != null) return T("errors.it_is_already_in_your_games", dup.Name) + (dup.Removed ? " (removed from the list)." : ".");
        return null;
    }

    // system: when known (the scan); else the file's header (GameId.SystemOf), else the emulator's first system for
    // the extension. coverFrom: Game.CoverFrom ("own" when the user picked the picture, "icon" for the file's own).
    public static Game Commit(Library lib, string rom, Emulator emu, string name, System.Windows.Media.Imaging.BitmapSource cover,
        string system = null, string coverFrom = null, bool save = true)
    {
        string ext = Path.GetExtension(rom).ToLowerInvariant();
        if (system == null || !emu.Systems.Contains(system))
        {
            string head = null;
            try { head = GameId.SystemOf(rom); } catch (Exception caught) { Log.Debug("emulators: system of game", caught); }
            system = head != null && emu.Systems.Contains(head) ? head
                : emu.Systems.FirstOrDefault(s => EmuCatalog.System(s)?.Exts.Contains(ext) == true) ?? emu.Systems.FirstOrDefault();
        }
        var g = new Game
        {
            Id = Library.NewId(), Name = name, Source = "rom", Added = DateTime.Now, Rom = Path.GetFullPath(rom), Target = Path.GetFullPath(rom),
            Emulator = emu.Id, System = system,
        };
        if (cover != null) { g.Cover = g.Id + ".png"; g.CoverFrom = coverFrom; g.CoverUrl = ""; Art.SavePng(cover, Path.Combine(lib.CoversDir, g.Cover)); }
        lib.Data.Games.Add(g);
        if (save) lib.Save();
        Log.Write($"added game file \"{name}\" -> {rom} with {emu.Name}");
        return g;
    }

    // The emulator's command line for a game. Every {rom} is replaced by the full path.
    public static string Args(Emulator e, string rom)
    {
        string a = string.IsNullOrWhiteSpace(e.Args) ? "\"{rom}\"" : e.Args;
        if (!a.Contains("{rom}")) a += " \"{rom}\"";
        return a.Replace("{rom}", rom);
    }
}
