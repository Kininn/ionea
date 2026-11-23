#nullable disable
// Native ports. A port is a PC program that plays one console
// game natively (Bloodborne's bbport, Ship of Harkinian, ...). It is on the emulator list like an emulator but has
// no systems (EmuDefs: "kind": "port"), so it never plays a game as an emulator. A game plays natively when an
// installed port lists its id:
//   - both the game's emulator and a matching port installed: the first A asks Play with (kept per profile);
//   - only one of them installed: the game starts with that one, nothing asked;
//   - the version the port needs is checked first (e.g. Bloodborne 01.09); a wrong one is refused with the reason;
//   - the first native start prepares the game (prepare.kind): "ps4-merge" lays the update over the base game as
//     hard links in data\ports\<port>\<game id> (no copy, the originals are never changed), stamped when done.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using static Ionea.Loc;

namespace Ionea;

public sealed record PortMatch(Emulator Emu, EmuDef Def, DefPortGame Game);

// What LaunchFx.Start needs for a native start.
public sealed class PortStart
{
    public Emulator Emu;
    public ProcessStartInfo Psi;
    public string GameDir;
}

public static class Ports
{
    public const string EmulatorChoice = "emulator";

    // ---- which ports play a game ----

    static readonly ConcurrentDictionary<string, (DateTime stamp, GameInfo info)> infos = new(StringComparer.OrdinalIgnoreCase);

    // The game's id and version (cached by the file's time; a header read for every menu refresh is too slow).
    // Its system: the game's own, else its emulator's first (never Ports.EmuOf: that asks this class).
    // A game whose own program is a port (no systems): the system that port plays.
    static string SystemOf(MainWindow w, Game g)
    {
        if (g.System != null) return g.System;
        var e = w.Emus.Get(g.Emulator);
        return EmuDefs.Of(e) is EmuDef d && d.IsPort ? d.Port.Games.FirstOrDefault()?.System : e?.Systems?.FirstOrDefault();
    }

    public static GameInfo Info(MainWindow w, Game g)
    {
        string sys = g == null ? null : SystemOf(w, g);
        if (g?.Source != "rom" || g.Rom == null || sys == null) return null;
        DateTime t;
        try { t = File.GetLastWriteTimeUtc(g.Rom); } catch { return null; }
        if (infos.TryGetValue(g.Rom, out var c) && c.stamp == t) return c.info;
        GameInfo i = null;
        try { i = GameId.ForGame(sys, g.Rom); } catch (Exception e) { Log.Write($"ports: reading {g.Rom}: {e.Message}"); }
        infos[g.Rom] = (t, i);
        return i;
    }

    // Installed ports (program present) that list this game.
    public static List<PortMatch> For(MainWindow w, Game g)
    {
        var l = new List<PortMatch>();
        string sys = g?.Source == "rom" ? SystemOf(w, g) : null;
        if (sys == null) return l;
        var ports = w.Emus.Data.Emulators.Where(e => e.Present && EmuDefs.Of(e)?.IsPort == true).ToList();
        if (ports.Count == 0) return l;
        string id = Info(w, g)?.Id;
        if (id == null) return l;
        foreach (var e in ports)
        {
            var d = EmuDefs.Of(e);
            var pg = d.Port.Games.FirstOrDefault(x => x.System == sys && x.Ids.Contains(id, StringComparer.OrdinalIgnoreCase));
            if (pg != null) l.Add(new PortMatch(e, d, pg));
        }
        return l;
    }

    static string ProfileKey(MainWindow w) => w.Profiles.Active?.Id ?? "";

    // The kept answer for the active profile: "emulator", a port's emulator id, or null (not asked).
    public static string Choice(MainWindow w, Game g) => g.PlayWith != null && g.PlayWith.TryGetValue(ProfileKey(w), out var c) ? c : null;

    public static void SetChoice(MainWindow w, Game g, string choice)
    {
        g.PlayWith ??= new();
        if (choice == null) g.PlayWith.Remove(ProfileKey(w)); else g.PlayWith[ProfileKey(w)] = choice;
        if (g.PlayWith.Count == 0) g.PlayWith = null;
        w.Lib.Save();
        Log.Write($"ports: \"{g.Name}\" plays with {choice ?? "(ask)"} for {w.Profiles.Active?.Name}");
    }

