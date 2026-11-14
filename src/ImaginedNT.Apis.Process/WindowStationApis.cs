using System.Text;
using ImaginedNT.Core.Nt;
using ImaginedNT.Core.Objects;
using static ImaginedNT.Apis.ApiHelpers;

namespace ImaginedNT.Core.Runtime;

public static class WindowStationApis
{
    private const uint StationAccess = 0x000F037F;
    private const uint DesktopAccess = 0x000F01FF;
    private static WindowStationManager Manager(ApiCallContext ctx) => ctx.Runtime.WindowStations;
    private static Encoding EncodingFor(ApiCallContext ctx) => ctx.Name.EndsWith('W') ? Encoding.Unicode :
        CodePagesEncodingProvider.Instance.GetEncoding(0) ?? Encoding.UTF8;
    private static string ReadName(ApiCallContext ctx, int index) => ctx.Arg(index) == 0 ? "" :
        ctx.Name.EndsWith('W') ? ctx.ReadUnicodeString(ctx.Arg(index)) : ctx.ReadAnsiString(ctx.Arg(index));

    private static ulong Fail(ApiCallContext ctx, int error)
    {
        ctx.SetLastError(error);
        return 0;
    }

    private static ulong Guard(ApiCallContext ctx, Func<ulong> action)
    {
        try { return action(); }
        catch (UnauthorizedAccessException) { return Fail(ctx, Win32Error.AccessDenied); }
        catch (ArgumentException) { return Fail(ctx, Win32Error.InvalidParameter); }
        catch (Exception ex)
        {
            ctx.Runtime.Trace.Error($"{ctx.Name}: {ex}", ctx.Process.Id, ctx.Thread.Id);
            return Fail(ctx, Win32Error.InvalidParameter);
        }
    }

    private static uint MapAccess(uint access, bool station)
    {
        var mapped = access & 0x01FFFFFF;
        if ((access & 0x12000000) != 0) mapped |= station ? StationAccess : DesktopAccess;
        if ((access & 0x80000000) != 0) mapped |= station ? 0x00020303u : 0x00020041u;
        if ((access & 0x40000000) != 0) mapped |= station ? 0x0002001Cu : 0x000200BEu;
        if ((access & 0x20000000) != 0) mapped |= station ? 0x00020060u : 0x00020100u;
        return mapped;
    }

    private static T? Get<T>(ApiCallContext ctx, ulong handle, uint access = 0) where T : NtObject
    {
        var status = ctx.Handles.TryGet(handle, access, out var obj);
        if (status != NtStatus.Success || obj is not T || obj.IsDisposed)
        {
            ctx.SetLastError(status == NtStatus.AccessDenied ? Win32Error.AccessDenied : Win32Error.InvalidHandle);
            return null;
        }
        var sessionId = obj is DesktopObject desktop ? desktop.SessionId : ((WindowStationObject)obj).SessionId;
        if (sessionId != ctx.Process.SessionId)
        {
            ctx.SetLastError(Win32Error.AccessDenied);
            return null;
        }
        return (T)obj;
    }

    private static bool Inherit(ApiCallContext ctx, ulong attributes)
    {
        if (attributes == 0) return false;
        if (ctx.Read<uint>(attributes) < 24) throw new ArgumentException("SECURITY_ATTRIBUTES");
        if (ctx.Read<ulong>(attributes + 8) != 0) throw new UnauthorizedAccessException("Guest USER security descriptors are not supported.");
        return ctx.Read<uint>(attributes + 16) != 0;
    }

    [VirtualApi("user32.dll", "CreateWindowStationW", ApiCategory.Gui)]
    [VirtualApi("user32.dll", "CreateWindowStationA", ApiCategory.Gui)]
    private static ulong CreateWindowStation(ApiCallContext ctx) => Guard(ctx, () =>
    {
        if (ctx.Arg32(1) != 0) return Fail(ctx, Win32Error.NotSupported);
        var inherit = Inherit(ctx, ctx.Arg(3));
        var name = ctx.Arg(0) == 0 ? $"Station-{Guid.NewGuid():N}" : ReadName(ctx, 0);
        var station = Manager(ctx).CreateStation(ctx.Process.SessionId, name);
        return ctx.Handles.Create(station, MapAccess(ctx.Arg32(2), true), inheritable: inherit);
    });

