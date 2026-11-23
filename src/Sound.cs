#nullable disable
// UI sounds through one long-lived waveOut stream that we mix into ourselves.
//
// Why: the old code called winmm PlaySound(ASYNC) per sound. Every call stops the previous clip, closes
// the audio stream and opens a new one. Mashing buttons (or the 85 ms pad repeat) did that many times a
// second, which can wedge Bluetooth audio drivers: all system sound stops until a reconnect.
// Now a press only adds samples to an already open stream. The stream is opened once when a sound is
// needed and closed again after IdleCloseMs of silence, so it doesn't hold the device (or keep the PC
// awake) while the launcher sits idle or a game runs.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ionea;

public static unsafe class SoundMixer
{
    [StructLayout(LayoutKind.Sequential)] struct WAVEFORMATEX { public ushort wFormatTag, nChannels; public uint nSamplesPerSec, nAvgBytesPerSec; public ushort nBlockAlign, wBitsPerSample, cbSize; }
    [StructLayout(LayoutKind.Sequential)] struct WAVEHDR { public IntPtr lpData; public uint dwBufferLength, dwBytesRecorded; public IntPtr dwUser; public uint dwFlags, dwLoops; public IntPtr lpNext, reserved; }
    [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr h, uint dev, ref WAVEFORMATEX fmt, IntPtr cb, IntPtr inst, uint flags);
    [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr h, IntPtr hdr, uint size);
    [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr h, IntPtr hdr, uint size);
    [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr h, IntPtr hdr, uint size);
    [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr h);
    [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr h);

    public const int Rate = 44100;
    const int BufFrames = 441;          // 10 ms per buffer
    const int StartBufs = 5;            // ~50 ms queued to start with
    const int MaxBufs = 9;              // grows up to ~90 ms if the device ever runs dry (see Underruns)
    const int MaxVoices = 4;            // mashing never stacks more than this; the oldest fades out
    const int FadeFrames = 220;         // ~5 ms fade when a voice is cut short (a hard cut is an audible click)
    const int SameClipGapMs = 35;       // the same clip retriggers at most this often
    const int IdleCloseMs = 20000;      // close the device after this much silence
    const int RetryOpenMs = 2000;       // after a failed open (no device), don't retry sooner than this
    const int LimitKnee = 24000;        // the mix passes as is below this; above it is bent softly towards full scale
    const uint WAVE_MAPPER = 0xFFFFFFFF, CALLBACK_EVENT = 0x50000, WHDR_DONE = 1, WHDR_PREPARED = 2;

    // gain: 1024 = as recorded. Fade > 0: being cut short, that many frames left of its fade-out.
    sealed class Voice { public string Name; public short[] Data; public int Pos; public int Gain = 1024; public int Fade; }

    static readonly object gate = new();
    static readonly List<Voice> voices = new();
    static readonly Dictionary<string, long> lastStart = new();
    static readonly AutoResetEvent wake = new(false);
    static Thread thread;
    static long failedAt = -RetryOpenMs;
    static long holdUntil;              // Warm(): keep the stream open (playing silence) until this tick

    // Wireless headphones (Bluetooth sets) and some USB DACs put their link to sleep
    // when no app is playing. The first sound after that wakes it, and the first 1-3 s of audio are lost:
    // the launcher's first clicks are silent. Warm() opens the stream right away and streams silence, so
    // the link is awake by the time the intro chime and the first presses play. Silence for IdleCloseMs
    // after the hold still closes it as before.
    public static long WarmedAt { get; private set; }   // TickCount64 when Warm() was first called (0: never)
    public static long OpenedAt { get; private set; }   // TickCount64 when the stream last opened
    public static void Warm(int holdMs)
    {
        lock (gate)
        {
            if (WarmedAt == 0) WarmedAt = Environment.TickCount64;
            holdUntil = Math.Max(holdUntil, Environment.TickCount64 + holdMs);
            StartThread();
        }
        wake.Set();
    }
    static bool Holding() { lock (gate) return Environment.TickCount64 < holdUntil; }
    static void StartThread()
    {
        if (thread != null) return;
        thread = new Thread(Run) { IsBackground = true, Name = "ui-sound", Priority = ThreadPriority.AboveNormal };
        thread.Start();
    }