    public static bool EmuReady(MainWindow w, Game g) => w.Emus.Get(g.Emulator) is Emulator e && e.Present && EmuDefs.Of(e)?.IsPort != true;

    // The port this game plays with now (null: its emulator, or not decided yet while both are installed).
    public static PortMatch Native(MainWindow w, Game g)
    {
        var l = For(w, g);
        if (l.Count == 0) return null;
        string c = Choice(w, g);
        if (!EmuReady(w, g)) return l.FirstOrDefault(m => m.Emu.Id == c) ?? l[0];   // only the port: no question
        if (c == null || c == EmulatorChoice) return null;
        return l.FirstOrDefault(m => m.Emu.Id == c);
    }

    // Both installed and the profile hasn't answered yet.
    // (A kept port that has since been removed counts as not answered.)
    public static bool MustAsk(MainWindow w, Game g)
    {
        if (!EmuReady(w, g)) return false;
        var l = For(w, g);
        if (l.Count == 0) return false;
        string c = Choice(w, g);
        return c == null || (c != EmulatorChoice && !l.Any(m => m.Emu.Id == c));
    }

    // Its emulator and a native port are both installed: every start asks which (the choice
    // shows each time, the last one focused), and Game settings asks whose settings.
    public static bool Both(MainWindow w, Game g) => g?.Source == "rom" && EmuReady(w, g) && For(w, g).Count > 0;

    // A game whose next start keeps the last answer without asking: it was just answered, or it restarts.
    public static string SkipAsk;

    // "Bloodborne (bbport)" -> "bbport": the port's own name for menus that already name the game.
    public static string ShortName(PortMatch m)
    {
        var x = System.Text.RegularExpressions.Regex.Match(m.Def.Name ?? "", @"\(([^)]+)\)\s*$");
        return x.Success ? x.Groups[1].Value : m.Def.Name;
    }

    // The program a game uses right now: its port when it plays natively, else its emulator. Every place that asks
    // "which emulator runs this game" for hotkeys, controls and tweaks uses this.
    public static Emulator EmuOf(MainWindow w, Game g) => g == null ? null : Native(w, g)?.Emu ?? w.Emus.Get(g.Emulator);

    // A native game whose port runs it as another program (bbport-run.exe starts bb-probe.exe): that program's
    // name. Its window is the one taken in, and it is paused and resumed with the started one.
    // Started through its link (GameLink): the link's full path, so the watch can tell it from the starter's own
    // helpers, which run under the same name.
    public static string GameProgram(MainWindow w, Game g) => g == null || Native(w, g) is not PortMatch m ? null : GameLink(m) ?? m.Def.Port?.GameProgram;

    // The game program run through a hard link named like Ionea, when the port lets Ionea choose its path
    // ("gameProgramEnv", e.g. BB_PROBE), so Task Manager shows it in Ionea's group. Null: not possible or turned off.
    public static string GameLink(PortMatch m)
    {
        var p = m?.Def.Port;
        if (!EmbedLogic.Group || string.IsNullOrEmpty(p?.GameProgramEnv) || string.IsNullOrEmpty(p.GameProgram) || m.Emu.Exe == null) return null;
        string dir = Path.GetDirectoryName(m.Emu.Exe), exe = Path.Combine(dir, p.GameProgram);
        if (!File.Exists(exe))   // next to the starter, or one folder down (bbport: bin\bb-probe.exe)
            try { exe = Directory.EnumerateDirectories(dir).Select(d => Path.Combine(d, p.GameProgram)).FirstOrDefault(File.Exists); } catch { exe = null; }
        // Beside the starter, its link would take the starter's own link name (Launcher.exe): left as it is.
        if (exe == null || string.Equals(Path.GetDirectoryName(exe), dir, StringComparison.OrdinalIgnoreCase)) return null;
        string link = EmbedLogic.AliasFile(exe);
        return string.Equals(link, exe, StringComparison.OrdinalIgnoreCase) ? null : link;
    }

