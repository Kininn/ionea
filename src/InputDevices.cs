#nullable disable
// Input devices: which keyboards, mice and controllers are plugged in, with their own names.
// Keyboards and mice are listed through raw input (nothing is opened). One physical device often
// shows up as both (gaming keyboards have a mouse part, gaming mice a keyboard part for macro keys),
// so parts are grouped by Windows' container id and each device's role is learned from what it
// actually sends while the launcher is in front. Learned roles and every device seen are kept in
// settings, so an unplugged device can be shown greyed with its name.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using static Ionea.Loc;

namespace Ionea;

public sealed class InputDevices
{
    public sealed record Dev(string Kind, string Key, string Name);

    sealed class Part { public IntPtr Handle; public bool Keyboard; public string Key, Name; }

    public static readonly Guid KeyboardGuid = new("884b96c3-56ef-11d1-bc8c-00a0c91405dd"), MouseGuid = new("378de44c-56ef-11d1-bc8c-00a0c91405dd");

    readonly MainWindow w;
    InputPrefs Prefs => w.Settings.Input;
    List<Part> parts = new();
    readonly Dictionary<IntPtr, string> byHandle = new();
    List<Dev> keyboards = new(), mice = new();
    bool rawOn, primed;
    public bool TestOnly;                      // test scripts: real devices are ignored, fake ones are used
    public readonly List<Dev> Fake = new();
    public event Action Changed;

    public InputDevices(MainWindow win) { w = win; }

    public IReadOnlyList<Dev> Keyboards => keyboards;
    public IReadOnlyList<Dev> Mice => mice;
    public List<Dev> Pads()
    {
        var list = new List<Dev>();
        if (TestOnly) return Fake.Where(d => d.Kind == "pad").ToList();
        foreach (var kv in w.Pads) list.Add(new Dev("pad", kv.Key, kv.Value));
        var slots = w.Input.Connected;
        int xin = slots.Count(c => c);
        for (int i = list.Count; i < xin; i++) list.Add(new Dev("pad", "xinput" + i, "Xbox controller"));   // XInput pads with no HID name
        return list;
    }
    public IReadOnlyList<Dev> Now(string kind) => kind switch { "keyboard" => keyboards, "mouse" => mice, _ => Pads() };
    public bool Connected(string kind) => Now(kind).Count > 0;

    // The device to show for a kind: the one plugged in, or the last one seen (greyed), or none.
    public (string Name, bool On, DateTime? Seen) Show(string kind)
    {
        var now = Now(kind);
        if (now.Count > 0) return (now.Count == 1 ? now[0].Name : $"{now[0].Name} +{now.Count - 1}", true, null);
        var last = Prefs.Seen?.Where(d => d.Kind == kind).OrderByDescending(d => d.LastSeen).FirstOrDefault();
        return last == null ? (null, false, null) : (last.Name, false, last.LastSeen);
    }

    public static string Noun(string kind) => kind switch { "keyboard" => T("menu.keyboard"), "mouse" => T("settings.mouse"), _ => T("settings.controller_2") };

    // ---- listing ----
    public void Start()
    {
        Refresh(false);
        primed = true;
    }

    // After a device arrives or leaves (and at start): list again, remember, tell the user.
    public void Refresh(bool toast = true)
    {
        var oldK = keyboards; var oldM = mice;
        if (!TestOnly)
        {
            try { parts = List(); }
            catch (Exception e) { Log.Write("input devices: " + e.Message); }
        }
        else parts = new();
        byHandle.Clear();
        foreach (var p in parts) byHandle[p.Handle] = p.Key;
        Classify();
        Remember();
        if (!primed) Log.Write($"input devices: keyboards [{string.Join(", ", keyboards.Select(d => d.Name))}], mice [{string.Join(", ", mice.Select(d => d.Name))}], controllers [{string.Join(", ", Pads().Select(d => d.Name))}]");
        if (toast && primed)
        {
            foreach (var d in keyboards.Where(d => !oldK.Any(o => o.Key == d.Key))) Event(d, true);
            foreach (var d in oldK.Where(d => !keyboards.Any(o => o.Key == d.Key))) Event(d, false);
            foreach (var d in mice.Where(d => !oldM.Any(o => o.Key == d.Key))) Event(d, true);
            foreach (var d in oldM.Where(d => !mice.Any(o => o.Key == d.Key))) Event(d, false);
        }
        UpdateRaw();
        Changed?.Invoke();
    }

    void Event(Dev d, bool on)
    {
        Log.Write($"{d.Kind} {(on ? "connected" : "disconnected")}: {d.Name}");
        if (Prefs.PopupsFor(d.Kind)) w.Toasts.Show(T(on ? "settings.device_connected" : "settings.device_disconnected", Noun(d.Kind)), d.Name, d.Kind + (on ? "-on" : "-off"));
    }

