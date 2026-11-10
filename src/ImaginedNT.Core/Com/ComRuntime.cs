using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ImaginedNT.Core.Diagnostics;
using ImaginedNT.Core.Native;
using ImaginedNT.Core.Runtime;
using ImaginedNT.Core.Thunks;

namespace ImaginedNT.Core.Com;

/// <summary>
/// The managed COM subsystem owned by an <see cref="ImaginedRuntime"/>. It provides:
/// <list type="bullet">
/// <item>apartment bookkeeping for <c>CoInitializeEx</c>/<c>CoUninitialize</c>,</item>
/// <item>managed class factories and CLSID registration (fully virtual COM),</item>
/// <item>guest-visible interface pointers and vtables whose entries dispatch back into C#,</item>
/// <item>partially imagined classes whose non-overridden slots jump to a real host COM object,</item>
/// <item>in-process server loading/unloading and guest <c>CoRegisterClassObject</c> factories.</item>
/// </list>
/// </summary>
public sealed unsafe class ComRuntime : IDisposable
{
    private sealed class ComThreadState
    {
        public object Gate { get; } = new();
        public ComApartment Apartment;
        public int InitCount;
    }

    private readonly object _gate = new();
    private readonly Dictionary<Guid, ComClassDescriptor> _classes = new();
    private readonly Dictionary<Guid, ComInterfaceDefinition> _interfaces = new();
    private readonly Dictionary<string, Guid> _interfaceNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, Func<Guid, ulong>> _hostFactories = new();
    private readonly List<ComServerRegistration> _servers = new();
    private readonly Dictionary<(int ProcessId, string Path), ComServerLoadState> _serverStates = new();
    private readonly List<GuestClassRegistration> _guestClasses = new();
    private readonly ConcurrentDictionary<ulong, ComInterfaceView> _views = new();
    private readonly Dictionary<(ComClassDescriptor Class, ComMethodBinding Binding), ulong> _methodThunks = new();
    private readonly Dictionary<(ComClassDescriptor Class, ComInterfaceDefinition Interface), ulong> _sharedVtables = new();
    private readonly List<ulong> _sharedVtableAllocations = new();
    private readonly Dictionary<ComClassDescriptor, ComClassDescriptor> _classFactoryDescriptors = new();
    private readonly Dictionary<int, HashSet<ComObject>> _objectsByProcess = new();
    private readonly ConditionalWeakTable<ImaginedThread, ComThreadState> _threadStates = new();
    private readonly ulong[] _unknownThunks = new ulong[3];
    private ulong _notImplementedThunk;
    private uint _nextCookie;
    private bool _disposed;

    public ComRuntime(ImaginedRuntime runtime)
    {
        Runtime = runtime;
        RegisterInterface(ComBuiltins.ClassFactory);
        RegisterInterface(ComBuiltins.Malloc);
    }

    public ImaginedRuntime Runtime { get; }

    public bool IsDisposed => _disposed;

    public IReadOnlyList<ComClassDescriptor> Classes
    {
        get { lock (_gate) return _classes.Values.ToArray(); }
    }

    public IReadOnlyList<ComServerRegistration> Servers
    {
        get { lock (_gate) return _servers.ToArray(); }
    }

    public int LiveObjectCount
    {
        get
        {
            lock (_gate)
            {
                var count = 0;
                foreach (var set in _objectsByProcess.Values) count += set.Count;
                return count;
            }
        }
    }

    // ---------------------------------------------------------------------------
    // Registration
    // ---------------------------------------------------------------------------

