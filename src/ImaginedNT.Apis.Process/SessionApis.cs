using System.Text;
using ImaginedNT.Core.Nt;
using static ImaginedNT.Apis.ApiHelpers;

namespace ImaginedNT.Core.Runtime;

public static class SessionApis
{
    [VirtualApi("kernel32.dll", "WTSGetActiveConsoleSessionId", ApiCategory.Process, "kernelbase!WTSGetActiveConsoleSessionId")]
    private static ulong WTSGetActiveConsoleSessionId(ApiCallContext ctx) => ctx.Runtime.ActiveConsoleSessionId ?? uint.MaxValue;

    [VirtualApi("wtsapi32.dll", "WTSEnumerateSessionsW", ApiCategory.Process)]
    [VirtualApi("wtsapi32.dll", "WTSEnumerateSessionsA", ApiCategory.Process)]
    private static ulong WTSEnumerateSessions(ApiCallContext ctx)
    {
        if (ctx.Arg(0) != 0) return BoolResult(ctx, false, Win32Error.InvalidHandle);
        if (ctx.Arg32(1) != 0 || ctx.Arg32(2) != 1 || ctx.Arg(3) == 0 || ctx.Arg(4) == 0)
            return BoolResult(ctx, false, Win32Error.InvalidParameter);
        ctx.Write(ctx.Arg(3), 0UL);
        ctx.Write(ctx.Arg(4), 0u);
        var sessions = ctx.Runtime.Sessions;
        var names = sessions.Select(s => GetEncoding(ctx).GetBytes(s.Name + '\0')).ToArray();
        var size = checked(sessions.Count * 24 + names.Sum(n => n.Length));
        var buffer = ctx.Process.Heap.Allocate((ulong)size, zero: true, threadId: ctx.Thread.Id);
        if (buffer == 0) return BoolResult(ctx, false, Win32Error.NotEnoughMemory);
        var nameBuffer = buffer + (ulong)sessions.Count * 24;
        for (var i = 0; i < sessions.Count; i++)
        {
            // WTS_SESSION_INFO 的 AMD64 布局：DWORD、指针、枚举，大小为 24 字节。
            var entry = buffer + (ulong)i * 24;
            ctx.Write(entry, sessions[i].Id);
            ctx.Write(entry + 8, nameBuffer);
            ctx.Write(entry + 16, (uint)sessions[i].State);
            ctx.WriteBytes(nameBuffer, names[i]);
            nameBuffer += (ulong)names[i].Length;
        }
        ctx.Write(ctx.Arg(3), buffer);
        ctx.Write(ctx.Arg(4), (uint)sessions.Count);
        return 1;
    }

    [VirtualApi("wtsapi32.dll", "WTSQuerySessionInformationW", ApiCategory.Process)]
    [VirtualApi("wtsapi32.dll", "WTSQuerySessionInformationA", ApiCategory.Process)]
    private static ulong WTSQuerySessionInformation(ApiCallContext ctx)
    {
        if (ctx.Arg(0) != 0) return BoolResult(ctx, false, Win32Error.InvalidHandle);
        if (ctx.Arg(3) == 0 || ctx.Arg(4) == 0) return BoolResult(ctx, false, Win32Error.InvalidParameter);
        ctx.Write(ctx.Arg(3), 0UL);
        ctx.Write(ctx.Arg(4), 0u);
        var session = ctx.Runtime.GetSession(ctx.Arg32(1) == uint.MaxValue ? ctx.Process.SessionId : ctx.Arg32(1));
        if (session is null) return BoolResult(ctx, false, 7022); // ERROR_CTX_WINSTATION_NOT_FOUND
        byte[] data;
        switch (ctx.Arg32(2))
        {
            case 4: // WTSSessionId
                data = BitConverter.GetBytes(session.Id);
                break;
            case 6: // WTSWinStationName
                data = GetEncoding(ctx).GetBytes(session.Name + '\0');
                break;
            case 8: // WTSConnectState
                data = BitConverter.GetBytes((uint)session.State);
                break;
            default:
                return BoolResult(ctx, false, Win32Error.NotSupported);
        }
        var buffer = ctx.Process.Heap.Allocate((ulong)data.Length, threadId: ctx.Thread.Id);
        if (buffer == 0) return BoolResult(ctx, false, Win32Error.NotEnoughMemory);
        ctx.WriteBytes(buffer, data);
        ctx.Write(ctx.Arg(3), buffer);
        ctx.Write(ctx.Arg(4), (uint)data.Length);
        return 1;
    }

    [VirtualApi("wtsapi32.dll", "WTSFreeMemory", ApiCategory.Process)]
    private static ulong WTSFreeMemory(ApiCallContext ctx)
    {
        ctx.Process.Heap.Free(ctx.Arg(0), ctx.Thread.Id);
        return 0;
    }

    private static Encoding GetEncoding(ApiCallContext ctx) => ctx.Name.EndsWith('W') ? Encoding.Unicode :
        CodePagesEncodingProvider.Instance.GetEncoding(0) ?? Encoding.UTF8;
}
