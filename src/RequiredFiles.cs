#nullable disable
// Required files: firmware, keys and BIOS files an emulator
// needs and Ionea never ships. A definition lists them ("requiredFiles", EmuDefs.DefFile); this file checks
// them, takes them from the user's own file, folder or zip, and copies them into place.
//
//   - Where: relative to the program; for a core, relative to its system folder (Cores\system\).
//   - Ready / Missing: a file is Ready when it is there and not empty; a folder when it passes its check (or
//     holds any file). Files the user put in place themselves count as Ready without a hash check.
//   - Picking is strict: by hash where a public list exists (md5 / md5s, from libretro-core-info), else by
//     structure (Check: Switch keys, Switch firmware, PS4 modules, PS2 BIOS, PS3 firmware, Dolphin's Sys
//     folder), else by file name. Anything else is refused and nothing is copied.
//   - A right file goes into the shared store (<data>\files\<share>\) first, then into every emulator on the
//     list that needs the same share and lacks it (Eden and Lichii share one set of keys). A file already in
//     place at one emulator is taken into the store when another one needs it.
//   - Install: the emulator installs it itself (RPCS3 and its .PUP); Ionea runs the emulator with the
//     definition's start options and then checks its Ready file.
// Nothing here ever downloads anything.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using static Ionea.Loc;

namespace Ionea;

public enum ReqState { Ready, Missing, Optional }

public sealed class ReqPick
{
    public List<string> Added = new();   // entry names now in place
    public string Error;                 // why it was refused (nothing copied)
}

public static class ReqFiles
{
    public static string StoreDir;       // <data>\files
    public static Func<IEnumerable<Emulator>> AllEmus = () => Array.Empty<Emulator>();
    const long MaxHash = 256L << 20;     // files hashed or checked by content: up to 256 MB
    const int MaxFiles = 20000;

    // Checks on one file, and checks on a folder.
    static readonly string[] FileChecks = { "switch-keys", "switch-titlekeys", "ps2-bios", "ps3-pup" };
    static readonly string[] FolderChecks = { "switch-firmware", "ps4-modules", "dolphin-sys" };
    public static bool KnownCheck(string id) => FileChecks.Contains(id) || FolderChecks.Contains(id);
    // Is it picked as a folder? (a folder entry that isn't "any one fitting file")
    public static bool PicksFolder(DefFile f) => IsFolder(f) && f.Md5s == null && !(f.Check != null && FileChecks.Contains(f.Check));

    // ---- what an emulator needs ----
    public static string Root(Emulator e) => e.IsCore ? Path.Combine(Path.GetDirectoryName(e.Exe), "system") : Path.GetDirectoryName(e.Exe);
    public static bool IsFolder(DefFile f) => f.Install == null && (f.Path.EndsWith("\\") || f.Path.EndsWith("/"));

    // Its required files (Known level and up; nothing for an emulator without a definition or a missing program).
    // An entry whose path would leave the emulator's folder, or names a check Ionea doesn't have, is skipped.
    public static List<DefFile> Of(Emulator e)
    {
        if (e?.Present != true) return new();
        var d = EmuDefs.Known(e);
        if (d?.RequiredFiles == null) return new();
        return d.RequiredFiles.Where(f =>
        {
            if (Target(e, f) == null) { Once($"required files: {d.Id}: {f.Path} is outside the emulator's folder; skipped"); return false; }
            if (f.Check != null && !KnownCheck(f.Check)) { Once($"required files: {d.Id}: unknown check {f.Check}; skipped"); return false; }
            return true;
        }).ToList();
    }

    static readonly HashSet<string> logged = new();
    static void Once(string s) { lock (logged) if (logged.Add(s)) Log.Write(s); }