    public void RegisterInterface(ComInterfaceDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ValidateInterface(definition);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_interfaces.TryGetValue(definition.InterfaceId, out var existing))
            {
                if (!SameInterface(existing, definition))
                    throw new InvalidOperationException(
                        $"duplicate COM interface registration: {definition.InterfaceId:B} ({existing.Name} vs {definition.Name})");
                return;
            }
            _interfaces[definition.InterfaceId] = definition;
            if (!string.IsNullOrWhiteSpace(definition.Name)) _interfaceNames[definition.Name] = definition.InterfaceId;
        }
    }

    private static void ValidateInterface(ComInterfaceDefinition definition)
    {
        if (definition.Methods.Count == 0) return;
        var max = definition.Methods.Keys.Max();
        for (var slot = 3; slot <= max; slot++)
        {
            if (!definition.Methods.ContainsKey(slot))
                throw new InvalidOperationException(
                    $"COM interface {definition.Name} ({definition.InterfaceId:B}): vtable slots must be " +
                    $"contiguous from 3; slot {slot} is missing");
        }
    }

    private static bool SameInterface(ComInterfaceDefinition left, ComInterfaceDefinition right)
    {
        if (!string.Equals(left.Name, right.Name, StringComparison.Ordinal) ||
            left.Methods.Count != right.Methods.Count) return false;
        foreach (var (slot, name) in left.Methods)
        {
            if (!right.Methods.TryGetValue(slot, out var other) ||
                !string.Equals(name, other, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    public bool TryGetInterface(Guid interfaceId, out ComInterfaceDefinition? definition)
    {
        lock (_gate) return _interfaces.TryGetValue(interfaceId, out definition);
    }

    public ComInterfaceDefinition GetInterface(Guid interfaceId)
    {
        lock (_gate)
        {
            return _interfaces.TryGetValue(interfaceId, out var definition)
                ? definition
                : throw new InvalidOperationException($"COM interface is not registered: {interfaceId:B}");
        }
    }

    /// <summary>Resolve a GUID string or a symbolic interface name declared by <c>VirtualComInterface</c>.</summary>
    public Guid ResolveInterfaceId(string interfaceId)
    {
        if (Guid.TryParse(interfaceId, out var guid)) return guid;
        lock (_gate)
        {
            if (_interfaceNames.TryGetValue(interfaceId, out guid)) return guid;
        }
        throw new InvalidOperationException($"unknown COM interface id/name: '{interfaceId}' (declare it with [VirtualComInterface])");
    }

    public void RegisterClass(ComClassDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.Interfaces.Count == 0)
            throw new InvalidOperationException($"COM class {descriptor.Name} does not declare any interfaces");
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_classes.TryGetValue(descriptor.ClassId, out var existing))
            {
                if (ReferenceEquals(existing, descriptor)) return;
                throw new InvalidOperationException(
                    $"duplicate COM class registration: {descriptor.ClassId:B} ({existing.Name} vs {descriptor.Name})");
            }
            _classes[descriptor.ClassId] = descriptor;
        }
    }

    public ComClassDescriptor? FindClass(Guid classId)
    {
        lock (_gate) return _classes.GetValueOrDefault(classId);
    }

    /// <summary>
    /// Register a host factory used by partially imagined classes. The callback receives the
    /// interface IID being requested and returns a real host COM interface pointer (already
    /// reference counted) or 0 when the host object does not support that interface.
    /// </summary>
    public void RegisterHostClass(Guid hostClassId, Func<Guid, ulong> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _hostFactories[hostClassId] = factory;
        }
    }

    public void RegisterServer(string path)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_servers.Any(s => string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase))) return;
            _servers.Add(new ComServerRegistration(path));
        }
    }

    // ---------------------------------------------------------------------------
    // Apartments
    // ---------------------------------------------------------------------------

    public int Initialize(ImaginedThread thread, uint flags)
    {
        var state = _threadStates.GetOrCreateValue(thread);
        var apartment = (flags & 0x2) != 0 ? ComApartment.Sta : ComApartment.Mta;
        lock (state.Gate)
        {
            if (state.InitCount == 0)
            {
                state.Apartment = apartment;
                state.InitCount = 1;
                Runtime.Trace.Emit(TraceKind.Api, $"CoInitializeEx({apartment})", thread.Process.Id, thread.Id);
                return ComHResult.S_OK;
            }
            if (state.Apartment == apartment)
            {
                state.InitCount++;
                return ComHResult.S_FALSE;
            }
            return ComHResult.RPC_E_CHANGED_MODE;
        }
    }

    public void Uninitialize(ImaginedThread thread)
    {
        if (!_threadStates.TryGetValue(thread, out var state)) return;
        lock (state.Gate)
        {
            if (state.InitCount == 0) return;
            state.InitCount--;
            if (state.InitCount == 0) state.Apartment = ComApartment.None;
        }
    }

    public ComApartment GetApartment(ImaginedThread thread)
    {
        if (!_threadStates.TryGetValue(thread, out var state)) return ComApartment.None;
        lock (state.Gate) return state.Apartment;
    }

    private bool CanUseHostCom =>
        Runtime.Options.AllowNativeForwarding && Runtime.Options.ImportPolicy != ImportPolicy.ManagedOnly;

    // ---------------------------------------------------------------------------
    // Object creation
    // ---------------------------------------------------------------------------

    /// <summary>Implements <c>CoCreateInstance</c> for managed classes, guest factories, servers and the host.</summary>
    public int CreateInstance(ImaginedThread thread, Guid classId, ulong outer, uint context, Guid interfaceId,
        out ulong pointer)
    {
        pointer = 0;
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (GetApartment(thread) == ComApartment.None) return ComHResult.CO_E_NOTINITIALIZED;
        if (outer != 0) return ComHResult.CLASS_E_NOAGGREGATION;

        ComClassDescriptor? descriptor;
        lock (_gate) _classes.TryGetValue(classId, out descriptor);
        if (descriptor is not null) return CreateManagedInstance(thread, descriptor, interfaceId, out pointer);

        GuestClassRegistration? guest;
        lock (_gate)
        {
            guest = _guestClasses.FirstOrDefault(g => g.ClassId == classId && g.ProcessId == thread.Process.Id);
        }
        if (guest is not null) return CreateFromGuest(guest, interfaceId, out pointer);

        var serverHr = TryCreateFromServers(thread, classId, interfaceId, out pointer);
        if (serverHr != ComHResult.REGDB_E_CLASSNOTREG) return serverHr;

        if (CanUseHostCom)
        {
            var hostHr = ComHostInterop.CreateInstance(classId, context, interfaceId, out var hostPointer);
            if (ComHResult.Succeeded(hostHr))
            {
                pointer = (ulong)(long)hostPointer;
                Runtime.Trace.Emit(TraceKind.Api,
                    $"COM created host object class={classId:B} iid={interfaceId:B} -> 0x{pointer:X16}",
                    thread.Process.Id, thread.Id);
            }
            return hostHr;
        }

        return ComHResult.REGDB_E_CLASSNOTREG;
    }

    public int CreateManagedObject(ImaginedThread thread, Guid classId, Guid interfaceId, object instance, out ulong pointer)
    {
        pointer = 0;
        var descriptor = FindClass(classId);
        if (descriptor is null || descriptor.IsPartiallyImagined) return ComHResult.CLASS_E_CLASSNOTAVAILABLE;
        return CreateManagedInstance(thread, descriptor, interfaceId, out pointer, instance);
    }

    public ComObject? FindObject(ulong pointer) => _views.TryGetValue(pointer, out var view) ? view.Object : null;

    private int CreateManagedInstance(ImaginedThread thread, ComClassDescriptor descriptor, Guid interfaceId,
        out ulong pointer, object? instance = null)
    {
        pointer = 0;
        if (descriptor.Factory is null)
        {
            Runtime.Trace.Error($"COM class {descriptor.Name} has no factory", thread.Process.Id, thread.Id);
            return ComHResult.E_FAIL;
        }
        if (descriptor.IsPartiallyImagined && !CanUseHostCom)
        {
            Runtime.Trace.Warn($"partially imagined COM class {descriptor.Name} requires host COM forwarding",
                thread.Process.Id, thread.Id);
            return ComHResult.CO_E_NOTSUPPORTED;
        }

        try
        {
            instance ??= descriptor.Factory();
        }
        catch (Exception ex)
        {
            Runtime.Trace.Error($"COM factory for {descriptor.Name} threw {ex.GetType().Name}: {ex.Message}",
                thread.Process.Id, thread.Id);
            return ComHResult.E_FAIL;
        }

        ComObject? obj = null;
        try
        {
            obj = new ComObject(this, thread.Process, descriptor, instance);
            obj.BuildHandlers();
            AddObject(obj);

            if (descriptor.IsPartiallyImagined)
            {
                var hostHr = AttachHostInterfaces(obj, descriptor, thread);
                if (ComHResult.Failed(hostHr))
                {
                    obj.Release();
                    return hostHr;
                }
            }

            BuildViews(obj);
            var view = ResolveRequestedView(obj, interfaceId);
            if (view is null)
            {
                obj.Release();
                return ComHResult.E_NOINTERFACE;
            }
            pointer = view.GuestPointer;
            Runtime.Trace.Emit(TraceKind.Api,
                $"COM created {descriptor.Name} clsid={descriptor.ClassId:B} iid={interfaceId:B} " +
                $"ifaces={obj.Views.Count} ptr=0x{pointer:X16}" +
                (descriptor.IsPartiallyImagined ? $" hostClsid={descriptor.HostClassId:B} (partially imagined)" : ""),
                thread.Process.Id, thread.Id);
            return ComHResult.S_OK;
        }
        catch (Exception ex)
        {
            Runtime.Trace.Error($"COM creation of {descriptor.Name} failed: {ex.GetType().Name}: {ex.Message}",
                thread.Process.Id, thread.Id);
            obj?.Release();
            return ComHResult.E_FAIL;
        }
    }

    private static ComInterfaceView? ResolveRequestedView(ComObject obj, Guid interfaceId) =>
        interfaceId == ComBuiltins.IUnknownId ? obj.PrimaryView : obj.FindView(interfaceId);

    /// <summary>Acquire host interface pointers for every declared interface of a partially imagined class.</summary>
    private int AttachHostInterfaces(ComObject obj, ComClassDescriptor descriptor, ImaginedThread thread)
    {
        var hostClassId = descriptor.HostClassId!.Value;
        Func<Guid, ulong>? factory = null;
        lock (_gate) _hostFactories.TryGetValue(hostClassId, out factory);

        if (factory is not null)
        {
            foreach (var iface in descriptor.Interfaces)
            {
                var iid = iface.Definition.InterfaceId;
                ulong hostPointer;
                try { hostPointer = factory(iid); }
                catch (Exception ex)
                {
                    Runtime.Trace.Error($"host COM factory for {hostClassId:B} threw {ex.GetType().Name}: {ex.Message}",
                        thread.Process.Id, thread.Id);
                    hostPointer = 0;
                }
                if (hostPointer == 0)
                {
                    Runtime.Trace.Warn($"host COM factory has no interface {iface.Definition.Name} ({iid:B})",
                        thread.Process.Id, thread.Id);
                    continue;
                }
                obj.SetHostInterface(iid, hostPointer);
                obj.AddHostPointer(hostPointer);
                Runtime.Trace.Emit(TraceKind.Api,
                    $"partially imagined COM host interface {iid:B} -> 0x{hostPointer:X16}",
                    thread.Process.Id, thread.Id);
            }
            return ComHResult.S_OK;
        }

        var hr = ComHostInterop.CreateInstance(hostClassId, (uint)ComClassContext.InprocServer,
            ComBuiltins.IUnknownId, out var unknown);
        if (ComHResult.Failed(hr) || unknown == IntPtr.Zero)
        {
            Runtime.Trace.Warn($"could not create host COM object {hostClassId:B} (hr=0x{hr:X8})",
                thread.Process.Id, thread.Id);
            return hr;
        }
        obj.AddHostPointer((ulong)(long)unknown);

        foreach (var iface in descriptor.Interfaces)
        {
            var iid = iface.Definition.InterfaceId;
            var queryHr = Marshal.QueryInterface(unknown, ref iid, out var hostPointer);
            if (ComHResult.Failed(queryHr) || hostPointer == IntPtr.Zero)
            {
                Runtime.Trace.Warn(
                    $"host COM object {hostClassId:B} does not expose {iface.Definition.Name} ({iid:B}); " +
                    "non-overridden slots will return E_NOTIMPL",
                    thread.Process.Id, thread.Id);
                continue;
            }
            obj.SetHostInterface(iid, (ulong)(long)hostPointer);
            obj.AddHostPointer((ulong)(long)hostPointer);
        }
        return ComHResult.S_OK;
    }

    /// <summary>Implements <c>CoGetClassObject</c>.</summary>
    public int GetClassObject(ImaginedThread thread, Guid classId, uint context, Guid interfaceId, out ulong pointer)
    {
        pointer = 0;
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (GetApartment(thread) == ComApartment.None) return ComHResult.CO_E_NOTINITIALIZED;
        if (interfaceId != ComBuiltins.IClassFactoryId && interfaceId != ComBuiltins.IUnknownId)
            return ComHResult.E_NOINTERFACE;

        ComClassDescriptor? descriptor;
        lock (_gate) _classes.TryGetValue(classId, out descriptor);
        if (descriptor is not null)
        {
            var factory = CreateClassFactoryObject(thread, descriptor);
            var view = interfaceId == ComBuiltins.IUnknownId ? factory.PrimaryView : factory.FindView(interfaceId);
            if (view is null)
            {
                factory.Release();
                return ComHResult.E_NOINTERFACE;
            }
            pointer = view.GuestPointer;
            Runtime.Trace.Emit(TraceKind.Api,
                $"COM class factory for {descriptor.Name} clsid={classId:B} -> 0x{pointer:X16}",
                thread.Process.Id, thread.Id);
            return ComHResult.S_OK;
        }

        GuestClassRegistration? guest;
        lock (_gate)
        {
            guest = _guestClasses.FirstOrDefault(g => g.ClassId == classId && g.ProcessId == thread.Process.Id);
        }
        if (guest is not null) return QueryGuestFactory(guest, interfaceId, out pointer);

        var serverHr = TryGetClassObjectFromServers(thread, classId, interfaceId, out pointer);
        if (serverHr != ComHResult.REGDB_E_CLASSNOTREG) return serverHr;

        if (CanUseHostCom)
        {
            var hostHr = ComHostInterop.GetClassObject(classId, context, interfaceId, out var hostPointer);
            if (ComHResult.Succeeded(hostHr))
            {
                pointer = (ulong)(long)hostPointer;
                Runtime.Trace.Emit(TraceKind.Api,
                    $"COM host class object {classId:B} iid={interfaceId:B} -> 0x{pointer:X16}",
                    thread.Process.Id, thread.Id);
            }
            return hostHr;
        }

        return ComHResult.REGDB_E_CLASSNOTREG;
    }

    private ComObject CreateClassFactoryObject(ImaginedThread thread, ComClassDescriptor target)
    {
        var descriptor = GetClassFactoryDescriptor(target);
        var factory = new ComObject(this, thread.Process, descriptor, descriptor.Factory!());
        factory.BuildHandlers();
        AddObject(factory);
        BuildViews(factory);
        return factory;
    }

    private ComClassDescriptor GetClassFactoryDescriptor(ComClassDescriptor target)
    {
        lock (_gate)
        {
            if (_classFactoryDescriptors.TryGetValue(target, out var existing)) return existing;
            var createInfo = typeof(ManagedClassFactory).GetMethod(nameof(ManagedClassFactory.CreateInstance),
                BindingFlags.Public | BindingFlags.Instance)!;
            var lockInfo = typeof(ManagedClassFactory).GetMethod(nameof(ManagedClassFactory.LockServer),
                BindingFlags.Public | BindingFlags.Instance)!;
            var interfaceBinding = new ComInterfaceBinding(ComBuiltins.ClassFactory,
                new Dictionary<int, ComMethodBinding>
                {
                    [3] = new(createInfo, 3, "CreateInstance"),
                    [4] = new(lockInfo, 4, "LockServer")
                });
            var descriptor = new ComClassDescriptor
            {
                ClassId = target.ClassId,
                Name = $"IClassFactory({target.Name})",
                ThreadingModel = ComThreadingModel.Both,
                Interfaces = [interfaceBinding],
                Factory = () => new ManagedClassFactory(this, target)
            };
            _classFactoryDescriptors[target] = descriptor;
            return descriptor;
        }
    }

    private void BuildViews(ComObject obj)
    {
        var primarySet = false;
        foreach (var interfaceBinding in obj.Descriptor.Interfaces)
        {
            var hostPointer = obj.Descriptor.IsPartiallyImagined
                ? obj.GetHostInterface(interfaceBinding.Definition.InterfaceId)
                : 0;
            var vtable = obj.Descriptor.IsPartiallyImagined
                ? BuildPartialVtable(obj, obj.Descriptor, interfaceBinding, hostPointer)
                : GetSharedVtable(obj.Descriptor, interfaceBinding);

            var block = Marshal.AllocHGlobal(16);
            Marshal.WriteInt64(block, (long)vtable);
            obj.AddInterfaceBlock((ulong)(long)block);
            var view = new ComInterfaceView
            {
                Object = obj,
                Definition = interfaceBinding.Definition,
                GuestPointer = (ulong)(long)block,
                VtablePointer = vtable,
                HostInterfacePointer = hostPointer,
                IsPrimary = !primarySet
            };
            primarySet = true;
            obj.AddView(view);
            _views[view.GuestPointer] = view;
        }
        if (!primarySet) throw new InvalidOperationException($"COM class {obj.Descriptor.Name} produced no interfaces");
    }

    private ulong BuildPartialVtable(ComObject obj, ComClassDescriptor descriptor, ComInterfaceBinding interfaceBinding,
        ulong hostPointer)
    {
        var definition = interfaceBinding.Definition;
        var slots = definition.MaxSlot + 1;
        var vtable = Marshal.AllocHGlobal(slots * sizeof(ulong));
        for (var slot = 0; slot < 3; slot++)
            Marshal.WriteInt64(vtable, slot * sizeof(ulong), (long)GetUnknownThunk(slot));

        var hostVtable = hostPointer == 0 ? 0 : obj.Process.Memory.Read<ulong>(hostPointer);
        Runtime.Trace.Emit(TraceKind.Api,
            $"partially imagined COM vtable {definition.Name}: hostThis=0x{hostPointer:X16} " +
            $"hostVtable=0x{hostVtable:X16} overrides={string.Join(',', interfaceBinding.Methods.Keys)}",
            obj.Process.Id);
        for (var slot = 3; slot <= definition.MaxSlot; slot++)
        {
            ulong entry;
            if (interfaceBinding.Methods.TryGetValue(slot, out var binding))
            {
                entry = GetMethodThunk(descriptor, binding);
            }
            else if (hostVtable != 0)
            {
                var target = obj.Process.Memory.Read<ulong>(hostVtable + (ulong)slot * sizeof(ulong));
                if (target == 0)
                {
                    entry = GetNotImplementedThunk();
                }
                else
                {
                    entry = Runtime.Thunks.RegisterEntryStub(X64StubEmitter.EmitComForwarder(hostPointer, target));
                    Runtime.Trace.Emit(TraceKind.Api,
                        $"COM forward {descriptor.Name}!{definition.Name} slot {slot} -> host 0x{target:X16} " +
                        $"this=0x{hostPointer:X16}",
                        obj.Process.Id);
                }
            }
            else
            {
                entry = GetNotImplementedThunk();
            }
            Marshal.WriteInt64(vtable, slot * sizeof(ulong), (long)entry);
        }
        obj.AddOwnedVtable((ulong)(long)vtable);
        return (ulong)(long)vtable;
    }

    private ulong GetSharedVtable(ComClassDescriptor descriptor, ComInterfaceBinding interfaceBinding)
    {
        lock (_gate)
        {
            var key = (descriptor, interfaceBinding.Definition);
            if (_sharedVtables.TryGetValue(key, out var existing)) return existing;
            var slots = interfaceBinding.Definition.MaxSlot + 1;
            var vtable = Marshal.AllocHGlobal(slots * sizeof(ulong));
            for (var slot = 0; slot < 3; slot++)
                Marshal.WriteInt64(vtable, slot * sizeof(ulong), (long)GetUnknownThunk(slot));
            for (var slot = 3; slot <= interfaceBinding.Definition.MaxSlot; slot++)
            {
                var entry = interfaceBinding.Methods.TryGetValue(slot, out var binding)
                    ? GetMethodThunk(descriptor, binding)
                    : GetNotImplementedThunk();
                Marshal.WriteInt64(vtable, slot * sizeof(ulong), (long)entry);
            }
            _sharedVtables[key] = (ulong)(long)vtable;
            _sharedVtableAllocations.Add((ulong)(long)vtable);
            return (ulong)(long)vtable;
        }
    }

    // ---------------------------------------------------------------------------
    // Dispatch
    // ---------------------------------------------------------------------------

    private ulong GetUnknownThunk(int slot)
    {
        lock (_gate)
        {
            if (_unknownThunks[slot] != 0) return _unknownThunks[slot];
            var captured = slot;
            _unknownThunks[slot] = Runtime.Thunks.Register(frame => DispatchUnknown(captured, frame),
                $"com:IUnknown.{captured}");
            return _unknownThunks[slot];
        }
    }

    private ulong GetNotImplementedThunk()
    {
        lock (_gate)
        {
            if (_notImplementedThunk != 0) return _notImplementedThunk;
            _notImplementedThunk = Runtime.Thunks.Register(_ => ComHResult.AsReturnValue(ComHResult.E_NOTIMPL),
                "com:notimplemented");
            return _notImplementedThunk;
        }
    }

    private ulong GetMethodThunk(ComClassDescriptor descriptor, ComMethodBinding binding)
    {
        lock (_gate)
        {
            var key = (descriptor, binding);
            if (_methodThunks.TryGetValue(key, out var existing)) return existing;
            var thunk = Runtime.Thunks.Register(frame => DispatchMethod(frame, descriptor, binding),
                $"com:{descriptor.Name}!{binding.Name}");
            _methodThunks[key] = thunk;
            return thunk;
        }
    }

    internal void RemoveView(ulong pointer) => _views.TryRemove(pointer, out _);

    private ulong DispatchUnknown(int slot, NativeCallFrame* frame)
    {
        if (!_views.TryGetValue(frame->Rcx, out var view))
            return ComHResult.AsReturnValue(ComHResult.E_UNEXPECTED);
        try
        {
            switch (slot)
            {
                case 0:
                    return ComHResult.AsReturnValue(QueryInterface(view, frame->Rdx, frame->R8));
                case 1:
                    return (uint)view.Object.AddRef();
                case 2:
                    return (uint)view.Object.Release();
                default:
                    return ComHResult.AsReturnValue(ComHResult.E_NOTIMPL);
            }
        }
        catch (Exception ex)
        {
            Runtime.Trace.Error($"COM IUnknown slot {slot} threw {ex.GetType().Name}: {ex.Message}",
                view.Object.Process.Id);
            return ComHResult.AsReturnValue(ComHResult.E_FAIL);
        }
    }

    private int QueryInterface(ComInterfaceView view, ulong interfaceIdPointer, ulong outputPointer)
    {
        var memory = view.Object.Process.Memory;
        if (outputPointer == 0) return ComHResult.E_POINTER;
        if (interfaceIdPointer == 0) return ComHResult.E_INVALIDARG;
        try { memory.Write(outputPointer, 0UL); } catch { return ComHResult.E_POINTER; }
        if (!ComMarshaling.TryReadGuid(memory, interfaceIdPointer, out var iid)) return ComHResult.E_INVALIDARG;

        if (iid == ComBuiltins.IUnknownId)
        {
            view.Object.AddRef();
            memory.Write(outputPointer, view.GuestPointer);
            return ComHResult.S_OK;
        }

        var other = view.Object.FindView(iid);
        if (other is null)
        {
            if (Runtime.Options.TraceNativeCalls)
                Runtime.Trace.Emit(TraceKind.Api, $"COM {view.Definition.Name}.QueryInterface({iid:B}) -> E_NOINTERFACE", view.Object.Process.Id);
            return ComHResult.E_NOINTERFACE;
        }
        other.Object.AddRef();
        memory.Write(outputPointer, other.GuestPointer);
        return ComHResult.S_OK;
    }

    private ulong DispatchMethod(NativeCallFrame* frame, ComClassDescriptor descriptor, ComMethodBinding binding)
    {
        if (!_views.TryGetValue(frame->Rcx, out var view))
            return ComHResult.AsReturnValue(ComHResult.E_UNEXPECTED);
        if (!ReferenceEquals(view.Object.Descriptor, descriptor))
            return ComHResult.AsReturnValue(ComHResult.E_UNEXPECTED);
        if (descriptor.ThreadingModel == ComThreadingModel.Apartment &&
            view.Object.CreatedThreadId != (int)NativeMethods.GetCurrentThreadId())
        {
            Runtime.Trace.Warn(
                $"COM object {descriptor.Name} is apartment-bound (created on thread {view.Object.CreatedThreadId})",
                view.Object.Process.Id);
            return ComHResult.AsReturnValue(ComHResult.RPC_E_WRONG_THREAD);
        }

        var thread = Runtime.GetCurrentThread() ??
                     view.Object.Process.GetOrRegisterHostThread(NativeMethods.GetCurrentThreadId());
        var args = FrameArguments(frame);
        var api = new ApiCallContext(view.Object.Process, thread, "com",
            $"{view.Definition.Name}!{binding.Name}", 0, frame->ReturnAddress, args);
        var context = new ComCallContext(api, view);
        try
        {
            var hr = (int)(uint)view.Object.GetHandler(binding)(context);
            if (Runtime.Options.TraceApiArguments) api.Complete((uint)hr);
            return (uint)hr;
        }
        catch (Exception ex)
        {
            Runtime.Trace.Error(
                $"COM method {view.Definition.Name}!{binding.Name} threw {ex.GetType().Name}: {ex.Message}",
                view.Object.Process.Id, thread.Id);
            return ComHResult.AsReturnValue(ComHResult.E_FAIL);
        }
    }

    private static ulong[] FrameArguments(NativeCallFrame* frame)
    {
        var args = new ulong[16];
        args[0] = frame->Rcx;
        args[1] = frame->Rdx;
        args[2] = frame->R8;
        args[3] = frame->R9;
        if (frame->StackArgs != 0)
        {
            for (var i = 4; i < args.Length; i++)
                args[i] = *(ulong*)(frame->StackArgs + (ulong)(i - 4) * 8);
        }
        return args;
    }

    // ---------------------------------------------------------------------------
    // Guest-registered class factories
    // ---------------------------------------------------------------------------

    public uint RegisterGuestClass(ImaginedProcess process, Guid classId, ulong factoryPointer, uint context)
    {
        if (factoryPointer == 0) return 0;
        var cookie = unchecked((uint)Interlocked.Increment(ref _nextCookie));
        lock (_gate)
        {
            _guestClasses.Add(new GuestClassRegistration
            {
                ProcessId = process.Id,
                ClassId = classId,
                FactoryPointer = factoryPointer,
                Context = context,
                Cookie = cookie
            });
        }
        CallGuest(process, factoryPointer, 1);
        Runtime.Trace.Emit(TraceKind.Api,
            $"CoRegisterClassObject clsid={classId:B} factory=0x{factoryPointer:X16} cookie={cookie}",
            process.Id);
        return cookie;
    }

    public int RevokeGuestClass(ImaginedProcess process, uint cookie)
    {
        GuestClassRegistration? registration;
        lock (_gate)
        {
            registration = _guestClasses.FirstOrDefault(g => g.ProcessId == process.Id && g.Cookie == cookie);
            if (registration is not null) _guestClasses.Remove(registration);
        }
        if (registration is null) return ComHResult.CO_E_OBJNOTREG;
        CallGuest(process, registration.FactoryPointer, 2);
        Runtime.Trace.Emit(TraceKind.Api, $"CoRevokeClassObject cookie={cookie}", process.Id);
        return ComHResult.S_OK;
    }

    private int CreateFromGuest(GuestClassRegistration registration, Guid interfaceId, out ulong pointer)
    {
        pointer = 0;
        var process = Runtime.GetProcessById(registration.ProcessId);
        if (process is null) return ComHResult.REGDB_E_CLASSNOTREG;
        var create = ReadGuestMethod(process, registration.FactoryPointer, 3);
        if (create == 0) return ComHResult.E_FAIL;
        var iid = &interfaceId;
        ulong result = 0;
        var hr = ((delegate* unmanaged<IntPtr, IntPtr, Guid*, ulong*, int>)(nint)create)(
            (IntPtr)(long)registration.FactoryPointer, IntPtr.Zero, iid, &result);
        pointer = result;
        return hr;
    }

    private int QueryGuestFactory(GuestClassRegistration registration, Guid interfaceId, out ulong pointer)
    {
        pointer = 0;
        var process = Runtime.GetProcessById(registration.ProcessId);
        if (process is null) return ComHResult.REGDB_E_CLASSNOTREG;
        var query = ReadGuestMethod(process, registration.FactoryPointer, 0);
        if (query == 0) return ComHResult.E_FAIL;
        var iid = &interfaceId;
        ulong result = 0;
        var hr = ((delegate* unmanaged<IntPtr, Guid*, ulong*, int>)(nint)query)(
            (IntPtr)(long)registration.FactoryPointer, iid, &result);
        pointer = result;
        return hr;
    }

    private static ulong ReadGuestMethod(ImaginedProcess process, ulong interfacePointer, int slot)
    {
        try
        {
            var vtable = process.Memory.Read<ulong>(interfacePointer);
            return vtable == 0 ? 0 : process.Memory.Read<ulong>(vtable + (ulong)slot * sizeof(ulong));
        }
        catch
        {
            return 0;
        }
    }

    private static void CallGuest(ImaginedProcess process, ulong interfacePointer, int slot)
    {
        var address = ReadGuestMethod(process, interfacePointer, slot);
        if (address == 0) return;
        if (slot is 1 or 2)
            ((delegate* unmanaged<IntPtr, uint>)(nint)address)((IntPtr)(long)interfacePointer);
    }

    // ---------------------------------------------------------------------------
    // In-process COM servers
    // ---------------------------------------------------------------------------

    public ulong LoadLibrary(ImaginedProcess process, string? path, bool autoFree)
    {
        if (string.IsNullOrWhiteSpace(path)) return 0;
        var state = EnsureServerState(process, path);
        if (state is null || state.Failed) return 0;
        state.LoadCount++;
        Runtime.Trace.Emit(TraceKind.Image,
            $"CoLoadLibrary('{path}') -> 0x{state.ModuleHandle:X16} ({(state.Guest ? "guest" : "host")}, autoFree={autoFree})",
            process.Id);
        return state.ModuleHandle;
    }

    public bool FreeLibrary(ImaginedProcess process, ulong handle)
    {
        ComServerLoadState? state;
        lock (_gate)
        {
            state = _serverStates.Values.FirstOrDefault(s =>
                s.Process.Id == process.Id && !s.Unloaded && s.ModuleHandle == handle);
        }
        if (state is null) return false;
        if (state.LoadCount > 0) state.LoadCount--;
        Runtime.Trace.Emit(TraceKind.Image, $"CoFreeLibrary(0x{handle:X16}) loadCount={state.LoadCount}",
            process.Id);
        if (state.LoadCount == 0 && CanUnloadNow(state)) UnloadServerState(state);
        return true;
    }

    public void FreeUnusedLibraries()
    {
        ComServerLoadState[] states;
        lock (_gate) states = _serverStates.Values.Where(s => !s.Unloaded).ToArray();
        foreach (var state in states)
        {
            if (state.CanUnloadNow == 0 || CanUnloadNow(state)) UnloadServerState(state);
        }
        if (CanUseHostCom)
        {
            try { ComHostInterop.FreeUnusedLibraries(); }
            catch (Exception ex) { Runtime.Trace.Warn($"CoFreeUnusedLibraries host call failed: {ex.Message}"); }
        }
    }

    private ComServerLoadState? EnsureServerState(ImaginedProcess process, string path)
    {
        var key = (process.Id, path);
        lock (_gate)
        {
            if (_serverStates.TryGetValue(key, out var existing)) return existing;
        }

        var state = LoadServerState(process, path);
        lock (_gate)
        {
            if (_serverStates.TryGetValue(key, out var existing)) return existing;
            _serverStates[key] = state;
            return state;
        }
    }

    private ComServerLoadState LoadServerState(ImaginedProcess process, string path)
    {
        var registration = new ComServerRegistration(path);
        try
        {
            string? guestPath = null;
            try { guestPath = process.FindLibraryPath(path, process.Image?.Image.Path); }
            catch { }

            if (guestPath is not null)
            {
                var mapped = process.LoadManagedLibrary(guestPath);
                var getClassObject = mapped is null
                    ? 0
                    : Runtime.ResolveExport(process, mapped.BaseAddress, "DllGetClassObject");
                var canUnloadNow = mapped is null
                    ? 0
                    : Runtime.ResolveExport(process, mapped.BaseAddress, "DllCanUnloadNow");
                var guestState = new ComServerLoadState
                {
                    Registration = registration,
                    Process = process,
                    Guest = true,
                    GuestBase = mapped?.BaseAddress ?? 0,
                    GetClassObject = getClassObject,
                    CanUnloadNow = canUnloadNow,
                    Failed = mapped is null
                };
                Runtime.Trace.Emit(TraceKind.Image,
                    $"in-process library '{path}' loaded as guest module base=0x{guestState.GuestBase:X16} " +
                    $"DllGetClassObject=0x{getClassObject:X16} DllCanUnloadNow=0x{canUnloadNow:X16}",
                    process.Id);
                return guestState;
            }

            if (!CanUseHostCom)
            {
                Runtime.Trace.Warn(
                    $"COM in-process library '{path}' is not a guest DLL and host COM is disabled",
                    process.Id);
                return new ComServerLoadState
                {
                    Registration = registration,
                    Process = process,
                    Failed = true
                };
            }

            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
            {
                Runtime.Trace.Warn($"COM server not found: '{path}'", process.Id);
                return new ComServerLoadState
                {
                    Registration = registration,
                    Process = process,
                    Failed = true
                };
            }

            var module = (IntPtr)(long)Runtime.NativeApis.LoadLibrary(fullPath);
            if (module == IntPtr.Zero)
            {
                Runtime.Trace.Warn($"COM server '{fullPath}' could not be loaded by the host", process.Id);
                return new ComServerLoadState
                {
                    Registration = registration,
                    Process = process,
                    Failed = true
                };
            }
            var hostGetClassObject = Runtime.NativeApis.GetProcAddress(module, "DllGetClassObject");
            var hostCanUnloadNow = Runtime.NativeApis.GetProcAddress(module, "DllCanUnloadNow");
            var hostState = new ComServerLoadState
            {
                Registration = registration,
                Process = process,
                HostModule = module,
                GetClassObject = (ulong)(long)hostGetClassObject,
                CanUnloadNow = (ulong)(long)hostCanUnloadNow,
                Failed = false
            };
            Runtime.Trace.Emit(TraceKind.Image,
                $"in-process library '{fullPath}' loaded as host module 0x{module:X16} " +
                $"DllGetClassObject=0x{hostGetClassObject:X16} DllCanUnloadNow=0x{hostCanUnloadNow:X16}",
                process.Id);
            return hostState;
        }
        catch (Exception ex)
        {
            Runtime.Trace.Error($"loading COM server '{path}' failed: {ex.GetType().Name}: {ex.Message}", process.Id);
            return new ComServerLoadState
            {
                Registration = registration,
                Process = process,
                Failed = true
            };
        }
    }

    private int TryCreateFromServers(ImaginedThread thread, Guid classId, Guid interfaceId, out ulong pointer)
    {
        pointer = 0;
        var last = ComHResult.REGDB_E_CLASSNOTREG;
        ComServerRegistration[] servers;
        lock (_gate) servers = _servers.ToArray();
        foreach (var server in servers)
        {
            var state = EnsureServerState(thread.Process, server.Path);
            if (state is null || state.Failed || state.GetClassObject == 0) continue;

            // Standard in-process activation: DllGetClassObject yields IClassFactory, then the
            // factory's CreateInstance yields the requested interface.
            var factoryHr = CallDllGetClassObject(state, classId, ComBuiltins.IClassFactoryId, out var factory);
            if (ComHResult.Failed(factoryHr) || factory == 0)
            {
                last = factoryHr;
                continue;
            }
            try
            {
                var hr = CallGuestFactoryCreate(thread.Process, factory, interfaceId, out pointer);
                if (ComHResult.Succeeded(hr) && pointer != 0)
                {
                    Runtime.Trace.Emit(TraceKind.Api,
                        $"COM server '{server.Path}' provided clsid={classId:B} iid={interfaceId:B} -> 0x{pointer:X16}",
                        thread.Process.Id, thread.Id);
                    return ComHResult.S_OK;
                }
                last = hr;
            }
            finally
            {
                CallGuest(thread.Process, factory, 2); // IUnknown::Release
            }
        }
        return last == ComHResult.CLASS_E_CLASSNOTAVAILABLE ? ComHResult.REGDB_E_CLASSNOTREG : last;
    }

    private static int CallGuestFactoryCreate(ImaginedProcess process, ulong factory, Guid interfaceId,
        out ulong pointer)
    {
        pointer = 0;
        var create = ReadGuestMethod(process, factory, 3);
        if (create == 0) return ComHResult.E_FAIL;
        var iid = &interfaceId;
        ulong result = 0;
        var hr = ((delegate* unmanaged<IntPtr, IntPtr, Guid*, ulong*, int>)(nint)create)(
            (IntPtr)(long)factory, IntPtr.Zero, iid, &result);
        pointer = result;
        return hr;
    }

    private int TryGetClassObjectFromServers(ImaginedThread thread, Guid classId, Guid interfaceId, out ulong pointer)
    {
        pointer = 0;
        var last = ComHResult.REGDB_E_CLASSNOTREG;
        ComServerRegistration[] servers;
        lock (_gate) servers = _servers.ToArray();
        foreach (var server in servers)
        {
            var state = EnsureServerState(thread.Process, server.Path);
            if (state is null || state.Failed || state.GetClassObject == 0) continue;
            var hr = CallDllGetClassObject(state, classId, interfaceId, out pointer);
            if (ComHResult.Succeeded(hr) && pointer != 0) return ComHResult.S_OK;
            last = hr;
        }
        return last == ComHResult.CLASS_E_CLASSNOTAVAILABLE ? ComHResult.REGDB_E_CLASSNOTREG : last;
    }

    private static int CallDllGetClassObject(ComServerLoadState state, Guid classId, Guid interfaceId, out ulong pointer)
    {
        pointer = 0;
        var pClass = &classId;
        var pInterface = &interfaceId;
        ulong result = 0;
        var hr = ((delegate* unmanaged<Guid*, Guid*, ulong*, int>)(nint)state.GetClassObject)(
            pClass, pInterface, &result);
        pointer = result;
        return hr;
    }

    private bool CanUnloadNow(ComServerLoadState state)
    {
        if (state.CanUnloadNow == 0) return true;
        try
        {
            return ((delegate* unmanaged<nint, int>)(nint)state.CanUnloadNow)(0) == ComHResult.S_OK;
        }
        catch (Exception ex)
        {
            Runtime.Trace.Warn($"DllCanUnloadNow for '{state.Registration.Path}' threw: {ex.Message}");
            return false;
        }
    }

    private void UnloadServerState(ComServerLoadState state)
    {
        if (state.Unloaded) return;
        state.Unloaded = true;
        lock (_gate) _serverStates.Remove((state.Process.Id, state.Registration.Path));
        if (state.Guest)
        {
            // Guest mappings are kept for the process lifetime, matching FreeLibrary semantics.
            state.CanUnload = true;
            Runtime.Trace.Emit(TraceKind.Image,
                $"COM guest server '{state.Registration.Path}' reports unloadable; mapping retained",
                state.Process.Id);
            return;
        }
        if (state.HostModule != IntPtr.Zero)
        {
            NativeMethods.FreeLibrary(state.HostModule);
            Runtime.Trace.Emit(TraceKind.Image, $"COM host server '{state.Registration.Path}' unloaded",
                state.Process.Id);
        }
    }

    // ---------------------------------------------------------------------------
    // Lifetime
    // ---------------------------------------------------------------------------

    internal void AddObject(ComObject obj)
    {
        lock (_gate)
        {
            if (!_objectsByProcess.TryGetValue(obj.Process.Id, out var set))
                _objectsByProcess[obj.Process.Id] = set = new HashSet<ComObject>();
            set.Add(obj);
        }
    }

    internal void OnObjectDestroyed(ComObject obj)
    {
        lock (_gate)
        {
            if (_objectsByProcess.TryGetValue(obj.Process.Id, out var set)) set.Remove(obj);
        }
    }

    /// <summary>Release every COM object owned by a process before its address space is torn down.</summary>
    public void ReleaseProcess(int processId)
    {
        ComObject[] objects;
        lock (_gate)
        {
            if (!_objectsByProcess.TryGetValue(processId, out var set)) objects = [];
            else
            {
                objects = set.ToArray();
                set.Clear();
                _objectsByProcess.Remove(processId);
            }
        }
        foreach (var obj in objects)
        {
            try { obj.Destroy(); }
            catch (Exception ex) { Runtime.Trace.Warn($"COM object cleanup for process {processId}: {ex.Message}"); }
        }

        GuestClassRegistration[] guestClasses;
        lock (_gate)
        {
            guestClasses = _guestClasses.Where(g => g.ProcessId == processId).ToArray();
            foreach (var guest in guestClasses) _guestClasses.Remove(guest);
        }
        if (guestClasses.Length > 0 && Runtime.GetProcessById(processId) is { } process)
        {
            foreach (var guest in guestClasses) CallGuest(process, guest.FactoryPointer, 2);
        }

        ComServerLoadState[] states;
        lock (_gate)
        {
            states = _serverStates.Values.Where(s => s.Process.Id == processId).ToArray();
            foreach (var state in states) _serverStates.Remove((processId, state.Registration.Path));
        }
        foreach (var state in states)
        {
            state.Unloaded = true;
            if (!state.Guest && state.HostModule != IntPtr.Zero) NativeMethods.FreeLibrary(state.HostModule);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        int[] processIds;
        lock (_gate) processIds = _objectsByProcess.Keys.ToArray();
        foreach (var processId in processIds) ReleaseProcess(processId);

        GuestClassRegistration[] guests;
        lock (_gate)
        {
            guests = _guestClasses.ToArray();
            _guestClasses.Clear();
        }
        foreach (var guest in guests)
        {
            var process = Runtime.GetProcessById(guest.ProcessId);
            if (process is not null) CallGuest(process, guest.FactoryPointer, 2);
        }

        ComServerLoadState[] servers;
        lock (_gate)
        {
            servers = _serverStates.Values.ToArray();
            _serverStates.Clear();
        }
        foreach (var state in servers)
        {
            if (!state.Guest && state.HostModule != IntPtr.Zero) NativeMethods.FreeLibrary(state.HostModule);
        }

        lock (_gate)
        {
            foreach (var allocation in _sharedVtableAllocations) Marshal.FreeHGlobal((IntPtr)(long)allocation);
            _sharedVtableAllocations.Clear();
            _sharedVtables.Clear();
            _methodThunks.Clear();
            _classFactoryDescriptors.Clear();
            _classes.Clear();
            _interfaces.Clear();
            _interfaceNames.Clear();
            _hostFactories.Clear();
            _servers.Clear();
        }
        _views.Clear();
        Runtime.Trace.Emit(TraceKind.Info, "COM runtime disposed");
    }
}
