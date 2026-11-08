using System.Runtime.CompilerServices;
using ImaginedNT.Core.Memory;
using ImaginedNT.Core.Nt;
using ImaginedNT.Core.Runtime;

namespace ImaginedNT.Apis.Crt;

internal sealed partial class CrtProcessState
{
    private static readonly ConditionalWeakTable<ImaginedProcess, CrtProcessState> States = new();
    private readonly Dictionary<string, GuestString> _strings = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private ulong _fileModes;

    private sealed record GuestString(ulong Address, ulong Capacity, string Value);

    internal static ulong GetFileModes(ApiCallContext ctx)
    {
        var state = States.GetValue(ctx.Process, static _ => new CrtProcessState());
        lock (state._gate)
        {
            if (state._fileModes != 0) return state._fileModes;
            var status = ctx.Memory.TryAllocate(0, 12, MemoryProtection.ReadWrite,
                AllocationType.Reserve | AllocationType.Commit, out var address, out _, "CRT file modes");
            if (status != NtStatus.Success)
            {
                ctx.Runtime.Trace.Warn($"{ctx.Name}: cannot allocate CRT file modes: {NtStatus.NameOf(status)}", ctx.Process.Id, ctx.Thread.Id);
                return 0;
            }
            // _fmode has app/OS copies; _commode is shared within the guest process.
            if (!CrtMemory.Write(ctx, address, 0x4000) || !CrtMemory.Write(ctx, address + 4, 0x4000) ||
                !CrtMemory.Write(ctx, address + 8, 0))
            {
                ctx.Memory.TryFree(address, 0, FreeType.Release);
                ctx.Runtime.Trace.Warn($"{ctx.Name}: cannot initialize CRT file modes at 0x{address:X}", ctx.Process.Id, ctx.Thread.Id);
                return 0;
            }
            return state._fileModes = address;
        }
    }

    internal static bool GetString(ApiCallContext ctx, string key, string value, int width, out ulong address)
    {
        address = 0;
        var state = States.GetValue(ctx.Process, static _ => new CrtProcessState());
        lock (state._gate)
        {
            key = width + ":" + key;
            state._strings.TryGetValue(key, out var previous);
            if (previous is not null && previous.Value == value)
            {
                address = previous.Address;
                return true;
            }
            var bytes = CrtMemory.Encode(value, width, ansi: true);
            var capacity = previous?.Capacity ?? 0;
            var buffer = previous?.Address ?? 0;
            var allocated = (ulong)bytes.Length > capacity;
            if (allocated)
            {
                var status = ctx.Memory.TryAllocate(0, (ulong)bytes.Length, MemoryProtection.ReadWrite,
                    AllocationType.Reserve | AllocationType.Commit, out buffer, out capacity, "CRT string");
                if (status != NtStatus.Success)
                {
                    ctx.Runtime.Trace.Warn($"{ctx.Name}: cannot allocate guest string: {NtStatus.NameOf(status)}", ctx.Process.Id, ctx.Thread.Id);
                    return false;
                }
            }
            if (!ctx.Memory.TryWrite(buffer, bytes))
            {
                if (allocated) ctx.Memory.TryFree(buffer, 0, FreeType.Release);
                ctx.Runtime.Trace.Warn($"{ctx.Name}: cannot write guest string at 0x{buffer:X}", ctx.Process.Id, ctx.Thread.Id);
                return false;
            }
            state._strings[key] = new(buffer, capacity, value);
            if (allocated && previous is not null) ctx.Memory.TryFree(previous.Address, 0, FreeType.Release);
            address = buffer;
            return true;
        }
    }
}
