using System.Buffers.Binary;
using System.Globalization;
using ImaginedNT.Core.Runtime;

namespace ImaginedNT.Apis.Crt;

public static class CrtTimeApis
{
    private const string Module = "api-ms-win-crt-time-l1-1-0.dll";

    [PartiallyImaginedApi(Module, "_strdate", ApiCategory.System, "ucrtbase!_strdate")]
    private static ulong Date(ApiCallContext ctx) => Format(ctx, 1, true, false);

    [PartiallyImaginedApi(Module, "_strdate_s", ApiCategory.System, "ucrtbase!_strdate_s")]
    private static ulong DateSecure(ApiCallContext ctx) => Format(ctx, 1, true, true);

    [PartiallyImaginedApi(Module, "_strtime", ApiCategory.System, "ucrtbase!_strtime")]
    private static ulong Time(ApiCallContext ctx) => Format(ctx, 1, false, false);

    [PartiallyImaginedApi(Module, "_strtime_s", ApiCategory.System, "ucrtbase!_strtime_s")]
    private static ulong TimeSecure(ApiCallContext ctx) => Format(ctx, 1, false, true);

    [PartiallyImaginedApi(Module, "_wstrdate", ApiCategory.System, "ucrtbase!_wstrdate")]
    private static ulong WideDate(ApiCallContext ctx) => Format(ctx, 2, true, false);

    [PartiallyImaginedApi(Module, "_wstrdate_s", ApiCategory.System, "ucrtbase!_wstrdate_s")]
    private static ulong WideDateSecure(ApiCallContext ctx) => Format(ctx, 2, true, true);

    [PartiallyImaginedApi(Module, "_wstrtime", ApiCategory.System, "ucrtbase!_wstrtime")]
    private static ulong WideTime(ApiCallContext ctx) => Format(ctx, 2, false, false);

    [PartiallyImaginedApi(Module, "_wstrtime_s", ApiCategory.System, "ucrtbase!_wstrtime_s")]
    private static ulong WideTimeSecure(ApiCallContext ctx) => Format(ctx, 2, false, true);

    [PartiallyImaginedApi(Module, "_time32", ApiCategory.System, "ucrtbase!_time32")]
    private static ulong Time32(ApiCallContext ctx) => GetTime(ctx, false);

    [PartiallyImaginedApi(Module, "_timespec32_get", ApiCategory.System, "ucrtbase!_timespec32_get")]
    private static ulong Timespec32(ApiCallContext ctx) => GetTimespec(ctx, false);

    [PartiallyImaginedApi(Module, "_time64", ApiCategory.System, "ucrtbase!_time64")]
    private static ulong Time64(ApiCallContext ctx) => GetTime(ctx, true);

    [PartiallyImaginedApi(Module, "_timespec64_get", ApiCategory.System, "ucrtbase!_timespec64_get")]
    private static ulong Timespec64(ApiCallContext ctx) => GetTimespec(ctx, true);

    [PartiallyImaginedApi(Module, "_gmtime64_s", ApiCategory.System, "ucrtbase!_gmtime64_s")]
    private static ulong Gmtime64Secure(ApiCallContext ctx)
    {
        Span<byte> bytes = stackalloc byte[36];
        bytes.Fill(0xFF);
        if (!CrtMemory.Range(ctx.Arg(0), (ulong)bytes.Length) || !ctx.Memory.TryWrite(ctx.Arg(0), bytes))
            return GmtimeError(ctx);
        if (!CrtMemory.Read(ctx, ctx.Arg(1), out long seconds) || seconds < 0 || seconds > 32535215999L)
            return GmtimeError(ctx);

        var utc = DateTimeOffset.FromUnixTimeSeconds(seconds);
        Span<int> fields = stackalloc int[] { utc.Second, utc.Minute, utc.Hour, utc.Day, utc.Month - 1,
            utc.Year - 1900, (int)utc.DayOfWeek, utc.DayOfYear - 1, 0 };
        for (var i = 0; i < fields.Length; i++) BinaryPrimitives.WriteInt32LittleEndian(bytes[(i * 4)..], fields[i]);
        return ctx.Memory.TryWrite(ctx.Arg(0), bytes) ? 0 : GmtimeError(ctx);
    }

