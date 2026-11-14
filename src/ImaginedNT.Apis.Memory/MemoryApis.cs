using static ImaginedNT.Apis.ApiHelpers;
using ImaginedNT.Core.Memory;
using ImaginedNT.Core.Nt;

namespace ImaginedNT.Core.Runtime;

public static class MemoryApis
{
    internal static uint TargetMemory(ApiCallContext ctx, ulong handle, uint access, out VirtualMemoryManager memory)
    {
        memory = ctx.Memory;
        if (handle == CurrentProcessPseudoHandle) return NtStatus.Success;
        var status = ctx.Handles.TryGet(handle, access, out var obj);
        if (status != NtStatus.Success) return status;
        if (obj is not ProcessObject p) return NtStatus.InvalidHandle;
        memory = p.Process.Memory;
        return NtStatus.Success;
    }

    [VirtualApi("ntdll.dll", "NtAllocateVirtualMemory", ApiCategory.Memory, "ntdll!ZwAllocateVirtualMemory")]
    private static ulong NtAllocateVirtualMemory(ApiCallContext ctx)
    {
        var status = TargetMemory(ctx, ctx.Arg(0), 8, out var memory);
        if (status != NtStatus.Success) return status;
        var basePtr = ctx.Arg(1);
        var sizePtr = ctx.Arg(3);
        if (basePtr == 0 || sizePtr == 0 || ctx.Arg(2) != 0) return NtStatus.InvalidParameter;
        if (!ctx.Memory.IsWritable(basePtr, 8) || !ctx.Memory.IsWritable(sizePtr, 8)) return NtStatus.AccessViolation;
        var requestedBase = ctx.Read<ulong>(basePtr);
        var size = ctx.Read<ulong>(sizePtr);
        var allocationType = (AllocationType)ctx.Arg32(4);
        var protection = (MemoryProtection)ctx.Arg32(5);
        status = memory.TryAllocate(requestedBase, size, protection, allocationType,
            out var actualBase, out var actualSize, "NtAllocateVirtualMemory");
        if (NtStatus.IsSuccess(status))
        {
            if (basePtr != 0) ctx.Memory.Write(basePtr, actualBase);
            ctx.Memory.Write(sizePtr, actualSize);
        }
        return status;
    }

    [VirtualApi("ntdll.dll", "NtFreeVirtualMemory", ApiCategory.Memory, "ntdll!ZwFreeVirtualMemory")]
    private static ulong NtFreeVirtualMemory(ApiCallContext ctx)
    {
        var status = TargetMemory(ctx, ctx.Arg(0), 8, out var memory);
        if (status != NtStatus.Success) return status;
        var basePtr = ctx.Arg(1);
        var sizePtr = ctx.Arg(2);
        if (basePtr == 0 || sizePtr == 0) return NtStatus.InvalidParameter;
        if (!ctx.Memory.IsWritable(basePtr, 8) || !ctx.Memory.IsWritable(sizePtr, 8)) return NtStatus.AccessViolation;
        var address = ctx.Read<ulong>(basePtr);
        var size = ctx.Read<ulong>(sizePtr);
        var freeType = (FreeType)ctx.Arg32(3);
        var allocationSize = memory.FindRegion(address)?.AllocationSize ?? 0;
        status = memory.TryFree(address, size, freeType);
        if (NtStatus.IsSuccess(status))
        {
            var start = address & ~(VirtualMemoryManager.PageSize - 1);
            ctx.Memory.Write(basePtr, start);
            ctx.Memory.Write(sizePtr, size == 0 ? allocationSize : VirtualMemoryManager.PageAlign(address + size) - start);
        }
        return status;
    }

