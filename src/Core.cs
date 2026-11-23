#nullable disable
// Ionea launcher: entry point, command line, log.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using static Ionea.Loc;

namespace Ionea;

public static class Program
{
    const int IntroHoldMs = 6000;   // the warm-up stream stays open at least this long (the intro is ~2.2 s)
    public static string RestartExe;   // set by Updater.Restart: start this exe again once this one has closed

    [STAThread]
    public static int Main(string[] args)
    {
        Options opt;
        try { opt = Options.Parse(args); }
        catch (Exception e) { MessageBox.Show(e.Message, "Launcher"); return 2; }
        try { Directory.CreateDirectory(opt.DataDir); }
        catch (Exception e) { MessageBox.Show($"Ionea can't create its data folder {opt.DataDir}: {e.Message}", "Ionea"); return 2; }
        Log.File = Path.Combine(opt.DataDir, "launcher.log");

        // One launcher per data folder.
        string key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(opt.DataDir.ToLowerInvariant())))[..12];
        using var mutex = new Mutex(true, "Ionea.Launcher." + key, out bool first);
        if (!first) { Native.ActivateWindowOfOtherInstance(); return 0; }

        Log.Write($"start {AppVersion.Full}, data={opt.DataDir}");
        // Wake the audio output now (silence), before the window is built: wireless headphones need 1-3 s
        // before they play anything, and this way that passes during the intro instead of eating the
        // first sounds. Clips are decoded in the background meanwhile.
        if (!opt.Mute) { SoundMixer.Warm(IntroHoldMs); Sounds.Preload(); }
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        // Crash.cs: every unhandled exception is logged; on the UI thread only a recoverable one is let go, any
        // other closes the launcher (settings kept unless they are what failed, the game stopped below), then says so.
        MainWindow win = null;
        string crashLog = null;
        Crash.Install(app.Dispatcher);
        Crash.Fatal = (e, path) =>
        {
            crashLog = path ?? "launcher.log";
            string st = e.ToString();
            if (win?.Settings != null && !st.Contains("Ionea.Settings.") && !st.Contains("Ionea.Json.")) win.Settings.Save();
            if (win != null) win.CloseForCrash(); else app.Shutdown(3);
        };
        win = new MainWindow(opt);
        int code = app.Run(win);
        if (win.GameMenu.Core is { } running && !running.Ended)
        {
            Log.Write($"exit: closing \"{running.Game.Name}\" first");
            if (!running.StopAndWait(15000)) Log.Write("exit: the game didn't close in 15 s");
        }
        if (crashLog != null)
        {
            Log.Write("exit after a crash");
            // Test runs never wait on a message box.
            string nl = Environment.NewLine;
            if (string.IsNullOrEmpty(opt.Script)) MessageBox.Show(T("errors.ionea_hit_a_problem_and_has", nl, nl, nl, crashLog), "Ionea", MessageBoxButton.OK, MessageBoxImage.Error);
            return 3;   // not started again after an update: a crash shouldn't loop
        }
        Log.Write("exit");
        if (RestartExe != null) StartAgain(args, mutex);
        return code;
    }

    // After an update or Go back (Updater.cs): the exe now in place starts with the same options, once this one has let
    // go of the one-launcher lock. A test script's own run (--script, --shots, first-start prompts) is not repeated;
    // IONEA_TEST_RESTART_SCRIPT (tests only) gives the new one its own script.
    static void StartAgain(string[] args, Mutex mutex)
    {
        try
        {
            var keep = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                string k = args[i].ToLowerInvariant();
                if (k is "--script" or "--shots") { i++; continue; }
                if (k is "--setup" or "--online-prompt" or "--files-prompt" or "--scan-prompt" or "--tutorial") continue;
                keep.Add(args[i]);
            }
            if (Environment.GetEnvironmentVariable("IONEA_TEST_RESTART_SCRIPT") is string ts && ts.Length > 0) { keep.Add("--script"); keep.Add(ts); }
            try { mutex.ReleaseMutex(); } catch { /* no longer owned */ }
            mutex.Dispose();
            var psi = new System.Diagnostics.ProcessStartInfo(RestartExe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(RestartExe) };
            foreach (var a in keep) psi.ArgumentList.Add(a);
            System.Diagnostics.Process.Start(psi);
            Log.Write("restart: started " + RestartExe);
        }
        catch (Exception e) { Log.Write("restart failed: " + e.Message); }
    }
}

public sealed class Options
{
    public string DataDir = DefaultDataDir();

    // Next to the program (a portable folder), unless that folder can't be written (e.g. under Program Files):
    // then the user's own %LOCALAPPDATA%\Ionea\data. A data folder already next to the program always wins.
    public static string DefaultDataDir()
    {
        string beside = Path.Combine(AppContext.BaseDirectory, "data");
        if (Directory.Exists(beside) || CanWrite(AppContext.BaseDirectory)) return beside;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ionea", "data");
    }