    // ---- one save for the port and the emulator ----
    // A port whose definition has "sharedSaves" listing the game's emulator writes the same save files (Bloodborne:
    // bbport's user\savedata\1\<id>\SPRJ0005 and shadPS4's user\home\1000\savedata\<id>\SPRJ0005 hold the same
    // userdata files; only each one's metadata differs, and that stays its own). Shared (the default, Game info >
    // Saves): each save folder of the game in the port's place is a link to the emulator's, in the active profile's
    // folders, so either program continues the same save. Synced at every start and end of the game with either
    // program, and when the setting changes (SaveSync.Run). Separate: the port gets its own copy again.
    public sealed record SaveShare(PortMatch Port, Emulator Emu, string PortDir, string EmuDir);

    // The installed port that shares this game's saves with its emulator, or null.
    public static SaveShare ShareOf(MainWindow w, Game g)
    {
        if (g?.Source != "rom" || w.Emus.Get(g.Emulator) is not Emulator emu || EmuDefs.Of(emu) is not EmuDef ed || ed.IsPort) return null;
        foreach (var m in For(w, g))
        {
            var s = m.Def.Port.SharedSaves;
            if (s?.Port == null || s.Emulator == null || s.Emulators?.Contains(ed.Id, StringComparer.OrdinalIgnoreCase) != true) continue;
            string id = Info(w, g)?.Id;
            if (id == null) return null;
            return new SaveShare(m, emu, SaveDir(w, m.Emu, m.Def, s.Port.Replace("{gameid}", id)), SaveDir(w, emu, ed, s.Emulator.Replace("{gameid}", id)));
        }
        return null;
    }

