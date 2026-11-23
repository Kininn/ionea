// Pure parts of the asynchronous OpenGL readback (GlHost.cs): which pixel buffer each frame goes into and which
// one to show, and the row copy that turns a bottom-up picture the right way up. No GL here, so they can be tested.
using System;
using System.Runtime.InteropServices;

namespace Ionea;

// A ring of readback slots. Each frame is read into the next slot (Begin); a slot is shown only once, and only
// while it is newer than the frame shown last. The slot just begun is never offered: its copy is still running.
public sealed class ReadbackRing
{
    readonly long[] seq;   // frame number held by each slot, 0 = nothing
    long last, shown;
    int next;

    public ReadbackRing(int slots) { seq = new long[Math.Max(slots, 2)]; }

    public int Count => seq.Length;

    // The slot for the frame being read now. Whatever it held before is dropped.
    public int Begin()
    {
        int s = next;
        next = (next + 1) % seq.Length;
        seq[s] = ++last;
        return s;
    }

    // Slots holding a frame not shown yet, other than the one just begun, newest first. Fills `into`, returns the count.
    public int Waiting(Span<int> into)
    {
        int n = 0;
        for (long f = last - 1; f > shown && f > last - seq.Length && n < into.Length; f--)
        {
            int s = Slot(f);
            if (s >= 0) into[n++] = s;
        }
        return n;
    }

    // The frame in this slot was shown: it and every older frame are never offered again.
    public void Shown(int slot) { if (seq[slot] > shown) shown = seq[slot]; }

    // The buffers were remade: forget every frame in them.
    public void Clear() { Array.Clear(seq); shown = last; }

    int Slot(long frame)
    {
        for (int s = 0; s < seq.Length; s++) if (seq[s] == frame) return s;
        return -1;
    }
}

public static class FrameRows
{
    // Copy `rows` rows of `row` bytes from src (pitch bytes from one row to the next; negative walks upwards)
    // into dst, packed and top-down.
    public static void Copy(IntPtr src, int pitch, byte[] dst, int row, int rows)
    {
        for (int y = 0; y < rows; y++) Marshal.Copy(src + y * pitch, dst, y * row, row);
    }

    // Where a picture starts and how far its rows step, so that copying it with Copy turns it the right way up
    // when its rows are stored bottom-up.
    public static (IntPtr start, int pitch) TopDown(IntPtr data, int row, int rows, bool bottomUp)
        => bottomUp && rows > 0 ? (data + (rows - 1) * row, -row) : (data, row);
}
