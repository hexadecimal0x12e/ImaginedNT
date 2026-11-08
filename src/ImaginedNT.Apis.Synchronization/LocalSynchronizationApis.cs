using System.Runtime.InteropServices;
using ImaginedNT.Core.Nt;
using static ImaginedNT.Apis.ApiHelpers;

namespace ImaginedNT.Core.Runtime;

public static class LocalSynchronizationApis
{
    [VirtualApi("kernel32.dll", "InitializeCriticalSectionEx", ApiCategory.Synchronization)]
    private static ulong InitializeCriticalSectionEx(ApiCallContext ctx) =>
        BoolResult(ctx, WindowsSynchronization.InitializeCriticalSectionEx(ctx.HandleArg(0), ctx.Arg32(1), ctx.Arg32(2)),
            Marshal.GetLastPInvokeError());

    [VirtualApi("kernel32.dll", "InitializeCriticalSection", ApiCategory.Synchronization)]
    private static ulong InitializeCriticalSection(ApiCallContext ctx)
    {
        WindowsSynchronization.InitializeCriticalSection(ctx.HandleArg(0));
        return 0;
    }

    [VirtualApi("kernel32.dll", "EnterCriticalSection", ApiCategory.Synchronization)]
    private static ulong EnterCriticalSection(ApiCallContext ctx)
    {
        WindowsSynchronization.EnterCriticalSection(ctx.HandleArg(0));
        return 0;
    }

    [VirtualApi("kernel32.dll", "LeaveCriticalSection", ApiCategory.Synchronization)]
    private static ulong LeaveCriticalSection(ApiCallContext ctx)
    {
        WindowsSynchronization.LeaveCriticalSection(ctx.HandleArg(0));
        return 0;
    }

    [VirtualApi("kernel32.dll", "DeleteCriticalSection", ApiCategory.Synchronization)]
    private static ulong DeleteCriticalSection(ApiCallContext ctx)
    {
        WindowsSynchronization.DeleteCriticalSection(ctx.HandleArg(0));
        return 0;
    }

    [VirtualApi("kernel32.dll", "TryEnterCriticalSection", ApiCategory.Synchronization)]
    private static ulong TryEnterCriticalSection(ApiCallContext ctx) =>
        WindowsSynchronization.TryEnterCriticalSection(ctx.HandleArg(0)) ? 1UL : 0UL;

    [VirtualApi("kernel32.dll", "InitializeSListHead", ApiCategory.Synchronization)]
    private static ulong InitializeSListHead(ApiCallContext ctx)
    {
        WindowsSynchronization.InitializeSListHead(ctx.HandleArg(0));
        return 0;
    }

    [VirtualApi("kernel32.dll", "InitOnceExecuteOnce", ApiCategory.Synchronization)]
    private static unsafe ulong InitOnceExecuteOnce(ApiCallContext ctx)
    {
        if (ctx.Arg(0) == 0 || ctx.Arg(1) == 0)
            return BoolResult(ctx, false, Win32Error.InvalidParameter);
        if (!WindowsSynchronization.InitOnceBeginInitialize(ctx.HandleArg(0), 0, out var pending, out var context))
            return BoolResult(ctx, false, Marshal.GetLastPInvokeError());

        if (pending)
        {
            var completed = false;
            try
            {
                context = 0;
                var result = ctx.Thread.IsManagedHosted
                    ? ctx.Thread.InvokeGuest(ctx.Arg(1), [ctx.Arg(0), ctx.Arg(2), (ulong)&context])
                    : unchecked((ulong)(uint)((delegate* unmanaged<ulong, ulong, ulong*, int>)(nint)ctx.Arg(1))(ctx.Arg(0), ctx.Arg(2), &context));
                if ((uint)result == 0) return 0;
                if (!WindowsSynchronization.InitOnceComplete(ctx.HandleArg(0), 0, context))
                    return BoolResult(ctx, false, Marshal.GetLastPInvokeError());
                completed = true;
            }
            finally
            {
                if (!completed)
                {
                    var error = ctx.LastError;
                    WindowsSynchronization.InitOnceComplete(ctx.HandleArg(0), 4, 0); // INIT_ONCE_INIT_FAILED
                    ctx.SetLastError(error);
                }
            }
        }

        if (ctx.Arg(3) != 0) ctx.Memory.Write(ctx.Arg(3), context);
        return 1;
    }

    [VirtualApi("kernel32.dll", "FlsAlloc", ApiCategory.Thread)]
    private static ulong FlsAlloc(ApiCallContext ctx)
    {
        var index = ctx.Process.Fls.Allocate(ctx.Arg(0));
        if (index == uint.MaxValue)
        {
            ctx.Runtime.Trace.Warn("FlsAlloc: no free FLS indices", ctx.Process.Id, ctx.Thread.Id);
            ctx.SetLastError(Win32Error.NotEnoughMemory);
        }
        return index;
    }

    [VirtualApi("kernel32.dll", "FlsSetValue", ApiCategory.Thread)]
    private static ulong FlsSetValue(ApiCallContext ctx) =>
        BoolResult(ctx, ctx.Process.Fls.Set(ctx.Arg32(0), ctx.Thread, ctx.Arg(1)), Win32Error.InvalidParameter);

    [VirtualApi("kernel32.dll", "FlsGetValue", ApiCategory.Thread)]
    private static ulong FlsGetValue(ApiCallContext ctx)
    {
        var valid = ctx.Process.Fls.TryGet(ctx.Arg32(0), ctx.Thread, out var value);
        ctx.SetLastError(valid ? Win32Error.Success : Win32Error.InvalidParameter);
        return value;
    }

    [VirtualApi("kernel32.dll", "FlsFree", ApiCategory.Thread)]
    private static ulong FlsFree(ApiCallContext ctx) =>
        BoolResult(ctx, ctx.Process.Fls.Free(ctx.Arg32(0), ctx.Thread), Win32Error.InvalidParameter);
}
