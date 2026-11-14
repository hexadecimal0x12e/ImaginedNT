using System.Globalization;
using System.Text.RegularExpressions;
using ImaginedNT.Core.Security;
using YamlDotNet.RepresentationModel;

namespace ImaginedNT.Core.Io;

public enum FileOperation { Read, Write, Enum, Execute, Delete }
public sealed record FileRule(bool Allowed, string? Target = null, bool Virtual = false, bool Discard = false);

public sealed class DriveMapTable
{
    private sealed record RuleSet(Dictionary<FileOperation, FileRule> Permissions, bool? Excluded = null);
    private sealed record Entry(string Principal, string Pattern, RuleSet Rules, List<Entry> Children);
    private readonly List<Entry> _entries;
    private readonly List<string> _includedPaths = [];
    private readonly RuleSet _defaults;
    private readonly bool _inherit;
    public VirtualDriveOptions Options { get; }
    public bool Readonly { get; }

    private DriveMapTable(YamlMappingNode root, string source)
    {
        var fields = root.Children.ToDictionary(p => Text(p.Key), p => p.Value, StringComparer.Ordinal);
        foreach (var key in fields.Keys)
            if (!new[] { "ImaginedNT VirtFS MapTable", "Label", "Serial", "MaximumSpace", "Readonly", "Inherit", "Default", "HostRoot", "Initials", "FS" }.Contains(key))
                throw Error(root, $"unknown field {key}");
        string Get(string key, string fallback) => fields.TryGetValue(key, out var n) ? Text(n) : fallback;
        if (Get("ImaginedNT VirtFS MapTable", "") != "v1") throw Error(root, "expected MapTable v1");
        var directory = Path.GetDirectoryName(Path.GetFullPath(source))!;
        var capacity = ulong.Parse(Get("MaximumSpace", "137438953472"), CultureInfo.InvariantCulture);
        Options = new(Path.GetFullPath(Get("HostRoot", "."), directory))
        {
            Label = Get("Label", "ImaginedNT"), TotalBytes = capacity, FreeBytes = capacity,
            SerialNumber = uint.Parse(Get("Serial", "1234-5678").Replace("-", ""), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
        };
        if (capacity / 4096 > uint.MaxValue) throw Error(root, "capacity exceeds supported volume size");
        Readonly = bool.Parse(Get("Readonly", "false"));
        _inherit = bool.Parse(Get("Inherit", "true"));
        var templates = fields.TryGetValue("Initials", out var initials) ? Mapping(initials).Children.ToDictionary(p => Text(p.Key).TrimStart('-'), p => p.Value) : [];
        RuleSet Parse(YamlNode node, HashSet<string>? stack = null)
        {
            stack ??= [];
            var result = new Dictionary<FileOperation, FileRule>();
            bool? excluded = null;
            foreach (var raw in Regex.Split(Text(node), ",(?=(?:[^\"]*\"[^\"]*\")*[^\"]*$)"))
            {
                var token = raw.Trim();
                if (token.Length == 0) continue;
                var inverse = token.StartsWith("-<");
                var template = token.TrimStart('+', '-');
                if (template.StartsWith('<') && template.EndsWith('>'))
                {
                    var value = template == "<default>" ? fields.GetValueOrDefault("Default") : templates.GetValueOrDefault(template);
                    if (value is null || !stack.Add(template)) throw Error(node, $"unknown or cyclic template {template}");
                    var expanded = Parse(value, stack);
                    foreach (var pair in expanded.Permissions) result[pair.Key] = inverse ? pair.Value with { Allowed = !pair.Value.Allowed, Target = null, Discard = false } : pair.Value;
                    if (expanded.Excluded is bool hidden) excluded = inverse ? !hidden : hidden;
                    stack.Remove(template);
                    continue;
                }
                if (token is "exclude" or "+exclude" or "-exclude")
                {
                    excluded = token != "-exclude";
                    continue;
                }
                var match = Regex.Match(token, "^([+-])(read|write|enum|execute|delete)(?::(discard|remap_host@\"([^\"]+)\"|remap_virt@\"([^\"]+)\"))?$", RegexOptions.CultureInvariant);
                if (!match.Success) throw Error(node, $"invalid permission {token}");
                var op = Enum.Parse<FileOperation>(match.Groups[2].Value, true);
                var target = match.Groups[4].Success ? Path.GetFullPath(match.Groups[4].Value, directory) : match.Groups[5].Success ? match.Groups[5].Value : null;
                if (match.Groups[3].Success && (match.Groups[1].Value != "+" || op is not (FileOperation.Read or FileOperation.Write))) throw Error(node, "remap requires +read or +write");
                if (match.Groups[3].Value == "discard" && op != FileOperation.Write) throw Error(node, "discard requires +write");
                result[op] = new(match.Groups[1].Value == "+", target, match.Groups[5].Success, match.Groups[3].Value == "discard");
            }
            return new(result, excluded);
        }
        _defaults = fields.TryGetValue("Default", out var defaults) ? Parse(defaults) : new([]);
        foreach (var t in templates.Values) _ = Parse(t);
        List<Entry> Entries(YamlNode node, int depth = 0)
        {
            if (depth > 64) throw Error(node, "rule tree too deep");
            var entries = new List<Entry>();
            foreach (var pair in Mapping(node).Children)
            {
                var name = Text(pair.Key);
                if (!name.Contains('@'))
                {
                    if (!name.StartsWith('-')) throw Error(pair.Key, "expected principal@path");
                    name = name[1..];
                }
                var separator = name.IndexOf('@');
                var principal = separator < 0 ? "*" : name[..separator];
                if (separator >= 0) name = name[(separator + 1)..];
                if (principal.Length == 0 || principal.Any(char.IsControl)) throw Error(pair.Key, "invalid rule principal");
                name = name.Replace('/', '\\');
                if (name.Length == 0 || name.Contains(':') || name.Split('\\').Any(p => p is ".." or ".") || (name.Contains('<') && name is not ("<self>" or "<left>"))) throw Error(pair.Key, "invalid rule path");
                entries.Add(pair.Value is YamlMappingNode ? new(principal, name, new([]), Entries(pair.Value, depth + 1)) : new(principal, name, Parse(pair.Value), []));
            }
            return entries;
        }
        _entries = fields.TryGetValue("FS", out var fs) ? Entries(fs) : [];
        void CollectIncluded(List<Entry> entries, string prefix)
        {
            foreach (var e in entries)
            {
                var path = e.Pattern == "<self>" ? prefix :
                    (prefix.Length == 0 ? "" : prefix + "\\") + (e.Pattern == "<left>" ? "*" : e.Pattern);
                if (e.Rules.Excluded == false && path.Length != 0) _includedPaths.Add(path);
                CollectIncluded(e.Children, path);
            }
        }
        CollectIncluded(_entries, "");
    }

    public static DriveMapTable Load(string path)
    {
        try
        {
            var stream = new YamlStream();
            using var reader = new StreamReader(HostFileSystem.OpenRead(path));
            stream.Load(reader);
            if (stream.Documents.Count != 1) throw new InvalidDataException("expected one YAML document");
            return new(Mapping(stream.Documents[0].RootNode), path);
        }
        catch (Exception ex) when (ex is not IOException && ex is not UnauthorizedAccessException)
        { throw new InvalidDataException($"{path}: {ex.Message}", ex); }
    }
    private static string Text(YamlNode node) => node is YamlScalarNode s ? s.Value ?? "" : throw Error(node, "expected scalar");
    private static YamlMappingNode Mapping(YamlNode node) => node as YamlMappingNode ?? throw Error(node, "expected mapping");
    private static InvalidDataException Error(YamlNode node, string message) => new($"line {node.Start.Line}, column {node.Start.Column}: {message}");
    private static bool Matches(string pattern, string path) => Regex.IsMatch(path.Replace('\\', '/'),
        "^" + Regex.Escape(pattern.Replace('\\', '/')).Replace(@"\*\*/", "(?:.*/)?").Replace(@"\*\*", ".*").Replace(@"\*", "[^/]*").Replace(@"\?", "[^/]") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static int Rank(Entry entry, AccessToken? token) => token?.Match(entry.Principal) ?? (entry.Principal == "*" ? 0 : -1);

    public IEnumerable<string> Children(string relative, AccessToken? token = null)
    {
        IEnumerable<string> Walk(List<Entry> entries, string prefix)
        {
            foreach (var e in entries.Where(e => Rank(e, token) >= 0 && e.Pattern.IndexOfAny(['*', '?', '<']) < 0))
            {
                var path = prefix.Length == 0 ? e.Pattern : prefix + "\\" + e.Pattern;
                yield return path;
                foreach (var child in Walk(e.Children, path)) yield return child;
            }
        }
        var prefix = relative.Length == 0 ? "" : relative + "\\";
        return Walk(_entries, "").Where(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(p => p[prefix.Length..].Split('\\')[0]).Where(p => p.Length != 0).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(p => !IsExcluded(prefix + p, token));
    }

    private bool IsExcluded(string relative, AccessToken? token)
    {
        _ = Resolve(relative, FileOperation.Enum, out var excluded, token);
        return excluded && !HasVisibleDescendants(relative, token);
    }

    public bool HasVisibleDescendants(string relative, AccessToken? token = null)
    {
        string[] parent = relative.Length == 0 ? [] : relative.Replace('/', '\\').Split('\\');
        foreach (var path in _includedPaths)
        {
            var parts = path.Replace('/', '\\').Split('\\');
            var visited = new HashSet<(int, int)>();
            bool Below(int p, int r)
            {
                if (!visited.Add((p, r))) return false;
                if (p == parts.Length) return false;
                if (r == parent.Length)
                {
                    var candidate = (relative.Length == 0 ? "" : relative + "\\") + string.Join("\\", parts[p..]);
                    _ = Resolve(candidate, FileOperation.Enum, out var excluded, token);
                    return !excluded;
                }
                if (parts[p] == "**") return Below(p + 1, r) || Below(p, r + 1);
                return Matches(parts[p], parent[r]) && Below(p + 1, r + 1);
            }
            if (Below(0, 0)) return true;
        }
        return false;
    }

    public (FileRule Rule, string Suffix) Resolve(string relative, FileOperation operation, AccessToken? token = null) => Resolve(relative, operation, out _, token);

    public (FileRule Rule, string Suffix) Resolve(string relative, FileOperation operation, out bool excluded, AccessToken? token = null)
    {
        relative = relative.Replace('/', '\\').Trim('\\');
        var rules = new Dictionary<FileOperation, FileRule>(_defaults.Permissions);
        var hidden = _defaults.Excluded ?? false;
        var bases = new Dictionary<FileOperation, string>();
        void Apply(RuleSet? values, string prefix)
        {
            if (values is null) return;
            foreach (var p in values.Permissions) { rules[p.Key] = p.Value; bases[p.Key] = prefix; }
            if (values.Excluded is bool value) hidden = value;
        }
        void Visit(List<Entry> entries, string remainder, string prefix)
        {
            void ApplyEntries(IEnumerable<Entry> candidates, string basePath)
            {
                foreach (var rankGroup in candidates.Where(e => Rank(e, token) >= 0).GroupBy(e => Rank(e, token)).OrderBy(g => g.Key))
                {
                    var permissions = new Dictionary<FileOperation, FileRule>();
                    bool? hiddenOverride = null;
                    foreach (var e in rankGroup)
                    {
                        foreach (var p in e.Rules.Permissions)
                            if (!permissions.TryGetValue(p.Key, out var existing) || existing.Allowed) permissions[p.Key] = p.Value;
                        if (e.Rules.Excluded is bool value) hiddenOverride = hiddenOverride == true || value;
                    }
                    Apply(new(permissions, hiddenOverride), basePath);
                }
            }
            ApplyEntries(entries.Where(e => e.Pattern == "<self>"), prefix);
            if (remainder.Length == 0) return;
            if (!_inherit)
            {
                rules = new(_defaults.Permissions);
                hidden = _defaults.Excluded ?? false;
                bases.Clear();
            }
            var head = remainder.Split('\\')[0];
            var matching = entries.Where(e => Rank(e, token) >= 0 && e.Pattern is not ("<self>" or "<left>") &&
                (Matches(e.Pattern, remainder) || Matches(e.Pattern, head))).OrderBy(e => e.Pattern.IndexOfAny(['*', '?']) < 0 ? 1 : 0).ToList();
            if (matching.Count == 0) matching = entries.Where(e => Rank(e, token) >= 0 && e.Pattern == "<left>").ToList();
            foreach (var group in matching.GroupBy(e => e.Pattern, StringComparer.OrdinalIgnoreCase))
            {
                var entry = group.First();
                var consumed = entry.Pattern.Contains('\\') ? remainder : head;
                var next = prefix.Length == 0 ? consumed : prefix + "\\" + consumed;
                ApplyEntries(group, next);
                var children = group.OrderBy(e => Rank(e, token)).SelectMany(e => e.Children).ToList();
                if (children.Count != 0) Visit(children, remainder.Length == consumed.Length ? "" : remainder[(consumed.Length + 1)..], next);
            }
        }
        Visit(_entries, relative, "");
        excluded = hidden;
        var rule = rules.GetValueOrDefault(operation) ?? new(false);
        var basis = bases.GetValueOrDefault(operation) ?? "";
        return (rule, basis.Length == 0 ? relative : relative.Length > basis.Length ? relative[(basis.Length + 1)..] : "");
    }
}
