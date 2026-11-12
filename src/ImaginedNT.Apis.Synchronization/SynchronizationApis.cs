using static ImaginedNT.Apis.ApiHelpers;
using ImaginedNT.Core.Io;
using ImaginedNT.Core.Nt;
using ImaginedNT.Core.Objects;

namespace ImaginedNT.Core.Runtime;

public static class SynchronizationApis
{
    private const uint CreateSuspended = 0x0000_0004;

    private static ulong EventWait(ApiCallContext ctx, EventObject ev, uint timeout)
    {
        ev.Wait(timeout);
        return NtStatus.Success;
    }

    private static uint WaitForObject(ApiCallContext ctx, ulong handle, uint timeout)
    {
        if (handle == CurrentProcessPseudoHandle)
        {
            var deadline = timeout == uint.MaxValue ? DateTime.MaxValue : DateTime.UtcNow + TimeSpan.FromMilliseconds(timeout);
            while (!ctx.Process.IsTerminating)
            {
                if (DateTime.UtcNow >= deadline) return NtStatus.WaitTimeout;
                Thread.Sleep(1);
            }
            return NtStatus.Wait0;
        }
        if (handle == CurrentThreadPseudoHandle)
        {
            return NtStatus.WaitTimeout;
        }
        if (!ctx.Handles.TryGet(handle, out var obj) || obj is null) return NtStatus.InvalidHandle;
        return obj switch
        {
            EventObject ev => ev.Wait(timeout),
            ImaginedHandle native => native.Wait(timeout),
            MutantObject mutant => mutant.Wait(ctx.Thread.Id, timeout),
            SemaphoreObject semaphore => semaphore.Wait(timeout),
            ThreadObject thread => thread.Wait(timeout),
            ProcessObject process => process.Wait(timeout),
            FileObject => WaitForFile(ctx, timeout),
            _ => NtStatus.WaitTimeout
        };
    }

    private static uint WaitForFile(ApiCallContext ctx, uint timeout)
    {
        if (timeout != 0) Thread.Sleep((int)Math.Min(timeout, int.MaxValue));
        return NtStatus.WaitTimeout;
    }

    private static uint TimeoutFromNt(ApiCallContext ctx, ulong timeoutPointer)
    {
        if (timeoutPointer == 0) return uint.MaxValue;
        var value = ctx.Memory.Read<long>(timeoutPointer);
        if (value == 0) return 0;
        if (value < 0)
        {
            var milliseconds = (ulong)(-value) / 10_000UL;
            return milliseconds == 0 ? 1 : (uint)Math.Min(milliseconds, uint.MaxValue - 1);
        }
        var target = DateTime.FromFileTimeUtc(value);
        var remaining = target - DateTime.UtcNow;
        return remaining <= TimeSpan.Zero ? 0 : (uint)Math.Min(remaining.TotalMilliseconds, uint.MaxValue - 1);
    }

    private static string NamedObjectPath(ApiCallContext ctx, string name) => ctx.Process.GetNamedObjectPath(name);

    private static string? ReadObjectName(ApiCallContext ctx, int index) =>
        ctx.Arg(index) == 0 ? null : ctx.Name.EndsWith('A') ? ctx.ReadAnsiString(ctx.Arg(index)) : ReadUnicodeArg(ctx, index);

    private static EventObject? FindEvent(ApiCallContext ctx, string path) => ctx.ObjectManager.Open<EventObject>(path);
    private static MutantObject? FindMutant(ApiCallContext ctx, string path) => ctx.ObjectManager.Open<MutantObject>(path);
    private static SemaphoreObject? FindSemaphore(ApiCallContext ctx, string path) => ctx.ObjectManager.Open<SemaphoreObject>(path);

    private static ulong OpenObjectHandle(ApiCallContext ctx, NtObject obj, uint access = 0x001F_0001) =>
        ctx.Handles.Create(obj, access);

