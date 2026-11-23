// A hosted game's picture drawn straight to the screen.
// WPF shows a change about two of its frames after it was handed over: 30 ms on a 164 Hz screen, measured with a
// pixel watcher (a plain WPF window is just as slow; a plain Win32 window shows it in about 4 ms). So the core's
// picture also goes to a child window of the launcher's, drawn with Direct2D the moment the core makes a frame
// (on a thread of its own, so the core never waits on it).
// The child sits exactly over the picture WPF draws, which is still kept up to date under it. CoreSession
// shows the child only while nothing of the launcher's is over the game (CoreSession.Steer), so what is seen
// stays the same: the same picture, scaled the same way (as the picture filter says, PictureFilter.cs, with its pattern), with the brightness
// veil drawn over it. Anything that fails here leaves the picture to WPF, as before.
// Mouse input goes through the child to the launcher's window (HTTRANSPARENT), so the pointer and touch work as before.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ionea;

public sealed unsafe class DirectPicture : IDisposable
{
    public static bool Allowed = true;   // tests: "directpicture off" leaves every picture to WPF

    // Why the picture can't go straight to the screen at all (null: it can, when nothing covers it). Streaming
    // mode leaves it to WPF: window capture (Discord, OBS) can show a Direct2D child window black or frozen.
    public static string? Off(bool streaming) => !Allowed ? "turned off" : streaming ? "streaming mode" : null;

    public readonly IntPtr Hwnd;
    public bool Shown { get; private set; }
    public bool Broken { get; private set; }   // Direct2D failed: WPF draws the picture from now on
    public volatile bool Live;                   // shown and drawn: the core thread draws each new frame
    public volatile float Veil;                  // the brightness veil's opacity (MainWindow.Dim)
    public volatile int Pattern;                 // the picture filter's pattern over it: 0 none, 1 CRT, 2 LCD grid (PictureFilter.cs)
    public Action? Repaint;                      // the window needs its picture again (WM_PAINT)
    public long Frames;                          // frames drawn here
    public double Slowest;                       // the longest one took this long (ms)
    public int Slow;                             // frames that took over 4 ms (the sound queue has about that to spare)

    readonly object gate = new();
    int x, y, cw, ch;                            // the child's place and size, in device pixels
    IntPtr factory, target, bitmap, brush;
    IntPtr tile, tileBrush; string? tileKey;     // the filter's pattern: one game pixel's tile, tiled over the picture
    int targetW, targetH, bitmapW, bitmapH;
    uint[] pixels = Array.Empty<uint>();
    uint[]? table; int tableFmt = -1;
    int failures;

