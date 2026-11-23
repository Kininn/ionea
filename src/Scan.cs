#nullable disable
// The scan: what emulators and games this PC already has, so a new user starts with a full
// list instead of a folder layout Ionea assumed. Cheapest places first: Start menu and desktop shortcuts, the
// installed-programs list, Scoop, EmuDeck, RetroBat, ES-DE, RetroArch, Program Files and the app-data folders.
// Then the game folders other programs already know: ES-DE's ROMs folder, RetroArch's playlists, Playnite's
// library (a plain text search of it), and each found emulator's own list of game folders (definitions'
// gameFolders). "Look harder" walks whole drives. The scan only reads; nothing changes until the user confirms
// the results (ScanCard). With --scan-home (tests) every place is looked for under that folder instead.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using static Ionea.Loc;

namespace Ionea;

public sealed class ScanEmu
{
    public string Exe, Where;
    public EmuDef Def;
    public int Missing;        // required files it lacks
    public bool Take = true;
}

public sealed class ScanFolder
{
    public string Path, System, From;
    public bool Take = true;
    public bool Capped;        // the scan stopped in this folder before its end (too many files)
    public int Depth = Scan.Depth;   // how many levels of subfolders are looked through
}

public sealed class ScanGame
{
    public string File, System;
    public bool NoEmu;         // no emulator Ionea has or found plays its system: added anyway, it asks for one at start
}

public sealed class ScanResult
{
    public List<ScanEmu> Emus = new();
    public List<ScanFolder> Folders = new();
    public List<ScanGame> Games = new();
    public string Esde;          // ES-DE's folder when it was found and none is set
    public string Chosen;        // Import a folder: the folder the user chose (null: the scan of this PC)
    public bool Deep;            // "Look harder" ran
    public bool GamesCapped;     // more games than the scan takes at once (MaxGames)
    public bool Empty => Emus.Count == 0 && Folders.Count == 0 && Games.Count == 0 && Esde == null;
}

public static class Scan
{
    public static string Home;   // tests (--scan-home): every place is under this folder

    static string Sp(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
    static string H => Home ?? Sp(Environment.SpecialFolder.UserProfile);
    static string AppData => Home != null ? Path.Combine(Home, "AppData", "Roaming") : Sp(Environment.SpecialFolder.ApplicationData);
    static string LocalAppData => Home != null ? Path.Combine(Home, "AppData", "Local") : Sp(Environment.SpecialFolder.LocalApplicationData);
    static string Documents => Home != null ? Path.Combine(Home, "Documents") : Sp(Environment.SpecialFolder.MyDocuments);
    static string ProgramData => Home != null ? Path.Combine(Home, "ProgramData") : Sp(Environment.SpecialFolder.CommonApplicationData);
    static IEnumerable<string> Desktops => Home != null ? new[] { Path.Combine(Home, "Desktop"), Path.Combine(Home, "Public", "Desktop") }
        : new[] { Sp(Environment.SpecialFolder.DesktopDirectory), Sp(Environment.SpecialFolder.CommonDesktopDirectory) };
    static IEnumerable<string> ProgramFiles => Home != null ? new[] { Path.Combine(Home, "Program Files"), Path.Combine(Home, "Program Files (x86)") }
        : new[] { Sp(Environment.SpecialFolder.ProgramFiles), Sp(Environment.SpecialFolder.ProgramFilesX86) }.Distinct();
    // Drive roots: every fixed drive, or the test's home.
    static IEnumerable<string> Roots()
    {
        if (Home != null) return new[] { Home };
        try { return DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => d.RootDirectory.FullName).ToList(); }
        catch { return new[] { Path.GetPathRoot(Sp(Environment.SpecialFolder.Windows)) }; }
    }

    // A definition's {tokens} for one program.
    public static string Expand(string pattern, string exe) => pattern
        .Replace("{exe}", exe != null ? Path.GetDirectoryName(exe) : "\0")
        .Replace("{appdata}", AppData).Replace("{localappdata}", LocalAppData)
        .Replace("{documents}", Documents).Replace("{home}", H);

    // An emulator program the scan came across: on the results when the catalog knows it and Ionea doesn't have it yet.
    static void TakeExe(EmuList emus, ScanResult r, HashSet<string> exes, string exe, string where)
    {
        if (exe == null || !exes.Add(exe) || !File.Exists(exe)) return;
        if (EmuCatalog.ForExe(exe) is not EmuDef def) return;
        if (emus.ByExe(exe) != null || emus.Data.Dismissed.Any(z => string.Equals(z, exe, StringComparison.OrdinalIgnoreCase))) return;
        if (!ProductFits(def, exe)) { Log.Write($"scan: {exe} has {def.Name}'s name but another product name; skipped"); return; }
        var found = new ScanEmu { Exe = Path.GetFullPath(exe), Def = def, Where = where };
        try { found.Missing = ReqFiles.Missing(emus.FromPreset(def, exe)); } catch (Exception caught) { Log.Debug("scan: required files", caught); }
        r.Emus.Add(found);
    }

