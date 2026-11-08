using System.Collections.Concurrent;
using ImaginedNT.Core.Diagnostics;
using ImaginedNT.Core.Io;
using ImaginedNT.Core.Memory;
using ImaginedNT.Core.Nt;
using ImaginedNT.Core.Objects;
using ImaginedNT.Core.Pe;

namespace ImaginedNT.Core.Runtime;

public sealed record ProcessExitInfo(int ExitCode, bool TimedOut, TimeSpan Duration);

/// <summary>
/// A guest process.  Each process owns a virtual address-space manager, an NT object namespace,
/// a handle table and a sandboxed file-system view.  Guest threads are hosted on C# managed
/// threads, which lets several imagined processes coexist in one host process while keeping
/// their runtime state (and their guest stacks) separate.
/// </summary>
public sealed class ImaginedProcess : IDisposable
{
    public (ulong Start, ulong Exit) ThreadPoolCallbacks { get; set; }
    public (ulong Probe, ulong CreateActivationContext, ulong ReleaseActivationContext) ManifestCallbacks { get; set; }
    internal FiberLocalStorage Fls { get; } = new();
    private readonly object _threadGate = new();
    private readonly object _exitGate = new();
    private readonly object _moduleGate = new();
    private readonly object _heapGate = new();
    private readonly object _tlsGate = new();
    private readonly HashSet<int> _tlsSlots = new();
    private readonly Stack<int> _freeTlsSlots = new();
    private int _nextTlsSlot;
    private readonly List<GuestHeap> _heaps = new();
    private readonly List<ImaginedThread> _threads = new();
    private readonly Dictionary<uint, ImaginedThread> _threadsByNativeId = new();
    private readonly List<PeMappedImage> _modules = new();
    private readonly Dictionary<PeMappedImage, PeExportRedirector> _exportRedirectors = new();
    private readonly Dictionary<PeMappedImage, GuestFailFastInterceptor> _failFastInterceptors = new();
    private readonly HashSet<PeMappedImage> _initializedLibraries = new();
    private readonly HashSet<PeMappedImage> _initializingLibraries = new();
    private List<PeMappedImage>? _loadTransaction;
    private int _libraryLoadDepth;
    private volatile ConcurrentDictionary<string, string> _environment = new(StringComparer.OrdinalIgnoreCase);
    private readonly RuntimeOptions _options;
    private readonly int _pid;
    private readonly JobObjectController? _job;
    private readonly bool _isRoot;
    private volatile bool _terminating;
    private ImaginedThread? _mainThread;
    private SyscallInterceptor? _syscallInterceptor;
    private ulong _commandLineBuffer;
    private bool _started;
    private bool _disposed;
    private int? _faultExitCode;
    private DateTimeOffset _startTime;

    internal ImaginedProcess(ImaginedRuntime runtime, int id, string imagePath, string commandLine, string currentDirectory,
        int? parentId, string sandboxRoot, IReadOnlyDictionary<string, string> environment, bool isRoot)
    {
        Runtime = runtime;
        Id = id;
        _pid = id;
        _options = runtime.Options;
        PackageIdentity = parentId is int parent
            ? runtime.GetProcessById(parent)?.PackageIdentity
            : _options.IsPackaged ? _options.PackageIdentity ?? throw new ArgumentException("package identity is required") : null;
        PackageIdentity?.Validate();
        _isRoot = isRoot;
        ImagePath = Path.GetFullPath(imagePath);
        CommandLine = commandLine;
        CurrentDirectory = currentDirectory;
        ParentId = parentId;
        StartTime = DateTimeOffset.UtcNow;
        _startTime = StartTime;

        Trace = runtime.Trace;
        Memory = new VirtualMemoryManager(Trace, id);
        Heap = new GuestHeap(Trace, id);
        _heaps.Add(Heap);
        ObjectManager = runtime.Objects;
        Handles = new HandleTable(Trace, id);
        FileSystem = new SandboxFileSystem(sandboxRoot, _options.AllowFileWrites, Trace, id, _options.Drives);
        foreach (var drive in FileSystem.Drives)
        {
            ObjectManager.EnsureDirectory(drive.DevicePath);
            ObjectManager.CreateSymbolicLink($@"\??\{drive.Letter}:", drive.DevicePath, overwrite: true);
        }

        // The job object is a property of the host process, not of an imagined child.  Only the
        // root process installs it so nested creations do not try to re-assign the host.
        if (_options.LimitJobObject && isRoot)
        {
            _job = new JobObjectController(_options.JobMemoryLimitBytes, _options.JobActiveProcessLimit);
            if (_job.Error is not null)
                Trace.Warn($"job object: {_job.Error}", id);
            else
                Trace.Emit(TraceKind.Security, $"job object active: memory<=0x{_options.JobMemoryLimitBytes:X}, processes<={_options.JobActiveProcessLimit}", id);
        }

        foreach (var pair in environment) _environment[pair.Key] = pair.Value;

        InitializeStandardHandles();
        Trace.Emit(TraceKind.Process,
            $"created imagined process {id} image='{ImagePath}' sandbox='{FileSystem.Root}' parent={(parentId?.ToString() ?? "<host>")} root={isRoot} package='{PackageIdentity?.FullName ?? "<none>"}'", id);
    }

