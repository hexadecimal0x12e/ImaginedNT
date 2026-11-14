using System.Runtime.InteropServices;
using ImaginedNT.Core.Diagnostics;
using ImaginedNT.Core.Native;

namespace ImaginedNT.Core.Memory;

[StructLayout(LayoutKind.Sequential)]
public struct MemoryStatus
{
    public uint Length, MemoryLoad;
    public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile;
    public ulong TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
}

[StructLayout(LayoutKind.Sequential)]
public struct ProcessMemoryCounters
{
    public uint Size, PageFaultCount;
    public ulong PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage;
    public ulong QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage;
}

public sealed record RuntimeMemorySnapshot(MemoryStatus Available,
    ProcessMemoryCounters Process, ulong VirtualBytes, ulong PeakVirtualBytes);

public enum HostMemoryKind { Heap, Local, Global, ComTask, Bstr }

public readonly record struct HostMemoryBlock(int ProcessId, ulong Heap, ulong Size, HostMemoryKind Kind);

/// <summary>Guest allocation accounting shared by all processes in one runtime.</summary>
public sealed class RuntimeMemory(ulong maximumPhysicalBytes = 0, ulong maximumVirtualBytes = 0, TraceLog? trace = null)
{
    public const ulong DefaultPhysicalBytes = 8UL * 1024 * 1024 * 1024;
    public const ulong DefaultVirtualBytes = 128UL * 1024 * 1024 * 1024 * 1024;

    private sealed class Usage
    {
        public ulong Committed, Reserved, PeakCommitted, PeakReserved;
    }

    private readonly Usage _total = new();
    private readonly Usage _system = new();
    private ulong _systemPeakWorkingSet;
    private readonly Dictionary<int, Usage> _processes = new();
    private readonly Dictionary<ulong, HostMemoryBlock> _hostBlocks = new();
    private readonly Dictionary<ulong, GuestHeap> _guestHeaps = new();
    public object Gate { get; } = new();
    public ulong MaximumPhysicalBytes { get; } = maximumPhysicalBytes;
    public ulong MaximumVirtualBytes { get; } = maximumVirtualBytes;

    public void RegisterProcess(int pid)
    {
        lock (Gate) _processes.TryAdd(pid, new());
    }

    public GuestHeap? FindGuestHeap(ulong handle)
    {
        lock (Gate) return _guestHeaps.GetValueOrDefault(handle);
    }

    public GuestHeap? FindGuestBlock(ulong address)
    {
        lock (Gate) return _guestHeaps.Values.FirstOrDefault(h => h.ContainsAddress(address));
    }

    internal void RegisterGuestHeap(ulong handle, GuestHeap heap)
    {
        lock (Gate) _guestHeaps.Add(handle, heap);
    }

    internal void ForgetGuestHeap(ulong handle)
    {
        lock (Gate) _guestHeaps.Remove(handle);
    }

    public RuntimeMemorySnapshot GetSnapshot(int? pid = null)
    {
        lock (Gate)
        {
            var physical = MaximumPhysicalBytes == 0 ? Math.Max(DefaultPhysicalBytes, _total.PeakCommitted) : MaximumPhysicalBytes;
            var virtualBytes = MaximumVirtualBytes == 0 ? Math.Max(DefaultVirtualBytes, _total.PeakReserved) : MaximumVirtualBytes;
            var available = new MemoryStatus
            {
                Length = (uint)Marshal.SizeOf<MemoryStatus>(),
                TotalPhysical = physical, AvailablePhysical = Remaining(physical, _total.Committed),
                TotalPageFile = physical, AvailablePageFile = Remaining(physical, _total.Committed),
                TotalVirtual = virtualBytes, AvailableVirtual = Remaining(virtualBytes, _total.Reserved),
                MemoryLoad = (uint)Math.Min(100, (double)_total.Committed / physical * 100)
            };
            var usage = pid is int id ? _processes.GetValueOrDefault(id) ?? new Usage() : _total;
            return new(available, new ProcessMemoryCounters
            {
                Size = (uint)Marshal.SizeOf<ProcessMemoryCounters>(),
                WorkingSetSize = usage.Committed, PeakWorkingSetSize = usage.PeakCommitted,
                PrivateUsage = usage.Committed, PagefileUsage = usage.Committed, PeakPagefileUsage = usage.PeakCommitted
            }, usage.Reserved, usage.PeakReserved);
        }
    }

    public RuntimeMemorySnapshot GetSystemSnapshot()
    {
        lock (Gate)
        {
            using var host = System.Diagnostics.Process.GetCurrentProcess();
            // 来宾分配已包含在宿主占用中，余量归属于模拟内核；各项独立扣减并避免下溢。
            var workingSet = Remaining((ulong)host.WorkingSet64, _total.Committed);
            _system.Committed = Remaining((ulong)host.PrivateMemorySize64, _total.Committed);
            _system.Reserved = Remaining((ulong)host.VirtualMemorySize64, _total.Reserved);
            _system.PeakCommitted = Math.Max(_system.PeakCommitted, _system.Committed);
            _system.PeakReserved = Math.Max(_system.PeakReserved, _system.Reserved);
            _systemPeakWorkingSet = Math.Max(_systemPeakWorkingSet, workingSet);
            return GetSnapshot() with
            {
                Process = new ProcessMemoryCounters
                {
                    Size = (uint)Marshal.SizeOf<ProcessMemoryCounters>(),
                    WorkingSetSize = workingSet, PeakWorkingSetSize = _systemPeakWorkingSet,
                    PrivateUsage = _system.Committed, PagefileUsage = _system.Committed,
                    PeakPagefileUsage = _system.PeakCommitted
                },
                VirtualBytes = _system.Reserved,
                PeakVirtualBytes = _system.PeakReserved
            };
        }
    }

