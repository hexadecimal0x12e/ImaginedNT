using System.Diagnostics;

namespace ImaginedNT.Tests;

internal static class EndToEndTests
{
    public static void Register(List<(string, Action)> tests)
    {
        tests.Add(("E2E hello.exe through the managed IAT", Hello));
        tests.Add(("E2E direct syscall interception", Syscall));
        tests.Add(("E2E guest thread creation and waiting", Threads));
        tests.Add(("E2E sandboxed file I/O", FileIo));
        tests.Add(("E2E virtual memory wrappers", MemoryApi));
        tests.Add(("E2E native forwarding for an unimplemented API", NativeForwarding));
        tests.Add(("E2E forced-base relocation", Relocation));
        tests.Add(("E2E strict policy rejects native imports", StrictRejectsNative));
        tests.Add(("E2E inspect mode", Inspect));
        tests.Add(("E2E parallel guest processes are isolated", ParallelProcesses));
        tests.Add(("E2E guest DLL loading, DllMain and exports", GuestDll));
        tests.Add(("E2E direct real ntdll syscall maps to managed NtClose", DirectRealSyscall));
        tests.Add(("E2E CPU spinner is stopped by the execution timeout", CpuSpinner));
        tests.Add(("E2E CreateProcessW spawns a child process", SpawnProcess));
        tests.Add(("E2E managed COM object through CoCreateInstance", ManagedCom));
    }

    private static (HostResult Result, string Trace) RunSample(string sample, string sandbox, string trace,
        params string[] extra) => RunImage(TestHost.Sample(sample), sandbox, trace, extra);

    private static (HostResult Result, string Trace) RunImage(string imagePath, string sandbox, string trace,
        params string[] extra)
    {
        var arguments = new List<string>
        {
            "run", imagePath,
            "--sandbox", sandbox,
            "--trace", trace,
            "--timeout", "20"
        };
        arguments.AddRange(extra);
        var result = TestHost.RunHost(arguments.ToArray());
        var traceText = File.Exists(trace) ? File.ReadAllText(trace) : string.Empty;
        return (result, traceText);
    }

    private static void Hello()
    {
        TestHost.RequireSamples();
        var sandbox = TestHost.NewTempDirectory("hello");
        var trace = Path.Combine(sandbox, "trace.jsonl");
        try
        {
            var (result, traceText) = RunSample("hello.exe", sandbox, trace, "--strict");
            Assert.Equal(42, result.ExitCode, "hello exit code");
            Assert.True(result.StdOut.Contains("Hello from ImaginedNT", StringComparison.Ordinal), "managed WriteFile output");
            Assert.True(traceText.Contains("kernel32!WriteFile", StringComparison.Ordinal), "trace contains WriteFile");
            Assert.True(traceText.Contains("ExitProcess(42)", StringComparison.Ordinal), "trace contains ExitProcess");
            Assert.True(traceText.Contains("managed kernel32!WriteFile", StringComparison.Ordinal), "import resolved managed");
        }
        finally
        {
            try { Directory.Delete(sandbox, true); } catch { }
        }
    }

    private static void Syscall()
    {
        TestHost.RequireSamples();
        var sandbox = TestHost.NewTempDirectory("syscall");
        var trace = Path.Combine(sandbox, "trace.jsonl");
        try
        {
            var (result, traceText) = RunSample("syscall.exe", sandbox, trace, "--strict", "--syscalls");
            Assert.Equal(42, result.ExitCode, "syscall demo exit code");
            Assert.True(traceText.Contains("trapped syscall 0x1000", StringComparison.Ordinal), "trace contains trapped syscall");
            Assert.True(traceText.Contains("NtImaginedTest", StringComparison.Ordinal), "trace contains virtual syscall name");
        }
        finally
        {
            try { Directory.Delete(sandbox, true); } catch { }
        }
    }