    // Import a folder: one folder the user chose, with everything in it: the emulators and cores anywhere inside
    // (a few levels down), and its games, with the systems their folders are named for ("SNES", "PS2",
    // "Nintendo - Game Boy Advance"). Games no emulator plays yet are offered too. Nothing changes until the
    // user confirms (ScanCard), as with the scan of the PC.
    public const int FolderDepth = 5;
    public static ScanResult RunFolder(MainWindow w, string folder)
    {
        var sw = Stopwatch.StartNew();
        string root = Clean(folder);
        var r = new ScanResult { Deep = true, Chosen = root };
        if (root == null || !Directory.Exists(root)) return r;
        var exes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int dirs = 0;
        Deep(root, 0, ref dirs, (exe, where) => TakeExe(w.Emus, r, exes, exe, where), sw, FolderDepth, T("settings.chosen_folder"));
        r.Folders.Add(new ScanFolder { Path = root, System = SystemByName(Path.GetFileName(root))?.Id, From = T("settings.chosen_folder"), Depth = FolderDepth });
        var present = w.Emus.Data.Emulators.Where(e => e.Present).Select(e => EmuDefs.Of(e)).Concat(r.Emus.Select(e => e.Def)).Where(d => d != null);
        Identify(w, r, new HashSet<string>(present.SelectMany(d => d.Systems), StringComparer.OrdinalIgnoreCase));
        // Its games are listed one row per system (ScanCard); with none, the folder isn't kept as a game folder.
        if (r.Games.Count == 0) r.Folders.Clear();
        Log.Write($"import folder {root}: {r.Emus.Count} emulator(s), {r.Games.Count} game(s) ({r.Games.Count(g => g.NoEmu)} with no emulator yet) in {dirs} folders, {sw.ElapsedMilliseconds} ms");
        return r;
    }

    public static ScanResult Run(MainWindow w, bool deep)
    {
        var sw = Stopwatch.StartNew();
        var r = new ScanResult { Deep = deep };
        var emus = w.Emus;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // folders already looked in
        var exes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TakeExe(string exe, string where) => Scan.TakeExe(emus, r, exes, exe, where);
        void Look(string d, string where, int depth)
        {
            if (string.IsNullOrEmpty(d) || !Directory.Exists(d)) return;
            string full;
            try { full = Path.GetFullPath(d).TrimEnd('\\'); } catch { return; }
            if (!seen.Add(full + "|" + depth)) return;
            foreach (string f in Files(full)) if (f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) TakeExe(f, where);
            if (File.Exists(Path.Combine(full, "ES-DE.exe")) && w.Opt.EsdeDir == null) r.Esde ??= full;
            if (depth > 0) foreach (string s in Dirs(full)) Look(s, where, depth - 1);
        }

        // 1. Shortcuts in the Start menu and on the desktop.
        foreach (string sm in new[] { Path.Combine(AppData, @"Microsoft\Windows\Start Menu\Programs"), Path.Combine(ProgramData, @"Microsoft\Windows\Start Menu\Programs") })
            foreach (string lnk in Walk(sm, "*.lnk", 4, 3000)) TakeExe(Target(lnk), "Start menu");
        foreach (string desk in Desktops)
            foreach (string lnk in Walk(desk, "*.lnk", 1, 500)) TakeExe(Target(lnk), "Desktop");

        // 2. The installed-programs list (in tests: <home>\uninstall.json, [{ "InstallLocation", "DisplayIcon" }]).
        foreach (var (loc, icon) in Installed())
        {
            TakeExe(IconExe(icon), "Installed programs");
            Look(loc, "Installed programs", 1);
        }

        // 3. Package managers and emulation suites.
        foreach (string app in Dirs(Path.Combine(H, "scoop", "apps"))) Look(Path.Combine(app, "current"), "Scoop", 1);
        Look(Path.Combine(AppData, "EmuDeck", "Emulators"), "EmuDeck", 2);            // verify
        foreach (string root in Roots())
        {
            Look(Path.Combine(root, "Emulation", "tools"), "EmuDeck", 2);              // verify
            Look(Path.Combine(root, "RetroBat", "emulators"), "RetroBat", 2);
            Look(Path.Combine(root, "ES-DE"), "ES-DE", 0);
            Look(Path.Combine(root, "ES-DE", "Emulators"), "ES-DE", 2);
            Look(Path.Combine(root, "Emulators"), "Emulators folder", 2);
            Look(Path.Combine(root, "Ionea", "Emulators"), "Emulators folder", 2);
            foreach (string ra in new[] { "RetroArch", "RetroArch-Win64" }) { Look(Path.Combine(root, ra), "RetroArch", 0); Look(Path.Combine(root, ra, "cores"), "RetroArch", 0); }
        }
        foreach (string f in Paths.EmuFolders(w.Settings, w.Opt)) Look(f, "Emulators folder", 2);
        Look(Path.Combine(AppData, "RetroArch"), "RetroArch", 0);
        Look(Path.Combine(AppData, "RetroArch", "cores"), "RetroArch", 0);

        // 4. Program Files and the app-data folders: each program's own folder.
        foreach (string pf in ProgramFiles) foreach (string d in Dirs(pf)) Look(d, "Program Files", 1);
        Look(Path.Combine(LocalAppData, "Programs"), "Programs", 2);
        foreach (string d in Dirs(LocalAppData)) Look(d, "App data", 0);
        foreach (string d in Dirs(AppData)) Look(d, "App data", 0);
        // A found RetroArch: its cores.
        foreach (string ra in r.Emus.Select(e => e.Exe).Concat(emus.Data.Emulators.Select(e => e.Exe)).Where(e => Path.GetFileName(e ?? "").Equals("retroarch.exe", StringComparison.OrdinalIgnoreCase)).ToList())
            Look(Path.Combine(Path.GetDirectoryName(ra), "cores"), "RetroArch", 0);

        // 5. Look harder: whole drives (in tests the home), a few levels down.
        if (deep)
        {
            int dirs = 0;
            foreach (string root in Roots()) Deep(root, 0, ref dirs, TakeExe, sw);
            Log.Write($"scan: looked harder through {dirs} folders");
        }

        // 6. Game folders.
        string esde = w.Opt.EsdeDir ?? r.Esde;
        EsdeFolders(r, esde);
        RetroArchFolders(r, emus);
        PlayniteFolders(r);
        var present = emus.Data.Emulators.Where(e => e.Present).Select(e => (EmuDefs.Of(e), e.Exe)).Concat(r.Emus.Select(e => (e.Def, e.Exe))).ToList();
        foreach (var (def, exe) in present) EmulatorFolders(r, def, exe);
        // Folders already known are not offered again.
        var known = new HashSet<string>(NewRoms.GameRoots(w).Select(Norm), StringComparer.OrdinalIgnoreCase);
        r.Folders = r.Folders.GroupBy(f => Norm(f.Path), StringComparer.OrdinalIgnoreCase).Select(g => g.First()).Where(f => !known.Contains(Norm(f.Path))).ToList();

        // 7. Games in the found folders that are clearly one system's, and that an emulator plays.
        var systems = new HashSet<string>(present.Where(p => p.Item1 != null).SelectMany(p => p.Item1.Systems), StringComparer.OrdinalIgnoreCase);
        Identify(w, r, systems);
        Log.Write($"scan{(deep ? " (looked harder)" : "")}: {r.Emus.Count} emulator(s), {r.Folders.Count} game folder(s), {r.Games.Count} game(s){(r.Esde != null ? ", ES-DE at " + r.Esde : "")} in {sw.ElapsedMilliseconds} ms");
        return r;
    }