    static bool CanWrite(string dir)
    {
        try
        {
            string t = Path.Combine(dir, ".ionea-write-" + Guid.NewGuid().ToString("N")[..8]);
            File.WriteAllText(t, "");
            File.Delete(t);
            return true;
        }
        catch { return false; }
    }
    public string EsdeDir;      // ES-DE's folder: --esde, else Settings.Paths.Esde (Paths.cs); null = none
    public bool NoImport;
    public int Dummies;
    public string DummyTarget;
    public string Script;
    public string ShotsDir;
    public bool? Windowed;
    public int Width = 1600, Height = 900;
    public double? Left, Top;   // test: where the window goes (--pos X,Y, e.g. on the second monitor)
    public bool Setup;          // test: show the first-start controls setup (--setup)
    public bool TutorialPrompt; // test: offer the first-start tutorial (--tutorial)
    public bool FilesPrompt;    // test: show the first-start Required files screen (--files-prompt)
    public bool OnlinePrompt;   // test: show the first-start Going online screen (--online-prompt)
    public string OnlineTestHost;   // test: a local server (host:port) every online item may use (--online-test-host; only with --script)
    public bool Mute;
    public bool GameSound;  // test: a hosted core's sound even with --mute (--game-sound), so the device path runs while the launcher stays quiet
    public bool? Intro;     // --intro on|off; test scripts get no intro unless asked for
    public bool? Outro;     // --outro on|off; test scripts get no outro unless asked for
    public string EmuDir;   // test: the only Emulators folder
    public string InboxDir;     // test: own Inbox (--inbox), else Settings.Paths.Inbox
    public string ScanHome;     // test: a fake user home the scan looks through instead of this PC (--scan-home; Scan.cs)
    public bool ScanPrompt;     // test: show the first-start scan (--scan-prompt)
    public string Language;     // --lang <id>: this run's UI language, e.g. xx-pseudo (Loc.cs); the setting is left alone

    public static Options Parse(string[] a)
    {
        var o = new Options();
        for (int i = 0; i < a.Length; i++)
        {
            string k = a[i].ToLowerInvariant();
            string Next() => i + 1 < a.Length ? a[++i] : throw new ArgumentException("Missing value after " + k);
            switch (k)
            {
                case "--data": o.DataDir = Path.GetFullPath(Next()); break;
                case "--esde": o.EsdeDir = Path.GetFullPath(Next()); break;
                case "--no-import": o.NoImport = true; break;
                case "--dummies": o.Dummies = int.Parse(Next()); break;
                case "--dummy-target": o.DummyTarget = Path.GetFullPath(Next()); break;
                case "--script": o.Script = Next(); break;
                case "--setup": o.Setup = true; break;   // test runs: show the first-start controls setup
                case "--online-prompt": o.OnlinePrompt = true; break;
                case "--tutorial": o.TutorialPrompt = true; break;   // test runs: offer the first-start tutorial
                case "--files-prompt": o.FilesPrompt = true; break;   // test runs: show the first-start Required files screen   // test runs: show the first-start Going online screen
                case "--online-test-host": o.OnlineTestHost = Next(); break;   // test runs: e.g. localhost:47811
                case "--shots": o.ShotsDir = Path.GetFullPath(Next()); break;
                case "--windowed": o.Windowed = true; break;
                case "--fullscreen": o.Windowed = false; break;
                case "--mute": o.Mute = true; break;
                case "--game-sound": o.GameSound = true; break;
                case "--intro": o.Intro = Next().ToLowerInvariant() is "on" or "1" or "true"; break;
                case "--outro": o.Outro = Next().ToLowerInvariant() is "on" or "1" or "true"; break;
                case "--record-sound": SoundMixer.StartRecording(Path.GetFullPath(Next())); break; // demo: UI sound out to raw PCM
                case "--emulators": o.EmuDir = Path.GetFullPath(Next()); break;                     // test: own Emulators folder
                case "--inbox": o.InboxDir = Path.GetFullPath(Next()); break;                      // test: own Inbox
                case "--scan-home": o.ScanHome = Path.GetFullPath(Next()); break;                  // test: a fake home for the scan
                case "--scan-prompt": o.ScanPrompt = true; break;                                  // test: show the first-start scan
                case "--lang": o.Language = Next(); break;                                         // this run's UI language (Loc.cs)
                case "--steam": SteamLib.RootOverride = Path.GetFullPath(Next()); break;          // test: a fake Steam folder
                case "--steam-fake-run": SteamRun.FakeRun = true; break;                           // test: only with --steam
                case "--size":
                    var p = Next().Split('x');
                    o.Width = int.Parse(p[0]); o.Height = int.Parse(p[1]); break;
                case "--pos":
                    var xy = Next().Split(',');
                    o.Left = double.Parse(xy[0], System.Globalization.CultureInfo.InvariantCulture); o.Top = double.Parse(xy[1], System.Globalization.CultureInfo.InvariantCulture); break;
                default: throw new ArgumentException("Unknown option " + a[i]);
            }
        }
        if (o.Script != null && File.Exists(o.Script)) { TestScript.Dir = Path.GetDirectoryName(Path.GetFullPath(o.Script)); o.Script = File.ReadAllText(o.Script); }
        return o;
    }
}

public static class Log
{
    public static string File;
    static readonly object gate = new();
    public static void Write(string s)
    {
        try
        {
            lock (gate)
            {
                if (File == null) return;
                var fi = new FileInfo(File);
                if (fi.Exists && fi.Length > 1_000_000) fi.Delete();
                System.IO.File.AppendAllText(File, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {s}{Environment.NewLine}");
            }
        }
        catch { }   // logging must never throw
    }

    // A failure that is let go on purpose but worth knowing about: one line, no stack.
    public static void Debug(string what, Exception e) => Write($"debug: {what}: {e.GetType().Name}: {e.Message}");
}

public static class Ui
{
    public static void After(double ms, Action a)
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(1, ms)) };
        t.Tick += (s, e) => { t.Stop(); a(); };
        t.Start();
    }
}