    private static ulong CreateNamedOrOpen<T>(ApiCallContext ctx, string? name, Func<T> factory, out T? result, out uint status)
        where T : NtObject
    {
        status = NtStatus.Success;
        result = null;
        if (string.IsNullOrEmpty(name))
        {
            result = factory();
            var unnamedHandle = ctx.Handles.Create(result);
            result.Release();
            return unnamedHandle;
        }

        lock (ctx.ObjectManager)
        {
            var path = NamedObjectPath(ctx, name!);
            var existing = ctx.ObjectManager.Open(path);
            if (existing is not null)
            {
                if (existing is not T typed)
                {
                    status = NtStatus.ObjectTypeMismatch;
                    return 0;
                }
                result = typed;
                if (ctx.Name.StartsWith("Create", StringComparison.Ordinal)) ctx.SetLastError(Win32Error.AlreadyExists);
                return OpenObjectHandle(ctx, typed);
            }

            var created = factory();
            created.Name = name;
            ctx.ObjectManager.CreateObject(path, created);
            result = created;
            var handle = ctx.Handles.Create(created);
            created.Release();
            if (ctx.Name.StartsWith("Create", StringComparison.Ordinal)) ctx.SetLastError(Win32Error.Success);
            return handle;
        }
    }

    private static ulong OpenNamedHandle<T>(ApiCallContext ctx, ObjectAttributes attributes, out uint status)
        where T : NtObject
    {
        status = NtStatus.Success;
        var path = attributes.FullPath;
        if (string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(attributes.Name))
            path = NamedObjectPath(ctx, attributes.Name);
        if (string.IsNullOrEmpty(path))
        {
            status = NtStatus.ObjectNameInvalid;
            return 0;
        }
        var obj = ctx.ObjectManager.Open(path);
        if (obj is null)
        {
            status = NtStatus.ObjectNameNotFound;
            return 0;
        }
        if (obj is not T typed)
        {
            status = NtStatus.ObjectTypeMismatch;
            return 0;
        }
        var handle = ctx.Handles.Create(typed);
        return handle;
    }

    // --- handles / close -------------------------------------------------------------
    [VirtualApi("kernel32.dll", "CloseHandle", ApiCategory.Object)]
    private static ulong CloseHandle(ApiCallContext ctx)
    {
        var handle = ctx.Arg(0);
        if (handle is 0 or 1 or 2)
        {
            var status = ctx.Handles.Close(handle);
            return BoolResult(ctx, status == NtStatus.Success, Win32FromStatus(status));
        }
        var result = ctx.Handles.Close(handle);
        return BoolResult(ctx, result == NtStatus.Success, Win32FromStatus(result));
    }

    [VirtualApi("ntdll.dll", "NtClose", ApiCategory.Object, "ntdll!ZwClose")]
    private static ulong NtClose(ApiCallContext ctx)
    {
        var handle = ctx.Arg(0);
        if (handle == CurrentProcessPseudoHandle || handle == CurrentThreadPseudoHandle) return NtStatus.InvalidHandle;
        return ctx.Handles.Close(handle);
    }

    [VirtualApi("kernel32.dll", "DuplicateHandle", ApiCategory.Object)]
    private static ulong DuplicateHandle(ApiCallContext ctx)
    {
        // (sourceProcess, sourceHandle, targetProcess, targetHandlePtr, access, inherit, options)
        var source = ctx.Arg(1);
        var targetPtr = ctx.Arg(3);
        var status = ctx.Handles.Duplicate(source, ctx.Arg32(4), out var newHandle, ctx.ArgBool(5));
        if (status != NtStatus.Success) return BoolResult(ctx, false, Win32FromStatus(status));
        if (targetPtr != 0) ctx.Memory.Write(targetPtr, newHandle);
        return 1;
    }

    [VirtualApi("kernel32.dll", "CompareObjectHandles", ApiCategory.Object)]
    private static ulong CompareObjectHandles(ApiCallContext ctx)
    {
        var a = GetObject(ctx, ctx.Arg(0));
        var b = GetObject(ctx, ctx.Arg(1));
        return a is not null && ReferenceEquals(a, b) ? 1UL : 0UL;
    }

