using System.Collections.Concurrent;
using ImaginedNT.Core.Memory;
using ImaginedNT.Core.Nt;
using static ImaginedNT.Apis.ApiHelpers;

namespace ImaginedNT.Core.Runtime;

public static class HeapApis
{
    [VirtualApi("kernel32.dll", "GetProcessHeap", ApiCategory.Memory, "kernelbase!GetProcessHeap")]
    private static ulong GetProcessHeap(ApiCallContext ctx) => ctx.Process.Heap.GetHandle(ctx.Thread.Id);

    [VirtualApi("kernel32.dll", "HeapCreate", ApiCategory.Memory, "kernelbase!HeapCreate")]
    private static ulong Create(ApiCallContext ctx)
    {
        if (!Flags(ctx, ctx.Arg32(0), 0x40001)) return 0;
        var handle = ctx.Process.CreateHeap(ctx.Arg32(0), ctx.Arg(1), ctx.Arg(2), ctx.Thread.Id);
        if (handle == 0) ctx.SetLastError(Win32Error.NotEnoughMemory);
        return handle;
    }

    [VirtualApi("kernel32.dll", "HeapDestroy", ApiCategory.Memory, "kernelbase!HeapDestroy")]
    private static ulong Destroy(ApiCallContext ctx) => BoolResult(ctx, ctx.Process.DestroyHeap(ctx.Arg(0)), Win32Error.InvalidHandle);

    private static readonly ConcurrentDictionary<nint, byte> HostHeaps = new();

    [VirtualApi("kernel32.dll", "HeapAlloc", ApiCategory.Memory, "kernelbase!HeapAlloc", "ntdll!RtlAllocateHeap")]
    private static ulong Allocate(ApiCallContext ctx)
    {
        if (TryHeap(ctx, out var heap))
        {
            if (!Flags(ctx, ctx.Arg32(1), 0x9)) return 0;
            var address = heap!.Allocate(ctx.Arg(2), (ctx.Arg32(1) & 8) != 0, ctx.Thread.Id);
            if (address == 0) ctx.SetLastError(Win32Error.NotEnoughMemory);
            return address;
        }
        if (!IsHostHeap((nint)(long)ctx.Arg(0))) return 0;
        var host = HostRtlAllocateHeap((nint)(long)ctx.Arg(0), ctx.Arg32(1), (nuint)ctx.Arg(2));
        if (host == 0) ctx.SetLastError(Win32Error.NotEnoughMemory);
        return (ulong)(long)host;
    }

    [VirtualApi("kernel32.dll", "HeapFree", ApiCategory.Memory, "kernelbase!HeapFree", "ntdll!RtlFreeHeap")]
    private static ulong Free(ApiCallContext ctx)
    {
        if (TryHeap(ctx, out var heap))
        {
            if (!Flags(ctx, ctx.Arg32(1), 1)) return 0;
            return BoolResult(ctx, heap!.Free(ctx.Arg(2), ctx.Thread.Id), Win32Error.InvalidParameter);
        }
        if (!IsHostHeap((nint)(long)ctx.Arg(0))) return 0;
        return BoolResult(ctx, HostRtlFreeHeap((nint)(long)ctx.Arg(0), ctx.Arg32(1),
            (nint)(long)ctx.Arg(2)) != 0, Win32Error.InvalidParameter);
    }

    [VirtualApi("kernel32.dll", "HeapReAlloc", ApiCategory.Memory, "kernelbase!HeapReAlloc", "ntdll!RtlReAllocateHeap")]
    private static ulong Reallocate(ApiCallContext ctx)
    {
        if (TryHeap(ctx, out var heap))
        {
            if (!Flags(ctx, ctx.Arg32(1), 0x19)) return 0;
            var address = heap!.Reallocate(ctx.Arg(2), ctx.Arg(3), (ctx.Arg32(1) & 8) != 0,
                (ctx.Arg32(1) & 0x10) != 0, ctx.Thread.Id);
            if (address == 0) ctx.SetLastError(Win32Error.NotEnoughMemory);
            return address;
        }
        if (!IsHostHeap((nint)(long)ctx.Arg(0))) return 0;
        var host = HostRtlReAllocateHeap((nint)(long)ctx.Arg(0), ctx.Arg32(1),
            (nint)(long)ctx.Arg(2), (nuint)ctx.Arg(3));
        if (host == 0) ctx.SetLastError(Win32Error.NotEnoughMemory);
        return (ulong)(long)host;
    }

    [VirtualApi("kernel32.dll", "HeapSize", ApiCategory.Memory, "kernelbase!HeapSize", "ntdll!RtlSizeHeap")]
    private static ulong Size(ApiCallContext ctx)
    {
        if (TryHeap(ctx, out var heap))
        {
            if (!Flags(ctx, ctx.Arg32(1), 1)) return ulong.MaxValue;
            var size = heap!.Size(ctx.Arg(2), ctx.Thread.Id);
            if (size == ulong.MaxValue) ctx.SetLastError(Win32Error.InvalidParameter);
            return size;
        }
        if (!IsHostHeap((nint)(long)ctx.Arg(0))) return ulong.MaxValue;
        var hostSize = HostRtlSizeHeap((nint)(long)ctx.Arg(0), ctx.Arg32(1), (nint)(long)ctx.Arg(2));
        if (hostSize == unchecked((nuint)(-1))) ctx.SetLastError(Win32Error.InvalidParameter);
        return (ulong)hostSize;
    }

    private static bool TryHeap(ApiCallContext ctx, out GuestHeap? heap)
    {
        heap = ctx.Process.FindHeap(ctx.Arg(0));
        return heap is not null;
    }

    /// <summary>
    /// Host system DLLs use their own PEB process heap (or heaps they created) when their
    /// DllMain runs.  Those handles are not in the guest heap list, so fall back to the host
    /// ntdll heap routines instead of failing initialization.
    /// </summary>
    private static bool IsHostHeap(nint handle)
    {
        if (handle == 0) return false;
        if (HostHeaps.ContainsKey(handle)) return true;
        if (!HostHeapValidate(handle, 0, 0)) return false;
        HostHeaps.TryAdd(handle, 0);
        return true;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "HeapValidate")]
    private static extern bool HostHeapValidate(nint heap, uint flags, nint block);

    [System.Runtime.InteropServices.DllImport("ntdll.dll", EntryPoint = "RtlAllocateHeap")]
    private static extern nint HostRtlAllocateHeap(nint heap, uint flags, nuint size);

    [System.Runtime.InteropServices.DllImport("ntdll.dll", EntryPoint = "RtlFreeHeap")]
    private static extern byte HostRtlFreeHeap(nint heap, uint flags, nint block);

    [System.Runtime.InteropServices.DllImport("ntdll.dll", EntryPoint = "RtlReAllocateHeap")]
    private static extern nint HostRtlReAllocateHeap(nint heap, uint flags, nint block, nuint size);

    [System.Runtime.InteropServices.DllImport("ntdll.dll", EntryPoint = "RtlSizeHeap")]
    private static extern nuint HostRtlSizeHeap(nint heap, uint flags, nint block);

    private static bool Flags(ApiCallContext ctx, uint flags, uint allowed)
    {
        if ((flags & ~allowed) == 0) return true;
        ctx.SetLastError((flags & 4) != 0 ? Win32Error.NotSupported : Win32Error.InvalidParameter);
        ctx.Runtime.Trace.Warn($"{ctx.Name}: unsupported heap flags 0x{flags:X}", ctx.Process.Id, ctx.Thread.Id);
        return false;
    }
}