    public DirectPicture(IntPtr parent)
    {
        Register();
        // The launcher's window already clips its children; make sure, so WPF never paints over this one.
        SetWindowLongPtrW(parent, GWL_STYLE, (IntPtr)(GetWindowLongPtrW(parent, GWL_STYLE).ToInt64() | WS_CLIPCHILDREN));
        Hwnd = CreateWindowExW(WS_EX_NOPARENTNOTIFY, ClassName, "", WS_CHILD | WS_CLIPSIBLINGS, 0, 0, 1, 1, parent, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
        if (Hwnd == IntPtr.Zero) { Broken = true; Log.Write($"core picture: no child window ({Marshal.GetLastWin32Error()}), WPF draws it"); return; }
        windows[Hwnd] = this;
        wake = new AutoResetEvent(false);
        drawer = new Thread(DrawLoop) { IsBackground = true, Name = "Ionea core picture", Priority = ThreadPriority.AboveNormal };
        drawer.Start();
    }

    // The core thread: the frame it just made, copied, then drawn on the picture's own thread. The core goes on at
    // once (drawing it there took about 1 ms, taken from the few ms the sound queue has to spare).
    // probe (tests): stamp when it was drawn (LatencyProbe).
    public void Submit(byte[] frame, int fw, int fh, int fmt, bool smooth, bool probe)
    {
        if (Broken || fw <= 0 || fh <= 0) return;
        int n = fw * fh * (fmt == 1 ? 4 : 2);
        if (frame.Length < n) return;
        lock (pendGate)
        {
            if (pending.Length < n) pending = new byte[n];
            Buffer.BlockCopy(frame, 0, pending, 0, n);
            pw = fw; ph = fh; pf = fmt; ps = smooth; pprobe |= probe; waiting = true;
        }
        wake?.Set();
    }

    readonly object pendGate = new();
    byte[] pending = Array.Empty<byte>(), drawing = Array.Empty<byte>();
    int pw, ph, pf; bool ps, pprobe, waiting;
    readonly AutoResetEvent? wake;   // null when Broken (no child window)
    readonly Thread? drawer;
    volatile bool stopping;

    void DrawLoop()
    {
        while (true)
        {
            wake!.WaitOne();   // the drawing thread exists only with it
            if (stopping) return;
            int w, h, f; bool sm, probe;
            lock (pendGate)
            {
                if (!waiting) continue;
                (pending, drawing) = (drawing, pending);
                w = pw; h = ph; f = pf; sm = ps; probe = pprobe; pprobe = false; waiting = false;
            }
            if (Live && Draw(drawing, w, h, f, sm) && probe) LatencyProbe.Drawn();
        }
    }

    // UI thread: put the child over this part of the window (device pixels) and show it. True when it moved,
    // changed size or was hidden: the caller draws the picture again at once.
    public bool Show(int left, int top, int width, int height)
    {
        if (Broken) return false;
        if (Shown && left == x && top == y && width == cw && height == ch) return false;
        lock (gate) { x = left; y = top; cw = width; ch = height; }
        SetWindowPos(Hwnd, IntPtr.Zero, left, top, width, height, SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_SHOWWINDOW);
        Shown = true;
        return true;
    }

    // UI thread: WPF's picture shows again.
    public void Hide()
    {
        Live = false;
        if (!Shown) return;
        Shown = false;
        SetWindowPos(Hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_HIDEWINDOW);
    }

    // The core thread, while the game loads: Direct2D and the drawing surface made now, not on the first frame
    // drawn here (that takes long enough to hold the core up, and its sound runs dry).
    public void Prepare()
    {
        if (Broken) return;
        lock (gate)
        {
            if (cw <= 0) { cw = ch = 1; }
            try { Ready(0, 0); } catch (Exception e) { Broken = true; Log.Write("core picture: Direct2D failed (" + e.Message + "), WPF draws it"); }
        }
    }

    // The picture's thread or the UI thread: draw one frame of the core's (libretro pixel format: 0 = 0RGB1555,
    // 1 = XRGB8888, 2 = RGB565).
    public bool Draw(byte[] frame, int fw, int fh, int fmt, bool smooth)
    {
        if (Broken || fw <= 0 || fh <= 0 || frame.Length < fw * fh * (fmt == 1 ? 4 : 2)) return false;
        bool taken = false;
        try
        {
            Monitor.Enter(gate, ref taken);
            if (cw <= 0 || ch <= 0) return false;
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                if (!Ready(fw, fh)) return false;
                fixed (byte* src = frame)
                {
                    int hr;
                    var all = new RectU { R = (uint)fw, B = (uint)fh };
                    if (fmt == 1) hr = Call<nint, nint, uint, int>(bitmap, 10, (nint)(&all), (nint)src, (uint)(fw * 4));   // CopyFromMemory
                    else
                    {
                        Expand(src, fw * fh, fmt);
                        fixed (uint* p = pixels) hr = Call<nint, nint, uint, int>(bitmap, 10, (nint)(&all), (nint)p, (uint)(fw * 4));
                    }
                    if (hr < 0) return Fail("picture copy", hr);
                }
                var dest = new RectF { R = cw, B = ch };
                CallV(target, 48);                                                               // BeginDraw
                CallV<nint, nint, float, int, nint>(target, 26, bitmap, (nint)(&dest), 1f, smooth ? 1 : 0, 0);   // DrawBitmap (1 linear, 0 nearest)
                IntPtr over = Overlay(fw, fh);
                if (over != IntPtr.Zero) CallV<nint, nint>(target, 17, (nint)(&dest), over);    // FillRectangle: the filter's pattern
                float veil = Veil;
                if (veil > 0)
                {
                    CallV<float>(brush, 4, veil);                                                // SetOpacity
                    CallV<nint, nint>(target, 17, (nint)(&dest), brush);                             // FillRectangle
                }
                int end = Call<nint, nint, int>(target, 49, 0, 0);       // EndDraw
                if (end == D2DERR_RECREATE_TARGET) { ReleaseTarget(); return false; }            // the GPU was reset: a new one next frame
                if (end < 0) return Fail("drawing", end);
                failures = 0;
                Frames++;
                double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                if (ms > Slowest) Slowest = ms;
                if (ms > 4) Slow++;
                return true;
            }
            catch (Exception e) { Broken = true; Log.Write("core picture: Direct2D failed (" + e.Message + "), WPF draws it"); return false; }
        }
        finally { if (taken) Monitor.Exit(gate); }
    }

