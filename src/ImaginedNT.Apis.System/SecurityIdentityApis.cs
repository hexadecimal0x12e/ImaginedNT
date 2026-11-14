using System.Security.Principal;
using System.Text;
using ImaginedNT.Core.Nt;
using ImaginedNT.Core.Security;
using static ImaginedNT.Apis.ApiHelpers;

namespace ImaginedNT.Core.Runtime;

public static class SecurityIdentityApis
{
    private static Encoding TextEncoding(ApiCallContext c) => c.Name.EndsWith('W')
        ? Encoding.Unicode : CodePagesEncodingProvider.Instance.GetEncoding(0) ?? Encoding.UTF8;

    private static string ReadText(ApiCallContext c, ulong address) => address == 0 ? "" :
        c.Name.EndsWith('W') ? c.ReadUnicodeString(address) : c.ReadAnsiString(address);

    private static string Domain(SecurityPrincipal principal) => principal.Sid switch
    {
        "S-1-1-0" => "",
        "S-1-5-18" or "S-1-5-11" => "NT AUTHORITY",
        _ => principal.Sid.StartsWith("S-1-5-32-", StringComparison.Ordinal) ? "BUILTIN" : "ImaginedMachine"
    };

    [VirtualApi("secur32.dll", "GetUserNameExW", ApiCategory.Security, "sspicli!GetUserNameExW")]
    [VirtualApi("secur32.dll", "GetUserNameExA", ApiCategory.Security, "sspicli!GetUserNameExA")]
    private static ulong GetUserNameEx(ApiCallContext c)
    {
        if (c.Arg(2) == 0 || c.Arg32(0) == 0) return BoolResult(c, false, Win32Error.InvalidParameter);
        if (c.Arg32(0) != 2) return BoolResult(c, false, 1332);
        var user = c.Thread.EffectiveToken.User;
        var value = Domain(user) + "\\" + user.Name;
        var encoding = TextEncoding(c);
        var bytes = encoding.GetBytes(value + '\0');
        var length = (uint)(c.Name.EndsWith('W') ? value.Length : bytes.Length - 1);
        var capacity = c.Read<uint>(c.Arg(2));
        if (c.Arg(1) == 0 || capacity <= length)
        {
            c.Write(c.Arg(2), length + 1);
            return BoolResult(c, false, 234);
        }
        c.WriteBytes(c.Arg(1), bytes);
        c.Write(c.Arg(2), length);
        return 1;
    }

    [VirtualApi("advapi32.dll", "LookupAccountSidW", ApiCategory.Security, "kernelbase!LookupAccountSidW")]
    [VirtualApi("advapi32.dll", "LookupAccountSidA", ApiCategory.Security, "kernelbase!LookupAccountSidA")]
    private static ulong LookupAccountSid(ApiCallContext c)
    {
        if (c.Arg(1) == 0 || c.Arg(3) == 0 || c.Arg(5) == 0) return BoolResult(c, false, Win32Error.InvalidParameter);
        var system = ReadText(c, c.Arg(0)).TrimStart('\\');
        if (system.Length != 0 && system != "." && !system.Equals("ImaginedMachine", StringComparison.OrdinalIgnoreCase))
            return BoolResult(c, false, Win32Error.NotSupported);
        if (c.Read<byte>(c.Arg(1)) != 1 || c.Read<byte>(c.Arg(1) + 1) > 15) return BoolResult(c, false, 1337);
        string sid;
        try { sid = new SecurityIdentifier(c.ReadBytes(c.Arg(1), 8 + c.Read<byte>(c.Arg(1) + 1) * 4), 0).Value; }
        catch (ArgumentException) { return BoolResult(c, false, 1337); }
        var token = c.Thread.EffectiveToken;
        var principal = new[] { token.User }.Concat(token.Groups).Concat(c.Runtime.Security.Users).Concat(c.Runtime.Security.Groups)
            .FirstOrDefault(p => p.Sid.Equals(sid, StringComparison.OrdinalIgnoreCase));
        if (principal is null) return BoolResult(c, false, 1332);
        var encoding = TextEncoding(c);
        var name = encoding.GetBytes(principal.Name + '\0');
        var domainName = Domain(principal);
        var domain = encoding.GetBytes(domainName + '\0');
        var nameLength = (uint)(c.Name.EndsWith('W') ? principal.Name.Length : name.Length - 1);
        var domainLength = (uint)(c.Name.EndsWith('W') ? domainName.Length : domain.Length - 1);
        var nameCapacity = c.Read<uint>(c.Arg(3));
        var domainCapacity = c.Read<uint>(c.Arg(5));
        if (c.Arg(2) == 0 || c.Arg(4) == 0 || nameCapacity <= nameLength || domainCapacity <= domainLength)
        {
            c.Write(c.Arg(3), nameLength + 1);
            c.Write(c.Arg(5), domainLength + 1);
            return BoolResult(c, false, Win32Error.InsufficientBuffer);
        }
        c.WriteBytes(c.Arg(2), name);
        c.WriteBytes(c.Arg(4), domain);
        c.Write(c.Arg(3), nameLength);
        c.Write(c.Arg(5), domainLength);
        if (c.Arg(6) != 0) c.Write(c.Arg(6), sid == token.User.Sid || c.Runtime.Security.Users.Any(p => p.Sid == sid) ? 1u :
            sid.StartsWith("S-1-5-32-", StringComparison.Ordinal) ? 4u : sid.StartsWith("S-1-5-21-", StringComparison.Ordinal) ? 2u : 5u);
        return 1;
    }
}
