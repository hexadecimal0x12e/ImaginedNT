using System.Runtime.InteropServices;
using System.Text;
using ImaginedNT.Core.Nt;
using ImaginedNT.Core.Objects;
using static ImaginedNT.Apis.ApiHelpers;

namespace ImaginedNT.Core.Runtime;

public static unsafe class ProcessEnumerationApis
{
    private sealed class ProcessSnapshot(IReadOnlyList<ProcessInformation> processes) : NtObject
    {
        public override ObjectType ObjectType => ObjectType.Unknown;
        public override string TypeName => "Snapshot";
        public IReadOnlyList<ProcessInformation> Processes { get; } = processes;
        public int Position { get; set; } = -1;
    }

    [VirtualApi("psapi.dll", "EnumProcesses", ApiCategory.Process,
        "kernel32!K32EnumProcesses", "kernelbase!K32EnumProcesses")]
    private static ulong EnumProcesses(ApiCallContext ctx)
    {
        if (ctx.Arg(2) == 0 || ctx.Arg(0) == 0 && ctx.Arg32(1) != 0)
            return BoolResult(ctx, false, Win32Error.InvalidParameter);
        var processes = ctx.Runtime.GetProcessInformation();
        var count = (int)Math.Min(ctx.Arg32(1) / 4, (uint)processes.Count);
        for (var i = 0; i < count; i++) ctx.Write(ctx.Arg(0) + (ulong)i * 4, (uint)processes[i].Id);
        ctx.Write(ctx.Arg(2), (uint)count * 4);
        return 1;
    }

    [VirtualApi("kernel32.dll", "CreateToolhelp32Snapshot", ApiCategory.Process, "kernelbase!CreateToolhelp32Snapshot")]
    private static ulong CreateToolhelp32Snapshot(ApiCallContext ctx)
    {
        if ((ctx.Arg32(0) & 2) == 0)
        {
            ctx.SetLastError(Win32Error.NotSupported);
            return InvalidHandleValue;
        }
        using var snapshot = new ProcessSnapshot(ctx.Runtime.GetProcessInformation());
        return ctx.Handles.Create(snapshot, 0x001F_FFFF, inheritable: (ctx.Arg32(0) & 0x8000_0000) != 0);
    }

    [VirtualApi("kernel32.dll", "Process32FirstW", ApiCategory.Process, "kernelbase!Process32FirstW")]
    [VirtualApi("kernel32.dll", "Process32First", ApiCategory.Process, "kernelbase!Process32First")]
    [VirtualApi("kernel32.dll", "Process32NextW", ApiCategory.Process, "kernelbase!Process32NextW")]
    [VirtualApi("kernel32.dll", "Process32Next", ApiCategory.Process, "kernelbase!Process32Next")]
    private static ulong Process32(ApiCallContext ctx)
    {
        if (GetObject(ctx, ctx.Arg(0)) is not ProcessSnapshot snapshot)
            return BoolResult(ctx, false, Win32Error.InvalidHandle);
        var unicode = ctx.Name.EndsWith('W');
        var size = unicode ? 568u : 304u;
        if (ctx.Arg(1) == 0 || ctx.Read<uint>(ctx.Arg(1)) < size)
            return BoolResult(ctx, false, Win32Error.InvalidParameter);
        lock (snapshot)
        {
            var index = ctx.Name.Contains("First", StringComparison.Ordinal) ? 0 : snapshot.Position + 1;
            if (index >= snapshot.Processes.Count) return BoolResult(ctx, false, Win32Error.NoMoreFiles);
            snapshot.Position = index;
            var p = snapshot.Processes[index];
            var buffer = ctx.Arg(1);
            ctx.WriteBytes(buffer, new byte[size]);
            ctx.Write(buffer, size);
            ctx.Write(buffer + 8, (uint)p.Id);
            ctx.Write(buffer + 28, (uint)p.Threads.Count);
            ctx.Write(buffer + 32, (uint)p.ParentId);
            ctx.Write(buffer + 36, p.Priority);
            var bytes = (unicode ? Encoding.Unicode :
                CodePagesEncodingProvider.Instance.GetEncoding(0) ?? Encoding.UTF8).GetBytes(p.Name);
            ctx.WriteBytes(buffer + 44, bytes.AsSpan(0, Math.Min(bytes.Length, unicode ? 518 : 259)));
            return 1;
        }
    }

