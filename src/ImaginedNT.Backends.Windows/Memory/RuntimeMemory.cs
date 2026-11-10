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
    private readonly Dictionary<(ulong Heap, ulong Address), (int Pid, ulong Size)> _hostBlocks = new();
    public object Gate { get; } = new();
    public ulong MaximumPhysicalBytes { get; } = maximumPhysicalBytes;
    public ulong MaximumVirtualBytes { get; } = maximumVirtualBytes;

    public void RegisterProcess(int pid)
    {
        lock (Gate) _processes.TryAdd(pid, new());
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
        lock (Gate) return _hostBlocks.GetValueOrDefault((heap, address)).Size;
    }

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

    public void TrackHostAllocation(int pid, ulong heap, ulong oldAddress, ulong address, ulong size)
    {
        lock (Gate)
        {
            if (_hostBlocks.Remove((heap, oldAddress), out var old))
            {
                pid = old.Pid;
                UpdateUsage(pid, old.Size, old.Size, size, size);
            }
            else UpdateUsage(pid, 0, 0, size, size);
            _hostBlocks.Add((heap, address), (pid, size));
        }
    }

    public void ForgetHostAllocation(ulong heap, ulong address)
    {
        lock (Gate)
            if (_hostBlocks.Remove((heap, address), out var block)) UpdateUsage(block.Pid, block.Size, block.Size, 0, 0);
    }

    public void FreeHostAllocations(int pid)
    {
        lock (Gate)
        {
            foreach (var block in _hostBlocks.Where(b => b.Value.Pid == pid).ToArray())
            {
                if (NativeMethods.HeapFree((nint)block.Key.Heap, 0, (nint)block.Key.Address))
                    ForgetHostAllocation(block.Key.Heap, block.Key.Address);
                else trace?.Warn($"could not free guest block on host heap at 0x{block.Key.Address:X}", pid);
            }
        }
    }

    private static ulong Remaining(ulong total, ulong used) => used < total ? total - used : 0;
}