    public static string Target(Emulator e, DefFile f)
    {
        string root = Path.GetFullPath(Root(e)).TrimEnd('\\');
        string t = Path.GetFullPath(Path.Combine(root, f.Path.Replace('/', '\\'))).TrimEnd('\\');
        return t.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase) ? t : null;
    }

    public static string ShareOf(Emulator e, DefFile f) =>
        Safe(f.Share ?? (f.Md5 != null ? "md5-" + f.Md5 : f.Check ?? (EmuDefs.Of(e)?.Id ?? e.Id) + "-" + f.Path));

    static string Safe(string s) => Regex.Replace(s.Trim('\\', '/'), @"[^A-Za-z0-9._-]+", "_");

    public static ReqState State(Emulator e, DefFile f) => InPlace(e, f) ? ReqState.Ready : f.Optional ? ReqState.Optional : ReqState.Missing;
    public static string StateName(ReqState s) => s switch { ReqState.Ready => T("settings.ready"), ReqState.Missing => T("settings.missing"), _ => T("settings.optional") };
    public static int Missing(Emulator e) => Of(e).Count(f => State(e, f) == ReqState.Missing);
    public static bool Has(Emulator e) => Of(e).Count > 0;

    static bool InPlace(Emulator e, DefFile f)
    {
        string t = Target(e, f);
        try
        {
            if (IsFolder(f))
            {
                if (!Directory.Exists(t)) return false;
                if (f.Check != null && FolderChecks.Contains(f.Check)) return FolderCheck(f.Check, t);
                return Directory.EnumerateFiles(t, "*", SearchOption.AllDirectories).Any(x => new FileInfo(x).Length > 0);
            }
            if (File.Exists(t)) return new FileInfo(t).Length > 0;
            return f.Install == null && Directory.Exists(t) && Directory.EnumerateFileSystemEntries(t).Any();   // some core paths name a folder
        }
        catch { return false; }
    }

    // ---- checks ----
    static readonly Dictionary<string, string> md5Cache = new(StringComparer.OrdinalIgnoreCase);
    public static string Md5(string file)
    {
        var fi = new FileInfo(file);
        string key = fi.FullName + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks;
        lock (md5Cache) if (md5Cache.TryGetValue(key, out var h)) return h;
        using var s = File.OpenRead(file);
        string hash = Convert.ToHexString(MD5.HashData(s)).ToLowerInvariant();
        lock (md5Cache) md5Cache[key] = hash;
        return hash;
    }

    public static bool FileCheck(string id, string file)
    {
        try
        {
            var fi = new FileInfo(file);
            if (!fi.Exists || fi.Length == 0) return false;
            switch (id)
            {
                case "switch-keys":   // prod.keys: "name = hex" lines, with the header key and a master key (verify)
                {
                    var names = KeyNames(fi, @"^([a-z0-9_]+)\s*=\s*[0-9a-fA-F]{32,}$");
                    return names != null && names.Contains("header_key") && names.Any(n => Regex.IsMatch(n, "^master_key_[0-9a-f]{2}$"));
                }
                case "switch-titlekeys":   // title.keys: "<32 hex rights id> = <32 hex key>" lines (verify)
                    return KeyNames(fi, @"^([0-9a-fA-F]{32})\s*=\s*[0-9a-fA-F]{32}$")?.Count > 0;
                case "ps2-bios":   // 4 MB or more, with the ROMDIR table: "RESET" and 16 bytes later "ROMDIR" (verify)
                {
                    if (fi.Length < 4L << 20 || fi.Length > 32L << 20) return false;
                    var b = new byte[Math.Min(fi.Length, 1L << 20)];
                    using (var s = fi.OpenRead()) s.ReadExactly(b);
                    var reset = Encoding.ASCII.GetBytes("RESET\0");
                    var romdir = Encoding.ASCII.GetBytes("ROMDIR");
                    for (int i = 0; i + 16 + romdir.Length <= b.Length; i += 16)
                        if (b.AsSpan(i, reset.Length).SequenceEqual(reset) && b.AsSpan(i + 16, romdir.Length).SequenceEqual(romdir)) return true;
                    return false;
                }
                case "ps3-pup":   // PS3 system update: starts with "SCEUF" (verify)
                {
                    var b = new byte[5];
                    using (var s = fi.OpenRead()) if (s.Read(b, 0, 5) < 5) return false;
                    return Encoding.ASCII.GetString(b) == "SCEUF";
                }
            }
        }
        catch (Exception caught) { Log.Debug("required files: check", caught); }
        return false;
    }

    // The key names of a small text file where every line is "name = value" (blank lines and ; # comments
    // allowed), or null when any other line is in it.
    static HashSet<string> KeyNames(FileInfo fi, string pattern)
    {
        if (fi.Length > 1 << 20) return null;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in File.ReadLines(fi.FullName))
        {
            string l = raw.Trim();
            if (l.Length == 0 || l.StartsWith(";") || l.StartsWith("#")) continue;
            var m = Regex.Match(l, pattern);
            if (!m.Success) return null;
            names.Add(m.Groups[1].Value.ToLowerInvariant());
        }
        return names;
    }

    public static bool FolderCheck(string id, string dir)
    {
        try
        {
            switch (id)
            {
                case "switch-firmware":   // the firmware's .nca files (or .nca folders), at least 20 of them (verify)
                    return Directory.EnumerateFileSystemEntries(dir, "*.nca").Take(20).Count() >= 20;
                case "ps4-modules":       // PS4 system modules: .sprx files (verify)
                    return Directory.EnumerateFiles(dir, "*.sprx").Any();
                case "dolphin-sys":       // Dolphin's Sys folder (not copyrighted; comes with Dolphin)
                    return File.Exists(Path.Combine(dir, "codehandler.bin")) && Directory.Exists(Path.Combine(dir, "GC"));
            }
        }
        catch (Exception caught) { Log.Debug("required files: check", caught); }
        return false;
    }

    // Is this candidate the right thing for the entry? (file: a file; a folder entry with a folder check: a folder)
    static bool FileFits(DefFile f, string file, string targetName)
    {
        long len; try { len = new FileInfo(file).Length; } catch { return false; }
        if (len == 0) return false;
        string md5 = len <= MaxHash ? Md5(file) : null;
        // with the user's own System.dat (SysDat): its hashes count as well, and where it lists a system or a
        // file name, a file must be in it
        if (f.Md5 != null) return md5 != null && (md5 == f.Md5 || SysDat.Known(md5, targetName));
        if (f.Md5s != null) return md5 != null && (f.Md5s.Contains(md5) || SysDat.InSystem(md5, f.Dat));
        if (f.Check != null && FileChecks.Contains(f.Check))
            return SysDat.HasSystem(f.Dat) ? md5 != null && SysDat.InSystem(md5, f.Dat) : md5 != null && FileCheck(f.Check, file);
        if (!Path.GetFileName(file).Equals(targetName, StringComparison.OrdinalIgnoreCase)) return false;
        return !SysDat.ListsName(targetName) || (md5 != null && SysDat.Known(md5, targetName));
    }

    // ---- picking ----
    // The user's file, folder or zip for this emulator. Only fills what isn't in place, except when one entry
    // was picked (its row): then it replaces what is there (the old copy is kept as .bak-ionea-<time>).
    public static ReqPick Pick(Emulator e, string path, DefFile only = null)
    {
        var r = new ReqPick();
        string stage = null;
        try
        {
            path = Path.GetFullPath(path);
            string root = path;
            if (File.Exists(path) && Regex.IsMatch(path, @"(?i)\.(zip|7z)$"))
            {
                stage = Path.Combine(Path.GetTempPath(), "ionea-files-" + Guid.NewGuid().ToString("N")[..8]);
                Directory.CreateDirectory(stage);
                Unpack(path, stage);
                root = stage;
            }
            else if (!File.Exists(path) && !Directory.Exists(path)) { r.Error = T("settings.it_was_not_found"); return r; }

            var files = new List<string>(); var dirs = new List<string>();
            if (File.Exists(root)) files.Add(root);
            else
            {
                dirs.Add(root);
                dirs.AddRange(Walk(root, SearchOption.AllDirectories, dirs: true).Take(MaxFiles));
                files.AddRange(Walk(root, SearchOption.AllDirectories, dirs: false).Take(MaxFiles));
            }

            var todo = only != null ? new List<DefFile> { only } : Of(e).Where(f => !InPlace(e, f)).ToList();
            if (todo.Count == 0) { r.Error = T("settings.nothing_is_missing"); return r; }
            foreach (var f in todo)
            {
                string why = Fill(e, f, files, dirs, replace: only != null);
                if (why == null) r.Added.Add(f.Name);
                else if (only != null) r.Error = why;
            }
            if (r.Added.Count == 0 && r.Error == null) r.Error = WhyNone(e, todo, files);
            if (r.Added.Count > 0) { r.Error = null; SupplyAll(); }
        }
        catch (Exception ex) { r.Error = ex.Message; r.Added.Clear(); Log.Write($"required files: {e.Name}: {path}: {ex}"); }
        finally { if (stage != null) try { Directory.Delete(stage, true); } catch { /* best-effort cleanup */ } }
        if (r.Error != null) Log.Write($"required files: refused {path} for {e.Name}: {r.Error}");
        else Log.Write($"required files: {e.Name}: in place from {path}: {string.Join(", ", r.Added)}");
        return r;
    }

    // A refusal that says what was wrong: a file with the right name but another hash, or nothing that fits.
    static string WhyNone(Emulator e, List<DefFile> todo, List<string> files)
    {
        foreach (var f in todo)
        {
            string name = Path.GetFileName(Target(e, f));
            if ((f.Md5 != null || (f.Check != null && FileChecks.Contains(f.Check))) && files.Any(x => Path.GetFileName(x).Equals(name, StringComparison.OrdinalIgnoreCase)))
                return f.Md5 != null ? T("settings.this_is_not_a_known_good", name) : T("settings.this_is_not_a_valid", name, f.Name);
        }
        return todo.Count == 1 ? T("settings.that_is_not", todo[0].Name) : T("settings.nothing_here_is_a_file_needs", e.Name);
    }

    static IEnumerable<string> Walk(string dir, SearchOption o, bool dirs)
    {
        try { return dirs ? Directory.EnumerateDirectories(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 6 })
                          : Directory.EnumerateFiles(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 6 }); }
        catch { return Array.Empty<string>(); }
    }

    // Puts one entry in place from the candidates, through the store. null = done, else why not.
    static string Fill(Emulator e, DefFile f, List<string> files, List<string> dirs, bool replace)
    {
        string target = Target(e, f), share = ShareOf(e, f), store = Path.Combine(StoreDir, share);
        string tname = Path.GetFileName(target);
        if (f.Install != null)
        {
            string pup = files.FirstOrDefault(x => FileFits(f, x, tname));
            if (pup == null) return T("settings.that_is_not", f.Name);
            string kept = Keep(store, pup, Path.GetFileName(pup), replace: true);
            return RunInstall(e, f, kept);
        }
        if (IsFolder(f) && f.Check != null && FolderChecks.Contains(f.Check))
        {
            string dir = dirs.FirstOrDefault(d => FolderCheck(f.Check, d));
            if (dir == null) return T("settings.that_is_not", f.Name);
            if (replace || !Directory.Exists(store) || !FolderCheck(f.Check, store)) { Reset(store); CopyTree(dir, store, overwrite: true); }
            Place(store, target, folder: true, replace);
            return null;
        }
        if (IsFolder(f))   // a folder that takes any one fitting file, under its own name
        {
            var hits = files.Where(x => FileFits(f, x, tname)).GroupBy(x => f.Md5s != null ? Md5(x) : Path.GetFileName(x).ToLowerInvariant()).Select(g => g.First()).ToList();
            if (hits.Count == 0) return T("settings.that_is_not", f.Name);
            foreach (string h in hits)
            {
                string sd = f.Md5s != null ? Path.Combine(StoreDir, "md5-" + Md5(h)) : store;
                string kept = Keep(sd, h, Path.GetFileName(h), replace);
                PlaceFile(kept, Path.Combine(target, Path.GetFileName(h)), replace);
            }
            return null;
        }
        string hit = files.FirstOrDefault(x => FileFits(f, x, tname));
        if (hit == null)
        {
            // a core path that names a folder (by name only)
            if (f.Md5 == null && f.Check == null && dirs.FirstOrDefault(d => Path.GetFileName(d).Equals(tname, StringComparison.OrdinalIgnoreCase)) is string sub)
            {
                Reset(store); CopyTree(sub, store, true); Place(store, target, true, replace); return null;
            }
            return T("settings.that_is_not", f.Name);
        }
        string k = Keep(store, hit, tname, replace);
        PlaceFile(k, target, replace);
        return null;
    }

    // Into the store as <store>\<name>; the stored copy's path.
    static string Keep(string store, string file, string name, bool replace)
    {
        Directory.CreateDirectory(store);
        string dest = Path.Combine(store, name);
        if (!SamePath(file, dest) && (replace || !File.Exists(dest))) File.Copy(file, dest, true);
        return dest;
    }

    static void Reset(string dir) { if (Directory.Exists(dir)) Directory.Delete(dir, true); Directory.CreateDirectory(dir); }

    static string Stamp => DateTime.Now.ToString("yyyyMMdd-HHmmss");

    static void PlaceFile(string from, string to, bool replace)
    {
        if (SamePath(from, to)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(to));
        if (File.Exists(to))
        {
            if (!replace || Md5Same(from, to)) return;
            File.Move(to, to + ".bak-ionea-" + Stamp);
        }
        File.Copy(from, to);
    }

    static bool Md5Same(string a, string b) { try { return new FileInfo(a).Length == new FileInfo(b).Length && Md5(a) == Md5(b); } catch { return false; } }

    static void Place(string from, string to, bool folder, bool replace)
    {
        if (SamePath(from, to)) return;
        if (Directory.Exists(to) && Directory.EnumerateFileSystemEntries(to).Any())
        {
            if (!replace) { CopyTree(from, to, overwrite: false); return; }
            Directory.Move(to, to + ".bak-ionea-" + Stamp);
        }
        CopyTree(from, to, overwrite: false);
    }

    static void CopyTree(string from, string to, bool overwrite)
    {
        Directory.CreateDirectory(to);
        foreach (string d in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, d)));
        foreach (string f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            string dest = Path.Combine(to, Path.GetRelativePath(from, f));
            if (overwrite || !File.Exists(dest)) File.Copy(f, dest, true);
        }
    }

    static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    static void Unpack(string archive, string to)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "tar.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        psi.ArgumentList.Add("-xf"); psi.ArgumentList.Add(archive); psi.ArgumentList.Add("-C"); psi.ArgumentList.Add(to);
        using var p = Process.Start(psi);
        p.StandardOutput.ReadToEndAsync();
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException("The archive could not be unpacked: " + err.Trim());
    }

    // The emulator installs it (RPCS3: --installfw). Waits up to 10 minutes, then checks its Ready file.
    static string RunInstall(Emulator e, DefFile f, string file)
    {
        var psi = new ProcessStartInfo(e.Exe, f.Install.Replace("{file}", file)) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(e.Exe) };
        Log.Write($"required files: {e.Name} installs {f.Name}: {psi.FileName} {psi.Arguments}");
        using (var p = Process.Start(psi))
            if (!p.WaitForExit(600_000)) { try { p.Kill(); } catch { /* already ended */ } return $"{e.Name} did not finish installing {f.Name}."; }
        return InPlace(e, f) ? null : $"{e.Name} did not install {f.Name}.";
    }

    // ---- supplying ----
    // Every emulator on the list gets what the store (or another emulator) already has and it lacks.
    // Never replaces a file that is there; never runs an emulator (installs only happen on a pick).
    public static void SupplyAll()
    {
        if (StoreDir == null) return;
        foreach (var e in AllEmus().ToList()) Supply(e);
    }

    public static void Supply(Emulator e)
    {
        if (StoreDir == null) return;
        foreach (var f in Of(e))
        {
            if (f.Install != null) continue;
            try
            {
                if (InPlace(e, f)) continue;
                string src = Source(e, f);
                if (src == null) continue;
                string target = Target(e, f);
                if (IsFolder(f) && f.Md5s == null && !(f.Check != null && FileChecks.Contains(f.Check))) Place(src, target, true, false);
                else if (IsFolder(f)) PlaceFile(src, Path.Combine(target, Path.GetFileName(src)), false);
                else if (Directory.Exists(src)) Place(src, target, true, false);
                else PlaceFile(src, target, false);
                Log.Write($"required files: {f.Name} copied to {e.Name} from {src}");
            }
            catch (Exception ex) { Log.Write($"required files: {f.Name} for {e.Name}: {ex.Message}"); }
        }
    }

    // Where a missing entry can come from: the store, else another emulator that has the same share in place
    // and passes the strict check (it goes into the store first). A folder, or one file.
    static string Source(Emulator e, DefFile f)
    {
        string tname = Path.GetFileName(Target(e, f));
        if (IsFolder(f) && (f.Md5s != null || (f.Check != null && FileChecks.Contains(f.Check))))
        {
            // any one fitting file: by its hash in the store, or the share's folder
            var dirs = f.Md5s != null ? f.Md5s.Concat(SysDat.Md5sOf(f.Dat)).Distinct().Select(m => Path.Combine(StoreDir, "md5-" + m)) : new[] { Path.Combine(StoreDir, ShareOf(e, f)) };
            foreach (string d in dirs.Where(Directory.Exists))
                if (Directory.GetFiles(d).FirstOrDefault(x => FileFits(f, x, tname)) is string hit) return hit;
            return null;
        }
        string share = ShareOf(e, f), store = Path.Combine(StoreDir, share);
        if (IsFolder(f) || (f.Md5 == null && f.Check == null && Directory.Exists(store) && !File.Exists(Path.Combine(store, tname))))
        {
            if (Directory.Exists(store) && (f.Check == null ? Directory.EnumerateFileSystemEntries(store).Any() : FolderCheck(f.Check, store))) return store;
        }
        else if (File.Exists(Path.Combine(store, tname)) && FileFits(f, Path.Combine(store, tname), tname)) return Path.Combine(store, tname);

        foreach (var other in AllEmus().Where(o => o != e).ToList())
            foreach (var of in Of(other).Where(of => of.Install == null && ShareOf(other, of) == share && InPlace(other, of)))
            {
                string have = Target(other, of);
                if (IsFolder(of) || Directory.Exists(have))
                {
                    if (of.Check != null && !FolderCheck(of.Check, have)) continue;
                    Reset(store); CopyTree(have, store, true);
                    Log.Write($"required files: {of.Name} taken into the store from {other.Name}");
                    return store;
                }
                if (!FileFits(of, have, Path.GetFileName(have))) continue;
                string kept = Keep(store, have, tname, replace: true);
                Log.Write($"required files: {of.Name} taken into the store from {other.Name}");
                return kept;
            }
        return null;
    }
}