    // --- events ---------------------------------------------------------------------
    [VirtualApi("kernel32.dll", "CreateEventW", ApiCategory.Synchronization)]
    [VirtualApi("kernel32.dll", "CreateEventA", ApiCategory.Synchronization)]
    private static ulong CreateEventW(ApiCallContext ctx)
    {
        var name = ReadObjectName(ctx, 3);
        var manual = ctx.Arg(1) != 0;
        var initial = ctx.Arg(2) != 0;
        var handle = CreateNamedOrOpen(ctx, name, () => new EventObject(manual, initial), out _, out var status);
        if (status != NtStatus.Success)
        {
            ctx.SetLastError(Win32FromStatus(status));
            return 0;
        }
        return handle;
    }

    [VirtualApi("kernel32.dll", "CreateEventExW", ApiCategory.Synchronization)]
    [VirtualApi("kernel32.dll", "CreateEventExA", ApiCategory.Synchronization)]
    private static ulong CreateEventExW(ApiCallContext ctx)
    {
        var name = ReadObjectName(ctx, 1);
        var flags = ctx.Arg32(2);
        var manual = (flags & 1) != 0;
        var initial = (flags & 2) != 0;
        var handle = CreateNamedOrOpen(ctx, name, () => new EventObject(manual, initial), out _, out var status);
        if (status != NtStatus.Success)
        {
            ctx.SetLastError(Win32FromStatus(status));
            return 0;
        }
        return handle;
    }

    [VirtualApi("kernel32.dll", "OpenEventW", ApiCategory.Synchronization)]
    [VirtualApi("kernel32.dll", "OpenEventA", ApiCategory.Synchronization)]
    private static ulong OpenEventW(ApiCallContext ctx)
    {
        var name = ReadObjectName(ctx, 2);
        if (string.IsNullOrEmpty(name))
        {
            ctx.SetLastError(Win32Error.InvalidParameter);
            return 0;
        }
        var ev = FindEvent(ctx, NamedObjectPath(ctx, name));
        if (ev is null)
        {
            ctx.SetLastError(Win32Error.FileNotFound);
            return 0;
        }
        return OpenObjectHandle(ctx, ev);
    }

    [VirtualApi("kernel32.dll", "SetEvent", ApiCategory.Synchronization)]
    private static ulong SetEvent(ApiCallContext ctx)
    {
        if (GetObject(ctx, ctx.Arg(0)) is not EventObject ev) return BoolResult(ctx, false, Win32Error.InvalidHandle);
        ev.Set();
        return 1;
    }

    [VirtualApi("kernel32.dll", "ResetEvent", ApiCategory.Synchronization)]
    private static ulong ResetEvent(ApiCallContext ctx)
    {
        if (GetObject(ctx, ctx.Arg(0)) is not EventObject ev) return BoolResult(ctx, false, Win32Error.InvalidHandle);
        ev.Reset();
        return 1;
    }

    [VirtualApi("kernel32.dll", "PulseEvent", ApiCategory.Synchronization)]
    private static ulong PulseEvent(ApiCallContext ctx)
    {
        if (GetObject(ctx, ctx.Arg(0)) is not EventObject ev) return BoolResult(ctx, false, Win32Error.InvalidHandle);
        ev.Pulse();
        return 1;
    }

    [VirtualApi("ntdll.dll", "NtCreateEvent", ApiCategory.Synchronization, "ntdll!ZwCreateEvent")]
    private static ulong NtCreateEvent(ApiCallContext ctx)
    {
        var handlePtr = ctx.Arg(0);
        var attrs = ctx.ReadObjectAttributes(ctx.Arg(2));
        var type = ctx.Arg32(3);
        var initialState = ctx.Arg(4) != 0;
        var name = attrs.FullPath ?? attrs.Name;
        var handle = CreateNamedOrOpen(ctx, name, () => new EventObject(type == 0, initialState), out _, out var status);
        if (status == NtStatus.Success && handlePtr != 0) ctx.Memory.Write(handlePtr, handle);
        return status;
    }

