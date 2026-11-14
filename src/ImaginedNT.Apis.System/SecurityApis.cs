using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Text;
using ImaginedNT.Core.Diagnostics;
using ImaginedNT.Core.Nt;
using ImaginedNT.Core.Objects;
using ImaginedNT.Core.Security;
using static ImaginedNT.Apis.ApiHelpers;

namespace ImaginedNT.Core.Runtime;

public static class SecurityApis
{
    [DllImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out ulong luid);

    private const uint TokenQuery = 8, TokenDuplicate = 2, TokenImpersonate = 4, TokenAllAccess = 0x000F01FF;

    private static bool CanInspect(ApiCallContext c, AccessToken token, bool asSelf = false)
    {
        var caller = asSelf ? c.Process.Token : c.Thread.EffectiveToken;
        return caller.IsSystem || caller.User.Sid == token.User.Sid || caller.HasPrivilege("SeDebugPrivilege");
    }

    private static ulong OpenToken(ApiCallContext c, AccessToken? token, ulong output, bool impersonation = false)
    {
        var nt = c.Name.StartsWith("Nt", StringComparison.Ordinal);
        var access = c.Arg32(1);
        if ((access & (NtAccess.GenericAll | NtAccess.MaximumAllowed)) != 0) access = TokenAllAccess;
        if ((access & NtAccess.GenericRead) != 0) access = (access & ~NtAccess.GenericRead) | 0x00020008;
        if ((access & NtAccess.GenericWrite) != 0) access = (access & ~NtAccess.GenericWrite) | 0x000200E0;
        uint status = output == 0 ? NtStatus.InvalidParameter : token is null ? 0xC000007C :
            (access & ~TokenAllAccess) != 0 ? NtStatus.AccessDenied : NtStatus.Success;
        if (output != 0) c.Write(output, 0UL);
        if (status == NtStatus.Success)
        {
            using var obj = new TokenObject(token!, impersonation);
            c.Write(output, c.Handles.Create(obj, access));
        }
        return nt ? status : BoolResult(c, status == NtStatus.Success, status == 0xC000007C ? 1008 : Win32FromStatus(status));
    }

    [VirtualApi("advapi32.dll", "OpenProcessToken", ApiCategory.Security, "kernelbase!OpenProcessToken")]
    [VirtualApi("ntdll.dll", "NtOpenProcessToken", ApiCategory.Security, "ntdll!ZwOpenProcessToken")]
    private static ulong OpenProcessToken(ApiCallContext c)
    {
        AccessToken? token = null;
        if (c.Arg(0) == CurrentProcessPseudoHandle) token = c.Process.Token;
        else if (c.Handles.TryGet(c.Arg(0), out var obj) && obj is ProcessObject p) token = p.Process.Token;
        else if (obj is SystemProcessObject) token = c.Runtime.Security.SystemToken;
        if (token is null) return c.Name.StartsWith("Nt") ? NtStatus.InvalidHandle : BoolResult(c, false, Win32Error.InvalidHandle);
        if (!CanInspect(c, token)) return c.Name.StartsWith("Nt") ? NtStatus.AccessDenied : BoolResult(c, false, Win32Error.AccessDenied);
        return OpenToken(c, token, c.Arg(2));
    }

    [VirtualApi("advapi32.dll", "OpenThreadToken", ApiCategory.Security, "kernelbase!OpenThreadToken")]
    [VirtualApi("ntdll.dll", "NtOpenThreadToken", ApiCategory.Security, "ntdll!ZwOpenThreadToken")]
    private static ulong OpenThreadToken(ApiCallContext c)
    {
        var thread = c.Arg(0) == CurrentThreadPseudoHandle ? c.Thread : (GetObject(c, c.Arg(0)) as ThreadObject)?.Thread;
        if (thread is null) return c.Name.StartsWith("Nt") ? NtStatus.InvalidHandle : BoolResult(c, false, Win32Error.InvalidHandle);
        if (!CanInspect(c, thread.EffectiveToken, c.ArgBool(2))) return c.Name.StartsWith("Nt") ? NtStatus.AccessDenied : BoolResult(c, false, Win32Error.AccessDenied);
        return OpenToken(c, thread.ImpersonationToken, c.Arg(3), true);
    }

