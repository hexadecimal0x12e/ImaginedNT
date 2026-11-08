using ImaginedNT.Core.Memory;
using ImaginedNT.Core.Nt;

namespace ImaginedNT.Core.Objects;

/// <summary>Managed view of a Windows OBJECT_ATTRIBUTES structure read out of guest memory.</summary>
public sealed class ObjectAttributes
{
    public uint Length { get; init; }
    public ulong RootDirectory { get; init; }
    public string? Name { get; init; }
    public string? FullPath { get; init; }
    public uint Attributes { get; init; }
    public ulong SecurityDescriptor { get; init; }
    public ulong SecurityQualityOfService { get; init; }

    public bool CaseInsensitive => (Attributes & (uint)ObjectAttributesFlags.CaseInsensitive) != 0;

    public static ObjectAttributes Read(VirtualMemoryManager memory, Func<ulong, string?> resolveHandlePath, ulong address)
    {
        if (address == 0) return new ObjectAttributes();
        var length = memory.Read<uint>(address);
        var root = length >= 0x10 ? memory.Read<ulong>(address + 8) : 0;
        var namePointer = length >= 0x18 ? memory.Read<ulong>(address + 0x10) : 0;
        var attributes = length >= 0x1C ? memory.Read<uint>(address + 0x18) : 0;
        var securityDescriptor = length >= 0x28 ? memory.Read<ulong>(address + 0x20) : 0;
        var securityQos = length >= 0x30 ? memory.Read<ulong>(address + 0x28) : 0;

        string? name = null;
        if (namePointer != 0)
        {
            var nameLength = memory.Read<ushort>(namePointer);
            var nameBuffer = memory.Read<ulong>(namePointer + 8);
            if (nameLength > 0 && nameBuffer != 0)
            {
                var bytes = memory.ReadBytes(nameBuffer, nameLength);
                name = System.Text.Encoding.Unicode.GetString(bytes);
            }
            else
            {
                name = string.Empty;
            }
        }

        string? rootPath = null;
        if (root != 0) rootPath = resolveHandlePath(root);

        string? fullPath = null;
        if (!string.IsNullOrEmpty(name))
        {
            if (!string.IsNullOrEmpty(rootPath))
                fullPath = ObjectManager.Combine(rootPath, name);
            else
                fullPath = ObjectManager.NormalizePath(name);
        }

        return new ObjectAttributes
        {
            Length = length,
            RootDirectory = root,
            Name = name,
            FullPath = fullPath,
            Attributes = attributes,
            SecurityDescriptor = securityDescriptor,
            SecurityQualityOfService = securityQos
        };
    }

    public static string? ReadUnicodeString(VirtualMemoryManager memory, ulong unicodeStringAddress)
    {
        if (unicodeStringAddress == 0) return null;
        var length = memory.Read<ushort>(unicodeStringAddress);
        var buffer = memory.Read<ulong>(unicodeStringAddress + 8);
        if (length == 0 || buffer == 0) return string.Empty;
        return System.Text.Encoding.Unicode.GetString(memory.ReadBytes(buffer, length));
    }

    public static void WriteUnicodeString(VirtualMemoryManager memory, ulong unicodeStringAddress, string value)
    {
        if (unicodeStringAddress == 0) return;
        var maximum = memory.Read<ushort>(unicodeStringAddress + 2);
        var buffer = memory.Read<ulong>(unicodeStringAddress + 8);
        var bytes = System.Text.Encoding.Unicode.GetBytes(value);
        if (bytes.Length + 2 > maximum)
            throw new NtMemoryException(NtStatus.BufferTooSmall, "UNICODE_STRING buffer too small");
        if (buffer != 0 && bytes.Length > 0) memory.WriteBytes(buffer, bytes);
        if (buffer != 0) memory.Write<ushort>(buffer + (ulong)bytes.Length, 0);
        memory.Write<ushort>(unicodeStringAddress, (ushort)bytes.Length);
    }
}
