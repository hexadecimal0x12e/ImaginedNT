using System.Runtime.InteropServices;
using ImaginedNT.Core.Memory;
using ImaginedNT.Core.Nt;
using static ImaginedNT.Apis.ApiHelpers;

namespace ImaginedNT.Core.Runtime;

public static class MemoryInformationApis
{
    [VirtualApi("kernel32.dll", "GlobalMemoryStatusEx", ApiCategory.Memory, "kernelbase!GlobalMemoryStatusEx")]
    private static ulong GlobalMemoryStatusEx(ApiCallContext ctx)
    {
        if (ctx.Arg(0) == 0 || ctx.Read<uint>(ctx.Arg(0)) != 64)
            return BoolResult(ctx, false, Win32Error.InvalidParameter);
        ctx.Memory.Write(ctx.Arg(0), ctx.Runtime.Memory.GetSnapshot().Available);
        return 1;
    }

    [VirtualApi("kernel32.dll", "GlobalMemoryStatus", ApiCategory.Memory, "kernelbase!GlobalMemoryStatus")]
    private static ulong GlobalMemoryStatus(ApiCallContext ctx)
    {
        if (ctx.Arg(0) == 0) return 0;
        var s = ctx.Runtime.Memory.GetSnapshot().Available;
        // MEMORYSTATUS uses SIZE_T fields on AMD64, unlike MEMORYSTATUSEX's DWORDLONG fields.
        s.Length = 56;
        ctx.Memory.WriteBytes(ctx.Arg(0), new byte[56]);
        ctx.Write(ctx.Arg(0), s.Length);
        ctx.Write(ctx.Arg(0) + 4, s.MemoryLoad);
        ctx.Write(ctx.Arg(0) + 8, s.TotalPhysical);
        ctx.Write(ctx.Arg(0) + 16, s.AvailablePhysical);
        ctx.Write(ctx.Arg(0) + 24, s.TotalPageFile);
        ctx.Write(ctx.Arg(0) + 32, s.AvailablePageFile);
        ctx.Write(ctx.Arg(0) + 40, s.TotalVirtual);
        ctx.Write(ctx.Arg(0) + 48, s.AvailableVirtual);
        return 0;
    }

    [VirtualApi("psapi.dll", "GetProcessMemoryInfo", ApiCategory.Memory,
        "kernel32!K32GetProcessMemoryInfo", "kernelbase!K32GetProcessMemoryInfo")]
    private static ulong GetProcessMemoryInfo(ApiCallContext ctx)
    {
        var process = GetProcessInformation(ctx, ctx.Arg(0));
        if (process is null) return BoolResult(ctx, false, Win32Error.InvalidHandle);
        if (ctx.Arg(1) == 0 || ctx.Arg32(2) < 72) return BoolResult(ctx, false, Win32Error.InvalidParameter);
        var counters = process.Memory.Process;
        counters.Size = ctx.Arg32(2) >= 80 ? 80u : 72u;
        var bytes = new byte[80];
        MemoryMarshal.Write(bytes.AsSpan(), in counters);
        ctx.WriteBytes(ctx.Arg(1), bytes.AsSpan(0, (int)counters.Size));
        return 1;
    }

    [VirtualApi("kernel32.dll", "GetPhysicallyInstalledSystemMemory", ApiCategory.Memory,
        "kernelbase!GetPhysicallyInstalledSystemMemory")]
    private static ulong GetPhysicallyInstalledSystemMemory(ApiCallContext ctx)
    {
        if (ctx.Arg(0) == 0) return BoolResult(ctx, false, Win32Error.InvalidParameter);
        ctx.Write(ctx.Arg(0), ctx.Runtime.Memory.GetSnapshot().Available.TotalPhysical / 1024);
        return 1;
    }

