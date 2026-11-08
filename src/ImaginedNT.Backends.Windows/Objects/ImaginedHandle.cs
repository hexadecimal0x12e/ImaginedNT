using ImaginedNT.Core.Native;
using Microsoft.Win32.SafeHandles;

namespace ImaginedNT.Core.Objects;

/// <summary>Owns a native NT handle exposed to the guest only through its handle table.</summary>
public sealed class ImaginedHandle : NtObject
{
    private readonly SafeFileHandle _handle;

    public ImaginedHandle(nint handle) => _handle = new SafeFileHandle(handle, true);
    public override ObjectType ObjectType => ObjectType.Unknown;
    public override string TypeName => nameof(ImaginedHandle);
    public override bool SupportsWait => true;
    public override nint GetNativeHandle() => _handle.DangerousGetHandle();
    public uint Wait(uint timeout) => NativeMethods.WaitForSingleObject(GetNativeHandle(), timeout);
    protected override void OnDispose() => _handle.Dispose();
}