    // Inside one of the definition's per-profile save folders: the active profile's copy (the one ProfileSaves links
    // in at the start), else in the program's own folder.
    static string SaveDir(MainWindow w, Emulator e, EmuDef d, string rel)
    {
        var pr = w.Profiles;
        bool mine = pr.Active != null && d.Saves?.Folders?.Any(f => rel.Equals(f, StringComparison.OrdinalIgnoreCase)
            || rel.StartsWith(f.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) == true;
        return mine ? Path.Combine(pr.Folder(pr.Active), "saves", d.Id, rel) : Path.Combine(Path.GetDirectoryName(e.Exe) ?? "", rel);
    }

    public static bool SavesShared(Game g, SaveShare s) => (g.Saves ?? s.Port.Def.Port.SharedSaves.Default ?? "shared") != "separate";

    public static void SetSaves(MainWindow w, Game g, bool shared)
    {
        if (ShareOf(w, g) is not SaveShare s) return;
        string def = s.Port.Def.Port.SharedSaves.Default ?? "shared", v = shared ? "shared" : "separate";
        g.Saves = v == def ? null : v;
        w.Lib.Save();
        Log.Write($"saves: \"{g.Name}\" {v} between {s.Port.Emu.Name} and {s.Emu.Name}");
        SyncSaves(w, g, "setting");
    }

    // Never throws: a failure is logged and the game starts with the saves as they are.
    public static void SyncSaves(MainWindow w, Game g, string when)
    {
        SaveShare s;
        try { s = ShareOf(w, g); } catch (Exception e) { Log.Write("saves: " + e.Message); return; }
        if (s == null) return;
        string backups = w.Profiles.Active != null ? w.Profiles.BackupsDir : Path.Combine(w.Lib.Dir, "save-backups");
        try
        {
            foreach (string line in SaveSync.Run(s.PortDir, s.EmuDir, SavesShared(g, s), s.Port.Def.Port.SharedSaves.Skip, backups, g.Name))
                Log.Write($"saves ({when}): \"{g.Name}\" {line}");
        }
        catch (Exception e) { Log.Write($"saves ({when}) \"{g.Name}\": {e}"); }
    }

    // ---- add-ons (DLC) for the port ----
    // Bloodborne: The Old Hunters and the two hats are in shadPS4's user\addcont\CUSA00900\<label> (only sce_sys: the
    // licence; the content itself is in the 01.09 update). bbport mounts add-ons from its own user\addcont\<id>\<label>,
    // so without them a save made with The Old Hunters asks for the DLC. Linked at every native start (cheap), so an
    // add-on installed or removed later, or another profile, is right at the next start.

    // The game's add-on folders, from its emulator's definition (gameData "addons"; "…\*": each folder inside is one).
    public static List<string> AddonSources(MainWindow w, Game g)
    {
        var l = new List<string>();
        string id = Info(w, g)?.Id;
        if (id == null || w.Emus.Get(g.Emulator) is not Emulator emu || emu.Exe == null) return l;
        string root = Path.GetDirectoryName(Path.GetFullPath(emu.Exe));
        var node = EmuDefs.Known(emu)?.GameData?["addons"];
        var pats = node is System.Text.Json.Nodes.JsonArray a ? a.Select(x => x?.ToString()) : node != null ? new[] { node.ToString() } : Array.Empty<string>();
        foreach (string pat in pats.Where(p => !string.IsNullOrEmpty(p)))
        {
            bool each = pat.EndsWith("\\*") || pat.EndsWith("/*");
            string p = (each ? pat[..^2] : pat).Replace("{gameid}", id).Replace("{gamedir}", Path.GetDirectoryName(g.Rom) ?? "").Replace('/', '\\');
            string d = Path.GetFullPath(Path.IsPathRooted(p) ? p : Path.Combine(root, p));
            if (!Directory.Exists(d)) continue;
            if (!each) { l.Add(d); continue; }
            l.AddRange(Directory.GetDirectories(d).OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        }
        return l;
    }

    // Never throws: a failure is logged and the game starts without its add-ons.
    public static void SyncAddons(MainWindow w, Game g, PortMatch m, string when)
    {
        try
        {
            if (m?.Def.Port?.Addons?.To is not string to || Info(w, g)?.Id is not string id) return;
            string dir = SaveDir(w, m.Emu, m.Def, to.Replace("{gameid}", id).Replace('/', '\\'));
            foreach (string line in AddonLinks.Run(dir, AddonSources(w, g)))
                Log.Write($"add-ons ({when}): \"{g.Name}\" for {m.Def.Name}: {line}");
        }
        catch (Exception e) { Log.Write($"add-ons ({when}) \"{g.Name}\": {e.Message}"); }
    }

    // ---- versions ----

    // The PS4 update folder next to a game folder (the way shadPS4 lays it out).
    public static string Ps4UpdateDir(string gameDir)
    {
        foreach (string s in new[] { "-patch", "-UPDATE", "-update" })
            if (Directory.Exists(gameDir + s) && File.Exists(Path.Combine(gameDir + s, "sce_sys", "param.sfo"))) return gameDir + s;
        return null;
    }

    static string SfoVersion(string dir)
    {
        try
        {
            string f = Path.Combine(dir, "sce_sys", "param.sfo");
            return File.Exists(f) ? GameId.Sfo(File.ReadAllBytes(f)).GetValueOrDefault("APP_VER") : null;
        }
        catch { return null; }
    }

    // The version the game plays at: its update's when there is one, else its own.
    public static (string baseVer, string update) Versions(MainWindow w, Game g)
    {
        string b = Info(w, g)?.Version, u = null;
        if (SystemOf(w, g) == "ps4" && RomLogic.IsEboot(g.Rom) && Ps4UpdateDir(Path.GetDirectoryName(g.Rom)) is string ud) u = SfoVersion(ud);
        return (b, u);
    }

    // null when the port takes this copy, else the reason for the user.
    public static string Why(MainWindow w, PortMatch m, Game g)
    {
        if (g.Rom == null || !File.Exists(g.Rom)) return T("errors.the_game_file_is_missing");
        if (m.Game.Versions == null || m.Game.Versions.Count == 0) return null;
        var (b, u) = Versions(w, g);
        string have = u ?? b;
        if (have != null && m.Game.Versions.Contains(have)) return null;
        string need = string.Join(" or ", m.Game.Versions);
        string name = m.Game.Name ?? g.Name;
        string now = have == null ? "its version can't be read" : u == null ? $"this copy is {b} with no update" : $"this copy is {b} with update {u}";
        return $"{m.Def.Name} needs {name} {need}. {char.ToUpper(now[0]) + now[1..]}. Add the update in Game info first.";
    }

    // ---- the start ----

    // Called by LaunchFx.Start for every emulator game before its own start. True: handled here (a question, a
    // card, preparing, or the native start itself); false: start it with its emulator as always.
    public static bool Route(MainWindow w, Game g, Rect from, BitmapSource cover)
    {
        if (g.Source != "rom") return false;
        bool skip = SkipAsk == g.Id;
        SkipAsk = null;
        if (Both(w, g) && (!skip || MustAsk(w, g)))
        {
            new PlayWithCard(w, g, () => { SkipAsk = g.Id; w.Launch.Start(g, from, cover); }).Open();
            return true;
        }
        var m = Native(w, g);
        if (m == null) return false;
        if (Why(w, m, g) is string why)
        {
            Log.Write($"ports: {m.Def.Name} refused \"{g.Name}\": {why}");
            new MessageCard(w, T("errors.can_t_play_natively"), why).Open();
            return true;
        }
        if (Prepared(w, m, g, out _) is string dir) { w.Launch.Start(g, from, cover, StartFor(m, g, dir)); return true; }
        new PreparingCard(w, g, m, dir2 => w.Launch.Start(g, from, cover, StartFor(m, g, dir2))).Open();
        return true;
    }

    static PortStart StartFor(PortMatch m, Game g, string dir)
    {
        string eboot = Path.Combine(dir, Path.GetFileName(g.Rom));
        string a = (m.Emu.Args ?? m.Def.Args ?? "").Replace("{gamedir}", dir).Replace("{rom}", eboot);
        var psi = new ProcessStartInfo(EmbedLogic.Alias(m.Emu), EmbedLogic.Windowed(m.Emu, a))
        {
            UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(m.Emu.Exe),
        };
        foreach (string v in m.Def.Port.GameDirEnv ?? new()) psi.Environment[v] = dir;
        if (GameLink(m) is string link) { psi.Environment[m.Def.Port.GameProgramEnv] = link; Log.Write($"ports: {m.Def.Port.GameProgram} runs as {link}"); }
        return new PortStart { Emu = m.Emu, Psi = psi, GameDir = dir };
    }

    // ---- preparing ----

    // Hard links only work on one drive, so a game on another drive than Ionea's data is prepared on its own drive.
    public static string PreparedRoot(MainWindow w, string gameDir)
    {
        string data = Path.GetFullPath(w.Opt.DataDir), game = Path.GetFullPath(gameDir);
        return string.Equals(Path.GetPathRoot(data), Path.GetPathRoot(game), StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(data, "ports") : Path.Combine(Path.GetPathRoot(game), "Ionea", "Ports");
    }

    static string PreparedDir(MainWindow w, PortMatch m, Game g) =>
        Path.Combine(PreparedRoot(w, Path.GetDirectoryName(g.Rom)), m.Def.Id, Info(w, g)?.Id ?? g.Id);
    const string StampName = "ionea-prepared.txt";

    // Where the prepared game is, or null when it must be prepared (first start, or the game or its update changed).
    public static string Prepared(MainWindow w, PortMatch m, Game g, out string expected)
    {
        string gameDir = Path.GetDirectoryName(g.Rom);
        string kind = m.Def.Port.Prepare?.Kind;
        expected = null;
        if (kind == null) return gameDir;   // nothing to prepare: it reads the game where it is
        string dir = PreparedDir(w, m, g);
        expected = StampText(w, m, g);
        try
        {
            string stamp = Path.Combine(dir, StampName);
            if (File.Exists(stamp) && File.ReadAllText(stamp) == expected && ProofOk(m, dir)) return dir;
        }
        catch (Exception caught) { Log.Debug("ports: prepared stamp", caught); }
        return null;
    }

    static bool ProofOk(PortMatch m, string dir) => m.Def.Port.Prepare?.Proof is not string p || File.Exists(Path.Combine(dir, p));

    static string StampText(MainWindow w, PortMatch m, Game g)
    {
        string gameDir = Path.GetDirectoryName(g.Rom);
        string ud = SystemOf(w, g) == "ps4" ? Ps4UpdateDir(gameDir) : null;
        var (b, u) = Versions(w, g);
        static string T(string f) { try { return File.GetLastWriteTimeUtc(f).Ticks.ToString(); } catch { return "0"; } }
        return $"port={m.Def.Id}\nkind={m.Def.Port.Prepare?.Kind}\nbase={gameDir}\nbaseVersion={b}\nbaseEboot={T(g.Rom)}\n" +
               $"update={ud}\nupdateVersion={u}\nupdateEboot={(ud == null ? "" : T(Path.Combine(ud, "eboot.bin")))}\n";
    }

    // Does the work (off the UI thread). Returns the prepared folder, or throws with a reason for the user.
    public static string Prepare(MainWindow w, PortMatch m, Game g, Action<string> status)
    {
        string kind = m.Def.Port.Prepare?.Kind;
        if (kind != "ps4-merge") throw new InvalidOperationException($"{m.Def.Name} asks for a preparing step Ionea doesn't know (\"{kind}\").");
        Prepared(w, m, g, out string expected);
        string gameDir = Path.GetDirectoryName(g.Rom);
        string ud = Ps4UpdateDir(gameDir);
        string dir = PreparedDir(w, m, g);
        string part = dir + ".part";
        Log.Write($"ports: preparing \"{g.Name}\" for {m.Def.Name}: {gameDir}{(ud != null ? " + " + ud : "")} -> {dir}");
        // Only Ionea's own prepared folders (data\ports, or <drive>\Ionea\Ports) are removed; they hold hard links, so removing them never
        // touches the game's own files.
        if (Directory.Exists(part)) Directory.Delete(part, true);
        int n = Link(gameDir, part, status, "game");
        if (ud != null) n += Link(ud, part, status, "update");
        if (!ProofOk(m, part)) throw new InvalidOperationException($"The prepared game has no {m.Def.Port.Prepare.Proof}.");
        File.WriteAllText(Path.Combine(part, StampName), expected);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.Move(part, dir);
        Log.Write($"ports: prepared \"{g.Name}\" ({n} files linked)");
        return dir;
    }

    // Hard links of every file in src inside dest; a file already there (the base game's, under the update) is replaced.
    static int Link(string src, string dest, Action<string> status, string what)
    {
        int n = 0;
        foreach (string f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            string to = Path.Combine(dest, Path.GetRelativePath(src, f));
            Directory.CreateDirectory(Path.GetDirectoryName(to));
            if (File.Exists(to)) File.Delete(to);   // a link from the base game: only that name goes
            if (!CreateHardLink(to, f, IntPtr.Zero))
            {
                int err = Marshal.GetLastWin32Error();
                throw new InvalidOperationException(err == 17
                    ? "The game is on another drive than Ionea's data folder. Move the game to the same drive first."
                    : $"Couldn't link {Path.GetFileName(f)} (error {err}).");
            }
            if (++n % 200 == 0) status?.Invoke($"{what}: {n} files");
        }
        return n;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateHardLink(string newName, string existing, IntPtr sec);
}

// The file work behind Ports.SyncSaves (no window, no library: the test drives it with made-up folders too).
// portDir / emuDir: the folders holding the game's save folders on each side (e.g. ...\CUSA00900).
// Shared:
//   - the shared save gets a dated copy in save-backups the first time each day, before anything else (as core saves);
//   - a save folder only the port has moves to the emulator's place and is linked back;
//   - both have a real copy (the first time): the newer one (by its newest file) is kept as the shared save. The
//     port's own copy always goes to a dated backup, and the emulator's too when the port's replaces it. An older
//     save never overwrites a newer one;
//   - every other save folder of the emulator's gets a link in the port's place.
// Separate: every link in the port's place becomes the port's own copy of the shared save; real folders are never touched.
public static class SaveSync
{
    public static List<string> Run(string portDir, string emuDir, bool shared, List<string> skip, string backups, string game)
    {
        var said = new List<string>();
        string name = string.Concat((game ?? "game").Split(Path.GetInvalidFileNameChars())).Trim();
        string stamp = DateTime.Now.ToString("yyyy-MM-dd HHmmss");
        if (!shared)
        {
            if (!Directory.Exists(portDir)) return said;
            foreach (var d in new DirectoryInfo(portDir).GetDirectories())
            {
                if (!IsLink(d)) continue;   // the port's own save: kept as it is
                string target = Plain(d.LinkTarget);
                Directory.Delete(d.FullName);   // only the link; the shared save stays
                if (target != null && Directory.Exists(target)) { Profiles.CopyTree(target, d.FullName); said.Add($"{d.Name}: separate, the port has its own copy of the shared save now"); }
                else said.Add($"{d.Name}: separate, a link to a missing save removed");
            }
            return said;
        }
        void Daily()   // before anything changes, and again after for a save that just moved over from the port
        {
            if (!Directory.Exists(emuDir)) return;
            foreach (var e in new DirectoryInfo(emuDir).GetDirectories())
            {
                if (IsLink(e) || !Directory.EnumerateFiles(e.FullName, "*", SearchOption.AllDirectories).Any()) continue;
                string day = Path.Combine(backups, $"{name} {e.Name} {DateTime.Now:yyyy-MM-dd}");
                if (!Directory.Exists(day)) { Profiles.CopyTree(e.FullName, day); said.Add($"{e.Name}: today's backup in {day}"); }
            }
        }
        Daily();
        if (Directory.Exists(portDir))
            foreach (var d in new DirectoryInfo(portDir).GetDirectories())
            {
                if (Skipped(d.Name, skip)) continue;
                string target = Path.Combine(emuDir, d.Name);
                if (IsLink(d))
                {
                    if (ProfileSaves.Same(d.LinkTarget, target) && Directory.Exists(target)) continue;
                    Directory.Delete(d.FullName);   // a link to another place or to nothing: linked again below
                    said.Add($"{d.Name}: an old link removed");
                    continue;
                }
                Directory.CreateDirectory(emuDir);
                if (!Directory.Exists(target))
                {
                    MoveDir(d.FullName, target);
                    said.Add($"{d.Name}: the port's save is the shared one now");
                }
                else
                {
                    DateTime pt = Newest(d.FullName), et = Newest(target);
                    Directory.CreateDirectory(backups);
                    if (pt > et)
                    {
                        string old = Path.Combine(backups, $"{name} {d.Name} emulator {stamp}");
                        Profiles.CopyTree(target, old);
                        CopyOver(d.FullName, target);
                        said.Add($"{d.Name}: the port's save is newer ({pt.ToLocalTime():yyyy-MM-dd HH:mm} > {et.ToLocalTime():yyyy-MM-dd HH:mm}) and is the shared one now; the emulator's is kept in {old}");
                    }
                    else said.Add($"{d.Name}: the emulator's save is newer or the same ({et.ToLocalTime():yyyy-MM-dd HH:mm} >= {pt.ToLocalTime():yyyy-MM-dd HH:mm}) and is kept");
                    string mine = Path.Combine(backups, $"{name} {d.Name} port {stamp}");
                    MoveDir(d.FullName, mine);
                    said.Add($"{d.Name}: the port's own copy is kept in {mine}");
                }
                ProfileSaves.Junction(d.FullName, target);
                said.Add($"{d.Name}: linked to {target}");
            }
        if (Directory.Exists(emuDir))
            foreach (var e in new DirectoryInfo(emuDir).GetDirectories())
            {
                if (IsLink(e) || Skipped(e.Name, skip)) continue;
                string p = Path.Combine(portDir, e.Name);
                if (Directory.Exists(p) || File.Exists(p)) continue;
                Directory.CreateDirectory(portDir);
                ProfileSaves.Junction(p, e.FullName);
                said.Add($"{e.Name}: linked to {e.FullName}");
            }
        Daily();
        return said;
    }

    // Directory.Move only works on one drive. Across drives: copy, check every file arrived, then delete the original.
    public static void MoveDir(string from, string to)
    {
        if (string.Equals(Path.GetPathRoot(Path.GetFullPath(from)), Path.GetPathRoot(Path.GetFullPath(to)), StringComparison.OrdinalIgnoreCase))
        {
            Directory.Move(from, to);
            return;
        }
        static (int n, long bytes) Measure(string dir)
        {
            var files = new DirectoryInfo(dir).GetFiles("*", SearchOption.AllDirectories);
            return (files.Length, files.Sum(f => f.Length));
        }
        var before = Measure(from);
        Profiles.CopyTree(from, to);
        var after = Measure(to);
        if (after != before) throw new IOException($"copying {from} to {to} left {after.n} files ({after.bytes} bytes) of {before.n} ({before.bytes} bytes); the original is kept");
        Directory.Delete(from, true);
    }

    static bool IsLink(FileSystemInfo f) => f.Attributes.HasFlag(FileAttributes.ReparsePoint);
    static string Plain(string p) => p == null ? null : p.StartsWith(@"\??\") ? p[4..] : p;

    static bool Skipped(string name, List<string> skip) => skip?.Any(s =>
        System.Text.RegularExpressions.Regex.IsMatch(name, "^" + System.Text.RegularExpressions.Regex.Escape(s).Replace(@"\*", ".*") + "$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase)) == true;

    // The save's own time: its newest file directly inside (the game's data), not the metadata in sub-folders.
    public static DateTime Newest(string dir) =>
        Directory.EnumerateFiles(dir).Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max();

    static void CopyOver(string from, string to)
    {
        foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            string t = Path.Combine(to, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(t));
            File.Copy(f, t, true);
        }
    }
}

// The file work behind Ports.SyncAddons (no window: the test drives it with made-up folders too). In dir, every add-on
// folder in sources gets a junction with its name. A link Ionea made earlier that points elsewhere or to nothing is
// made again or removed; only links are ever removed (never a real folder, never through a link).
public static class AddonLinks
{
    public static List<string> Run(string dir, IReadOnlyCollection<string> sources)
    {
        var said = new List<string>();
        var want = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string s in sources) want.TryAdd(Path.GetFileName(s.TrimEnd('\\')), s);
        if (Directory.Exists(dir))
            foreach (var d in new DirectoryInfo(dir).GetDirectories())
            {
                if (!d.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;   // a real folder: not Ionea's, kept
                if (want.TryGetValue(d.Name, out string t) && ProfileSaves.Same(d.LinkTarget, t)) continue;
                Directory.Delete(d.FullName);   // only the link
                said.Add($"{d.Name}: an old link removed");
            }
        foreach (var (name, src) in want)
        {
            string p = Path.Combine(dir, name);
            if (Directory.Exists(p) || File.Exists(p))
            {
                if (!File.GetAttributes(p).HasFlag(FileAttributes.ReparsePoint)) said.Add($"{name}: a real folder is there, kept");
                continue;
            }
            Directory.CreateDirectory(dir);
            ProfileSaves.Junction(p, src);
            said.Add($"{name}: linked to {src}");
        }
        return said;
    }
}

// Play with: the game's emulator or its native port. Asked at every start while both are installed; the
// last answer (kept per profile) is focused.
public sealed class PlayWithCard : MenuCard
{
    public PlayWithCard(MainWindow w, Game g, Action then) : base(w, T("settings.play_with"), 760)
    {
        var emu = w.Emus.Get(g.Emulator);
        string cur = Ports.Choice(w, g);
        if (emu != null && emu.Present && EmuDefs.Of(emu)?.IsPort != true)
            WithIcon(AddRow(emu.Name, () => cur == Ports.EmulatorChoice ? T("errors.last_played") : "",
                activate: () => { Close(false); Ports.SetChoice(w, g, Ports.EmulatorChoice); then?.Invoke(); }), "gear");
        foreach (var m in Ports.For(w, g))
        {
            if (cur == m.Emu.Id) Focus = Rows.Count;
            WithIcon(AddRow(T("errors.native") + m.Def.Name, () => cur == m.Emu.Id ? T("errors.last_played") : "",
                activate: () => { Close(false); Ports.SetChoice(w, g, m.Emu.Id); then?.Invoke(); }), "play");
        }
    }
}

// Preparing <game>…: the first native start lays out the game's files for the port.
public sealed class PreparingCard : Card
{
    readonly System.Windows.Controls.TextBlock status;
    bool cancelled, done;

    public PreparingCard(MainWindow w, Game g, PortMatch m, Action<string> then) : base(w, T("errors.preparing", g.Name), 820)
    {
        var spin = LaunchFx.Spinner(70);
        spin.HorizontalAlignment = HorizontalAlignment.Center;
        spin.Margin = new Thickness(0, 10, 0, 24);
        Body.Children.Add(spin);
        status = Note(m.Def.Name, 26, new Thickness(4, 0, 0, 0));
        status.TextAlignment = TextAlignment.Center;
        Task.Run(() =>
        {
            string dir = null, err = null;
            try { dir = Ports.Prepare(w, m, g, s => w.Dispatcher.BeginInvoke(() => status.Text = s)); }
            catch (Exception e) { err = e.Message; Log.Write($"ports: preparing \"{g.Name}\" failed: {e.Message}"); }
            w.Dispatcher.BeginInvoke(() =>
            {
                done = true;
                if (cancelled) return;
                Close(false);
                if (err != null) new MessageCard(w, T("errors.couldn_t_prepare", g.Name), err).Open();
                else then(dir);
            });
        });
    }

    // B leaves the card; the work finishes by itself (a half-made folder is made again next time).
    public override void OnAct(Act a)
    {
        if (a != Act.Back || done) return;
        cancelled = true;
        Close();
    }
}
