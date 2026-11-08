using System.Text;
using ImaginedNT.Core.Runtime;

namespace ImaginedNT.Apis.Crt;

public static class CrtFilesystemApis
{
    private const string Module = "api-ms-win-crt-filesystem-l1-1-0.dll";

    [PartiallyImaginedApi(Module, "_getdrive", ApiCategory.File, "ucrtbase!_getdrive")]
    private static ulong GetDrive(ApiCallContext ctx) => 3;

    [PartiallyImaginedApi(Module, "_getdrives", ApiCategory.File, "ucrtbase!_getdrives")]
    private static ulong GetDrives(ApiCallContext ctx) => 4;

    [PartiallyImaginedApi(Module, "_makepath", ApiCategory.File, "ucrtbase!_makepath")]
    private static ulong MakePath(ApiCallContext ctx) => MakePath(ctx, 1, false);

    [PartiallyImaginedApi(Module, "_makepath_s", ApiCategory.File, "ucrtbase!_makepath_s")]
    private static ulong MakePathSecure(ApiCallContext ctx) => MakePath(ctx, 1, true);

    [PartiallyImaginedApi(Module, "_wmakepath", ApiCategory.File, "ucrtbase!_wmakepath")]
    private static ulong MakeWidePath(ApiCallContext ctx) => MakePath(ctx, 2, false);

    [PartiallyImaginedApi(Module, "_wmakepath_s", ApiCategory.File, "ucrtbase!_wmakepath_s")]
    private static ulong MakeWidePathSecure(ApiCallContext ctx) => MakePath(ctx, 2, true);

    [PartiallyImaginedApi(Module, "_splitpath", ApiCategory.File, "ucrtbase!_splitpath")]
    private static ulong SplitPath(ApiCallContext ctx) => SplitPath(ctx, 1, false);

    [PartiallyImaginedApi(Module, "_splitpath_s", ApiCategory.File, "ucrtbase!_splitpath_s")]
    private static ulong SplitPathSecure(ApiCallContext ctx) => SplitPath(ctx, 1, true);

    [PartiallyImaginedApi(Module, "_wsplitpath", ApiCategory.File, "ucrtbase!_wsplitpath")]
    private static ulong SplitWidePath(ApiCallContext ctx) => SplitPath(ctx, 2, false);

    [PartiallyImaginedApi(Module, "_wsplitpath_s", ApiCategory.File, "ucrtbase!_wsplitpath_s")]
    private static ulong SplitWidePathSecure(ApiCallContext ctx) => SplitPath(ctx, 2, true);

    private static ulong MakePath(ApiCallContext ctx, int width, bool secure)
    {
        var dest = ctx.Arg(0);
        var capacity = secure ? ctx.Arg(1) : ulong.MaxValue;
        if (dest == 0 || capacity == 0) return secure ? CrtMemory.EInvalid : 0;
        var first = secure ? 2 : 1;
        var text = new StringBuilder();
        Span<ulong> lengths = stackalloc ulong[4];
        lengths.Clear();
        var drive = ctx.Arg(first);
        if (drive != 0)
        {
            ushort c;
            if (width == 1)
            {
                if (!CrtMemory.Read(ctx, drive, out byte b)) return PathError(ctx, dest, capacity, width, secure, CrtMemory.EInvalid);
                c = b;
            }
            else if (!CrtMemory.Read(ctx, drive, out c)) return PathError(ctx, dest, capacity, width, secure, CrtMemory.EInvalid);
            if (c != 0) text.Append((char)c).Append(':');
            lengths[0] = (ulong)width;
        }
        for (var i = 1; i <= 3; i++)
        {
            if (ctx.Arg(first + i) == 0) continue;
            if (!CrtMemory.ReadText(ctx, ctx.Arg(first + i), width, out var part))
                return PathError(ctx, dest, capacity, width, secure, CrtMemory.EInvalid);
            lengths[i] = ((ulong)part.Length + 1) * (ulong)width;
            if (part.Length == 0) continue;
            if (i == 3 && part[0] != '.') text.Append('.');
            text.Append(part);
            if (i == 1)
            {
                var last = 0;
                for (var j = 0; j < part.Length; j = CrtMemory.NextPathChar(part, j, width)) last = j;
                if (part[last] is not ('/' or '\\')) text.Append('\\');
            }
        }
        if ((ulong)text.Length >= capacity) return PathError(ctx, dest, capacity, width, secure, CrtMemory.ERange);
        if (secure)
            for (var i = 0; i < 4; i++)
                if (CrtMemory.Overlaps(dest, ((ulong)text.Length + 1) * (ulong)width, ctx.Arg(first + i), lengths[i]))
                    return PathError(ctx, dest, capacity, width, true, CrtMemory.EInvalid);
        if (!CrtMemory.WriteText(ctx, dest, text.ToString(), width))
            return PathError(ctx, dest, capacity, width, secure, CrtMemory.EInvalid);
        return 0;
    }

