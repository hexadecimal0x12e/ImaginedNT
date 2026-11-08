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
        if (sizePtr == 0) return NtStatus.InvalidParameter;
        var requestedBase = basePtr == 0 ? 0UL : ctx.Read<ulong>(basePtr);
        var size = ctx.Read<ulong>(sizePtr);
        var allocationType = (AllocationType)ctx.Arg32(4);
        var protection = (MemoryProtection)ctx.Arg32(5);
        if (protection == 0) protection = MemoryProtection.ReadWrite;
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
        var status = ctx.Process.Memory.TryFree(address, size, (FreeType)ctx.Arg32(3));
        if (NtStatus.IsSuccess(status))
        {
            ctx.Memory.Write(basePtr, address);
            ctx.Memory.Write(sizePtr, 0UL);
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
            ctx.Memory.Write(sizePtr, VirtualMemoryManager.PageAlign(size));
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
        if (infoClass == 0)
        {
            const int size = 48;
            if (infoLength < size) return NtStatus.InfoLengthMismatch;
            if (infoPtr != 0)
            {
                ctx.Memory.Write(infoPtr, query.BaseAddress);
                ctx.Memory.Write(infoPtr + 8, query.AllocationBase);
                ctx.Memory.Write(infoPtr + 16, (uint)query.Protection);
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
        {
            // MEMORY_SECTION_NAME: UNICODE_STRING (16 bytes on x64) followed by the name.
            var name = query.Label ?? string.Empty;
            var bytes = System.Text.Encoding.Unicode.GetBytes(name);
            var required = 16 + bytes.Length + 2;
            if (infoLength < (ulong)required) return NtStatus.BufferTooSmall;
            if (infoPtr != 0)
            {
                ctx.Memory.Write(infoPtr, (ushort)(bytes.Length + 2));
                ctx.Memory.Write(infoPtr + 2, (ushort)(bytes.Length + 2));
                ctx.Memory.Write(infoPtr + 4, 0u);
                ctx.Memory.Write(infoPtr + 8, infoPtr + 16);
                ctx.Memory.WriteBytes(infoPtr + 16, bytes);
                ctx.Memory.Write(infoPtr + 16 + (ulong)bytes.Length, (ushort)0);
            }
            if (returnLengthPtr != 0) ctx.Memory.Write(returnLengthPtr, (ulong)required);
            return NtStatus.Success;
        }
        return NtStatus.InvalidInfoClass;
    }

    [VirtualApi("ntdll.dll", "NtLockVirtualMemory", ApiCategory.Memory)]
    private static ulong NtLockVirtualMemory(ApiCallContext ctx) => NtStatus.Success;

    [VirtualApi("ntdll.dll", "NtUnlockVirtualMemory", ApiCategory.Memory)]
    private static ulong NtUnlockVirtualMemory(ApiCallContext ctx) => NtStatus.Success;

    // --- kernel32 wrappers ---------------------------------------------------------
    [VirtualApi("kernel32.dll", "VirtualAlloc", ApiCategory.Memory)]
    private static ulong VirtualAlloc(ApiCallContext ctx)
    {
        var requestedBase = ctx.Arg(0);
        var size = ctx.Arg(1);
        var type = (AllocationType)ctx.Arg32(2);
        var protection = (MemoryProtection)ctx.Arg32(3);
        var status = ctx.Process.Memory.TryAllocate(requestedBase, size, protection, type,
            out var actualBase, out _, "VirtualAlloc");
        if (!NtStatus.IsSuccess(status))
        {
            ctx.SetLastError(Win32FromStatus(status));
            return 0;
        }
        return actualBase;
    }

    [VirtualApi("kernel32.dll", "VirtualFree", ApiCategory.Memory)]
    private static ulong VirtualFree(ApiCallContext ctx)
    {
        var status = ctx.Process.Memory.TryFree(ctx.Arg(0), ctx.Arg(1), (FreeType)ctx.Arg32(2));
        return BoolResult(ctx, NtStatus.IsSuccess(status), Win32FromStatus(status));
    }

    [VirtualApi("kernel32.dll", "VirtualProtect", ApiCategory.Memory)]
    private static ulong VirtualProtect(ApiCallContext ctx)
    {
        var status = ctx.Process.Memory.TryProtect(ctx.Arg(0), ctx.Arg(1), (MemoryProtection)ctx.Arg32(2), out var old);
        if (NtStatus.IsSuccess(status) && ctx.Arg(3) != 0) ctx.Memory.Write(ctx.Arg(3), (uint)old);
        return BoolResult(ctx, NtStatus.IsSuccess(status), Win32FromStatus(status));
    }

    [VirtualApi("kernel32.dll", "VirtualQuery", ApiCategory.Memory)]
    private static ulong VirtualQuery(ApiCallContext ctx)
    {
        var query = ctx.Process.Memory.Query(ctx.Arg(0));
        if (query is null)
        {
            ctx.SetLastError(487);
            return 0;
        }
        var infoPtr = ctx.Arg(1);
        const ulong size = 48;
        if (infoPtr == 0 || ctx.Arg(2) < size) return 0;
        ctx.Memory.Write(infoPtr, query.BaseAddress);
        ctx.Memory.Write(infoPtr + 8, query.AllocationBase);
        ctx.Memory.Write(infoPtr + 16, (uint)query.Protection);
        ctx.Memory.Write(infoPtr + 24, query.RegionSize);
        ctx.Memory.Write(infoPtr + 32, (uint)query.State);
        ctx.Memory.Write(infoPtr + 36, (uint)query.Protection);
        ctx.Memory.Write(infoPtr + 40, (uint)query.Type);
        return size;
    }

    [VirtualApi("kernel32.dll", "VirtualLock", ApiCategory.Memory)]
    private static ulong VirtualLock(ApiCallContext ctx) => 1;

    [VirtualApi("kernel32.dll", "VirtualUnlock", ApiCategory.Memory)]
    private static ulong VirtualUnlock(ApiCallContext ctx) => 1;

    [VirtualApi("kernel32.dll", "GetLargePageMinimum", ApiCategory.Memory)]
    private static ulong GetLargePageMinimum(ApiCallContext ctx) => 0x20_0000;

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
