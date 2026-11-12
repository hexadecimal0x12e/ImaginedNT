using ImaginedNT.Core.Pe;
using ImaginedNT.Core.Runtime;

[assembly: PartiallyImaginedNativeApi("test-native-a.dll", "Add", ApiCategory.System, "test-native-b!Add")]
[assembly: PartiallyImaginedNativeApi("api-ms-win-crt-math-l1-1-0.dll", "sin", ApiCategory.System)]

namespace ImaginedNT.Tests;

internal static class NativeApiDeclarationTests
{
    public static void Register(List<(string, Action)> tests)
    {
        tests.Add(("Native API declarations discover names and aliases", Discovery));
        tests.Add(("Built-in CRT native declarations load", BuiltinCrtDeclarations));
        tests.Add(("Disabled native fallback blocks undeclared native imports", DeclaredOnly));
    }

    private static void Discovery()
    {
        var registry = new NativeApiRegistry();
        var count = NativeApiScanner.Register(typeof(NativeApiDeclarationTests).Assembly, registry);
        Assert.True(count >= 2, "declarations discovered");
        Assert.True(registry.IsDeclared("test-native-a.dll", "Add"), "primary declaration");
        Assert.True(registry.IsDeclared("test-native-b", "Add"), "alias declaration");
        Assert.True(registry.IsDeclared("api-ms-win-crt-math-l1-1-0", "sin"), "CRT declaration");
        Assert.False(registry.IsDeclared("api-ms-win-crt-stdio-l1-1-0", "fopen"), "undeclared name");
    }

    private static void BuiltinCrtDeclarations()
    {
        using var runtime = BuiltinApis.CreateRuntime(RuntimeOptions.ForTests());
        Assert.Equal(356, runtime.NativeDeclarations.Count, "built-in CRT declaration count");
        Assert.True(runtime.NativeDeclarations.IsDeclared("api-ms-win-crt-math-l1-1-0.dll", "sin"),
            "API-set declaration");
        Assert.True(runtime.NativeDeclarations.IsDeclared("ucrtbase", "sin"), "backing-DLL alias");
        Assert.True(runtime.NativeDeclarations.IsDeclared("api-ms-win-crt-private-l1-1-0.dll", "__CxxFrameHandler3"),
            "C++ exception handler declaration");
        Assert.False(runtime.NativeDeclarations.IsDeclared("api-ms-win-crt-stdio-l1-1-0.dll", "fopen"),
            "unsafe export not declared");
    }

    private static void DeclaredOnly()
    {
        foreach (var policy in new[] { NativeApiPolicy.AllowAll, NativeApiPolicy.DeclaredOnly })
        {
            var options = RuntimeOptions.ForTests();
            options.InterceptSyscalls = false;
            options.AllowUnimplementedApiForwarding = false;
            options.AllowNativeForwarding = true;
            options.FailOnUnresolvedImport = true;
            options.NativeApiPolicy = policy;
            using var runtime = new ImaginedRuntime(options, new[] { typeof(NativeApiDeclarationTests).Assembly });

            Assert.True(runtime.IsNativeForwardingAllowed("api-ms-win-crt-math-l1-1-0.dll", "sin"),
                $"{policy}: declared export allowed");
            Assert.False(runtime.IsNativeForwardingAllowed("api-ms-win-crt-stdio-l1-1-0.dll", "fopen"),
                $"{policy}: undeclared export denied");
            Assert.False(runtime.IsNativeForwardingAllowed("api-ms-win-crt-math-l1-1-0.dll", null, 1),
                $"{policy}: ordinal import denied");

            var declared = runtime.ResolveImport(new PeImport
            {
                ModuleName = "api-ms-win-crt-math-l1-1-0.dll",
                Name = "sin",
                IatRva = 0,
                Index = 0
            });
            Assert.Equal(ImportResolutionKind.Native, declared.Kind, $"{policy}: declared native resolution");
            Assert.True(declared.Address != 0, $"{policy}: declared native address");

            Assert.Throws<BadImageFormatException>(() => runtime.ResolveImport(new PeImport
            {
                ModuleName = "api-ms-win-crt-stdio-l1-1-0.dll",
                Name = "fopen",
                IatRva = 0,
                Index = 0
            }), $"{policy}: undeclared native import must fail");
        }
    }
}