    private static void Threads()
    {
        TestHost.RequireSamples();
        var sandbox = TestHost.NewTempDirectory("threads");
        var trace = Path.Combine(sandbox, "trace.jsonl");
        try
        {
            var (result, traceText) = RunSample("threads.exe", sandbox, trace, "--strict");
            Assert.Equal(55, result.ExitCode, "threads demo exit code");
            Assert.True(traceText.Contains("kernel32!CreateThread", StringComparison.Ordinal), "trace contains CreateThread");
            Assert.True(traceText.Contains("kernel32!WaitForSingleObject", StringComparison.Ordinal), "trace contains WaitForSingleObject");
            Assert.True(traceText.Contains("GetExitCodeThread", StringComparison.Ordinal), "trace contains GetExitCodeThread");
        }
        finally
        {
            try { Directory.Delete(sandbox, true); } catch { }
        }
    }

    private static void FileIo()
    {
        TestHost.RequireSamples();
        var sandbox = TestHost.NewTempDirectory("fileio");
        var trace = Path.Combine(sandbox, "trace.jsonl");
        try
        {
            var (result, traceText) = RunSample("fileio.exe", sandbox, trace, "--strict");
            Assert.Equal(66, result.ExitCode, "fileio demo exit code");
            var file = Path.Combine(sandbox, "sandbox-test.txt");
            Assert.True(File.Exists(file), "sandboxed file exists");
            Assert.Equal("imagined-file", File.ReadAllText(file), "sandboxed file contents");
            Assert.True(traceText.Contains("CreateFileW", StringComparison.Ordinal), "trace contains CreateFileW");
            Assert.True(traceText.Contains("WriteFile", StringComparison.Ordinal), "trace contains WriteFile");
        }
        finally
        {
            try { Directory.Delete(sandbox, true); } catch { }
        }
    }

    private static void MemoryApi()
    {
        TestHost.RequireSamples();
        var sandbox = TestHost.NewTempDirectory("memory");
        var trace = Path.Combine(sandbox, "trace.jsonl");
        try
        {
            var (result, traceText) = RunSample("memory.exe", sandbox, trace, "--strict");
            Assert.Equal(77, result.ExitCode, "memory demo exit code");
            Assert.True(traceText.Contains("VirtualAlloc", StringComparison.Ordinal), "trace contains VirtualAlloc");
            Assert.True(traceText.Contains("VirtualProtect", StringComparison.Ordinal), "trace contains VirtualProtect");
            Assert.True(traceText.Contains("VirtualQuery", StringComparison.Ordinal), "trace contains VirtualQuery");
        }
        finally
        {
            try { Directory.Delete(sandbox, true); } catch { }
        }
    }

    private static void NativeForwarding()
    {
        TestHost.RequireSamples();
        var sandbox = TestHost.NewTempDirectory("native");
        var trace = Path.Combine(sandbox, "trace.jsonl");
        try
        {
            var (result, traceText) = RunSample("native.exe", sandbox, trace);
            Assert.Equal(89, result.ExitCode, "native/last-error demo exit code");
            Assert.True(traceText.Contains("native KERNEL32.dll!GetACP", StringComparison.Ordinal), "trace shows native forwarding");
            Assert.True(traceText.Contains("native KERNEL32.dll!OpenProcess", StringComparison.Ordinal), "trace shows a second native forward");
            Assert.True(traceText.Contains("CreateFileW", StringComparison.Ordinal), "trace shows the managed file failure");
        }
        finally
        {
            try { Directory.Delete(sandbox, true); } catch { }
        }
    }

