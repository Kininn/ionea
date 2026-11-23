#nullable disable
// Covers from the internet, only when the user switched it on (Going online). Order of a
// game's cover: the one the user chose (never replaced), the game file's own icon (RomArt.cs), Steam's (Steam
// imports), LibRetro's box art (thumbnails.libretro.com), SteamGridDB (the user's own key), then the name tile.
// The background fetch only looks at games with no cover at all; the Change cover screen (CoverPickCard) shows
// every candidate side by side.
//
// LibRetro's box art is named after the game's entry in libretro-database (No-Intro / Redump names). The
// entry is found by the game's serial (GameId) or its CRC32, in that system's .dat (fetched under "Game names
// database", cached in data\names); never by the file's own name: a game the .dat doesn't know gets none here.
// Address layout and the .dat format are from memory: verify.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Ionea;

public static class Covers
{
    public const string Thumbs = "https://thumbnails.libretro.com", Dats = "https://raw.githubusercontent.com/libretro/libretro-database/master/metadat",
        Sgdb = "https://www.steamgriddb.com/api/v2";
    // Test runs with --online-test-host: the same paths on the local server.
    static string Base(string real, string test) => Online.TestHost != null ? $"http://{Online.TestHost}/{test}" : real;

    static string CandDir(MainWindow w, Game g) => Path.Combine(w.Lib.Dir, "cover-candidates", g.Id);
    public static string Candidate(MainWindow w, Game g, string source) => Path.Combine(CandDir(w, g), source + ".png");

    static bool running;
    public static int Fetched;   // covers the background fetch set (tests)

    public static bool SgdbOn(MainWindow w) => Online.Allowed("steamgriddb") && !string.IsNullOrWhiteSpace(w.Settings.SteamGridKey);

    // The background fetch: every game file with no cover gets LibRetro's, else SteamGridDB's. Nothing is sent
    // (or logged) while both are switched off. One at a time; a game tried in the last 30 days is skipped.
    // Runs at start and whenever a game file is added (a new user's first game got its cover only at
    // the next start). Called while it runs, it goes round once more for the games added meanwhile.
    static bool again;
    public static async void Queue(MainWindow w)
    {
        if (!(Online.Allowed("covers") || SgdbOn(w))) return;
        if (running) { again = true; return; }
        running = true;
        try
        {
          do
          {
            again = false;
            var games = w.Lib.Data.Games.Where(g => !g.Removed && g.Source == "rom" && g.Rom != null && g.CoverFrom is not ("own" or "name")
                && (g.Cover == null || !File.Exists(w.Lib.CoverPath(g) ?? ""))).ToList();
            foreach (var g in games)
            {
                string mark = Path.Combine(CandDir(w, g), "tried");
                if (File.Exists(mark) && File.GetLastWriteTime(mark) > DateTime.Now.AddDays(-30)) continue;
                string src = null;
                if (Online.Allowed("covers") && await Libretro(w, g)) src = "libretro";
                else if (SgdbOn(w) && await SteamGrid(w, g)) src = "steamgriddb";
                if (g.Cover != null && File.Exists(w.Lib.CoverPath(g) ?? "")) continue;   // set meanwhile (the user, an icon)
                if (src != null && RomArt.CoverFromImage(Candidate(w, g, src)) is { } b)
                {
                    GameContent.UseCover(w, g, b, src, src, SourceUrl(w, g, src));
                    Fetched++;
                }
                else { Directory.CreateDirectory(CandDir(w, g)); File.WriteAllText(mark, DateTime.Now.ToString("o")); }
            }
          } while (again);
        }
        catch (Exception e) { Log.Write("covers: " + e.Message); }
        finally { running = false; }
    }
    public static bool Busy => running;

    // ---- public addresses ----
    // The Discord status shows the game's cover as its small picture (Ionea's icon stays the big one). Discord
    // fetches a picture only from a public https address, so a cover has one only when it came from the internet:
    // Steam, LibRetro or SteamGridDB. A file of the user's, the game's own icon or the name tile has none.
    // Ionea never sends anything for this: the address goes to the Discord app on this PC, which fetches it.

