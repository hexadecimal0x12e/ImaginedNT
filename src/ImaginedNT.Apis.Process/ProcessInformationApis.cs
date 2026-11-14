using ImaginedNT.Core.Nt;
using static ImaginedNT.Apis.ApiHelpers;

namespace ImaginedNT.Core.Runtime;

public static class ProcessInformationApis
{
    private const uint SystemQueryAccess = 0x0002_1410;

    private static ulong OpenSystemProcess(ApiCallContext ctx, uint access, bool inherit)
    {
        using var obj = new SystemProcessObject(ctx.Runtime);
        return ctx.Handles.Create(obj, (access & 0x0200_0000) != 0 ? SystemQueryAccess : access, inheritable: inherit);
    }

    [VirtualApi("kernel32.dll", "OpenProcess", ApiCategory.Process, "kernelbase!OpenProcess")]
    private static ulong OpenProcess(ApiCallContext ctx)
    {
        if (ctx.ArgInt32(2) == ProcessInformation.SystemProcessId)
        {
            if ((ctx.Arg32(0) & ~(SystemQueryAccess | 0x0200_0000u)) != 0)
                return BoolResult(ctx, false, Win32Error.AccessDenied);
            return OpenSystemProcess(ctx, ctx.Arg32(0), ctx.ArgBool(1));
        }
        var process = ctx.Runtime.GetProcessById(ctx.ArgInt32(2));
        if (process is null || process.IsTerminating)
        {
            ctx.SetLastError(Win32Error.InvalidParameter);
            return 0;
        }
        if (!CanOpenProcess(ctx, process)) return BoolResult(ctx, false, Win32Error.AccessDenied);
        using var obj = new ProcessObject(process);
        return ctx.Handles.Create(obj, ctx.Arg32(0), inheritable: ctx.ArgBool(1));
    }

    [VirtualApi("ntdll.dll", "NtOpenProcess", ApiCategory.Process, "ntdll!ZwOpenProcess")]
    private static ulong NtOpenProcess(ApiCallContext ctx)
    {
        if (ctx.Arg(0) == 0 || ctx.Arg(3) == 0) return NtStatus.InvalidParameter;
        ctx.Write(ctx.Arg(0), 0UL);
        var id = ctx.Read<ulong>(ctx.Arg(3));
        if (id == ProcessInformation.SystemProcessId)
        {
            if ((ctx.Arg32(1) & ~(SystemQueryAccess | 0x0200_0000u)) != 0) return NtStatus.AccessDenied;
            ctx.Write(ctx.Arg(0), OpenSystemProcess(ctx, ctx.Arg32(1),
                ctx.Arg(2) != 0 && (ctx.Read<uint>(ctx.Arg(2) + 24) & 2) != 0));
            return NtStatus.Success;
        }
        var process = id <= int.MaxValue ? ctx.Runtime.GetProcessById((int)id) : null;
        if (process is null || process.IsTerminating) return NtStatus.InvalidCid;
        if (!CanOpenProcess(ctx, process)) return NtStatus.AccessDenied;
        using var obj = new ProcessObject(process);
        ctx.Write(ctx.Arg(0), ctx.Handles.Create(obj, ctx.Arg32(1),
            inheritable: ctx.Arg(2) != 0 && (ctx.Read<uint>(ctx.Arg(2) + 24) & 2) != 0));
        return NtStatus.Success;
    }

    private static bool CanOpenProcess(ApiCallContext ctx, ImaginedProcess process)
    {
        var token = ctx.Thread.EffectiveToken;
        return token.IsSystem || token.User.Sid == process.Token.User.Sid || token.HasPrivilege("SeDebugPrivilege");
    }

    [VirtualApi("kernel32.dll", "QueryFullProcessImageNameW", ApiCategory.Process, "kernelbase!QueryFullProcessImageNameW")]
    [VirtualApi("kernel32.dll", "QueryFullProcessImageNameA", ApiCategory.Process, "kernelbase!QueryFullProcessImageNameA")]
    private static ulong QueryFullProcessImageName(ApiCallContext ctx)
    {
        var p = GetProcessInformation(ctx, ctx.Arg(0));
        if (p is null) return BoolResult(ctx, false, Win32Error.InvalidHandle);
        if (ctx.Arg32(1) > 1 || ctx.Arg(2) == 0 || ctx.Arg(3) == 0)
            return BoolResult(ctx, false, Win32Error.InvalidParameter);
        var path = ctx.Arg32(1) == 1 && p.IsSystem ? @"\SystemRoot\System32\ntoskrnl.exe" : p.ImagePath;
        var unicode = ctx.Name.EndsWith('W');
        var count = unicode ? path.Length : AnsiEncoding.GetByteCount(path);
        if (ctx.Read<uint>(ctx.Arg(3)) <= count) return BoolResult(ctx, false, Win32Error.InsufficientBuffer);
        WriteName(ctx, ctx.Arg(2), path, unicode);
        ctx.Write(ctx.Arg(3), (uint)count);
        return 1;
    }

