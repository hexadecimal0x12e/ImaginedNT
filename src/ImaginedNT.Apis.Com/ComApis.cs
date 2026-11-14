using System.Runtime.InteropServices;
using ImaginedNT.Core.Com;
using ImaginedNT.Core.Memory;
using ImaginedNT.Core.Runtime;

namespace ImaginedNT.Apis.Com;

/// <summary>
/// Managed <c>ole32</c>/<c>combase</c> surface. These APIs let a guest initialize an apartment,
/// create managed or partially imagined COM objects, use class factories, register its own class
/// objects, load in-process servers, and allocate COM task memory.
/// </summary>
public static class ComApis
{
    // ---------------------------------------------------------------------------
    // Apartment initialization
    // ---------------------------------------------------------------------------

    [VirtualApi("ole32.dll", "CoInitialize", ApiCategory.Com, "combase.dll!CoInitialize")]
    private static ulong CoInitialize(ApiCallContext context) =>
        (ulong)(uint)context.Runtime.Com.Initialize(context.Thread, 0x2 /* COINIT_APARTMENTTHREADED */);

    [VirtualApi("ole32.dll", "CoInitializeEx", ApiCategory.Com, "combase.dll!CoInitializeEx")]
    private static ulong CoInitializeEx(ApiCallContext context) =>
        (ulong)(uint)context.Runtime.Com.Initialize(context.Thread, context.Arg32(1));

    [VirtualApi("ole32.dll", "CoUninitialize", ApiCategory.Com, "combase.dll!CoUninitialize")]
    private static ulong CoUninitialize(ApiCallContext context)
    {
        context.Runtime.Com.Uninitialize(context.Thread);
        return ComHResult.AsReturnValue(ComHResult.S_OK);
    }

    [VirtualApi("ole32.dll", "CoGetApartmentType", ApiCategory.Com, "combase.dll!CoGetApartmentType")]
    private static ulong CoGetApartmentType(ApiCallContext context)
    {
        var apartment = context.Runtime.Com.GetApartment(context.Thread);
        if (apartment == ComApartment.None) return ComHResult.AsReturnValue(ComHResult.CO_E_NOTINITIALIZED);
        if (context.Arg(0) != 0)
            context.Memory.Write(context.Arg(0), apartment == ComApartment.Sta ? 0 : 1);
        if (context.Arg(1) != 0) context.Memory.Write(context.Arg(1), 0);
        return ComHResult.AsReturnValue(ComHResult.S_OK);
    }

    [VirtualApi("ole32.dll", "CoInitializeSecurity", ApiCategory.Com, "combase.dll!CoInitializeSecurity")]
    private static ulong CoInitializeSecurity(ApiCallContext context) =>
        ComHResult.AsReturnValue(ComHResult.S_OK);

    // ---------------------------------------------------------------------------
    // Object creation
    // ---------------------------------------------------------------------------

    [VirtualApi("ole32.dll", "CoCreateInstance", ApiCategory.Com, "combase.dll!CoCreateInstance")]
    private static ulong CoCreateInstance(ApiCallContext context)
    {
        if (!ComMarshaling.TryReadGuid(context.Memory, context.Arg(0), out var classId) ||
            !ComMarshaling.TryReadGuid(context.Memory, context.Arg(3), out var interfaceId))
            return ComHResult.AsReturnValue(ComHResult.E_INVALIDARG);
        var output = context.Arg(4);
        if (output == 0) return ComHResult.AsReturnValue(ComHResult.E_POINTER);

        var hr = context.Runtime.Com.CreateInstance(context.Thread, classId, context.Arg(1), context.Arg32(2),
            interfaceId, out var pointer);
        try { context.Memory.Write(output, ComHResult.Succeeded(hr) ? pointer : 0UL); }
        catch { return ComHResult.AsReturnValue(ComHResult.E_POINTER); }
        return ComHResult.AsReturnValue(hr);
    }

