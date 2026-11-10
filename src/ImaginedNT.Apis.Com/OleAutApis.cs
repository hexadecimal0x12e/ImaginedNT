using System.Runtime.InteropServices;
using System.Text;
using ImaginedNT.Core.Com;
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
        var text = context.Arg(0) == 0 ? string.Empty : context.ReadUnicodeString(context.Arg(0));
        return (ulong)(long)Marshal.StringToBSTR(text);
    }

    [VirtualApi("oleaut32.dll", "SysAllocStringLen", ApiCategory.Com)]
    [VirtualApi("oleaut32.dll", "#4", ApiCategory.Com)]
    private static ulong SysAllocStringLen(ApiCallContext context)
    {
        var length = context.Arg32(1);
        if (length > 0x1000_0000) return 0;
        var pointer = context.Arg(0);
        string text;
        if (pointer == 0)
        {
            text = new string('\0', (int)length);
        }
        else
        {
            var bytes = context.Memory.ReadBytes(pointer, checked((int)length * 2));
            text = Encoding.Unicode.GetString(bytes);
        }
        return (ulong)(long)Marshal.StringToBSTR(text);
    }

    [VirtualApi("oleaut32.dll", "SysFreeString", ApiCategory.Com)]
    [VirtualApi("oleaut32.dll", "#6", ApiCategory.Com)]
    private static ulong SysFreeString(ApiCallContext context)
    {
        if (context.Arg(0) != 0) Marshal.FreeBSTR((IntPtr)(long)context.Arg(0));
        return 0;
    }

    [VirtualApi("oleaut32.dll", "SysStringLen", ApiCategory.Com)]
    [VirtualApi("oleaut32.dll", "#7", ApiCategory.Com)]
    private static ulong SysStringLen(ApiCallContext context) =>
        (ulong)ComMarshaling.BstrLength(context.Memory, context.Arg(0));

    [VirtualApi("oleaut32.dll", "SysStringByteLen", ApiCategory.Com)]
    private static ulong SysStringByteLen(ApiCallContext context)
    {
        var pointer = context.Arg(0);
        if (pointer < 4 || !context.Memory.IsReadable(pointer - 4, 4)) return 0;
        return (uint)Math.Max(0, context.Memory.Read<int>(pointer - 4));
    }
}
