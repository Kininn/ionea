using System;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Ionea;

// A copy of part of what the display shows, through DXGI desktop duplication (what screen recorders use). Unlike a
// GDI screen copy it includes games that present straight to the display (independent flip). COM by vtable index;
// everything is released before returning. Null (and why) when it can't: then the caller copies the screen.
static unsafe class DisplayCopy
{
    [DllImport("dxgi.dll")] static extern int CreateDXGIFactory1(in Guid riid, out IntPtr factory);
    [DllImport("d3d11.dll")]
    static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr levels,
        uint count, uint sdk, out IntPtr device, out int level, out IntPtr context);

    static readonly Guid IID_Factory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    static readonly Guid IID_Output1 = new("00cddea8-939b-4b83-a340-a685226666cc");
    static readonly Guid IID_Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    static IntPtr Fn(IntPtr obj, int i) => ((IntPtr*)*(IntPtr*)obj)[i];
    static void Release(IntPtr obj) { if (obj != IntPtr.Zero) ((delegate* unmanaged<IntPtr, uint>)Fn(obj, 2))(obj); }
    static int QI(IntPtr obj, Guid iid, out IntPtr res)
    {
        IntPtr r; int hr = ((delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)Fn(obj, 0))(obj, &iid, &r);
        res = hr < 0 ? IntPtr.Zero : r; return hr;
    }

    static readonly object gate = new();
    static IntPtr device, ctx, devMon;
    static void Drop() { Release(ctx); Release(device); ctx = 0; device = 0; devMon = 0; }

    // Makes the device ahead (a game window just came over the launcher), off the UI thread.
    public static void Warm(int x, int y, int wd, int ht) =>
        System.Threading.Tasks.Task.Run(() => { try { Grab(x, y, wd, ht, out _, true); } catch (Exception caught) { Log.Debug("screen copy: warm-up", caught); } });

    public static BitmapSource? Grab(int x, int y, int wd, int ht, out string? why) => Grab(x, y, wd, ht, out why, false);

    static BitmapSource? Grab(int x, int y, int wd, int ht, out string? why, bool warm)
    {
        why = null;
        IntPtr factory = 0, adapter = 0, output = 0, output1 = 0, dup = 0, res = 0, tex = 0, staging = 0;
        bool frame = false;
        lock (gate)
        try
        {
            if (CreateDXGIFactory1(IID_Factory1, out factory) < 0) { why = "no DXGI factory"; return null; }
            // The output (screen) holding the game's middle, and its adapter.
            int cx = x + wd / 2, cy = y + ht / 2; int ox = 0, oy = 0, ow = 0, oh = 0; IntPtr mon = 0;
            for (uint a = 0; output == 0; a++)
            {
                IntPtr ad;
                if (((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Fn(factory, 12))(factory, a, &ad) < 0) break;   // EnumAdapters1
                for (uint o = 0; ; o++)
                {
                    IntPtr op;
                    if (((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Fn(ad, 7))(ad, o, &op) < 0) break;   // EnumOutputs
                    byte* desc = stackalloc byte[96];   // DXGI_OUTPUT_DESC
                    ((delegate* unmanaged<IntPtr, byte*, int>)Fn(op, 7))(op, desc);   // GetDesc
                    int* rc = (int*)(desc + 64); int rot = *(int*)(desc + 84);
                    if (cx >= rc[0] && cx < rc[2] && cy >= rc[1] && cy < rc[3] && rot <= 1)
                    { output = op; adapter = ad; mon = *(IntPtr*)(desc + 88); ox = rc[0]; oy = rc[1]; ow = rc[2] - rc[0]; oh = rc[3] - rc[1]; break; }
                    Release(op);
                }
                if (output == 0) Release(ad);
            }
            if (output == 0) { why = "no screen (or a rotated one) under the game"; return null; }
            if (QI(output, IID_Output1, out output1) < 0) { why = "no IDXGIOutput1"; return null; }
            // The device is kept for that screen (making one is most of the time); a failing one is made again once.
            int hr = 0;
            for (int tries = 0; tries < 2; tries++)
            {
                if (device == 0 || devMon != mon)
                {
                    Drop();
                    if (D3D11CreateDevice(adapter, 0, 0, 0x20 /* BGRA */, 0, 0, 7, out device, out _, out ctx) < 0) { device = 0; ctx = 0; why = "no D3D11 device"; return null; }
                    devMon = mon;
                }
                hr = ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, int>)Fn(output1, 22))(output1, device, &dup);   // DuplicateOutput
                if (hr >= 0) break;
                Drop();
            }
            if (warm) { why = "warm"; return null; }
            if (hr < 0) { why = $"DuplicateOutput 0x{hr:X}"; return null; }
            // The first frame holds the whole picture; one with no present time yet may be empty: take the next.
            byte* info = stackalloc byte[64];   // DXGI_OUTDUPL_FRAME_INFO
            for (int i = 0; i < 4; i++)
            {
                hr = ((delegate* unmanaged<IntPtr, uint, byte*, IntPtr*, int>)Fn(dup, 8))(dup, 150, info, &res);   // AcquireNextFrame
                if (hr < 0) { why = $"AcquireNextFrame 0x{hr:X}"; return null; }
                frame = true;
                if (*(long*)info != 0) break;   // LastPresentTime
                Release(res); res = 0;
                ((delegate* unmanaged<IntPtr, int>)Fn(dup, 14))(dup); frame = false;   // ReleaseFrame
            }
            if (res == 0) { why = "no picture yet"; return null; }
            if (QI(res, IID_Texture2D, out tex) < 0) { why = "no texture"; return null; }
            uint* td = stackalloc uint[11];   // D3D11_TEXTURE2D_DESC
            ((delegate* unmanaged<IntPtr, uint*, void>)Fn(tex, 10))(tex, td);
            if (td[4] != 87 /* B8G8R8A8_UNORM */) { why = $"format {td[4]}"; return null; }
            // The game's part of the screen, kept inside it.
            int l = Math.Max(0, x - ox), t = Math.Max(0, y - oy), r = Math.Min(ow, x - ox + wd), b = Math.Min(oh, y - oy + ht);
            if (r - l < 16 || b - t < 16) { why = "the game is off its screen"; return null; }
            uint* sd = stackalloc uint[11] { (uint)(r - l), (uint)(b - t), 1, 1, 87, 1, 0, 3 /* STAGING */, 0, 0x20000 /* CPU read */, 0 };
            IntPtr st;
            hr = ((delegate* unmanaged<IntPtr, uint*, IntPtr, IntPtr*, int>)Fn(device, 5))(device, sd, 0, &st);   // CreateTexture2D
            if (hr < 0) { why = $"CreateTexture2D 0x{hr:X}"; return null; }
            staging = st;
            int* box = stackalloc int[6] { l, t, 0, r, b, 1 };
            ((delegate* unmanaged<IntPtr, IntPtr, uint, uint, uint, uint, IntPtr, uint, int*, void>)Fn(ctx, 46))(ctx, staging, 0, 0, 0, 0, tex, 0, box);   // CopySubresourceRegion
            byte* map = stackalloc byte[16];   // D3D11_MAPPED_SUBRESOURCE
            hr = ((delegate* unmanaged<IntPtr, IntPtr, uint, int, uint, byte*, int>)Fn(ctx, 14))(ctx, staging, 0, 1 /* READ */, 0, map);   // Map
            if (hr < 0) { why = $"Map 0x{hr:X}"; return null; }
            try
            {
                IntPtr data = *(IntPtr*)map; int pitch = *(int*)(map + 8);
                var src = BitmapSource.Create(r - l, b - t, 96, 96, PixelFormats.Bgr32, null, data, pitch * (b - t), pitch);
                src.Freeze();
                return src;
            }
            finally { ((delegate* unmanaged<IntPtr, IntPtr, uint, void>)Fn(ctx, 15))(ctx, staging, 0); }   // Unmap
        }
        catch (Exception e) { why = e.Message; return null; }
        finally
        {
            if (frame) ((delegate* unmanaged<IntPtr, int>)Fn(dup, 14))(dup);
            Release(staging); Release(tex); Release(res); Release(dup);
            Release(output1); Release(output); Release(adapter); Release(factory);
        }
    }
}