    private static void Relocation()
    {
        TestHost.RequireSamples();
        var sandbox = TestHost.NewTempDirectory("reloc");
        var trace = Path.Combine(sandbox, "trace.jsonl");
        try
        {
            var (result, traceText) = RunSample("reloc.exe", sandbox, trace, "--strict", "--base", "180000000");
            Assert.Equal(99, result.ExitCode, "relocated demo exit code");
            Assert.True(traceText.Contains("applied 1 base relocations", StringComparison.Ordinal), "trace shows relocation application");
            Assert.True(traceText.Contains("relocated=True", StringComparison.Ordinal), "trace shows relocated base");
        }
        finally
        {
            try { Directory.Delete(sandbox, true); } catch { }
        }
    }

    private static void StrictRejectsNative()
    {
        TestHost.RequireSamples();
        var sandbox = TestHost.NewTempDirectory("strict");
        var trace = Path.Combine(sandbox, "trace.jsonl");
        try
        {
            var (result, _) = RunSample("native.exe", sandbox, trace, "--strict");
            Assert.NotEqual(88, result.ExitCode, "strict mode must not run native import");
            Assert.True(result.StdErr.Contains("unresolved import", StringComparison.Ordinal), "strict failure is reported");
        }
        finally
        {
            try { Directory.Delete(sandbox, true); } catch { }
        }
    }

    private static void Inspect()
    {
        TestHost.RequireSamples();
        var result = TestHost.RunHost("inspect", TestHost.Sample("hello.exe"));
        Assert.Equal(0, result.ExitCode, "inspect exit code");
        Assert.True(result.StdOut.Contains("Imports:", StringComparison.Ordinal), "inspect prints imports");
        Assert.True(result.StdOut.Contains("WriteFile", StringComparison.Ordinal), "inspect lists WriteFile");
    }

    private static void DirectRealSyscall()
    {
        TestHost.RequireSamples();
        var sandbox = TestHost.NewTempDirectory("directsyscall");
        var trace = Path.Combine(sandbox, "trace.jsonl");
        try
        {
            var (result, traceText) = RunSample("directsyscall.exe", sandbox, trace, "--strict", "--syscalls");
            Assert.Equal(80, result.ExitCode, "direct real syscall exit code");
            Assert.True(traceText.Contains("trapped syscall 0x", StringComparison.Ordinal), "trace contains a trapped real syscall");
            Assert.True(traceText.Contains("NtClose (host number)", StringComparison.Ordinal), "trace maps the host number to NtClose");
        }
        finally
        {
            try { Directory.Delete(sandbox, true); } catch { }
        }
    }

    private static void CpuSpinner()
    {
        TestHost.RequireSamples();
        var sandbox = TestHost.NewTempDirectory("spin");
        var trace = Path.Combine(sandbox, "trace.jsonl");
        try
        {
            var started = Stopwatch.StartNew();
            var (result, traceText) = RunSample("spin.exe", sandbox, trace, "--strict", "--timeout", "1");
            started.Stop();
            Assert.Equal(124, result.ExitCode, "timeout exit code");
            Assert.True(started.Elapsed < TimeSpan.FromSeconds(20), "watchdog stopped the spinner promptly");
            Assert.True(result.StdErr.Contains("(timeout)", StringComparison.Ordinal), "host reports a timeout");
            Assert.True(traceText.Contains("timed out", StringComparison.OrdinalIgnoreCase), "trace records the timeout");
        }
        finally
        {
            try { Directory.Delete(sandbox, true); } catch { }
        }
    }

    private static void GuestDll()
    {
        TestHost.RequireSamples();
        var sandbox = TestHost.NewTempDirectory("dll");
        var trace = Path.Combine(sandbox, "trace.jsonl");
        try
        {
            File.Copy(TestHost.Sample("helper.dll"), Path.Combine(sandbox, "helper.dll"));
            var (result, traceText) = RunSample("dllhost.exe", sandbox, trace, "--strict");
            Assert.Equal(70, result.ExitCode, "dllhost exit code");
            Assert.True(traceText.Contains("loaded helper.dll", StringComparison.OrdinalIgnoreCase), "helper.dll was mapped by the managed loader");
            Assert.True(traceText.Contains("DllMain(DLL_PROCESS_ATTACH)", StringComparison.Ordinal), "DllMain was invoked");
            Assert.True(traceText.Contains("GetProcAddress", StringComparison.Ordinal), "GetProcAddress was routed managed");
        }
        finally
        {
            try { Directory.Delete(sandbox, true); } catch { }
        }
    }

