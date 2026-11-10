namespace ImaginedNT.Core.Runtime;

public delegate ulong ApiHandler(ApiCallContext context);

public sealed class ApiRegistration
{
    public required string Module { get; init; }
    public required string Name { get; init; }
    public required ApiHandler Handler { get; init; }
    public ApiCategory Category { get; init; } = ApiCategory.Unknown;
    public bool IsPartiallyImagined { get; init; }
    public byte[]? EntryStub { get; init; }
    public string DisplayName => $"{Module}!{Name}";
    public override string ToString() => $"{DisplayName} ({Category})";
}

/// <summary>
/// The managed symbol table.  Imports are looked up here first; anything not found can still
/// be forwarded to the real ntdll/kernel32/user32 on the host.
/// </summary>
public sealed class ApiRegistry
{
    private readonly Dictionary<string, ApiRegistration> _apis = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ApiRegistration> _ordered = new();
    private readonly HashSet<string> _partialModules = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _ordered.Count;
    public IReadOnlyList<ApiRegistration> All => _ordered;

    public static string NormalizeModule(string module)
    {
        if (string.IsNullOrWhiteSpace(module)) return string.Empty;
        var name = module.Replace('/', '\\');
        var slash = name.LastIndexOf('\\');
        if (slash >= 0) name = name[(slash + 1)..];
        if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name.ToLowerInvariant();
    }

    public static string NormalizeName(string name) => name.Trim().ToLowerInvariant();

    public static string Key(string module, string name) => NormalizeModule(module) + "!" + NormalizeName(name);

    private static IEnumerable<string> CandidateModules(string module)
    {
        var normalized = NormalizeModule(module);
        yield return normalized;

        if (normalized.StartsWith("api-ms-win-core-registry-", StringComparison.Ordinal) ||
            normalized.StartsWith("ext-ms-win-advapi32-registry-", StringComparison.Ordinal) ||
            normalized.StartsWith("ext-ms-win-kernelbase-registry-", StringComparison.Ordinal))
            yield return "advapi32";

        // COM API sets are backed by ole32/combase; register implementations against ole32.
        if (normalized.StartsWith("api-ms-win-core-com", StringComparison.Ordinal) ||
            normalized.StartsWith("ext-ms-win-com-", StringComparison.Ordinal) ||
            normalized == "combase")
        {
            yield return "ole32";
            yield return "combase";
        }

        // Many modern binaries import API sets instead of kernel32/ntdll.  The managed layer
        // registers against the classic names, so map the common API-set prefixes.
        if (normalized.StartsWith("api-ms-win-core-", StringComparison.Ordinal) ||
            normalized.StartsWith("ext-ms-win-", StringComparison.Ordinal))
        {
            yield return "kernel32";
            yield return "ntdll";
        }
        else if (normalized.StartsWith("api-ms-win-", StringComparison.Ordinal))
        {
            yield return "kernel32";
            yield return "ntdll";
            yield return "advapi32";
        }

        if (normalized == "kernelbase")
        {
            yield return "kernel32";
            yield return "ntdll";
        }
    }

    public void Register(string module, string name, ApiHandler handler, ApiCategory category = ApiCategory.Unknown,
        params string[] aliases)
        => Register(module, name, handler, category, false, aliases);

    public void RegisterPartiallyImagined(string module, string name, ApiHandler handler,
        ApiCategory category = ApiCategory.Unknown, params string[] aliases)
        => Register(module, name, handler, category, true, aliases);

    internal void Register(string module, string name, ApiHandler handler, ApiCategory category,
        bool partial, string[] aliases, byte[]? entryStub = null)
    {
        var registration = new ApiRegistration
        {
            Module = NormalizeModule(module),
            Name = name,
            Handler = handler,
            Category = category,
            IsPartiallyImagined = partial,
            EntryStub = entryStub
        };
        Add(registration);
        foreach (var alias in aliases)
        {
            var bang = alias.IndexOf('!');
            if (bang <= 0) throw new ArgumentException($"alias must be module!name: {alias}");
            var aliasModule = alias[..bang];
            var aliasName = alias[(bang + 1)..];
            Add(new ApiRegistration
            {
                Module = NormalizeModule(aliasModule),
                Name = aliasName,
                Handler = handler,
                Category = category,
                IsPartiallyImagined = partial,
                EntryStub = entryStub
            });
        }
    }

    public void RegisterAlias(string module, string name, ApiHandler handler, ApiCategory category = ApiCategory.Unknown)
    {
        Add(new ApiRegistration { Module = NormalizeModule(module), Name = name, Handler = handler, Category = category });
    }

    private void Add(ApiRegistration registration)
    {
        var key = Key(registration.Module, registration.Name);
        if (_apis.ContainsKey(key))
            throw new InvalidOperationException($"duplicate managed API registration: {registration.DisplayName}");
        _apis[key] = registration;
        _ordered.Add(registration);
        if (registration.IsPartiallyImagined) _partialModules.Add(registration.Module);
    }

    public bool IsPartiallyImagined(string module) => _partialModules.Contains(NormalizeModule(module));

    public bool TryGetExact(string module, string name, out ApiRegistration? registration) =>
        _apis.TryGetValue(Key(module, name), out registration);

    public bool TryGet(string module, string name, out ApiRegistration? registration)
    {
        if (IsPartiallyImagined(module)) return TryGetExact(module, name, out registration);
        foreach (var candidate in CandidateModules(module))
        {
            if (_apis.TryGetValue(Key(candidate, name), out registration)) return true;
        }
        registration = null;
        return false;
    }

    public bool TryGetByKey(string key, out ApiRegistration? registration)
    {
        var bang = key.IndexOf('!');
        if (bang <= 0)
        {
            registration = null;
            return false;
        }
        return TryGet(key[..bang], key[(bang + 1)..], out registration);
    }

    public IEnumerable<ApiRegistration> WhereCategory(ApiCategory category) => _ordered.Where(a => a.Category == category);
}
