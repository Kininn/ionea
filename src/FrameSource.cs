// Where the performance overlay's frame rate comes from.
//   FrameStats: the frame times of one game, written by whatever sees its frames and read twice a second.
//   Cores: the core thread calls Frame() for every frame it runs (CoreGame.cs).
//   WindowFrames: every other game inside Ionea (an emulator or port's window). Windows' own window capture
//     (Windows.Graphics.Capture, the API the Snipping Tool and Game Bar use) tells Ionea each time the window
//     shows a new frame. No hooking, nothing injected into the game, no admin. The picture itself is not kept:
//     the capture buffer is 1x1 pixel, so the copy is next to nothing. It counts frames the desktop shows, so a
//     game can't show more than the monitor's refresh rate here. No border, no cursor in the capture.
//   FrameLog: a port that writes a per-frame log itself (bbport's logs\<time>.frames.csv, one row per frame
//     with its time and frame time; the definition's "perf.frameLog"). Exact, also above the refresh rate.
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Ionea;

// Frame times of one game: one store and one add per frame, nothing allocated; read on the UI thread.
public sealed class FrameStats
{
    public const int N = 128;
    readonly float[] ring = new float[N];
    double last = -1;   // the last frame's time in seconds, on the source's own clock
    long count, seen;   // frames so far; Stopwatch stamp of the last one
    int head;
    public void Frame() => FrameAt(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
    public void FrameAt(double t)
    {
        double l = Volatile.Read(ref last);
        if (l >= 0 && t > l) { ring[head & (N - 1)] = (float)((t - l) * 1000); Volatile.Write(ref head, head + 1); }
        if (t > l) Volatile.Write(ref last, t);
        Volatile.Write(ref seen, Stopwatch.GetTimestamp());
        Interlocked.Increment(ref count);
    }
    public void Break() => Volatile.Write(ref last, -1);   // paused: the gap is not a frame time
    public long Count => Interlocked.Read(ref count);
    public int Head => Volatile.Read(ref head);
    public double Last => Volatile.Read(ref last);
    public float At(int i) => ring[i & (N - 1)];
    // Seconds since the last frame came (a long time when none has).
    public double Quiet { get { long s = Volatile.Read(ref seen); return s == 0 ? 1e9 : (Stopwatch.GetTimestamp() - s) / (double)Stopwatch.Frequency; } }
}

// A per-frame log a port writes while it runs, read from where Ionea last stopped (twice a second). The newest
// file matching the pattern that was written since the game started; a long one is read from near its end.
public sealed class FrameLog : IDisposable
{
    public readonly FrameStats Stats = new();
    readonly string dir, pattern, timeCol;
    readonly DateTime since;
    FileStream? fs; long pos; int tIdx = -1, polls;
    readonly StringBuilder rest = new();
    readonly byte[] buf = new byte[64 * 1024];
    double tBase = double.NaN;
    public string? File { get; private set; }

    public FrameLog(string folder, string filePattern, string time, DateTime started)
    {
        // "logs\*.frames.csv": the folder part and the file part
        string sub = Path.GetDirectoryName(filePattern) ?? "";
        dir = Path.Combine(folder, sub); pattern = Path.GetFileName(filePattern);
        timeCol = time; since = started;
    }

    public void Poll()
    {
        try
        {
            // every 2 s: a newer file (the game restarted, or an old one was the newest at first) is read instead
            if (fs != null && ++polls % 4 == 0 && Newest() is FileInfo nf && !string.Equals(nf.FullName, File, StringComparison.OrdinalIgnoreCase)) Close();
            if (fs == null && !Open()) return;
            long len = fs!.Length;   // Open made it
            if (len < pos) { pos = 0; rest.Clear(); }   // a new file under the same name
            while (pos < len)
            {
                fs.Position = pos;
                int n = fs.Read(buf, 0, (int)Math.Min(buf.Length, len - pos));
                if (n <= 0) break;
                pos += n;
                rest.Append(Encoding.ASCII.GetString(buf, 0, n));
                Lines();
            }
        }
        catch (Exception e) { Log.Write("overlay: frame log " + e.Message); Close(); }
    }

    bool Open()
    {
        var f = Newest();
        if (f == null) return false;
        fs = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
        File = f.FullName;
        // the header first, then (a long file, the overlay turned on late) only its last part
        var head = new byte[4096];
        int hn = fs.Read(head, 0, head.Length);
        string h = Encoding.ASCII.GetString(head, 0, hn);
        int nl = h.IndexOf('\n');
        if (nl < 0) { Close(); return false; }   // not even the header yet
        if (!Header(h[..nl].TrimEnd('\r'))) { Log.Write($"overlay: frame log {f.Name} has no {timeCol} column"); Close(); return false; }
        pos = Math.Max(nl + 1, fs.Length - 16 * 1024);
        if (pos > nl + 1) rest.Append('\u0001');   // starts mid-line: the first piece is dropped
        Log.Write($"overlay: frame rate from {f.Name}");
        return true;
    }

