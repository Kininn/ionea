// Games inside the launcher keep time with the display.
// Before, the core ran at its own rate by the clock (CoreGame.Loop) and each frame was shown the moment it was
// made. The display shows a frame at its next refresh, so a 59.73 fps core on a 60 Hz display showed one frame
// twice every few seconds, and at 120 or 144 Hz the frames were held for uneven times: visible in scrolling games.
// Now, when the core's rate is close to the display's (FrameTiming.Decide), the core runs one frame per refresh
// (or per 2, 3 ... refreshes when the display is an exact multiple of it): the core thread waits for the next
// refresh of the monitor the launcher is on (VBlankClock), so every frame is shown for the same time. The game
// then runs that little bit faster or slower than its own rate (at most 5 %), and its sound is played that much
// faster or slower too (CoreSound.SetSkew); the sound queue's own small correction (at most 0.5 %) keeps the two
// clocks together on top of that. A core far from the display's rate (50 fps on 60 Hz, 30 fps on 144 Hz) keeps
// its own clock, as before. Settings > Look > In games > Match the display turns it off.
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ionea;

public enum Pace { Clock, Display }

// How the core thread keeps time: by the clock at the core's rate, or by the display, Refreshes per frame.
public readonly struct PacePlan
{
    public readonly Pace Mode;
    public readonly int Refreshes;   // display refreshes per core frame (0 by the clock)
    public readonly double Rate;     // frames per second the core runs at
    public readonly double Skew;     // Rate against the core's own rate (1 by the clock): the sound's speed too

    public PacePlan(Pace mode, int refreshes, double rate, double skew) { Mode = mode; Refreshes = refreshes; Rate = rate; Skew = skew; }
    public static PacePlan ClockAt(double fps) => new(Pace.Clock, 0, fps, 1);
    public override string ToString() => Mode == Pace.Clock ? $"clock {Rate:0.###}" : $"display {Refreshes}x {Rate:0.###} skew {Skew:0.#####}";
}

public static class FrameTiming
{
    public const double MaxSkew = 0.05;         // one frame per refresh: the core may run up to 5 % off its own rate
    public const double MultipleSkew = 0.01;    // several refreshes per frame: only a (nearly) exact multiple
    public const int MaxRefreshes = 8;
    public const double MaxNudge = SoundQueue.MaxNudge;   // the sound queue's own correction on top (0.5 %)

    // Which way to keep time for a core of this rate on a display of this refresh rate (0 or less: not known).
    public static PacePlan Decide(double coreFps, double displayHz)
    {
        if (!(coreFps > 1) || !(displayHz > 1) || double.IsInfinity(coreFps) || double.IsInfinity(displayHz)) return PacePlan.ClockAt(coreFps > 1 ? coreFps : 60);
        int n = (int)Math.Round(displayHz / coreFps, MidpointRounding.AwayFromZero);
        if (n < 1 || n > MaxRefreshes) return PacePlan.ClockAt(coreFps);
        double rate = displayHz / n, skew = rate / coreFps;
        if (Math.Abs(skew - 1) > (n == 1 ? MaxSkew : MultipleSkew)) return PacePlan.ClockAt(coreFps);
        return new PacePlan(Pace.Display, n, rate, skew);
    }

    // How fast the sound plays against the core's own sound rate: the plan's skew, and the queue's correction on top.
    public static double SoundSpeed(double skew, double nudge) =>
        Math.Clamp(double.IsNaN(skew) ? 1 : skew, 1 - MaxSkew, 1 + MaxSkew) * (1 + Math.Clamp(double.IsNaN(nudge) ? 0 : nudge, -MaxNudge, MaxNudge));
}

// The refreshes of the monitor a window is on, counted on a thread of its own (D3DKMTWaitForVerticalBlankEvent),
// and that monitor's refresh rate. The core thread waits on it (Wait) and never blocks for long: when no refresh
// comes in time, or the counted rate doesn't agree with the one Windows reports, Ok is false and the core keeps
// its own clock. Looks again each second which monitor the window is on, and its rate every few seconds.
public sealed class VBlankClock : IDisposable
{
    readonly IntPtr hwnd;
    readonly Thread th;
    readonly AutoResetEvent tick = new(false);
    volatile bool stop, broken, agrees;
    long count;          // refreshes counted
    long lastTick;       // when the latest one came (Stopwatch ticks)
    double hz;           // the monitor's refresh rate as Windows reports it (0: not known)
    long used;           // core thread: the refresh the last frame started on

    public VBlankClock(IntPtr window)
    {
        hwnd = window;
        th = new Thread(Run) { IsBackground = true, Name = "Ionea display clock", Priority = ThreadPriority.Highest };
        th.Start();
    }

