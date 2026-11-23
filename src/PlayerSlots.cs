// Players in a game played inside the launcher (CoreGame.cs): up to four slots, each holding one controller,
// and slot N is the core's port N. Pads fill the slots in the order they are seen (the first pad is Player 1,
// the next one Player 2, ...). A pad that is unplugged frees its slot; the other players keep theirs. When it
// comes back it takes its old slot again if that is still free, otherwise the first free one. The keyboard is
// always Player 1 as well (CoreSession), so a game with no pad still plays.
// Read on the emulation thread (Update, each poll) and changed from the Home menu (Move): every call locks.
using System;
using System.Collections.Generic;
using System.Linq;

namespace Ionea;

public sealed class PlayerSlots
{
    public const int Max = 4;
    readonly string?[] slots = new string?[Max];         // the pad key in each slot, null when empty
    readonly Dictionary<string, int> was = new();       // the slot a pad had when it was unplugged
    readonly object gate = new();

    // Bumped whenever a slot changes, so the menu can tell its rows are out of date.
    public int Version { get; private set; }

    public string? this[int slot] { get { lock (gate) return slot >= 0 && slot < Max ? slots[slot] : null; } }

    public int SlotOf(string key)
    {
        lock (gate) return Array.IndexOf(slots, key);
    }

    public string[] Snapshot() { lock (gate) return (string[])slots.Clone(); }

    // The pads plugged in now, in the order they are found. Returns true when a slot changed.
    public bool Update(IReadOnlyList<string> connected)
    {
        lock (gate)
        {
            bool changed = false;
            for (int i = 0; i < Max; i++)
            {
                if (slots[i] is not string had || connected.Contains(had)) continue;
                was[had] = i;
                slots[i] = null;
                changed = true;
            }
            foreach (var key in connected)
            {
                if (key == null || Array.IndexOf(slots, key) >= 0) continue;
                int at = was.TryGetValue(key, out int old) && slots[old] == null ? old : Array.IndexOf(slots, null);
                if (at < 0) break;   // every slot is taken: a fifth pad waits
                slots[at] = key;
                was.Remove(key);
                changed = true;
            }
            if (changed) Version++;
            return changed;
        }
    }

    // The Home menu: the slot takes the next (d = +1) or previous (d = -1) plugged-in pad. A pad that already
    // had another slot swaps with this one, so no pad is ever in two slots.
    public void Move(int slot, int d, IReadOnlyList<string> connected)
    {
        if (slot < 0 || slot >= Max || connected.Count == 0) return;
        lock (gate)
        {
            int i = slots[slot] is string key ? connected.ToList().IndexOf(key) : -1;
            int n = connected.Count;
            string next = connected[i < 0 ? (d > 0 ? 0 : n - 1) : ((i + d) % n + n) % n];
            if (next == slots[slot]) return;
            int other = Array.IndexOf(slots, next);
            if (other >= 0) slots[other] = slots[slot];
            slots[slot] = next;
            Version++;
        }
    }
}
