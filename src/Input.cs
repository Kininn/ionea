// Input: keyboard (from the window) and XInput pads (polled) feed the same actions, with key repeat.
// Pads are read only while the launcher is the foreground window, so a running game never moves it.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Input;
using System.Windows.Threading;

namespace Ionea;

public enum Act { Up, Down, Left, Right, Accept, Back, Menu, Option, PageLeft, PageRight }

public sealed class InputHub
{
    public event Action<Act>? Action;
    public event Action<int, bool>? PadChanged;  // slot, connected (XInput)
    public event Action? HomePressed;             // View + Start together, or Guide: read even while a game is in front
    public Func<bool> CanRead = () => true;       // false while another window is in front
    public Func<bool> Enabled = () => true;       // false: controllers turned off in Settings > Input

    // Holding a direction: first repeat after 380 ms, then it speeds up the longer it is held
    // (85 ms, 60 ms after ~0.5 s of repeats, 42 ms after ~1.3 s), so long rows fly by.
    const int RepeatDelay = 380;
    static int RepeatRate(int n) => n < 6 ? 85 : n < 18 ? 60 : 42;
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(15) };
    readonly Stopwatch clock = Stopwatch.StartNew();
    // Slots 0-3: XInput; 4-7: other HID game controllers (HidPads), read as if they were Xbox pads.
    const int Slots = 8;
    public readonly HidPads Hid = new();
    readonly HidPads.Pad?[] hidAt = new HidPads.Pad?[4];
    readonly bool[] connected = new bool[Slots];
    readonly ushort[] prev = new ushort[Slots];
    readonly bool[] homeHeld = new bool[Slots];
    readonly long[] comboSince = new long[Slots];
    // View + Start (− and + on a Switch pad) opens the Home menu only when both are held this long: games use them
    // apart, and a quick press of one while the other was still down opened the menu mid-game. Guide / Home is at once.
    const int ComboHoldMs = 500;
    long hushUntil;
    // The Home menu just opened over a game: for a moment, and until released, buttons pressed for the game do nothing here.
    public void Hush(int ms) => hushUntil = clock.ElapsedMilliseconds + ms;
    readonly bool[] aHeld = new bool[Slots];
    readonly ushort[] xNow = new ushort[4];
    public bool AHeld => aHeld.Any(a => a);   // A held on a pad (the quick menu on a game)
    public Action<ushort, byte, byte>? Capture;   // set while a binding waits for a button: buttons, LT, RT of any pad

    static bool noEx;
    readonly long[] nextScan = new long[Slots];
    readonly int[] heldDir = new int[Slots];
    readonly long[] repeatAt = new long[Slots];
    readonly int[] repeatN = new int[Slots];
    bool primed;

    public bool[] Connected => connected.Take(4).ToArray();   // XInput slots

    // Every pad's state now: XInput pads, then the other controllers (not the ones that are an Xbox pad too).
    public List<Native.XINPUT_GAMEPAD> States()
    {
        var l = new List<Native.XINPUT_GAMEPAD>();
        for (int i = 0; i < 4; i++) if (Native.XInputGetState(i, out var st) == 0) l.Add(st.Gamepad);
        foreach (var p in Hid.List) if (!p.Broken && p.Mirror >= 0 && p.Packet > 0) l.Add(p.State);
        return l;
    }

    // The same pads with a key that stays the same while the pad is plugged in ("xinput0".."xinput3", or
    // "hid:" and the HID path), for the player slots of a game (PlayerSlots.cs).
    public List<(string Key, Native.XINPUT_GAMEPAD State)> KeyedStates()
    {
        var l = new List<(string, Native.XINPUT_GAMEPAD)>();
        for (int i = 0; i < 4; i++) if (Native.XInputGetState(i, out var st) == 0) l.Add((XKeys[i], st.Gamepad));
        foreach (var p in Hid.List) if (!p.Broken && p.Mirror >= 0 && p.Packet > 0) l.Add(("hid:" + p.Path, p.State));
        return l;
    }
    static readonly string[] XKeys = { "xinput0", "xinput1", "xinput2", "xinput3" };

    // A pad key's name for the Home menu: the controller's own name when known.
    public static string PadName(string key, IReadOnlyDictionary<string, string> names)
    {
        if (key == null) return "None";
        if (key.StartsWith("xinput")) return "Xbox controller " + (key[^1] - '0' + 1);
        string k = key.StartsWith("hid:") ? Devices.Key(key[4..]) : key;
        return names != null && names.TryGetValue(k, out var n) && !string.IsNullOrEmpty(n) ? n : "Controller";
    }

    public void Start()
    {
        timer.Tick += (s, e) => { try { Poll(); } catch (Exception ex) { Log.Write("pad poll: " + ex.Message); } };
        timer.Start();
    }

    public void Raise(Act a) => Action?.Invoke(a);

    // Keyboard keys -> actions: LauncherKeys.Map (each profile sets its own).

    void Poll()
    {
        long now = clock.ElapsedMilliseconds;
        bool on = Enabled();
        bool front = on && CanRead() && now >= hushUntil;
        var hids = Hid.List.Where(p => !p.Broken && p.Packet > 0).Take(4).ToList();
        bool anyX = false;
        for (int i = 0; i < Slots; i++)
        {
            Native.XINPUT_STATE st;
            bool ok;
            if (i < 4)
            {
                if (!connected[i] && now < nextScan[i]) continue;   // empty slots are slow to query: once a second
                ok = Read(i, out st);
                xNow[i] = ok ? (ushort)(st.Gamepad.wButtons & ~PadReport.Guide) : (ushort)0;
                anyX |= ok;
            }
            else
            {
                var pad = i - 4 < hids.Count ? hids[i - 4] : null;
                if (pad != hidAt[i - 4]) { hidAt[i - 4] = pad; connected[i] = false; }
                ok = pad != null && pad.Mirror >= 0;
                st = new Native.XINPUT_STATE { dwPacketNumber = pad?.Packet ?? 0, Gamepad = pad?.State ?? default };
                if (ok && !Own(pad!, st.Gamepad.wButtons, anyX, now)) { connected[i] = true; prev[i] = 0; heldDir[i] = 0; aHeld[i] = false; continue; }   // not known yet: wait
            }
            if (ok != connected[i])
            {
                connected[i] = ok; prev[i] = 0xFFFF; heldDir[i] = 0; aHeld[i] = false;
                if (primed) PadChanged?.Invoke(i, ok);
            }
            if (!ok) { nextScan[i] = now + 1000; continue; }

            ushort b = st.Gamepad.wButtons;
            if (Capture != null && front) { Capture(b, st.Gamepad.bLeftTrigger, st.Gamepad.bRightTrigger); prev[i] = 0xFFFF; heldDir[i] = Dir(b, st.Gamepad.sThumbLX, st.Gamepad.sThumbLY, heldDir[i]); continue; }
            // Menus follow the letters printed on a Nintendo pad (A confirms, B goes back, as on a Switch);
            // binds (Capture above) and games keep the buttons by position.
            if (i >= 4 && hidAt[i - 4] is HidPads.Pad lp && PadReport.Labelled(lp.Layout)) b = PadReport.ByLabel(b);
            bool guide = (b & 0x0400) != 0, combo = (b & 0x0030) == 0x0030;
            if (!combo) comboSince[i] = 0; else if (comboSince[i] == 0) comboSince[i] = now;
            bool home = guide || (combo && now - comboSince[i] >= ComboHoldMs);   // Guide, or View + Start held
            bool homeDown = home && !homeHeld[i];
            homeHeld[i] = home;
            if (homeDown && primed && on) { Log.Write($"pad: Home menu button ({(guide ? "Guide / Home" : "View + Start held")}, slot {i})"); HomePressed?.Invoke(); }
            int dir = Dir(b, st.Gamepad.sThumbLX, st.Gamepad.sThumbLY, heldDir[i]);
            if (!front) { prev[i] = 0xFFFF; heldDir[i] = dir; repeatAt[i] = long.MaxValue; aHeld[i] = false; continue; }   // a fresh press is needed after a game
            ushort down = (ushort)(b & ~prev[i]);
            prev[i] = b;
            aHeld[i] = (b & 0x1000) != 0;   // before Accept is raised: the home screen asks whether A is still down
            if ((down & 0x1000) != 0) Raise(Act.Accept);       // A
            if ((down & 0x2000) != 0) Raise(Act.Back);         // B
            if ((down & 0x4000) != 0) Raise(Act.Option);       // X
            if ((down & 0x0010) != 0 && (b & 0x0020) == 0) Raise(Act.Menu);   // Start (not with View down: that is the Home menu)
            if ((down & 0x0100) != 0) Raise(Act.PageLeft);     // LB
            if ((down & 0x0200) != 0) Raise(Act.PageRight);    // RB

            if (dir != heldDir[i])
            {
                heldDir[i] = dir;
                repeatN[i] = 0;
                if (dir != 0) { Raise((Act)(dir - 1)); repeatAt[i] = now + RepeatDelay; }
            }
            else if (dir != 0 && now >= repeatAt[i]) { Raise((Act)(dir - 1)); repeatAt[i] = now + RepeatRate(repeatN[i]++); }
        }
        primed = true;
    }

    // Is this HID pad its own, or an Xbox pad as well (Steam, a pad tool)? Decided on the first press while an
    // Xbox pad is plugged in: the same buttons on the Xbox pad within a few polls mean the same pad.
    bool Own(HidPads.Pad pad, ushort b, bool anyX, long now)
    {
        if (pad.Mirror == 1) return true;
        if (!anyX) return true;   // no Xbox pad: nothing to be the same as (asked again if one comes)
        b = (ushort)(b & ~PadReport.Guide);
        if (b == 0) { pad.ActiveSince = -1; return false; }
        if (pad.ActiveSince < 0) pad.ActiveSince = now;
        for (int x = 0; x < 4; x++)
            if (connected[x] && xNow[x] == b)
            {
                pad.Mirror = -1;
                Log.Write("controllers: a HID pad is also an Xbox pad; read once, as the Xbox pad");
                return false;
            }
        if (now - pad.ActiveSince < 90) return false;
        pad.Mirror = 1;
        Log.Write("controllers: a HID pad is its own pad");
        return true;
    }

    // XInputGetStateEx (ordinal 100) also reports the Guide button (0x0400); plain XInputGetState otherwise.
    static bool Read(int i, out Native.XINPUT_STATE st)
    {
        if (!noEx)
        {
            try { return Native.XInputGetStateEx(i, out st) == 0; }
            catch (EntryPointNotFoundException) { noEx = true; }
        }
        return Native.XInputGetState(i, out st) == 0;
    }

    // 0 none, 1 up, 2 down, 3 left, 4 right (Act + 1). D-pad first, then the left stick with hysteresis.
    static int Dir(ushort b, short lx, short ly, int held)
    {
        if ((b & 0x1) != 0) return 1;
        if ((b & 0x2) != 0) return 2;
        if ((b & 0x4) != 0) return 3;
        if ((b & 0x8) != 0) return 4;
        int on = 17000, keep = 12000;
        int ax = Math.Abs((int)lx), ay = Math.Abs((int)ly);
        int lim = held != 0 ? keep : on;
        if (Math.Max(ax, ay) < lim) return 0;
        if (ay >= ax) return ly > 0 ? 1 : 2;
        return lx < 0 ? 3 : 4;
    }

    public static string? Battery(int slot)
    {
        if (Native.XInputGetBatteryInformation(slot, 0, out Native.XINPUT_BATTERY_INFORMATION bi) != 0) return null;
        if (bi.BatteryType == 1) return "wired";
        if (bi.BatteryType == 0) return null;
        return bi.BatteryLevel switch { 0 => "battery empty", 1 => "battery low", 2 => "battery medium", _ => "battery full" };
    }
}

