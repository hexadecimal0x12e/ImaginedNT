using ImaginedNT.Core.Io;
namespace ImaginedNT.Core.Pe;

public enum PeMachine : ushort
{
    Unknown = 0,
    I386 = 0x014C,
    Amd64 = 0x8664,
    Arm64 = 0xAA64
}

[Flags]
public enum PeSectionCharacteristics : uint
{
    ContainsCode = 0x0000_0020,
    ContainsInitializedData = 0x0000_0040,
    ContainsUninitializedData = 0x0000_0080,
    LinkInfo = 0x0000_0200,
    LinkRemove = 0x0000_0800,
    LinkComdat = 0x0000_1000,
    NoDeferSpeculativeExceptions = 0x0000_4000,
    GpRel = 0x0000_8000,
    MemPurgeable = 0x0002_0000,
    MemLocked = 0x0004_0000,
    MemPreload = 0x0008_0000,
    Align1Bytes = 0x0010_0000,
    Align2Bytes = 0x0020_0000,
    Align4Bytes = 0x0030_0000,
    Align8Bytes = 0x0040_0000,
    Align16Bytes = 0x0050_0000,
    Align32Bytes = 0x0060_0000,
    Align64Bytes = 0x0070_0000,
    Align128Bytes = 0x0080_0000,
    Align256Bytes = 0x0090_0000,
    Align512Bytes = 0x00A0_0000,
    Align1024Bytes = 0x00B0_0000,
    Align2048Bytes = 0x00C0_0000,
    Align4096Bytes = 0x00D0_0000,
    Align8192Bytes = 0x00E0_0000,
    LinkNumbered = 0x0100_0000,
    LinkNRelocOverflow = 0x0200_0000,
    MemDiscardable = 0x0200_0000,
    MemNotCached = 0x0400_0000,
    MemNotPaged = 0x0800_0000,
    MemShared = 0x1000_0000,
    MemExecute = 0x2000_0000,
    MemRead = 0x4000_0000,
    MemWrite = 0x8000_0000
}

public sealed class PeSection
{
    public required string Name { get; init; }
    public required uint VirtualAddress { get; init; }
    public required uint VirtualSize { get; init; }
    public required uint PointerToRawData { get; init; }
    public required uint SizeOfRawData { get; init; }
    public required PeSectionCharacteristics Characteristics { get; init; }

    public ulong EndVirtualAddress => (ulong)VirtualAddress + Math.Max(VirtualSize, SizeOfRawData);
    public bool IsExecutable => (Characteristics & PeSectionCharacteristics.MemExecute) != 0;
    public bool IsWritable => (Characteristics & PeSectionCharacteristics.MemWrite) != 0;
    public bool ContainsRva(uint rva) => rva >= VirtualAddress && rva < EndVirtualAddress;
    public override string ToString() => $"{Name} rva=0x{VirtualAddress:X} vsize=0x{VirtualSize:X} raw=0x{PointerToRawData:X}/{SizeOfRawData:X} {Characteristics}";
}

public sealed class PeImport
{
    public required string ModuleName { get; init; }
    public string? Name { get; init; }
    public ushort? Ordinal { get; init; }
    public required uint IatRva { get; init; }
    public required int Index { get; init; }
    public bool IsDelayLoad { get; init; }
    public bool IsOrdinal => Name is null;
    public string DisplayName => Name ?? $"#{Ordinal}";
    public override string ToString() => $"{ModuleName}!{DisplayName} @IAT 0x{IatRva:X}";
}

public sealed class PeExport
{
    public required string? Name { get; init; }
    public required uint Ordinal { get; init; }
    public required uint Rva { get; init; }
    public string? Forwarder { get; init; }
    public override string ToString() => $"{Name ?? $"#{Ordinal}"} -> 0x{Rva:X}{(Forwarder is null ? string.Empty : $" ({Forwarder})")}";
}

public sealed class PeRelocation
{
    public required uint Rva { get; init; }
    public required int Type { get; init; }
    public override string ToString() => $"0x{Rva:X} type={Type}";
}