    public ImaginedRuntime Runtime { get; }
    public PackageIdentity? PackageIdentity { get; }
    public int Id { get; }
    public int? ParentId { get; internal set; }
    public bool IsRoot => _isRoot;
    public string ImagePath { get; }
    public string CommandLine { get; }
    public string CurrentDirectory { get; set; }
    public DateTimeOffset StartTime { get; }
    public TraceLog Trace { get; }
    public VirtualMemoryManager Memory { get; }
    public GuestHeap Heap { get; }

    public GuestHeap? FindHeap(ulong handle)
    {
        lock (_heapGate) return _heaps.FirstOrDefault(h => h.MatchesHandle(handle));
    }

    /// <summary>Allocate a Win32 TLS index; -1 means TLS_OUT_OF_INDEXES.</summary>
    public int AllocateTlsSlot()
    {
        lock (_tlsGate)
        {
            if (_freeTlsSlots.Count > 0)
            {
                var slot = _freeTlsSlots.Pop();
                _tlsSlots.Add(slot);
                return slot;
            }
            if (_nextTlsSlot >= 1088) return -1;
            var next = _nextTlsSlot++;
            _tlsSlots.Add(next);
            return next;
        }
    }

    public bool FreeTlsSlot(int slot)
    {
        lock (_tlsGate)
        {
            if (slot < 0 || slot >= 1088 || !_tlsSlots.Remove(slot)) return false;
            _freeTlsSlots.Push(slot);
            return true;
        }
    }

    public ulong CreateHeap(uint options, ulong initialSize, ulong maximumSize, int threadId)
    {
        GuestHeap? heap = null;
        try
        {
            heap = new GuestHeap(Trace, Id, options, initialSize, maximumSize);
            var handle = heap.GetHandle(threadId);
            lock (_heapGate)
            {
                if (handle == 0 || _disposed)
                {
                    heap.Dispose();
                    return 0;
                }
                _heaps.Add(heap);
            }
            return handle;
        }
        catch (OutOfMemoryException ex)
        {
            heap?.Dispose();
            Trace.Warn($"could not register guest heap: {ex.Message}", Id, threadId);
            return 0;
        }
    }

    public bool DestroyHeap(ulong handle)
    {
        lock (_heapGate)
        {
            var heap = _heaps.FirstOrDefault(h => h.MatchesHandle(handle));
            if (heap is null || ReferenceEquals(heap, Heap) || !heap.Destroy()) return false;
            _heaps.Remove(heap);
            return true;
        }
    }
    public ObjectManager ObjectManager { get; }
    public HandleTable Handles { get; }
    public SandboxFileSystem FileSystem { get; }
    public PeMappedImage? Image { get; private set; }
    public int? ExitCode { get; private set; }
    public bool IsTerminating => _terminating;
    internal int? FaultExitCode
    {
        get { lock (_exitGate) return _faultExitCode; }
    }
    internal bool HasRunningThreads => Threads.Any(t => t.IsExecutionActive);
    public IReadOnlyList<ImaginedThread> Threads
    {
        get { lock (_threadGate) return _threads.ToArray(); }
    }

    public ImaginedThread? MainThread
    {
        get { lock (_threadGate) return _mainThread; }
    }

    public IReadOnlyDictionary<string, string> EnvironmentVariables => _environment;
    public string NtImagePath => "\\??\\" + ImagePath;
    public IReadOnlyList<PeMappedImage> LoadedModules
    {
        get { lock (_threadGate) return _modules.ToArray(); }
    }

    public PeMappedImage? FindModule(ulong baseAddress)
    {
        lock (_moduleGate)
            lock (_threadGate)
            {
                if (Image is not null && Image.BaseAddress == baseAddress) return Image;
                return _modules.FirstOrDefault(m => m.BaseAddress == baseAddress || m.ContainsAddress(baseAddress));
            }
    }

    public PeMappedImage? FindModuleByName(string name)
    {
        var file = ApiRegistry.NormalizeModule(Runtime.ResolveLibraryName(name));
        lock (_moduleGate)
            lock (_threadGate)
            {
                if (Image is not null && ApiRegistry.NormalizeModule(Image.Image.FileName) == file) return Image;
                return _modules.FirstOrDefault(m => ApiRegistry.NormalizeModule(m.Image.FileName) == file);
            }
    }