    private static ulong PathError(ApiCallContext ctx, ulong dest, ulong capacity, int width, bool secure, ulong error)
    {
        CrtMemory.ClearError(ctx, dest, capacity, width, error);
        return secure ? error : 0;
    }

    private static ulong SplitPath(ApiCallContext ctx, int width, bool secure)
    {
        Span<ulong> buffers = stackalloc ulong[4], capacities = stackalloc ulong[4];
        for (var i = 0; i < 4; i++)
        {
            buffers[i] = ctx.Arg(secure ? 1 + i * 2 : 1 + i);
            capacities[i] = secure ? ctx.Arg(2 + i * 2) : buffers[i] == 0 ? 0 : i == 0 ? 3UL : 256UL;
        }
        var error = 0UL;
        for (var i = 0; i < 4; i++)
            if ((buffers[i] == 0) != (capacities[i] == 0) ||
                capacities[i] > (ulong)long.MaxValue / (ulong)width ||
                !CrtMemory.Range(buffers[i], capacities[i] * (ulong)width)) error = CrtMemory.EInvalid;
        if (!CrtMemory.ReadText(ctx, ctx.Arg(0), width, out var path)) error = CrtMemory.EInvalid;
        if (error == 0)
        {
            var start = path.Length >= 2 && path[1] == ':' ? 2 : 0;
            var slash = -1;
            var dot = -1;
            for (var i = start; i < path.Length; i = CrtMemory.NextPathChar(path, i, width))
            {
                if (path[i] is '/' or '\\') slash = i;
                else if (path[i] == '.') dot = i;
            }
            var file = Math.Max(start, slash + 1);
            if (dot < file) dot = path.Length;
            string[] parts = [path[..start], path[start..file], path[file..dot], path[dot..]];
            for (var i = 0; i < 4; i++)
            {
                if (buffers[i] == 0) continue;
                if ((ulong)parts[i].Length >= capacities[i]) error = CrtMemory.ERange;
                if (secure && CrtMemory.Overlaps(buffers[i], ((ulong)parts[i].Length + 1) * (ulong)width,
                    ctx.Arg(0), ((ulong)path.Length + 1) * (ulong)width)) error = CrtMemory.EInvalid;
                if (secure)
                    for (var j = 0; j < i; j++)
                        if (buffers[j] != 0 && CrtMemory.Overlaps(buffers[i], ((ulong)parts[i].Length + 1) * (ulong)width,
                            buffers[j], ((ulong)parts[j].Length + 1) * (ulong)width)) error = CrtMemory.EInvalid;
            }
            if (error == 0)
                for (var i = 0; i < 4; i++)
                    if (buffers[i] != 0 && !CrtMemory.WriteText(ctx, buffers[i], parts[i], width)) error = CrtMemory.EInvalid;
        }
        if (error != 0)
            for (var i = 0; i < 4; i++) CrtMemory.ClearError(ctx, buffers[i], capacities[i], width, error);
        return secure ? error : 0;
    }
}