    // For tests/logging.
    public static int Opens { get; private set; }
    public static int Underruns { get; private set; }   // times every queued buffer had played before we refilled
    public static int Queued { get; private set; } = StartBufs;

    // Demo recording (--record-sound): every buffer sent to the device also goes to a raw 16-bit stereo
    // 44.1 kHz file, padded with silence to wall-clock time since StartRecording, so it lines up with a
    // screen recording. The start time is logged as UTC ticks. The stream stays open while recording.
    static FileStream rec;
    static System.Diagnostics.Stopwatch recClock;
    static long recFrames;
    public static void StartRecording(string path)
    {
        rec = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        recClock = System.Diagnostics.Stopwatch.StartNew();
        RecordStartUtcTicks = DateTime.UtcNow.Ticks;
        File.WriteAllText(path + ".start", RecordStartUtcTicks.ToString());
    }
    public static long RecordStartUtcTicks { get; private set; }
    static void Record(short* buf, int samples)
    {
        if (rec == null) return;
        long due = (long)(recClock.Elapsed.TotalSeconds * Rate) - BufFrames * Queued;
        if (recFrames < due) { rec.Write(new byte[(due - recFrames) * 4]); recFrames = due; }
        rec.Write(new ReadOnlySpan<byte>(buf, samples * 2));
        recFrames += samples / 2;
        rec.Flush();
    }

    /// <summary>Queue a clip (16-bit stereo interleaved at 44.1 kHz). Never blocks.</summary>
    public static void Play(string name, short[] data, double gain = 1)
    {
        if (data == null || data.Length == 0 || gain <= 0) return;
        long now = Environment.TickCount64;
        lock (gate)
        {
            if (lastStart.TryGetValue(name, out long t) && now - t < SameClipGapMs) return;
            lastStart[name] = now;
            // The same clip restarts (like PlaySound did), others overlap. A voice cut short fades out over
            // a few ms instead of stopping dead mid-wave, which is what made the click.
            foreach (var v in voices) if (v.Name == name) CutShort(v);
            var live = voices.FindAll(v => v.Fade == 0);
            if (live.Count >= MaxVoices) CutShort(live[0]);
            voices.Add(new Voice { Name = name, Data = data, Gain = (int)Math.Round(Math.Clamp(gain, 0, 1) * 1024) });
            StartThread();
        }
        wake.Set();
    }

    static void CutShort(Voice v)
    {
        if (v.Fade > 0) return;
        v.Fade = Math.Max(1, Math.Min(FadeFrames, (v.Data.Length - v.Pos) / 2));
        v.Name = null;   // never matched (or cut) again
    }

    static bool HasVoices() { lock (gate) return voices.Count > 0; }

    // The launcher is closing (Outro.cs): everything playing, and anything started after, ramps down to
    // silence over ms and stays silent, so the stream ends on silence instead of being cut mid-wave.
    static int fadeTotal, fadeLeft;     // frames; fadeTotal > 0: fading or faded out
    public static bool FadedOut { get { lock (gate) return fadeTotal > 0 && fadeLeft == 0; } }
    public static void FadeOut(int ms)
    {
        lock (gate)
        {
            if (fadeTotal > 0) return;
            fadeTotal = fadeLeft = Math.Max(1, ms * Rate / 1000);
        }
    }
    static void ApplyFade(int[] mix)
    {
        if (fadeTotal == 0) return;
        for (int k = 0; k + 1 < mix.Length; k += 2)
        {
            long g = (long)fadeLeft * 1024 / fadeTotal;
            mix[k] = (int)(mix[k] * g >> 10); mix[k + 1] = (int)(mix[k + 1] * g >> 10);
            if (fadeLeft > 0) fadeLeft--;
        }
    }