    [VirtualApi("advapi32.dll", "GetUserNameW", ApiCategory.Security)]
    [VirtualApi("advapi32.dll", "GetUserNameA", ApiCategory.Security)]
    private static ulong GetUserName(ApiCallContext c)
    {
        if (c.Arg(1) == 0) return BoolResult(c, false, Win32Error.InvalidParameter);
        var encoding = c.Name.EndsWith('W') ? Encoding.Unicode : CodePagesEncodingProvider.Instance.GetEncoding(0) ?? Encoding.UTF8;
        var name = c.Thread.EffectiveToken.User.Name;
        var bytes = encoding.GetBytes(name + '\0');
        var required = (uint)(c.Name.EndsWith('W') ? name.Length + 1 : bytes.Length);
        var capacity = c.Read<uint>(c.Arg(1));
        c.Write(c.Arg(1), required);
        if (c.Arg(0) == 0 || capacity < required) return BoolResult(c, false, 234);
        c.WriteBytes(c.Arg(0), bytes);
        return 1;
    }

    [VirtualApi("advapi32.dll", "LogonUserW", ApiCategory.Security)]
    [VirtualApi("advapi32.dll", "LogonUserA", ApiCategory.Security)]
    private static ulong LogonUser(ApiCallContext c)
    {
        if (c.Arg(5) == 0 || c.Arg(0) == 0 || c.Arg32(3) is not (2 or 3 or 4 or 5 or 7 or 8 or 9) || c.Arg32(4) > 3)
            return BoolResult(c, false, Win32Error.InvalidParameter);
        c.Write(c.Arg(5), 0UL);
        string Read(int i) => c.Arg(i) == 0 ? "" : c.Name.EndsWith('W') ? c.ReadUnicodeString(c.Arg(i)) : c.ReadAnsiString(c.Arg(i));
        if (Read(1) is { Length: > 0 } domain && domain != "." && !domain.Equals("ImaginedMachine", StringComparison.OrdinalIgnoreCase))
            return BoolResult(c, false, 1326);
        try
        {
            using var obj = new TokenObject(c.Runtime.Security.Logon(Read(0), Read(2)), c.Arg32(3) == 3);
            c.Write(c.Arg(5), c.Handles.Create(obj, TokenAllAccess));
            return 1;
        }
        catch (UnauthorizedAccessException) { return BoolResult(c, false, 1326); }
    }

    [VirtualApi("advapi32.dll", "DuplicateToken", ApiCategory.Security)]
    [VirtualApi("advapi32.dll", "DuplicateTokenEx", ApiCategory.Security)]
    private static ulong DuplicateToken(ApiCallContext c)
    {
        var extended = c.Name.EndsWith("Ex", StringComparison.Ordinal);
        var output = c.Arg(extended ? 5 : 2);
        if (output == 0 || c.Arg32(extended ? 3 : 1) > 3 || (extended && c.Arg32(4) is not (1 or 2)))
            return BoolResult(c, false, Win32Error.InvalidParameter);
        c.Write(output, 0UL);
        var status = c.Handles.TryGet(c.Arg(0), TokenDuplicate, out var obj);
        if (status != NtStatus.Success) return BoolResult(c, false, Win32FromStatus(status));
        if (obj is not TokenObject token) return BoolResult(c, false, Win32Error.InvalidHandle);
        var entry = c.Handles.Snapshot().First(e => e.Handle == c.Arg(0));
        var access = extended && c.Arg32(1) != 0 ? c.Arg32(1) : entry.GrantedAccess;
        if ((access & NtAccess.GenericAll) != 0) access = TokenAllAccess;
        if ((access & ~entry.GrantedAccess) != 0) return BoolResult(c, false, Win32Error.AccessDenied);
        using var copy = new TokenObject(token.Token.Clone(), !extended || c.Arg32(4) == 2);
        c.Write(output, c.Handles.Create(copy, access));
        return 1;
    }

    [VirtualApi("advapi32.dll", "ImpersonateLoggedOnUser", ApiCategory.Security)]
    private static ulong ImpersonateLoggedOnUser(ApiCallContext c)
    {
        var status = c.Handles.TryGet(c.Arg(0), out var obj) ? NtStatus.Success : NtStatus.InvalidHandle;
        if (status != NtStatus.Success) return BoolResult(c, false, Win32FromStatus(status));
        if (obj is not TokenObject token) return BoolResult(c, false, Win32Error.InvalidHandle);
        status = c.Handles.TryGet(c.Arg(0), TokenQuery | (token.Impersonation ? TokenImpersonate : TokenDuplicate), out _);
        if (status != NtStatus.Success) return BoolResult(c, false, Win32FromStatus(status));
        c.Thread.ImpersonationToken = token.Token;
        return 1;
    }

    [VirtualApi("advapi32.dll", "RevertToSelf", ApiCategory.Security)]
    private static ulong RevertToSelf(ApiCallContext c)
    {
        c.Thread.ImpersonationToken = null;
        return 1;
    }