    [VirtualApi("user32.dll", "OpenWindowStationW", ApiCategory.Gui)]
    [VirtualApi("user32.dll", "OpenWindowStationA", ApiCategory.Gui)]
    private static ulong OpenWindowStation(ApiCallContext ctx) => Guard(ctx, () =>
    {
        var station = Manager(ctx).FindStation(ctx.Process.SessionId, ReadName(ctx, 0));
        return station is null ? Fail(ctx, Win32Error.FileNotFound) :
            ctx.Handles.Create(station, MapAccess(ctx.Arg32(2), true), inheritable: ctx.ArgBool(1));
    });

    [VirtualApi("user32.dll", "GetProcessWindowStation", ApiCategory.Gui)]
    private static ulong GetProcessWindowStation(ApiCallContext ctx)
    {
        if (!ctx.Handles.TryGet(ctx.Process.WindowStationHandle, out var obj) || obj != ctx.Process.WindowStation)
            ctx.Process.WindowStationHandle = ctx.Handles.Create(ctx.Process.WindowStation, StationAccess);
        return ctx.Process.WindowStationHandle;
    }

    [VirtualApi("user32.dll", "SetProcessWindowStation", ApiCategory.Gui)]
    private static ulong SetProcessWindowStation(ApiCallContext ctx) => Guard(ctx, () =>
    {
        if (Get<WindowStationObject>(ctx, ctx.Arg(0)) is not { } station) return 0;
        Manager(ctx).SetProcessWindowStation(ctx.Process, station);
        ctx.Process.WindowStationHandle = ctx.Arg(0);
        return 1;
    });

    [VirtualApi("user32.dll", "CloseWindowStation", ApiCategory.Gui)]
    private static ulong CloseWindowStation(ApiCallContext ctx)
    {
        if (Get<WindowStationObject>(ctx, ctx.Arg(0)) is null) return 0;
        if (ctx.Arg(0) == ctx.Process.WindowStationHandle) return Fail(ctx, Win32Error.AccessDenied);
        return BoolResult(ctx, ctx.Handles.Close(ctx.Arg(0)) == NtStatus.Success);
    }

    [VirtualApi("user32.dll", "CreateDesktopW", ApiCategory.Gui)]
    [VirtualApi("user32.dll", "CreateDesktopA", ApiCategory.Gui)]
    [VirtualApi("user32.dll", "CreateDesktopExW", ApiCategory.Gui)]
    [VirtualApi("user32.dll", "CreateDesktopExA", ApiCategory.Gui)]
    private static ulong CreateDesktop(ApiCallContext ctx) => Guard(ctx, () =>
    {
        if (ctx.Arg(1) != 0 || ctx.Arg(2) != 0 || ctx.Arg32(3) != 0 ||
            (ctx.Name.StartsWith("CreateDesktopEx", StringComparison.Ordinal) && ctx.Arg(7) != 0))
            return Fail(ctx, Win32Error.NotSupported);
        if ((MapAccess(ctx.Arg32(4), false) & 2) == 0) return Fail(ctx, Win32Error.AccessDenied);
        var inherit = Inherit(ctx, ctx.Arg(5));
        var stationHandle = GetProcessWindowStation(ctx);
        if (Get<WindowStationObject>(ctx, stationHandle, 8) is not { } station) return 0;
        return ctx.Handles.Create(Manager(ctx).CreateDesktop(station, ReadName(ctx, 0)),
            MapAccess(ctx.Arg32(4), false), inheritable: inherit);
    });

    [VirtualApi("user32.dll", "OpenDesktopW", ApiCategory.Gui)]
    [VirtualApi("user32.dll", "OpenDesktopA", ApiCategory.Gui)]
    private static ulong OpenDesktop(ApiCallContext ctx) => Guard(ctx, () =>
    {
        if (ctx.Arg32(1) != 0) return Fail(ctx, Win32Error.NotSupported);
        var desktop = Manager(ctx).FindDesktop(ctx.Process.WindowStation, ReadName(ctx, 0));
        return desktop is null ? Fail(ctx, Win32Error.FileNotFound) :
            ctx.Handles.Create(desktop, MapAccess(ctx.Arg32(3), false), inheritable: ctx.ArgBool(2));
    });

    [VirtualApi("user32.dll", "OpenInputDesktop", ApiCategory.Gui)]
    private static ulong OpenInputDesktop(ApiCallContext ctx)
    {
        if (ctx.Arg32(0) != 0) return Fail(ctx, Win32Error.NotSupported);
        var desktop = Manager(ctx).GetInputDesktop(ctx.Process.SessionId);
        if (desktop is null || desktop.WindowStation != ctx.Process.WindowStation) return Fail(ctx, Win32Error.AccessDenied);
        return ctx.Handles.Create(desktop, MapAccess(ctx.Arg32(2), false), inheritable: ctx.ArgBool(1));
    }

