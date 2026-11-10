using ImaginedNT.Core.Com;

namespace ImaginedNT.Apis.Com;

/// <summary>
/// The simplest COM component implemented by ImaginedNT itself. It is a fully managed ("Virtual
/// Api"-style) COM class: the host's COM registry is never consulted, and every vtable slot runs
/// in C#.
/// </summary>
/// <remarks>
/// The ABI is regular COM: <c>IUnknown</c> occupies slots 0-2, then
/// <code>
/// HRESULT Add(int a, int b, int* result);      // slot 3
/// HRESULT GetValue(int* value);                // slot 4
/// HRESULT SetValue(int value);                 // slot 5
/// HRESULT GetName(BSTR* name);                 // slot 6
/// </code>
/// <c>samples/comtest/comtest.c</c> drives it through <c>CoCreateInstance</c>.
/// </remarks>
public static class ImaginedComTest
{
    public const string InterfaceId = "{4D2C9A10-7B3E-4F1A-9C22-5D771B8E4A61}";
    public const string ClassId = "{E7A1B2C3-4D5E-6F70-8192-A3B4C5D6E7F8}";
    public const string Name = "Imagined.ComTest";

    public static readonly Guid InterfaceGuid = new(InterfaceId);
    public static readonly Guid ClassGuid = new(ClassId);

    public const string AddMethod = "Add";
    public const string GetValueMethod = "GetValue";
    public const string SetValueMethod = "SetValue";
    public const string GetNameMethod = "GetName";
}

/// <summary>Guest-facing contract of <see cref="ImaginedComTest"/>.</summary>
[VirtualComInterface(ImaginedComTest.InterfaceId, "IImaginedComTest")]
public interface IImaginedComTest
{
    [VirtualComMethod(ImaginedComTest.InterfaceId, 3, ImaginedComTest.AddMethod)]
    ulong Add(ComCallContext context);

    [VirtualComMethod(ImaginedComTest.InterfaceId, 4, ImaginedComTest.GetValueMethod)]
    ulong GetValue(ComCallContext context);

    [VirtualComMethod(ImaginedComTest.InterfaceId, 5, ImaginedComTest.SetValueMethod)]
    ulong SetValue(ComCallContext context);

    [VirtualComMethod(ImaginedComTest.InterfaceId, 6, ImaginedComTest.GetNameMethod)]
    ulong GetName(ComCallContext context);
}

[VirtualComClass(ImaginedComTest.ClassId, ImaginedComTest.Name, ThreadingModel = ComThreadingModel.Both)]
public sealed class ImaginedComTestObject : IImaginedComTest
{
    private int _value;

    public ulong Add(ComCallContext context)
    {
        var resultPointer = context.Arg(3);
        if (resultPointer == 0) return ComHResult.AsReturnValue(ComHResult.E_POINTER);
        context.WriteInt32(3, context.ArgInt32(1) + context.ArgInt32(2));
        return ComHResult.AsReturnValue(ComHResult.S_OK);
    }

    public ulong GetValue(ComCallContext context)
    {
        var valuePointer = context.Arg(1);
        if (valuePointer == 0) return ComHResult.AsReturnValue(ComHResult.E_POINTER);
        context.WriteInt32(1, _value);
        return ComHResult.AsReturnValue(ComHResult.S_OK);
    }

    public ulong SetValue(ComCallContext context)
    {
        _value = context.ArgInt32(1);
        return ComHResult.AsReturnValue(ComHResult.S_OK);
    }

    public ulong GetName(ComCallContext context)
    {
        if (context.Arg(1) == 0) return ComHResult.AsReturnValue(ComHResult.E_POINTER);
        return context.TryWriteBstr(1, "ImaginedNT")
            ? ComHResult.AsReturnValue(ComHResult.S_OK)
            : ComHResult.AsReturnValue(ComHResult.E_OUTOFMEMORY);
    }
}