    [VirtualApi("ntdll.dll", "NtQueryInformationProcess", ApiCategory.Memory, "ntdll!ZwQueryInformationProcess")]
    private static ulong NtQueryInformationProcess(ApiCallContext ctx)
    {
        var process = GetProcessInformation(ctx, ctx.Arg(0));
        if (process is null) return NtStatus.InvalidHandle;
        var length = ctx.Arg32(3);
        if (ctx.Arg32(1) == 0) // ProcessBasicInformation, AMD64
        {
            if (ctx.Arg(4) != 0) ctx.Write(ctx.Arg(4), 48u);
            if (length < 48) return NtStatus.InfoLengthMismatch;
            if (ctx.Arg(2) == 0) return NtStatus.AccessViolation;
            ctx.WriteBytes(ctx.Arg(2), new byte[48]);
            ctx.Write(ctx.Arg(2), unchecked((uint)(process.ExitCode ?? 259)));
            ctx.Write(ctx.Arg(2) + 16, 1UL);
            ctx.Write(ctx.Arg(2) + 24, process.Priority);
            ctx.Write(ctx.Arg(2) + 32, (ulong)process.Id);
            ctx.Write(ctx.Arg(2) + 40, (ulong)process.ParentId);
            return NtStatus.Success;
        }
        if (ctx.Arg32(1) is 27 or 43) // ProcessImageFileName / ProcessImageFileNameWin32
        {
            var path = process.IsSystem && ctx.Arg32(1) == 27 ? @"\SystemRoot\System32\ntoskrnl.exe" : process.ImagePath;
            var imageBytes = System.Text.Encoding.Unicode.GetBytes(path + '\0');
            if (ctx.Arg(4) != 0) ctx.Write(ctx.Arg(4), (uint)(16 + imageBytes.Length));
            if (length < 16 + imageBytes.Length) return NtStatus.InfoLengthMismatch;
            if (ctx.Arg(2) == 0) return NtStatus.AccessViolation;
            ctx.WriteBytes(ctx.Arg(2), new byte[16]);
            ctx.Write(ctx.Arg(2), (ushort)(imageBytes.Length - 2));
            ctx.Write(ctx.Arg(2) + 2, (ushort)imageBytes.Length);
            ctx.Write(ctx.Arg(2) + 8, ctx.Arg(2) + 16);
            ctx.WriteBytes(ctx.Arg(2) + 16, imageBytes);
            return NtStatus.Success;
        }
        if (ctx.Arg32(1) != 3) return NtStatus.InvalidInfoClass; // ProcessVmCounters
        if (ctx.Arg(4) != 0) ctx.Write(ctx.Arg(4), length >= 96 ? 96u : 88u);
        if (length != 88 && length != 96) return NtStatus.InfoLengthMismatch;
        if (ctx.Arg(2) == 0) return NtStatus.AccessViolation;
        var snapshot = process.Memory;
        var counters = new VmCounters
        {
            PeakVirtualSize = snapshot.PeakVirtualBytes, VirtualSize = snapshot.VirtualBytes,
            PeakWorkingSetSize = snapshot.Process.PeakWorkingSetSize, WorkingSetSize = snapshot.Process.WorkingSetSize,
            PagefileUsage = snapshot.Process.PagefileUsage, PeakPagefileUsage = snapshot.Process.PeakPagefileUsage,
            PrivateUsage = snapshot.Process.PrivateUsage
        };
        Span<byte> bytes = stackalloc byte[96];
        MemoryMarshal.Write(bytes, in counters);
        ctx.WriteBytes(ctx.Arg(2), bytes[..(int)length]);
        return NtStatus.Success;
    }

    [VirtualApi("psapi.dll", "GetPerformanceInfo", ApiCategory.Memory,
        "kernel32!K32GetPerformanceInfo", "kernelbase!K32GetPerformanceInfo")]
    private static ulong GetPerformanceInfo(ApiCallContext ctx)
    {
        if (ctx.Arg(0) == 0 || ctx.Arg32(1) < 104) return BoolResult(ctx, false, Win32Error.InvalidParameter);
        var snapshot = ctx.Runtime.Memory.GetSnapshot();
        var processes = ctx.Runtime.GetProcessInformation();
        var system = processes[0].Memory.Process;
        ctx.Write(ctx.Arg(0), new PerformanceInformation
        {
            Size = 104,
            CommitTotal = Pages(snapshot.Process.PrivateUsage + system.PrivateUsage), CommitLimit = snapshot.Available.TotalPageFile / VirtualMemoryManager.PageSize,
            CommitPeak = Pages(snapshot.Process.PeakPagefileUsage + system.PeakPagefileUsage), PhysicalTotal = snapshot.Available.TotalPhysical / VirtualMemoryManager.PageSize,
            PhysicalAvailable = (snapshot.Available.AvailablePhysical > system.WorkingSetSize
                ? snapshot.Available.AvailablePhysical - system.WorkingSetSize : 0) / VirtualMemoryManager.PageSize,
            KernelTotal = Pages(system.WorkingSetSize), KernelNonpaged = Pages(system.WorkingSetSize),
            PageSize = VirtualMemoryManager.PageSize,
            ProcessCount = (uint)processes.Count,
            HandleCount = (uint)processes.Sum(p => p.HandleCount),
            ThreadCount = (uint)processes.Sum(p => p.Threads.Count)
        });
        return 1;
    }

    private static ulong Pages(ulong size) => size / VirtualMemoryManager.PageSize + (size % VirtualMemoryManager.PageSize == 0 ? 0UL : 1UL);

    [StructLayout(LayoutKind.Sequential)]
    private struct PerformanceInformation
    {
        public uint Size;
        public ulong CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable;
        public ulong SystemCache, KernelTotal, KernelPaged, KernelNonpaged, PageSize;
        public uint HandleCount, ProcessCount, ThreadCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VmCounters
    {
        public ulong PeakVirtualSize, VirtualSize;
        public uint PageFaultCount;
        public ulong PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage;
        public ulong QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage;
    }
}
