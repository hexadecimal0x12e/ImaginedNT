using ImaginedNT.Core.Memory;
using ImaginedNT.Core.Nt;
using ImaginedNT.Core.Objects;
using ImaginedNT.Core.Runtime;

namespace ImaginedNT.Tests;

internal static class ProcessCreationTests
{
    public static void Register(List<(string, Action)> tests)
    {
        tests.Add(("Runtime hosts multiple guest processes on managed threads", MultipleProcesses));
        tests.Add(("CreateProcessW builds PROCESS_INFORMATION and starts the child", CreateProcessWApi));
        tests.Add(("CreateProcessA accepts ANSI strings", CreateProcessAApi));
        tests.Add(("CreateProcessW honours AllowProcessCreation=false", CreateProcessDenied));
        tests.Add(("TerminateProcess stops a running child", TerminateChild));
        tests.Add(("Named objects are shared by processes in one runtime", SharedNamedObjects));
    }

    private static RuntimeOptions Options()
    {
        var options = RuntimeOptions.ForTests();
        options.InterceptSyscalls = false;
        return options;
    }

    private static void MultipleProcesses()
    {
        TestHost.RequireSamples();
        using var runtime = BuiltinApis.CreateRuntime(Options());
        var parent = runtime.CreateProcess(TestHost.Sample("hello.exe"));
        parent.LoadImage(TestHost.Sample("hello.exe"));

        var children = new List<ImaginedProcess>();
        try
        {
            for (var i = 0; i < 2; i++)
            {
                var child = runtime.CreateProcess(new ProcessCreateOptions
                {
                    ImagePath = TestHost.Sample("child.exe"),
                    ParentId = parent.Id,
                    SandboxRoot = runtime.Options.SandboxRoot,
                    IsRoot = false
                });
                child.LoadImage(TestHost.Sample("child.exe"));
                var main = child.EnsureMainThread(suspended: true);
                Assert.True(main.IsManagedHosted, "child main thread is a C# managed thread");
                Assert.True(main.Start(), "child main thread starts");
                children.Add(child);
            }

            Assert.Equal(2, children.Count, "two children");
            Assert.NotEqual(children[0].Id, children[1].Id, "child PIDs are unique");
            Assert.NotEqual(children[0].Image!.BaseAddress, children[1].Image!.BaseAddress, "same image maps at distinct bases");
            Assert.Equal<int?>(parent.Id, children[0].ParentId, "parent id is recorded");

            foreach (var child in children)
            {
                var processObject = new ProcessObject(child);
                var handle = parent.Handles.Create(processObject, 0x001F_FFFF);
                processObject.Release();
                Assert.True(parent.Handles.TryGet(handle, out var handleObject), "process handle is valid");
                Assert.Equal(NtStatus.Wait0, ((ProcessObject)handleObject!).Wait(10_000), "child process handle signals");
                Assert.True(child.WaitForExit(10_000), "child process exits");
                Assert.Equal(123, child.ExitCode ?? -1, "child exit code");
                parent.Handles.Close(handle);
            }
        }
        finally
        {
            foreach (var child in children) child.Dispose();
        }
    }

    private static ApiCallContext CreateProcessContext(ImaginedRuntime runtime, ImaginedProcess parent, string imagePath,
        out ulong processInformation, bool inheritHandles = false)
    {
        var thread = parent.GetOrRegisterHostThread(0xDEAD_BEEFu);
        var application = parent.WriteGuestString(imagePath);
        var commandLine = parent.WriteGuestString("child.exe");
        var startupInfo = AllocateZero(parent, 104);
        parent.Memory.Write(startupInfo, 104u); // STARTUPINFO.cb
        processInformation = AllocateZero(parent, 24);

        var args = new ulong[16];
        args[0] = application;
        args[1] = commandLine;
        args[4] = inheritHandles ? 1UL : 0UL;
        args[8] = startupInfo;
        args[9] = processInformation;
        return new ApiCallContext(parent, thread, "kernel32", "CreateProcessW", 0, 0, args);
    }

