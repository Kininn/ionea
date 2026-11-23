#nullable disable
// Steam games. Read-only towards Steam: the registry, libraryfolders.vdf, appmanifest_*.acf and
// appcache\librarycache are only read. Games are always started by Steam itself (steam://rungameid/<appid>);
// the game exe is never started directly, nothing is injected, no input goes to the game. "Running" is
// detected from the outside: a process whose exe lives in the game's install folder (and its window).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using static Ionea.Loc;

namespace Ionea;

public sealed class SteamGame
{
    public string AppId, Name, InstallDir;
}

public static class SteamLib
{
    public static string RootOverride;   // --steam <dir> (test data); otherwise the registry

    public static string Root()
    {
        if (RootOverride != null) return RootOverride;
        foreach (var (hive, key, val) in new[] {
            (Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
            (Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath") })
        {
            try
            {
                using var k = hive.OpenSubKey(key);
                if (k?.GetValue(val) is string p && Directory.Exists(p)) return Path.GetFullPath(p);
            }
            catch { /* no Steam registry key */ }
        }
        return null;
    }

    // ---- KeyValues (VDF) text: "key" "value" | "key" { ... } ----
    public static Dictionary<string, object> ParseVdf(string text)
    {
        int i = 0;
        string Tok()
        {
            while (i < text.Length)
            {
                char c = text[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '/') { while (i < text.Length && text[i] != '\n') i++; continue; }
                if (c == '{' || c == '}') { i++; return c.ToString(); }
                if (c == '"')
                {
                    var sb = new StringBuilder(); i++;
                    while (i < text.Length && text[i] != '"')
                    {
                        if (text[i] == '\\' && i + 1 < text.Length) { i++; sb.Append(text[i] switch { 'n' => '\n', 't' => '\t', _ => text[i] }); }
                        else sb.Append(text[i]);
                        i++;
                    }
                    i++;
                    return "\"" + sb;
                }
                int s = i; while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '{' && text[i] != '}') i++;
                return "\"" + text[s..i];
            }
            return null;
        }
        Dictionary<string, object> Block()
        {
            var d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                string k = Tok();
                if (k == null || k == "}") return d;
                if (k == "{") continue;
                string v = Tok();
                if (v == null) return d;
                if (v == "{") d[k[1..]] = Block(); else if (v != "}") d[k[1..]] = v[1..];
            }
        }
        return Block();
    }

    static Dictionary<string, object> Sub(Dictionary<string, object> d, string k) => d != null && d.TryGetValue(k, out var v) ? v as Dictionary<string, object> : null;
    static string Str(Dictionary<string, object> d, string k) => d != null && d.TryGetValue(k, out var v) ? v as string : null;

    public static List<string> Libraries(string root)
    {
        var list = new List<string>();
        void Add(string p) { try { p = Path.GetFullPath(p); if (Directory.Exists(p) && !list.Contains(p, StringComparer.OrdinalIgnoreCase)) list.Add(p); } catch { /* not a usable path */ } }
        if (root == null) return list;
        Add(root);
        foreach (string f in new[] { Path.Combine(root, "steamapps", "libraryfolders.vdf"), Path.Combine(root, "config", "libraryfolders.vdf") })
        {
            if (!File.Exists(f)) continue;
            try
            {
                var lf = Sub(ParseVdf(File.ReadAllText(f)), "libraryfolders") ?? Sub(ParseVdf(File.ReadAllText(f)), "LibraryFolders");
                if (lf == null) continue;
                foreach (var (k, v) in lf)
                {
                    if (!int.TryParse(k, out _)) continue;                  // skip "contentstatsid" etc.
                    if (v is string s) Add(s);                               // old format: "1" "D:\\SteamLibrary"
                    else if (Str(v as Dictionary<string, object>, "path") is string p) Add(p);
                }
            }
            catch (Exception e) { Log.Write($"steam: {f}: {e.Message}"); }
            break;
        }
        return list;
    }

    // Tools, runtimes and redistributables that are installed like games but aren't.
    static readonly HashSet<string> ToolIds = new() { "228980", "1070560", "1391110", "1628350", "1493710", "2180100", "1887720", "1580130", "1420170", "1245040", "1113280", "961940", "930400", "858280", "250820", "1826330", "2348590", "2805730", "3658110", "1161040", "1054830" };
    static readonly Regex ToolName = new(@"(?i)^(steamworks|steam linux runtime|proton\b|steamvr|steam audio|source sdk|spacewar$)|redistributable|dedicated server|\bsdk\b", RegexOptions.Compiled);
    public static bool IsTool(string id, string name) => ToolIds.Contains(id) || ToolName.IsMatch(name ?? "");

    // Installed games (fully installed manifest, install folder present), sorted by name.
    public static List<SteamGame> Installed(string root = null)
    {
        root ??= Root();
        var games = new Dictionary<string, SteamGame>();
        foreach (string lib in Libraries(root))
        {
            string apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (string acf in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                try
                {
                    var st = Sub(ParseVdf(File.ReadAllText(acf)), "AppState");
                    string id = Str(st, "appid"), name = Str(st, "name"), dir = Str(st, "installdir");
                    if (id == null || string.IsNullOrWhiteSpace(name) || string.IsNullOrEmpty(dir) || games.ContainsKey(id)) continue;
                    if (!long.TryParse(Str(st, "StateFlags"), out long flags) || (flags & 4) == 0) continue;   // 4 = fully installed
                    if (IsTool(id, name)) continue;
                    string full = Path.Combine(apps, "common", dir);
                    if (!Directory.Exists(full)) continue;
                    games[id] = new SteamGame { AppId = id, Name = name.Trim(), InstallDir = Path.GetFullPath(full) };
                }
                catch (Exception e) { Log.Write($"steam: {acf}: {e.Message}"); }
            }
        }
        return games.Values.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static SteamGame Find(string appId) => Installed().FirstOrDefault(g => g.AppId == appId);

    // ---- library state ----

    static bool Under(string file, string dir) => file != null && dir != null &&
        Path.GetFullPath(file).StartsWith(dir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    // The game already in the list for this Steam game (same appid, or a program inside its folder).
    public static Game Existing(Library lib, SteamGame s) =>
        lib.Data.Games.FirstOrDefault(g => g.AppId == s.AppId) ??
        lib.Data.Games.FirstOrDefault(g => g.Source != "steam" && SafeUnder(g.Target, s.InstallDir));

    static bool SafeUnder(string f, string d) { try { return Under(f, d); } catch { return false; } }
    public static bool InFolderOf(Game steamGame, string target) => steamGame.Source == "steam" && SafeUnder(target, steamGame.Target);

    // null = added; otherwise a sentence for the user.
    public static string Add(Library lib, SteamGame s, out Game g, string folder = null)
    {
        g = null;
        if (Existing(lib, s) is Game dup) return T("errors.it_is_already_in_your_games", dup.Name) + (dup.Removed ? " (removed from the list; Settings > Removed games brings it back)." : ".");
        string name = AddGameLogic.Clean(s.Name);
        if (name.Length == 0) name = T("errors.steam_game") + s.AppId;
        if (lib.Data.Games.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) name += " (Steam)";
        for (int n = 2; lib.Data.Games.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); n++) name = $"{AddGameLogic.Clean(s.Name)} (Steam {n})";
        g = new Game { Id = Library.NewId(), Name = name, Source = "steam", AppId = s.AppId, Target = s.InstallDir, Added = DateTime.Now, Folder = folder };
        try
        {
            var cover = SteamArt.Cover(s.AppId, AddGameLogic.CoverSize);
            if (cover != null) { g.Cover = g.Id + ".png"; g.CoverFrom = "steam"; g.CoverUrl = Covers.SteamUrl(s.AppId); Art.SavePng(cover, Path.Combine(lib.CoversDir, g.Cover)); }
        }
        catch (Exception e) { Log.Write($"steam cover {s.AppId}: {e.Message}"); g.Cover = null; }
        lib.Data.Games.Add(g);
        lib.Save();
        Log.Write($"added Steam game \"{name}\" (appid {s.AppId}, {s.InstallDir})");
        return null;
    }

    public static bool SteamRunning()
    {
        var ps = Process.GetProcessesByName("steam");
        bool r = ps.Length > 0;
        foreach (var p in ps) p.Dispose();
        return r;
    }
}

// Covers from Steam's local cache: old flat names (<appid>_library_hero.jpg) and the newer per-app
// folders (<appid>\library_hero.jpg or <appid>\<hash>\library_hero.jpg).
public static class SteamArt
{
    static string CacheDir => SteamLib.Root() is string r ? Path.Combine(r, "appcache", "librarycache") : null;

    public static string FindFile(string appId, params string[] names)
    {
        string c = CacheDir;
        if (c == null || !Directory.Exists(c)) return null;
        string dir = Path.Combine(c, appId);
        foreach (string n in names)
        {
            if (Directory.Exists(dir))
            {
                var hit = Directory.EnumerateFiles(dir, n, SearchOption.AllDirectories)
                    .Select(f => new FileInfo(f)).Where(f => f.Length > 0).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
                if (hit != null) return hit.FullName;
            }
            string flat = Path.Combine(c, appId + "_" + n);
            if (File.Exists(flat)) return flat;
        }
        return null;
    }

    // Square cover: the wide hero art cropped to a square with the transparent logo on it; otherwise
    // the portrait capsule, otherwise the header on a dimmed copy of itself. null = no art (name tile).
    public static BitmapSource Cover(string appId, int n)
    {
        string hero = FindFile(appId, "library_hero.jpg", "library_hero.png");
        string logo = FindFile(appId, "logo.png");
        string cap = FindFile(appId, "library_600x900_2x.jpg", "library_600x900.jpg", "library_capsule.jpg");
        string head = FindFile(appId, "library_header.jpg", "header.jpg");
        int dec = n <= 256 ? n * 3 : 0;
        if (Art.LoadImage(hero, dec) is BitmapSource h)
        {
            var l = Art.LoadImage(logo, n <= 256 ? n : 0);
            return Art.Render(n, n, dc =>
            {
                Fill(dc, h, n, 0.5);
                if (l == null) return;
                dc.DrawRectangle(new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Color.FromArgb(110, 0, 0, 0), 90), null, new Rect(0, n * 0.45, n, n * 0.55));
                double s = Math.Min(n * 0.80 / l.PixelWidth, n * 0.34 / l.PixelHeight);
                double w = l.PixelWidth * s, ht = l.PixelHeight * s;
                dc.DrawImage(l, new Rect((n - w) / 2, n * 0.93 - ht, w, ht));
            });
        }
        if (Art.LoadImage(cap, n <= 256 ? n : 0) is BitmapSource c)
            return Art.Render(n, n, dc => Fill(dc, c, n, 0.35));
        if (Art.LoadImage(head, 0) is BitmapSource hd)
            return Art.Render(n, n, dc =>
            {
                Fill(dc, hd, n, 0.5);
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), null, new Rect(0, 0, n, n));
                double s = n * 0.92 / hd.PixelWidth, w = hd.PixelWidth * s, ht = hd.PixelHeight * s;
                dc.DrawImage(hd, new Rect((n - w) / 2, (n - ht) / 2, w, ht));
            });
        return null;
    }

