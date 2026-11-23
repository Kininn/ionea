#nullable disable
// Game info: one screen per game, from the hold menu and the
// Esc menu. The game's version and its installed update ("01.00 → 01.09"), its add-ons by name, its id, region,
// size, where it is stored, and the profile's play time. "Add update or add-on" installs a file from the user's
// own dump; "Change cover" picks Ionea's cover image.
// Where an emulator keeps updates and add-ons, and how they are installed, is in its definition ("gameData"):
//   read     how an update / add-on folder is read: "sfo" (PlayStation param.sfo), "wiiu-meta" (meta\meta.xml),
//            "3ds-tmd" (content\*.tmd), "switch" (the .nsp files in {content})
//   update   folders that may hold the update (the first one there counts)
//   addons   folders that hold add-ons; "…\*" means each folder inside is one add-on
//   install  {kind: "ps4-pkg" | "emulator" | "wiiu-folder" | "switch-keep", …}
//   mods     folders that hold the emulator's mods for the game, "…\*" each folder inside is one (yuzu family:
//            user\load\{gameid}\<name>, the name its Add-Ons list shows); listed after the add-ons as "(mod)"
//   modsOff  the emulator's config that names the mods turned off (yuzu family: [DisabledAddOns] in qt-config.ini)
// Paths: {gamedir} the game's own folder, {gameid} its id, {low} the id's last 8 hex digits (lower case),
// {content} Ionea's folder for this game's Switch files (data\game-content\switch\<id>); a relative path is
// inside the emulator's folder. Every path and format here is from public notes or memory: verify.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using static Ionea.Loc;

namespace Ionea;

public sealed class GameFacts
{
    public string System = "", Console, EmuName;
    public Emulator Emu;
    public JsonObject Data;            // the definition's gameData (null: no updates section)
    public string Id, Region, Base, Update, UpdateDir, Read, GameDir;
    public bool Section, NeedsKeys;
    public string InstallKind;
    public readonly List<string> Addons = new();
    public readonly List<(string Name, bool Off)> Mods = new();
    public long Size = -1;
    public string StoredOn;
    public DateTime? LastPlayed;
    public double Seconds;

    public string Version => NeedsKeys ? T("info.version_unknown")
        : Base == null && Update == null ? T("info.unknown")
        : Update == null ? Base
        : $"{Base ?? "?"} → {Update}";
    public string Low => Id != null && Id.Length >= 8 ? Id[^8..].ToLowerInvariant() : null;
}

public static class GameContent
{
    // Consoles whose games are never updated: the header's revision byte is shown, no updates section.
    static readonly HashSet<string> Revisions = new() { "gb", "gbc", "gba", "nds", "gc", "wii" };

    public static GameFacts Gather(MainWindow w, Game g)
    {
        var f = new GameFacts { Emu = w.Emus.Get(g.Emulator) };
        f.EmuName = f.Emu?.Name;
        f.System = g.Source == "rom" ? InputMap.SystemOf(w, g) : "";
        f.Console = g.Source == "steam" ? "Steam" : g.Source == "rom" ? InputMap.SystemName(f.System) : "PC";
        f.LastPlayed = w.Profiles.LastPlayed(g) ?? g.LastPlayed;
        f.Seconds = w.Profiles.Seconds(g.Id);
        if (g.Source != "rom" || string.IsNullOrEmpty(g.Rom)) return f;

        f.StoredOn = Storage.PlaceOf(w, g.Rom)?.Name ?? Path.GetDirectoryName(g.Rom);
        f.GameDir = GameDir(f.System, g.Rom);
        try
        {
            f.Size = RomLogic.IsEboot(g.Rom) ? Uninstall.Size(Uninstall.Targets(g))
                : f.GameDir != Path.GetDirectoryName(g.Rom) ? Uninstall.Size(new List<string> { f.GameDir })
                : File.Exists(g.Rom) ? Storage.SizeOf(g) : -1;
        }
        catch (Exception caught) { Log.Debug("game info: size", caught); }

        f.Data = f.Emu?.Present == true ? EmuDefs.Known(f.Emu)?.GameData : null;
        f.Section = f.Data != null && !Revisions.Contains(f.System);
        f.Read = Text(f.Data?["read"]);
        f.InstallKind = f.Section ? Text(f.Data?["install"]?["kind"]) : null;

        if (f.System is "switch") { Switch(w, g, f); Mods(w, f); return f; }

        var gi = GameId.ForGame(f.System, g.Rom);
        f.Id = gi?.Id; f.Region = gi?.Region;
        f.Base = Shown(f.System, gi?.Version);
        if (!f.Section || f.Id == null) return f;

        foreach (var d in Folders(w, f, f.Data["update"]))
        {
            if (!Directory.Exists(d) || Inside(g.Rom, d)) continue;
            var (ver, _) = ReadFolder(f.Read, d);
            if (ver != null) { f.Update = ver; f.UpdateDir = d; break; }
        }
        foreach (var d in AddonFolders(w, f))
        {
            if (Inside(g.Rom, d)) continue;
            var (_, name) = ReadFolder(f.Read, d);
            f.Addons.Add(name == null || name == gi?.Title ? "Add-on content" : name);
        }
        Mods(w, f);
        return f;
    }

    // The game's mods: one folder each in the definition's "mods" folders, by folder name, and whether the
    // emulator has it turned off ("modsOff").
    static void Mods(MainWindow w, GameFacts f)
    {
        if (!f.Section || f.Id == null || f.Data?["mods"] == null) return;
        try
        {
            var off = ModsOff(Resolve(w, f, Text(f.Data["modsOff"])), f.Id);
            foreach (var p in List(f.Data["mods"]))
            {
                bool each = p.EndsWith("\\*") || p.EndsWith("/*");
                var d = Resolve(w, f, each ? p[..^2] : p);
                if (d == null || !Directory.Exists(d)) continue;
                foreach (var sub in each ? Directory.GetDirectories(d).OrderBy(x => x, StringComparer.OrdinalIgnoreCase) : new[] { d }.AsEnumerable())
                    f.Mods.Add((Path.GetFileName(sub), off.Contains(Path.GetFileName(sub))));
            }
        }
        catch (Exception e) { Log.Write($"game info: mods: {e.Message}"); }
    }

