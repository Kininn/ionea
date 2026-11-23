#nullable disable
// Discord status: "Playing Ionea" on the user's Discord profile. Clicking the profile shows the
// game (line 1), the emulator, "Steam" or "PC" (line 2, "· Paused" while the Home menu holds it) and the time
// since it started. With no game open it shows just Ionea and its icon.
// Pictures: Ionea's icon is always the big picture; the game's cover is the small one in its
// corner (its name on hover) when the cover has a public web address (Covers.PublicUrl: Steam, LibRetro,
// SteamGridDB). Discord fetches that address itself; a cover only on this PC means no small picture.
// A game played through a native port says "PC" on line 2, not its emulator (Launch.PlayingNative).
//
// How: the Discord desktop app on this PC listens on a local named pipe (\\.\pipe\discord-ipc-0 .. 9). Ionea
// sends it a handshake with the Ionea application id, then SET_ACTIVITY frames. Ionea itself never goes online;
// the Discord app does. No Discord running: try again every 15 s. Discord drops the status when the pipe closes.
// The application ("Ionea", id below) and its art asset "ionea" live in the Discord Developer Portal.
// Per profile on/off: Settings > Profile or Network > Discord status (Settings.Discord, ProfilePrefs.Discord).
// Off, or the network connection off: the pipe is closed and not opened again until it is on. Each connection
// is written to the network activity log (Online.Note).
// Test hook: IONEA_DISCORD_PIPE names the pipe to use instead (a fake Discord), so tests never reach the real one.
using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using static Ionea.Loc;

namespace Ionea;

public sealed class DiscordPresence : IDisposable
{
    public const string AppId = "1572029408242438235";
    const string Art = "ionea";   // Rich Presence art asset key in the Developer Portal
    const int RetryMs = 15000, GapMs = 4000, IoMs = 5000;   // Discord allows about 5 updates per 20 s

    // What Ionea wants Discord to show. Null game = the home screen; Off = nothing at all.
    // Pic: the game's cover at a public https address (the small picture), or null.
    public sealed record Wish(bool Off, string Game, string Line2, bool Paused, DateTime? Since, string Pic = null);

    readonly object gate = new();
    readonly AutoResetEvent kick = new(false);
    readonly Thread thread;
    readonly string pipeOverride = Environment.GetEnvironmentVariable("IONEA_DISCORD_PIPE");
    Wish wanted;   // null until the launcher says what to show
    volatile bool stop;

    public string Status { get; private set; } = "starting";   // waiting (no Discord), connected
    public string Shown { get; private set; } = "none";         // for tests: off | home | <game>|<line 2>
    // For tests, also with no Discord: what the launcher asks for now (as Shown) and its small picture.
    public string Wanted { get { lock (gate) return wanted == null ? "none" : Describe(wanted); } }
    public string WantedPic { get { lock (gate) return Pic(wanted) ?? "none"; } }

    // enabled false (a test run without a fake Discord): never touches any pipe.
    public DiscordPresence(bool enabled)
    {
        thread = new Thread(Run) { IsBackground = true, Name = "discord status" };
        if (enabled) thread.Start(); else Status = "disabled";
    }

    public void Set(Wish w)
    {
        lock (gate) { if (w == wanted) return; wanted = w; }
        kick.Set();
    }

    public void Dispose()
    {
        stop = true; kick.Set();
        if (thread.IsAlive) thread.Join(1500);   // closes the pipe: Discord takes the status away
    }

    void Run()
    {
        while (!stop)
        {
            Wish now; lock (gate) now = wanted;
            if (now == null || now.Off) { Status = "off"; kick.WaitOne(); continue; }   // never touches the pipe while off
            NamedPipeClientStream pipe = null;
            try
            {
                pipe = Connect();
                if (pipe == null) { Status = "waiting"; kick.WaitOne(RetryMs); continue; }
                Write(pipe, 0, new JsonObject { ["v"] = 1, ["client_id"] = AppId });
                var ready = Read(pipe);
                if (ready.op != 1) throw new IOException("Discord answered the handshake with op " + ready.op + ": " + ready.json);
                Status = "connected";
                Log.Write("discord: connected");
                Online.Note("discord", "the Discord app on this PC", "connected");
                Wish sent = null;
                while (!stop && pipe.IsConnected)
                {
                    Wish w; lock (gate) w = wanted;
                    if (w == null || w.Off) break;   // closing the pipe takes the status away
                    if (w != sent)
                    {
                        Write(pipe, 1, Frame(w));
                        var r = Read(pipe);
                        if (r.json.Contains("\"evt\":\"ERROR\"")) Log.Write("discord: status refused: " + r.json);
                        sent = w; Shown = Describe(w);
                        for (int k = 0; k < GapMs / 100 && !stop; k++) Thread.Sleep(100);   // Discord's rate limit; newer wishes wait
                        continue;
                    }
                    kick.WaitOne(RetryMs);
                }
            }
            catch (Exception e) { if (!stop) Log.Write("discord: " + e.Message); }
            finally
            {
                pipe?.Dispose();
                if (Status == "connected" && !stop) Log.Write("discord: disconnected");
                Status = "waiting"; Shown = "none";
            }
            if (!stop) kick.WaitOne(RetryMs);
        }
    }

