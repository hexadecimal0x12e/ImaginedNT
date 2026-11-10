using System.Runtime.InteropServices;
using ImaginedNT.Apis.Com;
using ImaginedNT.Core.Com;
using ImaginedNT.Core.Runtime;

namespace ImaginedNT.Tests;

public static unsafe class ComTests
{
    public const string PartialClassId = "{9B31D57E-2B14-4C6A-8F03-7A1E6C2D9B45}";
    public const string PartialHostClassId = "{3C7F61A2-9E48-4B12-A5D6-0E8B4F7C2A19}";

    [ComVisible(true)]
    [Guid("4D2C9A10-7B3E-4F1A-9C22-5D771B8E4A61")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IHostImaginedComTest
    {
        // [PreserveSig] keeps the CCW native signature identical to the COM vtable: the int
        // return value is the HRESULT, not an extra [out, retval] parameter.
        [PreserveSig] int Add(int a, int b, out int result);
        [PreserveSig] int GetValue(out int value);
        [PreserveSig] int SetValue(int value);
        [PreserveSig] int GetName([MarshalAs(UnmanagedType.BStr)] out string name);
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class HostImaginedComTest : IHostImaginedComTest
    {
        private int _value = 123;

        public int Add(int a, int b, out int result)
        {
            result = a + b;
            return 0;
        }

        public int GetValue(out int value)
        {
            value = _value;
            return 0;
        }

        public int SetValue(int value)
        {
            _value = value;
            return 0;
        }

        public int GetName(out string name)
        {
            name = "HostCom";
            return 0;
        }
    }

    /// <summary>Only slot 3 (Add) is reimplemented; GetValue/SetValue/GetName come from the host.</summary>
    [PartiallyImaginedComClass(PartialClassId, PartialHostClassId, "Imagined.ComPartialTest")]
    public sealed class PartialImaginedComTest
    {
        [PartiallyImaginedComMethod(ImaginedComTest.InterfaceId, 3, ImaginedComTest.AddMethod)]
        public ulong Add(ComCallContext context)
        {
            var resultPointer = context.Arg(3);
            if (resultPointer == 0) return ComHResult.AsReturnValue(ComHResult.E_POINTER);
            context.WriteInt32(3, 1000 + context.ArgInt32(1) + context.ArgInt32(2));
            return ComHResult.AsReturnValue(ComHResult.S_OK);
        }
    }

    public static void Register(List<(string, Action)> tests)
    {
        tests.Add(("COM discovers managed interfaces, classes and ole32 APIs", Discovery));
        tests.Add(("COM creates a virtual object and invokes its vtable", VirtualObject));
        tests.Add(("COM class factory creates instances and locks the server", ClassFactory));
        tests.Add(("COM compares IUnknown/AddRef/Release and BSTR marshaling", LifetimeAndBstr));
        tests.Add(("Partially Imagined COM forwards non-overridden slots to a host object", PartialForwarding));
        tests.Add(("COM CoLoadLibrary/CoFreeLibrary loads and unloads an in-process server", LibraryLoading));
        tests.Add(("COM DllGetClassObject creates objects from a guest server DLL", ServerDll));
        tests.Add(("COM falls back to a real host COM component", HostFallback));
        tests.Add(("COM returns REGDB_E_CLASSNOTREG when host activation is disabled", ManagedOnlyMissingClass));
        tests.Add(("COM GUID and task-memory helpers marshal parameters", GuidAndTaskMemory));
    }

    private static void Discovery()
    {
        using var runtime = BuiltinApis.CreateRuntime(RuntimeOptions.ForTests());
        Assert.NotNull(runtime.Com.FindClass(ImaginedComTest.ClassGuid), "built-in COM class registered");
        Assert.True(runtime.Com.TryGetInterface(ImaginedComTest.InterfaceGuid, out var definition),
            "built-in COM interface registered");
        Assert.NotNull(definition, "interface definition");
        Assert.Equal(4, definition!.Methods.Count, "four declared interface methods");
        Assert.True(definition.Methods.ContainsKey(3) && definition.Methods.ContainsKey(6), "slot range");
        Assert.True(runtime.Apis.TryGet("ole32", "CoCreateInstance", out _), "CoCreateInstance registered");
        Assert.True(runtime.Apis.TryGet("api-ms-win-core-com-l1-1-0.dll", "CoCreateInstance", out _),
            "COM API set resolves to ole32");
    }

    private static (ImaginedRuntime Runtime, ImaginedProcess Process, nint Initialize, nint Create, nint Uninitialize)
        CreateComEnvironment()
    {
        TestHost.RequireSamples();
        var runtime = BuiltinApis.CreateRuntime(RuntimeOptions.ForTests());
        var process = runtime.CreateProcess(new ProcessCreateOptions { ImagePath = TestHost.Sample("hello.exe") });
        var initialize = (nint)runtime.GetManagedApiAddress("ole32", "CoInitializeEx");
        var create = (nint)runtime.GetManagedApiAddress("ole32", "CoCreateInstance");
        var uninitialize = (nint)runtime.GetManagedApiAddress("ole32", "CoUninitialize");
        var hr = ((delegate* unmanaged<nint, uint, int>)initialize)(0, 0);
        Assert.Equal(0, hr, "CoInitializeEx (MTA)");
        return (runtime, process, initialize, create, uninitialize);
    }

    private static void VirtualObject()
    {
        var (runtime, process, _, createAddress, uninitializeAddress) = CreateComEnvironment();
        try
        {
            var classId = ImaginedComTest.ClassGuid;
            var interfaceId = ImaginedComTest.InterfaceGuid;
            ulong objectPointer = 0;
            var hr = ((delegate* unmanaged<Guid*, ulong, uint, Guid*, ulong*, int>)createAddress)(
                &classId, 0, 1, &interfaceId, &objectPointer);
            Assert.Equal(0, hr, "CoCreateInstance");
            Assert.NotEqual(0UL, objectPointer, "interface pointer");

            var vtable = process.Memory.Read<ulong>(objectPointer);
            var add = (delegate* unmanaged<nint, int, int, int*, int>)process.Memory.Read<ulong>(vtable + 3 * 8);
            var setValue = (delegate* unmanaged<nint, int, int>)process.Memory.Read<ulong>(vtable + 5 * 8);
            var getValue = (delegate* unmanaged<nint, int*, int>)process.Memory.Read<ulong>(vtable + 4 * 8);
            var release = (delegate* unmanaged<nint, uint>)process.Memory.Read<ulong>(vtable + 2 * 8);

            int result = 0;
            Assert.Equal(0, add((nint)objectPointer, 2, 3, &result), "Add HRESULT");
            Assert.Equal(5, result, "Add result");
            Assert.Equal(0, setValue((nint)objectPointer, 77), "SetValue HRESULT");
            int value = 0;
            Assert.Equal(0, getValue((nint)objectPointer, &value), "GetValue HRESULT");
            Assert.Equal(77, value, "SetValue/GetValue roundtrip");
            Assert.Equal(0u, release((nint)objectPointer), "final Release");
            Assert.Equal(0, runtime.Com.LiveObjectCount, "object destroyed");
        }
        finally
        {
            ((delegate* unmanaged<int>)uninitializeAddress)();
            runtime.Dispose();
        }
    }

    private static void ClassFactory()
    {
        var (runtime, process, _, _, uninitializeAddress) = CreateComEnvironment();
        try
        {
            var classId = ImaginedComTest.ClassGuid;
            var factoryId = ComBuiltins.IClassFactoryId;
            ulong factory = 0;
            var getClassObject = (delegate* unmanaged<Guid*, uint, nint, Guid*, ulong*, int>)
                (nint)runtime.GetManagedApiAddress("ole32", "CoGetClassObject");
            Assert.Equal(0, getClassObject(&classId, 1, 0, &factoryId, &factory), "CoGetClassObject");
            Assert.NotEqual(0UL, factory, "class factory pointer");

            var factoryVtable = process.Memory.Read<ulong>(factory);
            var lockServer = (delegate* unmanaged<nint, int, int>)process.Memory.Read<ulong>(factoryVtable + 4 * 8);
            var createInstance = (delegate* unmanaged<nint, nint, Guid*, ulong*, int>)
                process.Memory.Read<ulong>(factoryVtable + 3 * 8);
            var factoryRelease = (delegate* unmanaged<nint, uint>)process.Memory.Read<ulong>(factoryVtable + 2 * 8);

            Assert.Equal(0, lockServer((nint)factory, 1), "LockServer(TRUE)");
            var interfaceId = ImaginedComTest.InterfaceGuid;
            ulong objectPointer = 0;
            Assert.Equal(0, createInstance((nint)factory, 0, &interfaceId, &objectPointer), "CreateInstance");
            Assert.NotEqual(0UL, objectPointer, "factory-created object");

            var vtable = process.Memory.Read<ulong>(objectPointer);
            var add = (delegate* unmanaged<nint, int, int, int*, int>)process.Memory.Read<ulong>(vtable + 3 * 8);
            var release = (delegate* unmanaged<nint, uint>)process.Memory.Read<ulong>(vtable + 2 * 8);
            int result = 0;
            Assert.Equal(0, add((nint)objectPointer, 7, 8, &result), "factory object Add");
            Assert.Equal(15, result, "factory object Add result");
            release((nint)objectPointer);

            Assert.Equal(0, lockServer((nint)factory, 0), "LockServer(FALSE)");
            Assert.Equal(0u, factoryRelease((nint)factory), "factory Release");
        }
        finally
        {
            ((delegate* unmanaged<int>)uninitializeAddress)();
            runtime.Dispose();
        }
    }

    private static void LifetimeAndBstr()
    {
        var (runtime, process, _, createAddress, uninitializeAddress) = CreateComEnvironment();
        try
        {
            var classId = ImaginedComTest.ClassGuid;
            var interfaceId = ImaginedComTest.InterfaceGuid;
            ulong objectPointer = 0;
            Assert.Equal(0, ((delegate* unmanaged<Guid*, ulong, uint, Guid*, ulong*, int>)createAddress)(
                &classId, 0, 1, &interfaceId, &objectPointer), "CoCreateInstance");

            var vtable = process.Memory.Read<ulong>(objectPointer);
            var queryInterface = (delegate* unmanaged<nint, Guid*, ulong*, int>)process.Memory.Read<ulong>(vtable);
            var addRef = (delegate* unmanaged<nint, uint>)process.Memory.Read<ulong>(vtable + 8);
            var release = (delegate* unmanaged<nint, uint>)process.Memory.Read<ulong>(vtable + 16);
            var getName = (delegate* unmanaged<nint, ulong*, int>)process.Memory.Read<ulong>(vtable + 6 * 8);

            var unknownId = ComBuiltins.IUnknownId;
            ulong unknown = 0;
            Assert.Equal(0, queryInterface((nint)objectPointer, &unknownId, &unknown), "QueryInterface(IUnknown)");
            Assert.NotEqual(0UL, unknown, "IUnknown pointer");
            Assert.Equal(3u, addRef((nint)objectPointer), "AddRef after QueryInterface");
            Assert.Equal(2u, release((nint)objectPointer), "Release QueryInterface reference");

            ulong name = 0;
            Assert.Equal(0, getName((nint)objectPointer, &name), "GetName");
            var text = ComMarshaling.ReadBstr(process.Memory, name);
            Assert.Equal("ImaginedNT", text, "BSTR contents");
            Assert.Equal(10, ComMarshaling.BstrLength(process.Memory, name), "BSTR length");
            Marshal.FreeBSTR((IntPtr)(long)name);

            Assert.Equal(1u, release((nint)objectPointer), "Release before final");
            Assert.Equal(0u, release((nint)objectPointer), "final Release");
        }
        finally
        {
            ((delegate* unmanaged<int>)uninitializeAddress)();
            runtime.Dispose();
        }
    }

    private static void PartialForwarding()
    {
        TestHost.RequireSamples();
        using var runtime = BuiltinApis.CreateRuntime(RuntimeOptions.ForTests());
        // Register only the COM metadata: the test assembly also carries native-declaration test
        // attributes that would conflict with the built-in CRT declarations.
        ComScanner.Register(typeof(ComTests).Assembly, runtime.Com);
        var process = runtime.CreateProcess(new ProcessCreateOptions { ImagePath = TestHost.Sample("hello.exe") });

        var host = new HostImaginedComTest();
        var hostClassId = Guid.Parse(PartialHostClassId);
        runtime.Com.RegisterHostClass(hostClassId, iid =>
        {
            if (iid == ImaginedComTest.InterfaceGuid)
                return (ulong)(long)Marshal.GetComInterfaceForObject(host, typeof(IHostImaginedComTest));
            if (iid == ComBuiltins.IUnknownId)
                return (ulong)(long)Marshal.GetIUnknownForObject(host);
            return 0;
        });

        var initialize = (delegate* unmanaged<nint, uint, int>)(nint)runtime.GetManagedApiAddress("ole32", "CoInitializeEx");
        Assert.Equal(0, initialize(0, 0), "CoInitializeEx");

        var classId = Guid.Parse(PartialClassId);
        var interfaceId = ImaginedComTest.InterfaceGuid;
        ulong objectPointer = 0;
        var create = (delegate* unmanaged<Guid*, ulong, uint, Guid*, ulong*, int>)
            (nint)runtime.GetManagedApiAddress("ole32", "CoCreateInstance");
        Assert.Equal(0, create(&classId, 0, 1, &interfaceId, &objectPointer), "partial CoCreateInstance");
        Assert.NotEqual(0UL, objectPointer, "partial interface pointer");
        Assert.Equal(1, runtime.Com.LiveObjectCount, "live partial object");

        var vtable = process.Memory.Read<ulong>(objectPointer);
        var add = (delegate* unmanaged<nint, int, int, int*, int>)process.Memory.Read<ulong>(vtable + 3 * 8);
        var getValue = (delegate* unmanaged<nint, int*, int>)process.Memory.Read<ulong>(vtable + 4 * 8);
        var setValue = (delegate* unmanaged<nint, int, int>)process.Memory.Read<ulong>(vtable + 5 * 8);
        var getName = (delegate* unmanaged<nint, ulong*, int>)process.Memory.Read<ulong>(vtable + 6 * 8);
        var release = (delegate* unmanaged<nint, uint>)process.Memory.Read<ulong>(vtable + 2 * 8);

        int result = 0;
        Assert.Equal(0, add((nint)objectPointer, 2, 3, &result), "overridden Add");
        Assert.Equal(1005, result, "overridden Add runs managed code");

        int value = 0;
        Assert.Equal(0, getValue((nint)objectPointer, &value), "forwarded GetValue");
        Assert.Equal(123, value, "forwarded GetValue reads host state");

        Assert.Equal(0, setValue((nint)objectPointer, 456), "forwarded SetValue");
        Assert.Equal(0, getValue((nint)objectPointer, &value), "forwarded GetValue after SetValue");
        Assert.Equal(456, value, "forwarded SetValue reached host object");

        ulong name = 0;
        Assert.Equal(0, getName((nint)objectPointer, &name), "forwarded GetName");
        Assert.Equal("HostCom", ComMarshaling.ReadBstr(process.Memory, name), "forwarded BSTR");
        Marshal.FreeBSTR((IntPtr)(long)name);

        Assert.Equal(0u, release((nint)objectPointer), "partial final Release");
        Assert.Equal(0, runtime.Com.LiveObjectCount, "partial object destroyed");
        ((delegate* unmanaged<int>)(nint)runtime.GetManagedApiAddress("ole32", "CoUninitialize"))();
    }

    private static void ServerDll()
    {
        var (runtime, process, _, _, uninitializeAddress) = CreateComEnvironment();
        try
        {
            File.Copy(TestHost.Sample("comserver.dll"), Path.Combine(process.FileSystem.Root, "comserver.dll"),
                overwrite: true);
            runtime.Com.RegisterServer("C:\\comserver.dll");

            var classId = Guid.Parse("A1B2C3D4-1122-3344-5566-778899AABBCC");
            var factoryId = ComBuiltins.IClassFactoryId;
            ulong factory = 0;
            var getClassObject = (delegate* unmanaged<Guid*, uint, nint, Guid*, ulong*, int>)
                (nint)runtime.GetManagedApiAddress("ole32", "CoGetClassObject");
            Assert.Equal(0, getClassObject(&classId, 1, 0, &factoryId, &factory), "CoGetClassObject(server)");
            Assert.NotEqual(0UL, factory, "guest class factory");

            var factoryVtable = process.Memory.Read<ulong>(factory);
            var createInstance = (delegate* unmanaged<nint, nint, Guid*, ulong*, int>)
                process.Memory.Read<ulong>(factoryVtable + 3 * 8);
            var factoryRelease = (delegate* unmanaged<nint, uint>)process.Memory.Read<ulong>(factoryVtable + 2 * 8);

            var interfaceId = ImaginedComTest.InterfaceGuid;
            ulong objectPointer = 0;
            Assert.Equal(0, createInstance((nint)factory, 0, &interfaceId, &objectPointer), "server CreateInstance");
            Assert.NotEqual(0UL, objectPointer, "server object pointer");

            var vtable = process.Memory.Read<ulong>(objectPointer);
            var add = (delegate* unmanaged<nint, int, int, int*, int>)process.Memory.Read<ulong>(vtable + 3 * 8);
            var getValue = (delegate* unmanaged<nint, int*, int>)process.Memory.Read<ulong>(vtable + 4 * 8);
            var setValue = (delegate* unmanaged<nint, int, int>)process.Memory.Read<ulong>(vtable + 5 * 8);
            var getName = (delegate* unmanaged<nint, ulong*, int>)process.Memory.Read<ulong>(vtable + 6 * 8);
            var release = (delegate* unmanaged<nint, uint>)process.Memory.Read<ulong>(vtable + 2 * 8);

            int result = 0;
            Assert.Equal(0, add((nint)objectPointer, 1, 1, &result), "server Add");
            Assert.Equal(2002, result, "server Add result");
            int value = 0;
            Assert.Equal(0, getValue((nint)objectPointer, &value), "server GetValue");
            Assert.Equal(500, value, "server initial value");
            Assert.Equal(0, setValue((nint)objectPointer, 700), "server SetValue");
            Assert.Equal(0, getValue((nint)objectPointer, &value), "server GetValue after SetValue");
            Assert.Equal(700, value, "server value changed");

            ulong name = 0;
            Assert.Equal(0, getName((nint)objectPointer, &name), "server GetName");
            Assert.Equal("ImaginedComServer", ComMarshaling.ReadBstr(process.Memory, name), "server BSTR");
            Marshal.FreeBSTR((IntPtr)(long)name);

            Assert.Equal(0u, release((nint)objectPointer), "server object Release");

            // CoRegisterClassObject holds the guest factory and CoCreateInstance uses the
            // registered factory for a different CLSID.
            var coCreate = (delegate* unmanaged<Guid*, ulong, uint, Guid*, ulong*, int>)
                (nint)runtime.GetManagedApiAddress("ole32", "CoCreateInstance");
            var registeredClassId = Guid.Parse("{B2C3D4E5-6677-8899-AABB-CCDDEEFF0011}");
            uint cookie = 0;
            var register = (delegate* unmanaged<Guid*, ulong, uint, uint, uint*, int>)
                (nint)runtime.GetManagedApiAddress("ole32", "CoRegisterClassObject");
            Assert.Equal(0, register(&registeredClassId, factory, 1, 1, &cookie), "CoRegisterClassObject");
            Assert.NotEqual(0u, cookie, "class registration cookie");
            // The registration itself holds one reference to the guest factory.
            Assert.Equal(1u, factoryRelease((nint)factory), "factory Release after registration");

            ulong registeredObject = 0;
            Assert.Equal(0, coCreate(&registeredClassId, 0, 1, &interfaceId, &registeredObject),
                "guest-registered class CoCreateInstance");
            Assert.NotEqual(0UL, registeredObject, "guest-registered class object");
            var registeredVtable = process.Memory.Read<ulong>(registeredObject);
            var registeredAdd = (delegate* unmanaged<nint, int, int, int*, int>)
                process.Memory.Read<ulong>(registeredVtable + 3 * 8);
            var registeredRelease = (delegate* unmanaged<nint, uint>)
                process.Memory.Read<ulong>(registeredVtable + 2 * 8);
            int registeredResult = 0;
            Assert.Equal(0, registeredAdd((nint)registeredObject, 6, 7, &registeredResult),
                "guest-registered Add");
            Assert.Equal(2013, registeredResult, "guest-registered Add result");
            registeredRelease((nint)registeredObject);

            var revoke = (delegate* unmanaged<uint, int>)(nint)runtime.GetManagedApiAddress("ole32", "CoRevokeClassObject");
            Assert.Equal(0, revoke(cookie), "CoRevokeClassObject");

            // CoCreateInstance uses DllGetClassObject + IClassFactory::CreateInstance internally.
            ulong created = 0;
            Assert.Equal(0, coCreate(&classId, 0, 1, &interfaceId, &created), "server CoCreateInstance");
            Assert.NotEqual(0UL, created, "server CoCreateInstance object");
            var createdVtable = process.Memory.Read<ulong>(created);
            var createdAdd = (delegate* unmanaged<nint, int, int, int*, int>)
                process.Memory.Read<ulong>(createdVtable + 3 * 8);
            var createdRelease = (delegate* unmanaged<nint, uint>)
                process.Memory.Read<ulong>(createdVtable + 2 * 8);
            int createdResult = 0;
            Assert.Equal(0, createdAdd((nint)created, 4, 5, &createdResult), "server CoCreateInstance Add");
            Assert.Equal(2009, createdResult, "server CoCreateInstance Add result");
            createdRelease((nint)created);

            var freeUnused = (delegate* unmanaged<int>)(nint)runtime.GetManagedApiAddress("ole32", "CoFreeUnusedLibraries");
            Assert.Equal(0, freeUnused(), "CoFreeUnusedLibraries");
        }
        finally
        {
            ((delegate* unmanaged<int>)uninitializeAddress)();
            runtime.Dispose();
        }
    }

    private static void HostFallback()
    {
        var (runtime, process, _, createAddress, uninitializeAddress) = CreateComEnvironment();
        try
        {
            // CLSID_ShellLink is an ordinary in-process host COM class. This exercises the
            // "no managed registration" path: CoCreateInstance must ask the host's real COM
            // registry and return a genuinely native interface pointer.
            var classId = new Guid("00021401-0000-0000-C000-000000000046");
            var interfaceId = ComBuiltins.IUnknownId;
            ulong hostObject = 0;
            var hr = ((delegate* unmanaged<Guid*, ulong, uint, Guid*, ulong*, int>)createAddress)(
                &classId, 0, 1, &interfaceId, &hostObject);
            if (hr < 0)
                throw new SkipTestException($"host CLSID_ShellLink is unavailable (0x{hr:X8})");
            Assert.NotEqual(0UL, hostObject, "host interface pointer");

            var vtable = process.Memory.Read<ulong>(hostObject);
            var addRef = (delegate* unmanaged<nint, uint>)process.Memory.Read<ulong>(vtable + 8);
            var release = (delegate* unmanaged<nint, uint>)process.Memory.Read<ulong>(vtable + 16);
            Assert.True(addRef((nint)hostObject) >= 2, "host AddRef");
            release((nint)hostObject);
            release((nint)hostObject);
        }
        finally
        {
            ((delegate* unmanaged<int>)uninitializeAddress)();
            runtime.Dispose();
        }
    }

    private static void ManagedOnlyMissingClass()
    {
        TestHost.RequireSamples();
        var options = RuntimeOptions.ForTests();
        options.ImportPolicy = ImportPolicy.ManagedOnly;
        options.AllowNativeForwarding = false;
        using var runtime = BuiltinApis.CreateRuntime(options);
        var process = runtime.CreateProcess(new ProcessCreateOptions { ImagePath = TestHost.Sample("hello.exe") });
        var initialize = (delegate* unmanaged<nint, uint, int>)(nint)runtime.GetManagedApiAddress("ole32", "CoInitializeEx");
        Assert.Equal(0, initialize(0, 0), "CoInitializeEx");
        try
        {
            var classId = Guid.Parse("{DEADBEEF-0000-0000-0000-000000000001}");
            var interfaceId = ComBuiltins.IUnknownId;
            ulong pointer = 0;
            var create = (delegate* unmanaged<Guid*, ulong, uint, Guid*, ulong*, int>)
                (nint)runtime.GetManagedApiAddress("ole32", "CoCreateInstance");
            var hr = create(&classId, 0, 1, &interfaceId, &pointer);
            Assert.Equal(ComHResult.REGDB_E_CLASSNOTREG, hr, "missing class HRESULT");
            Assert.Equal(0UL, pointer, "no interface pointer");
        }
        finally
        {
            ((delegate* unmanaged<int>)(nint)runtime.GetManagedApiAddress("ole32", "CoUninitialize"))();
        }
    }

    private static void GuidAndTaskMemory()
    {
        var (runtime, process, _, _, uninitializeAddress) = CreateComEnvironment();
        try
        {
            var createGuid = (delegate* unmanaged<Guid*, int>)
                (nint)runtime.GetManagedApiAddress("ole32", "CoCreateGuid");
            Guid generated;
            Assert.Equal(0, createGuid(&generated), "CoCreateGuid");
            Assert.NotEqual(Guid.Empty, generated, "generated GUID");

            var classGuid = ImaginedComTest.ClassGuid;
            ulong text = 0;
            var stringFromClsid = (delegate* unmanaged<Guid*, ulong*, int>)
                (nint)runtime.GetManagedApiAddress("ole32", "StringFromCLSID");
            Assert.Equal(0, stringFromClsid(&classGuid, &text), "StringFromCLSID");
            Assert.NotEqual(0UL, text, "CLSID string pointer");
            var formatted = Marshal.PtrToStringUni((IntPtr)(long)text);
            Assert.Equal(ImaginedComTest.ClassId, formatted, "CLSID string contents");

            var free = (delegate* unmanaged<ulong, ulong>)
                (nint)runtime.GetManagedApiAddress("ole32", "CoTaskMemFree");
            free(text);

            Guid parsed;
            var input = process.WriteGuestString(ImaginedComTest.ClassId);
            var guidFromString = (delegate* unmanaged<ulong, Guid*, int>)
                (nint)runtime.GetManagedApiAddress("ole32", "CLSIDFromString");
            Assert.Equal(0, guidFromString(input, &parsed), "CLSIDFromString");
            Assert.Equal(classGuid, parsed, "parsed CLSID");

            var allocate = (delegate* unmanaged<ulong, ulong>)
                (nint)runtime.GetManagedApiAddress("ole32", "CoTaskMemAlloc");
            var pointer = allocate(16);
            Assert.NotEqual(0UL, pointer, "CoTaskMemAlloc");
            Marshal.WriteInt32((IntPtr)(long)pointer, 0x12345678);
            Assert.Equal(0x12345678, Marshal.ReadInt32((IntPtr)(long)pointer), "task memory roundtrip");
            free(pointer);
        }
        finally
        {
            ((delegate* unmanaged<int>)uninitializeAddress)();
            runtime.Dispose();
        }
    }

    private static void LibraryLoading()
    {
        var (runtime, process, _, _, uninitializeAddress) = CreateComEnvironment();
        try
        {
            var helper = Path.Combine(process.FileSystem.Root, "helper.dll");
            File.Copy(TestHost.Sample("helper.dll"), helper, overwrite: true);
            var path = process.WriteGuestString("C:\\helper.dll");
            var load = (delegate* unmanaged<nint, int, ulong>)(nint)runtime.GetManagedApiAddress("ole32", "CoLoadLibrary");
            var free = (delegate* unmanaged<ulong, int>)(nint)runtime.GetManagedApiAddress("ole32", "CoFreeLibrary");
            var freeUnused = (delegate* unmanaged<int>)(nint)runtime.GetManagedApiAddress("ole32", "CoFreeUnusedLibraries");

            var handle = load((nint)path, 0);
            Assert.NotEqual(0UL, handle, "CoLoadLibrary handle");
            Assert.True(process.LoadedModules.Any(m =>
                string.Equals(m.Image.FileName, "helper.dll", StringComparison.OrdinalIgnoreCase)),
                "server mapped by the managed loader");
            Assert.Equal(1, free(handle), "CoFreeLibrary");
            Assert.Equal(0, freeUnused(), "CoFreeUnusedLibraries");
        }
        finally
        {
            ((delegate* unmanaged<int>)uninitializeAddress)();
            runtime.Dispose();
        }
    }
}
