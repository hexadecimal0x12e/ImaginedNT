using ImaginedNT.Core.Objects;

namespace ImaginedNT.Core.Security;

public sealed record SecurityPrincipal(string Name, string Sid);

public sealed class AccessToken
{
    private static long _nextId;
    private readonly Dictionary<string, uint> _privileges;
    public ulong Id { get; } = (ulong)Interlocked.Increment(ref _nextId);
    public SecurityPrincipal User { get; }
    public IReadOnlyList<SecurityPrincipal> Groups { get; }
    public IReadOnlyList<string> Privileges { get { lock (_privileges) return _privileges.Keys.ToArray(); } }
    public bool IsSystem => User.Sid == "S-1-5-18";

    public AccessToken(SecurityPrincipal user, IEnumerable<SecurityPrincipal>? groups = null, IEnumerable<string>? privileges = null)
    {
        User = user;
        Groups = Array.AsReadOnly((groups ?? []).ToArray());
        _privileges = (privileges ?? []).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(p => p, _ => 2u, StringComparer.OrdinalIgnoreCase);
    }

    public int Match(string principal)
    {
        if (principal == "*") return 0;
        if (principal.Equals(User.Name, StringComparison.OrdinalIgnoreCase) || principal.Equals(User.Sid, StringComparison.OrdinalIgnoreCase)) return 2;
        return Groups.Any(g => principal.Equals(g.Name, StringComparison.OrdinalIgnoreCase) || principal.Equals(g.Sid, StringComparison.OrdinalIgnoreCase)) ? 1 : -1;
    }

    public uint GetPrivilegeAttributes(string name)
    {
        lock (_privileges) return _privileges.GetValueOrDefault(name);
    }

    public bool HasPrivilege(string name) => (GetPrivilegeAttributes(name) & 2) != 0;

    public void SetPrivilegeAttributes(string name, uint attributes)
    {
        lock (_privileges)
        {
            if ((attributes & 4) != 0) _privileges.Remove(name);
            else if (_privileges.TryGetValue(name, out var current)) _privileges[name] = (current & ~2u) | (attributes & 2);
        }
    }

    public AccessToken Clone()
    {
        lock (_privileges)
        {
            var token = new AccessToken(User, Groups, _privileges.Keys);
            foreach (var p in _privileges) token.SetPrivilegeAttributes(p.Key, p.Value);
            return token;
        }
    }
}

public sealed record AccessRule(string Principal, uint Access, bool Allowed = true);

/// <summary>空 ACL 拒绝全部访问；未设置描述符的资源沿用原有访问策略。</summary>
public sealed class ResourceSecurity(IEnumerable<AccessRule> rules)
{
    public IReadOnlyList<AccessRule> Rules { get; } = Array.AsReadOnly(rules.ToArray());

    public uint GetAllowedAccess(AccessToken token)
    {
        uint allowed = 0, denied = 0;
        foreach (var rule in Rules)
        {
            if (token.Match(rule.Principal) < 0) continue;
            if (rule.Allowed) allowed |= rule.Access;
            else denied |= rule.Access;
        }
        return allowed & ~denied;
    }

    public bool Allows(AccessToken token, uint access) => (access & ~GetAllowedAccess(token)) == 0;
}

public sealed class TokenObject(AccessToken token, bool impersonation = false) : NtObject
{
    public AccessToken Token { get; } = token;
    public bool Impersonation { get; } = impersonation;
    public override ObjectType ObjectType => ObjectType.Token;
    public override string? Info => Token.User.Name;
}