    [VirtualApi("ntdll.dll", "NtOpenEvent", ApiCategory.Synchronization, "ntdll!ZwOpenEvent")]
    private static ulong NtOpenEvent(ApiCallContext ctx)
    {
        var attrs = ctx.ReadObjectAttributes(ctx.Arg(2));
        var handle = OpenNamedHandle<EventObject>(ctx, attrs, out var status);
        if (status == NtStatus.Success && ctx.Arg(0) != 0) ctx.Memory.Write(ctx.Arg(0), handle);
        return status;
    }

    [VirtualApi("ntdll.dll", "NtSetEvent", ApiCategory.Synchronization, "ntdll!ZwSetEvent")]
    private static ulong NtSetEvent(ApiCallContext ctx)
    {
        if (GetObject(ctx, ctx.Arg(0)) is not EventObject ev) return NtStatus.InvalidHandle;
        var previous = ev.Set();
        if (ctx.Arg(1) != 0) ctx.Memory.Write(ctx.Arg(1), previous ? 1 : 0);
        return NtStatus.Success;
    }

    [VirtualApi("ntdll.dll", "NtResetEvent", ApiCategory.Synchronization, "ntdll!ZwResetEvent")]
    private static ulong NtResetEvent(ApiCallContext ctx)
    {
        if (GetObject(ctx, ctx.Arg(0)) is not EventObject ev) return NtStatus.InvalidHandle;
        var previous = ev.Reset();
        if (ctx.Arg(1) != 0) ctx.Memory.Write(ctx.Arg(1), previous ? 1 : 0);
        return NtStatus.Success;
    }

    [VirtualApi("ntdll.dll", "NtPulseEvent", ApiCategory.Synchronization, "ntdll!ZwPulseEvent")]
    private static ulong NtPulseEvent(ApiCallContext ctx)
    {
        if (GetObject(ctx, ctx.Arg(0)) is not EventObject ev) return NtStatus.InvalidHandle;
        var previous = ev.Pulse();
        if (ctx.Arg(1) != 0) ctx.Memory.Write(ctx.Arg(1), previous ? 1 : 0);
        return NtStatus.Success;
    }

    // --- mutants --------------------------------------------------------------------
    [VirtualApi("kernel32.dll", "CreateMutexW", ApiCategory.Synchronization)]
    [VirtualApi("kernel32.dll", "CreateMutexA", ApiCategory.Synchronization)]
    private static ulong CreateMutexW(ApiCallContext ctx)
    {
        var name = ReadObjectName(ctx, 2);
        var initialOwner = ctx.Arg(1) != 0;
        var handle = CreateNamedOrOpen(ctx, name, () => new MutantObject(initialOwner, ctx.Thread.Id), out _, out var status);
        if (status != NtStatus.Success)
        {
            ctx.SetLastError(Win32FromStatus(status));
            return 0;
        }
        return handle;
    }

    [VirtualApi("kernel32.dll", "CreateMutexExW", ApiCategory.Synchronization)]
    [VirtualApi("kernel32.dll", "CreateMutexExA", ApiCategory.Synchronization)]
    private static ulong CreateMutexExW(ApiCallContext ctx)
    {
        // CreateMutexExW(attributes, name, flags, desiredAccess); CREATE_MUTEX_INITIAL_OWNER is 1.
        var name = ReadObjectName(ctx, 1);
        var initialOwner = (ctx.Arg32(2) & 1) != 0;
        var handle = CreateNamedOrOpen(ctx, name, () => new MutantObject(initialOwner, ctx.Thread.Id), out _, out var status);
        if (status != NtStatus.Success)
        {
            ctx.SetLastError(Win32FromStatus(status));
            return 0;
        }
        return handle;
    }