    private static byte[] Sid(string value)
    {
        var sid = new SecurityIdentifier(value);
        var bytes = new byte[sid.BinaryLength];
        sid.GetBinaryForm(bytes, 0);
        return bytes;
    }

    [VirtualApi("advapi32.dll", "AdjustTokenPrivileges", ApiCategory.Security, "kernelbase!AdjustTokenPrivileges")]
    private static ulong AdjustTokenPrivileges(ApiCallContext c)
    {
        var status = c.Handles.TryGet(c.Arg(0), 0x20u | (c.Arg(4) != 0 ? TokenQuery : 0u), out var obj);
        if (status != NtStatus.Success) return BoolResult(c, false, Win32FromStatus(status));
        if (obj is not TokenObject token) return BoolResult(c, false, Win32Error.InvalidHandle);
        if ((!c.ArgBool(1) && c.Arg(2) == 0) || (c.Arg(4) != 0 && c.Arg(5) == 0))
            return BoolResult(c, false, Win32Error.InvalidParameter);

        var requested = new Dictionary<ulong, uint>();
        if (!c.ArgBool(1))
        {
            var count = c.Read<uint>(c.Arg(2));
            if (count > (int.MaxValue - 4) / 12) return BoolResult(c, false, Win32Error.InvalidParameter);
            for (uint i = 0; i < count; i++)
            {
                var entry = c.Arg(2) + 4 + i * 12UL;
                requested[c.Read<ulong>(entry)] = c.Read<uint>(entry + 8);
            }
        }
        var changes = new List<(string Name, ulong Luid, uint Previous, uint Attributes)>();
        foreach (var name in token.Token.Privileges)
        {
            if (!LookupPrivilegeValue(null, name, out var luid)) continue;
            uint attributes = 0;
            if (!c.ArgBool(1) && !requested.Remove(luid, out attributes)) continue;
            var previous = token.Token.GetPrivilegeAttributes(name);
            if ((attributes & 4) != 0 || (previous & 2) != (attributes & 2))
                changes.Add((name, luid, previous, attributes));
        }
        var previousState = changes.Where(p => (p.Attributes & 4) == 0).ToArray();
        if (c.Arg(4) != 0)
        {
            var required = (uint)(4 + previousState.Length * 12);
            c.Write(c.Arg(5), required);
            if (c.Arg32(3) < required) return BoolResult(c, false, Win32Error.InsufficientBuffer);
            c.Write(c.Arg(4), (uint)previousState.Length);
            for (var i = 0; i < previousState.Length; i++)
            {
                var entry = c.Arg(4) + 4 + (ulong)i * 12;
                c.Write(entry, previousState[i].Luid);
                c.Write(entry + 8, previousState[i].Previous);
            }
        }
        foreach (var p in changes) token.Token.SetPrivilegeAttributes(p.Name, p.Attributes);
        if (changes.Count > 0)
            c.Runtime.Trace.Emit(TraceKind.Security, $"adjusted {changes.Count} privileges on token {token.Token.Id}", c.Process.Id, c.Thread.Id);
        c.SetLastError(requested.Count == 0 ? 0 : 1300);
        return 1;
    }

