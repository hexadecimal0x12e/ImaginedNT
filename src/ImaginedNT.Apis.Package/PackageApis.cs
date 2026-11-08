using System.Runtime.InteropServices;
using System.Text;
using ImaginedNT.Core.Objects;

namespace ImaginedNT.Core.Runtime;

public static partial class PackageApis
{
    private const uint InvalidParameter = 87, InsufficientBuffer = 122, NoPackage = 15700, NotFound = 1168;

    private static ulong StringResult(ApiCallContext c, string value, int lengthIndex)
    {
        var error = PrepareBuffer(c, lengthIndex, (uint)value.Length + 1);
        if (error == 0) PutString(c, c.Arg(lengthIndex + 1), value);
        return error;
    }

    private static uint PrepareBuffer(ApiCallContext c, int index, uint required)
    {
        if (c.Arg(index) == 0) return InvalidParameter;
        var capacity = c.Read<uint>(c.Arg(index));
        if (capacity != 0 && c.Arg(index + 1) == 0) return InvalidParameter;
        c.Write(c.Arg(index), required);
        return capacity < required ? InsufficientBuffer : 0;
    }

    private static void PutString(ApiCallContext c, ulong address, string value) =>
        c.WriteBytes(address, Encoding.Unicode.GetBytes(value + '\0'));

    private static ImaginedProcess? Process(ApiCallContext c) =>
        c.Arg(0) == ulong.MaxValue ? c.Process :
        c.TryGetObject(c.Arg(0), out var obj) && obj is ProcessObject p ? p.Process : null;

    private static ulong IdentityString(ApiCallContext c, bool current, Func<PackageIdentity, string> select, bool app = false)
    {
        var process = current ? c.Process : Process(c);
        if (process is null) return 6;
        if (process.PackageIdentity is not { } p) return app ? 15703u : NoPackage;
        return StringResult(c, select(p), current ? 0 : 1);
    }

    [VirtualApi("kernel32", "GetCurrentPackageFullName", ApiCategory.Package)]
    private static ulong GetCurrentPackageFullName(ApiCallContext c) => IdentityString(c, true, p => p.FullName);
    [VirtualApi("kernel32", "GetPackageFullName", ApiCategory.Package)]
    private static ulong GetPackageFullName(ApiCallContext c) => IdentityString(c, false, p => p.FullName);
    [VirtualApi("kernel32", "GetCurrentPackageFamilyName", ApiCategory.Package)]
    private static ulong GetCurrentPackageFamilyName(ApiCallContext c) => IdentityString(c, true, p => p.FamilyName);
    [VirtualApi("kernel32", "GetPackageFamilyName", ApiCategory.Package)]
    private static ulong GetPackageFamilyName(ApiCallContext c) => IdentityString(c, false, p => p.FamilyName);
    [VirtualApi("kernel32", "GetCurrentPackagePath", ApiCategory.Package)]
    private static ulong GetCurrentPackagePath(ApiCallContext c) => IdentityString(c, true, p => p.InstallPath);
    [VirtualApi("kernel32", "GetCurrentApplicationUserModelId", ApiCategory.Package)]
    private static ulong GetCurrentApplicationUserModelId(ApiCallContext c) => IdentityString(c, true, p => p.ApplicationUserModelId, true);
    [VirtualApi("kernel32", "GetApplicationUserModelId", ApiCategory.Package)]
    private static ulong GetApplicationUserModelId(ApiCallContext c) => IdentityString(c, false, p => p.ApplicationUserModelId, true);

    [VirtualApi("kernel32", "GetCurrentPackageId", ApiCategory.Package)]
    private static ulong GetCurrentPackageId(ApiCallContext c) =>
        c.Process.PackageIdentity is { } p ? WriteId(c, p, 0) : NoPackage;
    [VirtualApi("kernel32", "GetPackageId", ApiCategory.Package)]
    private static ulong GetPackageId(ApiCallContext c)
    {
        var process = Process(c);
        if (process is null) return 6;
        return process.PackageIdentity is { } p ? WriteId(c, p, 1) : NoPackage;
    }

    // PACKAGE_ID is packed to 4 bytes; x64 pointers occupy offsets 16, 24, 32, 40.
    private static ulong WriteId(ApiCallContext c, PackageIdentity p, int lengthIndex)
    {
        var strings = new[] { p.Name, p.Publisher, p.ResourceId, p.EffectivePublisherId };
        var error = PrepareBuffer(c, lengthIndex, (uint)(48 + strings.Sum(s => (s.Length + 1) * 2)));
        if (error != 0) return error;
        WriteIdFields(c, p, c.Arg(lengthIndex + 1));
        var cursor = c.Arg(lengthIndex + 1) + 48;
        for (var i = 0; i < strings.Length; i++)
            WriteStringPointer(c, c.Arg(lengthIndex + 1) + 16 + (ulong)i * 8, strings[i], ref cursor);
        return 0;
    }

