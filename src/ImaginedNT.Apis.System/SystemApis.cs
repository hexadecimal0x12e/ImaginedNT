using ImaginedNT.Core.Diagnostics;
using ImaginedNT.Core.Io;
using ImaginedNT.Core.Nt;
using ImaginedNT.Core.Objects;
using ImaginedNT.Core.Pe;

using static ImaginedNT.Apis.ApiHelpers;

namespace ImaginedNT.Core.Runtime;

public static class SystemApis
{
    // --- process / thread identity ------------------------------------------------
    [VirtualApi("kernel32.dll", "GetCurrentProcessId", ApiCategory.Process)]
    private static ulong GetCurrentProcessId(ApiCallContext ctx) => (ulong)ctx.Process.Id;

    [VirtualApi("kernel32.dll", "GetCurrentThreadId", ApiCategory.Thread)]
    private static ulong GetCurrentThreadId(ApiCallContext ctx) => (ulong)CurrentVirtualThreadId(ctx);

    [VirtualApi("kernel32.dll", "GetCurrentProcess", ApiCategory.Process)]
    private static ulong GetCurrentProcess(ApiCallContext ctx) => CurrentProcessPseudoHandle;

    [VirtualApi("kernel32.dll", "GetCurrentThread", ApiCategory.Thread)]
    private static ulong GetCurrentThread(ApiCallContext ctx) => CurrentThreadPseudoHandle;

    [VirtualApi("kernel32.dll", "GetProcessId", ApiCategory.Process)]
    private static ulong GetProcessId(ApiCallContext ctx)
    {
        var handle = ctx.Arg(0);
        if (handle == CurrentProcessPseudoHandle) return (ulong)ctx.Process.Id;
        var obj = GetObject(ctx, handle);
        return obj switch
        {
            ProcessObject p => (ulong)p.Process.Id,
            SystemProcessObject => ProcessInformation.SystemProcessId,
            _ => 0
        };
    }

    [VirtualApi("kernel32.dll", "GetThreadId", ApiCategory.Thread)]
    private static ulong GetThreadId(ApiCallContext ctx)
    {
        var handle = ctx.Arg(0);
        if (handle == CurrentThreadPseudoHandle) return (ulong)ctx.Thread.Id;
        var obj = GetObject(ctx, handle);
        return obj is ThreadObject t ? (ulong)t.Thread.Id : 0;
    }

    [VirtualApi("kernel32.dll", "GetExitCodeProcess", ApiCategory.Process)]
    private static ulong GetExitCodeProcess(ApiCallContext ctx)
    {
        var p = GetProcessInformation(ctx, ctx.Arg(0));
        if (p is null) return BoolResult(ctx, false, Win32Error.InvalidHandle);
        if (ctx.Arg(1) != 0) ctx.Memory.Write(ctx.Arg(1), unchecked((uint)(p.ExitCode ?? 259)));
        return 1;
    }

    [VirtualApi("kernel32.dll", "GetExitCodeThread", ApiCategory.Thread)]
    private static ulong GetExitCodeThread(ApiCallContext ctx)
    {
        var obj = GetObject(ctx, ctx.Arg(0));
        if (obj is not ThreadObject t) return BoolResult(ctx, false, Win32Error.InvalidHandle);
        if (ctx.Arg(1) != 0)
            ctx.Memory.Write(ctx.Arg(1), t.Thread.GetExitCode());
        return 1;
    }

    [VirtualApi("kernel32.dll", "GetLastError", ApiCategory.System)]
    private static ulong GetLastError(ApiCallContext ctx) => (ulong)ctx.LastError;

    [VirtualApi("kernel32.dll", "SetLastError", ApiCategory.System)]
    private static ulong SetLastError(ApiCallContext ctx)
    {
        ctx.SetLastError(unchecked((int)ctx.Arg32(0)));
        return 0;
    }

    // --- termination ---------------------------------------------------------------
    [VirtualApi("kernel32.dll", "RaiseFailFastException", ApiCategory.Process, "kernelbase!RaiseFailFastException")]
    [VirtualApi("ntdll.dll", "RtlFailFast2", ApiCategory.Process)]
    private static ulong RaiseFailFastException(ApiCallContext ctx)
    {
        Span<byte> record = stackalloc byte[40];
        if (ctx.Arg(0) != 0 && ctx.Memory.TryRead(ctx.Arg(0), record))
            return ctx.FailFast(BitConverter.ToUInt32(record[24..]) > 0 ? (uint)BitConverter.ToUInt64(record[32..]) : 7,
                BitConverter.ToUInt32(record));
        return ctx.FailFast(7, NtStatus.FailFastException);
    }

    [VirtualApi("ntdll.dll", "RtlFailFast", ApiCategory.Process)]
    private static ulong RtlFailFast(ApiCallContext ctx) => ctx.FailFast(ctx.Arg32(0));

    [VirtualApi("ntdll.dll", "RtlReportCriticalFailure", ApiCategory.Process)]
    [VirtualApi("ntdll.dll", "RtlReportFatalFailure", ApiCategory.Process)]
    private static ulong ReportFatalFailure(ApiCallContext ctx) => ctx.FailFast(7, ctx.Arg32(0));

    [VirtualApi("kernel32.dll", "FatalAppExitA", ApiCategory.Process, "kernelbase!FatalAppExitA")]
    [VirtualApi("kernel32.dll", "FatalAppExitW", ApiCategory.Process, "kernelbase!FatalAppExitW")]
    private static ulong FatalAppExit(ApiCallContext ctx) => ctx.FailFast(7, NtStatus.FatalAppExit);