    bool Ready(int fw, int fh)
    {
        if (factory == IntPtr.Zero)
        {
            var iid = new Guid("06152247-6f50-465a-9245-118bfd3b6007");   // ID2D1Factory
            int hr = D2D1CreateFactory(1, ref iid, IntPtr.Zero, out factory);   // multi-threaded: drawn from the core and the UI thread
            if (hr < 0) { factory = IntPtr.Zero; Broken = true; Log.Write($"core picture: no Direct2D (0x{hr:X8}), WPF draws it"); return false; }
        }
        if (target == IntPtr.Zero)
        {
            var rp = new RenderTargetProps { PixelFormat = DXGI_FORMAT_B8G8R8A8_UNORM, AlphaMode = D2D1_ALPHA_MODE_IGNORE, DpiX = 96, DpiY = 96 };
            var hp = new HwndTargetProps { Hwnd = Hwnd, Width = (uint)cw, Height = (uint)ch, PresentOptions = D2D1_PRESENT_OPTIONS_IMMEDIATELY };   // never waits for the screen
            IntPtr t;
            int hr = Call<nint, nint, nint, int>(factory, 14, (nint)(&rp), (nint)(&hp), (nint)(&t));   // CreateHwndRenderTarget
            if (hr < 0) return Fail("no drawing surface", hr);
            target = t; targetW = cw; targetH = ch;
            var black = new ColorF { A = 1 };
            IntPtr b;
            hr = Call<nint, nint, nint, int>(target, 8, (nint)(&black), 0, (nint)(&b));   // CreateSolidColorBrush
            if (hr < 0) { ReleaseTarget(); return Fail("no brush", hr); }
            brush = b;
        }
        else if (targetW != cw || targetH != ch)
        {
            var size = new SizeU { W = (uint)cw, H = (uint)ch };
            int hr = Call<nint, int>(target, 58, (nint)(&size));   // Resize
            if (hr < 0) { ReleaseTarget(); return Fail("resize", hr); }
            targetW = cw; targetH = ch;
        }
        if (fw > 0 && (bitmap == IntPtr.Zero || bitmapW != fw || bitmapH != fh))
        {
            Release(ref bitmap);
            var bp = new BitmapProps { PixelFormat = DXGI_FORMAT_B8G8R8A8_UNORM, AlphaMode = D2D1_ALPHA_MODE_IGNORE, DpiX = 96, DpiY = 96 };
            IntPtr bm;
            int hr = Call<SizeU, nint, uint, nint, nint, int>(target, 4, new SizeU { W = (uint)fw, H = (uint)fh }, 0, 0, (nint)(&bp), (nint)(&bm));   // CreateBitmap
            if (hr < 0) return Fail("no picture surface", hr);
            bitmap = bm; bitmapW = fw; bitmapH = fh;
        }
        return true;
    }

