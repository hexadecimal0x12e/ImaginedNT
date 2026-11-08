using System.Reflection;
using ImaginedNT.Core.Io;
using ImaginedNT.Core.Memory;
using ImaginedNT.Core.Nt;
using ImaginedNT.Core.Objects;
using ImaginedNT.Core.Pe;
using ImaginedNT.Core.Runtime;

namespace ImaginedNT.Tests;

internal static class Program
{
    private static int Main(string[] args)
    {
        var filter = args.FirstOrDefault(a => !a.StartsWith('-'));
        var tests = new List<(string Name, Action Body)>();
        PeTests.Register(tests);
        MemoryTests.Register(tests);
        ObjectFileTests.Register(tests);
        ManagedApiTests.Register(tests);
        PartiallyImaginedTests.Register(tests);
        NativeApiDeclarationTests.Register(tests);
        MethodTraceTests.Register(tests);
        LoaderTests.Register(tests);
        ProcessCreationTests.Register(tests);
        EndToEndTests.Register(tests);
        ShellTests.Register(tests);

        var passed = 0;
        var failed = 0;
        var skipped = 0;
        var failures = new List<string>();

        foreach (var (name, body) in tests)
        {
            if (filter is not null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                body();
                passed++;
                Console.WriteLine($"PASS  {name}");
            }
            catch (SkipTestException ex)
            {
                skipped++;
                Console.WriteLine($"SKIP  {name}: {ex.Message}");
            }
            catch (Exception ex)
            {
                failed++;
                failures.Add($"{name}: {ex.GetType().Name}: {ex.Message}");
                Console.WriteLine($"FAIL  {name}: {ex.GetType().Name}: {ex.Message}");
                if (Environment.GetEnvironmentVariable("IMAGINEDNT_TEST_DEBUG") == "1")
                    Console.WriteLine(ex);
            }
        }

        Console.WriteLine();
        Console.WriteLine($"passed={passed} failed={failed} skipped={skipped}");
        if (failures.Count > 0)
        {
            Console.WriteLine();
            foreach (var failure in failures) Console.WriteLine("  " + failure);
            return 1;
        }
        return 0;
    }
}

internal static class PeTests
{
    public static void Register(List<(string, Action)> tests)
    {
        tests.Add(("PE parses headers/sections/imports", ParseHello));
        tests.Add(("PE parses the relocation directory", ParseRelocations));
    }

    private static void ParseHello()
    {
        TestHost.RequireSamples();
        var image = PeImage.Load(TestHost.Sample("hello.exe"));
        Assert.Equal(PeMachine.Amd64, image.Machine, "machine");
        Assert.True(image.Is64Bit, "PE32+");
        Assert.False(image.IsRelocationStripped, "relocations are not marked stripped");
        Assert.Equal(0x1000u, image.EntryPointRva, "entry RVA");
        Assert.Equal(3, image.Sections.Count, "section count");
        Assert.Equal(3, image.Imports.Count, "import count");
        Assert.True(image.Imports.Any(i => i.ModuleName.Equals("KERNEL32.dll", StringComparison.OrdinalIgnoreCase) && i.Name == "WriteFile"), "WriteFile import");
        Assert.True(image.Sections.Any(s => s.Name == ".text" && s.IsExecutable), ".text is executable");
        Assert.True(image.Imports.All(i => image.RvaToFileOffset(i.IatRva) > 0), "IAT RVAs are mapped");
    }

    private static void ParseRelocations()
    {
        TestHost.RequireSamples();
        var image = PeImage.Load(TestHost.Sample("reloc.exe"));
        Assert.True(image.HasRelocations, "has relocations");
        Assert.True(image.Relocations.Count >= 1, "relocation count");
        Assert.True(image.Relocations.Any(r => r.Type == 10), "contains DIR64");
    }
}

