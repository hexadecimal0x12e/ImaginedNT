using System.Runtime.InteropServices;
using ImaginedNT.Core.Memory;
using ImaginedNT.Core.Runtime;

namespace ImaginedNT.Apis.Com;

/// <summary>
/// Managed <c>oleaut32</c> BSTR functions. BSTRs are ordinary OLE strings allocated in host
/// memory, which guest code can read and native-forwarded APIs can consume unchanged.
/// </summary>
public static class OleAutApis
{
    // The MSVC import library binds these BSTR helpers by ordinal for some exports.
    [VirtualApi("oleaut32.dll", "SysAllocString", ApiCategory.Com)]
    [VirtualApi("oleaut32.dll", "#2", ApiCategory.Com)]
    private static ulong SysAllocString(ApiCallContext context)
    {
        if (context.Arg(0) == 0) return 0;
        return AllocateString(context, context.Arg(0), StringLength(context, context.Arg(0)), false);
    }

    [VirtualApi("oleaut32.dll", "SysAllocStringLen", ApiCategory.Com)]
    [VirtualApi("oleaut32.dll", "#4", ApiCategory.Com)]
    private static ulong SysAllocStringLen(ApiCallContext context)
    {
        return AllocateString(context, context.Arg(0), context.Arg32(1), false);
    }

    [VirtualApi("oleaut32.dll", "SysAllocStringByteLen", ApiCategory.Com)]
    private static ulong SysAllocStringByteLen(ApiCallContext context)
        => AllocateString(context, context.Arg(0), context.Arg32(1), true);

    private static ulong AllocateString(ApiCallContext c, ulong source, uint length, bool bytes)
    {
        var size = bytes ? length : (ulong)length * 2;
        if (size > uint.MaxValue - 6 || source != 0 && !c.Memory.IsReadable(source, size)) return 0;
        lock (c.Runtime.Memory.Gate)
        {
            if (!c.Runtime.Memory.PrepareHostAllocation(size + 6)) return 0;
            var result = bytes ? HostSysAllocStringByteLen((nint)source, length) : HostSysAllocStringLen((nint)source, length);
            if (result != 0) c.Runtime.Memory.TrackHostAllocation(c.Process.Id, 0, 0, (ulong)result,
                RuntimeMemory.ChargeSize(size + 6), HostMemoryKind.Bstr);
            return (ulong)result;
        }
    }

    [VirtualApi("oleaut32.dll", "SysFreeString", ApiCategory.Com)]
    [VirtualApi("oleaut32.dll", "#6", ApiCategory.Com)]
    private static ulong SysFreeString(ApiCallContext context)
    {
        lock (context.Runtime.Memory.Gate)
        {
            if (!ValidString(context, context.Arg(0))) return 0;
            if (context.Arg(0) != 0) Marshal.FreeBSTR((nint)context.Arg(0));
            context.Runtime.Memory.ForgetHostAllocation(0, context.Arg(0));
        }
        return 0;
    }