// HID game controllers: names for the controller screen and connect/disconnect events for the toast.
public static class Devices
{
    public static readonly Guid HidGuid = new("4D1E55B2-F16F-11CF-88CB-001111000030");

    // One physical pad can expose several HID collections (&col01, &col02 ...); count it once.
    public static string Key(string path)
    {
        string p = path.ToLowerInvariant();
        if (p.StartsWith(@"\\?\") || p.StartsWith(@"\??\")) p = p.Substring(4);
        p = Regex.Replace(p, @"&col[0-9a-f]+", "");
        int brace = p.LastIndexOf("#{");
        return brace > 0 ? p.Substring(0, brace) : p;
    }

    // Called once at start and once per device arrival (never polled).
    public static Dictionary<string, string> List()
    {
        var result = new Dictionary<string, string>();
        foreach (string path in RawHidPaths())
            if (Describe(path, out string? name)) result[Key(path)] = name;
        return result;
    }

    // True if a raw-input game controller (joystick or gamepad) has this device path.
    // Only those are ever opened, so other HID devices (headsets, keyboards, mice) are never touched.
    public static bool IsGamepad(string path)
    {
        string k = Key(path);
        return RawHidPaths().Exists(p => Key(p) == k);
    }

    // Raw-input HID devices whose usage is Generic Desktop (0x01) / Joystick (0x04) or Gamepad (0x05).
    // The usage comes from the raw-input device info, so filtering opens no device.
    static List<string> RawHidPaths()
    {
        var result = new List<string>();
        uint n = 0, size = (uint)Marshal.SizeOf<Native.RAWINPUTDEVICELIST>();
        Native.GetRawInputDeviceList(null, ref n, size);
        if (n == 0) return result;
        var list = new Native.RAWINPUTDEVICELIST[n];
        if (Native.GetRawInputDeviceList(list, ref n, size) == uint.MaxValue) return result;
        foreach (var d in list)
        {
            if (d.dwType != 2) continue;  // RIM_TYPEHID
            var info = new byte[32];      // RID_DEVICE_INFO; HID part: usUsagePage at 20, usUsage at 22
            BitConverter.GetBytes(32u).CopyTo(info, 0);
            uint isz = 32;
            if ((int)Native.GetRawInputDeviceInfo(d.hDevice, 0x2000000b, info, ref isz) <= 0) continue;   // RIDI_DEVICEINFO
            ushort page = BitConverter.ToUInt16(info, 20), usage = BitConverter.ToUInt16(info, 22);
            if (page != 0x01 || (usage != 0x04 && usage != 0x05)) continue;
            uint len = 0;
            Native.GetRawInputDeviceInfo(d.hDevice, 0x20000007, (StringBuilder?)null, ref len);  // RIDI_DEVICENAME
            if (len == 0) continue;
            var sb = new StringBuilder((int)len + 1);
            if ((int)Native.GetRawInputDeviceInfo(d.hDevice, 0x20000007, sb, ref len) > 0) result.Add(@"\\?\" + sb.ToString().Substring(4));
        }
        return result;
    }

    // True if the HID path is a game controller (usage page 1, usage 4 joystick / 5 gamepad).
    public static bool Describe(string path, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? name)
    {
        name = null;
        using (var h = Native.CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
        {
            if (h.IsInvalid) return false;
            if (!Native.HidD_GetPreparsedData(h, out IntPtr pre)) return false;
            bool ok = Native.HidP_GetCaps(pre, out Native.HIDP_CAPS caps) == 0x110000;
            Native.HidD_FreePreparsedData(pre);
            if (!ok || caps.UsagePage != 1 || (caps.Usage != 4 && caps.Usage != 5)) return false;
            var buf = new byte[256];
            if (Native.HidD_GetProductString(h, buf, buf.Length)) name = Encoding.Unicode.GetString(buf).TrimEnd('\0').Trim();
        }
        if (string.IsNullOrEmpty(name) || name.Contains("XINPUT", StringComparison.OrdinalIgnoreCase))
        {
            string p = path.ToLowerInvariant();
            name = p.Contains("vid_045e") ? "Xbox controller" : p.Contains("vid_054c") ? "PlayStation controller"
                 : p.Contains("vid_057e") ? "Controller" : "Game controller";
        }
        return true;
    }
}

public static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct XINPUT_GAMEPAD { public ushort wButtons; public byte bLeftTrigger, bRightTrigger; public short sThumbLX, sThumbLY, sThumbRX, sThumbRY; }
    [StructLayout(LayoutKind.Sequential)]
    public struct XINPUT_STATE { public uint dwPacketNumber; public XINPUT_GAMEPAD Gamepad; }
    [StructLayout(LayoutKind.Sequential)]
    public struct XINPUT_BATTERY_INFORMATION { public byte BatteryType, BatteryLevel; }
    [DllImport("xinput1_4.dll")] public static extern int XInputGetState(int index, out XINPUT_STATE state);
    [DllImport("xinput1_4.dll", EntryPoint = "#100")] public static extern int XInputGetStateEx(int index, out XINPUT_STATE state);
    [DllImport("xinput1_4.dll")] public static extern int XInputGetBatteryInformation(int index, byte devType, out XINPUT_BATTERY_INFORMATION info);

    [StructLayout(LayoutKind.Sequential)] public struct RAWINPUTDEVICELIST { public IntPtr hDevice; public uint dwType; }
    [StructLayout(LayoutKind.Sequential)]
    public struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort a, b, c, d, e, f, g, h, i, j;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct DEV_BROADCAST_DEVICEINTERFACE { public int dbcc_size, dbcc_devicetype, dbcc_reserved; public Guid dbcc_classguid; public short dbcc_name; }

    [DllImport("user32.dll")] public static extern uint GetRawInputDeviceList([Out] RAWINPUTDEVICELIST[]? list, ref uint count, uint size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint GetRawInputDeviceInfo(IntPtr dev, uint cmd, StringBuilder? data, ref uint size);
    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW")] public static extern uint GetRawInputDeviceInfo(IntPtr dev, uint cmd, byte[] data, ref uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr tmpl);
    [DllImport("hid.dll")] public static extern bool HidD_GetPreparsedData(Microsoft.Win32.SafeHandles.SafeFileHandle h, out IntPtr data);
    [DllImport("hid.dll")] public static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] public static extern int HidP_GetCaps(IntPtr data, out HIDP_CAPS caps);
    [DllImport("hid.dll")] public static extern bool HidD_GetProductString(Microsoft.Win32.SafeHandles.SafeFileHandle h, byte[] buf, int len);
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr RegisterDeviceNotification(IntPtr h, ref DEV_BROADCAST_DEVICEINTERFACE filter, int flags);
    [DllImport("user32.dll")] public static extern bool UnregisterDeviceNotification(IntPtr h);

    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int index);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern bool ShowWindowAsync(IntPtr h, int cmd);

    // Hide every shown top-level window of a process (an emulator closing behind the launcher's closing screen).
    public static void HideWindowsOf(int pid)
    {
        EnumWindows((h, _) =>
        {
            if (IsWindowVisible(h) && GetWindowThreadProcessId(h, out uint p) != 0 && p == pid) ShowWindowAsync(h, 0 /* SW_HIDE */);
            return true;
        }, IntPtr.Zero);
    }
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern IntPtr OpenInputDesktop(int flags, bool inherit, uint access);
    [DllImport("user32.dll")] public static extern bool CloseDesktop(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetUserObjectInformation(IntPtr h, int index, StringBuilder info, int len, out int needed);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint PrivateExtractIcons(string file, int index, int cx, int cy, IntPtr[] icons, int[] ids, uint n, uint flags);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);

    // Bring a window to the front even when Windows' foreground lock would refuse it.
    public static void ForceForeground(IntPtr h)
    {
        if (IsIconic(h)) ShowWindow(h, 9);   // SW_RESTORE
        if (SetForegroundWindow(h) && GetForegroundWindow() == h) return;
        // Share the input state of the window in front (e.g. a full-screen game) for a moment.
        IntPtr fg = GetForegroundWindow();
        uint fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out _), me = GetCurrentThreadId();
        bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
        try { BringWindowToTop(h); SetForegroundWindow(h); }
        finally { if (attached) AttachThreadInput(me, fgThread, false); }
        if (GetForegroundWindow() == h) return;
        keybd_event(0x12, 0, 0, UIntPtr.Zero);        // a synthetic Alt press lifts the lock
        SetForegroundWindow(h);
        keybd_event(0x12, 0, 2, UIntPtr.Zero);
    }

    // True while the UAC prompt (or the lock screen) has the input desktop.
    public static bool SecureDesktop()
    {
        IntPtr d = OpenInputDesktop(0, false, 0x0001);
        if (d == IntPtr.Zero) return true;
        try
        {
            var name = new StringBuilder(64);
            if (!GetUserObjectInformation(d, 2, name, 128, out _)) return false;
            return !string.Equals(name.ToString(), "Default", StringComparison.OrdinalIgnoreCase);
        }
        finally { CloseDesktop(d); }
    }

    // Visible, normal-sized top-level windows of other processes.
    public static HashSet<IntPtr> BigWindows()
    {
        var set = new HashSet<IntPtr>();
        uint me = (uint)Environment.ProcessId;
        EnumWindows((h, l) =>
        {
            if (!IsWindowVisible(h) || (GetWindowLong(h, -20) & 0x80) != 0) return true;   // WS_EX_TOOLWINDOW
            if (!GetWindowRect(h, out RECT r) || r.R - r.L < 200 || r.B - r.T < 150) return true;
            GetWindowThreadProcessId(h, out uint pid);
            if (pid != me) set.Add(h);
            return true;
        }, IntPtr.Zero);
        return set;
    }

    public static void ActivateWindowOfOtherInstance()
    {
        // Our window class has no fixed title (it is the user's name), so look by process name. An embedded
        // emulator runs under the same name (EmbedLogic.Alias), so the program file must be ours too.
        string? me = Environment.ProcessPath;
        foreach (var p in Process.GetProcessesByName(Process.GetCurrentProcess().ProcessName))
        {
            using (p)
            {
                if (p.Id == Environment.ProcessId || p.MainWindowHandle == IntPtr.Zero) continue;
                string? path = null; try { path = p.MainModule?.FileName; } catch { /* no access to that process */ }
                if (me != null && !string.Equals(path, me, StringComparison.OrdinalIgnoreCase)) continue;
                ForceForeground(p.MainWindowHandle);
                return;
            }
        }
    }
}
