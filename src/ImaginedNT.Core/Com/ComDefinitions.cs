using System.Reflection;

namespace ImaginedNT.Core.Com;

/// <summary>COM/HRESULT constants used by the managed COM layer.</summary>
public static class ComHResult
{
    public const int S_OK = 0;
    public const int S_FALSE = 1;

    public const int E_NOTIMPL = unchecked((int)0x80004001);
    public const int E_NOINTERFACE = unchecked((int)0x80004002);
    public const int E_POINTER = unchecked((int)0x80004003);
    public const int E_FAIL = unchecked((int)0x80004005);
    public const int E_UNEXPECTED = unchecked((int)0x8000FFFF);
    public const int E_INVALIDARG = unchecked((int)0x80070057);
    public const int E_OUTOFMEMORY = unchecked((int)0x8007000E);

    public const int REGDB_E_CLASSNOTREG = unchecked((int)0x80040154);
    public const int CLASS_E_NOAGGREGATION = unchecked((int)0x80040110);
    public const int CLASS_E_CLASSNOTAVAILABLE = unchecked((int)0x80040111);
    public const int CO_E_CLASSSTRING = unchecked((int)0x800401F3);

    public const int CO_E_NOTINITIALIZED = unchecked((int)0x800401F0);
    public const int CO_E_NOTSUPPORTED = unchecked((int)0x80040100);
    public const int CO_E_OBJNOTREG = unchecked((int)0x800401FB);
    public const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);
    public const int RPC_E_WRONG_THREAD = unchecked((int)0x8001010E);

    public static bool Succeeded(int hr) => hr >= 0;
    public static bool Failed(int hr) => hr < 0;
    public static ulong AsReturnValue(int hr) => (ulong)(uint)hr;
}

public enum ComApartment
{
    None,
    Sta,
    Mta
}

public delegate ulong ComMethodHandler(ComCallContext context);

/// <summary>Identity and vtable layout of one COM interface (IUnknown occupies slots 0-2).</summary>
public sealed class ComInterfaceDefinition
{
    public ComInterfaceDefinition(Guid interfaceId, string name, IReadOnlyDictionary<int, string> methods)
    {
        InterfaceId = interfaceId;
        Name = name;
        Methods = methods;
        MaxSlot = methods.Count == 0 ? 2 : methods.Keys.Max();
    }

    public Guid InterfaceId { get; }
    public string Name { get; }

    /// <summary>Slot (3 and above) to method name.</summary>
    public IReadOnlyDictionary<int, string> Methods { get; }

    /// <summary>Highest occupied vtable slot, or 2 for an IUnknown-only interface.</summary>
    public int MaxSlot { get; }

    public string NameOf(int slot) => Methods.TryGetValue(slot, out var name) ? name : $"Slot{slot}";

    public override string ToString() => $"{Name} ({InterfaceId:B})";
}

/// <summary>A managed implementation of one COM method.</summary>
public sealed class ComMethodBinding
{
    public ComMethodBinding(MethodInfo method, int slot, string name, int hostSlot = -1)
    {
        Method = method;
        Slot = slot;
        Name = name;
        HostSlot = hostSlot >= 0 ? hostSlot : slot;
    }

    public MethodInfo Method { get; }
    public int Slot { get; }
    public string Name { get; }

    /// <summary>Host vtable slot for partially imagined forwarding (defaults to <see cref="Slot"/>).</summary>
    public int HostSlot { get; }

    /// <summary>True when the method belongs to a partially imagined class.</summary>
    public bool IsPartiallyImagined { get; init; }

    internal ComMethodHandler CreateHandler(object target)
    {
        try
        {
            return Method.CreateDelegate<ComMethodHandler>(target);
        }
        catch (ArgumentException)
        {
            // Non-public methods on some runtimes cannot be bound directly; use reflection.
            return context => Invoke(target, context);
        }
    }