    [VirtualApi("kernel32.dll", "ExitProcess", ApiCategory.Process)]
    private static ulong ExitProcess(ApiCallContext ctx)
    {
        var code = unchecked((int)ctx.Arg32(0));
        TraceTermination(ctx, code, "ExitProcess");
        ctx.Process.RequestProcessExit(code, ctx);
        return 0;
    }

    private static void TraceTermination(ApiCallContext ctx, int code, string api)
    {
        var trace = ctx.Runtime.Trace;
        trace.Emit(TraceKind.Process,
            $"{api}(0x{(uint)code:X8}) caller=0x{ctx.ReturnAddress:X16} rsp=0x{ctx.ReturnAddressSlot:X16}",
            ctx.Process.Id, ctx.Thread.Id);
        if (!ctx.Runtime.Options.TraceNativeCalls || ctx.ReturnAddressSlot == 0) return;
        var stack = new ulong[32];
        var count = 0;
        for (; count < stack.Length; count++)
        {
            try { stack[count] = ctx.Memory.Read<ulong>(ctx.ReturnAddressSlot + (ulong)(count * 8)); }
            catch { break; }
        }
        trace.Emit(TraceKind.Process,
            "guest stack: " + string.Join(' ', stack.Take(count).Select(v => $"0x{v:X}")),
            ctx.Process.Id, ctx.Thread.Id);
    }

    [VirtualApi("kernel32.dll", "ExitThread", ApiCategory.Thread)]
    private static ulong ExitThread(ApiCallContext ctx)
    {
        ctx.Process.ExitCurrentThread(unchecked((int)ctx.Arg32(0)), ctx);
        return 0;
    }

    [VirtualApi("kernel32.dll", "TerminateProcess", ApiCategory.Process)]
    private static ulong TerminateProcess(ApiCallContext ctx)
    {
        var handle = ctx.Arg(0);
        var code = unchecked((int)ctx.Arg32(1));
        TraceTermination(ctx, code, "TerminateProcess");
        if (handle == CurrentProcessPseudoHandle)
        {
            ctx.Process.RequestProcessExit(code, ctx);
            return 1;
        }
        var status = ctx.Handles.TryGet(handle, 1 /* PROCESS_TERMINATE */, out var obj);
        if (status != NtStatus.Success) return BoolResult(ctx, false, Win32FromStatus(status));
        if (obj is SystemProcessObject) return BoolResult(ctx, false, Win32Error.AccessDenied);
        if (obj is ProcessObject p)
        {
            if (ReferenceEquals(p.Process, ctx.Process))
            {
                ctx.Process.RequestProcessExit(code, ctx);
                return 1;
            }
            p.Process.Terminate(code);
            return 1;
        }
        return BoolResult(ctx, false, Win32Error.InvalidHandle);
    }

    [VirtualApi("ntdll.dll", "NtTerminateProcess", ApiCategory.Process, "ntdll!ZwTerminateProcess")]
    private static ulong NtTerminateProcess(ApiCallContext ctx)
    {
        var handle = ctx.Arg(0);
        var code = unchecked((int)ctx.Arg32(1));
        TraceTermination(ctx, code, "NtTerminateProcess");
        if (handle == 0 || handle == CurrentProcessPseudoHandle)
        {
            ctx.Process.RequestProcessExit(code, ctx);
            return NtStatus.Success;
        }
        var status = ctx.Handles.TryGet(handle, 1 /* PROCESS_TERMINATE */, out var obj);
        if (status != NtStatus.Success) return status;
        if (obj is SystemProcessObject) return NtStatus.AccessDenied;
        if (obj is not ProcessObject process) return NtStatus.InvalidHandle;
        if (ReferenceEquals(process.Process, ctx.Process)) ctx.Process.RequestProcessExit(code, ctx);
        else process.Process.Terminate(code);
        return NtStatus.Success;
    }

    private static void TerminateGuestThread(ApiCallContext ctx, ThreadObject thread, int exitCode)
    {
        if (!thread.Thread.IsMain)
        {
            thread.Thread.Terminate(exitCode);
            return;
        }

        // A process exits when its last thread exits.  Terminating the main thread through a
        // handle must therefore signal the owning process object, just like ExitThread would.
        if (ReferenceEquals(thread.Thread.Process, ctx.Process))
        {
            ctx.Process.RequestProcessExit(exitCode, ctx);
            return;
        }

        thread.Thread.Terminate(exitCode);
        if (!thread.Thread.Process.IsImaginedProcess)
            thread.Thread.Process.OnMainThreadExited(thread.Thread);
    }

    [VirtualApi("kernel32.dll", "TerminateThread", ApiCategory.Thread)]
    private static ulong TerminateThread(ApiCallContext ctx)
    {
        var obj = GetObject(ctx, ctx.Arg(0));
        if (obj is not ThreadObject t) return BoolResult(ctx, false, Win32Error.InvalidHandle);
        TerminateGuestThread(ctx, t, unchecked((int)ctx.Arg32(1)));
        return 1;
    }

    [VirtualApi("ntdll.dll", "NtTerminateThread", ApiCategory.Thread)]
    private static ulong NtTerminateThread(ApiCallContext ctx)
    {
        if (ctx.Arg(0) == 0 || ctx.Arg(0) == CurrentThreadPseudoHandle)
        {
            ctx.Process.ExitCurrentThread(unchecked((int)ctx.Arg32(1)), ctx);
            return NtStatus.Success;
        }
        var obj = GetObject(ctx, ctx.Arg(0));
        if (obj is not ThreadObject t) return NtStatus.InvalidHandle;
        TerminateGuestThread(ctx, t, unchecked((int)ctx.Arg32(1)));
        return NtStatus.Success;
    }

