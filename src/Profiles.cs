#nullable disable
// Profiles: who is playing. A profile holds only that person's game saves, save states and play
// stats (plus a name and a picture). The library, emulators and launcher settings are shared.
//   data\profiles.json            the list, the active profile, who owns the saves next to each game file
//   data\profiles\<id>\           picture.png, stats.json, saves\, states\, save-backups\
// Saves made before profiles existed belong to the first profile (the "owner").
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media.Imaging;
using static Ionea.Loc;

namespace Ionea;

public sealed class Profile
{
    public string Id { get; set; }
    public string Name { get; set; }
    public bool Picture { get; set; }       // picture.png in the profile's folder
    public int Hue { get; set; }            // colour of the circle while there's no picture
    public DateTime Created { get; set; }
}

public sealed class ProfilesFile
{
    public int Version { get; set; } = 1;
    public string Active { get; set; }
    public string Owner { get; set; }       // the first profile: it took over the saves made before profiles
    public List<Profile> Profiles { get; set; } = new();
    // Standalone emulators that keep saves next to the game file: whose saves sit there now
    // (ROM file name, lower case -> profile id). Missing = the owner's.
    public Dictionary<string, string> Beside { get; set; } = new();
}

public sealed class GameStat
{
    public double Seconds { get; set; }
    public int Sessions { get; set; }       // plays of a minute or more (older records counted every start)
    public DateTime? First { get; set; }
    public DateTime? Last { get; set; }
    public double Longest { get; set; }     // the longest single session (old records: their average, see Upgrade)
}

// A session still going: written at every checkpoint, so a launcher that closes mid-game loses at most
// one interval. Its time is already in Games and Days; at the next load it only becomes a session.
public sealed class OpenPlay
{
    public string Game { get; set; }
    public DateTime Start { get; set; }
    public double Seconds { get; set; }
}

public sealed class StatsFile
{
    public int Version { get; set; }        // 2 = the personal stats
    public Dictionary<string, GameStat> Games { get; set; } = new();
    public Dictionary<string, Dictionary<string, double>> Days { get; set; } = new();   // "yyyy-MM-dd" -> game id -> seconds
    public OpenPlay Open { get; set; }
}

public sealed class Profiles
{
    public readonly string DataDir, Root;
    public ProfilesFile Data;
    public StatsFile Stats = new();         // the active profile's
    string FilePath => Path.Combine(DataDir, "profiles.json");

    public Profiles(string dataDir)
    {
        DataDir = dataDir;
        Root = Path.Combine(dataDir, "profiles");
        Data = Json.Load<ProfilesFile>(FilePath);
        Data.Beside ??= new();
        if (Active == null && Data.Profiles.Count > 0) Data.Active = Data.Profiles[0].Id;
        Stats = LoadStats(Active);
    }

