namespace ImaginedNT.Core.Com;

/// <summary>COM apartment/threading model used by a managed COM class.</summary>
public enum ComThreadingModel
{
    /// <summary>The object is bound to the apartment that created it.</summary>
    Apartment,
    /// <summary>The object can be called from any apartment/thread.</summary>
    Free,
    /// <summary>Apartment or free, selected by the creator. The managed runtime treats it as free.</summary>
    Both,
    /// <summary>Neutral apartment. Treated as free in this runtime.</summary>
    Neutral
}

/// <summary>
/// Declares the COM interface implemented by a managed interface. Method slots are declared with
/// <see cref="VirtualComMethodAttribute"/> on the interface methods; a
/// <see cref="VirtualComClassAttribute"/> implementation is bound to those slots through the C#
/// interface map.
/// </summary>
[AttributeUsage(AttributeTargets.Interface, AllowMultiple = false, Inherited = false)]
public sealed class VirtualComInterfaceAttribute : Attribute
{
    public VirtualComInterfaceAttribute(string interfaceId, string? name = null)
    {
        if (string.IsNullOrWhiteSpace(interfaceId))
            throw new ArgumentException("interface id must not be empty", nameof(interfaceId));
        InterfaceId = interfaceId;
        Name = name;
    }

    /// <summary>Interface GUID, e.g. <c>{4D2C9A10-7B3E-4F1A-9C22-5D771B8E4A61}</c>.</summary>
    public string InterfaceId { get; }

    /// <summary>Optional symbolic name; also usable from <c>VirtualComMethodAttribute</c>.</summary>
    public string? Name { get; }

    public override string ToString() => Name is null ? InterfaceId : $"{Name} ({InterfaceId})";
}

/// <summary>
/// Declares the vtable slot of a COM method. The first three slots are reserved for
/// <c>IUnknown</c> (<c>QueryInterface</c>, <c>AddRef</c>, <c>Release</c>), so interface methods
/// start at slot 3. Apply this attribute to the interface method; an implementation class that
/// implements the interface is bound through the interface map.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public class VirtualComMethodAttribute : Attribute
{
    public VirtualComMethodAttribute(string interfaceId, int slot, string? name = null)
    {
        if (string.IsNullOrWhiteSpace(interfaceId))
            throw new ArgumentException("interface id must not be empty", nameof(interfaceId));
        if (slot < 3)
            throw new ArgumentOutOfRangeException(nameof(slot), "COM interface methods start at vtable slot 3 (IUnknown occupies 0-2)");
        InterfaceId = interfaceId;
        Slot = slot;
        Name = name;
    }

    /// <summary>Interface GUID or symbolic name declared with <see cref="VirtualComInterfaceAttribute"/>.</summary>
    public string InterfaceId { get; }

    /// <summary>Zero-based vtable slot; 3 is the first method after <c>IUnknown</c>.</summary>
    public int Slot { get; }

    /// <summary>Diagnostics name; defaults to the CLR method name.</summary>
    public string? Name { get; }

    public override string ToString() => $"{InterfaceId}#{Slot}";
}

/// <summary>
/// Declares that a method of a <see cref="PartiallyImaginedComClassAttribute"/> class overrides
/// a COM vtable slot. Non-overridden slots are forwarded to the real host COM object described by
/// the class's <c>HostClassId</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class PartiallyImaginedComMethodAttribute : VirtualComMethodAttribute
{
    public PartiallyImaginedComMethodAttribute(string interfaceId, int slot, string? name = null)
        : base(interfaceId, slot, name)
    {
    }

    /// <summary>
    /// Host vtable slot to forward to. Defaults to <see cref="VirtualComMethodAttribute.Slot"/>;
    /// set it when the host interface has a different layout than the guest interface.
    /// </summary>
    public int HostSlot { get; set; } = -1;
}

/// <summary>
/// Registers a managed COM class: a CLSID, a parameterless constructor and one or more interface
/// implementations. <c>CoCreateInstance</c>/<c>CoGetClassObject</c> can create it without touching
/// the host's COM registry.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public class VirtualComClassAttribute : Attribute
{
    public VirtualComClassAttribute(string classId, string? name = null)
    {
        if (string.IsNullOrWhiteSpace(classId))
            throw new ArgumentException("class id must not be empty", nameof(classId));
        ClassId = classId;
        Name = name;
    }

    /// <summary>Class GUID, e.g. <c>{E7A1B2C3-4D5E-6F70-8192-A3B4C5D6E7F8}</c>.</summary>
    public string ClassId { get; }

    /// <summary>Optional display name.</summary>
    public string? Name { get; }

    /// <summary>COM threading model honored by the managed object dispatcher.</summary>
    public ComThreadingModel ThreadingModel { get; set; } = ComThreadingModel.Both;

    public override string ToString() => Name is null ? ClassId : $"{Name} ({ClassId})";
}

/// <summary>
/// Registers a managed class that reimplements part of a real COM component. Overridden methods
/// (marked with <see cref="PartiallyImaginedComMethodAttribute"/> or <see cref="VirtualComMethodAttribute"/>)
/// run in C#; every other vtable slot is forwarded to the host object created from
/// <see cref="HostClassId"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class PartiallyImaginedComClassAttribute : VirtualComClassAttribute
{
    public PartiallyImaginedComClassAttribute(string classId, string hostClassId, string? name = null)
        : base(classId, name)
    {
        if (string.IsNullOrWhiteSpace(hostClassId))
            throw new ArgumentException("host class id must not be empty", nameof(hostClassId));
        HostClassId = hostClassId;
    }

    /// <summary>CLSID of the host (real) COM component used for non-overridden slots.</summary>
    public string HostClassId { get; }
}
