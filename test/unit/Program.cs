// Logic tests for the launcher: names, playlists, downloads, moving saves, data files, links, controller reports.
// Everything is written under %TEMP%\Ionea-tests\unit and removed again.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Ionea.Tests;

public sealed class Thing { public string Name { get; set; } public int N { get; set; } }

public static class Program
{
    static int fails, passes;
    static string root;

    static void Eq<T>(T want, T got, string what)
    {
        if (EqualityComparer<T>.Default.Equals(want, got)) { passes++; return; }
        fails++;
        Console.WriteLine($"FAIL {what}: want [{want}] got [{got}]");
    }
    static void True(bool b, string what) => Eq(true, b, what);

    [STAThread]
    public static int Main(string[] only)   // names of tests to run (all when none): dotnet run -c Release -- players
    {
        root = Path.Combine(Path.GetTempPath(), "Ionea-tests", "unit");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(root);
        Log.File = Path.Combine(root, "test.log");
        Tweaks.DataDir = Path.Combine(root, "data");
        Directory.CreateDirectory(Tweaks.DataDir);
        foreach (var (name, t) in new (string, Action)[]
        {
            ("names", Names), ("playlists", Playlists), ("download check", DownloadCheck), ("move", Move),
            ("data files", DataFiles), ("links", Links), ("pad reports", PadReports), ("game sound", GameSound),
            ("players", Players), ("display sync", DisplaySync), ("gl readback", GlReadback), ("vulkan frames", VulkanFrames),
            ("lru cache", Lru), ("group default", GroupDefault), ("tile range", TileRange), ("crash log", CrashLog),
            ("fast forward", FastForwardLogic), ("rewind", RewindRing), ("run ahead", RunAheadLogic), ("picture filter", PictureFilterLogic),
            ("language", Language), ("pause way", PauseWayLogic), ("tool path", ToolPathLogic), ("streaming mode", StreamingModeLogic),
        })
        {
            if (only.Length > 0 && !only.Contains(name)) continue;
            try { t(); }
            catch (Exception e) { fails++; Console.WriteLine($"FAIL {name}: {e}"); }
        }
        try { Directory.Delete(root, true); } catch { }
        Console.WriteLine($"{passes} passed, {fails} failed");
        return fails;
    }

    // The game sound queue (CoreSound.cs), with simulated clocks: a 60 fps core at 32768 Hz into a 48000 Hz
    // device read every 10 ms. speed: how much faster the game's clock runs than the device's.
    static unsafe void SoundRun(SoundQueue q, double seconds, double speed, double quietFrom, double quietTo, Action<float[], int, double> heard, double lateMs = 0)
    {
        const double inR = 32768;
        var frame = new short[700 * 2];
        var outBuf = new float[480 * 2];
        long made = 0;
        double nextFrame = 0, nextRead = 0;
        var rnd = new Random(1);
        double late = 0;
        while (nextRead < seconds)
        {
            if (nextFrame + late <= nextRead)
            {
                long want = (long)((nextFrame * speed + 1 / 60.0) * inR);
                int n = (int)(want - made);
                for (int i = 0; i < n; i++)
                {
                    short v = (short)(16000 * Math.Sin(2 * Math.PI * 1000 * (made + i) / inR));
                    frame[i * 2] = v; frame[i * 2 + 1] = (short)-v;
                }
                if (nextFrame < quietFrom || nextFrame >= quietTo) fixed (short* p = frame) q.Write((IntPtr)p, n, 1f);
                made = want;
                nextFrame += 1 / 60.0;
                late = rnd.NextDouble() * lateMs / 1000;   // the next frame is up to lateMs late
            }
            else
            {
                fixed (float* o = outBuf) q.Read(o, 480, 2);
                heard?.Invoke(outBuf, 480, nextRead);
                nextRead += 0.010;
            }
        }
    }

    static unsafe void GameSound()
    {
        // A tone goes through at the same pitch and level, with no gaps.
        var q = new SoundQueue(32768, 48000, 1000 / 60.0, 10);
        double sum = 0; long n = 0; int cross = 0; float last = 0; bool mirrored = true;
        SoundRun(q, 5, 1, 99, 99, (b, f, t) =>
        {
            if (t < 2) return;
            for (int i = 0; i < f; i++)
            {
                float l = b[i * 2];
                sum += l * l; n++;
                if ((last < 0) != (l < 0)) cross++;
                last = l;
                if (Math.Abs(l + b[i * 2 + 1]) > 1e-4) mirrored = false;
            }
        });
        double rms = Math.Sqrt(sum / n), want = 16000 / 32768.0 / Math.Sqrt(2);
        True(Math.Abs(rms / want - 1) < 0.02, $"sound: the tone keeps its level ({rms:0.0000} vs {want:0.0000})");
        double hz = cross / 2.0 / (n / 48000.0);
        True(Math.Abs(hz - 1000) < 10, $"sound: the tone keeps its pitch ({hz:0.0} Hz)");
        True(mirrored, "sound: left and right stay apart");
        Eq(0, q.Gaps, "sound: no gaps");
        True(q.SmoothMs < 26 + SoundQueue.StartMs, $"sound: little waits in the queue ({q.SmoothMs:0.0} ms, the start's extra still on)");
        Eq(0L, q.Dropped, "sound: nothing dropped");

        // Clocks a little apart either way: the queue holds near its target, nothing is lost or dropped.
        foreach (double speed in new[] { 1.002, 0.998 })
        {
            q = new SoundQueue(32768, 48000, 1000 / 60.0, 10);
            SoundRun(q, 120, speed, 99, 99, null);
            Eq(0, q.Gaps, $"sound: no gaps at speed {speed}");
            Eq(0L, q.Dropped, $"sound: nothing dropped at speed {speed}");
            True(Math.Abs(q.SmoothMs - q.TargetMs) < 2, $"sound: the queue holds at speed {speed} ({q.SmoothMs:0.0} ms for {q.TargetMs:0} ms)");
            True(Math.Abs(q.Nudge) <= SoundQueue.MaxNudge + 1e-9 && Math.Abs(q.Nudge) > 0.001, $"sound: the speed follows ({q.Nudge:P2})");
        }

        // The start's extra: on at first, gone after 30 s without trouble, and the queue settles at its target.
        q = new SoundQueue(32768, 48000, 1000 / 60.0, 10);
        True(Math.Abs(q.TargetMs - q.BaseTargetMs - SoundQueue.StartMs) < 1e-9, $"sound: a little more kept at the start ({q.TargetMs - q.BaseTargetMs:0.0} ms)");
        SoundRun(q, 45, 1, 99, 99, null);
        Eq(0, q.Gaps, "sound: no gaps over the start");
        True(Math.Abs(q.TargetMs - q.BaseTargetMs) < 1e-9, $"sound: back to the target after the start ({q.TargetMs - q.BaseTargetMs:0.0} ms)");
        True(Math.Abs(q.SmoothMs - q.TargetMs) < 2, $"sound: the queue follows it down ({q.SmoothMs:0.0} ms for {q.TargetMs:0} ms)");

        // Frames up to 3 ms late (the clock's sleeps, a core's slower frames): still no gaps.
        q = new SoundQueue(32768, 48000, 1000 / 60.0, 10);
        SoundRun(q, 60, 1, 99, 99, null, 3);
        Eq(0, q.Gaps, "sound: no gaps with late frames");

        // A tenth of a second with no sound from the game: one gap, a slightly longer queue after it.
        q = new SoundQueue(32768, 48000, 1000 / 60.0, 10);
        SoundRun(q, 6, 1, 3.0, 3.1, null);
        Eq(1, q.Gaps, "sound: a dry spell is one gap");
        True(q.TargetMs >= q.BaseTargetMs + SoundQueue.StepMs && q.TargetMs <= q.BaseTargetMs + SoundQueue.StartMs + 14, $"sound: the queue grows after a gap, by what was missing ({q.TargetMs - q.BaseTargetMs:0.0} ms)");

        // Far too much at once (after a stall): the oldest goes, the queue is back at its target.
        q = new SoundQueue(32768, 48000, 1000 / 60.0, 10);
        var lot = new short[16384 * 2];
        var o2 = new float[480 * 2];
        fixed (short* p = lot) q.Write((IntPtr)p, lot.Length / 2, 1f);
        fixed (float* o = o2) q.Read(o, 480, 2);
        True(q.Dropped > 0 && q.FillMs <= q.TargetMs + 1, $"sound: too much is dropped ({q.FillMs:0.0} ms left)");

        // Nothing from the game yet: silence.
        q = new SoundQueue(44100, 48000, 1000 / 60.0, 10);
        var o3 = new float[480 * 2];
        o3[0] = 1;
        bool any;
        fixed (float* o = o3) any = q.Read(o, 480, 2);
        True(!any && o3[0] == 0, "sound: silence before the game sends any");
    }

