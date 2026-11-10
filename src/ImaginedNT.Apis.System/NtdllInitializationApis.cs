using ImaginedNT.Core.Nt;

namespace ImaginedNT.Core.Runtime;

public static class NtdllInitializationApis
{
    [VirtualApi("ntdll.dll", "RtlPcToFileHeader", ApiCategory.Library)]
    private static unsafe ulong RtlPcToFileHeader(ApiCallContext ctx)
    {
        if (ctx.Process.FindModule(ctx.Arg(0)) is { } module)
        {
            if (ctx.Arg(1) != 0) ctx.Write(ctx.Arg(1), module.BaseAddress);
            return module.BaseAddress;
        }
        var address = ctx.Runtime.NativeApis.Resolve("ntdll.dll", "RtlPcToFileHeader", null, out _);
        return address == 0 ? 0 : ((delegate* unmanaged<ulong, ulong, ulong>)(nint)address)(ctx.Arg(0), ctx.Arg(1));
    }

    [PartiallyImaginedApi("ntdll.dll", "RtlEncodePointer", ApiCategory.Memory)]
    private static ulong RtlEncodePointer(ApiCallContext ctx) => HostEncodePointer(ctx.Arg(0));

    [PartiallyImaginedApi("ntdll.dll", "RtlDecodePointer", ApiCategory.Memory)]
    private static ulong RtlDecodePointer(ApiCallContext ctx) => HostDecodePointer(ctx.Arg(0));

    // Windows ntdll does not export swprintf; ReactOS ntdll does, and its guest implementation
    // must be used when that image is mapped.  This managed bridge is only a fallback for hosts
    // that expose the CRT helper through ntdll (see the _vswprintf entry point below).
    private static ulong Swprintf(ApiCallContext ctx)
    {
        if (ctx.ReturnAddressSlot == 0)
        {
            ctx.Runtime.Trace.Warn("swprintf: guest varargs stack is unavailable", ctx.Process.Id, ctx.Thread.Id);
            return unchecked((ulong)-1L);
        }
        // Home register varargs next to the remaining arguments on the x64 guest stack.
        ctx.Write(ctx.ReturnAddressSlot + 24, ctx.Arg(2));
        ctx.Write(ctx.ReturnAddressSlot + 32, ctx.Arg(3));
        return unchecked((ulong)(long)HostVswprintf(ctx.Arg(0), ctx.Arg(1), ctx.ReturnAddressSlot + 24));
    }

    [System.Runtime.InteropServices.DllImport("ntdll.dll", EntryPoint = "RtlEncodePointer")]
    private static extern ulong HostEncodePointer(ulong pointer);

    [System.Runtime.InteropServices.DllImport("ntdll.dll", EntryPoint = "RtlDecodePointer")]
    private static extern ulong HostDecodePointer(ulong pointer);

