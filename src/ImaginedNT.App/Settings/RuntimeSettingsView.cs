using System.Globalization;
using System.Reflection;
using System.Text.Json;
using ImaginedNT.Core.Diagnostics;
using ImaginedNT.Core.Io;
using ImaginedNT.Core.Runtime;
#if FULL_TRUST_APP
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
#else
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
#endif

namespace ImaginedNT.App.Settings;

internal sealed class RuntimeSettingsView : UserControl
{
    private readonly RuntimeSettings _settings;
    private readonly TraceLog _log;
    private readonly Func<Dictionary<char, string>> _getPaths;
    private readonly StackPanel _panel = new() { Spacing = 12, MaxWidth = 800, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly StackPanel _options = new() { Spacing = 12 };
    private readonly StackPanel _volumes = new() { Spacing = 12 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private readonly TextBlock _root = new() { TextWrapping = TextWrapping.Wrap };
    private readonly List<(PropertyInfo Property, Func<object?> Read)> _fields = [];
    private readonly List<(PropertyInfo Property, Func<object?> Read)> _packageFields = [];
    private readonly Dictionary<char, List<(PropertyInfo Property, Func<object?> Read)>> _driveFields = [];
    private Dictionary<char, string> _paths = [];

    public RuntimeSettingsView(RuntimeSettings settings, TraceLog log, Func<Dictionary<char, string>> getPaths, Action openSandbox)
    {
        _settings = settings;
        _log = log;
        _getPaths = getPaths;
#if FULL_TRUST_APP
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        _panel.MaxWidth = double.PositiveInfinity;
        _panel.HorizontalAlignment = HorizontalAlignment.Stretch;
        _panel.Spacing = _options.Spacing = _volumes.Spacing = 4;
        Content = _panel;
#else
        Content = new ScrollViewer { Content = _panel, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
#endif
        _panel.Children.Add(new TextBlock { Text = "保存后在下次启动 shell 或窗口程序时生效。", TextWrapping = TextWrapping.Wrap });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var save = new Button { Content = "保存设置" };
        save.Click += (_, _) => Save();
        var reset = new Button { Content = "恢复默认值" };
        reset.Click += (_, _) =>
        {
            Build(_settings.GetDefaults());
            _status.Text = "已恢复默认值，点击“保存设置”后生效。";
        };
        actions.Children.Add(save);
        actions.Children.Add(reset);
        _panel.Children.Add(actions);
        _panel.Children.Add(_status);
        _panel.Children.Add(new TextBlock { Text = "标准输入、输出、错误流、诊断输出目标及本机转发器工厂由宿主会话提供。", TextWrapping = TextWrapping.Wrap });
        var sandbox = new Button { Content = "配置沙盒目录和驱动器…" };
        sandbox.Click += (_, _) => openSandbox();
#if FULL_TRUST_APP
        _panel.Children.Add(new SettingsCard { Header = "沙盒目录与驱动器", Description = _root, Content = sandbox });
#else
        _panel.Children.Add(_root);
        _panel.Children.Add(sandbox);
#endif
        _panel.Children.Add(_options);
        Build(_settings.Options);
        _status.Text = _settings.LoadError ?? "";
    }

    private void Build(RuntimeOptions options)
    {
        _options.Children.Clear();
        _fields.Clear();
        _packageFields.Clear();
        _status.Text = "";
        string? group = null;
        foreach (var p in RuntimeSettings.Properties.OrderBy(p => Describe(p.Name).Group switch
        {
            "运行与身份" => 0, "DLL 与本机 API" => 1, "资源与访问" => 2, "诊断日志" => 3, _ => 4
        }))
        {
            if (p.Name is nameof(RuntimeOptions.PackageIdentity) or nameof(RuntimeOptions.Drives)) continue;
            var info = Describe(p.Name);
            if (group != info.Group)
            {
                group = info.Group;
                AddHeading(_options, group);
            }
            _fields.Add((p, AddEditor(_options, p, p.GetValue(options), info.Label, info.Hint)));
        }
        AddHeading(_options, "模拟包身份");
        foreach (var p in typeof(PackageIdentity).GetProperties().Where(p => p.CanWrite))
        {
            var label = p.Name switch
            {
                "Name" => "包名称", "Publisher" => "发布者", "PublisherId" => "发布者 ID（留空自动生成）",
                "Version" => "包版本（四段版本号）", "Architecture" => "架构（x86 / x64 / arm / arm64 / neutral）",
                "ResourceId" => "资源 ID", "InstallPath" => "来宾安装路径", "ApplicationId" => "应用 ID", _ => p.Name
            };
            _packageFields.Add((p, AddEditor(_options, p, p.GetValue(options.PackageIdentity), label, "")));
        }
        AddHeading(_options, "驱动器卷信息");
        _options.Children.Add(_volumes);
        BuildDrives(options);
    }

    public void RefreshSandbox()
    {
        var paths = _getPaths();
        _root.Text = $"沙盒根目录（SandboxRoot）：{paths['C']}";
        if (_paths.Count != paths.Count || _paths.Any(d => !paths.TryGetValue(d.Key, out var path) || path != d.Value))
            BuildDrives(_settings.Options);
    }

    private void BuildDrives(RuntimeOptions options)
    {
        _paths = _getPaths();
        _root.Text = $"沙盒根目录（SandboxRoot）：{_paths['C']}";
        _volumes.Children.Clear();
        _driveFields.Clear();
        foreach (var d in _paths.OrderBy(d => d.Key))
        {
            AddHeading(_volumes, $"{d.Key}:\\ → {d.Value}");
            var volume = options.Drives.GetValueOrDefault(d.Key) ?? new VirtualDriveOptions(d.Value);
            var fields = new List<(PropertyInfo, Func<object?>)>();
            foreach (var p in typeof(VirtualDriveOptions).GetProperties().Where(p => p.Name != "HostDirectory"))
            {
                var label = p.Name switch
                {
                    "MapTablePath" => "卷 YAML 路径（留空不使用）",
                    "Label" => "卷标", "SerialNumber" => "卷序列号（十进制，留空自动生成）",
                    "TotalBytes" => "总容量（字节）", "FreeBytes" => "空闲容量（字节）", _ => p.Name
                };
                fields.Add((p, AddEditor(_volumes, p, p.GetValue(volume), label, "")));
            }
            _driveFields[d.Key] = fields;
        }
    }

    private static void AddHeading(StackPanel panel, string text) => panel.Children.Add(new TextBlock
    {
#if FULL_TRUST_APP
        Text = text, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        Margin = new Thickness(0, 24, 0, 4), TextWrapping = TextWrapping.Wrap
#else
        Text = text, FontSize = 20, Margin = new Thickness(0, 16, 0, 0), TextWrapping = TextWrapping.Wrap
#endif
    });

    private static Func<object?> AddEditor(StackPanel panel, PropertyInfo p, object? value, string label, string hint)
    {
        var title = $"{label} · {p.Name}";
        FrameworkElement editor;
        Func<object?> read;
        if (p.PropertyType == typeof(bool))
        {
#if FULL_TRUST_APP
            var toggle = new ToggleSwitch { IsOn = value is true };
            editor = toggle;
            read = () => toggle.IsOn;
#else
            var check = new CheckBox { Content = title, IsChecked = (bool?)value };
            editor = check;
            read = () => check.IsChecked == true;
#endif
        }
        else if (p.PropertyType.IsEnum)
        {
            var combo = new ComboBox
            {
#if !FULL_TRUST_APP
                Header = title,
#endif
                ItemsSource = Enum.GetValues(p.PropertyType), SelectedItem = value, MinWidth = 260
            };
            editor = combo;
            read = () => combo.SelectedItem ?? throw new ArgumentException($"请选择{label}。");
        }
        else
        {
            var type = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
            var collection = p.Name is nameof(RuntimeOptions.DllRedirects) or nameof(RuntimeOptions.ComServerPaths);
            var text = new TextBox
            {
#if !FULL_TRUST_APP
                Header = title,
#endif
                HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 260,
                Text = value switch
                {
                    TimeSpan timeout => timeout == Timeout.InfiniteTimeSpan ? "-1" : timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture),
                    Dictionary<string, string> redirects => JsonSerializer.Serialize(redirects, new JsonSerializerOptions { WriteIndented = true }),
                    List<string> servers => string.Join(Environment.NewLine, servers),
                    _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
                },
                AcceptsReturn = collection, TextWrapping = collection ? TextWrapping.Wrap : TextWrapping.NoWrap,
                MaxHeight = collection ? 240 : double.PositiveInfinity
            };
            editor = text;
            read = () =>
            {
                try
                {
                    if (p.Name == nameof(RuntimeOptions.DllRedirects))
                        return new Dictionary<string, string>(JsonSerializer.Deserialize<Dictionary<string, string>>(text.Text)
                            ?? throw new ArgumentException("请输入 JSON 对象。"), StringComparer.OrdinalIgnoreCase);
                    if (p.Name == nameof(RuntimeOptions.ComServerPaths))
                        return text.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                    if (type == typeof(string))
                        return string.IsNullOrWhiteSpace(text.Text) && (p.DeclaringType == typeof(RuntimeOptions) ||
                            p.Name is "PublisherId" or nameof(VirtualDriveOptions.MapTablePath)) ? null : text.Text;
                    if (string.IsNullOrWhiteSpace(text.Text) && (Nullable.GetUnderlyingType(p.PropertyType) is not null ||
                        (type == typeof(Version) && p.DeclaringType == typeof(RuntimeOptions)))) return null;
                    if (type == typeof(Version)) return Version.Parse(text.Text.Trim());
                    if (type == typeof(TimeSpan))
                    {
                        var seconds = double.Parse(text.Text, CultureInfo.InvariantCulture);
                        if (!double.IsFinite(seconds) || (seconds < 0 && seconds != -1))
                            throw new ArgumentException("请输入非负秒数或 -1（无限）。");
                        return seconds == -1 ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(seconds);
                    }
                    return Convert.ChangeType(text.Text, type, CultureInfo.InvariantCulture);
                }
                catch (Exception ex) { throw new ArgumentException($"{label}（{p.Name}）：{ex.Message}", ex); }
            };
        }
#if FULL_TRUST_APP
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(editor, title);
        panel.Children.Add(new SettingsCard
        {
            Header = title, Description = hint, Content = editor,
            ContentAlignment = editor is TextBox ? ContentAlignment.Vertical : ContentAlignment.Right,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        });
#else
        panel.Children.Add(editor);
        if (hint.Length > 0) panel.Children.Add(new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap, Opacity = 0.7 });
#endif
        return read;
    }

    private void Save()
    {
        try
        {
            var options = _settings.Clone();
            foreach (var field in _fields)
            {
                if (field.Property.Name == nameof(RuntimeOptions.ComServerPaths))
                {
                    options.ComServerPaths.Clear();
                    options.ComServerPaths.AddRange((List<string>)field.Read()!);
                }
                else field.Property.SetValue(options, field.Read());
            }
            var package = new PackageIdentity();
            foreach (var field in _packageFields) field.Property.SetValue(package, field.Read());
            options.PackageIdentity = package;
            options.Drives.Clear();
            foreach (var d in _getPaths())
            {
                var volume = new VirtualDriveOptions(d.Value);
                if (_driveFields.TryGetValue(d.Key, out var fields))
                    foreach (var field in fields) field.Property.SetValue(volume, field.Read());
                options.Drives[d.Key] = volume;
            }
            _settings.Save(options);
            _status.Text = "设置已保存，下次启动来宾时生效。";
        }
        catch (Exception ex)
        {
            _status.Text = $"保存失败：{ex.Message}";
            _log.Error($"save runtime settings: {ex}");
        }
    }

    private static (string Group, string Label, string Hint) Describe(string name) => name switch
    {
        "IsPackaged" => ("运行与身份", "模拟 MSIX 包身份", "包身份字段在下方配置。"),
        "ExecuteImage" => ("运行与身份", "执行来宾映像", "关闭时仅初始化环境，由持有方手动创建和启动进程。"),
        "InterceptSyscalls" => ("运行与身份", "拦截直接系统调用", "修补映像中的直接系统调用指令。"),
        "TerminationMode" => ("运行与身份", "根进程退出方式", "ThreadExit：仅退出线程；HostExit：退出整个应用；ReturnToHost：返回来宾代码，仅供测试。"),
        "ExecutionTimeout" => ("运行与身份", "执行超时（秒）", "-1 表示无限；超时后终止来宾进程线程。"),
        "AllowProcessCreation" => ("运行与身份", "允许创建来宾子进程", ""),
        "SimulatedWindowsVersion" => ("运行与身份", "模拟 Windows 版本", "留空使用宿主版本，例如 10.0.19045.0。"),
        "CopyrightText" => ("运行与身份", "版权文本", "留空使用宿主结果。"),
        "ProductName" => ("运行与身份", "产品名称", "留空按模拟 Windows 版本选择默认名称。"),
        "ImportPolicy" => ("DLL 与本机 API", "导入解析策略", "ManagedFirst：运行时实现优先；ManagedOnly：仅托管。"),
        "DllRedirects" => ("DLL 与本机 API", "DLL 重定向（JSON）", "例如 {\"example.dll\": \"replacement.dll\"}，目标也可使用宿主完整路径。"),
        "ComServerPaths" => ("DLL 与本机 API", "COM 服务器 DLL 路径", "每行一个来宾沙盒路径或宿主路径。"),
        "FailOnUnresolvedImport" => ("DLL 与本机 API", "未解析导入时加载失败", "关闭时生成返回入口点未找到状态的占位函数。"),
        "AllowNativeForwarding" => ("DLL 与本机 API", "允许本机 API 转发", "仍受导入解析策略约束。"),
        "AllowUnimplementedApiForwarding" => ("DLL 与本机 API", "未实现 API 的本机保底转发", "默认开启；关闭后仅允许 Partially Imagined 声明的本机转发。"),
        "NativeApiPolicy" => ("DLL 与本机 API", "本机转发声明策略", "AllowAll：允许启用未实现 API 保底转发；DeclaredOnly：仅已声明导出。"),
        "WrapNativeHandles" => ("DLL 与本机 API", "包装本机句柄为来宾句柄", ""),
        "ExpandManagedHandles" => ("DLL 与本机 API", "为托管句柄创建本机表示", ""),
        "PreferHostSystemDlls" => ("DLL 与本机 API", "优先使用宿主系统 DLL", "导入系统 DLL 时优先使用宿主 System32 版本。"),
        "LimitJobObject" => ("资源与访问", "启用 Windows 作业对象限制", "将宿主进程加入作业对象，限制作用于整个宿主。"),
        "JobMemoryLimitBytes" => ("资源与访问", "作业内存上限（字节）", "启用作业对象限制时生效。"),
        "MaximumPhysicalMemoryBytes" => ("资源与访问", "来宾内存额度（字节）", "所有来宾共享已提交内存额度；0 不限制，统计容量从 8 GiB 起自动扩展。"),
        "MaximumVirtualMemoryBytes" => ("资源与访问", "来宾虚拟内存额度（字节）", "所有来宾共享预留额度；0 不限制，统计容量从 128 TiB 起自动扩展。"),
        "JobActiveProcessLimit" => ("资源与访问", "作业最大活动进程数", "启用作业对象限制时生效；0 按 1 处理。"),
        "AllowFileWrites" => ("资源与访问", "允许沙盒文件写入", ""),
        "ApplySectionProtections" => ("资源与访问", "按 PE 节属性设置内存保护", ""),
        "AllowWithCET" => ("资源与访问", "允许在启用 CET 的宿主上运行", ""),
        "WrapFailFast" => ("资源与访问", "将根进程 FailFast 包装为宿主异常", ""),
        "TraceFile" => ("诊断日志", "跟踪日志文件路径", "留空不写日志文件；使用宿主可访问的路径。"),
        "TraceToConsole" => ("诊断日志", "向诊断输出目标写入跟踪日志", ""),
        "TraceApiArguments" => ("诊断日志", "记录 API 调用参数", ""),
        "TraceNativeCalls" => ("诊断日志", "记录每次本机转发调用", "输出量较大，适合诊断时启用。"),
        _ => ("其他", name, "")
    };
}
