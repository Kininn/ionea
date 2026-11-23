#nullable disable
// Network. Ionea reaches the internet only for the kinds of use the
// user switched on, and not at all while Offline mode is on (per profile, on for every new profile and
// install). Every download goes through Online.Download:
//   - Offline mode on, or the item's switch off: refused before any request is made;
//   - an address outside the item's site (or plain http): refused the same way;
//   - a program (emulator or core): asks every time and shows the address, even with the switch on;
//   - every request is written to data\online-log.json (when, what for, the host, how it ended): each redirect
//     hop is its own entry, refused attempts are written too. The log stays on this PC; Settings > Network >
//     Network activity shows it and clears it.
// Nothing else in Ionea goes online: no telemetry, no update or package check of its own. The Discord status
// talks only to the Discord app on this PC (Discord.cs), and only with Offline mode off.
// The source switches belong to the PC (the shared settings.json, Settings.OnlineUse); Offline mode is per
// profile (Settings.Offline / ProfilePrefs.Offline).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using static Ionea.Loc;

namespace Ionea;

// item id -> allowed. An id that is missing was never asked: it counts as off, and the Going online screen
// shows again at the next start (a new kind of online use in a later version starts off and asks).
public sealed class OnlinePrefs
{
    public Dictionary<string, bool> On { get; set; } = new();
}

public sealed class OnlineItem
{
    public string Id, LabelId, Site;
    public string Label => T(LabelId);   // read when shown
    public string[] Hosts;   // the only places it may download from (and their subdomains); anything else is refused
    public string[] RedirectHosts;   // reached only when a host above sends the request on (GitHub's file storage), never first
    public bool Program;     // an emulator or core: asks every time, even with its switch on
}

public sealed class OnlineLogEntry
{
    public DateTime When { get; set; }
    public string Item { get; set; }
    public string Url { get; set; }
    public string Result { get; set; }   // done, blocked, cancelled, failed
    public long Bytes { get; set; }
    public string Note { get; set; }     // why it was blocked or failed
}

public sealed class OnlineLogFile
{
    public int Version { get; set; } = 1;
    public List<OnlineLogEntry> Entries { get; set; } = new();
}

public enum OnlineResult { Done, Blocked, Cancelled, Failed }

public static class Online
{
    public static readonly OnlineItem[] Items =
    {
        new() { Id = "covers", LabelId = "settings.covers", Site = "thumbnails.libretro.com", Hosts = new[] { "thumbnails.libretro.com" } },
        new() { Id = "names", LabelId = "settings.game_names_database", Site = "github.com", Hosts = new[] { "github.com", "githubusercontent.com" } },
        // GitHub releases (the definitions' release.github); a definition's fixed release address adds its host (Online.HostsOf)
        new() { Id = "emulators", LabelId = "settings.emulator_installs", Site = "github.com", Hosts = new[] { "github.com", "githubusercontent.com" }, Program = true },
        new() { Id = "cores", LabelId = "settings.cores", Site = "buildbot.libretro.com", Hosts = new[] { "buildbot.libretro.com" }, Program = true },
        new() { Id = "definitions", LabelId = "settings.definition_updates", Site = "github.com", Hosts = new[] { "github.com", "githubusercontent.com" } },
        new() { Id = "steamgriddb", LabelId = "settings.covers_from_steamgriddb", Site = "steamgriddb.com", Hosts = new[] { "steamgriddb.com" } },   // needs the user's own key
        // Updater.cs, repo kind only: the set repo's latest release, its SHA-256 list and zip, all asked of
        // api.github.com/repos/<owner>/<repo>/ (Updater.RepoApi). GitHub sends the files on to its file storage.
        new() { Id = "updates", LabelId = "settings.launcher_updates", Site = "github.com", Hosts = new[] { "api.github.com", "github.com" }, RedirectHosts = new[] { "githubusercontent.com" }, Program = true },
    };
    const int Keep = 500;   // log entries kept

    static Settings settings;
    static string logFile;
    static OnlineLogFile log;
    public static string TestHost;       // test runs only (--online-test-host host:port): a local server every item may use, http too
    public static int Requests;          // requests sent since start (tests: none while a switch is off)
    public static int Pending;           // downloads not finished yet (a program's question counts)
    public static OnlineResult? Last;    // how the latest download ended (tests)
    // A log line keeps the address without its query: a signed file address carries a one-time key there.
    static string Clean(string url) { int q = url?.IndexOfAny(new[] { '?', '#' }) ?? -1; return q < 0 ? url : url[..q]; }
    public static event Action Changed;  // Offline mode or a switch changed (the Discord status follows)
    static readonly object gate = new(); // the log is written from the download and Discord threads too
    static string[] emuHosts;

