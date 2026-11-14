using System.Runtime.InteropServices;
using ImaginedNT.Core.Memory;
using ImaginedNT.Core.Nt;

namespace ImaginedNT.Core.Runtime;

public static class LocalMemoryApis
{
    private static bool Global(ApiCallContext c) => c.Name.StartsWith("Global", StringComparison.Ordinal);
    private static HostMemoryKind Kind(ApiCallContext c) => Global(c) ? HostMemoryKind.Global : HostMemoryKind.Local;

    private static bool Valid(ApiCallContext c, ulong address, out GuestHeap? heap)
    {
        heap = c.Runtime.Memory.FindGuestBlock(address);
        if (heap is not null)
        {
            if (ReferenceEquals(heap, c.Process.Heap) && heap.Contains(address)) return true;
        }
        else if (!c.Runtime.Memory.TryGetHostBlock(address, out var block) || block.Kind == Kind(c) ||
            block.Kind == HostMemoryKind.Heap && block.Heap == (ulong)HostGetProcessHeap()) return true;
        c.SetLastError(Win32Error.InvalidHandle);
        return false;
    }

    [VirtualApi("kernel32.dll", "LocalAlloc", ApiCategory.Memory, "kernelbase!LocalAlloc")]
    [VirtualApi("kernel32.dll", "GlobalAlloc", ApiCategory.Memory, "kernelbase!GlobalAlloc")]
    private static ulong Allocate(ApiCallContext c)
    {
        lock (c.Runtime.Memory.Gate)
        {
            if (!c.Runtime.Memory.PrepareHostAllocation(c.Arg(1)))
            {
                c.SetLastError(Win32Error.NotEnoughMemory);
                return 0;
            }
            var result = Global(c) ? HostGlobalAlloc(c.Arg32(0), (nuint)c.Arg(1)) : HostLocalAlloc(c.Arg32(0), (nuint)c.Arg(1));
            if (result == 0) c.SetLastError(Marshal.GetLastPInvokeError());
            else c.Runtime.Memory.TrackHostAllocation(c.Process.Id, 0, 0, (ulong)result,
                RuntimeMemory.ChargeSize(c.Arg(1)), Kind(c));
            return (ulong)result;
        }
    }

    [VirtualApi("kernel32.dll", "LocalFree", ApiCategory.Memory, "kernelbase!LocalFree")]
    [VirtualApi("kernel32.dll", "GlobalFree", ApiCategory.Memory, "kernelbase!GlobalFree")]
    private static ulong Free(ApiCallContext c)
    {
        var address = c.Arg(0);
        if (address == 0) return 0;
        lock (c.Runtime.Memory.Gate)
        {
            if (!Valid(c, address, out var heap)) return address;
            if (heap is not null)
            {
                if (heap.Free(address, c.Thread.Id)) return 0;
                c.SetLastError(Win32Error.InvalidHandle);
                return address;
            }
            var result = Global(c) ? HostGlobalFree((nint)address) : HostLocalFree((nint)address);
            if (result != 0) c.SetLastError(Marshal.GetLastPInvokeError());
            else
            {
                var owner = c.Runtime.Memory.TryGetHostBlock(address, out var block) ? block.Heap : 0;
                c.Runtime.Memory.ForgetHostAllocation(owner, address);
            }
            return (ulong)result;
        }
    }

