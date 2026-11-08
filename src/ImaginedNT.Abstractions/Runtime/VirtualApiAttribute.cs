namespace ImaginedNT.Core.Runtime;

/// <summary>
/// Declares that a method implements a virtual Win32/NT entry point. This is the managed
/// counterpart of <c>[DllImport]</c>: a guest import is bound to this C# method instead of being
/// resolved against the host's real DLLs.
/// </summary>
/// <remarks>
/// <para>
/// The annotated method must be <c>static</c>, accept a single <c>ApiCallContext</c> and
/// return <see cref="ulong"/>. <c>VirtualApiScanner</c> discovers these methods when the
/// runtime starts, so API implementations register themselves declaratively instead of being
/// added to one long list.
/// </para>
/// <para>
/// The attribute may be applied more than once per method, which is how a single implementation
/// can cover e.g. both <c>kernel32!RtlNtStatusToDosError</c> and <c>ntdll!RtlNtStatusToDosError</c>.
/// Additional spellings of the same module/name can be supplied as <c>module!name</c> aliases.
/// </para>
/// <example>
/// <code>
/// [VirtualApi("kernel32.dll", "GetCurrentProcessId", ApiCategory.Process)]
/// private static ulong GetCurrentProcessId(ApiCallContext ctx) => (ulong)ctx.Process.Id;
/// </code>
/// </example>
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public class VirtualApiAttribute : Attribute
{
    public VirtualApiAttribute(string module, string name, ApiCategory category = ApiCategory.Unknown,
        params string[] aliases)
    {
        if (string.IsNullOrWhiteSpace(module)) throw new ArgumentException("module must not be empty", nameof(module));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name must not be empty", nameof(name));
        Module = module;
        Name = name;
        Category = category;
        Aliases = aliases ?? [];
    }

    /// <summary>Module that exports the virtual entry point, e.g. <c>kernel32.dll</c> or <c>ntdll.dll</c>.</summary>
    public string Module { get; }

    /// <summary>Exported symbol name, e.g. <c>CreateFileW</c>.</summary>
    public string Name { get; }

    /// <summary>Diagnostics category used by <c>--list-apis</c> and tracing.</summary>
    public ApiCategory Category { get; }

    /// <summary>Extra <c>module!name</c> spellings that map to the same implementation.</summary>
    public IReadOnlyList<string> Aliases { get; }

    /// <summary>Optional x64 entry code for APIs that must access the original guest registers and stack.</summary>
    public byte[]? EntryStub { get; set; }

    public override string ToString() => $"{Module}!{Name}";
}