    [VirtualApi("ole32.dll", "CoCreateInstanceEx", ApiCategory.Com, "combase.dll!CoCreateInstanceEx")]
    private static ulong CoCreateInstanceEx(ApiCallContext context)
    {
        // HRESULT CoCreateInstanceEx(REFCLSID, IUnknown *pUnkOuter, DWORD dwClsCtx,
        //                            COSERVERINFO *pServerInfo, DWORD dwCount, MULTI_QI *pResults)
        if (!ComMarshaling.TryReadGuid(context.Memory, context.Arg(0), out var classId))
            return ComHResult.AsReturnValue(ComHResult.E_INVALIDARG);
        var count = context.Arg32(4);
        var results = context.Arg(5);
        if (count == 0 || results == 0) return ComHResult.AsReturnValue(ComHResult.E_INVALIDARG);
        if (context.Arg(3) != 0) return ComHResult.AsReturnValue(ComHResult.CO_E_NOTSUPPORTED);
        if (count > 0x1000) return ComHResult.AsReturnValue(ComHResult.E_INVALIDARG);

        // MULTI_QI (x64): const IID* pIID; HRESULT hr; IUnknown* pItf (size 24).
        const uint multiQiSize = 24;
        var firstFailure = ComHResult.S_OK;
        var anySuccess = false;
        for (var index = 0u; index < count; index++)
        {
            var entry = results + index * multiQiSize;
            var iidPointer = context.Memory.Read<ulong>(entry);
            if (!ComMarshaling.TryReadGuid(context.Memory, iidPointer, out var interfaceId))
            {
                context.Memory.Write(entry + 8, unchecked((uint)ComHResult.E_INVALIDARG));
                context.Memory.Write(entry + 16, 0UL);
                if (firstFailure == ComHResult.S_OK) firstFailure = ComHResult.E_INVALIDARG;
                continue;
            }

            var hr = context.Runtime.Com.CreateInstance(context.Thread, classId, context.Arg(1),
                context.Arg32(2), interfaceId, out var pointer);
            context.Memory.Write(entry + 8, (uint)hr);
            context.Memory.Write(entry + 16, ComHResult.Succeeded(hr) ? pointer : 0UL);
            if (ComHResult.Succeeded(hr)) anySuccess = true;
            else if (firstFailure == ComHResult.S_OK) firstFailure = hr;
        }
        return ComHResult.AsReturnValue(anySuccess ? ComHResult.S_OK : firstFailure);
    }

    [VirtualApi("ole32.dll", "CoGetClassObject", ApiCategory.Com, "combase.dll!CoGetClassObject")]
    private static ulong CoGetClassObject(ApiCallContext context)
    {
        if (!ComMarshaling.TryReadGuid(context.Memory, context.Arg(0), out var classId) ||
            !ComMarshaling.TryReadGuid(context.Memory, context.Arg(3), out var interfaceId))
            return ComHResult.AsReturnValue(ComHResult.E_INVALIDARG);
        var output = context.Arg(4);
        if (output == 0) return ComHResult.AsReturnValue(ComHResult.E_POINTER);
        var hr = context.Runtime.Com.GetClassObject(context.Thread, classId, context.Arg32(1), interfaceId,
            out var pointer);
        try { context.Memory.Write(output, ComHResult.Succeeded(hr) ? pointer : 0UL); }
        catch { return ComHResult.AsReturnValue(ComHResult.E_POINTER); }
        return ComHResult.AsReturnValue(hr);
    }

    // ---------------------------------------------------------------------------
    // Guest class objects
    // ---------------------------------------------------------------------------

    [VirtualApi("ole32.dll", "CoRegisterClassObject", ApiCategory.Com, "combase.dll!CoRegisterClassObject")]
    private static ulong CoRegisterClassObject(ApiCallContext context)
    {
        // HRESULT CoRegisterClassObject(REFCLSID, IUnknown*, DWORD, DWORD, LPDWORD)
        if (!ComMarshaling.TryReadGuid(context.Memory, context.Arg(0), out var classId))
            return ComHResult.AsReturnValue(ComHResult.E_INVALIDARG);
        if (context.Arg(1) == 0) return ComHResult.AsReturnValue(ComHResult.E_INVALIDARG);
        var cookiePointer = context.Arg(4);
        if (cookiePointer == 0) return ComHResult.AsReturnValue(ComHResult.E_POINTER);
        var cookie = context.Runtime.Com.RegisterGuestClass(context.Process, classId, context.Arg(1), context.Arg32(2));
        if (cookie == 0) return ComHResult.AsReturnValue(ComHResult.E_FAIL);
        context.Memory.Write(cookiePointer, cookie);
        return ComHResult.AsReturnValue(ComHResult.S_OK);
    }

    [VirtualApi("ole32.dll", "CoRevokeClassObject", ApiCategory.Com, "combase.dll!CoRevokeClassObject")]
    private static ulong CoRevokeClassObject(ApiCallContext context)
    {
        var hr = context.Runtime.Com.RevokeGuestClass(context.Process, context.Arg32(0));
        return ComHResult.AsReturnValue(hr);
    }

    // ---------------------------------------------------------------------------
    // Task memory and GUID helpers
    // ---------------------------------------------------------------------------