    // The picture filter's pattern (PictureFilter.cs) as a brush: one game pixel's tile, made at its size on the
    // screen, repeated over the picture by the GPU. Made again only when the pattern or a size changes.
    IntPtr Overlay(int fw, int fh)
    {
        int pat = Pattern;
        string key = $"{pat} {cw} {ch} {fw} {fh}";
        if (key == tileKey) return tileBrush;
        Release(ref tileBrush); Release(ref tile);
        tileKey = key;
        double sx = (double)cw / fw, sy = (double)ch / fh;
        var px = PictureFilters.Tile(pat, sx, sy, out int tw, out int th);
        if (px == null) return IntPtr.Zero;
        var bp = new BitmapProps { PixelFormat = DXGI_FORMAT_B8G8R8A8_UNORM, AlphaMode = D2D1_ALPHA_MODE_PREMULTIPLIED, DpiX = 96, DpiY = 96 };
        IntPtr bm, br;
        int hr;
        fixed (uint* p = px) hr = Call<SizeU, nint, uint, nint, nint, int>(target, 4, new SizeU { W = (uint)tw, H = (uint)th }, (nint)p, (uint)(tw * 4), (nint)(&bp), (nint)(&bm));   // CreateBitmap
        if (hr < 0) { Log.Write($"core picture: filter pattern left out (0x{hr:X8})"); return IntPtr.Zero; }
        tile = bm;
        var bb = new BitmapBrushProps { ExtendX = D2D1_EXTEND_MODE_WRAP, ExtendY = D2D1_EXTEND_MODE_WRAP, Interpolation = 1 };
        var props = new BrushProps { Opacity = 1, M11 = (float)(sx / tw), M22 = (float)(sy / th) };   // one tile to one game pixel
        hr = Call<nint, nint, nint, nint, int>(target, 7, tile, (nint)(&bb), (nint)(&props), (nint)(&br));   // CreateBitmapBrush
        if (hr < 0) { Release(ref tile); Log.Write($"core picture: filter pattern left out (0x{hr:X8})"); return IntPtr.Zero; }
        tileBrush = br;
        return tileBrush;
    }

    // 16-bit pixels to 32-bit, through a table (the same expansion as WPF's: each part's top bits repeated).
    void Expand(byte* src, int n, int fmt)
    {
        if (table == null || tableFmt != fmt)
        {
            table = new uint[65536];
            for (int v = 0; v < 65536; v++)
            {
                int r, g, b;
                if (fmt == 2) { r = (v >> 11) & 31; g = (v >> 5) & 63; b = v & 31; g = (g << 2) | (g >> 4); }
                else { r = (v >> 10) & 31; g = (v >> 5) & 31; b = v & 31; g = (g << 3) | (g >> 2); }
                r = (r << 3) | (r >> 2); b = (b << 3) | (b >> 2);
                table[v] = 0xFF000000u | (uint)(r << 16) | (uint)(g << 8) | (uint)b;
            }
            tableFmt = fmt;
        }
        if (pixels.Length < n) pixels = new uint[n];
        var s = (ushort*)src;
        fixed (uint* d = pixels) fixed (uint* t = table)
            for (int i = 0; i < n; i++) d[i] = t[s[i]];
    }

    bool Fail(string what, int hr)
    {
        if (++failures >= 30) { Broken = true; Log.Write($"core picture: Direct2D {what} failed (0x{hr:X8}), WPF draws it"); }
        else if (failures == 1) Log.Write($"core picture: Direct2D {what} failed once (0x{hr:X8})");
        return false;
    }

    void ReleaseTarget() { Release(ref tileBrush); Release(ref tile); tileKey = null; Release(ref bitmap); Release(ref brush); Release(ref target); targetW = targetH = 0; }

