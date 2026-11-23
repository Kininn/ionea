using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Ionea;

// A plain Win32 window showing a still picture over a game window, drawn by GDI (on screen at once, unlike WPF).
// A game presenting straight to the display (bbport, Lichii: Vulkan, filling the screen) left the
// launcher's own picture stale under it, so hiding the game for the Home menu showed the library for a moment.
// The cover hides that switch: shown over the game, the game hidden and the menu drawn under it, then closed.
public sealed class Cover
{
    delegate IntPtr WndProc(IntPtr h, uint m, IntPtr wp, IntPtr lp);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEX { public int cbSize; public uint style; public WndProc proc; public int cls, wnd; public IntPtr inst, icon, cursor, bg; public string menu, name; public IntPtr iconSm; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    struct PAINTSTRUCT { public IntPtr dc; public int erase; public RECT rc; public int restore, incUpdate; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] reserved; }
    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER { public int size, w, h; public short planes, bits; public int compression, imageSize, xppm, yppm, used, important; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassExW(ref WNDCLASSEX c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWindowExW(int ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr owner, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int ht, uint flags);
    [DllImport("user32.dll")] static extern bool UpdateWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr BeginPaint(IntPtr h, out PAINTSTRUCT ps);
    [DllImport("user32.dll")] static extern bool EndPaint(IntPtr h, ref PAINTSTRUCT ps);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bi, uint usage, out IntPtr bits, IntPtr sec, uint off);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] static extern int SetStretchBltMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] static extern bool StretchBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, int sw, int sh, uint rop);
    [DllImport("dwmapi.dll")] static extern int DwmFlush();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string? name);

    const string Class = "IoneaCover";
    static WndProc? proc;  // kept alive: the window class calls it
    static readonly System.Collections.Generic.Dictionary<IntPtr, Cover> open = new();

    IntPtr hwnd, bmp; int bw, bh;

    // Over `child`'s client area, owned by `owner` (so it stays above the launcher). Null when it can't.
    public static Cover? Show(IntPtr owner, IntPtr child, BitmapSource? img)
    {
        if (img == null) return null;
        try
        {
            GetClientRect(child, out var r);
            var pt = new POINT(); ClientToScreen(child, ref pt);
            int w = r.R - r.L, h = r.B - r.T;
            if (w < 16 || h < 16) return null;
            var c = new Cover();
            if (!c.Picture(img)) return null;
            if (proc == null)
            {
                proc = Proc;
                var wc = new WNDCLASSEX { cbSize = Marshal.SizeOf<WNDCLASSEX>(), proc = proc, inst = GetModuleHandleW(null), name = Class };
                RegisterClassExW(ref wc);
            }
            // WS_POPUP; tool window, no activation, clicks pass through
            c.hwnd = CreateWindowExW(0x80 | 0x08000000 | 0x20, Class, "", 0x80000000, pt.X, pt.Y, w, h, owner, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
            if (c.hwnd == IntPtr.Zero) { c.Close(); return null; }
            open[c.hwnd] = c;
            SetWindowPos(c.hwnd, IntPtr.Zero /* HWND_TOP */, 0, 0, 0, 0, 0x0040 | 0x0010 | 0x0001 | 0x0002);   // SHOWWINDOW NOACTIVATE NOSIZE NOMOVE
            UpdateWindow(c.hwnd);
            DwmFlush();   // on screen before the game goes
            return c;
        }
        catch (Exception e) { Log.Write("cover: " + e.Message); return null; }
    }

    bool Picture(BitmapSource img)
    {
        var src = img.Format == PixelFormats.Bgr32 ? img : new FormatConvertedBitmap(img, PixelFormats.Bgr32, null, 0);
        bw = src.PixelWidth; bh = src.PixelHeight;
        var bi = new BITMAPINFOHEADER { size = Marshal.SizeOf<BITMAPINFOHEADER>(), w = bw, h = -bh, planes = 1, bits = 32 };
        bmp = CreateDIBSection(IntPtr.Zero, ref bi, 0, out IntPtr bits, IntPtr.Zero, 0);
        if (bmp == IntPtr.Zero) return false;
        src.CopyPixels(Int32Rect.Empty, bits, bw * 4 * bh, bw * 4);
        return true;
    }

    static IntPtr Proc(IntPtr h, uint m, IntPtr wp, IntPtr lp)
    {
        if (m == 0x0014) return 1;   // WM_ERASEBKGND: the picture covers it all
        if (m == 0x000F && open.TryGetValue(h, out var c))   // WM_PAINT
        {
            IntPtr dc = BeginPaint(h, out var ps);
            GetClientRect(h, out var r);
            IntPtr mem = CreateCompatibleDC(dc), old = SelectObject(mem, c.bmp);
            SetStretchBltMode(dc, 4 /* HALFTONE */);
            StretchBlt(dc, 0, 0, r.R, r.B, mem, 0, 0, c.bw, c.bh, 0x00CC0020);
            SelectObject(mem, old); DeleteDC(mem);
            EndPaint(h, ref ps);
            return IntPtr.Zero;
        }
        return DefWindowProcW(h, m, wp, lp);
    }

    public void Close()
    {
        if (hwnd != IntPtr.Zero) { open.Remove(hwnd); DestroyWindow(hwnd); hwnd = IntPtr.Zero; }
        if (bmp != IntPtr.Zero) { DeleteObject(bmp); bmp = IntPtr.Zero; }
    }
}
