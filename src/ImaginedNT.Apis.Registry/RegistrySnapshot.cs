namespace ImaginedNT.Core.Runtime;

public sealed record RegistryValueSnapshot(string Name, uint Type, byte[] Data);

public sealed record RegistryKeySnapshot(string Path, IReadOnlyList<string> SubKeys,
    IReadOnlyList<RegistryValueSnapshot> Values);
