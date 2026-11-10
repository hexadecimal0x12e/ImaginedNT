using System.Runtime.InteropServices;

namespace ImaginedNT.Core.Com;

/// <summary>
/// Real host COM services used for two things: creating the host object that a partially imagined
/// class forwards to, and the fallback that lets a guest create an ordinary host COM component
/// when the managed layer has no registration for its CLSID.
/// </summary>
internal static class ComHostInterop
{
    private const uint CoInitMultithreaded = 0;
    private const int RpcChangedMode = unchecked((int)0x80010106);
    private const int SFalse = 1;

    [ThreadStatic] private static bool _initialized;

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid,
        out IntPtr pointer);

    [DllImport("ole32.dll")]
    private static extern int CoGetClassObject(ref Guid clsid, uint context, IntPtr reserved, ref Guid iid,
        out IntPtr pointer);

    [DllImport("ole32.dll")]
    private static extern int CoFreeUnusedLibraries();

    /// <summary>Initialize host COM (MTA) on this thread. Safe to call repeatedly.</summary>
    public static void EnsureHostInitialized()
    {
        if (_initialized) return;
        _initialized = true;
        // A host thread may already be initialized in another model; that is fine for forwarding.
        var hr = CoInitializeEx(IntPtr.Zero, CoInitMultithreaded);
        if (hr is not (0 or SFalse or RpcChangedMode))
            WriteHostTrace($"host CoInitializeEx returned 0x{hr:X8}");
    }

    public static int CreateInstance(Guid clsid, uint context, Guid iid, out IntPtr pointer)
    {
        EnsureHostInitialized();
        var actual = context == 0 ? (uint)ComClassContext.InprocServer : context;
        return CoCreateInstance(ref clsid, IntPtr.Zero, actual, ref iid, out pointer);
    }

    public static int GetClassObject(Guid clsid, uint context, Guid iid, out IntPtr pointer)
    {
        EnsureHostInitialized();
        return CoGetClassObject(ref clsid, context, IntPtr.Zero, ref iid, out pointer);
    }

    public static void FreeUnusedLibraries()
    {
        EnsureHostInitialized();
        CoFreeUnusedLibraries();
    }

    private static void WriteHostTrace(string message) =>
        Console.Error.WriteLine($"ImaginedNT COM: {message}");
}