    /// <summary>
    /// Load a guest DLL into the managed address space, patch its imports, and call its
    /// DllMain(DLL_PROCESS_ATTACH).  This is the managed equivalent of LoadLibrary.
    /// </summary>
    public PeMappedImage? LoadManagedLibrary(string hostPath)
    {
        lock (_moduleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var fullPath = Path.GetFullPath(hostPath);
            if (FindModuleByName(fullPath) is { } existing)
            {
                if (_libraryLoadDepth == 0 && Runtime.GetCurrentThread()?.Process == this) InitializeLibraries();
                return existing;
            }
            var image = PeImage.Load(fullPath);
            if (!image.IsDll) throw new BadImageFormatException($"{fullPath} is not a DLL image");
            var outer = _libraryLoadDepth++ == 0;
            if (outer) _loadTransaction = new();
            PeMappedImage? mapped = null;
            var loaded = false;
            try
            {
                var loader = new PeLoader(Memory, import => ResolveImageImport(import, fullPath),
                    Trace, _options.ApplySectionProtections);
                mapped = loader.Load(image, beforeImports: m =>
                {
                    mapped = m;
                    lock (_threadGate) _modules.Add(m);
                    _loadTransaction!.Add(m);
                });
                loaded = true;
                _failFastInterceptors.Add(mapped, new GuestFailFastInterceptor(this, mapped));
                _exportRedirectors.Add(mapped, Runtime.RedirectExports(mapped));
                if (outer && Runtime.GetCurrentThread()?.Process == this) InitializeLibraries();
                Trace.Emit(TraceKind.Image, $"loaded guest module {image.FileName} base=0x{mapped.BaseAddress:X16}", Id);
                return mapped;
            }
            catch (Exception ex)
            {
                if (!loaded && mapped is not null)
                {
                    lock (_threadGate) _modules.Remove(mapped);
                    _loadTransaction!.Remove(mapped); // PeLoader already freed this allocation.
                }
                if (outer)
                {
                    RollbackLibraries();
                    Trace.Error($"loading guest DLL '{fullPath}' failed: {ex}", Id);
                }
                throw;
            }
            finally
            {
                _libraryLoadDepth--;
                if (outer) _loadTransaction = null;
            }
        }
    }

    private ImportResolution ResolveImageImport(PeImport import, string? importingPath)
    {
        if (!import.IsDelayLoad) return Runtime.ResolveImport(import, this, importingPath);
        ulong target = 0;
        var address = Runtime.Thunks.RegisterLazyForwarder(() =>
        {
            lock (_moduleGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (target == 0) target = Runtime.ResolveImport(import, this, importingPath).Address;
                return target;
            }
        }, $"{Path.GetFileName(importingPath)} -> {import.ModuleName}!{import.DisplayName}");
        return ImportResolution.Deferred(address, $"delay {import.ModuleName}!{import.DisplayName}");
    }

    private void RollbackLibraries()
    {
        foreach (var mapped in _loadTransaction!.AsEnumerable().Reverse())
        {
            if (_exportRedirectors.Remove(mapped, out var redirects)) redirects.Dispose();
            if (_failFastInterceptors.Remove(mapped, out var traps)) traps.Dispose();
            lock (_threadGate) _modules.Remove(mapped);
            _initializedLibraries.Remove(mapped);
            Memory.TryFree(mapped.BaseAddress, 0, FreeType.Release);
        }
        _loadTransaction.Clear();
    }

