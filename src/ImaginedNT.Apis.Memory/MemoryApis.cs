using static ImaginedNT.Apis.ApiHelpers;
using ImaginedNT.Core.Memory;
using ImaginedNT.Core.Nt;

namespace ImaginedNT.Core.Runtime;

public static class MemoryApis
{
    private static bool IsCurrentProcessHandle(ApiCallContext ctx, ulong handle)
    {
        if (handle == 0 || handle == CurrentProcessPseudoHandle) return true;
        var obj = GetObject(ctx, handle);
        return obj is ProcessObject p && ReferenceEquals(p.Process, ctx.Process);
    }

    [VirtualApi("ntdll.dll", "NtAllocateVirtualMemory", ApiCategory.Memory)]
    private static ulong NtAllocateVirtualMemory(ApiCallContext ctx)
    {
        if (!IsCurrentProcessHandle(ctx, ctx.Arg(0))) return NtStatus.InvalidHandle;
        var basePtr = ctx.Arg(1);
        var sizePtr = ctx.Arg(3);
        if (basePtr == 0 || sizePtr == 0 || ctx.Arg(2) != 0) return NtStatus.InvalidParameter;
        var requestedBase = ctx.Read<ulong>(basePtr);
        var size = ctx.Read<ulong>(sizePtr);
        var allocationType = (AllocationType)ctx.Arg32(4);
        var protection = (MemoryProtection)ctx.Arg32(5);
        var status = ctx.Process.Memory.TryAllocate(requestedBase, size, protection, allocationType,
            out var actualBase, out var actualSize, "NtAllocateVirtualMemory");
        if (NtStatus.IsSuccess(status))
        {
            if (basePtr != 0) ctx.Memory.Write(basePtr, actualBase);
            ctx.Memory.Write(sizePtr, actualSize);
        }
        return status;
    }

    [VirtualApi("ntdll.dll", "NtFreeVirtualMemory", ApiCategory.Memory)]
    private static ulong NtFreeVirtualMemory(ApiCallContext ctx)
    {
        if (!IsCurrentProcessHandle(ctx, ctx.Arg(0))) return NtStatus.InvalidHandle;
        var basePtr = ctx.Arg(1);
        var sizePtr = ctx.Arg(2);
        if (basePtr == 0 || sizePtr == 0) return NtStatus.InvalidParameter;
        var address = ctx.Read<ulong>(basePtr);
        var size = ctx.Read<ulong>(sizePtr);
        var freeType = (FreeType)ctx.Arg32(3);
        var allocationSize = ctx.Memory.FindRegion(address)?.AllocationSize ?? 0;
        var status = ctx.Process.Memory.TryFree(address, size, freeType);
        if (NtStatus.IsSuccess(status))
        {
            var start = address & ~(VirtualMemoryManager.PageSize - 1);
            ctx.Memory.Write(basePtr, start);
            ctx.Memory.Write(sizePtr, size == 0 ? allocationSize : VirtualMemoryManager.PageAlign(address + size) - start);
        }
        return status;
    }

    [VirtualApi("ntdll.dll", "NtProtectVirtualMemory", ApiCategory.Memory)]
    private static ulong NtProtectVirtualMemory(ApiCallContext ctx)
    {
        if (!IsCurrentProcessHandle(ctx, ctx.Arg(0))) return NtStatus.InvalidHandle;
        var basePtr = ctx.Arg(1);
        var sizePtr = ctx.Arg(2);
        var oldPtr = ctx.Arg(4);
        if (basePtr == 0 || sizePtr == 0) return NtStatus.InvalidParameter;
        var address = ctx.Read<ulong>(basePtr);
        var size = ctx.Read<ulong>(sizePtr);
        var status = ctx.Process.Memory.TryProtect(address, size, (MemoryProtection)ctx.Arg32(3), out var old);
        if (NtStatus.IsSuccess(status))
        {
            if (oldPtr != 0) ctx.Memory.Write(oldPtr, (uint)old);
            var start = address & ~(VirtualMemoryManager.PageSize - 1);
            ctx.Memory.Write(basePtr, start);
            ctx.Memory.Write(sizePtr, VirtualMemoryManager.PageAlign(address + size) - start);
        }
        return status;
    }

    [VirtualApi("ntdll.dll", "NtQueryVirtualMemory", ApiCategory.Memory)]
    private static ulong NtQueryVirtualMemory(ApiCallContext ctx)
    {
        if (!IsCurrentProcessHandle(ctx, ctx.Arg(0))) return NtStatus.InvalidHandle;
        var address = ctx.Arg(1);
        var infoClass = ctx.Arg32(2);
        var infoPtr = ctx.Arg(3);
        var infoLength = ctx.Arg(4);
        var returnLengthPtr = ctx.Arg(5);
        var query = ctx.Process.Memory.Query(address);
        if (query is null) return NtStatus.InvalidAddress;
        if (infoPtr == 0) return NtStatus.AccessViolation;
        if (infoClass == 0)
        {
            const int size = 48;
            if (infoLength < size) return NtStatus.InfoLengthMismatch;
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
            if (returnLengthPtr != 0) ctx.Memory.Write(returnLengthPtr, (ulong)size);
            return NtStatus.Success;
        }
        if (infoClass == 2)
            return HostQueryMemory(new IntPtr(-1), (IntPtr)(long)address, infoClass,
                (IntPtr)(long)infoPtr, (nuint)infoLength, (IntPtr)(long)returnLengthPtr);
        return NtStatus.InvalidInfoClass;
    }