    [VirtualApi("kernel32.dll", "FatalExit", ApiCategory.Process)]
    private static ulong FatalExit(ApiCallContext ctx)
    {
        ctx.Process.RequestProcessExit(unchecked((int)ctx.Arg32(0)), ctx);
        return 0;
    }

    // --- timing / system information ----------------------------------------------
    [VirtualApi("kernel32.dll", "GetTickCount", ApiCategory.System)]
    private static ulong GetTickCount(ApiCallContext ctx) => (ulong)(uint)Environment.TickCount64;

    [VirtualApi("kernel32.dll", "GetTickCount64", ApiCategory.System)]
    private static ulong GetTickCount64(ApiCallContext ctx) => (ulong)Environment.TickCount64;

    [VirtualApi("kernel32.dll", "QueryPerformanceCounter", ApiCategory.System)]
    private static ulong QueryPerformanceCounter(ApiCallContext ctx)
    {
        var value = System.Diagnostics.Stopwatch.GetTimestamp();
        if (ctx.Arg(0) != 0) ctx.Memory.Write(ctx.Arg(0), value);
        return 1;
    }

    [VirtualApi("kernel32.dll", "QueryPerformanceFrequency", ApiCategory.System)]
    private static ulong QueryPerformanceFrequency(ApiCallContext ctx)
    {
        if (ctx.Arg(0) != 0) ctx.Memory.Write(ctx.Arg(0), System.Diagnostics.Stopwatch.Frequency);
        return 1;
    }

    [VirtualApi("kernel32.dll", "GetSystemTimeAsFileTime", ApiCategory.System, "ntdll!NtQuerySystemTime")]
    private static ulong GetSystemTimeAsFileTime(ApiCallContext ctx)
    {
        if (ctx.Arg(0) != 0) ctx.Memory.Write(ctx.Arg(0), ToFileTime(DateTime.UtcNow));
        return 0;
    }

    [VirtualApi("kernel32.dll", "GetSystemTime", ApiCategory.System)]
    private static ulong GetSystemTime(ApiCallContext ctx)
    {
        WriteSystemTime(ctx, ctx.Arg(0), DateTime.UtcNow);
        return 0;
    }

    [VirtualApi("kernel32.dll", "GetLocalTime", ApiCategory.System)]
    private static ulong GetLocalTime(ApiCallContext ctx)
    {
        WriteSystemTime(ctx, ctx.Arg(0), DateTime.Now);
        return 0;
    }

    [VirtualApi("kernel32.dll", "Sleep", ApiCategory.Thread)]
    private static ulong Sleep(ApiCallContext ctx)
    {
        var ms = ctx.Arg32(0);
        Thread.Sleep((int)Math.Min(ms, int.MaxValue));
        return 0;
    }

    [VirtualApi("kernel32.dll", "SleepEx", ApiCategory.Thread)]
    private static ulong SleepEx(ApiCallContext ctx)
    {
        var ms = ctx.Arg32(0);
        Thread.Sleep((int)Math.Min(ms, int.MaxValue));
        return 0;
    }

    [VirtualApi("kernel32.dll", "SwitchToThread", ApiCategory.Thread)]
    private static ulong SwitchToThread(ApiCallContext ctx) => 1;

    [VirtualApi("kernel32.dll", "GetSystemInfo", ApiCategory.System)]
    private static ulong GetSystemInfo(ApiCallContext ctx)
    {
        WriteSystemInfo(ctx, ctx.Arg(0));
        return 0;
    }

    [VirtualApi("kernel32.dll", "GetNativeSystemInfo", ApiCategory.System)]
    private static ulong GetNativeSystemInfo(ApiCallContext ctx)
    {
        WriteSystemInfo(ctx, ctx.Arg(0));
        return 0;
    }

    // --- version reporting ----------------------------------------------------------
    private static readonly Lazy<Version> HostWindowsVersion = new(QueryHostWindowsVersion);

    internal static Version EffectiveWindowsVersion(ApiCallContext ctx) =>
        ctx.Runtime.Options.SimulatedWindowsVersion ?? HostWindowsVersion.Value;

    private static Version QueryHostWindowsVersion()
    {
        try
        {
            var info = new HostOsVersionInfo { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<HostOsVersionInfo>() };
            if (HostRtlGetVersion(ref info) == 0 && info.Major != 0)
                return new Version((int)info.Major, (int)info.Minor, (int)info.Build, 0);
        }
        catch
        {
            // Fall back to the managed view below.
        }
        return Environment.OSVersion.Version;
    }

    [VirtualApi("kernel32.dll", "GetVersion", ApiCategory.System,
        "api-ms-win-core-sysinfo-l1-1-0.dll!GetVersion", "kernelbase.dll!GetVersion")]
    private static ulong GetVersion(ApiCallContext ctx)
    {
        var version = EffectiveWindowsVersion(ctx);
        return (uint)(((version.Build & 0xFFFF) << 16) | ((version.Minor & 0xFF) << 8) | (version.Major & 0xFF));
    }

    [VirtualApi("kernel32.dll", "GetVersionExW", ApiCategory.System,
        "api-ms-win-core-sysinfo-l1-1-0.dll!GetVersionExW", "kernelbase.dll!GetVersionExW")]
    private static ulong GetVersionExW(ApiCallContext ctx) => ctx.Arg(0) == 0 ? 0 :
        WriteVersionInfo(ctx, ctx.Arg(0), ctx.Read<uint>(ctx.Arg(0)), unicode: true);

