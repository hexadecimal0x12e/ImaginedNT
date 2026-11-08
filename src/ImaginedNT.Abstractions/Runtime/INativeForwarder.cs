namespace ImaginedNT.Core.Runtime;

public interface INativeForwarder : IDisposable
{
    ulong Resolve(string moduleName, string? exportName, ushort? ordinal, out string detail);
    IntPtr GetModuleHandle(string moduleName);
    string? GetModuleName(IntPtr module) => null;
    IntPtr GetProcAddress(IntPtr module, string name);
    IntPtr GetProcAddress(IntPtr module, IntPtr ordinal);
    ulong LoadLibrary(string moduleName);
}
