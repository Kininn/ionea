// A libretro core loaded into the launcher. The core only emulates: every frame it is given the
// controller state and hands back a picture and sound. All calls into the core happen on one thread
// (CoreSession's emulation thread); this class is not thread-safe by design.
// Supported: software-drawn cores (picture in memory), OpenGL cores (GlHost draws them off screen and the
// picture is read back into memory) and Vulkan cores (VulkanHost.cs, read back the same way). Cores that
// only draw with Direct3D are refused, with a message saying so.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Ionea;

public sealed class CoreOption
{
    public string Key = null!;          // always set where one is made
    public string? Desc;
    public List<string> Values = new();
    public List<string?>? Labels;        // what each value is called on screen (core options v1/v2), or null
    public string? Value;
    public string? Default;              // the core's own choice
    public string? Category;             // core options v2: the core's category key ("video", "audio"), or null
    public string Label(string v)
    {
        int i = Values.IndexOf(v);
        return Labels != null && i >= 0 && i < Labels.Count && !string.IsNullOrEmpty(Labels[i]) ? Labels[i]! : v;
    }
}

public sealed unsafe class RetroCore : IDisposable
{
    // ---- libretro structs ----
    [StructLayout(LayoutKind.Sequential)] struct SystemInfo { public IntPtr LibraryName, LibraryVersion, ValidExtensions; [MarshalAs(UnmanagedType.I1)] public bool NeedFullpath, BlockExtract; }
    [StructLayout(LayoutKind.Sequential)] public struct Geometry { public uint BaseWidth, BaseHeight, MaxWidth, MaxHeight; public float AspectRatio; }
    [StructLayout(LayoutKind.Sequential)] public struct Timing { public double Fps, SampleRate; }
    [StructLayout(LayoutKind.Sequential)] public struct AvInfo { public Geometry Geometry; public Timing Timing; }
    [StructLayout(LayoutKind.Sequential)] struct GameInfo { public IntPtr Path, Data; public UIntPtr Size; public IntPtr Meta; }
    [StructLayout(LayoutKind.Sequential)] struct Variable { public IntPtr Key, Value; }