    public double Hz => Volatile.Read(ref hz);
    // Refreshes are coming, at the rate Windows reports.
    public bool Ok => !broken && agrees && Hz > 1 && Stopwatch.GetTimestamp() - Volatile.Read(ref lastTick) < Stopwatch.Frequency / 4;

    // Core thread: from the refresh now (after a pause, or when the display takes over from the clock).
    public void Resync() => used = Volatile.Read(ref count);

    // Core thread: wait until n refreshes after the one the last frame started on. Already past it (the frame
    // took longer): go at once, from this refresh. False when no refresh came in time (keep the clock this frame).
    public bool Wait(int n)
    {
        long target = used + Math.Max(1, n);
        long now = Volatile.Read(ref count);
        if (now >= target) { used = now; return true; }
        int ms = Math.Clamp((int)(3000 / Math.Max(Hz, 1)), 20, 100);   // three refreshes
        while ((now = Volatile.Read(ref count)) < target)
            if (stop || !tick.WaitOne(ms)) return false;
        used = now;
        return true;
    }

    public void Dispose()
    {
        stop = true;
        th.Join(250);   // ends at the next refresh; a monitor that's off may hold it longer (a background thread)
    }

    void Run()
    {
        IntPtr mon = IntPtr.Zero;
        uint adapter = 0, source = 0;
        string? device = null;
        long nextLook = 0, countAtLook = 0, lookAt = 0;
        int looks = 0, fails = 0, quick = 0, off = 0;
        try
        {
            while (!stop)
            {
                long now = Stopwatch.GetTimestamp();
                if (now >= nextLook)
                {
                    nextLook = now + Stopwatch.Frequency;
                    var m = MonitorFromWindow(hwnd, 2);   // MONITOR_DEFAULTTONEAREST
                    bool moved = m != mon || adapter == 0;
                    if (moved)
                    {
                        Close(ref adapter);
                        mon = m;
                        device = Open(m, out adapter, out source);
                        agrees = false; off = 0; quick = 0;
                    }
                    if (moved || ++looks % 5 == 0)
                    {
                        double r = device != null ? RefreshOf(device) : 0, was = Hz;
                        Volatile.Write(ref hz, r);
                        if (moved || Math.Abs(r - was) > 0.01)
                            Log.Write(adapter != 0 ? $"display clock: {device} at {r:0.###} Hz" : $"display clock: no refresh events for {device ?? "the monitor"}; games keep their own clock");
                    }
                    else
                    {
                        // The refreshes counted over the last second against the reported rate: they must agree.
                        long c = Volatile.Read(ref count);
                        double secs = (now - lookAt) / (double)Stopwatch.Frequency, counted = lookAt > 0 && secs > 0.5 ? (c - countAtLook) / secs : 0, r = Hz;
                        bool ok = counted > 0 && r > 1 && Math.Abs(counted / r - 1) < 0.03;
                        if (ok) { if (!agrees) Log.Write($"display clock: {counted:0.##} refreshes a second counted, as reported"); agrees = true; off = 0; }
                        else if (counted > 0 && ++off == 2) { if (agrees) Log.Write($"display clock: {counted:0.##} refreshes a second counted against {r:0.###} Hz reported; games keep their own clock"); agrees = false; }
                    }
                    countAtLook = Volatile.Read(ref count); lookAt = now;
                }
                if (adapter == 0) { Thread.Sleep(250); continue; }
                var wv = new WaitVBlank { Adapter = adapter, Device = 0, Source = source };
                int st = D3DKMTWaitForVerticalBlankEvent(ref wv);
                if (st != 0)
                {
                    if (++fails >= 20) { Log.Write($"display clock: refresh wait failed (0x{st:X8}); looking again"); Close(ref adapter); fails = 0; nextLook = 0; }
                    Thread.Sleep(5);
                    continue;
                }
                fails = 0;
                long t = Stopwatch.GetTimestamp(), prev = Volatile.Read(ref lastTick);
                double expect = Hz > 1 ? Stopwatch.Frequency / Hz : 0;
                // A wait that doesn't wait (some virtual displays): no refreshes to keep time with.
                if (expect > 0 && prev != 0 && t - prev < expect * 0.5) { if (++quick > 60) { broken = true; Log.Write("display clock: refresh waits return at once; games keep their own clock"); return; } }
                else if (quick > 0) quick--;
                Volatile.Write(ref lastTick, t);
                Interlocked.Increment(ref count);
                tick.Set();
            }
        }
        catch (Exception e) { broken = true; Log.Write("display clock: " + e.Message); }
        finally { Close(ref adapter); }
    }