    internal string? FindLibraryPath(string name, string? importingPath = null, bool includeSystem = false)
    {
        name = Runtime.ResolveLibraryName(name);
        if (Runtime.IsDllRedirectPath(name))
            return HostFileSystem.FileExists(name) ? name : throw new FileNotFoundException($"redirected DLL not found: '{name}'", name);
        if (Runtime.FindImportedLibrary(name) is { } imported) return imported;
        var candidates = new List<string>();
        try { candidates.Add(FileSystem.MapPath(name, CurrentDirectory)); }
        catch (NtFileException) { }
        if (!Path.IsPathRooted(name))
        {
            if (importingPath is not null) candidates.Add(Path.Combine(Path.GetDirectoryName(importingPath)!, name));
            candidates.Add(Path.Combine(Path.GetDirectoryName(ImagePath)!, name));
        }
        if (includeSystem) candidates.Add(Path.Combine(Environment.SystemDirectory, Path.GetFileName(name)));
        foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (HostFileSystem.FileExists(path)) return Path.GetFullPath(path);
            if (!path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && HostFileSystem.FileExists(path + ".dll"))
                return Path.GetFullPath(path + ".dll");
        }
        return null;
    }

    internal void InitializeLibraries()
    {
        lock (_moduleGate)
            foreach (var mapped in LoadedModules) InitializeLibrary(mapped);
    }

    internal unsafe void RunTlsCallbacks(PeMappedImage image, uint reason)
    {
        if (image.TlsCallbacks.Count == 0) return;
        var thread = Runtime.GetCurrentThread();
        foreach (var callback in image.TlsCallbacks)
        {
            if (callback == 0) continue;
            if (thread is { IsManagedHosted: true } && thread.Process == this)
                thread.InvokeGuest(callback, [image.BaseAddress, reason, 0]);
            else
                ((delegate* unmanaged<nint, uint, nint, int>)(nint)callback)((nint)image.BaseAddress, reason, 0);
        }
    }

    private unsafe void InitializeLibrary(PeMappedImage mapped)
    {
        if (_initializedLibraries.Contains(mapped) || !_initializingLibraries.Add(mapped)) return;
        try
        {
            foreach (var name in mapped.Image.Imports.Where(i => !i.IsDelayLoad).Select(i => i.ModuleName).Distinct(StringComparer.OrdinalIgnoreCase))
                if (FindModuleByName(name) is { } dependency && dependency.Image.IsDll) InitializeLibrary(dependency);
            RunTlsCallbacks(mapped, 1 /* DLL_PROCESS_ATTACH */);
            if (mapped.EntryPoint != 0)
            {
                var thread = Runtime.GetCurrentThread();
                var result = thread is { IsManagedHosted: true } && thread.Process == this
                    ? thread.InvokeGuest(mapped.EntryPoint, [mapped.BaseAddress, 1, 0])
                    : (ulong)((delegate* unmanaged<nint, uint, nint, int>)(nint)mapped.EntryPoint)((nint)mapped.BaseAddress, 1, 0);
                if ((uint)result == 0)
                    throw new BadImageFormatException($"DllMain returned FALSE for {mapped.Image.FileName}");
                Trace.Emit(TraceKind.Image, $"DllMain(DLL_PROCESS_ATTACH) completed for {mapped.Image.FileName}", Id);
            }
            _initializedLibraries.Add(mapped);
        }
        finally { _initializingLibraries.Remove(mapped); }
    }

    public void SetEnvironmentVariable(string name, string? value) =>
        SetEnvironmentVariable(name, value, EnvironmentVariableTarget.Process);

    private static string EscapeForErrorMessage(string? value)
    {
        if (value is null) return "<null>";
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var character in value)
        {
            switch (character)
            {
                case '\\': builder.Append("\\\\"); break;
                case '\"': builder.Append("\\\""); break;
                case '\r': builder.Append("\\r"); break;
                case '\n': builder.Append("\\n"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (character < 0x20) builder.Append($"\\u{(int)character:X4}");
                    else builder.Append(character);
                    break;
            }
        }
        return builder.ToString();
    }

    public void SetEnvironmentVariable(string name, string? value, EnvironmentVariableTarget target)
    {
        if (string.IsNullOrEmpty(name) || name.Contains('=') || name.Contains('\0') || name.Length > 32766 ||
            value is { Length: > 32766 } || value?.Contains('\0') == true)
            throw new ArgumentException($"Invalid environment variable name or value. Try to set name {EscapeForErrorMessage(name)} and value {EscapeForErrorMessage(value)}");
        if (target == EnvironmentVariableTarget.Process)
        {
            if (value is null) _environment.TryRemove(name, out _);
            else _environment[name] = value;
            return;
        }
        if (target is not (EnvironmentVariableTarget.User or EnvironmentVariableTarget.Machine))
            throw new ArgumentOutOfRangeException(nameof(target));
        if (!FileSystem.AllowWrites) throw new UnauthorizedAccessException("Registry writes are disabled");
        try { RegistryStore.Get(Runtime, FileSystem.Root).SetEnvironment(name, value, target); }
        catch (Exception ex)
        {
            Trace.Error($"could not persist virtual environment variable '{name}': {ex}", Id);
            throw;
        }
    }
    public bool TryGetEnvironmentVariable(string name, out string value) => _environment.TryGetValue(name, out value!);

    internal void ReplaceEnvironment(IReadOnlyDictionary<string, string> values) =>
        _environment = new(values, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, string> GetEnvironmentVariables(EnvironmentVariableTarget target = EnvironmentVariableTarget.Process) =>
        target == EnvironmentVariableTarget.Process ? new Dictionary<string, string>(_environment, StringComparer.OrdinalIgnoreCase)
            : RegistryStore.Get(Runtime, FileSystem.Root).ReadEnvironment(target)
                .ToDictionary(p => p.Key, p => p.Value.Value, StringComparer.OrdinalIgnoreCase);

    public string ExpandEnvironmentVariables(string value) => VirtualEnvironment.Expand(value, _environment);

    /// <summary>Copy a UTF-16 string into guest memory and return its address (cached for the command line).</summary>
    public ulong WriteGuestString(string value, bool unicode = true)
    {
        var bytes = unicode ? System.Text.Encoding.Unicode.GetBytes(value + "\0") : System.Text.Encoding.ASCII.GetBytes(value + "\0");
        var status = Memory.TryAllocate(0, (ulong)bytes.Length, MemoryProtection.ReadWrite,
            AllocationType.Commit | AllocationType.Reserve, out var address, out _, "guest string");
        if (status != Nt.NtStatus.Success)
            throw new InvalidOperationException($"cannot allocate guest string: {Nt.NtStatus.NameOf(status)}");
        Memory.WriteBytes(address, bytes);
        return address;
    }

    public ulong GetCommandLineBuffer()
    {
        if (_commandLineBuffer == 0) _commandLineBuffer = WriteGuestString(CommandLine);
        return _commandLineBuffer;
    }

    public ImaginedThread? FindThreadByVirtualId(int id)
    {
        lock (_threadGate) return _threads.FirstOrDefault(t => t.Id == id);
    }

    internal void RegisterThreadHandle(ImaginedThread thread, uint access, out ulong handle)
    {
        var obj = new ThreadObject(thread);
        handle = Handles.Create(obj, access);
        obj.Release();
    }

    private void InitializeStandardHandles()
    {
        var stdin = new ConsoleFileObject("\\Device\\ConDrv\\Input", _options.StandardInput, canRead: true, canWrite: false);
        var stdout = new ConsoleFileObject("\\Device\\ConDrv\\Output", _options.StandardOutput, canRead: false, canWrite: true);
        var stderr = new ConsoleFileObject("\\Device\\ConDrv\\Error", _options.StandardError, canRead: false, canWrite: true);
        Handles.Seed(0, stdin, 0x0012_019F, inheritable: true);
        Handles.Seed(1, stdout, 0x0012_019F, inheritable: true);
        Handles.Seed(2, stderr, 0x0012_019F, inheritable: true);
        stdin.Release();
        stdout.Release();
        stderr.Release();
    }

    /// <summary>
    /// Copy the parent's inheritable handles into this process and apply
    /// STARTF_USESTDHANDLES.  Called before the child main thread is released.
    /// </summary>
    public void ApplyStartupHandles(ImaginedProcess? parent, bool inheritHandles, bool useStandardHandles,
        ulong standardInput, ulong standardOutput, ulong standardError)
    {
        if (parent is null) return;

        if (inheritHandles)
        {
            foreach (var entry in parent.Handles.Snapshot())
            {
                if (!entry.Inheritable) continue;
                Handles.Seed(entry.Handle, entry.Object, entry.GrantedAccess, inheritable: true);
            }
            Trace.Emit(TraceKind.Process, $"inherited {parent.Handles.Snapshot().Count(e => e.Inheritable)} handle(s) from process {parent.Id}", Id);
        }

        if (!useStandardHandles) return;
        SeedStandardHandle(0, parent, standardInput);
        SeedStandardHandle(1, parent, standardOutput);
        SeedStandardHandle(2, parent, standardError);
    }

    private void SeedStandardHandle(ulong childHandle, ImaginedProcess parent, ulong parentHandle)
    {
        if (parentHandle == 0 || parentHandle == unchecked((ulong)(-1)) || parentHandle == unchecked((ulong)(-2))) return;
        if (!parent.Handles.TryGet(parentHandle, out var obj) || obj is null)
        {
            Trace.Warn($"STARTF_USESTDHANDLES: parent handle 0x{parentHandle:X} is not valid; keeping the default", Id);
            return;
        }
        Handles.Seed(childHandle, obj, 0x0012_019F, inheritable: true);
    }

    public PeMappedImage LoadImage(string path, ulong forcedBase = 0)
    {
        lock (_moduleGate)
        {
            if (Image is not null) throw new InvalidOperationException("this imagined process already has a main image");
            var image = PeImage.Load(path);
            if (image.IsDll) throw new BadImageFormatException($"{image.FileName}: a DLL cannot be used as a process image");
            Trace.Emit(TraceKind.Image, $"parsing {image}", Id);
            _libraryLoadDepth++;
            _loadTransaction = new();
            try
            {
                var loader = new PeLoader(Memory, import => ResolveImageImport(import, image.Path),
                    Trace, _options.ApplySectionProtections);
                Image = loader.Load(image, forcedBase, mapped => Image = mapped);
                _failFastInterceptors.Add(Image, new GuestFailFastInterceptor(this, Image));
                lock (_threadGate) _syscallInterceptor = Runtime.InstallSyscallInterceptor(this, Image);
                return Image;
            }
            catch (Exception ex)
            {
                Trace.Error($"loading guest image '{path}' failed: {ex}", Id);
                if (Image is not null && _failFastInterceptors.Remove(Image, out var traps)) traps.Dispose();
                Image = null;
                RollbackLibraries();
                throw;
            }
            finally
            {
                _libraryLoadDepth--;
                _loadTransaction = null;
            }
        }
    }

    /// <summary>Create (but do not start) the main guest thread.  Idempotent.</summary>
    public ImaginedThread EnsureMainThread(bool suspended = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Image is null) throw new InvalidOperationException("no image has been loaded");
        lock (_threadGate)
        {
            if (_mainThread is not null) return _mainThread;
        }
        return CreateThreadAt(Image.EntryPoint, 0, isMain: true, name: "main", suspended: suspended);
    }

    public bool StartMainThread()
    {
        var main = MainThread;
        return main is not null && main.Start();
    }

    /// <summary>Create the main guest thread and wait for it to finish.</summary>
    public ProcessExitInfo Run(TimeSpan? timeout = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Image is null) throw new InvalidOperationException("no image has been loaded");
        if (_started) throw new InvalidOperationException("the imagined process has already run");
        _started = true;
        _startTime = DateTimeOffset.UtcNow;

        Runtime.Trace.Emit(TraceKind.Process, $"starting main thread at 0x{Image.EntryPoint:X16}", Id);
        var main = MainThread ?? CreateThreadAt(Image.EntryPoint, 0, isMain: true, name: "main");
        if (main.State == ThreadState.Created && !main.Start())
        {
            ExitCode ??= -1;
            SignalExit(ExitCode.Value);
            Trace.Error($"main thread for process {Id} could not be started", Id);
            return new ProcessExitInfo(ExitCode.Value, false, DateTimeOffset.UtcNow - _startTime);
        }

        var effectiveTimeout = timeout ?? _options.ExecutionTimeout;
        if (!main.Wait(effectiveTimeout, out var nativeExitCode))
        {
            Runtime.Trace.Error($"process {Id} timed out after {effectiveTimeout.TotalSeconds:0.##}s; terminating", Id);
            ExitCode ??= 124; // conventional timeout exit code, distinct from guest statuses
            SignalExit(ExitCode.Value);
            main.Terminate(ExitCode.Value);
            if (!main.Wait(TimeSpan.FromSeconds(2), out _))
                Runtime.Trace.Warn($"timed-out main thread {main.Id} did not stop cooperatively", Id);
            TerminateOtherThreads(main);
            Runtime.Trace.Flush();
            return new ProcessExitInfo(ExitCode.Value, true, DateTimeOffset.UtcNow - _startTime);
        }

        ExitCode ??= (int)nativeExitCode;
        SignalExit(ExitCode.Value);
        TerminateOtherThreads(main);
        Runtime.Trace.Emit(TraceKind.Process,
            $"process {Id} exited with code {ExitCode} after {(DateTimeOffset.UtcNow - _startTime).TotalMilliseconds:0}ms", Id);
        Runtime.Trace.Flush();
        return new ProcessExitInfo(ExitCode.Value, false, DateTimeOffset.UtcNow - _startTime);
    }

    internal void OnMainThreadExited(ImaginedThread thread)
    {
        var code = thread.ReadExitCode() ?? 0;
        lock (_exitGate) ExitCode ??= code;
        SignalExit(ExitCode ?? code);
        TerminateOtherThreads(thread);
        Runtime.Trace.Emit(TraceKind.Process,
            $"process {Id} exit signalled by main thread {thread.Id} (code {ExitCode})", Id, thread.Id);
    }

    private void TerminateOtherThreads(ImaginedThread? except)
    {
        foreach (var thread in Threads)
        {
            if (ReferenceEquals(thread, except)) continue;
            if (thread.State is ThreadState.Exited or ThreadState.Terminated) continue;
            if (FaultExitCode is int fault) thread.RequestTermination(fault);
            else thread.Terminate(ExitCode ?? 0);
        }
    }

    public ImaginedThread CreateThreadAt(ulong startAddress, ulong parameter, bool isMain = false, string name = "guest-thread", bool suspended = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (startAddress == 0) throw new ArgumentException("start address is zero");

        var thread = new ImaginedThread(this, Runtime.NextThreadId(), 0, IntPtr.Zero,
            startAddress, parameter, isMain, name, managedHosted: true);
        lock (_threadGate)
        {
            _threads.Add(thread);
            if (isMain) _mainThread = thread;
        }

        Trace.Emit(TraceKind.Thread,
            $"created managed thread {thread.Id} start=0x{startAddress:X16} param=0x{parameter:X16} main={isMain} suspended={suspended}", Id, thread.Id);

        if (!suspended && !thread.Start())
        {
            Trace.Error($"managed thread {thread.Id} could not be started", Id, thread.Id);
            thread.Terminate(-1);
            throw new InvalidOperationException($"guest thread at 0x{startAddress:X} could not be started");
        }

        return thread;
    }

    internal void AttachThread(ImaginedThread thread, uint nativeId)
    {
        thread.NativeId = nativeId;
        lock (_threadGate) _threadsByNativeId[nativeId] = thread;
        Runtime.RegisterThread(thread);
    }

    internal void DetachThread(ImaginedThread thread)
    {
        lock (_threadGate)
        {
            if (thread.NativeId != 0 &&
                _threadsByNativeId.TryGetValue(thread.NativeId, out var current) &&
                ReferenceEquals(current, thread))
            {
                _threadsByNativeId.Remove(thread.NativeId);
            }
        }
        Runtime.UnregisterThread(thread);
    }

    public ImaginedThread? FindThreadByNativeId(uint nativeId)
    {
        lock (_threadGate) return _threadsByNativeId.TryGetValue(nativeId, out var t) ? t : null;
    }

    public ImaginedThread GetOrRegisterHostThread(uint nativeId)
    {
        lock (_threadGate)
        {
            if (_threadsByNativeId.TryGetValue(nativeId, out var existing)) return existing;
            var thread = new ImaginedThread(this, Runtime.NextThreadId(), nativeId, IntPtr.Zero, 0, 0, false, "host-callback");
            _threads.Add(thread);
            _threadsByNativeId[nativeId] = thread;
            Runtime.RegisterThread(thread);
            return thread;
        }
    }

    /// <summary>Wait until the process has signalled its exit event.</summary>
    public bool WaitForExit(int milliseconds)
    {
        lock (_exitGate)
        {
            if (_terminating) return true;
            if (milliseconds == Timeout.Infinite)
            {
                while (!_terminating) Monitor.Wait(_exitGate);
                return true;
            }

            var deadline = Environment.TickCount64 + Math.Max(0, milliseconds);
            while (!_terminating)
            {
                var remaining = deadline - Environment.TickCount64;
                if (remaining <= 0) return false;
                Monitor.Wait(_exitGate, (int)Math.Min(remaining, int.MaxValue));
            }
            return true;
        }
    }

    private void SignalExit(int exitCode)
    {
        lock (_exitGate)
        {
            ExitCode ??= exitCode;
            _terminating = true;
            Monitor.PulseAll(_exitGate);
        }
    }

    internal void FailFast(uint code, ulong address, string source, uint status = NtStatus.StackBufferOverrun)
    {
        lock (_exitGate)
        {
            if (_faultExitCode is not null) return;
            _faultExitCode = unchecked((int)status);
            ExitCode = _faultExitCode;
            _terminating = true;
            Monitor.PulseAll(_exitGate);
        }
        foreach (var thread in Threads) thread.RequestTermination(unchecked((int)status));
        Trace.Error($"guest fail-fast: code={code} status=0x{status:X8} at 0x{address:X16} ({source}); stopping virtual process",
            Id, Runtime.GetCurrentThread()?.Id ?? 0);

        var message = code switch
        {
            0 => "a legacy GS security check failed",
            1 => "a VTGuard virtual call check failed",
            2 => "a stack cookie check failed",
            3 => "a doubly linked list entry was corrupted",
            4 => "the stack is in an incorrect state",
            5 => "an invalid argument was detected",
            6 => "the GS security cookie could not be initialized",
            7 => "the application reported a fatal error",
            8 => "a range check failed",
            9 => "unsafe registry access was detected",
            10 => "a guarded indirect call check failed",
            11 => "a guarded write check failed",
            12 => "an invalid fiber switch was requested",
            13 => "an invalid CPU context was supplied",
            14 => "an invalid reference count was detected",
            18 => "an invalid jump buffer was detected",
            19 => "read-only module data was modified",
            20 => "a module certification check failed",
            21 => "an invalid exception chain was detected",
            22 => "a cryptographic library failure occurred",
            23 => "an invalid call occurred during a DLL callout",
            24 => "the image was loaded at an invalid base address",
            25 => "a delay-load protection check failed",
            26 => "an unsafe extension call was detected",
            27 => "a deprecated service was invoked",
            28 => "an invalid buffer access was detected",
            29 => "a balanced tree was corrupted",
            30 => "an invalid next-thread pointer was detected",
            31 => "a guarded indirect call check was suppressed",
            32 => "APCs were unexpectedly disabled",
            33 => "an invalid idle state was detected",
            34 => "read-only module data protection failed",
            35 => "an unexpected heap exception occurred",
            36 => "an invalid lock state was detected",
            37 => "a guarded jump-table check failed",
            38 => "an invalid long-jump target was detected",
            39 => "an invalid dispatch context was detected",
            40 => "an invalid thread was detected",
            41 => "an invalid system-call number was used",
            42 => "an invalid file operation was requested",
            43 => "access to a low-privilege app container was denied",
            44 => "a guard shadow-stack check failed",
            45 => "loader continuity validation failed",
            46 => "guard export suppression validation failed",
            47 => "the control stack is invalid",
            48 => "setting the thread context was denied",
            49 => "the import address table is invalid",
            50 => "heap metadata was corrupted",
            51 => "a payload restriction was violated",
            52 => "low-integrity access was denied",
            53 => "an enclave call failed",
            54 => "an unhandled lightweight syscall exception occurred",
            55 => "adminless access was denied",
            56 => "an unexpected call was made",
            57 => "an invalid control return address was detected",
            58 => "the host behaved unexpectedly",
            59 => "processor flags were corrupted",
            60 => "a vectored exception handler was corrupted",
            61 => "event tracing state was corrupted",
            62 => "a Registered I/O operation was aborted",
            63 => "an invalid page frame number was detected",
            64 => "an XFG guarded indirect call check failed",
            65 => "a cast guard check failed",
            66 => "a host visibility change was detected",
            67 => "a kernel CET shadow-stack operation failed",
            68 => "a patch callback failed",
            69 => "a patch to NTDLL failed",
            70 => "invalid fiber-local storage data was detected",
            71 => "AddressSanitizer detected an error",
            72 => "a CLR ahead-of-time exception occurred",
            73 => "a pointer authentication return-address check failed",
            74 => "an invalid thread state was detected",
            75 => "the WOW64 state was corrupted",
            76 => "invalid extended processor state was detected",
            77 => "a kernel pointer was expected but not supplied",
            int.MaxValue => "ImaginedNT Specific failfast code",
            _ => $"a fatal error has occurred (code={code})"
        };

        if (IsRoot)
        {
            if(_options.WrapFailFast) Environment.FailFast($"The root process has called fail-fast because '{message}' (status=0x{status:X8} at 0x{address:X16}, from {source})");
            Console.Error.WriteLine($"ImaginedNT: Root process {Path.GetFileName(ImagePath) ?? "<unknown>"} called a fail-fast because '{message}' (status=0x{status:X8} at 0x{address:X16}, from {source})");
            Environment.Exit(unchecked((int)status));
        }

        _options.StandardError.Write(System.Text.Encoding.UTF8.GetBytes(
            $"ImaginedNT: Guest process {Path.GetFileName(ImagePath) ?? "<unknown>"} called a fail-fast because '{message}' (status=0x{status:X8} at 0x{address:X16}, from {source})\r\n"));
    }

    /// <summary>Terminate the process from the outside (TerminateProcess on a child handle).</summary>
    public void Terminate(int exitCode)
    {
        SignalExit(exitCode);
        Trace.Emit(TraceKind.Process, $"terminating process {Id} with code {exitCode}", Id);
        foreach (var thread in Threads)
        {
            if (thread.State is ThreadState.Exited or ThreadState.Terminated) continue;
            if (FaultExitCode is int fault) thread.RequestTermination(fault);
            else thread.Terminate(exitCode);
        }
    }

    /// <summary>Called by ExitProcess.  Root processes follow the configured termination policy.</summary>
    public int RequestProcessExit(int exitCode, ApiCallContext? context = null)
    {
        lock (_exitGate) ExitCode ??= exitCode;
        var code = ExitCode ?? exitCode;
        var currentNativeId = Native.NativeMethods.GetCurrentThreadId();
        var current = FindThreadByNativeId(currentNativeId);
        var canRedirect = context is not null && context.CanRedirectThreadExit;

        if (_terminating)
        {
            // Re-entrant ExitProcess (for example a cleanup callback): keep the original code.
            if (IsRoot && _options.TerminationMode == ProcessTerminationMode.ReturnToHost) return code;
            if (canRedirect)
            {
                context!.PendingThreadExitCode = code;
                return code;
            }
            if (!IsRoot) Native.NativeMethods.ExitThread(unchecked((uint)code));
            return code;
        }

        Trace.Emit(TraceKind.Process, $"ExitProcess({code}) requested", Id, current?.Id ?? 0);
        var returnToHostRoot = IsRoot && _options.TerminationMode == ProcessTerminationMode.ReturnToHost;
        if (!returnToHostRoot) current?.MarkExited(code, ThreadState.Exited);
        SignalExit(code);
        if (!IsRoot || _options.TerminationMode == ProcessTerminationMode.HostExit)
            TerminateOtherThreads(current);

        if (IsRoot)
        {
            switch (_options.TerminationMode)
            {
                case ProcessTerminationMode.HostExit:
                    Trace.Flush();
                    Environment.Exit(code);
                    return code;
                case ProcessTerminationMode.ReturnToHost:
                    // Test-only mode: return to the guest so it can run its own epilogue.  The
                    // thread stays registered until the managed wrapper completes.
                    return code;
                case ProcessTerminationMode.ThreadExit:
                    if (canRedirect)
                    {
                        context!.PendingThreadExitCode = code;
                        return code;
                    }
                    Native.NativeMethods.ExitThread(unchecked((uint)code));
                    return code;
            }
        }

        // An imagined child process must never terminate the host.  Prefer unwinding through the
        // guest's own return address to the unmanaged exit trampoline; that avoids calling
        // ExitThread from inside a managed dispatch frame.
        if (canRedirect)
        {
            context!.PendingThreadExitCode = code;
            return code;
        }
        Native.NativeMethods.ExitThread(unchecked((uint)code));
        return code;
    }

    /// <summary>Called by ExitThread.  Non-main threads leave through the guest's own stack.</summary>
    public uint ExitCurrentThread(int exitCode, ApiCallContext? context = null)
    {
        var nativeId = Native.NativeMethods.GetCurrentThreadId();
        var thread = FindThreadByNativeId(nativeId) ?? GetOrRegisterHostThread(nativeId);
        Trace.Emit(TraceKind.Thread, $"ExitThread({exitCode})", Id, thread.Id);

        if (thread.IsMain)
        {
            RequestProcessExit(exitCode, context);
            // The request either redirected the guest return address, exited the host/thread, or
            // returned to the guest in test-only ReturnToHost mode.
            return 0;
        }

        thread.MarkExited(exitCode, ThreadState.Exited);
        if (context is not null && context.CanRedirectThreadExit)
        {
            context.PendingThreadExitCode = exitCode;
            return 0;
        }

        // Fallback when no guest return-address slot is available (for example a direct syscall
        // path): stop the host thread immediately.
        Native.NativeMethods.ExitThread(unchecked((uint)exitCode));
        return 0;
    }

    public async Task<ProcessExitInfo> RunAsync(TimeSpan? timeout = null) => await Task.Run(() => Run(timeout));

    public event Action? Disposing;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_exitGate)
        {
            ExitCode ??= 0;
            _terminating = true;
            Monitor.PulseAll(_exitGate);
        }

        var currentNativeId = Native.NativeMethods.GetCurrentThreadId();
        foreach (var thread in Threads)
        {
            try
            {
                if (thread.NativeId == currentNativeId) continue; // never tear down the caller
                if (thread.State is ThreadState.Running or ThreadState.Created)
                {
                    if (FaultExitCode is int fault) thread.RequestTermination(fault);
                    else thread.Terminate(ExitCode ?? 0);
                }
                // Never free the address space while a managed guest thread can still touch it.
                if (!thread.Wait(TimeSpan.FromSeconds(2), out _)) continue;
                thread.Dispose();
            }
            catch { }
        }

        if (HasRunningThreads)
        {
            Trace.Warn($"process {Id} still has running guest threads; retaining its resources", Id);
            return;
        }
        if (Disposing is { } disposing)
        {
            Disposing = null;
            foreach (Action dispose in disposing.GetInvocationList())
                try { dispose(); }
                catch (Exception ex) { Trace.Warn($"process {Id} resource cleanup: {ex}", Id); }
        }
        try { _syscallInterceptor?.Dispose(); } catch { }
        _syscallInterceptor = null;
        foreach (var redirects in _exportRedirectors.Values) redirects.Dispose();
        _exportRedirectors.Clear();
        foreach (var traps in _failFastInterceptors.Values) traps.Dispose();
        _failFastInterceptors.Clear();
        try { Handles.Dispose(); } catch { }
        // The object namespace is session-wide and owned by the runtime.
        lock (_heapGate)
        {
            foreach (var heap in _heaps) heap.Dispose();
            _heaps.Clear();
        }
        try { Memory.Dispose(); } catch { }
        try { _job?.Dispose(); } catch { }
        Trace.Emit(TraceKind.Process, $"process {Id} disposed", Id);
    }

    public override string ToString() => $"ImaginedProcess {Id} {ImagePath}";
}
