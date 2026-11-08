using System.Buffers.Binary;
using System.Numerics;
using ImaginedNT.Core.Runtime;

namespace ImaginedNT.Apis.Crt;

public static class CrtUtilityApis
{
    private const string Module = "api-ms-win-crt-utility-l1-1-0.dll";

    [PartiallyImaginedApi(Module, "abs", ApiCategory.System, "ucrtbase!abs")]
    [PartiallyImaginedApi(Module, "labs", ApiCategory.System, "ucrtbase!labs")]
    private static ulong Abs32(ApiCallContext ctx) => unchecked((uint)(ctx.ArgInt32(0) < 0 ? -ctx.ArgInt32(0) : ctx.ArgInt32(0)));

    [PartiallyImaginedApi(Module, "_abs64", ApiCategory.System, "ucrtbase!_abs64")]
    [PartiallyImaginedApi(Module, "llabs", ApiCategory.System, "ucrtbase!llabs")]
    [PartiallyImaginedApi(Module, "imaxabs", ApiCategory.System, "ucrtbase!imaxabs")]
    private static ulong Abs64(ApiCallContext ctx) => unchecked((ulong)((long)ctx.Arg(0) < 0 ? -(long)ctx.Arg(0) : (long)ctx.Arg(0)));

    [PartiallyImaginedApi(Module, "_byteswap_ushort", ApiCategory.System, "ucrtbase!_byteswap_ushort")]
    private static ulong ByteSwap16(ApiCallContext ctx) => BinaryPrimitives.ReverseEndianness((ushort)ctx.Arg32(0));

    [PartiallyImaginedApi(Module, "_byteswap_ulong", ApiCategory.System, "ucrtbase!_byteswap_ulong")]
    private static ulong ByteSwap32(ApiCallContext ctx) => BinaryPrimitives.ReverseEndianness(ctx.Arg32(0));

    [PartiallyImaginedApi(Module, "_byteswap_uint64", ApiCategory.System, "ucrtbase!_byteswap_uint64")]
    private static ulong ByteSwap64(ApiCallContext ctx) => BinaryPrimitives.ReverseEndianness(ctx.Arg(0));

    [PartiallyImaginedApi(Module, "_rotl", ApiCategory.System, "ucrtbase!_rotl")]
    [PartiallyImaginedApi(Module, "_lrotl", ApiCategory.System, "ucrtbase!_lrotl")]
    private static ulong RotateLeft32(ApiCallContext ctx) => BitOperations.RotateLeft(ctx.Arg32(0), ctx.ArgInt32(1));

    [PartiallyImaginedApi(Module, "_rotr", ApiCategory.System, "ucrtbase!_rotr")]
    [PartiallyImaginedApi(Module, "_lrotr", ApiCategory.System, "ucrtbase!_lrotr")]
    private static ulong RotateRight32(ApiCallContext ctx) => BitOperations.RotateRight(ctx.Arg32(0), ctx.ArgInt32(1));

    [PartiallyImaginedApi(Module, "_rotl64", ApiCategory.System, "ucrtbase!_rotl64")]
    private static ulong RotateLeft64(ApiCallContext ctx) => BitOperations.RotateLeft(ctx.Arg(0), ctx.ArgInt32(1));

    [PartiallyImaginedApi(Module, "_rotr64", ApiCategory.System, "ucrtbase!_rotr64")]
    private static ulong RotateRight64(ApiCallContext ctx) => BitOperations.RotateRight(ctx.Arg(0), ctx.ArgInt32(1));

    [PartiallyImaginedApi(Module, "_swab", ApiCategory.Memory, "ucrtbase!_swab")]
    private static ulong Swab(ApiCallContext ctx)
    {
        var count = ctx.ArgInt32(2) & ~1;
        if (count <= 0 || !CrtMemory.Range(ctx.Arg(0), (ulong)count) || !CrtMemory.Range(ctx.Arg(1), (ulong)count)) return 0;
        Span<byte> bytes = stackalloc byte[4096];
        for (var done = 0; done < count;)
        {
            var n = Math.Min(bytes.Length, count - done);
            if (!ctx.Memory.TryRead(ctx.Arg(0) + (ulong)done, bytes[..n])) return 0;
            for (var i = 0; i < n; i += 2)
                (bytes[i], bytes[i + 1]) = (bytes[i + 1], bytes[i]);
            if (!ctx.Memory.TryWrite(ctx.Arg(1) + (ulong)done, bytes[..n])) return 0;
            done += n;
        }
        return 0;
    }
}