    // Steam's portrait capsule (Discord crops it to its small circle).
    public static string SteamUrl(string appId) => $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/library_600x900.jpg";

    // Each downloaded candidate keeps the address it came from next to it (libretro.png + libretro.url).
    static string UrlFile(MainWindow w, Game g, string source) => Path.Combine(CandDir(w, g), source + ".url");
    static void WriteUrl(MainWindow w, Game g, string source, string url)
    {
        try { Directory.CreateDirectory(CandDir(w, g)); File.WriteAllText(UrlFile(w, g, source), url); }
        catch (Exception e) { Log.Write($"covers: {source} address for \"{g.Name}\": {e.Message}"); }
    }
    static string ReadUrl(MainWindow w, Game g, string source)
    {
        try { string u = File.ReadAllText(UrlFile(w, g, source)).Trim(); return u.Length > 0 ? u : null; } catch { return null; }
    }

    // The public address of a cover candidate (the Change cover screen's sources), null when it has none.
    public static string SourceUrl(MainWindow w, Game g, string source) => source switch
    {
        "steam" => g.AppId != null ? SteamUrl(g.AppId) : null,
        "libretro" or "steamgriddb" => ReadUrl(w, g, source),
        _ => null,
    };

    // The public address of the picture the game's tile shows now, or null.
    public static string PublicUrl(Game g)
    {
        if (g?.Cover == null) return null;
        if (!string.IsNullOrEmpty(g.CoverUrl)) return g.CoverUrl;
        // A Steam game added by an older version whose cover is still Steam's.
        if (g.CoverUrl == null && g.Source == "steam" && g.AppId != null && g.CoverFrom is null or "steam") return SteamUrl(g.AppId);
        return null;
    }

    // A game file's cover set by an older version (CoverUrl null): when it is one of its downloaded candidates
    // (the picture made from it again matches the tile's), that candidate's address; otherwise none (""). Once
    // per game; found() runs when an address was found. A match whose address can't be worked out yet (the
    // LibRetro names list not here) stays unknown and is tried again the next time Ionea starts.
    static readonly HashSet<string> urlTried = new();
    public static async void FindUrl(MainWindow w, Game g, Action found)
    {
        if (g?.CoverUrl != null || g.Cover == null || g.Source != "rom" || !urlTried.Add(g.Id)) return;
        try
        {
            var tile = Art.LoadImage(w.Lib.CoverPath(g), 0);
            if (tile == null) return;
            bool matched = false;
            foreach (string src in new[] { "libretro", "steamgriddb" })
            {
                string cand = Candidate(w, g, src);
                if (!File.Exists(cand) || !Same(RomArt.CoverFromImage(cand), tile)) continue;
                matched = true;
                string url = ReadUrl(w, g, src);
                if (url == null && src == "libretro" && await LibretroUrl(w, g) is string u) WriteUrl(w, g, src, url = u);
                if (url == null) continue;
                g.CoverUrl = url; w.Lib.Save();
                Log.Write($"covers: \"{g.Name}\"'s cover is {src}'s: {url}");
                found?.Invoke();
                return;
            }
            if (matched) return;
            g.CoverUrl = ""; w.Lib.Save();
            Log.Write($"covers: \"{g.Name}\"'s cover has no public address");
        }
        catch (Exception e) { Log.Write($"covers: finding \"{g.Name}\"'s cover address: {e.Message}"); }
    }

    // Two square covers alike to the eye (the same picture made again: at most a rounding step apart).
    static bool Same(System.Windows.Media.Imaging.BitmapSource a, System.Windows.Media.Imaging.BitmapSource b)
    {
        if (a == null || b == null || a.PixelWidth != b.PixelWidth || a.PixelHeight != b.PixelHeight) return false;
        static byte[] Px(System.Windows.Media.Imaging.BitmapSource s)
        {
            var c = new System.Windows.Media.Imaging.FormatConvertedBitmap(s, System.Windows.Media.PixelFormats.Pbgra32, null, 0);
            var p = new byte[c.PixelWidth * c.PixelHeight * 4];
            c.CopyPixels(p, c.PixelWidth * 4, 0);
            return p;
        }
        byte[] x = Px(a), y = Px(b);
        long d = 0;
        for (int i = 0; i < x.Length; i++) d += Math.Abs(x[i] - y[i]);
        return d < x.Length * 2L;   // under 2 per channel on average; different art is far apart
    }