    // ---- emulator places ----

    static bool ProductFits(EmuDef d, string exe)
    {
        if (d.Detect?.Product is not { Count: > 0 } want || exe.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) return true;
        string have;
        try { have = FileVersionInfo.GetVersionInfo(exe).ProductName?.Trim(); } catch { return true; }
        if (string.IsNullOrEmpty(have)) return true;
        return want.Any(p => have.Contains(p, StringComparison.OrdinalIgnoreCase));
    }

    static string Target(string lnk)
    {
        try { Shortcut.Read(lnk, out string t, out _, out _, out _); return string.IsNullOrWhiteSpace(t) ? null : t; }
        catch { return null; }
    }

    static string IconExe(string icon)
    {
        if (string.IsNullOrWhiteSpace(icon)) return null;
        string s = icon.Trim().Trim('"');
        int comma = s.LastIndexOf(',');
        if (comma > 2 && !s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s[..comma].Trim().Trim('"');
        return s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? s : null;
    }

    static IEnumerable<(string loc, string icon)> Installed()
    {
        var l = new List<(string, string)>();
        if (Home != null)
        {
            string f = Path.Combine(Home, "uninstall.json");
            try
            {
                if (File.Exists(f))
                    foreach (var e in JsonDocument.Parse(File.ReadAllText(f)).RootElement.EnumerateArray())
                        l.Add((Str(e, "InstallLocation"), Str(e, "DisplayIcon")));
            }
            catch (Exception e) { Log.Write("scan: uninstall.json: " + e.Message); }
            return l;
        }
        foreach (var hive in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
            foreach (string key in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
                try
                {
                    using var k = hive.OpenSubKey(key);
                    if (k == null) continue;
                    foreach (string n in k.GetSubKeyNames())
                        try
                        {
                            using var s = k.OpenSubKey(n);
                            l.Add((s?.GetValue("InstallLocation") as string, s?.GetValue("DisplayIcon") as string));
                        }
                        catch { /* unreadable registry key */ }
                }
                catch { /* unreadable registry key */ }
        return l;
    }

    static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static readonly string[] SkipDeep = { "Windows", "$Recycle.Bin", "System Volume Information", "WinSxS", "node_modules", ".git", "Temp", "Microsoft", "WindowsApps", "Packages", "steamapps\\shadercache", "$WinREAgent" };
    static void Deep(string d, int depth, ref int dirs, Action<string, string> program, Stopwatch sw, int maxDepth = 6, string where = "Looked harder")
    {
        if (depth > maxDepth || dirs > 200_000 || sw.ElapsedMilliseconds > 90_000) return;
        dirs++;
        foreach (string f in Files(d)) if (f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || f.EndsWith("_libretro.dll", StringComparison.OrdinalIgnoreCase)) program(f, where);
        foreach (string s in Dirs(d))
        {
            string n = Path.GetFileName(s);
            if (n.StartsWith(".") || SkipDeep.Any(x => n.Equals(x, StringComparison.OrdinalIgnoreCase))) continue;
            try { if ((File.GetAttributes(s) & FileAttributes.ReparsePoint) != 0) continue; } catch { continue; }
            Deep(s, depth + 1, ref dirs, program, sw, maxDepth, where);
        }
    }

    // ---- game folders ----

    static void Folder(ScanResult r, string path, string system, string from)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        string p = Clean(path);
        if (p == null || !Directory.Exists(p)) return;
        r.Folders.Add(new ScanFolder { Path = p, System = system, From = from });
    }

    // A folder as an emulator or frontend wrote it: quotes, forward slashes, Qt's doubled backslashes.
    static string Clean(string p)
    {
        p = p.Trim().Trim('"', '\'').Replace("\\\\", "\\").Replace('/', '\\');
        if (p.Length < 3) return null;
        try { return Path.GetFullPath(Environment.ExpandEnvironmentVariables(p)).TrimEnd('\\'); } catch { return null; }
    }

    static string Norm(string p) { try { return Path.GetFullPath(p).TrimEnd('\\'); } catch { return p ?? ""; } }

    // ES-DE: its ROMDirectory (es_settings.xml; empty means ROMs beside a portable ES-DE, else in the user's home),
    // one folder per system with ES-DE's system names.
    static void EsdeFolders(ScanResult r, string esde)
    {
        var settingsFiles = new List<(string file, string defRoms)>();
        if (esde != null) settingsFiles.Add((Path.Combine(esde, "ES-DE", "settings", "es_settings.xml"), Path.Combine(esde, "ROMs")));
        settingsFiles.Add((Path.Combine(H, "ES-DE", "settings", "es_settings.xml"), Path.Combine(H, "ROMs")));
        foreach (var (file, defRoms) in settingsFiles)
        {
            if (!File.Exists(file)) continue;
            string roms = defRoms;
            try
            {
                var m = Regex.Match(File.ReadAllText(file), @"<string\s+name=""ROMDirectory""\s+value=""([^""]*)""");
                if (m.Success && m.Groups[1].Value.Trim().Length > 0)
                    roms = m.Groups[1].Value.Replace("%ESPATH%", esde ?? H).Replace("~", H);
            }
            catch (Exception caught) { Log.Debug("scan: ES-DE settings", caught); }
            if (!Directory.Exists(roms)) continue;
            foreach (string d in Dirs(roms))
            {
                string name = Path.GetFileName(d);
                var sys = EmuDefs.Systems.FirstOrDefault(s => s.Esde?.Contains(name, StringComparer.OrdinalIgnoreCase) == true);
                if (sys != null && Files(d).Length + Dirs(d).Length > 0) Folder(r, d, sys.Id, "ES-DE");
            }
            break;
        }
    }

    // RetroArch's playlists (.lpl, JSON): each game's folder, and its system from the playlist's database name.
    static void RetroArchFolders(ScanResult r, EmuList emus)
    {
        var dirs = new List<string> { Path.Combine(AppData, "RetroArch", "playlists") };
        foreach (string root in Roots()) { dirs.Add(Path.Combine(root, "RetroArch", "playlists")); dirs.Add(Path.Combine(root, "RetroArch-Win64", "playlists")); }
        foreach (var e in emus.Data.Emulators.Select(e => e.Exe).Concat(r.Emus.Select(e => e.Exe)))
            if (Path.GetFileName(e ?? "").Equals("retroarch.exe", StringComparison.OrdinalIgnoreCase)) dirs.Add(Path.Combine(Path.GetDirectoryName(e), "playlists"));
        foreach (string pl in dirs.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists).SelectMany(d => Walk(d, "*.lpl", 0, 200)))
        {
            try
            {
                if (new FileInfo(pl).Length > 32 << 20) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(pl));
                if (!doc.RootElement.TryGetProperty("items", out var items)) continue;
                foreach (var it in items.EnumerateArray())
                {
                    string path = Str(it, "path"), db = Str(it, "db_name") ?? Path.GetFileName(pl);
                    if (path == null) continue;
                    int hash = path.IndexOf('#'); if (hash > 0) path = path[..hash];
                    string sysName = Path.GetFileNameWithoutExtension(db);
                    var sys = EmuDefs.Systems.FirstOrDefault(s => string.Equals(s.Thumbs, sysName, StringComparison.OrdinalIgnoreCase));
                    string dir = Path.GetDirectoryName(Clean(path) ?? "");
                    if (dir != null) Folder(r, dir, sys?.Id, "RetroArch");
                }
            }
            catch (Exception e) { Log.Write($"scan: {Path.GetFileName(pl)}: {e.Message}"); }
        }
    }