    public static void Init(Settings s, string dataDir)
    {
        settings = s;
        settings.OnlineUse ??= new(); settings.OnlineUse.On ??= new();
        logFile = Path.Combine(dataDir, "online-log.json");
    }

    public static OnlineItem Find(string id) => Items.FirstOrDefault(i => i.Id == id);
    static Dictionary<string, bool> On => settings?.OnlineUse?.On ?? new();
    // Offline mode: no online connection of any kind; the source switches keep their answers underneath.
    public static bool Offline => settings?.Offline ?? true;
    public static bool Switched(string id) => On.TryGetValue(id, out bool on) && on;   // the switch itself
    public static bool Allowed(string id) => !Offline && Switched(id);                  // may go online now

    public static void SetOffline(bool on)
    {
        if (settings == null || settings.Offline == on) return;
        settings.Offline = on; settings.Save();
        Log.Write($"online: offline mode {(on ? "on" : "off")}");
        Changed?.Invoke();
    }

    // The hosts an item may reach (and their subdomains), as Settings > Network shows them.
    public static string[] HostsOf(OnlineItem it)
    {
        if (it.Id != "emulators") return it.Hosts;
        if (emuHosts == null)
        {
            var extra = new List<string>();
            try
            {
                EmuDefs.EnsureLoaded();
                foreach (var d in EmuDefs.All)
                    if (!d.IsCore && d.Release?.Url != null && Uri.TryCreate(d.Release.Url, UriKind.Absolute, out Uri du)) extra.Add(du.Host.ToLowerInvariant());
            }
            catch (Exception e) { Log.Write("online: definition hosts: " + e.Message); }
            emuHosts = it.Hosts.Concat(extra).Distinct().ToArray();
        }
        return emuHosts;
    }
    public static string HostsText(OnlineItem it) => string.Join(", ", HostsOf(it)) + (it.RedirectHosts is { Length: > 0 } r ? T("settings.files_from", string.Join(", ", r)) : "");
    public static string Label(string id) => Find(id)?.Label ?? (id == "discord" ? T("settings.discord_status") : id);
    public static bool AllAsked => Items.All(i => On.ContainsKey(i.Id));

    public static void Set(string id, bool on)
    {
        if (Find(id) == null) throw new ArgumentException("no online item " + id);
        On[id] = on; settings.Save();
        Log.Write($"online: {id} switched {(on ? "on" : "off")}");
        Changed?.Invoke();
    }

    // The Going online screen was answered: every item not switched on stays off.
    public static void MarkAsked()
    {
        if (AllAsked) return;
        foreach (var i in Items) On.TryAdd(i.Id, false);
        settings.Save();
        Log.Write("online: " + string.Join(", ", Items.Select(i => i.Id + " " + (Allowed(i.Id) ? "on" : "off"))));
    }

    // Tests: forget one answer, as if that item were new in this version.
    public static void Forget(string id) { On.Remove(id); settings.Save(); }

    public static List<OnlineLogEntry> Entries
    {
        get
        {
            lock (gate)
            {
                if (log == null) { log = logFile == null ? new() : Json.Load<OnlineLogFile>(logFile); log.Entries ??= new(); }
                return log.Entries;
            }
        }
    }

    // A copy, newest last (the log card reads it while a download may be adding to it).
    public static List<OnlineLogEntry> Snapshot() { lock (gate) return Entries.ToList(); }

    // One line in the activity log (any thread). Saved at once; only on this PC.
    public static void Note(string item, string url, string result, long bytes = 0, string note = null)
    {
        try
        {
            lock (gate)
            {
                Entries.Add(new OnlineLogEntry { When = DateTime.Now, Item = item, Url = Clean(url), Result = result, Bytes = bytes, Note = note });
                if (Entries.Count > Keep) Entries.RemoveRange(0, Entries.Count - Keep);
                if (logFile != null) Json.Save(logFile, log);
            }
        }
        catch (Exception e) { Log.Write("online log: " + e.Message); }
    }

    public static void ClearLog()
    {
        try { lock (gate) { Entries.Clear(); if (logFile != null) Json.Save(logFile, log); } }
        catch (Exception e) { Log.Write("online log: " + e.Message); }
        Log.Write("online: activity log cleared");
    }

    // ---- the one download path ----

    static readonly HttpClient http = MakeClient();
    static HttpClient MakeClient()
    {
        // Redirects are followed by hand so each new address is checked against the item's site first.
        var h = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(20) };
        var c = new HttpClient(h) { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Ionea-launcher");
        return c;
    }