    private static void WriteIdFields(ApiCallContext c, PackageIdentity p, ulong address)
    {
        c.Write(address, 0u);
        c.Write(address + 4, p.ProcessorArchitecture);
        c.Write(address + 8, ((ulong)p.Version.Major << 48) | ((ulong)p.Version.Minor << 32) |
            ((ulong)p.Version.Build << 16) | (uint)p.Version.Revision);
    }

    private static void WriteStringPointer(ApiCallContext c, ulong address, string value, ref ulong cursor)
    {
        c.Write(address, cursor);
        PutString(c, cursor, value);
        cursor += (ulong)(value.Length + 1) * 2;
    }

    private static bool Matches(ApiCallContext c, string name) =>
        c.Process.PackageIdentity is { } p && string.Equals(p.FullName, name, StringComparison.OrdinalIgnoreCase);

    [VirtualApi("kernel32", "GetPackagePathByFullName", ApiCategory.Package)]
    [VirtualApi("kernel32", "GetStagedPackagePathByFullName", ApiCategory.Package)]
    private static ulong GetPackagePathByFullName(ApiCallContext c)
    {
        if (c.Arg(0) == 0) return InvalidParameter;
        return Matches(c, c.ReadUnicodeString(c.Arg(0))) ? StringResult(c, c.Process.PackageIdentity!.InstallPath, 1) : NotFound;
    }