internal static class MemoryTests
{
    public static void Register(List<(string, Action)> tests)
    {
        tests.Add(("VirtualMemoryManager reserve/commit/protect/query/free", MemoryLifecycle));
        tests.Add(("VirtualMemoryManager rejects invalid protect of unmapped memory", ProtectUnmapped));
        tests.Add(("Host ntdll syscall numbers map to managed NT APIs", RealSyscallMap));
    }

    private static void MemoryLifecycle()
    {
        using var memory = new VirtualMemoryManager();
        var status = memory.TryAllocate(0, 0x1234, MemoryProtection.ReadWrite,
            AllocationType.Commit | AllocationType.Reserve, out var address, out var size, "test allocation");
        Assert.Equal(NtStatus.Success, status, "allocate status");
        Assert.NotEqual(0UL, address, "allocation address");
        Assert.True(size >= 0x2000, "allocation rounded to pages/granularity");

        var payload = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        memory.WriteBytes(address, payload);
        Assert.Equal(8, memory.ReadBytes(address, 8).Length, "read length");
        Assert.Equal((byte)4, memory.ReadBytes(address, 8)[3], "roundtrip byte");

        var query = memory.Query(address);
        Assert.NotNull(query, "query result");
        Assert.Equal(MemoryState.Commit, query!.State, "query state");

        status = memory.TryProtect(address, 0x1000, MemoryProtection.ReadOnly, out var old);
        Assert.Equal(NtStatus.Success, status, "protect status");
        Assert.True(old != 0, "old protection available");
        Assert.False(memory.TryWrite(address, new byte[] { 9 }), "write to read-only page must fail");

        status = memory.TryProtect(address, 0x1000, MemoryProtection.ReadWrite, out _);
        Assert.Equal(NtStatus.Success, status, "re-protect status");
        Assert.True(memory.TryWrite(address, new byte[] { 9 }), "write after re-protect");

        status = memory.TryFree(address, 0, FreeType.Release);
        Assert.Equal(NtStatus.Success, status, "free status");
    }

    private static void ProtectUnmapped()
    {
        using var memory = new VirtualMemoryManager();
        var status = memory.TryProtect(0x1234_0000, 0x1000, MemoryProtection.ReadWrite, out _);
        Assert.True(status != NtStatus.Success, "protecting an unmapped page fails");
    }

    private static void RealSyscallMap()
    {
        Assert.True(NativeSyscallMap.Count > 50, "ntdll syscall numbers discovered");
        Assert.True(NativeSyscallMap.TryGetNumber("NtClose", out var number), "NtClose syscall number");
        Assert.True(number > 0, "syscall number is non-zero");

        using var runtime = BuiltinApis.CreateRuntime(RuntimeOptions.ForTests());
        var process = runtime.CreateProcess(TestHost.Sample("hello.exe"));
        var ev = new EventObject(manualReset: true, initialState: false);
        var handle = process.Handles.Create(ev, 0x1);
        ev.Release();
        var args = new ulong[6];
        args[0] = handle;
        var status = runtime.Syscalls.Dispatch(number, args, 0, out var result);
        Assert.Equal(NtStatus.Success, status, "real syscall dispatch status");
        Assert.Equal(0UL, result, "NtClose returns STATUS_SUCCESS");
        Assert.False(process.Handles.TryGet(handle, out _), "NtClose closed the managed handle");
    }
}

internal static class ObjectFileTests
{
    public static void Register(List<(string, Action)> tests)
    {
        tests.Add(("ObjectManager named events, symlinks and handles", Objects));
        tests.Add(("SandboxFileSystem maps C: and blocks traversal", FileSandbox));
    }