    private static void CreateProcessWApi()
    {
        TestHost.RequireSamples();
        using var runtime = BuiltinApis.CreateRuntime(Options());
        var parent = runtime.CreateProcess(TestHost.Sample("hello.exe"));
        parent.LoadImage(TestHost.Sample("hello.exe"));
        var imagePath = Path.Combine(Path.GetDirectoryName(TestHost.Sample("hello.exe"))!, "child.exe");
        var inheritedEvent = new EventObject(manualReset: true, initialState: false);
        var inheritedHandle = parent.Handles.Create(inheritedEvent, 0x1, inheritable: true);
        inheritedEvent.Release();
        var context = CreateProcessContext(runtime, parent, imagePath, out var processInformation, inheritHandles: true);

        Assert.True(runtime.Apis.TryGet("kernel32.dll", "CreateProcessW", out var registration), "CreateProcessW is registered");
        var result = registration!.Handler(context);
        Assert.Equal(1UL, result, "CreateProcessW succeeds");

        var processHandle = parent.Memory.Read<ulong>(processInformation);
        var threadHandle = parent.Memory.Read<ulong>(processInformation + 8);
        var pid = parent.Memory.Read<uint>(processInformation + 16);
        var tid = parent.Memory.Read<uint>(processInformation + 20);
        Assert.NotEqual(0UL, processHandle, "process handle written");
        Assert.NotEqual(0UL, threadHandle, "thread handle written");
        Assert.NotEqual(0u, pid, "process id written");
        Assert.NotEqual(0u, tid, "thread id written");

        Assert.True(parent.Handles.TryGet(processHandle, out var processObject), "process handle is valid");
        Assert.True(processObject is ProcessObject, "process handle references a ProcessObject");
        var child = ((ProcessObject)processObject!).Process;
        Assert.Equal((int)pid, child.Id, "PROCESS_INFORMATION pid");
        Assert.Equal<int?>(parent.Id, child.ParentId, "child parent id");
        Assert.NotNull(child.MainThread, "child main thread exists");
        Assert.Equal((int)tid, child.MainThread!.Id, "PROCESS_INFORMATION tid");
        Assert.True(child.MainThread.IsManagedHosted, "child runs on a managed thread");
        Assert.True(child.Handles.TryGet(inheritedHandle, out var inherited), "inheritable handle was copied");
        Assert.True(ReferenceEquals(inherited, inheritedEvent), "inherited handle references the same object");

        Assert.True(child.WaitForExit(10_000), "child exits");
        Assert.Equal(123, child.ExitCode, "child exit code visible through the process object");
        Assert.True(parent.Handles.TryGet(threadHandle, out var threadObject), "thread handle is valid");
        Assert.True(threadObject is ThreadObject, "thread handle references a ThreadObject");
        Assert.Equal((uint)123, ((ThreadObject)threadObject!).Thread.GetExitCode(), "thread exit code");

        parent.Handles.Close(threadHandle);
        parent.Handles.Close(processHandle);
        parent.Handles.Close(inheritedHandle);
    }

    private static void CreateProcessAApi()
    {
        TestHost.RequireSamples();
        using var runtime = BuiltinApis.CreateRuntime(Options());
        var parent = runtime.CreateProcess(TestHost.Sample("hello.exe"));
        parent.LoadImage(TestHost.Sample("hello.exe"));
        var thread = parent.GetOrRegisterHostThread(0xDEAD_BEEFu);
        var imagePath = Path.Combine(Path.GetDirectoryName(TestHost.Sample("hello.exe"))!, "child.exe");

        var startupInfo = AllocateZero(parent, 104);
        parent.Memory.Write(startupInfo, 104u);
        var processInformation = AllocateZero(parent, 24);
        var args = new ulong[16];
        args[0] = parent.WriteGuestString(imagePath, unicode: false);
        args[1] = parent.WriteGuestString("child.exe", unicode: false);
        args[8] = startupInfo;
        args[9] = processInformation;
        var context = new ApiCallContext(parent, thread, "kernel32", "CreateProcessA", 0, 0, args);

        Assert.True(runtime.Apis.TryGet("kernel32.dll", "CreateProcessA", out var registration), "CreateProcessA is registered");
        Assert.Equal(1UL, registration!.Handler(context), "CreateProcessA succeeds");

        var processHandle = parent.Memory.Read<ulong>(processInformation);
        Assert.True(parent.Handles.TryGet(processHandle, out var processObject), "ANSI-created process handle is valid");
        var child = ((ProcessObject)processObject!).Process;
        Assert.True(child.WaitForExit(10_000), "ANSI-created child exits");
        Assert.Equal(123, child.ExitCode ?? -1, "ANSI-created child exit code");
        parent.Handles.Close(processHandle);
        parent.Handles.Close(parent.Memory.Read<ulong>(processInformation + 8));
    }