    [VirtualApi("ntdll.dll", "NtLockVirtualMemory", ApiCategory.Memory)]
    private static ulong NtLockVirtualMemory(ApiCallContext ctx) => LockNt(ctx, true);

    [VirtualApi("ntdll.dll", "NtUnlockVirtualMemory", ApiCategory.Memory)]
    private static ulong NtUnlockVirtualMemory(ApiCallContext ctx) => LockNt(ctx, false);

    // --- kernel32 wrappers ---------------------------------------------------------
    [VirtualApi("kernel32.dll", "VirtualAlloc", ApiCategory.Memory, "kernelbase!VirtualAlloc")]
    private static ulong VirtualAlloc(ApiCallContext ctx) => VirtualAllocCore(ctx, 0);

    [VirtualApi("kernel32.dll", "VirtualAllocEx", ApiCategory.Memory, "kernelbase!VirtualAllocEx")]
    private static ulong VirtualAllocEx(ApiCallContext ctx)
    {
        if (!IsCurrentProcessHandle(ctx, ctx.Arg(0)))
        {
            ctx.SetLastError(Win32Error.InvalidHandle);
            return 0;
        }
        return VirtualAllocCore(ctx, 1);
    }

    private static ulong VirtualAllocCore(ApiCallContext ctx, int offset)
    {
        var requestedBase = ctx.Arg(offset);
        var size = ctx.Arg(offset + 1);
        var type = (AllocationType)ctx.Arg32(offset + 2);
        var protection = (MemoryProtection)ctx.Arg32(offset + 3);
        var status = ctx.Process.Memory.TryAllocate(requestedBase, size, protection, type,
            out var actualBase, out _, "VirtualAlloc");
        if (!NtStatus.IsSuccess(status))
        {
            ctx.SetLastError(Win32FromStatus(status));
            return 0;
        }
        return actualBase;
    }

    [VirtualApi("kernel32.dll", "VirtualFree", ApiCategory.Memory, "kernelbase!VirtualFree")]
    private static ulong VirtualFree(ApiCallContext ctx) => VirtualFreeCore(ctx, 0);

    [VirtualApi("kernel32.dll", "VirtualFreeEx", ApiCategory.Memory, "kernelbase!VirtualFreeEx")]
    private static ulong VirtualFreeEx(ApiCallContext ctx)
    {
        if (!IsCurrentProcessHandle(ctx, ctx.Arg(0)))
        {
            ctx.SetLastError(Win32Error.InvalidHandle);
            return 0;
        }
        return VirtualFreeCore(ctx, 1);
    }

    private static ulong VirtualFreeCore(ApiCallContext ctx, int offset)
    {
        var status = ctx.Process.Memory.TryFree(ctx.Arg(offset), ctx.Arg(offset + 1), (FreeType)ctx.Arg32(offset + 2));
        return BoolResult(ctx, NtStatus.IsSuccess(status), Win32FromStatus(status));
    }

    [VirtualApi("kernel32.dll", "VirtualProtect", ApiCategory.Memory, "kernelbase!VirtualProtect")]
    private static ulong VirtualProtect(ApiCallContext ctx) => VirtualProtectCore(ctx, 0);

    [VirtualApi("kernel32.dll", "VirtualProtectEx", ApiCategory.Memory, "kernelbase!VirtualProtectEx")]
    private static ulong VirtualProtectEx(ApiCallContext ctx)
    {
        if (!IsCurrentProcessHandle(ctx, ctx.Arg(0)))
        {
            ctx.SetLastError(Win32Error.InvalidHandle);
            return 0;
        }
        return VirtualProtectCore(ctx, 1);
    }

    private static ulong VirtualProtectCore(ApiCallContext ctx, int offset)
    {
        var status = ctx.Process.Memory.TryProtect(ctx.Arg(offset), ctx.Arg(offset + 1), (MemoryProtection)ctx.Arg32(offset + 2), out var old);
        if (NtStatus.IsSuccess(status) && ctx.Arg(offset + 3) != 0) ctx.Memory.Write(ctx.Arg(offset + 3), (uint)old);
        return BoolResult(ctx, NtStatus.IsSuccess(status), Win32FromStatus(status));
    }

    [VirtualApi("kernel32.dll", "VirtualQuery", ApiCategory.Memory, "kernelbase!VirtualQuery")]
    private static ulong VirtualQuery(ApiCallContext ctx) => VirtualQueryCore(ctx, 0);

