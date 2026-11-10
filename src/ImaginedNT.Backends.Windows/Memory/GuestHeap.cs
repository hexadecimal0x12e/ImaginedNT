using System.Runtime.InteropServices;
using ImaginedNT.Core.Diagnostics;
using ImaginedNT.Core.Native;

namespace ImaginedNT.Core.Memory;

public sealed class GuestHeap(TraceLog trace, int processId, uint options = 0, ulong initialSize = 0, ulong maximumSize = 0, RuntimeMemory? memory = null) : IDisposable
{
    private readonly RuntimeMemory _memory = memory ?? new RuntimeMemory();
    private const uint ZeroMemory = 0x8, InPlaceOnly = 0x10;
    private readonly object _gate = new();
    private readonly Dictionary<ulong, ulong> _blocks = new();
    private ulong _allocatedBytes, _committedBytes, _reservedBytes;
    private IntPtr _handle;
    private bool _disposed;

    private ulong CommittedSize(ulong allocated) => VirtualMemoryManager.PageAlign(
        Math.Max(VirtualMemoryManager.PageSize, Math.Max(initialSize, allocated)));

    private ulong ReservedSize(ulong committed) => VirtualMemoryManager.GranularityAlign(Math.Max(maximumSize, committed));

    private bool CanResize(ulong allocated)
    {
        var committed = CommittedSize(allocated);
        var reserved = ReservedSize(committed);
        return _memory.CanAllocate(committed > _committedBytes ? committed - _committedBytes : 0,
            reserved > _reservedBytes ? reserved - _reservedBytes : 0);
    }

    private void SetUsage(ulong allocated)
    {
        var committed = CommittedSize(allocated);
        var reserved = ReservedSize(committed);
        _memory.UpdateUsage(processId, _committedBytes, _reservedBytes, committed, reserved);
        _allocatedBytes = allocated;
        _committedBytes = committed;
        _reservedBytes = reserved;
    }

    public ulong GetHandle(int threadId = 0)
    {
        lock (_memory.Gate)
        lock (_gate)
        {
            if (_disposed) return 0;
            if (_handle == IntPtr.Zero)
            {
                if (initialSize > ulong.MaxValue - VirtualMemoryManager.AllocationGranularity ||
                    maximumSize > ulong.MaxValue - VirtualMemoryManager.AllocationGranularity || !CanResize(0)) return 0;
                _memory.RegisterProcess(processId);
                _handle = NativeMethods.HeapCreate(options, (UIntPtr)initialSize, (UIntPtr)maximumSize);
                if (_handle == IntPtr.Zero)
                {
                    trace.Warn($"could not create guest heap: Win32 error {Marshal.GetLastWin32Error()}", processId, threadId);
                    return 0;
                }
                SetUsage(0);
            }
            return (ulong)(nuint)_handle;
        }
    }

    public bool MatchesHandle(ulong handle)
    {
        lock (_memory.Gate)
        lock (_gate) return !_disposed && handle != 0 && handle == (ulong)(nuint)_handle;
    }

    public bool Contains(ulong address)
    {
        lock (_memory.Gate)
        lock (_gate) return !_disposed && _blocks.ContainsKey(address);
    }

    public ulong Allocate(ulong size, bool zero = false, int threadId = 0)
    {
        lock (_memory.Gate)
        lock (_gate)
        {
            if (size > ulong.MaxValue - VirtualMemoryManager.AllocationGranularity) return 0;
            var charged = (Math.Max(size, 1UL) + 15) & ~15UL;
            if (GetHandle(threadId) == 0 || charged > ulong.MaxValue - VirtualMemoryManager.AllocationGranularity - _allocatedBytes ||
                !CanResize(_allocatedBytes + charged)) return 0;
            try { _blocks.EnsureCapacity(_blocks.Count + 1); }
            catch (OutOfMemoryException ex)
            {
                trace.Warn($"could not track guest heap allocation: {ex.Message}", processId, threadId);
                return 0;
            }
            var address = (ulong)(nuint)NativeMethods.HeapAlloc(_handle, zero ? ZeroMemory : 0, (UIntPtr)Math.Max(size, 1UL));
            if (address == 0)
            {
                trace.Warn($"guest heap allocation of 0x{size:X} bytes failed", processId, threadId);
                return 0;
            }
            _blocks.Add(address, charged);
            SetUsage(_allocatedBytes + charged);
            return address;
        }
    }