    // Playnite keeps its library in a LiteDB file; its game paths are plain text in it. A text search finds them
    // without reading the format (verify on a real library).
    static void PlayniteFolders(ScanResult r)
    {
        foreach (string db in new[] { Path.Combine(AppData, "Playnite", "library", "games.db") })
        {
            try
            {
                if (!File.Exists(db) || new FileInfo(db).Length > 64 << 20) continue;
                string text;
                using (var fs = new FileStream(db, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) using (var sr = new StreamReader(fs, Encoding.UTF8)) text = sr.ReadToEnd();
                var exts = new HashSet<string>(EmuDefs.Systems.SelectMany(s => s.Exts), StringComparer.OrdinalIgnoreCase);
                exts.Remove(".zip"); exts.Remove(".7z");
                var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (Match m in Regex.Matches(text, @"[A-Za-z]:\\[^\x00-\x1f""<>|*?]{2,255}"))
                {
                    string p = m.Value;
                    string ext = Path.GetExtension(p);
                    if (!exts.Contains(ext) || !File.Exists(p)) continue;
                    dirs.Add(Path.GetDirectoryName(p));
                    if (dirs.Count > 200) break;
                }
                foreach (string d in dirs) Folder(r, d, null, "Playnite");
            }
            catch (Exception e) { Log.Write("scan: Playnite: " + e.Message); }
        }
    }

    // An emulator's own list of game folders (its definition's gameFolders).
    static void EmulatorFolders(ScanResult r, EmuDef def, string exe)
    {
        if (def?.GameFolders == null) return;
        foreach (var gf in def.GameFolders)
        {
            string file = Expand(gf.File ?? "", exe);
            if (file.Contains('\0') || !File.Exists(file)) continue;
            try
            {
                if (new FileInfo(file).Length > 4 << 20) continue;
                string text = File.ReadAllText(file);
                foreach (Match m in Regex.Matches(text, gf.Match, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2)))
                    if (m.Groups.Count > 1) Folder(r, m.Groups[1].Value, def.Systems.Count == 1 ? def.Systems[0] : null, def.Name);
            }
            catch (Exception e) { Log.Write($"scan: {def.Name}'s {Path.GetFileName(file)}: {e.Message}"); }
        }
    }

