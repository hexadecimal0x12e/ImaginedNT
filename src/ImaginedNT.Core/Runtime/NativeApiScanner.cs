using System.Reflection;

namespace ImaginedNT.Core.Runtime;

/// <summary>
/// Discovers assembly-level <see cref="PartiallyImaginedNativeApiAttribute"/> declarations. This runs alongside
/// <see cref="VirtualApiScanner"/> when an API assembly is registered.
/// </summary>
public static class NativeApiScanner
{
    /// <summary>Register every native declaration in <paramref name="assembly"/>.</summary>
    /// <returns>The number of primary declarations added, excluding aliases.</returns>
    /// <exception cref="InvalidOperationException">A declaration is duplicated or malformed.</exception>
    public static int Register(Assembly assembly, NativeApiRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(registry);

        var before = registry.Count;
        foreach (var attribute in assembly.GetCustomAttributes(typeof(PartiallyImaginedNativeApiAttribute), inherit: false)
                     .OfType<PartiallyImaginedNativeApiAttribute>())
        {
            try
            {
                registry.Register(attribute.Module, attribute.Name, attribute.Category, [.. attribute.Aliases]);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                throw new InvalidOperationException(
                    $"{ex.Message}; declared by assembly {assembly.GetName().Name}", ex);
            }
        }
        return registry.Count - before;
    }
}