    [VirtualApi("kernel32.dll", "LocalReAlloc", ApiCategory.Memory, "kernelbase!LocalReAlloc")]
    [VirtualApi("kernel32.dll", "GlobalReAlloc", ApiCategory.Memory, "kernelbase!GlobalReAlloc")]
    private static ulong Reallocate(ApiCallContext c)
    {
        var address = c.Arg(0);
        var size = c.Arg(1);
        var flags = c.Arg32(2);
        lock (c.Runtime.Memory.Gate)
        {
            if (address == 0 || !Valid(c, address, out var heap))
            {
                c.SetLastError(Win32Error.InvalidHandle);
                return 0;
            }
            if (heap is not null)
            {
                if ((flags & ~0x1F2u) != 0)
                {
                    c.SetLastError(Win32Error.InvalidParameter);
                    return 0;
                }
                if ((flags & 0x80) != 0) return address;
                var resized = heap.Reallocate(address, size, (flags & 0x40) != 0, (flags & 2) == 0, c.Thread.Id);
                if (resized == 0) c.SetLastError(Win32Error.NotEnoughMemory);
                return resized;
            }
            var old = c.Runtime.Memory.TryGetHostBlock(address, out var block) ? block.Size : 0;
            var requested = size;
            if ((flags & 0x80) != 0)
                requested = Global(c) ? (ulong)HostGlobalSize((nint)address) : (ulong)HostLocalSize((nint)address);
            if (!c.Runtime.Memory.PrepareHostAllocation(requested, old))
            {
                c.SetLastError(Win32Error.NotEnoughMemory);
                return 0;
            }
            var result = Global(c) ? HostGlobalReAlloc((nint)address, (nuint)size, flags)
                : HostLocalReAlloc((nint)address, (nuint)size, flags);
            if (result == 0) c.SetLastError(Marshal.GetLastPInvokeError());
            else c.Runtime.Memory.TrackHostAllocation(c.Process.Id, 0, address, (ulong)result,
                RuntimeMemory.ChargeSize(requested), Kind(c));
            return (ulong)result;
        }
    }

    [VirtualApi("kernel32.dll", "LocalSize", ApiCategory.Memory, "kernelbase!LocalSize")]
    [VirtualApi("kernel32.dll", "GlobalSize", ApiCategory.Memory, "kernelbase!GlobalSize")]
    private static ulong Size(ApiCallContext c)
    {
        lock (c.Runtime.Memory.Gate)
        {
            if (!Valid(c, c.Arg(0), out var heap)) return 0;
            if (heap is not null) return heap.Size(c.Arg(0), c.Thread.Id);
            var result = Global(c) ? HostGlobalSize((nint)c.Arg(0)) : HostLocalSize((nint)c.Arg(0));
            if (result == 0) c.SetLastError(Marshal.GetLastPInvokeError());
            return (ulong)result;
        }
    }

    [VirtualApi("kernel32.dll", "LocalLock", ApiCategory.Memory, "kernelbase!LocalLock")]
    [VirtualApi("kernel32.dll", "GlobalLock", ApiCategory.Memory, "kernelbase!GlobalLock")]
    private static ulong Lock(ApiCallContext c)
    {
        lock (c.Runtime.Memory.Gate)
        {
            if (!Valid(c, c.Arg(0), out var heap)) return 0;
            if (heap is not null) return c.Arg(0);
            var result = Global(c) ? HostGlobalLock((nint)c.Arg(0)) : HostLocalLock((nint)c.Arg(0));
            if (result == 0) c.SetLastError(Marshal.GetLastPInvokeError());
            return (ulong)result;
        }
    }

    [VirtualApi("kernel32.dll", "LocalUnlock", ApiCategory.Memory, "kernelbase!LocalUnlock")]
    [VirtualApi("kernel32.dll", "GlobalUnlock", ApiCategory.Memory, "kernelbase!GlobalUnlock")]
    private static ulong Unlock(ApiCallContext c)
    {
        lock (c.Runtime.Memory.Gate)
        {
            if (!Valid(c, c.Arg(0), out var heap)) return 0;
            if (heap is not null)
            {
                c.SetLastError(Global(c) ? 0 : 158);
                return Global(c) ? 1UL : 0UL;
            }
            var result = Global(c) ? HostGlobalUnlock((nint)c.Arg(0)) : HostLocalUnlock((nint)c.Arg(0));
            c.SetLastError(Marshal.GetLastPInvokeError());
            return result ? 1UL : 0UL;
        }
    }

