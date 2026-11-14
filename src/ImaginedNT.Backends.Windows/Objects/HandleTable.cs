using ImaginedNT.Core.Diagnostics;
using ImaginedNT.Core.Nt;

namespace ImaginedNT.Core.Objects;

public sealed class HandleEntry
{
    internal ImaginedHandle? NativeRepresentation { get; set; }
    public required NtObject Object { get; init; }
    public required uint GrantedAccess { get; init; }
    public required ulong Handle { get; init; }
    public string? Name { get; init; }

    /// <summary>HANDLE_FLAG_INHERIT; copied into a child process by CreateProcessW.</summary>
    public bool Inheritable { get; set; }
}

/// <summary>Per-process handle table.  Handles are small 4-byte-aligned integers, just like NT.</summary>
public sealed class HandleTable : IDisposable
{
    private static uint NormalizeAccess(NtObject obj, uint access)
    {
        if (obj is not Io.FileObject) return access;
        if ((access & NtAccess.GenericAll) != 0) access = (access & ~NtAccess.GenericAll) | 0x001F01FF;
        if ((access & NtAccess.GenericRead) != 0) access = (access & ~NtAccess.GenericRead) | 0x00120089;
        if ((access & NtAccess.GenericWrite) != 0) access = (access & ~NtAccess.GenericWrite) | 0x00120116;
        if ((access & NtAccess.GenericExecute) != 0) access = (access & ~NtAccess.GenericExecute) | 0x001200A0;
        return access;
    }

    private readonly object _gate = new();
    private readonly Dictionary<ulong, HandleEntry> _handles = new();
    private readonly TraceLog? _trace;
    private readonly int _pid;
    private ulong _nextHandle = 4;
    private ulong _nextImaginedHandle = 0x4000_0000;
    private bool _disposed;

    public HandleTable(TraceLog? trace = null, int pid = 0)
    {
        _trace = trace;
        _pid = pid;
    }

    public int Count
    {
        get { lock (_gate) return _handles.Count; }
    }

    public ulong WrapNative(nint handle, uint access = 0x01FF_FFFF, bool inheritable = false)
    {
        using var obj = new ImaginedHandle(handle);
        lock (_gate)
        {
            while (_handles.ContainsKey(_nextImaginedHandle)) _nextImaginedHandle += 4;
            if (_nextImaginedHandle > 0x7FFF_FFFC) throw new OutOfMemoryException("ImaginedHandle table exhausted");
            var value = _nextImaginedHandle;
            _nextImaginedHandle += 4;
            return Create(obj, access, value, inheritable: inheritable);
        }
    }

    public bool HasNativeRepresentation(ulong handle)
    {
        lock (_gate) return _handles.TryGetValue(handle, out var entry) &&
            (entry.Object is ImaginedHandle || entry.NativeRepresentation is not null);
    }