    [VirtualApi("kernel32.dll", "OpenMutexW", ApiCategory.Synchronization)]
    [VirtualApi("kernel32.dll", "OpenMutexA", ApiCategory.Synchronization)]
    private static ulong OpenMutexW(ApiCallContext ctx)
    {
        var name = ReadObjectName(ctx, 2);
        if (string.IsNullOrEmpty(name))
        {
            ctx.SetLastError(Win32Error.InvalidParameter);
            return 0;
        }
        var mutant = FindMutant(ctx, NamedObjectPath(ctx, name));
        if (mutant is null)
        {
            ctx.SetLastError(Win32Error.FileNotFound);
            return 0;
        }
        return OpenObjectHandle(ctx, mutant);
    }

    [VirtualApi("kernel32.dll", "ReleaseMutex", ApiCategory.Synchronization)]
    private static ulong ReleaseMutex(ApiCallContext ctx)
    {
        if (GetObject(ctx, ctx.Arg(0)) is not MutantObject mutant)
            return BoolResult(ctx, false, Win32Error.InvalidHandle);
        var status = mutant.Release(ctx.Thread.Id);
        return BoolResult(ctx, status == NtStatus.Success, Win32FromStatus(status));
    }

    [VirtualApi("ntdll.dll", "NtCreateMutant", ApiCategory.Synchronization, "ntdll!ZwCreateMutant")]
    private static ulong NtCreateMutant(ApiCallContext ctx)
    {
        var handlePtr = ctx.Arg(0);
        var attrs = ctx.ReadObjectAttributes(ctx.Arg(2));
        var initialOwner = ctx.Arg(3) != 0;
        var name = attrs.FullPath ?? attrs.Name;
        var handle = CreateNamedOrOpen(ctx, name, () => new MutantObject(initialOwner, ctx.Thread.Id), out _, out var status);
        if (status == NtStatus.Success && handlePtr != 0) ctx.Memory.Write(handlePtr, handle);
        return status;
    }

    [VirtualApi("ntdll.dll", "NtOpenMutant", ApiCategory.Synchronization, "ntdll!ZwOpenMutant")]
    private static ulong NtOpenMutant(ApiCallContext ctx)
    {
        var attrs = ctx.ReadObjectAttributes(ctx.Arg(2));
        var handle = OpenNamedHandle<MutantObject>(ctx, attrs, out var status);
        if (status == NtStatus.Success && ctx.Arg(0) != 0) ctx.Memory.Write(ctx.Arg(0), handle);
        return status;
    }

    [VirtualApi("ntdll.dll", "NtReleaseMutant", ApiCategory.Synchronization, "ntdll!ZwReleaseMutant")]
    private static ulong NtReleaseMutant(ApiCallContext ctx)
    {
        if (GetObject(ctx, ctx.Arg(0)) is not MutantObject mutant) return NtStatus.InvalidHandle;
        // NT releases one acquisition.  The previous count is not exposed by the managed object.
        var status = mutant.Release(ctx.Thread.Id);
        if (ctx.Arg(1) != 0) ctx.Memory.Write(ctx.Arg(1), mutant.RecursionCount);
        return status;
    }

    // --- semaphores -----------------------------------------------------------------
    [VirtualApi("kernel32.dll", "CreateSemaphoreW", ApiCategory.Synchronization)]
    [VirtualApi("kernel32.dll", "CreateSemaphoreA", ApiCategory.Synchronization)]
    [VirtualApi("kernel32.dll", "CreateSemaphoreExW", ApiCategory.Synchronization)]
    [VirtualApi("kernel32.dll", "CreateSemaphoreExA", ApiCategory.Synchronization)]
    private static ulong CreateSemaphoreW(ApiCallContext ctx)
    {
        var name = ReadObjectName(ctx, 3);
        var initial = (int)Math.Min(ctx.Arg32(1), int.MaxValue);
        var maximum = (int)Math.Min(ctx.Arg32(2), int.MaxValue);
        if (maximum <= 0) maximum = Math.Max(initial, 1);
        var handle = CreateNamedOrOpen(ctx, name, () => new SemaphoreObject(initial, maximum), out _, out var status);
        if (status != NtStatus.Success)
        {
            ctx.SetLastError(Win32FromStatus(status));
            return 0;
        }
        return handle;
    }

