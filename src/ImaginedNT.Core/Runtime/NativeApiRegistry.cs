namespace ImaginedNT.Core.Runtime;

/// <summary>A native export that the runtime is allowed to resolve through the host.</summary>
public sealed record NativeApiDeclaration(string Module, string Name, ApiCategory Category)
{
    public string DisplayName => $"{Module}!{Name}";
    public override string ToString() => DisplayName;
}

/// <summary>
/// The declaration table for native forwards. It is deliberately separate from
/// <see cref="ApiRegistry"/>: a declaration has no managed handler and never replaces the export.
/// </summary>
public sealed class NativeApiRegistry
{
    private readonly Dictionary<string, NativeApiDeclaration> _keys = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<NativeApiDeclaration> _declarations = new();

    /// <summary>Number of primary declarations; aliases do not increase this count.</summary>
    public int Count => _declarations.Count;

    /// <summary>Primary declarations in registration order; aliases are not repeated.</summary>
    public IReadOnlyList<NativeApiDeclaration> All => _declarations;

    /// <summary>Register a native export and optional <c>module!name</c> aliases.</summary>
    /// <exception cref="InvalidOperationException">The module/name or an alias is already declared.</exception>
    public void Register(string module, string name, ApiCategory category = ApiCategory.Unknown,
        params string[] aliases)
    {
        var primary = new NativeApiDeclaration(ApiRegistry.NormalizeModule(module), name, category);
        Add(primary);
        foreach (var alias in aliases)
        {
            var bang = alias.IndexOf('!');
            if (bang <= 0 || bang == alias.Length - 1)
                throw new ArgumentException($"alias must be module!name: {alias}", nameof(aliases));
            Add(new NativeApiDeclaration(
                ApiRegistry.NormalizeModule(alias[..bang]), alias[(bang + 1)..], category), primary);
        }
    }

    public bool IsDeclared(string module, string? name) =>
        name is not null && _keys.ContainsKey(ApiRegistry.Key(module, name));

    public bool TryGet(string module, string name, out NativeApiDeclaration? declaration) =>
        _keys.TryGetValue(ApiRegistry.Key(module, name), out declaration);

    private void Add(NativeApiDeclaration declaration, NativeApiDeclaration? primary = null)
    {
        var key = ApiRegistry.Key(declaration.Module, declaration.Name);
        if (_keys.ContainsKey(key))
            throw new InvalidOperationException($"duplicate native API declaration: {declaration.DisplayName}");
        _keys.Add(key, primary ?? declaration);
        if (primary is null) _declarations.Add(declaration);
    }
}
