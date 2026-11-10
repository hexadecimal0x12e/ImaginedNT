using ImaginedNT.Core.Runtime;

namespace ImaginedNT.Core.Com;

/// <summary>
/// An in-process COM server DLL registered with the runtime (for example through
/// <c>--com-server</c>). The library is loaded lazily per imagined process; its
/// <c>DllGetClassObject</c>/<c>DllCanUnloadNow</c> exports participate in
/// <c>CoGetClassObject</c> and <c>CoFreeUnusedLibraries</c>.
/// </summary>
public sealed class ComServerRegistration
{
    public ComServerRegistration(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("server path must not be empty", nameof(path));
        Path = path;
    }

    public string Path { get; }

    public override string ToString() => Path;
}

/// <summary>Load state of one server library in one imagined process.</summary>
internal sealed class ComServerLoadState
{
    public required ComServerRegistration Registration { get; init; }
    public required ImaginedProcess Process { get; init; }
    public bool Guest { get; init; }
    public ulong GuestBase { get; init; }
    public IntPtr HostModule { get; init; }
    public ulong GetClassObject { get; init; }
    public ulong CanUnloadNow { get; init; }
    public int LoadCount { get; set; }
    public bool Failed { get; init; }
    public bool Unloaded { get; set; }
    public bool CanUnload { get; set; }

    public ulong ModuleHandle => Guest ? GuestBase : (ulong)(long)HostModule;

    public override string ToString() =>
        $"{Registration.Path} ({(Guest ? "guest" : "host")}, base=0x{ModuleHandle:X})";
}

/// <summary>A class object registered by guest code with <c>CoRegisterClassObject</c>.</summary>
internal sealed class GuestClassRegistration
{
    public required int ProcessId { get; init; }
    public required Guid ClassId { get; init; }
    public required ulong FactoryPointer { get; init; }
    public required uint Context { get; init; }
    public required uint Cookie { get; init; }
}