    public Profile Active => Data.Profiles.FirstOrDefault(p => p.Id == Data.Active);
    public bool Any => Data.Profiles.Count > 0;
    public Profile Get(string id) => Data.Profiles.FirstOrDefault(p => p.Id == id);
    public Profile ByName(string name) => Data.Profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase));
    public List<Profile> List => Data.Profiles.OrderBy(p => p.Created).ToList();

    public string Folder(Profile p) => Path.Combine(Root, p.Id);
    public string PicturePath(Profile p) => Path.Combine(Folder(p), "picture.png");
    public string CoreSaves => Path.Combine(Folder(Active), "saves", "cores");
    public string StatesDir => Path.Combine(Folder(Active), "states");
    public string BackupsDir => Path.Combine(Folder(Active), "save-backups");

    public void Save() { try { Json.Save(FilePath, Data); } catch (Exception e) { Log.Write("profiles save: " + e.Message); } }

    // ---- names ----
    public const int MaxName = 20;
    public static string Clean(string s) => Regex.Replace(s ?? "", @"\s+", " ").Trim();

    // null when the name can be used; otherwise what to tell the user.
    public string CheckName(string name, string exceptId = null)
    {
        name = Clean(name);
        if (name.Length == 0) return T("settings.type_a_name_first");
        if (name.Length > MaxName) return T("settings.keep_it_to_letters", MaxName);
        var other = ByName(name);
        if (other != null && other.Id != exceptId) return T("settings.there_s_already_a_profile_called", other.Name);
        return null;
    }

    // ---- create / switch / change / remove ----
    static readonly int Hues = Art.AvatarHues;

    public int NextHue()
    {
        var used = Data.Profiles.Select(p => p.Hue).ToHashSet();
        return Enumerable.Range(0, Hues).Where(h => !used.Contains(h)).DefaultIfEmpty(Data.Profiles.Count % Hues).First();
    }

    public Profile Create(string name, BitmapSource picture, IEnumerable<string> coreSaveDirs, Library lib)
    {
        name = Clean(name);
        if (CheckName(name) is string err) throw new InvalidOperationException(err);
        var p = new Profile { Id = Library.NewId(), Name = name, Hue = NextHue(), Created = DateTime.Now };
        Directory.CreateDirectory(Folder(p));
        if (picture != null) { Art.SavePng(picture, PicturePath(p)); p.Picture = true; }
        bool first = Data.Owner == null;
        Data.Profiles.Add(p);
        if (first) Data.Owner = p.Id;
        Save();
        Log.Write($"profile \"{name}\" created ({p.Id}{(first ? ", owns the saves made before profiles" : "")})");
        if (first) TakeOverOldSaves(p, coreSaveDirs, lib);
        return p;
    }

    public void Switch(Profile p)
    {
        if (p == null || p.Id == Data.Active) return;
        Data.Active = p.Id; Save();
        Stats = LoadStats(p);
        Log.Write($"profile: now \"{p.Name}\"");
        Switched?.Invoke();
    }
    public event Action Switched;   // MainWindow loads the new profile's own settings
    public string SettingsFile(Profile p) => Path.Combine(Folder(p), "settings.json");

    public void Rename(Profile p, string name) { p.Name = Clean(name); Save(); Log.Write($"profile {p.Id} renamed \"{p.Name}\""); }

    public void SetPicture(Profile p, BitmapSource picture)
    {
        try
        {
            if (picture != null) { Art.SavePng(picture, PicturePath(p)); p.Picture = true; }
            else { if (File.Exists(PicturePath(p))) File.Delete(PicturePath(p)); p.Picture = false; }
        }
        catch (Exception e) { Log.Write("profile picture: " + e.Message); }
        Save();
        Art.ForgetAvatar(p.Id);
    }

    // Square crop of any picture, 512 px.
    public static BitmapSource PictureFrom(string file)
    {
        var src = Art.LoadImage(file, 0);
        if (src == null) return null;
        const int n = 512;
        return Art.Render(n, n, dc =>
        {
            double sc = Math.Max((double)n / src.PixelWidth, (double)n / src.PixelHeight);
            double w = src.PixelWidth * sc, h = src.PixelHeight * sc;
            dc.DrawImage(src, new Rect((n - w) / 2, (n - h) / 2, w, h));
        });
    }

    // Removes a profile that isn't in use: its saves (also those sitting next to game files) go to the Recycle Bin.
    public string Remove(Profile p, Library lib)
    {
        if (p == null) return T("settings.no_such_profile");
        if (p.Id == Data.Active) return T("settings.switch_to_another_profile_first");
        if (Data.Profiles.Count < 2) return T("settings.the_last_profile_can_t_be");
        // Its saves next to game files go into its own folder first, so they leave with it.
        foreach (var g in lib.Data.Games.Where(g => g.Source == "rom" && g.Rom != null))
        {
            if (WhoseBeside(g.Rom) != p.Id) continue;
            try { foreach (var f in ProfileSaves.BesideFiles(g.Rom)) MoveFile(f, Path.Combine(ProfileSaves.Stash(this, p.Id, g.Rom), Path.GetFileName(f))); }
            catch (Exception e) { return T("settings.couldn_t_take_s_save_for", p.Name, g.Name, e.Message); }
            Data.Beside[ProfileSaves.Key(g.Rom)] = Nobody;   // nothing of anyone's is left there
        }
        foreach (var k in Data.Beside.Where(kv => kv.Value == p.Id).Select(kv => kv.Key).ToList()) Data.Beside.Remove(k);
        string err = Directory.Exists(Folder(p)) ? Uninstall.Recycle(new List<string> { Folder(p) }) : null;
        if (err != null) { Save(); return err; }
        Data.Profiles.Remove(p);
        if (Data.Owner == p.Id) Data.Owner = Data.Active;
        Save();
        Art.ForgetAvatar(p.Id);
        Log.Write($"profile \"{p.Name}\" removed (folder to the Recycle Bin)");
        return null;
    }

    public const string Nobody = "nobody";
    public string WhoseBeside(string rom) => Data.Beside.TryGetValue(ProfileSaves.Key(rom), out var id) ? id : Data.Owner;

    // The first profile takes the saves made before profiles: the cores' save folders are copied (the
    // originals stay as they are), save states are moved, and "last played" carries over for sorting.
    void TakeOverOldSaves(Profile p, IEnumerable<string> coreSaveDirs, Library lib)
    {
        string to = Path.Combine(Folder(p), "saves", "cores");
        foreach (string dir in coreSaveDirs.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try { int n = CopyTree(dir, to); Log.Write($"profile: copied {n} save file(s) from {dir}"); }
            catch (Exception e) { Log.Write($"profile: copying saves from {dir}: {e.Message}"); }
        }
        string states = Path.Combine(DataDir, "states");
        if (Directory.Exists(states))
            foreach (var f in Directory.GetFiles(states))
                try { MoveFile(f, Path.Combine(Folder(p), "states", Path.GetFileName(f))); } catch (Exception e) { Log.Write("profile: state " + e.Message); }
        var st = LoadStats(p);
        foreach (var g in lib.Data.Games.Where(g => g.LastPlayed != null))
            st.Games[g.Id] = new GameStat { Last = g.LastPlayed, First = g.LastPlayed };
        SaveStats(p, st);
        if (p.Id == Data.Active) Stats = st;
    }

    public static int CopyTree(string from, string to)
    {
        int n = 0;
        Directory.CreateDirectory(to);
        foreach (var d in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, d)));
        foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            string t = Path.Combine(to, Path.GetRelativePath(from, f));
            if (File.Exists(t)) continue;
            File.Copy(f, t); n++;
        }
        return n;
    }

    public static void MoveFile(string from, string to)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to));
        File.Move(from, to, true);
    }

    // ---- play stats ----
    // How time counts: only while the game really runs. From the moment the game's process
    // (Steam/PC: a process from its install folder) is up until it ends; the clock stops whenever the Home
    // menu pauses the game (and while it waits paused on the Home screen), so paused time never counts.
    // A play under a minute adds its time but is not a session. While playing, the time so far is written
    // every 30 s and at every pause, so a crash or a closed launcher keeps all but the last few seconds.
    public const double MinSession = 60;
    string StatsPath(Profile p) => Path.Combine(Folder(p), "stats.json");
    StatsFile LoadStats(Profile p)
    {
        if (p == null) return new StatsFile();
        var s = Json.Load<StatsFile>(StatsPath(p));
        s.Games ??= new(); s.Days ??= new();
        bool dirty = false;
        if (s.Version < 2) { Upgrade(p, s); dirty = true; }
        if (s.Open != null)   // the launcher closed mid-game: the time is in already, make it a session
        {
            var o = s.Open; s.Open = null;
            if (s.Games.TryGetValue(o.Game ?? "", out var g)) EndSession(g, o.Start, o.Seconds);
            Log.Write($"stats: an open session ({o.Seconds:0} s) from {o.Start:yyyy-MM-dd HH:mm} closed after a restart");
            dirty = true;
        }
        if (dirty) SaveStats(p, s);
        return s;
    }

    // Records from an older version are kept as they are (play time, sessions, first / last, days); a copy
    // of the old file stays as stats.v1.json. Their longest session isn't known: one session is exact, more
    // take the average (never more than the real longest).
    void Upgrade(Profile p, StatsFile s)
    {
        try { if (File.Exists(StatsPath(p))) File.Copy(StatsPath(p), Path.Combine(Folder(p), "stats.v1.json"), false); } catch (Exception caught) { Log.Debug("profiles: stats copy", caught); }
        foreach (var g in s.Games.Values)
            if (g.Longest <= 0 && g.Sessions > 0) g.Longest = g.Seconds / g.Sessions;
        s.Version = 2;
        Log.Write($"stats: \"{p.Name}\" upgraded ({s.Games.Count} games, {s.Days.Count} days kept)");
    }
    void SaveStats(Profile p, StatsFile s) { try { Directory.CreateDirectory(Folder(p)); Json.Save(StatsPath(p), s); } catch (Exception e) { Log.Write("stats save: " + e.Message); } }

    public DateTime? LastPlayed(Game g) => Stats.Games.TryGetValue(g.Id, out var s) ? s.Last : null;
    public double Seconds(string gameId) => Stats.Games.TryGetValue(gameId, out var s) ? s.Seconds : 0;
    public GameStat StatOf(string gameId) => Stats.Games.TryGetValue(gameId ?? "", out var s) ? s : null;

    GameStat Of(string id) { if (!Stats.Games.TryGetValue(id, out var s)) Stats.Games[id] = s = new GameStat(); return s; }
    void AddTime(string id, DateTime at, double seconds)
    {
        if (seconds <= 0) return;
        Of(id).Seconds += seconds;
        string day = at.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (!Stats.Days.TryGetValue(day, out var d)) Stats.Days[day] = d = new();
        d[id] = (d.TryGetValue(id, out var x) ? x : 0) + seconds;
    }
    static void EndSession(GameStat s, DateTime start, double seconds)
    {
        if (s.First == null || start < s.First) s.First = start;
        if (s.Last == null || s.Last < start) s.Last = start;
        if (seconds < MinSession) return;
        s.Sessions++;
        s.Longest = Math.Max(s.Longest, seconds);
    }

    // A whole session at once (tests, dummy history): its time on the day it started.
    public void Record(Game g, DateTime start, double seconds)
    {
        var p = Active;
        if (p == null) return;
        seconds = Math.Max(0, seconds);
        AddTime(g.Id, start, seconds);
        EndSession(Of(g.Id), start, seconds);
        SaveStats(p, Stats);
        Log.Write($"profile \"{p.Name}\": played \"{g.Name}\" {seconds:0} s");
    }

    // The game on screen: time counts while it runs, not while it's paused in the Home menu.
    Game playing; DateTime playStart; Stopwatch playClock; double counted; Profile playFor;
    System.Windows.Threading.DispatcherTimer checkpoints;
    public void BeginPlay(Game g)
    {
        if (playing != null) EndPlay(playing);
        playing = g; playFor = Active; playStart = DateTime.Now; playClock = Stopwatch.StartNew(); counted = 0;
        var s = Of(g.Id);
        s.First ??= playStart;
        s.Last = playStart;   // "recent" order sees it at once
        Stats.Open = new OpenPlay { Game = g.Id, Start = playStart };
        if (playFor != null) SaveStats(playFor, Stats);
        if (checkpoints == null)
        {
            checkpoints = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            checkpoints.Tick += (_, _) => Checkpoint();
        }
        checkpoints.Start();
    }
    // The time played since the last checkpoint goes in, and the file is written.
    public void Checkpoint()
    {
        if (playing == null || playFor == null || playFor.Id != Data.Active) return;
        double now = playClock.Elapsed.TotalSeconds;
        AddTime(playing.Id, DateTime.Now, now - counted);
        counted = now;
        Stats.Open = new OpenPlay { Game = playing.Id, Start = playStart, Seconds = now };
        SaveStats(playFor, Stats);
    }
    public void PausePlay() { Checkpoint(); playClock?.Stop(); }
    public void ResumePlay() { if (playing != null) playClock?.Start(); }
    public void EndPlay(Game g)
    {
        if (playing == null || playing.Id != g.Id) return;
        checkpoints?.Stop();
        Checkpoint();
        double total = playClock.Elapsed.TotalSeconds;
        var p = playFor;
        playing = null; playFor = null;
        if (p == null || p.Id != Data.Active) return;
        EndSession(Of(g.Id), playStart, total);
        Stats.Open = null;
        SaveStats(p, Stats);
        Log.Write($"profile \"{p.Name}\": played \"{g.Name}\" {total:0} s{(total < MinSession ? " (under a minute: not a session)" : "")}");
    }

    // For the profile card.
    public sealed class Summary
    {
        public double Total; public int Days, MonthCount;
        public Game Most, Last;
        public double MostSeconds;
        public DateTime? LastAt;
        public List<Game> Month = new();
    }

    public Summary Summarize(Library lib)
    {
        Game G(string id) => lib.Data.Games.FirstOrDefault(g => g.Id == id);
        var r = new Summary { Total = Stats.Games.Values.Sum(s => s.Seconds), Days = Stats.Days.Count(d => d.Value.Values.Sum() > 0) };
        var most = Stats.Games.Where(kv => kv.Value.Seconds > 0 && G(kv.Key) != null).OrderByDescending(kv => kv.Value.Seconds).FirstOrDefault();
        if (most.Key != null) { r.Most = G(most.Key); r.MostSeconds = most.Value.Seconds; }
        var last = Stats.Games.Where(kv => kv.Value.Last != null && G(kv.Key) != null).OrderByDescending(kv => kv.Value.Last).FirstOrDefault();
        if (last.Key != null) { r.Last = G(last.Key); r.LastAt = last.Value.Last; }
        string since = DateTime.Today.AddDays(-29).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var month = new Dictionary<string, double>();
        foreach (var d in Stats.Days.Where(d => string.CompareOrdinal(d.Key, since) >= 0))
            foreach (var kv in d.Value) month[kv.Key] = (month.TryGetValue(kv.Key, out var x) ? x : 0) + kv.Value;
        r.Month = month.OrderByDescending(kv => kv.Value).Select(kv => G(kv.Key)).Where(g => g != null).ToList();
        r.MonthCount = r.Month.Count;
        return r;
    }

    public static string Hours(double seconds)
    {
        if (seconds < 60) return seconds < 1 ? "0 min" : "under a minute";
        int m = (int)(seconds / 60), h = m / 60;
        return h == 0 ? $"{m} min" : m % 60 == 0 ? $"{h} h" : $"{h} h {m % 60} min";
    }
}

