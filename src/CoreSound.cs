// Game sound with as little delay as the PC allows.
//
// Before, the game's sound went out through waveOut (WaveOut in Libretro.cs) in six queued blocks, about 100 ms,
// and the full queue held the game back to the right speed. Now the game keeps time with the clock
// (CoreGame.Loop) and its sound goes into a short queue (SoundQueue) that the sound device reads straight from:
// WASAPI shared mode, woken by the device each period, at the shortest period the device allows. The queue is
// read out at the device's rate through a resampler whose speed is nudged by at most 0.5 % to keep the queue at
// its target, so the game's clock and the device's clock never drift apart and nobody can hear the nudge.
// What remains: the queue's target, which is what one device period takes plus half a video frame (a core hands
// its sound over a frame at a time) plus a few ms to spare, the device's period and the device's own delay.
// When the queue runs dry anyway (a slow frame), the target grows a little, and it slowly comes back down after
// a while without trouble. The first 30 s keep 4 ms more: the first game after the PC sat idle could run dry
// once early on (a click), seen on this PC.
// If the PC can't open the device this way, CoreGame uses WaveOut as before.
// When the game keeps time with the display (DisplaySync.cs) it runs a little faster or slower than its own rate;
// the sound is then read out that much faster or slower too (SetSkew), and the nudge works on top of that.
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ionea;

// The queue between the core (Write, on the core's thread) and the device (Read, on the sound thread).
public sealed unsafe class SoundQueue
{
    public const double SpareMs = 4, StepMs = 4, MaxRaiseMs = 36, MaxNudge = 0.005;
    public const double StartMs = 4, StartS = 30;   // kept on top of the target at first, for this long without trouble
    const double Gain = 0.4;        // speed nudge per second of distance from the target (0.5 % at 12.5 ms)
    const double Settle = 0.02;     // and per second² of it, so two clocks that run apart still settle on the target
    const double SmoothS = 0.4;     // how slowly the measured fill follows (a frame's sawtooth is smoothed out)
    const double TooMuchMs = 60;    // more than this above the target: the oldest sound is dropped
    const double CloseMs = 1;       // less than this to spare after a read counts as a close call

    readonly object gate = new();
    readonly float[] ring;          // stereo frames at the core's rate
    readonly int mask;
    int rd, count;
    readonly double inRate, frameMs;
    double readMs;
    double outRate, ratio0, ratio;
    double skew = 1;                // the game's speed against its own rate (FrameTiming.Decide)
    Resampler rs;   // set by SetOutRate, from the constructor on
    float[] st = new float[(Resampler.Half * 4 + 16) * 2];   // frames being resampled (history + what is next)
    int stCount = Resampler.Half - 1;
    double pos = Resampler.Half - 1;
    bool primed;
    double smooth = -1, t, graceUntil = 1, lastChange, lastRaise = -1, held;

    public double BaseTargetMs, TargetMs;
    bool starting;                  // the start's extra is still on
    public double FillMs;            // queued sound at the last Read
    public double SmoothMs => smooth < 0 ? FillMs : smooth;
    public double Seconds => t;   // sound read so far
    public double Nudge => ratio / (ratio0 * skew) - 1;
    public double Skew => skew;
    public int Gaps;
    public int LoudAt = -1;   // tests (LatencyProbe): where in the last read the first loud sample went
    public double LowMs = double.MaxValue;   // the closest call: least sound left over after a read (after the first second)
    public long Dropped;             // core frames thrown away (queue far too full)

    // frameMs: a video frame (what a core hands over at once); readMs: what the device takes at once.
    public SoundQueue(double inRate, double outRate, double frameMs, double readMs)
    {
        this.inRate = inRate;
        this.frameMs = frameMs;
        int cap = 1;
        while (cap < inRate) cap <<= 1;   // a second or a little more
        ring = new float[cap * 2];
        mask = cap - 1;
        SetOutRate(outRate, readMs);
    }

