using ImaginedNT.Core.Runtime;

namespace ImaginedNT.Apis.Crt;

internal sealed partial class CrtProcessState
{
    private ulong _newHandler, _pureCallHandler;
    private int _newMode;

    internal static int GetNewMode(ApiCallContext ctx)
    {
        var state = States.GetValue(ctx.Process, static _ => new CrtProcessState());
        lock (state._gate) return state._newMode;
    }

    internal static int SetNewMode(ApiCallContext ctx, int mode)
    {
        var state = States.GetValue(ctx.Process, static _ => new CrtProcessState());
        lock (state._gate)
        {
            var previous = state._newMode;
            state._newMode = mode;
            return previous;
        }
    }

    internal static ulong GetNewHandler(ApiCallContext ctx)
    {
        var state = States.GetValue(ctx.Process, static _ => new CrtProcessState());
        lock (state._gate) return state._newHandler;
    }

    internal static ulong SetNewHandler(ApiCallContext ctx)
    {
        var state = States.GetValue(ctx.Process, static _ => new CrtProcessState());
        lock (state._gate)
        {
            var previous = state._newHandler;
            state._newHandler = ctx.Arg(0);
            return previous;
        }
    }

    internal static ulong GetPureCallHandler(ApiCallContext ctx)
    {
        var state = States.GetValue(ctx.Process, static _ => new CrtProcessState());
        lock (state._gate) return state._pureCallHandler;
    }

    internal static ulong SetPureCallHandler(ApiCallContext ctx)
    {
        var state = States.GetValue(ctx.Process, static _ => new CrtProcessState());
        lock (state._gate)
        {
            var previous = state._pureCallHandler;
            state._pureCallHandler = ctx.Arg(0);
            return previous;
        }
    }
}