    [VirtualApi("ntdll.dll", "NtProtectVirtualMemory", ApiCategory.Memory, "ntdll!ZwProtectVirtualMemory")]
    private static ulong NtProtectVirtualMemory(ApiCallContext ctx)
    {
        var status = TargetMemory(ctx, ctx.Arg(0), 8, out var memory);
        if (status != NtStatus.Success) return status;
        var basePtr = ctx.Arg(1);
        var sizePtr = ctx.Arg(2);
        var oldPtr = ctx.Arg(4);
        if (basePtr == 0 || sizePtr == 0 || oldPtr == 0) return NtStatus.InvalidParameter;
        if (!ctx.Memory.IsWritable(oldPtr, 4) || !ctx.Memory.IsWritable(basePtr, 8) ||
            !ctx.Memory.IsWritable(sizePtr, 8)) return NtStatus.AccessViolation;
        var address = ctx.Read<ulong>(basePtr);
        var size = ctx.Read<ulong>(sizePtr);
        status = memory.TryProtect(address, size, (MemoryProtection)ctx.Arg32(3), out var old);
        if (NtStatus.IsSuccess(status))
        {
            if (oldPtr != 0) ctx.Memory.Write(oldPtr, (uint)old);
            var start = address & ~(VirtualMemoryManager.PageSize - 1);
            ctx.Memory.Write(basePtr, start);
            ctx.Memory.Write(sizePtr, VirtualMemoryManager.PageAlign(address + size) - start);
        }
        return status;
    }

    [VirtualApi("ntdll.dll", "NtQueryVirtualMemory", ApiCategory.Memory, "ntdll!ZwQueryVirtualMemory")]
    private static ulong NtQueryVirtualMemory(ApiCallContext ctx)
    {
        var status = TargetMemory(ctx, ctx.Arg(0), 0x400, out var memory);
        if (status != NtStatus.Success) return status;
        var address = ctx.Arg(1);
        var infoClass = ctx.Arg32(2);
        var infoPtr = ctx.Arg(3);
        var infoLength = ctx.Arg(4);
        var returnLengthPtr = ctx.Arg(5);
        var query = memory.Query(address);
        if (query is null) return NtStatus.InvalidAddress;
        if (infoClass == 0)
        {
            const int size = 48;
            if (returnLengthPtr != 0) ctx.Memory.Write(returnLengthPtr, (ulong)size);
            if (infoLength < size) return NtStatus.InfoLengthMismatch;
            if (infoPtr == 0) return NtStatus.AccessViolation;
            if (infoPtr != 0)
            {
                ctx.Memory.Write(infoPtr, query.BaseAddress);
                ctx.Memory.Write(infoPtr + 8, query.AllocationBase);
                ctx.Memory.Write(infoPtr + 16, (uint)query.AllocationProtection);
                ctx.Memory.Write(infoPtr + 20, (ushort)0);
                ctx.Memory.Write(infoPtr + 22, (ushort)0);
                ctx.Memory.Write(infoPtr + 24, query.RegionSize);
                ctx.Memory.Write(infoPtr + 32, (uint)query.State);
                ctx.Memory.Write(infoPtr + 36, (uint)query.Protection);
                ctx.Memory.Write(infoPtr + 40, (uint)query.Type);
                ctx.Memory.Write(infoPtr + 44, 0u);
            }
            return NtStatus.Success;
        }
        if (infoClass == 2)
            return HostQueryMemory(new IntPtr(-1), (IntPtr)(long)address, infoClass,
                (IntPtr)(long)infoPtr, (nuint)infoLength, (IntPtr)(long)returnLengthPtr);
        return NtStatus.InvalidInfoClass;
    }

    [VirtualApi("ntdll.dll", "NtLockVirtualMemory", ApiCategory.Memory, "ntdll!ZwLockVirtualMemory")]
    private static ulong NtLockVirtualMemory(ApiCallContext ctx) => LockNt(ctx, true);

    [VirtualApi("ntdll.dll", "NtUnlockVirtualMemory", ApiCategory.Memory, "ntdll!ZwUnlockVirtualMemory")]
    private static ulong NtUnlockVirtualMemory(ApiCallContext ctx) => LockNt(ctx, false);

    // --- kernel32 wrappers ---------------------------------------------------------
    [VirtualApi("kernel32.dll", "VirtualAlloc", ApiCategory.Memory, "kernelbase!VirtualAlloc")]
    private static ulong VirtualAlloc(ApiCallContext ctx) => VirtualAllocCore(ctx, 0, ctx.Memory);

    [VirtualApi("kernel32.dll", "VirtualAllocEx", ApiCategory.Memory, "kernelbase!VirtualAllocEx")]
    private static ulong VirtualAllocEx(ApiCallContext ctx)
    {
        var status = TargetMemory(ctx, ctx.Arg(0), 8, out var memory);
        return status == NtStatus.Success ? VirtualAllocCore(ctx, 1, memory) : BoolResult(ctx, false, Win32FromStatus(status));
    }

