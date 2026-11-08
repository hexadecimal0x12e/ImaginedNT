using ImaginedNT.Core.Runtime;

namespace ImaginedNT.Apis.Crt;

public static class CrtExceptionApis
{
    private const string Module = "api-ms-win-crt-private-l1-1-0.dll";

    [PartiallyImaginedApi("api-ms-win-crt-runtime-l1-1-0.dll", "_seh_filter_exe", ApiCategory.Process,
        "ucrtbase!_seh_filter_exe", "api-ms-win-crt-private-l1-1-0!_o__seh_filter_exe", "ucrtbase!_o__seh_filter_exe")]
    private static ulong FilterExeException(ApiCallContext ctx)
    {
        ctx.Runtime.Trace.Warn($"{ctx.Name}: exception 0x{ctx.Arg32(0):X8}, exception pointers=0x{ctx.Arg(1):X}; continuing SEH search",
            ctx.Process.Id, ctx.Thread.Id);
        // With the default signal disposition, UCRT leaves the exception to the next SEH handler.
        return 0;
    }

    [PartiallyImaginedApi(Module, "_o__purecall", ApiCategory.Process, "ucrtbase!_o__purecall",
        "ucrtbase!_purecall", "vcruntime140!_purecall")]
    private static ulong PureCall(ApiCallContext ctx)
    {
        var handler = CrtProcessState.GetPureCallHandler(ctx);
        if (handler != 0) CrtSearchApis.Call(ctx, handler, []);
        return ctx.FailFast(7);
    }

    [PartiallyImaginedApi(Module, "_o__set_purecall_handler", ApiCategory.Process, "ucrtbase!_o__set_purecall_handler",
        "ucrtbase!_set_purecall_handler", "vcruntime140!_set_purecall_handler")]
    private static ulong SetPureCallHandler(ApiCallContext ctx) => CrtProcessState.SetPureCallHandler(ctx);

    [PartiallyImaginedApi(Module, "_o__get_purecall_handler", ApiCategory.Process, "ucrtbase!_o__get_purecall_handler",
        "ucrtbase!_get_purecall_handler", "vcruntime140!_get_purecall_handler")]
    private static ulong GetPureCallHandler(ApiCallContext ctx) => CrtProcessState.GetPureCallHandler(ctx);

    [PartiallyImaginedApi(Module, "_o___std_exception_copy", ApiCategory.Memory, "ucrtbase!_o___std_exception_copy",
        "ucrtbase!__std_exception_copy", "vcruntime140!__std_exception_copy")]
    private static ulong CopyException(ApiCallContext ctx)
    {
        // x64 __std_exception_data: message pointer followed by an ownership byte.
        if (!CrtMemory.Read(ctx, ctx.Arg(0), out ulong message) ||
            !CrtMemory.Read(ctx, ctx.Arg(0) + 8, out byte owned)) return ctx.FailFast(5);
        var copy = message;
        if (owned != 0 && message != 0)
        {
            if (!CrtMemory.Length(ctx, message, 1, ulong.MaxValue, out var length)) return ctx.FailFast(5);
            copy = ctx.Process.Heap.Allocate(length + 1, false, ctx.Thread.Id);
            if (copy == 0) return ctx.FailFast(7);
            if (!CrtMemory.Copy(ctx, copy, message, length + 1))
            {
                ctx.Process.Heap.Free(copy, ctx.Thread.Id);
                return ctx.FailFast(5);
            }
        }
        if (CrtMemory.Write(ctx, ctx.Arg(1), copy) &&
            CrtMemory.Write(ctx, ctx.Arg(1) + 8, (byte)(owned != 0 && message != 0 ? 1 : 0))) return 0;
        if (copy != message) ctx.Process.Heap.Free(copy, ctx.Thread.Id);
        return ctx.FailFast(5);
    }

    [PartiallyImaginedApi(Module, "_o___std_exception_destroy", ApiCategory.Memory, "ucrtbase!_o___std_exception_destroy",
        "ucrtbase!__std_exception_destroy", "vcruntime140!__std_exception_destroy")]
    private static ulong DestroyException(ApiCallContext ctx)
    {
        if (!CrtMemory.Read(ctx, ctx.Arg(0), out ulong message) ||
            !CrtMemory.Read(ctx, ctx.Arg(0) + 8, out byte owned)) return ctx.FailFast(5);
        if (owned != 0 && message != 0) ctx.Process.Heap.Free(message, ctx.Thread.Id);
        return CrtMemory.Write(ctx, ctx.Arg(0), 0UL) && CrtMemory.Write(ctx, ctx.Arg(0) + 8, (byte)0)
            ? 0 : ctx.FailFast(5);
    }
}
