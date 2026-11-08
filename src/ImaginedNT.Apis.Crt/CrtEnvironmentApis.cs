using ImaginedNT.Core.Runtime;

namespace ImaginedNT.Apis.Crt;

public static class CrtEnvironmentApis
{
    private const string Module = "api-ms-win-crt-environment-l1-1-0.dll";

    [PartiallyImaginedApi(Module, "getenv", ApiCategory.Process, "ucrtbase!getenv", "msvcrt!getenv")]
    private static ulong Getenv(ApiCallContext ctx) => Get(ctx, 1);

    [PartiallyImaginedApi(Module, "_wgetenv", ApiCategory.Process, "ucrtbase!_wgetenv", "msvcrt!_wgetenv")]
    private static ulong Wgetenv(ApiCallContext ctx) => Get(ctx, 2);

    [PartiallyImaginedApi(Module, "getenv_s", ApiCategory.Process, "ucrtbase!getenv_s", "msvcrt!getenv_s")]
    private static ulong GetenvSecure(ApiCallContext ctx) => GetSecure(ctx, 1);

    [PartiallyImaginedApi(Module, "_wgetenv_s", ApiCategory.Process, "ucrtbase!_wgetenv_s", "msvcrt!_wgetenv_s")]
    private static ulong WgetenvSecure(ApiCallContext ctx) => GetSecure(ctx, 2);

    [PartiallyImaginedApi(Module, "_putenv_s", ApiCategory.Process, "ucrtbase!_putenv_s", "msvcrt!_putenv_s")]
    private static ulong PutenvSecure(ApiCallContext ctx) => Put(ctx, 1);

    [PartiallyImaginedApi(Module, "_wputenv_s", ApiCategory.Process, "ucrtbase!_wputenv_s", "msvcrt!_wputenv_s")]
    private static ulong WputenvSecure(ApiCallContext ctx) => Put(ctx, 2);

    [PartiallyImaginedApi(Module, "_putenv", ApiCategory.Process, "ucrtbase!_putenv", "msvcrt!_putenv")]
    private static ulong Putenv(ApiCallContext ctx) => PutEntry(ctx, 1);

    [PartiallyImaginedApi(Module, "_wputenv", ApiCategory.Process, "ucrtbase!_wputenv", "msvcrt!_wputenv")]
    private static ulong Wputenv(ApiCallContext ctx) => PutEntry(ctx, 2);

    [PartiallyImaginedApi(Module, "_dupenv_s", ApiCategory.Process, "ucrtbase!_dupenv_s", "msvcrt!_dupenv_s")]
    private static ulong DuplicateEnvironment(ApiCallContext ctx) => Duplicate(ctx, 1);

    [PartiallyImaginedApi(Module, "_wdupenv_s", ApiCategory.Process, "ucrtbase!_wdupenv_s", "msvcrt!_wdupenv_s")]
    private static ulong DuplicateWideEnvironment(ApiCallContext ctx) => Duplicate(ctx, 2);

    [PartiallyImaginedApi(Module, "__p__environ", ApiCategory.Process, "ucrtbase!__p__environ", "msvcrt!__p__environ")]
    private static ulong EnvironmentPointer(ApiCallContext ctx) => CrtProcessState.GetEnvironmentPointer(ctx, 1);

    [PartiallyImaginedApi(Module, "__p__wenviron", ApiCategory.Process, "ucrtbase!__p__wenviron", "msvcrt!__p__wenviron")]
    private static ulong WideEnvironmentPointer(ApiCallContext ctx) => CrtProcessState.GetEnvironmentPointer(ctx, 2);

    private static bool ReadName(ApiCallContext ctx, ulong address, int width, out string name) =>
        CrtMemory.ReadText(ctx, address, width, out name, ansi: true, maxLength: 32766);

    private static ulong Get(ApiCallContext ctx, int width)
    {
        if (!ReadName(ctx, ctx.Arg(0), width, out var name) ||
            !ctx.Process.TryGetEnvironmentVariable(name, out var value)) return 0;
        return CrtProcessState.GetString(ctx, "env:" + name, value, width, out var address) ? address : 0;
    }

    private static ulong GetSecure(ApiCallContext ctx, int width)
    {
        var dest = ctx.Arg(1);
        var capacity = ctx.Arg(2);
        if (!CrtMemory.Write(ctx, ctx.Arg(0), 0UL)) return CrtMemory.EInvalid;
        if ((dest == 0) != (capacity == 0)) return CrtMemory.EInvalid;
        if (dest != 0 && !CrtMemory.WriteChar(ctx, dest, 0, width)) return CrtMemory.EInvalid;
        if (!ReadName(ctx, ctx.Arg(3), width, out var name)) return CrtMemory.EInvalid;
        if (!ctx.Process.TryGetEnvironmentVariable(name, out var value)) return 0;
        var bytes = CrtMemory.Encode(value, width, ansi: true);
        var required = (ulong)bytes.Length / (ulong)width;
        if (!CrtMemory.Write(ctx, ctx.Arg(0), required)) return CrtMemory.EInvalid;
        if (capacity == 0) return 0;
        if (capacity < required) return CrtMemory.ERange;
        return ctx.Memory.TryWrite(dest, bytes) ? 0 : CrtMemory.EInvalid;
    }

    private static ulong Put(ApiCallContext ctx, int width)
    {
        if (!ReadName(ctx, ctx.Arg(0), width, out var name) || name.Length == 0 || name.Contains('=') ||
            !CrtMemory.ReadText(ctx, ctx.Arg(1), width, out var value, ansi: true, maxLength: 32766)) return CrtMemory.EInvalid;
        ctx.Process.SetEnvironmentVariable(name, value.Length == 0 ? null : value);
        CrtProcessState.RefreshEnvironment(ctx);
        return 0;
    }

    private static ulong PutEntry(ApiCallContext ctx, int width)
    {
        if (!ReadName(ctx, ctx.Arg(0), width, out var entry)) return uint.MaxValue;
        var equals = entry.IndexOf('=');
        if (equals <= 0) return uint.MaxValue;
        ctx.Process.SetEnvironmentVariable(entry[..equals], equals == entry.Length - 1 ? null : entry[(equals + 1)..]);
        CrtProcessState.RefreshEnvironment(ctx);
        return 0;
    }

    private static ulong Duplicate(ApiCallContext ctx, int width)
    {
        if (!CrtMemory.Write(ctx, ctx.Arg(0), 0UL)) return CrtMemory.EInvalid;
        if (ctx.Arg(1) != 0 && !CrtMemory.Write(ctx, ctx.Arg(1), 0UL)) return CrtMemory.EInvalid;
        if (!ReadName(ctx, ctx.Arg(2), width, out var name)) return CrtMemory.EInvalid;
        if (!ctx.Process.TryGetEnvironmentVariable(name, out var value)) return 0;
        var bytes = CrtMemory.Encode(value, width, ansi: true);
        var address = ctx.Process.Heap.Allocate((ulong)bytes.Length, threadId: ctx.Thread.Id);
        if (address == 0) return CrtMemory.ENoMemory;
        if (!ctx.Memory.TryWrite(address, bytes) || !CrtMemory.Write(ctx, ctx.Arg(0), address) ||
            (ctx.Arg(1) != 0 && !CrtMemory.Write(ctx, ctx.Arg(1), (ulong)bytes.Length / (ulong)width)))
        {
            CrtMemory.Write(ctx, ctx.Arg(0), 0UL);
            ctx.Process.Heap.Free(address, ctx.Thread.Id);
            return CrtMemory.EInvalid;
        }
        return 0;
    }
}