    [VirtualApi("oleaut32.dll", "SysReAllocString", ApiCategory.Com)]
    [VirtualApi("oleaut32.dll", "#3", ApiCategory.Com)]
    [VirtualApi("oleaut32.dll", "SysReAllocStringLen", ApiCategory.Com)]
    [VirtualApi("oleaut32.dll", "#5", ApiCategory.Com)]
    private static ulong ReallocateString(ApiCallContext c)
    {
        if (c.Arg(0) == 0 || !c.Memory.IsWritable(c.Arg(0), 8)) return 0;
        var length = c.Name is "SysReAllocStringLen" or "#5" ? c.Arg32(2)
            : c.Arg(1) == 0 ? 0 : StringLength(c, c.Arg(1));
        var size = (ulong)length * 2;
        if (size > uint.MaxValue - 6 || c.Arg(1) != 0 && !c.Memory.IsReadable(c.Arg(1), size)) return 0;
        lock (c.Runtime.Memory.Gate)
        {
            var previous = c.Read<ulong>(c.Arg(0));
            if (!ValidString(c, previous)) return 0;
            var clear = (c.Name is "SysReAllocString" or "#3") && c.Arg(1) == 0;
            if (clear)
            {
                if (previous != 0) Marshal.FreeBSTR((nint)previous);
                c.Runtime.Memory.ForgetHostAllocation(0, previous);
                c.Write(c.Arg(0), 0UL);
                return 1;
            }
            if (!c.Runtime.Memory.PrepareHostAllocation(size + 6, c.Runtime.Memory.GetHostBlockSize(0, previous))) return 0;
            var pointer = (nint)previous;
            var result = c.Name is "SysReAllocStringLen" or "#5"
                ? HostSysReAllocStringLen(ref pointer, (nint)c.Arg(1), length)
                : HostSysReAllocString(ref pointer, (nint)c.Arg(1));
            if (!result) return 0;
            c.Write(c.Arg(0), (ulong)pointer);
            if (pointer == 0) c.Runtime.Memory.ForgetHostAllocation(0, previous);
            else c.Runtime.Memory.TrackHostAllocation(c.Process.Id, 0, previous, (ulong)pointer,
                RuntimeMemory.ChargeSize(size + 6), HostMemoryKind.Bstr);
            return 1;
        }
    }

    [VirtualApi("oleaut32.dll", "SysStringLen", ApiCategory.Com)]
    [VirtualApi("oleaut32.dll", "#7", ApiCategory.Com)]
    private static ulong SysStringLen(ApiCallContext context) =>
        ByteLength(context, context.Arg(0)) / 2;

    [VirtualApi("oleaut32.dll", "SysStringByteLen", ApiCategory.Com)]
    private static ulong SysStringByteLen(ApiCallContext context)
    {
        return ByteLength(context, context.Arg(0));
    }

    private static uint ByteLength(ApiCallContext c, ulong pointer)
        => pointer < 4 || !c.Memory.IsReadable(pointer - 4, 4) ? 0 : c.Read<uint>(pointer - 4);

    private static uint StringLength(ApiCallContext c, ulong pointer)
    {
        Span<byte> buffer = stackalloc byte[1024];
        uint length = 0;
        while (length < int.MaxValue)
        {
            var address = pointer + (ulong)length * 2;
            var count = (int)Math.Min((ulong)buffer.Length, 4096 - (address & 4095));
            count = Math.Max(2, count & ~1);
            if (!c.Memory.TryRead(address, buffer[..count]))
                throw new NtMemoryException(ImaginedNT.Core.Nt.NtStatus.AccessViolation, "unreadable BSTR source");
            for (var i = 0; i < count; i += 2)
            {
                if (buffer[i] == 0 && buffer[i + 1] == 0) return length;
                length++;
            }
        }
        return length;
    }

    private static bool ValidString(ApiCallContext c, ulong pointer)
    {
        if (c.Runtime.Memory.CanUseHostBlock(pointer, HostMemoryKind.Bstr)) return true;
        c.Runtime.Trace.Warn($"{c.Name}: incompatible allocator for 0x{pointer:X}", c.Process.Id, c.Thread.Id);
        return false;
    }

    [DllImport("oleaut32.dll", EntryPoint = "SysAllocStringLen")]
    private static extern nint HostSysAllocStringLen(nint source, uint length);
    [DllImport("oleaut32.dll", EntryPoint = "SysAllocStringByteLen")]
    private static extern nint HostSysAllocStringByteLen(nint source, uint length);
    [DllImport("oleaut32.dll", EntryPoint = "SysReAllocString")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HostSysReAllocString(ref nint pointer, nint source);
    [DllImport("oleaut32.dll", EntryPoint = "SysReAllocStringLen")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HostSysReAllocStringLen(ref nint pointer, nint source, uint length);
}