    private static void Objects()
    {
        using var manager = new ObjectManager();
        var ev = new EventObject(manualReset: true, initialState: false);
        manager.CreateObject("\\BaseNamedObjects\\test-event", ev);
        var opened = manager.Open<EventObject>("\\BaseNamedObjects\\test-event");
        Assert.NotNull(opened, "named event opens");
        Assert.True(ReferenceEquals(opened, ev), "named event identity");

        using var handles = new HandleTable();
        var handle = handles.Create(opened!, 0x1);
        Assert.True(handle != 0, "handle allocated");
        Assert.Equal(NtStatus.WaitTimeout, opened!.Wait(0), "unsignaled event times out");
        opened.Set();
        Assert.Equal(NtStatus.Wait0, opened.Wait(0), "signaled event completes");

        manager.CreateSymbolicLink("\\TestLink", "\\BaseNamedObjects");
        var throughLink = manager.Open<EventObject>("\\TestLink\\test-event");
        Assert.True(ReferenceEquals(throughLink, ev), "symlink resolves");

        Assert.Equal(NtStatus.Success, handles.Close(handle), "handle close");
        Assert.Equal(NtStatus.InvalidHandle, handles.Close(handle), "double close fails");

        var restricted = handles.Create(opened, 0x1);
        Assert.Equal(NtStatus.AccessDenied, handles.TryGet(restricted, 0x2, out _), "access mask enforced");
        handles.Close(restricted);

        var inheritable = handles.Create(opened, 0x1);
        Assert.True(handles.TryGetInformation(inheritable, out var handleFlags), "handle information");
        Assert.Equal(0u, handleFlags & HandleTable.HandleFlagInherit, "not inheritable by default");
        Assert.True(handles.SetInformation(inheritable, HandleTable.HandleFlagInherit, HandleTable.HandleFlagInherit), "set inherit flag");
        Assert.True(handles.TryGetInformation(inheritable, out handleFlags), "handle information after set");
        Assert.Equal(HandleTable.HandleFlagInherit, handleFlags & HandleTable.HandleFlagInherit, "inherit flag set");
        handles.Close(inheritable);
    }

    private static void FileSandbox()
    {
        var root = TestHost.NewTempDirectory("fs");
        try
        {
            var fs = new SandboxFileSystem(root);
            var mapped = fs.MapPath("\\??\\C:\\nested\\file.txt");
            Assert.True(mapped.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase), "path maps under root");
            Assert.True(fs.MapPath("C:\\file.txt").StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase), "drive path maps under root");
            Assert.Throws<NtFileException>(() => fs.MapPath("C:\\..\\..\\escape.txt"), "traversal blocked");
            Assert.Throws<NtFileException>(() => fs.MapPath("..\\..\\..\\escape.txt", "C:\\a"), "relative traversal blocked");

            Directory.CreateDirectory(Path.Combine(root, "nested"));
            using (var writer = fs.CreateOrOpen("C:\\nested\\file.txt", (uint)NtFileAccess.GenericWrite, 0,
                       CreateDisposition.Create, CreateOptions.NonDirectoryFile))
            {
                writer.Write(System.Text.Encoding.ASCII.GetBytes("sandbox-data"));
                writer.Flush();
            }

            using var reader = fs.CreateOrOpen("C:\\nested\\file.txt", (uint)NtFileAccess.GenericRead, 0,
                CreateDisposition.Open, CreateOptions.NonDirectoryFile);
            var buffer = new byte[32];
            var read = reader.Read(buffer);
            Assert.Equal("sandbox-data", System.Text.Encoding.ASCII.GetString(buffer, 0, read), "sandbox file roundtrip");

            // Relative paths must resolve against a Win32-style current directory (C:\nested), not
            // against the host drive.
            using (var relative = fs.CreateOrOpen("relative.txt", (uint)NtFileAccess.GenericWrite, 0,
                       CreateDisposition.Create, CreateOptions.NonDirectoryFile, "C:\\nested"))
            {
                relative.Write(System.Text.Encoding.ASCII.GetBytes("relative"));
                relative.Flush();
            }
            Assert.True(File.Exists(Path.Combine(root, "nested", "relative.txt")), "relative path resolves under the sandbox");