    // Each physical device: a keyboard, a mouse or both, from what it has sent (or its parts and name until then).
    void Classify()
    {
        var k = new List<Dev>(); var m = new List<Dev>();
        var unknown = new List<(string key, string name)>();
        foreach (var g in parts.GroupBy(p => p.Key))
        {
            string name = g.First().Name;
            bool hasK = g.Any(p => p.Keyboard), hasM = g.Any(p => !p.Keyboard);
            string role = Role(g.Key, hasK, hasM, name);
            if (role == "unknown") { unknown.Add((g.Key, name)); continue; }
            if (role is "keyboard" or "both") k.Add(new Dev("keyboard", g.Key, name ?? "Keyboard"));
            if (role is "mouse" or "both") m.Add(new Dev("mouse", g.Key, name ?? "Mouse"));
        }
        // A device with both parts that hasn't sent anything yet fills a kind no known device has
        // (a gaming keyboard and a gaming mouse: the first key press settles which is which).
        bool noK = k.Count == 0, noM = m.Count == 0;
        foreach (var (key, name) in unknown)
        {
            if (noK) k.Add(new Dev("keyboard", key, name ?? "Keyboard"));
            if (noM) m.Add(new Dev("mouse", key, name ?? "Mouse"));
        }
        foreach (var d in Fake) (d.Kind == "keyboard" ? k : d.Kind == "mouse" ? m : null)?.Add(d);
        keyboards = k; mice = m;
    }

    string Role(string key, bool hasK, bool hasM, string name)
    {
        if (!hasK) return "mouse";
        if (!hasM) return "keyboard";
        if (Prefs.Roles != null && Prefs.Roles.TryGetValue(key, out string r)) return r;
        string n = (name ?? "").ToLowerInvariant();
        if (n.Contains("mouse")) return "mouse";
        if (n.Contains("keyboard")) return "keyboard";
        return "unknown";   // not shown until it sends something
    }

    bool Unresolved => parts.GroupBy(p => p.Key).Any(g => g.Any(p => p.Keyboard) && g.Any(p => !p.Keyboard) && Role(g.Key, true, true, g.First().Name) == "unknown");

    void Remember()
    {
        Prefs.Seen ??= new();
        bool changed = false;
        foreach (var d in keyboards.Concat(mice).Concat(Pads()))
        {
            if (TestOnly && !Fake.Contains(d)) continue;
            var s = Prefs.Seen.FirstOrDefault(x => x.Kind == d.Kind && x.Key == d.Key);
            if (s == null) { Prefs.Seen.Add(new KnownDevice { Kind = d.Kind, Key = d.Key, Name = d.Name, LastSeen = DateTime.Now }); changed = true; }
            else if (s.Name != d.Name || (DateTime.Now - s.LastSeen).TotalHours > 1) { s.Name = d.Name; s.LastSeen = DateTime.Now; changed = true; }
        }
        if (changed) w.Settings.Save();
    }

    public void PadsChanged() { Remember(); Changed?.Invoke(); }

    // ---- learning roles from raw input ----
    // Raw input is only asked for while some device's role is unknown (a fast mouse sends thousands of
    // messages a second). Only while the launcher is in front; legacy messages keep coming as usual.
    void UpdateRaw()
    {
        bool want = !TestOnly && Unresolved && w.Hwnd != IntPtr.Zero;
        if (want == rawOn) return;
        var devs = new[]
        {
            new RAWINPUTDEVICE { UsagePage = 1, Usage = 6, Flags = want ? 0u : 0x1u, Target = want ? w.Hwnd : IntPtr.Zero },
            new RAWINPUTDEVICE { UsagePage = 1, Usage = 2, Flags = want ? 0u : 0x1u, Target = want ? w.Hwnd : IntPtr.Zero },
        };
        if (RegisterRawInputDevices(devs, 2, (uint)Marshal.SizeOf<RAWINPUTDEVICE>())) { rawOn = want; Log.Write("input devices: " + (want ? "learning which device is a keyboard or a mouse" : "roles known")); }
    }

    // WM_INPUT
    public void OnRawInput(IntPtr l)
    {
        uint size = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        if (GetRawInputData(l, 0x10000005, out RAWINPUTHEADER h, ref size, (uint)Marshal.SizeOf<RAWINPUTHEADER>()) == uint.MaxValue) return;
        if (h.hDevice == IntPtr.Zero || !byHandle.TryGetValue(h.hDevice, out string key)) return;   // injected input has no device
        string got = h.dwType == 1 ? "keyboard" : h.dwType == 0 ? "mouse" : null;
        if (got == null) return;
        Prefs.Roles ??= new();
        Prefs.Roles.TryGetValue(key, out string had);
        string role = had == null || had == got ? got : "both";
        if (role == had) return;
        var g = parts.Where(p => p.Key == key).ToList();
        if (!(g.Any(p => p.Keyboard) && g.Any(p => !p.Keyboard))) return;   // only one kind of part: nothing to learn
        Prefs.Roles[key] = role;
        w.Settings.Save();
        Log.Write($"input devices: {g.First().Name} is a {role}");
        Classify();
        Remember();
        UpdateRaw();
        Changed?.Invoke();
    }

