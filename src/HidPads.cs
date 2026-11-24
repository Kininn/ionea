// Controllers that aren't Xbox pads (PlayStation, Switch Pro, most other HID game controllers) in the launcher's
// menus and its built-in cores. Their HID reports arrive through raw input (also while a game is in front, so the
// Home button works there too) and are turned into the same state an Xbox pad gives, with the buttons by
// position: the bottom button is A, the right one B, the left one X, the top one Y. In the launcher's menus a pad
// printed with Nintendo's letters is read by its letters instead (InputHub.Poll): A confirms, B goes back.
// A pad that Steam or a pad tool already shows as an Xbox pad would act twice; the first press tells: when the
// Xbox pad shows the same buttons at the same moment, the HID pad is left to the Xbox pad.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Ionea;

public static class PadReport
{
    public const ushort Up = 0x1, Down = 0x2, Left = 0x4, Right = 0x8, Start = 0x10, Back = 0x20, LThumb = 0x40, RThumb = 0x80,
        LB = 0x100, RB = 0x200, Guide = 0x400, A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000;

    // NintendoWired: wired pads for Switch by other makers (PDP, HORI, PowerA ...). Their HID buttons come as
    // Y, B, A, X (left, bottom, right, top), then L, R, ZL, ZR, Minus, Plus, the stick clicks, Home, Capture.
    public enum Layout { Generic, Sony, Nintendo, NintendoWired }

    public static Layout LayoutOf(string? path, string? name = null)
    {
        string p = path?.ToLowerInvariant() ?? "";
        if (p.Contains("vid_054c")) return Layout.Sony;
        if (p.Contains("vid_057e")) return Layout.Nintendo;
        string n = name?.ToLowerInvariant() ?? "";
        return n.Contains("switch") || n.Contains("nintendo") || n.Contains("pokken") ? Layout.NintendoWired : Layout.Generic;
    }

    // Printed with Nintendo's letters: A on the right, B at the bottom, X on top, Y on the left.
    public static bool Labelled(Layout l) => l is Layout.Nintendo or Layout.NintendoWired;

    // Buttons by position -> by the letters printed on a Nintendo pad (A and B, X and Y change places).
    public static ushort ByLabel(ushort b)
    {
        ushort r = (ushort)(b & ~(A | B | X | Y));
        if ((b & A) != 0) r |= B;
        if ((b & B) != 0) r |= A;
        if ((b & X) != 0) r |= Y;
        if ((b & Y) != 0) r |= X;
        return r;
    }

    public static bool IsDualSense(string path)
    {
        string p = path?.ToLowerInvariant() ?? "";
        return p.Contains("vid_054c") && (p.Contains("pid_0ce6") || p.Contains("pid_0df2"));
    }

    // Xbox pads (and the HID side of one) are read through XInput already.
    public static bool ReadByXInput(string path)
    {
        string p = path?.ToLowerInvariant() ?? "";
        return p.Contains("ig_") || p.Contains("vid_045e");
    }

    // HID button numbers (from 1) -> Xbox buttons by position; L2 / R2 (7 / 8) come back as triggers.
    public static ushort Buttons(Layout l, IEnumerable<int> pressed, out bool lt, out bool rt)
    {
        ushort b = 0; lt = rt = false;
        foreach (int n in pressed)
        {
            switch (l, n)
            {
                case (Layout.Sony, 1): b |= X; break;        // square
                case (Layout.Sony, 2): b |= A; break;        // cross
                case (Layout.Sony, 3): b |= B; break;        // circle
                case (Layout.Sony, 4): b |= Y; break;        // triangle
                case (Layout.Sony, 14): b |= Back; break;    // touchpad click
                case (Layout.Nintendo, 1): b |= A; break;    // B, the bottom button
                case (Layout.Nintendo, 2): b |= B; break;    // A, the right one
                case (Layout.Nintendo, 3): b |= X; break;    // Y, the left one
                case (Layout.Nintendo, 4): b |= Y; break;    // X, the top one
                case (Layout.NintendoWired, 1): b |= X; break;   // Y, the left one
                case (Layout.NintendoWired, 2): b |= A; break;   // B, the bottom one
                case (Layout.NintendoWired, 3): b |= B; break;   // A, the right one
                case (Layout.NintendoWired, 4): b |= Y; break;   // X, the top one
                case (Layout.Generic, 1): b |= A; break;
                case (Layout.Generic, 2): b |= B; break;
                case (Layout.Generic, 3): b |= X; break;
                case (Layout.Generic, 4): b |= Y; break;
                case (_, 5): b |= LB; break;
                case (_, 6): b |= RB; break;
                case (_, 7): lt = true; break;
                case (_, 8): rt = true; break;
                case (_, 9): b |= Back; break;              // Share / Create / Minus / Select
                case (_, 10): b |= Start; break;            // Options / Plus / Start
                case (_, 11): b |= LThumb; break;
                case (_, 12): b |= RThumb; break;
                case (_, 13): b |= Guide; break;            // PS / Home
            }
        }
        return b;
    }