    [VirtualApi("kernel32.dll", "GetVersionExA", ApiCategory.System,
        "api-ms-win-core-sysinfo-l1-1-0.dll!GetVersionExA", "kernelbase.dll!GetVersionExA")]
    private static ulong GetVersionExA(ApiCallContext ctx) => ctx.Arg(0) == 0 ? 0 :
        WriteVersionInfo(ctx, ctx.Arg(0), ctx.Read<uint>(ctx.Arg(0)), unicode: false);

    [VirtualApi("ntdll.dll", "RtlGetVersion", ApiCategory.System)]
    private static ulong RtlGetVersion(ApiCallContext ctx)
    {
        var buffer = ctx.Arg(0);
        if (buffer == 0) return NtStatus.InvalidParameter;
        var size = ctx.Memory.Read<uint>(buffer);
        return WriteVersionInfo(ctx, buffer, size, unicode: true) != 0 ? NtStatus.Success : NtStatus.InvalidParameter;
    }

    [VirtualApi("ntdll.dll", "RtlGetNtVersionNumbers", ApiCategory.System)]
    private static ulong RtlGetNtVersionNumbers(ApiCallContext ctx)
    {
        var version = EffectiveWindowsVersion(ctx);
        if (ctx.Arg(0) != 0) ctx.Memory.Write(ctx.Arg(0), (uint)version.Major);
        if (ctx.Arg(1) != 0) ctx.Memory.Write(ctx.Arg(1), (uint)version.Minor);
        if (ctx.Arg(2) != 0) ctx.Memory.Write(ctx.Arg(2), (uint)version.Build);
        return 0;
    }

    private static ulong WriteVersionInfo(ApiCallContext ctx, ulong buffer, uint size, bool unicode)
    {
        var baseSize = unicode ? 0x114u : 0x94u;
        var exSize = unicode ? 0x11Cu : 0x9Cu;
        if (buffer == 0 || size < baseSize)
        {
            ctx.SetLastError(Win32Error.InsufficientBuffer);
            return 0;
        }

        var version = EffectiveWindowsVersion(ctx);
        ctx.Memory.Write(buffer, size);
        ctx.Memory.Write(buffer + 4, (uint)version.Major);
        ctx.Memory.Write(buffer + 8, (uint)version.Minor);
        ctx.Memory.Write(buffer + 12, (uint)version.Build);
        ctx.Memory.Write(buffer + 16, 2u); // VER_PLATFORM_WIN32_NT
        const int csdOffset = 20;
        var csdBytes = unicode ? 256 : 128;
        ctx.Memory.WriteBytes(buffer + csdOffset, new byte[csdBytes]);
        if (size >= exSize)
        {
            var ex = buffer + csdOffset + (ulong)csdBytes;
            ctx.Memory.Write(ex, (ushort)0);      // wServicePackMajor
            ctx.Memory.Write(ex + 2, (ushort)0);  // wServicePackMinor
            ctx.Memory.Write(ex + 4, (ushort)0);  // wSuiteMask
            ctx.Memory.Write(ex + 6, (byte)1);    // VER_NT_WORKSTATION
            ctx.Memory.Write(ex + 7, (byte)0);
        }
        return 1;
    }

    [VirtualApi("kernel32.dll", "VerifyVersionInfoW", ApiCategory.System,
        "api-ms-win-core-sysinfo-l1-1-0.dll!VerifyVersionInfoW", "kernelbase.dll!VerifyVersionInfoW")]
    private static ulong VerifyVersionInfoW(ApiCallContext ctx) => VerifyVersionInfo(ctx, unicode: true);

    [VirtualApi("kernel32.dll", "VerifyVersionInfoA", ApiCategory.System,
        "api-ms-win-core-sysinfo-l1-1-0.dll!VerifyVersionInfoA", "kernelbase.dll!VerifyVersionInfoA")]
    private static ulong VerifyVersionInfoA(ApiCallContext ctx) => VerifyVersionInfo(ctx, unicode: false);

