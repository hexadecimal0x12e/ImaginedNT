using System.Reflection;

namespace ImaginedNT.Core.Runtime;

/// <summary>
/// Discovers <see cref="VirtualApiAttribute"/> methods and mirrors them into an
/// <see cref="ApiRegistry"/>. Running the scan once at startup removes the need for one central
/// method that registers every managed API by hand.
/// </summary>
public static class VirtualApiScanner
{
    private const BindingFlags MethodFlags =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

    /// <summary>
    /// Registers every annotated static method in <paramref name="assembly"/>. Returns the number
    /// of entry points added, aliases included.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// An annotated method has the wrong shape, or two methods claim the same module/entry point.
    /// </exception>
    public static int Register(Assembly assembly, ApiRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(registry);

        var entries = new List<Entry>();
        var handlers = new Dictionary<MethodInfo, ApiHandler>();

        foreach (var type in LoadableTypes(assembly))
        {
            foreach (var method in type.GetMethods(MethodFlags))
            {
                foreach (var attribute in method.GetCustomAttributes<VirtualApiAttribute>(inherit: false))
                {
                    Validate(type, method);
                    if (!handlers.TryGetValue(method, out var handler))
                    {
                        handler = method.CreateDelegate<ApiHandler>();
                        handlers.Add(method, handler);
                    }
                    entries.Add(new Entry(type, method, attribute, handler));
                }
            }
        }

        // Reflection order is not guaranteed. Sorting keeps ApiRegistry.All (and therefore the
        // --list-apis output and duplicate diagnostics) stable between runs.
        entries.Sort(static (left, right) =>
        {
            var result = string.Compare(ApiRegistry.NormalizeModule(left.Attribute.Module),
                ApiRegistry.NormalizeModule(right.Attribute.Module), StringComparison.Ordinal);
            if (result != 0) return result;
            result = string.Compare(ApiRegistry.NormalizeName(left.Attribute.Name),
                ApiRegistry.NormalizeName(right.Attribute.Name), StringComparison.Ordinal);
            if (result != 0) return result;
            return string.CompareOrdinal(left.Method.Name, right.Method.Name);
        });

        var before = registry.Count;
        foreach (var entry in entries)
        {
            try
            {
                registry.Register(entry.Attribute.Module, entry.Attribute.Name, entry.Handler,
                    entry.Attribute.Category, entry.Attribute is PartiallyImaginedApiAttribute,
                    [.. entry.Attribute.Aliases], entry.Attribute.EntryStub);
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException(
                    $"{ex.Message}; declared by {entry.Type.FullName}.{entry.Method.Name}", ex);
            }
        }
        return registry.Count - before;
    }

    private static void Validate(Type type, MethodInfo method)
    {
        var signature = $"{type.FullName}.{method.Name}";
        if (!method.IsStatic)
            throw new InvalidOperationException($"{signature}: [VirtualApi] methods must be static");
        if (method.ReturnType != typeof(ulong))
            throw new InvalidOperationException($"{signature}: [VirtualApi] methods must return ulong");
        var parameters = method.GetParameters();
        if (parameters.Length != 1 || parameters[0].ParameterType != typeof(ApiCallContext))
            throw new InvalidOperationException(
                $"{signature}: [VirtualApi] methods must take a single {nameof(ApiCallContext)} parameter");
        if (method.ContainsGenericParameters)
            throw new InvalidOperationException($"{signature}: [VirtualApi] methods must not be generic");
    }

    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            throw new InvalidOperationException(
                $"could not enumerate Virtual APIs in '{assembly.FullName}': " +
                string.Join("; ", ex.LoaderExceptions.OfType<Exception>().Select(e => e.Message)), ex);
        }
    }

    private readonly record struct Entry(Type Type, MethodInfo Method, VirtualApiAttribute Attribute,
        ApiHandler Handler);
}