    private static void SpawnProcess()
    {
        TestHost.RequireSamples();
        var sandbox = TestHost.NewTempDirectory("spawn");
        var trace = Path.Combine(sandbox, "trace.jsonl");
        try
        {
            var image = Path.Combine(sandbox, "spawn.exe");
            File.Copy(TestHost.Sample("spawn.exe"), image);
            File.Copy(TestHost.Sample("child.exe"), Path.Combine(sandbox, "child.exe"));

            var (result, traceText) = RunImage(image, sandbox, trace, "--strict");
            Assert.Equal(101, result.ExitCode, "spawn demo exit code");
            Assert.True(result.StdOut.Contains("spawn: parent", StringComparison.Ordinal), "parent output");
            Assert.True(result.StdOut.Contains("spawn: child", StringComparison.Ordinal), "child process output");
            Assert.True(traceText.Contains("CreateProcessW created child pid", StringComparison.Ordinal), "trace shows CreateProcessW");
            Assert.True(traceText.Contains("parent=", StringComparison.Ordinal), "trace records the parent pid");
            Assert.True(traceText.Contains("managed guest thread", StringComparison.Ordinal), "child runs on a managed thread");
            Assert.True(traceText.Contains("ExitProcess(123)", StringComparison.Ordinal), "child exit path is traced");
        }
        finally
        {
            try { Directory.Delete(sandbox, true); } catch { }
        }
    }

    private static void ManagedCom()
    {
        TestHost.RequireSamples();
        var sandbox = TestHost.NewTempDirectory("com");
        var trace = Path.Combine(sandbox, "trace.jsonl");
        try
        {
            var (result, traceText) = RunSample("comtest.exe", sandbox, trace, "--strict");
            Assert.Equal(42, result.ExitCode, "COM demo exit code");
            Assert.True(traceText.Contains("COM created Imagined.ComTest", StringComparison.Ordinal),
                "trace shows a managed COM object");
            Assert.True(traceText.Contains("com!IImaginedComTest!Add", StringComparison.Ordinal),
                "trace shows managed vtable dispatch");
            Assert.True(traceText.Contains("ole32!CoCreateInstance", StringComparison.Ordinal),
                "trace shows managed CoCreateInstance");
            Assert.True(traceText.Contains("oleaut32!#6", StringComparison.Ordinal),
                "trace shows managed SysFreeString");
        }
        finally
        {
            try { Directory.Delete(sandbox, true); } catch { }
        }
    }

    private static void ParallelProcesses()
    {
        TestHost.RequireSamples();
        const int count = 3;
        var sandboxes = new string[count];
        var results = new HostResult[count];
        var tasks = new Task[count];
        for (var i = 0; i < count; i++)
        {
            var index = i;
            sandboxes[index] = TestHost.NewTempDirectory($"parallel-{index}");
            tasks[index] = Task.Run(() =>
            {
                var trace = Path.Combine(sandboxes[index], "trace.jsonl");
                results[index] = RunSample("hello.exe", sandboxes[index], trace, "--strict").Result;
            });
        }
        try
        {
            Task.WaitAll(tasks);
            for (var i = 0; i < count; i++)
            {
                Assert.Equal(42, results[i].ExitCode, $"parallel guest {i} exit code");
                Assert.True(results[i].StdOut.Contains("Hello from ImaginedNT", StringComparison.Ordinal), $"parallel guest {i} output");
            }
        }
        finally
        {
            foreach (var sandbox in sandboxes)
            {
                try { Directory.Delete(sandbox, true); } catch { }
            }
        }
    }
}
