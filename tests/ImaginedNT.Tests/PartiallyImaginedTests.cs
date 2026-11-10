using System.Buffers.Binary;
using System.Text;
using ImaginedNT.Core.Pe;
using ImaginedNT.Core.Runtime;

namespace ImaginedNT.Tests;

internal static class PartiallyImaginedTests
{
    public static void Register(List<(string, Action)> tests)
    {
        tests.Add(("Partially Imagined discovers named and ordinal implementations", Discovery));
        tests.Add(("Partially Imagined redirects internal calls and preserves adjacent native functions", InternalCalls));
        tests.Add(("Partially Imagined resolves circular DLL imports and forwarded exports", CrossLibraryCalls));
        tests.Add(("Partially Imagined rolls back conflicting export aliases", ConflictingAliases));
    }

    [PartiallyImaginedApi("partial-a.dll", "Replace")]
    [PartiallyImaginedApi("partial-a.dll", (ushort)1)]
    private static ulong Replace(ApiCallContext ctx) => (ulong)ctx.Process.Id;

    private static void Discovery()
    {
        var registry = new ApiRegistry();
        Assert.Equal(2, VirtualApiScanner.Register(typeof(PartiallyImaginedTests).Assembly, registry), "discovered exports");
        Assert.True(registry.TryGetExact("partial-a", "Replace", out var api) && api!.IsPartiallyImagined, "named replacement");
        Assert.True(registry.TryGetExact("partial-a.dll", "#1", out api) && api!.IsPartiallyImagined, "ordinal replacement");
        Assert.False(registry.TryGet("unrelated.dll", "Replace", out _), "replacement stays in its module");
        registry.Register("kernel32", "NativeOnly", _ => 1);
        Assert.False(registry.TryGet("partial-a", "NativeOnly", out _), "native part does not inherit unrelated APIs");
    }

    private static ImaginedRuntime CreateRuntime(ImportPolicy policy = ImportPolicy.ManagedFirst)
    {
        var options = RuntimeOptions.ForTests();
        options.InterceptSyscalls = false;
        options.ImportPolicy = policy;
        options.FailOnUnresolvedImport = true;
        return new ImaginedRuntime(options, new[] { typeof(PartiallyImaginedTests).Assembly });
    }

    private static uint Call(ImaginedProcess process, ulong address)
    {
        Assert.NotEqual(0UL, address, "resolved address");
        var thread = process.CreateThreadAt(address, 0);
        Assert.True(thread.Wait(TimeSpan.FromSeconds(5), out var result), "guest call completed");
        return result;
    }

