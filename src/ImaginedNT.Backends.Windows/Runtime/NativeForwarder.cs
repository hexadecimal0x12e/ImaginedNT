using System.Collections.Concurrent;
using ImaginedNT.Core.Native;

namespace ImaginedNT.Core.Runtime;

/// <summary>
/// Resolves imports that the managed layer deliberately sends back to the real NT/Windows
/// implementation.  This is the "let the host OS do it" half of the Wine-style split.
/// </summary>
public sealed class NativeForwarder : INativeForwarder
{
    private readonly object _gate = new();
    private readonly Dictionary<string, IntPtr> _modules = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IntPtr> _exports = new(StringComparer.OrdinalIgnoreCase);
    private readonly Diagnostics.TraceLog? _trace;
    private readonly int _pid;
    private bool _disposed;

    public NativeForwarder(Diagnostics.TraceLog? trace, int pid)
    {
        _trace = trace;
        _pid = pid;
    }

    public ulong Resolve(string moduleName, string? exportName, ushort? ordinal, out string detail)
    {
        detail = string.Empty;
        lock (_gate)
        {
            if (_disposed) return 0;
            var module = NormalizeModule(moduleName);
            var key = exportName is not null ? $"{module}!{exportName}" : $"{module}!#{ordinal}";
            if (_exports.TryGetValue(key, out var cached))
            {
                detail = cached == IntPtr.Zero ? "cached miss" : $"native {moduleName}!{exportName ?? "#" + ordinal}";
                return (ulong)(long)cached;
            }

            var handle = LoadModule(module);
            if (handle == IntPtr.Zero)
            {
                _exports[key] = IntPtr.Zero;
                detail = $"native module {moduleName} not loaded";
                return 0;
            }

            IntPtr address;
            if (exportName is not null)
            {
                address = NativeMethods.GetProcAddress(handle, exportName);
            }
            else
            {
                address = NativeMethods.GetProcAddress(handle, (IntPtr)(ordinal ?? 0));
            }

            if (address == IntPtr.Zero)
            {
                // API sets sometimes have a different backing DLL; ask Windows for the
                // already-loaded module by name.
                address = NativeMethods.GetProcAddress(handle, exportName ?? string.Empty);
            }

            _exports[key] = address;
            detail = address == IntPtr.Zero
                ? $"native {moduleName}!{exportName ?? "#" + ordinal} not found"
                : $"native {moduleName}!{exportName ?? "#" + ordinal} @0x{(ulong)(long)address:X16}";
            if (address == IntPtr.Zero)
                _trace?.Emit(Diagnostics.TraceKind.Warning, $"native export not found: {key}", _pid);
            return (ulong)(long)address;
        }
    }

    private IntPtr LoadModule(string module)
    {
        if (_modules.TryGetValue(module, out var cached)) return cached;
        var candidates = new List<string>();
        if (module.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) candidates.Add(module);
        else candidates.Add(module + ".dll");
        if (!module.Contains('.')) candidates.Add(module);

        var handle = IntPtr.Zero;
        foreach (var candidate in candidates)
        {
            handle = NativeMethods.GetModuleHandleW(candidate);
            if (handle != IntPtr.Zero) break;
            handle = NativeMethods.LoadLibraryW(candidate);
            if (handle != IntPtr.Zero) break;
        }

        _modules[module] = handle;
        return handle;
    }

    public IntPtr GetModuleHandle(string moduleName)
    {
        lock (_gate)
        {
            var module = NormalizeModule(moduleName);
            return LoadModule(module);
        }
    }

    public IntPtr GetProcAddress(IntPtr module, string name) => NativeMethods.GetProcAddress(module, name);

    public string? GetModuleName(IntPtr module)
    {
        lock (_gate)
        {
            foreach (var entry in _modules)
                if (entry.Value == module && module != IntPtr.Zero) return entry.Key;
        }
        var path = new System.Text.StringBuilder(32768);
        return NativeMethods.GetModuleFileNameW(module, path, (uint)path.Capacity) == 0
            ? null : Path.GetFileName(path.ToString());
    }
    public IntPtr GetProcAddress(IntPtr module, IntPtr ordinal) => NativeMethods.GetProcAddress(module, ordinal);

    public ulong LoadLibrary(string moduleName)
    {
        lock (_gate)
        {
            var module = NormalizeModule(moduleName);
            var handle = LoadModule(module);
            return (ulong)(long)handle;
        }
    }

    private static string NormalizeModule(string module)
    {
        var name = module.Trim();
        if (name.IndexOfAny(['\\', '/']) >= 0 || Path.IsPathRooted(name))
            return Path.GetFullPath(name).ToLowerInvariant();
        return name.ToLowerInvariant();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            // Deliberately do not call FreeLibrary on modules that may be process-wide
            // dependencies (ntdll, kernel32, ucrtbase).  The host is short-lived.
            _modules.Clear();
            _exports.Clear();
        }
    }
}