            var readOnly = new SandboxFileSystem(root, allowWrites: false);
            Assert.Throws<NtFileException>(
                () => readOnly.CreateOrOpen("C:\\nested\\blocked.txt", (uint)NtFileAccess.GenericWrite, 0,
                    CreateDisposition.Create, CreateOptions.NonDirectoryFile),
                "read-only sandbox rejects writes");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}

internal static class ManagedApiTests
{
    public static void Register(List<(string, Action)> tests)
    {
        tests.Add(("VirtualApi attributes are discovered at runtime startup", DiscoveryCoversAttributes));
    }

    private static void DiscoveryCoversAttributes()
    {
        using var runtime = BuiltinApis.CreateRuntime(RuntimeOptions.ForTests());
        var declared = BuiltinApis.Assemblies.SelectMany(assembly => assembly.GetTypes())
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                                BindingFlags.Static | BindingFlags.DeclaredOnly))
            .SelectMany(method => method.GetCustomAttributes<VirtualApiAttribute>(inherit: false))
            .ToList();

        var expected = declared.Sum(attribute => 1 + attribute.Aliases.Count);
        Assert.True(expected > 100, "managed API surface discovered");
        Assert.Equal(expected, runtime.Apis.Count, "registered entry point count");

        static bool IsRegistered(ApiRegistry registry, string module, string name)
        {
            var key = ApiRegistry.Key(module, name);
            return registry.All.Any(api => ApiRegistry.Key(api.Module, api.Name) == key);
        }

        foreach (var attribute in declared)
        {
            Assert.True(IsRegistered(runtime.Apis, attribute.Module, attribute.Name),
                $"exact registration for {attribute}");
            foreach (var alias in attribute.Aliases)
            {
                var bang = alias.IndexOf('!');
                Assert.True(bang > 0, $"alias shape: {alias}");
                Assert.True(IsRegistered(runtime.Apis, alias[..bang], alias[(bang + 1)..]),
                    $"alias registration for {alias}");
            }
        }
    }
}

internal static class LoaderTests
{
    public static void Register(List<(string, Action)> tests)
    {
        tests.Add(("Loader maps image and patches the IAT", LoadAndPatch));
        tests.Add(("Loader maps a relocation-free image at an alternate base", RelocationFreeAlternateBase));
    }

    private static RuntimeOptions Options()
    {
        var options = RuntimeOptions.ForTests();
        options.InterceptSyscalls = false;
        return options;
    }

    private static void LoadAndPatch()
    {
        TestHost.RequireSamples();
        using var runtime = BuiltinApis.CreateRuntime(Options());
        var process = runtime.CreateProcess(TestHost.Sample("hello.exe"));
        var image = process.LoadImage(TestHost.Sample("hello.exe"));
        Assert.Equal(3, image.Imports.Count, "import bindings");
        Assert.True(image.Imports.All(i => i.Resolution.Kind == ImportResolutionKind.Managed), "all managed");
        foreach (var binding in image.Imports)
        {
            var slotValue = process.Memory.Read<ulong>(binding.SlotAddress);
            Assert.Equal(binding.Resolution.Address, slotValue, $"IAT slot {binding.Import.DisplayName}");
        }
    }

    private static void RelocationFreeAlternateBase()
    {
        TestHost.RequireSamples();
        using var runtime = BuiltinApis.CreateRuntime(Options());
        var process = runtime.CreateProcess(TestHost.Sample("hello.exe"));
        var image = process.LoadImage(TestHost.Sample("hello.exe"), 0x1800_0000);
        Assert.True(image.WasRelocated, "image was mapped away from its preferred base");
        Assert.True(image.BaseAddress != image.Image.ImageBase, "alternate base honoured");
        Assert.Equal(3, image.Imports.Count, "imports patched at the alternate base");
        foreach (var binding in image.Imports)
            Assert.Equal(binding.Resolution.Address, process.Memory.Read<ulong>(binding.SlotAddress), $"IAT slot {binding.Import.DisplayName}");
    }
}
