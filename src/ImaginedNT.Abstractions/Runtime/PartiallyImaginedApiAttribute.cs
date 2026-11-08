namespace ImaginedNT.Core.Runtime;

/// <summary>完全替换指定 DLL 的一个导出，其余导出保留本机实现。</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class PartiallyImaginedApiAttribute : VirtualApiAttribute
{
    public PartiallyImaginedApiAttribute(string module, string name, ApiCategory category = ApiCategory.Unknown,
        params string[] aliases) : base(module, name, category, aliases) { }

    public PartiallyImaginedApiAttribute(string module, ushort ordinal, ApiCategory category = ApiCategory.Unknown,
        params string[] aliases) : base(module, $"#{ordinal}", category, aliases) { }
}
