using ImaginedNT.Core.Diagnostics;
using ImaginedNT.Core.Nt;

namespace ImaginedNT.Core.Runtime;

/// <summary>
/// Virtual syscall numbers understood by the managed layer.  These are deliberately in a
/// private range: a real Windows syscall number is meaningless without a matching kernel ABI,
/// so ImaginedNT only claims numbers it can emulate completely.  A future port would replace
/// this table with ntdll stub parsing and a per-build syscall map.
/// </summary>
public static class VirtualSyscallNumbers
{
    public const uint NtImaginedTest = 0x1000;
    public const uint NtImaginedGetProcessId = 0x1001;
    public const uint NtImaginedGetThreadId = 0x1002;
    public const uint NtImaginedQuerySystemTime = 0x1003;
    public const uint NtImaginedAllocate = 0x1010;
    public const uint NtImaginedFree = 0x1011;
    public const uint NtImaginedWrite = 0x1020;
    public const uint NtImaginedExitThread = 0x1021;

    public static string NameOf(uint number) => number switch
    {
        NtImaginedTest => "NtImaginedTest",
        NtImaginedGetProcessId => "NtImaginedGetProcessId",
        NtImaginedGetThreadId => "NtImaginedGetThreadId",
        NtImaginedQuerySystemTime => "NtImaginedQuerySystemTime",
        NtImaginedAllocate => "NtImaginedAllocate",
        NtImaginedFree => "NtImaginedFree",
        NtImaginedWrite => "NtImaginedWrite",
        NtImaginedExitThread => "NtImaginedExitThread",
        _ => $"0x{number:X}"
    };
}

public delegate ulong SyscallHandler(SyscallContext context);

public sealed class SyscallContext
{
    public required ImaginedRuntime Runtime { get; init; }
    public required ImaginedProcess Process { get; init; }
    public required ImaginedThread Thread { get; init; }
    public required uint Number { get; init; }
    public required ulong[] Arguments { get; init; }
    public required ulong ReturnAddress { get; init; }
    public ulong Arg(int index) => index >= 0 && index < Arguments.Length ? Arguments[index] : 0;
}

public sealed class SyscallDispatcher
{
    private readonly Dictionary<uint, (string Name, SyscallHandler Handler)> _handlers = new();
    private readonly ImaginedRuntime _runtime;

    public SyscallDispatcher(ImaginedRuntime runtime)
    {
        _runtime = runtime;
        Register(VirtualSyscallNumbers.NtImaginedTest, "NtImaginedTest", ctx => 41);
        Register(VirtualSyscallNumbers.NtImaginedGetProcessId, "NtImaginedGetProcessId", ctx => (ulong)ctx.Process.Id);
        Register(VirtualSyscallNumbers.NtImaginedGetThreadId, "NtImaginedGetThreadId", ctx => (ulong)ctx.Thread.Id);
        Register(VirtualSyscallNumbers.NtImaginedQuerySystemTime, "NtImaginedQuerySystemTime", ctx => (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Register(VirtualSyscallNumbers.NtImaginedAllocate, "NtImaginedAllocate", ctx =>
        {
            var size = ctx.Arg(0);
            var protection = (MemoryProtection)ctx.Arg(1);
            if (protection == 0) protection = MemoryProtection.ReadWrite;
            var status = ctx.Process.Memory.TryAllocate(0, size, protection, AllocationType.Commit | AllocationType.Reserve,
                out var baseAddress, out _, "syscall allocation");
            return NtStatus.IsSuccess(status) ? baseAddress : 0;
        });
        Register(VirtualSyscallNumbers.NtImaginedFree, "NtImaginedFree", ctx =>
        {
            var status = ctx.Process.Memory.TryFree(ctx.Arg(0), 0, FreeType.Release);
            return status;
        });
        Register(VirtualSyscallNumbers.NtImaginedWrite, "NtImaginedWrite", ctx =>
        {
            var address = ctx.Arg(0);
            var length = (int)Math.Min(ctx.Arg(1), 1 << 20);
            var bytes = ctx.Process.Memory.ReadBytes(address, length);
            var stdout = _runtime.Options.StandardOutput;
            stdout.Write(bytes, 0, bytes.Length);
            stdout.Flush();
            return (ulong)bytes.Length;
        });
        Register(VirtualSyscallNumbers.NtImaginedExitThread, "NtImaginedExitThread", ctx =>
        {
            ctx.Process.ExitCurrentThread((int)ctx.Arg(0));
            return Nt.NtStatus.Success;
        });
    }

    public int Count => _handlers.Count;

    public void Register(uint number, string name, SyscallHandler handler)
    {
        if (!_handlers.TryAdd(number, (name, handler)))
            throw new InvalidOperationException($"duplicate syscall number 0x{number:X}");
    }

    public uint Dispatch(uint number, ulong[] args, ulong returnAddress, out ulong result, ulong returnAddressSlot = 0)
    {
        result = 0;
        if (!_handlers.TryGetValue(number, out var entry))
        {
            // Not one of our private virtual numbers: ask the host build's ntdll which real
            // Nt* syscall this is, then run the matching managed implementation.
            if (NativeSyscallMap.TryGetName(number, out var nativeName))
            {
                var status = _runtime.DispatchManagedByName("ntdll", nativeName, args, returnAddress, out result, returnAddressSlot);
                _runtime.Trace.Emit(TraceKind.Syscall,
                    $"syscall 0x{number:X} {nativeName} (host number) -> 0x{result:X} status=0x{status:X} caller=0x{returnAddress:X16}",
                    _runtime.GetCurrentThread()?.Process.Id ?? 0);
                return status;
            }

            _runtime.Trace.Emit(TraceKind.Syscall, $"unhandled syscall 0x{number:X} from 0x{returnAddress:X16}", _runtime.GetCurrentThread()?.Process.Id ?? 0);
            return Nt.NtStatus.NotImplemented;
        }

        var nativeId = Native.NativeMethods.GetCurrentThreadId();
        var thread = _runtime.GetCurrentThread() ?? _runtime.PrimaryProcess?.GetOrRegisterHostThread(nativeId);
        var process = thread?.Process;
        if (process is null || thread is null) return Nt.NtStatus.ProcessIsTerminating;
        var context = new SyscallContext
        {
            Runtime = _runtime,
            Process = process,
            Thread = thread,
            Number = number,
            Arguments = args,
            ReturnAddress = returnAddress
        };

        try
        {
            result = entry.Handler(context);
            _runtime.Trace.Emit(TraceKind.Syscall,
                $"syscall 0x{number:X} {entry.Name} -> 0x{result:X} caller=0x{returnAddress:X16}", process.Id, thread.Id);
            return Nt.NtStatus.Success;
        }
        catch (Exception ex)
        {
            _runtime.Trace.Error($"syscall {entry.Name} failed: {ex.GetType().Name}: {ex.Message}", process.Id, thread.Id);
            result = 0;
            return Nt.NtStatus.NotImplemented;
        }
    }
}
