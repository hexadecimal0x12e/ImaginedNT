using System.Runtime.InteropServices;
using System.Text;
using ImaginedNT.Core.Memory;

namespace ImaginedNT.Core.Com;

/// <summary>
/// Parameter helpers shared by the COM APIs and managed COM method implementations. COM pointers
/// are plain addresses in the guest's address space (the guest executes in the host address space),
/// so GUIDs, interface pointers, BSTRs and strings are read/written through the process's
/// <see cref="VirtualMemoryManager"/>.
/// </summary>
public static class ComMarshaling
{
    public const int GuidSize = 16;

    public static bool TryReadGuid(VirtualMemoryManager memory, ulong address, out Guid guid)
    {
        guid = default;
        if (address == 0 || !memory.IsReadable(address, GuidSize)) return false;
        try
        {
            guid = new Guid(memory.ReadBytes(address, GuidSize));
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryWriteGuid(VirtualMemoryManager memory, ulong address, Guid guid)
    {
        if (address == 0) return false;
        try
        {
            memory.WriteBytes(address, guid.ToByteArray());
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static Guid ReadGuid(VirtualMemoryManager memory, ulong address) =>
        TryReadGuid(memory, address, out var guid) ? guid : Guid.Empty;

    /// <summary>Read a guest interface pointer (the value stored in a <c>void**</c>).</summary>
    public static ulong ReadInterface(VirtualMemoryManager memory, ulong address) =>
        address == 0 ? 0 : memory.Read<ulong>(address);

    /// <summary>Write an interface pointer to a guest <c>void**</c> output parameter.</summary>
    public static bool TryWriteInterface(VirtualMemoryManager memory, ulong address, ulong pointer)
    {
        if (address == 0) return false;
        try
        {
            memory.Write(address, pointer);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Allocate a real BSTR in host memory (guest-visible because both share an address space).</summary>
    public static ulong AllocBstr(string? value) =>
        (ulong)(long)Marshal.StringToBSTR(value ?? string.Empty);

    public static void FreeBstr(ulong pointer)
    {
        if (pointer != 0) Marshal.FreeBSTR((IntPtr)(long)pointer);
    }

    /// <summary>Read a BSTR without dereferencing an invalid pointer.</summary>
    public static string? ReadBstr(VirtualMemoryManager memory, ulong pointer)
    {
        if (pointer == 0) return null;
        try
        {
            if (pointer < 4 || !memory.IsReadable(pointer - 4, 4)) return null;
            var byteLength = memory.Read<int>(pointer - 4);
            if (byteLength < 0 || byteLength > 64 * 1024 * 1024) return null;
            if (!memory.IsReadable(pointer, (ulong)byteLength + 2)) return null;
            var bytes = memory.ReadBytes(pointer, byteLength);
            return Encoding.Unicode.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }

    public static int BstrLength(VirtualMemoryManager memory, ulong pointer)
    {
        if (pointer < 4) return 0;
        try
        {
            var byteLength = memory.IsReadable(pointer - 4, 4) ? memory.Read<int>(pointer - 4) : 0;
            return byteLength > 0 ? byteLength / 2 : 0;
        }
        catch
        {
            return 0;
        }
    }

    public static string FormatGuid(Guid guid) => "{" + guid.ToString("D").ToUpperInvariant() + "}";

    /// <summary>Read a NUL-terminated ANSI/Unicode string from guest memory, returning null for a null pointer.</summary>
    public static string? ReadString(VirtualMemoryManager memory, ulong address, bool unicode = true) =>
        address == 0 ? null : unicode ? memory.ReadUnicodeString(address) : memory.ReadAnsiString(address);

    /// <summary>Allocate a COM task-allocator string. The guest frees it with <c>CoTaskMemFree</c>.</summary>
    public static ulong AllocTaskString(string value) =>
        (ulong)(long)Marshal.StringToCoTaskMemUni(value);

    public static void FreeTaskMemory(ulong pointer)
    {
        if (pointer != 0) Marshal.FreeCoTaskMem((IntPtr)(long)pointer);
    }
}
