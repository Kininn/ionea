#nullable disable
// Version and updates. Settings > About and updates.
//   Version: Launcher.csproj's VersionPrefix (1.0.0) plus a build number and date that Install-Launcher.ps1 stamps
//   (AppVersion). Shown in Settings > About and updates and small in the start screen's corner.
//   One update source, not a setting: the latest release of Ionea's own GitHub repo (Updater.Owner/Name, built in,
//     so no other repo can be set or mistyped; tag = version, a .zip asset with a SHA-256). Only with
//     the network connection on (Online.cs; off by default), and only when asked for in About and updates: Check for
//     updates, or at launch Update notification (a pop-up) / Auto update (installs and restarts), both off by
//     default (AtLaunch). Every request
//     goes to api.github.com/repos/<Owner>/<Name>/ only (RepoApi); GitHub sends the zip on to its file storage.
//     Update now asks once, downloads the zip into
//     <install>\update\download, checks its SHA-256, unpacks it and swaps. A matching zip already in the Inbox or
//     the Downloads folder is used instead of downloading it again.
//   Test scripts only (updatekind / updatefolder, never saved or shown): a source folder instead. Check compares its
//   version (VersionPrefix + build.txt) with this one; the same version with source files changed after this exe
//   was built also counts as newer. Update now runs the folder's Install-Launcher.ps1 -BuildOnly into
//   <install>\update\staged, then swaps. This lets the updater's swap be tested offline.
//   The swap: the running Launcher.exe is renamed to Launcher.previous.exe (a running exe can be renamed, not
//   overwritten) and the new one copied in; definitions\ the same way (definitions.previous\). The data folder is
//   never touched. Then Ionea restarts (outro, then Program.Main starts the new exe once this one let go of its
//   single-instance lock). Go back to the previous version swaps the two back. Never while a game is running.
//   Installed or portable: a release also has Ionea-<version>-setup.exe (installer\Ionea.iss), which this
//   never takes; the zip is the same self-contained build, swapped into the folder this exe runs from.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using static Ionea.Loc;

namespace Ionea;

// Settings > About and updates (this PC's, in the shared settings.json).
public sealed class UpdatePrefs
{
    // (no update source here: updates come only from Updater.Owner/Name; an old settings.json's Kind / Folder /
    // Repo / TokenSealed are ignored)
    public bool CheckAtStart { get; set; }         // Update notification: at launch, a pop-up when a new version is out. Off by default
    public bool AutoUpdate { get; set; }           // Auto update: at launch, installs a new version and restarts. Off by default
    public DateTime? LastChecked { get; set; }
    public string LastResult { get; set; }
    public string RestartedFrom { get; set; }      // set just before a restart into another version: the toast after it
}

public static class AppVersion
{
    static readonly Assembly A = typeof(AppVersion).Assembly;
    static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-GB");
    public static readonly Version Full = A.GetName().Version ?? new Version(0, 0, 0, 0);   // 1.0.0.<build>
    public static string Number => Three(Full);
    public static int BuildNo => Math.Max(0, Full.Revision);
    public static readonly DateTime? Built = ReadBuilt();

    static DateTime? ReadBuilt()
    {
        var m = A.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(x => x.Key == "IoneaBuilt");
        return m != null && DateTime.TryParse(m.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var t) ? t : null;
    }

    public static string Three(Version v) => $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
    // "1.0.0 · build 3 · 18 Nov 2026"
    public static string Text => Describe(Full, Built);
    public static string Describe(Version v, DateTime? built) =>
        Three(v) + (v.Revision > 0 ? $" · build {v.Revision}" : " · own build") + (built is DateTime b ? " · " + b.ToString("d MMM yyyy", En) : "");
    // the start screen's corner: "Ionea 1.0.0 (3)"
    public static string Short => T("settings.ionea", Number) + (BuildNo > 0 ? $" ({BuildNo})" : "");

    // An exe's own version (its file version), or null.
    public static Version Of(string exe)
    {
        try
        {
            var fv = FileVersionInfo.GetVersionInfo(exe);
            return new Version(fv.FileMajorPart, fv.FileMinorPart, fv.FileBuildPart, fv.FilePrivatePart);
        }
        catch { return null; }
    }
}

public enum UpdateState { NotChecked, Checking, UpToDate, Available, Building, Failed, Refused }

public sealed class UpdateOffer
{
    public string Kind;          // folder / repo
    public Version Version;      // what it would install
    public string VersionText;   // "9.9.9", "1.0.0 with newer changes"
    public string Why;           // why it counts as newer
    public string Changelog;     // CHANGELOG.md or the release notes, or null
    // repo only
    public string Tag, AssetName, AssetUrl, AssetApi, Sha256;   // AssetApi: api.github.com's address of the zip (works for a private repo)
    public long Size;
}

public static class Updater
{
    const int BuildMinutes = 10;