    private static ulong VirtualAllocCore(ApiCallContext ctx, int offset, VirtualMemoryManager memory)
    {
        var requestedBase = ctx.Arg(offset);
        var size = ctx.Arg(offset + 1);
        var type = (AllocationType)ctx.Arg32(offset + 2);
        var protection = (MemoryProtection)ctx.Arg32(offset + 3);
        var status = memory.TryAllocate(requestedBase, size, protection, type,
            out var actualBase, out _, "VirtualAlloc");
        if (!NtStatus.IsSuccess(status))
        {
            ctx.SetLastError(Win32FromStatus(status));
            return 0;
        }
        return actualBase;
    }

    [VirtualApi("kernel32.dll", "VirtualFree", ApiCategory.Memory, "kernelbase!VirtualFree")]
    private static ulong VirtualFree(ApiCallContext ctx) => VirtualFreeCore(ctx, 0, ctx.Memory);

    [VirtualApi("kernel32.dll", "VirtualFreeEx", ApiCategory.Memory, "kernelbase!VirtualFreeEx")]
    private static ulong VirtualFreeEx(ApiCallContext ctx)
    {
        var status = TargetMemory(ctx, ctx.Arg(0), 8, out var memory);
        return status == NtStatus.Success ? VirtualFreeCore(ctx, 1, memory) : BoolResult(ctx, false, Win32FromStatus(status));
    }

    private static ulong VirtualFreeCore(ApiCallContext ctx, int offset, VirtualMemoryManager memory)
    {
        var status = memory.TryFree(ctx.Arg(offset), ctx.Arg(offset + 1), (FreeType)ctx.Arg32(offset + 2));
        return BoolResult(ctx, NtStatus.IsSuccess(status), Win32FromStatus(status));
    }

    [VirtualApi("kernel32.dll", "VirtualProtect", ApiCategory.Memory, "kernelbase!VirtualProtect")]
    private static ulong VirtualProtect(ApiCallContext ctx) => VirtualProtectCore(ctx, 0, ctx.Memory);

    [VirtualApi("kernel32.dll", "VirtualProtectEx", ApiCategory.Memory, "kernelbase!VirtualProtectEx")]
    private static ulong VirtualProtectEx(ApiCallContext ctx)
    {
        var status = TargetMemory(ctx, ctx.Arg(0), 8, out var memory);
        return status == NtStatus.Success ? VirtualProtectCore(ctx, 1, memory) : BoolResult(ctx, false, Win32FromStatus(status));
    }

    private static ulong VirtualProtectCore(ApiCallContext ctx, int offset, VirtualMemoryManager memory)
    {
        if (ctx.Arg(offset + 3) == 0 || !ctx.Memory.IsWritable(ctx.Arg(offset + 3), 4))
            return BoolResult(ctx, false, Win32Error.InvalidParameter);
        var status = memory.TryProtect(ctx.Arg(offset), ctx.Arg(offset + 1), (MemoryProtection)ctx.Arg32(offset + 2), out var old);
        if (NtStatus.IsSuccess(status) && ctx.Arg(offset + 3) != 0) ctx.Memory.Write(ctx.Arg(offset + 3), (uint)old);
        return BoolResult(ctx, NtStatus.IsSuccess(status), Win32FromStatus(status));
    }

    [VirtualApi("kernel32.dll", "VirtualQuery", ApiCategory.Memory, "kernelbase!VirtualQuery")]
    private static ulong VirtualQuery(ApiCallContext ctx) => VirtualQueryCore(ctx, 0, ctx.Memory);

    [VirtualApi("kernel32.dll", "VirtualQueryEx", ApiCategory.Memory, "kernelbase!VirtualQueryEx")]
    private static ulong VirtualQueryEx(ApiCallContext ctx)
    {
        var status = TargetMemory(ctx, ctx.Arg(0), 0x400, out var memory);
        return status == NtStatus.Success ? VirtualQueryCore(ctx, 1, memory) : BoolResult(ctx, false, Win32FromStatus(status));
    }

