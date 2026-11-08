using ImaginedNT.Core.Nt;

namespace ImaginedNT.Core.Memory;

public sealed class MemoryRegion
{
    public required ulong BaseAddress { get; init; }
    public required ulong RegionSize { get; init; }
    public ulong AllocationBase { get; init; }
    public ulong AllocationSize { get; init; }
    public MemoryProtection Protection { get; set; }
    public MemoryState State { get; set; }
    public MemoryType Type { get; init; } = MemoryType.Private;
    public string? Label { get; init; }

    public ulong EndAddress => BaseAddress + RegionSize;
    public bool Contains(ulong address) => address >= BaseAddress && address < EndAddress;
    public bool ContainsRange(ulong address, ulong length) => address >= BaseAddress && address + length <= EndAddress && address + length >= address;
    public override string ToString() => $"0x{BaseAddress:X16}-0x{EndAddress:X16} {State} {Protection} {Type} {Label}";
}

public sealed class MemoryQueryResult
{
    public ulong BaseAddress { get; init; }
    public ulong AllocationBase { get; init; }
    public ulong RegionSize { get; init; }
    public MemoryState State { get; init; }
    public MemoryProtection Protection { get; init; }
    public MemoryType Type { get; init; }
    public string? Label { get; init; }
}

public sealed class NtMemoryException : Exception
{
    public uint Status { get; }
    public NtMemoryException(uint status, string message) : base(message) => Status = status;
}

/// <summary>
/// The virtual-memory manager for one imagined process.  It is a thin, tracked façade over the
/// host's own address space: the guest image really executes in this process, so the NT memory
/// semantics (reserve/commit/protect/query) are mapped onto VirtualAlloc/VirtualProtect while the
/// manager keeps the metadata the NT layer needs for query and diagnostics.
/// </summary>
public sealed unsafe class VirtualMemoryManager : IDisposable
{
    public const ulong PageSize = 0x1000;
    public const ulong AllocationGranularity = 0x10000;

    private readonly object _gate = new();
    private readonly List<MemoryRegion> _regions = new();
    private readonly List<ulong> _ownedAllocations = new();
    private readonly Dictionary<ulong, (ulong Address, uint Count)> _functionTables = new();
    private readonly Diagnostics.TraceLog? _trace;
    private readonly int _pid;
    private bool _disposed;

    public VirtualMemoryManager(Diagnostics.TraceLog? trace = null, int pid = 0)
    {
        _trace = trace;
        _pid = pid;
    }

    public IReadOnlyList<MemoryRegion> Regions
    {
        get { lock (_gate) return _regions.ToArray(); }
    }

    public static ulong PageAlign(ulong value) => (value + PageSize - 1) & ~(PageSize - 1);
    public static ulong GranularityAlign(ulong value) => (value + AllocationGranularity - 1) & ~(AllocationGranularity - 1);

    public MemoryRegion? FindRegion(ulong address)
    {
        lock (_gate)
        {
            MemoryRegion? best = null;
            foreach (var r in _regions)
            {
                if (r.Contains(address) && (best is null || r.BaseAddress >= best.BaseAddress)) best = r;
            }
            return best;
        }
    }

    public void AddRegion(MemoryRegion region)
    {
        lock (_gate) _regions.Add(region);
    }