// Where a profile's saves live, per kind of emulator.
//  - Cores (inside the launcher): the save directory and battery saves are the profile's saves\cores.
//  - Standalone emulators with save folders (shadPS4, Azahar, Dolphin...): each save folder becomes a
//    junction to the profile's copy before the game starts.
//  - Standalone emulators that save next to the game file (mGBA, melonDS, Snes9x): the file is swapped
//    with the profile's own copy before the game starts.
public static class ProfileSaves
{
    public static readonly string[] SaveExt = { ".sav", ".srm", ".dsv", ".sgm", ".eep", ".fla", ".sra", ".rtc" };

    // Where an emulator keeps saves comes from its definition (saves.folders: relative to its folder; saves.beside:
    // next to the game file). Any emulator with a definition, also one whose version dropped a level: that only
    // stops Ionea writing its config, never mixes two profiles' saves.
    static DefSaves SavesOf(Emulator e) => EmuDefs.Known(e)?.Saves;
    static bool HasFolders(DefSaves s) => s?.Folders != null && s.Folders.Count > 0;
    // Every profile plays on the same saves in it (Prepare's "shared"); cores keep them apart themselves.
    public static bool Shared(Emulator e) => e != null && !e.IsCore && !HasFolders(SavesOf(e)) && SavesOf(e)?.Beside != true;

