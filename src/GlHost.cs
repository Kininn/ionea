// OpenGL for libretro cores that draw with the GPU (3DS, GameCube/Wii). The core thread owns a hidden
// window with a GL context; the core draws into our framebuffer object and every frame we read the
// picture back into memory, so the launcher shows it exactly like a software-drawn core.
// The readback is asynchronous where the context allows it (OpenGL 3.2: pixel buffers and fences): each frame
// is copied into one of three pixel buffers by the GPU, and the one copied a frame earlier is shown, so the
// core never waits for the copy. The picture is one frame later on screen. Without them (or when anything
// fails) each frame is read directly, which waits for the GPU. IONEA_GL_READBACK=blocking forces the direct way.
// Everything here runs on the core thread only.
using System;
using System.Runtime.InteropServices;

namespace Ionea;

public sealed unsafe class GlHost : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    struct PIXELFORMATDESCRIPTOR
    {
        public ushort nSize, nVersion; public uint dwFlags; public byte iPixelType, cColorBits, cRedBits, cRedShift, cGreenBits, cGreenShift, cBlueBits, cBlueShift,
            cAlphaBits, cAlphaShift, cAccumBits, cAccumRedBits, cAccumGreenBits, cAccumBlueBits, cAccumAlphaBits, cDepthBits, cStencilBits, cAuxBuffers, iLayerType, bReserved;
        public uint dwLayerMask, dwVisibleMask, dwDamageMask;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowExW(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern int ChoosePixelFormat(IntPtr dc, ref PIXELFORMATDESCRIPTOR pfd);
    [DllImport("gdi32.dll")] static extern bool SetPixelFormat(IntPtr dc, int fmt, ref PIXELFORMATDESCRIPTOR pfd);
    [DllImport("opengl32.dll")] static extern IntPtr wglCreateContext(IntPtr dc);
    [DllImport("opengl32.dll")] static extern bool wglMakeCurrent(IntPtr dc, IntPtr ctx);
    [DllImport("opengl32.dll")] static extern bool wglDeleteContext(IntPtr ctx);
    [DllImport("opengl32.dll")] static extern IntPtr wglGetProcAddress(IntPtr name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr mod, IntPtr name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr LoadLibraryW(string name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate UIntPtr GetFbFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate IntPtr GetProcFn(IntPtr name);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate IntPtr CreateAttribsFn(IntPtr dc, IntPtr share, int* attribs);

    // GL entry points we use ourselves.
    delegate* unmanaged[Stdcall]<int, uint*, void> glGenFramebuffers, glGenRenderbuffers, glGenTextures, glDeleteFramebuffers, glDeleteRenderbuffers, glDeleteTextures;
    delegate* unmanaged[Stdcall]<uint, uint, void> glBindFramebuffer, glBindRenderbuffer, glBindTexture, glBindBuffer;
    delegate* unmanaged[Stdcall]<uint, uint, uint, uint, int, void> glFramebufferTexture2D;
    delegate* unmanaged[Stdcall]<uint, uint, int, int, void> glRenderbufferStorage;
    delegate* unmanaged[Stdcall]<uint, uint, uint, uint, void> glFramebufferRenderbuffer;
    delegate* unmanaged[Stdcall]<uint, uint> glCheckFramebufferStatus;
    delegate* unmanaged[Stdcall]<uint, int, int, int, int, int, uint, uint, void*, void> glTexImage2D;
    delegate* unmanaged[Stdcall]<uint, uint, int, void> glTexParameteri;
    delegate* unmanaged[Stdcall]<int, int, int, int, uint, uint, void*, void> glReadPixels;
    delegate* unmanaged[Stdcall]<uint, int*, void> glGetIntegerv;
    delegate* unmanaged[Stdcall]<uint, int, void> glPixelStorei;
    delegate* unmanaged[Stdcall]<uint, byte*> glGetString;
    // Asynchronous readback (OpenGL 3.0 buffers, 3.2 fences); null when the context lacks them.
    delegate* unmanaged[Stdcall]<int, uint*, void> glGenBuffers, glDeleteBuffers;
    delegate* unmanaged[Stdcall]<uint, nint, void*, uint, void> glBufferData;
    delegate* unmanaged[Stdcall]<uint, nint, nint, uint, void*> glMapBufferRange;
    delegate* unmanaged[Stdcall]<uint, byte> glUnmapBuffer;
    delegate* unmanaged[Stdcall]<uint, uint, IntPtr> glFenceSync;
    delegate* unmanaged[Stdcall]<IntPtr, uint, ulong, uint> glClientWaitSync;
    delegate* unmanaged[Stdcall]<IntPtr, void> glDeleteSync;

    const uint GL_FRAMEBUFFER = 0x8D40, GL_READ_FRAMEBUFFER = 0x8CA8, GL_READ_FRAMEBUFFER_BINDING = 0x8CAA, GL_RENDERBUFFER = 0x8D41,
        GL_COLOR_ATTACHMENT0 = 0x8CE0, GL_DEPTH_ATTACHMENT = 0x8D00, GL_DEPTH_STENCIL_ATTACHMENT = 0x821A, GL_DEPTH24_STENCIL8 = 0x88F0,
        GL_DEPTH_COMPONENT24 = 0x81A6, GL_TEXTURE_2D = 0x0DE1, GL_RGBA8 = 0x8058, GL_RGBA = 0x1908, GL_BGRA = 0x80E1, GL_UNSIGNED_BYTE = 0x1401,
        GL_FRAMEBUFFER_COMPLETE = 0x8CD5, GL_TEXTURE_MIN_FILTER = 0x2801, GL_TEXTURE_MAG_FILTER = 0x2800, GL_LINEAR = 0x2601,
        GL_PIXEL_PACK_BUFFER = 0x88EB, GL_PIXEL_PACK_BUFFER_BINDING = 0x88ED, GL_PACK_ALIGNMENT = 0x0D05, GL_PACK_ROW_LENGTH = 0x0D02,
        GL_VERSION = 0x1F02, GL_RENDERER = 0x1F01, GL_MAJOR_VERSION = 0x821B, GL_MINOR_VERSION = 0x821C,
        GL_STREAM_READ = 0x88E1, GL_MAP_READ_BIT = 0x0001, GL_SYNC_GPU_COMMANDS_COMPLETE = 0x9117, GL_SYNC_FLUSH_COMMANDS_BIT = 0x1,
        GL_TIMEOUT_EXPIRED = 0x911B, GL_WAIT_FAILED = 0x911D;

    readonly IntPtr opengl32 = LoadLibraryW("opengl32.dll");
    IntPtr hwnd, hdc, ctx;
    uint fbo, tex, rb;
    int fbW, fbH;
    readonly bool depth, stencil;
    public readonly bool BottomLeft;
    public readonly GetFbFn GetFb;
    public readonly GetProcFn GetProc;
    byte[] pixels = Array.Empty<byte>();
    public string Info = "";

    const int Slots = 3;
    const ulong NewestWaitNs = 2_000_000;   // how long the frame copied last time may still take (2 ms)
    readonly uint[] pbo = new uint[Slots];
    readonly IntPtr[] fence = new IntPtr[Slots];
    readonly int[] slotW = new int[Slots], slotH = new int[Slots];
    readonly ReadbackRing ring = new(Slots);
    int pboBytes;   // size of each pixel buffer, 0 = not made
    bool async;     // the asynchronous readback is available and working

    // type: 1 = OpenGL (compatibility), 3 = OpenGL core profile at major.minor.
    public GlHost(int type, uint major, uint minor, bool depth, bool stencil, bool bottomLeft, bool debug)
    {
        this.depth = depth; this.stencil = stencil; BottomLeft = bottomLeft;
        hwnd = CreateWindowExW(0, "STATIC", "core-gl", 0x80000000 /* WS_POPUP */, 0, 0, 16, 16, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException("GL: no window");
        hdc = GetDC(hwnd);
        var pfd = new PIXELFORMATDESCRIPTOR
        {
            nSize = (ushort)sizeof(PIXELFORMATDESCRIPTOR), nVersion = 1, dwFlags = 0x4 | 0x20 | 0x1 /* DRAW_TO_WINDOW | SUPPORT_OPENGL | DOUBLEBUFFER */,
            iPixelType = 0, cColorBits = 32, cAlphaBits = 8, cDepthBits = 24, cStencilBits = 8,
        };
        int pf = ChoosePixelFormat(hdc, ref pfd);
        if (pf == 0 || !SetPixelFormat(hdc, pf, ref pfd)) throw new InvalidOperationException("GL: no pixel format");
        var legacy = wglCreateContext(hdc);
        if (legacy == IntPtr.Zero || !wglMakeCurrent(hdc, legacy)) throw new InvalidOperationException("GL: can't create a context");
        ctx = legacy;

        var create = Proc("wglCreateContextAttribsARB");
        if (create != IntPtr.Zero)
        {
            var fn = Marshal.GetDelegateForFunctionPointer<CreateAttribsFn>(create);
            IntPtr Try(int maj, int min, bool core)
            {
                int* a = stackalloc int[9];
                a[0] = 0x2091; a[1] = maj; a[2] = 0x2092; a[3] = min;
                a[4] = 0x9126; a[5] = core ? 1 : 2;     // core / compatibility profile
                a[6] = 0x2094; a[7] = debug ? 1 : 0; a[8] = 0;
                return fn(hdc, IntPtr.Zero, a);
            }
            IntPtr better = IntPtr.Zero;
            if (type == 3)
            {
                if (major >= 3) better = Try((int)major, (int)minor, true);
                if (better == IntPtr.Zero) better = Try(4, 6, true);
                if (better == IntPtr.Zero) better = Try(3, 3, true);
            }
            else better = Try(4, 6, false);   // newest compatibility context
            if (better != IntPtr.Zero && wglMakeCurrent(hdc, better)) { wglDeleteContext(legacy); ctx = better; }
            else wglMakeCurrent(hdc, legacy);
        }

        T Get<T>(string n) where T : unmanaged { var p = Proc(n); if (p == IntPtr.Zero) throw new InvalidOperationException("GL: missing " + n); return *(T*)&p; }
        glGenFramebuffers = (delegate* unmanaged[Stdcall]<int, uint*, void>)Get<nint>("glGenFramebuffers");
        glGenRenderbuffers = (delegate* unmanaged[Stdcall]<int, uint*, void>)Get<nint>("glGenRenderbuffers");
        glGenTextures = (delegate* unmanaged[Stdcall]<int, uint*, void>)Get<nint>("glGenTextures");
        glDeleteFramebuffers = (delegate* unmanaged[Stdcall]<int, uint*, void>)Get<nint>("glDeleteFramebuffers");
        glDeleteRenderbuffers = (delegate* unmanaged[Stdcall]<int, uint*, void>)Get<nint>("glDeleteRenderbuffers");
        glDeleteTextures = (delegate* unmanaged[Stdcall]<int, uint*, void>)Get<nint>("glDeleteTextures");
        glBindFramebuffer = (delegate* unmanaged[Stdcall]<uint, uint, void>)Get<nint>("glBindFramebuffer");
        glBindRenderbuffer = (delegate* unmanaged[Stdcall]<uint, uint, void>)Get<nint>("glBindRenderbuffer");
        glBindTexture = (delegate* unmanaged[Stdcall]<uint, uint, void>)Get<nint>("glBindTexture");
        glBindBuffer = (delegate* unmanaged[Stdcall]<uint, uint, void>)Get<nint>("glBindBuffer");
        glFramebufferTexture2D = (delegate* unmanaged[Stdcall]<uint, uint, uint, uint, int, void>)Get<nint>("glFramebufferTexture2D");
        glRenderbufferStorage = (delegate* unmanaged[Stdcall]<uint, uint, int, int, void>)Get<nint>("glRenderbufferStorage");
        glFramebufferRenderbuffer = (delegate* unmanaged[Stdcall]<uint, uint, uint, uint, void>)Get<nint>("glFramebufferRenderbuffer");
        glCheckFramebufferStatus = (delegate* unmanaged[Stdcall]<uint, uint>)Get<nint>("glCheckFramebufferStatus");
        glTexImage2D = (delegate* unmanaged[Stdcall]<uint, int, int, int, int, int, uint, uint, void*, void>)Get<nint>("glTexImage2D");
        glTexParameteri = (delegate* unmanaged[Stdcall]<uint, uint, int, void>)Get<nint>("glTexParameteri");
        glReadPixels = (delegate* unmanaged[Stdcall]<int, int, int, int, uint, uint, void*, void>)Get<nint>("glReadPixels");
        glGetIntegerv = (delegate* unmanaged[Stdcall]<uint, int*, void>)Get<nint>("glGetIntegerv");
        glPixelStorei = (delegate* unmanaged[Stdcall]<uint, int, void>)Get<nint>("glPixelStorei");
        glGetString = (delegate* unmanaged[Stdcall]<uint, byte*>)Get<nint>("glGetString");
        Info = $"{Marshal.PtrToStringAnsi((IntPtr)glGetString(GL_VERSION))} on {Marshal.PtrToStringAnsi((IntPtr)glGetString(GL_RENDERER))}";
        async = Environment.GetEnvironmentVariable("IONEA_GL_READBACK") != "blocking" && LoadAsync();
        Log.Write($"core gl: readback {(async ? "asynchronous (pixel buffers, one frame later)" : "direct")}");

        GetFb = () => (UIntPtr)fbo;
        GetProc = name => Proc(name);
    }

    bool LoadAsync()
    {
        int major = 0, minor = 0;
        glGetIntegerv(GL_MAJOR_VERSION, &major);   // not known before 3.0: stays 0
        glGetIntegerv(GL_MINOR_VERSION, &minor);
        if (major * 10 + minor < 32) return false;
        nint gen = Proc("glGenBuffers"), del = Proc("glDeleteBuffers"), data = Proc("glBufferData"), map = Proc("glMapBufferRange"),
            unmap = Proc("glUnmapBuffer"), fs = Proc("glFenceSync"), cw = Proc("glClientWaitSync"), ds = Proc("glDeleteSync");
        if (gen == 0 || del == 0 || data == 0 || map == 0 || unmap == 0 || fs == 0 || cw == 0 || ds == 0) return false;
        glGenBuffers = (delegate* unmanaged[Stdcall]<int, uint*, void>)gen;
        glDeleteBuffers = (delegate* unmanaged[Stdcall]<int, uint*, void>)del;
        glBufferData = (delegate* unmanaged[Stdcall]<uint, nint, void*, uint, void>)data;
        glMapBufferRange = (delegate* unmanaged[Stdcall]<uint, nint, nint, uint, void*>)map;
        glUnmapBuffer = (delegate* unmanaged[Stdcall]<uint, byte>)unmap;
        glFenceSync = (delegate* unmanaged[Stdcall]<uint, uint, IntPtr>)fs;
        glClientWaitSync = (delegate* unmanaged[Stdcall]<IntPtr, uint, ulong, uint>)cw;
        glDeleteSync = (delegate* unmanaged[Stdcall]<IntPtr, void>)ds;
        return true;
    }

    IntPtr Proc(string name)
    {
        var p = Marshal.StringToHGlobalAnsi(name);
        try { return Proc(p); } finally { Marshal.FreeHGlobal(p); }
    }

    IntPtr Proc(IntPtr name)
    {
        var p = wglGetProcAddress(name);
        long v = (long)p;
        if (v >= -1 && v <= 3) p = GetProcAddress(opengl32, name);   // GL 1.1 functions come from opengl32.dll itself
        return p;
    }

    // (Re)make the framebuffer the core draws into, at least w x h.
    public void EnsureFramebuffer(int w, int h)
    {
        w = Math.Max(w, 1); h = Math.Max(h, 1);
        if (fbo != 0 && w <= fbW && h <= fbH) return;
        w = Math.Max(w, fbW); h = Math.Max(h, fbH);
        DeleteFramebuffer();
        uint id;
        glGenTextures(1, &id); tex = id;
        glBindTexture(GL_TEXTURE_2D, tex);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, (int)GL_LINEAR);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, (int)GL_LINEAR);
        glTexImage2D(GL_TEXTURE_2D, 0, (int)GL_RGBA8, w, h, 0, GL_RGBA, GL_UNSIGNED_BYTE, null);
        glBindTexture(GL_TEXTURE_2D, 0);
        glGenFramebuffers(1, &id); fbo = id;
        glBindFramebuffer(GL_FRAMEBUFFER, fbo);
        glFramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, tex, 0);
        if (depth)
        {
            glGenRenderbuffers(1, &id); rb = id;
            glBindRenderbuffer(GL_RENDERBUFFER, rb);
            glRenderbufferStorage(GL_RENDERBUFFER, stencil ? GL_DEPTH24_STENCIL8 : GL_DEPTH_COMPONENT24, w, h);
            glFramebufferRenderbuffer(GL_FRAMEBUFFER, stencil ? GL_DEPTH_STENCIL_ATTACHMENT : GL_DEPTH_ATTACHMENT, GL_RENDERBUFFER, rb);
            glBindRenderbuffer(GL_RENDERBUFFER, 0);
        }
        uint st = glCheckFramebufferStatus(GL_FRAMEBUFFER);
        glBindFramebuffer(GL_FRAMEBUFFER, 0);
        if (st != GL_FRAMEBUFFER_COMPLETE) throw new InvalidOperationException($"GL: framebuffer incomplete (0x{st:X})");
        fbW = w; fbH = h;
        Log.Write($"core gl: framebuffer {w}x{h}");
    }

    // Hand the core's picture (w x h at the framebuffer's lower-left) to show as BGRA rows: show(data, w, h, pitch),
    // with a negative pitch when the rows are stored bottom-up, so the one copy show makes turns it the right way up.
    // Asynchronously it is the picture of an earlier frame, and on some frames there is none (show isn't called).
    public void Read(int w, int h, Action<IntPtr, int, int, int> show)
    {
        int oldFb, oldPbo, oldAlign, oldRow;
        glGetIntegerv(GL_READ_FRAMEBUFFER_BINDING, &oldFb);
        glGetIntegerv(GL_PIXEL_PACK_BUFFER_BINDING, &oldPbo);
        glGetIntegerv(GL_PACK_ALIGNMENT, &oldAlign);
        glGetIntegerv(GL_PACK_ROW_LENGTH, &oldRow);
        glBindFramebuffer(GL_READ_FRAMEBUFFER, fbo);
        glPixelStorei(GL_PACK_ALIGNMENT, 4);
        glPixelStorei(GL_PACK_ROW_LENGTH, 0);
        try
        {
            if (!async || !ReadAsync(w, h, show)) ReadDirect(w, h, show);
        }
        finally
        {
            glBindFramebuffer(GL_READ_FRAMEBUFFER, (uint)oldFb);
            glBindBuffer(GL_PIXEL_PACK_BUFFER, (uint)oldPbo);
            glPixelStorei(GL_PACK_ALIGNMENT, oldAlign);
            glPixelStorei(GL_PACK_ROW_LENGTH, oldRow);
        }
    }

    // Waits for the GPU to finish the frame, then copies it.
    void ReadDirect(int w, int h, Action<IntPtr, int, int, int> show)
    {
        int n = w * h * 4;
        if (pixels.Length != n) pixels = new byte[n];
        glBindBuffer(GL_PIXEL_PACK_BUFFER, 0);
        fixed (byte* p = pixels)
        {
            glReadPixels(0, 0, w, h, GL_BGRA, GL_UNSIGNED_BYTE, p);
            var (start, pitch) = FrameRows.TopDown((IntPtr)p, w * 4, h, BottomLeft);
            show(start, w, h, pitch);
        }
    }

    // Starts this frame's copy into the next pixel buffer and shows the newest earlier one the GPU has finished.
    // False (before anything is shown) when it can't: then the direct way takes this frame and all later ones.
    bool ReadAsync(int w, int h, Action<IntPtr, int, int, int> show)
    {
        int n = w * h * 4;
        if (n > pboBytes && !MakeBuffers(n, w, h)) return false;
        int s = ring.Begin();
        if (fence[s] != IntPtr.Zero) { glDeleteSync(fence[s]); fence[s] = IntPtr.Zero; }
        glBindBuffer(GL_PIXEL_PACK_BUFFER, pbo[s]);
        glReadPixels(0, 0, w, h, GL_BGRA, GL_UNSIGNED_BYTE, null);   // into the buffer: returns at once
        fence[s] = glFenceSync(GL_SYNC_GPU_COMMANDS_COMPLETE, 0);
        if (fence[s] == IntPtr.Zero) return Fail("no fence");
        slotW[s] = w; slotH[s] = h;

        Span<int> waiting = stackalloc int[Slots];
        int k = ring.Waiting(waiting);
        for (int i = 0; i < k; i++)
        {
            int t = waiting[i];
            if (fence[t] == IntPtr.Zero) continue;
            uint r = glClientWaitSync(fence[t], GL_SYNC_FLUSH_COMMANDS_BIT, i == 0 ? NewestWaitNs : 0);
            if (r == GL_TIMEOUT_EXPIRED) continue;   // not done yet: an older finished one, else nothing this frame
            if (r == GL_WAIT_FAILED) return Fail("fence wait failed");
            glDeleteSync(fence[t]); fence[t] = IntPtr.Zero;
            int tw = slotW[t], th = slotH[t];
            glBindBuffer(GL_PIXEL_PACK_BUFFER, pbo[t]);
            void* p = glMapBufferRange(GL_PIXEL_PACK_BUFFER, 0, (nint)tw * th * 4, GL_MAP_READ_BIT);
            if (p == null) return Fail("can't map a pixel buffer");
            ring.Shown(t);
            try
            {
                var (start, pitch) = FrameRows.TopDown((IntPtr)p, tw * 4, th, BottomLeft);
                show(start, tw, th, pitch);
            }
            finally { glUnmapBuffer(GL_PIXEL_PACK_BUFFER); }
            return true;
        }
        return true;   // nothing finished yet (the first frame, or the GPU is behind): the picture on screen stays
    }

    // Pixel buffers of n bytes each, made again when a bigger picture comes (smaller ones fit in them as they are).
    bool MakeBuffers(int n, int w, int h)
    {
        FreeBuffers();
        uint* ids = stackalloc uint[Slots];
        glGenBuffers(Slots, ids);
        for (int i = 0; i < Slots; i++) pbo[i] = ids[i];
        for (int i = 0; i < Slots; i++)
        {
            if (pbo[i] == 0) return Fail("no pixel buffers");
            glBindBuffer(GL_PIXEL_PACK_BUFFER, pbo[i]);
            glBufferData(GL_PIXEL_PACK_BUFFER, n, null, GL_STREAM_READ);
        }
        pboBytes = n;
        Log.Write($"core gl: pixel buffers for {w}x{h}");
        return true;
    }

    bool Fail(string why)
    {
        Log.Write($"core gl: asynchronous readback off ({why}), each frame is read directly");
        async = false;
        FreeBuffers();
        return false;
    }

    void FreeBuffers()
    {
        for (int i = 0; i < Slots; i++)
        {
            if (fence[i] != IntPtr.Zero) { glDeleteSync(fence[i]); fence[i] = IntPtr.Zero; }
            if (pbo[i] != 0) { uint id = pbo[i]; glDeleteBuffers(1, &id); pbo[i] = 0; }
        }
        pboBytes = 0;
        ring.Clear();
    }

    void DeleteFramebuffer()
    {
        uint id;
        if (fbo != 0) { id = fbo; glDeleteFramebuffers(1, &id); fbo = 0; }
        if (rb != 0) { id = rb; glDeleteRenderbuffers(1, &id); rb = 0; }
        if (tex != 0) { id = tex; glDeleteTextures(1, &id); tex = 0; }
    }

    public void Dispose()
    {
        try { if (ctx != IntPtr.Zero) { if (glDeleteBuffers != null) FreeBuffers(); DeleteFramebuffer(); } } catch { /* best-effort cleanup; the context may be gone */ }
        wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
        if (ctx != IntPtr.Zero) { wglDeleteContext(ctx); ctx = IntPtr.Zero; }
        if (hdc != IntPtr.Zero) { ReleaseDC(hwnd, hdc); hdc = IntPtr.Zero; }
        if (hwnd != IntPtr.Zero) { DestroyWindow(hwnd); hwnd = IntPtr.Zero; }
    }
}