    // null when the item may fetch from this address, otherwise why not.
    static string Refuse(OnlineItem it, Uri u, bool redirect = false)
    {
        if (u == null || !u.IsAbsoluteUri) return "not an address";
        if (TestHost != null && string.Equals(u.Authority, TestHost, StringComparison.OrdinalIgnoreCase) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)) return null;
        if (u.Scheme != Uri.UriSchemeHttps) return "not https";
        var hosts = HostsOf(it);
        if (redirect && it.RedirectHosts != null) hosts = (hosts ?? Array.Empty<string>()).Concat(it.RedirectHosts).ToArray();
        if (hosts == null || !hosts.Any(h => u.Host.Equals(h, StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase)))
            return "not " + it.Site;
        return null;
    }

    // Download url to the file dest for one item. name: what a program's question calls it. headers: sent to the
    // first address's host only (a key never follows a redirect elsewhere). info: a program item's small page
    // about a release (its list of files), fetched without the question; the program itself is asked (unless the
    // caller asked already, as Update now does). status: the HTTP status code of a refused request (401, 404, ...).
    public static async Task<OnlineResult> Download(MainWindow w, string itemId, string url, string dest, string name = null,
        IDictionary<string, string> headers = null, bool info = false, Action<int> status = null)
    {
        Pending++;
        Last = null;
        try
        {
            var it = Find(itemId);
            Uri.TryCreate(url, UriKind.Absolute, out Uri u);
            if (it == null) return Write(itemId, url, OnlineResult.Blocked, 0, "unknown kind of download");
            if (Offline) return Write(it.Id, url, OnlineResult.Blocked, 0, "offline mode");
            if (!Allowed(it.Id)) return Write(it.Id, url, OnlineResult.Blocked, 0, "switched off");
            if (Refuse(it, u) is string why) return Write(it.Id, url, OnlineResult.Blocked, 0, why);
            if (it.Program && !info && !await Ask(w, name ?? Path.GetFileNameWithoutExtension(dest), u.AbsoluteUri))
                return Write(it.Id, url, OnlineResult.Cancelled, 0, null);
            if (!Allowed(it.Id)) return Write(it.Id, url, OnlineResult.Blocked, 0, Offline ? "offline mode" : "switched off");   // changed while asking
            var at = new string[] { u.AbsoluteUri };   // the address of the last request (after redirects)
            try { long n = await Fetch(it, u, dest, headers, at, status); return Write(it.Id, at[0], OnlineResult.Done, n, null); }
            catch (Exception e) { return Write(it.Id, at[0], OnlineResult.Failed, 0, e.Message); }
        }
        finally { Pending--; }
    }

    static async Task<long> Fetch(OnlineItem it, Uri u, string dest, IDictionary<string, string> headers, string[] at, Action<int> status)
    {
        string part = dest + ".part";
        string keyHost = u.Host;
        try
        {
            for (int hop = 0; ; hop++)
            {
                System.Threading.Interlocked.Increment(ref Requests);
                // off the UI thread from here: a big file doesn't hold up the screen
                using var req = new HttpRequestMessage(HttpMethod.Get, u);
                if (headers != null && u.Host.Equals(keyHost, StringComparison.OrdinalIgnoreCase))
                    foreach (var h in headers) req.Headers.TryAddWithoutValidation(h.Key, h.Value);
                using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                int code = (int)res.StatusCode;
                if (code is >= 300 and < 400 && res.Headers.Location is Uri loc)
                {
                    if (hop >= 5) throw new IOException("too many redirects");
                    var next = loc.IsAbsoluteUri ? loc : new Uri(u, loc);
                    if (Refuse(it, next, redirect: true) is string why) throw new IOException($"sent on to {next.Host}: {why}");
                    Log.Write($"online: {it.Id} sent on to {Clean(next.AbsoluteUri)}");
                    Note(it.Id, u.AbsoluteUri, "sent on", 0, "to " + next.Host);   // this request was a redirect
                    u = next; at[0] = u.AbsoluteUri;
                    continue;
                }
                if (code < 200 || code >= 300) { status?.Invoke(code); throw new IOException($"{code} {res.ReasonPhrase}"); }
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dest)));
                long n;
                await using (var fs = File.Create(part)) { await res.Content.CopyToAsync(fs).ConfigureAwait(false); n = fs.Length; }
                File.Move(part, dest, true);
                return n;
            }
        }
        catch { try { File.Delete(part); } catch { /* best-effort cleanup; the error is thrown on */ } throw; }
    }

    static Task<bool> Ask(MainWindow w, string name, string address)
    {
        var tcs = new TaskCompletionSource<bool>();
        new DownloadCard(w, name, address, ok => tcs.TrySetResult(ok)).Open();
        return tcs.Task;
    }

    static OnlineResult Write(string item, string url, OnlineResult r, long bytes, string note)
    {
        Last = r;
        string word = Word(r);
        Log.Write($"online: {item} {word} {Clean(url)}" + (note != null ? $" ({note})" : "") + (r == OnlineResult.Done ? $", {bytes} bytes" : ""));
        Note(item, url, word, bytes, note);
        return r;
    }

    public static string Word(OnlineResult r) => r.ToString().ToLowerInvariant();

    public static string HostOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out Uri u) ? (u.IsDefaultPort ? u.Host : u.Authority) : url;
}

