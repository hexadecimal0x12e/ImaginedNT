using ImaginedNT.Core.Runtime;

namespace ImaginedNT.Apis.Crt;

public static partial class CrtHeapApis
{
    private const string Module = "api-ms-win-crt-heap-l1-1-0.dll";
    private const ulong MaxSize = ulong.MaxValue - 31;

    [PartiallyImaginedApi(Module, "_set_new_mode", ApiCategory.Memory, "ucrtbase!_set_new_mode",
        "api-ms-win-crt-private-l1-1-0!_o__set_new_mode", "ucrtbase!_o__set_new_mode")]
    private static ulong SetNewMode(ApiCallContext ctx)
    {
        var mode = unchecked((int)ctx.Arg32(0));
        if (mode is 0 or 1) return (ulong)CrtProcessState.SetNewMode(ctx, mode);
        ctx.Runtime.Trace.Warn($"{ctx.Name}: invalid new handler mode {mode}", ctx.Process.Id, ctx.Thread.Id);
        CrtLocaleData.SetErrno(22);
        return ctx.FailFast(5);
    }

    [PartiallyImaginedApi(Module, "_query_new_mode", ApiCategory.Memory, "ucrtbase!_query_new_mode",
        "api-ms-win-crt-private-l1-1-0!_o__query_new_mode", "ucrtbase!_o__query_new_mode")]
    private static ulong QueryNewMode(ApiCallContext ctx) => (ulong)CrtProcessState.GetNewMode(ctx);

    [PartiallyImaginedApi(Module, "_callnewh", ApiCategory.Memory, "ucrtbase!_callnewh",
        "api-ms-win-crt-private-l1-1-0!_o__callnewh", "ucrtbase!_o__callnewh")]
    private static ulong CallNewHandler(ApiCallContext ctx) => InvokeNewHandler(ctx, ctx.Arg(0)) ? 1UL : 0UL;

    private static bool InvokeNewHandler(ApiCallContext ctx, ulong size)
    {
        var handler = CrtProcessState.GetNewHandler(ctx);
        return handler != 0 && (uint)CrtSearchApis.Call(ctx, handler, [size]) != 0;
    }

    [PartiallyImaginedApi(Module, "_set_new_handler", ApiCategory.Memory, "ucrtbase!_set_new_handler",
        "api-ms-win-crt-private-l1-1-0!_o__set_new_handler", "ucrtbase!_o__set_new_handler")]
    private static ulong SetNewHandler(ApiCallContext ctx) => CrtProcessState.SetNewHandler(ctx);

    [PartiallyImaginedApi(Module, "_query_new_handler", ApiCategory.Memory, "ucrtbase!_query_new_handler",
        "api-ms-win-crt-private-l1-1-0!_o__query_new_handler", "ucrtbase!_o__query_new_handler")]
    private static ulong QueryNewHandler(ApiCallContext ctx) => CrtProcessState.GetNewHandler(ctx);

    [PartiallyImaginedApi(Module, "malloc", ApiCategory.Memory, "ucrtbase!malloc",
        "api-ms-win-crt-private-l1-1-0!_o_malloc", "ucrtbase!_o_malloc")]
    [PartiallyImaginedApi(Module, "_malloc_base", ApiCategory.Memory, "ucrtbase!_malloc_base",
        "api-ms-win-crt-private-l1-1-0!_o__malloc_base", "ucrtbase!_o__malloc_base")]
    private static ulong Malloc(ApiCallContext ctx) => Allocate(ctx, ctx.Arg(0));

    [PartiallyImaginedApi(Module, "calloc", ApiCategory.Memory, "ucrtbase!calloc",
        "api-ms-win-crt-private-l1-1-0!_o_calloc", "ucrtbase!_o_calloc")]
    [PartiallyImaginedApi(Module, "_calloc_base", ApiCategory.Memory, "ucrtbase!_calloc_base",
        "api-ms-win-crt-private-l1-1-0!_o__calloc_base", "ucrtbase!_o__calloc_base")]
    private static ulong Calloc(ApiCallContext ctx) =>
        Product(ctx, ctx.Arg(0), ctx.Arg(1), out var size) ? Allocate(ctx, size, true) : 0;

    [PartiallyImaginedApi(Module, "free", ApiCategory.Memory, "ucrtbase!free",
        "api-ms-win-crt-private-l1-1-0!_o_free", "ucrtbase!_o_free")]
    [PartiallyImaginedApi(Module, "_free_base", ApiCategory.Memory, "ucrtbase!_free_base",
        "api-ms-win-crt-private-l1-1-0!_o__free_base", "ucrtbase!_o__free_base")]
    private static ulong Free(ApiCallContext ctx)
    {
        if (!ctx.Process.Heap.Free(ctx.Arg(0), ctx.Thread.Id)) CrtLocaleData.SetErrno(22);
        return 0;
    }

