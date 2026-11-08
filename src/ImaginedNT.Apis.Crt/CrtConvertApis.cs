using ImaginedNT.Core.Runtime;

namespace ImaginedNT.Apis.Crt;

public static class CrtConvertApis
{
    private const string Module = "api-ms-win-crt-convert-l1-1-0.dll";

    [PartiallyImaginedApi(Module, "_itoa", ApiCategory.Memory, "ucrtbase!_itoa")]
    [PartiallyImaginedApi(Module, "_ltoa", ApiCategory.Memory, "ucrtbase!_ltoa")]
    private static ulong Int32Narrow(ApiCallContext ctx) => Format(ctx, 32, true, 1, false);

    [PartiallyImaginedApi(Module, "_i64toa", ApiCategory.Memory, "ucrtbase!_i64toa")]
    private static ulong Int64Narrow(ApiCallContext ctx) => Format(ctx, 64, true, 1, false);

    [PartiallyImaginedApi(Module, "_ultoa", ApiCategory.Memory, "ucrtbase!_ultoa",
        "api-ms-win-crt-private-l1-1-0!_o__ultoa", "ucrtbase!_o__ultoa")]
    private static ulong UInt32Narrow(ApiCallContext ctx) => Format(ctx, 32, false, 1, false);

    [PartiallyImaginedApi(Module, "_ui64toa", ApiCategory.Memory, "ucrtbase!_ui64toa")]
    private static ulong UInt64Narrow(ApiCallContext ctx) => Format(ctx, 64, false, 1, false);

    [PartiallyImaginedApi(Module, "_itoa_s", ApiCategory.Memory, "ucrtbase!_itoa_s")]
    [PartiallyImaginedApi(Module, "_ltoa_s", ApiCategory.Memory, "ucrtbase!_ltoa_s")]
    private static ulong Int32NarrowSecure(ApiCallContext ctx) => Format(ctx, 32, true, 1, true);

    [PartiallyImaginedApi(Module, "_i64toa_s", ApiCategory.Memory, "ucrtbase!_i64toa_s")]
    private static ulong Int64NarrowSecure(ApiCallContext ctx) => Format(ctx, 64, true, 1, true);

    [PartiallyImaginedApi(Module, "_ultoa_s", ApiCategory.Memory, "ucrtbase!_ultoa_s",
        "api-ms-win-crt-private-l1-1-0!_o__ultoa_s", "ucrtbase!_o__ultoa_s")]
    private static ulong UInt32NarrowSecure(ApiCallContext ctx) => Format(ctx, 32, false, 1, true);

    [PartiallyImaginedApi(Module, "_ui64toa_s", ApiCategory.Memory, "ucrtbase!_ui64toa_s")]
    private static ulong UInt64NarrowSecure(ApiCallContext ctx) => Format(ctx, 64, false, 1, true);

    [PartiallyImaginedApi(Module, "_itow", ApiCategory.Memory, "ucrtbase!_itow")]
    [PartiallyImaginedApi(Module, "_ltow", ApiCategory.Memory, "ucrtbase!_ltow")]
    private static ulong Int32Wide(ApiCallContext ctx) => Format(ctx, 32, true, 2, false);

    [PartiallyImaginedApi(Module, "_i64tow", ApiCategory.Memory, "ucrtbase!_i64tow")]
    private static ulong Int64Wide(ApiCallContext ctx) => Format(ctx, 64, true, 2, false);

    [PartiallyImaginedApi(Module, "_ultow", ApiCategory.Memory, "ucrtbase!_ultow")]
    private static ulong UInt32Wide(ApiCallContext ctx) => Format(ctx, 32, false, 2, false);

    [PartiallyImaginedApi(Module, "_ui64tow", ApiCategory.Memory, "ucrtbase!_ui64tow")]
    private static ulong UInt64Wide(ApiCallContext ctx) => Format(ctx, 64, false, 2, false);

    [PartiallyImaginedApi(Module, "_itow_s", ApiCategory.Memory, "ucrtbase!_itow_s")]
    [PartiallyImaginedApi(Module, "_ltow_s", ApiCategory.Memory, "ucrtbase!_ltow_s")]
    private static ulong Int32WideSecure(ApiCallContext ctx) => Format(ctx, 32, true, 2, true);

    [PartiallyImaginedApi(Module, "_i64tow_s", ApiCategory.Memory, "ucrtbase!_i64tow_s")]
    private static ulong Int64WideSecure(ApiCallContext ctx) => Format(ctx, 64, true, 2, true);

    [PartiallyImaginedApi(Module, "_ultow_s", ApiCategory.Memory, "ucrtbase!_ultow_s")]
    private static ulong UInt32WideSecure(ApiCallContext ctx) => Format(ctx, 32, false, 2, true);

    [PartiallyImaginedApi(Module, "_ui64tow_s", ApiCategory.Memory, "ucrtbase!_ui64tow_s")]
    private static ulong UInt64WideSecure(ApiCallContext ctx) => Format(ctx, 64, false, 2, true);

    private static ulong Format(ApiCallContext ctx, int bits, bool signed, int width, bool secure)
    {
        var dest = ctx.Arg(1);
        var capacity = secure ? ctx.Arg(2) : ulong.MaxValue;
        var radix = ctx.ArgInt32(secure ? 3 : 2);
        if (dest == 0 || capacity == 0) return secure ? CrtMemory.EInvalid : 0;
        var value = bits == 32 ? ctx.Arg32(0) : ctx.Arg(0);
        var negative = signed && radix == 10 && (bits == 32 ? (int)(uint)value < 0 : (long)value < 0);
        if (secure && capacity <= (negative ? 2UL : 1UL))
            return CrtMemory.ClearError(ctx, dest, capacity, width, CrtMemory.ERange);
        if (radix < 2 || radix > 36)
        {
            CrtMemory.ClearError(ctx, dest, capacity, width, CrtMemory.EInvalid);
            return secure ? CrtMemory.EInvalid : 0;
        }
        if (negative) value = bits == 32 ? unchecked(0u - (uint)value) : unchecked(0UL - value);
        Span<char> text = stackalloc char[65];
        var i = text.Length;
        do
        {
            var d = (int)(value % (uint)radix);
            text[--i] = (char)(d < 10 ? '0' + d : 'a' + d - 10);
            value /= (uint)radix;
        } while (value != 0);
        if (negative) text[--i] = '-';
        if ((ulong)(text.Length - i) >= capacity)
            return CrtMemory.ClearError(ctx, dest, capacity, width, CrtMemory.ERange);
        if (!CrtMemory.WriteText(ctx, dest, new string(text[i..]), width))
            return secure ? CrtMemory.EInvalid : 0;
        return secure ? 0 : dest;
    }
}