    [VirtualApi("kernel32.dll", "OpenSemaphoreW", ApiCategory.Synchronization)]
    [VirtualApi("kernel32.dll", "OpenSemaphoreA", ApiCategory.Synchronization)]
    private static ulong OpenSemaphoreW(ApiCallContext ctx)
    {
        var name = ReadObjectName(ctx, 2);
        if (string.IsNullOrEmpty(name))
        {
            ctx.SetLastError(Win32Error.InvalidParameter);
            return 0;
        }
        var semaphore = FindSemaphore(ctx, NamedObjectPath(ctx, name));
        if (semaphore is null)
        {
            ctx.SetLastError(Win32Error.FileNotFound);
            return 0;
        }
        return OpenObjectHandle(ctx, semaphore);
    }

    [VirtualApi("kernel32.dll", "ReleaseSemaphore", ApiCategory.Synchronization)]
    private static ulong ReleaseSemaphore(ApiCallContext ctx)
    {
        if (GetObject(ctx, ctx.Arg(0)) is not SemaphoreObject semaphore)
            return BoolResult(ctx, false, Win32Error.InvalidHandle);
        var status = semaphore.Release((int)Math.Max(1, ctx.Arg32(1)), out var previous);
        if (status == NtStatus.Success && ctx.Arg(2) != 0) ctx.Memory.Write(ctx.Arg(2), previous);
        return BoolResult(ctx, status == NtStatus.Success, Win32FromStatus(status));
    }

    [VirtualApi("ntdll.dll", "NtCreateSemaphore", ApiCategory.Synchronization, "ntdll!ZwCreateSemaphore")]
    private static ulong NtCreateSemaphore(ApiCallContext ctx)
    {
        var handlePtr = ctx.Arg(0);
        var attrs = ctx.ReadObjectAttributes(ctx.Arg(2));
        var initial = (int)Math.Min(ctx.Arg32(3), int.MaxValue);
        var maximum = (int)Math.Min(ctx.Arg32(4), int.MaxValue);
        var name = attrs.FullPath ?? attrs.Name;
        var handle = CreateNamedOrOpen(ctx, name, () => new SemaphoreObject(initial, maximum > 0 ? maximum : Math.Max(initial, 1)), out _, out var status);
        if (status == NtStatus.Success && handlePtr != 0) ctx.Memory.Write(handlePtr, handle);
        return status;
    }

    [VirtualApi("ntdll.dll", "NtOpenSemaphore", ApiCategory.Synchronization, "ntdll!ZwOpenSemaphore")]
    private static ulong NtOpenSemaphore(ApiCallContext ctx)
    {
        var attrs = ctx.ReadObjectAttributes(ctx.Arg(2));
        var handle = OpenNamedHandle<SemaphoreObject>(ctx, attrs, out var status);
        if (status == NtStatus.Success && ctx.Arg(0) != 0) ctx.Memory.Write(ctx.Arg(0), handle);
        return status;
    }

    [VirtualApi("ntdll.dll", "NtReleaseSemaphore", ApiCategory.Synchronization, "ntdll!ZwReleaseSemaphore")]
    private static ulong NtReleaseSemaphore(ApiCallContext ctx)
    {
        if (GetObject(ctx, ctx.Arg(0)) is not SemaphoreObject semaphore) return NtStatus.InvalidHandle;
        var status = semaphore.Release((int)Math.Max(1, ctx.Arg32(1)), out var previous);
        if (status == NtStatus.Success && ctx.Arg(2) != 0) ctx.Memory.Write(ctx.Arg(2), previous);
        return status;
    }

    // --- waits ----------------------------------------------------------------------
    [VirtualApi("kernel32.dll", "WaitForSingleObject", ApiCategory.Synchronization, "kernel32!WaitForSingleObjectEx")]
    private static ulong WaitForSingleObject(ApiCallContext ctx)
    {
        var status = WaitForObject(ctx, ctx.Arg(0), ctx.Arg32(1));
        return status switch
        {
            NtStatus.Wait0 => (uint)WaitResult.Object0,
            NtStatus.WaitTimeout => (uint)WaitResult.Timeout,
            _ => (uint)WaitResult.Failed
        };
    }