    public static UpdateState State { get; private set; } = UpdateState.NotChecked;
    public static string Status { get; private set; } = T("settings.not_checked_yet");   // short, for the row
    public static string Detail { get; private set; }                         // the whole sentence
    public static UpdateOffer Offer { get; private set; }
    public static bool Busy => State is UpdateState.Checking or UpdateState.Building;
    public static event Action Changed;
    public static bool TestGameRunning;     // test scripts: as if a game were running
    public static Action BeforeRestart;     // test scripts: log the result before the launcher restarts

    static Version Into;                    // what the restart starts (after Apply or SwapBack)
    static Settings settings;
    public static void Init(Settings s) { settings = s; s.Updates ??= new(); }
    static UpdatePrefs P => settings.Updates;

    public static string Exe => Environment.ProcessPath;
    public static string InstallDir => Path.GetDirectoryName(Exe);
    public static string PreviousExe => Path.Combine(InstallDir, "Launcher.previous.exe");
    public static string WorkDir => Path.Combine(InstallDir, "update");   // next to the exe, never in data\
    // Test scripts only (TestScript updatekind / updatefolder): a source folder instead of the repo. Never saved.
    public static bool TestUseFolder;
    public static string TestFolder;
    public static string Folder => TestUseFolder && !string.IsNullOrWhiteSpace(TestFolder) ? TestFolder : null;
    public static bool RepoKind => !TestUseFolder;
    // Where releases come from: built in, never read from settings.
    public const string Owner = "Kininn", Name = "ionea";
    public static string RepoName => $"{Owner}/{Name}";
    public static string RepoUrl => $"https://github.com/{Owner}/{Name}";
    public static Version PreviousVersion => File.Exists(PreviousExe) ? AppVersion.Of(PreviousExe) : null;

    // What api.github.com is sent (Online.Download keeps headers on the first host).
    static Dictionary<string, string> ApiHeaders(string accept)
    {
        var h = new Dictionary<string, string> { ["Accept"] = accept, ["X-GitHub-Api-Version"] = "2022-11-28" };
        return h;
    }
    // The set repo's API root; everything Ionea asks for updates starts with it.
    static string RepoApi(string owner, string name) => $"https://api.github.com/repos/{owner}/{name}/";
    static bool InRepo(string url, string owner, string name) => url != null && url.StartsWith(RepoApi(owner, name), StringComparison.OrdinalIgnoreCase);

    public static bool GameRunning(MainWindow w) => TestGameRunning || w.GameMenu?.Game != null || w.Launch?.Running == true;

    static void Set(UpdateState st, string status, string detail = null)
    {
        State = st; Status = status; Detail = detail ?? status;
        Log.Write($"update: {st.ToString().ToLowerInvariant()}: {Detail}");
        Changed?.Invoke();
    }

    // Source changed: what was checked no longer holds.
    public static void Forget()
    {
        if (Busy) return;
        Offer = null;
        Set(UpdateState.NotChecked, T("settings.not_checked_yet"));
    }

    // ---- checking ----

    // quiet: no error pop-up (at launch); notify: then a pop-up when a new version is out.
    public static async Task Check(MainWindow w, bool quiet = false, bool notify = true)
    {
        if (Busy) return;
        Offer = null;
        Set(UpdateState.Checking, T("settings.checking"), T("settings.checking_2", RepoKind ? RepoUrl : Folder ?? T("settings.checking_no_source_folder")));
        try
        {
            var (st, status, detail, offer) = RepoKind ? await CheckRepo(w) : await Task.Run(() => CheckFolder(Folder));
            Offer = st == UpdateState.Available ? offer : null;
            Set(st, status, detail);
        }
        catch (Exception e) { Set(UpdateState.Failed, T("settings.failed"), T("settings.the_check_failed", e.Message)); }
        P.LastChecked = DateTime.Now; P.LastResult = Detail; settings.Save();
        if (State == UpdateState.Available && quiet && notify) w.Toasts.Show(T("settings.update_ready"), T("settings.ionea_settings_about_and_updates", Offer.VersionText), "info");
        else if (!quiet && State is UpdateState.Failed or UpdateState.Refused) w.Toasts.Show(T("settings.update_check"), Detail, "error");
    }