    public static bool Separates(Emulator e) => e.IsCore || SavesOf(e) is DefSaves s && (HasFolders(s) || s.Beside);

    public static string Key(string rom) => Path.GetFileName(rom).ToLowerInvariant();
    public static string Stash(Profiles pr, string id, string rom) => Path.Combine(pr.Root, id, "saves", "beside", Path.GetFileName(rom));

    public static List<string> BesideFiles(string rom)
    {
        string dir = Path.GetDirectoryName(rom), stem = Path.GetFileNameWithoutExtension(rom);
        if (dir == null || !Directory.Exists(dir)) return new();
        return SaveExt.Select(x => Path.Combine(dir, stem + x)).Where(File.Exists).ToList();
    }

    // ---- cores ----
    public static string CoreSave(Profiles pr, string rom) => Path.Combine(pr.CoreSaves, Path.GetFileNameWithoutExtension(rom) + ".sav");

    // The owner's first start of a core game takes the save that sat next to the game before profiles
    // (a copy; the file next to the game stays).
    public static void TakeBesideSave(Profiles pr, string rom, string to)
    {
        if (File.Exists(to) || pr.WhoseBeside(rom) != pr.Active?.Id) return;
        string old = Path.ChangeExtension(rom, ".sav");
        if (!File.Exists(old)) return;
        try { Directory.CreateDirectory(Path.GetDirectoryName(to)); File.Copy(old, to); Log.Write($"profile: took {Path.GetFileName(old)} from next to the game"); }
        catch (Exception e) { Log.Write("profile: taking the old save: " + e.Message); }
    }