    static void Run()
    {
        var done = new AutoResetEvent(false);
        var mix = new int[BufFrames * 2];
        while (true)
        {
            // Sleep until there's something to play.
            while (!HasVoices() && !Holding()) wake.WaitOne();
            if (Environment.TickCount64 - failedAt < RetryOpenMs) { lock (gate) { voices.Clear(); holdUntil = 0; } continue; }

            var fmt = new WAVEFORMATEX { wFormatTag = 1, nChannels = 2, nSamplesPerSec = Rate, wBitsPerSample = 16, nBlockAlign = 4, nAvgBytesPerSec = Rate * 4 };
            int r = waveOutOpen(out IntPtr h, WAVE_MAPPER, ref fmt, done.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, CALLBACK_EVENT);
            if (r != 0)
            {
                failedAt = Environment.TickCount64;
                lock (gate) { voices.Clear(); holdUntil = 0; }
                try { Log.Write("ui sound: no output device (waveOut error " + r + ")"); } catch { /* logging must never stop the sound thread */ }
                continue;
            }
            Opens++;
            OpenedAt = Environment.TickCount64;
            try { Log.Write(WarmedAt > 0 && Opens == 1 ? $"ui sound: stream open {OpenedAt - WarmedAt} ms after the warm-up call" : "ui sound: stream open"); } catch { /* logging must never stop the sound thread */ }
            var hdrs = new IntPtr[MaxBufs];
            for (int i = 0; i < MaxBufs; i++)
            {
                hdrs[i] = Marshal.AllocHGlobal(sizeof(WAVEHDR));
                var hd = (WAVEHDR*)hdrs[i];
                *hd = default;
                hd->lpData = Marshal.AllocHGlobal(BufFrames * 4);
                hd->dwBufferLength = BufFrames * 4;
                hd->dwFlags = WHDR_DONE;
            }

            long lastSound = Environment.TickCount64;
            bool broken = false, primed = false;
            while (!broken)
            {
                // Every queued buffer already played: the device ran dry, which is heard as a crackle or
                // a gap. Queue one more buffer from now on (a little more latency, no more drop-outs).
                int bufs = Queued, free = 0;
                for (int i = 0; i < bufs; i++) if ((((WAVEHDR*)hdrs[i])->dwFlags & WHDR_DONE) != 0) free++;
                // Only while something is audible: the warm-up's silence running dry during startup
                // (the UI thread is busy building the window) can't be heard and shouldn't add latency.
                if (primed && free == bufs && HasVoices())
                {
                    Underruns++;
                    if (Queued < MaxBufs) Queued++;
                    try { Log.Write($"ui sound: the output ran dry, now queueing {Queued * BufFrames * 1000 / Rate} ms"); } catch { /* logging must never stop the sound thread */ }
                }
                primed = true;
                for (int i = 0; i < Queued && !broken; i++)
                {
                    var hd = (WAVEHDR*)hdrs[i];
                    if ((hd->dwFlags & WHDR_DONE) == 0) continue;
                    if ((hd->dwFlags & WHDR_PREPARED) != 0) waveOutUnprepareHeader(h, hdrs[i], (uint)sizeof(WAVEHDR));

                    bool any = Fill(mix);
                    if (any) lastSound = Environment.TickCount64;
                    var dst = (short*)hd->lpData;
                    for (int k = 0; k < mix.Length; k++) dst[k] = Limit(mix[k]);
                    Record(dst, mix.Length);

                    hd->dwFlags = 0;
                    if (waveOutPrepareHeader(h, hdrs[i], (uint)sizeof(WAVEHDR)) != 0 || waveOutWrite(h, hdrs[i], (uint)sizeof(WAVEHDR)) != 0)
                    {
                        hd->dwFlags = WHDR_DONE;   // device went away (e.g. headphones disconnected): reopen on the next sound
                        broken = true;
                    }
                }
                if (rec == null && !HasVoices() && !Holding() && Environment.TickCount64 - lastSound > IdleCloseMs) break;
                done.WaitOne(50);
            }

            waveOutReset(h);
            for (int i = 0; i < MaxBufs; i++)
            {
                var hd = (WAVEHDR*)hdrs[i];
                if ((hd->dwFlags & WHDR_PREPARED) != 0) waveOutUnprepareHeader(h, hdrs[i], (uint)sizeof(WAVEHDR));
                Marshal.FreeHGlobal(hd->lpData);
                Marshal.FreeHGlobal(hdrs[i]);
            }
            waveOutClose(h);
            if (broken) { lock (gate) voices.Clear(); try { Log.Write("ui sound: output device lost, will reopen"); } catch { /* logging must never stop the sound thread */ } }
        }
    }

