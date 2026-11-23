#nullable disable
// Download an emulator: for a user with none, the official release of an emulator whose
// definition names one (release: a GitHub repository and a pattern for the Windows file, or a fixed address).
// The release's file list comes under Emulator installs without a question; the emulator's own file is
// asked for with its full address (Online.Download), saved in data\downloads and installed like a checked
// download (EmuList.Install), after its size and SHA-256 match what the release lists (GitHub's asset digest).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using static Ionea.Loc;

namespace Ionea;

public static class EmuInstall
{
    public const string GithubApi = "https://api.github.com";
    public static bool Busy;

    // The definitions with an official download, standalone emulators first, by name.
    public static List<EmuDef> Offers()
    {
        EmuDefs.EnsureLoaded();
        return EmuDefs.All.Where(d => d.Release != null && (d.Release.Url != null || (d.Release.Github != null && d.Release.Asset != null)))
            .OrderBy(d => d.IsCore).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // null when the file matches the release's size and SHA-256 (each only when the release lists it), otherwise what differs.
    public static string Check(string file, string digest, long size)
    {
        var fi = new FileInfo(file);
        if (size >= 0 && fi.Length != size) return $"{fi.Length} bytes, the release lists {size}";
        if (string.IsNullOrEmpty(digest)) { Log.Write($"emulator download: {Path.GetFileName(file)}: the release lists no checksum"); return null; }
        if (!digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) { Log.Write($"emulator download: unknown checksum kind {digest}"); return null; }
        string want = digest[7..].Trim().ToLowerInvariant();
        string have;
        using (var fs = File.OpenRead(file)) have = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fs)).ToLowerInvariant();
        return have == want ? null : "its checksum differs";
    }

    static string Api => Online.TestHost != null ? $"http://{Online.TestHost}/github" : GithubApi;

    // The emulator installed and added, or why not (null, null: the user said no).
    public static async Task<(Emulator, string)> Run(MainWindow w, EmuDef def)
    {
        if (Busy) return (null, T("settings.another_download_is_running"));
        Busy = true;
        try
        {
            string dir = w.Emus.DownloadsDir;
            Directory.CreateDirectory(dir);
            string url = def.Release.Url, file = null, item = def.IsCore ? "cores" : "emulators", digest = null;
            long size = -1;
            // test runs: a fixed address comes from the test server, under /release/<host><path>
            if (url != null && Online.TestHost != null && Uri.TryCreate(url, UriKind.Absolute, out var fixedAt)) url = $"http://{Online.TestHost}/release/{fixedAt.Host}{fixedAt.AbsolutePath}";
            if (url == null)
            {
                string info = Path.Combine(dir, def.Id + "-release.json");
                var r = await Online.Download(w, "emulators", $"{Api}/repos/{def.Release.Github}/releases/latest", info, def.Name, info: true);
                if (r != OnlineResult.Done) return (null, r == OnlineResult.Cancelled ? null : T("settings.the_list_of_s_files_could", def.Name, Online.Word(r)));
                try
                {
                    using var d = JsonDocument.Parse(File.ReadAllText(info));
                    var rx = new Regex(def.Release.Asset, RegexOptions.IgnoreCase);
                    foreach (var a in d.RootElement.GetProperty("assets").EnumerateArray())
                    {
                        string n = a.GetProperty("name").GetString();
                        if (n == null || !rx.IsMatch(n)) continue;
                        file = n; url = a.GetProperty("browser_download_url").GetString();
                        // GitHub lists each file's SHA-256 ("sha256:<hex>") and size: the download is checked against both
                        if (a.TryGetProperty("digest", out var dg) && dg.ValueKind == JsonValueKind.String) digest = dg.GetString();
                        if (a.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number) size = sz.GetInt64();
                        break;
                    }
                }
                catch (Exception e) { return (null, $"{def.Name}'s file list could not be read: {e.Message}"); }
                finally { try { File.Delete(info); } catch { /* best-effort cleanup */ } }
                if (url == null) return (null, $"{def.Name}'s latest release has no Windows file Ionea knows.");
            }
            file ??= Path.GetFileName(new Uri(url).LocalPath);
            if (string.IsNullOrWhiteSpace(file) || file.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) file = def.Id + ".zip";
            string dest = Path.Combine(dir, file);
            var got = await Online.Download(w, item, url, dest, def.Name);
            if (got == OnlineResult.Cancelled) return (null, null);
            if (got != OnlineResult.Done) return (null, $"{def.Name} could not be downloaded ({Online.Word(got)}).");
            if (Check(dest, digest, size) is string bad)
            {
                Log.Write($"emulator download: {dest}: {bad}");
                try { File.Delete(dest); } catch { /* best-effort cleanup */ }
                return (null, $"{def.Name}'s download is not the file the release lists ({bad}). Nothing was installed.");
            }
            try
            {
                var emu = w.Emus.Install(dest, out _);   // as a checked download (EmuPick.Install): on this thread
                try { File.Delete(dest); } catch { /* best-effort cleanup */ }
                w.Emus.UseCores(w.Lib, new List<Emulator> { emu });
                ReqFiles.SupplyAll();
                return (emu, null);
            }
            catch (Exception e) { Log.Write($"emulator download: {dest}: {e.Message}"); return (null, e.Message); }
        }
        finally { Busy = false; }
    }
}
