using System.Runtime.InteropServices;
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
        if (!Flags(ctx, ctx.Arg32(0), 0x40005)) return 0;
        var handle = ctx.Process.CreateHeap(ctx.Arg32(0), ctx.Arg(1), ctx.Arg(2), ctx.Thread.Id);
        if (handle == 0) ctx.SetLastError(Win32Error.NotEnoughMemory);
        return handle;
    }

    [VirtualApi("kernel32.dll", "HeapDestroy", ApiCategory.Memory, "kernelbase!HeapDestroy")]
    private static ulong Destroy(ApiCallContext ctx) => BoolResult(ctx, ctx.Process.DestroyHeap(ctx.Arg(0)), Win32Error.InvalidHandle);

    [VirtualApi("ntdll.dll", "RtlCreateHeap", ApiCategory.Memory)]
    private static ulong RtlCreateHeap(ApiCallContext ctx)
    {
        if (ctx.Arg(1) != 0 || ctx.Arg(4) != 0 || ctx.Arg(5) != 0)
        {
            ctx.SetLastError(Win32Error.NotSupported);
            ctx.Runtime.Trace.Warn("RtlCreateHeap: caller-supplied storage, locks and parameters are unsupported", ctx.Process.Id, ctx.Thread.Id);
            return 0;
        }
        if ((ctx.Arg32(0) & 2) == 0 || !Flags(ctx, ctx.Arg32(0), 0x40007))
            return BoolResult(ctx, false, Win32Error.InvalidParameter);
        if (ctx.Arg(2) > ulong.MaxValue - VirtualMemoryManager.AllocationGranularity ||
            ctx.Arg(3) > ulong.MaxValue - VirtualMemoryManager.AllocationGranularity)
            return BoolResult(ctx, false, Win32Error.InvalidParameter);
        var reserve = ctx.Arg(2) != 0 ? ctx.Arg(2) : ctx.Arg(3) == 0 ? 0x40000UL : VirtualMemoryManager.GranularityAlign(ctx.Arg(3));
        var commit = ctx.Arg(3) == 0 ? VirtualMemoryManager.PageSize : Math.Min(ctx.Arg(3), reserve);
        var handle = ctx.Process.CreateHeap(ctx.Arg32(0) & ~2u, commit, 0, ctx.Thread.Id, reserve);
        if (handle == 0) ctx.SetLastError(Win32Error.NotEnoughMemory);
        return handle;
    }

    [VirtualApi("ntdll.dll", "RtlDestroyHeap", ApiCategory.Memory)]
    private static ulong RtlDestroyHeap(ApiCallContext ctx)
    {
        if (ctx.Process.DestroyHeap(ctx.Arg(0))) return 0;
        ctx.SetLastError(Win32Error.InvalidHandle);
        return ctx.Arg(0);
    }

    [VirtualApi("kernel32.dll", "GetProcessHeaps", ApiCategory.Memory, "kernelbase!GetProcessHeaps")]
    private static ulong GetProcessHeaps(ApiCallContext ctx)
    {
        var heaps = ctx.Process.GetHeaps().Select(h => h.GetHandle(ctx.Thread.Id)).Where(h => h != 0).ToArray();
        if (heaps.Length == 0) return BoolResult(ctx, false, Win32Error.NotEnoughMemory);
        if (ctx.Arg32(0) >= heaps.Length && heaps.Length != 0)
        {
            if (ctx.Arg(1) == 0 || !ctx.Memory.IsWritable(ctx.Arg(1), (ulong)heaps.Length * 8))
                return BoolResult(ctx, false, Win32Error.InvalidParameter);
            for (var i = 0; i < heaps.Length; i++) ctx.Write(ctx.Arg(1) + (ulong)i * 8, heaps[i]);
        }
        return (ulong)heaps.Length;
    }

    [VirtualApi("kernel32.dll", "HeapAlloc", ApiCategory.Memory, "kernelbase!HeapAlloc", "ntdll!RtlAllocateHeap")]
    private static ulong Allocate(ApiCallContext ctx)
    {
        if (!Flags(ctx, ctx.Arg32(1), 0xD)) return 0;
        if (TryHeap(ctx, out var heap))
        {
            var address = heap!.Allocate(ctx.Arg(2), (ctx.Arg32(1) & 8) != 0, ctx.Thread.Id);
            return address == 0 ? AllocationFailure(ctx) : address;
        }
        if (!IsHostHeap(ctx)) return 0;
        lock (ctx.Runtime.Memory.Gate)
        {
            if (!CheckHostAllocation(ctx, ctx.Arg(2))) return 0;
            var host = HostRtlAllocateHeap((nint)(long)ctx.Arg(0), ctx.Arg32(1) & ~4u, (nuint)ctx.Arg(2));
            if (host != 0) ctx.Runtime.Memory.TrackHostAllocation(ctx.Process.Id, ctx.Arg(0), 0, (ulong)host, ChargeSize(ctx.Arg(2)));
            return host == 0 ? AllocationFailure(ctx) : (ulong)(long)host;
        }
    }

    [VirtualApi("kernel32.dll", "HeapFree", ApiCategory.Memory, "kernelbase!HeapFree", "ntdll!RtlFreeHeap")]
    private static ulong Free(ApiCallContext ctx)
    {
        if (!Flags(ctx, ctx.Arg32(1), 1)) return 0;
        if (TryHeap(ctx, out var heap))
        {
            return BoolResult(ctx, heap!.Free(ctx.Arg(2), ctx.Thread.Id), Win32Error.InvalidParameter);
        }
        if (!IsHostHeap(ctx)) return 0;
        lock (ctx.Runtime.Memory.Gate)
        {
            if (ctx.Arg(2) == 0) return 1;
            if (!HostBlock(ctx)) return 0;
            var freed = HostRtlFreeHeap((nint)(long)ctx.Arg(0), ctx.Arg32(1), (nint)(long)ctx.Arg(2)) != 0;
            if (freed) ctx.Runtime.Memory.ForgetHostAllocation(ctx.Arg(0), ctx.Arg(2));
            return BoolResult(ctx, freed, Win32Error.InvalidParameter);
        }
    }

    [VirtualApi("kernel32.dll", "HeapReAlloc", ApiCategory.Memory, "kernelbase!HeapReAlloc", "ntdll!RtlReAllocateHeap")]
    private static ulong Reallocate(ApiCallContext ctx)
    {
        if (!Flags(ctx, ctx.Arg32(1), 0x1D)) return 0;
        if (TryHeap(ctx, out var heap))
        {
            if (!heap!.Contains(ctx.Arg(2))) return BoolResult(ctx, false, Win32Error.InvalidParameter);
            var address = heap!.Reallocate(ctx.Arg(2), ctx.Arg(3), (ctx.Arg32(1) & 8) != 0,
                (ctx.Arg32(1) & 0x10) != 0, ctx.Thread.Id);
            return address == 0 ? AllocationFailure(ctx) : address;
        }
        if (!IsHostHeap(ctx)) return 0;
        lock (ctx.Runtime.Memory.Gate)
        {
            if (!HostBlock(ctx)) return 0;
            var oldSize = ctx.Runtime.Memory.GetHostBlockSize(ctx.Arg(0), ctx.Arg(2));
            if (!CheckHostAllocation(ctx, ctx.Arg(3), oldSize)) return 0;
            var host = HostRtlReAllocateHeap((nint)(long)ctx.Arg(0), ctx.Arg32(1) & ~4u,
                (nint)(long)ctx.Arg(2), (nuint)ctx.Arg(3));
            if (host != 0) ctx.Runtime.Memory.TrackHostAllocation(ctx.Process.Id, ctx.Arg(0), ctx.Arg(2), (ulong)host, ChargeSize(ctx.Arg(3)));
            return host == 0 ? AllocationFailure(ctx) : (ulong)(long)host;
        }
    }

    [VirtualApi("kernel32.dll", "HeapSize", ApiCategory.Memory, "kernelbase!HeapSize", "ntdll!RtlSizeHeap")]
    private static ulong Size(ApiCallContext ctx)
    {
        if (!Flags(ctx, ctx.Arg32(1), 1)) return ulong.MaxValue;
        if (TryHeap(ctx, out var heap))
        {
            var size = heap!.Size(ctx.Arg(2), ctx.Thread.Id);
            if (size == ulong.MaxValue) ctx.SetLastError(Win32Error.InvalidParameter);
            return size;
        }
        if (!IsHostHeap(ctx)) return ulong.MaxValue;
        lock (ctx.Runtime.Memory.Gate)
        {
            if (!HostBlock(ctx)) return ulong.MaxValue;
            return (ulong)HostRtlSizeHeap((nint)(long)ctx.Arg(0), ctx.Arg32(1), (nint)(long)ctx.Arg(2));
        }
    }

    [VirtualApi("kernel32.dll", "HeapValidate", ApiCategory.Memory, "kernelbase!HeapValidate", "ntdll!RtlValidateHeap")]
    private static ulong Validate(ApiCallContext ctx)
    {
        if (!Flags(ctx, ctx.Arg32(1), 1)) return 0;
        if (TryHeap(ctx, out var heap))
        {
            lock (ctx.Runtime.Memory.Gate)
                return BoolResult(ctx, (ctx.Arg(2) == 0 || heap!.Contains(ctx.Arg(2))) &&
                    HostHeapValidate((nint)ctx.Arg(0), ctx.Arg32(1), (nint)ctx.Arg(2)), Win32Error.InvalidParameter);
        }
        if (!IsHostHeap(ctx)) return 0;
        lock (ctx.Runtime.Memory.Gate)
            return ctx.Arg(2) == 0 ? BoolResult(ctx, HostHeapValidate((nint)ctx.Arg(0), ctx.Arg32(1), 0), Win32Error.InvalidParameter)
                : HostBlock(ctx) ? 1UL : 0UL;
    }

    [VirtualApi("kernel32.dll", "HeapCompact", ApiCategory.Memory, "kernelbase!HeapCompact", "ntdll!RtlCompactHeap")]
    private static ulong Compact(ApiCallContext ctx)
    {
        if (!Flags(ctx, ctx.Arg32(1), 1) || !TryHeap(ctx, out _) && !IsHostHeap(ctx)) return 0;
        lock (ctx.Runtime.Memory.Gate) return (ulong)HostHeapCompact((nint)ctx.Arg(0), ctx.Arg32(1));
    }

    [VirtualApi("kernel32.dll", "HeapWalk", ApiCategory.Memory, "kernelbase!HeapWalk")]
    private static ulong Walk(ApiCallContext ctx)
    {
        if (!ctx.Memory.IsWritable(ctx.Arg(1), 40)) return BoolResult(ctx, false, Win32Error.InvalidParameter);
        if (!TryHeap(ctx, out _) && !IsHostHeap(ctx)) return 0;
        lock (ctx.Runtime.Memory.Gate)
        {
            var result = HostHeapWalk((nint)ctx.Arg(0), (nint)ctx.Arg(1));
            return BoolResult(ctx, result, Marshal.GetLastPInvokeError());
        }
    }

    private static ulong ChargeSize(ulong size) => (Math.Max(size, 1UL) + 15) & ~15UL;

    private static ulong AllocationFailure(ApiCallContext ctx)
    {
        ctx.SetLastError(Win32Error.NotEnoughMemory);
        var flags = ctx.Arg32(1) | (ctx.Process.FindHeap(ctx.Arg(0))?.Options ?? 0);
        if ((flags & 4) == 0 || ctx.ReturnAddressSlot == 0) return 0;
        // Raise HEAP_GENERATE_EXCEPTIONS only after the managed dispatcher returns, so guest
        // SEH never unwinds through a CLR callback. Restore the original API return address.
        var raise = ctx.Runtime.NativeApis.Resolve("ntdll.dll", "RtlRaiseStatus", null, out _);
        if (raise == 0) return 0;
        ctx.Write(ctx.ReturnAddressSlot, ctx.Runtime.Thunks.RegisterEntryStub([
            0x48, 0xB8, .. BitConverter.GetBytes(ctx.ReturnAddress), // mov rax, return address
            0x50,                                                // push rax
            0xB9, .. BitConverter.GetBytes(NtStatus.NoMemory),     // mov ecx, STATUS_NO_MEMORY
            0x48, 0xB8, .. BitConverter.GetBytes(raise),            // mov rax, RtlRaiseStatus
            0xFF, 0xE0                                           // jmp rax
        ]));
        return 0;
    }

    private static bool CheckHostAllocation(ApiCallContext ctx, ulong size, ulong oldSize = 0)
    {
        if (ctx.Runtime.Memory.PrepareHostAllocation(size, oldSize)) return true;
        ctx.SetLastError(Win32Error.NotEnoughMemory);
        return false;
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
    private static unsafe bool IsHostHeap(ApiCallContext ctx)
    {
        var handle = (nint)ctx.Arg(0);
        if (handle != 0 && ctx.Runtime.Memory.FindGuestHeap(ctx.Arg(0)) is null)
        {
            Span<nint> heaps = stackalloc nint[64];
            uint count;
            fixed (nint* p = heaps) count = HostGetProcessHeaps((uint)heaps.Length, p);
            if (count > heaps.Length)
            {
                heaps = new nint[count];
                fixed (nint* p = heaps) count = HostGetProcessHeaps((uint)heaps.Length, p);
            }
            if (count <= heaps.Length && heaps[..(int)count].Contains(handle)) return true;
        }
        ctx.SetLastError(Win32Error.InvalidHandle);
        return false;
    }

    private static bool HostBlock(ApiCallContext ctx)
    {
        if (ctx.Arg(2) != 0 && ctx.Runtime.Memory.FindGuestBlock(ctx.Arg(2)) is null &&
            (!ctx.Runtime.Memory.TryGetHostBlock(ctx.Arg(2), out var block) ||
                block.Kind == HostMemoryKind.Heap && block.Heap == ctx.Arg(0)) &&
            HostHeapValidate((nint)ctx.Arg(0), 0, (nint)ctx.Arg(2))) return true;
        ctx.SetLastError(Win32Error.InvalidParameter);
        return false;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetProcessHeaps")]
    private static extern unsafe uint HostGetProcessHeaps(uint count, nint* heaps);

    [DllImport("kernel32.dll", EntryPoint = "HeapCompact")]
    private static extern nuint HostHeapCompact(nint heap, uint flags);

    [DllImport("kernel32.dll", EntryPoint = "HeapWalk", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HostHeapWalk(nint heap, nint entry);

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