    public bool Free(ulong address, int threadId = 0)
    {
        if (address == 0) return true;
        lock (_memory.Gate)
        lock (_gate)
        {
            if (_disposed || !_blocks.TryGetValue(address, out var charged))
            {
                trace.Warn($"cannot free unknown guest heap block at 0x{address:X}", processId, threadId);
                return false;
            }
            if (!NativeMethods.HeapFree(_handle, 0, (nint)address))
            {
                trace.Warn($"could not free guest heap block at 0x{address:X}", processId, threadId);
                return false;
            }
            _blocks.Remove(address);
            SetUsage(_allocatedBytes - charged);
            return true;
        }
    }

    public ulong Reallocate(ulong address, ulong size, bool zero = false, bool inPlace = false, int threadId = 0)
    {
        lock (_memory.Gate)
        lock (_gate)
        {
            if (_disposed || !_blocks.TryGetValue(address, out var oldCharged))
            {
                trace.Warn($"cannot resize unknown guest heap block at 0x{address:X}", processId, threadId);
                return 0;
            }
            if (size > ulong.MaxValue - VirtualMemoryManager.AllocationGranularity) return 0;
            var charged = (Math.Max(size, 1UL) + 15) & ~15UL;
            var remaining = _allocatedBytes - oldCharged;
            if (charged > ulong.MaxValue - VirtualMemoryManager.AllocationGranularity - remaining ||
                !CanResize(remaining + charged)) return 0;
            var result = (ulong)(nuint)NativeMethods.HeapReAlloc(_handle,
                (zero ? ZeroMemory : 0) | (inPlace ? InPlaceOnly : 0), (nint)address, (UIntPtr)Math.Max(size, 1UL));
            if (result == 0)
            {
                if (inPlace)
                {
                    var previousSize = (ulong)NativeMethods.HeapSize(_handle, 0, (nint)address);
                    if (previousSize != ulong.MaxValue && size <= previousSize) return address;
                }
                if (!inPlace) trace.Warn($"could not resize guest heap block at 0x{address:X} to 0x{size:X} bytes", processId, threadId);
                return 0;
            }
            _blocks.Remove(address);
            _blocks.Add(result, charged);
            SetUsage(remaining + charged);
            return result;
        }
    }

    public ulong Size(ulong address, int threadId = 0)
    {
        lock (_memory.Gate)
        lock (_gate)
        {
            if (_disposed || !_blocks.ContainsKey(address))
            {
                trace.Warn($"cannot query unknown guest heap block at 0x{address:X}", processId, threadId);
                return ulong.MaxValue;
            }
            var size = (ulong)NativeMethods.HeapSize(_handle, 0, (nint)address);
            if (size == ulong.MaxValue) trace.Warn($"could not query guest heap block at 0x{address:X}", processId, threadId);
            return size;
        }
    }

    public void Dispose() => Destroy();

    public bool Destroy()
    {
        lock (_memory.Gate)
        lock (_gate)
        {
            if (_disposed) return true;
            if (_handle != IntPtr.Zero && !NativeMethods.HeapDestroy(_handle))
            {
                trace.Warn("could not destroy guest heap", processId);
                return false;
            }
            _handle = IntPtr.Zero;
            _blocks.Clear();
            if (_committedBytes != 0 || _reservedBytes != 0)
                _memory.UpdateUsage(processId, _committedBytes, _reservedBytes, 0, 0);
            _allocatedBytes = _committedBytes = _reservedBytes = 0;
            _disposed = true;
            return true;
        }
    }
}