    private static ulong VirtualQueryCore(ApiCallContext ctx, int offset, VirtualMemoryManager memory)
    {
        var query = memory.Query(ctx.Arg(offset));
        if (query is null)
        {
            ctx.SetLastError(487);
            return 0;
        }
        var infoPtr = ctx.Arg(offset + 1);
        const ulong size = 48;
        if (infoPtr == 0 || ctx.Arg(offset + 2) < size || !ctx.Memory.IsWritable(infoPtr, size))
        {
            ctx.SetLastError(Win32Error.InvalidParameter);
            return 0;
        }
        ctx.Memory.WriteBytes(infoPtr, new byte[48]);
        ctx.Memory.Write(infoPtr, query.BaseAddress);
        ctx.Memory.Write(infoPtr + 8, query.AllocationBase);
        ctx.Memory.Write(infoPtr + 16, (uint)query.AllocationProtection);
        ctx.Memory.Write(infoPtr + 24, query.RegionSize);
        ctx.Memory.Write(infoPtr + 32, (uint)query.State);
        ctx.Memory.Write(infoPtr + 36, (uint)query.Protection);
        ctx.Memory.Write(infoPtr + 40, (uint)query.Type);
        return size;
    }

    [VirtualApi("kernel32.dll", "VirtualLock", ApiCategory.Memory, "kernelbase!VirtualLock")]
    private static ulong VirtualLock(ApiCallContext ctx) =>
        BoolResult(ctx, HostLock((IntPtr)(long)ctx.Arg(0), (nuint)ctx.Arg(1)), System.Runtime.InteropServices.Marshal.GetLastWin32Error());

    [VirtualApi("kernel32.dll", "VirtualUnlock", ApiCategory.Memory, "kernelbase!VirtualUnlock")]
    private static ulong VirtualUnlock(ApiCallContext ctx) =>
        BoolResult(ctx, HostUnlock((IntPtr)(long)ctx.Arg(0), (nuint)ctx.Arg(1)), System.Runtime.InteropServices.Marshal.GetLastWin32Error());

    [VirtualApi("kernel32.dll", "GetLargePageMinimum", ApiCategory.Memory, "kernelbase!GetLargePageMinimum")]
    private static ulong GetLargePageMinimum(ApiCallContext ctx) => (ulong)HostLargePageMinimum();

    private static ulong LockNt(ApiCallContext ctx, bool locked)
    {
        var status = TargetMemory(ctx, ctx.Arg(0), 8, out _);
        if (status != NtStatus.Success) return status;
        if (ctx.Arg(1) == 0 || ctx.Arg(2) == 0 || ctx.Arg32(3) != 1) return NtStatus.InvalidParameter;
        if (!ctx.Memory.IsWritable(ctx.Arg(1), 8) || !ctx.Memory.IsWritable(ctx.Arg(2), 8)) return NtStatus.AccessViolation;
        var address = ctx.Read<ulong>(ctx.Arg(1));
        var size = ctx.Read<ulong>(ctx.Arg(2));
        if (size == 0 || size > ulong.MaxValue - VirtualMemoryManager.PageSize ||
            address > ulong.MaxValue - size - VirtualMemoryManager.PageSize) return NtStatus.InvalidParameter;
        var start = address & ~(VirtualMemoryManager.PageSize - 1);
        var length = VirtualMemoryManager.PageAlign(address + size) - start;
        var ok = locked ? HostLock((IntPtr)(long)start, (nuint)length) : HostUnlock((IntPtr)(long)start, (nuint)length);
        if (!ok) return NtStatus.FromWin32(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
        ctx.Write(ctx.Arg(1), start);
        ctx.Write(ctx.Arg(2), length);
        return NtStatus.Success;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "VirtualLock", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool HostLock(IntPtr address, nuint size);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "VirtualUnlock", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool HostUnlock(IntPtr address, nuint size);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "GetLargePageMinimum")]
    private static extern nuint HostLargePageMinimum();

    [System.Runtime.InteropServices.DllImport("ntdll.dll", EntryPoint = "NtQueryVirtualMemory")]
    private static extern uint HostQueryMemory(IntPtr process, IntPtr address, uint infoClass, IntPtr buffer, nuint length, IntPtr returned);

    // --- CRT-style memory helpers --------------------------------------------------
    [VirtualApi("ntdll.dll", "RtlZeroMemory", ApiCategory.Memory,
        "kernel32!RtlZeroMemory", "ntdll!RtlSecureZeroMemory", "kernel32!RtlSecureZeroMemory")]
    private static ulong RtlZeroMemory(ApiCallContext ctx)
    {
        ZeroMemory(ctx, ctx.Arg(0), ctx.Arg(1));
        return ctx.Arg(0);
    }