    // ---- raw input list -> physical devices ----
    static List<Part> List()
    {
        var result = new List<Part>();
        uint n = 0, sz = (uint)Marshal.SizeOf<Native.RAWINPUTDEVICELIST>();
        Native.GetRawInputDeviceList(null, ref n, sz);
        if (n == 0) return result;
        var list = new Native.RAWINPUTDEVICELIST[n];
        if (Native.GetRawInputDeviceList(list, ref n, sz) == uint.MaxValue) return result;
        foreach (var d in list.Take((int)n))
        {
            if (d.dwType > 1) continue;   // 0 mouse, 1 keyboard
            uint len = 0;
            Native.GetRawInputDeviceInfo(d.hDevice, 0x20000007, (StringBuilder)null, ref len);
            if (len == 0) continue;
            var sb = new StringBuilder((int)len + 1);
            if ((int)Native.GetRawInputDeviceInfo(d.hDevice, 0x20000007, sb, ref len) <= 0) continue;
            string path = sb.ToString();
            string id = InstanceId(path);
            if (id == null || id.StartsWith("ROOT\\", StringComparison.OrdinalIgnoreCase) || path.Contains("RDP_", StringComparison.OrdinalIgnoreCase)) continue;   // remote-desktop / virtual parts
            if (CM_Locate_DevNodeW(out uint inst, id, 0) != 0) continue;
            string container = Prop(inst, ContainerKey) is byte[] cb && cb.Length == 16 ? new Guid(cb).ToString() : null;
            if (container == null || container == "00000000-0000-0000-ffff-ffffffffffff") container = id.ToLowerInvariant();
            result.Add(new Part { Handle = d.hDevice, Keyboard = d.dwType == 1, Key = container, Name = Name(inst) });
        }
        return result;
    }

    // \\?\HID#VID_31E3&PID_1312&MI_00#7&2a&0&0000#{884b96c3-...} -> HID\VID_31E3&PID_1312&MI_00\7&2a&0&0000
    static string InstanceId(string path)
    {
        string p = path;
        if (p.StartsWith(@"\\?\") || p.StartsWith(@"\??\")) p = p.Substring(4);
        int brace = p.LastIndexOf("#{");
        if (brace > 0) p = p.Substring(0, brace);
        return p.Length == 0 ? null : p.Replace('#', '\\');
    }

    // The name the device itself reports (the USB product string), from the part or its parents.
    static string Name(uint inst)
    {
        uint cur = inst;
        for (int up = 0; up < 3; up++)
        {
            if (Text(Prop(cur, BusDescKey)) is string s && Useful(s)) return s.Trim();
            if (CM_Get_Parent(out uint parent, cur, 0) != 0) break;
            cur = parent;
        }
        cur = inst;
        for (int up = 0; up < 3; up++)
        {
            if (Text(Prop(cur, FriendlyKey)) is string s && Useful(s)) return s.Trim();
            if (CM_Get_Parent(out uint parent, cur, 0) != 0) break;
            cur = parent;
        }
        return null;
    }

    static bool Useful(string s) => !string.IsNullOrWhiteSpace(s) && !s.StartsWith("HID", StringComparison.OrdinalIgnoreCase)
        && !s.StartsWith("USB Input Device", StringComparison.OrdinalIgnoreCase) && !s.StartsWith("USB Composite", StringComparison.OrdinalIgnoreCase);

    static string Text(byte[] b) => b == null ? null : Encoding.Unicode.GetString(b).TrimEnd('\0');

    static byte[] Prop(uint inst, DEVPROPKEY key)
    {
        uint size = 0;
        CM_Get_DevNode_PropertyW(inst, ref key, out _, null, ref size, 0);
        if (size == 0) return null;
        var buf = new byte[size];
        return CM_Get_DevNode_PropertyW(inst, ref key, out _, buf, ref size, 0) == 0 ? buf : null;
    }

    [StructLayout(LayoutKind.Sequential)] struct DEVPROPKEY { public Guid fmtid; public uint pid; }
    static readonly DEVPROPKEY BusDescKey = new() { fmtid = new Guid("540b947e-8b40-45bc-a8a2-6a0b894cbda2"), pid = 4 };
    static readonly DEVPROPKEY FriendlyKey = new() { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };
    static readonly DEVPROPKEY ContainerKey = new() { fmtid = new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), pid = 2 };

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Locate_DevNodeW(out uint inst, string id, uint flags);
    [DllImport("cfgmgr32.dll")] static extern int CM_Get_Parent(out uint parent, uint inst, uint flags);
    [DllImport("cfgmgr32.dll")] static extern int CM_Get_DevNode_PropertyW(uint inst, ref DEVPROPKEY key, out uint type, byte[] buf, ref uint size, uint flags);

    [StructLayout(LayoutKind.Sequential)] struct RAWINPUTDEVICE { public ushort UsagePage, Usage; public uint Flags; public IntPtr Target; }
    [StructLayout(LayoutKind.Sequential)] struct RAWINPUTHEADER { public uint dwType, dwSize; public IntPtr hDevice, wParam; }
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devs, uint n, uint size);
    [DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr h, uint cmd, out RAWINPUTHEADER data, ref uint size, uint header);
}
