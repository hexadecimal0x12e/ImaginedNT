using ImaginedNT.Core.Runtime;

namespace ImaginedNT.Apis.Crt;

public static partial class CrtExitApis
{
    [PartiallyImaginedApi(Module, "_initialize_onexit_table", ApiCategory.Process, "ucrtbase!_initialize_onexit_table",
        "api-ms-win-crt-private-l1-1-0!_o__initialize_onexit_table", "ucrtbase!_o__initialize_onexit_table")]
    private static ulong InitializeOnExitTable(ApiCallContext ctx) => CrtProcessState.InitializeOnExitTable(ctx);

    [PartiallyImaginedApi(Module, "_register_onexit_function", ApiCategory.Process, "ucrtbase!_register_onexit_function",
        "api-ms-win-crt-private-l1-1-0!_o__register_onexit_function", "ucrtbase!_o__register_onexit_function")]
    private static ulong RegisterOnExitFunction(ApiCallContext ctx) => CrtProcessState.RegisterOnExitFunction(ctx);

    [PartiallyImaginedApi(Module, "_execute_onexit_table", ApiCategory.Process, "ucrtbase!_execute_onexit_table",
        "api-ms-win-crt-private-l1-1-0!_o__execute_onexit_table", "ucrtbase!_o__execute_onexit_table")]
    private static ulong ExecuteOnExitTable(ApiCallContext ctx) => CrtProcessState.ExecuteOnExitTable(ctx);
}

internal sealed partial class CrtProcessState
{
    private struct OnExitTable
    {
        public ulong First, Last, End;
    }

    internal static ulong InitializeOnExitTable(ApiCallContext ctx)
    {
        var state = States.GetValue(ctx.Process, static _ => new CrtProcessState());
        lock (state._exitCallbacksGate)
        {
            if (!CrtMemory.Read(ctx, ctx.Arg(0), out OnExitTable table)) return OnExitError(ctx);
            if (table.First != table.End) return 0;
            return CrtMemory.Write(ctx, ctx.Arg(0), default(OnExitTable)) ? 0 : OnExitError(ctx);
        }
    }

    internal static ulong RegisterOnExitFunction(ApiCallContext ctx)
    {
        var state = States.GetValue(ctx.Process, static _ => new CrtProcessState());
        lock (state._exitCallbacksGate)
        {
            if (!ReadOnExitTable(ctx, out var table)) return OnExitError(ctx);
            if (table.Last != table.End)
            {
                if (!CrtMemory.Write(ctx, table.Last, ctx.Arg(1))) return OnExitError(ctx);
                table.Last += 8;
                return CrtMemory.Write(ctx, ctx.Arg(0), table) ? 0 : OnExitError(ctx);
            }
            var bytes = table.End - table.First;
            var increment = Math.Max(256UL, Math.Min(bytes, 4096UL));
            if (bytes > (ulong)long.MaxValue - increment) return OnExitError(ctx);
            var buffer = ctx.Process.Heap.Allocate(bytes + increment, true, ctx.Thread.Id);
            if (buffer == 0) return uint.MaxValue;
            var updated = new OnExitTable { First = buffer, Last = buffer + bytes + 8, End = buffer + bytes + increment };
            if (!CrtMemory.Copy(ctx, buffer, table.First, bytes) ||
                !CrtMemory.Write(ctx, buffer + bytes, ctx.Arg(1)) || !CrtMemory.Write(ctx, ctx.Arg(0), updated))
            {
                ctx.Process.Heap.Free(buffer, ctx.Thread.Id);
                return OnExitError(ctx);
            }
            if (table.First != 0) ctx.Process.Heap.Free(table.First, ctx.Thread.Id);
            return 0;
        }
    }

    internal static unsafe ulong ExecuteOnExitTable(ApiCallContext ctx)
    {
        var state = States.GetValue(ctx.Process, static _ => new CrtProcessState());
        lock (state._exitCallbacksGate)
        {
            for (;;)
            {
                if (!CrtMemory.Read(ctx, ctx.Arg(0), out OnExitTable table)) return OnExitError(ctx);
                if (table.First is 0 or ulong.MaxValue) return 0;
                if (!ReadOnExitTable(ctx, out table)) return OnExitError(ctx);
                if (table.Last == table.First)
                {
                    if (!CrtMemory.Write(ctx, ctx.Arg(0), default(OnExitTable))) return OnExitError(ctx);
                    return ctx.Process.Heap.Free(table.First, ctx.Thread.Id) ? 0UL : uint.MaxValue;
                }
                table.Last -= 8;
                if (!CrtMemory.Read(ctx, table.Last, out ulong fn) || !CrtMemory.Write(ctx, table.Last, 0UL) ||
                    !CrtMemory.Write(ctx, ctx.Arg(0), table)) return OnExitError(ctx);
                if (fn == 0) continue;
                if (ctx.Thread.IsManagedHosted) ctx.Thread.InvokeGuest(fn, []);
                else ((delegate* unmanaged<void>)(nint)fn)();
            }
        }
    }

    private static bool ReadOnExitTable(ApiCallContext ctx, out OnExitTable table)
    {
        if (!CrtMemory.Read(ctx, ctx.Arg(0), out table)) return false;
        if (table.First == 0) return table.Last == 0 && table.End == 0;
        if (table.Last < table.First || table.End < table.Last || (table.Last - table.First) % 8 != 0 ||
            (table.End - table.First) % 8 != 0 || !CrtMemory.Range(table.First, table.End - table.First) ||
            !ctx.Process.Heap.Contains(table.First)) return false;
        var size = ctx.Process.Heap.Size(table.First, ctx.Thread.Id);
        return size != ulong.MaxValue && size >= table.End - table.First;
    }

    private static ulong OnExitError(ApiCallContext ctx)
    {
        ctx.Runtime.Trace.Warn($"{ctx.Name}: invalid or inaccessible onexit table at 0x{ctx.Arg(0):X}", ctx.Process.Id, ctx.Thread.Id);
        return uint.MaxValue;
    }
}