    [VirtualApi("user32.dll", "GetThreadDesktop", ApiCategory.Gui)]
    private static ulong GetThreadDesktop(ApiCallContext ctx)
    {
        var thread = ctx.Runtime.Processes.Where(p => p.SessionId == ctx.Process.SessionId)
            .SelectMany(p => p.Threads).FirstOrDefault(t => t.Id == ctx.ArgInt32(0) &&
                t.State is ThreadState.Created or ThreadState.Running);
        if (thread is null) return Fail(ctx, Win32Error.InvalidParameter);
        if (thread.Process == ctx.Process)
        {
            if (!ctx.Handles.TryGet(thread.DesktopHandle, out var obj) || obj != thread.Desktop)
                thread.DesktopHandle = ctx.Handles.Create(thread.Desktop, DesktopAccess);
            return thread.DesktopHandle;
        }
        return ctx.Handles.Snapshot().FirstOrDefault(h => h.Object == thread.Desktop)?.Handle ??
            ctx.Handles.Create(thread.Desktop, 0x00020001);
    }

    [VirtualApi("user32.dll", "SetThreadDesktop", ApiCategory.Gui)]
    private static ulong SetThreadDesktop(ApiCallContext ctx) => Guard(ctx, () =>
    {
        if (Get<DesktopObject>(ctx, ctx.Arg(0)) is not { } desktop) return 0;
        if (!Manager(ctx).SetThreadDesktop(ctx.Thread, desktop)) return Fail(ctx, 170); // ERROR_BUSY
        ctx.Thread.DesktopHandle = ctx.Arg(0);
        return 1;
    });

    [VirtualApi("user32.dll", "CloseDesktop", ApiCategory.Gui)]
    private static ulong CloseDesktop(ApiCallContext ctx)
    {
        if (Get<DesktopObject>(ctx, ctx.Arg(0)) is not { } desktop) return 0;
        if (desktop == ctx.Process.StartupDesktop || ctx.Process.Threads.Any(t =>
                t.DesktopHandle == ctx.Arg(0) && t.State is ThreadState.Created or ThreadState.Running))
            return Fail(ctx, 170);
        return BoolResult(ctx, ctx.Handles.Close(ctx.Arg(0)) == NtStatus.Success);
    }

    [VirtualApi("user32.dll", "SwitchDesktop", ApiCategory.Gui)]
    private static ulong SwitchDesktop(ApiCallContext ctx) => Guard(ctx, () =>
    {
        if (Get<DesktopObject>(ctx, ctx.Arg(0), 0x100) is not { } desktop) return 0;
        if (desktop.WindowStation != ctx.Process.WindowStation) return Fail(ctx, Win32Error.AccessDenied);
        if (Manager(ctx).GetInputDesktop(ctx.Process.SessionId)?.Security is { } security &&
            !security.Allows(ctx.Thread.EffectiveToken, 0x100)) return Fail(ctx, Win32Error.AccessDenied);
        Manager(ctx).SwitchDesktop(desktop);
        return 1;
    });

    [VirtualApi("user32.dll", "EnumWindowStationsW", ApiCategory.Gui)]
    [VirtualApi("user32.dll", "EnumWindowStationsA", ApiCategory.Gui)]
    private static ulong EnumWindowStations(ApiCallContext ctx) => Enumerate(ctx,
        Manager(ctx).Stations.Where(s => s.SessionId == ctx.Process.SessionId).Select(s => s.Name!).ToArray(), 0);

    [VirtualApi("user32.dll", "EnumDesktopsW", ApiCategory.Gui)]
    [VirtualApi("user32.dll", "EnumDesktopsA", ApiCategory.Gui)]
    private static ulong EnumDesktops(ApiCallContext ctx)
    {
        var station = Get<WindowStationObject>(ctx, ctx.Arg(0) == 0 ? GetProcessWindowStation(ctx) : ctx.Arg(0), 1);
        return station is null ? 0 : Enumerate(ctx, Manager(ctx).GetDesktops(station).Select(d => d.Name!).ToArray(), 1);
    }