public sealed class PeTlsInfo
{
    public ulong StartAddressOfRawData { get; init; }
    public ulong EndAddressOfRawData { get; init; }
    public ulong AddressOfIndex { get; init; }
    public ulong AddressOfCallbacks { get; init; }
    public uint SizeOfZeroFill { get; init; }
    public uint Characteristics { get; init; }
    public IReadOnlyList<ulong> CallbackVas { get; init; } = Array.Empty<ulong>();
}

public sealed class PeDataDirectory
{
    public uint VirtualAddress { get; init; }
    public uint Size { get; init; }
    public bool IsPresent => VirtualAddress != 0;
}

public sealed class PeImage
{
    public const int ImportDirectoryIndex = 1;
    public const int ExportDirectoryIndex = 0;
    public const int ExceptionDirectoryIndex = 3;
    public const int BaseRelocationDirectoryIndex = 5;
    public const int TlsDirectoryIndex = 9;
    public const int DelayImportDirectoryIndex = 13;

    private readonly byte[] _data;

    private PeImage(byte[] data, string? path)
    {
        _data = data;
        Path = path;
    }

    public string? Path { get; }
    public ReadOnlyMemory<byte> Data => _data;
    public PeMachine Machine { get; private set; }
    public bool Is64Bit { get; private set; }
    public bool IsDll { get; private set; }
    public ushort Characteristics { get; private set; }
    public ulong ImageBase { get; private set; }
    public uint EntryPointRva { get; private set; }
    public uint SizeOfImage { get; private set; }
    public uint SizeOfHeaders { get; private set; }
    public uint SectionAlignment { get; private set; }
    public uint FileAlignment { get; private set; }
    public ushort Subsystem { get; private set; }
    public ushort DllCharacteristics { get; private set; }
    public ulong SizeOfStackReserve { get; private set; }
    public uint TimeDateStamp { get; private set; }
    public IReadOnlyList<PeSection> Sections { get; private set; } = Array.Empty<PeSection>();
    public IReadOnlyList<PeImport> Imports { get; private set; } = Array.Empty<PeImport>();
    public IReadOnlyList<PeExport> Exports { get; private set; } = Array.Empty<PeExport>();
    public IReadOnlyList<PeRelocation> Relocations { get; private set; } = Array.Empty<PeRelocation>();
    public PeTlsInfo? Tls { get; private set; }
    public IReadOnlyList<PeDataDirectory> DataDirectories { get; private set; } = Array.Empty<PeDataDirectory>();
    public bool HasRelocations => DataDirectories.Count > BaseRelocationDirectoryIndex &&
                                  DataDirectories[BaseRelocationDirectoryIndex].IsPresent &&
                                  Relocations.Count > 0;

    /// <summary>IMAGE_FILE_RELOCS_STRIPPED: the linker guarantees the image cannot be rebased.</summary>
    public bool IsRelocationStripped => (Characteristics & 0x0001) != 0;
    public bool HasDelayImports => DataDirectories.Count > DelayImportDirectoryIndex &&
                                   DataDirectories[DelayImportDirectoryIndex].IsPresent;

    public string FileName => string.IsNullOrEmpty(Path) ? "<memory>" : System.IO.Path.GetFileName(Path);

    public static PeImage Load(string path)
    {
        var data = HostFileSystem.ReadAllBytes(path);
        return Parse(data, System.IO.Path.GetFullPath(path));
    }

    public static PeImage Parse(ReadOnlySpan<byte> data, string? path = null)
    {
        var image = new PeImage(data.ToArray(), path);
        image.ParseHeaders();
        return image;
    }

    private ushort U16(int offset)
    {
        if (offset < 0 || offset + 2 > _data.Length) throw Bad($"u16 out of bounds @0x{offset:X}");
        return (ushort)(_data[offset] | (_data[offset + 1] << 8));
    }

    private uint U32(int offset)
    {
        if (offset < 0 || offset + 4 > _data.Length) throw Bad($"u32 out of bounds @0x{offset:X}");
        return (uint)(_data[offset] | (_data[offset + 1] << 8) | (_data[offset + 2] << 16) | (_data[offset + 3] << 24));
    }

    private ulong U64(int offset) => U32(offset) | ((ulong)U32(offset + 4) << 32);

