using System.Text.Json;
using ImaginedNT.Core.Io;
using ImaginedNT.App.Settings;
using ImaginedNT.Core.Diagnostics;
using ImaginedNT.Core.Runtime;

namespace ImaginedNT.FullTrustApp.Hosting;

internal static class AppRuntime
{
    private static readonly string ConfigPath;
    private static Task? _stop;
    private static Task? _shutdown;

    public static TraceLog Log { get; }
    public static RuntimeSettings Settings { get; }
    public static Dictionary<char, string> Drives { get; } = new();
    public static string SandboxRoot { get; set; }
    public static string RootProcessPath { get; private set; } = Path.Combine(AppContext.BaseDirectory, "conhost.exe");
    public static string RootProcessArguments { get; private set; } = "";
    public static Dictionary<char, string> DriveYaml { get; } = new();
    public static HostSession? Session { get; private set; }
    public static string TerminalOutput { get; private set; } = "";
    public static string Status { get; private set; } = "就绪";
    public static string Error { get; private set; } = "";
    public static bool IsClosed { get; private set; }
    public static bool IsStopping { get; private set; }
    public static bool CanConfigure => Session is null && !IsClosed && !IsStopping;
    public static bool IsRunning => Session is not null && !IsClosed && !IsStopping;
    public static event Action<string>? BugChecked;
    public static event Action? Changed;
    public static event Action<string>? NavigationRequested;

    static AppRuntime()
    {
        var data = Microsoft.Windows.Storage.ApplicationData.GetDefault().LocalPath;
        Directory.CreateDirectory(data);
        Log = new TraceLog(filePath: Path.Combine(data, "app.jsonl"));
        ConfigPath = Path.Combine(data, "sandbox-drives.json");
        SandboxRoot = Path.Combine(data, "sandbox");
        LoadConfig();
        Settings = new RuntimeSettings(Path.Combine(data, "runtime-settings.json"), Log, () => new RuntimeOptions
        {
            TerminationMode = ProcessTerminationMode.ThreadExit,
            TraceFile = Path.Combine(data, "runtime.jsonl"),
            CopyrightText = "Powered by ImaginedNT NT Simulator"
        });
        Settings.Load();
    }