    // ---- delegates (core -> us) ----
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] delegate bool EnvFn(uint cmd, IntPtr data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void VideoFn(IntPtr data, uint width, uint height, UIntPtr pitch);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void SampleFn(short l, short r);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate UIntPtr BatchFn(IntPtr data, UIntPtr frames);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void PollFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate short StateFn(uint port, uint device, uint index, uint id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void LogFn(int level, IntPtr fmt);   // printf-style; extra args ignored

    // ---- functions (us -> core) ----
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void SetCb(IntPtr cb);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void VoidFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate uint UintFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void InfoFn(out SystemInfo i);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void AvFn(out AvInfo i);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] delegate bool LoadFn(ref GameInfo g);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate UIntPtr SizeFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] delegate bool SerFn(IntPtr data, UIntPtr size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr MemDataFn(uint id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate UIntPtr MemSizeFn(uint id);

    readonly IntPtr lib;
    readonly VoidFn init, deinit, run, reset, unload;
    readonly UintFn apiVersion;
    readonly InfoFn sysInfo;
    readonly AvFn avInfo;
    readonly LoadFn load;
    readonly SizeFn serSize;
    readonly SerFn serialize, unserialize;
    readonly MemDataFn memData;
    readonly MemSizeFn memSize;
    readonly List<Delegate> keep = new();   // callbacks must outlive the core
    readonly List<IntPtr> strings = new();  // UTF-8 strings handed to the core
    IntPtr romData;
    bool loaded, inited;

    public string Name { get; private set; } = "core";
    public string Version { get; private set; } = "";
    public string Extensions { get; private set; } = "";
    public AvInfo Av;
    public int PixelFormat = 0;               // 0 = 0RGB1555, 1 = XRGB8888, 2 = RGB565
    public readonly List<CoreOption> Options = new();
    public volatile bool OptionsChanged;
    public Dictionary<string, string> Saved = new();   // the user's option choices, applied when the core lists its options

    // Hooks set by the session.
    public Action<IntPtr, int, int, int>? OnVideo;          // data (null = repeat last), width, height, pitch (negative: rows bottom-up, data is the top row)
    public Action<IntPtr, int>? OnAudio;                    // interleaved stereo int16, frame count
    public Func<int, int, short>? OnInput;                  // port, joypad id
    public Func<int, int, int, short>? OnAnalog;            // port, index (0 left stick, 1 right stick, 2 buttons), id
    public Func<int, short>? OnPointer;                     // id: 0 x, 1 y, 2 pressed (x/y in -0x7fff..0x7fff)
    public volatile bool UsesAnalog;                        // the core asked for a stick at least once
    public Action? OnPoll;
    public Action<string?>? OnMessage;
    public Action? OnGeometry;

    readonly string systemDir, saveDir, dllPath;

    // ---- GPU-drawn cores ----
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void HwCb();
    GlHost? gl;
    VulkanHost? vk;
    HwCb? hwReset, hwDestroy;
    bool contextLive;     // context_reset was called, so context_destroy is owed
    string? refused;      // why the core's GPU request was turned down, shown if it then can't load the game
    public bool Hardware => gl != null || vk != null;
    public string? GpuApi => vk != null ? "Vulkan" : gl != null ? "OpenGL" : null;
    short[] one = new short[2];

    public RetroCore(string dll, string systemDir, string saveDir)
    {
        this.systemDir = systemDir; this.saveDir = saveDir; dllPath = dll;
        Directory.CreateDirectory(systemDir); Directory.CreateDirectory(saveDir);
        lib = NativeLibrary.Load(dll);
        T F<T>(string n) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(lib, n));
        init = F<VoidFn>("retro_init"); deinit = F<VoidFn>("retro_deinit"); run = F<VoidFn>("retro_run");
        reset = F<VoidFn>("retro_reset"); unload = F<VoidFn>("retro_unload_game");
        apiVersion = F<UintFn>("retro_api_version");
        sysInfo = F<InfoFn>("retro_get_system_info"); avInfo = F<AvFn>("retro_get_system_av_info");
        load = F<LoadFn>("retro_load_game");
        serSize = F<SizeFn>("retro_serialize_size"); serialize = F<SerFn>("retro_serialize"); unserialize = F<SerFn>("retro_unserialize");
        memData = F<MemDataFn>("retro_get_memory_data"); memSize = F<MemSizeFn>("retro_get_memory_size");

        if (apiVersion() != 1) throw new InvalidOperationException("This core uses a libretro version the launcher doesn't know.");
        sysInfo(out var si);
        Name = Utf8(si.LibraryName) ?? "core"; Version = Utf8(si.LibraryVersion) ?? ""; Extensions = Utf8(si.ValidExtensions) ?? "";

        Set("retro_set_environment", new EnvFn(Env));
        Set("retro_set_video_refresh", new VideoFn(VideoIn));
        Set("retro_set_audio_sample", new SampleFn((l, r) => { fixed (short* s = one) { s[0] = l; s[1] = r; OnAudio?.Invoke((IntPtr)s, 1); } }));
        Set("retro_set_audio_sample_batch", new BatchFn((d, n) => { OnAudio?.Invoke(d, (int)n); return n; }));
        Set("retro_set_input_poll", new PollFn(() => OnPoll?.Invoke()));
        Set("retro_set_input_state", new StateFn(InputIn));
    }

    void VideoIn(IntPtr d, uint w, uint h, UIntPtr p)
    {
        if (d == new IntPtr(-1))   // RETRO_HW_FRAME_BUFFER_VALID: the picture is in our framebuffer
        {
            var show = OnVideo;
            if (show == null || w == 0 || h == 0) return;
            if (vk != null) vk.Frame((int)w, (int)h, show);   // like OpenGL: an earlier frame, or none this time
            else gl?.Read((int)w, (int)h, show);   // may be an earlier frame, or none this time (GlHost.cs)
            return;
        }
        if (d == IntPtr.Zero && vk != null && OnVideo is { } dupe && vk.Dupe(dupe)) return;   // a dupe: an earlier GPU picture may be ready now
        OnVideo?.Invoke(d, (int)w, (int)h, (int)p);
    }

    short InputIn(uint port, uint dev, uint idx, uint id)
    {
        switch (dev & 0xFF)
        {
            case 1:   // joypad
                if (OnInput == null) return 0;
                if (id == 256)   // RETRO_DEVICE_ID_JOYPAD_MASK: all buttons at once
                {
                    int m = 0;
                    for (int i = 0; i < 16; i++) if (OnInput((int)port, i) != 0) m |= 1 << i;
                    return (short)m;
                }
                return OnInput((int)port, (int)id);
            case 5:   // analog
                UsesAnalog = true;
                return OnAnalog?.Invoke((int)port, (int)idx, (int)id) ?? 0;
            case 6:   // pointer (touch screen)
                return port == 0 && idx == 0 ? OnPointer?.Invoke((int)id) ?? 0 : (short)0;
            default: return 0;
        }
    }

    void Set(string fn, Delegate cb)
    {
        keep.Add(cb);
        F2<SetCb>(fn)(Marshal.GetFunctionPointerForDelegate(cb));
    }
    T F2<T>(string n) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(lib, n));

    public void Init() { init(); inited = true; }

    // The settings a core offers, without starting a game: most cores list them when they get the
    // environment callback; the rest when they start (init / deinit, no game, no picture).
    [ThreadStatic] static bool TraceEnv;

    public static List<CoreOption> Probe(string dll, string systemDir, string saveDir)
    {
        TraceEnv = Environment.GetEnvironmentVariable("IONEA_TRACE_ENV") == "1";
        using var c = new RetroCore(dll, systemDir, saveDir);
        if (c.Options.Count == 0) c.Init();
        return c.Options.ToList();
    }

    public void LoadGame(string rom)
    {
        sysInfo(out var si);
        var gi = new GameInfo { Path = Str(rom) };
        if (!si.NeedFullpath)
        {
            byte[] b = File.ReadAllBytes(rom);
            romData = Marshal.AllocHGlobal(b.Length);
            Marshal.Copy(b, 0, romData, b.Length);
            gi.Data = romData; gi.Size = (UIntPtr)b.Length;
        }
        if (!load(ref gi)) throw new InvalidOperationException(refused ?? $"{Name} couldn't open this game file.");
        loaded = true;
        avInfo(out Av);
        if (gl != null)
        {
            GrowFramebuffer();
            Log.Write($"core {Name}: OpenGL {gl.Info}");
            hwReset?.Invoke();   // context_reset: the core sets up its GPU side now
            contextLive = true;
        }
        else if (vk != null)
        {
            try { vk.Create(); }
            catch (Exception e)
            {
                Log.Write($"core {Name}: Vulkan: {e.Message}");
                throw new InvalidOperationException("This game needs a graphics card that can run Vulkan. Updating the graphics driver may help.");
            }
            Log.Write($"core {Name}: Vulkan {vk.Info}");
            hwReset?.Invoke();   // context_reset: the core gets the device (GET_HW_RENDER_INTERFACE) now
            contextLive = true;
        }
    }

    void GrowFramebuffer()
    {
        if (gl == null || !loaded) return;
        gl.EnsureFramebuffer((int)Math.Max(Av.Geometry.MaxWidth, Av.Geometry.BaseWidth), (int)Math.Max(Av.Geometry.MaxHeight, Av.Geometry.BaseHeight));
    }

    // The core asks for an OpenGL or Vulkan context (SET_HW_RENDER). OpenGL is made now, Vulkan after the game is
    // loaded (the core may hand in its negotiation interface first); either way the core gets it after load.
    // retro_hw_render_callback on x64: type@0 reset@8 get_fb@16 get_proc@24 depth@32 stencil@33
    // bottom_left@34 major@36 minor@40 cache@44 destroy@48 debug@56.
    bool HwRender(IntPtr data)
    {
        byte* b = (byte*)data;
        int type = *(int*)b;
        if (type == 6) return VulkanRender(b);
        if (type != 1 && type != 3)
        {
            Log.Write($"core {Name}: wants GPU type {type}, not supported");
            refused = type is >= 7 and <= 10
                ? $"{Name} only draws with Direct3D, which the launcher can't host yet. Pick another core for this system, or another renderer in the core's settings."
                : $"{Name} wants a kind of graphics the launcher can't host (type {type}).";
            return false;
        }
        uint major = *(uint*)(b + 36), minor = *(uint*)(b + 40);
        bool depth = b[32] != 0, stencil = b[33] != 0, bottomLeft = b[34] != 0, debug = b[56] != 0;
        try
        {
            gl?.Dispose();
            gl = new GlHost(type, major, minor, depth, stencil, bottomLeft, debug);
        }
        catch (Exception e) { Log.Write($"core {Name}: OpenGL {type} {major}.{minor}: {e.Message}"); gl = null; return false; }
        keep.Add(gl.GetFb); keep.Add(gl.GetProc);
        *(IntPtr*)(b + 16) = Marshal.GetFunctionPointerForDelegate(gl.GetFb);
        *(IntPtr*)(b + 24) = Marshal.GetFunctionPointerForDelegate(gl.GetProc);
        IntPtr r = *(IntPtr*)(b + 8), d = *(IntPtr*)(b + 48);
        hwReset = r != IntPtr.Zero ? Marshal.GetDelegateForFunctionPointer<HwCb>(r) : null;
        hwDestroy = d != IntPtr.Zero ? Marshal.GetDelegateForFunctionPointer<HwCb>(d) : null;
        PixelFormat = 1;
        refused = null;
        Log.Write($"core {Name}: OpenGL {(type == 3 ? "core" : "compat")} {major}.{minor} requested{(depth ? ", depth" : "")}{(stencil ? ", stencil" : "")}{(bottomLeft ? ", bottom-left" : "")}");
        return true;
    }

    // RETRO_HW_CONTEXT_VULKAN. vulkan-1.dll missing: refused, and the core may draw another way instead.
    bool VulkanRender(byte* b)
    {
        string? why = VulkanHost.Unavailable();
        if (why != null)
        {
            Log.Write($"core {Name}: wants Vulkan, {why}");
            refused = $"{Name} draws with Vulkan, but no Vulkan driver was found on this PC. Updating the graphics driver may help.";
            return false;
        }
        gl?.Dispose(); gl = null;
        vk?.Dispose();
        vk = new VulkanHost(Name);
        *(IntPtr*)(b + 16) = IntPtr.Zero; *(IntPtr*)(b + 24) = IntPtr.Zero;   // get_current_framebuffer, get_proc_address: OpenGL only
        IntPtr r = *(IntPtr*)(b + 8), d = *(IntPtr*)(b + 48);
        hwReset = r != IntPtr.Zero ? Marshal.GetDelegateForFunctionPointer<HwCb>(r) : null;
        hwDestroy = d != IntPtr.Zero ? Marshal.GetDelegateForFunctionPointer<HwCb>(d) : null;
        PixelFormat = 1;
        refused = null;
        Log.Write($"core {Name}: Vulkan requested");
        return true;
    }

    public void Run()
    {
        if (vk == null) { run(); return; }
        vk.BeginFrame();   // this frame's sync index, free again
        run();
        vk.EndFrame();
        if (vk.Lost != null) throw new InvalidOperationException(vk.Lost);
    }
    public void Reset() => reset();

    // Saved game memory (battery save), e.g. Pokemon's save file.
    public Span<byte> SaveRam
    {
        get
        {
            IntPtr p = memData(0); long n = (long)memSize(0);
            return p == IntPtr.Zero || n <= 0 ? Span<byte>.Empty : new Span<byte>((void*)p, (int)n);
        }
    }

    public byte[]? SaveState()
    {
        int n = (int)serSize();
        if (n <= 0) return null;
        var buf = new byte[n];
        fixed (byte* p = buf) return serialize((IntPtr)p, (UIntPtr)n) ? buf : null;
    }

    // Rewind (Rewind.cs): the state's size now (0: none), and the state into a buffer of that size, made once.
    public int StateSize() => (int)Math.Min(int.MaxValue, (ulong)serSize());
    public bool SaveStateInto(byte[] buf)
    {
        fixed (byte* p = buf) return serialize((IntPtr)p, (UIntPtr)buf.Length);
    }
    public ulong Quirks;   // RETRO_SERIALIZATION_QUIRK_*, as the core said (SET_SERIALIZATION_QUIRKS)
    // GET_AUDIO_VIDEO_ENABLE: 1 picture, 2 sound, 4 states only used in this session (fast). Run-ahead (RunAhead.cs)
    // turns the picture and sound off for the frames it doesn't show or play, so cores can skip that work.
    public int AvEnable = 3;

    public bool LoadState(byte[] buf)
    {
        fixed (byte* p = buf) return unserialize((IntPtr)p, (UIntPtr)buf.Length);
    }

    // One setting from any of the three ways a core lists them. The user's saved choice wins when the core accepts it.
    void AddOption(string? key, string? desc, List<string> values, List<string?>? labels, string? def, string? category = null)
    {
        if (string.IsNullOrEmpty(key) || values.Count == 0) return;
        var o = Options.FirstOrDefault(x => x.Key == key) ?? new CoreOption { Key = key };
        o.Desc = string.IsNullOrWhiteSpace(desc) ? key : desc.Trim();
        o.Values = values; o.Labels = labels;
        if (category != null) o.Category = category;
        o.Default = def != null && values.Contains(def) ? def : values[0];
        if (o.Value == null || !o.Values.Contains(o.Value))
            o.Value = Saved.TryGetValue(key, out var sv) && o.Values.Contains(sv) ? sv : def != null && values.Contains(def) ? def : values[0];
        if (!Options.Contains(o)) Options.Add(o);
    }

    // Core options v1 / v2 definitions: key, desc, [v2: desc_categorized, info, info_categorized, category_key | v1: info],
    // values[128] {value, label}, default_value. 'head' is the number of pointers before the values.
    void ReadOptions(IntPtr defs, int head)
    {
        if (defs == IntPtr.Zero) return;
        int ps = IntPtr.Size, stride = (head + 256 + 1) * ps;
        for (IntPtr d = defs; ; d += stride)
        {
            string? key = Utf8(Marshal.ReadIntPtr(d));
            if (key == null) break;
            string? desc = Utf8(Marshal.ReadIntPtr(d, ps));
            var values = new List<string>(); var labels = new List<string?>();
            for (int i = 0; i < 128; i++)
            {
                string? v = Utf8(Marshal.ReadIntPtr(d, (head + 2 * i) * ps));
                if (v == null) break;
                values.Add(v); labels.Add(Utf8(Marshal.ReadIntPtr(d, (head + 2 * i + 1) * ps)));
            }
            string? def = Utf8(Marshal.ReadIntPtr(d, (head + 256) * ps));
            string? cat = head >= 6 ? Utf8(Marshal.ReadIntPtr(d, 5 * ps)) : null;   // v2: category_key
            AddOption(key, desc, values, labels.Any(l => l != null) ? labels : null, def, cat);
        }
    }

    public void SetOption(string key, string value)
    {
        var o = Options.FirstOrDefault(x => x.Key == key);
        if (o == null || !o.Values.Contains(value)) return;
        o.Value = value; OptionsChanged = true;
    }

    // ---- environment: what the core asks of the frontend ----
    bool Env(uint cmd, IntPtr data)
    {
        try
        {
            switch (cmd & 0xFFFF)   // some commands carry the EXPERIMENTAL flag (0x10000)
            {
                case 1: return true;                                                    // SET_ROTATION
                case 2: if (data != IntPtr.Zero) *(byte*)data = 0; return true;        // GET_OVERSCAN
                case 3: if (data != IntPtr.Zero) *(byte*)data = 1; return true;        // GET_CAN_DUPE
                case 6:                                                                 // SET_MESSAGE
                    if (data != IntPtr.Zero) OnMessage?.Invoke(Utf8(Marshal.ReadIntPtr(data)));
                    return true;
                case 8: return true;                                                    // SET_PERFORMANCE_LEVEL
                case 9: Marshal.WriteIntPtr(data, Str(systemDir)); return true;        // GET_SYSTEM_DIRECTORY
                case 10:                                                                // SET_PIXEL_FORMAT
                    int f = *(int*)data;
                    if (f < 0 || f > 2) return false;
                    PixelFormat = f; return true;
                case 11: return true;                                                   // SET_INPUT_DESCRIPTORS
                case 14: return HwRender(data);                                         // SET_HW_RENDER
                case 15:                                                                // GET_VARIABLE
                {
                    var v = (Variable*)data;
                    string? key = Utf8(v->Key);
                    var o = Options.FirstOrDefault(x => x.Key == key);
                    if (o == null) { v->Value = IntPtr.Zero; return false; }
                    v->Value = Str(o.Value);
                    return true;
                }
                case 16:                                                                // SET_VARIABLES
                {
                    for (var v = (Variable*)data; v->Key != IntPtr.Zero; v++)
                    {
                        string? key = Utf8(v->Key); string spec = Utf8(v->Value) ?? "";
                        int semi = spec.IndexOf(';');
                        var values = (semi >= 0 ? spec[(semi + 1)..] : spec).Split('|').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
                        AddOption(key, semi >= 0 ? spec[..semi].Trim() : key, values, null, values.FirstOrDefault());
                    }
                    return true;
                }
                case 53: ReadOptions(data, 3); return true;                          // SET_CORE_OPTIONS (v1)
                case 54: if (data != IntPtr.Zero) ReadOptions(Marshal.ReadIntPtr(data), 3); return true;   // SET_CORE_OPTIONS_INTL: the English list
                case 67: if (data != IntPtr.Zero) ReadOptions(Marshal.ReadIntPtr(data, IntPtr.Size), 6); return true;   // SET_CORE_OPTIONS_V2: {categories, definitions}
                case 68:                                                                // SET_CORE_OPTIONS_V2_INTL: {us, local}
                    if (data != IntPtr.Zero && Marshal.ReadIntPtr(data) is IntPtr us && us != IntPtr.Zero) ReadOptions(Marshal.ReadIntPtr(us, IntPtr.Size), 6);
                    return true;
                case 17:                                                                // GET_VARIABLE_UPDATE
                    *(byte*)data = (byte)(OptionsChanged ? 1 : 0); OptionsChanged = false; return true;
                case 18: return true;                                                   // SET_SUPPORT_NO_GAME
                case 19: Marshal.WriteIntPtr(data, Str(dllPath)); return true;         // GET_LIBRETRO_PATH
                case 24: *(ulong*)data = (1 << 1) | (1 << 5) | (1 << 6); return true;  // GET_INPUT_DEVICE_CAPABILITIES: joypad, analog, pointer
                case 27:                                                                // GET_LOG_INTERFACE
                {
                    var cb = new LogFn((lvl, fmt) => { if (lvl >= 2) Log.Write($"core {Name}: {Utf8(fmt)?.Trim()}"); });
                    keep.Add(cb);
                    Marshal.WriteIntPtr(data, Marshal.GetFunctionPointerForDelegate(cb));
                    return true;
                }
                case 30: Marshal.WriteIntPtr(data, Str(systemDir)); return true;       // GET_CORE_ASSETS_DIRECTORY
                case 31: Marshal.WriteIntPtr(data, Str(saveDir)); return true;          // GET_SAVE_DIRECTORY
                case 32: Av = *(AvInfo*)data; GrowFramebuffer(); OnGeometry?.Invoke(); return true;   // SET_SYSTEM_AV_INFO
                case 35: return true;                                                   // SET_CONTROLLER_INFO
                case 36: return true;                                                   // SET_MEMORY_MAPS
                case 37: Av.Geometry = *(Geometry*)data; GrowFramebuffer(); OnGeometry?.Invoke(); return true;   // SET_GEOMETRY
                case 39: *(uint*)data = 0; return true;                                 // GET_LANGUAGE (English)
                case 52: *(uint*)data = 2; return true;                                 // GET_CORE_OPTIONS_VERSION: v2 (melonDS DS and Dolphin only list their settings this way)
                case 42: return false;                                                  // SET_SUPPORT_ACHIEVEMENTS: not taken, the launcher has no achievements (cores must not count on them)
                case 44:                                                                // SET_SERIALIZATION_QUIRKS (with the experimental bit: SET_HW_SHARED_CONTEXT)
                    if ((cmd & 0x10000) == 0 && data != IntPtr.Zero) Quirks = *(ulong*)data;
                    return true;

                case 47: if (data != IntPtr.Zero) *(int*)data = AvEnable; return true; // GET_AUDIO_VIDEO_ENABLE: both on, but not during run-ahead's hidden frames
                case 50: *(float*)data = 60f; return true;                              // GET_TARGET_REFRESH_RATE
                case 51: return true;                                                   // GET_INPUT_BITMASKS
                case 55: return true;                                                   // SET_CORE_OPTIONS_DISPLAY
                case 69: return true;                                                   // SET_CORE_OPTIONS_UPDATE_DISPLAY_CALLBACK (not called; options are all shown)
                case 56: *(uint*)data = 3; return true;                                 // GET_PREFERRED_HW_RENDER: OpenGL core (Vulkan works too, for cores that only have that)
                case 41:                                                                // GET_HW_RENDER_INTERFACE: Vulkan's, after context_reset
                    if (vk == null || vk.Interface == IntPtr.Zero || data == IntPtr.Zero) return false;
                    *(IntPtr*)data = vk.Interface; return true;
                case 43:                                                                // SET_HW_RENDER_CONTEXT_NEGOTIATION_INTERFACE
                    if (vk == null || data == IntPtr.Zero || *(int*)data != 0) return false;
                    vk.Negotiation = data; return true;
                case 73:                                                                // GET_HW_RENDER_CONTEXT_NEGOTIATION_INTERFACE_SUPPORT
                    if (data == IntPtr.Zero) return false;
                    *(uint*)((byte*)data + 4) = *(int*)data == 0 ? 2u : 0u;          // Vulkan: up to version 2; others: none
                    return true;
                case 60:                                                                // SET_MESSAGE_EXT
                    if (data != IntPtr.Zero)
                    {
                        string? m = Utf8(Marshal.ReadIntPtr(data));
                        if (*(int*)((byte*)data + 20) == 2) Log.Write($"core {Name}: {m}");   // target: log only
                        else OnMessage?.Invoke(m);
                    }
                    return true;
                case 61: *(uint*)data = 1; return true;                                 // GET_INPUT_MAX_USERS
                case 74: *(byte*)data = 1; return true;                                 // GET_JIT_CAPABLE
                default: if (TraceEnv) Log.Write($"core env {cmd & 0xFFFF}{((cmd & 0x10000) != 0 ? " (experimental)" : "")}: not handled"); return false;
            }
        }
        catch (Exception e) { Log.Write($"core env {cmd}: {e.Message}"); return false; }
    }

    IntPtr Str(string? s)
    {
        if (s == null) return IntPtr.Zero;
        var p = Marshal.StringToCoTaskMemUTF8(s);
        lock (strings) strings.Add(p);
        return p;
    }

    static string? Utf8(IntPtr p) => p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);

    public void Dispose()
    {
        // RetroArch's order: context_destroy, unload, deinit; the GL context stays current until the end.
        // Vulkan: the GPU finishes first, and the device goes after deinit (the core's destroy_device, then ours).
        try { vk?.Idle(); } catch (Exception e) { Log.Write("core vulkan idle: " + e.Message); }
        try { if (contextLive) hwDestroy?.Invoke(); } catch (Exception e) { Log.Write("core gpu destroy: " + e.Message); }
        contextLive = false;
        try { if (loaded) unload(); } catch (Exception e) { Log.Write("core unload: " + e.Message); }
        try { if (inited) deinit(); } catch (Exception e) { Log.Write("core deinit: " + e.Message); }
        loaded = inited = false;
        try { gl?.Dispose(); } catch { /* best-effort cleanup */ }
        gl = null;
        try { vk?.Dispose(); } catch (Exception e) { Log.Write("core vulkan close: " + e.Message); }
        vk = null;
        try { NativeLibrary.Free(lib); } catch { /* best-effort cleanup */ }
        if (romData != IntPtr.Zero) { Marshal.FreeHGlobal(romData); romData = IntPtr.Zero; }
        lock (strings) { foreach (var p in strings) Marshal.FreeCoTaskMem(p); strings.Clear(); }
    }
}