    // ---- games ----

    // A file is clearly a game when its header says its system (GameId.SystemOf), or when its folder is one
    // system's and the extension is that system's. Anything less clear stays for New game files.
    // Subfolders are looked through too (a folder per game, per region, per letter), up to Depth levels.
    // A playlist (.m3u) or a cue sheet (.cue/.gdi) stands for the files it names: those are not added again.
    public const int Depth = 3, MaxFilesPerFolder = 20000, MaxGames = 5000;
    static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "saves", "save", "states", "savestates", "screenshots", "media", "downloaded_media", "images", "videos",
        "manuals", "covers", "boxart", "snaps", "titles", "marquees", "thumbnails", "bios", "system", "cheats",
        "patches", "shaders", "overlays", "config", "cache",
    };

    // A folder named for a system says its games' system: its id or ES-DE name ("snes", "psx"), its name
    // ("Super Nintendo"), its libretro name with or without the maker ("Nintendo - Game Boy Advance", "Game Boy
    // Advance") or a common short name ("PS2", "Genesis"). Case, spaces and punctuation don't count. The short
    // lists' systems win over ES-DE's extra ones (EmuSystem.More). Null when the name says no system.
    static readonly Dictionary<string, string> Aliases = new()
    {
        ["ps1"] = "psx", ["playstation1"] = "psx", ["playstationone"] = "psx", ["psone"] = "psx", ["sonyplaystation"] = "psx",
        ["playstation"] = "psx", ["ps2"] = "ps2", ["ps3"] = "ps3", ["ps4"] = "ps4", ["ps5"] = "ps5", ["vita"] = "psvita", ["psv"] = "psvita",
        ["famicom"] = "nes", ["superfamicom"] = "snes", ["supernes"] = "snes", ["n64"] = "n64", ["nintendo64"] = "n64",
        ["gameboy"] = "gb", ["gameboycolor"] = "gbc", ["gameboyadvance"] = "gba", ["ds"] = "nds", ["nintendods"] = "nds",
        ["3ds"] = "3ds", ["n3ds"] = "3ds", ["nintendo3ds"] = "3ds", ["gamecube"] = "gc", ["ngc"] = "gc", ["nintendogamecube"] = "gc",
        ["nintendowii"] = "wii", ["nintendowiiu"] = "wiiu", ["nintendoswitch"] = "switch",
        ["megadrive"] = "genesis", ["segagenesis"] = "genesis", ["segamegadrive"] = "genesis", ["md"] = "genesis",
        ["dc"] = "dreamcast", ["segadreamcast"] = "dreamcast", ["playstationportable"] = "psp", ["sonypsp"] = "psp",
    };
    // Names too general to say a system (a "PC" folder holds PC games, not IBM PC disk images).
    static readonly HashSet<string> NotSystemNames = new(StringComparer.OrdinalIgnoreCase) { "pc", "windows", "ports", "games", "roms", "mess", "electron", "flash" };
    static Dictionary<string, EmuSystem> byName;
    static IReadOnlyList<EmuSystem> namesFor;

    public static string NameKey(string s) => new string((s ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    public static EmuSystem SystemByName(string folderName)
    {
        string key = NameKey(folderName);
        if (key.Length == 0 || NotSystemNames.Contains(key)) return null;
        var systems = EmuCatalog.Systems;
        if (byName == null || !ReferenceEquals(namesFor, systems))
        {
            var d = new Dictionary<string, EmuSystem>(StringComparer.Ordinal);
            void Put(string name, EmuSystem s) { string k = NameKey(name); if (k.Length > 0 && !NotSystemNames.Contains(k)) d.TryAdd(k, s); }
            foreach (var s in systems.OrderBy(s => s.More ? 1 : 0))   // the short lists' systems first: they keep a shared name
            {
                Put(s.Id, s); Put(s.Name, s);
                foreach (string e in s.Esde ?? Array.Empty<string>()) Put(e, s);
                if (s.Thumbs != null) { Put(s.Thumbs, s); int dash = s.Thumbs.IndexOf(" - ", StringComparison.Ordinal); if (dash > 0) Put(s.Thumbs[(dash + 3)..], s); }
            }
            foreach (var (alias, id) in Aliases) if (systems.FirstOrDefault(s => s.Id == id) is EmuSystem s) d[alias] = s;
            byName = d; namesFor = systems;
        }
        return byName.TryGetValue(key, out var found) ? found : null;
    }

    // An extension only one of the short lists' systems uses (.gba, .nds, .sfc, .z64): that system. Shared ones
    // (.iso, .bin, .chd, .zip) and the many ES-DE systems' extensions say nothing on their own.
    // Program and data extensions a system also uses (.app: Wii U content too; .elf, .prx: programs and plugins).
    static readonly HashSet<string> WeakExts = new(StringComparer.OrdinalIgnoreCase) { ".zip", ".7z", ".gz", ".app", ".elf", ".prx", ".dol", ".img", ".dat", ".rom", ".bin", ".iso", ".chd", ".cue", ".m3u", ".nca" };
    public static string SystemByExt(string ext)
    {
        if (string.IsNullOrEmpty(ext) || WeakExts.Contains(ext)) return null;
        var l = EmuCatalog.Systems.Where(s => !s.More && s.Exts.Contains(ext)).Take(2).ToList();
        return l.Count == 1 ? l[0].Id : null;
    }

    // Archives in a folder named for a system are its games when it is a cartridge system (ROM sets come zipped);
    // disc systems' emulators open their disc images, not archives of them.
    static readonly string[] DiscExts = { ".iso", ".cue", ".chd", ".gdi", ".cdi", ".rvz", ".wbfs", ".wud" };
    static bool Zipped(EmuSystem s) => !s.Exts.Any(x => DiscExts.Contains(x)) && s.Id is not ("switch" or "switch2" or "ps3" or "ps4" or "ps5" or "3ds" or "wiiu");

    // A game file's system as the scan sees it: its header, else its folder's name (and the extension that
    // system uses), else an extension only one system has. For a file picked by hand (Add game > Game file).
    public static string SystemOfFile(string file)
    {
        string sys = null;
        try { sys = GameId.SystemOf(file); } catch (Exception caught) { Log.Debug("scan: system of file", caught); }
        if (sys != null) return sys;
        string ext = Path.GetExtension(file).ToLowerInvariant();
        for (string d = Path.GetDirectoryName(file); d != null && sys == null; d = Path.GetDirectoryName(d))
        {
            if (SystemByName(Path.GetFileName(d)) is EmuSystem s && (ext is ".zip" or ".7z" ? Zipped(s) : s.Exts.Contains(ext))) sys = s.Id;
            if (Path.GetPathRoot(d) == d) break;
        }
        return sys ?? SystemByExt(ext);
    }

    static void Identify(MainWindow w, ScanResult r, HashSet<string> playable)
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // The library's game files, once: a check per file against the whole library is too slow for big folders.
        var have = new HashSet<string>(w.Lib.Data.Games.Where(g => g.Source == "rom" && g.Rom != null).Select(g => Norm(g.Rom)), StringComparer.OrdinalIgnoreCase);
        // Import a folder: an emulator's own folder in it holds its firmware and system files, not games.
        var emuDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (r.Chosen != null)
            foreach (string exe in r.Emus.Select(e => e.Exe).Concat(w.Emus.Data.Emulators.Select(e => e.Exe)))
                if (exe != null && Path.GetDirectoryName(Norm(exe)) is string ed && !string.Equals(ed, r.Chosen, StringComparison.OrdinalIgnoreCase)) emuDirs.Add(ed);
        foreach (var f in r.Folders)
        {
            int files = 0;
            // folderSys: the system this folder's games are for (the scan's, or a folder's name); fromName: it came from a
            // folder's name, so the user sorted these files by system and archives (.zip) count as that system's games.
            bool Go(string dir, int left, EmuSystem folderSys, bool fromName)   // false: stop (a cap was reached)
            {
                var all = Files(dir);
                var named = Named(all);   // the files a playlist or cue sheet here stands for
                var list = all.Where(x => !named.Contains(x)).ToList();
                var sub = new List<string>();
                foreach (string d in Dirs(dir))
                {
                    if (NotGames.Contains(Path.GetFileName(d)) || IsHidden(d) || emuDirs.Contains(Norm(d))) continue;
                    string eboot = Path.Combine(d, "eboot.bin"), ps3 = Path.Combine(d, "PS3_GAME", "USRDIR", "EBOOT.BIN");
                    if (File.Exists(eboot)) { if (!RomLogic.IsPs4Update(d)) list.Add(eboot); }   // PS4 game folder
                    else if (File.Exists(ps3)) list.Add(ps3);                                    // PS3 folder game
                    else if (Directory.Exists(Path.Combine(d, "code")) && Files(Path.Combine(d, "code")).FirstOrDefault(x => x.EndsWith(".rpx", StringComparison.OrdinalIgnoreCase)) is string rpx)
                        list.Add(rpx);                                                           // Wii U folder game
                    else sub.Add(d);
                }
                foreach (string file in list)
                {
                    if (++files > MaxFilesPerFolder) { f.Capped = true; Log.Write($"scan: stopped in {f.Path} after {MaxFilesPerFolder} files"); return false; }
                    if (r.Games.Count >= MaxGames) { r.GamesCapped = true; return false; }
                    string ext = Path.GetExtension(file).ToLowerInvariant();
                    string sys = null;
                    try { sys = GameId.SystemOf(file); } catch (Exception caught) { Log.Debug("scan: system of file", caught); }
                    if (sys == null && ext == ".m3u" && Refs(file).FirstOrDefault() is string disc)   // a playlist: its first disc says
                        try { sys = GameId.SystemOf(disc); } catch (Exception caught) { Log.Debug("scan: system of disc", caught); }
                    if (sys == null && folderSys != null && (ext is ".zip" or ".7z" ? fromName && Zipped(folderSys) : folderSys.Exts.Contains(ext))) sys = folderSys.Id;
                    sys ??= SystemByExt(ext);
                    if (sys == null || !taken.Add(file) || have.Contains(Norm(file)) || AddGameLogic.InInbox(file)) continue;
                    r.Games.Add(new ScanGame { File = file, System = sys, NoEmu = !playable.Contains(sys) });
                }
                if (left > 0)
                    foreach (string d in sub)
                    {
                        var s = SystemByName(Path.GetFileName(d));   // "Games\SNES\...": that folder is the SNES's
                        if (!Go(d, left - 1, s ?? folderSys, s != null || fromName)) return false;
                    }
                return true;
            }
            var top = f.System != null ? EmuCatalog.System(f.System) : null;
            bool topNamed = false;
            if (top == null && SystemByName(Path.GetFileName(f.Path)) is EmuSystem n) { top = n; topNamed = true; }
            if (!Go(f.Path, f.Depth, top, topNamed) && r.GamesCapped) return;
        }
    }

    static bool IsHidden(string d)
    {
        try { return (File.GetAttributes(d) & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0; } catch { return true; }
    }

    // The files that the playlists (.m3u) and cue sheets (.cue, .gdi) among these name, with the files those name in turn.
    public static HashSet<string> Named(IEnumerable<string> files)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string list, int depth)
        {
            foreach (string x in Refs(list))
                if (set.Add(x) && depth > 0) Add(x, depth - 1);
        }
        foreach (string x in files)
            if (Path.GetExtension(x).ToLowerInvariant() is ".m3u" or ".cue" or ".gdi") Add(x, 2);
        return set;
    }

    // The files a playlist or cue sheet names that exist, full paths. Other files: none.
    public static List<string> Refs(string file)
    {
        var l = new List<string>();
        try
        {
            string ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is not (".m3u" or ".cue" or ".gdi") || new FileInfo(file).Length > 1 << 16) return l;
            string dir = Path.GetDirectoryName(file) ?? "";
            foreach (string raw in File.ReadAllLines(file))
            {
                string line = raw.Trim();
                string name = null;
                if (ext == ".m3u") { if (line.Length > 0 && !line.StartsWith('#')) name = line; }
                else if (ext == ".cue") { var m = Regex.Match(line, @"^FILE\s+""([^""]+)""", RegexOptions.IgnoreCase); if (m.Success) name = m.Groups[1].Value; }
                else { var m = Regex.Match(line, @"^\d+\s+\d+\s+\d+\s+\d+\s+(""[^""]+""|\S+)"); if (m.Success) name = m.Groups[1].Value.Trim('"'); }
                if (name == null || name.IndexOfAny(Path.GetInvalidPathChars()) >= 0) continue;
                string full = Path.GetFullPath(Path.Combine(dir, name.Replace('/', '\\')));
                if (File.Exists(full)) l.Add(full);
            }
        }
        catch (Exception caught) { Log.Debug("scan: playlist", caught); }
        return l;
    }

    // ---- confirm ----

    // Adds what the results screen left ticked. Returns the number of games added.
    public static int Apply(MainWindow w, ScanResult r)
    {
        var added = new List<Emulator>();
        foreach (var e in r.Emus.Where(e => e.Take))
        {
            try
            {
                if (w.Emus.ByExe(e.Exe) != null) continue;
                var em = w.Emus.FromPreset(e.Def, e.Exe);
                em.Controls = true;   // Ionea manages its controls, as for every emulator; its page's switch turns it off
                w.Emus.Add(em);
                added.Add(em);
            }
            catch (Exception x) { Log.Write($"scan: {e.Exe} not added: {x.Message}"); }
        }
        if (r.Esde != null && w.Settings.Paths.Esde == null)
        {
            w.Settings.Paths.Esde = r.Esde; w.Opt.EsdeDir ??= r.Esde;
            if (!w.Opt.NoImport) EsdeImport.Run(w.Lib, r.Esde);
        }
        var folders = r.Folders.Where(f => f.Take).ToList();
        foreach (var f in folders) Paths.AddGames(w.Settings, f.Path, f.System, f.From);
        w.Settings.ScanAsked = true;
        w.Settings.Save();

        int games = 0;
        Waiting = 0;
        string icons = RomArt.IconsDir(w.Lib);
        var have = new HashSet<string>(w.Lib.Data.Games.Where(g => g.Source == "rom" && g.Rom != null).Select(g => Norm(g.Rom)), StringComparer.OrdinalIgnoreCase);
        foreach (var g in r.Games)
        {
            if (!folders.Any(f => g.File.StartsWith(f.Path + "\\", StringComparison.OrdinalIgnoreCase))) continue;
            if (!File.Exists(g.File) || !have.Add(Norm(g.File))) continue;
            // No emulator plays it yet: it is added all the same, and asks for one when it is started (Launch).
            var emu = w.Emus.ForFile(g.File).FirstOrDefault(e => e.Systems.Contains(g.System)) ?? w.Emus.Sorted().FirstOrDefault(e => e.Present && e.Systems.Contains(g.System));
            try
            {
                System.Windows.Media.Imaging.BitmapSource cover = null;
                try { cover = RomArt.Cover(g.File, icons); } catch (Exception caught) { Log.Debug("scan: game file cover", caught); }
                RomLogic.Commit(w.Lib, g.File, emu, RomLogic.NameFromFile(g.File), cover, g.System, cover != null ? "icon" : null, save: false);
                games++;
                if (emu == null) Waiting++;
            }
            catch (Exception x) { Log.Write($"scan: {g.File} not added: {x.Message}"); }
        }
        if (games > 0) w.Lib.Save();
        if (added.Count > 0) { w.Emus.UseCores(w.Lib, added); ReqFiles.SupplyAll(); }
        Log.Write($"scan confirmed: {added.Count} emulator(s), {folders.Count} game folder(s), {games} game(s), {Waiting} with no emulator yet");
        if (games > 0) Covers.Queue(w);
        return games;
    }

    // The last Apply's games that no emulator plays yet.
    public static int Waiting;

    // ---- small helpers ----

    static string[] Files(string d) { try { return Directory.GetFiles(d); } catch { return Array.Empty<string>(); } }
    static string[] Dirs(string d) { try { return Directory.Exists(d) ? Directory.GetDirectories(d) : Array.Empty<string>(); } catch { return Array.Empty<string>(); } }
    static IEnumerable<string> Walk(string d, string pattern, int depth, int max)
    {
        var l = new List<string>();
        void Go(string x, int left)
        {
            if (l.Count >= max) return;
            try { l.AddRange(Directory.GetFiles(x, pattern).Take(max - l.Count)); } catch { /* no access to this folder */ }
            if (left > 0) foreach (string s in Dirs(x)) Go(s, left - 1);
        }
        if (Directory.Exists(d)) Go(d, depth);
        return l;
    }
}
