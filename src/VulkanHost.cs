// Vulkan for libretro cores that draw with the GPU (SET_HW_RENDER with RETRO_HW_CONTEXT_VULKAN), e.g. the
// Vulkan renderers of N64 and PlayStation cores. There is no window or swapchain: the core draws into its own
// image and hands it over (set_image); every frame the GPU copies it into one of three host-visible buffers with
// a fence, and the newest earlier copy that has finished is shown, so the launcher shows it exactly like a
// software-drawn core (filters, screenshots, display sync, fast-forward). The picture is one frame later on
// screen, like the OpenGL readback (GlHost.cs). Future: share the image with Direct3D (zero copy) instead.
// The three buffers are the core's sync indexes (get_sync_index): before a frame runs, the work handed in for
// its index three frames ago is waited for, so the core may reuse what it used then.
// The instance and device come from the core's negotiation interface when it has one (create_instance,
// create_device2 or create_device), else Ionea makes them. vulkan-1.dll is loaded only when a core asks.
// All calls but the ones the core makes from its own threads (lock_queue, unlock_queue) are on the core thread.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using static Ionea.Loc;

namespace Ionea;

public sealed unsafe class VulkanHost : IDisposable
{
    // ---- Vulkan structs (x64 layout) ----
    [StructLayout(LayoutKind.Sequential)] struct VkApplicationInfo { public int sType; public IntPtr pNext, pApplicationName; public uint applicationVersion; public IntPtr pEngineName; public uint engineVersion, apiVersion; }
    [StructLayout(LayoutKind.Sequential)] struct VkInstanceCreateInfo { public int sType; public IntPtr pNext; public uint flags; public IntPtr pApplicationInfo; public uint enabledLayerCount; public IntPtr ppEnabledLayerNames; public uint enabledExtensionCount; public IntPtr ppEnabledExtensionNames; }
    [StructLayout(LayoutKind.Sequential)] struct VkDeviceQueueCreateInfo { public int sType; public IntPtr pNext; public uint flags, queueFamilyIndex, queueCount; public IntPtr pQueuePriorities; }
    [StructLayout(LayoutKind.Sequential)] struct VkDeviceCreateInfo { public int sType; public IntPtr pNext; public uint flags, queueCreateInfoCount; public IntPtr pQueueCreateInfos; public uint enabledLayerCount; public IntPtr ppEnabledLayerNames; public uint enabledExtensionCount; public IntPtr ppEnabledExtensionNames; public IntPtr pEnabledFeatures; }
    [StructLayout(LayoutKind.Sequential)] struct VkCommandPoolCreateInfo { public int sType; public IntPtr pNext; public uint flags, queueFamilyIndex; }
    [StructLayout(LayoutKind.Sequential)] struct VkCommandBufferAllocateInfo { public int sType; public IntPtr pNext; public ulong commandPool; public int level; public uint commandBufferCount; }
    [StructLayout(LayoutKind.Sequential)] struct VkCommandBufferBeginInfo { public int sType; public IntPtr pNext; public uint flags; public IntPtr pInheritanceInfo; }
    [StructLayout(LayoutKind.Sequential)] struct VkFenceCreateInfo { public int sType; public IntPtr pNext; public uint flags; }
    [StructLayout(LayoutKind.Sequential)] struct VkBufferCreateInfo { public int sType; public IntPtr pNext; public uint flags; public ulong size; public uint usage; public int sharingMode; public uint queueFamilyIndexCount; public IntPtr pQueueFamilyIndices; }
    [StructLayout(LayoutKind.Sequential)] struct VkMemoryRequirements { public ulong size, alignment; public uint memoryTypeBits; }
    [StructLayout(LayoutKind.Sequential)] struct VkMemoryAllocateInfo { public int sType; public IntPtr pNext; public ulong allocationSize; public uint memoryTypeIndex; }
    [StructLayout(LayoutKind.Sequential)] struct VkMappedMemoryRange { public int sType; public IntPtr pNext; public ulong memory, offset, size; }
    [StructLayout(LayoutKind.Sequential)] struct VkSubmitInfo { public int sType; public IntPtr pNext; public uint waitSemaphoreCount; public IntPtr pWaitSemaphores, pWaitDstStageMask; public uint commandBufferCount; public IntPtr pCommandBuffers; public uint signalSemaphoreCount; public IntPtr pSignalSemaphores; }
    [StructLayout(LayoutKind.Sequential)] struct VkImageSubresourceRange { public uint aspectMask, baseMipLevel, levelCount, baseArrayLayer, layerCount; }
    [StructLayout(LayoutKind.Sequential)] struct VkImageMemoryBarrier { public int sType; public IntPtr pNext; public uint srcAccessMask, dstAccessMask; public int oldLayout, newLayout; public uint srcQueueFamilyIndex, dstQueueFamilyIndex; public ulong image; public VkImageSubresourceRange subresourceRange; }
    [StructLayout(LayoutKind.Sequential)] struct VkBufferMemoryBarrier { public int sType; public IntPtr pNext; public uint srcAccessMask, dstAccessMask, srcQueueFamilyIndex, dstQueueFamilyIndex; public ulong buffer, offset, size; }
    [StructLayout(LayoutKind.Sequential)] struct VkImageSubresourceLayers { public uint aspectMask, mipLevel, baseArrayLayer, layerCount; }
    [StructLayout(LayoutKind.Sequential)] struct VkBufferImageCopy { public ulong bufferOffset; public uint bufferRowLength, bufferImageHeight; public VkImageSubresourceLayers imageSubresource; public int x, y, z; public uint width, height, depth; }
    [StructLayout(LayoutKind.Sequential)] struct VkImageViewCreateInfo { public int sType; public IntPtr pNext; public uint flags; public ulong image; public int viewType, format, r, g, b, a; public VkImageSubresourceRange subresourceRange; }