    // UI thread, after the core thread has stopped.
    public void Dispose()
    {
        Live = false;
        if (drawer != null) { stopping = true; wake?.Set(); drawer.Join(1000); }
        lock (gate) { ReleaseTarget(); Release(ref factory); Broken = true; }
        if (Hwnd != IntPtr.Zero) { windows.Remove(Hwnd); DestroyWindow(Hwnd); }
    }

    static void Release(ref IntPtr p) { if (p != IntPtr.Zero) { Call<uint>(p, 2); p = IntPtr.Zero; } }

    // ---- COM calls by vtable slot ----
    static void* Slot(IntPtr p, int i) => (*(void***)p)[i];
    static T Call<T>(IntPtr p, int i) where T : unmanaged => ((delegate* unmanaged[Stdcall]<IntPtr, T>)Slot(p, i))(p);
    static R Call<A, R>(IntPtr p, int i, A a) where A : unmanaged where R : unmanaged => ((delegate* unmanaged[Stdcall]<IntPtr, A, R>)Slot(p, i))(p, a);
    static R Call<A, B, R>(IntPtr p, int i, A a, B b) where A : unmanaged where B : unmanaged where R : unmanaged => ((delegate* unmanaged[Stdcall]<IntPtr, A, B, R>)Slot(p, i))(p, a, b);
    static R Call<A, B, C, R>(IntPtr p, int i, A a, B b, C c) where A : unmanaged where B : unmanaged where C : unmanaged where R : unmanaged => ((delegate* unmanaged[Stdcall]<IntPtr, A, B, C, R>)Slot(p, i))(p, a, b, c);
    static R Call<A, B, C, D, R>(IntPtr p, int i, A a, B b, C c, D d) where A : unmanaged where B : unmanaged where C : unmanaged where D : unmanaged where R : unmanaged => ((delegate* unmanaged[Stdcall]<IntPtr, A, B, C, D, R>)Slot(p, i))(p, a, b, c, d);
    static R Call<A, B, C, D, E, R>(IntPtr p, int i, A a, B b, C c, D d, E e) where A : unmanaged where B : unmanaged where C : unmanaged where D : unmanaged where E : unmanaged where R : unmanaged => ((delegate* unmanaged[Stdcall]<IntPtr, A, B, C, D, E, R>)Slot(p, i))(p, a, b, c, d, e);
    static void CallV(IntPtr p, int i) => ((delegate* unmanaged[Stdcall]<IntPtr, void>)Slot(p, i))(p);
    static void CallV<A>(IntPtr p, int i, A a) where A : unmanaged => ((delegate* unmanaged[Stdcall]<IntPtr, A, void>)Slot(p, i))(p, a);
    static void CallV<A, B>(IntPtr p, int i, A a, B b) where A : unmanaged where B : unmanaged => ((delegate* unmanaged[Stdcall]<IntPtr, A, B, void>)Slot(p, i))(p, a, b);
    static void CallV<A, B, C, D, E>(IntPtr p, int i, A a, B b, C c, D d, E e) where A : unmanaged where B : unmanaged where C : unmanaged where D : unmanaged where E : unmanaged => ((delegate* unmanaged[Stdcall]<IntPtr, A, B, C, D, E, void>)Slot(p, i))(p, a, b, c, d, e);