    private string AsciiZ(int offset, int max = 4096)
    {
        if (offset < 0 || offset >= _data.Length) throw Bad($"ascii string out of bounds @0x{offset:X}");
        var end = offset;
        while (end < _data.Length && end - offset < max && _data[end] != 0) end++;
        return System.Text.Encoding.ASCII.GetString(_data, offset, end - offset);
    }

    private static BadImageFormatException Bad(string message) => new($"Invalid PE image: {message}");

    private void ParseHeaders()
    {
        if (_data.Length < 0x40 || _data[0] != 'M' || _data[1] != 'Z') throw Bad("missing MZ signature");
        var lfanew = (int)U32(0x3C);
        if (lfanew <= 0 || lfanew + 0x18 > _data.Length || U32(lfanew) != 0x0000_4550) throw Bad("missing PE signature");

        var fileHeader = lfanew + 4;
        Machine = (PeMachine)U16(fileHeader);
        var numberOfSections = U16(fileHeader + 2);
        if (numberOfSections > 96) throw Bad($"too many sections ({numberOfSections})");
        TimeDateStamp = U32(fileHeader + 4);
        var sizeOfOptionalHeader = U16(fileHeader + 16);
        Characteristics = U16(fileHeader + 18);
        IsDll = (Characteristics & 0x2000) != 0;

        var optional = fileHeader + 20;
        var magic = U16(optional);
        Is64Bit = magic switch
        {
            0x20B => true,
            0x10B => false,
            _ => throw Bad($"unknown optional header magic 0x{magic:X}")
        };

        if (Is64Bit)
        {
            EntryPointRva = U32(optional + 0x10);
            ImageBase = U64(optional + 0x18);
            SectionAlignment = U32(optional + 0x20);
            FileAlignment = U32(optional + 0x24);
            SizeOfImage = U32(optional + 0x38);
            SizeOfHeaders = U32(optional + 0x3C);
            Subsystem = U16(optional + 0x44);
            DllCharacteristics = U16(optional + 0x46);
            SizeOfStackReserve = U64(optional + 0x48);
            var count = U32(optional + 0x6C);
            ReadDataDirectories(optional + 0x70, count);
        }
        else
        {
            EntryPointRva = U32(optional + 0x10);
            ImageBase = U32(optional + 0x1C);
            SectionAlignment = U32(optional + 0x20);
            FileAlignment = U32(optional + 0x24);
            SizeOfImage = U32(optional + 0x38);
            SizeOfHeaders = U32(optional + 0x3C);
            Subsystem = U16(optional + 0x44);
            DllCharacteristics = U16(optional + 0x46);
            SizeOfStackReserve = U32(optional + 0x48);
            var count = U32(optional + 0x5C);
            ReadDataDirectories(optional + 0x60, count);
        }

        if (SectionAlignment == 0 || FileAlignment == 0) throw Bad("zero alignment");
        if (SizeOfImage == 0) throw Bad("zero SizeOfImage");
        if (SizeOfImage > 0x6000_0000) throw Bad($"SizeOfImage too large (0x{SizeOfImage:X})");

        var sections = new List<PeSection>(numberOfSections);
        var sectionTable = optional + sizeOfOptionalHeader;
        for (var i = 0; i < numberOfSections; i++)
        {
            var off = sectionTable + i * 40;
            if (off + 40 > _data.Length) throw Bad("section table out of bounds");
            var nameBytes = new byte[8];
            Array.Copy(_data, off, nameBytes, 0, 8);
            var name = System.Text.Encoding.ASCII.GetString(nameBytes).TrimEnd('\0');
            var virtualSize = U32(off + 8);
            var virtualAddress = U32(off + 12);
            var rawSize = U32(off + 16);
            var rawPointer = U32(off + 20);
            if (rawSize > 0x1000_0000) throw Bad($"section {name} raw size too large (0x{rawSize:X})");
            if (virtualSize > 0x4000_0000) throw Bad($"section {name} virtual size too large");
            if (rawSize != 0 && (ulong)rawPointer + rawSize > (ulong)_data.Length)
                throw Bad($"section {name} raw data extends past EOF");
            sections.Add(new PeSection
            {
                Name = name,
                VirtualSize = virtualSize,
                VirtualAddress = virtualAddress,
                SizeOfRawData = rawSize,
                PointerToRawData = rawPointer,
                Characteristics = (PeSectionCharacteristics)U32(off + 36)
            });
        }

        Sections = sections;
        ParseImports();
        ParseExports();
        ParseRelocations();
        ParseTls();
    }