    /// <summary>Reserve/commit a new image mapping with RWX and remember its allocation.</summary>
    public ulong MapImage(ulong preferredBase, ulong sizeOfImage, string label)
    {
        var size = GranularityAlign(sizeOfImage);
        var address = Native.NativeMethods.VirtualAlloc(
            preferredBase == 0 ? IntPtr.Zero : (IntPtr)(long)preferredBase,
            (UIntPtr)size,
            Native.NativeMethods.MEM_RESERVE | Native.NativeMethods.MEM_COMMIT,
            Native.NativeMethods.PAGE_EXECUTE_READWRITE);

        if (address == IntPtr.Zero && preferredBase != 0)
        {
            address = Native.NativeMethods.VirtualAlloc(IntPtr.Zero, (UIntPtr)size,
                Native.NativeMethods.MEM_RESERVE | Native.NativeMethods.MEM_COMMIT,
                Native.NativeMethods.PAGE_EXECUTE_READWRITE);
        }

        if (address == IntPtr.Zero)
        {
            var error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            throw new NtMemoryException(NtStatus.FromWin32(error), $"VirtualAlloc failed for image {label} (0x{size:X}) err={error}");
        }

        var actual = (ulong)(long)address;
        lock (_gate) _ownedAllocations.Add(actual);
        AddRegion(new MemoryRegion
        {
            BaseAddress = actual,
            RegionSize = size,
            AllocationBase = actual,
            AllocationSize = size,
            Protection = MemoryProtection.ExecuteReadWrite,
            State = MemoryState.Commit,
            Type = MemoryType.Image,
            Label = label
        });
        _trace?.Emit(Diagnostics.TraceKind.Memory, $"mapped image {label} size=0x{size:X} at 0x{actual:X16} (preferred 0x{preferredBase:X16})", _pid);
        return actual;
    }

    public void AddImageSection(ulong baseAddress, ulong size, MemoryProtection protection, string label)
    {
        AddRegion(new MemoryRegion
        {
            BaseAddress = baseAddress,
            RegionSize = size,
            AllocationBase = baseAddress,
            AllocationSize = size,
            Protection = protection,
            State = MemoryState.Commit,
            Type = MemoryType.Image,
            Label = label
        });
    }

    public uint TryAllocate(ulong requestedBase, ulong size, MemoryProtection protection, AllocationType type,
        out ulong actualBase, out ulong actualSize, string? label = null)
    {
        actualBase = 0;
        actualSize = 0;
        if (size == 0) return NtStatus.InvalidParameter;
        if ((type & (AllocationType.Reserve | AllocationType.Commit)) == 0) return NtStatus.InvalidParameter;

        // Windows accepts VirtualAlloc(NULL, size, MEM_COMMIT, ...) and commits a fresh region.
        // Treat it as reserve+commit so the allocation is tracked and released as a unit.
        if ((type & AllocationType.Reserve) == 0 && requestedBase == 0)
            type |= AllocationType.Reserve;

        if (requestedBase != 0)
        {
            requestedBase = (type & AllocationType.Reserve) != 0
                ? requestedBase & ~(AllocationGranularity - 1)
                : requestedBase & ~(PageSize - 1);
        }

        var allocSize = (type & AllocationType.Reserve) != 0 ? GranularityAlign(size) : PageAlign(size);
        var hostType = 0u;
        if ((type & AllocationType.Reserve) != 0) hostType |= Native.NativeMethods.MEM_RESERVE;
        if ((type & AllocationType.Commit) != 0) hostType |= Native.NativeMethods.MEM_COMMIT;
        if ((type & AllocationType.Reset) != 0) hostType |= 0x0008_0000;
        if ((type & AllocationType.LargePages) != 0) hostType |= 0x2000_0000;

        var addr = Native.NativeMethods.VirtualAlloc(
            requestedBase == 0 ? IntPtr.Zero : (IntPtr)(long)requestedBase,
            (UIntPtr)allocSize,
            hostType,
            Native.NativeMethods.ToPageProtection((uint)protection));

        if (addr == IntPtr.Zero)
        {
            var err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            return NtStatus.FromWin32(err);
        }

        actualBase = (ulong)(long)addr;
        actualSize = allocSize;
        if ((type & AllocationType.Reserve) != 0)
        {
            lock (_gate) _ownedAllocations.Add(actualBase);
        }

        AddRegion(new MemoryRegion
        {
            BaseAddress = actualBase,
            RegionSize = actualSize,
            AllocationBase = actualBase,
            AllocationSize = actualSize,
            Protection = protection,
            State = (type & AllocationType.Commit) != 0 ? MemoryState.Commit : MemoryState.Reserve,
            Type = MemoryType.Private,
            Label = label
        });

        _trace?.Emit(Diagnostics.TraceKind.Memory,
            $"NtAllocateVirtualMemory base=0x{actualBase:X16} size=0x{actualSize:X} type={type} prot={protection}", _pid);
        return NtStatus.Success;
    }