    [VirtualApi("kernel32.dll", "LocalHandle", ApiCategory.Memory, "kernelbase!LocalHandle")]
    [VirtualApi("kernel32.dll", "GlobalHandle", ApiCategory.Memory, "kernelbase!GlobalHandle")]
    private static ulong Handle(ApiCallContext c)
    {
        lock (c.Runtime.Memory.Gate)
        {
            if (!Valid(c, c.Arg(0), out var heap)) return 0;
            if (heap is not null) return c.Arg(0);
            var result = Global(c) ? HostGlobalHandle((nint)c.Arg(0)) : HostLocalHandle((nint)c.Arg(0));
            if (result == 0) c.SetLastError(Marshal.GetLastPInvokeError());
            return (ulong)result;
        }
    }

    [VirtualApi("kernel32.dll", "LocalFlags", ApiCategory.Memory, "kernelbase!LocalFlags")]
    [VirtualApi("kernel32.dll", "GlobalFlags", ApiCategory.Memory, "kernelbase!GlobalFlags")]
    private static ulong Flags(ApiCallContext c)
    {
        lock (c.Runtime.Memory.Gate)
        {
            if (!Valid(c, c.Arg(0), out var heap)) return 0x8000;
            if (heap is not null) return 0;
            var result = Global(c) ? HostGlobalFlags((nint)c.Arg(0)) : HostLocalFlags((nint)c.Arg(0));
            if (result == 0x8000) c.SetLastError(Marshal.GetLastPInvokeError());
            return result;
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "GetProcessHeap")]
    private static extern nint HostGetProcessHeap();
    [DllImport("kernel32.dll", EntryPoint = "LocalAlloc", SetLastError = true)]
    private static extern nint HostLocalAlloc(uint flags, nuint size);
    [DllImport("kernel32.dll", EntryPoint = "GlobalAlloc", SetLastError = true)]
    private static extern nint HostGlobalAlloc(uint flags, nuint size);
    [DllImport("kernel32.dll", EntryPoint = "LocalFree", SetLastError = true)]
    private static extern nint HostLocalFree(nint memory);
    [DllImport("kernel32.dll", EntryPoint = "GlobalFree", SetLastError = true)]
    private static extern nint HostGlobalFree(nint memory);
    [DllImport("kernel32.dll", EntryPoint = "LocalReAlloc", SetLastError = true)]
    private static extern nint HostLocalReAlloc(nint memory, nuint size, uint flags);
    [DllImport("kernel32.dll", EntryPoint = "GlobalReAlloc", SetLastError = true)]
    private static extern nint HostGlobalReAlloc(nint memory, nuint size, uint flags);
    [DllImport("kernel32.dll", EntryPoint = "LocalSize", SetLastError = true)]
    private static extern nuint HostLocalSize(nint memory);
    [DllImport("kernel32.dll", EntryPoint = "GlobalSize", SetLastError = true)]
    private static extern nuint HostGlobalSize(nint memory);
    [DllImport("kernel32.dll", EntryPoint = "LocalLock", SetLastError = true)]
    private static extern nint HostLocalLock(nint memory);
    [DllImport("kernel32.dll", EntryPoint = "GlobalLock", SetLastError = true)]
    private static extern nint HostGlobalLock(nint memory);
    [DllImport("kernel32.dll", EntryPoint = "LocalUnlock", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HostLocalUnlock(nint memory);
    [DllImport("kernel32.dll", EntryPoint = "GlobalUnlock", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HostGlobalUnlock(nint memory);
    [DllImport("kernel32.dll", EntryPoint = "LocalHandle", SetLastError = true)]
    private static extern nint HostLocalHandle(nint memory);
    [DllImport("kernel32.dll", EntryPoint = "GlobalHandle", SetLastError = true)]
    private static extern nint HostGlobalHandle(nint memory);
    [DllImport("kernel32.dll", EntryPoint = "LocalFlags", SetLastError = true)]
    private static extern uint HostLocalFlags(nint memory);
    [DllImport("kernel32.dll", EntryPoint = "GlobalFlags", SetLastError = true)]
    private static extern uint HostGlobalFlags(nint memory);
}
