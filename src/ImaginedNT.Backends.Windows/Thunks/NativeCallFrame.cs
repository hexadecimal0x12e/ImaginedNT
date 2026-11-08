using System.Runtime.InteropServices;

namespace ImaginedNT.Core.Thunks;

/// <summary>
/// The register/stack snapshot handed to the managed dispatcher by the universal x64 thunk.
/// The native layout is defined by <see cref="X64StubEmitter"/>; do not reorder fields.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 128)]
public unsafe struct NativeCallFrame
{
    [FieldOffset(0)] public ulong Rcx;
    [FieldOffset(8)] public ulong Rdx;
    [FieldOffset(16)] public ulong R8;
    [FieldOffset(24)] public ulong R9;

    /// <summary>Pointer to the first stack argument (at the caller's [rsp+0x20] before the call).</summary>
    [FieldOffset(32)] public ulong StackArgs;

    [FieldOffset(40)] public XmmValue Xmm0;
    [FieldOffset(56)] public XmmValue Xmm1;
    [FieldOffset(72)] public XmmValue Xmm2;
    [FieldOffset(88)] public XmmValue Xmm3;

    /// <summary>The return address into the guest image.</summary>
    [FieldOffset(104)] public ulong ReturnAddress;

    /// <summary>The thunk's API identifier (written by the per-import stub into r11).</summary>
    [FieldOffset(112)] public ulong ApiId;

    [FieldOffset(120)] private ulong _reserved;

    public readonly ulong Arg(int index) => index switch
    {
        0 => Rcx,
        1 => Rdx,
        2 => R8,
        3 => R9,
        _ => *(ulong*)(StackArgs + (ulong)(index - 4) * 8)
    };
}

[StructLayout(LayoutKind.Sequential, Size = 16)]
public struct XmmValue
{
    public ulong Low;
    public ulong High;
}
