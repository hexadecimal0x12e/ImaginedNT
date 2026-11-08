using ImaginedNT.Core.Nt;

namespace ImaginedNT.Core.Pe;

public enum ImportResolutionKind
{
    Managed,
    Native,
    Failed,
    Deferred
}

public readonly record struct ImportResolution(ImportResolutionKind Kind, ulong Address, string Detail)
{
    public static ImportResolution Managed(ulong address, string detail) => new(ImportResolutionKind.Managed, address, detail);
    public static ImportResolution Native(ulong address, string detail) => new(ImportResolutionKind.Native, address, detail);
    public static ImportResolution Deferred(ulong address, string detail) => new(ImportResolutionKind.Deferred, address, detail);
    public static ImportResolution Failed(string detail) => new(ImportResolutionKind.Failed, 0, detail);
    public static ImportResolution Failed(ulong stubAddress, string detail) => new(ImportResolutionKind.Failed, stubAddress, detail);
}

public sealed class ImportBinding
{
    public required PeImport Import { get; init; }
    public required ulong SlotAddress { get; init; }
    public required ImportResolution Resolution { get; init; }
    public override string ToString() => $"{Import.ModuleName}!{Import.DisplayName} -> 0x{Resolution.Address:X} ({Resolution.Kind})";
}

/// <summary>A PE image mapped into the guest address space, with its IAT patched.</summary>
public sealed class PeMappedImage
{
    public bool ThreadCalloutsDisabled { get; set; }
    public required PeImage Image { get; init; }
    public required ulong BaseAddress { get; init; }
    public required ulong AllocationSize { get; init; }
    public required ulong EntryPoint { get; init; }
    public required bool WasRelocated { get; init; }
    public required IReadOnlyList<ImportBinding> Imports { get; init; }
    public required IReadOnlyList<ulong> TlsCallbacks { get; init; }
    public required IReadOnlyList<(PeSection Section, ulong Address, MemoryProtection Protection)> Sections { get; init; }

    public ulong SizeOfImage => Image.SizeOfImage;
    public ulong EndAddress => BaseAddress + SizeOfImage;

    public ulong RvaToVa(uint rva) => BaseAddress + rva;
    public bool ContainsAddress(ulong address) => address >= BaseAddress && address < EndAddress;

    public ImportBinding? FindImportForSlot(ulong slotAddress) =>
        Imports.FirstOrDefault(b => b.SlotAddress == slotAddress);

    public string Describe() =>
        $"{Image.FileName} base=0x{BaseAddress:X16} entry=0x{EntryPoint:X16} relocated={WasRelocated} imports={Imports.Count} tls={TlsCallbacks.Count}";
}

/// <summary>Maps a parsed PE image into memory, applies base relocations, patches the IAT and sets section protections.</summary>
public sealed unsafe class PeLoader
{
    private readonly Memory.VirtualMemoryManager _memory;
    private readonly Func<PeImport, ImportResolution> _resolver;
    private readonly Diagnostics.TraceLog _trace;
    private readonly bool _applyProtections;

    public PeLoader(Memory.VirtualMemoryManager memory, Func<PeImport, ImportResolution> resolver, Diagnostics.TraceLog trace, bool applyProtections = true)
    {
        _memory = memory;
        _resolver = resolver;
        _trace = trace;
        _applyProtections = applyProtections;
    }