    // ---- standalone emulators: before the game starts ----
    // Returns null when the active profile's saves are in place (shared = this emulator can't keep them apart).
    public static string Prepare(Profiles pr, Emulator emu, string rom, out bool shared)
    {
        shared = false;
        if (pr.Active == null) return null;
        var sv = SavesOf(emu);
        if (HasFolders(sv))
        {
            string root = Path.GetDirectoryName(emu.Exe);
            string key = EmuDefs.Of(emu).Id;   // the profile keeps them under the definition id (= emu.Preset)
            foreach (var rel in sv.Folders)
                if (Link(pr, Path.Combine(root, rel), Path.Combine(key, rel)) is string err) return err;
            return null;
        }
        if (sv?.Beside == true) return rom == null ? null : Swap(pr, rom);
        shared = true;
        return null;
    }

    static string Link(Profiles pr, string path, string rel)
    {
        string mine = Path.Combine(pr.Folder(pr.Active), "saves", rel);
        try
        {
            FileAttributes? attr = null;
            try { attr = File.GetAttributes(path); } catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
            if (attr is FileAttributes a && a.HasFlag(FileAttributes.ReparsePoint))
            {
                if (Same(new DirectoryInfo(path).LinkTarget, mine)) { Directory.CreateDirectory(mine); return null; }
                Directory.Delete(path);   // only the link; the folder it points to stays
            }
            else if (attr is FileAttributes d && d.HasFlag(FileAttributes.Directory))
            {
                // A real folder: the saves made before profiles. They become the owner's.
                var owner = pr.Get(pr.Data.Owner) ?? pr.Active;
                string theirs = Path.Combine(pr.Folder(owner), "saves", rel);
                if (Directory.Exists(theirs) && Directory.EnumerateFileSystemEntries(theirs).Any())
                {
                    string keep = path + ".before-profiles-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    Directory.Move(path, keep);
                    Log.Write($"profile: {path} kept as {keep} ({owner.Name} already had saves there)");
                }
                else
                {
                    if (Directory.Exists(theirs)) Directory.Delete(theirs);
                    Directory.CreateDirectory(Path.GetDirectoryName(theirs));
                    MoveDir(path, theirs);
                    Log.Write($"profile: {path} -> {owner.Name}'s saves");
                }
            }
            else if (attr != null) return $"{path} is a file, not a folder";
            Directory.CreateDirectory(mine);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            Junction(path, mine);
            Log.Write($"profile: {path} -> {mine}");
            return null;
        }
        catch (Exception e) { Log.Write($"profile link {path}: {e}"); return e.Message; }
    }