    [VirtualApi("ntdll.dll", "RtlFillMemory", ApiCategory.Memory, "kernel32!RtlFillMemory")]
    private static ulong RtlFillMemory(ApiCallContext ctx)
    {
        FillMemory(ctx, ctx.Arg(0), ctx.Arg(1), (byte)ctx.Arg32(2));
        return NtStatus.Success;
    }

    [VirtualApi("ntdll.dll", "RtlCopyMemory", ApiCategory.Memory, "ntdll!memcpy", "kernel32!RtlCopyMemory")]
    private static ulong RtlCopyMemory(ApiCallContext ctx)
    {
        CopyMemory(ctx, ctx.Arg(0), ctx.Arg(1), ctx.Arg(2), move: false);
        return ctx.Arg(0);
    }

    [VirtualApi("ntdll.dll", "RtlMoveMemory", ApiCategory.Memory, "ntdll!memmove", "kernel32!RtlMoveMemory")]
    private static ulong RtlMoveMemory(ApiCallContext ctx)
    {
        CopyMemory(ctx, ctx.Arg(0), ctx.Arg(1), ctx.Arg(2), move: true);
        return ctx.Arg(0);
    }

    [VirtualApi("ntdll.dll", "memset", ApiCategory.Memory)]
    private static ulong Memset(ApiCallContext ctx)
    {
        FillMemory(ctx, ctx.Arg(0), ctx.Arg(2), (byte)ctx.Arg32(1));
        return ctx.Arg(0);
    }

    [VirtualApi("ntdll.dll", "RtlCompareMemory", ApiCategory.Memory)]
    private static ulong RtlCompareMemory(ApiCallContext ctx)
    {
        var length = ctx.Arg(2);
        CheckRange(ctx.Arg(0), length);
        CheckRange(ctx.Arg(1), length);
        Span<byte> a = stackalloc byte[4096];
        Span<byte> b = stackalloc byte[4096];
        for (ulong done = 0; done < length;)
        {
            var count = (int)Math.Min((ulong)a.Length, length - done);
            if (!ctx.Memory.TryRead(ctx.Arg(0) + done, a[..count]) || !ctx.Memory.TryRead(ctx.Arg(1) + done, b[..count]))
                throw new NtMemoryException(NtStatus.AccessViolation, "unreadable memory in RtlCompareMemory");
            for (var i = 0; i < count; i++)
                if (a[i] != b[i]) return done + (ulong)i;
            done += (ulong)count;
        }
        return length;
    }

    private static void ZeroMemory(ApiCallContext ctx, ulong address, ulong length)
    {
        FillMemory(ctx, address, length, 0);
    }

    private static void FillMemory(ApiCallContext ctx, ulong address, ulong length, byte value)
    {
        CheckRange(address, length);
        var remaining = length;
        Span<byte> chunk = stackalloc byte[4096];
        chunk.Fill(value);
        while (remaining > 0)
        {
            var count = (int)Math.Min(remaining, (ulong)chunk.Length);
            ctx.Memory.WriteBytes(address, chunk[..count]);
            address += (ulong)count;
            remaining -= (ulong)count;
        }
    }

    private static void CopyMemory(ApiCallContext ctx, ulong destination, ulong source, ulong length, bool move)
    {
        CheckRange(destination, length);
        CheckRange(source, length);
        var backward = move && destination > source && destination - source < length;
        Span<byte> chunk = stackalloc byte[4096];
        for (ulong copied = 0; copied < length;)
        {
            var count = (int)Math.Min(length - copied, (ulong)chunk.Length);
            var offset = backward ? length - copied - (ulong)count : copied;
            if (!ctx.Memory.TryRead(source + offset, chunk[..count]))
                throw new Memory.NtMemoryException(NtStatus.AccessViolation, $"unreadable source 0x{source:X}");
            ctx.Memory.WriteBytes(destination + offset, chunk[..count]);
            copied += (ulong)count;
        }
    }

    private static void CheckRange(ulong address, ulong length)
    {
        if (length != 0 && (address == 0 || address > long.MaxValue || length > (ulong)long.MaxValue - address))
            throw new NtMemoryException(NtStatus.AccessViolation, "memory range overflow");
    }
}