    // A hat switch value, 0 = up, clockwise in eighths; anything else is centred.
    public static ushort Hat(int v) => v switch
    {
        0 => Up, 1 => (ushort)(Up | Right), 2 => Right, 3 => (ushort)(Down | Right),
        4 => Down, 5 => (ushort)(Down | Left), 6 => Left, 7 => (ushort)(Up | Left), _ => 0,
    };

    // A raw axis value into XInput's range; HID's y grows downwards, XInput's upwards.
    public static short Axis(long v, long min, long max, bool flip)
    {
        if (max <= min) return 0;
        double t = (v - min) / (double)(max - min) * 2 - 1;
        if (flip) t = -t;
        return (short)Math.Clamp(Math.Round(t * 32767), -32768, 32767);
    }

    // PlayStation reports that the HID description doesn't list (what Bluetooth pads send after a game or
    // Steam switched them to the full report): DualShock 4 0x11, DualSense 0x31. False for anything else.
    public static bool SonyFull(byte[] r, int len, out Native.XINPUT_GAMEPAD g)
    {
        g = default;
        int o, lx, l2, pad;
        bool ds;
        if (len >= 12 && r[0] == 0x11) { o = 3; ds = false; }        // DualShock 4, Bluetooth
        else if (len >= 12 && r[0] == 0x31) { o = 2; ds = true; }    // DualSense, Bluetooth
        else return false;
        lx = o;
        if (ds) { l2 = o + 4; pad = o + 7; } else { pad = o + 4; l2 = o + 7; }
        byte b0 = r[pad], b1 = r[pad + 1], b2 = r[pad + 2];
        ushort b = Hat(b0 & 0x0F);
        if ((b0 & 0x10) != 0) b |= X;
        if ((b0 & 0x20) != 0) b |= A;
        if ((b0 & 0x40) != 0) b |= B;
        if ((b0 & 0x80) != 0) b |= Y;
        if ((b1 & 0x01) != 0) b |= LB;
        if ((b1 & 0x02) != 0) b |= RB;
        if ((b1 & 0x10) != 0) b |= Back;
        if ((b1 & 0x20) != 0) b |= Start;
        if ((b1 & 0x40) != 0) b |= LThumb;
        if ((b1 & 0x80) != 0) b |= RThumb;
        if ((b2 & 0x01) != 0) b |= Guide;
        if ((b2 & 0x02) != 0) b |= Back;
        g.wButtons = b;
        g.sThumbLX = Axis(r[lx], 0, 255, false); g.sThumbLY = Axis(r[lx + 1], 0, 255, true);
        g.sThumbRX = Axis(r[lx + 2], 0, 255, false); g.sThumbRY = Axis(r[lx + 3], 0, 255, true);
        g.bLeftTrigger = r[l2]; g.bRightTrigger = r[l2 + 1];
        return true;
    }
}

public sealed class HidPads
{
    public sealed class Pad
    {
        public IntPtr Handle, Pre;
        public string? Path;
        public PadReport.Layout Layout;
        public string? Name;
        public readonly List<(ushort usage, long min, long max, int bits)> Axes = new();
        public int MaxButtons;
        public Native.XINPUT_GAMEPAD State;
        public uint Packet;
        public int Mirror;        // 0 not known yet, 1 its own pad, -1 the same pad as an Xbox pad
        public long ActiveSince = -1;
        public bool Broken;
    }

    readonly List<Pad> pads = new();
    readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
    bool registered;
    public static bool Off;   // tests: no real HID pads

    public IReadOnlyList<Pad> List => pads;
    public long Now => clock.ElapsedMilliseconds;