    FileInfo? Newest() => Directory.Exists(dir)
        ? new DirectoryInfo(dir).EnumerateFiles(pattern).Where(x => x.CreationTime >= since || x.LastWriteTime >= since).OrderByDescending(x => x.CreationTime).FirstOrDefault()
        : null;

    bool Header(string line)
    {
        tIdx = Array.IndexOf(line.Split(','), timeCol);
        return tIdx >= 0;
    }

    void Lines()
    {
        int start = 0;
        string s = rest.ToString();
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '\n') continue;
            var line = s.AsSpan(start, i - start).TrimEnd('\r');
            start = i + 1;
            if (line.Length == 0 || line[0] == '\u0001') continue;
            Row(line);
        }
        rest.Clear();
        if (start < s.Length) rest.Append(s, start, s.Length - start);
    }

    void Row(ReadOnlySpan<char> line)
    {
        int col = 0, at = 0;
        ReadOnlySpan<char> t = default;
        for (int i = 0; i <= line.Length && col <= tIdx; i++)
        {
            if (i < line.Length && line[i] != ',') continue;
            if (col == tIdx) t = line[at..i];
            col++; at = i + 1;
        }
        if (!double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v)) return;
        if (double.IsNaN(tBase)) tBase = v;
        Stats.FrameAt(v - tBase + 1);   // +1: FrameStats counts times above 0
    }

    void Close() { try { fs?.Dispose(); } catch { /* best-effort cleanup */ } fs = null; pos = 0; rest.Clear(); tBase = double.NaN; File = null; Stats.Break(); }
    public void Dispose() => Close();
}

// Counts the frames one window shows, with Windows.Graphics.Capture. Started and stopped on its own thread.
public sealed unsafe class WindowFrames : IDisposable
{
    public readonly FrameStats Stats = new();
    public readonly IntPtr Window;
    public string? Error { get; private set; }
    public bool Running { get; private set; }
    readonly ManualResetEventSlim stop = new(false);
    Thread thread;

    IntPtr device, d3d, item, pool, session, handler; long token;
    GCHandle self;