    // The monitor's display device (\\.\DISPLAYn) and its adapter for refresh waits (adapter 0: none).
    static string? Open(IntPtr mon, out uint adapter, out uint source)
    {
        adapter = 0; source = 0;
        var mi = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
        if (mon == IntPtr.Zero || !GetMonitorInfoW(mon, ref mi)) return null;
        IntPtr dc = CreateDCW(null, mi.Device, null, IntPtr.Zero);
        if (dc == IntPtr.Zero) return mi.Device;
        try
        {
            var oa = new OpenFromHdc { Hdc = dc };
            if (D3DKMTOpenAdapterFromHdc(ref oa) == 0) { adapter = oa.Adapter; source = oa.Source; }
        }
        finally { DeleteDC(dc); }
        return mi.Device;
    }

    static void Close(ref uint adapter)
    {
        if (adapter == 0) return;
        var ca = new CloseAdapter { Adapter = adapter };
        D3DKMTCloseAdapter(ref ca);
        adapter = 0;
    }

    // The display device's refresh rate: exact from the display configuration (59.94, 143.998 ...), else the
    // whole number from its current mode (59 read as 59.94).
    public static double RefreshOf(string device)
    {
        try
        {
            if (GetDisplayConfigBufferSizes(2, out uint np, out uint nm) == 0 && np > 0)   // QDC_ONLY_ACTIVE_PATHS
            {
                var paths = new PathInfo[np];
                var modes = new byte[nm * 64];   // DISPLAYCONFIG_MODE_INFO
                if (QueryDisplayConfig(2, ref np, paths, ref nm, modes, IntPtr.Zero) == 0)
                    for (int i = 0; i < np; i++)
                    {
                        var p = paths[i];
                        var sn = new SourceName { Type = 1, Size = (uint)Marshal.SizeOf<SourceName>(), LuidLow = p.SrcLuidLow, LuidHigh = p.SrcLuidHigh, Id = p.SrcId };
                        if (DisplayConfigGetDeviceInfo(ref sn) != 0 || !string.Equals(sn.Gdi, device, StringComparison.OrdinalIgnoreCase)) continue;
                        if (p.RefreshDen != 0 && p.RefreshNum != 0) return (double)p.RefreshNum / p.RefreshDen;
                        int at = (int)p.TgtMode * 64;
                        if (p.TgtMode != 0xFFFFFFFF && at + 40 <= modes.Length && BitConverter.ToInt32(modes, at) == 2)   // a target mode: its vSyncFreq
                        {
                            uint num = BitConverter.ToUInt32(modes, at + 32), den = BitConverter.ToUInt32(modes, at + 36);
                            if (num != 0 && den != 0) return (double)num / den;
                        }
                    }
            }
        }
        catch (Exception e) { Log.Write("display clock: display configuration: " + e.Message); }
        var dm = new DevMode { Size = (ushort)Marshal.SizeOf<DevMode>() };
        if (EnumDisplaySettingsW(device, -1, ref dm) && dm.Frequency > 1)
            return dm.Frequency switch { 23 => 23.976, 29 => 29.97, 59 => 59.94, 119 => 119.88, _ => dm.Frequency };
        return 0;
    }

    [StructLayout(LayoutKind.Sequential)] struct OpenFromHdc { public IntPtr Hdc; public uint Adapter; public uint LuidLow; public int LuidHigh; public uint Source; }
    [StructLayout(LayoutKind.Sequential)] struct CloseAdapter { public uint Adapter; }
    [StructLayout(LayoutKind.Sequential)] struct WaitVBlank { public uint Adapter, Device, Source; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MonitorInfoEx { public int Size; public int L, T, R, B, WL, WT, WR, WB; public uint Flags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device; }
    [StructLayout(LayoutKind.Sequential)]
    struct PathInfo
    {
        public uint SrcLuidLow; public int SrcLuidHigh; public uint SrcId, SrcMode, SrcFlags;
        public uint TgtLuidLow; public int TgtLuidHigh; public uint TgtId, TgtMode; public int Tech, Rotation, Scaling;
        public uint RefreshNum, RefreshDen; public int ScanLine, Available; public uint TgtFlags;
        public uint Flags;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SourceName { public int Type; public uint Size; public uint LuidLow; public int LuidHigh; public uint Id; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Gdi; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public ushort SpecVersion, DriverVersion, Size, DriverExtra; public uint Fields;
        public int X, Y; public uint Orientation, FixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public ushort LogPixels; public uint BitsPerPel, Width, Height, DisplayFlags, Frequency;
        public uint IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
    }

    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfoW(IntPtr mon, ref MonitorInfoEx mi);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettingsW(string device, int mode, ref DevMode dm);
    [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint np, [Out] PathInfo[] paths, ref uint nm, [Out] byte[] modes, IntPtr topology);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref SourceName info);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateDCW(string? driver, string device, string? output, IntPtr init);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern int D3DKMTOpenAdapterFromHdc(ref OpenFromHdc a);
    [DllImport("gdi32.dll")] static extern int D3DKMTCloseAdapter(ref CloseAdapter a);
    [DllImport("gdi32.dll")] static extern int D3DKMTWaitForVerticalBlankEvent(ref WaitVBlank a);
}