    private static void InternalCalls()
    {
        var directory = TestHost.NewTempDirectory("partial-internal");
        try
        {
            var path = WriteLibrary(directory, "partial-a.dll");
            foreach (var policy in new[] { ImportPolicy.ManagedFirst, ImportPolicy.NativeFirst })
            {
                using var runtime = CreateRuntime(policy);
                foreach (var _ in new[] { 0, 1 })
                {
                    var process = runtime.CreateProcess(path);
                    var mapped = process.LoadManagedLibrary(path)!;
                    Assert.Equal(0UL, mapped.EntryPoint, "DLL without DllMain");
                    Assert.Equal((uint)process.Id, Call(process, mapped.BaseAddress + 0x1000), "raw export entry");
                    Assert.Equal((uint)process.Id, Call(process, mapped.BaseAddress + 0x1010), "relative internal call");
                    Assert.Equal(42u, Call(process, mapped.BaseAddress + 0x1006), "adjacent native function preserved");
                    var named = runtime.ResolveExport(process, mapped.BaseAddress, "Replace");
                    Assert.Equal(named, runtime.ResolveExport(process, mapped.BaseAddress, "Alias"), "named alias");
                    Assert.Equal(named, runtime.ResolveExport(process, mapped.BaseAddress, null, 1), "ordinal alias");
                    Assert.Equal((uint)process.Id, Call(process, named), "dynamic replacement");
                    runtime.Options.ImportPolicy = ImportPolicy.ManagedOnly;
                    // Guest-mapped exports are guest code, not host native forwards; they remain
                    // callable under managed-only/strict policy (see ResolveMappedExport).
                    Assert.NotEqual(0UL, runtime.ResolveExport(process, mapped.BaseAddress, "Tiny"),
                        "guest export remains callable under managed-only");
                    Assert.Equal(named, runtime.ResolveExport(process, mapped.BaseAddress, "Replace"), "managed part remains available");
                    runtime.Options.ImportPolicy = policy;
                }
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void CrossLibraryCalls()
    {
        var directory = TestHost.NewTempDirectory("partial-cross");
        try
        {
            var a = WriteLibrary(directory, "partial-a.dll", "partial-b.dll");
            var b = WriteLibrary(directory, "partial-b.dll", "partial-a.dll");
            using var runtime = CreateRuntime(ImportPolicy.NativeFirst);
            runtime.RegisterLibrary(a);
            runtime.RegisterLibrary(b);
            var process = runtime.CreateProcess(a);
            var mapped = process.LoadManagedLibrary(b)!;
            Assert.Equal(2, process.LoadedModules.Count, "circular imports reuse mapped DLLs");
            Assert.Equal(ImportResolutionKind.Managed, mapped.Imports.Single().Resolution.Kind, "cross-DLL IAT replacement");
            Assert.Equal((uint)process.Id, Call(process, runtime.ResolveExport(process, mapped.BaseAddress, "Cross")), "native-to-managed cross-DLL call");
            Assert.Equal((uint)process.Id, Call(process, runtime.ResolveExport(process, mapped.BaseAddress, "Forward")), "forwarded replacement");
            var other = process.FindModuleByName("partial-a")!;
            Assert.Equal(6u, Call(process, runtime.ResolveExport(process, other.BaseAddress, "Cross")), "native part of dependency");
            Assert.Equal(6u, Call(process, runtime.ResolveExport(process, mapped.BaseAddress, "Internal")), "unmodified module's internal call");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void ConflictingAliases()
    {
        var directory = TestHost.NewTempDirectory("partial-conflict");
        try
        {
            var path = WriteLibrary(directory, "partial-a.dll");
            using var runtime = CreateRuntime();
            runtime.Apis.RegisterPartiallyImagined("partial-a", "Alias", _ => 9);
            var process = runtime.CreateProcess(path);
            Assert.Throws<InvalidOperationException>(() => process.LoadManagedLibrary(path), "shared entry rejects different implementations");
            Assert.Equal(0, process.LoadedModules.Count, "failed mapping removed");
        }
        finally { Directory.Delete(directory, true); }
    }

    // A CRT-free PE with adjacent tiny exports, a relative internal call, and an optional IAT call.
    private static string WriteLibrary(string directory, string name, string? dependency = null)
    {
        var bytes = new byte[0x1000];
        void W16(int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);
        void W32(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
        void W64(int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset), value);
        void Text(uint rva, string value) => Encoding.ASCII.GetBytes(value + "\0").CopyTo(bytes, (int)rva - 0xE00);
        void Code(int rva, byte[] code) => code.CopyTo(bytes, rva - 0xE00);

        W16(0, 0x5A4D);
        W32(0x3C, 0x80);
        W32(0x80, 0x4550);
        W16(0x84, 0x8664);
        W16(0x86, 1);
        W16(0x94, 0xF0);
        W16(0x96, 0x2022);
        W16(0x98, 0x20B);
        W64(0xB0, 0x180000000);
        W32(0xB8, 0x1000);
        W32(0xBC, 0x200);
        W32(0xD0, 0x2000);
        W32(0xD4, 0x200);
        W16(0xDC, 3);
        W32(0x104, 16);
        W32(0x108, 0x1400);
        W32(0x10C, 0x300);
        Encoding.ASCII.GetBytes(".text").CopyTo(bytes, 0x188);
        W32(0x190, 0xE00);
        W32(0x194, 0x1000);
        W32(0x198, 0xE00);
        W32(0x19C, 0x200);
        W32(0x1AC, 0x60000020);

        Code(0x1000, [0xB8, 6, 0, 0, 0, 0xC3, 0xB8, 42, 0, 0, 0, 0xC3]);
        Code(0x1010, [0x48, 0x83, 0xEC, 0x28, 0xE8, 0xE7, 0xFF, 0xFF, 0xFF, 0x48, 0x83, 0xC4, 0x28, 0xC3]);
        Code(0x1030, [0xB8, 8, 0, 0, 0, 0xC3]);
        if (dependency is not null)
        {
            W32(0x110, 0x1200);
            W32(0x114, 40);
            W32(0x400, 0x1340);
            W32(0x40C, 0x1300);
            W32(0x410, 0x1320);
            Text(0x1300, dependency);
            W64(0x520, 0x1360);
            W64(0x540, 0x1360);
            Text(0x1362, "Replace");
            Code(0x1030, [0x48, 0x83, 0xEC, 0x28, 0xFF, 0x15, 0xE6, 2, 0, 0, 0x48, 0x83, 0xC4, 0x28, 0xC3]);
        }

        W32(0x610, 1);
        W32(0x614, 5);
        W32(0x618, 6);
        W32(0x61C, 0x1440);
        W32(0x620, 0x1480);
        W32(0x624, 0x14B0);
        uint[] functions = [0x1000, 0x1006, 0x1010, 0x1030, 0x1600];
        for (var i = 0; i < functions.Length; i++) W32(0x640 + i * 4, functions[i]);
        string[] names = ["Replace", "Alias", "Tiny", "Internal", "Cross", "Forward"];
        ushort[] ordinals = [0, 0, 1, 2, 3, 4];
        uint cursor = 0x1500;
        for (var i = 0; i < names.Length; i++)
        {
            W32(0x680 + i * 4, cursor);
            W16(0x6B0 + i * 2, ordinals[i]);
            Text(cursor, names[i]);
            cursor += (uint)names[i].Length + 1;
        }
        Text(0x1600, dependency is null ? Path.GetFileNameWithoutExtension(name) + ".Tiny" :
            Path.GetFileNameWithoutExtension(dependency) + ".Replace");
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