    public WindowFrames(IntPtr hwnd)
    {
        Window = hwnd;
        thread = new Thread(Run) { IsBackground = true, Name = "overlay frames" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    public void Dispose() { stop.Set(); }

    void Run()
    {
        try
        {
            RoInitialize(1);   // multithreaded
            Setup();
            Running = true;
            Log.Write($"overlay: counting frames of window 0x{Window.ToInt64():x}");
            stop.Wait();
        }
        catch (Exception e) { Error = e.Message; Log.Write("overlay: no frame count (" + e.Message + ")"); }
        finally { Running = false; Teardown(); }
    }

    static void Check(int hr, string what) { if (hr < 0) throw new Exception($"{what} 0x{hr:x8}"); }
    static void** V(IntPtr p) => *(void***)p;
    static int QI(IntPtr p, Guid iid, out IntPtr r) { IntPtr o; int hr = ((delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)V(p)[0])(p, &iid, &o); r = hr < 0 ? IntPtr.Zero : o; return hr; }
    static void Rel(ref IntPtr p) { if (p != IntPtr.Zero) { ((delegate* unmanaged<IntPtr, uint>)V(p)[2])(p); p = IntPtr.Zero; } }
    static void CloseWinRt(IntPtr p)   // IClosable.Close
    {
        if (p == IntPtr.Zero || QI(p, IID_IClosable, out var c) < 0) return;
        ((delegate* unmanaged<IntPtr, int>)V(c)[6])(c);
        Rel(ref c);
    }
    static IntPtr Factory(string cls, Guid iid)
    {
        Check(WindowsCreateString(cls, cls.Length, out var hs), "string");
        try { Check(RoGetActivationFactory(hs, ref iid, out var f), cls); return f; }
        finally { WindowsDeleteString(hs); }
    }

    void Setup()
    {
        // A Direct3D device for the capture (hardware, BGRA), as the WinRT device the frame pool takes.
        Check(D3D11CreateDevice(IntPtr.Zero, 1 /* HARDWARE */, IntPtr.Zero, 0x20 /* BGRA_SUPPORT */, IntPtr.Zero, 0, 7, out device, out _, out var ctx), "D3D11CreateDevice");
        Rel(ref ctx);
        Check(QI(device, IID_IDXGIDevice, out var dxgi), "IDXGIDevice");
        try { Check(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var insp), "CreateDirect3D11DeviceFromDXGIDevice"); Check(QI(insp, IID_IDirect3DDevice, out d3d), "IDirect3DDevice"); Rel(ref insp); }
        finally { Rel(ref dxgi); }

        // The window as a capture item.
        IntPtr interop = Factory("Windows.Graphics.Capture.GraphicsCaptureItem", IID_IGraphicsCaptureItemInterop);
        try
        {
            Guid iid = IID_IGraphicsCaptureItem; IntPtr it;
            Check(((delegate* unmanaged<IntPtr, IntPtr, Guid*, IntPtr*, int>)V(interop)[3])(interop, Window, &iid, &it), "CreateForWindow");
            item = it;
        }
        finally { Rel(ref interop); }

        // A free-threaded frame pool of two 1x1 buffers: frames arrive on a system thread, and almost nothing is copied.
        IntPtr statics = Factory("Windows.Graphics.Capture.Direct3D11CaptureFramePool", IID_IDirect3D11CaptureFramePoolStatics2);
        try
        {
            IntPtr p;
            Check(((delegate* unmanaged<IntPtr, IntPtr, int, int, SizeInt32, IntPtr*, int>)V(statics)[6])(statics, d3d, 87 /* B8G8R8A8 */, 2, new SizeInt32 { W = 1, H = 1 }, &p), "CreateFreeThreaded");
            pool = p;
        }
        finally { Rel(ref statics); }

        self = GCHandle.Alloc(this);
        handler = Handler.Make(self);
        long tk;
        Check(((delegate* unmanaged<IntPtr, IntPtr, long*, int>)V(pool)[8])(pool, handler, &tk), "add_FrameArrived");
        token = tk;

        IntPtr s;
        Check(((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, int>)V(pool)[10])(pool, item, &s), "CreateCaptureSession");
        session = s;
        if (QI(session, IID_IGraphicsCaptureSession2, out var s2) >= 0) { ((delegate* unmanaged<IntPtr, byte, int>)V(s2)[7])(s2, 0); Rel(ref s2); }   // no cursor
        if (QI(session, IID_IGraphicsCaptureSession3, out var s3) >= 0)   // no yellow border round the game
        {
            int hr = ((delegate* unmanaged<IntPtr, byte, int>)V(s3)[7])(s3, 0);
            if (hr < 0) Log.Write($"overlay: capture border can't be turned off (0x{hr:x8})");
            Rel(ref s3);
        }
        Check(((delegate* unmanaged<IntPtr, int>)V(session)[6])(session), "StartCapture");
    }

    // A frame came: take every waiting one, note its time, give the buffer back.
    internal void Arrived()
    {
        IntPtr p = pool;
        if (p == IntPtr.Zero) return;
        for (int i = 0; i < 4; i++)
        {
            IntPtr f;
            if (((delegate* unmanaged<IntPtr, IntPtr*, int>)V(p)[7])(p, &f) < 0 || f == IntPtr.Zero) break;
            long t;
            if (QI(f, IID_IDirect3D11CaptureFrame, out var fr) >= 0)
            {
                if (((delegate* unmanaged<IntPtr, long*, int>)V(fr)[7])(fr, &t) >= 0) Stats.FrameAt(t / 1e7);
                Rel(ref fr);
            }
            CloseWinRt(f);
            Rel(ref f);
        }
    }

    void Teardown()
    {
        try
        {
            if (pool != IntPtr.Zero && token != 0) ((delegate* unmanaged<IntPtr, long, int>)V(pool)[9])(pool, token);
            CloseWinRt(session); Rel(ref session);
            IntPtr p = pool; pool = IntPtr.Zero;
            CloseWinRt(p); Rel(ref p);
            Rel(ref item); Rel(ref d3d); Rel(ref device);
            Rel(ref handler);
        }
        catch (Exception e) { Log.Write("overlay: frame count stop " + e.Message); }
    }

    // The TypedEventHandler<Direct3D11CaptureFramePool, object> Windows calls: a small COM object by hand.
    static class Handler
    {
        static readonly Guid IID = Pinterface("pinterface({9de1c534-6ae1-11e0-84e1-18a905bcc53f};rc(Windows.Graphics.Capture.Direct3D11CaptureFramePool;{24eb6d22-1975-422e-82e7-780dbd8ddf24});cinterface(IInspectable))");
        static readonly IntPtr vtbl = MakeVtbl();
        static IntPtr MakeVtbl()
        {
            var t = (IntPtr*)NativeMemory.Alloc((nuint)(4 * sizeof(IntPtr)));
            t[0] = (IntPtr)(delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)&QueryInterface;
            t[1] = (IntPtr)(delegate* unmanaged<IntPtr, uint>)&AddRef;
            t[2] = (IntPtr)(delegate* unmanaged<IntPtr, uint>)&Release;
            t[3] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, int>)&Invoke;
            return (IntPtr)t;
        }
        // layout: vtable, ref count, GCHandle of the WindowFrames
        public static IntPtr Make(GCHandle h)
        {
            var o = (IntPtr*)NativeMemory.Alloc((nuint)(3 * sizeof(IntPtr)));
            o[0] = vtbl; o[1] = (IntPtr)1; o[2] = GCHandle.ToIntPtr(h);
            return (IntPtr)o;
        }
        [UnmanagedCallersOnly]
        static int QueryInterface(IntPtr me, Guid* iid, IntPtr* r)
        {
            if (*iid == IID || *iid == IID_IUnknown || *iid == IID_IAgileObject) { *r = me; Interlocked.Increment(ref ((long*)me)[1]); return 0; }
            *r = IntPtr.Zero; return unchecked((int)0x80004002);   // E_NOINTERFACE
        }
        [UnmanagedCallersOnly] static uint AddRef(IntPtr me) => (uint)Interlocked.Increment(ref ((long*)me)[1]);
        [UnmanagedCallersOnly]
        static uint Release(IntPtr me)
        {
            long n = Interlocked.Decrement(ref ((long*)me)[1]);
            if (n == 0) { var h = GCHandle.FromIntPtr(((IntPtr*)me)[2]); if (h.IsAllocated) h.Free(); NativeMemory.Free((void*)me); }
            return (uint)n;
        }
        [UnmanagedCallersOnly]
        static int Invoke(IntPtr me, IntPtr sender, IntPtr args)
        {
            try { if (GCHandle.FromIntPtr(((IntPtr*)me)[2]).Target is WindowFrames wf) wf.Arrived(); } catch { /* a frame callback must never throw into the capture API */ }
            return 0;
        }
    }

