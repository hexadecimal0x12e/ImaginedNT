using ImaginedNT.Core.Runtime;

namespace ImaginedNT.Apis.Crt;

public static class CrtRuntimeApis
{
    private const string Module = "api-ms-win-crt-runtime-l1-1-0.dll";

    [PartiallyImaginedApi(Module, "_initterm", ApiCategory.Process, "ucrtbase!_initterm")]
    private static ulong InitTerm(ApiCallContext ctx) => RunInitializers(ctx, false);

    [PartiallyImaginedApi(Module, "_initterm_e", ApiCategory.Process, "ucrtbase!_initterm_e")]
    private static ulong InitTermE(ApiCallContext ctx) => RunInitializers(ctx, true);

    private static unsafe ulong RunInitializers(ApiCallContext ctx, bool checkResult)
    {
        var start = ctx.Arg(0);
        var end = ctx.Arg(1);
        if (start >= end) return 0;
        if (!CrtMemory.Range(start, end - start) || (end - start) % 8 != 0)
        {
            ctx.Runtime.Trace.Warn($"{ctx.Name}: invalid initializer table 0x{start:X}..0x{end:X}", ctx.Process.Id, ctx.Thread.Id);
            return CrtMemory.EInvalid;
        }
        for (var p = start; p < end; p += 8)
        {
            if (!CrtMemory.Read(ctx, p, out ulong fn))
            {
                ctx.Runtime.Trace.Warn($"{ctx.Name}: cannot read initializer at 0x{p:X}", ctx.Process.Id, ctx.Thread.Id);
                return CrtMemory.EInvalid;
            }
            if (fn == 0) continue;
            uint result = 0;
            if (ctx.Thread.IsManagedHosted) result = (uint)ctx.Thread.InvokeGuest(fn, []);
            else if (checkResult) result = ((delegate* unmanaged<uint>)(nint)fn)();
            else ((delegate* unmanaged<void>)(nint)fn)();
            if (!checkResult || result == 0) continue;
            ctx.Runtime.Trace.Warn($"{ctx.Name}: initializer at 0x{fn:X} returned {unchecked((int)result)}", ctx.Process.Id, ctx.Thread.Id);
            return result;
        }
        return 0;
    }

    [PartiallyImaginedApi(Module, "abort", ApiCategory.Process, "ucrtbase!abort", "msvcrt!abort", "api-ms-win-crt-private-l1-1-0!_o_abort")]
    [PartiallyImaginedApi(Module, "terminate", ApiCategory.Process, "ucrtbase!terminate", "msvcrt!terminate", "api-ms-win-crt-private-l1-1-0!_o_terminate")]
    [PartiallyImaginedApi(Module, "_invoke_watson", ApiCategory.Process, "ucrtbase!_invoke_watson", "msvcrt!_invoke_watson", "api-ms-win-crt-private-l1-1-0!_o__invoke_watson")]
    [PartiallyImaginedApi(Module, "_invalid_parameter_noinfo", ApiCategory.Process, "ucrtbase!_invalid_parameter_noinfo", "msvcrt!_invalid_parameter_noinfo",
        "api-ms-win-crt-private-l1-1-0!_o__invalid_parameter_noinfo", "ucrtbase!_o__invalid_parameter_noinfo")]
    [PartiallyImaginedApi(Module, "_invalid_parameter_noinfo_noreturn", ApiCategory.Process, "ucrtbase!_invalid_parameter_noinfo_noreturn", "msvcrt!_invalid_parameter_noinfo_noreturn", "api-ms-win-crt-private-l1-1-0!_o__invalid_parameter_noinfo_noreturn")]
    private static ulong FailFast(ApiCallContext ctx) => ctx.FailFast(ctx.Name.Contains("invalid_parameter", StringComparison.Ordinal) ||
        ctx.Name.Contains("invoke_watson", StringComparison.Ordinal) ? 5u : 7u);

    [PartiallyImaginedApi(Module, "_set_invalid_parameter_handler", ApiCategory.Process,
        "ucrtbase!_set_invalid_parameter_handler", "msvcrt!_set_invalid_parameter_handler")]
    [PartiallyImaginedApi(Module, "_set_thread_local_invalid_parameter_handler", ApiCategory.Process,
        "ucrtbase!_set_thread_local_invalid_parameter_handler", "msvcrt!_set_thread_local_invalid_parameter_handler")]
    private static ulong SetInvalidParameterHandler(ApiCallContext ctx) => 0;

    [PartiallyImaginedApi(Module, "_get_invalid_parameter_handler", ApiCategory.Process,
        "ucrtbase!_get_invalid_parameter_handler", "msvcrt!_get_invalid_parameter_handler")]
    [PartiallyImaginedApi(Module, "_get_thread_local_invalid_parameter_handler", ApiCategory.Process,
        "ucrtbase!_get_thread_local_invalid_parameter_handler", "msvcrt!_get_thread_local_invalid_parameter_handler")]
    private static ulong GetInvalidParameterHandler(ApiCallContext ctx) => 0;

    [PartiallyImaginedApi(Module, "__threadhandle", ApiCategory.Thread, "ucrtbase!__threadhandle")]
    private static ulong ThreadHandle(ApiCallContext ctx) => unchecked((ulong)-2);

    [PartiallyImaginedApi(Module, "__threadid", ApiCategory.Thread, "ucrtbase!__threadid")]
    private static ulong ThreadId(ApiCallContext ctx) => (uint)ctx.Thread.Id;

    [PartiallyImaginedApi(Module, "_getpid", ApiCategory.Process, "ucrtbase!_getpid")]
    private static ulong GetPid(ApiCallContext ctx) => (uint)ctx.Process.Id;

    [PartiallyImaginedApi(Module, "_get_pgmptr", ApiCategory.Process, "ucrtbase!_get_pgmptr")]
    private static ulong GetProgramPointer(ApiCallContext ctx) => GetProgramPointer(ctx, 1);

    [PartiallyImaginedApi(Module, "_get_wpgmptr", ApiCategory.Process, "ucrtbase!_get_wpgmptr")]
    private static ulong GetWideProgramPointer(ApiCallContext ctx) => GetProgramPointer(ctx, 2);

    private static ulong GetProgramPointer(ApiCallContext ctx, int width)
    {
        if (!CrtMemory.Write(ctx, ctx.Arg(0), 0UL)) return CrtMemory.EInvalid;
        if (!CrtProcessState.GetString(ctx, "program", ctx.Process.ImagePath, width, out var address)) return CrtMemory.ENoMemory;
        return CrtMemory.Write(ctx, ctx.Arg(0), address) ? 0 : CrtMemory.EInvalid;
    }

    [PartiallyImaginedApi(Module, "_sleep", ApiCategory.Thread, "ucrtbase!_sleep")]
    private static ulong Sleep(ApiCallContext ctx)
    {
        var remaining = ctx.Arg32(0);
        if (remaining == 0) remaining = 1;
        if (remaining == uint.MaxValue)
        {
            Thread.Sleep(Timeout.Infinite);
            return 0;
        }
        do
        {
            var n = (int)Math.Min(remaining, int.MaxValue);
            Thread.Sleep(n);
            remaining -= (uint)n;
        } while (remaining != 0);
        return 0;
    }
}