    // ---- LibRetro ----

    public static async Task<bool> Libretro(MainWindow w, Game g)
    {
        var sys = EmuCatalog.System(g.System);
        if (sys?.Thumbs == null || g.Rom == null) return false;
        string dest = Candidate(w, g, "libretro");
        if (File.Exists(dest)) return true;
        if (await DatName(w, sys, g.Rom) is not string name) return false;
        string path = $"{Uri.EscapeDataString(sys.Thumbs)}/Named_Boxarts/{Uri.EscapeDataString(ThumbName(name))}.png";
        if (await Online.Download(w, "covers", $"{Base(Thumbs, "thumbs")}/{path}", dest) == OnlineResult.Done && Art.LoadImage(dest, 64) != null)
        {
            WriteUrl(w, g, "libretro", $"{Thumbs}/{path}");   // the public address, also in test runs
            return true;
        }
        try { File.Delete(dest); } catch { /* best-effort cleanup */ }
        return false;
    }

    // The public address of the game's LibRetro box art (nothing downloaded), null when its names list doesn't know it.
    static async Task<string> LibretroUrl(MainWindow w, Game g)
    {
        var sys = EmuCatalog.System(g.System);
        if (sys?.Thumbs == null || g.Rom == null || await DatName(w, sys, g.Rom) is not string name) return null;
        return $"{Thumbs}/{Uri.EscapeDataString(sys.Thumbs)}/Named_Boxarts/{Uri.EscapeDataString(ThumbName(name))}.png";
    }

    // libretro-thumbnails replaces these characters in a file name with _.
    public static string ThumbName(string name) => Regex.Replace(name, @"[&\*/:`<>\?\\\|""]", "_");

    static readonly Dictionary<string, DatIndex> dats = new(StringComparer.OrdinalIgnoreCase);

    // The game's name in its system's .dat: by serial, else by CRC32 (a NES file also without its 16-byte header).
    static async Task<string> DatName(MainWindow w, EmuSystem sys, string rom)
    {
        if (sys.Dat == null) return null;
        string file = Path.Combine(w.Lib.Dir, "names", sys.Dat, sys.Thumbs + ".dat");
        if (!File.Exists(file) || File.GetLastWriteTime(file) < DateTime.Now.AddDays(-90))
        {
            if (!Online.Allowed("names")) { if (!File.Exists(file)) return null; }
            else
            {
                string url = $"{Base(Dats, "dat")}/{sys.Dat}/{Uri.EscapeDataString(sys.Thumbs)}.dat";
                await Online.Download(w, "names", url, file);
                if (!File.Exists(file)) return null;
            }
        }
        if (!dats.TryGetValue(file, out var idx)) dats[file] = idx = await Task.Run(() => DatIndex.Load(file));
        string serial = null;
        try { serial = GameId.Read(sys.Reader, rom)?.Id; } catch (Exception caught) { Log.Debug("covers: game serial", caught); }
        if (serial != null && idx.BySerial.TryGetValue(NormSerial(serial), out string byS)) return byS;
        var crcs = await Task.Run(() => Crcs(rom));
        foreach (uint c in crcs) if (idx.ByCrc.TryGetValue(c, out string byC)) return byC;
        return null;
    }

    public static string NormSerial(string s) => Regex.Replace(s ?? "", @"[^A-Za-z0-9]", "").ToUpperInvariant();

    // The file's CRC32 (up to 256 MB), and for an iNES file also without its header.
    static List<uint> Crcs(string rom)
    {
        var l = new List<uint>();
        try
        {
            var fi = new FileInfo(rom);
            if (!fi.Exists || fi.Length > 256L << 20 || fi.Length == 0) return l;
            byte[] b = File.ReadAllBytes(rom);
            l.Add(Crc32.Of(b, 0, b.Length));
            if (b.Length > 16 && b[0] == 'N' && b[1] == 'E' && b[2] == 'S' && b[3] == 0x1A) l.Add(Crc32.Of(b, 16, b.Length - 16));
        }
        catch (Exception caught) { Log.Debug("covers: checksum", caught); }
        return l;
    }