    // A parameterized WinRT interface's IID (the SHA-1 name-based GUID WinRT defines for a pinterface signature).
    public static Guid Pinterface(string sig)
    {
        byte[] ns = { 0x11, 0xf4, 0x7a, 0xd5, 0x7b, 0x73, 0x42, 0xc0, 0xab, 0xae, 0x87, 0x8b, 0x1e, 0x16, 0xad, 0xee };
        var data = ns.Concat(Encoding.UTF8.GetBytes(sig)).ToArray();
        var h = SHA1.HashData(data);
        int a = (h[0] << 24) | (h[1] << 16) | (h[2] << 8) | h[3];
        short b = (short)((h[4] << 8) | h[5]);
        short c = (short)((((h[6] << 8) | h[7]) & 0x0fff) | 0x5000);
        byte d = (byte)((h[8] & 0x3f) | 0x80);
        return new Guid(a, b, c, d, h[9], h[10], h[11], h[12], h[13], h[14], h[15]);
    }

    struct SizeInt32 { public int W, H; }
    static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");
    static readonly Guid IID_IAgileObject = new("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90");
    static readonly Guid IID_IClosable = new("30D5A829-7FA4-4026-83BB-D75BAE4EA99E");
    static readonly Guid IID_IDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    static readonly Guid IID_IDirect3DDevice = new("A37624AB-8D5F-4650-9D3E-9EAE3D9BC670");
    static readonly Guid IID_IGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    static readonly Guid IID_IGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    static readonly Guid IID_IDirect3D11CaptureFramePoolStatics2 = new("589B103F-6BBC-5DF5-A991-02E28B3B66D5");
    static readonly Guid IID_IDirect3D11CaptureFrame = new("FA50C623-38DA-4B32-ACF3-FA9734AD800E");
    static readonly Guid IID_IGraphicsCaptureSession2 = new("2C39AE40-7D2E-5044-804E-8B6799D4CF9E");
    static readonly Guid IID_IGraphicsCaptureSession3 = new("F2CDD966-22AE-5EA1-9596-3A289344C3BE");

    [DllImport("combase.dll")] static extern int RoInitialize(int type);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] static extern int WindowsCreateString(string s, int len, out IntPtr hs);
    [DllImport("combase.dll")] static extern int WindowsDeleteString(IntPtr hs);
    [DllImport("combase.dll")] static extern int RoGetActivationFactory(IntPtr cls, ref Guid iid, out IntPtr f);
    [DllImport("d3d11.dll")] static extern int D3D11CreateDevice(IntPtr adapter, int driver, IntPtr soft, uint flags, IntPtr levels, uint nLevels, uint sdk, out IntPtr dev, out int level, out IntPtr ctx);
    [DllImport("d3d11.dll")] static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgi, out IntPtr inspectable);
}
