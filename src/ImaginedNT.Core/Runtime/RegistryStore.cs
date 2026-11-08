using ImaginedNT.Core.Io;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ImaginedNT.Core.Runtime;

internal sealed class RegistryStore
{
    private static readonly ConditionalWeakTable<ImaginedRuntime, ConcurrentDictionary<string, Lazy<RegistryStore>>> Stores = new();
    internal static RegistryStore Get(ImaginedRuntime runtime, string root) =>
        Stores.GetValue(runtime, _ => new(StringComparer.OrdinalIgnoreCase))
            .GetOrAdd(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)),
                path => new Lazy<RegistryStore>(() => new(path))).Value;

    private RegistryKeyData? EnvironmentKey(EnvironmentVariableTarget target) => target switch
    {
        EnvironmentVariableTarget.User => Find(Root(0x80000001), "Environment"),
        EnvironmentVariableTarget.Machine => Find(Root(0x80000002), @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment"),
        _ => throw new ArgumentOutOfRangeException(nameof(target))
    };

    internal Dictionary<string, (string Value, bool Expand)> ReadEnvironment(EnvironmentVariableTarget target)
    {
        lock (Gate)
        {
            var values = new Dictionary<string, (string, bool)>(StringComparer.OrdinalIgnoreCase);
            if (EnvironmentKey(target) is not { } key) return values;
            foreach (var (name, value) in key.Values)
                if (name.Length > 0 && !name.Contains('=') && value.Type is 1 or 2 && (value.Data.Length & 1) == 0)
                    values[name] = (Encoding.Unicode.GetString(value.Data).Split('\0')[0], value.Type == 2);
            return values;
        }
    }

    internal void SetEnvironment(string name, string? value, EnvironmentVariableTarget target)
    {
        lock (Gate)
        {
            var key = EnvironmentKey(target);
            if (value is null)
            {
                if (key is not null && key.Values.ContainsKey(name)) DeleteValue(key, name);
                return;
            }
            key ??= target == EnvironmentVariableTarget.User
                ? Create(Root(0x80000001), "Environment", "", out _)
                : Create(Root(0x80000002), @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment", "", out _);
            SetValue(key, name, value.Contains('%') ? 2u : 1u, Encoding.Unicode.GetBytes(value + '\0'));
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, MaxDepth = 2048 };
    private readonly RegistryDocument _document;

    internal RegistryStore(string root)
    {
        FilePath = Path.Combine(root, "registry.json");
        try
        {
            using var stream = HostFileSystem.OpenRead(FilePath);
            _document = JsonSerializer.Deserialize<RegistryDocument>(stream, JsonOptions)
                ?? throw new InvalidDataException("Empty registry document");
            if (_document.Version != 1 || _document.Roots is null)
                throw new InvalidDataException("Invalid registry document version or roots");
            _document.Roots = new(_document.Roots, StringComparer.OrdinalIgnoreCase);
            foreach (var key in _document.Roots.Values) Restore(key);
        }
        catch (FileNotFoundException)
        {
            _document = new();
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException("Duplicate registry names", ex);
        }
        foreach (var name in RootNames.Values)
            _document.Roots.TryAdd(name, new());
    }

    internal static IReadOnlyDictionary<uint, string> RootNames { get; } = new Dictionary<uint, string>
    {
        [0x80000000] = "HKEY_CLASSES_ROOT",
        [0x80000001] = "HKEY_CURRENT_USER",
        [0x80000002] = "HKEY_LOCAL_MACHINE",
        [0x80000003] = "HKEY_USERS",
        [0x80000005] = "HKEY_CURRENT_CONFIG"
    };

    internal object Gate { get; } = new();
    internal string FilePath { get; }
    internal RegistryKeyData Root(uint handle) => _document.Roots[RootNames[handle]];

    private static void Restore(RegistryKeyData key)
    {
        if (key is null || key.SubKeys is null || key.Values is null || key.ClassName is null || key.LastWriteTime < 0)
            throw new InvalidDataException("Invalid registry key");
        key.SubKeys = new(key.SubKeys, StringComparer.OrdinalIgnoreCase);
        key.Values = new(key.Values, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, child) in key.SubKeys)
        {
            if (name.Length is 0 or > 255 || name.Contains('\\') || name.Contains('\0'))
                throw new InvalidDataException("Invalid registry subkey name");
            Restore(child);
        }
        foreach (var (name, value) in key.Values)
            if (name.Contains('\0') || value is null || value.Data is null)
                throw new InvalidDataException("Invalid registry value");
    }

    internal static RegistryKeyData? Find(RegistryKeyData key, string path)
    {
        foreach (var name in path.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!key.SubKeys.TryGetValue(name, out var child)) return null;
            key = child;
        }
        return key;
    }

    internal RegistryKeyData Create(RegistryKeyData key, string path, string className, out bool created)
    {
        var parts = path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            if (key.SubKeys.TryGetValue(parts[i], out var child))
            {
                key = child;
                continue;
            }

            var first = new RegistryKeyData();
            var last = first;
            for (var j = i + 1; j < parts.Length; j++)
            {
                child = new();
                last.SubKeys.Add(parts[j], child);
                last = child;
            }
            last.ClassName = className;
            var name = parts[i];
            Change(key, () => key.SubKeys.Add(name, first), () => key.SubKeys.Remove(name));
            created = true;
            return last;
        }
        created = false;
        return key;
    }

    internal void SetValue(RegistryKeyData key, string name, uint type, byte[] data)
    {
        key.Values.TryGetValue(name, out var old);
        Change(key, () => key.Values[name] = new() { Type = type, Data = data }, () =>
        {
            if (old is null) key.Values.Remove(name);
            else key.Values[name] = old;
        });
    }

    internal void DeleteValue(RegistryKeyData key, string name)
    {
        var old = key.Values[name];
        Change(key, () => key.Values.Remove(name), () => key.Values[name] = old);
    }

    internal void DeleteKey(RegistryKeyData parent, string name)
    {
        var old = parent.SubKeys[name];
        Change(parent, () => parent.SubKeys.Remove(name), () => parent.SubKeys[name] = old);
        MarkDeleted(old);
    }

    internal void Clear(RegistryKeyData key)
    {
        var children = key.SubKeys;
        var values = key.Values;
        Change(key, () =>
        {
            key.SubKeys = new(StringComparer.OrdinalIgnoreCase);
            key.Values = new(StringComparer.OrdinalIgnoreCase);
        }, () =>
        {
            key.SubKeys = children;
            key.Values = values;
        });
        foreach (var child in children.Values) MarkDeleted(child);
    }

    private static void MarkDeleted(RegistryKeyData key)
    {
        key.Deleted = true;
        foreach (var child in key.SubKeys.Values) MarkDeleted(child);
    }

    private void Change(RegistryKeyData key, Action apply, Action undo)
    {
        var time = key.LastWriteTime;
        apply();
        key.LastWriteTime = DateTime.UtcNow.ToFileTimeUtc();
        try { Flush(); }
        catch
        {
            undo();
            key.LastWriteTime = time;
            throw;
        }
    }

    internal void Flush()
    {
        var temp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = HostFileSystem.Open(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, _document, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            HostFileSystem.Move(temp, FilePath, overwrite: true);
        }
        finally
        {
            if (HostFileSystem.FileExists(temp)) HostFileSystem.DeleteFile(temp);
        }
    }
}

internal sealed class RegistryDocument
{
    public RegistryDocument() { }
    public int Version { get; set; } = 1;
    public Dictionary<string, RegistryKeyData> Roots { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class RegistryKeyData
{
    public RegistryKeyData() { }
    public string ClassName { get; set; } = "";
    public long LastWriteTime { get; set; } = DateTime.UtcNow.ToFileTimeUtc();
    public Dictionary<string, RegistryKeyData> SubKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, RegistryValueData> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    [JsonIgnore]
    public bool Deleted { get; set; }
}

internal sealed class RegistryValueData
{
    public RegistryValueData() { }
    public uint Type { get; set; }
    public byte[] Data { get; set; } = [];
}