    [VirtualApi("kernel32", "GetPackagePath", ApiCategory.Package)]
    private static ulong GetPackagePath(ApiCallContext c)
    {
        if (c.Arg(0) == 0 || c.Arg32(1) != 0) return InvalidParameter;
        var length = 0u;
        var error = NativePackageFullNameFromId((nint)c.Arg(0), ref length, 0);
        if (error != InsufficientBuffer) return (uint)error;
        var buffer = Marshal.AllocHGlobal(checked((int)length * 2));
        try
        {
            error = NativePackageFullNameFromId((nint)c.Arg(0), ref length, buffer);
            if (error != 0) return (uint)error;
            return Matches(c, Marshal.PtrToStringUni(buffer)!) ? StringResult(c, c.Process.PackageIdentity!.InstallPath, 2) : NotFound;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [VirtualApi("kernel32", "PackageIdFromFullName", ApiCategory.Package)]
    private static ulong PackageIdFromFullName(ApiCallContext c)
    {
        if (c.Arg(0) == 0 || (c.Arg32(1) & ~0x100u) != 0) return InvalidParameter;
        if (c.Arg32(1) == 0x100)
            return Matches(c, c.ReadUnicodeString(c.Arg(0))) ? WriteId(c, c.Process.PackageIdentity!, 2) : NotFound;
        return (uint)NativePackageIdFromFullName((nint)c.Arg(0), 0, (nint)c.Arg(2), (nint)c.Arg(3));
    }

    private sealed class PackageReference(PackageIdentity identity) : NtObject
    {
        public PackageIdentity Identity { get; } = identity;
        public override ObjectType ObjectType => ObjectType.Unknown;
    }

    [VirtualApi("kernel32", "OpenPackageInfoByFullName", ApiCategory.Package)]
    private static ulong OpenPackageInfoByFullName(ApiCallContext c)
    {
        if (c.Arg(0) == 0 || c.Arg32(1) != 0 || c.Arg(2) == 0) return InvalidParameter;
        c.Write(c.Arg(2), 0UL);
        if (!Matches(c, c.ReadUnicodeString(c.Arg(0)))) return NotFound;
        using var reference = new PackageReference(c.Process.PackageIdentity!);
        c.Write(c.Arg(2), c.Handles.Create(reference));
        return 0;
    }

    [VirtualApi("kernel32", "ClosePackageInfo", ApiCategory.Package)]
    private static ulong ClosePackageInfo(ApiCallContext c)
    {
        if (!c.TryGetObject(c.Arg(0), out var obj) || obj is not PackageReference) return 6;
        return c.Handles.Close(c.Arg(0)) == 0 ? 0UL : 6UL;
    }

    [VirtualApi("kernel32", "GetPackageInfo", ApiCategory.Package)]
    private static ulong GetPackageInfo(ApiCallContext c) =>
        c.TryGetObject(c.Arg(0), out var obj) && obj is PackageReference p ? WriteInfo(c, p.Identity, 1) : 6;
    [VirtualApi("kernel32", "GetCurrentPackageInfo", ApiCategory.Package)]
    private static ulong GetCurrentPackageInfo(ApiCallContext c) =>
        c.Process.PackageIdentity is { } p ? WriteInfo(c, p, 0) : NoPackage;

    private static ulong WriteInfo(ApiCallContext c, PackageIdentity p, int flagsIndex)
    {
        var included = c.Arg32(flagsIndex) == 0 || (c.Arg32(flagsIndex) & 0x10) != 0;
        c.WriteUInt32IfNotNull(c.Arg(flagsIndex + 3), included ? 1u : 0u);
        var strings = new[] { p.InstallPath, p.FullName, p.FamilyName, p.Name, p.Publisher, p.ResourceId, p.EffectivePublisherId };
        var error = PrepareBuffer(c, flagsIndex + 1, included ? (uint)(80 + strings.Sum(s => (s.Length + 1) * 2)) : 0);
        if (error != 0 || !included) return error;
        var buffer = c.Arg(flagsIndex + 2);
        c.Write(buffer, 0u);
        c.Write(buffer + 4, 0u);
        WriteIdFields(c, p, buffer + 32);
        var cursor = buffer + 80;
        for (var i = 0; i < strings.Length; i++)
            WriteStringPointer(c, buffer + (i < 3 ? 8UL + (ulong)i * 8 : 48UL + (ulong)(i - 3) * 8), strings[i], ref cursor);
        return 0;
    }

    [VirtualApi("kernel32", "GetPackageApplicationIds", ApiCategory.Package)]
    private static ulong GetPackageApplicationIds(ApiCallContext c)
    {
        if (!c.TryGetObject(c.Arg(0), out var obj) || obj is not PackageReference p) return 6;
        var id = p.Identity.ApplicationUserModelId;
        c.WriteUInt32IfNotNull(c.Arg(3), 1);
        var error = PrepareBuffer(c, 1, (uint)(8 + (id.Length + 1) * 2));
        if (error != 0) return error;
        c.Write(c.Arg(2), c.Arg(2) + 8);
        PutString(c, c.Arg(2) + 8, id);
        return 0;
    }

    [VirtualApi("kernel32", "GetPackagesByPackageFamily", ApiCategory.Package)]
    private static ulong GetPackagesByPackageFamily(ApiCallContext c) => EnumeratePackages(c, false);
    [VirtualApi("kernel32", "FindPackagesByPackageFamily", ApiCategory.Package)]
    private static ulong FindPackagesByPackageFamily(ApiCallContext c) => EnumeratePackages(c, true);

    private static ulong EnumeratePackages(ApiCallContext c, bool filtered)
    {
        var index = filtered ? 2 : 1;
        if (c.Arg(0) == 0 || c.Arg(index) == 0 || c.Arg(index + 2) == 0) return InvalidParameter;
        var capacity = c.Read<uint>(c.Arg(index));
        var length = c.Read<uint>(c.Arg(index + 2));
        if ((capacity != 0 && c.Arg(index + 1) == 0) || (length != 0 && c.Arg(index + 3) == 0)) return InvalidParameter;
        var p = c.Process.PackageIdentity;
        var match = p is not null && string.Equals(p.FamilyName, c.ReadUnicodeString(c.Arg(0)), StringComparison.OrdinalIgnoreCase) &&
            (!filtered || (c.Arg32(1) & 0xF) == 0);
        var required = match ? (uint)p!.FullName.Length + 1 : 0;
        c.Write(c.Arg(index), match ? 1u : 0u);
        c.Write(c.Arg(index + 2), required);
        if (!match) return 0;
        if (capacity < 1 || length < required) return InsufficientBuffer;
        c.Write(c.Arg(index + 1), c.Arg(index + 3));
        PutString(c, c.Arg(index + 3), p!.FullName);
        if (filtered) c.WriteUInt32IfNotNull(c.Arg(6), 0);
        return 0;
    }

    [DllImport("kernel32.dll", EntryPoint = "PackageIdFromFullName", ExactSpelling = true)]
    private static extern int NativePackageIdFromFullName(nint name, uint flags, nint length, nint buffer);
    [DllImport("kernel32.dll", EntryPoint = "PackageFullNameFromId", ExactSpelling = true)]
    private static extern int NativePackageFullNameFromId(nint id, ref uint length, nint buffer);
}