    private void ReadDataDirectories(int offset, uint count)
    {
        count = Math.Min(count, 16);
        var list = new List<PeDataDirectory>((int)count);
        for (var i = 0; i < count; i++)
        {
            var off = offset + i * 8;
            if (off + 8 > _data.Length) break;
            list.Add(new PeDataDirectory { VirtualAddress = U32(off), Size = U32(off + 4) });
        }
        while (list.Count < 16) list.Add(new PeDataDirectory());
        DataDirectories = list;
    }

    private PeDataDirectory Directory(int index) => index < DataDirectories.Count ? DataDirectories[index] : new PeDataDirectory();

    public PeSection? SectionForRva(uint rva) => Sections.FirstOrDefault(s => s.ContainsRva(rva));

    public int RvaToFileOffset(uint rva)
    {
        if (rva < SizeOfHeaders)
        {
            if (rva >= _data.Length) throw Bad($"header rva 0x{rva:X} past EOF");
            return (int)rva;
        }

        var section = SectionForRva(rva);
        if (section is null) throw Bad($"RVA 0x{rva:X} does not belong to any section");
        var delta = rva - section.VirtualAddress;
        var offset = section.PointerToRawData + delta;
        if (offset >= _data.Length) throw Bad($"RVA 0x{rva:X} maps past EOF");
        return (int)offset;
    }

    public byte[] ReadRva(uint rva, int length)
    {
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
        var result = new byte[length];
        if (rva < SizeOfHeaders)
        {
            var copy = Math.Min(length, _data.Length - (int)rva);
            if (copy > 0) Array.Copy(_data, (int)rva, result, 0, copy);
            return result;
        }

        var section = SectionForRva(rva);
        if (section is null) throw Bad($"RVA 0x{rva:X} not mapped by a section");
        var delta = rva - section.VirtualAddress;
        if (delta >= section.SizeOfRawData) return result;
        var available = (int)Math.Min((uint)length, section.SizeOfRawData - delta);
        var fileOffset = (long)section.PointerToRawData + delta;
        if (fileOffset >= _data.Length) return result;
        available = (int)Math.Min(available, _data.Length - fileOffset);
        if (available > 0) Array.Copy(_data, fileOffset, result, 0, available);
        return result;
    }

    private void ParseImports()
    {
        var imports = new List<PeImport>();
        var dir = Directory(ImportDirectoryIndex);
        if (dir.IsPresent)
        {
            var descriptorOffset = RvaToFileOffset(dir.VirtualAddress);
            for (var i = 0; i < 4096; i++)
            {
                var off = descriptorOffset + i * 20;
                if (off + 20 > _data.Length) throw Bad("import descriptor table past EOF");
                var lookup = U32(off);
                var name = U32(off + 12);
                var iat = U32(off + 16);
                if (lookup == 0 && name == 0 && iat == 0) break;
                if (name != 0)
                    ParseImportThunks(imports, AsciiZ(RvaToFileOffset(name)), lookup != 0 ? lookup : iat, iat);
            }
        }

        dir = Directory(DelayImportDirectoryIndex);
        if (dir.IsPresent)
        {
            var descriptorOffset = RvaToFileOffset(dir.VirtualAddress);
            for (var i = 0; i < Math.Min(dir.Size / 32, 4096); i++)
            {
                var off = descriptorOffset + i * 32;
                if (off + 32 > _data.Length) throw Bad("delay import descriptor table past EOF");
                if (U32(off + 4) == 0) break;
                var rvaBased = (U32(off) & 1) != 0;
                uint Rva(uint value) => rvaBased ? value : checked((uint)(value - ImageBase));
                ParseImportThunks(imports, AsciiZ(RvaToFileOffset(Rva(U32(off + 4)))),
                    Rva(U32(off + 16)), Rva(U32(off + 12)), rvaBased, isDelayLoad: true);
            }
        }
        Imports = imports;
    }