    [PartiallyImaginedApi(Module, "realloc", ApiCategory.Memory, "ucrtbase!realloc",
        "api-ms-win-crt-private-l1-1-0!_o_realloc", "ucrtbase!_o_realloc")]
    [PartiallyImaginedApi(Module, "_realloc_base", ApiCategory.Memory, "ucrtbase!_realloc_base",
        "api-ms-win-crt-private-l1-1-0!_o__realloc_base", "ucrtbase!_o__realloc_base")]
    private static ulong Realloc(ApiCallContext ctx) => Resize(ctx, ctx.Arg(0), ctx.Arg(1), false);

    [PartiallyImaginedApi(Module, "_recalloc", ApiCategory.Memory, "ucrtbase!_recalloc",
        "api-ms-win-crt-private-l1-1-0!_o__recalloc", "ucrtbase!_o__recalloc")]
    private static ulong Recalloc(ApiCallContext ctx) =>
        Product(ctx, ctx.Arg(1), ctx.Arg(2), out var size) ? Resize(ctx, ctx.Arg(0), size, true) : 0;

    [PartiallyImaginedApi(Module, "_msize", ApiCategory.Memory, "ucrtbase!_msize",
        "api-ms-win-crt-private-l1-1-0!_o__msize", "ucrtbase!_o__msize")]
    private static ulong Size(ApiCallContext ctx)
    {
        var size = ctx.Process.Heap.Size(ctx.Arg(0), ctx.Thread.Id);
        if (size == ulong.MaxValue) CrtLocaleData.SetErrno(22);
        return size;
    }

    [PartiallyImaginedApi(Module, "_expand", ApiCategory.Memory, "ucrtbase!_expand",
        "api-ms-win-crt-private-l1-1-0!_o__expand", "ucrtbase!_o__expand")]
    private static ulong Expand(ApiCallContext ctx)
    {
        if (!ValidSize(ctx, ctx.Arg(1))) return 0;
        if (!ctx.Process.Heap.Contains(ctx.Arg(0)))
        {
            CrtLocaleData.SetErrno(22);
            return 0;
        }
        var result = ctx.Process.Heap.Reallocate(ctx.Arg(0), ctx.Arg(1), inPlace: true, threadId: ctx.Thread.Id);
        if (result == 0) CrtLocaleData.SetErrno(12);
        return result;
    }

    private static ulong Allocate(ApiCallContext ctx, ulong size, bool zero = false)
    {
        if (!ValidSize(ctx, size)) return 0;
        do
        {
            var address = ctx.Process.Heap.Allocate(size, zero, ctx.Thread.Id);
            if (address != 0) return address;
        } while (CrtProcessState.GetNewMode(ctx) != 0 && InvokeNewHandler(ctx, size));
        CrtLocaleData.SetErrno(12);
        return 0;
    }

    private static ulong Resize(ApiCallContext ctx, ulong address, ulong size, bool zero)
    {
        if (!ValidSize(ctx, size)) return 0;
        if (address == 0) return Allocate(ctx, size, zero);
        if (size == 0)
        {
            if (!ctx.Process.Heap.Free(address, ctx.Thread.Id)) CrtLocaleData.SetErrno(22);
            return 0;
        }
        do
        {
            var result = ctx.Process.Heap.Reallocate(address, size, zero, threadId: ctx.Thread.Id);
            if (result != 0) return result;
            if (!ctx.Process.Heap.Contains(address))
            {
                CrtLocaleData.SetErrno(22);
                return 0;
            }
        } while (CrtProcessState.GetNewMode(ctx) != 0 && InvokeNewHandler(ctx, size));
        CrtLocaleData.SetErrno(12);
        return 0;
    }

    private static bool ValidSize(ApiCallContext ctx, ulong size)
    {
        if (size <= MaxSize) return true;
        ctx.Runtime.Trace.Warn($"{ctx.Name}: allocation size 0x{size:X} exceeds CRT limit", ctx.Process.Id, ctx.Thread.Id);
        CrtLocaleData.SetErrno(12);
        return false;
    }

    private static bool Product(ApiCallContext ctx, ulong count, ulong size, out ulong result)
    {
        result = 0;
        if (count != 0 && size > MaxSize / count)
        {
            ctx.Runtime.Trace.Warn($"{ctx.Name}: allocation size overflow ({count} * {size})", ctx.Process.Id, ctx.Thread.Id);
            CrtLocaleData.SetErrno(12);
            return false;
        }
        result = count * size;
        return true;
    }
}