    public PeMappedImage Load(PeImage image, ulong forcedBase = 0, Action<PeMappedImage>? beforeImports = null)
    {
        if (!image.Is64Bit)
            throw new BadImageFormatException($"{image.FileName}: only PE32+ (AMD64) images can be executed by this build");
        if (image.Machine != PeMachine.Amd64)
            throw new BadImageFormatException($"{image.FileName}: unsupported machine {image.Machine}");
        if (image.SizeOfImage == 0) throw new BadImageFormatException($"{image.FileName}: SizeOfImage is zero");

        var preferred = forcedBase != 0 ? forcedBase : image.ImageBase;
        var baseAddress = _memory.MapImage(preferred, image.SizeOfImage, image.FileName);
        var delta = baseAddress - image.ImageBase;
        var relocated = delta != 0;

        try
        {
            if (relocated && !image.HasRelocations && image.IsRelocationStripped)
                throw new BadImageFormatException($"{image.FileName}: cannot relocate: loaded at 0x{baseAddress:X16}, preferred 0x{image.ImageBase:X16}, and the image has relocations stripped");

            // An image without IMAGE_FILE_RELOCS_STRIPPED and with no .reloc entries needs no
            // fixups at all, so loading it at another base is safe.  This is what lets several
            // imagined processes map the same fixed-looking sample image at different bases.

            // Sections must be present before relocations are applied: relocations patch words
            // inside the copied image data, and copying afterwards would silently undo them.
            CopyHeaders(image, baseAddress);
            CopySections(image, baseAddress);

            if (relocated) ApplyRelocations(image, baseAddress, delta);

            var bindings = new List<ImportBinding>();

            var tlsCallbacks = new List<ulong>();
            if (image.Tls is not null)
            {
                foreach (var callback in image.Tls.CallbackVas)
                {
                    var va = callback;
                    if (relocated) va = unchecked(callback + (ulong)delta);
                    tlsCallbacks.Add(va);
                }
            }

            var sections = new List<(PeSection, ulong, MemoryProtection)>();
            var mapped = new PeMappedImage
            {
                Image = image,
                BaseAddress = baseAddress,
                AllocationSize = Memory.VirtualMemoryManager.GranularityAlign(image.SizeOfImage),
                EntryPoint = image.EntryPointRva == 0 ? 0 : baseAddress + image.EntryPointRva,
                WasRelocated = relocated,
                Imports = bindings,
                TlsCallbacks = tlsCallbacks,
                Sections = sections
            };

            beforeImports?.Invoke(mapped);
            bindings.AddRange(PatchImports(image, baseAddress));
            if (_applyProtections) ApplyProtections(image, baseAddress, sections);
            Native.NativeMethods.FlushInstructionCache(Native.NativeMethods.GetCurrentProcess(),
                (IntPtr)(long)baseAddress, (UIntPtr)image.SizeOfImage);

            if (image.DataDirectories.Count > PeImage.ExceptionDirectoryIndex &&
                image.DataDirectories[PeImage.ExceptionDirectoryIndex] is { IsPresent: true, Size: > 0 } exceptions)
            {
                if (exceptions.Size % 12 != 0 || (ulong)exceptions.VirtualAddress + exceptions.Size > image.SizeOfImage)
                    throw new BadImageFormatException($"{image.FileName}: invalid AMD64 exception directory");
                if (!_memory.RegisterFunctionTable(baseAddress, baseAddress + exceptions.VirtualAddress, exceptions.Size / 12))
                    throw new BadImageFormatException($"{image.FileName}: could not register AMD64 exception table");
            }

            _trace.Emit(Diagnostics.TraceKind.Image,
                $"loaded {image.FileName}: base=0x{baseAddress:X16} entry=0x{mapped.EntryPoint:X16} relocated={relocated} imports={bindings.Count()} tls={tlsCallbacks.Count}");

            var managed = bindings.Count(b => b.Resolution.Kind == ImportResolutionKind.Managed);
            var native = bindings.Count(b => b.Resolution.Kind == ImportResolutionKind.Native);
            _trace.Emit(Diagnostics.TraceKind.Import,
                $"{image.FileName}: {managed} imports -> managed layer, {native} imports -> native NT/user32, {bindings.Count(b => b.Resolution.Kind == ImportResolutionKind.Deferred)} deferred, {bindings.Count(b => b.Resolution.Kind == ImportResolutionKind.Failed)} unresolved");

            return mapped;
        }
        catch
        {
            try { _memory.TryFree(baseAddress, 0, FreeType.Release); } catch { /* best effort */ }
            throw;
        }
    }

    private static void CopyHeaders(PeImage image, ulong baseAddress)
    {
        var headers = Math.Min(image.SizeOfHeaders, image.SizeOfImage);
        var bytes = image.ReadRva(0, (int)headers);
        fixed (byte* p = bytes)
        {
            Buffer.MemoryCopy(p, (void*)(long)baseAddress, headers, headers);
        }
    }

    private static void CopySections(PeImage image, ulong baseAddress)
    {
        foreach (var section in image.Sections)
        {
            if (section.SizeOfRawData == 0) continue;
            var bytes = image.ReadRva(section.VirtualAddress, (int)section.SizeOfRawData);
            if (bytes.Length == 0) continue;
            var destination = (void*)(long)(baseAddress + section.VirtualAddress);
            fixed (byte* source = bytes)
            {
                Buffer.MemoryCopy(source, destination, bytes.Length, bytes.Length);
            }
        }
    }

