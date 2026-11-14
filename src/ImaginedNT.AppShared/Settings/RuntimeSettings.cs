using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using ImaginedNT.Core.Diagnostics;
using ImaginedNT.Core.Io;
using ImaginedNT.Core.Runtime;

namespace ImaginedNT.AppShared.Settings;

public sealed class RuntimeSettings(string path, TraceLog log, Func<RuntimeOptions>? defaults = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        IgnoreReadOnlyProperties = true,
        Converters = { new JsonStringEnumConverter() }
    };

    // Streams and the native-forwarder factory belong to the host session.
    internal static readonly PropertyInfo[] Properties = typeof(RuntimeOptions).GetProperties()
        .Where(p => p.Name != nameof(RuntimeOptions.SandboxRoot) &&
            !typeof(Stream).IsAssignableFrom(p.PropertyType) &&
            !typeof(TextWriter).IsAssignableFrom(p.PropertyType) &&
            !typeof(Delegate).IsAssignableFrom(p.PropertyType)).ToArray();

    public RuntimeOptions Options { get; private set; } = CreateDefaults(defaults);
    public string? LoadError { get; private set; }

    private static RuntimeOptions CreateDefaults(Func<RuntimeOptions>? defaults) => defaults?.Invoke() ?? new()
    {
        TerminationMode = ProcessTerminationMode.ThreadExit
    };

    public RuntimeOptions GetDefaults() => CreateDefaults(defaults);

    public void Load()
    {
        if (!File.Exists(path)) return;
        try
        {
            Options = Read(File.ReadAllText(path));
            LoadError = null;
        }
        catch (Exception ex)
        {
            LoadError = $"读取运行时设置失败：{ex.Message}";
            log.Error($"load runtime settings: {ex}");
        }
    }

    private static string Serialize(RuntimeOptions options) => JsonSerializer.Serialize(
        Properties.ToDictionary(p => p.Name, p => JsonSerializer.SerializeToElement(p.GetValue(options), p.PropertyType, JsonOptions)), JsonOptions);

    private RuntimeOptions Read(string json)
    {
        var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, JsonOptions)
            ?? throw new InvalidDataException("运行时配置为空。");
        var options = GetDefaults();
        foreach (var p in Properties)
        {
            if (!values.TryGetValue(p.Name, out var value)) continue;
            if (p.Name == nameof(RuntimeOptions.ComServerPaths))
                options.ComServerPaths.AddRange(value.Deserialize<List<string>>(JsonOptions)
                    ?? throw new InvalidDataException("COM 服务器列表不能为空。"));
            else
                p.SetValue(options, value.Deserialize(p.PropertyType, JsonOptions));
        }
        Validate(options);
        return options;
    }

    public RuntimeOptions Clone() => Read(Serialize(Options));

    public RuntimeOptions CreateOptions(string sandbox, IReadOnlyDictionary<char, string> drives)
    {
        var options = Clone();
        options.SandboxRoot = sandbox;
        // Paths always come from the sandbox page so UWP folder grants stay attached.
        options.Drives = drives.Append(new KeyValuePair<char, string>('C', sandbox)).ToDictionary(d => d.Key,
            d => options.Drives.TryGetValue(d.Key, out var volume)
                ? volume with { HostDirectory = d.Value } : new VirtualDriveOptions(d.Value));
        return options;
    }

    public void Save(RuntimeOptions options)
    {
        Validate(options);
        File.WriteAllText(path + ".tmp", Serialize(options));
        HostFileSystem.Move(path + ".tmp", path, overwrite: true);
        Options = options;
        LoadError = null;
        log.Info("runtime settings saved");
    }

    internal static void Validate(RuntimeOptions options)
    {
        if (options.ExecutionTimeout < TimeSpan.Zero && options.ExecutionTimeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentException("执行超时必须为非负秒数或 -1（无限）。");
        foreach (var p in Properties.Where(p => p.PropertyType.IsEnum))
            if (!Enum.IsDefined(p.PropertyType, p.GetValue(options)!))
                throw new ArgumentException($"{p.Name} 无效。");
        if (options.PackageIdentity is null || options.Drives is null || options.DllRedirects is null)
            throw new ArgumentException("包身份、驱动器和 DLL 重定向配置不能为空。");
        foreach (var d in options.Drives)
            if (d.Key < 'A' || d.Key > 'Z' || d.Value is null || d.Value.Label is null ||
                d.Value.FreeBytes > d.Value.TotalBytes || d.Value.TotalBytes / 4096 > uint.MaxValue)
                throw new ArgumentException($"驱动器 {d.Key}: 的卷信息无效，空闲容量不得超过总容量，总容量须小于 16 TiB。");
        if (options.DllRedirects.Any(d => string.IsNullOrWhiteSpace(d.Key) || string.IsNullOrWhiteSpace(d.Value)) ||
            options.ComServerPaths.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("DLL 重定向和 COM 服务器路径不能包含空项。");
        if (options.SimulatedWindowsVersion is { } version &&
            (version.Build < 0 || version.Revision < 0))
            throw new ArgumentException("模拟 Windows 版本须使用四段版本号，例如 10.0.19045.0。");
        if (!options.IsPackaged) return;
        var identity = options.PackageIdentity;
        if (identity.Name is null || identity.Name.Length is < 3 or > 50 ||
            identity.Name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '.' && c != '-') ||
            string.IsNullOrWhiteSpace(identity.Publisher) || identity.Publisher.Contains('\0') ||
            string.IsNullOrWhiteSpace(identity.InstallPath) || identity.InstallPath.Contains('\0') ||
            string.IsNullOrWhiteSpace(identity.ApplicationId) || identity.ApplicationId.Length > 64 ||
            identity.ApplicationId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '.') ||
            identity.ResourceId is null || identity.ResourceId.Length > 30 ||
            identity.ResourceId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '.' && c != '-') ||
            identity.Version is null || identity.Version.Major > ushort.MaxValue || identity.Version.Minor > ushort.MaxValue ||
            identity.Version.Build is < 0 or > ushort.MaxValue || identity.Version.Revision is < 0 or > ushort.MaxValue ||
            identity.EffectivePublisherId.Length != 13 || identity.EffectivePublisherId.Any(c => !"0123456789abcdefghjkmnpqrstvwxyz".Contains(c)))
            throw new ArgumentException("模拟包身份无效，请检查名称、发布者、应用 ID 和四段版本号。");
        _ = identity.ProcessorArchitecture;
    }
}
