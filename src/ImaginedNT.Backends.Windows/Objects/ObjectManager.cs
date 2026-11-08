using ImaginedNT.Core.Diagnostics;

namespace ImaginedNT.Core.Objects;

/// <summary>
/// A small, per-process NT object namespace.  Directories, symbolic links and named
/// synchronisation objects live here.  Paths are case-insensitive and rooted at "\".
/// </summary>
public sealed class ObjectManager : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, NtObject> _objects = new(StringComparer.OrdinalIgnoreCase);
    private readonly TraceLog? _trace;
    private readonly int _pid;
    private long _nextId;

    public ObjectManager(TraceLog? trace = null, int pid = 0)
    {
        _trace = trace;
        _pid = pid;
        InitializeNamespace();
    }

    public int ObjectCount
    {
        get { lock (_gate) return _objects.Count; }
    }

    public static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "\\";
        var normalized = path.Replace('/', '\\').Trim();
        if (!normalized.StartsWith('\\')) normalized = "\\" + normalized;
        while (normalized.Contains("\\\\", StringComparison.Ordinal)) normalized = normalized.Replace("\\\\", "\\", StringComparison.Ordinal);
        if (normalized.Length > 1) normalized = normalized.TrimEnd('\\');
        return normalized;
    }

    private void InitializeNamespace()
    {
        var root = new DirectoryObject { Name = "\\", FullPath = "\\" };
        _objects["\\"] = root;
        EnsureDirectory("\\BaseNamedObjects");
        EnsureDirectory("\\KnownDlls");
        EnsureDirectory("\\Sessions");
        EnsureDirectory("\\Sessions\\0");
        EnsureDirectory("\\Device");
        EnsureDirectory("\\Device\\ImaginedDrive0");
        EnsureDirectory("\\??");
        CreateSymbolicLink("\\DosDevices", "\\??", overwrite: true);
        CreateSymbolicLink("\\??\\C:", "\\Device\\ImaginedDrive0", overwrite: true);
    }

    public DirectoryObject EnsureDirectory(string path)
    {
        path = NormalizePath(path);
        lock (_gate)
        {
            if (_objects.TryGetValue(path, out var existing) && existing is DirectoryObject dir) return dir;
            var parentPath = GetParent(path);
            if (parentPath != path)
            {
                var parent = EnsureDirectory(parentPath);
                var name = GetName(path);
                var created = new DirectoryObject { Name = name, FullPath = path };
                _objects[path] = created;
                return created;
            }
            var root = new DirectoryObject { Name = "\\", FullPath = "\\" };
            _objects["\\"] = root;
            return root;
        }
    }

    public SymbolicLinkObject CreateSymbolicLink(string path, string target, bool overwrite = false)
    {
        path = NormalizePath(path);
        lock (_gate)
        {
            if (_objects.TryGetValue(path, out var existing))
            {
                if (!overwrite) throw new InvalidOperationException($"object already exists: {path}");
                existing.Release();
                _objects.Remove(path);
            }
            EnsureDirectory(GetParent(path));
            var link = new SymbolicLinkObject { Name = GetName(path), FullPath = path, Target = target };
            _objects[path] = link;
            _trace?.Emit(TraceKind.Object, $"symbolic link {path} -> {target}", _pid);
            return link;
        }
    }

    public void CreateObject(string path, NtObject obj, bool overwrite = false)
    {
        path = NormalizePath(path);
        lock (_gate)
        {
            if (_objects.ContainsKey(path) && !overwrite)
                throw new InvalidOperationException($"object already exists: {path}");
            EnsureDirectory(GetParent(path));
            if (_objects.TryGetValue(path, out var existing))
            {
                existing.Release();
                _objects.Remove(path);
            }
            obj.Name = GetName(path);
            obj.FullPath = path;
            _objects[path] = obj;
            obj.AddRef(); // namespace owns one reference
            _trace?.Emit(TraceKind.Object, $"created {obj.TypeName} {path} ({obj.Info})", _pid);
        }
    }

    public T? Open<T>(string path) where T : NtObject
    {
        var obj = Open(path);
        return obj as T;
    }

    public NtObject? Open(string path)
    {
        var resolved = Resolve(path);
        if (resolved is null) return null;
        lock (_gate)
        {
            // The namespace can retain an instance after every handle has closed.  Normally that
            // is intentional (the object's name survives), but if the instance was disposed by an
            // unbalanced close, hand out a fresh object instead of a dead one.
            if (resolved.IsDisposed && resolved is not DirectoryObject && resolved is not SymbolicLinkObject)
            {
                var key = resolved.FullPath ?? NormalizePath(path);
                if (_objects.TryGetValue(key, out var current) && ReferenceEquals(current, resolved))
                    _objects.Remove(key);
                if (resolved.ReferenceCount > 0) resolved.Release();
                return null;
            }
        }
        return resolved;
    }

    /// <summary>Resolve every symbolic link in the path, including relative link targets.</summary>
    public NtObject? Resolve(string path)
    {
        path = NormalizePath(path);
        for (var iteration = 0; iteration < 32; iteration++)
        {
            lock (_gate)
            {
                if (_objects.TryGetValue(path, out var direct)) return direct;
            }

            var segments = path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            var current = "\\";
            var replaced = false;
            foreach (var segment in segments)
            {
                current = current == "\\" ? "\\" + segment : current + "\\" + segment;
                NtObject? component;
                lock (_gate) _objects.TryGetValue(current, out component);
                if (component is SymbolicLinkObject link)
                {
                    var target = link.Target;
                    if (!target.StartsWith('\\'))
                    {
                        var parent = GetParent(current);
                        target = (parent == "\\" ? "\\" : parent + "\\") + target;
                    }
                    var remainder = path[current.Length..];
                    path = NormalizePath(target + remainder);
                    replaced = true;
                    break;
                }
            }

            if (!replaced) return null;
        }
        _trace?.Warn($"symbolic link loop while resolving {path}", _pid);
        return null;
    }

    public bool Delete(string path)
    {
        path = NormalizePath(path);
        lock (_gate)
        {
            if (!_objects.TryGetValue(path, out var obj)) return false;
            if (obj is DirectoryObject)
            {
                var prefix = path == "\\" ? "\\" : path + "\\";
                if (_objects.Keys.Any(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !string.Equals(k, path, StringComparison.OrdinalIgnoreCase)))
                    return false;
            }
            _objects.Remove(path);
            obj.Release();
            return true;
        }
    }

    public IReadOnlyList<(string Path, NtObject Object)> Enumerate()
    {
        lock (_gate) return _objects.Select(kv => (kv.Key, kv.Value)).ToArray();
    }

    public async Task<NtObject?> WaitForNamedObjectAsync(string path, TimeSpan timeout)
    {
        var obj = Resolve(path);
        if (obj is null || !obj.SupportsWait) return null;
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (obj.IsSignaled) return obj;
            await Task.Delay(10).ConfigureAwait(false);
        }
        return null;
    }

    public static string GetParent(string path)
    {
        path = NormalizePath(path);
        if (path == "\\") return "\\";
        var index = path.LastIndexOf('\\');
        return index <= 0 ? "\\" : path[..index];
    }

    public static string GetName(string path)
    {
        path = NormalizePath(path);
        if (path == "\\") return "\\";
        var index = path.LastIndexOf('\\');
        return index < 0 ? path : path[(index + 1)..];
    }

    public static string Combine(string directory, string name)
    {
        directory = NormalizePath(directory);
        name = name.Replace('/', '\\').Trim('\\');
        return directory == "\\" ? "\\" + name : directory + "\\" + name;
    }

    public string NewAnonymousName(ObjectType type) => $"\\anonymous\\{type}\\{Interlocked.Increment(ref _nextId):X}";

    public void Dispose()
    {
        NtObject[] objects;
        lock (_gate)
        {
            objects = _objects.Values.Distinct().ToArray();
            _objects.Clear();
        }
        foreach (var obj in objects)
        {
            try { obj.Release(); } catch { }
        }
    }
}