    [VirtualApi("advapi32.dll", "GetTokenInformation", ApiCategory.Security)]
    [VirtualApi("ntdll.dll", "NtQueryInformationToken", ApiCategory.Security, "ntdll!ZwQueryInformationToken")]
    private static ulong GetTokenInformation(ApiCallContext c)
    {
        var nt = c.Name.StartsWith("Nt", StringComparison.Ordinal);
        ulong Fail(uint s) => nt ? s : BoolResult(c, false, Win32FromStatus(s));
        var status = c.Handles.TryGet(c.Arg(0), TokenQuery, out var obj);
        if (status != NtStatus.Success) return Fail(status);
        if (obj is not TokenObject token) return Fail(NtStatus.InvalidHandle);
        var type = c.Arg32(1);
        if (type == 9 && !token.Impersonation) return Fail(NtStatus.InvalidParameter);
        var privileges = new List<(ulong Luid, uint Attributes)>();
        if (type == 3)
            foreach (var name in token.Token.Privileges)
                if (LookupPrivilegeValue(null, name, out var luid)) privileges.Add((luid, token.Token.GetPrivilegeAttributes(name)));
        var sids = type switch
        {
            1 or 4 => new[] { Sid(token.Token.User.Sid) },
            5 => new[] { Sid(token.Token.Groups.FirstOrDefault()?.Sid ?? token.Token.User.Sid) },
            2 => token.Token.Groups.Select(g => Sid(g.Sid)).ToArray(),
            25 => new[] { Sid(token.Token.IsSystem ? "S-1-16-16384" : token.Token.Match("Administrators") >= 0 ? "S-1-16-12288" : "S-1-16-8192") },
            _ => Array.Empty<byte[]>()
        };
        var header = type switch
        {
            1 or 25 => 16, 4 or 5 => 8, 2 => 8 + sids.Length * 16, 3 => 4 + privileges.Count * 12,
            10 => 56, 11 => 8, 33 or 34 => 16, 8 or 9 or 18 or 20 or 21 or 23 or 26 or 29 => 4, _ => 0
        };
        if (header == 0) return Fail(NtStatus.InvalidParameter);
        var required = (uint)(header + sids.Sum(s => s.Length));
        if (c.Arg(4) == 0) return Fail(NtStatus.InvalidParameter);
        c.Write(c.Arg(4), required);
        if (c.Arg(2) == 0 || c.Arg32(3) < required) return nt ? NtStatus.BufferTooSmall : BoolResult(c, false, Win32Error.InsufficientBuffer);
        c.WriteBytes(c.Arg(2), new byte[required]);
        if (type is 33 or 34)
        {
            // CLAIM_SECURITY_ATTRIBUTES_INFORMATION v1，模拟令牌没有声明属性。
            c.Write(c.Arg(2), (ushort)1);
            return nt ? NtStatus.Success : 1;
        }
        if (type == 3)
        {
            c.Write(c.Arg(2), (uint)privileges.Count);
            for (var i = 0; i < privileges.Count; i++)
            {
                c.Write(c.Arg(2) + 4 + (ulong)i * 12, privileges[i].Luid);
                c.Write(c.Arg(2) + 12 + (ulong)i * 12, privileges[i].Attributes);
            }
            return nt ? NtStatus.Success : 1;
        }
        if (type == 10)
        {
            c.Write(c.Arg(2), token.Token.Id);
            c.Write(c.Arg(2) + 8, token.Token.Id);
            c.Write(c.Arg(2) + 16, long.MaxValue);
            c.Write(c.Arg(2) + 24, token.Impersonation ? 2u : 1u);
            c.Write(c.Arg(2) + 28, 2u);
            c.Write(c.Arg(2) + 40, (uint)token.Token.Groups.Count);
            c.Write(c.Arg(2) + 44, (uint)token.Token.Privileges.Count);
            c.Write(c.Arg(2) + 48, token.Token.Id);
            return nt ? NtStatus.Success : 1;
        }
        if (sids.Length > 0 || type == 2)
        {
            if (type == 2) c.Write(c.Arg(2), (uint)sids.Length);
            var offset = header;
            for (var i = 0; i < sids.Length; i++)
            {
                var entry = c.Arg(2) + (ulong)(type == 2 ? 8 + i * 16 : 0);
                c.Write(entry, c.Arg(2) + (ulong)offset);
                if (type == 2) c.Write(entry + 8, 7u);
                if (type == 25) c.Write(entry + 8, 0x60u);
                c.WriteBytes(c.Arg(2) + (ulong)offset, sids[i]);
                offset += sids[i].Length;
            }
        }
        else c.Write(c.Arg(2), type switch
        {
            8 => token.Impersonation ? 2u : 1u,
            9 => 2u,
            18 => 1u,
            20 => token.Token.Match("Administrators") >= 0 ? 1u : 0u,
            _ => 0u
        });
        return nt ? NtStatus.Success : 1;
    }

    [VirtualApi("advapi32.dll", "CheckTokenMembership", ApiCategory.Security)]
    private static ulong CheckTokenMembership(ApiCallContext c)
    {
        if (c.Arg(1) == 0 || c.Arg(2) == 0) return BoolResult(c, false, Win32Error.InvalidParameter);
        var token = c.Thread.EffectiveToken;
        if (c.Arg(0) != 0)
        {
            var status = c.Handles.TryGet(c.Arg(0), TokenQuery, out var obj);
            if (status != NtStatus.Success) return BoolResult(c, false, Win32FromStatus(status));
            if (obj is not TokenObject t) return BoolResult(c, false, Win32Error.InvalidHandle);
            token = t.Token;
        }
        if (c.Read<byte>(c.Arg(1)) != 1 || c.Read<byte>(c.Arg(1) + 1) > 15) return BoolResult(c, false, 1337);
        var bytes = c.ReadBytes(c.Arg(1), 8 + c.Read<byte>(c.Arg(1) + 1) * 4);
        c.Write(c.Arg(2), token.Match(new SecurityIdentifier(bytes, 0).Value) >= 0 ? 1u : 0u);
        return 1;
    }
}
