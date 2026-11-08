namespace ImaginedNT.Core.Runtime;

/// <summary>
/// Declares that a native export is intentionally part of the forwardable surface and is safe to
/// leave to the host DLL. This is the native-side counterpart of <see cref="VirtualApiAttribute"/>:
/// it carries no managed handler and does not replace the export; it only records that the
/// entry point may be resolved natively under a declaration-checking policy.
/// </summary>
/// <remarks>
/// <para>
/// Apply the attribute at assembly level. The CRT declaration assembly
/// (<c>ImaginedNT.Apis.Crt</c>) uses this to describe the <c>api-ms-win-crt-*</c> exports that are
/// pure data/arithmetic and safe to forward to <c>ucrtbase</c>.
/// </para>
/// <para>
/// The runtime's default <see cref="NativeApiPolicy.AllowAll"/> ignores these declarations. Set
/// <see cref="RuntimeOptions.NativeApiPolicy"/> to <see cref="NativeApiPolicy.DeclaredOnly"/> to
/// allow native forwarding only for declared module/name pairs.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [assembly: PartiallyImaginedNativeApi("api-ms-win-crt-math-l1-1-0.dll", "sin", ApiCategory.System, "ucrtbase!sin")]
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true, Inherited = false)]
public sealed class PartiallyImaginedNativeApiAttribute : Attribute
{
    public PartiallyImaginedNativeApiAttribute(string module, string name, ApiCategory category = ApiCategory.Unknown,
        params string[] aliases)
    {
        if (string.IsNullOrWhiteSpace(module)) throw new ArgumentException("module must not be empty", nameof(module));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name must not be empty", nameof(name));
        Module = module;
        Name = name;
        Category = category;
        Aliases = aliases ?? [];
    }

    /// <summary>Module that exports the native entry point, e.g. <c>api-ms-win-crt-math-l1-1-0.dll</c>.</summary>
    public string Module { get; }

    /// <summary>Exported symbol name, e.g. <c>sin</c>.</summary>
    public string Name { get; }

    /// <summary>Diagnostics category used by the API listing.</summary>
    public ApiCategory Category { get; }

    /// <summary>Extra <c>module!name</c> spellings that resolve to the same native export.</summary>
    public IReadOnlyList<string> Aliases { get; }

    public override string ToString() => $"{Module}!{Name}";
}