// Sound out through waveOut at the core's own sample rate (Windows resamples). Writing blocks while
// enough sound is queued, which also keeps the emulation at exactly the right speed.
public sealed unsafe class WaveOut : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] struct WAVEFORMATEX { public ushort wFormatTag, nChannels; public uint nSamplesPerSec, nAvgBytesPerSec; public ushort nBlockAlign, wBitsPerSample, cbSize; }
    [StructLayout(LayoutKind.Sequential)] struct WAVEHDR { public IntPtr lpData; public uint dwBufferLength, dwBytesRecorded; public IntPtr dwUser; public uint dwFlags, dwLoops; public IntPtr lpNext, reserved; }
    [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr h, uint dev, ref WAVEFORMATEX fmt, IntPtr cb, IntPtr inst, uint flags);
    [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr h, IntPtr hdr, uint size);
    [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr h, IntPtr hdr, uint size);
    [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr h, IntPtr hdr, uint size);
    [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr h);
    [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr h);
    [DllImport("winmm.dll")] static extern int waveOutPause(IntPtr h);
    [DllImport("winmm.dll")] static extern int waveOutRestart(IntPtr h);

    const int Count = 6;
    readonly IntPtr h;
    readonly IntPtr[] hdrs = new IntPtr[Count];
    readonly int chunkFrames;
    readonly short[] pending;
    int pendingFrames, next;
    public float Volume = 1f;

    public WaveOut(int rate)
    {
        chunkFrames = Math.Max(256, rate / 60);
        pending = new short[chunkFrames * 2];
        var fmt = new WAVEFORMATEX { wFormatTag = 1, nChannels = 2, nSamplesPerSec = (uint)rate, wBitsPerSample = 16, nBlockAlign = 4, nAvgBytesPerSec = (uint)rate * 4 };
        int r = waveOutOpen(out h, 0xFFFFFFFF, ref fmt, IntPtr.Zero, IntPtr.Zero, 0);
        if (r != 0) throw new InvalidOperationException("No sound device (waveOut error " + r + ")");
        for (int i = 0; i < Count; i++)
        {
            hdrs[i] = Marshal.AllocHGlobal(sizeof(WAVEHDR));
            var hd = (WAVEHDR*)hdrs[i];
            *hd = default;
            hd->lpData = Marshal.AllocHGlobal(chunkFrames * 4);
            hd->dwBufferLength = (uint)(chunkFrames * 4);
            hd->dwFlags = 1;   // WHDR_DONE: free
        }
    }

    public void Write(IntPtr data, int frames)
    {
        var src = (short*)data;
        int i = 0;
        while (i < frames)
        {
            int n = Math.Min(frames - i, chunkFrames - pendingFrames);
            for (int k = 0; k < n * 2; k++)
            {
                int v = (int)(src[i * 2 + k] * Volume);
                pending[pendingFrames * 2 + k] = (short)Math.Clamp(v, short.MinValue, short.MaxValue);
            }
            pendingFrames += n; i += n;
            if (pendingFrames == chunkFrames) Flush();
        }
    }

    void Flush()
    {
        var hd = (WAVEHDR*)hdrs[next];
        while ((hd->dwFlags & 1) == 0) System.Threading.Thread.Sleep(1);   // wait for a free buffer: this paces the game
        if ((hd->dwFlags & 2) != 0) waveOutUnprepareHeader(h, hdrs[next], (uint)sizeof(WAVEHDR));
        fixed (short* p = pending) Buffer.MemoryCopy(p, (void*)hd->lpData, chunkFrames * 4, chunkFrames * 4);
        hd->dwFlags = 0;
        waveOutPrepareHeader(h, hdrs[next], (uint)sizeof(WAVEHDR));
        waveOutWrite(h, hdrs[next], (uint)sizeof(WAVEHDR));
        pendingFrames = 0;
        next = (next + 1) % Count;
    }

    public void Pause() => waveOutPause(h);
    public void Restart() => waveOutRestart(h);

    public void Dispose()
    {
        waveOutReset(h);
        for (int i = 0; i < Count; i++)
        {
            var hd = (WAVEHDR*)hdrs[i];
            if ((hd->dwFlags & 2) != 0) waveOutUnprepareHeader(h, hdrs[i], (uint)sizeof(WAVEHDR));
            Marshal.FreeHGlobal(hd->lpData);
            Marshal.FreeHGlobal(hdrs[i]);
        }
        waveOutClose(h);
    }
}