    [VirtualApi("ole32.dll", "CoTaskMemAlloc", ApiCategory.Com, "combase.dll!CoTaskMemAlloc")]
    private static ulong CoTaskMemAlloc(ApiCallContext context)
        => AllocateTaskMemory(context, context.Arg(0));

    private static ulong AllocateTaskMemory(ApiCallContext context, ulong size)
    {
        lock (context.Runtime.Memory.Gate)
        {
            if (!context.Runtime.Memory.PrepareHostAllocation(size)) return 0;
            var result = HostCoTaskMemAlloc((nuint)size);
            if (result != 0) context.Runtime.Memory.TrackHostAllocation(context.Process.Id, 0, 0, (ulong)result,
                RuntimeMemory.ChargeSize(size), HostMemoryKind.ComTask);
            return (ulong)result;
        }
    }

    [VirtualApi("ole32.dll", "CoTaskMemFree", ApiCategory.Com, "combase.dll!CoTaskMemFree")]
    private static ulong CoTaskMemFree(ApiCallContext context)
    {
        lock (context.Runtime.Memory.Gate)
        {
            if (!ValidTaskMemory(context, context.Arg(0))) return 0;
            if (context.Arg(0) != 0) Marshal.FreeCoTaskMem((nint)context.Arg(0));
            context.Runtime.Memory.ForgetHostAllocation(0, context.Arg(0));
        }
        return 0;
    }

    [VirtualApi("ole32.dll", "CoTaskMemRealloc", ApiCategory.Com, "combase.dll!CoTaskMemRealloc")]
    private static ulong CoTaskMemRealloc(ApiCallContext context)
    {
        var pointer = context.Arg(0);
        var size = context.Arg(1);
        lock (context.Runtime.Memory.Gate)
        {
            if (!ValidTaskMemory(context, pointer)) return 0;
            if (pointer != 0 && size == 0)
            {
                Marshal.FreeCoTaskMem((nint)pointer);
                context.Runtime.Memory.ForgetHostAllocation(0, pointer);
                return 0;
            }
            var oldSize = context.Runtime.Memory.GetHostBlockSize(0, pointer);
            if (!context.Runtime.Memory.PrepareHostAllocation(size, oldSize)) return 0;
            var result = HostCoTaskMemRealloc((nint)pointer, (nuint)size);
            if (result != 0) context.Runtime.Memory.TrackHostAllocation(context.Process.Id, 0, pointer, (ulong)result,
                RuntimeMemory.ChargeSize(size), HostMemoryKind.ComTask);
            else if (size == 0) context.Runtime.Memory.ForgetHostAllocation(0, pointer);
            return (ulong)result;
        }
    }

    private static bool ValidTaskMemory(ApiCallContext context, ulong pointer)
    {
        if (context.Runtime.Memory.CanUseHostBlock(pointer, HostMemoryKind.ComTask)) return true;
        context.Runtime.Trace.Warn($"{context.Name}: incompatible allocator for 0x{pointer:X}", context.Process.Id, context.Thread.Id);
        return false;
    }

    [DllImport("ole32.dll", EntryPoint = "CoTaskMemAlloc")]
    private static extern nint HostCoTaskMemAlloc(nuint size);

    [DllImport("ole32.dll", EntryPoint = "CoTaskMemRealloc")]
    private static extern nint HostCoTaskMemRealloc(nint memory, nuint size);

    [VirtualApi("ole32.dll", "CoCreateGuid", ApiCategory.Com, "combase.dll!CoCreateGuid")]
    private static ulong CoCreateGuid(ApiCallContext context)
    {
        if (context.Arg(0) == 0) return ComHResult.AsReturnValue(ComHResult.E_POINTER);
        context.Memory.WriteBytes(context.Arg(0), Guid.NewGuid().ToByteArray());
        return ComHResult.AsReturnValue(ComHResult.S_OK);
    }

    [VirtualApi("ole32.dll", "StringFromCLSID", ApiCategory.Com, "combase.dll!StringFromCLSID")]
    [VirtualApi("ole32.dll", "StringFromIID", ApiCategory.Com, "combase.dll!StringFromIID")]
    private static ulong StringFromGuid(ApiCallContext context)
    {
        if (!ComMarshaling.TryReadGuid(context.Memory, context.Arg(0), out var guid))
            return ComHResult.AsReturnValue(ComHResult.E_INVALIDARG);
        var output = context.Arg(1);
        if (output == 0) return ComHResult.AsReturnValue(ComHResult.E_POINTER);
        if (!context.Memory.IsWritable(output, 8)) return ComHResult.AsReturnValue(ComHResult.E_POINTER);
        var bytes = System.Text.Encoding.Unicode.GetBytes(ComMarshaling.FormatGuid(guid) + '\0');
        var pointer = AllocateTaskMemory(context, (ulong)bytes.Length);
        context.Write(output, pointer);
        if (pointer == 0) return ComHResult.AsReturnValue(ComHResult.E_OUTOFMEMORY);
        context.WriteBytes(pointer, bytes);
        return ComHResult.AsReturnValue(ComHResult.S_OK);
    }