    NamedPipeClientStream Connect()
    {
        // Discord takes the first free number; stable, PTB and Canary may all be running.
        for (int i = 0; i < 10; i++)
        {
            string name = pipeOverride ?? "discord-ipc-" + i;
            var p = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            try { p.Connect(200); return p; }
            catch { p.Dispose(); }
            if (pipeOverride != null) break;
        }
        return null;
    }

    static JsonObject Frame(Wish w)
    {
        var args = new JsonObject { ["pid"] = Environment.ProcessId };
        if (!w.Off)
        {
            var a = new JsonObject
            {
                ["type"] = 0,   // Playing
                ["assets"] = new JsonObject { ["large_image"] = Art, ["large_text"] = "Ionea" },
            };
            if (w.Game != null)
            {
                if (Fit(w.Game) is string d) a["details"] = d;
                string line2 = w.Paused ? (w.Line2 == null ? T("home.paused") : w.Line2 + T("errors.paused")) : w.Line2;
                if (Fit(line2) is string s) a["state"] = s;
                if (w.Since is DateTime t) a["timestamps"] = new JsonObject { ["start"] = new DateTimeOffset(t).ToUnixTimeSeconds() };
                if (Pic(w) is string pic)
                {
                    var assets = (JsonObject)a["assets"];
                    assets["small_image"] = pic;
                    if (Fit(w.Game) is string name) assets["small_text"] = name;
                }
            }
            args["activity"] = a;
        }
        // No "activity": Discord clears the status.
        return new JsonObject { ["cmd"] = "SET_ACTIVITY", ["args"] = args, ["nonce"] = Guid.NewGuid().ToString("N") };
    }

    // Discord takes 2 to 128 characters per line.
    static string Fit(string s)
    {
        s = s?.Trim();
        if (string.IsNullOrEmpty(s) || s.Length < 2) return null;
        return s.Length <= 128 ? s : s[..127] + "…";
    }

    // The small picture: only for a game, only https (Discord's media proxy takes nothing else), at most 256 characters.
    static string Pic(Wish w) => w != null && !w.Off && w.Game != null && w.Pic is string p && p.Length <= 256
        && p.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? p : null;

    static string Describe(Wish w) => w.Off ? "off" : w.Game == null ? "home"
        : w.Game + "|" + (w.Paused ? (w.Line2 == null ? T("home.paused") : w.Line2 + T("errors.paused")) : w.Line2 ?? "");

    // Frame: op (int32 LE), length (int32 LE), UTF-8 JSON.
    static void Write(Stream s, int op, JsonNode body)
    {
        byte[] json = Encoding.UTF8.GetBytes(body.ToJsonString());
        var buf = new byte[8 + json.Length];
        BinaryPrimitives.WriteInt32LittleEndian(buf, op);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4), json.Length);
        json.CopyTo(buf, 8);
        using var cts = new CancellationTokenSource(IoMs);
        s.WriteAsync(buf, cts.Token).AsTask().GetAwaiter().GetResult();
        s.Flush();
    }

    static (int op, string json) Read(Stream s)
    {
        var head = ReadExactly(s, 8);
        int op = BinaryPrimitives.ReadInt32LittleEndian(head);
        int len = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(4));
        if (len < 0 || len > 1 << 20) throw new IOException("bad frame from Discord");
        string json = Encoding.UTF8.GetString(ReadExactly(s, len));
        if (op == 2) throw new IOException("Discord closed the connection: " + json);
        return (op, json);
    }

    static byte[] ReadExactly(Stream s, int n)
    {
        var buf = new byte[n];
        using var cts = new CancellationTokenSource(IoMs);
        s.ReadExactlyAsync(buf, cts.Token).AsTask().GetAwaiter().GetResult();
        return buf;
    }
}