    private static ulong Enumerate(ApiCallContext ctx, string[] names, int callbackIndex)
    {
        if (ctx.Arg(callbackIndex) == 0) return Fail(ctx, Win32Error.InvalidParameter);
        foreach (var name in names)
        {
            var data = EncodingFor(ctx).GetBytes(name + '\0');
            var buffer = ctx.Process.Heap.Allocate((ulong)data.Length, threadId: ctx.Thread.Id);
            if (buffer == 0) return Fail(ctx, Win32Error.NotEnoughMemory);
            try
            {
                ctx.WriteBytes(buffer, data);
                if ((uint)ctx.Thread.InvokeGuest(ctx.Arg(callbackIndex), [buffer, ctx.Arg(callbackIndex + 1)]) == 0) return 0;
            }
            finally { ctx.Process.Heap.Free(buffer, ctx.Thread.Id); }
        }
        return 1;
    }

    [VirtualApi("user32.dll", "GetUserObjectInformationW", ApiCategory.Gui)]
    [VirtualApi("user32.dll", "GetUserObjectInformationA", ApiCategory.Gui)]
    private static ulong GetUserObjectInformation(ApiCallContext ctx)
    {
        if (!ctx.Handles.TryGet(ctx.Arg(0), out var obj) || obj is not (WindowStationObject or DesktopObject) || obj.IsDisposed)
            return Fail(ctx, Win32Error.InvalidHandle);
        if ((obj is DesktopObject desktop ? desktop.SessionId : ((WindowStationObject)obj).SessionId) != ctx.Process.SessionId)
            return Fail(ctx, Win32Error.AccessDenied);
        byte[] data;
        switch (ctx.Arg32(1))
        {
            case 1: // USEROBJECTFLAGS
                data = new byte[12];
                ctx.Handles.TryGetInformation(ctx.Arg(0), out var flags);
                BitConverter.GetBytes(flags & 1).CopyTo(data, 0);
                BitConverter.GetBytes(obj is WindowStationObject s && s.IsInteractive ? 1u : 0u).CopyTo(data, 8);
                break;
            case 2:
                data = EncodingFor(ctx).GetBytes(obj.Name + '\0');
                break;
            case 3:
                data = EncodingFor(ctx).GetBytes((obj is DesktopObject ? "Desktop" : "WindowStation") + '\0');
                break;
            case 6: // UOI_IO
                data = BitConverter.GetBytes(obj is DesktopObject d && Manager(ctx).GetInputDesktop(d.SessionId) == d ? 1 : 0);
                break;
            default:
                return Fail(ctx, Win32Error.NotSupported);
        }
        if (ctx.Arg(4) != 0) ctx.Write(ctx.Arg(4), (uint)data.Length);
        if (ctx.Arg32(3) < data.Length) return Fail(ctx, 122);
        if (ctx.Arg(2) == 0) return Fail(ctx, Win32Error.InvalidParameter);
        ctx.WriteBytes(ctx.Arg(2), data);
        return 1;
    }

    [VirtualApi("user32.dll", "SetUserObjectInformationW", ApiCategory.Gui)]
    [VirtualApi("user32.dll", "SetUserObjectInformationA", ApiCategory.Gui)]
    private static ulong SetUserObjectInformation(ApiCallContext ctx)
    {
        if (!ctx.Handles.TryGet(ctx.Arg(0), out var obj) || obj is not (WindowStationObject or DesktopObject) || obj.IsDisposed)
            return Fail(ctx, Win32Error.InvalidHandle);
        if ((obj is DesktopObject desktop ? desktop.SessionId : ((WindowStationObject)obj).SessionId) != ctx.Process.SessionId)
            return Fail(ctx, Win32Error.AccessDenied);
        if (ctx.Arg32(1) != 1) return Fail(ctx, Win32Error.NotSupported);
        if (ctx.Arg(2) == 0 || ctx.Arg32(3) < 12) return Fail(ctx, Win32Error.InvalidParameter);
        if (ctx.Read<uint>(ctx.Arg(2) + 4) != 0) return Fail(ctx, Win32Error.InvalidParameter);
        var flags = ctx.Read<uint>(ctx.Arg(2) + 8);
        if (flags != (obj is WindowStationObject s && s.IsInteractive ? 1u : 0u)) return Fail(ctx, Win32Error.NotSupported);
        return BoolResult(ctx, ctx.Handles.SetInformation(ctx.Arg(0), 1, ctx.Read<uint>(ctx.Arg(2)) != 0 ? 1u : 0u));
    }

    [VirtualApi("user32.dll", "GetUserObjectSecurity", ApiCategory.Gui)]
    [VirtualApi("user32.dll", "SetUserObjectSecurity", ApiCategory.Gui)]
    private static ulong UserObjectSecurity(ApiCallContext ctx) => Fail(ctx, Win32Error.NotSupported);
}