    // ---- SteamGridDB ----

    // The game's name searched, the first result's first square grid downloaded. The key goes to steamgriddb.com only.
    public static async Task<bool> SteamGrid(MainWindow w, Game g)
    {
        string dest = Candidate(w, g, "steamgriddb");
        if (File.Exists(dest)) return true;
        string key = w.Settings.SteamGridKey?.Trim();
        if (string.IsNullOrEmpty(key)) return false;
        var auth = new Dictionary<string, string> { ["Authorization"] = "Bearer " + key };
        string tmp = Path.Combine(CandDir(w, g), "sgdb.json");
        try
        {
            string api = Base(Sgdb, "sgdb");
            if (await Online.Download(w, "steamgriddb", $"{api}/search/autocomplete/{Uri.EscapeDataString(RomLogic.WithoutDisc(g.Name))}", tmp, headers: auth) != OnlineResult.Done) return false;
            long id = 0;
            using (var d = JsonDocument.Parse(File.ReadAllText(tmp)))
                if (d.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0 && data[0].TryGetProperty("id", out var idv)) id = idv.GetInt64();
            if (id == 0) return false;
            if (await Online.Download(w, "steamgriddb", $"{api}/grids/game/{id}?dimensions=512x512,1024x1024", tmp, headers: auth) != OnlineResult.Done) return false;
            string url = null;
            using (var d = JsonDocument.Parse(File.ReadAllText(tmp)))
                if (d.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0 && data[0].TryGetProperty("url", out var u)) url = u.GetString();
            if (url == null) return false;
            if (await Online.Download(w, "steamgriddb", url, dest) == OnlineResult.Done && Art.LoadImage(dest, 64) != null) { WriteUrl(w, g, "steamgriddb", url); return true; }
            try { File.Delete(dest); } catch { /* best-effort cleanup */ }
            return false;
        }
        catch (Exception e) { Log.Write("covers: SteamGridDB: " + e.Message); return false; }
        finally { try { File.Delete(tmp); } catch { /* best-effort cleanup */ } }
    }
}

// A clrmamepro .dat (libretro-database's metadat): game ( name "…" … rom ( name "…" size … crc 1A2B3C4D serial "…" ) ).
public sealed class DatIndex
{
    public readonly Dictionary<string, string> BySerial = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<uint, string> ByCrc = new();

    public static DatIndex Load(string file)
    {
        var x = new DatIndex();
        try
        {
            string text = File.ReadAllText(file);
            foreach (Match g in Regex.Matches(text, @"(?ms)^game\s*\((.*?)^\)"))
            {
                string body = g.Groups[1].Value;
                var nm = Regex.Match(body, @"(?m)^\s*name\s+""([^""]+)""");
                if (!nm.Success) continue;
                string name = nm.Groups[1].Value;
                foreach (Match s in Regex.Matches(body, @"serial\s+""([^""]+)"""))
                {
                    string v = s.Groups[1].Value;
                    x.BySerial.TryAdd(Covers.NormSerial(v), name);
                    // Nintendo's "AGB-AQTE-USA" style: also the game code the cartridge header holds (verify)
                    if (Regex.Match(v, @"^[A-Z]{3}-([A-Z0-9]{4})(-|$)") is { Success: true } c4) x.BySerial.TryAdd(c4.Groups[1].Value, name);
                }
                foreach (Match c in Regex.Matches(body, @"\bcrc\s+([0-9A-Fa-f]{8})\b"))
                    x.ByCrc.TryAdd(Convert.ToUInt32(c.Groups[1].Value, 16), name);
            }
        }
        catch (Exception e) { Log.Write($"covers: {Path.GetFileName(file)}: {e.Message}"); }
        return x;
    }
}

public static class Crc32
{
    static readonly uint[] T = Make();
    static uint[] Make()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++) { uint c = i; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; t[i] = c; }
        return t;
    }
    public static uint Of(byte[] b, int at, int len)
    {
        uint c = 0xFFFFFFFFu;
        for (int i = at; i < at + len; i++) c = T[(c ^ b[i]) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
