#nullable disable
// Game helper: a program that runs beside one game while it plays, e.g. a local stand-in for an
// online service the game asks for before it starts. Set per game in library.json:
//   "Helper": { "Program": "<exe>", "Args": "<arguments>", "Dir": "<working folder, optional>" }
// Before the emulator starts, Ionea runs the helper with its standard input and output as pipes and reads its lines:
//   env NAME=value   added to the emulator's environment for this start
//   say <text>       written to launcher.log
//   fail <text>      the start is refused, <text> is shown
//   ready            the emulator starts now
//   own name         the emulator starts under its own program file, not the link named like the launcher
//                    (EmbedLogic.Alias). Windows treats a program called Launcher.exe differently: a version.dll
//                    beside it was not loaded. It is then not in the launcher's Task Manager group.
// Other lines are logged. It must say ready within ReadySeconds. When the game has ended (or did not start), Ionea
// closes the helper's standard input, its sign to tidy up and exit, waits up to StopSeconds, then ends it and
// everything it started. It is held in a kill-on-close job, so it also ends when Ionea does.
// Emulator and native-port starts only (Launch.cs); hosted cores, Steam and PC games don't use it.
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using static Ionea.Loc;

namespace Ionea;

public sealed class HelperRun
{
    // IONEA_TEST_HELPER_READY=<seconds>: a shorter wait for a test (test\gh1-helper.ps1).
    public static int ReadySeconds = int.TryParse(Environment.GetEnvironmentVariable("IONEA_TEST_HELPER_READY"), out int r) && r > 0 ? r : 120;
    public static int StopSeconds = 10;

    readonly Process p;
    readonly string name;
    IntPtr job;
    int stopped;

    HelperRun(Process proc, string label) { p = proc; name = label; }

    // On the game-run thread, before the emulator starts: the helper's env lines go into `emu`, and "own name"
    // makes `ownExe` (the emulator's real program file) its program. Returns null and the reason when the game
    // must not start.
    public static HelperRun Start(Game g, ProcessStartInfo emu, string ownExe, out string error)
    {
        error = null;
        var h = g.Helper;
        string label = Path.GetFileName(h?.Program ?? "");
        if (string.IsNullOrWhiteSpace(h?.Program) || !File.Exists(h.Program))
        {
            error = T("errors.its_helper_program_is_missing", h?.Program);
            Log.Write($"helper: \"{g.Name}\": missing {h?.Program}");
            return null;
        }
        string dir = !string.IsNullOrWhiteSpace(h.Dir) && Directory.Exists(h.Dir) ? h.Dir : Path.GetDirectoryName(h.Program);
        var si = new ProcessStartInfo(h.Program, h.Args ?? "")
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = dir,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false),
        };
        Process proc;
        try { proc = Process.Start(si); }
        catch (Exception x)
        {
            error = T("errors.its_helper_didn_t_start", x.Message);
            Log.Write($"helper: \"{g.Name}\": {label} didn't start: {x.Message}");
            return null;
        }
        var run = new HelperRun(proc, label);
        run.job = EmbedLogic.Hold(proc);
        Log.Write($"helper: \"{g.Name}\": {label} started (pid {proc.Id})");

        string lastErr = null;
        proc.ErrorDataReceived += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) { lastErr = e.Data.Trim(); Log.Write($"helper: {label} err: {lastErr}"); } };
        proc.BeginErrorReadLine();
        var lines = new BlockingCollection<string>();
        bool ready = false;
        var reader = new Thread(() =>
        {
            try
            {
                string line;
                while ((line = proc.StandardOutput.ReadLine()) != null)
                {
                    if (!Volatile.Read(ref ready)) lines.Add(line);
                    else if (line.Trim().Length > 0) Log.Write($"helper: {label}: {Said(line)}");
                }
            }
            catch { /* the helper ended: its output pipe is closed */ }
            lines.CompleteAdding();
        }) { IsBackground = true, Name = "helper-out" };
        reader.Start();

        var until = DateTime.Now.AddSeconds(ReadySeconds);
        int added = 0;
        while (true)
        {
            int left = (int)(until - DateTime.Now).TotalMilliseconds;
            string line = null;
            bool got = left > 0 && lines.TryTake(out line, left);
            if (!got)
            {
                error = lines.IsCompleted
                    ? (lastErr != null ? T("errors.its_helper_stopped_before_the_game_why", lastErr) : T("errors.its_helper_stopped_before_the_game"))
                    : T("errors.its_helper_wasn_t_ready_after", ReadySeconds);
                Log.Write($"helper: \"{g.Name}\": {label} {(lines.IsCompleted ? "ended before ready" : "not ready in time")}");
                run.Stop();
                return null;
            }
            string t = line.Trim();
            if (t == "ready")
            {
                Volatile.Write(ref ready, true);
                while (lines.TryTake(out string rest)) if (rest.Trim().Length > 0) Log.Write($"helper: {label}: {Said(rest)}");
                Log.Write($"helper: \"{g.Name}\": {label} ready, {added} setting(s) for the emulator");
                return run;
            }
            if (t.StartsWith("env ") && t.IndexOf('=') is int eq && eq > 4)
            {
                string k = t[4..eq].Trim(), v = t[(eq + 1)..];
                if (k.Length > 0 && emu != null) { emu.Environment[k] = v; added++; }
            }
            else if (t == "own name")
            {
                if (emu != null && !string.IsNullOrEmpty(ownExe) && File.Exists(ownExe))
                {
                    emu.FileName = ownExe;
                    Log.Write($"helper: \"{g.Name}\": {label} asked for the emulator's own name: {Path.GetFileName(ownExe)}");
                }
                else Log.Write($"helper: \"{g.Name}\": {label} asked for the emulator's own name; none known");
            }
            else if (t.StartsWith("fail "))
            {
                error = t[5..].Trim();
                Log.Write($"helper: \"{g.Name}\": {label} refused: {error}");
                run.Stop();
                return null;
            }
            else if (t.Length > 0) Log.Write($"helper: {label}: {Said(line)}");
        }
    }

    static string Said(string line) { string t = line.Trim(); return t.StartsWith("say ") ? t[4..] : t; }

    // When the game has ended: closed standard input, then (after StopSeconds) the helper and everything it started.
    public void Stop()
    {
        if (Interlocked.Exchange(ref stopped, 1) == 1) return;
        try { p.StandardInput.Close(); } catch { /* already ended */ }
        bool ended = false;
        try { ended = p.WaitForExit(StopSeconds * 1000); } catch { ended = true; }
        if (!ended) { try { p.Kill(true); } catch { /* already ended */ } Log.Write($"helper: {name} didn't end in {StopSeconds} s, ended"); }
        else Log.Write($"helper: {name} ended");
        EmbedLogic.Release(job); job = IntPtr.Zero;   // kill-on-close: whatever it left running ends here too
        p.Dispose();
    }
}