    // A (new) device: its rate and what it takes at once (sound thread only).
    [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(rs))]
    public void SetOutRate(double rate, double readMs)
    {
        // Just before the device takes its part, the queue holds the target give or take half a frame.
        this.readMs = readMs;
        BaseTargetMs = readMs + frameMs / 2 + Resampler.Half * 1000.0 / inRate + SpareMs;
        TargetMs = BaseTargetMs + StartMs; starting = true; lastChange = t;
        outRate = rate;
        rs = new Resampler(inRate, rate);
        ratio0 = inRate / rate;
        ratio = ratio0 * skew;
        held = 0;
        Restart();
    }

    // The game now runs this much faster (above 1) or slower than its own rate: its sound is read out as much faster.
    public void SetSkew(double s)
    {
        lock (gate) skew = FrameTiming.SoundSpeed(s, 0);
    }

    // After a pause or a new device: fill up to the target before playing, and don't count gaps for a moment.
    public void Restart() { primed = false; smooth = -1; graceUntil = t + 1; }

    // The core's sound: interleaved stereo 16-bit.
    public void Write(IntPtr data, int frames, float volume)
    {
        var s = (short*)data;
        float v = volume / 32768f;
        int cap = mask + 1;
        if (frames > cap) { s += (frames - cap) * 2; frames = cap; }
        lock (gate)
        {
            int over = count + frames - cap;
            if (over > 0) { rd = (rd + over) & mask; count -= over; Dropped += over; }
            int w = (rd + count) & mask;
            for (int i = 0; i < frames; i++)
            {
                ring[w * 2] = s[i * 2] * v;
                ring[w * 2 + 1] = s[i * 2 + 1] * v;
                w = (w + 1) & mask;
            }
            count += frames;
        }
    }

    // Fills dst with frames × channels floats (the device's layout). False when it was all silence.
    public bool Read(float* dst, int frames, int channels)
    {
        const int H = Resampler.Half;
        double ms = 1000.0 / inRate;
        int made = 0;
        bool probe = LatencyProbe.On;
        LoudAt = -1;
        lock (gate)
        {
            double fill = (count + stCount - pos) * ms;
            if (fill > TargetMs + TooMuchMs)
            {
                int drop = Math.Min(count, (int)((fill - TargetMs) / ms));
                rd = (rd + drop) & mask; count -= drop; Dropped += drop;
                fill -= drop * ms;
                smooth = -1;
            }
            FillMs = fill;
            // Start once there is the target plus half a frame: a start right after the core handed over a frame
            // is at the top of the queue's normal swing, and the average must still come out at the target.
            // Anything older on top of that (left from before a pause) goes, so the start isn't late.
            double start = TargetMs + frameMs / 2;
            if (!primed && fill >= start)
            {
                primed = true; smooth = -1;
                int drop = Math.Min(count, (int)((fill - start) / ms));
                if (fill - start > 2 && drop > 0) { rd = (rd + drop) & mask; count -= drop; fill -= drop * ms; FillMs = fill; }
            }
            else if (primed && t >= graceUntil)
            {
                double left = fill - Resampler.Half * 1000.0 / inRate - readMs;   // what this read leaves
                LowMs = Math.Min(LowMs, left);
                // A close call: keep more from now on, before it ever runs dry. (Below nothing this read runs dry
                // and the gap itself raises the queue.)
                if (left >= 0 && left < CloseMs && t - lastRaise > 0.5) { TargetMs = Math.Min(BaseTargetMs + MaxRaiseMs, TargetMs + CloseMs + 1 - left); lastChange = lastRaise = t; starting = false; }
            }
            if (primed)
            {
                double dt = frames / outRate;
                smooth = smooth < 0 ? fill : smooth + (fill - smooth) * (dt / (SmoothS + dt));
                double off = (smooth - TargetMs) / 1000.0;
                // (only while the plain nudge isn't at its limit, so a big catch-up doesn't overshoot later)
                if (Math.Abs(off * Gain) < MaxNudge) held = Math.Clamp(held + off * Settle * dt, -MaxNudge, MaxNudge);
                double nudge = Math.Clamp(off * Gain + held, -MaxNudge, MaxNudge);
                ratio = ratio0 * FrameTiming.SoundSpeed(skew, nudge);
                int need = (int)(pos + (frames - 1) * ratio) + H + 1 - stCount;
                if (need > 0)
                {
                    if ((stCount + need) * 2 > st.Length) Array.Resize(ref st, (stCount + need) * 2 + 256);
                    int take = Math.Min(need, count);
                    int first = Math.Min(take, mask + 1 - rd);
                    Array.Copy(ring, rd * 2, st, stCount * 2, first * 2);
                    if (take > first) Array.Copy(ring, 0, st, (stCount + first) * 2, (take - first) * 2);
                    rd = (rd + take) & mask; count -= take; stCount += take;
                }
            }
        }
        if (primed)
        {
            fixed (float* sp = st)
                for (; made < frames; made++)
                {
                    int n0 = (int)pos;
                    if (n0 + H >= stCount) break;
                    rs.At(sp, n0, pos - n0, out float l, out float r);
                    if (probe && LoudAt < 0 && (l > 0.015f || l < -0.015f)) LoudAt = made;
                    float* o = dst + made * channels;
                    if (channels >= 2) { o[0] = l; o[1] = r; for (int c = 2; c < channels; c++) o[c] = 0; }
                    else o[0] = (l + r) * 0.5f;
                    pos += ratio;
                }
            int shift = (int)pos - (H - 1);
            if (shift > 0)
            {
                Array.Copy(st, shift * 2, st, 0, (stCount - shift) * 2);
                stCount -= shift; pos -= shift;
            }
            if (made < frames)
            {
                // Ran dry: wait for the target again, and from now on keep what was missing and a little more.
                primed = false;
                double missing = (frames - made) * ratio * 1000.0 / inRate;
                if (t >= graceUntil) { Gaps++; TargetMs = Math.Min(BaseTargetMs + MaxRaiseMs, TargetMs + Math.Max(StepMs, missing + 2)); starting = false; }
                lastChange = t;
            }
        }
        for (int i = made * channels; i < frames * channels; i++) dst[i] = 0;
        t += frames / outRate;
        if (starting) { if (t - lastChange > StartS) { starting = false; TargetMs = Math.Max(BaseTargetMs, TargetMs - StartMs); lastChange = t; } }
        else if (TargetMs > BaseTargetMs && t - lastChange > 30) { TargetMs = Math.Max(BaseTargetMs, TargetMs - 1); lastChange = t; }
        return made > 0;
    }
}