    [System.Runtime.InteropServices.DllImport("ntdll.dll", EntryPoint = "_vswprintf", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
    private static extern int HostVswprintf(ulong buffer, ulong format, ulong arguments);

    [VirtualApi("ntdll.dll", "RtlInitUnicodeString", ApiCategory.Memory)]
    private static ulong RtlInitUnicodeString(ApiCallContext ctx)
    {
        if (ctx.Arg(0) == 0) return 0;
        ushort length = 0;
        if (ctx.Arg(1) != 0)
        {
            while (length < 0xfffc && ctx.Read<ushort>(ctx.Arg(1) + length) != 0)
                length += 2;
        }
        ctx.Write(ctx.Arg(0), length);
        ctx.Write(ctx.Arg(0) + 2, ctx.Arg(1) == 0 ? (ushort)0 : (ushort)(length + 2));
        ctx.Write(ctx.Arg(0) + 8, ctx.Arg(1));
        return 0;
    }

    [VirtualApi("ntdll.dll", "DbgPrint", ApiCategory.System)]
    private static ulong DbgPrint(ApiCallContext ctx)
    {
        var format = ctx.Arg(0) == 0 ? null : ctx.ReadAnsiString(ctx.Arg(0));
        ctx.Runtime.Trace.Emit(ImaginedNT.Core.Diagnostics.TraceKind.Api, $"DbgPrint: {format}",
            ctx.Process.Id, ctx.Thread.Id);
        return 0;
    }

    [VirtualApi("ntdll.dll", "vDbgPrintExWithPrefix", ApiCategory.System)]
    private static ulong VDbgPrintExWithPrefix(ApiCallContext ctx)
    {
        var format = ctx.Arg(3) == 0 ? null : ctx.ReadAnsiString(ctx.Arg(3));
        ctx.Runtime.Trace.Emit(ImaginedNT.Core.Diagnostics.TraceKind.Api, $"DbgPrint: {format}",
            ctx.Process.Id, ctx.Thread.Id);
        return 0;
    }

    [VirtualApi("ntdll.dll", "RtlAssert", ApiCategory.System)]
    private static ulong RtlAssert(ApiCallContext ctx)
    {
        var expression = ctx.Arg(0) == 0 ? null : ctx.ReadAnsiString(ctx.Arg(0));
        ctx.Runtime.Trace.Warn($"RtlAssert: {expression}", ctx.Process.Id, ctx.Thread.Id);
        return 0;
    }

    // ReactOS ntdll's VEH list is initialized by its loader entry point, which is not run for a
    // DLL mapped by this runtime.  Registering the first handler therefore walks a zeroed list
    // (fault at +0x18).  Keep the guest bookkeeping harmless until we have a real guest VEH list.
    [VirtualApi("ntdll.dll", "RtlAddVectoredExceptionHandler", ApiCategory.System)]
    [VirtualApi("ntdll.dll", "RtlAddVectoredContinueHandler", ApiCategory.System)]
    private static ulong RtlAddVectoredHandler(ApiCallContext ctx) => 1;

    [VirtualApi("ntdll.dll", "RtlRemoveVectoredExceptionHandler", ApiCategory.System)]
    [VirtualApi("ntdll.dll", "RtlRemoveVectoredContinueHandler", ApiCategory.System)]
    private static ulong RtlRemoveVectoredHandler(ApiCallContext ctx) => 1;

    [VirtualApi("ntdll.dll", "RtlSetThreadPoolStartFunc", ApiCategory.Synchronization)]
    private static ulong RtlSetThreadPoolStartFunc(ApiCallContext ctx)
    {
        ctx.Process.ThreadPoolCallbacks = (ctx.Arg(0), ctx.Arg(1));
        return NtStatus.Success;
    }

    [VirtualApi("ntdll.dll", "LdrSetDllManifestProber", ApiCategory.System)]
    private static ulong LdrSetDllManifestProber(ApiCallContext ctx)
    {
        ctx.Process.ManifestCallbacks = (ctx.Arg(0), ctx.Arg(1), ctx.Arg(2));
        return 0;
    }

    [VirtualApi("ntdll.dll", "LdrDisableThreadCalloutsForDll", ApiCategory.System)]
    private static ulong LdrDisableThreadCalloutsForDll(ApiCallContext ctx)
    {
        var module = ctx.Process.FindModule(ctx.Arg(0));
        if (module is null || module.BaseAddress != ctx.Arg(0)) return NtStatus.InvalidParameter;
        if (module.Image.Tls is not null) return NtStatus.NotSupported;
        module.ThreadCalloutsDisabled = true;
        return NtStatus.Success;
    }

    [VirtualApi("ntdll.dll", "CsrClientConnectToServer", ApiCategory.System)]
    private static ulong CsrClientConnectToServer(ApiCallContext ctx)
    {
        if (ctx.Arg(4) != 0) ctx.Write<byte>(ctx.Arg(4), 0);
        if (ctx.Arg(2) != 0 && ctx.Arg(3) == 0) return NtStatus.InvalidParameter;
        // ReactOS kernel32 treats a failed CSR connection as fatal in DllMain.  There is no real
        // csrss in the host process, but the console/process APIs used by the guest are routed
        // through the managed layer; report a successful virtual connection and let the guest
        // continue.  Calls that actually need CSR will fail at their own API boundary.
        ctx.Runtime.Trace.Warn("CsrClientConnectToServer: using the virtual CSR server",
            ctx.Process.Id, ctx.Thread.Id);
        return NtStatus.Success;
    }
}