    public uint AcquireNative(ulong handle, bool expand, out ImaginedHandle? native)
    {
        native = null;
        lock (_gate)
        {
            if (!_handles.TryGetValue(handle, out var entry)) return NtStatus.InvalidHandle;
            if (entry.Object is ImaginedHandle wrapped) native = wrapped;
            else
            {
                if (!expand && entry.NativeRepresentation is null) return NtStatus.InvalidHandle;
                if (entry.NativeRepresentation is null)
                {
                    var source = entry.Object.GetNativeHandle();
                    if (source == 0) return NtStatus.NotSupported;
                    var process = Native.NativeMethods.GetCurrentProcess();
                    var access = entry.GrantedAccess;
                    if (!DuplicateHandle(process, source, process, out var copy,
                        access == 0x01FF_FFFF ? 0 : access, false, access == 0x01FF_FFFF ? 2u : 0))
                        return NtStatus.AccessDenied;
                    entry.NativeRepresentation = new ImaginedHandle(copy);
                }
                native = entry.NativeRepresentation;
            }
            native.AddRef();
            return NtStatus.Success;
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(nint sourceProcess, nint source, nint targetProcess,
        out nint target, uint access,
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] bool inherit,
        uint options);

    public IReadOnlyList<HandleEntry> Snapshot()
    {
        lock (_gate) return _handles.Values.ToArray();
    }

    public ulong Create(NtObject obj, uint grantedAccess = 0x01FF_FFFF, ulong fixedHandle = 0, string? name = null,
        bool inheritable = false)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            grantedAccess = NormalizeAccess(obj, grantedAccess);
            if (obj.Security is { } security)
            {
                var token = TokenProvider?.Invoke();
                var allowed = token is null ? 0 : NormalizeAccess(obj, security.GetAllowedAccess(token));
                if ((grantedAccess & ~NtAccess.MaximumAllowed & ~allowed) != 0 || (token is null && grantedAccess != 0))
                {
                    _trace?.Emit(TraceKind.Security, $"denied open {obj.TypeName} '{name ?? obj.FullPath}' access=0x{grantedAccess:X}", _pid);
                    return 0;
                }
                if ((grantedAccess & NtAccess.MaximumAllowed) != 0) grantedAccess = allowed;
            }
            var handle = fixedHandle != 0 ? fixedHandle : AllocateHandle();
            if (fixedHandle == 0 && handle == 0) handle = AllocateHandle();
            if (_handles.TryGetValue(handle, out var existing))
            {
                existing.Object.Release();
                existing.NativeRepresentation?.Release();
                _handles.Remove(handle);
            }
            obj.AddRef();
            _handles[handle] = new HandleEntry
            {
                Object = obj,
                GrantedAccess = grantedAccess,
                Handle = handle,
                Name = name ?? obj.FullPath,
                Inheritable = inheritable
            };
            _trace?.Emit(TraceKind.Object, $"handle 0x{handle:X} -> {obj.TypeName} '{name ?? obj.FullPath}' access=0x{grantedAccess:X}", _pid);
            return handle;
        }
    }

    /// <summary>Seed a fixed handle (used for stdin/stdout/stderr and handle inheritance).</summary>
    public void Seed(ulong handle, NtObject obj, uint access = 0x01FF_FFFF, bool inheritable = false)
    {
        access = NormalizeAccess(obj, access);
        lock (_gate)
        {
            if (_handles.TryGetValue(handle, out var existing))
            {
                existing.Object.Release();
                existing.NativeRepresentation?.Release();
            }
            obj.AddRef();
            _handles[handle] = new HandleEntry
            {
                Object = obj,
                GrantedAccess = access,
                Handle = handle,
                Name = obj.FullPath,
                Inheritable = inheritable
            };
        }
    }

    /// <summary>Set or clear HANDLE_FLAG_INHERIT for one handle.</summary>
    public bool SetInformation(ulong handle, uint mask, uint flags)
    {
        lock (_gate)
        {
            if (!_handles.TryGetValue(handle, out var entry)) return false;
            if ((mask & HandleFlagInherit) != 0) entry.Inheritable = (flags & HandleFlagInherit) != 0;
            return true;
        }
    }

    public bool TryGetInformation(ulong handle, out uint flags)
    {
        lock (_gate)
        {
            if (!_handles.TryGetValue(handle, out var entry))
            {
                flags = 0;
                return false;
            }
            flags = entry.Inheritable ? HandleFlagInherit : 0;
            return true;
        }
    }

    public const uint HandleFlagInherit = 0x0000_0001;
    public Func<Security.AccessToken?>? TokenProvider { get; init; }
    public const uint HandleFlagProtectFromClose = 0x0000_0002;

    private ulong AllocateHandle()
    {
        var candidate = _nextHandle >= 0x4000_0000 ? 4 : _nextHandle;
        while (candidate == 0 || _handles.ContainsKey(candidate))
        {
            candidate += 4;
            if (candidate >= 0x4000_0000) candidate = 4;
        }
        _nextHandle = candidate + 4;
        return candidate;
    }

    public uint TryGet(ulong handle, uint desiredAccess, out NtObject? obj)
    {
        obj = null;
        lock (_gate)
        {
            if (!_handles.TryGetValue(handle, out var entry)) return NtStatus.InvalidHandle;
            desiredAccess = NormalizeAccess(entry.Object, desiredAccess);
            var missing = desiredAccess & ~entry.GrantedAccess & ~NtAccess.MaximumAllowed;
            if (missing != 0) return NtStatus.AccessDenied;
            obj = entry.Object;
            return NtStatus.Success;
        }
    }

    public bool TryGet(ulong handle, out NtObject? obj) => TryGet(handle, 0, out obj) == NtStatus.Success;

    public uint Close(ulong handle)
    {
        NtObject? obj = null;
        lock (_gate)
        {
            if (!_handles.TryGetValue(handle, out var entry)) return NtStatus.InvalidHandle;
            _handles.Remove(handle);
            entry.NativeRepresentation?.Release();
            obj = entry.Object;
        }
        obj.Release();
        _trace?.Emit(TraceKind.Object, $"closed handle 0x{handle:X}", _pid);
        return NtStatus.Success;
    }

    public uint Duplicate(ulong source, uint desiredAccess, out ulong newHandle, bool inheritable = false)
    {
        newHandle = 0;
        lock (_gate)
        {
            if (!_handles.TryGetValue(source, out var entry)) return NtStatus.InvalidHandle;
            var access = desiredAccess == 0 ? entry.GrantedAccess : NormalizeAccess(entry.Object, desiredAccess);
            if ((access & ~entry.GrantedAccess) != 0) return NtStatus.AccessDenied;
            if (entry.Object is ImaginedHandle native)
            {
                var process = Native.NativeMethods.GetCurrentProcess();
                if (!DuplicateHandle(process, native.GetNativeHandle(), process, out var copy,
                    desiredAccess, inheritable, desiredAccess == 0 ? 2u : 0)) return NtStatus.AccessDenied;
                newHandle = WrapNative(copy, access, inheritable);
                return NtStatus.Success;
            }
            var handle = AllocateHandle();
            entry.Object.AddRef();
            _handles[handle] = new HandleEntry
            {
                Object = entry.Object,
                GrantedAccess = access,
                Handle = handle,
                Name = entry.Name,
                Inheritable = inheritable
            };
            newHandle = handle;
            return NtStatus.Success;
        }
    }

    public NtObject? FindByName(string fullPath)
    {
        lock (_gate)
        {
            return _handles.Values.Select(h => h.Object)
                .FirstOrDefault(o => string.Equals(o.FullPath, fullPath, StringComparison.OrdinalIgnoreCase));
        }
    }

    public void Dispose()
    {
        NtObject[] objects;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            objects = _handles.Values.SelectMany(h => h.NativeRepresentation is null
                ? new[] { h.Object } : new NtObject[] { h.Object, h.NativeRepresentation }).ToArray();
            _handles.Clear();
        }
        foreach (var obj in objects) obj.Release();
    }
}