    // The source folder's version: VersionPrefix in Launcher.csproj and its last build number (build.txt).
    public static (UpdateState, string, string, UpdateOffer) CheckFolder(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return (UpdateState.Failed, T("settings.no_source_folder"), T("settings.set_the_source_folder_first"), null);
        if (!Directory.Exists(dir)) return (UpdateState.Failed, T("settings.folder_not_found"), T("settings.the_source_folder_isn_t_there", dir), null);
        string proj = Path.Combine(dir, "Launcher.csproj");
        if (!File.Exists(proj)) return (UpdateState.Failed, T("settings.not_a_source_folder"), T("settings.there_is_no_launcher_csproj_in", dir), null);
        if (!File.Exists(Path.Combine(dir, "Install-Launcher.ps1"))) return (UpdateState.Failed, T("settings.not_a_source_folder"), T("settings.there_is_no_install_launcher_ps1", dir), null);
        string xml = File.ReadAllText(proj);
        var m = Regex.Match(xml, @"<VersionPrefix>\s*([\d.]+)\s*</VersionPrefix>");
        if (!m.Success) m = Regex.Match(xml, @"<Version>\s*([\d.]+)\s*</Version>");
        if (!m.Success || !Version.TryParse(Pad3(m.Groups[1].Value), out var pre)) return (UpdateState.Failed, T("settings.no_version"), T("settings.has_no_version", proj), null);
        int build = 0;
        try { int.TryParse(File.ReadAllText(Path.Combine(dir, "build.txt")).Trim(), out build); } catch (Exception caught) { Log.Debug("updater: build number", caught); }
        var src = new Version(pre.Major, pre.Minor, Math.Max(0, pre.Build), build);
        string log = ReadChangelog(Path.Combine(dir, "CHANGELOG.md"));
        var mine = AppVersion.Full;
        int cmp = Compare3(src, mine);
        UpdateOffer Offer(string text, string why) => new() { Kind = "folder", Version = src, VersionText = text, Why = why, Changelog = log };
        if (cmp > 0) return (UpdateState.Available, T("settings.version_ready", AppVersion.Three(src)), T("settings.ionea_is_in_this_is", AppVersion.Three(src), dir, AppVersion.Number), Offer(AppVersion.Three(src), "a newer version"));
        if (cmp < 0) return (UpdateState.UpToDate, T("settings.up_to_date"), T("settings.the_source_folder_has_older_than", AppVersion.Three(src), AppVersion.Number), null);
        if (build > AppVersion.BuildNo) return (UpdateState.Available, T("settings.build_ready", build), T("settings.the_source_folder_has_build_of", build, AppVersion.Number, AppVersion.BuildNo), Offer(T("settings.version_build", AppVersion.Three(src), build), "a newer build"));
        // the same version: newer only if its files changed after this exe was built
        DateTime newest = NewestSource(dir, out string which);
        if (AppVersion.Built is DateTime built && newest > built.AddMinutes(1))
            return (UpdateState.Available, T("settings.newer_changes"), T("settings.changed_after_this_build", which, newest.ToString("d MMM HH:mm"), built.ToString("d MMM HH:mm")),
                Offer(T("settings.with_newer_changes", AppVersion.Number), "source files changed after this build"));
        return (UpdateState.UpToDate, T("settings.up_to_date"), T("settings.ionea_is_the_newest_in", AppVersion.Number, dir), null);
    }

    static string Pad3(string v) => v.Count(c => c == '.') == 0 ? v + ".0.0" : v.Count(c => c == '.') == 1 ? v + ".0" : v;
    static int Compare3(Version a, Version b) => new Version(a.Major, a.Minor, Math.Max(0, a.Build)).CompareTo(new Version(b.Major, b.Minor, Math.Max(0, b.Build)));

    // The newest file the build uses (backups and build output left out).
    static DateTime NewestSource(string dir, out string which)
    {
        DateTime best = DateTime.MinValue; which = null;
        IEnumerable<string> Files(string sub, string pattern, bool deep)
        {
            string d = Path.Combine(dir, sub);
            if (!Directory.Exists(d)) return Array.Empty<string>();
            try { return Directory.EnumerateFiles(d, pattern, deep ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).ToList(); }
            catch { return Array.Empty<string>(); }
        }
        foreach (var f in Files("src", "*.cs", false).Concat(Files("assets", "*", true)).Concat(Files("definitions", "*.json", true))
            .Concat(new[] { T("settings.launcher_csproj"), "THIRD-PARTY.md" }.Select(n => Path.Combine(dir, n)).Where(File.Exists)))
        {
            if (Path.GetFileName(f).Contains(".bak", StringComparison.OrdinalIgnoreCase)) continue;
            var t = File.GetLastWriteTime(f);
            if (t > best) { best = t; which = Path.GetRelativePath(dir, f); }
        }
        return best;
    }

    static string ReadChangelog(string file)
    {
        try { if (File.Exists(file)) { string t = File.ReadAllText(file).Trim(); return t.Length > 6000 ? t[..6000] + "\n…" : t; } } catch (Exception caught) { Log.Debug("updater: release notes", caught); }
        return null;
    }