// Band-limited resampling: a 48-tap Kaiser-windowed sinc, 256 phases in between, linear between phases.
public sealed unsafe class Resampler
{
    public const int Half = 24, Taps = Half * 2, Phases = 256;
    readonly float[] c = new float[(Phases + 1) * Taps];

    public Resampler(double inRate, double outRate)
    {
        double g = 0.91 * Math.Min(1, outRate / inRate);   // the cutoff, so the stop band starts by the lower Nyquist
        const double beta = 7;
        double i0b = I0(beta);
        var row = new double[Taps];
        for (int p = 0; p <= Phases; p++)
        {
            double f = (double)p / Phases, sum = 0;
            for (int k = 0; k < Taps; k++)
            {
                double d = f + (Half - 1) - k, u = d / Half;
                double w = Math.Abs(u) >= 1 ? 0 : I0(beta * Math.Sqrt(1 - u * u)) / i0b;
                double x = Math.PI * g * d;
                row[k] = (Math.Abs(x) < 1e-9 ? g : g * Math.Sin(x) / x) * w;
                sum += row[k];
            }
            for (int k = 0; k < Taps; k++) c[p * Taps + k] = (float)(row[k] / sum);
        }
    }

    static double I0(double x)
    {
        double s = 1, term = 1;
        for (int k = 1; k < 40; k++) { double h = x / (2 * k); term *= h * h; s += term; }
        return s;
    }

    // The sound at st[n0 + f] (stereo frames). Needs Half - 1 frames before n0 and Half after.
    public void At(float* st, int n0, double f, out float l, out float r)
    {
        double ph = f * Phases;
        int p = (int)ph;
        float t = (float)(ph - p);
        float L = 0, R = 0;
        fixed (float* cp = c)
        {
            float* a = cp + p * Taps, b = a + Taps, s = st + (n0 - (Half - 1)) * 2;
            for (int k = 0; k < Taps; k++)
            {
                float co = a[k] + t * (b[k] - a[k]);
                L += co * s[k * 2];
                R += co * s[k * 2 + 1];
            }
        }
        l = L; r = R;
    }
}