    // Mix the active voices into one buffer (silence when none). Returns whether anything played.
    static bool Fill(int[] mix)
    {
        Array.Clear(mix);
        lock (gate)
        {
            if (voices.Count == 0) { if (fadeTotal > 0) fadeLeft = Math.Max(0, fadeLeft - mix.Length / 2); return false; }
            for (int v = voices.Count - 1; v >= 0; v--)
            {
                var vo = voices[v];
                int n = Math.Min(mix.Length, vo.Data.Length - vo.Pos);
                if (vo.Fade > 0)
                {
                    // fading out after being cut short: a straight ramp to silence, then gone
                    n = Math.Min(n, vo.Fade * 2);
                    for (int k = 0; k < n; k += 2)
                    {
                        long g = (long)vo.Gain * vo.Fade / FadeFrames;
                        mix[k] += (int)(vo.Data[vo.Pos + k] * g >> 10);
                        mix[k + 1] += (int)(vo.Data[vo.Pos + k + 1] * g >> 10);
                        vo.Fade--;
                    }
                    vo.Pos += n;
                    if (vo.Fade <= 0 || vo.Pos >= vo.Data.Length) voices.RemoveAt(v);
                    continue;
                }
                if (vo.Gain >= 1024) for (int k = 0; k < n; k++) mix[k] += vo.Data[vo.Pos + k];
                else for (int k = 0; k < n; k++) mix[k] += vo.Data[vo.Pos + k] * vo.Gain >> 10;
                vo.Pos += n;
                if (vo.Pos >= vo.Data.Length) voices.RemoveAt(v);
            }
            ApplyFade(mix);
            return true;
        }
    }

    // Overlapping clips can add up past full scale. Clamping there squares the wave off (a harsh crackle);
    // instead everything above the knee is bent smoothly towards full scale, so it never hard-clips.
    internal static short Limit(int x)
    {
        int a = Math.Abs(x);
        if (a <= LimitKnee) return (short)x;
        const double room = short.MaxValue - LimitKnee;
        double y = LimitKnee + room * Math.Tanh((a - LimitKnee) / room);
        return (short)(x < 0 ? -y : y);
    }

    /// <summary>Read a PCM WAV into 16-bit stereo 44.1 kHz samples (mono is doubled). Other formats: null.</summary>
    public static short[] Decode(byte[] wav)
    {
        if (wav.Length < 44 || wav[0] != 'R' || wav[1] != 'I' || wav[8] != 'W') return null;
        int ch = 0, bits = 0, rate = 0, fmtTag = 0;
        int p = 12;
        while (p + 8 <= wav.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(wav, p, 4);
            int len = BitConverter.ToInt32(wav, p + 4);
            int body = p + 8;
            if (len < 0 || body + len > wav.Length) len = wav.Length - body;
            if (id == "fmt ")
            {
                fmtTag = BitConverter.ToUInt16(wav, body); ch = BitConverter.ToUInt16(wav, body + 2);
                rate = BitConverter.ToInt32(wav, body + 4); bits = BitConverter.ToUInt16(wav, body + 14);
            }
            else if (id == "data")
            {
                if (fmtTag != 1 || bits != 16 || rate != Rate || (ch != 1 && ch != 2)) return null;
                int samples = len / 2;
                var raw = new short[samples];
                Buffer.BlockCopy(wav, body, raw, 0, samples * 2);
                if (ch == 2) return raw.Length % 2 == 0 ? raw : raw[..^1];
                var st = new short[samples * 2];
                for (int i = 0; i < samples; i++) st[i * 2] = st[i * 2 + 1] = raw[i];
                return st;
            }
            p = body + len + (len & 1);
        }
        return null;
    }
}
