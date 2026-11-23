// Rewind for games played inside the launcher (libretro cores, CoreGame.Loop).
// Every frame (or every 2 or 4 frames for bigger games) the core's state (retro_serialize) goes into RewindBuffer;
// holding the rewind key loads them back one by one, newest first, so the game runs backwards. The buffer keeps
// only what changed from one state to the next (most of a game's memory stays the same from frame to frame), in a
// fixed amount of memory (Settings > Look > In games > Fast-forward and rewind > Rewind memory): when it is full
// the oldest states go first. Not for a core with no state, one whose state fails or says it is incomplete, and by
// default not for games drawn with the GPU (their states are big and slow to make).
// Keys: Settings > Input > Launcher keys > Rewind (F5), and View + LB on Player 1's controller.
using System;
using System.Numerics;

namespace Ionea;

// The newest state in full, and before it a ring of steps back: each step is the XOR of a state with the one after
// it, its runs of zeros (the bytes that didn't change) packed away. Steps are kept one after another in one block
// of Capacity bytes, around and around; a new step that doesn't fit pushes out the oldest ones in its way.
public sealed class RewindBuffer
{
    public const int MinZeros = 8;   // a run of unchanged bytes shorter than this stays in the literal bytes

    readonly byte[] arena;
    int head;                        // where the next step goes: just after the newest one
    (int Start, int Len)[] ring = new (int, int)[256];
    int first, count;                // the oldest step in ring, and how many there are
    byte[]? cur;                     // null before any state
    byte[] diff = Array.Empty<byte>(), pack = Array.Empty<byte>();   // cur's size once there is one
    public int Capacity => arena.Length;
    public int Count => count;                  // steps back there are
    public byte[]? Current => cur;              // the newest state (after StepBack: the one stepped back to); null before any
    public long Used { get; private set; }      // bytes of steps kept

    public RewindBuffer(int capacity) { arena = GC.AllocateUninitializedArray<byte>(Math.Max(64, capacity)); }

    public void Clear() { first = count = 0; head = 0; Used = 0; }

    // A new state, the newest. A state of another size than before starts over (a core whose state size changes).
    public void Push(ReadOnlySpan<byte> state)
    {
        if (cur == null || cur.Length != state.Length)
        {
            cur = state.ToArray(); diff = new byte[cur.Length]; pack = new byte[cur.Length * 2 + 64];
            Clear();
            return;
        }
        Xor(cur, state, diff);
        int len = Pack(diff, pack);
        state.CopyTo(cur);
        if (len > arena.Length) { Clear(); return; }   // a step bigger than all the memory: no going back past here
        if (head + len > arena.Length)
        {
            while (count > 0 && Oldest.Start >= head) Drop();   // the end of the block is left: the steps there go
            head = 0;
        }
        while (count > 0 && Oldest.Start < head + len && Oldest.Start + Oldest.Len > head) Drop();
        Array.Copy(pack, 0, arena, head, len);
        if (count == ring.Length) Grow();
        ring[(first + count) % ring.Length] = (head, len);
        count++; Used += len; head += len;
    }

    // Back one state: Current becomes the state before it. False at the oldest (Current stays).
    public bool StepBack()
    {
        if (count == 0) return false;
        var (start, len) = ring[(first + count - 1) % ring.Length];
        count--; Used -= len;
        Unpack(arena.AsSpan(start, len), cur!);   // a step was kept: cur is there
        head = count > 0 ? Newest.Start + Newest.Len : 0;
        return true;
    }

    (int Start, int Len) Oldest => ring[first];
    (int Start, int Len) Newest => ring[(first + count - 1) % ring.Length];
    void Drop() { Used -= ring[first].Len; first = (first + 1) % ring.Length; count--; }
    void Grow()
    {
        var bigger = new (int, int)[ring.Length * 2];
        for (int i = 0; i < count; i++) bigger[i] = ring[(first + i) % ring.Length];
        ring = bigger; first = 0;
    }

    // Packs d: pairs of (zeros to skip, literal bytes) as 7-bit numbers, then the literal bytes, to the end of d.
    public static int Pack(ReadOnlySpan<byte> d, byte[] o)
    {
        int n = d.Length, i = 0, w = 0;
        while (i < n)
        {
            int z = d[i..].IndexOfAnyExcept((byte)0);
            if (z < 0) z = n - i;
            int s = i + z, e = s;
            while (e < n)   // the literal ends at a long enough run of zeros, or at the end
            {
                int k = d[e..].IndexOf((byte)0);
                if (k < 0) { e = n; break; }
                e += k;
                int r = d[e..].IndexOfAnyExcept((byte)0);
                int run = r < 0 ? n - e : r;
                if (run >= MinZeros || e + run >= n) break;
                e += run;
            }
            w = Num(o, w, z); w = Num(o, w, e - s);
            d[s..e].CopyTo(o.AsSpan(w)); w += e - s;
            i = e;
        }
        return w;
    }

    // XORs a packed step into state.
    public static void Unpack(ReadOnlySpan<byte> p, byte[] state)
    {
        int r = 0, at = 0;
        while (r < p.Length)
        {
            at += Num(p, ref r);
            int lit = Num(p, ref r);
            Xor(state.AsSpan(at, lit), p.Slice(r, lit), state.AsSpan(at, lit));
            r += lit; at += lit;
        }
    }

    static void Xor(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, Span<byte> o)
    {
        int i = 0, vs = Vector<byte>.Count;
        for (; i <= a.Length - vs; i += vs) (new Vector<byte>(a[i..]) ^ new Vector<byte>(b[i..])).CopyTo(o[i..]);
        for (; i < a.Length; i++) o[i] = (byte)(a[i] ^ b[i]);
    }

    static int Num(byte[] o, int w, int v)
    {
        while (v >= 0x80) { o[w++] = (byte)(v | 0x80); v >>= 7; }
        o[w++] = (byte)v;
        return w;
    }
    static int Num(ReadOnlySpan<byte> p, ref int r)
    {
        int v = 0, sh = 0;
        byte b;
        do { b = p[r++]; v |= (b & 0x7f) << sh; sh += 7; } while (b >= 0x80);
        return v;
    }

    // How many frames between states: every frame for small states, fewer for big ones.
    public static int Interval(int stateSize) => stateSize <= 1 << 20 ? 1 : stateSize <= 4 << 20 ? 2 : 4;
}