    // ---- libretro_vulkan.h ----
    [StructLayout(LayoutKind.Sequential)] struct RetroVkImage { public ulong imageView; public int imageLayout; public VkImageViewCreateInfo createInfo; }
    [StructLayout(LayoutKind.Sequential)] struct RetroVkContext { public IntPtr gpu, device, queue; public uint queueFamilyIndex; public IntPtr presentationQueue; public uint presentationQueueFamilyIndex; }
    [StructLayout(LayoutKind.Sequential)] struct RetroVkNegotiation { public int interfaceType; public uint interfaceVersion; public IntPtr getApplicationInfo, createDevice, destroyDevice, createInstance, createDevice2; }
    [StructLayout(LayoutKind.Sequential)]
    struct RetroVkInterface
    {
        public int interfaceType; public uint interfaceVersion;
        public IntPtr handle, instance, gpu, device, getDeviceProcAddr, getInstanceProcAddr, queue;
        public uint queueIndex;
        public IntPtr setImage, getSyncIndex, getSyncIndexMask, setCommandBuffers, waitSyncIndex, lockQueue, unlockQueue, setSignalSemaphore;
    }

    const int Slots = 3;
    const ulong FenceWaitNs = 5_000_000_000;   // longer than this for a frame: the GPU has stopped responding
    const ulong NewestWaitNs = 2_000_000;      // how long the frame copied last time may still take (2 ms)
    const int VK_SUCCESS = 0, VK_NOT_READY = 1, VK_TIMEOUT = 2, VK_ERROR_DEVICE_LOST = -4;
    const uint Ignored = 0xFFFFFFFF;           // VK_QUEUE_FAMILY_IGNORED
    const int LayoutGeneral = 1, LayoutTransferSrc = 6;
    const uint StageTransfer = 0x1000, StageHost = 0x4000, StageAll = 0x10000;
    const uint AccessColorWrite = 0x100, AccessTransferRead = 0x800, AccessTransferWrite = 0x1000, AccessHostRead = 0x2000, AccessMemoryRead = 0x8000, AccessMemoryWrite = 0x10000;

    // ---- the loader: vulkan-1.dll, loaded once and kept ----
    static IntPtr loader;
    static delegate* unmanaged<IntPtr, byte*, IntPtr> gipa;   // vkGetInstanceProcAddr

    // Null when Vulkan can be used on this PC, else why not.
    public static string? Unavailable()
    {
        if (loader != IntPtr.Zero) return null;
        if (!NativeLibrary.TryLoad("vulkan-1.dll", out var h)) return "vulkan-1.dll not found";
        if (!NativeLibrary.TryGetExport(h, "vkGetInstanceProcAddr", out var p)) { NativeLibrary.Free(h); return "vulkan-1.dll has no vkGetInstanceProcAddr"; }
        gipa = (delegate* unmanaged<IntPtr, byte*, IntPtr>)p;
        loader = h;
        return null;
    }

    static IntPtr Gipa(IntPtr instance, string name)
    {
        fixed (byte* n = Encoding.ASCII.GetBytes(name + "\0")) return gipa(instance, n);
    }

    // ---- instance and device entry points ----
    delegate* unmanaged<IntPtr, void*, void> vkDestroyInstance, vkDestroyDevice;
    delegate* unmanaged<IntPtr, uint*, IntPtr*, int> vkEnumeratePhysicalDevices;
    delegate* unmanaged<IntPtr, byte*, void> vkGetPhysicalDeviceProperties, vkGetPhysicalDeviceMemoryProperties;
    delegate* unmanaged<IntPtr, uint*, byte*, void> vkGetPhysicalDeviceQueueFamilyProperties;
    delegate* unmanaged<IntPtr, VkDeviceCreateInfo*, void*, IntPtr*, int> vkCreateDevice;
    delegate* unmanaged<IntPtr, byte*, IntPtr> vkGetDeviceProcAddr;
    delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void> vkGetDeviceQueue;
    delegate* unmanaged<IntPtr, int> vkDeviceWaitIdle, vkEndCommandBuffer;
    delegate* unmanaged<IntPtr, uint, VkSubmitInfo*, ulong, int> vkQueueSubmit;
    delegate* unmanaged<IntPtr, VkCommandPoolCreateInfo*, void*, ulong*, int> vkCreateCommandPool;
    delegate* unmanaged<IntPtr, ulong, void*, void> vkDestroyCommandPool, vkDestroyFence, vkDestroyBuffer, vkFreeMemory;
    delegate* unmanaged<IntPtr, VkCommandBufferAllocateInfo*, IntPtr*, int> vkAllocateCommandBuffers;
    delegate* unmanaged<IntPtr, uint, int> vkResetCommandBuffer;
    delegate* unmanaged<IntPtr, VkCommandBufferBeginInfo*, int> vkBeginCommandBuffer;
    delegate* unmanaged<IntPtr, uint, uint, uint, uint, void*, uint, VkBufferMemoryBarrier*, uint, VkImageMemoryBarrier*, void> vkCmdPipelineBarrier;
    delegate* unmanaged<IntPtr, ulong, int, ulong, uint, VkBufferImageCopy*, void> vkCmdCopyImageToBuffer;
    delegate* unmanaged<IntPtr, VkFenceCreateInfo*, void*, ulong*, int> vkCreateFence;
    delegate* unmanaged<IntPtr, uint, ulong*, int> vkResetFences;
    delegate* unmanaged<IntPtr, uint, ulong*, uint, ulong, int> vkWaitForFences;
    delegate* unmanaged<IntPtr, ulong, int> vkGetFenceStatus;
    delegate* unmanaged<IntPtr, VkBufferCreateInfo*, void*, ulong*, int> vkCreateBuffer;
    delegate* unmanaged<IntPtr, ulong, VkMemoryRequirements*, void> vkGetBufferMemoryRequirements;
    delegate* unmanaged<IntPtr, VkMemoryAllocateInfo*, void*, ulong*, int> vkAllocateMemory;
    delegate* unmanaged<IntPtr, ulong, ulong, ulong, int> vkBindBufferMemory;
    delegate* unmanaged<IntPtr, ulong, ulong, ulong, uint, void**, int> vkMapMemory;
    delegate* unmanaged<IntPtr, ulong, void> vkUnmapMemory;
    delegate* unmanaged<IntPtr, uint, VkMappedMemoryRange*, int> vkInvalidateMappedMemoryRanges;