// The user's own System.dat (libretro-database's BIOS list, ClrMamePro format), when one is in
// <data>\definitions\ (subfolders too). Ionea ships none: the user gets it and drops it in.
// It adds hashes: an entry's own md5 / md5s still count; a file listed there under the entry's file name
// counts too; an entry with "dat" (a system name in the file, e.g. "Sony - PlayStation") takes any file of that
// system; one with a structure check and "dat" is checked by hash instead once the file lists that system;
// a file name it lists must have one of its hashes.
public static class SysDat
{
    static readonly Dictionary<string, HashSet<string>> bySystem = new(StringComparer.OrdinalIgnoreCase);   // system -> md5s
    static readonly Dictionary<string, HashSet<string>> byName = new(StringComparer.OrdinalIgnoreCase);     // file name -> md5s
    public static string File;   // the file read, or null
    public static int Count;

    public static void Load(string dir)
    {
        bySystem.Clear(); byName.Clear(); File = null; Count = 0;
        try
        {
            if (!Directory.Exists(dir)) return;
            File = Directory.EnumerateFiles(dir, "System.dat", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive }).FirstOrDefault();
            if (File == null) return;
            Parse(System.IO.File.ReadAllText(File));
            Log.Write($"required files: System.dat {File}: {Count} files for {bySystem.Count} systems");
        }
        catch (Exception ex) { Log.Write($"required files: System.dat could not be read: {ex.Message}"); bySystem.Clear(); byName.Clear(); Count = 0; }
    }

    // game ( name "<system>" ... rom ( name <file> size <n> crc <x> md5 <x> sha1 <x> ) ... )
    static void Parse(string text)
    {
        var tok = Regex.Matches(text, @"""([^""]*)""|\(|\)|[^\s()""]+").Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Value).ToList();
        string system = null; int depth = 0;
        for (int i = 0; i < tok.Count; i++)
        {
            string t = tok[i];
            if (t == "(") { depth++; continue; }
            if (t == ")") { if (--depth <= 0) { depth = 0; system = null; } continue; }
            if (depth == 0 && t.Equals("game", StringComparison.OrdinalIgnoreCase)) { system = ""; continue; }
            if (depth == 1 && system == "" && t == "name" && i + 1 < tok.Count) { system = tok[++i]; continue; }
            if (depth == 1 && system is { Length: > 0 } && t == "rom" && i + 1 < tok.Count && tok[i + 1] == "(")
            {
                string name = null, md5 = null; int j = i + 2;
                for (; j < tok.Count && tok[j] != ")"; j++)
                {
                    if (tok[j] == "name" && j + 1 < tok.Count) name = tok[++j];
                    else if (tok[j] == "md5" && j + 1 < tok.Count) md5 = tok[++j].ToLowerInvariant();
                }
                i = j;   // the rom's ")"
                if (md5 == null || !Regex.IsMatch(md5, "^[0-9a-f]{32}$")) continue;
                Add(bySystem, system, md5);
                if (name != null) Add(byName, Path.GetFileName(name.Replace('/', '\\')), md5);
                Count++;
            }
        }
    }

    static void Add(Dictionary<string, HashSet<string>> d, string k, string v) { if (!d.TryGetValue(k, out var s)) d[k] = s = new(); s.Add(v); }

    public static bool HasSystem(string system) => system != null && bySystem.ContainsKey(system);
    public static bool InSystem(string md5, string system) => system != null && bySystem.TryGetValue(system, out var s) && s.Contains(md5);
    public static bool ListsName(string name) => name != null && byName.ContainsKey(name);
    public static bool Known(string md5, string name) => name != null && byName.TryGetValue(name, out var s) && s.Contains(md5);
    public static IEnumerable<string> Md5sOf(string system) => system != null && bySystem.TryGetValue(system, out var s) ? s : Enumerable.Empty<string>();
}

// Settings > Emulators > <emulator> > Required files, and after adding an emulator that lacks something.
// Each needed file with Ready / Missing; A on one picks it (a file, a zip or a folder), the two Add rows take
// a file / zip or a folder and fill whatever in it fits.
public sealed class RequiredFilesCard : MenuCard
{
    const int MaxRows = 8;
    readonly Emulator e;
    readonly Action back;

    public RequiredFilesCard(MainWindow w, Emulator emu, Action back = null, string pick = null, DefFile only = null) : base(w, "Required files", 1180)
    {
        e = emu; this.back = back;
        if (pick == null) { Build(null); return; }
        LoadThen(() => ReqFiles.Pick(e, pick, only), Build);
    }

    void Build(ReqPick done)
    {
        Note(e.Name, 26, new Thickness(4, 0, 0, 16));
        var list = ReqFiles.Of(e);
        var shown = list.OrderBy(f => f.Optional ? 1 : 0).Take(MaxRows).ToList();
        foreach (var f in shown)
        {
            var file = f;
            var r = AddRow(file.Name, () => ReqFiles.StateName(ReqFiles.State(e, file)), activate: () => PickFor(file));
            var st = ReqFiles.State(e, file);
            if (st == ReqState.Missing) r.ValueText.Foreground = P.Error;
            else if (st == ReqState.Optional) r.ValueText.Foreground = P.Text2;
        }
        if (list.Count > shown.Count) Note($"and {list.Count - shown.Count} more", 22, new Thickness(4, 2, 0, 8));
        Divider();
        AddRow("Add a file or zip", activate: () => FileDialog(null, p => Again(p, null)));
        AddRow("Add a folder", activate: () => FolderDialog(null, p => Again(p, null)));
        AddRow("Done", activate: Leave);
        if (done == null) return;
        if (done.Error != null) w.Dispatcher.BeginInvoke(() => new MessageCard(w, "Not the right file", done.Error).Open());
        else { Sounds.Play("favorite"); w.Toasts.Show("Ready", $"{e.Name}: {string.Join(", ", done.Added)}", "plus"); }
    }

    void PickFor(DefFile f)
    {
        if (ReqFiles.PicksFolder(f)) FolderDialog(f, p => Again(p, f));
        else FileDialog(f, p => Again(p, f));
    }

    void Again(string path, DefFile only) { Close(false); new RequiredFilesCard(w, e, back, path, only).Open(); }

    void FileDialog(DefFile f, Action<string> done) =>
        Picker.File(w, f == null ? $"Files for {e.Name}" : f.Name, "Files and zips|*.*|Zip (*.zip; *.7z)|*.zip;*.7z", null, null, done);

    void FolderDialog(DefFile f, Action<string> done) =>
        Picker.Folder(w, f == null ? $"A folder with files for {e.Name}" : f.Name, null, done);

    void Leave() { Close(); back?.Invoke(); }

    public override void OnAct(Act a)
    {
        if (Loading) return;   // a copy or an install is running
        if (a == Act.Back) { Leave(); return; }
        base.OnAct(a);
    }

    // For tests: as if the user had picked this path (on one entry's row, or with the Add rows).
    public static void PickForTest(MainWindow w, Emulator e, string path, string entry)
    {
        var f = entry == null ? null : ReqFiles.Of(e).FirstOrDefault(x => x.Name == entry);
        if (w.TopScreen is RequiredFilesCard c) c.Close(false);
        new RequiredFilesCard(w, e, null, path, f).Open();
    }
}

// The first start (and adding several emulators at once): every emulator that lacks a needed file.
public sealed class RequiredOverviewCard : MenuCard
{
    readonly Action then;

    public static List<Emulator> Lacking(MainWindow w) => w.Emus.Sorted().Where(e => ReqFiles.Missing(e) > 0).ToList();

    public RequiredOverviewCard(MainWindow w, Action then = null, IEnumerable<Emulator> only = null) : base(w, "Required files", 1080)
    {
        this.then = then;
        var list = Lacking(w).Where(e => only == null || only.Contains(e)).ToList();
        if (list.Count == 0) Note("Everything is ready.", 28, new Thickness(4, 0, 0, 18));
        foreach (var e in list)
        {
            var em = e;
            var r = AddRow(em.Name, () => { int n = ReqFiles.Missing(em); return n == 0 ? "Ready" : $"{n} missing"; },
                activate: () => { Close(false); new RequiredFilesCard(w, em, () => new RequiredOverviewCard(w, then, only).Open()).Open(); });
            if (ReqFiles.Missing(em) > 0) r.ValueText.Foreground = P.Error;
        }
        AddRow("Done", activate: Leave);
    }

    void Leave() { w.Settings.FilesAsked = true; w.Settings.Save(); Close(); then?.Invoke(); }

    public override void OnAct(Act a)
    {
        if (a == Act.Back) { Leave(); return; }
        base.OnAct(a);
    }
}
