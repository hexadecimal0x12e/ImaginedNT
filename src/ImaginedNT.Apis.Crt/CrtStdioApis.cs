using ImaginedNT.Core.Runtime;

namespace ImaginedNT.Apis.Crt;

public static partial class CrtStdioApis
{
    private const string Module = "api-ms-win-crt-stdio-l1-1-0.dll";

    [PartiallyImaginedApi(Module, "__acrt_iob_func", ApiCategory.File, "ucrtbase!__acrt_iob_func",
        "api-ms-win-crt-private-l1-1-0!_o___acrt_iob_func", "ucrtbase!_o___acrt_iob_func")]
    private static ulong GetStandardStream(ApiCallContext ctx)
    {
        var id = (uint)ctx.Arg(0);
        if (id < 3) return CrtStdioState.Get(ctx).StandardStream(ctx, (int)id);
        ctx.Runtime.Trace.Warn($"{ctx.Name}: invalid standard stream index {id}", ctx.Process.Id, ctx.Thread.Id);
        return 0;
    }

    [PartiallyImaginedApi(Module, "_get_stream_buffer_pointers", ApiCategory.File, "ucrtbase!_get_stream_buffer_pointers",
        "api-ms-win-crt-private-l1-1-0!_o__get_stream_buffer_pointers", "ucrtbase!_o__get_stream_buffer_pointers")]
    private static ulong GetStreamBufferPointers(ApiCallContext ctx) => CrtStdioState.Get(ctx).Invoke(ctx);

    [PartiallyImaginedApi(Module, "_set_fmode", ApiCategory.File, "ucrtbase!_set_fmode",
        "api-ms-win-crt-private-l1-1-0!_o__set_fmode", "ucrtbase!_o__set_fmode")]
    private static ulong SetFileMode(ApiCallContext ctx)
    {
        var mode = ctx.ArgInt32(0);
        if (mode is not (0x4000 or 0x8000 or 0x10000))
        {
            ctx.Runtime.Trace.Warn($"{ctx.Name}: invalid file mode 0x{mode:X}", ctx.Process.Id, ctx.Thread.Id);
            return CrtMemory.EInvalid;
        }
        var p = GetFileModePointer(ctx);
        if (p == 0) return CrtMemory.ENoMemory;
        if (CrtMemory.Write(ctx, p, mode)) return 0;
        ctx.Runtime.Trace.Warn($"{ctx.Name}: cannot write file mode at 0x{p:X}", ctx.Process.Id, ctx.Thread.Id);
        return CrtMemory.EInvalid;
    }

    [PartiallyImaginedApi(Module, "_get_fmode", ApiCategory.File, "ucrtbase!_get_fmode",
        "api-ms-win-crt-private-l1-1-0!_o__get_fmode", "ucrtbase!_o__get_fmode")]
    private static ulong GetFileMode(ApiCallContext ctx)
    {
        if (ctx.Arg(0) == 0) return CrtMemory.EInvalid;
        var p = GetFileModePointer(ctx);
        if (p == 0) return CrtMemory.ENoMemory;
        return CrtMemory.Read(ctx, p, out int mode) && CrtMemory.Write(ctx, ctx.Arg(0), mode) ? 0 : CrtMemory.EInvalid;
    }

    [PartiallyImaginedApi(Module, "__p__fmode", ApiCategory.File, "ucrtbase!__p__fmode",
        "api-ms-win-crt-private-l1-1-0!_o___p__fmode", "ucrtbase!_o___p__fmode")]
    private static ulong GetFileModePointer(ApiCallContext ctx)
    {
        var p = CrtProcessState.GetFileModes(ctx);
        return p == 0 ? 0 : p + (ctx.Name.StartsWith("_o_", StringComparison.Ordinal) ? 4UL : 0);
    }

    [PartiallyImaginedApi(Module, "__p__commode", ApiCategory.File, "ucrtbase!__p__commode",
        "api-ms-win-crt-private-l1-1-0!_o___p__commode", "ucrtbase!_o___p__commode")]
    private static ulong GetCommitModePointer(ApiCallContext ctx)
    {
        var p = CrtProcessState.GetFileModes(ctx);
        return p == 0 ? 0 : p + 8;
    }
}