    private void ParseImportThunks(List<PeImport> imports, string moduleName, uint lookupRva, uint iatRva,
        bool rvaBased = true, bool isDelayLoad = false)
    {
        var thunkSize = Is64Bit ? 8 : 4;
        var lookupOff = RvaToFileOffset(lookupRva);
        var index = 0;
        while (true)
        {
            if (index > 65535) throw Bad($"import thunk table too large in {moduleName}");
            var thunkOff = lookupOff + index * thunkSize;
            if (thunkOff + thunkSize > _data.Length) throw Bad($"import thunk past EOF in {moduleName}");
            var value = Is64Bit ? U64(thunkOff) : U32(thunkOff);
            if (value == 0) break;
            if (Is64Bit && (value & 0x8000_0000_0000_0000) != 0)
            {
                imports.Add(new PeImport { ModuleName = moduleName, Ordinal = (ushort)value, IatRva = iatRva + (uint)(index * thunkSize), Index = index, IsDelayLoad = isDelayLoad });
            }
            else if (!Is64Bit && (value & 0x8000_0000) != 0)
            {
                imports.Add(new PeImport { ModuleName = moduleName, Ordinal = (ushort)value, IatRva = iatRva + (uint)(index * thunkSize), Index = index, IsDelayLoad = isDelayLoad });
            }
            else
            {
                var hintNameRva = rvaBased ? checked((uint)value) : checked((uint)(value - ImageBase));
                var hintOff = RvaToFileOffset(hintNameRva);
                var name = AsciiZ(hintOff + 2);
                imports.Add(new PeImport { ModuleName = moduleName, Name = name, IatRva = iatRva + (uint)(index * thunkSize), Index = index, IsDelayLoad = isDelayLoad });
            }
            index++;
        }
        if (imports.Count > 1_000_000) throw Bad("too many imports");
    }

    private void ParseExports()
    {
        var dir = Directory(ExportDirectoryIndex);
        if (!dir.IsPresent) return;
        var off = RvaToFileOffset(dir.VirtualAddress);
        if (off + 40 > _data.Length) throw Bad("export directory past EOF");
        var baseOrdinal = U32(off + 16);
        var numberOfFunctions = U32(off + 20);
        var numberOfNames = U32(off + 24);
        if (numberOfFunctions > 1_000_000 || numberOfNames > 1_000_000)
            throw Bad("export table too large");
        var addressOfFunctions = U32(off + 28);
        var addressOfNames = U32(off + 32);
        var addressOfNameOrdinals = U32(off + 36);
        var functions = new uint[numberOfFunctions];
        var functionOff = RvaToFileOffset(addressOfFunctions);
        for (var i = 0; i < numberOfFunctions; i++)
        {
            if (functionOff + i * 4 + 4 > _data.Length) throw Bad("export function table past EOF");
            functions[i] = U32(functionOff + i * 4);
        }

        var exports = new List<PeExport>();
        var names = new Dictionary<int, List<string>>();
        var nameOff = RvaToFileOffset(addressOfNames);
        var ordinalOff = RvaToFileOffset(addressOfNameOrdinals);
        for (var i = 0; i < numberOfNames; i++)
        {
            if (nameOff + i * 4 + 4 > _data.Length || ordinalOff + i * 2 + 2 > _data.Length) throw Bad("export name table past EOF");
            var nameRva = U32(nameOff + i * 4);
            var ordinalIndex = U16(ordinalOff + i * 2);
            if (ordinalIndex >= numberOfFunctions) continue;
            if (!names.TryGetValue(ordinalIndex, out var aliases)) names.Add(ordinalIndex, aliases = new());
            aliases.Add(AsciiZ(RvaToFileOffset(nameRva)));
        }

        for (var i = 0; i < numberOfFunctions; i++)
        {
            var rva = functions[i];
            if (rva == 0) continue;
            string? forwarder = null;
            if (rva >= dir.VirtualAddress && rva < dir.VirtualAddress + dir.Size)
            {
                forwarder = AsciiZ(RvaToFileOffset(rva));
            }
            if (names.TryGetValue(i, out var aliases))
            {
                foreach (var name in aliases)
                    exports.Add(new PeExport { Name = name, Ordinal = baseOrdinal + (uint)i, Rva = rva, Forwarder = forwarder });
            }
            else
                exports.Add(new PeExport { Name = null, Ordinal = baseOrdinal + (uint)i, Rva = rva, Forwarder = forwarder });
        }
        Exports = exports;
    }

