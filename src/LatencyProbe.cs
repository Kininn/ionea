// Tests: how long a press takes to reach a hosted game's picture and sound (the script's "latency" command).
// The script presses a game button through Buttons (read even while the launcher isn't in front, so a test needs
// no real input and no focus). The core thread, the UI thread and the sound thread stamp when the press got to
// them. Nothing here runs unless a test turned it on.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace Ionea;

public static class LatencyProbe
{
    public static volatile bool On;        // a latency test is running
    public static volatile int Buttons;    // test presses, as libretro button bits
    static volatile bool armed;            // a press is out, waiting for the game
    static long pressed, polled, framed, drawn, made, heard;
    static long ticks, firstTick, lastTick;
    public static readonly List<double[]> Results = new();   // per press: polled, frame, handed over, sound made, sound out (ms)

    static long Now => Stopwatch.GetTimestamp();
    static double Ms(long from, long to) => from == 0 || to == 0 ? double.NaN : (to - from) * 1000.0 / Stopwatch.Frequency;

    public static void Press(int id)
    {
        polled = framed = drawn = made = heard = 0;
        pressed = Now;
        armed = true;
        Buttons |= 1 << id;
    }

    // Ends the press: what got through is kept (NaN for what didn't).
    public static double[] Release(int id)
    {
        Buttons &= ~(1 << id);
        armed = false;
        var r = new[] { Ms(pressed, polled), Ms(pressed, framed), Ms(pressed, drawn), Ms(pressed, made), Ms(pressed, heard) };
        lock (Results) Results.Add(r);
        return r;
    }

    // core thread
    public static void Polled() { if (armed && polled == 0) Interlocked.CompareExchange(ref polled, Now, 0); }
    // core thread: the game's picture, bright = the press shows in it. True when it's the first one that does.
    public static bool Frame(bool bright) => armed && bright && polled != 0 && framed == 0 && Interlocked.CompareExchange(ref framed, Now, 0) == 0;
    // core thread: sound from the game, loud = the press is heard in it
    public static void Made(bool loud) { if (armed && loud && polled != 0 && made == 0) Interlocked.CompareExchange(ref made, Now, 0); }
    // UI thread: the bright picture went to WPF
    public static void Drawn() { if (armed && drawn == 0) Interlocked.CompareExchange(ref drawn, Now, 0); }
    // sound thread: the first loud sample leaves for the device; it plays this far after now
    public static void Out(double inMs) { if (armed && made != 0 && heard == 0) Interlocked.CompareExchange(ref heard, Now + (long)(inMs * Stopwatch.Frequency / 1000), 0); }
    // UI thread: each WPF frame
    public static void Tick() { long n = Now; if (firstTick == 0) firstTick = n; lastTick = n; ticks++; }

    public static long PressedAt => pressed;   // Stopwatch ticks (QPC), the same clock in every process

    public static double FramesPerSecond => ticks > 1 ? (ticks - 1) / Ms(firstTick, lastTick) * 1000 : 0;

    public static void Reset() { lock (Results) Results.Clear(); ticks = 0; firstTick = lastTick = 0; Buttons = 0; armed = false; }

    public static string Summary()
    {
        string[] names = { "read by the game", "frame with it", "picture to Windows", "sound made", "sound to the device" };
        var s = new List<string>();
        lock (Results)
            for (int i = 0; i < names.Length; i++)
            {
                var v = Results.Select(r => r[i]).Where(x => !double.IsNaN(x)).OrderBy(x => x).ToList();
                if (v.Count == 0) { s.Add($"{names[i]} never"); continue; }
                s.Add($"{names[i]} {v.Average():0.0} ms (min {v[0]:0.0}, max {v[^1]:0.0}, {v.Count}/{Results.Count})");
            }
        return string.Join("; ", s) + $"; WPF at {FramesPerSecond:0} frames a second";
    }
}