    static async Task<(UpdateState, string, string, UpdateOffer)> CheckRepo(MainWindow w)
    {
        const string owner = Owner, name = Name;
        Directory.CreateDirectory(WorkDir);
        string info = Path.Combine(WorkDir, "release.json");
        var hdr = ApiHeaders("application/vnd.github+json");
        int code = 0;
        // the release's page: a data file (no question), refused while the network connection is off
        var r = await Online.Download(w, "updates", RepoApi(owner, name) + "releases/latest", info, T("settings.ionea_release_info"), hdr, info: true, status: c => code = c);
        if (r == OnlineResult.Blocked && Online.Offline)
            return (UpdateState.Refused, T("settings.offline"), T("settings.the_network_connection_is_off"), null);
        if (r != OnlineResult.Done) return (UpdateState.Failed, T("settings.failed"), T("settings.the_release_page_could_not_be_read", owner, name, Online.Word(r) + (code > 0 ? ", " + code : "")), null);

        using var doc = JsonDocument.Parse(File.ReadAllText(info));
        var root = doc.RootElement;
        string tag = Str(root, "tag_name");
        var vm = Regex.Match(tag ?? "", @"(\d+)\.(\d+)(?:\.(\d+))?(?:\.(\d+))?");
        if (!vm.Success) return (UpdateState.Failed, T("settings.no_version"), T("settings.the_latest_release_s_tag_has", tag), null);
        var ver = new Version(int.Parse(vm.Groups[1].Value), int.Parse(vm.Groups[2].Value), vm.Groups[3].Success ? int.Parse(vm.Groups[3].Value) : 0, vm.Groups[4].Success ? int.Parse(vm.Groups[4].Value) : 0);
        int cmp = Compare3(ver, AppVersion.Full);
        if (cmp == 0 && vm.Groups[4].Success) cmp = ver.Revision.CompareTo(AppVersion.BuildNo);
        if (cmp <= 0) return (UpdateState.UpToDate, T("settings.up_to_date"), T("settings.the_latest_release_is_this_is", tag, AppVersion.Number, AppVersion.BuildNo), null);
        string vtext = AppVersion.Three(ver) + (vm.Groups[4].Success && ver.Revision > 0 ? $" build {ver.Revision}" : "");

        // the zip: one named after Ionea or the launcher first, else the only zip
        var assets = root.TryGetProperty("assets", out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().ToList() : new();
        var zips = assets.Where(x => Str(x, "name")?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true).ToList();
        var zip = zips.FirstOrDefault(x => Regex.IsMatch(Str(x, "name"), "(?i)ionea|launcher")).ValueKind != JsonValueKind.Undefined
            ? zips.First(x => Regex.IsMatch(Str(x, "name"), "(?i)ionea|launcher")) : zips.Count == 1 ? zips[0] : default;
        if (zip.ValueKind == JsonValueKind.Undefined) return (UpdateState.Failed, T("settings.no_zip"), T("settings.release_has_no_single_ionea_zip", tag), null);
        string zname = Str(zip, "name"), zurl = Str(zip, "browser_download_url"), zapi = Str(zip, "url");
        if (!InRepo(zapi, owner, name)) return (UpdateState.Failed, T("settings.not_this_repo"), T("settings.release_s_zip_is_not_in", tag, owner, name), null);
        // its SHA-256: GitHub's own digest, else a .sha256 / SHA256SUMS file in the release
        string sha = Str(zip, "digest") is string dg && dg.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? dg[7..] : null;
        if (sha == null)
        {
            var sums = assets.FirstOrDefault(x => Str(x, "name") is string n && (n.Equals(zname + ".sha256", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(n, "(?i)sha256")));
            if (sums.ValueKind != JsonValueKind.Undefined)
            {
                string sf = Path.Combine(WorkDir, "sha256.txt");
                string sapi = Str(sums, "url");
                if (InRepo(sapi, owner, name) && await Online.Download(w, "updates", sapi, sf, "SHA-256 list", ApiHeaders("application/octet-stream"), info: true) == OnlineResult.Done)
                {
                    var lines = File.ReadAllLines(sf);
                    string line = lines.FirstOrDefault(l => l.Contains(zname, StringComparison.OrdinalIgnoreCase)) ?? (lines.Length == 1 ? lines[0] : null);
                    sha = line == null ? null : Regex.Match(line, "[0-9a-fA-F]{64}").Value;
                }
            }
        }
        if (string.IsNullOrEmpty(sha) || !Regex.IsMatch(sha, "^[0-9a-fA-F]{64}$"))
            return (UpdateState.Failed, T("settings.no_sha_256"), T("settings.release_publishes_no_sha_256_for", tag, zname), null);
        long size = zip.TryGetProperty("size", out var sz) && sz.TryGetInt64(out long n0) ? n0 : 0;
        string notes = Str(root, "body");
        var offer = new UpdateOffer
        {
            Kind = "repo", Version = ver, VersionText = vtext, Why = "a newer release", Changelog = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
            Tag = tag, AssetName = zname, AssetUrl = zurl, AssetApi = zapi, Sha256 = sha.ToLowerInvariant(), Size = size,
        };
        return (UpdateState.Available, vtext + " ready", T("settings.release_is_newer_than_build", tag, zname, AppVersion.Number, AppVersion.BuildNo), offer);
    }

    static string Str(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // ---- updating ----

    // Update now: asks first, then builds (folder) or downloads the release's zip (repo; one already in the Inbox or
    // Downloads with the release's SHA-256 is used instead), swaps and restarts.
    public static void UpdateNow(MainWindow w, bool confirmed = false)
    {
        if (Busy) return;
        if (GameRunning(w)) { Blocked(w); return; }
        var o = Offer;
        if (o == null || State != UpdateState.Available)
        {
            new MessageCard(w, T("settings.nothing_to_update"), State == UpdateState.NotChecked ? T("settings.check_for_updates_first") : Detail).Open();
            return;
        }
        string zip = null;
        if (o.Kind == "repo") zip = FindInInbox(o);   // null: Update now downloads it
        if (confirmed) new UpdatingCard(w, o, zip).Open();
        else new UpdateAskCard(w, o, zip).Open();
    }

    static void Blocked(MainWindow w)
    {
        Log.Write("update: refused, a game is running");
        Detail = T("settings.close_the_game_first_ionea_never");
        Changed?.Invoke();
        new MessageCard(w, T("home.close_the_game_first"), T("settings.ionea_never_updates_while_a_game")).Open();
    }

    // Where a downloaded release zip is looked for: the Inbox (if one is set), then the user's Downloads folder.
    public static IEnumerable<string> DownloadDirs()
    {
        if (AddGameLogic.Inbox != null) yield return AddGameLogic.Inbox;
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home)) yield return Path.Combine(home, "Downloads");
    }

    // The downloaded zip whose SHA-256 is the release's (the asset's own name first), in DownloadDirs().
    public static string FindInInbox(UpdateOffer o)
    {
        if (o.Sha256 == null) return null;
        foreach (string dir in DownloadDirs())
        {
            if (!Directory.Exists(dir)) continue;
            var files = Directory.GetFiles(dir, "*.zip")
                .OrderByDescending(f => Path.GetFileName(f).Equals(o.AssetName, StringComparison.OrdinalIgnoreCase))
                .Where(f => o.Size <= 0 || new FileInfo(f).Length == o.Size || Path.GetFileName(f).Equals(o.AssetName, StringComparison.OrdinalIgnoreCase));
            foreach (var f in files)
                if (Sha(f) == o.Sha256) return f;
        }
        return null;
    }

    static string Sha(string f)
    {
        using var s = File.OpenRead(f);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    // The release's zip into <install>\update\download (off the UI thread): through Online.Download (the network
    // connection, the repo's own address and the network log all apply), then its size and SHA-256 checked.
    // (null, path) when it is there, otherwise (why not, null); a wrong download is deleted.
    public static async Task<(string why, string zip)> FetchZip(MainWindow w, UpdateOffer o)
    {
        const string owner = Owner, name = Name;
        if (!InRepo(o.AssetApi, owner, name)) return (T("settings.the_zip_is_not_in", owner, name), null);
        string dir = Path.Combine(WorkDir, "download");
        Directory.CreateDirectory(dir);
        string dest = Path.Combine(dir, Path.GetFileName(o.AssetName));
        int code = 0;
        // Update now's question was the question (UpdateAskCard shows what and from where)
        var r = await Online.Download(w, "updates", o.AssetApi, dest, o.AssetName, ApiHeaders("application/octet-stream"), info: true, status: c => code = c).ConfigureAwait(false);
        if (r == OnlineResult.Blocked && Online.Offline) return (T("settings.the_network_connection_is_off_2"), null);
        if (r != OnlineResult.Done) return (T("settings.the_download_failed", o.AssetName, Online.Word(r) + (code > 0 ? ", " + code : "")), null);
        if ((o.Size > 0 && new FileInfo(dest).Length != o.Size) || Sha(dest) != o.Sha256)
        {
            try { File.Delete(dest); } catch { /* best-effort cleanup */ }
            return (T("settings.the_downloaded_doesn_t_match_the", o.AssetName), null);
        }
        Log.Write($"update: downloaded {o.AssetName}, SHA-256 matches");
        return (null, dest);
    }

    // The new Ionea in <install>\update\staged (off the UI thread). null when it is ready, otherwise why not.
    public static string Prepare(UpdateOffer o, string zip, out string staged)
    {
        staged = Path.Combine(WorkDir, "staged");
        try
        {
            if (Directory.Exists(staged)) Directory.Delete(staged, true);
            Directory.CreateDirectory(WorkDir);
            if (o.Kind == "folder")
            {
                if (Folder == null) return T("settings.set_the_source_folder_first");
                string script = Path.Combine(Folder, "Install-Launcher.ps1");
                if (!File.Exists(script)) return T("settings.there_is_no_install_launcher_ps1", Folder);
                string logFile = Path.Combine(WorkDir, "build.log");
                var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Folder };
                foreach (var x in new[] { "-NoProfile", "-ExecutionPolicy", T("settings.bypass"), T("settings.file"), script, "-BuildOnly", staged }) psi.ArgumentList.Add(x);
                Log.Write($"update: building {Folder}");
                using var p = Process.Start(psi);
                var outT = p.StandardOutput.ReadToEndAsync(); var errT = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(BuildMinutes * 60_000)) { try { p.Kill(true); } catch { /* already ended */ } return T("settings.the_build_took_over_minutes_and", BuildMinutes); }
                File.WriteAllText(logFile, outT.Result + Environment.NewLine + errT.Result);
                if (p.ExitCode != 0) return T("settings.the_build_failed_exit_see", p.ExitCode, logFile);
            }
            else
            {
                // the user's download; its SHA-256 again, right before anything is unpacked
                if (zip == null || !File.Exists(zip)) return T("settings.the_downloaded_zip_isn_t_there");
                if (Sha(zip) != o.Sha256) return T("settings.doesn_t_match_the_release_s_sha", Path.GetFileName(zip));
                string un = Path.Combine(WorkDir, "unpack");
                if (Directory.Exists(un)) Directory.Delete(un, true);
                Directory.CreateDirectory(un);
                var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "tar.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
                foreach (var x in new[] { "-xf", zip, "-C", un }) psi.ArgumentList.Add(x);
                using (var p = Process.Start(psi))
                {
                    string err = p.StandardError.ReadToEnd(); p.StandardOutput.ReadToEnd();
                    p.WaitForExit();
                    if (p.ExitCode != 0) return T("settings.the_zip_could_not_be_unpacked", err.Trim());
                }
                string top = un;
                var entries = Directory.GetFileSystemEntries(un);
                if (entries.Length == 1 && Directory.Exists(entries[0])) top = entries[0];
                if (!File.Exists(Path.Combine(top, "Launcher.exe"))) return T("settings.the_zip_has_no_launcher_exe");
                Directory.Move(top, staged);
                try { if (Directory.Exists(un)) Directory.Delete(un, true); } catch { /* best-effort cleanup */ }
                // a zip Ionea downloaded itself is not needed any more (the user's own download stays)
                if (Path.GetFullPath(zip).StartsWith(Path.Combine(WorkDir, "download") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    try { File.Delete(zip); } catch { /* best-effort cleanup */ }
            }
            string exe = Path.Combine(staged, "Launcher.exe");
            if (!File.Exists(exe)) return T("settings.the_build_made_no_launcher_exe");
            var v = AppVersion.Of(exe);
            Log.Write($"update: staged {v} in {staged}");
            return null;
        }
        catch (Exception e) { return T("settings.the_update_could_not_be_prepared", e.Message); }
    }

    // The staged Ionea in place of this one: the running exe and definitions become the previous version.
    // Fast (renames and one copy), on the UI thread. null when done, otherwise why not (nothing changed then).
    public static string Apply(string staged)
    {
        string exe = Exe, prev = PreviousExe, fresh = Path.Combine(staged, "Launcher.exe");
        string defs = Path.Combine(InstallDir, "definitions"), pdefs = defs + ".previous", ndefs = Path.Combine(staged, "definitions");
        bool movedExe = false, movedDefs = false;
        try
        {
            if (!File.Exists(fresh)) return T("settings.the_new_launcher_exe_is_missing");
            var nv = AppVersion.Of(fresh);   // (the exe's own path keeps answering with the running version)
            if (File.Exists(prev)) File.Delete(prev);
            File.Move(exe, prev); movedExe = true;   // the running exe: renamed, it keeps running
            File.Copy(fresh, exe);
            if (Directory.Exists(ndefs))
            {
                if (Directory.Exists(pdefs)) Directory.Delete(pdefs, true);
                if (Directory.Exists(defs)) { Directory.Move(defs, pdefs); movedDefs = true; }
                CopyTree(ndefs, defs);
            }
            foreach (var f in new[] { "THIRD-PARTY.md", "CHANGELOG.md" })
                if (File.Exists(Path.Combine(staged, f))) File.Copy(Path.Combine(staged, f), Path.Combine(InstallDir, f), true);
            Log.Write($"update: swapped in {nv}; {AppVersion.Full} kept as {Path.GetFileName(prev)}");
            Into = nv;
            NoteInstalledVersion(nv);
            try { Directory.Delete(staged, true); } catch { /* best-effort cleanup */ }
            return null;
        }
        catch (Exception e)
        {
            // back as it was
            try
            {
                if (movedExe) { if (File.Exists(exe)) File.Delete(exe); File.Move(prev, exe); }
                if (movedDefs) { if (Directory.Exists(defs)) Directory.Delete(defs, true); Directory.Move(pdefs, defs); }
            }
            catch (Exception e2) { Log.Write("update: putting it back failed: " + e2.Message); }
            Log.Write("update: swap failed: " + e.Message);
            return T("settings.the_new_version_could_not_be", e.Message);
        }
    }

    // Go back to the previous version: the two swap places (so going forward again works the same way).
    public static string SwapBack()
    {
        string exe = Exe, prev = PreviousExe, tmp = Path.Combine(InstallDir, "Launcher.swap.exe");
        string defs = Path.Combine(InstallDir, "definitions"), pdefs = defs + ".previous", tdefs = defs + ".swap";
        if (!File.Exists(prev)) return T("settings.there_is_no_previous_version");
        var bv = AppVersion.Of(prev);
        int step = 0;
        try
        {
            if (File.Exists(tmp)) File.Delete(tmp);
            File.Move(exe, tmp); step = 1;
            File.Move(prev, exe); step = 2;
            File.Move(tmp, prev); step = 3;
            if (Directory.Exists(pdefs))
            {
                if (Directory.Exists(tdefs)) Directory.Delete(tdefs, true);
                if (Directory.Exists(defs)) Directory.Move(defs, tdefs);
                Directory.Move(pdefs, defs);
                if (Directory.Exists(tdefs)) Directory.Move(tdefs, pdefs);
            }
            Log.Write($"update: back to {bv}; {AppVersion.Full} kept as {Path.GetFileName(prev)}");
            Into = bv;
            NoteInstalledVersion(bv);
            return null;
        }
        catch (Exception e)
        {
            try
            {
                if (step == 1) File.Move(tmp, exe);
                else if (step == 2) { File.Move(exe, prev); File.Move(tmp, exe); }
            }
            catch (Exception e2) { Log.Write("update: putting it back failed: " + e2.Message); }
            Log.Write("update: going back failed: " + e.Message);
            return T("settings.could_not_go_back", e.Message);
        }
    }

    static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var d in Directory.GetDirectories(from, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, d)));
        foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories)) File.Copy(f, Path.Combine(to, Path.GetRelativePath(from, f)), true);
    }

    // Installed with installer\Ionea.iss (per user): Windows' list of installed apps shows the version now in place.
    // Only the installer's own entry for this very folder; a portable copy has none, and nothing is created.
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{45EA0111-6439-4817-9D26-7E1A24869E40}_is1";
    static void NoteInstalledVersion(Version v)
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(UninstallKey, true);
            if (k?.GetValue("InstallLocation") is not string loc || v == null) return;
            if (!string.Equals(Path.GetFullPath(loc).TrimEnd('\\'), Path.GetFullPath(InstallDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;
            k.SetValue("DisplayVersion", v.ToString());
        }
        catch (Exception e) { Log.Write("update: installed version not noted: " + e.Message); }
    }

    // Ionea closes (outro) and the exe now in place starts again (Program.Main, after the lock is let go).
    public static void Restart(MainWindow w)
    {
        P.RestartedFrom = AppVersion.Full.ToString(); settings.Save();
        Program.RestartExe = Exe;
        Log.Write($"update: restarting into {Into}");
        BeforeRestart?.Invoke();
        w.Close();
    }

    // At start: the toast after an update, and the launch check when Update notification or Auto update is on.
    public static void AfterStart(MainWindow w, bool script)
    {
        if (P.RestartedFrom is string from)
        {
            P.RestartedFrom = null; settings.Save();
            if (Version.TryParse(from, out var fv) && fv != AppVersion.Full)
            {
                bool back = fv > AppVersion.Full;
                Log.Write($"update: started {AppVersion.Full} after {(back ? "going back from" : "updating from")} {fv}");
                Ui.After(1800, () => w.Toasts.Show(back ? T("settings.back_to_the_previous_version") : T("settings.ionea_updated"), $"{AppVersion.Three(fv)} → {AppVersion.Number}" + (AppVersion.BuildNo > 0 ? T("settings.build", AppVersion.BuildNo) : ""), "info"));
            }
        }
        if ((P.CheckAtStart || P.AutoUpdate) && !script) Ui.After(8000, () => _ = AtLaunch(w));
    }

    // The launch check (also TestScript updatelaunch): only with the network connection on (the repo; a test
    // folder needs none). A new version is a pop-up (Update notification), or with Auto update it is installed
    // and Ionea restarts into it; never while a game runs (then a pop-up says it waits for Update now).
    public static async Task AtLaunch(MainWindow w)
    {
        if (Busy || (RepoKind && Online.Offline)) return;
        if (!P.CheckAtStart && !P.AutoUpdate) return;
        await Check(w, quiet: true, notify: !P.AutoUpdate);
        if (!P.AutoUpdate || State != UpdateState.Available || Offer == null) return;
        if (GameRunning(w))
        {
            Log.Write("update: auto update waits, a game is running");
            w.Toasts.Show(T("settings.update_ready"), T("settings.ionea_is_ready_auto_update_waits", Offer.VersionText), "info");
            return;
        }
        Log.Write("update: auto update to " + Offer.VersionText);
        UpdateNow(w, confirmed: true);
    }

    // ---- what the rows show ----
    public static string OfferText => Offer == null ? (State == UpdateState.Building ? T("settings.building") : T("settings.nothing_new")) : Offer.VersionText;
    public static string LastCheckedText =>
        P.LastChecked is DateTime t ? (t.Date == DateTime.Today ? T("settings.today_2", t.ToString("HH:mm")) : t.ToString("d MMM HH:mm", CultureInfo.GetCultureInfo("en-GB"))) : T("settings.never");
    public static string TestState => State.ToString().ToLowerInvariant() + (Offer != null ? " " + Offer.Version : "");

    // The changelog to show: the checked source's (or release's), else this version's own CHANGELOG.md.
    public static (string title, string text) Changelog()
    {
        if (Offer?.Changelog != null) return (T("settings.what_s_new_in", Offer.VersionText), Offer.Changelog);
        if (!RepoKind && Folder != null && ReadChangelog(Path.Combine(Folder, "CHANGELOG.md")) is string s) return (T("settings.changelog_source_folder"), s);
        if (ReadChangelog(Path.Combine(InstallDir, "CHANGELOG.md")) is string m) return (T("settings.changelog"), m);
        return (T("settings.changelog"), null);
    }
}