    // yuzu family: [DisabledAddOns] has, for each game, n\title_id=<the id in decimal> and n\disabled\m\d=<name>.
    public static HashSet<string> ModsOff(string config, string gameId)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (config == null || !File.Exists(config) || !ulong.TryParse(gameId, System.Globalization.NumberStyles.HexNumber, null, out ulong id)) return set;
        string t = File.ReadAllText(config);
        var start = Regex.Match(t, @"(?m)^\[DisabledAddOns\][ \t]*\r?$");
        if (!start.Success) return set;
        int from = start.Index + start.Length;
        var next = Regex.Match(t[from..], @"(?m)^\[");
        string sec = next.Success ? t.Substring(from, next.Index) : t[from..];
        string dec = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (Match m in Regex.Matches(sec, @"(?m)^(\d+)\\title_id[ \t]*=[ \t]*(\d+)[ \t]*\r?$"))
        {
            if (m.Groups[2].Value != dec) continue;
            foreach (Match d in Regex.Matches(sec, @"(?m)^" + m.Groups[1].Value + @"\\disabled\\\d+\\d[ \t]*=[ \t]*(.*?)[ \t]*\r?$"))
            {
                string n = d.Groups[1].Value;
                if (n.Length >= 2 && n[0] == '"' && n[^1] == '"') n = n[1..^1];
                if (n.Length > 0) set.Add(n);
            }
        }
        return set;
    }

    // The folder the game is: PS4 the eboot's folder, Wii U the one holding code\, PS3 the one holding PS3_GAME.
    static string GameDir(string system, string rom)
    {
        string dir = Path.GetDirectoryName(rom);
        if (system == "wiiu" && Path.GetFileName(dir).Equals("code", StringComparison.OrdinalIgnoreCase)) return Path.GetDirectoryName(dir);
        if (system == "ps3")
            for (string d = dir; d != null && d.Length > 3; d = Path.GetDirectoryName(d))
                if (Path.GetFileName(d).Equals("PS3_GAME", StringComparison.OrdinalIgnoreCase)) return Path.GetDirectoryName(d);
        return dir;
    }

    static bool Inside(string file, string dir)
    {
        try { return Path.GetFullPath(file).StartsWith(Path.GetFullPath(dir).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    // A version as the console shows it.
    static string Shown(string system, string v)
    {
        if (string.IsNullOrEmpty(v)) return null;
        if (Revisions.Contains(system)) return v == "0" ? T("settings.original") : T("info.rev", v);
        return system is "wiiu" or "3ds" ? "v" + v : v;
    }

    static string Text(JsonNode n) { try { return n?.GetValue<string>(); } catch { return null; } }
    static IEnumerable<string> List(JsonNode n) =>
        n is JsonArray a ? a.Select(Text).Where(s => s != null) : Text(n) is string s ? new[] { s } : Array.Empty<string>();

    public static string ContentDir(MainWindow w, string system, string id) => Path.Combine(w.Opt.DataDir, "game-content", system, id);

    // A gameData path with its {…} filled in; relative ones are inside the emulator's folder (null if one leaves it).
    static string Resolve(MainWindow w, GameFacts f, string pattern)
    {
        if (f.Emu?.Exe == null || pattern == null) return null;
        string p = pattern.Replace("{gamedir}", f.GameDir ?? "").Replace("{gameid}", f.Id ?? "").Replace("{low}", f.Low ?? "")
                          .Replace("{content}", f.Id == null ? "" : ContentDir(w, f.System, f.Id)).Replace('/', '\\');
        if (Path.IsPathRooted(p)) return Path.GetFullPath(p);
        string root = Path.GetFullPath(Path.GetDirectoryName(f.Emu.Exe)).TrimEnd('\\');
        string t = Path.GetFullPath(Path.Combine(root, p));
        return t.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase) ? t : null;
    }
    static IEnumerable<string> Folders(MainWindow w, GameFacts f, JsonNode n) => List(n).Select(p => Resolve(w, f, p)).Where(p => p != null);

    static IEnumerable<string> AddonFolders(MainWindow w, GameFacts f)
    {
        foreach (var p in List(f.Data?["addons"]))
        {
            bool each = p.EndsWith("\\*") || p.EndsWith("/*");
            var d = Resolve(w, f, each ? p[..^2] : p);
            if (d == null || !Directory.Exists(d)) continue;
            if (!each) { yield return d; continue; }
            foreach (var sub in Directory.GetDirectories(d).OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) yield return sub;
        }
    }

    // The version and name in an update / add-on folder.
    static (string ver, string name) ReadFolder(string read, string dir)
    {
        try
        {
            switch (read)
            {
                case "sfo":
                {
                    string sfo = new[] { Path.Combine(dir, "sce_sys", "param.sfo"), Path.Combine(dir, "PARAM.SFO") }.FirstOrDefault(File.Exists);
                    if (sfo == null) return (null, null);
                    var d = GameId.Sfo(File.ReadAllBytes(sfo));
                    string name = d.GetValueOrDefault("TITLE") is string t ? Regex.Replace(t, @"\s+", " ").Trim() : null;
                    return (d.GetValueOrDefault("APP_VER") ?? d.GetValueOrDefault("VERSION"), name);
                }
                case "wiiu-meta":
                {
                    string meta = Path.Combine(dir, "meta", "meta.xml");
                    if (!File.Exists(meta)) return (null, null);
                    var x = System.Xml.Linq.XDocument.Load(meta).Root;
                    string v = x?.Element("title_version")?.Value?.Trim();
                    return (string.IsNullOrEmpty(v) ? null : "v" + v, x?.Element("longname_en")?.Value?.Trim());
                }
                case "3ds-tmd":
                {
                    string tmd = Directory.Exists(Path.Combine(dir, "content")) ? Directory.GetFiles(Path.Combine(dir, "content"), "*.tmd").OrderBy(s => s).FirstOrDefault() : null;
                    var v = tmd == null ? null : TmdVersion(File.ReadAllBytes(tmd));
                    return (v == null ? null : "v" + v, null);
                }
            }
        }
        catch (Exception e) { Log.Write($"game info: {dir}: {e.Message}"); }
        return (null, null);
    }

    // 3DS title metadata: signature type (big-endian) at 0, the header after the signature and its padding,
    // the title version at 0x9C in the header (big-endian).
    public static int? TmdVersion(byte[] b)
    {
        if (b.Length < 4) return null;
        uint type = (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
        int head = (type & 0xFFFF) switch { 0 or 3 => 0x240, 1 or 4 => 0x140, 2 or 5 => 0x80, _ => -1 };
        if (type >> 16 != 1 || head < 0 || b.Length < head + 0x9E) return null;
        return b[head + 0x9C] << 8 | b[head + 0x9D];
    }

    // ---- Switch: the game file and the .nsp files kept in Ionea's folder for it ----
    public static SwitchKeys Keys(GameFacts f)
    {
        if (f.Emu?.Present != true) return null;
        var files = ReqFiles.Of(f.Emu);
        string Path(string check) => files.FirstOrDefault(x => x.Check == check) is DefFile d ? ReqFiles.Target(f.Emu, d) : null;
        try { return SwitchKeys.Load(Path("switch-keys"), Path("switch-titlekeys")); } catch { return null; }
    }

    static void Switch(MainWindow w, Game g, GameFacts f)
    {
        var keys = Keys(f);
        if (keys == null) { f.NeedsKeys = true; return; }
        var titles = File.Exists(g.Rom) ? SwitchRead.Read(g.Rom, keys) : new();
        var game = titles.FirstOrDefault(t => t.Type == SwitchRead.Game);
        if (game == null) { Log.Write($"game info: {Path.GetFileName(g.Rom)}: {SwitchRead.LastError ?? "no game in it"}"); return; }
        f.Id = game.IdText; f.Base = game.Shown;
        var updates = titles.Where(t => t.Type == SwitchRead.Update).ToList();
        if (f.Section)
            foreach (var (file, t) in KeptSwitch(w, f, keys))
            {
                if (t.Type == SwitchRead.Update) updates.Add(t);
                else if (t.Type == SwitchRead.Addon) f.Addons.Add(AddonName(file));
            }
        var u = updates.OrderByDescending(t => t.Version).FirstOrDefault();
        if (u != null) f.Update = u.Shown;
    }

    // The .nsp files in the game's folder in Ionea, with what each holds (only this game's).
    static List<(string file, SwitchTitle t)> KeptSwitch(MainWindow w, GameFacts f, SwitchKeys keys)
    {
        var l = new List<(string, SwitchTitle)>();
        string dir = ContentDir(w, f.System, f.Id);
        if (!Directory.Exists(dir) || !ulong.TryParse(f.Id, System.Globalization.NumberStyles.HexNumber, null, out ulong id)) return l;
        foreach (var file in Directory.GetFiles(dir, "*.nsp").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            foreach (var t in SwitchRead.Read(file, keys))
                if (Belongs(t, id)) l.Add((file, t));
        return l;
    }

    // Updates: the game's id + 0x800; add-ons: the game's id + 0x1000 + n (and the game named in their meta).
    static bool Belongs(SwitchTitle t, ulong game) => t.Type == SwitchRead.Update ? t.App == game || t.Id == game + 0x800
        : t.Type == SwitchRead.Addon && (t.App == game || ((t.Id - 0x1000) & ~0xFFFUL) == game);

    // An add-on .nsp has no name inside it: its file name without the [ids] and (tags).
    static string AddonName(string file)
    {
        string n = Regex.Replace(Path.GetFileNameWithoutExtension(file), @"\[[^\]]*\]|\([^)]*\)", " ");
        n = Regex.Replace(n.Replace('_', ' '), @"\s+", " ").Trim();
        return n.Length == 0 ? "Add-on content" : n;
    }

    // ---- installing (runs off the UI thread; step tells the card what it is doing) ----
    public sealed record Result(bool Ok, string Text);
    static Result No(string t) => new(false, t);

    public static Result Install(MainWindow w, Game g, GameFacts f, string path, Action<string> step)
    {
        if (f.InstallKind == null) return No(T("info.no_updates_from_ionea", f.EmuName ?? T("info.this_emulator")));
        if (string.IsNullOrEmpty(path) || (!File.Exists(path) && !Directory.Exists(path))) return No(T("info.that_file_isn_t_there"));
        if (AddGameLogic.InInbox(path)) return No(AddGameLogic.NotChecked(T("home.files")));
        if (f.NeedsKeys) return No(T("info.add_prod_keys_under_required_files"));
        if (f.Id == null) return No(T("info.ionea_can_t_read_this_game"));
        if (f.Emu?.Present != true) return No(T("info.the_game_s_emulator_isn_t"));
        string exe = Path.GetFileNameWithoutExtension(f.Emu.Exe);
        if (!f.Emu.IsCore && Process.GetProcessesByName(exe).Length > 0) return No(T("info.close_first", f.EmuName));
        var inst = f.Data?["install"] as JsonObject;
        try
        {
            return f.InstallKind switch
            {
                "ps4-pkg" => Ps4Pkg(w, f, inst, path, step),
                "emulator" => ByEmulator(f, inst, path, step),
                "wiiu-folder" => WiiUFolder(w, f, path, step),
                "switch-keep" => SwitchKeep(w, f, inst, path, step),
                _ => No(T("info.ionea_doesn_t_know_the_install", f.InstallKind)),
            };
        }
        catch (Exception e) { Log.Write($"game info: install failed: {e}"); return No(T("info.it_couldn_t_be_installed") + e.Message); }
    }

    static byte[] Head(string file, int n)
    {
        using var s = File.OpenRead(file);
        if (s.Length < n) return null;
        var b = new byte[n]; s.ReadExactly(b); return b;
    }

    static string Fill(string args, string file, string id, string outDir = null) =>
        args.Replace("{file}", file).Replace("{gameid}", id ?? "").Replace("{out}", outDir ?? "");

    static int Run(string exe, string args, out string output, int timeoutMs = 60 * 60 * 1000)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(exe),
        };
        using var p = Process.Start(psi);
        var so = p.StandardOutput.ReadToEndAsync(); var se = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { /* already ended */ } output = "timed out"; return -1; }
        p.WaitForExit();
        output = so.Result + se.Result;
        return p.ExitCode;
    }

    static string Space(string onDir, long need)
    {
        try
        {
            var d = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(onDir)));
            return d.AvailableFreeSpace < need ? T("info.not_enough_free_space_on", d.Name.TrimEnd('\\'), Storage.Size(need)) : null;
        }
        catch { return null; }
    }

    static int Compare(string a, string b)
    {
        static Version V(string s) => System.Version.TryParse(Regex.Replace(s ?? "", @"[^\d.]", "").Trim('.') is var t && t.Contains('.') ? t : t + ".0", out var v) ? v : new Version(0, 0);
        return V(a).CompareTo(V(b));
    }

    // A folder moved into place (copied across drives), its parent made first.
    static void Place(string from, string to)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to));
        if (string.Equals(Path.GetPathRoot(Path.GetFullPath(from)), Path.GetPathRoot(Path.GetFullPath(to)), StringComparison.OrdinalIgnoreCase)) Directory.Move(from, to);
        else { CopyDir(from, to); Directory.Delete(from, true); }
    }
    static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
    }

    // PS4 .pkg: the PKG magic, the title id from the content id, the kind from
    // the PKG extractor's exit code (101 game, 102 update, 103 add-on); unpacked in data\work, then put in place:
    // the update in the first update folder (only a higher APP_VER replaces one there; the old one goes to the
    // Recycle Bin), an add-on in its own folder (named by the content id's label). The .pkg stays as it is.
    static Result Ps4Pkg(MainWindow w, GameFacts f, JsonObject inst, string pkg, Action<string> step)
    {
        if (!File.Exists(pkg) || !pkg.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase)) return No(T("info.choose_a_ps4_pkg_file"));
        var h = Head(pkg, 0x64);
        if (h == null || h[0] != 0x7F || h[1] != 0x43 || h[2] != 0x4E || h[3] != 0x54) return No(T("info.this_file_is_not_a_ps4"));
        string cid = Encoding.ASCII.GetString(h, 0x40, 36);
        var m = Regex.Match(cid, @"^[A-Z]{2}\d{4}-([A-Z]{4}\d{5})_\d\d-[A-Z0-9]{16}$");
        if (!m.Success) return No(T("info.this_package_s_content_id_can"));
        string tid = m.Groups[1].Value;
        if (tid != f.Id) return No(T("info.this_package_is_for_not_this", tid, f.Id));
        string tool = ToolPath.Find(inst?["tool"], w.Opt.DataDir, AppContext.BaseDirectory);
        if (tool != null && !File.Exists(tool)) Log.Write($"game info: PKG extractor not found ({tool})");
        if (tool == null || !File.Exists(tool)) return No(tool == null ? T("info.pkg_extractor_missing") : T("info.pkg_extractor_missing_in", Path.GetDirectoryName(tool)));

        step(T("info.checking_the_package"));
        int code = Run(tool, Fill(Text(inst["check"]) ?? "\"{file}\" --check-type", pkg, tid), out _, 5 * 60 * 1000);
        string kind = code switch { 101 => "game", 102 => "update", 103 => "addon", _ => null };
        if (kind == "game") return No(T("info.this_is_the_game_itself_not"));
        if (kind == null) return No(T("info.the_pkg_extractor_couldn_t_read", code));

        string work = Path.Combine(w.Opt.DataDir, "work", "addon-" + DateTime.Now.Ticks);
        long size = new FileInfo(pkg).Length;
        if (Space(w.Opt.DataDir, (long)(size * 1.1) + (1L << 30)) is string sp) return No(sp);
        try
        {
            Directory.CreateDirectory(work);
            step(kind == "update" ? T("info.unpacking_the_update") : T("info.unpacking_the_add_on"));
            Run(tool, Fill(Text(inst["unpack"]) ?? "\"{file}\" \"{out}\"", pkg, tid, work), out string said);
            var bad = Regex.Match(said, @"(Cannot|Failed|Error)[^\r\n]*");
            if (bad.Success) return No(T("info.unpacking_failed", bad.Value.Trim()));
            var root = new DirectoryInfo(work).EnumerateFiles("param.sfo", SearchOption.AllDirectories)
                .Where(x => x.Directory.Name.Equals("sce_sys", StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.FullName.Length).FirstOrDefault()?.Directory.Parent;
            if (root == null) return No(T("info.unpacking_gave_no_sce_sys_param"));
            var (ver, name) = ReadFolder("sfo", root.FullName);
            var sfo = GameId.Sfo(File.ReadAllBytes(Path.Combine(root.FullName, "sce_sys", "param.sfo")));
            if (sfo.GetValueOrDefault("TITLE_ID") is string st && st.Length > 0 && st != tid) return No(T("info.it_unpacked_with_a_different_title"));

            step(T("info.putting_it_in_place"));
            if (kind == "update")
            {
                string dest = f.UpdateDir ?? Folders(w, f, f.Data["update"]).FirstOrDefault();
                if (dest == null) return No(T("info.the_definition_for_names_no_update", f.EmuName));
                if (Directory.Exists(dest))
                {
                    var (have, _) = ReadFolder("sfo", dest);
                    if (have != null && Compare(ver, have) <= 0) return No(T("info.update_is_already_installed", have));
                    if (Uninstall.Recycle(new List<string> { dest }) is string err) return No(T("info.the_old_update_couldn_t_be", err));
                }
                Place(root.FullName, dest);
                Log.Write($"game info: PS4 update {ver} for {tid} put in {dest}{(f.Update != null ? $" (was {f.Update})" : "")}");
                return new(true, T("info.update_is_installed", ver));
            }
            string pattern = List(f.Data["addons"]).FirstOrDefault(p => p.EndsWith("\\*"));
            string to = pattern == null ? null : Resolve(w, f, pattern[..^1] + cid[20..]);
            if (to == null) return No(T("info.the_definition_for_names_no_add", f.EmuName));
            if (Directory.Exists(to)) return No(T("info.already_installed", name ?? T("info.this_add_on")));
            Place(root.FullName, to);
            Log.Write($"game info: PS4 add-on {cid[20..]} for {tid} put in {to}");
            return new(true, T("info.is_installed", name ?? T("info.the_add_on")));
        }
        finally { try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch { /* best-effort cleanup */ } }
    }

    // PS3 .pkg: the emulator installs it itself (RPCS3: --installpkg, verify). The package's content id must be
    // this game's.
    static Result ByEmulator(GameFacts f, JsonObject inst, string file, Action<string> step)
    {
        string args = Text(inst?["args"]);
        if (args == null) return No(T("info.the_definition_for_has_no_install", f.EmuName));
        var exts = List(inst["exts"]).ToList();
        if (!File.Exists(file) || (exts.Count > 0 && !exts.Any(x => file.EndsWith(x, StringComparison.OrdinalIgnoreCase)))) return No(T("info.choose_a_file", string.Join(T("common.or"), exts)));
        if (f.System == "ps3")
        {
            var h = Head(file, 0x60);
            if (h == null || h[0] != 0x7F || h[1] != 0x50 || h[2] != 0x4B || h[3] != 0x47) return No(T("info.this_file_is_not_a_ps3"));
            var m = Regex.Match(Encoding.ASCII.GetString(h, 0x30, 36), @"^[A-Z]{2}\d{4}-([A-Z]{4}\d{5})_\d\d-");
            if (!m.Success) return No(T("info.this_package_s_content_id_can"));
            if (m.Groups[1].Value != f.Id) return No(T("info.this_package_is_for_not_this", m.Groups[1].Value, f.Id));
        }
        step(T("info.installing_with", f.EmuName));
        int code = Run(f.Emu.Exe, Fill(args, file, f.Id), out string said);
        Log.Write($"game info: {f.EmuName} install of {Path.GetFileName(file)} ended with code {code}");
        return code == 0 ? new(true, T("info.installed_it", f.EmuName)) : No(T("info.couldn_t_install_it_code", f.EmuName, code));
    }

    // Wii U: a decrypted update or add-on folder (meta\meta.xml, code, content). Its title id says which:
    // 0005000E… update, 0005000C… add-on; the low half must be the game's. Copied to the emulator's title folder.
    static Result WiiUFolder(MainWindow w, GameFacts f, string dir, Action<string> step)
    {
        string meta = Path.Combine(dir, "meta", "meta.xml");
        if (!Directory.Exists(dir) || !File.Exists(meta)) return No(T("info.choose_the_update_s_or_add"));
        var x = System.Xml.Linq.XDocument.Load(meta).Root;
        string id = x?.Element("title_id")?.Value?.Trim().ToUpperInvariant();
        if (id == null || !Regex.IsMatch(id, "^[0-9A-F]{16}$")) return No(T("info.its_meta_xml_has_no_title"));
        if (id[8..].ToLowerInvariant() != f.Low) return No(T("info.this_is_for_another_game", id));
        bool update = id.StartsWith("0005000E"), addon = id.StartsWith("0005000C");
        if (!update && !addon) return No(T("info.this_is_the_game_itself_not"));
        string dest = Folders(w, f, update ? f.Data["update"] : f.Data["addons"]).FirstOrDefault();
        if (dest == null) return No(T(update ? "info.the_definition_for_names_no_update" : "info.the_definition_for_names_no_add", f.EmuName));
        var (ver, _) = ReadFolder("wiiu-meta", dir);
        if (Directory.Exists(dest))
        {
            var (have, _) = ReadFolder("wiiu-meta", dest);
            if (have != null && Compare(ver, have) <= 0) return No(T(update ? "info.update_already_installed" : "info.add_on_already_installed", have));
        }
        long size = Uninstall.Size(new List<string> { dir });
        if (Space(dest, size + (256L << 20)) is string sp) return No(sp);
        step(update ? T("info.copying_the_update") : T("info.copying_the_add_on"));
        string temp = dest + ".ionea-new";
        if (Directory.Exists(temp)) Directory.Delete(temp, true);
        CopyDir(dir, temp);
        if (Directory.Exists(dest) && Uninstall.Recycle(new List<string> { dest }) is string err) { Directory.Delete(temp, true); return No(T("info.the_old_one_couldn_t_be", err)); }
        Directory.Move(temp, dest);
        Log.Write($"game info: Wii U {(update ? "update" : "add-on")} {id} {ver} copied to {dest}");
        return new(true, update ? T("info.update_is_installed", ver) : T("info.the_add_on_is_installed"));
    }

    // Switch: the .nsp is checked (an update or add-on of this game) and copied to Ionea's folder for the game;
    // that folder is added to the emulator's game folders (qt-config.ini), where it reads updates and add-ons.
    // The emulator's NAND is never touched. A replaced update goes to the Recycle Bin.
    static Result SwitchKeep(MainWindow w, GameFacts f, JsonObject inst, string file, Action<string> step)
    {
        if (!File.Exists(file) || !file.EndsWith(".nsp", StringComparison.OrdinalIgnoreCase)) return No(T("info.choose_an_nsp_file"));
        var keys = Keys(f);
        if (keys == null) return No(T("info.add_prod_keys_under_required_files"));
        step(T("info.reading_the_file"));
        var titles = SwitchRead.Read(file, keys);
        if (titles.Count == 0) return No(T("info.ionea_can_t_read_this_file", SwitchRead.LastError));
        if (!ulong.TryParse(f.Id, System.Globalization.NumberStyles.HexNumber, null, out ulong game)) return No(T("info.ionea_can_t_read_this_game_2"));
        var t = titles[0];
        if (t.Type == SwitchRead.Game) return No(T("info.this_is_a_game_not_an"));
        if (!Belongs(t, game)) return No(T("info.this_is_for_another_game_2", t.App));
        string config = Resolve(w, f, Text(inst?["config"]));
        if (config == null) return No(T("info.the_definition_for_names_no_settings", f.EmuName));

        string dir = ContentDir(w, f.System, f.Id);
        var kept = KeptSwitch(w, f, keys);
        var old = new List<string>();
        if (t.Type == SwitchRead.Update)
        {
            var have = kept.Where(k => k.t.Type == SwitchRead.Update).OrderByDescending(k => k.t.Version).FirstOrDefault();
            if (have.t != null && have.t.Version >= t.Version) return No(T("info.update_is_already_installed", have.t.Shown));
            old = kept.Where(k => k.t.Type == SwitchRead.Update).Select(k => k.file).ToList();
        }
        else if (kept.Any(k => k.t.Id == t.Id)) return No(T("info.is_already_installed", AddonName(file)));
        string to = Path.Combine(dir, Path.GetFileName(file));
        if (File.Exists(to) && !old.Contains(to, StringComparer.OrdinalIgnoreCase)) return No(T("info.a_file_with_this_name_is"));
        if (Space(w.Opt.DataDir, new FileInfo(file).Length + (256L << 20)) is string sp) return No(sp);

        step(T("info.copying_the_file"));
        Directory.CreateDirectory(dir);
        string temp = to + ".ionea-new";
        File.Copy(file, temp, true);
        if (old.Count > 0 && Uninstall.Recycle(old) is string err) { File.Delete(temp); return No(T("info.the_old_update_couldn_t_be", err)); }
        File.Move(temp, to, true);
        string said = AddGameDir(config, dir);
        Log.Write($"game info: Switch {(t.Type == SwitchRead.Update ? "update " + t.Shown : "add-on " + t.IdText)} for {f.Id} kept in {to}; {Path.GetFileName(config)}: {said}");
        return new(true, t.Type == SwitchRead.Update ? T("info.update_is_added", t.Shown) : T("info.addon_is_added", AddonName(file)));
    }

    // A game folder in a yuzu-style qt-config.ini ([UI] Paths\gamedirs\N\…, Paths\gamedirs\size=N). A file without
    // a list gets the usual three first (SDMC, UserNAND, SysNAND). The first change keeps a .bak-ionea copy.
    public static string AddGameDir(string ini, string dir)
    {
        string want = Path.GetFullPath(dir).TrimEnd('\\').Replace('\\', '/');
        string raw = File.Exists(ini) ? File.ReadAllText(ini) : "";
        string nl = raw.Contains("\r\n") ? "\r\n" : "\n";
        var lines = raw.Length == 0 ? new List<string>() : raw.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').ToList();
        int ui = lines.FindIndex(l => l.Trim() == "[UI]");
        if (ui < 0) { if (lines.Count > 0) lines.Add(""); lines.Add("[UI]"); ui = lines.Count - 1; }
        int end = lines.FindIndex(ui + 1, l => l.StartsWith("[")); if (end < 0) end = lines.Count;
        var rx = new Regex(@"^Paths\\gamedirs\\(\d+)\\path=(.*)$");
        int max = 0, size = 0, sizeAt = -1, last = -1;
        for (int i = ui + 1; i < end; i++)
        {
            var m = rx.Match(lines[i]);
            if (m.Success)
            {
                max = Math.Max(max, int.Parse(m.Groups[1].Value));
                if (m.Groups[2].Value.Trim().Replace('\\', '/').TrimEnd('/').Equals(want, StringComparison.OrdinalIgnoreCase)) return "already listed";
            }
            if (lines[i].StartsWith(@"Paths\gamedirs\size=")) { sizeAt = i; int.TryParse(lines[i][20..], out size); }
            if (lines[i].StartsWith(@"Paths\gamedirs\")) last = i;
        }
        var add = new List<string>();
        void Entry(int n, string path)
        {
            add.Add($@"Paths\gamedirs\{n}\path={path}");
            add.Add($@"Paths\gamedirs\{n}\deep_scan\default=true");
            add.Add($@"Paths\gamedirs\{n}\deep_scan=false");
            add.Add($@"Paths\gamedirs\{n}\expanded\default=true");
            add.Add($@"Paths\gamedirs\{n}\expanded=true");
        }
        int next = Math.Max(max, size);
        if (next == 0) { Entry(1, "SDMC"); Entry(2, "UserNAND"); Entry(3, "SysNAND"); next = 3; }
        Entry(++next, want);
        int at = last >= 0 ? last + 1 : end;
        lines.InsertRange(at, add);
        if (sizeAt >= 0) lines[sizeAt] = $@"Paths\gamedirs\size={next}";
        else lines.Insert(at, $@"Paths\gamedirs\size={next}");
        if (File.Exists(ini) && !Directory.GetFiles(Path.GetDirectoryName(ini), Path.GetFileName(ini) + ".bak-ionea-*").Any())
            File.Copy(ini, ini + ".bak-ionea-" + DateTime.Now.ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(Path.GetDirectoryName(ini));
        File.WriteAllText(ini, string.Join(nl, lines) + nl, new UTF8Encoding(false));
        return $"added as game folder {next}";
    }

    // ---- the cover ----
    // Ionea's own copy of the image, square like the others; the old copy goes to the Recycle Bin.
    public static string SetCover(MainWindow w, Game g, string file)
    {
        if (file == null || !File.Exists(file)) return T("info.that_image_isn_t_there");
        if (AddGameLogic.InInbox(file)) return AddGameLogic.NotChecked(T("home.images"));
        var b = AddGameLogic.PhotoCover(file);
        if (b == null) return T("home.that_image_can_t_be_read");
        UseCover(w, g, b, "own", file);
        return null;
    }

    // The name tile instead of a picture, kept that way (the background fetch skips it).
    public static void NoCover(MainWindow w, Game g)
    {
        string old = w.Lib.CoverPath(g);
        g.Cover = null; g.CoverFrom = "name"; g.CoverUrl = ""; g.CoverZoom = g.CoverX = g.CoverY = null; w.Lib.Save();
        if (old != null && File.Exists(old) && Uninstall.Recycle(new List<string> { old }) is string err) Log.Write($"game info: old cover {old}: {err}");
        Log.Write($"game info: \"{g.Name}\" shows its name, no picture");
        w.Home?.Rebuild(g.Id);
    }

    // A finished cover (square, CoverSize) for the game. from: Game.CoverFrom; "own" is never replaced by Ionea.
    // url: the picture's public web address (Covers.PublicUrl, Discord status), null when it has none.
    // whole: a picture that isn't (near) square starts zoomed out to show all of it, as Change cover showed it.
    public static void UseCover(MainWindow w, Game g, System.Windows.Media.Imaging.BitmapSource b, string from, string what, string url = null, bool whole = false)
    {
        string old = w.Lib.CoverPath(g);
        string name = $"{g.Id}-{DateTime.Now:yyyyMMddHHmmssfff}.png";
        Directory.CreateDirectory(w.Lib.CoversDir);
        Art.SavePng(b, Path.Combine(w.Lib.CoversDir, name));
        g.Cover = name; g.CoverFrom = from; g.CoverUrl = url ?? ""; g.CoverZoom = g.CoverX = g.CoverY = null;   // a new picture starts at the plain fill
        double aspect = CoverView.Aspect(b);
        if (whole && (aspect < 0.85 || aspect > 1.18)) { g.CoverZoom = Math.Round(CoverView.Whole(aspect), 4); g.CoverX = g.CoverY = 0.5; }
        w.Lib.Save();
        if (old != null && File.Exists(old) && Uninstall.Recycle(new List<string> { old }) is string err) Log.Write($"game info: old cover {old}: {err}");
        Log.Write($"game info: new cover for \"{g.Name}\" ({from}) from {what}");
        w.Home?.Rebuild(g.Id);
    }
}

public static class GameRename
{
    // Game info > Rename: the rules of a new game's name; the game's own name in other letters is fine.
    public static string Error(MainWindow w, Game g, string name) =>
        name.Equals(g.Name, StringComparison.OrdinalIgnoreCase) ? null : AddGameLogic.CheckName(name, w.Lib);

    public static void Apply(MainWindow w, Game g, string name)
    {
        name = AddGameLogic.Clean(name ?? "");
        if (name.Length == 0 || name == g.Name || Error(w, g, name) != null) return;
        string old = g.Name;
        g.Name = name; w.Lib.Save();
        Log.Write($"game info: \"{old}\" renamed \"{name}\"");
        w.Home?.Rebuild(g.Id);
    }
}

// The Game info screen. Facts on top, three rows below: Add update or add-on, Required files (a Switch game
// without keys), Change cover.
public sealed class GameInfoCard : MenuCard
{
    public static GameInfoCard Current;
    readonly Game g;
    readonly bool fromGameMenu;
    GameFacts f;
    readonly ContentControl head = new(), facts = new();
    readonly StackPanel rows = new() { Margin = new Thickness(0, 26, 0, 0) };
    readonly Dictionary<string, string> shown = new();
    FrameworkElement nameEl;
    bool stale;

    public GameInfoCard(MainWindow w, Game game, bool fromGameMenu = false) : base(w, null, 1320)
    {
        g = game; this.fromGameMenu = fromGameMenu;
        Box.Padding = new Thickness(48, 40, 48, 36);
        Body.Children.Add(head);
        Body.Children.Add(facts);
        Body.Children.Add(rows);
        RowHost = rows;
        Current = this;
        Header();
        Load();
    }

    void Load()
    {
        ClearRows(); shown.Clear(); facts.Content = null;
        LoadThen(() => GameContent.Gather(w, g), r => { f = r ?? new GameFacts { Console = "" }; Build(); });
    }

    public void Redraw() => Header();
    void Header()
    {
        var h = new Grid();
        h.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        h.ColumnDefinitions.Add(new ColumnDefinition());
        var cover = new Border { Width = 150, Height = 150, CornerRadius = new CornerRadius(24), Background = CoverColour.Fill(g), Margin = new Thickness(0, 0, 32, 0), ClipToBounds = true };
        string file = w.Lib.CoverPath(g);
        if (file != null && File.Exists(file) && Art.LoadCover(file, CoverView.Zoom(g) > 1.001 ? 0 : 300) is System.Windows.Media.Imaging.BitmapSource bmp) cover.Background = CoverView.Brush(bmp, g);
        else cover.Child = new Viewbox { Margin = new Thickness(36), Child = Art.Icon("play", 48, CoverColour.Ink2(g)) };
        h.Children.Add(cover);
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var name = Fonts.Text(g.Name, 46, P.Text, FontWeights.Normal, Fonts.Display);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.Cursor = System.Windows.Input.Cursors.IBeam;
        nameEl = name;
        names.Children.Add(name);
        var sub = Fonts.Text(f == null ? "" : string.Join(" · ", new[] { f.Console, f.EmuName }.Where(s => !string.IsNullOrEmpty(s))), 28, P.Text2);
        sub.Margin = new Thickness(2, 8, 0, 0);
        names.Children.Add(sub);
        Grid.SetColumn(names, 1); h.Children.Add(names);
        head.Content = h;
    }

    static string Ago(DateTime at)
    {
        int days = (DateTime.Today - at.Date).Days;
        return days <= 0 ? T("settings.today") : days == 1 ? T("settings.yesterday") : days < 30 ? N("info.days_ago", days) : at.ToString("d MMMM yyyy", System.Globalization.CultureInfo.GetCultureInfo("en-GB"));
    }

    void Build()
    {
        Header();
        var grid = new Grid { Margin = new Thickness(0, 34, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());

        // left: version, updates and add-ons
        var left = new StackPanel();
        left.Children.Add(Caption(T("settings.version")));
        var ver = Fonts.Text(f.Version, 40, f.NeedsKeys ? P.Text2 : P.Text, FontWeights.Normal, Fonts.Display);
        ver.Margin = new Thickness(0, 2, 0, 0);
        left.Children.Add(ver);
        shown["Version"] = f.Version;
        if (f.Section && !f.NeedsKeys)
        {
            var cap = Caption(T("info.updates_and_add_ons")); cap.Margin = new Thickness(0, 28, 0, 6);
            left.Children.Add(cap);
            // add-ons first, then the emulator's mods (a mod turned off in the emulator in the second colour)
            var items = f.Addons.Select(a => (Text: a, Dim: false))
                .Concat(f.Mods.Select(m => (Text: m.Name + (m.Off ? " (mod, off)" : " (mod)"), Dim: m.Off))).ToList();
            if (items.Count == 0) left.Children.Add(Line(T("info.no_add_ons"), P.Text2));
            foreach (var a in items.Take(items.Count > 6 ? 5 : 6)) left.Children.Add(Line(a.Text, a.Dim ? P.Text2 : P.Text));
            if (items.Count > 6) left.Children.Add(Line(T("settings.and_more", items.Count - 5), P.Text2));
        }
        grid.Children.Add(left);

        // right: details and play
        var right = new StackPanel();
        void Fact(string label, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            shown[label] = value;
            var r = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            r.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            r.ColumnDefinitions.Add(new ColumnDefinition());
            r.Children.Add(Fonts.Text(label, 26, P.Text2));
            var v = Fonts.Text(value, 28, P.Text);
            v.HorizontalAlignment = HorizontalAlignment.Right; v.TextTrimming = TextTrimming.CharacterEllipsis; v.Margin = new Thickness(24, 0, 0, 0);
            v.ToolTip = value;
            Grid.SetColumn(v, 1); r.Children.Add(v);
            right.Children.Add(r);
        }
        Fact(T("info.game_id"), f.Id);
        Fact(T("info.region"), f.Region);
        Fact(T("settings.size"), f.Size > 0 ? Storage.Size(f.Size) : null);
        Fact(T("settings.stored_on"), f.StoredOn);
        right.Children.Add(new Border { Height = 2, Background = P.Line, Opacity = 0.7, Margin = new Thickness(0, 6, 0, 18) });
        Fact(T("settings.last_played"), f.LastPlayed is DateTime lp ? Ago(lp) : T("settings.never"));
        Fact(T("info.play_time"), Profiles.Hours(f.Seconds));
        // this profile's numbers for the game (ProfileStats.cs), once it has been played
        var gn = PlayStats.ForGame(w.Profiles, g.Id);
        if (gn.Seconds > 0 || gn.Sessions > 0)
        {
            var gb = System.Globalization.CultureInfo.GetCultureInfo("en-GB");
            Fact(T("settings.sessions"), gn.Sessions.ToString());
            Fact(T("info.first_played"), gn.First?.ToString("d MMM yyyy", gb));
            if (gn.Sessions > 0) Fact(T("info.average_session"), Profiles.Hours(gn.Average));
            Fact(T("info.share_of_your_time"), gn.Share >= 0.995 ? T("info.all_of_it") : gn.Share < 0.01 ? "under 1 %" : $"{Math.Round(gn.Share * 100)} %");
        }
        Grid.SetColumn(right, 2); grid.Children.Add(right);
        facts.Content = grid;

        if (f.InstallKind != null && !f.NeedsKeys) Opens(WithIcon(AddRow(T("info.add_update_or_add_on"), activate: PickAddon), "plus"));
        if (f.NeedsKeys && f.Emu != null) Opens(WithIcon(AddRow(T("settings.required_files"), () => T("info.prod_keys_missing"), activate: () => { stale = true; new RequiredFilesCard(w, f.Emu).Open(); }), "folder"));
        if (g.Source == "rom") Opens(WithIcon(AddRow(T("settings.display"), () => DisplayLogic.Summary(w, g), activate: () => new DisplayCard(w, g).Open()), "screen"));   // Display.cs
        // A native port and its emulator with the same save files: one save for both, or each its own (Ports.SyncSaves)
        if (Ports.ShareOf(w, g) is Ports.SaveShare share)
            WithIcon(AddRow(T("info.saves"), () => Ports.SavesShared(g, share) ? T("info.shared") : T("info.separate"), _ =>
            {
                if (Running()) { new MessageCard(w, T("settings.the_game_is_running"), T("info.close_it_first_then_change_where")).Open(); return; }
                Ports.SetSaves(w, g, !Ports.SavesShared(g, share));
                Refresh();
            }), "states");
        Opens(WithIcon(AddRow(T("common.rename"), () => g.Name, activate: Rename), "pen"));
        Opens(WithIcon(AddRow(T("settings.change_cover"), activate: () => new CoverPickCard(w, g, _ => PickCover()).Open()), "picture"));
        // the name tile's colour (CoverColour.cs): Left / Right step through them, A opens the list
        WithIcon(WithList(AddRow(T("info.cover_colour"), () => CoverColour.Of(g).Name + (File.Exists(w.Lib.CoverPath(g) ?? "") ? T("info.name_tile_only") : ""),
                d => { CoverColour.Set(w, g, CoverColour.IndexOf(g) + d); Redraw(); }),
            () => CoverColour.All.Select(c => c.Name).ToList(), () => CoverColour.IndexOf(g), i => { CoverColour.Set(w, g, i); Redraw(); Refresh(); }), "palette");
        // zoom and move the picture (CoverZoom.cs); only when there is a picture to adjust
        Opens(WithIcon(AddRow(T("home.adjust_cover"), () => File.Exists(w.Lib.CoverPath(g) ?? "") ? (CoverView.Plain(g) ? "" : FormattableString.Invariant($"{CoverView.Zoom(g):0.0}x")) : T("info.no_picture"),
            activate: () => { if (File.Exists(w.Lib.CoverPath(g) ?? "")) new CoverAdjustCard(w, g).Open(); else new MessageCard(w, T("home.adjust_cover"), T("info.this_game_shows_its_name_choose")).Open(); }), "search"));
        Focus = 0;
        Refresh();
    }

    TextBlock Caption(string t) => Fonts.Text(t, 24, P.Text2, FontWeights.SemiBold);
    TextBlock Line(string t, Brush b)
    {
        var x = Fonts.Text(t, 28, b);
        x.TextTrimming = TextTrimming.CharacterEllipsis; x.Margin = new Thickness(0, 0, 0, 6);
        return x;
    }

    bool Running() => HoldMenu.IsRunning(w, g) || w.GameMenu.Game?.Id == g.Id;

    void PickAddon()
    {
        if (Running()) { new MessageCard(w, T("settings.the_game_is_running"), T("info.close_it_first_then_add_to")).Open(); return; }
        if (f.InstallKind == "wiiu-folder") Picker.Folder(w, T("info.choose_the_update_s_or_add_2"), null, Add);
        else
        {
            bool nsp = f.InstallKind == "switch-keep";
            Picker.File(w, T("info.choose_an_update_or_add_on"), nsp ? T("info.switch_update_or_add_on_nsp") : T("info.package_pkg_pkg"), new[] { nsp ? ".nsp" : ".pkg" }, null, Add);
        }
    }

    // As after the file picker (tests call it straight).
    public void Add(string path)
    {
        if (Running()) { new MessageCard(w, T("settings.the_game_is_running"), T("info.close_it_first_then_add_to")).Open(); return; }
        if (f == null || f.InstallKind == null || f.NeedsKeys) { Log.Write("game info: nothing to add to for " + g.Name); return; }
        stale = true;
        new AddonCard(w, g, f, path).Open();
    }

    void PickCover()
    {
        Picker.File(w, T("home.choose_a_cover_image"), Picker.ImageFilter, Picker.Images, null, Cover);
    }

    public void Cover(string file)
    {
        if (GameContent.SetCover(w, g, file) is string err) { new MessageCard(w, T("settings.change_cover"), err).Open(); return; }
        Header();
        w.Toasts.Show(T("settings.cover_changed"), g.Name, "info");
        new CoverAdjustCard(w, g).Open();   // a new picture: frame it at once
    }

    // The name, here where it shows (Rename row, or a click on the name).
    public void Rename()
    {
        new TextCard(w, T("common.rename"), g.Name, 80, n => { GameRename.Apply(w, g, n); Header(); Refresh(); }, n => GameRename.Error(w, g, n)).Open();
    }

    public override bool OnMouse(MouseAct m, Point p)
    {
        if (m == MouseAct.Down && nameEl != null && nameEl.IsVisible && Pointer.Over(nameEl, w.Stage, p)) { Sounds.Play("select"); Rename(); return true; }
        return base.OnMouse(m, p);
    }

    public override void OnTop()
    {
        if (stale) { stale = false; Load(); return; }
        base.OnTop();
    }

    public override void Close(bool sound = true)
    {
        if (Current == this) Current = null;
        base.Close(sound);
        if (fromGameMenu && w.GameMenu.Game != null) w.GameMenu.ShowCard();
    }

    // For tests.
    public string Fact(string label) => shown.TryGetValue(label, out var v) ? v : "none";
    public string AddonList => f == null || f.Addons.Count == 0 ? "none" : string.Join(" | ", f.Addons);
    public string ModList => f == null || f.Mods.Count == 0 ? "none" : string.Join(" | ", f.Mods.Select(m => m.Name + (m.Off ? " (off)" : "")));
    public bool HasSection => f?.Section == true && !f.NeedsKeys;
    public string Kind => f?.InstallKind ?? "none";
}

// Installing one update or add-on: a spinner with what it is doing, then the result.
public sealed class AddonCard : MenuCard
{
    public static string LastResult = "none";
    public static bool Busy;
    readonly TextBlock status;

    public AddonCard(MainWindow w, Game g, GameFacts f, string path) : base(w, T("info.add_update_or_add_on"), 940)
    {
        status = Note(Path.GetFileName(path.TrimEnd('\\')), 28, new Thickness(4, 0, 0, 8));
        Busy = true; LastResult = "busy";
        LoadThen(() =>
        {
            GameContent.Result r;
            try { r = GameContent.Install(w, g, f, path, s => w.Dispatcher.BeginInvoke(() => status.Text = s)); }
            catch (Exception e) { r = new(false, e.Message); }
            LastResult = (r.Ok ? "ok: " : "refused: ") + r.Text;
            Log.Write($"game info: add to \"{g.Name}\" from {path}: {LastResult}");
            Busy = false;
            return r;
        }, r =>
        {
            status.Text = r.Text;
            status.Foreground = r.Ok ? P.Text : P.Error;
            AddRow(T("common.ok"), activate: () => Close());
            Refresh();
        });
    }

    // It can't be stopped halfway: Back waits until it is done.
    public override void OnAct(Act a) { if (Loading) return; base.OnAct(a); }
}

// Where a definition's helper program is ("install": { "tool": ... }): one path or a list tried in order. {data} is
// the data folder, {app} the folder Launcher.exe runs from; a relative path starts in {app}. The first that exists
// wins; when none does, the first (for the "not installed" message). No fixed folder is built in: a user's own
// definition (<data>\definitions) can point it anywhere.
public static class ToolPath
{
    public static string Find(JsonNode node, string dataDir, string appDir, Func<string, bool> exists = null)
    {
        exists ??= File.Exists;
        IEnumerable<JsonNode> items = node is JsonArray a ? a : new[] { node };
        string first = null;
        foreach (var n in items)
        {
            if (n is not JsonValue v || !v.TryGetValue(out string p) || string.IsNullOrWhiteSpace(p)) continue;
            string app = appDir.TrimEnd('\\', '/');
            string full = Path.GetFullPath(p.Replace("{data}", dataDir.TrimEnd('\\', '/')).Replace("{app}", app).Replace('/', '\\'), app);
            first ??= full;
            if (exists(full)) return full;
        }
        return first;
    }
}