    [VirtualApi("psapi.dll", "GetProcessImageFileNameW", ApiCategory.Process,
        "kernel32!K32GetProcessImageFileNameW", "kernelbase!K32GetProcessImageFileNameW")]
    [VirtualApi("psapi.dll", "GetProcessImageFileNameA", ApiCategory.Process,
        "kernel32!K32GetProcessImageFileNameA", "kernelbase!K32GetProcessImageFileNameA")]
    private static ulong GetProcessImageFileName(ApiCallContext ctx)
    {
        var p = GetProcessInformation(ctx, ctx.Arg(0));
        if (p is null) return BoolResult(ctx, false, Win32Error.InvalidHandle);
        var buffer = ctx.Arg(1);
        var size = ctx.Arg32(2);
        if (buffer == 0 || size == 0) return BoolResult(ctx, false, Win32Error.InvalidParameter);
        var name = p.IsSystem ? @"\SystemRoot\System32\ntoskrnl.exe" : p.ImagePath;
        var unicode = ctx.Name.EndsWith('W');
        var count = unicode ? name.Length : AnsiEncoding.GetByteCount(name);
        if (size <= count) return BoolResult(ctx, false, Win32Error.InsufficientBuffer);
        WriteName(ctx, buffer, name, unicode);
        return (ulong)count;
    }

    [VirtualApi("kernel32.dll", "GetProcessHandleCount", ApiCategory.Process, "kernelbase!GetProcessHandleCount")]
    private static ulong GetProcessHandleCount(ApiCallContext ctx)
    {
        var p = GetProcessInformation(ctx, ctx.Arg(0));
        if (p is null) return BoolResult(ctx, false, Win32Error.InvalidHandle);
        if (ctx.Arg(1) == 0) return BoolResult(ctx, false, Win32Error.InvalidParameter);
        ctx.Write(ctx.Arg(1), (uint)p.HandleCount);
        return 1;
    }

    [VirtualApi("kernel32.dll", "GetProcessTimes", ApiCategory.Process, "kernelbase!GetProcessTimes")]
    private static ulong GetProcessTimes(ApiCallContext ctx)
    {
        var p = GetProcessInformation(ctx, ctx.Arg(0));
        if (p is null) return BoolResult(ctx, false, Win32Error.InvalidHandle);
        for (var i = 1; i <= 4; i++)
            if (ctx.Arg(i) == 0) return BoolResult(ctx, false, Win32Error.InvalidParameter);
        ctx.Write(ctx.Arg(1), p.StartTime.UtcDateTime.ToFileTimeUtc());
        ctx.Write(ctx.Arg(2), 0UL);
        ctx.Write(ctx.Arg(3), 0UL);
        ctx.Write(ctx.Arg(4), 0UL);
        return 1;
    }

    [VirtualApi("kernel32.dll", "GetPriorityClass", ApiCategory.Process, "kernelbase!GetPriorityClass")]
    private static ulong GetPriorityClass(ApiCallContext ctx) => GetProcessInformation(ctx, ctx.Arg(0)) is null
        ? BoolResult(ctx, false, Win32Error.InvalidHandle) : 0x20;

    [VirtualApi("kernel32.dll", "ProcessIdToSessionId", ApiCategory.Process, "kernelbase!ProcessIdToSessionId")]
    private static ulong ProcessIdToSessionId(ApiCallContext ctx)
    {
        var process = ctx.Runtime.GetProcessInformation(ctx.ArgInt32(0));
        if (process is null)
            return BoolResult(ctx, false, Win32Error.InvalidParameter);
        if (ctx.Arg(1) == 0) return BoolResult(ctx, false, Win32Error.InvalidParameter);
        ctx.Write(ctx.Arg(1), process.SessionId);
        return 1;
    }

    private static System.Text.Encoding AnsiEncoding =>
        System.Text.CodePagesEncodingProvider.Instance.GetEncoding(0) ?? System.Text.Encoding.UTF8;

    private static void WriteName(ApiCallContext ctx, ulong buffer, string name, bool unicode)
    {
        ctx.WriteBytes(buffer, (unicode ? System.Text.Encoding.Unicode : AnsiEncoding).GetBytes(name + '\0'));
    }
}