// Update now asks first: what it installs and that it restarts. Cancel is focused.
public sealed class UpdateAskCard : MenuCard
{
    public UpdateAskCard(MainWindow w, UpdateOffer o, string zip) : base(w, T("settings.update_ionea"), 1100)
    {
        string how = o.Kind == "folder"
            ? T("settings.ionea_builds_the_source_folder_which", Updater.Folder, o.VersionText)
            : zip != null ? T("settings.ionea_unpacks_sha_256_matches_the", System.IO.Path.GetFileName(zip), o.VersionText)
            : o.Size > 0 ? T("settings.ionea_downloads_size", o.AssetName, (o.Size / 1048576.0).ToString("0.0"), Updater.RepoName, o.Tag, o.VersionText)
            : T("settings.ionea_downloads", o.AssetName, Updater.RepoName, o.Tag, o.VersionText);
        Note($"{AppVersion.Number} → {o.VersionText}. {how}", 28, new Thickness(4, -6, 0, 12));
        Note(T("settings.games_settings_and_saves_are_kept"), 24, new Thickness(4, 0, 0, 24));
        AddRow(T("common.cancel"), activate: () => Close());
        WithIcon(AddRow(T("settings.update_and_restart"), activate: () => { Close(false); new UpdatingCard(w, o, zip).Open(); }), "restart");
    }
}