// The default sound device through WASAPI, on a thread of its own. Same use as WaveOut, but Write never waits.
public sealed unsafe class CoreSound : IDisposable
{
    public float Volume = 1f;

    readonly double inRate, fps;
    SoundQueue q = null!;   // made on the sound thread (Run) before the constructor returns
    readonly Thread th;
    Thread watch;
    readonly ManualResetEventSlim ready = new(), quit = new();
    volatile bool stop, pauseReq, reopen;
    volatile string? devId;
    string? openError;
    volatile IntPtr ev;
    IntPtr en, ac, rc;
    int outRate, ch, devTarget;
    double latSum, streamMs, periodMs; long latN;

    public CoreSound(double rate, double fps)
    {
        inRate = rate;
        this.fps = fps;
        th = new Thread(Run) { IsBackground = true, Name = "core sound", Priority = ThreadPriority.Highest };
        th.Start();
        if (!ready.Wait(3000)) { stop = true; throw new Exception("the sound device did not answer"); }
        if (openError != null) throw new Exception(openError);
        watch = new Thread(Watch) { IsBackground = true, Name = "core sound device" };
        watch.Start();
    }

    public void Write(IntPtr data, int frames)
    {
        if (LatencyProbe.On) LatencyProbe.Made(Loud(data, frames));
        q.Write(data, frames, Volume);
    }

    static unsafe bool Loud(IntPtr data, int frames)
    {
        short* s = (short*)data;
        for (int i = 0; i < frames * 2; i++) if (s[i] > 500 || s[i] < -500) return true;
        return false;
    }
    // Core thread: the game's speed against its own rate (1: its own rate). Passed on only when it changes.
    double skewNow = 1;
    public void SetSkew(double s)
    {
        if (s == skewNow || q == null) return;
        skewNow = s;
        q.SetSkew(s);
    }
    public void Pause() { pauseReq = true; SetEvent(ev); }
    public void Restart() { pauseReq = false; SetEvent(ev); }

    public void Dispose()
    {
        stop = true;
        quit.Set();
        watch?.Join(2000);
        SetEvent(ev);
        th.Join(2000);
        if (q != null)
            Log.Write($"core sound: {(latN > 0 ? latSum / latN : 0):0} ms of sound waiting on average (queue target {q.TargetMs:0} ms" +
                      $"{(q.LowMs < double.MaxValue ? $", closest call {q.LowMs:0.0} ms to spare" : "")}){(streamMs > 0 ? $", plus {streamMs:0.0} ms in the device" : "")}; " +
                      $"{q.Gaps} gaps{(q.Dropped > 0 ? $", {q.Dropped * 1000.0 / inRate:0} ms dropped" : "")}");
    }