    [VirtualApi("kernel32.dll", "WaitForMultipleObjects", ApiCategory.Synchronization)]
    private static ulong WaitForMultipleObjects(ApiCallContext ctx)
    {
        var count = (int)Math.Min(ctx.Arg32(0), 64);
        var arrayPtr = ctx.Arg(1);
        var waitAll = ctx.Arg(2) != 0;
        var timeout = ctx.Arg32(3);
        var handles = new ulong[count];
        for (var i = 0; i < count; i++) handles[i] = ctx.Memory.Read<ulong>(arrayPtr + (ulong)(i * 8));
        if (count == 0) return 0xFFFFFFFF;
        if (waitAll)
        {
            var deadline = timeout == uint.MaxValue ? DateTime.MaxValue : DateTime.UtcNow + TimeSpan.FromMilliseconds(timeout);
            foreach (var handle in handles)
            {
                var remaining = timeout == uint.MaxValue ? uint.MaxValue : (uint)Math.Max(0, (deadline - DateTime.UtcNow).TotalMilliseconds);
                var status = WaitForObject(ctx, handle, remaining);
                if (status != NtStatus.Wait0) return status == NtStatus.WaitTimeout ? (uint)WaitResult.Timeout : (uint)WaitResult.Failed;
            }
            return 0;
        }
        var start = DateTime.UtcNow;
        for (var i = 0; i < count; i++)
        {
            var remaining = timeout == uint.MaxValue ? uint.MaxValue : (uint)Math.Max(0, timeout - (DateTime.UtcNow - start).TotalMilliseconds);
            var status = WaitForObject(ctx, handles[i], remaining);
            if (status == NtStatus.Wait0) return (ulong)i;
            if (status != NtStatus.WaitTimeout) return (uint)WaitResult.Failed;
        }
        return (uint)WaitResult.Timeout;
    }

    [VirtualApi("ntdll.dll", "NtWaitForSingleObject", ApiCategory.Synchronization, "ntdll!ZwWaitForSingleObject")]
    private static ulong NtWaitForSingleObject(ApiCallContext ctx)
    {
        var timeout = TimeoutFromNt(ctx, ctx.Arg(2));
        return WaitForObject(ctx, ctx.Arg(0), timeout);
    }

    [VirtualApi("ntdll.dll", "NtWaitForMultipleObjects", ApiCategory.Synchronization, "ntdll!ZwWaitForMultipleObjects")]
    private static ulong NtWaitForMultipleObjects(ApiCallContext ctx)
    {
        var count = (int)Math.Min(ctx.Arg32(0), 64);
        var arrayPtr = ctx.Arg(1);
        var waitType = ctx.Arg32(2);
        var timeout = TimeoutFromNt(ctx, ctx.Arg(4));
        var handles = new ulong[count];
        for (var i = 0; i < count; i++) handles[i] = ctx.Memory.Read<ulong>(arrayPtr + (ulong)(i * 8));
        if (waitType == 0)
        {
            foreach (var handle in handles)
            {
                var status = WaitForObject(ctx, handle, timeout);
                if (status != NtStatus.Wait0) return status;
            }
            return NtStatus.Wait0;
        }
        for (var i = 0; i < count; i++)
        {
            var status = WaitForObject(ctx, handles[i], timeout);
            if (status == NtStatus.Wait0) return NtStatus.Wait0 + (uint)i;
            if (status != NtStatus.WaitTimeout) return status;
        }
        return NtStatus.WaitTimeout;
    }

    [VirtualApi("kernel32.dll", "SignalObjectAndWait", ApiCategory.Synchronization)]
    private static ulong SignalObjectAndWait(ApiCallContext ctx)
    {
        if (GetObject(ctx, ctx.Arg(0)) is EventObject ev) ev.Set();
        var status = WaitForObject(ctx, ctx.Arg(1), ctx.Arg32(2));
        return status == NtStatus.Wait0 ? (uint)WaitResult.Object0 : status == NtStatus.WaitTimeout ? (uint)WaitResult.Timeout : (uint)WaitResult.Failed;
    }