    [VirtualApi("kernel32.dll", "VirtualQueryEx", ApiCategory.Memory, "kernelbase!VirtualQueryEx")]
    private static ulong VirtualQueryEx(ApiCallContext ctx)
    {
        if (!IsCurrentProcessHandle(ctx, ctx.Arg(0)))
        {
            ctx.SetLastError(Win32Error.InvalidHandle);
            return 0;
        }
        return VirtualQueryCore(ctx, 1);
    }

    private static ulong VirtualQueryCore(ApiCallContext ctx, int offset)
    {
        var query = ctx.Process.Memory.Query(ctx.Arg(offset));
        if (query is null)
        {
            ctx.SetLastError(487);
            return 0;
        }
        var infoPtr = ctx.Arg(offset + 1);
        const ulong size = 48;
        if (infoPtr == 0 || ctx.Arg(offset + 2) < size)
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
        if (!IsCurrentProcessHandle(ctx, ctx.Arg(0))) return NtStatus.InvalidHandle;
        if (ctx.Arg(1) == 0 || ctx.Arg(2) == 0 || ctx.Arg32(3) != 1) return NtStatus.InvalidParameter;
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
    [VirtualApi("ntdll.dll", "RtlZeroMemory", ApiCategory.Memory)]
    private static ulong RtlZeroMemory(ApiCallContext ctx)
    {
        ZeroMemory(ctx, ctx.Arg(0), ctx.Arg(1));
        return NtStatus.Success;
    }

    [VirtualApi("ntdll.dll", "RtlFillMemory", ApiCategory.Memory)]
    private static ulong RtlFillMemory(ApiCallContext ctx)
    {
        FillMemory(ctx, ctx.Arg(0), ctx.Arg(1), (byte)ctx.Arg32(2));
        return NtStatus.Success;
    }

    [VirtualApi("ntdll.dll", "RtlCopyMemory", ApiCategory.Memory, "ntdll!memcpy")]
    private static ulong RtlCopyMemory(ApiCallContext ctx)
    {
        CopyMemory(ctx, ctx.Arg(0), ctx.Arg(1), ctx.Arg(2), move: false);
        return NtStatus.Success;
    }

    [VirtualApi("ntdll.dll", "RtlMoveMemory", ApiCategory.Memory)]
    private static ulong RtlMoveMemory(ApiCallContext ctx)
    {
        CopyMemory(ctx, ctx.Arg(0), ctx.Arg(1), ctx.Arg(2), move: true);
        return NtStatus.Success;
    }

    [VirtualApi("ntdll.dll", "RtlCompareMemory", ApiCategory.Memory)]
    private static ulong RtlCompareMemory(ApiCallContext ctx)
    {
        var length = ctx.Arg(2);
        var a = ctx.Memory.ReadBytes(ctx.Arg(0), (int)Math.Min(length, 1 << 20));
        var b = ctx.Memory.ReadBytes(ctx.Arg(1), (int)Math.Min(length, 1 << 20));
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i]) return (ulong)i;
        }
        return (ulong)a.Length;
    }

    private static void ZeroMemory(ApiCallContext ctx, ulong address, ulong length)
    {
        var remaining = (int)Math.Min(length, 1 << 30);
        var chunk = new byte[Math.Min(remaining, 0x10000)];
        while (remaining > 0)
        {
            var count = Math.Min(remaining, chunk.Length);
            ctx.Memory.WriteBytes(address, chunk.AsSpan(0, count));
            address += (ulong)count;
            remaining -= count;
        }
    }

    private static void FillMemory(ApiCallContext ctx, ulong address, ulong length, byte value)
    {
        var remaining = (int)Math.Min(length, 1 << 30);
        var chunk = new byte[Math.Min(remaining, 0x10000)];
        Array.Fill(chunk, value);
        while (remaining > 0)
        {
            var count = Math.Min(remaining, chunk.Length);
            ctx.Memory.WriteBytes(address, chunk.AsSpan(0, count));
            address += (ulong)count;
            remaining -= count;
        }
    }

    private static void CopyMemory(ApiCallContext ctx, ulong destination, ulong source, ulong length, bool move)
    {
        if (length == 0) return;
        var size = (int)Math.Min(length, 1 << 30);
        if (move && destination > source && destination < source + (ulong)size)
        {
            var remaining = size;
            var buffer = new byte[Math.Min(size, 0x10000)];
            while (remaining > 0)
            {
                var count = Math.Min(remaining, buffer.Length);
                var offset = remaining - count;
                ctx.Memory.TryRead(source + (ulong)offset, buffer.AsSpan(0, count));
                ctx.Memory.WriteBytes(destination + (ulong)offset, buffer.AsSpan(0, count));
                remaining -= count;
            }
            return;
        }

        var chunk = new byte[Math.Min(size, 0x10000)];
        var copied = 0;
        while (copied < size)
        {
            var count = Math.Min(size - copied, chunk.Length);
            if (!ctx.Memory.TryRead(source + (ulong)copied, chunk.AsSpan(0, count)))
                throw new Memory.NtMemoryException(NtStatus.AccessViolation, $"unreadable source 0x{source:X}");
            ctx.Memory.WriteBytes(destination + (ulong)copied, chunk.AsSpan(0, count));
            copied += count;
        }
    }
}