    void Run()
    {
        int com = CoInitializeEx(IntPtr.Zero, 0);   // COINIT_MULTITHREADED
        uint task = 0;
        IntPtr mm = AvSetMmThreadCharacteristicsW("Pro Audio", ref task);
        try
        {
            ev = CreateEventW(IntPtr.Zero, false, false, IntPtr.Zero);
            Guid clsid = CLSID_MMDeviceEnumerator, iid = IID_IMMDeviceEnumerator;
            IntPtr e;
            int hr = CoCreateInstance(ref clsid, IntPtr.Zero, 0x17, ref iid, out e);
            en = e;
            try { Check(hr, "the device list"); Open(); }
            catch (Exception x) { openError = x.Message; Close(); ready.Set(); return; }
            ready.Set();
            bool running = true;
            long nextTry = 0;
            while (!stop)
            {
                if (ac == IntPtr.Zero)
                {
                    if (Environment.TickCount64 >= nextTry)
                    {
                        try { Open(); running = true; Log.Write("core sound: on the device again"); }
                        catch { Close(); nextTry = Environment.TickCount64 + 1000; }
                    }
                    if (ac == IntPtr.Zero) { WaitForSingleObject(ev, 200); continue; }
                }
                WaitForSingleObject(ev, 200);
                if (stop) break;
                if (reopen) { reopen = false; Log.Write("core sound: the default device changed"); Close(); continue; }
                if (pauseReq == running)
                {
                    if (pauseReq) { AStop(ac); running = false; }
                    else { AStart(ac); running = true; q.Restart(); }
                }
                if (!running) continue;
                uint pad;
                hr = GetCurrentPadding(ac, &pad);
                if (hr < 0) { Lost(hr); continue; }
                int n = devTarget - (int)pad;
                if (n <= 0) continue;
                IntPtr buf;
                hr = GetBuffer(rc, (uint)n, &buf);
                if (hr < 0) { Lost(hr); continue; }
                int gapsBefore = q.Gaps;
                bool sound = q.Read((float*)buf, n, ch);
                if (q.LoudAt >= 0) LatencyProbe.Out((pad + q.LoudAt) * 1000.0 / outRate);   // tests
                if (q.Gaps != gapsBefore && q.Gaps <= 10) Log.Write($"core sound: ran dry {q.Seconds:0.0} s in; the queue keeps {q.TargetMs:0} ms from now on");
                ReleaseBuffer(rc, (uint)n, sound ? 0u : 2u);   // 2: AUDCLNT_BUFFERFLAGS_SILENT
                // Waiting right now: the queue after this read plus the device after this write (the part just
                // moved from one to the other counted once).
                if (sound) { latSum += q.FillMs + pad * 1000.0 / outRate; latN++; }
            }
        }
        catch (Exception x) { Log.Write("core sound: " + x.Message); }
        finally
        {
            Close();
            Release(ref en);
            var h = ev;
            ev = IntPtr.Zero;
            if (h != IntPtr.Zero) CloseHandle(h);
            if (mm != IntPtr.Zero) AvRevertMmThreadCharacteristics(mm);
            if (com >= 0) CoUninitialize();
            ready.Set();
        }
    }

    void Lost(int hr)
    {
        Log.Write($"core sound: the device went away (0x{hr:X8}); trying again");
        Close();
    }

