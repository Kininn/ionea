#nullable disable
// Ionea launcher: crash log and unhandled exceptions (UI thread, other threads, tasks).
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Ionea;

// Every unhandled exception goes to crash.log next to launcher.log (type, message, stack, thread, build), with a
// line in launcher.log too. crash.log is rotated at 256 KB into crash.1.log .. crash.3.log.
// On the UI thread only a known-recoverable exception (Recoverable) is let go; anything else, or recoverable ones
// coming too often, ends the launcher cleanly through Fatal (set by Program.Main: tell the user, stop the game, close).
public static class Crash
{
    const long MaxBytes = 256 * 1024;
    const int Keep = 3;
    const int BurstMax = 3;                          // more recoverable UI-thread exceptions than this ...
    static readonly TimeSpan BurstWindow = TimeSpan.FromSeconds(60);   // ... in this long count as fatal
    static readonly object gate = new();
    static readonly Queue<DateTime> recent = new();
    static int fatalStarted;
    public static string Dir;                        // null = the folder of Log.File
    public static Action<Exception, string> Fatal;   // what a fatal UI-thread exception does (exception, crash log path)
    public static bool FatalStarted => fatalStarted != 0;

    public static string FilePath
    {
        get
        {
            string d = Dir ?? (Log.File != null ? Path.GetDirectoryName(Log.File) : null);
            return d == null ? null : Path.Combine(d, "crash.log");
        }
    }

    public static void Install(Dispatcher ui)
    {
        ui.UnhandledException += OnDispatcher;
        AppDomain.CurrentDomain.UnhandledException += OnDomain;
        TaskScheduler.UnobservedTaskException += OnTask;
    }

    // Expected trouble from the outside world (files, network, devices): the action failed but the launcher's own
    // state is fine. Never a programming error (null, bad cast, index, invalid operation) or the runtime giving out.
    public static bool Recoverable(Exception e) => e switch
    {
        null => false,
        OutOfMemoryException or InsufficientExecutionStackException or AccessViolationException => false,
        OperationCanceledException => true,                       // a cancelled download, scan or wait
        IOException or UnauthorizedAccessException => true,       // a file in use, missing, a full disk, no rights
        System.Net.Http.HttpRequestException or TimeoutException => true,
        System.ComponentModel.Win32Exception => true,             // a program that can't be started
        System.Text.Json.JsonException => true,                   // a broken file someone else wrote
        AggregateException a => a.InnerExceptions.Count > 0 && System.Linq.Enumerable.All(a.InnerExceptions, Recoverable),
        _ => false,
    };

    static void OnDispatcher(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var ex = e.Exception;
        if (FatalStarted) { Write(ex, "ui thread, while closing after a crash"); e.Handled = true; return; }   // no loop
        bool ok = Recoverable(ex) && !Burst();
        Write(ex, ok ? "ui thread, recovered" : "ui thread, fatal");
        e.Handled = true;   // either way: a recovered one keeps going, a fatal one closes the launcher below, cleanly
        if (ok) return;
        if (Interlocked.Exchange(ref fatalStarted, 1) != 0) return;
        try { Fatal?.Invoke(ex, FilePath); }
        catch (Exception f) { Write(f, "ui thread, closing after a crash failed"); Environment.Exit(3); }
    }

    static bool Burst()
    {
        lock (gate)
        {
            var now = DateTime.UtcNow;
            recent.Enqueue(now);
            while (recent.Count > 0 && now - recent.Peek() > BurstWindow) recent.Dequeue();
            return recent.Count > BurstMax;
        }
    }

    static void OnDomain(object sender, UnhandledExceptionEventArgs e) =>
        Write(e.ExceptionObject as Exception, e.IsTerminating ? "other thread, the launcher ends" : "other thread",
            e.ExceptionObject is Exception ? null : e.ExceptionObject?.ToString());

    static void OnTask(object sender, UnobservedTaskExceptionEventArgs e)
    {
        Write(e.Exception, "task nobody waited for");
        e.SetObserved();
    }

    // Writes one entry (flushed: the file is closed again before this returns). Never throws.
    public static void Write(Exception e, string where, string other = null)
    {
        try
        {
            var t = Thread.CurrentThread;
            string thread = $"{(string.IsNullOrEmpty(t.Name) ? "unnamed" : t.Name)} (#{t.ManagedThreadId}{(t.IsBackground ? ", background" : "")})";
            Log.Write($"crash: {where}: {e?.GetType().FullName ?? "?"}: {e?.Message ?? other} (details in crash.log)");
            string file = FilePath;
            if (file == null) return;
            var sb = new StringBuilder();
            sb.AppendLine($"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {where}");
            sb.AppendLine($"build: {AppVersion.Full}");
            sb.AppendLine($"thread: {thread}");
            sb.AppendLine($"type: {e?.GetType().FullName ?? "(not an exception)"}");
            sb.AppendLine($"message: {e?.Message ?? other}");
            sb.AppendLine(e?.ToString() ?? "");
            sb.AppendLine();
            lock (gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                Rotate(file);
                File.AppendAllText(file, sb.ToString());
            }
        }
        catch { }   // the crash log must never be the next crash
    }

    static void Rotate(string file)
    {
        var fi = new FileInfo(file);
        if (!fi.Exists || fi.Length < MaxBytes) return;
        string Old(int n) => Path.Combine(Path.GetDirectoryName(file), $"crash.{n}.log");
        File.Delete(Old(Keep));
        for (int n = Keep - 1; n >= 1; n--) if (File.Exists(Old(n))) File.Move(Old(n), Old(n + 1));
        File.Move(file, Old(1));
    }
}