    internal ulong Invoke(object target, ComCallContext context)
    {
        try
        {
            return (ulong)Method.Invoke(target, [context])!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    public override string ToString() => $"{Name}@slot{Slot}";
}

/// <summary>The managed implementations declared by one class for one COM interface.</summary>
public sealed class ComInterfaceBinding
{
    public ComInterfaceBinding(ComInterfaceDefinition definition,
        IReadOnlyDictionary<int, ComMethodBinding> methods)
    {
        Definition = definition;
        Methods = methods;
    }

    public ComInterfaceDefinition Definition { get; }

    /// <summary>Overridden/implemented slots. Slots absent from this map are forwarded (partial COM).</summary>
    public IReadOnlyDictionary<int, ComMethodBinding> Methods { get; }

    public override string ToString() => $"{Definition.Name} ({Methods.Count} method(s))";
}

/// <summary>Everything the runtime needs to create and dispatch one managed COM class.</summary>
public sealed class ComClassDescriptor
{
    public required Guid ClassId { get; init; }
    public required string Name { get; init; }
    public ComThreadingModel ThreadingModel { get; init; } = ComThreadingModel.Both;
    public required IReadOnlyList<ComInterfaceBinding> Interfaces { get; init; }

    /// <summary>Creates the managed state object. Also used by partially imagined classes for overrides.</summary>
    public Func<object>? Factory { get; init; }

    /// <summary>Host CLSID for partially imagined classes; null for fully managed classes.</summary>
    public Guid? HostClassId { get; init; }

    public bool IsPartiallyImagined => HostClassId is not null;

    /// <summary>Number of guest-visible objects currently alive for this class.</summary>
    public int LiveObjectCount;

    /// <summary>Number of outstanding IClassFactory::LockServer locks.</summary>
    public int LockCount;

    public override string ToString() => $"{Name} ({ClassId:B})";
}

/// <summary>One guest-visible interface pointer and its vtable.</summary>
public sealed class ComInterfaceView
{
    public required ComObject Object { get; init; }
    public required ComInterfaceDefinition Definition { get; init; }

    /// <summary>Address the guest receives as the interface pointer (points at the vtable pointer).</summary>
    public required ulong GuestPointer { get; init; }

    /// <summary>Guest-visible vtable address (array of method thunk addresses).</summary>
    public required ulong VtablePointer { get; init; }

    /// <summary>Host interface pointer used by forwarded slots; 0 for fully managed views.</summary>
    public required ulong HostInterfacePointer { get; init; }

    public bool IsPrimary { get; init; }

    public override string ToString() => $"{Definition.Name}@0x{GuestPointer:X16}";
}

/// <summary>Well-known COM identities and the built-in IClassFactory layout.</summary>
public static class ComBuiltins
{
    public static readonly Guid IUnknownId = new("00000000-0000-0000-c000-000000000046");
    public static readonly Guid IClassFactoryId = new("00000001-0000-0000-c000-000000000046");
    public static readonly Guid IMallocId = new("00000002-0000-0000-c000-000000000046");

    public static ComInterfaceDefinition ClassFactory { get; } = new(
        IClassFactoryId, "IClassFactory",
        new Dictionary<int, string>
        {
            [3] = "CreateInstance",
            [4] = "LockServer"
        });

    public static ComInterfaceDefinition Malloc { get; } = new(
        IMallocId, "IMalloc",
        new Dictionary<int, string>
        {
            [3] = "Alloc",
            [4] = "Realloc",
            [5] = "Free",
            [6] = "GetSize",
            [7] = "DidAlloc",
            [8] = "HeapMinimize"
        });
}

/// <summary>CLSCTX values accepted by <c>CoCreateInstance</c>.</summary>
[Flags]
public enum ComClassContext : uint
{
    InprocServer = 0x0001,
    InprocHandler = 0x0002,
    LocalServer = 0x0004,
    InprocServer16 = 0x0008,
    RemoteServer = 0x0010,
    InprocHandler16 = 0x0020,
    LocalServer16 = 0x0040,
    RemoteServer16 = 0x0080,
    NoCodeDownload = 0x0400,
    NoCustomMarshal = 0x1000,
    EnableCodeDownload = 0x2000,
    NoFailureLog = 0x4000,
    DisableAAA = 0x8000,
    EnableAAA = 0x10000,
    FromRot = 0x40000,
    ActivationContext = 0x80000,
    All = InprocServer | InprocHandler | LocalServer | RemoteServer
}