    private static void CreateProcessDenied()
    {
        TestHost.RequireSamples();
        var options = Options();
        options.AllowProcessCreation = false;
        using var runtime = BuiltinApis.CreateRuntime(options);
        var parent = runtime.CreateProcess(TestHost.Sample("hello.exe"));
        parent.LoadImage(TestHost.Sample("hello.exe"));
        var imagePath = Path.Combine(Path.GetDirectoryName(TestHost.Sample("hello.exe"))!, "child.exe");
        var context = CreateProcessContext(runtime, parent, imagePath, out _);

        Assert.True(runtime.Apis.TryGet("kernel32.dll", "CreateProcessW", out var registration), "CreateProcessW is registered");
        Assert.Equal(0UL, registration!.Handler(context), "policy rejects creation");
        Assert.Equal(Win32Error.AccessDenied, context.LastError, "ERROR_ACCESS_DENIED");
    }

    private static void TerminateChild()
    {
        TestHost.RequireSamples();
        using var runtime = BuiltinApis.CreateRuntime(Options());
        var parent = runtime.CreateProcess(TestHost.Sample("hello.exe"));
        parent.LoadImage(TestHost.Sample("hello.exe"));

        var child = runtime.CreateProcess(new ProcessCreateOptions
        {
            ImagePath = TestHost.Sample("spin.exe"),
            ParentId = parent.Id,
            SandboxRoot = runtime.Options.SandboxRoot,
            IsRoot = false
        });
        child.LoadImage(TestHost.Sample("spin.exe"));
        var main = child.EnsureMainThread(suspended: true);
        Assert.True(main.Start(), "spin child starts");
        Thread.Sleep(50);
        Assert.False(child.WaitForExit(0), "spin child is still running");
        var processObject = new ProcessObject(child);
        try
        {
            Assert.Equal(NtStatus.WaitTimeout, processObject.Wait(0), "process handle not yet signalled");
        }
        finally
        {
            processObject.Release();
        }

        child.Terminate(9);
        Assert.True(child.WaitForExit(5_000), "terminated child signals its process object");
        Assert.Equal(9, child.ExitCode ?? -1, "terminated child exit code");
        Assert.Equal(ImaginedNT.Core.Runtime.ThreadState.Terminated, main.State, "main thread was terminated");
        child.Dispose();
    }

    private static void SharedNamedObjects()
    {
        TestHost.RequireSamples();
        using var runtime = BuiltinApis.CreateRuntime(Options());
        var parent = runtime.CreateProcess(TestHost.Sample("hello.exe"));
        parent.LoadImage(TestHost.Sample("hello.exe"));
        var child = runtime.CreateProcess(new ProcessCreateOptions
        {
            ImagePath = TestHost.Sample("child.exe"),
            ParentId = parent.Id,
            SandboxRoot = runtime.Options.SandboxRoot,
            IsRoot = false
        });
        child.LoadImage(TestHost.Sample("child.exe"));

        var named = new EventObject(manualReset: true, initialState: false);
        parent.ObjectManager.CreateObject("\\BaseNamedObjects\\imagined-cross-process", named);
        named.Release();

        var opened = child.ObjectManager.Open<EventObject>("\\BaseNamedObjects\\imagined-cross-process");
        Assert.NotNull(opened, "child resolves the parent's named event");
        Assert.True(ReferenceEquals(named, opened), "both processes refer to the same object");

        var parentHandle = parent.Handles.Create(opened!, 0x001F_0001);
        var childHandle = child.Handles.Create(opened!, 0x001F_0001);
        Assert.NotEqual(0UL, parentHandle, "parent handle");
        Assert.NotEqual(0UL, childHandle, "child handle");
        Assert.Equal(NtStatus.WaitTimeout, opened!.Wait(0), "event starts unsignalled");
        named.Set();
        Assert.Equal(NtStatus.Wait0, opened.Wait(0), "child sees the parent's signal");
        parent.Handles.Close(parentHandle);
        child.Handles.Close(childHandle);
        child.Dispose();
    }

    private static unsafe ulong AllocateZero(ImaginedProcess process, int size)
    {
        var status = process.Memory.TryAllocate(0, (ulong)size, MemoryProtection.ReadWrite,
            AllocationType.Commit | AllocationType.Reserve, out var address, out _, "test process creation");
        Assert.Equal(NtStatus.Success, status, "test allocation");
        process.Memory.WriteBytes(address, new byte[size]);
        return address;
    }
}