    private ImportBinding[] PatchImports(PeImage image, ulong baseAddress)
    {
        var bindings = new List<ImportBinding>(image.Imports.Count);
        foreach (var import in image.Imports)
        {
            var resolution = _resolver(import);
            var slotAddress = baseAddress + import.IatRva;
            if (resolution.Address != 0)
            {
                if (image.Is64Bit)
                {
                    *(ulong*)(long)slotAddress = resolution.Address;
                }
                else
                {
                    *(uint*)(long)slotAddress = (uint)resolution.Address;
                }
            }

            var binding = new ImportBinding { Import = import, SlotAddress = slotAddress, Resolution = resolution };
            bindings.Add(binding);

            var kind = resolution.Kind switch
            {
                ImportResolutionKind.Managed => "managed",
                ImportResolutionKind.Native => "native",
                ImportResolutionKind.Deferred => "deferred",
                _ => "FAILED"
            };
            _trace.Emit(Diagnostics.TraceKind.Import,
                $"IAT {image.FileName}+0x{import.IatRva:X} {import.ModuleName}!{import.DisplayName} -> 0x{resolution.Address:X16} [{kind}] {resolution.Detail}");
        }
        return bindings.ToArray();
    }

    private void ApplyRelocations(PeImage image, ulong baseAddress, ulong delta)
    {
        var applied = 0;
        foreach (var relocation in image.Relocations)
        {
            var address = (byte*)(long)(baseAddress + relocation.Rva);
            switch (relocation.Type)
            {
                case 0: // IMAGE_REL_BASED_ABSOLUTE (padding)
                    break;
                case 3: // IMAGE_REL_BASED_HIGHLOW
                {
                    var value = *(uint*)address;
                    *(uint*)address = unchecked(value + (uint)delta);
                    applied++;
                    break;
                }
                case 4: // IMAGE_REL_BASED_HIGHADJ - cannot be applied without consuming the next entry
                    _trace.Warn($"{image.FileName}: HIGHADJ relocation at 0x{relocation.Rva:X} ignored");
                    break;
                case 10: // IMAGE_REL_BASED_DIR64
                {
                    var value = *(ulong*)address;
                    *(ulong*)address = unchecked(value + delta);
                    applied++;
                    break;
                }
                case 1:
                    *(ushort*)address = unchecked((ushort)(*(ushort*)address + (ushort)(delta >> 16)));
                    applied++;
                    break;
                case 2:
                    *(ushort*)address = unchecked((ushort)(*(ushort*)address + (ushort)delta));
                    applied++;
                    break;
                default:
                    _trace.Warn($"{image.FileName}: unsupported relocation type {relocation.Type} at 0x{relocation.Rva:X}");
                    break;
            }
        }
        _trace.Emit(Diagnostics.TraceKind.Image, $"{image.FileName}: applied {applied} base relocations (delta 0x{delta:X})");
    }

    private void ApplyProtections(PeImage image, ulong baseAddress, List<(PeSection, ulong, MemoryProtection)> sections)
    {
        var headerSize = Memory.VirtualMemoryManager.PageAlign(image.SizeOfHeaders);
        var old = 0u;
        Native.NativeMethods.VirtualProtect((IntPtr)(long)baseAddress, (UIntPtr)headerSize, Native.NativeMethods.PAGE_READONLY, out old);

        foreach (var section in image.Sections)
        {
            var protection = SectionProtection(section);
            var size = Memory.VirtualMemoryManager.PageAlign(Math.Max(section.VirtualSize, section.SizeOfRawData));
            if (size == 0) continue;
            var address = baseAddress + section.VirtualAddress;
            if (Native.NativeMethods.VirtualProtect((IntPtr)(long)address, (UIntPtr)size, Native.NativeMethods.ToPageProtection((uint)protection), out old))
            {
                _memory.AddImageSection(address, size, protection, $"{image.FileName}!{section.Name}");
                sections.Add((section, address, protection));
            }
            else
            {
                var err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                _trace.Warn($"{image.FileName}: VirtualProtect failed for {section.Name} size=0x{size:X} err={err}");
            }
        }
    }

    private static MemoryProtection SectionProtection(PeSection section)
    {
        var executable = section.IsExecutable;
        var writable = section.IsWritable;
        return (executable, writable) switch
        {
            (true, true) => MemoryProtection.ExecuteReadWrite,
            (true, false) => MemoryProtection.ExecuteRead,
            (false, true) => MemoryProtection.ReadWrite,
            _ => MemoryProtection.ReadOnly
        };
    }
}