    readonly string core;
    GCHandle self;
    IntPtr instance, gpu, device, queue;
    uint family;
    RetroVkInterface* iface;
    IntPtr appName;
    bool made, disposed;
    readonly uint[] memFlags = new uint[32];
    int memCount;

    // Per slot (sync index).
    ulong pool;
    readonly IntPtr[] cmd = new IntPtr[Slots];
    readonly ulong[] fence = new ulong[Slots], buf = new ulong[Slots], mem = new ulong[Slots];
    readonly byte*[] map = new byte*[Slots];
    readonly int[] slotW = new int[Slots], slotH = new int[Slots], slotKind = new int[Slots];
    readonly VkFrameRing frames = new(Slots);
    long bufBytes;
    bool coherent, submitted, warnedFormat;
    byte[] scratch = Array.Empty<byte>();

    // What the core handed over (its own thread calls are only lock_queue / unlock_queue).
    RetroVkImage image;
    bool haveImage;
    uint srcFamily = Ignored;
    readonly List<ulong> waitSems = new();
    readonly List<IntPtr> coreCmds = new();
    ulong signalSem;
    readonly SemaphoreSlim queueLock = new(1, 1);

    public IntPtr Negotiation;   // the core's retro_hw_render_context_negotiation_interface_vulkan, or zero
    public string Info = "";
    public string? Lost;         // set when the GPU stopped responding: the game ends with this
    public IntPtr Interface => iface != null ? (IntPtr)iface : IntPtr.Zero;   // GET_HW_RENDER_INTERFACE, after Create

    public VulkanHost(string coreName)
    {
        core = coreName;
        self = GCHandle.Alloc(this);
    }

    IntPtr Handle => GCHandle.ToIntPtr(self);
    static VulkanHost? From(IntPtr h) => h == IntPtr.Zero ? null : GCHandle.FromIntPtr(h).Target as VulkanHost;

    // ---- making the instance and the device (after the game is loaded, before context_reset) ----
    public void Create()
    {
        var neg = (RetroVkNegotiation*)Negotiation;
        if (neg != null && neg->interfaceType != 0) neg = null;   // not the Vulkan negotiation interface
        bool v2 = neg != null && neg->interfaceVersion >= 2;
        Log.Write($"core vulkan: {(neg == null ? "no negotiation interface" : $"negotiation interface v{neg->interfaceVersion}")}");

        appName = Marshal.StringToHGlobalAnsi("Ionea");
        bool v11 = Gipa(IntPtr.Zero, "vkEnumerateInstanceVersion") != IntPtr.Zero;
        var own = new VkApplicationInfo { sType = 0, pApplicationName = appName, pEngineName = appName, apiVersion = v11 ? (1u << 22) | (1u << 12) : 1u << 22 };
        VkApplicationInfo* app = null;
        if (neg != null && neg->getApplicationInfo != IntPtr.Zero) app = ((delegate* unmanaged<VkApplicationInfo*>)neg->getApplicationInfo)();
        if (app == null) app = &own;

        if (v2 && neg->createInstance != IntPtr.Zero)
            instance = ((delegate* unmanaged<IntPtr, VkApplicationInfo*, IntPtr, IntPtr, IntPtr>)neg->createInstance)(
                (IntPtr)gipa, app, (IntPtr)(delegate* unmanaged<IntPtr, VkInstanceCreateInfo*, IntPtr>)&InstanceWrapper, Handle);
        else
        {
            var ci = new VkInstanceCreateInfo { sType = 1, pApplicationInfo = (IntPtr)app };
            instance = MakeInstance(&ci);
        }
        if (instance == IntPtr.Zero) throw new InvalidOperationException("no Vulkan instance");
        nint I(string n) { var p = Gipa(instance, n); if (p == IntPtr.Zero) throw new InvalidOperationException("Vulkan: missing " + n); return p; }
        vkDestroyInstance = (delegate* unmanaged<IntPtr, void*, void>)I("vkDestroyInstance");
        vkEnumeratePhysicalDevices = (delegate* unmanaged<IntPtr, uint*, IntPtr*, int>)I("vkEnumeratePhysicalDevices");
        vkGetPhysicalDeviceProperties = (delegate* unmanaged<IntPtr, byte*, void>)I("vkGetPhysicalDeviceProperties");
        vkGetPhysicalDeviceMemoryProperties = (delegate* unmanaged<IntPtr, byte*, void>)I("vkGetPhysicalDeviceMemoryProperties");
        vkGetPhysicalDeviceQueueFamilyProperties = (delegate* unmanaged<IntPtr, uint*, byte*, void>)I("vkGetPhysicalDeviceQueueFamilyProperties");
        vkCreateDevice = (delegate* unmanaged<IntPtr, VkDeviceCreateInfo*, void*, IntPtr*, int>)I("vkCreateDevice");
        vkGetDeviceProcAddr = (delegate* unmanaged<IntPtr, byte*, IntPtr>)I("vkGetDeviceProcAddr");

        gpu = PickGpu();

        // The device: the core's way when it has one, else ours.
        var ctx = new RetroVkContext();
        bool byCore = false;
        if (v2 && neg->createDevice2 != IntPtr.Zero)
        {
            var f = (delegate* unmanaged<RetroVkContext*, IntPtr, IntPtr, ulong, IntPtr, IntPtr, IntPtr, byte>)neg->createDevice2;
            var wrap = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, VkDeviceCreateInfo*, IntPtr>)&DeviceWrapper;
            byCore = f(&ctx, instance, gpu, 0, (IntPtr)gipa, wrap, Handle) != 0;
            if (!byCore) { Log.Write("core vulkan: the core can't use that GPU, letting it choose"); ctx = new RetroVkContext(); byCore = f(&ctx, instance, IntPtr.Zero, 0, (IntPtr)gipa, wrap, Handle) != 0; }
        }
        else if (neg != null && neg->createDevice != IntPtr.Zero)
        {
            byte* features = stackalloc byte[256];   // VkPhysicalDeviceFeatures (220 bytes): nothing required by us
            new Span<byte>(features, 256).Clear();
            var f = (delegate* unmanaged<RetroVkContext*, IntPtr, IntPtr, ulong, IntPtr, IntPtr, uint, IntPtr, uint, void*, byte>)neg->createDevice;
            byCore = f(&ctx, instance, gpu, 0, (IntPtr)gipa, IntPtr.Zero, 0, IntPtr.Zero, 0, features) != 0;
        }
        if (byCore && (ctx.device == IntPtr.Zero || ctx.queue == IntPtr.Zero)) throw new InvalidOperationException("the core made no Vulkan device");
        if (byCore) { if (ctx.gpu != IntPtr.Zero) gpu = ctx.gpu; device = ctx.device; queue = ctx.queue; family = ctx.queueFamilyIndex; }
        else
        {
            if (neg != null) Log.Write("core vulkan: the core made no device, Ionea makes it");
            int fam = GraphicsFamily(gpu);
            float priority = 1f;
            var q = new VkDeviceQueueCreateInfo { sType = 2, queueFamilyIndex = (uint)fam, queueCount = 1, pQueuePriorities = (IntPtr)(&priority) };
            var di = new VkDeviceCreateInfo { sType = 3, queueCreateInfoCount = 1, pQueueCreateInfos = (IntPtr)(&q) };
            IntPtr dev;
            int r = vkCreateDevice(gpu, &di, null, &dev);
            if (r != VK_SUCCESS) throw new InvalidOperationException($"no Vulkan device (error {r})");
            device = dev; family = (uint)fam;
        }
        made = true;
        LoadDevice();
        if (queue == IntPtr.Zero) { IntPtr qq; vkGetDeviceQueue(device, family, 0, &qq); queue = qq; }

