using System.Runtime.InteropServices;
using ImaginedNT.Core.Nt;
using static ImaginedNT.Apis.ApiHelpers;

namespace ImaginedNT.Core.Runtime;

public static class ProcessMemoryApis
{
    [VirtualApi("kernel32.dll", "ReadProcessMemory", ApiCategory.Memory, "kernelbase!ReadProcessMemory")]
    [VirtualApi("kernel32.dll", "WriteProcessMemory", ApiCategory.Memory, "kernelbase!WriteProcessMemory")]
    [VirtualApi("ntdll.dll", "NtReadVirtualMemory", ApiCategory.Memory, "ntdll!ZwReadVirtualMemory")]
    [VirtualApi("ntdll.dll", "NtWriteVirtualMemory", ApiCategory.Memory, "ntdll!ZwWriteVirtualMemory")]
    private static ulong Transfer(ApiCallContext c)
    {
        var nt = c.Name.StartsWith("Nt", StringComparison.Ordinal) || c.Name.StartsWith("Zw", StringComparison.Ordinal);
        var write = c.Name.Contains("Write", StringComparison.Ordinal);
        var access = write ? 0x20u : 0x10u;
        if (write && !nt) access |= 8;
        var status = MemoryApis.TargetMemory(c, c.Arg(0), access, out _);
        if (status != NtStatus.Success) return nt ? status : BoolResult(c, false, Win32FromStatus(status));
        if (c.Arg(4) != 0 && !c.Memory.IsWritable(c.Arg(4), 8))
            return nt ? NtStatus.AccessViolation : BoolResult(c, false, 998);
        nuint transferred;
        if (nt)
        {
            status = write
                ? HostNtWriteVirtualMemory(-1, (nint)c.Arg(1), (nint)c.Arg(2), (nuint)c.Arg(3), out transferred)
                : HostNtReadVirtualMemory(-1, (nint)c.Arg(1), (nint)c.Arg(2), (nuint)c.Arg(3), out transferred);
            if (c.Arg(4) != 0) c.Write(c.Arg(4), (ulong)transferred);
            return status;
        }
        var result = write
            ? HostWriteProcessMemory(-1, (nint)c.Arg(1), (nint)c.Arg(2), (nuint)c.Arg(3), out transferred)
            : HostReadProcessMemory(-1, (nint)c.Arg(1), (nint)c.Arg(2), (nuint)c.Arg(3), out transferred);
        var error = Marshal.GetLastPInvokeError();
        if (c.Arg(4) != 0) c.Write(c.Arg(4), (ulong)transferred);
        return BoolResult(c, result, error);
    }

    [DllImport("kernel32.dll", EntryPoint = "ReadProcessMemory", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HostReadProcessMemory(nint process, nint address, nint buffer, nuint size, out nuint transferred);
    [DllImport("kernel32.dll", EntryPoint = "WriteProcessMemory", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HostWriteProcessMemory(nint process, nint address, nint buffer, nuint size, out nuint transferred);
    [DllImport("ntdll.dll", EntryPoint = "NtReadVirtualMemory")]
    private static extern uint HostNtReadVirtualMemory(nint process, nint address, nint buffer, nuint size, out nuint transferred);
    [DllImport("ntdll.dll", EntryPoint = "NtWriteVirtualMemory")]
    private static extern uint HostNtWriteVirtualMemory(nint process, nint address, nint buffer, nuint size, out nuint transferred);
}
