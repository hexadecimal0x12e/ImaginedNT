using System.Runtime.CompilerServices;
using ImaginedNT.Core.Diagnostics;
using ImaginedNT.Core.Nt;

namespace ImaginedNT.Core.Runtime;

public static class FeatureConfigurationApis
{
    // Virtual feature configuration is fixed for the lifetime of the process.
    private const ulong ChangeStamp = 1;
    private static readonly ConditionalWeakTable<ImaginedProcess, Registrations> States = new();
    private static long _nextRegistration;

    private sealed class Registrations
    {
        public readonly Dictionary<ulong, (ulong Callback, ulong Context)> Entries = new();
    }

    [VirtualApi("ntdll.dll", "RtlQueryFeatureConfigurationChangeStamp", ApiCategory.System)]
    private static ulong RtlQueryFeatureConfigurationChangeStamp(ApiCallContext ctx) => ChangeStamp;

    [VirtualApi("ntdll.dll", "RtlRegisterFeatureConfigurationChangeNotification", ApiCategory.System)]
    private static ulong RtlRegisterFeatureConfigurationChangeNotification(ApiCallContext ctx)
    {
        if (ctx.Arg(0) == 0 || ctx.Arg(3) == 0) return NtStatus.InvalidParameter;
        if (ctx.Process.IsTerminating) return NtStatus.ProcessIsTerminating;

        Span<byte> stamp = stackalloc byte[8];
        if (ctx.Arg(2) != 0 && !ctx.Memory.TryRead(ctx.Arg(2), stamp)) return NtStatus.AccessViolation;
        Span<byte> output = stackalloc byte[8];
        output.Clear();
        if (!ctx.Memory.TryWrite(ctx.Arg(3), output)) return NtStatus.AccessViolation;

        var state = States.GetValue(ctx.Process, static _ => new Registrations());
        var handle = (ulong)Interlocked.Increment(ref _nextRegistration);
        lock (state.Entries) state.Entries.Add(handle, (ctx.Arg(0), ctx.Arg(1)));
        BitConverter.TryWriteBytes(output, handle);
        if (!ctx.Memory.TryWrite(ctx.Arg(3), output))
        {
            lock (state.Entries) state.Entries.Remove(handle);
            return NtStatus.AccessViolation;
        }

        ctx.Runtime.Trace.Emit(TraceKind.Api, $"feature configuration notification registered: 0x{handle:X}",
            ctx.Process.Id, ctx.Thread.Id);
        if (ctx.Arg(2) != 0 && BitConverter.ToUInt64(stamp) != ChangeStamp)
            ctx.Thread.InvokeGuest(ctx.Arg(0), [ctx.Arg(1)]);
        return NtStatus.Success;
    }

    [VirtualApi("ntdll.dll", "RtlUnregisterFeatureConfigurationChangeNotification", ApiCategory.System)]
    private static ulong RtlUnregisterFeatureConfigurationChangeNotification(ApiCallContext ctx)
    {
        if (!States.TryGetValue(ctx.Process, out var state)) return NtStatus.InvalidHandle;
        lock (state.Entries)
        {
            if (!state.Entries.Remove(ctx.Arg(0))) return NtStatus.InvalidHandle;
        }
        return NtStatus.Success;
    }
}
