using ImaginedNT.Core.Nt;

namespace ImaginedNT.Core.Runtime;

public static class AccountApis
{
    private static bool CanManage(ApiCallContext c) => c.Thread.EffectiveToken.IsSystem || c.Thread.EffectiveToken.Match("Administrators") >= 0;
    private static string Read(ApiCallContext c, ulong pointer) => pointer == 0 ? "" : c.ReadUnicodeString(pointer);
    private static bool LocalServer(ApiCallContext c) => Read(c, c.Arg(0)) is "" or "." or "ImaginedMachine" or @"\\ImaginedMachine";

    [VirtualApi("netapi32.dll", "NetUserAdd", ApiCategory.Security)]
    private static ulong NetUserAdd(ApiCallContext c)
    {
        if (!LocalServer(c)) return Win32Error.NotSupported;
        if (!CanManage(c)) return Win32Error.AccessDenied;
        if (c.Arg32(1) != 1) return 124;
        if (c.Arg(2) == 0) return Win32Error.InvalidParameter;
        if (c.Arg(3) != 0) c.Write(c.Arg(3), 0u);
        var name = Read(c, c.Read<ulong>(c.Arg(2)));
        if (c.Runtime.Security.FindUser(name) is not null) return 2224;
        if (c.Read<uint>(c.Arg(2) + 20) != 1)
        {
            if (c.Arg(3) != 0) c.Write(c.Arg(3), 4u);
            return Win32Error.InvalidParameter;
        }
        try
        {
            c.Runtime.Security.CreateUser(name, Read(c, c.Read<ulong>(c.Arg(2) + 8)));
            if ((c.Read<uint>(c.Arg(2) + 40) & 2) != 0) c.Runtime.Security.SetEnabled(name, false);
            return 0;
        }
        catch (ArgumentException)
        {
            if (c.Arg(3) != 0) c.Write(c.Arg(3), 1u);
            return Win32Error.InvalidParameter;
        }
    }

    [VirtualApi("netapi32.dll", "NetUserDel", ApiCategory.Security)]
    private static ulong NetUserDel(ApiCallContext c)
    {
        if (!LocalServer(c)) return Win32Error.NotSupported;
        if (!CanManage(c)) return Win32Error.AccessDenied;
        try { return c.Runtime.Security.DeleteUser(Read(c, c.Arg(1))) ? 0UL : 2221UL; }
        catch (InvalidOperationException) { return Win32Error.AccessDenied; }
    }

    [VirtualApi("netapi32.dll", "NetUserSetInfo", ApiCategory.Security)]
    private static ulong NetUserSetInfo(ApiCallContext c)
    {
        if (!LocalServer(c)) return Win32Error.NotSupported;
        if (!CanManage(c)) return Win32Error.AccessDenied;
        if (c.Arg(3) == 0) return Win32Error.InvalidParameter;
        if (c.Arg32(2) is not (1003 or 1008)) return 124;
        var name = Read(c, c.Arg(1));
        if (c.Runtime.Security.FindUser(name) is null) return 2221;
        if (c.Arg(4) != 0) c.Write(c.Arg(4), 0u);
        try
        {
            if (c.Arg32(2) == 1003) c.Runtime.Security.SetPassword(name, Read(c, c.Read<ulong>(c.Arg(3))));
            else c.Runtime.Security.SetEnabled(name, (c.Read<uint>(c.Arg(3)) & 2) == 0);
            return 0;
        }
        catch (InvalidOperationException) { return Win32Error.AccessDenied; }
    }

    [VirtualApi("netapi32.dll", "NetLocalGroupAdd", ApiCategory.Security)]
    private static ulong NetLocalGroupAdd(ApiCallContext c)
    {
        if (!LocalServer(c)) return Win32Error.NotSupported;
        if (!CanManage(c)) return Win32Error.AccessDenied;
        if (c.Arg32(1) > 1) return 124;
        if (c.Arg(2) == 0) return Win32Error.InvalidParameter;
        if (c.Arg(3) != 0) c.Write(c.Arg(3), 0u);
        try { c.Runtime.Security.CreateGroup(Read(c, c.Read<ulong>(c.Arg(2)))); return 0; }
        catch (ArgumentException) { return 2223; }
    }

    [VirtualApi("netapi32.dll", "NetLocalGroupAddMembers", ApiCategory.Security)]
    [VirtualApi("netapi32.dll", "NetLocalGroupDelMembers", ApiCategory.Security)]
    private static ulong NetLocalGroupMembers(ApiCallContext c)
    {
        if (!LocalServer(c)) return Win32Error.NotSupported;
        if (!CanManage(c)) return Win32Error.AccessDenied;
        if (c.Arg32(2) != 3) return 124;
        if (c.Arg(3) == 0 && c.Arg32(4) != 0) return Win32Error.InvalidParameter;
        var group = Read(c, c.Arg(1));
        if (!c.Runtime.Security.Groups.Any(g => g.Name.Equals(group, StringComparison.OrdinalIgnoreCase))) return 2220;
        var users = new List<string>();
        for (uint i = 0; i < c.Arg32(4); i++)
        {
            var name = Read(c, c.Read<ulong>(c.Arg(3) + i * 8UL));
            var parts = name.Split('\\', 2);
            if (parts.Length == 2 && parts[0] != "." && !parts[0].Equals("ImaginedMachine", StringComparison.OrdinalIgnoreCase)) return 1387;
            name = parts[^1];
            if (c.Runtime.Security.FindUser(name) is null) return 1387;
            users.Add(name);
        }
        foreach (var user in users) c.Runtime.Security.SetGroupMembership(user, group, c.Name == "NetLocalGroupAddMembers");
        return 0;
    }
}