    public void Start(IntPtr hwnd)
    {
        if (Off || registered || hwnd == IntPtr.Zero) return;
        var devs = new[]
        {
            new RAWINPUTDEVICE { UsagePage = 1, Usage = 4, Flags = 0x100, Target = hwnd },   // joystick, RIDEV_INPUTSINK
            new RAWINPUTDEVICE { UsagePage = 1, Usage = 5, Flags = 0x100, Target = hwnd },   // gamepad
        };
        registered = RegisterRawInputDevices(devs, 2, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        Log.Write(registered ? "controllers: reading HID game controllers" : "controllers: HID game controllers can't be read (" + Marshal.GetLastWin32Error() + ")");
    }

    // A device came or went: forget the pads that aren't there any more (a new one is picked up by its first report).
    public void Refresh()
    {
        if (pads.Count == 0) return;
        uint n = 0, sz = (uint)Marshal.SizeOf<Native.RAWINPUTDEVICELIST>();
        Native.GetRawInputDeviceList(null, ref n, sz);
        var list = new Native.RAWINPUTDEVICELIST[Math.Max(1, n)];
        if (n > 0 && Native.GetRawInputDeviceList(list, ref n, sz) == uint.MaxValue) return;
        var now = new HashSet<IntPtr>(list.Take((int)n).Select(d => d.hDevice));
        foreach (var p in pads.Where(p => !now.Contains(p.Handle)).ToList())
        {
            Log.Write("controllers: HID pad gone");
            if (p.Pre != IntPtr.Zero) Marshal.FreeHGlobal(p.Pre);
            pads.Remove(p);
        }
    }

    // WM_INPUT. True if it was a game controller's report (then it is not a keyboard's or a mouse's).
    public bool OnRawInput(IntPtr l)
    {
        if (!registered) return false;
        int hsz = Marshal.SizeOf<RAWINPUTHEADER>();
        uint size = 0;
        GetRawInputData(l, 0x10000003, IntPtr.Zero, ref size, (uint)hsz);   // RID_INPUT
        if (size == 0 || size > 1 << 16) return false;
        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(l, 0x10000003, buf, ref size, (uint)hsz) == uint.MaxValue) return false;
            var h = Marshal.PtrToStructure<RAWINPUTHEADER>(buf);
            if (h.dwType != 2) return false;   // RIM_TYPEHID
            if (h.hDevice == IntPtr.Zero) return true;
            var pad = Find(h.hDevice);
            if (pad == null || pad.Broken) return true;
            int each = Marshal.ReadInt32(buf, hsz), count = Marshal.ReadInt32(buf, hsz + 4);
            if (each <= 0 || count <= 0 || hsz + 8 + each * count > size) return true;
            IntPtr report = buf + hsz + 8 + each * (count - 1);   // the newest
            Read(pad, report, each);
            return true;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    Pad Find(IntPtr dev)
    {
        foreach (var p in pads) if (p.Handle == dev) return p;
        string? path = DeviceName(dev);
        var pad = new Pad { Handle = dev, Path = path, Layout = PadReport.LayoutOf(path) };
        pads.Add(pad);
        if (path == null || PadReport.ReadByXInput(path)) { pad.Broken = true; return pad; }   // read as an Xbox pad
        if (Devices.Describe(path, out string? name)) { pad.Name = name; pad.Layout = PadReport.LayoutOf(path, name); }
        uint psz = 0;
        GetRawInputDeviceInfo(dev, 0x20000005, IntPtr.Zero, ref psz);   // RIDI_PREPARSEDDATA
        if (psz == 0) { pad.Broken = true; return pad; }
        pad.Pre = Marshal.AllocHGlobal((int)psz);
        if ((int)GetRawInputDeviceInfo(dev, 0x20000005, pad.Pre, ref psz) <= 0) { pad.Broken = true; return pad; }
        if (Native.HidP_GetCaps(pad.Pre, out Native.HIDP_CAPS caps) != Ok) { pad.Broken = true; return pad; }
        ushort nv = caps.c;   // NumberInputValueCaps (after NumberLinkCollectionNodes, NumberInputButtonCaps)
        if (nv > 0)
        {
            var vc = new byte[72 * nv];
            if (HidP_GetValueCaps(0, vc, ref nv, pad.Pre) == Ok)
                for (int i = 0; i < nv; i++)
                {
                    int o = 72 * i;
                    ushort page = BitConverter.ToUInt16(vc, o), bits = BitConverter.ToUInt16(vc, o + 18);
                    bool range = vc[o + 12] != 0;
                    long min = BitConverter.ToInt32(vc, o + 40), max = BitConverter.ToInt32(vc, o + 44);
                    if (max < min || (min == 0 && max == -1)) { min = 0; max = (1L << Math.Min(bits, (ushort)31)) - 1; }   // an unsigned maximum read as signed
                    ushort u0 = BitConverter.ToUInt16(vc, o + 56), u1 = range ? BitConverter.ToUInt16(vc, o + 58) : u0;
                    if (page != 1) continue;
                    for (int u = u0; u <= u1 && u <= 0x39; u++) pad.Axes.Add(((ushort)u, min, max, bits));
                }
        }
        pad.MaxButtons = (int)HidP_MaxUsageListLength(0, 9, pad.Pre);
        Log.Write($"controllers: HID pad {pad.Name} ({pad.Layout}, {pad.MaxButtons} buttons, {pad.Axes.Count} axes)");
        return pad;
    }

    const int Ok = 0x110000;

    void Read(Pad pad, IntPtr report, int len)
    {
        var g = new Native.XINPUT_GAMEPAD();
        uint n = (uint)Math.Max(pad.MaxButtons, 1);
        var usages = new ushort[n];
        int st = HidP_GetUsages(0, 9, 0, usages, ref n, pad.Pre, report, (uint)len);
        if (st != Ok)
        {
            if (pad.Layout != PadReport.Layout.Sony) return;
            var bytes = new byte[len];
            Marshal.Copy(report, bytes, 0, len);
            if (!PadReport.SonyFull(bytes, len, out g)) return;
        }
        else
        {
            g.wButtons = PadReport.Buttons(pad.Layout, usages.Take((int)n).Select(u => (int)u), out bool lt, out bool rt);
            bool analogTriggers = false;
            foreach (var (u, min, max, bits) in pad.Axes)
            {
                if (HidP_GetUsageValue(0, 1, 0, u, out uint raw, pad.Pre, report, (uint)len) != Ok) continue;
                long v = raw;
                if (min < 0 && bits > 0 && bits < 32 && v >= 1L << (bits - 1)) v -= 1L << bits;   // signed
                switch (u)
                {
                    case 0x30: g.sThumbLX = PadReport.Axis(v, min, max, false); break;   // X
                    case 0x31: g.sThumbLY = PadReport.Axis(v, min, max, true); break;    // Y
                    case 0x32:                                                           // Z: right x (Sony, most pads)
                        g.sThumbRX = PadReport.Axis(v, min, max, false); break;
                    case 0x35: g.sThumbRY = PadReport.Axis(v, min, max, true); break;    // Rz: right y
                    case 0x33:                                                           // Rx: L2 (Sony), right x (Switch Pro)
                        if (pad.Layout == PadReport.Layout.Sony) { g.bLeftTrigger = (byte)Math.Clamp((v - min) * 255 / Math.Max(1, max - min), 0, 255); analogTriggers = true; }
                        else g.sThumbRX = PadReport.Axis(v, min, max, false);
                        break;
                    case 0x34:                                                           // Ry: R2 (Sony), right y (Switch Pro)
                        if (pad.Layout == PadReport.Layout.Sony) { g.bRightTrigger = (byte)Math.Clamp((v - min) * 255 / Math.Max(1, max - min), 0, 255); analogTriggers = true; }
                        else g.sThumbRY = PadReport.Axis(v, min, max, true);
                        break;
                    case 0x39: g.wButtons |= PadReport.Hat((int)(v - min)); break;       // hat
                }
            }
            if (!analogTriggers) { if (lt) g.bLeftTrigger = 255; if (rt) g.bRightTrigger = 255; }
        }
        pad.State = g;
        pad.Packet++;
    }

    static string? DeviceName(IntPtr dev)
    {
        uint len = 0;
        Native.GetRawInputDeviceInfo(dev, 0x20000007, (StringBuilder?)null, ref len);   // RIDI_DEVICENAME
        if (len == 0) return null;
        var sb = new StringBuilder((int)len + 1);
        return (int)Native.GetRawInputDeviceInfo(dev, 0x20000007, sb, ref len) > 0 ? sb.ToString() : null;
    }

    [StructLayout(LayoutKind.Sequential)] struct RAWINPUTDEVICE { public ushort UsagePage, Usage; public uint Flags; public IntPtr Target; }
    [StructLayout(LayoutKind.Sequential)] struct RAWINPUTHEADER { public uint dwType, dwSize; public IntPtr hDevice, wParam; }
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devs, uint n, uint size);
    [DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr h, uint cmd, IntPtr data, ref uint size, uint header);
    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW")] static extern uint GetRawInputDeviceInfo(IntPtr dev, uint cmd, IntPtr data, ref uint size);
    [DllImport("hid.dll")] static extern int HidP_GetValueCaps(int type, byte[] caps, ref ushort len, IntPtr pre);
    [DllImport("hid.dll")] static extern uint HidP_MaxUsageListLength(int type, ushort page, IntPtr pre);
    [DllImport("hid.dll")] static extern int HidP_GetUsages(int type, ushort page, ushort link, ushort[] list, ref uint len, IntPtr pre, IntPtr report, uint reportLen);
    [DllImport("hid.dll")] static extern int HidP_GetUsageValue(int type, ushort page, ushort link, ushort usage, out uint value, IntPtr pre, IntPtr report, uint reportLen);
}