// The first start's Network screen: Offline mode (on), then a switch per kind of use, all off, each with
// its hosts. Closing it (Done or B) is the answer; what was not switched on stays off.
public sealed class OnlineCard : MenuCard
{
    readonly Action then;
    bool done;

    public OnlineCard(MainWindow w, Action then = null) : base(w, T("settings.network"), 1180)
    {
        this.then = then;
        // One press for the usual answer: Offline mode off and every source on (programs still ask each time).
        WithIcon(AddRow(T("settings.turn_everything_on"), activate: () =>
        {
            if (Online.Offline) Online.SetOffline(false);
            foreach (var i in Online.Items) if (!Online.Switched(i.Id)) Online.Set(i.Id, true);
            Sounds.Play("select");
            Refresh();
        }), "globe");
        OnlineRows();
        Divider();
        AddRow(T("common.done"), activate: () => Close());
    }

    public override void Close(bool sound = true)
    {
        Online.MarkAsked();
        base.Close(sound);
        if (done) return;
        done = true;
        then?.Invoke();
    }
}

// Settings > Network > Network activity: every request (and refused attempt), newest first: when, what for,
// the host. Stored only in data\online-log.json on this PC; Clear log empties it.
public sealed class OnlineLogCard : MenuCard
{
    public const int Shown = 60;

    public OnlineLogCard(MainWindow w) : base(w, T("settings.network_activity"), 1440)
    {
        var list = Online.Snapshot();
        Note(T("settings.kept_on_this_pc_only"), 24, new Thickness(4, -6, 0, 18));
        if (list.Count == 0) Note(T("settings.nothing_yet_2"), 28, new Thickness(4, 0, 0, 18));
        foreach (var e in Enumerable.Reverse(list).Take(Shown))
        {
            var en = e;
            AddRow(When(en.When) + "   " + Online.Label(en.Item), () =>
                (en.Result == "done" ? "" : Status(en.Result, en.Note) + "  ·  ") + Online.HostOf(en.Url));
        }
        if (list.Count > Shown) Note(T("settings.older_in_the_file", list.Count - Shown), 24, new Thickness(4, 6, 0, 14));
        if (list.Count > 0) AddRow(T("settings.clear_log"), activate: () => { Online.ClearLog(); Close(); new OnlineLogCard(w).Open(); });
        AddRow(T("common.done"), activate: () => Close());
    }

    // A refused attempt says why: Offline mode, its switch, or a host outside its source.
    static string Status(string r, string note) => r == "blocked" ? note switch
    {
        "offline mode" => T("settings.offline"), "switched off" => T("settings.switched_off"),
        string n when n.StartsWith("not ") => T("settings.other_host"), _ => T("settings.blocked"),
    } : r switch { "cancelled" => T("settings.cancelled"), "failed" => T("settings.failed"), "sent on" => T("settings.sent_on"), "connected" => T("settings.connected"), _ => r };
    static string When(DateTime t) => t.Date == DateTime.Today ? t.ToString("HH:mm") : t.ToString("d MMM HH:mm", System.Globalization.CultureInfo.InvariantCulture);
}

// A program download asks every time and shows the address. Cancel is focused first; B cancels.
public sealed class DownloadCard : MenuCard
{
    readonly Action<bool> answer;
    bool replied;
    public readonly string Address;

    public DownloadCard(MainWindow w, string name, string address, Action<bool> answer) : base(w, T("settings.download", name), 1180)
    {
        this.answer = answer; Address = address;
        var t = Note(address, 28, new Thickness(4, -6, 0, 26));
        t.Foreground = P.Text;
        AddRow(T("common.cancel"), activate: () => Close());
        WithIcon(AddRow(T("settings.download_2"), activate: () => { Reply(true); Close(false); }), "save");
    }

    void Reply(bool ok) { if (replied) return; replied = true; answer(ok); }

    public override void Close(bool sound = true) { base.Close(sound); Reply(false); }
}
