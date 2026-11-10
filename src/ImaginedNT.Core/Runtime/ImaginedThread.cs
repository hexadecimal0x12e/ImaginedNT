using System.Runtime.InteropServices;
using ImaginedNT.Core.Diagnostics;
using ImaginedNT.Core.Native;

namespace ImaginedNT.Core.Runtime;

public enum ThreadState
{
    Created,
    Running,
    Exited,
    Terminated
}

/// <summary>
/// One guest thread.  Guest threads are hosted on real C# managed threads; the managed thread
/// body calls the guest start address as an unmanaged function pointer.  Keeping the host side
/// managed means every imagined process owns a normal CLR thread object that the runtime can
/// enumerate, wait for, and terminate without inventing a second thread registry.
/// </summary>
public sealed class ImaginedThread : IDisposable
{
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _exitedSignal = new(initialState: false);
    private readonly Dictionary<int, ulong> _tlsValues = new();
    private readonly Thread? _managedThread;
    private readonly bool _managedHosted;
    private readonly IntPtr _launcherContext;
    private readonly ulong _exitTrampolineAddress;
    private IntPtr _handle;
    private int _started;
    private int _finished;
    private int _disposed;
    private int _terminationRequested;
    private int _terminationExitCode;
    private volatile bool _exitRequested;

    internal ImaginedThread(ImaginedProcess process, int id, uint nativeId, IntPtr handle, ulong startAddress, ulong parameter,
        bool isMain, string name, bool managedHosted = false)
    {
        Process = process;
        Id = id;
        NativeId = nativeId;
        _handle = handle;
        StartAddress = startAddress;
        Parameter = parameter;
        IsMain = isMain;
        Name = name;
        _managedHosted = managedHosted;

        if (managedHosted)
        {
            _managedThread = new Thread(RunManagedGuest)
            {
                IsBackground = true,
                Name = $"imagined-p{process.Id}-t{id}"
            };
            _launcherContext = Marshal.AllocHGlobal(sizeof(ulong));
            Marshal.WriteInt64(_launcherContext, 0);
            _exitTrampolineAddress = process.Runtime.Thunks.AllocateThreadExitTrampoline((ulong)_launcherContext);
            if (_exitTrampolineAddress == 0)
            {
                Marshal.FreeHGlobal(_launcherContext);
                throw new InvalidOperationException("guest thread exit trampoline allocation failed");
            }
            State = ThreadState.Created;
            Suspended = true;
        }
        else
        {
            State = ThreadState.Running;
        }
    }

    public ImaginedProcess Process { get; }
    public int Id { get; }
    public uint NativeId { get; internal set; }
    public ulong StartAddress { get; }
    public ulong Parameter { get; }
    public bool IsMain { get; }
    public string Name { get; }
    public ThreadState State { get; internal set; }
    public int LastError { get; set; }
    public int? ExitCode { get; internal set; }
    public IntPtr HostHandle
    {
        get { lock (_gate) return _handle; }
    }

    /// <summary>True when this guest thread is executed by a System.Threading.Thread.</summary>
    public bool IsManagedHosted => _managedHosted;

    /// <summary>Unmanaged stub that unwinds the guest frame back into the managed thread body.</summary>
    public ulong ExitTrampolineAddress => _exitTrampolineAddress;

    /// <summary>True when guest code asked this thread to exit and the trampoline is armed.</summary>
    public bool HasRequestedExit => _exitRequested;

    internal bool CanUnwindGuest => _exitTrampolineAddress != 0 && _launcherContext != IntPtr.Zero &&
        Marshal.ReadInt64(_launcherContext) != 0;

    /// <summary>
    /// True after Terminate requested a stop.  A managed guest thread that cannot be stopped with
    /// TerminateThread unwinds cooperatively at its next managed API call.
    /// </summary>
    public bool TerminationRequested => Volatile.Read(ref _terminationRequested) != 0;

    /// <summary>The exit code supplied to <see cref="Terminate"/>.</summary>
    public int TerminationExitCode => Volatile.Read(ref _terminationExitCode);

    /// <summary>True once the guest thread body has really returned (or was force-stopped).</summary>
    public bool IsFinished => Volatile.Read(ref _finished) != 0;
    internal bool IsExecutionActive => HasStarted && (!IsFinished || _managedThread?.IsAlive == true);

    /// <summary>Record the exit request so the syscall trap can redirect RIP as well.</summary>
    internal void RequestExit() => _exitRequested = true;
    public bool Suspended { get; internal set; }
    public bool HasStarted => Volatile.Read(ref _started) != 0;