        byte* mp = stackalloc byte[520];   // VkPhysicalDeviceMemoryProperties
        vkGetPhysicalDeviceMemoryProperties(gpu, mp);
        memCount = (int)Math.Min(32u, *(uint*)mp);
        for (int i = 0; i < memCount; i++) memFlags[i] = *(uint*)(mp + 4 + i * 8);

        var pi = new VkCommandPoolCreateInfo { sType = 39, flags = 2 /* RESET_COMMAND_BUFFER */, queueFamilyIndex = family };
        ulong p;
        Check(vkCreateCommandPool(device, &pi, null, &p), "command pool"); pool = p;
        var ai = new VkCommandBufferAllocateInfo { sType = 40, commandPool = pool, level = 0, commandBufferCount = Slots };
        IntPtr* cbs = stackalloc IntPtr[Slots];
        Check(vkAllocateCommandBuffers(device, &ai, cbs), "command buffers");
        for (int i = 0; i < Slots; i++)
        {
            cmd[i] = cbs[i];
            var fi = new VkFenceCreateInfo { sType = 8 };
            ulong fe;
            Check(vkCreateFence(device, &fi, null, &fe), "fence"); fence[i] = fe;
        }

        iface = (RetroVkInterface*)NativeMemory.AllocZeroed((nuint)sizeof(RetroVkInterface));
        *iface = new RetroVkInterface
        {
            interfaceType = 0, interfaceVersion = 5, handle = Handle, instance = instance, gpu = gpu, device = device,
            getDeviceProcAddr = (IntPtr)vkGetDeviceProcAddr, getInstanceProcAddr = (IntPtr)gipa, queue = queue, queueIndex = family,
            setImage = (IntPtr)(delegate* unmanaged<IntPtr, RetroVkImage*, uint, ulong*, uint, void>)&SetImage,
            getSyncIndex = (IntPtr)(delegate* unmanaged<IntPtr, uint>)&GetSyncIndex,
            getSyncIndexMask = (IntPtr)(delegate* unmanaged<IntPtr, uint>)&GetSyncIndexMask,
            setCommandBuffers = (IntPtr)(delegate* unmanaged<IntPtr, uint, IntPtr*, void>)&SetCommandBuffers,
            waitSyncIndex = (IntPtr)(delegate* unmanaged<IntPtr, void>)&WaitSyncIndex,
            lockQueue = (IntPtr)(delegate* unmanaged<IntPtr, void>)&LockQueue,
            unlockQueue = (IntPtr)(delegate* unmanaged<IntPtr, void>)&UnlockQueue,
            setSignalSemaphore = (IntPtr)(delegate* unmanaged<IntPtr, ulong, void>)&SetSignalSemaphore,
        };
        byte* props = stackalloc byte[1024];   // VkPhysicalDeviceProperties (824 bytes)
        vkGetPhysicalDeviceProperties(gpu, props);
        uint api = *(uint*)props;
        Info = $"{Marshal.PtrToStringUTF8((IntPtr)(props + 20))}, Vulkan {api >> 22}.{(api >> 12) & 0x3FF}, device made by {(byCore ? "the core" : "Ionea")}";
    }

    IntPtr MakeInstance(VkInstanceCreateInfo* ci)
    {
        var create = (delegate* unmanaged<VkInstanceCreateInfo*, void*, IntPtr*, int>)Gipa(IntPtr.Zero, "vkCreateInstance");
        if (create == null) return IntPtr.Zero;
        IntPtr inst;
        int r = create(ci, null, &inst);
        if (r != VK_SUCCESS) { Log.Write($"core vulkan: vkCreateInstance error {r}"); return IntPtr.Zero; }
        return inst;
    }

    // The best GPU: a graphics card before a built-in one; never a software one.
    IntPtr PickGpu()
    {
        uint n = 0;
        vkEnumeratePhysicalDevices(instance, &n, null);
        if (n == 0) throw new InvalidOperationException("no Vulkan graphics card");
        IntPtr* list = stackalloc IntPtr[(int)n];
        vkEnumeratePhysicalDevices(instance, &n, list);
        byte* props = stackalloc byte[1024];
        IntPtr best = IntPtr.Zero; int bestScore = 0;
        for (int i = 0; i < n; i++)
        {
            vkGetPhysicalDeviceProperties(list[i], props);
            int type = *(int*)(props + 16);
            int score = type switch { 2 => 3, 1 => 2, 0 or 3 => 1, _ => 0 };   // discrete, integrated, other / virtual; CPU never
            string? name = Marshal.PtrToStringUTF8((IntPtr)(props + 20));
            if (score > 0 && GraphicsFamily(list[i]) < 0) score = 0;
            Log.Write($"core vulkan: GPU {name} (type {type}){(score == 0 ? ", not usable" : "")}");
            if (score > bestScore) { best = list[i]; bestScore = score; }
        }
        if (best == IntPtr.Zero) throw new InvalidOperationException("no graphics card that can run it");
        return best;
    }

    // A queue family with graphics (and compute, when there is one), or -1.
    int GraphicsFamily(IntPtr g)
    {
        uint n = 0;
        vkGetPhysicalDeviceQueueFamilyProperties(g, &n, null);
        if (n == 0) return -1;
        byte* q = stackalloc byte[(int)n * 24];
        vkGetPhysicalDeviceQueueFamilyProperties(g, &n, q);
        int graphics = -1;
        for (int i = 0; i < n; i++)
        {
            uint flags = *(uint*)(q + i * 24), count = *(uint*)(q + i * 24 + 4);
            if (count == 0 || (flags & 1) == 0) continue;
            if ((flags & 2) != 0) return i;
            if (graphics < 0) graphics = i;
        }
        return graphics;
    }

    void LoadDevice()
    {
        nint D(string n)
        {
            IntPtr p;
            fixed (byte* s = Encoding.ASCII.GetBytes(n + "\0")) p = vkGetDeviceProcAddr(device, s);
            if (p == IntPtr.Zero) throw new InvalidOperationException("Vulkan: missing " + n);
            return p;
        }
        vkDestroyDevice = (delegate* unmanaged<IntPtr, void*, void>)D("vkDestroyDevice");
        vkGetDeviceQueue = (delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)D("vkGetDeviceQueue");
        vkDeviceWaitIdle = (delegate* unmanaged<IntPtr, int>)D("vkDeviceWaitIdle");
        vkQueueSubmit = (delegate* unmanaged<IntPtr, uint, VkSubmitInfo*, ulong, int>)D("vkQueueSubmit");
        vkCreateCommandPool = (delegate* unmanaged<IntPtr, VkCommandPoolCreateInfo*, void*, ulong*, int>)D("vkCreateCommandPool");
        vkDestroyCommandPool = (delegate* unmanaged<IntPtr, ulong, void*, void>)D("vkDestroyCommandPool");
        vkAllocateCommandBuffers = (delegate* unmanaged<IntPtr, VkCommandBufferAllocateInfo*, IntPtr*, int>)D("vkAllocateCommandBuffers");
        vkResetCommandBuffer = (delegate* unmanaged<IntPtr, uint, int>)D("vkResetCommandBuffer");
        vkBeginCommandBuffer = (delegate* unmanaged<IntPtr, VkCommandBufferBeginInfo*, int>)D("vkBeginCommandBuffer");
        vkEndCommandBuffer = (delegate* unmanaged<IntPtr, int>)D("vkEndCommandBuffer");
        vkCmdPipelineBarrier = (delegate* unmanaged<IntPtr, uint, uint, uint, uint, void*, uint, VkBufferMemoryBarrier*, uint, VkImageMemoryBarrier*, void>)D("vkCmdPipelineBarrier");
        vkCmdCopyImageToBuffer = (delegate* unmanaged<IntPtr, ulong, int, ulong, uint, VkBufferImageCopy*, void>)D("vkCmdCopyImageToBuffer");
        vkCreateFence = (delegate* unmanaged<IntPtr, VkFenceCreateInfo*, void*, ulong*, int>)D("vkCreateFence");
        vkDestroyFence = (delegate* unmanaged<IntPtr, ulong, void*, void>)D("vkDestroyFence");
        vkResetFences = (delegate* unmanaged<IntPtr, uint, ulong*, int>)D("vkResetFences");
        vkWaitForFences = (delegate* unmanaged<IntPtr, uint, ulong*, uint, ulong, int>)D("vkWaitForFences");
        vkGetFenceStatus = (delegate* unmanaged<IntPtr, ulong, int>)D("vkGetFenceStatus");
        vkCreateBuffer = (delegate* unmanaged<IntPtr, VkBufferCreateInfo*, void*, ulong*, int>)D("vkCreateBuffer");
        vkDestroyBuffer = (delegate* unmanaged<IntPtr, ulong, void*, void>)D("vkDestroyBuffer");
        vkGetBufferMemoryRequirements = (delegate* unmanaged<IntPtr, ulong, VkMemoryRequirements*, void>)D("vkGetBufferMemoryRequirements");
        vkAllocateMemory = (delegate* unmanaged<IntPtr, VkMemoryAllocateInfo*, void*, ulong*, int>)D("vkAllocateMemory");
        vkFreeMemory = (delegate* unmanaged<IntPtr, ulong, void*, void>)D("vkFreeMemory");
        vkBindBufferMemory = (delegate* unmanaged<IntPtr, ulong, ulong, ulong, int>)D("vkBindBufferMemory");
        vkMapMemory = (delegate* unmanaged<IntPtr, ulong, ulong, ulong, uint, void**, int>)D("vkMapMemory");
        vkUnmapMemory = (delegate* unmanaged<IntPtr, ulong, void>)D("vkUnmapMemory");
        vkInvalidateMappedMemoryRanges = (delegate* unmanaged<IntPtr, uint, VkMappedMemoryRange*, int>)D("vkInvalidateMappedMemoryRanges");
    }

    static void Check(int r, string what) { if (r != VK_SUCCESS) throw new InvalidOperationException($"Vulkan: no {what} (error {r})"); }

    // ---- every frame (core thread) ----

    // Before retro_run: the next sync index; the work handed in for it last time round must be done.
    public void BeginFrame()
    {
        if (Lost != null || iface == null) return;
        int s = frames.Begin(out bool wait);
        if (wait) WaitSlot(s);
        submitted = false;
    }

    // RETRO_HW_FRAME_BUFFER_VALID: copy the image set with set_image, show the newest earlier copy that finished.
    public void Frame(int w, int h, Action<IntPtr, int, int, int> show)
    {
        if (Lost != null || iface == null) return;
        int kind = haveImage ? VkPixels.Kind(image.createInfo.format) : 0;
        if (haveImage && kind == 0 && !warnedFormat) { warnedFormat = true; Log.Write($"core vulkan: picture format {image.createInfo.format} can't be shown yet"); }
        bool copy = kind != 0 && w > 0 && h > 0 && image.createInfo.image != 0;
        long n = (long)w * h * 4;
        if (copy && n > bufBytes && !MakeBuffers(n, w, h)) copy = false;
        Submit(copy, w, h, kind, true);
        ShowFinished(show);
    }

    // A duped frame (no new picture): the core's command buffers and signal semaphore still go to the GPU.
    // False: no earlier picture was ready either, so the frame stays a dupe.
    public bool Dupe(Action<IntPtr, int, int, int> show)
    {
        if (Lost != null || iface == null) return false;
        Submit(false, 0, 0, 0, false);
        return ShowFinished(show);
    }

    // After retro_run: command buffers handed in without a picture are run anyway.
    public void EndFrame()
    {
        if (Lost != null || iface == null || coreCmds.Count == 0) return;
        Submit(false, 0, 0, 0, false);
    }

    // fresh: a new picture (not a dupe). Its set_image semaphores are waited for even when it can't be copied.
    void Submit(bool copy, int w, int h, int kind, bool fresh)
    {
        int s = frames.Index;
        if (submitted) WaitSlot(s);   // a second submission this frame: the slot's fence is used again
        if (Lost != null) return;
        if (copy)
        {
            if (!Ok(vkResetCommandBuffer(cmd[s], 0))) return;
            var bi = new VkCommandBufferBeginInfo { sType = 42, flags = 1 /* ONE_TIME_SUBMIT */ };
            if (!Ok(vkBeginCommandBuffer(cmd[s], &bi))) return;
            RecordCopy(cmd[s], buf[s], w, h);
            if (!Ok(vkEndCommandBuffer(cmd[s]))) return;
        }
        int nc = coreCmds.Count + (copy ? 1 : 0);
        ulong sig = signalSem;
        // Semaphores from set_image are waited for once, and not when the core hands in command buffers.
        int nw = fresh && coreCmds.Count == 0 ? waitSems.Count : 0;
        if (nc == 0 && sig == 0 && nw == 0) return;   // nothing for the GPU this frame
        IntPtr* cmds = stackalloc IntPtr[Math.Max(nc, 1)];
        for (int i = 0; i < coreCmds.Count; i++) cmds[i] = coreCmds[i];   // the core's first, then ours
        if (copy) cmds[nc - 1] = cmd[s];
        ulong* ws = stackalloc ulong[Math.Max(nw, 1)];
        uint* stages = stackalloc uint[Math.Max(nw, 1)];
        for (int i = 0; i < nw; i++) { ws[i] = waitSems[i]; stages[i] = StageAll; }
        var si = new VkSubmitInfo
        {
            sType = 4, waitSemaphoreCount = (uint)nw, pWaitSemaphores = (IntPtr)ws, pWaitDstStageMask = (IntPtr)stages,
            commandBufferCount = (uint)nc, pCommandBuffers = (IntPtr)cmds, signalSemaphoreCount = sig != 0 ? 1u : 0u, pSignalSemaphores = (IntPtr)(&sig),
        };
        int r;
        queueLock.Wait();
        try { r = vkQueueSubmit(queue, 1, &si, fence[s]); }
        finally { queueLock.Release(); }
        coreCmds.Clear(); signalSem = 0;
        if (fresh) waitSems.Clear();
        if (!Ok(r)) return;
        submitted = true;
        frames.Submitted(copy);
        if (copy) { slotW[s] = w; slotH[s] = h; slotKind[s] = kind; }
    }

    // The core's image into a buffer: to TRANSFER_SRC (or as it is, GENERAL), copied, back to the core's layout.
    // When the core's queue family differs, the image is taken over and handed back (ownership transfer).
    void RecordCopy(IntPtr cb, ulong buffer, int w, int h)
    {
        var ci = image.createInfo;
        uint mip = ci.subresourceRange.baseMipLevel, layer = ci.subresourceRange.baseArrayLayer;
        int layout = image.imageLayout, copyLayout = layout == LayoutGeneral ? LayoutGeneral : LayoutTransferSrc;
        bool move = srcFamily != Ignored && srcFamily != family;
        var range = new VkImageSubresourceRange { aspectMask = 1, baseMipLevel = mip, levelCount = 1, baseArrayLayer = layer, layerCount = 1 };
        var take = new VkImageMemoryBarrier
        {
            sType = 45, srcAccessMask = AccessColorWrite | AccessMemoryWrite, dstAccessMask = AccessTransferRead, oldLayout = layout, newLayout = copyLayout,
            srcQueueFamilyIndex = move ? srcFamily : Ignored, dstQueueFamilyIndex = move ? family : Ignored, image = ci.image, subresourceRange = range,
        };
        vkCmdPipelineBarrier(cb, StageAll, StageTransfer, 0, 0, null, 0, null, 1, &take);
        var region = new VkBufferImageCopy
        {
            imageSubresource = new VkImageSubresourceLayers { aspectMask = 1, mipLevel = mip, baseArrayLayer = layer, layerCount = 1 },
            width = (uint)w, height = (uint)h, depth = 1,
        };
        vkCmdCopyImageToBuffer(cb, ci.image, copyLayout, buffer, 1, &region);
        var back = take;
        back.srcAccessMask = AccessTransferRead; back.dstAccessMask = AccessMemoryRead | AccessMemoryWrite;
        back.oldLayout = copyLayout; back.newLayout = layout;
        back.srcQueueFamilyIndex = move ? family : Ignored; back.dstQueueFamilyIndex = move ? srcFamily : Ignored;
        var host = new VkBufferMemoryBarrier
        {
            sType = 44, srcAccessMask = AccessTransferWrite, dstAccessMask = AccessHostRead, srcQueueFamilyIndex = Ignored, dstQueueFamilyIndex = Ignored,
            buffer = buffer, offset = 0, size = ulong.MaxValue,
        };
        vkCmdPipelineBarrier(cb, StageTransfer, StageAll | StageHost, 0, 0, null, 1, &host, 1, &back);
    }

    // The newest earlier copy the GPU has finished, shown once (BGRA, top-down). None yet: the picture stays.
    bool ShowFinished(Action<IntPtr, int, int, int> show)
    {
        if (Lost != null) return false;
        Span<int> c = stackalloc int[Slots];
        int k = frames.Candidates(c);
        for (int i = 0; i < k; i++)
        {
            int t = c[i];
            if (frames.Pending(t))
            {
                ulong f = fence[t];
                int r = i == 0 ? vkWaitForFences(device, 1, &f, 1, NewestWaitNs) : vkGetFenceStatus(device, f);
                if (r == VK_TIMEOUT || r == VK_NOT_READY) continue;   // not done yet: an older finished one, else nothing
                if (!Ok(r) || !Ok(vkResetFences(device, 1, &f))) return false;
                frames.Waited(t);
            }
            if (!coherent)
            {
                var mr = new VkMappedMemoryRange { sType = 6, memory = mem[t], offset = 0, size = ulong.MaxValue };
                vkInvalidateMappedMemoryRanges(device, 1, &mr);
            }
            frames.Shown(t);
            int tw = slotW[t], th = slotH[t], n = tw * th * 4;
            if (slotKind[t] == 1) { show((IntPtr)map[t], tw, th, tw * 4); return true; }
            if (scratch.Length < n) scratch = new byte[n];
            VkPixels.ToBgra(slotKind[t], new ReadOnlySpan<byte>(map[t], n), scratch);
            fixed (byte* p = scratch) show((IntPtr)p, tw, th, tw * 4);
            return true;
        }
        return false;
    }

    void WaitSlot(int s)
    {
        if (Lost != null || !frames.Pending(s)) return;
        ulong f = fence[s];
        int r = vkWaitForFences(device, 1, &f, 1, FenceWaitNs);
        if (r == VK_TIMEOUT) { Fail("the graphics card stopped responding"); return; }
        if (!Ok(r) || !Ok(vkResetFences(device, 1, &f))) return;
        frames.Waited(s);
    }

    // Readback buffers of n bytes each, made again when a bigger picture comes (smaller ones fit as they are).
    // The frames in flight are waited for first. The picture of the frame being made now is not shown.
    bool MakeBuffers(long n, int w, int h)
    {
        for (int s = 0; s < Slots; s++) WaitSlot(s);
        if (Lost != null) return false;
        FreeBuffers();
        for (int s = 0; s < Slots; s++)
        {
            var bi = new VkBufferCreateInfo { sType = 12, size = (ulong)n, usage = 2 /* TRANSFER_DST */, sharingMode = 0 };
            ulong b, m; void* p;
            if (!Ok(vkCreateBuffer(device, &bi, null, &b))) return false;
            buf[s] = b;
            VkMemoryRequirements req;
            vkGetBufferMemoryRequirements(device, b, &req);
            int type = MemoryType(req.memoryTypeBits);
            if (type < 0) { Fail("no memory the CPU can read"); return false; }
            var ai = new VkMemoryAllocateInfo { sType = 5, allocationSize = req.size, memoryTypeIndex = (uint)type };
            if (!Ok(vkAllocateMemory(device, &ai, null, &m))) return false;
            mem[s] = m;
            if (!Ok(vkBindBufferMemory(device, b, m, 0)) || !Ok(vkMapMemory(device, m, 0, ulong.MaxValue, 0, &p))) return false;
            map[s] = (byte*)p;
            coherent = (memFlags[type] & 4) != 0;
        }
        bufBytes = n;
        frames.Clear();
        Log.Write($"core vulkan: readback buffers for {w}x{h}{(coherent ? "" : " (not coherent)")}");
        return true;
    }

    // Host-visible memory, cached for fast reading when there is such.
    int MemoryType(uint bits)
    {
        foreach (uint want in new uint[] { 2 | 4 | 8, 2 | 8, 2 | 4 })   // VISIBLE | COHERENT | CACHED, VISIBLE | CACHED, VISIBLE | COHERENT
            for (int i = 0; i < memCount; i++)
                if ((bits & (1u << i)) != 0 && (memFlags[i] & want) == want) return i;
        return -1;
    }

    void FreeBuffers()
    {
        for (int s = 0; s < Slots; s++)
        {
            if (map[s] != null) { vkUnmapMemory(device, mem[s]); map[s] = null; }
            if (buf[s] != 0) { vkDestroyBuffer(device, buf[s], null); buf[s] = 0; }
            if (mem[s] != 0) { vkFreeMemory(device, mem[s], null); mem[s] = 0; }
        }
        bufBytes = 0;
    }

    bool Ok(int r)
    {
        if (r >= 0) return true;
        Fail(r == VK_ERROR_DEVICE_LOST ? "the graphics card was reset (device lost)" : $"Vulkan error {r}");
        return false;
    }

    void Fail(string why)
    {
        if (Lost != null) return;
        Lost = T("errors.the_graphics_card_stopped_drawing_this", why);
        Log.Write($"core vulkan ({core}): {why}, the game stops");
    }

    // Before context_destroy: nothing of the core's may still be in use by the GPU.
    public void Idle()
    {
        if (device == IntPtr.Zero || vkDeviceWaitIdle == null) return;
        queueLock.Wait();
        try { vkDeviceWaitIdle(device); }
        finally { queueLock.Release(); }
        for (int s = 0; s < Slots; s++)
            if (frames.Pending(s) && fence[s] != 0) { ulong f = fence[s]; vkResetFences(device, 1, &f); frames.Waited(s); }
    }

    // After the core is unloaded: our objects, the core's own device resources (destroy_device), the device, the instance.
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            if (device != IntPtr.Zero && vkCreateFence != null)
            {
                Idle();
                FreeBuffers();
                for (int s = 0; s < Slots; s++) if (fence[s] != 0) { vkDestroyFence(device, fence[s], null); fence[s] = 0; }
                if (pool != 0) { vkDestroyCommandPool(device, pool, null); pool = 0; }
            }
            var neg = (RetroVkNegotiation*)Negotiation;
            if (instance != IntPtr.Zero && neg != null && neg->interfaceType == 0 && neg->destroyDevice != IntPtr.Zero) ((delegate* unmanaged<void>)neg->destroyDevice)();
            if (made && vkDestroyDevice != null) vkDestroyDevice(device, null);
            if (instance != IntPtr.Zero && vkDestroyInstance != null) vkDestroyInstance(instance, null);
        }
        catch (Exception e) { Log.Write("core vulkan close: " + e.Message); }
        device = instance = IntPtr.Zero;
        if (iface != null) { NativeMemory.Free(iface); iface = null; }
        if (appName != IntPtr.Zero) { Marshal.FreeHGlobal(appName); appName = IntPtr.Zero; }
        if (self.IsAllocated) self.Free();
        queueLock.Dispose();
    }

    // ---- called by the core (retro_hw_render_interface_vulkan and the negotiation wrappers) ----
    // An exception must never cross back into the core: each one is caught and logged.

    [UnmanagedCallersOnly]
    static IntPtr InstanceWrapper(IntPtr opaque, VkInstanceCreateInfo* ci)
    {
        try { return From(opaque)?.MakeInstance(ci) ?? IntPtr.Zero; }
        catch (Exception e) { Log.Write("core vulkan instance: " + e.Message); return IntPtr.Zero; }
    }

    [UnmanagedCallersOnly]
    static IntPtr DeviceWrapper(IntPtr g, IntPtr opaque, VkDeviceCreateInfo* ci)
    {
        try
        {
            var v = From(opaque);
            if (v == null || v.vkCreateDevice == null) return IntPtr.Zero;
            IntPtr dev;
            int r = v.vkCreateDevice(g, ci, null, &dev);
            if (r != VK_SUCCESS) { Log.Write($"core vulkan: vkCreateDevice error {r}"); return IntPtr.Zero; }
            return dev;
        }
        catch (Exception e) { Log.Write("core vulkan device: " + e.Message); return IntPtr.Zero; }
    }

    [UnmanagedCallersOnly]
    static void SetImage(IntPtr h, RetroVkImage* img, uint n, ulong* sems, uint src)
    {
        try
        {
            var v = From(h);
            if (v == null || img == null) return;
            v.image = *img; v.haveImage = true; v.srcFamily = src;
            v.waitSems.Clear();
            for (int i = 0; i < n && sems != null; i++) v.waitSems.Add(sems[i]);
        }
        catch (Exception e) { Log.Write("core vulkan set_image: " + e.Message); }
    }

    [UnmanagedCallersOnly] static uint GetSyncIndex(IntPtr h) { try { return (uint)(From(h)?.frames.Index ?? 0); } catch { return 0; } }
    [UnmanagedCallersOnly] static uint GetSyncIndexMask(IntPtr h) { try { return From(h)?.frames.Mask ?? 1; } catch { return 1; } }

    [UnmanagedCallersOnly]
    static void SetCommandBuffers(IntPtr h, uint n, IntPtr* cmds)
    {
        try
        {
            var v = From(h);
            if (v == null) return;
            v.coreCmds.Clear();
            for (int i = 0; i < n && cmds != null; i++) v.coreCmds.Add(cmds[i]);
        }
        catch (Exception e) { Log.Write("core vulkan set_command_buffers: " + e.Message); }
    }

    [UnmanagedCallersOnly]
    static void WaitSyncIndex(IntPtr h)
    {
        try { var v = From(h); if (v != null && v.iface != null) v.WaitSlot(v.frames.Index); }
        catch (Exception e) { Log.Write("core vulkan wait_sync_index: " + e.Message); }
    }

    [UnmanagedCallersOnly] static void LockQueue(IntPtr h) { try { From(h)?.queueLock.Wait(); } catch (Exception e) { Log.Write("core vulkan lock_queue: " + e.Message); } }
    [UnmanagedCallersOnly] static void UnlockQueue(IntPtr h) { try { From(h)?.queueLock.Release(); } catch (Exception e) { Log.Write("core vulkan unlock_queue: " + e.Message); } }

    [UnmanagedCallersOnly]
    static void SetSignalSemaphore(IntPtr h, ulong sem)
    {
        try { var v = From(h); if (v != null) v.signalSem = sem; }
        catch (Exception e) { Log.Write("core vulkan set_signal_semaphore: " + e.Message); }
    }
}
