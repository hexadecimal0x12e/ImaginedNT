using ImaginedNT.Core.Memory;
using ImaginedNT.Core.Runtime;

namespace ImaginedNT.Core.Com;

/// <summary>
/// Arguments and services available to a managed COM method implementation. Argument 0 is always
/// the interface <c>this</c> pointer; the remaining indices follow the COM method signature.
/// Methods return an <c>HRESULT</c> as <see cref="ulong"/> (the low 32 bits are the HRESULT).
/// </summary>
public sealed class ComCallContext
{
    internal ComCallContext(ApiCallContext api, ComInterfaceView view)
    {
        Api = api;
        Interface = view;
    }

    public ApiCallContext Api { get; }
    public ComInterfaceView Interface { get; }
    public ComObject Object => Interface.Object;
    public Guid InterfaceId => Interface.Definition.InterfaceId;
    public string InterfaceName => Interface.Definition.Name;

    public ImaginedProcess Process => Api.Process;
    public ImaginedThread Thread => Api.Thread;

    /// <summary>The COM interface pointer (<c>this</c>).</summary>
    public ulong This => Api.Arg(0);

    public ulong Arg(int index) => Api.Arg(index);
    public uint Arg32(int index) => Api.Arg32(index);
    public int ArgInt32(int index) => Api.ArgInt32(index);
    public bool ArgBool(int index) => Api.ArgBool(index);
    public ulong ArgPtr(int index) => Api.Arg(index);

    public VirtualMemoryManager Memory => Api.Memory;

    public ulong Succeeded() => ComHResult.AsReturnValue(ComHResult.S_OK);
    public ulong Failed(int hr) => ComHResult.AsReturnValue(hr);

    public bool TryReadGuid(int index, out Guid guid) =>
        ComMarshaling.TryReadGuid(Api.Memory, Arg(index), out guid);

    public Guid ReadGuid(int index, Guid fallback = default) =>
        TryReadGuid(index, out var guid) ? guid : fallback;

    public bool TryWriteGuid(int index, Guid guid) =>
        ComMarshaling.TryWriteGuid(Api.Memory, Arg(index), guid);

    public ulong ReadPointer(int index) => ComMarshaling.ReadInterface(Api.Memory, Arg(index));

    public bool TryWritePointer(int index, ulong value) =>
        ComMarshaling.TryWriteInterface(Api.Memory, Arg(index), value);

    public void WriteInt32(int index, int value) => Api.WriteUInt32IfNotNull(Arg(index), unchecked((uint)value));

    public string? ReadString(int index, bool unicode = true) =>
        Arg(index) == 0 ? null : unicode ? Api.ReadUnicodeString(Arg(index)) : Api.ReadAnsiString(Arg(index));

    public string? ReadBstr(int index) => ComMarshaling.ReadBstr(Api.Memory, Arg(index));

    public int ReadBstrLength(int index) => ComMarshaling.BstrLength(Api.Memory, Arg(index));

    /// <summary>Allocate a BSTR; the guest owns it and frees it with <c>SysFreeString</c>.</summary>
    public ulong AllocBstr(string? value) => ComMarshaling.AllocBstr(value);

    public void FreeBstr(ulong pointer) => ComMarshaling.FreeBstr(pointer);

    /// <summary>Write a newly allocated BSTR to a <c>BSTR*</c> parameter.</summary>
    public bool TryWriteBstr(int index, string? value)
    {
        var output = Arg(index);
        if (output == 0) return false;
        var pointer = AllocBstr(value);
        try
        {
            Api.Memory.Write(output, pointer);
            return true;
        }
        catch
        {
            FreeBstr(pointer);
            return false;
        }
    }

    public override string ToString() =>
        $"{InterfaceName}@{This:X} object={Object.Descriptor.Name}";
}