    private void ParseRelocations()
    {
        var dir = Directory(BaseRelocationDirectoryIndex);
        if (!dir.IsPresent) return;
        var off = RvaToFileOffset(dir.VirtualAddress);
        var end = Math.Min(_data.Length, (long)off + dir.Size);
        var list = new List<PeRelocation>();
        var cursor = off;
        while (cursor + 8 <= end)
        {
            var pageRva = U32(cursor);
            var blockSize = U32(cursor + 4);
            if (blockSize < 8 || cursor + blockSize > end) break;
            var entries = (int)((blockSize - 8) / 2);
            for (var i = 0; i < entries; i++)
            {
                var entry = U16(cursor + 8 + i * 2);
                var type = entry >> 12;
                if (type == 0) continue;
                list.Add(new PeRelocation { Rva = pageRva + (uint)(entry & 0x0FFF), Type = type });
            }
            cursor += (int)blockSize;
            if (list.Count > 1_000_000) throw Bad("too many relocations");
        }
        Relocations = list;
    }

    private void ParseTls()
    {
        var dir = Directory(TlsDirectoryIndex);
        if (!dir.IsPresent) return;
        var off = RvaToFileOffset(dir.VirtualAddress);
        ulong start, end, index, callbacks;
        uint zeroFill, characteristics;
        if (Is64Bit)
        {
            if (off + 40 > _data.Length) throw Bad("TLS directory past EOF");
            start = U64(off);
            end = U64(off + 8);
            index = U64(off + 16);
            callbacks = U64(off + 24);
            zeroFill = U32(off + 32);
            characteristics = U32(off + 36);
        }
        else
        {
            if (off + 24 > _data.Length) throw Bad("TLS directory past EOF");
            start = U32(off);
            end = U32(off + 4);
            index = U32(off + 8);
            callbacks = U32(off + 12);
            zeroFill = U32(off + 16);
            characteristics = U32(off + 20);
        }

        var callbackVas = new List<ulong>();
        if (callbacks != 0)
        {
            // AddressOfCallbacks is a VA in the on-disk image.  Convert it to an RVA using the
            // preferred image base so both pre- and post-relocation callback tables work.
            var cbRva = (uint)(callbacks - ImageBase);
            try
            {
                var cbOff = RvaToFileOffset(cbRva);
                for (var i = 0; i < 256; i++)
                {
                    var value = Is64Bit ? U64(cbOff + i * 8) : U32(cbOff + i * 4);
                    if (value == 0) break;
                    callbackVas.Add(value);
                }
            }
            catch (BadImageFormatException)
            {
                // tolerate an unresolvable callback table; the loader reports it separately
            }
        }

        Tls = new PeTlsInfo
        {
            StartAddressOfRawData = start,
            EndAddressOfRawData = end,
            AddressOfIndex = index,
            AddressOfCallbacks = callbacks,
            SizeOfZeroFill = zeroFill,
            Characteristics = characteristics,
            CallbackVas = callbackVas
        };
    }

    public PeSection? SectionByName(string name) =>
        Sections.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<(uint Rva, byte[] Bytes)> ExecutableSectionBytes()
    {
        foreach (var section in Sections.Where(s => s.IsExecutable))
        {
            var length = (int)Math.Min(section.SizeOfRawData, section.VirtualSize == 0 ? section.SizeOfRawData : Math.Max(section.VirtualSize, 1u));
            if (length <= 0) continue;
            yield return (section.VirtualAddress, ReadRva(section.VirtualAddress, length));
        }
    }

    public override string ToString() =>
        $"{FileName}: {Machine} {(Is64Bit ? "PE32+" : "PE32")} imagebase=0x{ImageBase:X} entry=0x{EntryPointRva:X} sections={Sections.Count} imports={Imports.Count} relocs={Relocations.Count}";
}
