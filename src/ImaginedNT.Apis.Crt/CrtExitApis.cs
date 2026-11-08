using ImaginedNT.Core.Runtime;

namespace ImaginedNT.Apis.Crt;

public static partial class CrtExitApis
{
    private const string Module = "api-ms-win-crt-runtime-l1-1-0.dll";

    [PartiallyImaginedApi(Module, "_register_thread_local_exe_atexit_callback", ApiCategory.Process,
        "ucrtbase!_register_thread_local_exe_atexit_callback")]
    private static ulong RegisterThreadLocalExit(ApiCallContext ctx) => CrtProcessState.RegisterThreadLocalExitCallback(ctx);

    [PartiallyImaginedApi(Module, "_crt_atexit", ApiCategory.Process, "ucrtbase!_crt_atexit", "msvcrt!atexit",
        "api-ms-win-crt-private-l1-1-0!_o__crt_atexit", "ucrtbase!_o__crt_atexit")]
    private static ulong RegisterExit(ApiCallContext ctx) => CrtProcessState.RegisterExitCallback(ctx, false);

    [PartiallyImaginedApi(Module, "_crt_at_quick_exit", ApiCategory.Process, "ucrtbase!_crt_at_quick_exit")]
    private static ulong RegisterQuickExit(ApiCallContext ctx) => CrtProcessState.RegisterExitCallback(ctx, true);

    [PartiallyImaginedApi(Module, "exit", ApiCategory.Process, "ucrtbase!exit", "msvcrt!exit",
        "api-ms-win-crt-private-l1-1-0!_o_exit", "ucrtbase!_o_exit")]
    [PartiallyImaginedApi(Module, "_exit", ApiCategory.Process, "ucrtbase!_exit", "msvcrt!_exit",
        "api-ms-win-crt-private-l1-1-0!_o__exit", "ucrtbase!_o__exit")]
    [PartiallyImaginedApi(Module, "quick_exit", ApiCategory.Process, "ucrtbase!quick_exit")]
    private static ulong Exit(ApiCallContext ctx)
    {
        var name = ctx.Name.StartsWith("_o_", StringComparison.Ordinal) ? ctx.Name[3..] : ctx.Name;
        if (name == "exit") CrtProcessState.RunExitCallbacks(ctx, false);
        else if (name == "quick_exit") CrtProcessState.RunExitCallbacks(ctx, true);
        return unchecked((uint)ctx.Process.RequestProcessExit(ctx.ArgInt32(0), ctx));
    }

    [PartiallyImaginedApi(Module, "_cexit", ApiCategory.Process, "ucrtbase!_cexit", "msvcrt!_cexit",
        "api-ms-win-crt-private-l1-1-0!_o__cexit", "ucrtbase!_o__cexit")]
    private static ulong Cleanup(ApiCallContext ctx)
    {
        CrtProcessState.RunExitCallbacks(ctx, false);
        return 0;
    }

    [PartiallyImaginedApi(Module, "_c_exit", ApiCategory.Process, "ucrtbase!_c_exit", "msvcrt!_c_exit")]
    private static ulong QuickCleanup(ApiCallContext ctx) => 0;
}

internal sealed partial class CrtProcessState
{
    private readonly object _exitCallbacksGate = new();
    private readonly List<ulong> _exitCallbacks = new();
    private readonly List<ulong> _quickExitCallbacks = new();
    private ulong _threadLocalExitCallback;

    internal static ulong RegisterThreadLocalExitCallback(ApiCallContext ctx)
    {
        var state = States.GetValue(ctx.Process, static _ => new CrtProcessState());
        lock (state._exitCallbacksGate)
        {
            if (state._threadLocalExitCallback != 0)
            {
                ctx.Runtime.Trace.Warn($"{ctx.Name}: thread-local exit callback already registered", ctx.Process.Id, ctx.Thread.Id);
                return ctx.FailFast(7);
            }
            state._threadLocalExitCallback = ctx.Arg(0);
            return 0;
        }
    }

    internal static ulong RegisterExitCallback(ApiCallContext ctx, bool quick)
    {
        try
        {
            var state = States.GetValue(ctx.Process, static _ => new CrtProcessState());
            lock (state._exitCallbacksGate)
                (quick ? state._quickExitCallbacks : state._exitCallbacks).Add(ctx.Arg(0));
            return 0;
        }
        catch (OutOfMemoryException ex)
        {
            ctx.Runtime.Trace.Warn($"{ctx.Name}: cannot register exit callback: {ex.Message}", ctx.Process.Id, ctx.Thread.Id);
            return uint.MaxValue;
        }
    }

    internal static unsafe void RunExitCallbacks(ApiCallContext ctx, bool quick)
    {
        if (!States.TryGetValue(ctx.Process, out var state)) return;
        lock (state._exitCallbacksGate)
        {
            if (!quick && state._threadLocalExitCallback != 0)
            {
                if (ctx.Thread.IsManagedHosted) ctx.Thread.InvokeGuest(state._threadLocalExitCallback, [0, 0, 0]);
                else ((delegate* unmanaged<nint, uint, nint, void>)(nint)state._threadLocalExitCallback)(0, 0, 0);
            }
            var callbacks = quick ? state._quickExitCallbacks : state._exitCallbacks;
            while (callbacks.Count != 0)
            {
                var fn = callbacks[^1];
                callbacks.RemoveAt(callbacks.Count - 1);
                if (fn == 0) continue;
                if (ctx.Thread.IsManagedHosted) ctx.Thread.InvokeGuest(fn, []);
                else ((delegate* unmanaged<void>)(nint)fn)();
            }
        }
    }
}
