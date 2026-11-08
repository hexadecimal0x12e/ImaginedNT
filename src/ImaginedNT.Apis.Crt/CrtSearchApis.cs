using ImaginedNT.Core.Runtime;

namespace ImaginedNT.Apis.Crt;

public static class CrtSearchApis
{
    private const string Module = "api-ms-win-crt-utility-l1-1-0.dll";

    [PartiallyImaginedApi(Module, "bsearch", ApiCategory.Memory, "ucrtbase!bsearch",
        "api-ms-win-crt-private-l1-1-0!_o_bsearch", "ucrtbase!_o_bsearch")]
    private static ulong BinarySearch(ApiCallContext ctx)
    {
        var count = ctx.Arg(2);
        var size = ctx.Arg(3);
        if (ctx.Arg(0) == 0 || !ValidArray(ctx.Arg(1), count, size, ctx.Arg(4))) return Invalid(ctx);
        ulong lo = 0, hi = count;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            var p = ctx.Arg(1) + mid * size;
            var result = unchecked((int)Call(ctx, ctx.Arg(4), [ctx.Arg(0), p]));
            if (result == 0) return p;
            if (result < 0) hi = mid;
            else lo = mid + 1;
        }
        return 0;
    }

    [PartiallyImaginedApi(Module, "qsort_s", ApiCategory.Memory, "ucrtbase!qsort_s",
        "api-ms-win-crt-private-l1-1-0!_o_qsort_s", "ucrtbase!_o_qsort_s")]
    private static ulong Sort(ApiCallContext ctx)
    {
        var count = ctx.Arg(1);
        var size = ctx.Arg(2);
        if (!ValidArray(ctx.Arg(0), count, size, ctx.Arg(3))) return Invalid(ctx);
        if (count < 2) return 0;
        Span<byte> a = stackalloc byte[1024], b = stackalloc byte[1024];
        // Heapsort bounds both comparison count and scratch space for guest-controlled sizes.
        for (var i = count / 2; i > 0; i--)
            if (!Sift(ctx, i - 1, count, size, a, b)) return Invalid(ctx);
        for (var end = count - 1; end > 0; end--)
        {
            if (!Swap(ctx, 0, end, size, a, b) || !Sift(ctx, 0, end, size, a, b)) return Invalid(ctx);
        }
        return 0;
    }

    private static bool Sift(ApiCallContext ctx, ulong root, ulong count, ulong size, Span<byte> a, Span<byte> b)
    {
        while (root < count / 2)
        {
            var child = root * 2 + 1;
            if (child + 1 < count && Compare(ctx, child, child + 1, size) < 0) child++;
            if (Compare(ctx, root, child, size) >= 0) break;
            if (!Swap(ctx, root, child, size, a, b)) return false;
            root = child;
        }
        return true;
    }

    private static int Compare(ApiCallContext ctx, ulong a, ulong b, ulong size) =>
        unchecked((int)Call(ctx, ctx.Arg(3), [ctx.Arg(4), ctx.Arg(0) + a * size, ctx.Arg(0) + b * size]));

    private static bool Swap(ApiCallContext ctx, ulong left, ulong right, ulong size, Span<byte> a, Span<byte> b)
    {
        left = ctx.Arg(0) + left * size;
        right = ctx.Arg(0) + right * size;
        for (ulong i = 0; i < size;)
        {
            var n = (int)Math.Min((ulong)a.Length, size - i);
            if (!ctx.Memory.TryRead(left + i, a[..n]) || !ctx.Memory.TryRead(right + i, b[..n]) ||
                !ctx.Memory.TryWrite(left + i, b[..n]) || !ctx.Memory.TryWrite(right + i, a[..n])) return false;
            i += (ulong)n;
        }
        return true;
    }

    private static bool ValidArray(ulong address, ulong count, ulong size, ulong compare) =>
        size != 0 && compare != 0 && count <= (ulong)long.MaxValue / size && CrtMemory.Range(address, count * size);

    private static ulong Invalid(ApiCallContext ctx)
    {
        CrtLocaleData.SetErrno(22);
        ctx.Runtime.Trace.Warn($"{ctx.Name}: invalid search/sort arguments or inaccessible array", ctx.Process.Id, ctx.Thread.Id);
        return 0;
    }

    internal static unsafe ulong Call(ApiCallContext ctx, ulong function, ReadOnlySpan<ulong> args)
    {
        if (ctx.Thread.IsManagedHosted) return ctx.Thread.InvokeGuest(function, args);
        return args.Length switch
        {
            0 => ((delegate* unmanaged<ulong>)(nint)function)(),
            1 => ((delegate* unmanaged<ulong, ulong>)(nint)function)(args[0]),
            2 => ((delegate* unmanaged<ulong, ulong, ulong>)(nint)function)(args[0], args[1]),
            3 => ((delegate* unmanaged<ulong, ulong, ulong, ulong>)(nint)function)(args[0], args[1], args[2]),
            _ => throw new ArgumentOutOfRangeException(nameof(args))
        };
    }
}