    [VirtualApi("ole32.dll", "CLSIDFromString", ApiCategory.Com, "combase.dll!CLSIDFromString")]
    [VirtualApi("ole32.dll", "IIDFromString", ApiCategory.Com, "combase.dll!IIDFromString")]
    private static ulong GuidFromString(ApiCallContext context)
    {
        var input = context.Arg(0);
        if (input == 0) return ComHResult.AsReturnValue(ComHResult.E_INVALIDARG);
        var text = context.ReadUnicodeString(input);
        if (!Guid.TryParse(text, out var guid))
            return ComHResult.AsReturnValue(ComHResult.CO_E_CLASSSTRING);
        var output = context.Arg(1);
        if (output == 0) return ComHResult.AsReturnValue(ComHResult.E_POINTER);
        context.Memory.WriteBytes(output, guid.ToByteArray());
        return ComHResult.AsReturnValue(ComHResult.S_OK);
    }

    // ---------------------------------------------------------------------------
    // In-process server loading and process information
    // ---------------------------------------------------------------------------

    [VirtualApi("ole32.dll", "CoLoadLibrary", ApiCategory.Com, "combase.dll!CoLoadLibrary")]
    private static ulong CoLoadLibrary(ApiCallContext context)
    {
        var path = context.Arg(0) == 0 ? null : context.ReadUnicodeString(context.Arg(0));
        return context.Runtime.Com.LoadLibrary(context.Process, path, context.ArgBool(1));
    }

    [VirtualApi("ole32.dll", "CoFreeLibrary", ApiCategory.Com, "combase.dll!CoFreeLibrary")]
    private static ulong CoFreeLibrary(ApiCallContext context) =>
        context.Runtime.Com.FreeLibrary(context.Process, context.Arg(0)) ? 1UL : 0UL;

    [VirtualApi("ole32.dll", "CoFreeUnusedLibraries", ApiCategory.Com, "combase.dll!CoFreeUnusedLibraries")]
    private static ulong CoFreeUnusedLibraries(ApiCallContext context)
    {
        context.Runtime.Com.FreeUnusedLibraries();
        return 0;
    }

    [VirtualApi("ole32.dll", "CoGetCurrentProcess", ApiCategory.Com, "combase.dll!CoGetCurrentProcess")]
    private static ulong CoGetCurrentProcess(ApiCallContext context) => (ulong)context.Process.Id;

    [VirtualApi("ole32.dll", "CoGetCurrentLogicalThreadId", ApiCategory.Com,
        "combase.dll!CoGetCurrentLogicalThreadId")]
    private static ulong CoGetCurrentLogicalThreadId(ApiCallContext context)
    {
        if (context.Arg(0) == 0) return ComHResult.AsReturnValue(ComHResult.E_POINTER);
        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes, context.Thread.Id);
        bytes[4] = 0x4E; bytes[5] = 0x54; bytes[6] = 0x49; bytes[7] = 0x4D;
        bytes[8] = 0x41; bytes[9] = 0x47; bytes[10] = 0x49; bytes[11] = 0x4E;
        bytes[12] = 0x45; bytes[13] = 0x44; bytes[14] = 0x43; bytes[15] = 0x4F;
        context.Memory.WriteBytes(context.Arg(0), bytes);
        return ComHResult.AsReturnValue(ComHResult.S_OK);
    }

    [VirtualApi("ole32.dll", "CoGetCallerTID", ApiCategory.Com, "combase.dll!CoGetCallerTID")]
    private static ulong CoGetCallerTID(ApiCallContext context)
    {
        if (context.Arg(0) != 0) context.Memory.Write(context.Arg(0), 0u);
        return ComHResult.AsReturnValue(ComHResult.S_OK);
    }

    [VirtualApi("ole32.dll", "CoSetProxyBlanket", ApiCategory.Com, "combase.dll!CoSetProxyBlanket")]
    private static ulong CoSetProxyBlanket(ApiCallContext context) =>
        ComHResult.AsReturnValue(ComHResult.S_OK);
}