// The build or unpack runs with a spinner; then the swap and the restart. A failure says why; nothing is changed then.
public sealed class UpdatingCard : MenuCard
{
    public static bool Working;   // tests

    public UpdatingCard(MainWindow w, UpdateOffer o, string zip) : base(w, o.Kind == "folder" ? T("settings.building_ionea", o.VersionText) : zip == null ? T("settings.downloading_ionea", o.VersionText) : T("settings.unpacking_ionea", o.VersionText), 1100)
    {
        Working = true;
        LoadThen(() =>
        {
            string z = zip;
            if (o.Kind == "repo" && z == null)
            {
                var (fwhy, path) = Updater.FetchZip(w, o).GetAwaiter().GetResult();
                if (fwhy != null) return (why: fwhy, staged: (string)null);
                z = path;
            }
            string pwhy = Updater.Prepare(o, z, out string st); return (why: pwhy, staged: st);
        }, r =>
        {
            Working = false;
            string why = r.why ?? (Updater.GameRunning(w) ? "A game started meanwhile; Ionea never updates while a game is running." : Updater.Apply(r.staged));
            if (why == null) { Note(T("settings.restarting"), 28); Ui.After(300, () => Updater.Restart(w)); return; }
            Log.Write("update: not done: " + why);
            Note(why, 28, new Thickness(4, 0, 0, 24));
            AddRow(T("common.ok"), activate: () => Close());
        });
    }
}