    [VirtualApi("ntdll.dll", "NtQuerySystemInformation", ApiCategory.Process, "ntdll!ZwQuerySystemInformation")]
    private static ulong NtQuerySystemInformation(ApiCallContext ctx)
    {
        var infoClass = ctx.Arg32(0);
        if (infoClass is not (5 or 57 or 252))
        {
            // 未模拟的进程/句柄列表不能转发为宿主列表。
            if (infoClass is 16 or 53 or 64 or 88 or 148 || !ctx.Runtime.Options.AllowNativeForwarding ||
                ctx.Runtime.Options.ImportPolicy == ImportPolicy.ManagedOnly ||
                !ctx.Runtime.IsNativeForwardingAllowed("ntdll", "NtQuerySystemInformation"))
                return NtStatus.InvalidInfoClass;
            var address = ctx.Runtime.NativeApis.Resolve("ntdll.dll", "NtQuerySystemInformation", null, out _);
            return address == 0 ? NtStatus.InvalidInfoClass :
                ((delegate* unmanaged<uint, ulong, uint, ulong, uint>)(nint)address)(infoClass, ctx.Arg(1), ctx.Arg32(2), ctx.Arg(3));
        }
        var processes = ctx.Runtime.GetProcessInformation();
        var basic = infoClass == 252;
        var threadSize = infoClass == 57 ? 136 : 80;
        var sizes = processes.Select(p => ((basic ? 48 : 256 + p.Threads.Count * threadSize) +
            (p.Name.Length + 1) * 2 + 7) & ~7).ToArray();
        var required = checked((uint)sizes.Sum());
        if (ctx.Arg(3) != 0) ctx.Write(ctx.Arg(3), required);
        if (ctx.Arg32(2) < required) return NtStatus.InfoLengthMismatch;
        if (ctx.Arg(1) == 0) return NtStatus.AccessViolation;
        var buffer = ctx.Arg(1);
        for (var i = 0; i < processes.Count; i++)
        {
            var p = processes[i];
            ctx.WriteBytes(buffer, new byte[sizes[i]]);
            var nameBuffer = buffer + (ulong)(basic ? 48 : 256 + p.Threads.Count * threadSize);
            var name = new UnicodeString
            {
                Length = (ushort)(p.Name.Length * 2), MaximumLength = (ushort)((p.Name.Length + 1) * 2), Buffer = nameBuffer
            };
            var next = i + 1 == processes.Count ? 0u : (uint)sizes[i];
            if (basic)
            {
                ctx.Write(buffer, new BasicProcessInformation
                {
                    NextEntryOffset = next, ProcessId = (ulong)p.Id, ParentProcessId = (ulong)p.ParentId,
                    SequenceNumber = (ulong)p.StartTime.UtcTicks, ImageName = name
                });
            }
            else
            {
                var m = p.Memory;
                ctx.Write(buffer, new SystemProcessInformation
                {
                    NextEntryOffset = next, NumberOfThreads = (uint)p.Threads.Count,
                    WorkingSetPrivateSize = Math.Min(m.Process.WorkingSetSize, m.Process.PrivateUsage),
                    NumberOfThreadsHighWatermark = (uint)p.Threads.Count,
                    CreateTime = p.StartTime.UtcDateTime.ToFileTimeUtc(), ImageName = name,
                    BasePriority = p.Priority, ProcessId = (ulong)p.Id, ParentProcessId = (ulong)p.ParentId,
                    HandleCount = (uint)p.HandleCount, SessionId = p.SessionId,
                    PeakVirtualSize = m.PeakVirtualBytes, VirtualSize = m.VirtualBytes,
                    PageFaultCount = m.Process.PageFaultCount,
                    PeakWorkingSetSize = m.Process.PeakWorkingSetSize, WorkingSetSize = m.Process.WorkingSetSize,
                    PagefileUsage = m.Process.PagefileUsage, PeakPagefileUsage = m.Process.PeakPagefileUsage,
                    PrivatePageCount = m.Process.PrivateUsage
                });
                for (var j = 0; j < p.Threads.Count; j++)
                {
                    var t = p.Threads[j];
                    var address = buffer + 256 + (ulong)(j * threadSize);
                    ctx.Write(address, new SystemThreadInformation
                    {
                        CreateTime = p.StartTime.UtcDateTime.ToFileTimeUtc(), StartAddress = t.StartAddress,
                        ProcessId = (ulong)p.Id, ThreadId = (ulong)t.Id, Priority = p.Priority, BasePriority = p.Priority,
                        State = t.Suspended ? 5u : t.State == ThreadState.Running ? 2u : 0u,
                        WaitReason = t.Suspended ? 5u : 0u
                    });
                    if (infoClass == 57) ctx.Write(address + 96, t.StartAddress);
                }
            }
            ctx.WriteBytes(nameBuffer, Encoding.Unicode.GetBytes(p.Name));
            buffer += (ulong)sizes[i];
        }
        return NtStatus.Success;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length, MaximumLength;
        public ulong Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicProcessInformation
    {
        public uint NextEntryOffset;
        public ulong ProcessId, ParentProcessId, SequenceNumber;
        public UnicodeString ImageName;
    }

    // AMD64 的固定进程头为 256 字节，后接线程数组及映像名。
    [StructLayout(LayoutKind.Sequential)]
    private struct SystemProcessInformation
    {
        public uint NextEntryOffset, NumberOfThreads;
        public ulong WorkingSetPrivateSize;
        public uint HardFaultCount, NumberOfThreadsHighWatermark;
        public ulong CycleTime;
        public long CreateTime, UserTime, KernelTime;
        public UnicodeString ImageName;
        public int BasePriority;
        public ulong ProcessId, ParentProcessId;
        public uint HandleCount, SessionId;
        public ulong UniqueProcessKey, PeakVirtualSize, VirtualSize;
        public uint PageFaultCount;
        public ulong PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage;
        public ulong QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivatePageCount;
        public long ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public long ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemThreadInformation
    {
        public long KernelTime, UserTime, CreateTime;
        public uint WaitTime;
        public ulong StartAddress, ProcessId, ThreadId;
        public int Priority, BasePriority;
        public uint ContextSwitches, State, WaitReason;
    }
}
