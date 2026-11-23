#nullable disable
// Storage places: folders that hold game files (this PC, other drives, network shares).
// A game can be moved between places to free space without deleting it. Every place is sorted the
// same way: <place>\<system folder>\<game file>, e.g. <place>\nds\Some Game.nds.
// A move copies the game file and its companions (same name: .sav etc., plus the tracks a .cue/.m3u
// lists), checks the copy byte for byte (SHA-256), points the library at it, and only then deletes
// the originals. A cancelled or failed move leaves the game where it was.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using static Ionea.Loc;

namespace Ionea;

public sealed class Place
{
    public string Name { get; set; }
    public string Path { get; set; }
}

public static class Storage
{
    // First run: this PC's game folder (ES-DE's ROMs when ES-DE is set, else <system drive>\Ionea\ROMs) and every
    // other fixed drive's \Ionea\ROMs. Other places (a network share) are added in Settings > Storage.
    public static List<Place> Defaults(string esdeDir)
    {
        string sys = System.IO.Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\";
        string first = esdeDir != null ? System.IO.Path.Combine(esdeDir, "ROMs") : System.IO.Path.Combine(sys, "Ionea", "ROMs");
        var list = new List<Place> { new() { Name = T("settings.this_pc_drive", System.IO.Path.GetPathRoot(first)?.TrimEnd('\\')), Path = first } };
        string home = System.IO.Path.GetPathRoot(list[0].Path)?.ToUpperInvariant();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady || d.Name.ToUpperInvariant() == home) continue;
                string letter = d.Name.TrimEnd('\\');
                list.Add(new Place { Name = $"{letter} drive", Path = System.IO.Path.Combine(d.Name, "Ionea", "ROMs") });
            }
            catch { /* drive not ready */ }
        }
        return list;
    }

    public static List<Place> Places(MainWindow w)
    {
        if (w.Settings.Places == null || w.Settings.Places.Count == 0) { w.Settings.Places = Defaults(w.Opt.EsdeDir); w.Settings.Save(); }
        return w.Settings.Places;
    }

    static string Norm(string p) => System.IO.Path.GetFullPath(p).TrimEnd('\\') + "\\";

    // The place a file is in (the longest matching root), or null.
    public static Place PlaceOf(MainWindow w, string file)
    {
        if (string.IsNullOrEmpty(file)) return null;
        try
        {
            string f = System.IO.Path.GetFullPath(file);
            return Places(w).Where(p => f.StartsWith(Norm(p.Path), StringComparison.OrdinalIgnoreCase))
                            .OrderByDescending(p => p.Path.Length).FirstOrDefault();
        }
        catch { return null; }
    }

    // Where the game goes inside a place: the same sub-folders as now (for a game already in a place),
    // otherwise the system's folder name.
    public static string SubPath(MainWindow w, Game g)
    {
        string dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(g.Rom));
        var from = PlaceOf(w, g.Rom);
        if (from != null)
        {
            string rel = System.IO.Path.GetRelativePath(Norm(from.Path), dir);
            return rel == "." ? "" : rel;
        }
        return g.System ?? System.IO.Path.GetFileName(dir);
    }

    // Can the place be reached right now? A sleeping computer takes ~20 s to fail, so checks run in the
    // background, all at once, and their answer is kept for a minute. Waits at most ms for an answer.
    static readonly Dictionary<string, (Task<bool> task, long at)> checks = new(StringComparer.OrdinalIgnoreCase);

    static Task<bool> Check(Place p)
    {
        string root = Root(p.Path);
        lock (checks)
        {
            if (checks.TryGetValue(root, out var c) && (!c.task.IsCompleted || Environment.TickCount64 - c.at < 60_000)) return c.task;
            var t = Task.Run(() => { try { return Directory.Exists(root); } catch { return false; } });
            checks[root] = (t, Environment.TickCount64);
            return t;
        }
    }

    public static void CheckAll(MainWindow w) { foreach (var p in Places(w)) Check(p); }

    public static bool Reachable(Place p, int ms = 3000)
    {
        var t = Check(p);
        return t.Wait(ms) && t.Result;
    }

    // Every place at once: reachable, and its free space (-1 when not reachable or unknown).
    public static Dictionary<Place, (bool up, long free)> Probe(List<Place> places)
    {
        var tasks = places.Select(p => Task.Run(() => { bool up = Reachable(p); return (p, up, free: up ? Free(p) : -1L); })).ToArray();
        Task.WaitAll(tasks);
        return tasks.ToDictionary(t => t.Result.p, t => (t.Result.up, t.Result.free));
    }

    // The part of a path that must already exist: a drive (C:\) or a share (\\host\share).
    static string Root(string path)
    {
        if (path.StartsWith(@"\\"))
        {
            var parts = path.TrimStart('\\').Split('\\');
            return parts.Length >= 2 ? $@"\\{parts[0]}\{parts[1]}" : path;
        }
        return System.IO.Path.GetPathRoot(path);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetDiskFreeSpaceExW(string dir, out ulong free, out ulong total, out ulong totalFree);

    public static long Free(Place p)
    {
        try { return GetDiskFreeSpaceExW(Root(p.Path) + "\\", out ulong free, out _, out _) ? (long)free : -1; } catch { return -1; }
    }

    public static string Size(long b) => b < 0 ? "?" : b >= 1L << 30 ? $"{b / (double)(1L << 30):0.#} GB" : b >= 1 << 20 ? $"{b / (double)(1 << 20):0} MB" : $"{Math.Max(1, b / 1024)} KB";

    // The game file and the files that belong with it.
    public static List<string> FilesOf(Game g)
    {
        var rom = System.IO.Path.GetFullPath(g.Rom);
        string dir = System.IO.Path.GetDirectoryName(rom), stem = System.IO.Path.GetFileNameWithoutExtension(rom);
        var set = new List<string> { rom };
        void Add(string f) { if (File.Exists(f) && !set.Contains(f, StringComparer.OrdinalIgnoreCase)) set.Add(f); }
        foreach (var f in Directory.EnumerateFiles(dir))
            if (string.Equals(System.IO.Path.GetFileNameWithoutExtension(f), stem, StringComparison.OrdinalIgnoreCase)) Add(f);
        // Tracks listed by a .cue / .gdi / .m3u (only files in the same folder). A list found through another
        // list is read too: an .m3u names the discs' .cue files, and each .cue names its .bin tracks.
        for (int i = 0; i < set.Count; i++)
        {
            string list = set[i];
            if (!(list.EndsWith(".cue", StringComparison.OrdinalIgnoreCase) || list.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase) || list.EndsWith(".gdi", StringComparison.OrdinalIgnoreCase))) continue;
            try
            {
                foreach (var line in File.ReadLines(list))
                {
                    string l = line.Trim(), name = null;
                    if (l.StartsWith("FILE ", StringComparison.OrdinalIgnoreCase))
                    {
                        int a = l.IndexOf('"'), b = l.LastIndexOf('"');
                        name = a >= 0 && b > a ? l[(a + 1)..b] : l[5..].Split(' ')[0];
                    }
                    else if (list.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase) && l.Length > 0 && !l.StartsWith("#")) name = l;
                    else if (list.EndsWith(".gdi", StringComparison.OrdinalIgnoreCase))
                    {
                        // track, sector, type, sector size, then the file name (quoted when it has spaces)
                        var m = System.Text.RegularExpressions.Regex.Match(l, @"^\d+\s+\d+\s+\d+\s+\d+\s+(""[^""]+""|\S+)");
                        if (m.Success) name = m.Groups[1].Value.Trim('"');
                    }
                    if (name != null && name.IndexOfAny(new[] { '\\', '/', ':' }) < 0) Add(System.IO.Path.Combine(dir, name));
                }
            }
            catch (Exception caught) { Log.Debug("storage: playlist", caught); }
        }
        return set;
    }

    public static long SizeOf(Game g) { try { return FilesOf(g).Sum(f => new FileInfo(f).Length); } catch { return 0; } }

    // A PS4 game is a whole folder (eboot.bin and the rest): moving only its eboot.bin would break it.
    public static bool CanMove(Game g) => g.Source == "rom" && g.Rom != null && !RomLogic.IsEboot(g.Rom);

    // Move one game. Runs off the UI thread; commit (point the library at the new file) is called
    // once every file is safely at the destination, before the originals are deleted.
    public static void Move(Game g, string destDir, Action<long> progress, Action<string> commit, CancellationToken cancel)
    {
        var files = FilesOf(g);
        Directory.CreateDirectory(destDir);
        string rom = System.IO.Path.GetFullPath(g.Rom);
        if (string.Equals(System.IO.Path.GetDirectoryName(rom).TrimEnd('\\'), System.IO.Path.GetFullPath(destDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The game is already there.");
        bool sameDrive = !rom.StartsWith(@"\\") && !destDir.StartsWith(@"\\") &&
            string.Equals(System.IO.Path.GetPathRoot(rom), System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(destDir)), StringComparison.OrdinalIgnoreCase);

        // Nothing at the destination may be overwritten unless it is the very same file.
        var plan = new List<(string from, string to, bool copy)>();
        foreach (var f in files)
        {
            string to = System.IO.Path.Combine(destDir, System.IO.Path.GetFileName(f));
            if (File.Exists(to))
            {
                if (new FileInfo(to).Length != new FileInfo(f).Length || !Hash(f, cancel).AsSpan().SequenceEqual(Hash(to, cancel)))
                    throw new InvalidOperationException($"A different file called \"{System.IO.Path.GetFileName(f)}\" is already there.");
                plan.Add((f, to, false));
            }
            else plan.Add((f, to, true));
        }

        if (sameDrive)
        {
            // On the same drive the originals themselves are moved, so a failure part way (a file in use) moves
            // the ones already moved back; they are never deleted.
            var moved = new List<(string from, string to)>();
            try
            {
                foreach (var (from, to, copy) in plan) if (copy) { File.Move(from, to); moved.Add((from, to)); }
            }
            catch
            {
                for (int i = moved.Count - 1; i >= 0; i--)
                    try { File.Move(moved[i].to, moved[i].from); }
                    catch (Exception e) { Log.Write($"storage: couldn't move {moved[i].to} back to {moved[i].from}: {e.Message}"); }
                throw;
            }
            commit(System.IO.Path.Combine(destDir, System.IO.Path.GetFileName(rom)));
            foreach (var (from, _, copy) in plan) if (!copy) TryDelete(from);
            return;
        }

        var made = new List<string>();
        long done = 0;
        try
        {
            foreach (var (from, to, copy) in plan)
            {
                if (!copy) { done += new FileInfo(from).Length; progress(done); continue; }
                string part = to + ".moving";
                made.Add(part);
                byte[] want = CopyHashed(from, part, cancel, n => { done += n; progress(done); });
                byte[] got = Hash(part, cancel);   // read the copy back: it must match the original exactly
                if (!want.AsSpan().SequenceEqual(got)) throw new IOException($"The copy of \"{System.IO.Path.GetFileName(from)}\" didn't match the original.");
                File.Move(part, to);
                made.Remove(part); made.Add(to);
            }
        }
        catch
        {
            foreach (var f in made) TryDelete(f);   // leave nothing half-moved behind; the originals are untouched
            throw;
        }
        commit(System.IO.Path.Combine(destDir, System.IO.Path.GetFileName(rom)));
        foreach (var (from, _, _) in plan) TryDelete(from);
    }

    static void TryDelete(string f)
    {
        try { File.Delete(f); } catch (Exception e) { Log.Write($"storage: couldn't delete {f}: {e.Message}"); }
    }

    static byte[] CopyHashed(string from, string to, CancellationToken cancel, Action<long> step)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using (var src = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
        using (var dst = new FileStream(to, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            var buf = new byte[4 << 20];
            int n;
            while ((n = src.Read(buf, 0, buf.Length)) > 0)
            {
                cancel.ThrowIfCancellationRequested();
                h.AppendData(buf, 0, n);
                dst.Write(buf, 0, n);
                step(n);
            }
            dst.Flush(true);
        }
        return h.GetHashAndReset();
    }

    static byte[] Hash(string f, CancellationToken cancel)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var s = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        var buf = new byte[4 << 20];
        int n;
        while ((n = s.Read(buf, 0, buf.Length)) > 0) { cancel.ThrowIfCancellationRequested(); h.AppendData(buf, 0, n); }
        return h.GetHashAndReset();
    }
}

// Pick where a game (or several) should go.
public sealed class PlacePickCard : MenuCard
{
    public PlacePickCard(MainWindow w, List<Game> games, string title) : base(w, title, 1000)
    {
        var here = games.Select(g => Storage.PlaceOf(w, g.Rom)).Distinct().ToList();
        var places = Storage.Places(w).Where(p => !(here.Count == 1 && here[0] == p)).ToList();
        LoadThen(() => (st: Storage.Probe(places), need: games.Sum(Storage.SizeOf)), got =>
        {
            long need = got.need;
            foreach (var p in places)
            {
                var place = p;
                var (up, free) = got.st != null && got.st.TryGetValue(place, out var x) ? x : (false, -1L);
                AddRow(place.Name, () => !up ? T("settings.not_connected") : $"{Storage.Size(free)} free", activate: () =>
                {
                    if (!up) { new MessageCard(w, T("settings.isn_t_connected", place.Name), T("settings.can_t_reach_right_now", place.Path)).Open(); return; }
                    if (free >= 0 && free < need + (256L << 20)) { new MessageCard(w, T("settings.not_enough_space"), T("settings.has_free_the_game_files_need", place.Name, Storage.Size(free), Storage.Size(need))).Open(); return; }
                    Close(false);
                    new MoveCard(w, games, place).Open();
                });
            }
            AddRow(T("common.cancel"), activate: () => Close());
            Footer($"{(games.Count == 1 ? "The game" : games.Count + " games")}: {Storage.Size(need)}");
        });
    }
}

// The move itself, with progress. B cancels (the games stay where they were).
public sealed class MoveCard : Card, IScreen
{
    readonly List<Game> games;
    readonly Place to;
    readonly TextBlock line, amount;
    readonly Border bar;
    readonly CancellationTokenSource cancel = new();
    const double BarW = 880;
    public static bool Busy { get; private set; }
    public static string LastResult { get; private set; } = "";

    public MoveCard(MainWindow w, List<Game> list, Place place) : base(w, list.Count == 1 ? T("settings.moving_to", place.Name) : T("settings.moving_games_to", list.Count, place.Name), 1000)
    {
        games = list; to = place;
        line = Note(list[0].Name, 30, new Thickness(4, 0, 0, 18));
        line.Foreground = P.Text;
        var track = new Border { Width = BarW, Height = 14, CornerRadius = new CornerRadius(7), Background = P.RowFocus, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(4, 0, 0, 14) };
        bar = new Border { Width = 0, Height = 14, CornerRadius = new CornerRadius(7), Background = P.Bar, HorizontalAlignment = HorizontalAlignment.Left };
        track.Child = bar;
        Body.Children.Add(track);
        amount = Note("", 24);
    }

    public override void Open()
    {
        base.Open();
        Busy = true; LastResult = "moving";
        long total = Math.Max(1, games.Sum(Storage.SizeOf));
        Task.Run(() => Work(total));
    }

    void Work(long total)
    {
        long before = 0; int moved = 0; string error = null;
        long lastUi = 0;
        foreach (var g in games)
        {
            if (cancel.IsCancellationRequested) break;
            if (w.Dispatcher.Invoke(() => w.GameMenu.Game == g)) { error = $"\"{g.Name}\" is running. Close it first."; continue; }
            long size = Storage.SizeOf(g);
            string dest = Path.Combine(to.Path, w.Dispatcher.Invoke(() => Storage.SubPath(w, g)));
            w.Dispatcher.BeginInvoke(() => line.Text = g.Name);
            try
            {
                string from = g.Rom;
                Storage.Move(g, dest, n =>
                {
                    long now = Environment.TickCount64;
                    if (now - lastUi < 100) return;
                    lastUi = now;
                    long d = before + n;
                    w.Dispatcher.BeginInvoke(() => { bar.Width = BarW * Math.Min(1, d / (double)total); amount.Text = $"{Storage.Size(d)} of {Storage.Size(total)}"; });
                }, newRom => w.Dispatcher.Invoke(() =>
                {
                    g.Rom = newRom; w.Lib.Save();
                    w.Profiles.RomMoved(from, newRom);
                    Log.Write($"storage: moved \"{g.Name}\" {from} -> {newRom}");
                }), cancel.Token);
                moved++;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e) { error = $"\"{g.Name}\": {e.Message}"; Log.Write($"storage: move \"{g.Name}\" to {to.Name} failed: {e.Message}"); }
            before += size;
        }
        w.Dispatcher.BeginInvoke(() => Finish(moved, error));
    }

    void Finish(int moved, string error)
    {
        Busy = false;
        bool cancelled = cancel.IsCancellationRequested;
        LastResult = error != null ? "failed" : cancelled ? "cancelled" : "done";
        Close(false);
        if (error != null) new MessageCard(w, moved > 0 ? T("settings.moved_of", moved, games.Count) : T("settings.couldn_t_move_the_game"), error).Open();
        else if (cancelled) w.Toasts.Show(T("settings.move_cancelled"), moved > 0 ? T("settings.moved_the_rest_stayed_where_they", moved) : T("settings.nothing_was_moved"), "info");
        else w.Toasts.Show(T("settings.moved_to_2", to.Name), games.Count == 1 ? games[0].Name : $"{moved} games", "folder");
        Sounds.Play(error == null && !cancelled ? "select" : "back");
    }

    public override void OnAct(Act a)
    {
        if (a == Act.Back && !cancel.IsCancellationRequested) { cancel.Cancel(); line.Text = T("settings.cancelling"); Sounds.Play("back"); }
    }

    public override void Close(bool sound = true)
    {
        if (Busy) return;   // closes itself when the work has stopped
        base.Close(sound);
    }
}

// Settings > Storage: every place with its free space and games; A on a place moves all its games.
public sealed class StorageCard : MenuCard
{
    public StorageCard(MainWindow w) : base(w, T("settings.storage"), 1100)
    {
        var places = Storage.Places(w).ToList();
        LoadThen(() => Storage.Probe(places), st => Build(places, st));
    }

    void Build(List<Place> places, Dictionary<Place, (bool up, long free)> st)
    {
        var games = w.Lib.Data.Games.Where(g => !g.Removed && Storage.CanMove(g)).ToList();
        foreach (var p in places)
        {
            var place = p;
            var mine = games.Where(g => Storage.PlaceOf(w, g.Rom) == place).ToList();
            var (up, freeB) = st != null && st.TryGetValue(place, out var x) ? x : (false, -1L);
            string free = up ? Storage.Size(freeB) + " free" : "not connected";
            string n = mine.Count == 1 ? "1 game" : $"{mine.Count} games";
            AddRow(place.Name, () => $"{n}  ·  {free}", activate: () =>
            {
                if (mine.Count == 0) { new MessageCard(w, place.Name, T("settings.no_games_are_stored_here_yet", place.Path)).Open(); return; }
                if (!up) { new MessageCard(w, T("settings.isn_t_connected", place.Name), T("settings.can_t_reach_right_now", place.Path)).Open(); return; }
                Close(false);
                new PlacePickCard(w, mine, T("settings.move_all_from_to", n, place.Name)).Open();
            });
        }
        int elsewhere = games.Count(g => Storage.PlaceOf(w, g.Rom) == null);
        if (elsewhere > 0) Note(Loc.N("settings.games_in_other_folders", elsewhere), 22, new Thickness(4, 10, 0, 0));
        AddRow(T("settings.add_a_place"), activate: () =>
        {
            Close(false);
            new TextCard(w, T("settings.folder_for_games_e_g_f"), "", 200, path =>
            {
                try
                {
                    string full = System.IO.Path.GetFullPath(path);
                    string name = full.StartsWith(@"\\") ? full.TrimStart('\\').Split('\\')[0] : System.IO.Path.GetPathRoot(full).TrimEnd('\\') + " " + System.IO.Path.GetFileName(full.TrimEnd('\\'));
                    Storage.Places(w).Add(new Place { Name = name.Trim(), Path = full });
                    w.Settings.Save();
                    Log.Write($"storage: added place {name} = {full}");
                }
                catch (Exception e) { new MessageCard(w, T("settings.that_isn_t_a_folder_path"), e.Message).Open(); return; }
                new StorageCard(w).Open();
            }).Open();
        });
    }
}
