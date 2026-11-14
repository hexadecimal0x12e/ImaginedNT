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
    public MemoryProtection AllocationProtection { get; init; }
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
    private readonly Dictionary<ulong, ulong> _ownedAllocations = new();
    private readonly RuntimeMemory _memory;
    private readonly Dictionary<ulong, (ulong Address, uint Count)> _functionTables = new();
    private readonly Diagnostics.TraceLog? _trace;
    private readonly int _pid;
    private bool _disposed;

    public VirtualMemoryManager(Diagnostics.TraceLog? trace = null, int pid = 0, RuntimeMemory? memory = null)
    {
        _trace = trace;
        _pid = pid;
        _memory = memory ?? new RuntimeMemory();
        _memory.RegisterProcess(pid);
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
        lock (_gate)
        {
            var overlaps = _regions.Where(r => r.BaseAddress < region.EndAddress && region.BaseAddress < r.EndAddress).ToArray();
            foreach (var r in overlaps)
            {
                _regions.Remove(r);
                if (r.BaseAddress < region.BaseAddress) _regions.Add(Slice(r, r.BaseAddress, region.BaseAddress));
                if (r.EndAddress > region.EndAddress) _regions.Add(Slice(r, region.EndAddress, r.EndAddress));
            }
            _regions.Add(region);
        }
    }

    private static MemoryRegion Slice(MemoryRegion r, ulong start, ulong end) => new()
    {
        BaseAddress = start, RegionSize = end - start, AllocationBase = r.AllocationBase,
        AllocationSize = r.AllocationSize, Protection = r.Protection, State = r.State, Type = r.Type, Label = r.Label
    };

    public ulong MapImage(ulong preferredBase, ulong sizeOfImage, string label)
    {
        var size = GranularityAlign(sizeOfImage);
        if (sizeOfImage == 0 || size == 0) throw new NtMemoryException(NtStatus.InvalidParameter, "invalid image size");
        var status = TryAllocate(preferredBase, size, MemoryProtection.ExecuteReadWrite,
            AllocationType.Reserve | AllocationType.Commit, out var address, out _, label);
        if (!NtStatus.IsSuccess(status) && preferredBase != 0)
            status = TryAllocate(0, size, MemoryProtection.ExecuteReadWrite,
                AllocationType.Reserve | AllocationType.Commit, out address, out _, label);
        if (!NtStatus.IsSuccess(status)) throw new NtMemoryException(status, $"could not map image {label} (size=0x{size:X}, status=0x{status:X8})");
        AddRegion(new MemoryRegion
        {
            BaseAddress = address, RegionSize = size, AllocationBase = address, AllocationSize = size,
            Protection = MemoryProtection.ExecuteReadWrite, State = MemoryState.Commit, Type = MemoryType.Image, Label = label
        });
        return address;
    }

    public void AddImageSection(ulong baseAddress, ulong size, MemoryProtection protection, string label)
    {
        var owner = FindRegion(baseAddress);
        AddRegion(new MemoryRegion
        {
            BaseAddress = baseAddress,
            RegionSize = size,
            AllocationBase = owner?.AllocationBase ?? baseAddress,
            AllocationSize = owner?.AllocationSize ?? size,
            Protection = protection,
            State = MemoryState.Commit,
            Type = MemoryType.Image,
            Label = label
        });
    }

    public uint TryAllocate(ulong requestedBase, ulong size, MemoryProtection protection, AllocationType type,
        out ulong actualBase, out ulong actualSize, string? label = null)
    {
        actualBase = actualSize = 0;
        if (size == 0 || size > ulong.MaxValue - AllocationGranularity || requestedBase > ulong.MaxValue - size - PageSize)
            return NtStatus.InvalidParameter;
        if (type is AllocationType.Reset || (uint)type == 0x1000000)
        {
            var resetStart = requestedBase & ~(PageSize - 1);
            var resetLength = PageAlign(size + requestedBase - resetStart);
            lock (_memory.Gate)
            lock (_gate)
            {
                var owner = _ownedAllocations.FirstOrDefault(a => requestedBase >= a.Key && requestedBase - a.Key < a.Value);
                if (owner.Value == 0 || resetLength > owner.Value - (resetStart - owner.Key)) return NtStatus.InvalidAddress;
                var reset = Native.NativeMethods.VirtualAlloc((nint)resetStart, (nuint)resetLength, (uint)type, (uint)protection);
                if (reset == 0) return NtStatus.FromWin32(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
                actualBase = (ulong)reset;
                actualSize = resetLength;
                return NtStatus.Success;
            }
        }
        if ((type & ~(AllocationType.Reserve | AllocationType.Commit | AllocationType.TopDown | AllocationType.WriteWatch)) != 0 ||
            (type & (AllocationType.Reserve | AllocationType.Commit)) == 0) return NtStatus.InvalidParameter;
        if (requestedBase == 0) type |= AllocationType.Reserve;
        var start = requestedBase & ~((type.HasFlag(AllocationType.Reserve) ? AllocationGranularity : PageSize) - 1);
        var length = PageAlign(size + requestedBase - start);
        lock (_memory.Gate)
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var owner = _ownedAllocations.FirstOrDefault(a => start >= a.Key && start - a.Key < a.Value);
            if (!type.HasFlag(AllocationType.Reserve) && (owner.Value == 0 || length > owner.Value - (start - owner.Key)))
                return NtStatus.InvalidAddress;
            var commit = type.HasFlag(AllocationType.Commit) ? length : 0;
            if (commit != 0 && owner.Value != 0)
                commit -= CommittedBytes(start, start + length);
            if (!_memory.CanAllocate(commit, type.HasFlag(AllocationType.Reserve) ? length : 0)) return NtStatus.NoMemory;
            var address = Native.NativeMethods.VirtualAlloc((IntPtr)(long)start, (UIntPtr)length, (uint)type,
                (uint)protection);
            if (address == IntPtr.Zero) return NtStatus.FromWin32(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            actualBase = (ulong)(long)address;
            actualSize = length;
            if (type.HasFlag(AllocationType.Reserve)) _ownedAllocations.Add(actualBase, length);
            var previous = FindRegion(actualBase);
            AddRegion(new MemoryRegion
            {
                BaseAddress = actualBase, RegionSize = length,
                AllocationBase = type.HasFlag(AllocationType.Reserve) ? actualBase : owner.Key,
                AllocationSize = type.HasFlag(AllocationType.Reserve) ? length : owner.Value,
                Protection = type.HasFlag(AllocationType.Commit) ? protection : MemoryProtection.NoAccess,
                State = type.HasFlag(AllocationType.Commit) ? MemoryState.Commit : MemoryState.Reserve,
                Type = previous?.Type ?? MemoryType.Private, Label = label ?? previous?.Label
            });
            _memory.UpdateUsage(_pid, 0, 0, commit, type.HasFlag(AllocationType.Reserve) ? length : 0);
            return NtStatus.Success;
        }
    }

    public uint TryCommit(ulong baseAddress, ulong size, MemoryProtection protection) =>
        baseAddress == 0 ? NtStatus.InvalidParameter :
        TryAllocate(baseAddress, size, protection, AllocationType.Commit, out _, out _);

    private ulong CommittedBytes(ulong start, ulong end)
    {
        ulong committed = 0;
        foreach (var region in _regions)
            if (region.State == MemoryState.Commit && region.BaseAddress < end && region.EndAddress > start)
                committed += Math.Min(region.EndAddress, end) - Math.Max(region.BaseAddress, start);
        return committed;
    }

    public uint TryProtect(ulong baseAddress, ulong size, MemoryProtection protection, out MemoryProtection oldProtection)
    {
        oldProtection = MemoryProtection.NoAccess;
        if (baseAddress == 0 || size == 0) return NtStatus.InvalidParameter;
        if (size > ulong.MaxValue - PageSize || baseAddress > ulong.MaxValue - size - PageSize) return NtStatus.InvalidParameter;
        lock (_memory.Gate)
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var region = FindRegion(baseAddress);
            if (!Native.NativeMethods.VirtualProtect((IntPtr)(long)baseAddress, (UIntPtr)size, (uint)protection, out var old))
                return NtStatus.FromWin32(System.Runtime.InteropServices.Marshal.GetLastWin32Error());

            oldProtection = (MemoryProtection)Native.NativeMethods.ToNtProtection(old);
            if (region is not null) AddRegion(new MemoryRegion
            {
                BaseAddress = baseAddress & ~(PageSize - 1),
                RegionSize = PageAlign(baseAddress + size) - (baseAddress & ~(PageSize - 1)),
                AllocationBase = region?.AllocationBase ?? baseAddress,
                AllocationSize = region?.AllocationSize ?? PageAlign(size),
                Protection = protection,
                State = MemoryState.Commit,
                Type = region?.Type ?? MemoryType.Private,
                Label = region?.Label
            });
        }
        _trace?.Emit(Diagnostics.TraceKind.Memory, $"protect 0x{baseAddress:X16} size=0x{size:X} {oldProtection} -> {protection}", _pid);
        return NtStatus.Success;
    }

    public uint TryFree(ulong baseAddress, ulong size, FreeType freeType)
    {
        if (baseAddress == 0) return NtStatus.InvalidParameter;
        if (freeType != FreeType.Release && freeType != FreeType.Decommit) return NtStatus.InvalidParameter;
        if (freeType == FreeType.Release && size != 0) return NtStatus.InvalidParameter;
        if (size > ulong.MaxValue - PageSize || baseAddress > ulong.MaxValue - size - PageSize) return NtStatus.InvalidParameter;
        var hostType = 0u;
        if ((freeType & FreeType.Decommit) != 0) hostType |= Native.NativeMethods.MEM_DECOMMIT;
        if ((freeType & FreeType.Release) != 0) hostType |= Native.NativeMethods.MEM_RELEASE;
        if (hostType == 0) return NtStatus.InvalidParameter;

        lock (_memory.Gate)
        lock (_gate)
        {
            var owner = _ownedAllocations.FirstOrDefault(a => baseAddress >= a.Key && baseAddress - a.Key < a.Value);
            if (owner.Value == 0 || (freeType == FreeType.Release && owner.Key != baseAddress)) return NtStatus.InvalidAddress;
            if (freeType == FreeType.Decommit && size == 0 && owner.Key != baseAddress) return NtStatus.InvalidParameter;
            if (size != 0 && size > owner.Value - (baseAddress - owner.Key)) return NtStatus.InvalidParameter;
            var start = baseAddress & ~(PageSize - 1);
            var end = freeType == FreeType.Release || size == 0 ? owner.Key + owner.Value : PageAlign(baseAddress + size);
            var committed = CommittedBytes(start, end);
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
            _memory.UpdateUsage(_pid, committed, freeType == FreeType.Release ? owner.Value : 0, 0, 0);
            if (registered) _functionTables.Remove(baseAddress);
            if (freeType == FreeType.Release)
            {
                _ownedAllocations.Remove(baseAddress);
                _regions.RemoveAll(r => r.BaseAddress >= owner.Key && r.BaseAddress < owner.Key + owner.Value);
            }
            else
            {
                var region = FindRegion(start);
                AddRegion(new MemoryRegion
                {
                    BaseAddress = start, RegionSize = end - start, AllocationBase = owner.Key, AllocationSize = owner.Value,
                    State = MemoryState.Reserve, Protection = MemoryProtection.NoAccess,
                    Type = region?.Type ?? MemoryType.Private, Label = region?.Label
                });
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
            Protection = (MemoryProtection)info.Protect,
            AllocationProtection = (MemoryProtection)info.AllocationProtect,
            Type = type,
            Label = label
        };
    }

    public bool IsReadable(ulong address, ulong length)
        => CanAccess(address, length, false);

    public bool IsWritable(ulong address, ulong length)
        => CanAccess(address, length, true);

    private bool CanAccess(ulong address, ulong length, bool write)
    {
        if (length == 0) return true;
        if (address == 0 || address > long.MaxValue || length > (ulong)long.MaxValue - address) return false;
        var current = address;
        var remaining = length;
        while (remaining > 0)
        {
            if (Native.NativeMethods.VirtualQuery((IntPtr)(long)current, out var info, (UIntPtr)System.Runtime.InteropServices.Marshal.SizeOf<Native.NativeMethods.MEMORY_BASIC_INFORMATION>()) == 0)
                return false;
            if (info.State != Native.NativeMethods.MEM_COMMIT) return false;
            if ((info.Protect & Native.NativeMethods.PAGE_NOACCESS) != 0 || (info.Protect & Native.NativeMethods.PAGE_GUARD) != 0) return false;
            var protection = (MemoryProtection)(info.Protect & 0xFF);
            if (write && protection is not (MemoryProtection.ReadWrite or MemoryProtection.WriteCopy or
                MemoryProtection.ExecuteReadWrite or MemoryProtection.ExecuteWriteCopy)) return false;
            if (!write && protection == MemoryProtection.Execute) return false;
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
        if (destination.Length == 0) return true;
        if (address == 0 || address > long.MaxValue || (ulong)destination.Length > (ulong)long.MaxValue - address) return false;
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
        if (source.Length == 0) return true;
        if (address == 0 || address > long.MaxValue || (ulong)source.Length > (ulong)long.MaxValue - address) return false;
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
            var count = (int)Math.Min((ulong)Math.Min(buffer.Length, maxChars - i), PageSize - ((address + offset) & (PageSize - 1)));
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
        lock (_memory.Gate)
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var baseAddress in _ownedAllocations.Keys.ToArray())
            {
                var status = TryFree(baseAddress, 0, FreeType.Release);
                if (!NtStatus.IsSuccess(status)) _trace?.Warn($"could not release guest allocation at 0x{baseAddress:X}: 0x{status:X8}", _pid);
            }
        }
    }
}