    // Fill the square and crop; ay = where the crop sits vertically (0 top, 0.5 centre).
    static void Fill(DrawingContext dc, BitmapSource b, int n, double ay)
    {
        double sc = Math.Max((double)n / b.PixelWidth, (double)n / b.PixelHeight);
        double w = b.PixelWidth * sc, h = b.PixelHeight * sc;
        dc.DrawImage(b, new Rect((n - w) / 2, (n - h) * ay, w, h));
    }
}

// Starts a Steam game through Steam and follows it from the outside (process list and windows only).
public sealed class SteamRun : RunBase
{
    readonly Game g;
    readonly string dir;
    public volatile bool Shown, NeverStarted;
    public int WaitSeconds;
    public static bool FakeRun;          // --steam-fake-run (tests with a fake Steam folder): start the first .exe instead

    public SteamRun(Game game, string installDir) { g = game; dir = installDir.TrimEnd('\\') + "\\"; }

    public override void Begin()
    {
        bool steamUp = SteamLib.SteamRunning();
        WaitSeconds = steamUp ? 90 : 180;   // Steam itself may have to start (and check for updates) first
        var th = new Thread(() =>
        {
            try
            {
                if (FakeRun && SteamLib.RootOverride != null)
                    Process.Start(new ProcessStartInfo(Directory.GetFiles(dir, "*.exe", SearchOption.AllDirectories).First()) { UseShellExecute = true, Arguments = "3" })?.Dispose();
                else
                    Process.Start(new ProcessStartInfo("steam://rungameid/" + g.AppId) { UseShellExecute = true })?.Dispose();
            }
            catch (Exception e) { Failed = "Steam could not be asked to start the game: " + e.Message; Log.Write($"steam start {g.AppId}: {e}"); return; }
            Log.Write($"steam: asked Steam to start {g.AppId} (Steam was {(steamUp ? "running" : "not running")}), watching {dir}");
            Started = true;
            var t0 = DateTime.Now;
            DateTime? seen = null, lastAlive = null;
            while (true)
            {
                var pids = GamePids(dir);
                if (pids.Count > 0)
                {
                    lastAlive = DateTime.Now;
                    if (seen == null)
                    {
                        seen = DateTime.Now;
                        Log.Write($"steam: game process up after {(seen.Value - t0).TotalSeconds:0.0} s: {string.Join(", ", pids.Select(p => $"{p.Value} ({p.Key})"))}");
                        OnStarted?.Invoke();
                    }
                    if (!Shown && HasWindow(pids.Keys))
                    {
                        Shown = true;
                        Log.Write($"steam: game window after {(DateTime.Now - t0).TotalSeconds:0.0} s");
                    }
                }
                else if (seen == null && (DateTime.Now - t0).TotalSeconds > WaitSeconds)
                {
                    NeverStarted = true;
                    Log.Write($"steam: no game process within {WaitSeconds} s");
                    break;
                }
                else if (seen != null && (DateTime.Now - lastAlive.Value).TotalSeconds > 6) break;   // gone for good (not a hand-over)
                Thread.Sleep(seen == null ? 500 : 1000);
            }
            if (seen != null) Log.Write($"steam: game ended after {(DateTime.Now - seen.Value).TotalSeconds:0} s");
            Ended = true;
            OnEnded?.Invoke();
        }) { IsBackground = true, Name = "steam-run" };
        th.Start();
    }

    // pid -> exe path, for every process whose exe lives in the install folder. Only asks Windows for
    // the image path (limited query rights), like Task Manager does.
    public static Dictionary<int, string> GamePids(string dir)
    {
        var r = new Dictionary<int, string>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id <= 4) continue;
                string path = ImagePath(p.Id);
                if (path != null && path.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) r[p.Id] = path;
            }
            catch { /* the process has ended or no access */ }
            finally { p.Dispose(); }
        }
        return r;
    }

    static string ImagePath(int pid)
    {
        IntPtr h = OpenProcess(0x1000, false, pid);   // PROCESS_QUERY_LIMITED_INFORMATION
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024); int n = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref n) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    static bool HasWindow(IEnumerable<int> pids)
    {
        var set = new HashSet<uint>(pids.Select(p => (uint)p));
        bool found = false;
        EnumWindows((h, l) =>
        {
            if (!IsWindowVisible(h)) return true;
            GetWindowThreadProcessId(h, out uint pid);
            if (set.Contains(pid) && GetWindowRect(h, out RECT r) && r.R - r.L >= 320 && r.B - r.T >= 200) { found = true; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int n);
}