    [StructLayout(LayoutKind.Sequential)] struct SizeU { public uint W, H; }
    [StructLayout(LayoutKind.Sequential)] struct RectU { public uint L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct RectF { public float L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct ColorF { public float R, G, B, A; }
    [StructLayout(LayoutKind.Sequential)] struct RenderTargetProps { public int Type, PixelFormat, AlphaMode; public float DpiX, DpiY; public int Usage, MinLevel; }
    [StructLayout(LayoutKind.Sequential)] struct HwndTargetProps { public IntPtr Hwnd; public uint Width, Height; public int PresentOptions; }
    [StructLayout(LayoutKind.Sequential)] struct BitmapProps { public int PixelFormat, AlphaMode; public float DpiX, DpiY; }
    [StructLayout(LayoutKind.Sequential)] struct BitmapBrushProps { public int ExtendX, ExtendY, Interpolation; }
    [StructLayout(LayoutKind.Sequential)] struct BrushProps { public float Opacity, M11, M12, M21, M22, Dx, Dy; }
    const int DXGI_FORMAT_B8G8R8A8_UNORM = 87, D2D1_ALPHA_MODE_IGNORE = 3, D2D1_ALPHA_MODE_PREMULTIPLIED = 1, D2D1_EXTEND_MODE_WRAP = 1, D2D1_PRESENT_OPTIONS_IMMEDIATELY = 2;
    const int D2DERR_RECREATE_TARGET = unchecked((int)0x8899000C);
    [DllImport("d2d1.dll")] static extern int D2D1CreateFactory(int type, ref Guid riid, IntPtr options, out IntPtr factory);

    // ---- the child window ----
    const string ClassName = "IoneaPicture";
    static WndProcFn? proc;  // kept alive: Windows calls it
    static readonly Dictionary<IntPtr, DirectPicture> windows = new();
    delegate IntPtr WndProcFn(IntPtr h, uint msg, IntPtr wp, IntPtr lp);

    static void Register()
    {
        if (proc != null) return;
        proc = WndProc;
        var wc = new WNDCLASSEXW { cbSize = Marshal.SizeOf<WNDCLASSEXW>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc), hInstance = GetModuleHandleW(null), lpszClassName = ClassName };
        if (RegisterClassExW(ref wc) == 0 && Marshal.GetLastWin32Error() != 1410) Log.Write($"core picture: window class not registered ({Marshal.GetLastWin32Error()})");   // 1410: already there
    }

    static IntPtr WndProc(IntPtr h, uint msg, IntPtr wp, IntPtr lp)
    {
        switch (msg)
        {
            case WM_NCHITTEST: return (IntPtr)HTTRANSPARENT;   // the mouse belongs to the launcher's window under it
            case WM_ERASEBKGND: return (IntPtr)1;               // the picture covers it all
            case WM_PAINT:
                BeginPaint(h, out var ps); EndPaint(h, ref ps);
                if (windows.TryGetValue(h, out var d) && d.Shown) d.Repaint?.Invoke();
                return IntPtr.Zero;
        }
        return DefWindowProcW(h, msg, wp, lp);
    }

    const int GWL_STYLE = -16;
    const long WS_CLIPCHILDREN = 0x02000000;
    const int WS_CHILD = 0x40000000, WS_CLIPSIBLINGS = 0x04000000, WS_EX_NOPARENTNOTIFY = 0x4;
    const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40, SWP_HIDEWINDOW = 0x80, SWP_NOOWNERZORDER = 0x200;
    const uint WM_PAINT = 0x0F, WM_ERASEBKGND = 0x14, WM_NCHITTEST = 0x84;
    const int HTTRANSPARENT = -1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEXW { public int cbSize; public uint style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra; public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string lpszMenuName, lpszClassName; public IntPtr hIconSm; }
    [StructLayout(LayoutKind.Sequential)] struct PAINTSTRUCT { public IntPtr hdc; public int fErase, l, t, r, b, fRestore, fIncUpdate; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved; }
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowExW(int ex, string cls, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProcW(IntPtr h, uint msg, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int h2, uint flags);
    [DllImport("user32.dll")] static extern IntPtr BeginPaint(IntPtr h, out PAINTSTRUCT ps);
    [DllImport("user32.dll")] static extern bool EndPaint(IntPtr h, ref PAINTSTRUCT ps);
    [DllImport("user32.dll")] static extern IntPtr GetWindowLongPtrW(IntPtr h, int i);
    [DllImport("user32.dll")] static extern IntPtr SetWindowLongPtrW(IntPtr h, int i, IntPtr v);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string? name);
}