// Go back to the previous version: asks, then swaps and restarts.
public sealed class GoBackCard : MenuCard
{
    public GoBackCard(MainWindow w) : base(w, T("settings.go_back_to_the_previous_version"), 1100)
    {
        var pv = Updater.PreviousVersion;
        Note(T("settings.ionea_restarts_as", pv == null ? T("settings.the_previous_version") : AppVersion.Describe(pv, null)), 28, new Thickness(4, -6, 0, 24));
        AddRow(T("common.cancel"), activate: () => Close());
        WithIcon(AddRow(T("settings.go_back_and_restart"), activate: () =>
        {
            if (Updater.GameRunning(w)) { Close(false); new MessageCard(w, T("home.close_the_game_first"), T("settings.ionea_never_changes_version_while_a")).Open(); return; }
            string why = Updater.SwapBack();
            if (why != null) { Close(false); new MessageCard(w, T("settings.could_not_go_back_2"), why).Open(); return; }
            Close(false);
            Updater.Restart(w);
        }), "restart");
    }
}

public sealed class ChangelogCard : MenuCard
{
    public ChangelogCard(MainWindow w) : base(w, Updater.Changelog().title, 1300)
    {
        string text = Updater.Changelog().text;
        var t = Note(text ?? T("settings.the_source_has_no_changelog_md"), 24, new Thickness(4, 0, 0, 24));
        if (text != null) t.Foreground = P.Text;
        AddRow(T("common.done"), activate: () => Close());
    }
}