    public static bool Same(string a, string b)
    {
        if (a == null || b == null) return false;
        static string N(string p) => Path.GetFullPath(p.StartsWith(@"\??\") ? p[4..] : p).TrimEnd('\\');
        return string.Equals(N(a), N(b), StringComparison.OrdinalIgnoreCase);
    }

    static void MoveDir(string from, string to)
    {
        if (string.Equals(Path.GetPathRoot(from), Path.GetPathRoot(to), StringComparison.OrdinalIgnoreCase)) { Directory.Move(from, to); return; }
        // Another drive: copy, then keep the original under a new name (nothing is deleted).
        Profiles.CopyTree(from, to);
        Directory.Move(from, from + ".before-profiles-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
    }

    public static void Junction(string link, string target)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/d /c mklink /J \"{link}\" \"{target}\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi);
        string err = p.StandardError.ReadToEnd(); p.StandardOutput.ReadToEnd();
        p.WaitForExit(10000);
        if (!File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Couldn't link the save folder: " + err.Trim());
    }

    static string Swap(Profiles pr, string rom)
    {
        string me = pr.Active.Id, cur = pr.WhoseBeside(rom);
        if (cur == me) { pr.Data.Beside[Key(rom)] = me; pr.Save(); return null; }
        try
        {
            string theirs = Stash(pr, cur ?? Profiles.Nobody, rom);
            foreach (var f in BesideFiles(rom)) Profiles.MoveFile(f, Path.Combine(theirs, Path.GetFileName(f)));
            string mine = Stash(pr, me, rom);
            if (Directory.Exists(mine))
                foreach (var f in Directory.GetFiles(mine)) File.Move(f, Path.Combine(Path.GetDirectoryName(rom), Path.GetFileName(f)));
            pr.Data.Beside[Key(rom)] = me; pr.Save();
            Log.Write($"profile: saves next to {Path.GetFileName(rom)} are now {pr.Active.Name}'s");
            return null;
        }
        catch (Exception e) { Log.Write($"profile swap {rom}: {e}"); return e.Message; }
    }
}