    public uint TryCommit(ulong baseAddress, ulong size, MemoryProtection protection)
    {
        if (baseAddress == 0 || size == 0) return NtStatus.InvalidParameter;
        var alignedBase = baseAddress & ~(PageSize - 1);
        var alignedSize = PageAlign(baseAddress + size) - alignedBase;
        var addr = Native.NativeMethods.VirtualAlloc((IntPtr)(long)alignedBase, (UIntPtr)alignedSize,
            Native.NativeMethods.MEM_COMMIT, Native.NativeMethods.ToPageProtection((uint)protection));
        if (addr == IntPtr.Zero)
        {
            return NtStatus.FromWin32(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
        }

        AddRegion(new MemoryRegion
        {
            BaseAddress = alignedBase,
            RegionSize = alignedSize,
            AllocationBase = alignedBase,
            AllocationSize = alignedSize,
            Protection = protection,
            State = MemoryState.Commit,
            Type = MemoryType.Private
        });
        _trace?.Emit(Diagnostics.TraceKind.Memory, $"commit 0x{baseAddress:X16} size=0x{size:X} prot={protection}", _pid);
        return NtStatus.Success;
    }

    public uint TryProtect(ulong baseAddress, ulong size, MemoryProtection protection, out MemoryProtection oldProtection)
    {
        oldProtection = MemoryProtection.NoAccess;
        if (baseAddress == 0 || size == 0) return NtStatus.InvalidParameter;
        var addr = (IntPtr)(long)baseAddress;
        if (!Native.NativeMethods.VirtualProtect(addr, (UIntPtr)size, Native.NativeMethods.ToPageProtection((uint)protection), out var old))
        {
            return NtStatus.FromWin32(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
        }

        oldProtection = (MemoryProtection)Native.NativeMethods.ToNtProtection(old);
        AddRegion(new MemoryRegion
        {
            BaseAddress = baseAddress & ~(PageSize - 1),
            RegionSize = PageAlign(baseAddress + size) - (baseAddress & ~(PageSize - 1)),
            AllocationBase = baseAddress & ~(PageSize - 1),
            AllocationSize = PageAlign(size),
            Protection = protection,
            State = MemoryState.Commit,
            Type = MemoryType.Private
        });
        _trace?.Emit(Diagnostics.TraceKind.Memory, $"protect 0x{baseAddress:X16} size=0x{size:X} {oldProtection} -> {protection}", _pid);
        return NtStatus.Success;
    }

    public uint TryFree(ulong baseAddress, ulong size, FreeType freeType)
    {
        if (baseAddress == 0) return NtStatus.InvalidParameter;
        var hostType = 0u;
        if ((freeType & FreeType.Decommit) != 0) hostType |= Native.NativeMethods.MEM_DECOMMIT;
        if ((freeType & FreeType.Release) != 0) hostType |= Native.NativeMethods.MEM_RELEASE;
        if (hostType == 0) return NtStatus.InvalidParameter;

        lock (_gate)
        {
            var registered = (freeType & FreeType.Release) != 0 && _functionTables.ContainsKey(baseAddress);
            var table = _functionTables.GetValueOrDefault(baseAddress);
            if (registered && !Native.NativeMethods.RtlDeleteFunctionTable((nint)table.Address))
            {
                _trace?.Warn($"could not unregister exception table at 0x{table.Address:X}; retaining image", _pid);
                return NtStatus.AccessDenied;
            }
            var ok = Native.NativeMethods.VirtualFree((IntPtr)(long)baseAddress,
                (freeType & FreeType.Release) != 0 ? UIntPtr.Zero : (UIntPtr)size, hostType);
            if (!ok)
            {
                var status = NtStatus.FromWin32(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
                if (registered && !Native.NativeMethods.RtlAddFunctionTable((nint)table.Address, table.Count, baseAddress))
                {
                    _functionTables.Remove(baseAddress);
                    _trace?.Error($"could not restore exception table at 0x{table.Address:X} after failed image release", _pid);
                }
                return status;
            }
            if (registered) _functionTables.Remove(baseAddress);
            var allocationRegion = _regions.FirstOrDefault(r => r.BaseAddress == baseAddress);
            var effectiveSize = size != 0 ? size : allocationRegion?.RegionSize ?? 0;
            if ((freeType & FreeType.Release) != 0)
            {
                _ownedAllocations.Remove(baseAddress);
                _regions.RemoveAll(r => r.BaseAddress >= baseAddress &&
                                        (effectiveSize == 0 || r.BaseAddress < baseAddress + effectiveSize));
            }
            else
            {
                _regions.RemoveAll(r => r.BaseAddress >= baseAddress && r.BaseAddress < baseAddress + effectiveSize);
            }
        }

        _trace?.Emit(Diagnostics.TraceKind.Memory, $"free 0x{baseAddress:X16} size=0x{size:X} {freeType}", _pid);
        return NtStatus.Success;
    }

    public bool RegisterFunctionTable(ulong imageBase, ulong table, uint count)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _functionTables.Add(imageBase, (table, count));
            if (Native.NativeMethods.RtlAddFunctionTable((nint)table, count, imageBase)) return true;
            _functionTables.Remove(imageBase);
            return false;
        }
    }

    public MemoryQueryResult? Query(ulong address)
    {
        if (address == 0) return null;
        if (Native.NativeMethods.VirtualQuery((IntPtr)(long)address, out var info, (UIntPtr)System.Runtime.InteropServices.Marshal.SizeOf<Native.NativeMethods.MEMORY_BASIC_INFORMATION>()) == 0)
        {
            return null;
        }

        var label = FindRegion(address)?.Label;
        var state = (MemoryState)info.State;
        var type = (MemoryType)info.Type;
        return new MemoryQueryResult
        {
            BaseAddress = (ulong)(long)info.BaseAddress,
            AllocationBase = (ulong)(long)info.AllocationBase,
            RegionSize = (ulong)info.RegionSize,
            State = state,
            Protection = (MemoryProtection)Native.NativeMethods.ToNtProtection(info.Protect),
            Type = type,
            Label = label
        };
    }

    public bool IsReadable(ulong address, ulong length)
    {
        if (address == 0 || length == 0) return false;
        var current = address;
        var remaining = length;
        while (remaining > 0)
        {
            if (Native.NativeMethods.VirtualQuery((IntPtr)(long)current, out var info, (UIntPtr)System.Runtime.InteropServices.Marshal.SizeOf<Native.NativeMethods.MEMORY_BASIC_INFORMATION>()) == 0)
                return false;
            if (info.State != Native.NativeMethods.MEM_COMMIT) return false;
            if ((info.Protect & Native.NativeMethods.PAGE_NOACCESS) != 0 || (info.Protect & Native.NativeMethods.PAGE_GUARD) != 0) return false;
            var regionEnd = (ulong)(long)info.BaseAddress + (ulong)info.RegionSize;
            var span = Math.Min(remaining, regionEnd - current);
            if (span == 0 || regionEnd <= current) return false;
            current += span;
            remaining -= span;
        }
        return true;
    }

    public bool TryRead(ulong address, Span<byte> destination)
    {
        if (address == 0 || destination.Length == 0) return false;
        fixed (byte* buffer = destination)
        {
            return Native.NativeMethods.ReadProcessMemory(Native.NativeMethods.GetCurrentProcess(),
                (IntPtr)(long)address, buffer, (UIntPtr)(uint)destination.Length, out var read)
                && (ulong)read == (ulong)destination.Length;
        }
    }

    public byte[] ReadBytes(ulong address, int length)
    {
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
        var buffer = new byte[length];
        if (length > 0 && !TryRead(address, buffer))
            throw new NtMemoryException(NtStatus.AccessViolation, $"unreadable guest memory at 0x{address:X16} length {length}");
        return buffer;
    }

    public T Read<T>(ulong address) where T : unmanaged
    {
        var size = System.Runtime.CompilerServices.Unsafe.SizeOf<T>();
        var bytes = ReadBytes(address, size);
        return System.Runtime.CompilerServices.Unsafe.ReadUnaligned<T>(ref bytes[0]);
    }

    public bool TryWrite(ulong address, ReadOnlySpan<byte> source)
    {
        if (address == 0 || source.Length == 0) return false;
        fixed (byte* buffer = source)
        {
            return Native.NativeMethods.WriteProcessMemory(Native.NativeMethods.GetCurrentProcess(),
                (IntPtr)(long)address, buffer, (UIntPtr)(uint)source.Length, out var written)
                && (ulong)written == (ulong)source.Length;
        }
    }

    public void Write<T>(ulong address, T value) where T : unmanaged
    {
        var bytes = new byte[System.Runtime.CompilerServices.Unsafe.SizeOf<T>()];
        System.Runtime.CompilerServices.Unsafe.WriteUnaligned(ref bytes[0], value);
        if (!TryWrite(address, bytes))
            throw new NtMemoryException(NtStatus.AccessViolation, $"unwritable guest memory at 0x{address:X16}");
    }

    public void WriteBytes(ulong address, ReadOnlySpan<byte> bytes)
    {
        if (!TryWrite(address, bytes))
            throw new NtMemoryException(NtStatus.AccessViolation, $"unwritable guest memory at 0x{address:X16} length {bytes.Length}");
    }

    /// <summary>Read a NUL-terminated UTF-16 string from guest memory.</summary>
    public string ReadUnicodeString(ulong address, int maxChars = 0x8000)
    {
        if (address == 0) return string.Empty;
        var sb = new System.Text.StringBuilder(Math.Min(maxChars, 260));
        var chunk = new byte[512];
        var offset = 0UL;
        for (var i = 0; i < maxChars; i++)
        {
            if (!TryRead(address + offset, chunk.AsSpan(0, 2)))
                throw new NtMemoryException(NtStatus.AccessViolation, $"unreadable UNICODE_STRING at 0x{address:X16}");
            var ch = (char)(chunk[0] | (chunk[1] << 8));
            if (ch == '\0') break;
            sb.Append(ch);
            offset += 2;
        }
        return sb.ToString();
    }

    public string ReadAnsiString(ulong address, int maxChars = 0x10000)
    {
        if (address == 0) return string.Empty;
        var sb = new System.Text.StringBuilder(Math.Min(maxChars, 260));
        var buffer = new byte[256];
        var i = 0;
        var offset = 0UL;
        while (i < maxChars)
        {
            var count = Math.Min(buffer.Length, maxChars - i);
            if (!TryRead(address + offset, buffer.AsSpan(0, count)))
                throw new NtMemoryException(NtStatus.AccessViolation, $"unreadable ANSI string at 0x{address:X16}");
            for (var j = 0; j < count; j++)
            {
                if (buffer[j] == 0) return sb.ToString();
                sb.Append((char)buffer[j]);
                i++;
            }
            offset += (ulong)count;
        }
        return sb.ToString();
    }

    public string ReadUnicodeString(ushort lengthBytes, ulong bufferAddress)
    {
        if (bufferAddress == 0 || lengthBytes == 0) return string.Empty;
        var chars = lengthBytes / 2;
        var bytes = ReadBytes(bufferAddress, chars * 2);
        return System.Text.Encoding.Unicode.GetString(bytes);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            // Release allocations that were not already released.  This is important for test
            // hosts which load several images into one process.  In a one-shot runner the process
            // exits immediately afterwards, so leaking would also be harmless.
            foreach (var baseAddress in _ownedAllocations.ToArray())
            {
                if (_functionTables.TryGetValue(baseAddress, out var table))
                {
                    if (!Native.NativeMethods.RtlDeleteFunctionTable((nint)table.Address))
                    {
                        _trace?.Warn($"could not unregister exception table at 0x{table.Address:X}; retaining image", _pid);
                        continue;
                    }
                    _functionTables.Remove(baseAddress);
                }
                Native.NativeMethods.VirtualFree((IntPtr)(long)baseAddress, UIntPtr.Zero, Native.NativeMethods.MEM_RELEASE);
            }
            _ownedAllocations.Clear();
            _regions.Clear();
        }
    }
}