    private static ulong GmtimeError(ApiCallContext ctx)
    {
        CrtLocaleData.SetErrno((int)CrtMemory.EInvalid);
        ctx.Runtime.Trace.Warn($"{ctx.Name}: invalid time or buffer (tm=0x{ctx.Arg(0):X}, time=0x{ctx.Arg(1):X})",
            ctx.Process.Id, ctx.Thread.Id);
        return CrtMemory.EInvalid;
    }

    private static ulong Format(ApiCallContext ctx, int width, bool date, bool secure)
    {
        var dest = ctx.Arg(0);
        var capacity = secure ? ctx.Arg(1) : 9;
        if (dest == 0 || capacity == 0) return secure ? CrtMemory.EInvalid : 0;
        if (capacity < 9) return CrtMemory.ClearError(ctx, dest, capacity, width, CrtMemory.ERange);
        var text = DateTime.Now.ToString(date ? "MM/dd/yy" : "HH:mm:ss", CultureInfo.InvariantCulture);
        if (!CrtMemory.WriteText(ctx, dest, text, width)) return secure ? CrtMemory.EInvalid : 0;
        return secure ? 0 : dest;
    }

    private static ulong GetTime(ApiCallContext ctx, bool wide)
    {
        var seconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (seconds < 0 || seconds > (wide ? 32535215999L : int.MaxValue)) seconds = -1;
        if (ctx.Arg(0) != 0 && !(wide ? CrtMemory.Write(ctx, ctx.Arg(0), seconds) : CrtMemory.Write(ctx, ctx.Arg(0), (int)seconds)))
            return wide ? ulong.MaxValue : uint.MaxValue;
        return wide ? unchecked((ulong)seconds) : unchecked((uint)seconds);
    }

    private static ulong GetTimespec(ApiCallContext ctx, bool wide)
    {
        if (ctx.Arg(0) == 0 || ctx.ArgInt32(1) != 1) return 0;
        var now = DateTimeOffset.UtcNow;
        var seconds = now.ToUnixTimeSeconds();
        if (seconds < 0 || seconds > (wide ? 32535215999L : int.MaxValue)) return 0;
        Span<byte> bytes = stackalloc byte[12];
        var offset = wide ? 8 : 4;
        if (wide) BinaryPrimitives.WriteInt64LittleEndian(bytes, seconds);
        else BinaryPrimitives.WriteInt32LittleEndian(bytes, (int)seconds);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[offset..], (int)((now.Ticks % TimeSpan.TicksPerSecond) * 100));
        return ctx.Memory.TryWrite(ctx.Arg(0), bytes[..(offset + 4)]) ? 1UL : 0;
    }

    [PartiallyImaginedApi(Module, "_getsystime", ApiCategory.System, "ucrtbase!_getsystime")]
    private static ulong GetSystemTime(ApiCallContext ctx)
    {
        var now = DateTime.Now;
        Span<int> fields = stackalloc int[] { now.Second, now.Minute, now.Hour, now.Day, now.Month - 1, now.Year - 1900,
            (int)now.DayOfWeek, now.DayOfYear - 1, TimeZoneInfo.Local.IsDaylightSavingTime(now) ? 1 : 0 };
        Span<byte> bytes = stackalloc byte[36];
        for (var i = 0; i < fields.Length; i++) BinaryPrimitives.WriteInt32LittleEndian(bytes[(i * 4)..], fields[i]);
        return ctx.Memory.TryWrite(ctx.Arg(0), bytes) ? (uint)now.Millisecond : 0;
    }
}