    // --- threads --------------------------------------------------------------------
    [VirtualApi("kernel32.dll", "CreateThread", ApiCategory.Thread)]
    private static ulong CreateThread(ApiCallContext ctx)
    {
        var start = ctx.Arg(2);
        var parameter = ctx.Arg(3);
        var flags = ctx.Arg32(4);
        var threadIdPtr = ctx.Arg(5);
        if (start == 0) return BoolResult(ctx, false, Win32Error.InvalidParameter);
        var thread = ctx.Process.CreateThreadAt(start, parameter, false, "guest-thread", (flags & CreateSuspended) != 0);
        var handle = ctx.Handles.Create(new ThreadObject(thread), 0x001F_FFFF);
        if (threadIdPtr != 0) ctx.Memory.Write(threadIdPtr, (uint)thread.Id);
        return handle;
    }

    [VirtualApi("ntdll.dll", "NtCreateThreadEx", ApiCategory.Thread, "ntdll!ZwCreateThreadEx")]
    private static ulong NtCreateThreadEx(ApiCallContext ctx)
    {
        var handlePtr = ctx.Arg(0);
        var start = ctx.Arg(4);
        var parameter = ctx.Arg(5);
        var flags = ctx.Arg32(6);
        if (start == 0) return NtStatus.InvalidParameter;
        var thread = ctx.Process.CreateThreadAt(start, parameter, false, "guest-thread", (flags & CreateSuspended) != 0);
        var handle = ctx.Handles.Create(new ThreadObject(thread), 0x001F_FFFF);
        if (handlePtr != 0) ctx.Memory.Write(handlePtr, handle);
        return NtStatus.Success;
    }

    [VirtualApi("kernel32.dll", "ResumeThread", ApiCategory.Thread)]
    private static ulong ResumeThread(ApiCallContext ctx)
    {
        if (GetObject(ctx, ctx.Arg(0)) is not ThreadObject thread) return 0xFFFFFFFF;
        return thread.Thread.Resume() ? 1UL : 0xFFFFFFFF;
    }

    [VirtualApi("kernel32.dll", "SuspendThread", ApiCategory.Thread)]
    private static ulong SuspendThread(ApiCallContext ctx) => 1;

    [VirtualApi("kernel32.dll", "TlsAlloc", ApiCategory.Thread, "kernelbase!TlsAlloc")]
    private static ulong TlsAlloc(ApiCallContext ctx)
    {
        var slot = ctx.Process.AllocateTlsSlot();
        if (slot < 0)
        {
            ctx.SetLastError(Win32Error.NotEnoughMemory);
            return 0xFFFFFFFF;
        }
        return (ulong)slot;
    }

    [VirtualApi("kernel32.dll", "TlsFree", ApiCategory.Thread, "kernelbase!TlsFree")]
    private static ulong TlsFree(ApiCallContext ctx) =>
        BoolResult(ctx, ctx.Process.FreeTlsSlot((int)ctx.Arg32(0)), Win32Error.InvalidParameter);

    [VirtualApi("kernel32.dll", "TlsSetValue", ApiCategory.Thread, "kernelbase!TlsSetValue")]
    private static ulong TlsSetValue(ApiCallContext ctx) =>
        BoolResult(ctx, ctx.Thread.SetTlsValue((int)ctx.Arg32(0), ctx.Arg(1)), Win32Error.InvalidParameter);

    [VirtualApi("kernel32.dll", "TlsGetValue", ApiCategory.Thread, "kernelbase!TlsGetValue")]
    private static ulong TlsGetValue(ApiCallContext ctx)
    {
        var value = ctx.Thread.GetTlsValue((int)ctx.Arg32(0));
        ctx.SetLastError(0);
        return value;
    }

}