    // Opens the default device at its own mix format and the shortest period it allows, and starts it.
    void Open()
    {
        IntPtr dev = IntPtr.Zero, c = IntPtr.Zero, mf = IntPtr.Zero;
        try
        {
            Check(DefaultDevice(en, &dev), "the default device");
            string? id = IdOf(dev);
            c = Activate(dev);
            Check(GetMixFormat(c, &mf), "the mix format");
            var f = (WaveFormat*)mf;
            bool isFloat = f->Bits == 32 && (f->Tag == 3 || (f->Tag == 0xFFFE && f->Extra >= 22 && f->Sub == IeeeFloat));
            if (!isFloat) throw new Exception($"the device mixes in another format ({f->Tag:X}, {f->Bits} bits)");
            int rate = (int)f->Rate, channels = f->Channels;

            uint period = 0;
            bool small = false, tried = false;
            Guid i3 = IID_IAudioClient3;
            IntPtr c3;
            if (QI(c, &i3, &c3) >= 0)
            {
                uint def, fund, min, max;
                if (GetSharedModeEnginePeriod(c3, mf, &def, &fund, &min, &max) >= 0)
                {
                    tried = true;
                    period = min;
                    int hr = InitializeShared(c3, 0x40000, min, mf);   // AUDCLNT_STREAMFLAGS_EVENTCALLBACK
                    if (hr < 0)
                    {
                        // Another program holds the engine at its own period: use that one.
                        IntPtr cf; uint cur;
                        if (GetCurrentSharedModeEnginePeriod(c3, &cf, &cur) >= 0)
                        {
                            Marshal.FreeCoTaskMem(cf);
                            period = cur;
                            hr = InitializeShared(c3, 0x40000, cur, mf);
                        }
                    }
                    small = hr >= 0;
                }
                Release(ref c3);
            }
            if (!small)
            {
                if (tried) { Release(ref c); c = Activate(dev); }
                Check(Initialize(c, 0, 0x40000, 0, 0, mf), "starting the stream");
                long dp, mp;
                Check(GetDevicePeriod(c, &dp, &mp), "the device period");
                period = (uint)(dp * rate / 10_000_000);
            }
            uint size;
            long lat;
            Check(GetBufferSize(c, &size), "the buffer size");
            if (GetStreamLatency(c, &lat) < 0) lat = 0;
            if (ev == IntPtr.Zero) throw new Exception("no event");
            Check(SetEventHandle(c, ev), "the device's wake-up");
            Guid irc = IID_IAudioRenderClient;
            IntPtr r;
            Check(GetService(c, &irc, &r), "the render client");

            ac = c; c = IntPtr.Zero; rc = r;
            outRate = rate; ch = channels;
            // Kept in the device: one period (what it takes at its next tick, refilled as soon as it wakes us) and a
            // couple of ms to spare.
            devTarget = (int)Math.Min(size, period + Math.Max(period / 5, (uint)(rate * 2 / 1000)));
            periodMs = period * 1000.0 / rate;
            streamMs = lat / 10_000.0;
            // What the device takes each wake-up: one period.
            if (q == null) q = new SoundQueue(inRate, rate, 1000.0 / fps, periodMs);
            else q.SetOutRate(rate, periodMs);
            IntPtr b;
            if (GetBuffer(rc, (uint)devTarget, &b) >= 0) ReleaseBuffer(rc, (uint)devTarget, 2);   // silence to start
            Check(AStart(ac), "playing");
            devId = id;
            Log.Write($"core sound: low-latency output, {rate} Hz, {channels} channels, period {periodMs:0.0} ms" +
                      $"{(small ? "" : " (the device's usual period)")}, {devTarget * 1000.0 / rate:0.0} ms kept in the device{(streamMs > 0 ? $", device delay {streamMs:0.0} ms" : "")}");
        }
        finally
        {
            Release(ref c);
            Release(ref dev);
            if (mf != IntPtr.Zero) Marshal.FreeCoTaskMem(mf);
        }
    }

    void Close()
    {
        devId = null;
        if (ac != IntPtr.Zero) AStop(ac);
        Release(ref rc);
        Release(ref ac);
    }

    // Follows the default device when it changes, as waveOut did.
    void Watch()
    {
        int com = CoInitializeEx(IntPtr.Zero, 0);
        Guid clsid = CLSID_MMDeviceEnumerator, iid = IID_IMMDeviceEnumerator;
        if (CoCreateInstance(ref clsid, IntPtr.Zero, 0x17, ref iid, out IntPtr e) < 0) { if (com >= 0) CoUninitialize(); return; }
        try
        {
            while (!quit.Wait(1000))
            {
                string? cur = devId;
                if (cur == null) continue;
                IntPtr d;
                if (DefaultDevice(e, &d) < 0) continue;
                string? now = IdOf(d);
                Release(ref d);
                if (now != null && now != cur) { reopen = true; SetEvent(ev); }
            }
        }
        catch (Exception caught) { Log.Debug("game sound: device watch", caught); }
        finally { Release(ref e); if (com >= 0) CoUninitialize(); }
    }

    // ---- COM by hand (vtable slots), so nothing depends on the runtime's COM wrappers ----
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    struct WaveFormat { public ushort Tag, Channels; public uint Rate, BytesPerSec; public ushort Align, Bits, Extra, Valid; public uint Mask; public Guid Sub; }

    static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    static readonly Guid IID_IMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    static readonly Guid IID_IAudioClient3 = new("7ED4EE07-8E67-4CD4-8C1A-2B7A5987AD42");
    static readonly Guid IID_IAudioRenderClient = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
    static readonly Guid IeeeFloat = new("00000003-0000-0010-8000-00AA00389B71");

    static void** VT(IntPtr p) => *(void***)p;
    static void Check(int hr, string what) { if (hr < 0) throw new Exception($"{what} failed (0x{hr:X8})"); }
    static void Release(ref IntPtr p)
    {
        if (p == IntPtr.Zero) return;
        ((delegate* unmanaged[Stdcall]<IntPtr, uint>)VT(p)[2])(p);
        p = IntPtr.Zero;
    }
    static int QI(IntPtr p, Guid* iid, IntPtr* o) => ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)VT(p)[0])(p, iid, o);
    static int DefaultDevice(IntPtr en, IntPtr* d) => ((delegate* unmanaged[Stdcall]<IntPtr, int, int, IntPtr*, int>)VT(en)[4])(en, 0, 0, d);   // render, console
    static string? IdOf(IntPtr dev)
    {
        IntPtr s;
        if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)VT(dev)[5])(dev, &s) < 0) return null;
        string? id = Marshal.PtrToStringUni(s);
        Marshal.FreeCoTaskMem(s);
        return id;
    }
    static IntPtr Activate(IntPtr dev)
    {
        Guid iid = IID_IAudioClient;
        IntPtr c;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint, IntPtr, IntPtr*, int>)VT(dev)[3])(dev, &iid, 0x17, IntPtr.Zero, &c), "opening the device");
        return c;
    }
    static int Initialize(IntPtr c, int mode, uint flags, long buffer, long period, IntPtr fmt) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, int, uint, long, long, IntPtr, IntPtr, int>)VT(c)[3])(c, mode, flags, buffer, period, fmt, IntPtr.Zero);
    static int GetBufferSize(IntPtr c, uint* n) => ((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)VT(c)[4])(c, n);
    static int GetStreamLatency(IntPtr c, long* l) => ((delegate* unmanaged[Stdcall]<IntPtr, long*, int>)VT(c)[5])(c, l);
    static int GetCurrentPadding(IntPtr c, uint* n) => ((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)VT(c)[6])(c, n);
    static int GetMixFormat(IntPtr c, IntPtr* f) => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)VT(c)[8])(c, f);
    static int GetDevicePeriod(IntPtr c, long* d, long* m) => ((delegate* unmanaged[Stdcall]<IntPtr, long*, long*, int>)VT(c)[9])(c, d, m);
    static int AStart(IntPtr c) => ((delegate* unmanaged[Stdcall]<IntPtr, int>)VT(c)[10])(c);
    static int AStop(IntPtr c) => ((delegate* unmanaged[Stdcall]<IntPtr, int>)VT(c)[11])(c);
    static int SetEventHandle(IntPtr c, IntPtr h) => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)VT(c)[13])(c, h);
    static int GetService(IntPtr c, Guid* iid, IntPtr* o) => ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)VT(c)[14])(c, iid, o);
    static int GetSharedModeEnginePeriod(IntPtr c, IntPtr fmt, uint* def, uint* fund, uint* min, uint* max) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint*, uint*, uint*, uint*, int>)VT(c)[18])(c, fmt, def, fund, min, max);
    static int GetCurrentSharedModeEnginePeriod(IntPtr c, IntPtr* fmt, uint* period) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, uint*, int>)VT(c)[19])(c, fmt, period);
    static int InitializeShared(IntPtr c, uint flags, uint period, IntPtr fmt) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr, IntPtr, int>)VT(c)[20])(c, flags, period, fmt, IntPtr.Zero);
    static int GetBuffer(IntPtr r, uint n, IntPtr* data) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)VT(r)[3])(r, n, data);
    static int ReleaseBuffer(IntPtr r, uint n, uint flags) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, int>)VT(r)[4])(r, n, flags);

    [DllImport("ole32.dll")] static extern int CoInitializeEx(IntPtr reserved, uint coinit);
    [DllImport("ole32.dll")] static extern void CoUninitialize();
    [DllImport("ole32.dll")] static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint ctx, ref Guid iid, out IntPtr ppv);
    [DllImport("kernel32.dll")] static extern IntPtr CreateEventW(IntPtr attrs, bool manual, bool initial, IntPtr name);
    [DllImport("kernel32.dll")] static extern bool SetEvent(IntPtr h);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("avrt.dll", CharSet = CharSet.Unicode)] static extern IntPtr AvSetMmThreadCharacteristicsW(string task, ref uint index);
    [DllImport("avrt.dll")] static extern bool AvRevertMmThreadCharacteristics(IntPtr h);
}
