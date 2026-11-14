using System.Security.Cryptography;
using ImaginedNT.Core.Diagnostics;

namespace ImaginedNT.Core.Security;

/// <summary>运行时独立的用户库；令牌保存登录时的组和权限快照。</summary>
public sealed class SecurityAccounts
{
    private sealed class Account(SecurityPrincipal principal)
    {
        public SecurityPrincipal Principal { get; } = principal;
        public byte[] Salt { get; } = RandomNumberGenerator.GetBytes(16);
        public byte[] Password { get; set; } = [];
        public HashSet<string> Groups { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Privileges { get; } = new(StringComparer.OrdinalIgnoreCase) { "SeChangeNotifyPrivilege" };
        public bool Enabled { get; set; } = true;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Account> _users = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SecurityPrincipal> _groups = new(StringComparer.OrdinalIgnoreCase);
    private readonly TraceLog _trace;
    private readonly string _domain = $"S-1-5-21-{RandomNumberGenerator.GetInt32(1, int.MaxValue)}-{RandomNumberGenerator.GetInt32(1, int.MaxValue)}-{RandomNumberGenerator.GetInt32(1, int.MaxValue)}";
    private int _rid = 1000;

    public SecurityAccounts(TraceLog trace)
    {
        _trace = trace;
        _groups.Add("Everyone", new("Everyone", "S-1-1-0"));
        _groups.Add("Authenticated Users", new("Authenticated Users", "S-1-5-11"));
        _groups.Add("Users", new("Users", "S-1-5-32-545"));
        _groups.Add("Administrators", new("Administrators", "S-1-5-32-544"));
        var system = new Account(new("SYSTEM", "S-1-5-18"));
        system.Groups.Add("Administrators");
        system.Privileges.UnionWith(["SeDebugPrivilege", "SeImpersonatePrivilege", "SeAssignPrimaryTokenPrivilege"]);
        _users.Add("SYSTEM", system);
    }

    public IReadOnlyList<SecurityPrincipal> Users { get { lock (_gate) return _users.Values.Select(u => u.Principal).ToArray(); } }
    public IReadOnlyList<SecurityPrincipal> Groups { get { lock (_gate) return _groups.Values.ToArray(); } }
    public AccessToken SystemToken => CreateToken("SYSTEM");

    public SecurityPrincipal? FindUser(string name)
    {
        lock (_gate) return FindAccount(name)?.Principal;
    }

    private Account? FindAccount(string name) => _users.GetValueOrDefault(name) ??
        _users.Values.FirstOrDefault(u => u.Principal.Sid.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name != name.Trim() || name.Length > 64 || name is "*" or "." or ".." ||
            name.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase) || name.Any(c => char.IsControl(c) || "\\/[]:;|=,+*?<>@\"".Contains(c)))
            throw new ArgumentException("invalid account name", nameof(name));
    }

    public SecurityPrincipal CreateUser(string name, string password = "")
    {
        ValidateName(name);
        lock (_gate)
        {
            if (_users.ContainsKey(name) || _groups.ContainsKey(name)) throw new ArgumentException("account already exists", nameof(name));
            var u = new Account(new(name, $"{_domain}-{_rid++}"));
            u.Password = Hash(password, u.Salt);
            u.Groups.Add("Users");
            _users.Add(name, u);
            _trace.Emit(TraceKind.Security, $"created user '{name}' SID={u.Principal.Sid}");
            return u.Principal;
        }
    }

    public SecurityPrincipal CreateGroup(string name)
    {
        ValidateName(name);
        lock (_gate)
        {
            if (_groups.ContainsKey(name) || _users.ContainsKey(name)) throw new ArgumentException("account already exists", nameof(name));
            var group = new SecurityPrincipal(name, $"{_domain}-{_rid++}");
            _groups.Add(name, group);
            _trace.Emit(TraceKind.Security, $"created group '{name}' SID={group.Sid}");
            return group;
        }
    }

    public bool DeleteUser(string user)
    {
        lock (_gate)
        {
            var u = FindAccount(user);
            if (u is null) return false;
            if (u.Principal.Sid == "S-1-5-18") throw new InvalidOperationException("SYSTEM cannot be deleted");
            _users.Remove(u.Principal.Name);
            _trace.Emit(TraceKind.Security, $"deleted user '{u.Principal.Name}'");
            return true;
        }
    }

    public void SetGroupMembership(string user, string group, bool member = true)
    {
        lock (_gate)
        {
            var u = RequireUser(user);
            var g = _groups.GetValueOrDefault(group) ?? _groups.Values.FirstOrDefault(p => p.Sid.Equals(group, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException("unknown group", nameof(group));
            if (member) u.Groups.Add(g.Name);
            else u.Groups.Remove(g.Name);
            _trace.Emit(TraceKind.Security, $"group membership: '{u.Principal.Name}' '{g.Name}' member={member}");
        }
    }

    public void SetPrivilege(string user, string privilege, bool granted = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(privilege);
        lock (_gate)
        {
            var u = RequireUser(user);
            if (granted) u.Privileges.Add(privilege);
            else u.Privileges.Remove(privilege);
            _trace.Emit(TraceKind.Security, $"user privilege: '{u.Principal.Name}' {privilege} granted={granted}");
        }
    }

    public void SetEnabled(string user, bool enabled)
    {
        lock (_gate)
        {
            var u = RequireUser(user);
            if (u.Principal.Sid == "S-1-5-18") throw new InvalidOperationException("SYSTEM cannot be disabled");
            u.Enabled = enabled;
            _trace.Emit(TraceKind.Security, $"user '{u.Principal.Name}' enabled={enabled}");
        }
    }

    public void SetPassword(string user, string password)
    {
        lock (_gate)
        {
            var u = RequireUser(user);
            if (u.Principal.Sid == "S-1-5-18") throw new InvalidOperationException("SYSTEM cannot log on with a password");
            u.Password = Hash(password, u.Salt);
            _trace.Emit(TraceKind.Security, $"password changed for '{u.Principal.Name}'");
        }
    }

    private Account RequireUser(string name) => FindAccount(name) ?? throw new ArgumentException("unknown user", nameof(name));
    private static byte[] Hash(string password, byte[] salt) => Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);

    public AccessToken Logon(string user, string password = "")
    {
        lock (_gate)
        {
            var u = FindAccount(user);
            if (u is null || !u.Enabled || u.Principal.Sid == "S-1-5-18" || !CryptographicOperations.FixedTimeEquals(Hash(password, u.Salt), u.Password))
            {
                _trace.Emit(TraceKind.Security, $"logon denied for '{user}'");
                throw new UnauthorizedAccessException("invalid user or password");
            }
            _trace.Emit(TraceKind.Security, $"user logged on: '{u.Principal.Name}'");
            return CreateToken(u.Principal.Name);
        }
    }

    // 宿主和系统组件使用；来宾必须通过 Logon 验证凭据。
    public AccessToken CreateToken(string user)
    {
        lock (_gate)
        {
            var u = RequireUser(user);
            if (!u.Enabled) throw new UnauthorizedAccessException("account is disabled");
            return new(u.Principal, u.Groups.Select(g => _groups[g]).Concat([_groups["Everyone"], _groups["Authenticated Users"]]), u.Privileges);
        }
    }
}
