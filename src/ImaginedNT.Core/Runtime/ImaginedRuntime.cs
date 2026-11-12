using ImaginedNT.Core.Io;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using ImaginedNT.Core.Com;
using ImaginedNT.Core.Diagnostics;
using ImaginedNT.Core.Native;
using ImaginedNT.Core.Objects;
using ImaginedNT.Core.Pe;
using ImaginedNT.Core.Thunks;

namespace ImaginedNT.Core.Runtime;

/// <summary>
/// The managed NT kernel surface.  It owns the API symbol table, the IAT thunk arena, native
/// forwarding, the process registry and the host-thread → guest-thread routing table.  Multiple
/// imagined processes can live in one host process; every guest thread is a C# managed thread
/// whose identity selects the owning <see cref="ImaginedProcess"/> during dispatch.
/// </summary>
public sealed unsafe class ImaginedRuntime : IDisposable
{
    private readonly object _gate = new();
    internal object StateGate => _gate;
    private readonly DateTimeOffset _startTime = DateTimeOffset.UtcNow;
    private readonly List<ImaginedProcess> _processes = new();
    private readonly Dictionary<uint, SessionInformation> _sessions = new();
    private uint? _activeConsoleSessionId;
    private int _maximumVirtualPid = ushort.MaxValue;
    private readonly ConcurrentDictionary<uint, ImaginedThread> _threadsByNativeId = new();
    private readonly AsyncLocal<ImaginedThread?> _imaginedThread = new();
    private int _nextVirtualThreadId = 3;
    private bool _disposed;
    private int _bugChecked;
    public bool IsBugChecked => Volatile.Read(ref _bugChecked) != 0;
    public event Action<string>? BugChecked;
    private readonly HashSet<Assembly> _apiAssemblies = new();
    private readonly Dictionary<string, string> _importedLibraries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _dllRedirects = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dllRedirectPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Api, ulong Target), ulong> _nativeThunks = new();
    private readonly Dictionary<string, ulong> _managedThunks = new();
    private readonly ConcurrentDictionary<(int Pid, string Api), byte> _nativeWarnings = new();
    private static ImaginedRuntime? _diagnosticRuntime;
    private IntPtr _diagnosticHandler;

    public ImaginedRuntime(RuntimeOptions? options = null, IEnumerable<Assembly>? apiAssemblies = null)
    {
        Options = options ?? new RuntimeOptions();
        if (Options.DefaultSessionId == uint.MaxValue) throw new ArgumentOutOfRangeException(nameof(options), "invalid default session ID");
        foreach (var (source, target) in Options.DllRedirects)
        {
            var key = ApiRegistry.NormalizeModule(source.Trim());
            if (key.Length == 0 || string.IsNullOrWhiteSpace(target))
                throw new ArgumentException("DLL redirects require a source DLL and a target DLL");
            var value = target.Trim();
            if (value.IndexOfAny(['\\', '/']) >= 0 || Path.IsPathRooted(value))
            {
                value = Path.GetFullPath(value);
                _dllRedirectPaths.Add(value);
            }
            if (!_dllRedirects.TryAdd(key, value))
                throw new ArgumentException($"duplicate DLL redirect for '{source}'");
        }
        foreach (var source in _dllRedirects.Keys) ResolveLibraryName(source);
        var console = Options.TraceToConsole ? (Options.DiagnosticWriter ?? Console.Error) : null;
        Trace = new TraceLog(console, Options.TraceFile);
        Memory = new ImaginedNT.Core.Memory.RuntimeMemory(Options.MaximumPhysicalMemoryBytes, Options.MaximumVirtualMemoryBytes, Trace);
        foreach (var source in _dllRedirects.Keys)
            Trace.Emit(TraceKind.Image, $"DLL redirect: {source}.dll -> {ResolveLibraryName(source)}");

        // The guest trampolines restore RSP/RIP without CET shadow-stack or EH-continuation support.
        if (NativeMethods.GetProcessMitigationPolicy(NativeMethods.GetCurrentProcess(), NativeMethods.ProcessUserShadowStackPolicy,
                out var policy, (UIntPtr)sizeof(uint)))
        {
            if ((policy & 0x5) != 0) // EnableUserShadowStack / SetContextIpValidation
            {
                if (options?.AllowWithCET ?? true)
                {
                    var color = Console.ForegroundColor;
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.Error.WriteLine($"WARNING: CET shadow-stack/context validation is enabled (flags=0x{policy:X8}).");
                    Console.Error.WriteLine($"CET shadow-stack/context can cause some feature in guest trampolines incompatibility (like fail-fast catching).");
                    Console.ForegroundColor = color;
                }
                else
                {
                    throw new PlatformNotSupportedException($"CET shadow-stack/context validation is enabled (flags=0x{policy:X8}). ImaginedNT guest trampolines require a host built with <CETCompat>false</CETCompat>. Check the executable's Windows exploit-protection policy if CET is forced on.");
                }
            }
            else
            {
                Trace.Emit(Diagnostics.TraceKind.Info, $"CET shadow-stack/context validation is disabled (flags=0x{policy:X8}).");
            }
        }
        else if (Marshal.GetLastWin32Error() != 87) // Policy unavailable on older Windows versions.
            Trace.Warn($"could not query the host CET policy, {Marshal.GetLastWin32Error()}");

       

        Apis = new ApiRegistry();
        NativeDeclarations = new NativeApiRegistry();
        NativeApis = Options.NativeForwarderFactory?.Invoke(Trace) ?? new NativeForwarder(Trace, 0);
        Thunks = new IatThunkAllocator(Trace);
        Objects = new ObjectManager(Trace);
        WindowStations = new WindowStationManager(this);
        CreateSession(0, "Services");
        if (Options.DefaultSessionId != 0) CreateSession(Options.DefaultSessionId, "Console");
        SetActiveConsoleSession(Options.DefaultSessionId);
        Syscalls = new SyscallDispatcher(this);
        Com = new ComRuntime(this);
        foreach (var server in Options.ComServerPaths) Com.RegisterServer(server);
        try
        {
            if (apiAssemblies is not null)
                foreach (var assembly in apiAssemblies) RegisterApis(assembly);
        }
        catch
        {
            Dispose();
            throw;
        }
        if (Options.TraceNativeCalls)
        {
            _diagnosticRuntime = this;
            delegate* unmanaged<SyscallInterceptor.EXCEPTION_POINTERS*, int> handler = &TraceGuestFault;
            _diagnosticHandler = NativeMethods.AddVectoredExceptionHandler(1, (nint)handler);
        }

        Trace.Emit(TraceKind.Info,
            $"ImaginedNT runtime started: managed APIs={Apis.Count} native declarations={NativeDeclarations.Count} " +
            $"policy={Options.ImportPolicy} nativePolicy={Options.NativeApiPolicy} nativeFallback={Options.AllowUnimplementedApiForwarding} termination={Options.TerminationMode} " +
            $"hostPid={Environment.ProcessId} systemPid={ProcessInformation.SystemProcessId}");
    }

    /// <summary>Register an API assembly before loading or running guest code.</summary>
    public void RegisterApis(Assembly assembly)
    {
        ObjectDisposedException.ThrowIf(_disposed || IsBugChecked, this);
        ArgumentNullException.ThrowIfNull(assembly);
        if (_apiAssemblies.Contains(assembly)) return;
        var managedCount = VirtualApiScanner.Register(assembly, Apis);
        var nativeCount = NativeApiScanner.Register(assembly, NativeDeclarations);
        var comCount = ComScanner.Register(assembly, Com, Trace);
        _apiAssemblies.Add(assembly);
        Trace.Emit(TraceKind.Info,
            $"registered {managedCount} managed API entry points, {nativeCount} native API declarations and " +
            $"{comCount} COM class(es) from {assembly.GetName().Name}");
    }

    public void RegisterLibrary(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed || IsBugChecked, this);
        var fullPath = Path.GetFullPath(path);
        var image = PeImage.Load(fullPath);
        if (!image.IsDll || !image.Is64Bit || image.Machine != PeMachine.Amd64)
            throw new BadImageFormatException($"{path}: expected a Windows x64 DLL");

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed || IsBugChecked, this);
            var key = ApiRegistry.NormalizeModule(image.FileName);
            if (_importedLibraries.TryGetValue(key, out var existing))
            {
                if (string.Equals(existing, fullPath, StringComparison.OrdinalIgnoreCase)) return;
                throw new InvalidOperationException($"DLL '{image.FileName}' already imported from '{existing}'");
            }
            _importedLibraries.Add(key, fullPath);
        }
        Trace.Emit(TraceKind.Image, $"registered guest DLL '{fullPath}'");
    }

    public string? FindImportedLibrary(string name)
    {
        lock (_gate)
            return _importedLibraries.GetValueOrDefault(ApiRegistry.NormalizeModule(name));
    }

    /// <summary>解析 DLL 重定向链；模块名不区分大小写，可省略 .dll。</summary>
    public string ResolveLibraryName(string name)
    {
        if (_dllRedirects.Count == 0) return name;
        HashSet<string>? visited = null;
        while (_dllRedirects.TryGetValue(ApiRegistry.NormalizeModule(name), out var target))
        {
            var key = ApiRegistry.NormalizeModule(name);
            if (!(visited ??= new(StringComparer.OrdinalIgnoreCase)).Add(key))
                throw new ArgumentException($"DLL redirect cycle at '{name}'");
            name = target;
            if (ApiRegistry.NormalizeModule(target) == key) break;
        }
        return name;
    }

    internal bool IsDllRedirectPath(string name) => _dllRedirectPaths.Contains(name);

    public RuntimeOptions Options { get; }
    public Memory.RuntimeMemory Memory { get; }
    public TraceLog Trace { get; }
    public ApiRegistry Apis { get; }
    public NativeApiRegistry NativeDeclarations { get; }
    public INativeForwarder NativeApis { get; }
    public IatThunkAllocator Thunks { get; }
    public SyscallDispatcher Syscalls { get; }

    /// <summary>Managed COM subsystem (apartments, class factories, guest vtables, partial forwarding).</summary>
    public ComRuntime Com { get; }

    /// <summary>运行时对象空间，包含全局对象以及各会话的子目录；句柄表仍归进程所有。</summary>
    public ObjectManager Objects { get; }
    public WindowStationManager WindowStations { get; }

    public IReadOnlyList<SessionInformation> Sessions
    {
        get { lock (_gate) return _sessions.Values.OrderBy(s => s.Id).ToArray(); }
    }

    public uint? ActiveConsoleSessionId
    {
        get { lock (_gate) return _activeConsoleSessionId; }
    }

    public SessionInformation? GetSession(uint id)
    {
        lock (_gate) return _sessions.GetValueOrDefault(id);
    }

    public SessionInformation CreateSession(uint? sessionId = null, string? name = null)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed || IsBugChecked, this);
            var id = sessionId ?? 1;
            if (sessionId is null)
                while (_sessions.ContainsKey(id) && id < uint.MaxValue) id++;
            if (id == uint.MaxValue) throw new ArgumentOutOfRangeException(nameof(sessionId));
            if (_sessions.ContainsKey(id)) throw new InvalidOperationException($"session {id} already exists");
            var session = new SessionInformation
            {
                Id = id, Name = name ?? $"Session-{id}", State = SessionState.Connected
            };
            Objects.EnsureDirectory(session.ObjectDirectory);
            if (id == 0)
                Objects.CreateSymbolicLink($@"{session.ObjectDirectory}\BaseNamedObjects", session.BaseNamedObjectsPath);
            else
                Objects.EnsureDirectory(session.BaseNamedObjectsPath);
            Objects.CreateSymbolicLink($@"{session.BaseNamedObjectsPath}\Global", @"\BaseNamedObjects");
            Objects.CreateSymbolicLink($@"{session.BaseNamedObjectsPath}\Local", session.BaseNamedObjectsPath);
            _sessions.Add(id, session);
            WindowStations.InitializeSession(id);
            Trace.Emit(TraceKind.Process, $"created session {id} '{session.Name}'");
            return session;
        }
    }

    public void SetActiveConsoleSession(uint? sessionId)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed || IsBugChecked, this);
            if (sessionId is uint id && !_sessions.ContainsKey(id))
                throw new InvalidOperationException($"session {id} does not exist");
            if (_activeConsoleSessionId == sessionId) return;
            if (_activeConsoleSessionId is uint previous)
                _sessions[previous] = _sessions[previous] with { State = SessionState.Disconnected };
            _activeConsoleSessionId = sessionId;
            if (sessionId is uint active)
                _sessions[active] = _sessions[active] with { State = SessionState.Active };
            Trace.Emit(TraceKind.Process, $"active console session: {sessionId?.ToString() ?? "none"}");
        }
    }

    /// <summary>删除已无进程的会话；Session 0 保留到运行时销毁。</summary>
    public bool DeleteSession(uint id)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed || IsBugChecked, this);
            if (id == 0 || !_sessions.TryGetValue(id, out var session) || _processes.Any(p => p.SessionId == id))
                return false;
            if (_activeConsoleSessionId == id) SetActiveConsoleSession(null);
            WindowStations.DeleteSession(id);
            Objects.DeleteTree(session.ObjectDirectory);
            _sessions.Remove(id);
            Trace.Emit(TraceKind.Process, $"deleted session {id}");
            return true;
        }
    }

    /// <summary>The first (root) imagined process, kept for compatibility with single-process callers.</summary>
    public ImaginedProcess? Process
    {
        get { lock (_gate) return _processes.FirstOrDefault(); }
    }

    public ImaginedProcess? PrimaryProcess => Process;

    public IReadOnlyList<ImaginedProcess> Processes
    {
        get { lock (_gate) return _processes.ToArray(); }
    }

    public ImaginedProcess? GetProcessById(int id)
    {
        lock (_gate) return _processes.FirstOrDefault(p => p.Id == id);
    }

    public IReadOnlyList<ProcessInformation> GetProcessInformation()
    {
        lock (_gate)
            return new[] { new ProcessInformation(this, _startTime) }
                .Concat(_processes.Where(p => !p.IsTerminating).Select(p => new ProcessInformation(p))).ToArray();
    }

    public ProcessInformation? GetProcessInformation(int id)
    {
        lock (_gate)
        {
            if (id == ProcessInformation.SystemProcessId) return new ProcessInformation(this, _startTime);
            var process = _processes.FirstOrDefault(p => p.Id == id);
            return process is null ? null : new ProcessInformation(process);
        }
    }

    public ImaginedProcess CreateProcess(string imagePath, string? commandLine = null, string? currentDirectory = null)
    {
        return CreateProcess(new ProcessCreateOptions
        {
            ImagePath = imagePath,
            CommandLine = commandLine,
            CurrentDirectory = currentDirectory
        });
    }

    /// <summary>
    /// Create and register an imagined process.  The image is not loaded yet: callers normally
    /// use <see cref="ImaginedProcess.LoadImage"/> followed by <see cref="ImaginedProcess.Run"/>,
    /// or <see cref="ImaginedProcess.EnsureMainThread"/> for a child process.
    /// </summary>
    public ImaginedProcess CreateProcess(ProcessCreateOptions options) => CreateProcess(options, null);

    /// <summary>创建无需 PE 文件的托管进程；通过 EnsureMainThread/Run 启动，入口返回值作为退出码。</summary>
    public ImaginedProcess CreateImaginedProcess(ProcessCreateOptions options,
        Func<ImaginedProcessContext, CancellationToken, Task<int>> entryPoint)
    {
        ArgumentNullException.ThrowIfNull(entryPoint);
        return CreateProcess(options, null, entryPoint);
    }

    public ImaginedProcess CreateImaginedProcess(ProcessCreateOptions options,
        Func<ImaginedProcessContext, CancellationToken, int> entryPoint)
    {
        ArgumentNullException.ThrowIfNull(entryPoint);
        return CreateImaginedProcess(options, (context, token) => Task.FromResult(entryPoint(context, token)));
    }

    /// <summary>创建运行时的托管根进程；必须在创建其他进程前调用。</summary>
    public ImaginedProcess CreateRootImaginedProcess(ProcessCreateOptions options,
        Func<ImaginedProcessContext, CancellationToken, Task<int>> entryPoint)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(entryPoint);
        if (options.ParentId is not null || options.IsRoot == false)
            throw new ArgumentException("a root process cannot have a parent or IsRoot=false", nameof(options));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed || IsBugChecked, this);
            if (_processes.Count != 0)
                throw new InvalidOperationException("the root process must be created before other processes");
            return CreateImaginedProcess(options, entryPoint);
        }
    }

    public ImaginedProcess CreateRootImaginedProcess(ProcessCreateOptions options,
        Func<ImaginedProcessContext, CancellationToken, int> entryPoint)
    {
        ArgumentNullException.ThrowIfNull(entryPoint);
        return CreateRootImaginedProcess(options, (context, token) => Task.FromResult(entryPoint(context, token)));
    }

    /// <summary>创建并启动运行时的托管根进程；入口完成后退出。</summary>
    public ImaginedProcess StartRootImaginedProcess(ProcessCreateOptions options,
        Func<ImaginedProcessContext, CancellationToken, Task<int>> entryPoint) =>
        StartImaginedProcess(CreateRootImaginedProcess(options, entryPoint));

    public ImaginedProcess StartRootImaginedProcess(ProcessCreateOptions options,
        Func<ImaginedProcessContext, CancellationToken, int> entryPoint)
    {
        ArgumentNullException.ThrowIfNull(entryPoint);
        return StartRootImaginedProcess(options, (context, token) => Task.FromResult(entryPoint(context, token)));
    }

    /// <summary>创建并启动托管进程；Terminate 只请求取消，入口完成后才退出。</summary>
    public ImaginedProcess StartImaginedProcess(ProcessCreateOptions options,
        Func<ImaginedProcessContext, CancellationToken, Task<int>> entryPoint) =>
        StartImaginedProcess(CreateImaginedProcess(options, entryPoint));

    private static ImaginedProcess StartImaginedProcess(ImaginedProcess process)
    {
        if (process.EnsureMainThread(suspended: false).HasStarted) return process;
        process.Dispose();
        throw new InvalidOperationException("imagined process entry point could not be started");
    }

    public ImaginedProcess StartImaginedProcess(ProcessCreateOptions options,
        Func<ImaginedProcessContext, CancellationToken, int> entryPoint)
    {
        ArgumentNullException.ThrowIfNull(entryPoint);
        return StartImaginedProcess(options, (context, token) => Task.FromResult(entryPoint(context, token)));
    }

    /// <summary>创建来宾进程；非空 processId 指定固定 PID，否则随机分配。</summary>
    internal ImaginedProcess CreateProcess(ProcessCreateOptions options, int? processId,
        Func<ImaginedProcessContext, CancellationToken, Task<int>>? entryPoint = null)
    {
        ObjectDisposedException.ThrowIf(_disposed || IsBugChecked, this);
        ArgumentNullException.ThrowIfNull(options);
        if (processId is <= 0 or ProcessInformation.SystemProcessId)
            throw new ArgumentOutOfRangeException(nameof(processId));
        var fullPath = Path.GetFullPath(options.ImagePath);
        if (entryPoint is null && !HostFileSystem.FileExists(fullPath))
            throw new FileNotFoundException($"image not found: {fullPath}", fullPath);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed || IsBugChecked, this);
            ImaginedProcess? parent = null;
            if (options.ParentId is int parentId)
            {
                parent = _processes.FirstOrDefault(p => p.Id == parentId)
                         ?? throw new InvalidOperationException($"parent process {parentId} does not exist in this runtime");
            }

            var sessionId = options.SessionId ?? parent?.SessionId ?? Options.DefaultSessionId;
            var session = _sessions.GetValueOrDefault(sessionId)
                ?? throw new InvalidOperationException($"session {sessionId} does not exist in this runtime");
            var desktop = WindowStations.ResolveStartupDesktop(sessionId, parent, options.Desktop);

            var pid = processId ?? 0;
            if (processId.HasValue)
            {
                if (_processes.Any(p => p.Id == pid))
                    throw new InvalidOperationException($"process {pid} already exists in this runtime");
            }
            else
            {
                var usedPids = _processes.Select(p => p.Id).Append(ProcessInformation.SystemProcessId).Order().ToArray();
                var occupied = usedPids.Count(id => id <= _maximumVirtualPid);
                while (occupied == _maximumVirtualPid)
                {
                    if (_maximumVirtualPid == int.MaxValue)
                        throw new InvalidOperationException("virtual process IDs exhausted");
                    _maximumVirtualPid = (int)Math.Min((long)_maximumVirtualPid * 2 + 1, int.MaxValue);
                    occupied = usedPids.Count(id => id <= _maximumVirtualPid);
                    Trace.Emit(TraceKind.Process, $"expanded virtual PID limit to {_maximumVirtualPid}");
                }

                // 随机选择空闲 PID 的序号，再跳过已占用的 ID，避免范围接近用满时反复碰撞。
                pid = RandomNumberGenerator.GetInt32(_maximumVirtualPid - occupied) + 1;
                foreach (var id in usedPids)
                {
                    if (id > pid) break;
                    pid++;
                }
            }
            var sandboxRoot = options.SandboxRoot ?? parent?.FileSystem.Root ?? Options.EffectiveSandboxRoot(pid);
            // Win32 guests expect a drive-qualified current directory (cmd.exe builds paths from
            // it); the sandbox maps C:\ to its root.
            var currentDirectory = options.CurrentDirectory ?? parent?.CurrentDirectory ?? "C:\\";
            var environment = options.Environment ?? parent?.GetEnvironmentVariables() ?? VirtualEnvironment.Create(this, sandboxRoot);
            var isRoot = options.IsRoot ?? _processes.Count == 0;
            var commandLine = options.CommandLine ?? $"\"{fullPath}\"";
            var process = new ImaginedProcess(this, pid, fullPath, commandLine, currentDirectory,
                options.ParentId, sandboxRoot, environment, isRoot, session, desktop, entryPoint);
            _processes.Add(process);
            Trace.Emit(TraceKind.Process,
                $"runtime now owns {_processes.Count} imagined process(es); pid={pid} parent={(options.ParentId?.ToString() ?? "host")}", pid);
            return process;
        }
    }

    internal void RemoveProcess(ImaginedProcess process)
    {
        lock (_gate) _processes.Remove(process);
    }

    /// <summary>Raised once when a virtual process signals its exit, before its resources are disposed.</summary>
    public event Action<ImaginedProcess>? ProcessExited;
    public event Action<ImaginedThread>? ThreadExited;

    internal void OnThreadExited(ImaginedThread thread)
    {
        if (ThreadExited is not { } exited) return;
        foreach (Action<ImaginedThread> handler in exited.GetInvocationList())
            try { handler(thread); }
            catch (Exception ex) { Trace.Warn($"thread {thread.Id} exit cleanup failed: {ex}", thread.Process.Id, thread.Id); }
    }

    internal void OnProcessExited(ImaginedProcess process)
    {
        if (ProcessExited is not { } exited) return;
        foreach (Action<ImaginedProcess> handler in exited.GetInvocationList())
            try { handler(process); }
            catch (Exception ex) { Trace.Warn($"process {process.Id} exit cleanup failed: {ex}", process.Id); }
    }

    // --- host thread / guest process routing ---------------------------------------

    internal int NextThreadId() => Interlocked.Increment(ref _nextVirtualThreadId);

    public void RegisterThread(ImaginedThread thread)
    {
        if (thread.NativeId != 0) _threadsByNativeId[thread.NativeId] = thread;
    }

    public void UnregisterThread(ImaginedThread thread)
    {
        if (thread.NativeId == 0) return;
        if (_threadsByNativeId.TryGetValue(thread.NativeId, out var current) && ReferenceEquals(current, thread))
            _threadsByNativeId.TryRemove(thread.NativeId, out _);
    }

    public ImaginedThread? FindThreadByNativeId(uint nativeId) =>
        _threadsByNativeId.TryGetValue(nativeId, out var thread) ? thread : null;

    /// <summary>Resolve the guest thread executing on the current host thread.</summary>
    public ImaginedThread? GetCurrentThread() => _imaginedThread.Value is { IsFinished: false } thread
        ? thread : FindThreadByNativeId(Native.NativeMethods.GetCurrentThreadId());

    internal void SetImaginedThread(ImaginedThread? thread) => _imaginedThread.Value = thread;

    private ImaginedThread? ResolveThread()
    {
        if (GetCurrentThread() is { } thread) return thread;
        var nativeId = Native.NativeMethods.GetCurrentThreadId();
        if (_disposed) return null;

        // A callback from a native forwarded API can arrive on a thread the runtime did not
        // create.  Attribute it to the root process, matching the old single-process behavior.
        var primary = PrimaryProcess;
        return primary?.GetOrRegisterHostThread(nativeId);
    }

    /// <summary>Resolve one import according to the configured policy.</summary>
    public ImportResolution ResolveImport(PeImport import) =>
        ResolveImport(import, GetCurrentThread()?.Process ?? PrimaryProcess);

    internal ImportResolution ResolveImport(PeImport import, ImaginedProcess? process, string? importingPath = null)
    {
        if (TryResolveImport(import, process: process, importingPath: importingPath) is { } resolved) return resolved;

        var message = $"unresolved import {import.ModuleName}!{import.DisplayName} " +
                      $"(policy={Options.ImportPolicy}, nativeForwarding={Options.AllowNativeForwarding}, " +
                      $"nativeFallback={Options.AllowUnimplementedApiForwarding}, nativeApiPolicy={Options.NativeApiPolicy})";
        if (Options.FailOnUnresolvedImport) throw new BadImageFormatException(message);
        Trace.Warn(message);
        var failThunk = Thunks.Register(frame =>
        {
            Trace.Warn($"guest called unresolved import {import.ModuleName}!{import.DisplayName} caller=0x{frame->ReturnAddress:X16}");
            return Nt.NtStatus.EntrypointNotFound;
        }, $"unresolved:{import.ModuleName}!{import.DisplayName}");
        return ImportResolution.Failed(failThunk, message);
    }

    public ulong ResolveNativeExport(ulong module, string? name, ushort? ordinal)
    {
        if (module == 0) return 0;
        var moduleName = NativeApis.GetModuleName((IntPtr)(long)module) ?? $"module@0x{module:X}";
        return TryResolveImport(new PeImport
        {
            ModuleName = moduleName,
            Name = name,
            Ordinal = ordinal,
            IatRva = 0,
            Index = 0
        }, module, GetCurrentThread()?.Process ?? PrimaryProcess)?.Address ?? 0;
    }

    /// <summary>
    /// Return the dispatcher address of a registered managed API, creating its thunk on first use.
    /// Useful for embedding hosts and tests that need to call a managed API directly.
    /// </summary>
    public ulong GetManagedApiAddress(string module, string name)
    {
        ObjectDisposedException.ThrowIf(_disposed || IsBugChecked, this);
        if (!Apis.TryGet(module, name, out var registration) || registration is null)
            throw new InvalidOperationException($"managed API is not registered: {module}!{name}");
        return ManagedImport(registration).Address;
    }

    public ulong ResolveExport(ImaginedProcess process, ulong module, string? name, ushort? ordinal = null)
    {
        var mapped = process.FindModule(module);
        if (mapped is null) return ResolveNativeExport(module, name, ordinal);
        return ResolveMappedExport(process, mapped, new PeImport
        {
            ModuleName = mapped.Image.FileName,
            Name = name,
            Ordinal = ordinal,
            IatRva = 0,
            Index = 0
        }, 0)?.Address ?? 0;
    }

    private ImportResolution ManagedImport(ApiRegistration registration)
    {
        lock (_gate)
        {
            if (!_managedThunks.TryGetValue(registration.DisplayName, out var thunk))
            {
                thunk = registration.EntryStub is { } code
                    ? Thunks.RegisterEntryStub(code)
                    : Thunks.Register(frame => DispatchManaged(registration, frame), registration.DisplayName);
                _managedThunks.Add(registration.DisplayName, thunk);
            }
            return ImportResolution.Managed(thunk, $"managed {registration.DisplayName} ({registration.Category})");
        }
    }

    internal ApiRegistration? FindExportOverride(PeMappedImage mapped, PeExport export)
    {
        ApiRegistration? result = null;
        foreach (var e in mapped.Image.Exports.Where(e => e.Rva == export.Rva))
        {
            foreach (var name in e.Name is null ? new[] { $"#{e.Ordinal}" } : new[] { e.Name, $"#{e.Ordinal}" })
            {
                if (!Apis.TryGet(mapped.Image.FileName, name, out var r) || r is null) continue;
                if (result is not null && result.Handler != r.Handler)
                    throw new InvalidOperationException($"conflicting runtime implementations for {mapped.Image.FileName}+0x{export.Rva:X}: {result.Name}, {r.Name}");
                result ??= r;
            }
        }
        return result;
    }

    internal PeExportRedirector RedirectExports(PeMappedImage mapped)
    {
        var redirects = new PeExportRedirector(Trace);
        try
        {
            foreach (var group in mapped.Image.Exports.GroupBy(e => e.Rva))
            {
                var export = group.First();
                var registration = FindExportOverride(mapped, export);
                if (registration is null || export.Forwarder is not null) continue;
                if (!mapped.Image.Sections.Any(s => s.IsExecutable && s.ContainsRva(export.Rva)))
                    throw new BadImageFormatException($"{registration.DisplayName}: runtime override requires a function export");
                redirects.Add(mapped.BaseAddress + export.Rva, ManagedImport(registration).Address);
                Trace.Emit(TraceKind.Import, $"managed export {registration.DisplayName} at 0x{mapped.BaseAddress + export.Rva:X16}");
            }
            return redirects;
        }
        catch
        {
            redirects.Dispose();
            throw;
        }
    }

    private ImportResolution? ResolveMappedExport(ImaginedProcess process, PeMappedImage mapped, PeImport import, int depth)
    {
        var export = mapped.Image.Exports.FirstOrDefault(e => import.Name is null
            ? e.Ordinal == import.Ordinal : e.Name == import.Name);
        var registration = export is null ? null : FindExportOverride(mapped, export);
        if (registration is not null) return ManagedImport(registration);
        if (Apis.TryGet(mapped.Image.FileName, import.DisplayName, out registration) && registration is not null)
            return ManagedImport(registration);
        if (export is null) return null;
        if (export.Forwarder is { } forwarder)
        {
            var dot = forwarder.LastIndexOf('.');
            if (dot <= 0 || dot == forwarder.Length - 1)
                throw new BadImageFormatException($"invalid export forwarder: {mapped.Image.FileName}!{import.DisplayName} -> {forwarder}");
            var name = forwarder[(dot + 1)..];
            ushort? ordinal = null;
            if (name.StartsWith('#'))
            {
                if (!ushort.TryParse(name.AsSpan(1), out var n))
                    throw new BadImageFormatException($"invalid export forwarder ordinal: {forwarder}");
                ordinal = n;
            }
            return TryResolveImport(new PeImport
            {
                ModuleName = forwarder[..dot],
                Name = ordinal is null ? name : null,
                Ordinal = ordinal,
                IatRva = 0,
                Index = 0
            }, process: process, importingPath: mapped.Image.Path, depth: depth + 1);
        }
        // A guest-mapped image's export is guest code, not a host native forward.  It remains
        // callable under managed-only/strict policy (needed by LoadLibraryW + GetProcAddress).
        var target = mapped.BaseAddress + export.Rva;
        if (!mapped.Image.Sections.Any(s => s.IsExecutable && s.ContainsRva(export.Rva)))
            return ImportResolution.Native(target, $"guest data {mapped.Image.FileName}!{import.DisplayName}");
        return ImportResolution.Native(WrapNativeImport(import, target), $"guest native {mapped.Image.FileName}!{import.DisplayName}");
    }

    public static bool IsHostSystemModule(string module) =>
        ApiRegistry.NormalizeModule(module) is
            "kernel32" or "kernelbase" or "ntdll" or "msvcrt" or "ucrtbase" or
            "user32" or "advapi32" or "gdi32" or "rpcrt4" or "ole32" or "oleaut32" or
            "shell32" or "shlwapi" or "comctl32" or "ws2_32" or "crypt32" or "sechost" or
            "setupapi" or "version" or "winmm" or "imm32" or "uxtheme" or "dwmapi" or
            "activeds" or "netapi32" or "wintrust" or "combase" or "coml2" or "comcat";

    // Keep the CRT mapped per guest for its startup, environment and stdio state.
    internal bool ShouldLoadLibraryNatively(string path) =>
        Path.GetFullPath(path).StartsWith(Environment.SystemDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
        ApiRegistry.NormalizeModule(path) is not ("msvcrt" or "ucrtbase") &&
        FindImportedLibrary(path) is null && !IsDllRedirectPath(path);

    private ImportResolution? TryResolveImport(PeImport import, ulong? module = null, ImaginedProcess? process = null,
        string? importingPath = null, int depth = 0)
    {
        if (depth >= 64) throw new BadImageFormatException($"export forwarder cycle at {import.ModuleName}!{import.DisplayName}");
        var redirected = ResolveLibraryName(import.ModuleName);
        if (!string.Equals(redirected, import.ModuleName, StringComparison.OrdinalIgnoreCase))
        {
            import = new PeImport
            {
                ModuleName = redirected,
                Name = import.Name,
                Ordinal = import.Ordinal,
                IatRva = import.IatRva,
                Index = import.Index,
                IsDelayLoad = import.IsDelayLoad
            };
            module = null;
        }
        var policy = Options.ImportPolicy;
        Apis.TryGet(import.ModuleName, import.DisplayName, out var registration);
        if (registration is { IsPartiallyImagined: false })
            return ManagedImport(registration);

        // --host-system-dlls: use the host's fully initialized system DLLs for imports that the
        // managed layer does not implement, instead of mapping an uninitialized guest copy of
        // ntdll/kernel32 into the address space (which ReactOS binaries hit quickly).
        if (Options.PreferHostSystemDlls && Options.AllowNativeForwarding && policy != ImportPolicy.ManagedOnly &&
            IsHostSystemModule(import.ModuleName) &&
            ApiRegistry.NormalizeModule(import.ModuleName) is not ("msvcrt" or "ucrtbase"))
        {
            if (registration is not null) return ManagedImport(registration);
            if (IsNativeForwardingAllowed(import.ModuleName, import.Name, import.Ordinal))
            {
                var hostAddress = NativeApis.Resolve(import.ModuleName, import.Name, import.Ordinal, out var nativeDetail);
                if (hostAddress != 0)
                    return ImportResolution.Native(WrapNativeImport(import, hostAddress), nativeDetail);
            }
        }
        var partial = Apis.IsPartiallyImagined(import.ModuleName);
        var mapped = process?.FindModuleByName(import.ModuleName);
        if (mapped is null && process is not null && Options.AllowNativeForwarding && policy != ImportPolicy.ManagedOnly &&
            (partial || registration is null))
        {
            var path = process.FindLibraryPath(import.ModuleName, importingPath, partial);
            if (path is not null)
            {
                // Windows system DLLs require the host loader's private initialization state.
                if (ShouldLoadLibraryNatively(path))
                {
                    if (registration is not null) return ManagedImport(registration);
                    if (!IsNativeForwardingAllowed(import.ModuleName, import.Name, import.Ordinal)) return null;
                    var address = NativeApis.Resolve(path, import.Name, import.Ordinal, out var detail);
                    return address == 0 ? null : ImportResolution.Native(WrapNativeImport(import, address), detail);
                }
                mapped = process.LoadManagedLibrary(path) ?? throw new BadImageFormatException($"could not load guest DLL '{path}'");
            }
        }
        if (mapped is not null) return ResolveMappedExport(process!, mapped, import, depth);
        if (registration is not null) return ManagedImport(registration);
        if (partial && !NativeDeclarations.IsDeclared(import.ModuleName, import.DisplayName)) return null;

        if (policy != ImportPolicy.ManagedOnly && Options.AllowNativeForwarding &&
            IsNativeForwardingAllowed(import.ModuleName, import.Name, import.Ordinal))
        {
            var native = ResolveNativeImport(import, module, out var detail);
            if (native != 0) return ImportResolution.Native(WrapNativeImport(import, native), detail);
        }
        return null;
    }

    /// <summary>
    /// Whether the configured native forwarding policy permits resolving this export. The default
    /// policy allows undeclared exports only when the unimplemented-API fallback is enabled.
    /// Explicit declarations remain eligible when that fallback is disabled.
    /// </summary>
    public bool IsNativeForwardingAllowed(string moduleName, string? exportName, ushort? ordinal = null)
    {
        if (Options.AllowUnimplementedApiForwarding && Options.NativeApiPolicy == NativeApiPolicy.AllowAll) return true;
        if (exportName is not null) return NativeDeclarations.IsDeclared(moduleName, exportName);
        // Ordinal-only imports have no stable name to audit. They are accepted only if a
        // declaration explicitly uses the "#ordinal" spelling.
        return ordinal is ushort value && NativeDeclarations.IsDeclared(moduleName, $"#{value}");
    }

    private ulong ResolveNativeImport(PeImport import, ulong? module, out string detail)
    {
        if (module is not ulong handle)
            return NativeApis.Resolve(import.ModuleName, import.Name, import.Ordinal, out detail);
        var address = import.Name is null
            ? NativeApis.GetProcAddress((IntPtr)(long)handle, (IntPtr)(import.Ordinal ?? 0))
            : NativeApis.GetProcAddress((IntPtr)(long)handle, import.Name);
        detail = $"native {import.ModuleName}!{import.DisplayName} @0x{(ulong)(long)address:X16}";
        return (ulong)(long)address;
    }

    private ulong WrapNativeImport(PeImport import, ulong target)
    {
        var api = $"{ApiRegistry.NormalizeModule(import.ModuleName)}!{import.DisplayName}";
        lock (_gate)
        {
            if (_nativeThunks.TryGetValue((api, target), out var thunk)) return thunk;
            if ((Options.WrapNativeHandles || Options.ExpandManagedHandles) &&
                ApiRegistry.NormalizeModule(import.ModuleName) == "ntdll" && import.Name is null)
            {
                Trace.Warn($"native handle conversion requires a named ntdll export: {api}");
                thunk = Thunks.Register(_ => Nt.NtStatus.NotSupported, $"native-handles:{api}");
                _nativeThunks.Add((api, target), thunk);
                return thunk;
            }
            if ((Options.WrapNativeHandles || Options.ExpandManagedHandles) &&
                (NativeHandleBridge.IsNtApi(import.ModuleName, import.DisplayName) ||
                    NativeApiSignatures.TryGetWin32(import.ModuleName, import.DisplayName, out _)))
            {
                if (NativeHandleBridge.IsNtApi(import.ModuleName, import.DisplayName) &&
                    !NativeApiSignatures.TryGet(import.DisplayName, out _))
                    Trace.Warn($"native handle conversion has no signature for {api}; calls return STATUS_NOT_SUPPORTED");
                thunk = Thunks.Register(frame =>
                {
                    var thread = ResolveThread();
                    return thread is null ? Nt.NtStatus.ProcessIsTerminating :
                        NativeHandleBridge.InvokeFrame(thread.Process, thread, import.ModuleName, import.DisplayName, target, frame);
                }, $"native-handles:{api}");
                _nativeThunks.Add((api, target), thunk);
                return thunk;
            }
            thunk = Thunks.RegisterNativeForwarder(target, frame =>
            {
                var thread = ResolveThread();
                if (thread is { TerminationRequested: true, CanUnwindGuest: true })
                {
                    thread.RequestExit();
                    return ~thread.ExitTrampolineAddress;
                }
                if (_nativeWarnings.TryAdd((thread?.Process.Id ?? 0, api), 0))
                    Trace.Warn($"API not redirected: {api}; forwarding to native @0x{target:X16}",
                        thread?.Process.Id ?? 0, thread?.Id ?? 0);
                if (Options.TraceNativeCalls)
                {
                    // Logged before the native call: a blocking forward has no completion line.
                    // The guest return address sits at the forwarder frame's original rsp, 0xA8
                    // bytes above the callback frame's StackArgs (forwarder pushes + 0x90 save area).
                    var guestReturn = frame->StackArgs >= 0xA8 ? *(ulong*)(frame->StackArgs + 0xA8) : 0;
                    var extra = string.Empty;
                    if (frame->Rcx != 0 && api.Contains("SRWLock", StringComparison.Ordinal))
                    {
                        try { extra = $" lock=0x{*(ulong*)frame->Rcx:X}"; }
                        catch { extra = " lock=<bad pointer>"; }
                    }
                    Trace.Emit(TraceKind.Api,
                        $"native {api}({frame->Rcx:X},{frame->Rdx:X},{frame->R8:X},{frame->R9:X}){extra} guest=0x{guestReturn:X16} -> @0x{target:X16}",
                        thread?.Process.Id ?? 0, thread?.Id ?? 0);
                }
                return 0;
            }, $"native:{api}");
            _nativeThunks.Add((api, target), thunk);
            return thunk;
        }
    }

    /// <summary>Dispatch a managed API with an argument vector supplied by the syscall trap.</summary>
    internal uint DispatchManagedByName(string module, string name, IReadOnlyList<ulong> args, ulong returnAddress,
        out ulong result, ulong returnAddressSlot = 0)
    {
        result = 0;
        var thread = ResolveThread();
        var process = thread?.Process;
        if (process is null) return Nt.NtStatus.ProcessIsTerminating;
        Apis.TryGet(module, name, out var registration);

        var copied = new ulong[16];
        for (var i = 0; i < copied.Length && i < args.Count; i++) copied[i] = args[i];
        var context = new ApiCallContext(process, thread!, module, name, 0, returnAddress, copied, returnAddressSlot);
        try
        {
            if (registration is null)
            {
                if (!Options.AllowNativeForwarding || Options.ImportPolicy == ImportPolicy.ManagedOnly ||
                    !IsNativeForwardingAllowed(module, name) ||
                    !(Options.WrapNativeHandles || Options.ExpandManagedHandles)) return Nt.NtStatus.NotImplemented;
                var target = NativeApis.Resolve(module, name, null, out _);
                result = target == 0 ? Nt.NtStatus.EntrypointNotFound : NativeHandleBridge.Invoke(context, target);
            }
            else result = registration.Handler(context);
            var lastError = ImaginedNT.Core.Native.NativeMethods.GetLastError();
            context.Complete(result);
            ImaginedNT.Core.Native.NativeMethods.SetLastError(lastError);
            if (context.PendingThreadExitCode is int exitCode)
            {
                if (context.TryRedirectThreadExit()) result = unchecked((uint)exitCode);
                else if (!thread!.IsImaginedEntry) ImaginedNT.Core.Native.NativeMethods.ExitThread(unchecked((uint)exitCode));
            }
            // Nt* syscall handlers return an NTSTATUS in rax; that value is the syscall result.
            return (uint)result;
        }
        catch (GuestThreadExitException ex)
        {
            context.PendingThreadExitCode = ex.ExitCode;
            result = unchecked((uint)ex.ExitCode);
            return (uint)result;
        }
        catch (Io.NtFileException ex)
        {
            result = ex.Status;
            Trace.Emit(TraceKind.Syscall, $"{name} failed: {Nt.NtStatus.NameOf(ex.Status)} ({ex.Message})", process.Id, thread!.Id);
            return ex.Status;
        }
        catch (Exception ex)
        {
            Trace.Error($"{name} (syscall dispatch) threw {ex.GetType().Name}: {ex.Message}", process.Id, thread!.Id);
            result = Nt.NtStatus.NotImplemented;
            return Nt.NtStatus.NotImplemented;
        }
        finally
        {
            if (process.FaultExitCode is int code) context.PendingThreadExitCode = code;
            if (context.PendingThreadExitCode is not null) context.TryRedirectThreadExit();
        }
    }

    private ulong DispatchManaged(ApiRegistration registration, NativeCallFrame* frame)
    {
        var thread = ResolveThread();
        var process = thread?.Process;
        if (thread is null || process is null)
        {
            Trace.Error($"managed API {registration.DisplayName} called without a process");
            return Nt.NtStatus.ProcessIsTerminating;
        }

        var args = new ulong[16];
        args[0] = frame->Rcx;
        args[1] = frame->Rdx;
        args[2] = frame->R8;
        args[3] = frame->R9;
        if (frame->StackArgs != 0)
        {
            for (var i = 4; i < args.Length; i++)
            {
                args[i] = *(ulong*)(frame->StackArgs + (ulong)(i - 4) * 8);
            }
        }

        // The universal stub stores the first stack argument at rbp+0x30, i.e. guest rsp+0x28
        // (return address + 32-byte shadow space), so the return-address slot is stackArgs-0x28.
        var returnAddressSlot = frame->StackArgs >= 0x28 ? frame->StackArgs - 0x28 : 0;
        var context = new ApiCallContext(process, thread, registration.Module, registration.Name, frame->ApiId,
            frame->ReturnAddress, args, returnAddressSlot);
        try
        {
            if (thread.TerminationRequested && !thread.IsImaginedEntry)
            {
                // TerminateThread is not a supported way to stop a CLR thread.  Unwind the guest
                // through its own stub instead: the next managed call after Terminate returns to
                // the thread exit trampoline and the guest stack is restored normally.
                var requestedExit = thread.TerminationExitCode;
                context.PendingThreadExitCode = requestedExit;
                if (context.TryRedirectThreadExit())
                {
                    Trace.Emit(TraceKind.Thread,
                        $"thread {thread.Id} unwound after termination request (exit={requestedExit})",
                        process.Id, thread.Id);
                    return unchecked((uint)requestedExit);
                }

                Trace.Warn($"thread {thread.Id} cannot redirect its exit; stopping the host thread", process.Id, thread.Id);
                ImaginedNT.Core.Native.NativeMethods.ExitThread(unchecked((uint)requestedExit));
                return unchecked((uint)requestedExit);
            }

            var result = registration.Handler(context);
            // Tracing writes files/console and would otherwise clobber the host thread's
            // last-error slot between the handler and the guest's next GetLastError.
            var lastError = ImaginedNT.Core.Native.NativeMethods.GetLastError();
            var completed = context.Complete(result);
            ImaginedNT.Core.Native.NativeMethods.SetLastError(lastError);
            if (context.PendingThreadExitCode is int exitCode)
            {
                if (context.TryRedirectThreadExit())
                {
                    // The stub will ret into the unmanaged exit trampoline, which uses EAX.
                    return unchecked((uint)exitCode);
                }
                if (!thread.IsImaginedEntry) ImaginedNT.Core.Native.NativeMethods.ExitThread(unchecked((uint)exitCode));
            }
            return completed;
        }
        catch (GuestThreadExitException ex)
        {
            context.PendingThreadExitCode = ex.ExitCode;
            return unchecked((uint)ex.ExitCode);
        }
        catch (Io.NtFileException ex)
        {
            Trace.Emit(TraceKind.Api, $"{registration.DisplayName} failed: {Nt.NtStatus.NameOf(ex.Status)} ({ex.Message})", process.Id, thread!.Id);
            return ex.Status;
        }
        catch (Memory.NtMemoryException ex)
        {
            Trace.Emit(TraceKind.Api, $"{registration.DisplayName} memory failure: {Nt.NtStatus.NameOf(ex.Status)} ({ex.Message})", process.Id, thread!.Id);
            return ex.Status;
        }
        catch (Exception ex)
        {
            Trace.Error($"{registration.DisplayName} threw {ex.GetType().Name}: {ex.Message}", process.Id, thread!.Id);
            return Nt.NtStatus.NotImplemented;
        }
        finally
        {
            if (process.FaultExitCode is int code) context.PendingThreadExitCode = code;
            if (context.PendingThreadExitCode is not null) context.TryRedirectThreadExit();
        }
    }

    /// <summary>Patch direct syscalls in a newly loaded image; the process owns the interceptor.</summary>
    internal SyscallInterceptor? InstallSyscallInterceptor(ImaginedProcess process, PeMappedImage image)
    {
        if (!Options.InterceptSyscalls) return null;
        try
        {
            var interceptor = new SyscallInterceptor(process, image, Trace);
            var count = interceptor.PatchedCount;
            Trace.Emit(TraceKind.Syscall,
                count == 0
                    ? $"syscall interception armed for process {process.Id}; no syscall opcodes found in executable sections"
                    : $"syscall interception armed for process {process.Id}; patched {count} syscall instruction(s) in {image.Image.FileName}",
                process.Id);
            return interceptor;
        }
        catch (Exception ex)
        {
            Trace.Warn($"syscall interception could not be installed for process {process.Id}: {ex.Message}", process.Id);
            return null;
        }
    }

    /// <summary>ExecuteImage 启用时创建并运行映像，返回退出码；关闭时不创建进程并返回 0。</summary>
    public int Run(string imagePath, TimeSpan? timeout = null, string? commandLine = null, ulong forcedBase = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed || IsBugChecked, this);
        if (!Options.ExecuteImage) return 0;
        var process = CreateProcess(new ProcessCreateOptions
        {
            ImagePath = imagePath,
            CommandLine = commandLine,
            IsRoot = true
        });
        process.LoadImage(imagePath, forcedBase);
        var info = process.Run(timeout);
        return info.ExitCode;
    }

    [UnmanagedCallersOnly]
    private static int TraceGuestFault(SyscallInterceptor.EXCEPTION_POINTERS* pointers)
    {
        try
        {
            var runtime = _diagnosticRuntime;
            var record = pointers->ExceptionRecord;
            var context = pointers->ContextRecord;
            if (runtime is null || record is null || context is null) return 0;
            var code = record->ExceptionCode;
            if (code is not (0xC0000005 or 0xC00000FD or 0xC000001D or 0xC0000094 or 0xC0000096)) return 0;

            var address = (ulong)record->ExceptionAddress;
            var thread = runtime.GetCurrentThread();
            runtime.Trace.Emit(TraceKind.Error,
                $"guest fault 0x{code:X8} at 0x{address:X16} rip=0x{context->Rip:X16} rsp=0x{context->Rsp:X16} " +
                $"rbp=0x{context->Rbp:X16} info0=0x{record->ExceptionInformation[0]:X} info1=0x{record->ExceptionInformation[1]:X}",
                thread?.Process.Id ?? 0, thread?.Id ?? 0);

            // Keep this handler allocation-light: it runs on the faulting thread and must not
            // rely on LINQ/iterator machinery while the CLR is unwinding an access violation.
        }
        catch
        {
            // Exception diagnostics must never mask the original fault.
        }
        return 0; // EXCEPTION_CONTINUE_SEARCH
    }

    public void StopWithBugCheck(string? message = null)
    {
        ImaginedProcess[] processes;
        lock (_gate)
        {
            if (_disposed || Interlocked.Exchange(ref _bugChecked, 1) != 0) return;
            processes = _processes.ToArray();
        }
        var stack = new System.Diagnostics.StackTrace(1, true).ToString();
        const int exitCode = unchecked((int)0xDEAD0001);
        try
        {
            Trace.Error($"BUGCHECK: {message ?? "Fatal error occurs"}\nCaller stack:\n{stack}");
            var memory = Memory.GetSnapshot();
            Trace.Error($"Kernel: started={_startTime:O}, processes={processes.Length}, APIs={Apis.Count}, " +
                $"virtualBytes={memory.VirtualBytes}, committed={memory.Process.PrivateUsage}");
            foreach (var p in processes)
            {
                Trace.Error($"Process: id={p.Id}, parent={p.ParentId}, image={p.ImagePath}, exit={p.ExitCode}, terminating={p.IsTerminating}");
                foreach (var t in p.Threads)
                    Trace.Error($"Thread: pid={p.Id}, tid={t.Id}, native={t.NativeId}, state={t.State}, start=0x{t.StartAddress:X}, exit={t.ExitCode}");
                foreach (var m in p.LoadedModules) Trace.Error($"Module: pid={p.Id}, {m}");
                foreach (var h in p.Handles.Snapshot()) Trace.Error($"Handle: pid={p.Id}, {h}");
            }
            Trace.Flush();
        }
        finally
        {
            foreach (var p in processes.Reverse())
                try { p.Terminate(exitCode); }
                catch (Exception ex) { Trace.Error($"bugcheck termination failed: pid={p.Id}: {ex}"); }
            try { BugChecked?.Invoke(message ?? "Fatal error occurs"); }
            catch (Exception ex) { Trace.Error($"bugcheck notification failed: {ex}"); }
            Dispose();
        }
    }

    public void Dispose()
    {
        ImaginedProcess[] processes;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            processes = _processes.ToArray();
        }

        // Children are disposed first: their memory and threads go away before the root's, and
        // any ProcessObject in a parent handle table only observes an inert process.
        for (var i = processes.Length - 1; i >= 0; i--)
        {
            try { processes[i].Dispose(); } catch { }
        }

        if (processes.Any(p => p.HasRunningThreads))
        {
            Trace.Warn("runtime still has running guest threads; retaining shared resources");
            // A new runtime may reuse this path before the remaining guest threads exit.
            Trace.CloseFile();
            ThreadPool.QueueUserWorkItem(state =>
            {
                try
                {
                    foreach (var thread in processes.SelectMany(p => p.Threads).Where(t => t.IsExecutionActive))
                        thread.Wait(Timeout.InfiniteTimeSpan, out _);
                    if (!processes.Any(p => p.HasRunningThreads)) DisposeResources(processes);
                }
                catch (Exception ex) { Trace.Error($"deferred runtime cleanup failed: {ex}"); }
            });
            return;
        }
        DisposeResources(processes);
    }

    private void DisposeResources(ImaginedProcess[] processes)
    {
        foreach (var process in processes)
            try { process.Dispose(); }
            catch (Exception ex) { Trace.Warn($"process {process.Id} cleanup failed: {ex}", process.Id); }
        _threadsByNativeId.Clear();
        if (_diagnosticHandler != IntPtr.Zero)
        {
            NativeMethods.RemoveVectoredExceptionHandler(_diagnosticHandler);
            _diagnosticHandler = IntPtr.Zero;
        }
        if (ReferenceEquals(_diagnosticRuntime, this)) _diagnosticRuntime = null;
        try { Com.Dispose(); } catch { }
        try { Objects.Dispose(); } catch { }
        Thunks.Dispose();
        NativeApis.Dispose();
        Trace.Flush();
        Trace.Dispose();
    }
}