    private static ulong VerifyVersionInfo(ApiCallContext ctx, bool unicode)
    {
        var info = ctx.Arg(0);
        var typeMask = ctx.Arg32(1);
        var conditionMask = ctx.Arg(2);
        if (info == 0) return BoolResult(ctx, false, Win32Error.InvalidParameter);

        var size = ctx.Memory.Read<uint>(info);
        var version = EffectiveWindowsVersion(ctx);
        var requiredMajor = ctx.Memory.Read<uint>(info + 4);
        var requiredMinor = ctx.Memory.Read<uint>(info + 8);
        var requiredBuild = ctx.Memory.Read<uint>(info + 12);
        var requiredPlatform = ctx.Memory.Read<uint>(info + 16);
        const int csdOffset = 20;
        var csdBytes = unicode ? 256 : 128;
        var ex = info + csdOffset + (ulong)csdBytes;
        var hasExFields = size >= (uint)(csdOffset + csdBytes + 8);
        var requiredSpMajor = hasExFields ? ctx.Memory.Read<ushort>(ex) : (ushort)0;
        var requiredSpMinor = hasExFields ? ctx.Memory.Read<ushort>(ex + 2) : (ushort)0;
        var requiredProductType = hasExFields ? ctx.Memory.Read<byte>(ex + 6) : (byte)0;

        static bool Matches(uint actual, uint required, ulong mask, int shift)
        {
            return ((mask >> shift) & 7) switch
            {
                1 => actual == required,   // VER_EQUAL
                2 => actual > required,    // VER_GREATER
                3 => actual >= required,   // VER_GREATER_EQUAL
                4 => actual < required,    // VER_LESS
                5 => actual <= required,   // VER_LESS_EQUAL
                6 => actual != required,   // VER_NOT_EQUAL
                _ => true
            };
        }

        if ((typeMask & 0x0001) != 0 && !Matches((uint)version.Minor, requiredMinor, conditionMask, 0) ||
            (typeMask & 0x0002) != 0 && !Matches((uint)version.Major, requiredMajor, conditionMask, 1) ||
            (typeMask & 0x0004) != 0 && !Matches((uint)version.Build, requiredBuild, conditionMask, 2) ||
            (typeMask & 0x0008) != 0 && !Matches(2, requiredPlatform, conditionMask, 3) ||
            (typeMask & 0x0010) != 0 && !Matches(0, requiredSpMinor, conditionMask, 4) ||
            (typeMask & 0x0020) != 0 && !Matches(0, requiredSpMajor, conditionMask, 5) ||
            (typeMask & 0x0080) != 0 && !Matches(1, requiredProductType, conditionMask, 7))
            return BoolResult(ctx, false, Win32Error.OldWinVersion);

        return 1;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential,
        CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct HostOsVersionInfo
    {
        public uint Size;
        public uint Major;
        public uint Minor;
        public uint Build;
        public uint Platform;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 128)]
        public string ServicePack;
        public ushort ServicePackMajor;
        public ushort ServicePackMinor;
        public ushort SuiteMask;
        public byte ProductType;
        public byte Reserved;
    }

    [System.Runtime.InteropServices.DllImport("ntdll.dll", EntryPoint = "RtlGetVersion")]
    private static extern int HostRtlGetVersion(ref HostOsVersionInfo info);

    // --- branding -------------------------------------------------------------------
    [VirtualApi("ext-ms-win-branding-winbrand-l1-1-0.dll", "BrandingFormatString", ApiCategory.System,
        "winbrand.dll!BrandingFormatString")]
    private static ulong BrandingFormatString(ApiCallContext ctx)
    {
        var options = ctx.Runtime.Options;
        if (options.CopyrightText is null && options.ProductName is null)
            return (ulong)(long)HostBrandingFormatString((nint)(long)ctx.Arg(0));

        var format = ctx.Arg(0) == 0 ? string.Empty : ctx.ReadUnicodeString(ctx.Arg(0));
        var version = EffectiveWindowsVersion(ctx);
        var product = options.ProductName ?? $"Windows {version.Major}.{version.Minor}";
        var copyright = options.CopyrightText ?? "Copyright © Microsoft Corporation. All rights reserved.";
        var text = format
            .Replace("%WINDOWS_COPYRIGHT%", copyright, StringComparison.Ordinal)
            .Replace("%WINDOWS_LONG%", product, StringComparison.Ordinal)
            .Replace("%WINDOWS_SHORT%", product, StringComparison.Ordinal)
            .Replace("%WINDOWS_PRODUCT%", product, StringComparison.Ordinal);
        var pointer = System.Runtime.InteropServices.Marshal.StringToHGlobalUni(text);
        return (ulong)(long)pointer;
    }

    [System.Runtime.InteropServices.DllImport("winbrand.dll", EntryPoint = "BrandingFormatString",
        CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern nint HostBrandingFormatString(nint format);

    [VirtualApi("kernel32.dll", "IsDebuggerPresent", ApiCategory.System)]
    private static ulong IsDebuggerPresent(ApiCallContext ctx) => 0;

    [VirtualApi("kernel32.dll", "SetUnhandledExceptionFilter", ApiCategory.System)]
    private static ulong SetUnhandledExceptionFilter(ApiCallContext ctx) => 0;

    [VirtualApi("kernel32.dll", "OutputDebugStringW", ApiCategory.System)]
    private static ulong OutputDebugStringW(ApiCallContext ctx)
    {
        var text = ReadUnicodeArg(ctx, 0);
        ctx.Runtime.Trace.Emit(TraceKind.Api, $"OutputDebugString: {text}", ctx.Process.Id, ctx.Thread.Id);
        return 0;
    }

    [VirtualApi("kernel32.dll", "OutputDebugStringA", ApiCategory.System)]
    private static ulong OutputDebugStringA(ApiCallContext ctx)
    {
        var text = ctx.Arg(0) == 0 ? null : ctx.ReadAnsiString(ctx.Arg(0));
        ctx.Runtime.Trace.Emit(TraceKind.Api, $"OutputDebugStringA: {text}", ctx.Process.Id, ctx.Thread.Id);
        return 0;
    }

    [VirtualApi("kernel32.dll", "RtlNtStatusToDosError", ApiCategory.System)]
    [VirtualApi("ntdll.dll", "RtlNtStatusToDosError", ApiCategory.System)]
    private static ulong RtlNtStatusToDosError(ApiCallContext ctx) => (ulong)Win32FromStatus(ctx.Arg32(0));

    // --- environment / command line ------------------------------------------------
    [VirtualApi("kernel32.dll", "GetCommandLineW", ApiCategory.Process)]
    private static ulong GetCommandLineW(ApiCallContext ctx) => ctx.Process.GetCommandLineBuffer();

    [VirtualApi("kernel32.dll", "GetCommandLineA", ApiCategory.Process)]
    private static ulong GetCommandLineA(ApiCallContext ctx)
    {
        // ANSI command line is rarely used; allocate lazily each call.
        return ctx.Process.WriteGuestString(ctx.Process.CommandLine, unicode: false);
    }

    [VirtualApi("kernel32.dll", "GetCurrentDirectoryW", ApiCategory.File)]
    private static ulong GetCurrentDirectoryW(ApiCallContext ctx)
    {
        var size = (int)Math.Min(ctx.Arg32(0), int.MaxValue);
        var buffer = ctx.Arg(1);
        var value = ctx.Process.CurrentDirectory;
        return WriteUtf16(ctx, buffer, size, value);
    }

    [VirtualApi("kernel32.dll", "SetCurrentDirectoryW", ApiCategory.File)]
    private static ulong SetCurrentDirectoryW(ApiCallContext ctx)
    {
        var value = ReadUnicodeArg(ctx, 0);
        if (string.IsNullOrEmpty(value)) return BoolResult(ctx, false, Win32Error.InvalidParameter);
        try
        {
            var host = ctx.Process.FileSystem.MapPath(value, ctx.Process.CurrentDirectory, FileOperation.Enum);
            if (!HostFileSystem.DirectoryExists(host))
            {
                ctx.Runtime.Trace.Warn($"SetCurrentDirectoryW('{value}') failed: current='{ctx.Process.CurrentDirectory}', host='{host}'",
                    ctx.Process.Id, ctx.Thread.Id);
                return BoolResult(ctx, false, Win32Error.PathNotFound);
            }
            ctx.Process.CurrentDirectory = ctx.Process.FileSystem.GetFullPath(value, ctx.Process.CurrentDirectory);
            return 1;
        }
        catch (NtFileException ex)
        {
            return BoolResult(ctx, false, Win32FromStatus(ex.Status));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return BoolResult(ctx, false, Win32Error.AccessDenied);
        }
    }

    [VirtualApi("kernel32.dll", "GetTempPathW", ApiCategory.File)]
    private static ulong GetTempPathW(ApiCallContext ctx)
    {
        var size = (int)Math.Min(ctx.Arg32(0), int.MaxValue);
        var buffer = ctx.Arg(1);
        const string temp = "C:\\Temp\\";
        return WriteUtf16(ctx, buffer, size, temp);
    }

    [VirtualApi("kernel32.dll", "GetWindowsDirectoryW", ApiCategory.System)]
    private static ulong GetWindowsDirectoryW(ApiCallContext ctx) =>
        WriteUtf16(ctx, ctx.Arg(1), (int)Math.Min(ctx.Arg32(0), int.MaxValue), "C:\\Windows");

    [VirtualApi("kernel32.dll", "GetSystemDirectoryW", ApiCategory.System)]
    private static ulong GetSystemDirectoryW(ApiCallContext ctx) =>
        WriteUtf16(ctx, ctx.Arg(1), (int)Math.Min(ctx.Arg32(0), int.MaxValue), "C:\\Windows\\System32");

    [VirtualApi("kernel32.dll", "GetComputerNameW", ApiCategory.System)]
    private static ulong GetComputerNameW(ApiCallContext ctx)
    {
        if (ctx.Arg(0) != 0)
        {
            var size = ctx.Arg(1);
            var capacity = size == 0 ? 0 : (int)Math.Min(ctx.Memory.Read<uint>(size), 1024);
            WriteUtf16(ctx, ctx.Arg(0), capacity, "IMAGINEDNT");
            if (size != 0) ctx.Memory.Write(size, (uint)"IMAGINEDNT".Length);
        }
        return 1;
    }

    // --- library / module loading --------------------------------------------------
    [VirtualApi("kernel32.dll", "GetModuleHandleW", ApiCategory.Library)]
    private static ulong GetModuleHandleW(ApiCallContext ctx) =>
        GetModuleHandle(ctx, ctx.Arg(0) == 0 ? null : ctx.ReadUnicodeString(ctx.Arg(0)));

    [VirtualApi("kernel32.dll", "GetModuleHandleA", ApiCategory.Library)]
    private static ulong GetModuleHandleA(ApiCallContext ctx) =>
        GetModuleHandle(ctx, ctx.Arg(0) == 0 ? null : ctx.ReadAnsiString(ctx.Arg(0)));

    private static ulong GetModuleHandle(ApiCallContext ctx, string? name)
    {
        if (string.IsNullOrEmpty(name) || MatchesMainImage(ctx, name)) return ctx.Process.Image?.BaseAddress ?? 0;
        name = ctx.Runtime.ResolveLibraryName(name);
        var managed = ctx.Process.FindModuleByName(name);
        if (managed is not null) return managed.BaseAddress;
        if (ctx.Runtime.FindImportedLibrary(name) is not null || ctx.Runtime.IsDllRedirectPath(name) ||
            (ctx.Runtime.Apis.IsPartiallyImagined(name) &&
                (!ImaginedRuntime.IsHostSystemModule(name) ||
                    ApiRegistry.NormalizeModule(name) is "msvcrt" or "ucrtbase")))
        {
            ctx.SetLastError(126);
            return 0;
        }
        var handle = ctx.Runtime.NativeApis.GetModuleHandle(name);
        if (handle == IntPtr.Zero) ctx.SetLastError(126);
        return (ulong)(long)handle;
    }

    [VirtualApi("kernel32.dll", "GetProcAddress", ApiCategory.Library)]
    private static ulong GetProcAddress(ApiCallContext ctx)
        => ResolveProcedureAddress(ctx, ctx.Arg(0), ctx.Arg(1));

    [VirtualApi("ntdll.dll", "LdrGetProcedureAddress", ApiCategory.Library)]
    private static ulong LdrGetProcedureAddress(ApiCallContext ctx)
    {
        if (ctx.Arg(3) == 0) return NtStatus.InvalidParameter;
        var name = ctx.Arg(1);
        if (name == 0 && ctx.Arg32(2) > ushort.MaxValue) return NtStatus.InvalidParameter;
        var address = name == 0
            ? ResolveProcedureAddress(ctx, ctx.Arg(0), ctx.Arg32(2))
            : ResolveProcedureAddress(ctx, ctx.Arg(0), 0,
                System.Text.Encoding.ASCII.GetString(ctx.Memory.ReadBytes(
                    ctx.Memory.Read<ulong>(name + 8), ctx.Memory.Read<ushort>(name))));
        ctx.Memory.Write(ctx.Arg(3), address);
        return address == 0 ? NtStatus.EntrypointNotFound : NtStatus.Success;
    }

    private static ulong ResolveProcedureAddress(ApiCallContext ctx, ulong module, ulong namePtr, string? exportName = null)
    {
        var address = exportName is null && namePtr < 0x1_0000
            ? ctx.Runtime.ResolveExport(ctx.Process, module, null, (ushort)namePtr)
            : ctx.Runtime.ResolveExport(ctx.Process, module, exportName ?? ctx.ReadAnsiString(namePtr));
        if (address == 0) ctx.SetLastError(127);
        return address;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, nint> ResourceModules =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Guest-mapped modules are not visible to the host loader, so a native FormatMessageW called
    /// by guest code with FORMAT_MESSAGE_FROM_HMODULE (or lpSource == 0, meaning "the calling
    /// module") cannot see the guest image's message table.  Load the backing file as a datafile
    /// once and retry with that handle.  System/string sources still go straight to the host.
    /// </summary>
    [VirtualApi("kernel32.dll", "FormatMessageW", ApiCategory.System)]
    private static ulong FormatMessageW(ApiCallContext ctx)
    {
        var flags = ctx.Arg32(0);
        var source = ctx.Arg(1);
        var messageId = ctx.Arg32(2);
        var languageId = ctx.Arg32(3);
        var buffer = ctx.Arg(4);
        var size = ctx.Arg32(5);
        var arguments = ctx.Arg(6);
        const uint FromModule = 0x0000_0800;
        if ((flags & FromModule) != 0)
        {
            var path = source == 0
                ? FindCallerModulePath(ctx)
                : ctx.Process.FindModule(source)?.Image.Path;
            nint module = 0;
            if (path is not null && !ResourceModules.TryGetValue(path, out module))
                module = LoadResourceModule(path);
            if (path is not null && module != 0)
            {
                var result = NativeFormatMessageW(flags, module, messageId, languageId, (nint)(long)buffer, size, (nint)(long)arguments);
                if (result != 0 || MarshalGetLastError() != 1511 /* MUI message not found: retry without MUI */)
                    return result;
            }
        }
        return NativeFormatMessageW(flags, (nint)(long)source, messageId, languageId, (nint)(long)buffer, size, (nint)(long)arguments);
    }

    private static string? FindCallerModulePath(ApiCallContext ctx)
    {
        foreach (var module in ctx.Process.LoadedModules)
            if (module.ContainsAddress(ctx.ReturnAddress)) return module.Image.Path;
        return ctx.Process.Image?.Image.Path;
    }

    private static nint LoadResourceModule(string path)
    {
        if (ResourceModules.TryGetValue(path, out var cached)) return cached;
        var handle = NativeLoadLibraryExW(path, 0, 0x2 /* LOAD_LIBRARY_AS_DATAFILE */);
        if (handle != 0) ResourceModules.TryAdd(path, handle);
        return handle;
    }

    [VirtualApi("user32.dll", "LoadStringW", ApiCategory.Library,
        "kernel32!LoadStringW", "api-ms-win-core-libraryloader-l1-2-0!LoadStringW")]
    private static ulong LoadStringW(ApiCallContext ctx)
    {
        var mapped = ctx.Arg(0) == 0 ? ctx.Process.Image : ctx.Process.FindModule(ctx.Arg(0));
        var module = mapped?.Image.Path is { } path ? LoadResourceModule(path) : (nint)(long)ctx.Arg(0);
        return (uint)NativeLoadStringW(module, ctx.Arg32(1), (nint)(long)ctx.Arg(2), ctx.ArgInt32(3));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "LoadStringW", SetLastError = true)]
    private static extern int NativeLoadStringW(nint module, uint id, nint buffer, int capacity);

    private static int MarshalGetLastError() =>
        (int)System.Runtime.InteropServices.Marshal.GetLastWin32Error();

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "FormatMessageW",
        CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern uint NativeFormatMessageW(uint flags, nint source, uint messageId, uint languageId,
        nint buffer, uint size, nint arguments);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW",
        CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern nint NativeLoadLibraryExW(string path, nint file, uint flags);

    [VirtualApi("kernel32.dll", "GetModuleFileNameW", ApiCategory.Library)]
    private static ulong GetModuleFileNameW(ApiCallContext ctx)
    {
        var module = ctx.Arg(0);
        var buffer = ctx.Arg(1);
        var size = (int)Math.Min(ctx.Arg32(2), int.MaxValue);
        var mapped = module == 0 ? ctx.Process.Image : ctx.Process.FindModule(module);
        if (mapped is null)
        {
            ctx.SetLastError(126);
            return 0;
        }
        var value = mapped.Image.Path ?? ctx.Process.ImagePath;
        var returnValue = WriteUtf16(ctx, buffer, size, value);
        if (returnValue + 1 > (ulong)size) ctx.SetLastError(122);
        return returnValue;
    }

    [VirtualApi("kernel32.dll", "LoadLibraryW", ApiCategory.Library)]
    private static ulong LoadLibraryW(ApiCallContext ctx) => LoadLibrary(ctx, ReadUnicodeArg(ctx, 0));

    [VirtualApi("kernel32.dll", "LoadLibraryA", ApiCategory.Library)]
    private static ulong LoadLibraryA(ApiCallContext ctx) =>
        LoadLibrary(ctx, ctx.Arg(0) == 0 ? null : ctx.ReadAnsiString(ctx.Arg(0)));

    private static ulong LoadLibrary(ApiCallContext ctx, string? name)
    {
        if (string.IsNullOrEmpty(name)) return 0;
        name = ctx.Runtime.ResolveLibraryName(name);
        if (ctx.Process.FindModuleByName(name) is { } existing) return existing.BaseAddress;
        if (ctx.Runtime.Options.PreferHostSystemDlls && ImaginedRuntime.IsHostSystemModule(name) &&
            ApiRegistry.NormalizeModule(name) is not ("msvcrt" or "ucrtbase"))
        {
            var hostModule = ctx.Runtime.NativeApis.GetModuleHandle(name);
            if (hostModule != 0) return (ulong)(long)hostModule;
        }

        try
        {
            var managedPath = ResolveManagedLibraryPath(ctx, name);
            if (managedPath is not null)
            {
                if (ctx.Runtime.Options.AllowNativeForwarding && ctx.Runtime.Options.ImportPolicy != ImportPolicy.ManagedOnly &&
                    ctx.Runtime.ShouldLoadLibraryNatively(managedPath))
                {
                    var native = ctx.Runtime.NativeApis.LoadLibrary(managedPath);
                    if (native == 0) ctx.SetLastError(126);
                    return native;
                }
                var module = ctx.Process.LoadManagedLibrary(managedPath);
                if (module is null)
                {
                    ctx.SetLastError(193); // ERROR_BAD_EXE_FORMAT
                    return 0;
                }
                return module.BaseAddress;
            }
        }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException or IOException)
        {
            ctx.Runtime.Trace.Warn($"{ctx.Name}('{name}') failed: {ex.Message}", ctx.Process.Id, ctx.Thread.Id);
            ctx.SetLastError(ex is FileNotFoundException or DirectoryNotFoundException ? 126 : 193);
            return 0;
        }

        if (ctx.Runtime.Apis.IsPartiallyImagined(name))
        {
            ctx.SetLastError(126);
            return 0;
        }
        if (!ctx.Runtime.Options.AllowNativeForwarding)
        {
            ctx.SetLastError(Win32Error.CallNotImplemented);
            return 0;
        }

        var handle = ctx.Runtime.NativeApis.LoadLibrary(name);
        if (handle == 0)
        {
            ctx.SetLastError(126);
            return 0;
        }
        return handle;
    }

    [VirtualApi("kernel32.dll", "FreeLibrary", ApiCategory.Library)]
    private static ulong FreeLibrary(ApiCallContext ctx)
    {
        var mapped = ctx.Process.FindModule(ctx.Arg(0));
        if (mapped is not null)
        {
            ctx.Runtime.Trace.Emit(TraceKind.Image, $"FreeLibrary({mapped.Image.FileName}) ignored; modules live for the process lifetime", ctx.Process.Id, ctx.Thread.Id);
            return 1;
        }
        return 1;
    }

    [VirtualApi("kernel32.dll", "GetModuleFileNameA", ApiCategory.Library)]
    private static ulong GetModuleFileNameA(ApiCallContext ctx)
    {
        var size = (int)Math.Min(ctx.Arg32(2), int.MaxValue);
        var mapped = ctx.Arg(0) == 0 ? ctx.Process.Image : ctx.Process.FindModule(ctx.Arg(0));
        var value = mapped?.Image.Path ?? ctx.Process.ImagePath;
        return WriteAnsi(ctx, ctx.Arg(1), size, value);
    }

    private static string? ResolveManagedLibraryPath(ApiCallContext ctx, string name)
        => ctx.Process.FindLibraryPath(name, ctx.Process.FindModule(ctx.ReturnAddress)?.Image.Path,
            ctx.Runtime.Apis.IsPartiallyImagined(name));

    private static bool MatchesMainImage(ApiCallContext ctx, string name)
    {
        var image = ctx.Process.Image;
        if (image is null) return false;
        var fileName = Path.GetFileName(name);
        if (string.Equals(fileName, image.Image.FileName, StringComparison.OrdinalIgnoreCase)) return true;
        return string.Equals(name, ctx.Process.ImagePath, StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteSystemTime(ApiCallContext ctx, ulong address, DateTime time)
    {
        if (address == 0) return;
        var values = new ushort[]
        {
            (ushort)time.Year, (ushort)time.Month, (ushort)time.DayOfWeek, (ushort)time.Day,
            (ushort)time.Hour, (ushort)time.Minute, (ushort)time.Second, (ushort)time.Millisecond
        };
        for (var i = 0; i < values.Length; i++) ctx.Memory.Write(address + (ulong)(i * 2), values[i]);
    }

    private static void WriteSystemInfo(ApiCallContext ctx, ulong address)
    {
        if (address == 0) return;
        ctx.Memory.Write(address, (ushort)9);                      // PROCESSOR_ARCHITECTURE_AMD64
        ctx.Memory.Write(address + 4, 0x1000u);                    // page size
        ctx.Memory.Write(address + 8, 0x0000_0000_0001_0000UL);    // minimum application address
        ctx.Memory.Write(address + 16, 0x0000_7FFF_FFFF_FFFFUL);   // maximum application address
        ctx.Memory.Write(address + 24, (ulong)Environment.ProcessorCount);
        ctx.Memory.Write(address + 32, (uint)Environment.ProcessorCount);
        ctx.Memory.Write(address + 36, 8664u);                     // processor type
        ctx.Memory.Write(address + 40, 0x10000u);                  // allocation granularity
        ctx.Memory.Write(address + 44, (ushort)6);
        ctx.Memory.Write(address + 46, (ushort)0);
    }
}
