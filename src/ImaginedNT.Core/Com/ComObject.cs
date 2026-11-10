using System.Runtime.InteropServices;
using ImaginedNT.Core.Diagnostics;
using ImaginedNT.Core.Runtime;

namespace ImaginedNT.Core.Com;

/// <summary>
/// A guest-visible COM object. Each supported interface gets a small guest-readable block whose
/// first pointer is the shared (or per-instance, for partially imagined objects) vtable. All
/// interfaces share one reference count, as COM identity requires.
/// </summary>
public sealed class ComObject
{
    private readonly object _gate = new();
    private readonly List<ComInterfaceView> _views = new();
    private readonly Dictionary<Guid, ComInterfaceView> _viewsByInterface = new();
    private readonly Dictionary<Guid, ulong> _hostInterfaces = new();
    private readonly List<ulong> _interfaceBlocks = new();
    private readonly List<ulong> _ownedVtables = new();
    private readonly List<ulong> _hostPointers = new();
    private readonly Dictionary<ComMethodBinding, ComMethodHandler> _handlers = new();
    private int _refCount = 1;
    private int _destroyed;

    internal ComObject(ComRuntime runtime, ImaginedProcess process, ComClassDescriptor descriptor, object? managedInstance)
    {
        Runtime = runtime;
        Process = process;
        Descriptor = descriptor;
        ManagedInstance = managedInstance;
        CreatedThreadId = (int)Native.NativeMethods.GetCurrentThreadId();
        Interlocked.Increment(ref descriptor.LiveObjectCount);
    }

    public ComRuntime Runtime { get; }
    public ImaginedProcess Process { get; }
    public ComClassDescriptor Descriptor { get; }
    public object? ManagedInstance { get; }
    public int CreatedThreadId { get; }
    public Guid? HostClassId => Descriptor.HostClassId;

    public int RefCount => Volatile.Read(ref _refCount);
    public bool IsDestroyed => Volatile.Read(ref _destroyed) != 0;

    public IReadOnlyList<ComInterfaceView> Views
    {
        get { lock (_gate) return _views.ToArray(); }
    }

    public ComInterfaceView? PrimaryView
    {
        get { lock (_gate) return _views.FirstOrDefault(); }
    }

    internal object Gate => _gate;

    /// <summary>Bind every managed method to the class instance once.</summary>
    internal void BuildHandlers()
    {
        if (ManagedInstance is null) return;
        foreach (var interfaceBinding in Descriptor.Interfaces)
        {
            foreach (var binding in interfaceBinding.Methods.Values)
            {
                _handlers[binding] = binding.CreateHandler(ManagedInstance);
            }
        }
    }

    internal ComMethodHandler GetHandler(ComMethodBinding binding)
    {
        if (_handlers.TryGetValue(binding, out var handler)) return handler;
        throw new InvalidOperationException($"no managed handler bound for {Descriptor.Name}!{binding}");
    }

    internal void AddView(ComInterfaceView view)
    {
        lock (_gate)
        {
            _views.Add(view);
            _viewsByInterface[view.Definition.InterfaceId] = view;
        }
    }

    internal ComInterfaceView? FindView(Guid interfaceId)
    {
        lock (_gate) return _viewsByInterface.GetValueOrDefault(interfaceId);
    }

    internal void SetHostInterface(Guid interfaceId, ulong pointer)
    {
        lock (_gate) _hostInterfaces[interfaceId] = pointer;
    }

    internal ulong GetHostInterface(Guid interfaceId)
    {
        lock (_gate) return _hostInterfaces.GetValueOrDefault(interfaceId);
    }

    internal void AddInterfaceBlock(ulong address) => _interfaceBlocks.Add(address);
    internal void AddOwnedVtable(ulong address) => _ownedVtables.Add(address);
    internal void AddHostPointer(ulong pointer)
    {
        if (pointer != 0) _hostPointers.Add(pointer);
    }

    internal int AddRef() => Interlocked.Increment(ref _refCount);

    internal int Release()
    {
        var count = Interlocked.Decrement(ref _refCount);
        if (count == 0) Destroy();
        return count;
    }

    internal void Destroy()
    {
        if (Interlocked.Exchange(ref _destroyed, 1) != 0) return;
        ComInterfaceView[] views;
        lock (_gate)
        {
            views = _views.ToArray();
            _views.Clear();
            _viewsByInterface.Clear();
            _hostInterfaces.Clear();
        }

        foreach (var view in views) Runtime.RemoveView(view.GuestPointer);

        // Release host interface references acquired for partially imagined forwarding.
        foreach (var pointer in _hostPointers)
        {
            try { Marshal.Release((IntPtr)(long)pointer); }
            catch (Exception ex) { Runtime.Runtime.Trace.Warn($"COM host release failed: {ex.Message}"); }
        }
        _hostPointers.Clear();

        foreach (var address in _ownedVtables)
            if (address != 0) Marshal.FreeHGlobal((IntPtr)(long)address);
        _ownedVtables.Clear();

        foreach (var address in _interfaceBlocks)
            if (address != 0) Marshal.FreeHGlobal((IntPtr)(long)address);
        _interfaceBlocks.Clear();

        if (ManagedInstance is IDisposable disposable)
        {
            try { disposable.Dispose(); }
            catch (Exception ex) { Runtime.Runtime.Trace.Warn($"COM object dispose failed: {ex.Message}"); }
        }

        Interlocked.Decrement(ref Descriptor.LiveObjectCount);
        Runtime.OnObjectDestroyed(this);
        Runtime.Runtime.Trace.Emit(TraceKind.Object,
            $"COM object destroyed: {Descriptor.Name} clsid={Descriptor.ClassId:B} interfaces={views.Length}",
            Process.Id);
    }

    public override string ToString() =>
        $"{Descriptor.Name} ({Descriptor.ClassId:B}) refs={RefCount} views={Views.Count}";
}
