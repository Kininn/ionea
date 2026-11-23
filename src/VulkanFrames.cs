// Pure parts of the Vulkan host (VulkanHost.cs): the frames in flight (one per sync index the core sees) and
// which finished one to show, and the pixel formats a core's image can come in, turned into the launcher's BGRA.
// No Vulkan here, so they can be tested.
using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Ionea;

// The frames in flight. Each retro_run gets the next slot (its sync index): the GPU work handed in for that slot
// the last time round must be finished (waited for) before the core may reuse its resources. A frame whose
// picture was copied is shown once, the newest finished one first, never the current one (its copy is still
// running), so the picture is one frame later on screen, like the OpenGL readback (GlReadback.cs).
public sealed class VkFrameRing
{
    readonly ReadbackRing ring;
    readonly bool[] pending, picture;

    public VkFrameRing(int slots)
    {
        ring = new ReadbackRing(slots);
        pending = new bool[ring.Count];
        picture = new bool[ring.Count];
    }

    public int Count => ring.Count;
    public uint Mask => (uint)((1 << Count) - 1);   // get_sync_index_mask: bit N set, get_sync_index can be N
    public int Index { get; private set; }            // get_sync_index: the slot of the frame being run

    // A new frame: its slot. wait: GPU work submitted in that slot earlier hasn't been waited for yet.
    public int Begin(out bool wait)
    {
        Index = ring.Begin();
        wait = pending[Index];
        picture[Index] = false;
        return Index;
    }

    // Work for the current frame went to the GPU (with this slot's fence); withPicture: it copies the picture.
    public void Submitted(bool withPicture)
    {
        pending[Index] = true;
        picture[Index] |= withPicture;
    }

    public bool Pending(int slot) => pending[slot];
    public void Waited(int slot) => pending[slot] = false;
    public void WaitedAll() => Array.Clear(pending);

    // Slots with a copied picture not shown yet, newest first, never the current frame's. Fills `into`.
    public int Candidates(Span<int> into)
    {
        Span<int> all = stackalloc int[Count];
        int n = ring.Waiting(all), k = 0;
        for (int i = 0; i < n && k < into.Length; i++) if (picture[all[i]]) into[k++] = all[i];
        return k;
    }

    public void Shown(int slot) => ring.Shown(slot);

    // The readback buffers were remade: no picture in them is shown.
    public void Clear() { ring.Clear(); Array.Clear(picture); }
}

public static class VkPixels
{
    // How an image format becomes BGRA rows: 0 not supported, 1 BGRA already, 2 RGBA (red and blue swapped),
    // 3 A2R10G10B10, 4 A2B10G10R10 (10 bits a colour, cut to 8).
    public static int Kind(int vkFormat) => vkFormat switch
    {
        44 or 50 => 1,             // B8G8R8A8_UNORM / _SRGB
        37 or 43 or 51 or 57 => 2, // R8G8B8A8_UNORM / _SRGB, A8B8G8R8_UNORM_PACK32 / _SRGB_PACK32 (the same bytes)
        58 => 3,                   // A2R10G10B10_UNORM_PACK32
        64 => 4,                   // A2B10G10R10_UNORM_PACK32
        _ => 0,
    };

    // Each 4-byte pixel of src into dst as BGRA (blue in the lowest byte). Kind 1 is a plain copy.
    public static void ToBgra(int kind, ReadOnlySpan<byte> src, Span<byte> dst)
    {
        var s = MemoryMarshal.Cast<byte, uint>(src);
        var d = MemoryMarshal.Cast<byte, uint>(dst);
        int n = Math.Min(s.Length, d.Length), i = 0;
        switch (kind)
        {
            case 1: s[..n].CopyTo(d); return;
            case 2:
                if (Vector.IsHardwareAccelerated && n >= Vector<uint>.Count)
                {
                    var keep = new Vector<uint>(0xFF00FF00u); var low = new Vector<uint>(0xFFu); var high = new Vector<uint>(0xFF0000u);
                    var vs = MemoryMarshal.Cast<uint, Vector<uint>>(s[..n]);
                    var vd = MemoryMarshal.Cast<uint, Vector<uint>>(d[..n]);
                    for (int v = 0; v < vs.Length; v++)
                    {
                        var x = vs[v];
                        vd[v] = (x & keep) | (Vector.ShiftLeft(x, 16) & high) | (Vector.ShiftRightLogical(x, 16) & low);
                    }
                    i = vs.Length * Vector<uint>.Count;
                }
                for (; i < n; i++) { uint x = s[i]; d[i] = (x & 0xFF00FF00u) | ((x & 0xFFu) << 16) | ((x >> 16) & 0xFFu); }
                return;
            case 3:
            case 4:
                for (; i < n; i++)
                {
                    uint x = s[i];
                    uint a = (x >> 30) * 85, c2 = (x >> 22) & 0xFF, c1 = (x >> 12) & 0xFF, c0 = (x >> 2) & 0xFF;   // top 8 of each 10 bits
                    uint r = kind == 3 ? c2 : c0, b = kind == 3 ? c0 : c2;
                    d[i] = (a << 24) | (r << 16) | (c1 << 8) | b;
                }
                return;
        }
    }
}
