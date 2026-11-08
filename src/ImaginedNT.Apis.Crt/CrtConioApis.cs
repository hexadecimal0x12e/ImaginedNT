using ImaginedNT.Core.Runtime;

namespace ImaginedNT.Apis.Crt;

public static class CrtConioApis
{
    [PartiallyImaginedApi("api-ms-win-crt-conio-l1-1-0.dll", "_getch", ApiCategory.Console,
        "ucrtbase!_getch", "api-ms-win-crt-private-l1-1-0!_o__getch", "ucrtbase!_o__getch")]
    private static ulong GetCh(ApiCallContext ctx)
    {
        try
        {
            if (ctx.TryGetFile(0, out var file) && file is { CanRead: true })
                return unchecked((ulong)file.ReadConsoleByte());
            CrtLocaleData.SetErrno(9);
        }
        catch (Exception ex)
        {
            ctx.Runtime.Trace.Warn($"{ctx.Name}: {ex}", ctx.Process.Id, ctx.Thread.Id);
            CrtLocaleData.SetErrno(5);
        }
        return ulong.MaxValue;
    }
}