    // The home row's cover cache (Lru.cs): past its budget the least recently used goes, and a read counts as a use.
    static void Lru()
    {
        string Order<V>(LruCache<string, V> c) => string.Join(",", c.Keys());
        var c = new LruCache<string, int>(100, 3);
        c.Set("a", 1); c.Set("b", 2); c.Set("c", 3);
        Eq("c,b,a", Order(c), "newest first");
        c.Set("d", 4);
        Eq("d,c,b", Order(c), "over the count: the oldest goes, the rest stay");
        True(c.TryGet("b", out int b) && b == 2, "b still there");
        Eq("b,d,c", Order(c), "a read moves it to the front");
        c.Set("e", 5);
        Eq("e,b,d", Order(c), "so c goes next, not b");
        True(!c.TryGet("c", out _), "c gone");
        True(c.Peek("d", out _) && Order(c) == "e,b,d", "a peek does not move it");
        c.Set("b", 20);
        Eq("b,e,d", Order(c), "set again: replaced and in front");
        Eq(3, c.Count, "replacing does not add");

        var w = new LruCache<string, int>(10, 100, v => v);   // by weight
        w.Set("x", 4); w.Set("y", 4);
        Eq(8L, w.Weight, "weights add up");
        w.TryGet("x", out _);
        w.Set("z", 4);
        Eq("z,x", Order(w), "over the weight: the least recently used goes");
        Eq(8L, w.Weight, "its weight with it");
        w.Set("big", 50);
        Eq("big", Order(w), "one entry over the budget alone stays (and pushes out the rest)");
        True(w.Remove("big") && w.Count == 0 && w.Weight == 0, "remove");
    }

    // The crash log (Crash.cs): a recoverable UI-thread exception is logged and the dispatcher keeps going, any
    // other is logged and handed to Fatal, a faulted task nobody waited for is logged and observed.
    static void CrashLog()
    {
        Crash.Dir = Path.Combine(root, "crash");
        string file = Crash.FilePath;
        string Read() => File.Exists(file) ? File.ReadAllText(file) : "";
        var d = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        Exception fatal = null;
        Crash.Fatal = (e, path) => fatal = e;
        Crash.Install(d);
        void Run(Action a)
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            d.BeginInvoke(a);
            d.BeginInvoke(() => frame.Continue = false, System.Windows.Threading.DispatcherPriority.Background);
            System.Windows.Threading.Dispatcher.PushFrame(frame);
        }

        Run(() => throw new IOException("test: file in use"));
        bool after = false;
        Run(() => after = true);
        True(Read().Contains("ui thread, recovered") && Read().Contains("test: file in use"), "recoverable logged");
        True(after && fatal == null && !Crash.FatalStarted, "recoverable keeps running");

        Run(() => throw new InvalidOperationException("test: broken state"));
        True(Read().Contains("ui thread, fatal") && Read().Contains("System.InvalidOperationException"), "fatal logged");
        True(fatal is InvalidOperationException && Crash.FatalStarted, "fatal handed on");
        True(Read().Contains("build: ") && Read().Contains("thread: "), "build and thread written");

