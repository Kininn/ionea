#nullable disable
// A cache that keeps what was used most recently: past its budget it drops the least recently used
// entries one by one (never everything at once). The budget is a total weight (for pictures, their bytes)
// and a count; a read moves the entry to the front.
using System;
using System.Collections.Generic;

namespace Ionea;

public sealed class LruCache<TKey, TValue>
{
    readonly Dictionary<TKey, LinkedListNode<(TKey key, TValue value, long weight)>> map;
    readonly LinkedList<(TKey key, TValue value, long weight)> order = new();   // first = most recently used
    readonly Func<TValue, long> weigh;

    public long MaxWeight { get; }
    public int MaxCount { get; }
    public long Weight { get; private set; }
    public int Count => map.Count;

    public LruCache(long maxWeight, int maxCount, Func<TValue, long> weigh = null, IEqualityComparer<TKey> comparer = null)
    {
        MaxWeight = maxWeight; MaxCount = maxCount;
        this.weigh = weigh ?? (_ => 1);
        map = new(comparer);
    }

    public bool TryGet(TKey key, out TValue value)
    {
        if (map.TryGetValue(key, out var node))
        {
            order.Remove(node); order.AddFirst(node);
            value = node.Value.value;
            return true;
        }
        value = default;
        return false;
    }

    // Without making it the most recent (a look that is not a use).
    public bool Peek(TKey key, out TValue value)
    {
        bool hit = map.TryGetValue(key, out var node);
        value = hit ? node.Value.value : default;
        return hit;
    }

    public void Set(TKey key, TValue value)
    {
        Remove(key);
        long wt = Math.Max(0, weigh(value));
        map[key] = order.AddFirst((key, value, wt));
        Weight += wt;
        // the newest stays even when it alone is over the budget
        while (order.Count > 1 && (Weight > MaxWeight || order.Count > MaxCount)) Remove(order.Last.Value.key);
    }

    public bool Remove(TKey key)
    {
        if (!map.Remove(key, out var node)) return false;
        order.Remove(node);
        Weight -= node.Value.weight;
        return true;
    }

    public void Clear() { map.Clear(); order.Clear(); Weight = 0; }

    // Keys from most to least recently used (tests).
    public IEnumerable<TKey> Keys() { foreach (var e in order) yield return e.key; }
}