    private static void LoadConfig()
    {
        try
        {
            var map = File.Exists(ConfigPath)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(ConfigPath)) ?? []
                : new Dictionary<string, string>();
            foreach (var p in map.Where(p => p.Key.StartsWith("yaml:") && p.Key.Length == 6)) DriveYaml[p.Key[5]] = p.Value;
            if (map.TryGetValue("C", out var root)) SandboxRoot = root;
            else if (!DriveYaml.ContainsKey('C')) DriveYaml['C'] = Path.Combine(AppContext.BaseDirectory, "Assets", "rootfs.yaml");
            if (map.TryGetValue("rootProcess", out var image) && !string.IsNullOrWhiteSpace(image)) RootProcessPath = image;
            if (map.TryGetValue("rootArguments", out var arguments)) RootProcessArguments = arguments;
            foreach (var p in map.Where(p => p.Key != "C"))
                if (p.Key.Length == 1 && char.IsAsciiLetter(p.Key[0]) && Path.IsPathFullyQualified(p.Value))
                    Drives[char.ToUpperInvariant(p.Key[0])] = Path.GetFullPath(p.Value);
            if (DriveYaml.TryGetValue('C', out var yaml)) SandboxRoot = DriveMapTable.Load(yaml).Options.HostDirectory;
        }
        catch (Exception ex) { ReportError("读取配置", ex); }
    }

    public static Dictionary<char, string> GetDrivePaths() =>
        Drives.Append(new KeyValuePair<char, string>('C', SandboxRoot)).ToDictionary(d => d.Key, d => d.Value);

    public static void SaveConfig(string? rootProcessPath = null, string? rootProcessArguments = null)
    {
        var root = Path.GetFullPath(SandboxRoot.Trim());
        var mappings = Drives.ToDictionary(p => p.Key.ToString(), p => p.Value);
        mappings["C"] = root;
        mappings["rootProcess"] = rootProcessPath ?? RootProcessPath;
        mappings["rootArguments"] = rootProcessArguments ?? RootProcessArguments;
        foreach (var p in DriveYaml) mappings[$"yaml:{p.Key}"] = p.Value;
        File.WriteAllText(ConfigPath + ".tmp", JsonSerializer.Serialize(mappings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(ConfigPath + ".tmp", ConfigPath, true);
        SandboxRoot = root;
        RootProcessPath = mappings["rootProcess"];
        RootProcessArguments = mappings["rootArguments"];
        Log.Info($"sandbox configuration saved: {root}");
        SetMessage("配置已保存");
    }

    private static void EnsureSamples()
    {
        if (DriveYaml.ContainsKey('C')) return;
        Directory.CreateDirectory(SandboxRoot);
        foreach (var name in new[] { "conhost.exe" })
        {
            var source = Path.Combine(AppContext.BaseDirectory, name);
            var target = Path.Combine(SandboxRoot, name);
            if (File.Exists(source) && !File.Exists(target)) File.Copy(source, target);
        }
    }

    public static async Task StartAsync(string? imagePath = null, string? arguments = null)
    {
        if (!CanConfigure) return;
        HostSession? session = null;
        try
        {
            imagePath = (imagePath ?? RootProcessPath).Trim();
            arguments ??= RootProcessArguments;
            SaveConfig();
            var options = Settings.CreateOptions(SandboxRoot, Drives);
            foreach (var p in DriveYaml)
            {
                var table = DriveMapTable.Load(p.Value);
                options.Drives[p.Key] = table.Options with { MapTablePath = p.Value };
                if (p.Key == 'C') options.SandboxRoot = table.Options.HostDirectory;
            }
            EnsureSamples();
            var fs = new SandboxFileSystem(options.SandboxRoot!, options.AllowFileWrites, drives: options.Drives);
            var image = !options.ExecuteImage ? "" : Path.IsPathFullyQualified(imagePath) ? Path.GetFullPath(imagePath)
                : DriveYaml.ContainsKey('C') ? fs.MapPath(imagePath, "C:\\", FileOperation.Execute)
                : Path.GetFullPath(imagePath, SandboxRoot);
            session = new HostSession(options, Log);
            session.Runtime.BugChecked += message => BugChecked?.Invoke(message);
            Session = session;
            Status = options.ExecuteImage ? $"运行中 · {Path.GetFileName(image)}" : "环境运行中";
            Changed?.Invoke();
            var exit = await session.RunAsync(image, arguments);
            if (ReferenceEquals(Session, session) && !IsClosed)
            {
                DrainOutput();
                Status = $"来宾已退出（{exit.ExitCode}）";
                Changed?.Invoke();
            }
        }
        catch (Exception ex) { ReportError("启动或运行来宾", ex); }
        finally
        {
            if (ReferenceEquals(Session, session) && !IsStopping) await StopAsync();
        }
    }

    public static void DrainOutput()
    {
        if (Session?.ReadOutput() is not { Length: > 0 } text) return;
        TerminalOutput += text;
        if (TerminalOutput.Length > 200_000) TerminalOutput = TerminalOutput[^160_000..];
    }

    public static void ClearOutput() => TerminalOutput = "";

    public static void SendCommand(string command)
    {
        if (IsRunning) Session!.Send(command);
    }

    public static void UpdateStatus()
    {
        var runtime = Session?.Runtime;
        Status = $"{(runtime is null ? "就绪" : "运行中")}　·　进程 {runtime?.Processes.Count(p => p.ExitCode is null) ?? 0}/{runtime?.Processes.Count ?? 0}　·　托管 API {runtime?.Apis.Count ?? 0}";
        Changed?.Invoke();
    }

    public static void SetMessage(string message)
    {
        Error = message;
        Changed?.Invoke();
    }

    public static void ReportError(string action, Exception ex)
    {
        Log.Error($"{action}: {ex}");
        SetMessage($"{action}失败：{ex.Message}");
    }

    public static void Navigate(string page) => NavigationRequested?.Invoke(page);

    public static async Task StopAsync()
    {
        if (_stop is not null) { await _stop; return; }
        if (Session is null) return;
        var session = Session;
        IsStopping = true;
        Changed?.Invoke();
        _stop = session.DisposeAsync().AsTask();
        try { await _stop; }
        catch (Exception ex) { ReportError("停止来宾", ex); }
        finally
        {
            DrainOutput();
            Session = null;
            _stop = null;
            IsStopping = false;
            Status = "已停止";
            Changed?.Invoke();
        }
    }

    public static Task ShutdownAsync() => _shutdown ??= ShutdownCoreAsync();

    private static async Task ShutdownCoreAsync()
    {
        IsClosed = true;
        Changed?.Invoke();
        try { await StopAsync(); }
        finally { Log.Dispose(); }
    }
}