    // Allocation paths hold Gate until the native operation and accounting both finish.
    public bool CanAllocate(ulong physicalBytes, ulong virtualBytes)
    {
        lock (Gate)
        {
            if (physicalBytes <= Remaining(MaximumPhysicalBytes == 0 ? ulong.MaxValue : MaximumPhysicalBytes, _total.Committed) &&
                virtualBytes <= Remaining(MaximumVirtualBytes == 0 ? ulong.MaxValue : MaximumVirtualBytes, _total.Reserved)) return true;
            trace?.Warn($"guest memory limit: requested commit=0x{physicalBytes:X}, virtual=0x{virtualBytes:X}, " +
                $"used commit=0x{_total.Committed:X}, virtual=0x{_total.Reserved:X}, " +
                $"limits commit=0x{MaximumPhysicalBytes:X}, virtual=0x{MaximumVirtualBytes:X}");
            return false;
        }
    }

    public void UpdateUsage(int pid, ulong oldCommitted, ulong oldReserved, ulong committed, ulong reserved)
    {
        lock (Gate)
        {
            Update(_processes[pid], oldCommitted, oldReserved, committed, reserved);
            Update(_total, oldCommitted, oldReserved, committed, reserved);
        }
    }

    private static void Update(Usage usage, ulong oldCommitted, ulong oldReserved, ulong committed, ulong reserved)
    {
        usage.Committed = checked(usage.Committed - oldCommitted + committed);
        usage.Reserved = checked(usage.Reserved - oldReserved + reserved);
        usage.PeakCommitted = Math.Max(usage.PeakCommitted, usage.Committed);
        usage.PeakReserved = Math.Max(usage.PeakReserved, usage.Reserved);
    }

    public ulong GetHostBlockSize(ulong heap, ulong address)
    {
        lock (Gate) return _hostBlocks.TryGetValue(address, out var block) && block.Heap == heap ? block.Size : 0;
    }

    public bool TryGetHostBlock(ulong address, out HostMemoryBlock block)
    {
        lock (Gate) return _hostBlocks.TryGetValue(address, out block);
    }

    public bool CanUseHostBlock(ulong address, HostMemoryKind kind)
    {
        lock (Gate) return address == 0 || FindGuestBlock(address) is null &&
            (!_hostBlocks.TryGetValue(address, out var block) || block.Kind == kind);
    }

    public bool PrepareHostAllocation(ulong size, ulong oldSize = 0)
    {
        lock (Gate)
        {
            if (size > long.MaxValue - 15) return false;
            var charged = ChargeSize(size);
            var extra = charged > oldSize ? charged - oldSize : 0;
            return CanAllocate(extra, extra) && PrepareHostAllocation();
        }
    }

    public static ulong ChargeSize(ulong size) => (Math.Max(size, 1UL) + 15) & ~15UL;

    public bool PrepareHostAllocation()
    {
        lock (Gate)
        {
            try { _hostBlocks.EnsureCapacity(_hostBlocks.Count + 1); }
            catch (OutOfMemoryException ex)
            {
                trace?.Warn($"could not track guest block on host heap: {ex.Message}");
                return false;
            }
            return true;
        }
    }

    public void TrackHostAllocation(int pid, ulong heap, ulong oldAddress, ulong address, ulong size,
        HostMemoryKind kind = HostMemoryKind.Heap)
    {
        lock (Gate)
        {
            if (oldAddress != 0 && _hostBlocks.Remove(oldAddress, out var old))
            {
                pid = old.ProcessId;
                UpdateUsage(pid, old.Size, old.Size, 0, 0);
            }
            // 原生组件可能已释放并复用了地址，旧记录不能阻止新分配入账。
            if (_hostBlocks.Remove(address, out var previous))
                UpdateUsage(previous.ProcessId, previous.Size, previous.Size, 0, 0);
            UpdateUsage(pid, 0, 0, size, size);
            _hostBlocks.Add(address, new(pid, heap, size, kind));
        }
    }

    public void ForgetHostAllocation(ulong heap, ulong address)
    {
        lock (Gate)
            if (_hostBlocks.TryGetValue(address, out var block) && block.Heap == heap)
            {
                _hostBlocks.Remove(address);
                UpdateUsage(block.ProcessId, block.Size, block.Size, 0, 0);
            }
    }

    public void FreeHostAllocations(int pid)
    {
        lock (Gate)
        {
            foreach (var block in _hostBlocks.Where(b => b.Value.ProcessId == pid).ToArray())
            {
                if (block.Value.Kind != HostMemoryKind.Heap)
                {
                    // 这些分配允许向原生组件转移所有权，退出时仅撤销记账。
                    ForgetHostAllocation(block.Value.Heap, block.Key);
                    continue;
                }
                var freed = NativeMethods.HeapFree((nint)block.Value.Heap, 0, (nint)block.Key);
                if (freed) ForgetHostAllocation(block.Value.Heap, block.Key);
                else trace?.Warn($"could not free {block.Value.Kind} block at 0x{block.Key:X}", pid);
            }
        }
    }

    private static ulong Remaining(ulong total, ulong used) => used < total ? total - used : 0;

}
