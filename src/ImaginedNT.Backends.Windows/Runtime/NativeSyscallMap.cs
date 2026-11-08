using ImaginedNT.Core.Native;
using ImaginedNT.Core.Pe;

namespace ImaginedNT.Core.Runtime;

/// <summary>
/// Discovers the host build's real NT syscall numbers by reading the first bytes of ntdll's
/// Nt*/Zw* stubs.  A Wine-like layer cannot hard-code these numbers: they change between
/// Windows builds.  Numbers discovered here let the VEH trap emulate direct syscalls that an
/// application issues without going through the (possibly hooked) ntdll exports.
/// </summary>
public static class NativeSyscallMap
{
    private static readonly Lazy<IReadOnlyDictionary<uint, string>> NumberToName = new(Build, isThreadSafe: true);
    private static readonly Lazy<IReadOnlyDictionary<string, uint>> NameToNumber = new(BuildReverse, isThreadSafe: true);

    public static int Count => NumberToName.Value.Count;
    public static IReadOnlyDictionary<uint, string> Numbers => NumberToName.Value;

    public static bool TryGetName(uint number, out string name) => NumberToName.Value.TryGetValue(number, out name!);

    public static bool TryGetNumber(string name, out uint number)
    {
        if (NameToNumber.Value.TryGetValue(name, out number)) return true;
        if (!name.StartsWith("Nt", StringComparison.Ordinal) && !name.StartsWith("Zw", StringComparison.Ordinal))
            return NameToNumber.Value.TryGetValue("Nt" + name, out number);
        return false;
    }

    private static IReadOnlyDictionary<uint, string> Build()
    {
        var map = new Dictionary<uint, string>();
        try
        {
            var path = Path.Combine(Environment.SystemDirectory, "ntdll.dll");
            var image = PeImage.Load(path);
            foreach (var export in image.Exports)
            {
                if (export.Name is null || export.Forwarder is not null) continue;
                if (!export.Name.StartsWith("Nt", StringComparison.Ordinal) &&
                    !export.Name.StartsWith("Zw", StringComparison.Ordinal)) continue;
                var stub = image.ReadRva(export.Rva, 32);
                var number = ExtractNumber(stub);
                if (number != 0) map.TryAdd(number, export.Name);
            }
        }
        catch
        {
            // An unusual build can leave the map empty; virtual syscalls still work.
        }
        return map;
    }

    private static IReadOnlyDictionary<string, uint> BuildReverse()
    {
        var reverse = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (var pair in NumberToName.Value)
        {
            reverse.TryAdd(pair.Value, pair.Key);
            if (pair.Value.StartsWith("Zw", StringComparison.Ordinal))
                reverse.TryAdd("Nt" + pair.Value[2..], pair.Key);
            if (pair.Value.StartsWith("Nt", StringComparison.Ordinal))
                reverse.TryAdd("Zw" + pair.Value[2..], pair.Key);
        }
        return reverse;
    }

    /// <summary>Find the "mov eax, imm32" at the start of a Windows x64 syscall stub.</summary>
    private static uint ExtractNumber(ReadOnlySpan<byte> stub)
    {
        for (var i = 0; i + 5 <= stub.Length; i++)
        {
            if (stub[i] != 0xB8) continue;
            var value = (uint)(stub[i + 1] | (stub[i + 2] << 8) | (stub[i + 3] << 16) | (stub[i + 4] << 24));
            if (value is > 0 and < 0x4000) return value;
        }
        return 0;
    }
}
