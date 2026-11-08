namespace ImaginedNT.Core.Runtime;

// Guest threads currently have one fiber each; indices belong to the virtual process.
internal sealed class FiberLocalStorage
{
    private readonly Dictionary<uint, Slot> _slots = new();

    private sealed class Slot(ulong callback)
    {
        public readonly ulong Callback = callback;
        public readonly Dictionary<ImaginedThread, ulong> Values = new();
        public bool Freeing;
    }

    internal uint Allocate(ulong callback)
    {
        lock (_slots)
        {
            for (uint i = 0; i < 4080; i++)
            {
                if (_slots.ContainsKey(i)) continue;
                _slots.Add(i, new Slot(callback));
                return i;
            }
            return uint.MaxValue;
        }
    }

    internal bool Set(uint index, ImaginedThread thread, ulong value)
    {
        lock (_slots)
        {
            if (!_slots.TryGetValue(index, out var slot) || slot.Freeing) return false;
            if (value == 0) slot.Values.Remove(thread);
            else slot.Values[thread] = value;
            return true;
        }
    }

    internal bool TryGet(uint index, ImaginedThread thread, out ulong value)
    {
        lock (_slots)
        {
            value = 0;
            if (!_slots.TryGetValue(index, out var slot) || slot.Freeing) return false;
            slot.Values.TryGetValue(thread, out value);
            return true;
        }
    }

    internal bool Free(uint index, ImaginedThread thread)
    {
        Slot slot;
        ulong[] values;
        lock (_slots)
        {
            if (!_slots.TryGetValue(index, out slot!) || slot.Freeing) return false;
            slot.Freeing = true;
            values = slot.Values.Values.ToArray();
            slot.Values.Clear();
        }
        try
        {
            foreach (var value in values) InvokeCallback(thread, slot.Callback, value);
        }
        finally
        {
            lock (_slots) _slots.Remove(index);
        }
        return true;
    }

    internal void ReleaseThread(ImaginedThread thread)
    {
        var callbacks = new List<(ulong Callback, ulong Value)>();
        lock (_slots)
        {
            foreach (var slot in _slots.Values)
            {
                if (slot.Values.Remove(thread, out var value) && slot.Callback != 0)
                    callbacks.Add((slot.Callback, value));
            }
        }
        foreach (var (callback, value) in callbacks) InvokeCallback(thread, callback, value, true);
    }

    private static unsafe void InvokeCallback(ImaginedThread thread, ulong callback, ulong value, bool exiting = false)
    {
        if (callback == 0 || value == 0) return;
        if (thread.IsManagedHosted) thread.InvokeGuest(callback, [value], exiting);
        else ((delegate* unmanaged<ulong, void>)(nint)callback)(value);
    }
}