    /// <summary>Start the managed guest thread (ResumeThread / CREATE_SUSPENDED release).</summary>
    public bool Start()
    {
        if (!_managedHosted || _managedThread is null) return false;
        if (Volatile.Read(ref _finished) != 0) return false;
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0) return false;
        try
        {
            _managedThread.Start();
            lock (_gate)
            {
                if (State == ThreadState.Created) State = ThreadState.Running;
            }
            Suspended = false;
            return true;
        }
        catch (Exception ex)
        {
            Process.Trace.Error($"managed guest thread {Id} failed to start: {ex.GetType().Name}: {ex.Message}", Process.Id, Id);
            MarkExited(-1, ThreadState.Terminated);
            return false;
        }
    }

    public bool Resume()
    {
        if (State is ThreadState.Exited or ThreadState.Terminated) return false;
        if (!_managedHosted) return false;
        if (!HasStarted) return Start();
        Suspended = false;
        return true;
    }

    public bool Wait(TimeSpan timeout, out uint exitCode)
    {
        exitCode = GetExitCode();
        if (timeout != Timeout.InfiniteTimeSpan && timeout < TimeSpan.Zero) timeout = TimeSpan.Zero;

        if (_managedHosted && _managedThread is not null)
        {
            if (ReferenceEquals(Thread.CurrentThread, _managedThread)) return false;
            if (!HasStarted)
            {
                // A suspended thread can only complete by being terminated before it starts.
                if (Volatile.Read(ref _finished) == 0)
                {
                    try
                    {
                        if (!_exitedSignal.Wait(timeout)) return false;
                    }
                    catch (ObjectDisposedException)
                    {
                        return State is ThreadState.Exited or ThreadState.Terminated;
                    }
                }
                exitCode = GetExitCode();
                return true;
            }

            // Join the actual managed thread.  MarkExited may have been signalled early by
            // ExitProcess in ReturnToHost mode; joining guarantees the guest code is really gone
            // before the caller releases the process address space.
            try
            {
                if (!_managedThread.Join(timeout)) return false;
            }
            catch (ThreadStateException)
            {
                // Start() has been requested but the Thread object is not started yet; wait for
                // the exit signal instead of falsely reporting completion.
                try
                {
                    if (!_exitedSignal.Wait(timeout)) return false;
                }
                catch (ObjectDisposedException)
                {
                    return State is ThreadState.Exited or ThreadState.Terminated;
                }
            }
            catch (ObjectDisposedException)
            {
                // Treat a disposed host thread as finished.
            }
            exitCode = GetExitCode();
            return true;
        }

        if (Volatile.Read(ref _finished) == 0) return false;
        exitCode = GetExitCode();
        return true;
    }

    public uint GetExitCode()
    {
        lock (_gate)
        {
            return ExitCode is int code ? unchecked((uint)code) : NativeMethods.STILL_ACTIVE;
        }
    }

    /// <summary>Record the exit code/state and wake every waiter.  Safe to call more than once.</summary>
    internal void MarkExited(int exitCode, ThreadState state)
    {
        if (Interlocked.Exchange(ref _finished, 1) != 0) return;
        lock (_gate)
        {
            ExitCode ??= exitCode;
            State = state;
        }
        _exitedSignal.Set();
        Process.DetachThread(this);
    }

    internal int? ReadExitCode()
    {
        lock (_gate) return ExitCode;
    }

    /// <summary>
    /// Stop managed guest threads through their exit trampoline at the next API boundary.
    /// TerminateThread would also destroy CLR state owned by the hosting thread.
    /// </summary>
    public void Terminate(int exitCode = -1)
    {
        if (State is ThreadState.Exited or ThreadState.Terminated) return;

        RequestTermination(exitCode);
    }

    internal void RequestTermination(int exitCode)
    {
        Interlocked.Exchange(ref _terminationExitCode, exitCode);
        Volatile.Write(ref _terminationRequested, 1);
        if (!HasStarted) MarkExited(exitCode, ThreadState.Terminated);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (_handle != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(_handle);
                _handle = IntPtr.Zero;
            }
        }
        // Only free the launcher context once the guest thread is really gone; a still-running
        // thread could be about to use it from the exit trampoline.
        if (_launcherContext != IntPtr.Zero && (Volatile.Read(ref _finished) != 0 || !HasStarted))
        {
            Marshal.FreeHGlobal(_launcherContext);
        }
        // The exit signal is intentionally not disposed: a ProcessObject waiter may still be
        // observing it while the owning process is being torn down.  ManualResetEventSlim does
        // not allocate an OS handle unless WaitHandle is touched.
    }

    internal unsafe ulong InvokeGuest(ulong address, ReadOnlySpan<ulong> arguments, bool exiting = false)
    {
        if (_launcherContext == IntPtr.Zero)
            throw new InvalidOperationException("guest execution requires a managed guest thread");
        if (Process.FaultExitCode is int fault) throw new GuestProcessFaultException(fault);
        if (TerminationRequested) throw new GuestThreadExitException(TerminationExitCode);
        ulong* args = stackalloc ulong[16];
        new Span<ulong>(args, 16).Clear();
        arguments.CopyTo(new Span<ulong>(args, 16));
        var previous = *(ulong*)_launcherContext;
        int? lastError = null;
        try
        {
            var launcher = (delegate* unmanaged<ulong, ulong*, ulong*, ulong>)(nint)Process.Runtime.Thunks.LauncherAddress;
            var result = launcher(address, args, (ulong*)_launcherContext);
            lastError = NativeMethods.GetLastError();
            if (Process.FaultExitCode is int code) throw new GuestProcessFaultException(code);
            if (HasRequestedExit && !exiting) throw new GuestThreadExitException(ReadExitCode() ?? Process.ExitCode ??
                (TerminationRequested ? TerminationExitCode : unchecked((int)result)));
            return result;
        }
        finally
        {
            *(ulong*)_launcherContext = previous;
            if (lastError is int error) NativeMethods.SetLastError(error);
        }
    }

    private void RunManagedGuest()
    {
        var nativeId = NativeMethods.GetCurrentThreadId();
        Process.AttachThread(this, nativeId);
        var handle = NativeMethods.OpenThread(NativeMethods.THREAD_ALL_ACCESS, false, nativeId);
        if (handle != IntPtr.Zero)
        {
            lock (_gate) _handle = handle;
        }

        Process.Trace.Emit(TraceKind.Thread,
            $"managed guest thread {Id} started native={nativeId} start=0x{StartAddress:X16} param=0x{Parameter:X16} main={IsMain}",
            Process.Id, Id);

        try
        {
            if (IsMain)
            {
                // The Windows loader runs the main image's TLS callbacks before DLL DllMains.
                // ReactOS cmd.exe initializes its CRT security cookie there, so skipping this
                // turns later __security_check_cookie calls into fail-fast 0xC0000409.
                if (Process.Image is { } image) Process.RunTlsCallbacks(image, 1 /* DLL_PROCESS_ATTACH */);
                Process.InitializeLibraries();
            }
            var code = InvokeGuest(StartAddress, [Parameter]);
            CompleteFromGuest(IsMain ? 0 : unchecked((int)code));
        }
        catch (GuestProcessFaultException ex)
        {
            Complete(ex.ExitCode, ThreadState.Terminated);
        }
        catch (GuestThreadExitException ex)
        {
            CompleteFromGuest(ex.ExitCode);
        }
        catch (Exception ex)
        {
            Process.Trace.Error($"guest thread {Id} terminated with {ex.GetType().Name}: {ex.Message}", Process.Id, Id);
            Complete(TerminationRequested ? TerminationExitCode : -1, ThreadState.Terminated);
        }
        finally
        {
            Process.DetachThread(this);
        }
    }

    private void CompleteFromGuest(int exitCode)
    {
        if (!TerminationRequested)
        {
            try { Process.Fls.ReleaseThread(this); }
            catch (Exception ex)
            {
                Process.Trace.Error($"FLS cleanup failed: {ex.GetType().Name}: {ex.Message}", Process.Id, Id);
                if (ex is GuestProcessFaultException fault) exitCode = fault.ExitCode;
                else if (ex is GuestThreadExitException exit) exitCode = exit.ExitCode;
            }
        }
        if (TerminationRequested)
            Complete(TerminationExitCode, ThreadState.Terminated);
        else
            Complete(exitCode, ThreadState.Exited);
    }

    private void Complete(int exitCode, ThreadState state)
    {
        if (IsMain)
        {
            lock (_gate) ExitCode ??= exitCode;
            // Signal the process before the thread handle so waiters on hProcess cannot miss it.
            Process.OnMainThreadExited(this);
        }
        MarkExited(exitCode, state);
    }

    internal ulong GetTlsValue(int slot)
    {
        lock (_gate) return _tlsValues.TryGetValue(slot, out var value) ? value : 0;
    }

    internal bool SetTlsValue(int slot, ulong value)
    {
        if (slot < 0 || slot >= 1088) return false;
        lock (_gate)
        {
            if (value == 0) _tlsValues.Remove(slot);
            else _tlsValues[slot] = value;
        }
        return true;
    }

    public override string ToString() =>
        $"Thread {Id} (native {NativeId}) {Name} {State} managed={_managedHosted} start=0x{StartAddress:X}";
}