        static void Forget() => System.Threading.Tasks.Task.Run(() => throw new FormatException("test: task failed"));
        Forget();
        for (int i = 0; i < 50 && !Read().Contains("test: task failed"); i++)
        {
            System.Threading.Thread.Sleep(20);
            GC.Collect(); GC.WaitForPendingFinalizers();
        }
        True(Read().Contains("task nobody waited for") && Read().Contains("test: task failed"), "unobserved task logged");
        Crash.Dir = null; Crash.Fatal = null;
    }

    // Which home tiles are built (Home.cs): the columns on screen plus a margin, whole columns, and in an
    // open folder the page shown and its neighbours.
    static void TileRange()
    {
        string R((int a, int b) r) => r.b < r.a ? "none" : r.a + ".." + r.b;
        // large: 420 tiles, 44 apart, one row; the row starts 96 in on a 1920 screen
        Eq("0..3", R(HomeScreen.RowRange(5001, 1, 464, 420, 0, 96, 1920, 0)), "large, at the start: four on screen");
        Eq("0..5", R(HomeScreen.RowRange(5001, 1, 464, 420, 0, 96, 1920, 2)), "and two more past the right edge");
        // medium: 270 tiles, 32 apart, two rows
        Eq("6..19", R(HomeScreen.RowRange(5001, 2, 302, 270, 1000, 96, 1920, 0)), "medium, scrolled: columns 3 to 9, both rows");
        Eq("2..23", R(HomeScreen.RowRange(5001, 2, 302, 270, 1000, 96, 1920, 2)), "with two columns each side");
        Eq("4..17", R(HomeScreen.RowRange(5001, 2, 302, 270, 668, 96, 1920, 0)), "a column whose edge just meets the screen's left is off it");
        Eq("6..14", R(HomeScreen.RowRange(15, 2, 302, 270, 1000, 96, 1920, 0)), "the last column, half full");
        Eq("4996..5000", R(HomeScreen.RowRange(5001, 4, 158, 136, 1250 * 158, 96, 1920, 0)), "small, scrolled to the end: the last two columns");
        Eq("none", R(HomeScreen.RowRange(0, 1, 464, 420, 0, 96, 1920, 2)), "nothing to show");
        Eq("0..17", R(HomeScreen.PageRange(40, 0, 9, 1)), "folder, first page: it and the next");
        Eq("9..35", R(HomeScreen.PageRange(40, 2, 9, 1)), "a middle page and both neighbours");
        Eq("27..39", R(HomeScreen.PageRange(40, 4, 9, 1)), "the last page, part full");
    }

    // Keeping time with the display (DisplaySync.cs): which way for a core's rate on a display's refresh rate,
    // and how far the sound's speed may go.
    // Rewind (Rewind.cs): steps back give the states pushed, newest first; the memory stays within its size, the
    // oldest going first as it wraps; the oldest state stays put; a new size starts over.
    static void RewindRing()
    {
        var rnd = new Random(5);
        byte[] Next(byte[] s, int changes)
        {
            var n = (byte[])s.Clone();
            for (int i = 0; i < changes; i++) n[rnd.Next(n.Length)] = (byte)rnd.Next(256);
            return n;
        }
        // Step back: exactly the states pushed, newest first, then stays at the oldest.
        var b = new RewindBuffer(1 << 20);
        var all = new List<byte[]> { new byte[5000] };
        rnd.NextBytes(all[0]);
        b.Push(all[0]);
        for (int i = 0; i < 50; i++) { all.Add(Next(all[^1], 20)); b.Push(all[^1]); }
        Eq(50, b.Count, "50 steps back after 51 states");
        True(b.Current.AsSpan().SequenceEqual(all[^1]), "the newest state is the last pushed");
        for (int i = all.Count - 2; i >= 0; i--) True(b.StepBack() && b.Current.AsSpan().SequenceEqual(all[i]), $"step back to state {i}");
        True(!b.StepBack() && b.Current.AsSpan().SequenceEqual(all[0]), "at the oldest: no step, the state stays");
        Eq(0L, b.Used, "nothing kept after stepping all the way back");
        // Unchanged states pack to almost nothing; a fully changed one stays about its size.
        Eq(4, RewindBuffer.Pack(new byte[100000], new byte[200064]), "an unchanged state packs to 4 bytes");
        var noise = new byte[10000]; rnd.NextBytes(noise);
        True(RewindBuffer.Pack(noise, new byte[20064]) < 10100, "a fully changed state packs to about its size");
        // Capacity and wrap: a small buffer, many states. It never uses more than its size, keeps the newest, and
        // still gives exact states back after wrapping many times around.
        var small = new RewindBuffer(64 * 1024);
        var kept = new List<byte[]> { new byte[20000] };
        small.Push(kept[0]);
        long most = 0;
        for (int i = 0; i < 2000; i++)
        {
            kept.Add(Next(kept[^1], 1 + rnd.Next(300)));
            small.Push(kept[^1]);
            most = Math.Max(most, small.Used);
            // now and then step back a few and go on from there, as a player does
            if (i % 97 == 0) for (int k = 0; k < 5 && small.StepBack(); k++) kept.RemoveAt(kept.Count - 1);
        }
        True(most <= small.Capacity, $"never more than the capacity ({most} of {small.Capacity})");
        True(small.Count > 10 && small.Count < kept.Count - 1, $"the oldest went, the newest stayed ({small.Count} of {kept.Count - 1})");
        bool same = true;
        for (int i = kept.Count - 2, n = small.Count; n > 0; i--, n--) same &= small.StepBack() && small.Current.AsSpan().SequenceEqual(kept[i]);
        True(same, "after wrapping: every step back is the state pushed");
        // A state of another size starts over.
        small.Push(new byte[100]);
        Eq(0, small.Count, "a new state size: no steps back");
        Eq(1, RewindBuffer.Interval(400 << 10), "a small state: every frame");
        Eq(4, RewindBuffer.Interval(16 << 20), "a big state: every 4 frames");
    }

    // Run-ahead (RunAhead.cs): which games and frames run ahead, and when it gives up for being too slow.
    static void RunAheadLogic()
    {
        Eq(0, RunAhead.Clean(7), "a frame count not offered: off");
        Eq(3, RunAhead.Clean(3), "3 frames stays");
        Eq("Off", RunAhead.Name(0), "off's name");
        Eq("1 frame", RunAhead.Name(1), "one frame's name");
        Eq(null, RunAhead.Unfit(false, 0), "a 2D core with full states can");
        True(RunAhead.Unfit(true, 0) != null, "a game drawn with the GPU can't");
        True(RunAhead.Unfit(false, 1) != null, "a core whose states are incomplete can't");
        Eq(null, RunAhead.Unfit(false, 2 | 16), "other quirks (must initialise, one session) are fine");
        True(RunAhead.Now(2, null, 0), "on, fit, playing: runs ahead");
        True(!RunAhead.Now(0, null, 0), "set off: doesn't");
        True(!RunAhead.Now(2, "gave up", 0), "given up: doesn't");
        True(!RunAhead.Now(2, null, 1), "fast-forward or rewind: doesn't");
        // The budget: a few slow frames are let go, too many of the last 60 turn it off.
        var b = new AheadBudget();
        bool off = false;
        for (int i = 0; i < 600; i++) off |= b.Note(i % 10 == 0 ? 20 : 5, 16.7);   // one in ten over
        True(!off, "a slow frame now and then: stays on");
        b.Clear();
        int at = -1;
        for (int i = 0; i < 100 && at < 0; i++) if (b.Note(13, 16.7)) at = i;
        Eq(AheadBudget.Over - 1, at, "every frame over 3/4 of the frame: off at the Over-th");
        b.Clear();
        True(!b.Note(13, 16.7), "cleared (after a pause): counts from nothing");
    }

    // Picture filters (PictureFilter.cs): which one a game gets, and the pattern laid over it.
    static void PictureFilterLogic()
    {
        Eq("sharp", PictureFilters.Pick(null, null, null, false), "nothing chosen, a 2D game: sharp");
        Eq("smooth", PictureFilters.Pick(null, null, "auto", true), "automatic, a game drawn with the GPU: smooth");
        Eq("crt", PictureFilters.Pick(null, null, "crt", false), "every game's choice");
        Eq("lcd", PictureFilters.Pick(null, "lcd", "crt", false), "the system's beats every game's");
        Eq("smooth", PictureFilters.Pick("smooth", "lcd", "crt", false), "the game's own beats its system's");
        Eq("sharp", PictureFilters.Pick("auto", "lcd", "crt", false), "the game set to automatic: automatic, not its system's");
        Eq("lcd", PictureFilters.Pick("bogus", "lcd", null, false), "a filter not known: passed over");
        True(PictureFilters.Smooth("crt") && !PictureFilters.Smooth("lcd") && !PictureFilters.Smooth("sharp"), "CRT scales smoothly, LCD and sharp don't");
        Eq(0, PictureFilters.Pattern("smooth"), "smooth: nothing laid over");
        True(PictureFilters.Tile(1, 1.5, 1.5, out _, out _) == null, "CRT on pixels too small: left out");
        True(PictureFilters.Tile(2, 2, 2, out _, out _) == null, "LCD grid under 3 screen pixels a game pixel: left out");
        var lcd = PictureFilters.Tile(2, 4, 4, out int tw, out int th);
        Eq(16, tw * th, "a 4x4 tile for 4x4 game pixels");
        Eq(0u, lcd[0] >> 24, "the pixel's inside is clear");
        True((lcd[3] >> 24) > 0 && (lcd[15] >> 24) > 0, "its last column and row are dark");
        var crt = PictureFilters.Tile(1, 5, 5, out tw, out th);
        True((crt[2] >> 24) > (crt[2 * 5 + 2] >> 24), "CRT: a line's edge darker than its middle");
        True((crt[2 * 5] >> 24) > (crt[2 * 5 + 2] >> 24), "CRT: a slight gap between pixels");
    }

    // Fast-forward (FastForward.cs): the pace at each speed, the pictures shown, and hold / toggle keys.

    static void FastForwardLogic()
    {
        Eq(1000.0 / 240, FastForward.FrameMs(60, 4), "4x a 60 fps game: a frame every 4.2 ms");
        Eq(0.0, FastForward.FrameMs(60, 0), "no limit: no waiting");
        Eq(4, FastForward.Clean(5), "a speed not offered: the default");
        Eq(0, FastForward.Clean(0), "no limit stays");
        Eq("No limit", FastForward.SpeedName(0), "no limit's name");
        // 2x of 60 fps: a frame every 8.33 ms, about every other one shown, never two within 16 ms.
        int shown = 0; double last = double.MinValue, gap = double.MaxValue, prev = double.NaN;
        for (int i = 0; i < 240; i++)
        {
            double t = i * 1000.0 / 120;
            if (!FastForward.Show(t, last)) continue;
            if (!double.IsNaN(prev)) gap = Math.Min(gap, t - prev);
            prev = t; last = t; shown++;
        }
        Eq(120, shown, "2x: every other frame shown (60 a second)");
        True(gap >= FastForward.ShowMs - 0.5, "2x: no two pictures closer than a 60 Hz refresh");
        // No limit, 1000 frames in a second: still about 60 shown.
        shown = 0; last = double.MinValue;
        for (int i = 0; i < 1000; i++) if (FastForward.Show(i, last)) { last = i; shown++; }
        True(shown >= 58 && shown <= 63, $"1000 fps: about 60 pictures ({shown})");
        True(FastForward.Show(0, double.MinValue), "the first frame fast is shown");
        // Held: on while down. Toggle: each new press turns it over, holding changes nothing.
        True(FastForward.Next(false, true, false, false) && !FastForward.Next(true, false, true, false), "held");
        True(FastForward.Next(false, true, false, true), "toggle: a press turns it on");
        True(FastForward.Next(true, true, true, true), "toggle: still held, still on");
        True(FastForward.Next(true, false, true, true), "toggle: let go, still on");
        True(!FastForward.Next(true, true, false, true), "toggle: the next press turns it off");
    }

    static void DisplaySync()
    {

        void Is(double fps, double hz, Pace mode, int n, string what)
        {
            var p = FrameTiming.Decide(fps, hz);
            Eq(mode, p.Mode, what + " (way)");
            Eq(n, p.Refreshes, what + " (refreshes a frame)");
            if (mode == Pace.Display) True(Math.Abs(p.Rate * n - hz) < 1e-9 && Math.Abs(p.Skew - p.Rate / fps) < 1e-12, what + " (rate and skew)");
            else Eq(1.0, p.Skew, what + " (own speed)");
        }
        Is(59.7275, 60, Pace.Display, 1, "GBA on 60 Hz");
        Is(60.0988, 59.94, Pace.Display, 1, "NES on 59.94 Hz");
        Is(59.7275, 120, Pace.Display, 2, "GBA on 120 Hz");
        Is(30, 60, Pace.Display, 2, "30 fps on 60 Hz");
        Is(30, 120, Pace.Display, 4, "30 fps on 120 Hz");
        Is(50, 100, Pace.Display, 2, "50 fps on 100 Hz");
        Is(57.5, 60, Pace.Display, 1, "4.3 % off on 60 Hz");
        Is(50, 60, Pace.Clock, 0, "50 fps on 60 Hz");
        Is(30, 144, Pace.Clock, 0, "30 fps on 144 Hz");
        Is(59.7275, 144, Pace.Clock, 0, "GBA on 144 Hz");
        Is(59.7275, 164, Pace.Clock, 0, "GBA on 164 Hz");
        Is(60, 75, Pace.Clock, 0, "60 fps on 75 Hz");
        Is(120, 60, Pace.Clock, 0, "120 fps on 60 Hz");
        Is(60, 0, Pace.Clock, 0, "display rate not known");
        Is(double.NaN, 60, Pace.Clock, 0, "core rate not known");
        Eq(60.0, FrameTiming.Decide(0, 60).Rate, "no core rate: 60 by the clock");

        // The sound's speed: the skew (at most 5 %) times the queue's own correction (at most 0.5 %).
        True(Math.Abs(FrameTiming.SoundSpeed(1.0045, 0) - 1.0045) < 1e-12, "sound speed: the skew");
        True(Math.Abs(FrameTiming.SoundSpeed(1, 0.003) - 1.003) < 1e-12, "sound speed: a small correction");
        True(Math.Abs(FrameTiming.SoundSpeed(1, 0.02) - 1.005) < 1e-12, "sound speed: the correction stops at 0.5 %");
        True(Math.Abs(FrameTiming.SoundSpeed(1, -0.02) - 0.995) < 1e-12, "sound speed: and at -0.5 %");
        True(Math.Abs(FrameTiming.SoundSpeed(1.2, 0) - 1.05) < 1e-12, "sound speed: the skew stops at 5 %");
        True(Math.Abs(FrameTiming.SoundSpeed(0.5, -1) - 0.95 * 0.995) < 1e-12, "sound speed: both at their limits");
        Eq(1.0, FrameTiming.SoundSpeed(double.NaN, double.NaN), "sound speed: nothing known");
        var q = new SoundQueue(32768, 48000, 1000 / 60.0, 10);
        q.SetSkew(1.3);
        True(Math.Abs(q.Skew - 1.05) < 1e-12, "sound queue: the skew is held to 5 %");
    }

    // The asynchronous OpenGL readback (GlReadback.cs): which buffer is shown, and the copy that turns a
    // bottom-up picture the right way up.
    static unsafe void GlReadback()
    {
        var ring = new ReadbackRing(3);
        int[] w = new int[3];
        string Waiting() => string.Join(",", w.Take(ring.Waiting(w)));
        Eq(0, ring.Begin(), "first frame: slot 0");
        Eq("", Waiting(), "first frame: nothing to show yet");
        Eq(1, ring.Begin(), "second frame: slot 1");
        Eq("0", Waiting(), "second frame: the first one");
        ring.Shown(0);
        Eq(2, ring.Begin(), "third frame: slot 2");
        Eq("1", Waiting(), "third frame: the second one");
        Eq(0, ring.Begin(), "fourth frame: back to slot 0");
        Eq("2,1", Waiting(), "GPU behind: newest first, then the older one");
        ring.Shown(1);
        Eq("2", Waiting(), "the older one shown: the newer one still waits");
        Eq(1, ring.Begin(), "fifth frame");
        Eq("0,2", Waiting(), "frames 4 and 3");
        ring.Shown(0);
        Eq("", Waiting(), "frame 4 shown: frame 3 is never shown after it");
        ring.Begin(); ring.Clear(); ring.Begin();
        Eq("", Waiting(), "buffers remade: nothing old is shown");

        byte[] src = { 1, 2, 3, 4, 5, 6 }, dst = new byte[6];
        fixed (byte* p = src)
        {
            var (start, pitch) = FrameRows.TopDown((IntPtr)p, 2, 3, true);
            FrameRows.Copy(start, pitch, dst, 2, 3);
            Eq("5,6,3,4,1,2", string.Join(",", dst), "bottom-up rows turned the right way up");
            (start, pitch) = FrameRows.TopDown((IntPtr)p, 2, 3, false);
            FrameRows.Copy(start, pitch, dst, 2, 3);
            Eq("1,2,3,4,5,6", string.Join(",", dst), "top-down rows as they are");
        }
    }

    static void VulkanFrames()
    {
        var f = new VkFrameRing(3);
        int[] c = new int[3];
        string Cand() => string.Join(",", c.Take(f.Candidates(c)));
        Eq(7u, f.Mask, "three sync indexes: mask 0b111");
        Eq(0, f.Begin(out bool wait), "frame 1: index 0"); Eq(false, wait, "frame 1: nothing to wait for");
        f.Submitted(true);
        Eq("", Cand(), "frame 1: its own copy is still running");
        Eq(1, f.Begin(out wait), "frame 2: index 1");
        Eq("0", Cand(), "frame 2: frame 1's copy");
        True(f.Pending(0), "frame 1's fence not waited for yet");
        f.Waited(0); f.Shown(0); f.Submitted(true);
        Eq(2, f.Begin(out wait), "frame 3: index 2");
        f.Submitted(false);   // only the core's command buffers, no picture
        Eq("1", Cand(), "frame 3: frame 2's copy");
        Eq(0, f.Begin(out wait), "frame 4: back to index 0"); Eq(false, wait, "index 0 was waited for already");
        f.Submitted(true);
        Eq("1", Cand(), "frame 4: frame 3 had no picture, frame 2's still waits");
        Eq(1, f.Begin(out wait), "frame 5: index 1"); Eq(true, wait, "index 1's work must finish before the core reuses it");
        f.Waited(1);
        Eq("0", Cand(), "frame 5: frame 4's copy; frame 2's slot is reused");
        f.Submitted(true); f.WaitedAll();
        True(!f.Pending(0) && !f.Pending(1) && !f.Pending(2), "device idle: nothing pending");
        f.Clear();
        Eq("", Cand(), "buffers remade: nothing old is shown");

        Eq(1, VkPixels.Kind(44), "B8G8R8A8_UNORM"); Eq(2, VkPixels.Kind(37), "R8G8B8A8_UNORM"); Eq(3, VkPixels.Kind(58), "A2R10G10B10");
        Eq(4, VkPixels.Kind(64), "A2B10G10R10"); Eq(0, VkPixels.Kind(97), "a float format: not shown");
        var rgba = new byte[40 * 4]; var bgra = new byte[40 * 4];   // more than one vector, and a tail
        for (int i = 0; i < 40; i++) { rgba[i * 4] = (byte)i; rgba[i * 4 + 1] = 0x20; rgba[i * 4 + 2] = 0x30; rgba[i * 4 + 3] = 0x40; }
        VkPixels.ToBgra(2, rgba, bgra);
        True(Enumerable.Range(0, 40).All(i => bgra[i * 4] == 0x30 && bgra[i * 4 + 1] == 0x20 && bgra[i * 4 + 2] == i && bgra[i * 4 + 3] == 0x40), "RGBA: red and blue swapped");
        uint px = (3u << 30) | (0x3FFu << 20) | (0x200u << 10) | 0x004u;
        var ten = BitConverter.GetBytes(px); var out4 = new byte[4];
        VkPixels.ToBgra(3, ten, out4);
        Eq("1,128,255,255", string.Join(",", out4), "A2R10G10B10: top 8 bits, blue first");
        VkPixels.ToBgra(4, ten, out4);
        Eq("255,128,1,255", string.Join(",", out4), "A2B10G10R10: red in the low bits");
        VkPixels.ToBgra(1, rgba, bgra);
        True(rgba.AsSpan().SequenceEqual(bgra), "BGRA: copied as it is");
    }

    static void Names()
    {
        Eq(2, RomLogic.Disc("Final Fantasy VII (USA) (Disc 2)"), "disc in brackets");
        Eq(3, RomLogic.Disc("Game (Disk 3 of 4)"), "disk n of m");
        Eq(1, RomLogic.Disc("Game [CD1]"), "cd in square brackets");
        Eq((int?)null, RomLogic.Disc("Discworld (USA)"), "a word with disc in it");
        Eq((int?)null, RomLogic.Disc("Game (USA)"), "no disc");
        Eq("Final Fantasy VII (Disc 2)", RomLogic.NameFromFile(@"C:\roms\Final Fantasy VII (USA) (Disc 2).chd"), "name keeps the disc");
        Eq("Pokemon - White Version 2", RomLogic.NameFromFile(@"C:\roms\Pokemon - White Version 2 (USA, Europe) (NDSi Enhanced) [!].nds"), "name drops tags");
        Eq("Final Fantasy VII", RomLogic.WithoutDisc("Final Fantasy VII (Disc 2)"), "without disc");
        Eq("Game", RomLogic.WithoutDisc("Game [CD 1]"), "without cd");
    }

    static void Playlists()
    {
        string d = Path.Combine(root, "ff7");
        Directory.CreateDirectory(d);
        foreach (var f in new[] { "FF7 (Disc 1).cue", "FF7 (Disc 1).bin", "FF7 (Disc 2).cue", "FF7 (Disc 2).bin", "Other.chd" }) File.WriteAllText(Path.Combine(d, f), "");
        File.WriteAllText(Path.Combine(d, "FF7 (Disc 1).cue"), "FILE \"FF7 (Disc 1).bin\" BINARY\n  TRACK 01 MODE2/2352\n");
        File.WriteAllText(Path.Combine(d, "FF7 (Disc 2).cue"), "FILE \"FF7 (Disc 2).bin\" BINARY\n");
        File.WriteAllText(Path.Combine(d, "FF7.m3u"), "# discs\nFF7 (Disc 1).cue\nFF7 (Disc 2).cue\nmissing.cue\n");
        var refs = Scan.Refs(Path.Combine(d, "FF7.m3u"));
        Eq(2, refs.Count, "m3u lists the discs that exist");
        Eq("FF7 (Disc 1).cue", Path.GetFileName(refs[0]), "m3u keeps the order");
        var named = Scan.Named(Directory.GetFiles(d));
        True(named.Contains(Path.Combine(d, "FF7 (Disc 1).cue")), "named: disc 1 cue");
        True(named.Contains(Path.Combine(d, "FF7 (Disc 2).bin")), "named: disc 2 bin, through its cue");
        True(!named.Contains(Path.Combine(d, "FF7.m3u")), "named: not the m3u itself");
        True(!named.Contains(Path.Combine(d, "Other.chd")), "named: not a file nobody names");
        string g = Path.Combine(d, "game.gdi");
        File.WriteAllText(Path.Combine(d, "track01.bin"), ""); File.WriteAllText(Path.Combine(d, "track 03.raw"), "");
        File.WriteAllText(g, "3\n1 0 4 2352 track01.bin 0\n2 600 0 2352 \"track 03.raw\" 0\n");
        Eq(2, Scan.Refs(g).Count, "gdi tracks, plain and quoted");
        Eq(0, Scan.Refs(Path.Combine(d, "Other.chd")).Count, "other files name nothing");
    }

    static void DownloadCheck()
    {
        string f = Path.Combine(root, "emu.zip");
        File.WriteAllBytes(f, Encoding.ASCII.GetBytes("an emulator"));
        string sha = "sha256:" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))).ToLowerInvariant();
        Eq((string)null, EmuInstall.Check(f, sha, 11), "matching size and checksum");
        Eq((string)null, EmuInstall.Check(f, null, -1), "nothing listed");
        True(EmuInstall.Check(f, sha, 12) != null, "wrong size");
        True(EmuInstall.Check(f, "sha256:" + new string('0', 64), 11) != null, "wrong checksum");
        Eq((string)null, EmuInstall.Check(f, sha.ToUpperInvariant().Replace("SHA256:", "sha256:"), -1), "checksum in upper case");
    }

    static void Move()
    {
        string a = Path.Combine(root, "move-a"), b = Path.Combine(root, "move-b");
        Directory.CreateDirectory(Path.Combine(a, "sub"));
        File.WriteAllText(Path.Combine(a, "sub", "save.dat"), "save");
        SaveSync.MoveDir(a, b);
        True(!Directory.Exists(a), "moved away");
        Eq("save", File.ReadAllText(Path.Combine(b, "sub", "save.dat")), "moved with its files");
    }

    static void DataFiles()
    {
        string f = Path.Combine(root, "thing.json");
        Json.TakeBroken();
        Eq(0, Json.Load<Thing>(f).N, "missing file: empty");
        Json.Save(f, new Thing { Name = "one", N = 1 });
        Json.Save(f, new Thing { Name = "two", N = 2 });
        True(File.Exists(f + ".prev"), "the save before is kept");
        Eq(1, Json.Load<Thing>(f + ".prev").N, ".prev is the save before");
        File.WriteAllText(f, "{ not json");
        var t = Json.Load<Thing>(f);
        Eq(1, t.N, "broken file: the save before is used");
        var broken = Json.TakeBroken();
        Eq(1, broken.Count, "broken file reported once");
        True(broken.Count == 1 && broken[0].Restored, "reported as restored");
        True(broken.Count == 1 && broken[0].Kept != null && File.ReadAllText(broken[0].Kept) == "{ not json", "the broken file is kept");
        Json.Save(f, new Thing { Name = "three", N = 3 });
        Eq(1, Json.Load<Thing>(f + ".prev").N, "saving over a broken file leaves .prev the last good one");
        Eq(3, Json.Load<Thing>(f).N, "saved");
        Eq(0, Json.TakeBroken().Count, "nothing more to report");
    }

    // Group in Task Manager: off for a new install, and moved to off once for older settings that had it on.
    static void GroupDefault()
    {
        string d = Path.Combine(root, "group"); Directory.CreateDirectory(d);
        True(!Settings.Load(d).TaskManagerGroup, "new install: off");
        File.WriteAllText(Path.Combine(d, "settings.json"), "{\"TaskManagerGroup\": true}");
        var s = Settings.Load(d);
        True(!s.TaskManagerGroup, "old settings: moved to off");
        s.TaskManagerGroup = true; s.Save();
        True(Settings.Load(d).TaskManagerGroup, "turned on after the move: kept");
    }

    static void Links()
    {
        bool group = EmbedLogic.Group;
        EmbedLogic.Group = true;
        try
        {
            string name = Path.GetFileName(Environment.ProcessPath);
            string d1 = Path.Combine(root, "emu1"), d2 = Path.Combine(root, "emu2");
            Directory.CreateDirectory(d1); Directory.CreateDirectory(d2);
            string exe1 = Path.Combine(d1, "emu.exe"); File.WriteAllText(exe1, "emu");
            string link1 = EmbedLogic.AliasFile(exe1);
            Eq(Path.Combine(d1, name), link1, "a link is made");
            Eq(link1, EmbedLogic.AliasFile(exe1), "the same link again");
            // the program's own file with the launcher's name is never replaced
            string exe2 = Path.Combine(d2, "emu.exe"); File.WriteAllText(exe2, "emu2");
            string theirs = Path.Combine(d2, name); File.WriteAllText(theirs, "theirs");
            Eq(exe2, EmbedLogic.AliasFile(exe2), "someone else's file: the real program starts");
            Eq("theirs", File.ReadAllText(theirs), "someone else's file is left alone");
            // a link Ionea made that went stale (the emulator was updated) is made again
            File.Delete(exe1); File.WriteAllText(exe1, "emu, updated");
            Eq(Path.Combine(d1, name), EmbedLogic.AliasFile(exe1), "a stale own link is made again");
        }
        finally { EmbedLogic.Group = group; }
    }

    // Player slots (PlayerSlots.cs): pads fill slots in order, an unplugged pad frees only its own slot and
    // gets it back when it returns, and a pad moved in the menu swaps with the one it replaces.
    static void Players()
    {
        var s = new PlayerSlots();
        s.Update(new[] { "a", "b" });
        Eq("a", s[0], "first pad is Player 1"); Eq("b", s[1], "second pad is Player 2");
        s.Update(new[] { "b" });
        Eq(null, s[0], "unplugged pad frees Player 1"); Eq("b", s[1], "Player 2 keeps its pad");
        s.Update(new[] { "b", "c" });
        Eq("c", s[0], "a new pad takes the first free slot");
        s.Update(new[] { "b", "c", "a" });
        Eq("a", s[2], "a pad coming back while its slot is taken goes to the next free one");
        s.Update(new[] { "b", "a" });
        s.Update(new[] { "b", "a", "c" });
        Eq("c", s[0], "replugged pad gets its own slot back");
        s.Move(0, 1, new[] { "b", "a", "c" });
        Eq("b", s[0], "menu: Player 1 takes the next pad"); Eq("c", s[1], "menu: the pads swap");
        s.Update(new[] { "a", "b", "c", "d", "e" });
        Eq("d", s[3], "fourth slot"); Eq(-1, s.SlotOf("e"), "a fifth pad has no slot");
    }

    static void PadReports()
    {
        ushort b = PadReport.Buttons(PadReport.Layout.Sony, new[] { 2, 3, 10, 13 }, out bool lt, out bool rt);
        Eq((ushort)(PadReport.A | PadReport.B | PadReport.Start | PadReport.Guide), b, "PlayStation: cross, circle, options, PS");
        b = PadReport.Buttons(PadReport.Layout.Nintendo, new[] { 1, 2, 3, 4 }, out lt, out rt);
        Eq((ushort)(PadReport.A | PadReport.B | PadReport.X | PadReport.Y), b, "Switch: by position");
        Eq((ushort)PadReport.A, PadReport.Buttons(PadReport.Layout.Nintendo, new[] { 1 }, out _, out _), "Switch: the bottom button (B) is A");
        PadReport.Buttons(PadReport.Layout.Generic, new[] { 7, 8 }, out lt, out rt);
        True(lt && rt, "L2 and R2 are triggers");
        Eq((ushort)PadReport.Up, PadReport.Hat(0), "hat up");
        Eq((ushort)(PadReport.Down | PadReport.Left), PadReport.Hat(5), "hat down-left");
        Eq((ushort)0, PadReport.Hat(8), "hat centred");
        Eq((short)32767, PadReport.Axis(255, 0, 255, false), "axis right");
        Eq((short)-32767, PadReport.Axis(0, 0, 255, false), "axis left");
        Eq((short)32767, PadReport.Axis(0, 0, 255, true), "y axis up");
        True(Math.Abs((int)PadReport.Axis(128, 0, 255, false)) < 200, "axis centre");
        True(PadReport.ReadByXInput(@"\\?\HID#VID_045E&PID_0B13&IG_00#..."), "an Xbox pad is read by XInput");
        True(!PadReport.ReadByXInput(@"\\?\HID#VID_054C&PID_09CC#..."), "a PlayStation pad isn't");
        Eq(PadReport.Layout.Sony, PadReport.LayoutOf(@"\\?\HID#VID_054C&PID_0CE6#x"), "DualSense layout");

        // DualShock 4 over Bluetooth, report 0x11: cross + options, stick right, L2 half
        var r = new byte[78];
        r[0] = 0x11; r[3] = 255; r[4] = 128; r[5] = 128; r[6] = 128;
        r[7] = 0x08 | 0x20; r[8] = 0x20; r[9] = 0; r[10] = 128;
        True(PadReport.SonyFull(r, r.Length, out var g), "DS4 0x11 read");
        Eq((ushort)(PadReport.A | PadReport.Start), g.wButtons, "DS4 0x11 buttons (hat centred)");
        Eq((short)32767, g.sThumbLX, "DS4 0x11 left stick");
        Eq((byte)128, g.bLeftTrigger, "DS4 0x11 L2");
        // DualSense over Bluetooth, report 0x31: circle, d-pad left, PS
        r = new byte[78];
        r[0] = 0x31; r[2] = 128; r[3] = 0; r[4] = 128; r[5] = 128; r[6] = 0; r[7] = 255;
        r[9] = 0x06 | 0x40; r[10] = 0; r[11] = 0x01;
        True(PadReport.SonyFull(r, r.Length, out g), "DualSense 0x31 read");
        Eq((ushort)(PadReport.Left | PadReport.B | PadReport.Guide), g.wButtons, "DualSense 0x31 buttons");
        Eq((short)32767, g.sThumbLY, "DualSense 0x31 stick up");
        Eq((byte)255, g.bRightTrigger, "DualSense 0x31 R2");
        True(!PadReport.SonyFull(new byte[64], 64, out _), "other reports aren't read this way");
    }

    // A definition's helper program (GameInfo.cs ToolPath): {data} / {app}, a list tried in order, none found.
    static void ToolPathLogic()
    {
        string data = @"X:\Some\data", app = @"X:\Some\Launcher\";
        var both = new JsonArray(@"{data}\Tools\t.exe", @"{app}\..\Tools\t.exe");
        Eq(@"X:\Some\data\Tools\t.exe", ToolPath.Find(both, data, app, f => true), "the first that exists");
        Eq(@"X:\Some\Tools\t.exe", ToolPath.Find(both, data, app, f => f == @"X:\Some\Tools\t.exe"), "next to Ionea's folder");
        Eq(@"X:\Some\data\Tools\t.exe", ToolPath.Find(both, data, app, f => false), "none: the first, for the message");
        Eq(@"X:\Some\Launcher\tools\t.exe", ToolPath.Find(JsonValue.Create("tools/t.exe"), data, app, f => false), "one relative path starts in {app}");
        Eq(@"Y:\t.exe", ToolPath.Find(JsonValue.Create(@"Y:\t.exe"), data, app, f => false), "a full path as it is");
        Eq(null, ToolPath.Find(null, data, app, f => true), "no tool");
    }

    // Streaming mode (Settings > Look > In games): standalone emulators become child windows (EmbedLogic.Owned) and
    // hosted pictures stay with WPF (DirectPicture.Off), so window capture of the launcher sees the game.
    static void StreamingModeLogic()
    {
        var emu = new Emulator { Name = "Some emulator", Exe = @"X:\Emus\emu.exe" };
        Eq(true, EmbedLogic.Owned(emu, false), "off: an emulator's window is owned, over the launcher");
        Eq(false, EmbedLogic.Owned(emu, true), "on: a child window inside the launcher");
        Eq(true, EmbedLogic.CanEmbed(emu), "on: still played inside the launcher");
        var alone = new Emulator { Name = "On its own", Exe = @"X:\Emus\emu.exe", Inside = false };
        Eq(false, EmbedLogic.Owned(alone, false), "played on its own: never owned");
        Eq(false, EmbedLogic.Owned(new Emulator { Name = "Core", Exe = @"X:\Cores\c_libretro.dll" }, false), "a core: never owned");
        Eq(false, EmbedLogic.Owned(null, false), "no emulator: not owned");
        bool was = DirectPicture.Allowed;
        try
        {
            DirectPicture.Allowed = true;
            Eq((string)null, DirectPicture.Off(false), "off: the picture can go straight to the screen");
            Eq("streaming mode", DirectPicture.Off(true), "on: WPF draws the picture");
            DirectPicture.Allowed = false;
            Eq("turned off", DirectPicture.Off(false), "tests' directpicture off: WPF");
            True(DirectPicture.Off(true) != null, "both: WPF");
        }
        finally { DirectPicture.Allowed = was; }
    }

    // How the Home menu holds a standalone emulator (HomeMenu.cs, PauseLogic): frozen only when the setting is on.
    static void PauseWayLogic()
    {
        Eq(PauseWay.Freeze, PauseLogic.Decide(true, "F4"), "setting on: frozen, even with a pause key");
        Eq(PauseWay.Freeze, PauseLogic.Decide(true, null), "setting on, no key: frozen");
        Eq(PauseWay.Key, PauseLogic.Decide(false, "F4"), "setting off, a pause key: its own pause");
        Eq(PauseWay.Unfocus, PauseLogic.Decide(false, null), "setting off, no key: keeps running behind the menu");
        Eq(PauseWay.Unfocus, PauseLogic.Decide(false, " "), "a blank key is no key");
    }

    // UI text by id (Loc.cs): the active language, then English, then the id; {0} args; one / other plurals;
    // the Language setting's choice; the pseudo-locale.
    static void Language()
    {
        string dir = Path.Combine(root, "lang-data"), lang = Path.Combine(dir, "lang");
        Directory.CreateDirectory(lang);
        File.WriteAllText(Path.Combine(lang, "en.json"), """
            { "_name": "English", "t.hello": "Hello", "t.only_en": "Only English", "t.moved": "Moved to {0} at {1:0.0} %",
              "t.games": { "one": "{0} game", "other": "{0} games" }, "t.left": { "one": "{0} game left in {1}", "other": "{0} games left in {1}" },
              "t.bad": "Broken {1" }
            """);
        File.WriteAllText(Path.Combine(lang, "xx.json"), """
            { "_name": "Test language", "t.hello": "Hallo", "t.games": { "one": "{0} spill", "other": "{0} spillene" } }
            """);
        try
        {
            Loc.Use("en", dir);
            Eq("Hello", Loc.T("t.hello"), "English text");
            Eq("t.nothing", Loc.T("t.nothing"), "no such id: the id");
            Eq("Moved to Home at 12.5 %", Loc.T("t.moved", "Home", 12.5), "args and a format");
            Eq("Broken {1", Loc.T("t.bad", "x"), "bad placeholders: the text as it is");
            Eq("1 game", Loc.N("t.games", 1), "plural one");
            Eq("0 games", Loc.N("t.games", 0), "plural other (0)");
            Eq("3 games left in Home", Loc.N("t.left", 3, "Home"), "plural with args");
            Eq("t.nothing", Loc.N("t.nothing", 2), "no such plural: the id");

            Loc.Use("xx", dir);
            Eq("xx", Loc.Current, "a language with a file");
            Eq("Hallo", Loc.T("t.hello"), "translated");
            Eq("Only English", Loc.T("t.only_en"), "missing: English");
            Eq("2 spillene", Loc.N("t.games", 2), "translated plural");
            Eq("1 game left in Home", Loc.N("t.left", 1, "Home"), "missing plural: English");
            Eq("Hello", Loc.ToEnglish("Hallo"), "back to English (row pictures)");
            Eq("Not a string", Loc.ToEnglish("Not a string"), "unknown text stays");
            True(File.ReadAllText(Log.File).Contains("no text for \"t.nothing\""), "an id with no text is logged");

            var have = new[] { "en", "xx", "nb", "xx-pseudo" };
            Eq("en", Loc.Resolve("auto", "de-DE", have), "auto, no file: English");
            Eq("nb", Loc.Resolve("auto", "nb-NO", have), "auto: the language of the Windows region");
            Eq("nb", Loc.Resolve(null, "nb", have), "null is auto");
            Eq("en", Loc.Resolve("auto", "xx-pseudo", have), "the pseudo-locale is never automatic");
            Eq("xx", Loc.Resolve("XX", "nb-NO", have), "a set language wins, any case");
            Eq("en", Loc.Resolve("fr", "nb-NO", have), "a set language with no file: English");

            Loc.Use("xx-pseudo", dir);
            string p = Loc.T("t.moved", "Home", 1.0);
            True(p.StartsWith("[") && p.EndsWith("]") && p.Contains("Home") && p.Contains("1.0"), "pseudo: bracketed, args kept: " + p);
            True(Loc.PseudoOf("Moved to {0}").Length >= (int)("Moved to {0}".Length * 1.3), "pseudo: about 30 % longer");
            Eq("Hello", Loc.ToEnglish(Loc.T("t.hello")), "pseudo back to English");
        }
        finally { Loc.Use("en", Path.Combine(root, "no-such-folder")); }
    }
}
