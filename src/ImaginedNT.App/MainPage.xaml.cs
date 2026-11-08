using ImaginedNT.Core.Io;
using System.Text;
using ImaginedNT.Core.Diagnostics;
using ImaginedNT.Core.Runtime;
using Windows.ApplicationModel;
using Windows.Storage;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using System.Diagnostics;

namespace ImaginedNT.App;

public sealed partial class MainPage : Page
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly List<string> _history = [];
    private string _sandboxRoot = Path.Combine(ApplicationData.Current.LocalFolder.Path, "sandbox");
    private readonly Dictionary<char, string> _drives = new();
    private readonly TraceLog _log = new(filePath: Path.Combine(ApplicationData.Current.LocalFolder.Path, "app.jsonl"));
    private ShellSession? _session;
    private ImaginedProcess[] _processes = [];
    private FileEntry[] _files = [];
    private string _filePath = "C:\\";
    private string _registryPath = "HKEY_CURRENT_USER";
    private string _page = "terminal";
    private string _state = "正在启动 shell…";
    private string _historyDraft = "";
    private int _historyIndex;
    private int _ticks;
    private bool _loaded;
    private bool _refreshing;
    private bool _stopping;
    private bool _closed;

    public MainPage()
    {
        _storageAccess = new(_log);
        HostFileSystem.GrantedAccess = _storageAccess;
        InitializeComponent();
        _timer.Tick += OnTick;
        Navigation.SelectedItem = Navigation.MenuItems[0];
        UpdateSandboxSettings();
        try
        {
            File.Copy(Path.Combine(Package.Current.InstalledLocation.Path,"shell.exe"), Path.Combine(_sandboxRoot, "shell.exe"));
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.ToString());
            //ReportError("复制 shell.exe", ex);
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_loaded) return;
        _loaded = true;
        _timer.Start();
        try { LoadSandboxSettings(); }
        catch (Exception ex)
        {
            _state = "请检查沙盒设置后启动 shell";
            ReportError("读取沙盒设置", ex);
            StartButton.IsEnabled = true;
            SandboxControls.IsEnabled = true;
            UpdateStatus();
            return;
        }
        await StartShellAsync();
    }

    private async Task StartShellAsync()
    {
        if (_closed || _stopping || _choosingFolder || _session?.IsRunning == true) return;
        StartButton.IsEnabled = false;
        SandboxControls.IsEnabled = false;
        SetInputEnabled(false);
        try
        {
            if (_session is not null) await _session.DisposeAsync();
            _session = null;
            await RestoreSandboxAccessAsync();
            if (_closed || _stopping) return;
            _session = new ShellSession(_sandboxRoot, new Dictionary<char, string>(_drives), _log);
            _filePath = "C:\\";
            FilePathInput.Text = _filePath;
            _files = [];
            FileList.ItemsSource = null;
            FilePreview.Text = "";
            _state = "shell 运行中";
            PageError.Text = "";
            StopButton.IsEnabled = true;
            var task = _session.RunAsync();
            SetInputEnabled(true);
            CommandInput.Focus(FocusState.Programmatic);
            UpdateStatus();
            var exit = await task;
            DrainOutput();
            AppendTerminal($"\n[shell 已退出，退出码 {exit.ExitCode}]\n");
            _state = $"shell 已退出（{exit.ExitCode}）";
        }
        catch (Exception ex)
        {
            _state = "shell 启动或运行失败";
            ReportError("shell", ex);
            AppendTerminal($"\n[{ex.Message}]\n");
        }
        finally
        {
            if (!_closed)
            {
                SetInputEnabled(false);
                StopButton.IsEnabled = false;
                StartButton.IsEnabled = !_stopping;
                SandboxControls.IsEnabled = !_stopping;
                UpdateStatus();
                await RefreshPageAsync();
            }
        }
    }

    public async Task StopShellAsync()
    {
        if (_stopping) return;
        _stopping = true;
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = false;
        SandboxControls.IsEnabled = false;
        SetInputEnabled(false);
        try
        {
            if (_session is not null) await _session.StopAsync();
        }
        catch (Exception ex) { ReportError("停止 shell", ex); }
        finally
        {
            _stopping = false;
            StartButton.IsEnabled = !_closed;
            SandboxControls.IsEnabled = !_closed;
            _log.Flush();
        }
    }

    public async Task ShutdownAsync()
    {
        if (_closed) return;
        _closed = true;
        _timer.Stop();
        try
        {
            if (_session is not null) await _session.DisposeAsync();
        }
        catch (Exception ex) { _log.Error($"session cleanup failed: {ex}"); }
        finally { _log.Dispose(); }
    }

    private void SetInputEnabled(bool enabled)
    {
        CommandInput.IsEnabled = enabled;
        SendButton.IsEnabled = enabled;
    }

    private async void OnStartClicked(object sender, RoutedEventArgs args) => await StartShellAsync();
    private async void OnStopClicked(object sender, RoutedEventArgs args) => await StopShellAsync();
    private void OnClearClicked(object sender, RoutedEventArgs args) => TerminalText.Text = "";
    private void OnSendClicked(object sender, RoutedEventArgs args) => SendCommand();

    private void SendCommand()
    {
        if (_session?.IsRunning != true || _stopping) return;
        var command = CommandInput.Text;
        if (Encoding.UTF8.GetByteCount(command) > 511)
        {
            PageError.Text = "命令不能超过 511 个 UTF-8 字节。";
            return;
        }
        DrainOutput();
        AppendTerminal(command + "\n");
        _session.Send(command);
        if (command.Length > 0 && (_history.Count == 0 || _history[^1] != command))
        {
            _history.Add(command);
            if (_history.Count > 100) _history.RemoveAt(0);
        }
        _historyIndex = _history.Count;
        _historyDraft = "";
        CommandInput.Text = "";
        CommandInput.Focus(FocusState.Programmatic);
    }

    private void OnCommandKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Enter)
        {
            args.Handled = true;
            SendCommand();
        }
        else if (args.Key is VirtualKey.Up or VirtualKey.Down)
        {
            args.Handled = true;
            if (_historyIndex == _history.Count) _historyDraft = CommandInput.Text;
            _historyIndex = Math.Clamp(_historyIndex + (args.Key == VirtualKey.Up ? -1 : 1), 0, _history.Count);
            CommandInput.Text = _historyIndex == _history.Count ? _historyDraft : _history[_historyIndex];
            CommandInput.SelectionStart = CommandInput.Text.Length;
        }
    }

    private void DrainOutput()
    {
        var text = _session?.ReadOutput();
        if (!string.IsNullOrEmpty(text)) AppendTerminal(text);
    }

    private void AppendTerminal(string text)
    {
        var follow = TerminalScroll.ScrollableHeight - TerminalScroll.VerticalOffset < 32;
        text = TerminalText.Text + text;
        // The sample shell emits these sequences for cls; keep partial sequences across writes.
        var clear = text.LastIndexOf("\u001b[2J\u001b[H", StringComparison.Ordinal);
        if (clear >= 0) text = text[(clear + 7)..];
        if (text.Length > 100_000) text = text[^100_000..];
        TerminalText.Text = text;
        if (follow && _page == "terminal")
        {
            TerminalScroll.UpdateLayout();
            TerminalScroll.ChangeView(null, TerminalScroll.ScrollableHeight, null, true);
        }
    }

    private async void OnTick(object? sender, object args)
    {
        DrainOutput();
        if (++_ticks < 10) return;
        _ticks = 0;
        UpdateStatus();
        await RefreshPageAsync();
    }

    private void UpdateStatus()
    {
        var runtime = _session?.Runtime;
        var processes = runtime?.Processes ?? [];
        RuntimeStatus.Text = $"{_state}　·　进程 {processes.Count(p => p.ExitCode is null)}/{processes.Count}　·　托管 API {runtime?.Apis.Count ?? 0}";
    }

    private async void OnNavigationChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item) return;
        _page = (string)item.Tag;
        TerminalPage.Visibility = _page == "terminal" ? Visibility.Visible : Visibility.Collapsed;
        ProcessesPage.Visibility = _page == "processes" ? Visibility.Visible : Visibility.Collapsed;
        FilesPage.Visibility = _page == "files" ? Visibility.Visible : Visibility.Collapsed;
        RegistryPage.Visibility = _page == "registry" ? Visibility.Visible : Visibility.Collapsed;
        LogsPage.Visibility = _page == "logs" ? Visibility.Visible : Visibility.Collapsed;
        SandboxPage.Visibility = _page == "sandbox" ? Visibility.Visible : Visibility.Collapsed;
        PageError.Text = "";
        if (_loaded) await RefreshPageAsync();
    }

    private async Task RefreshPageAsync()
    {
        if (_refreshing || _closed) return;
        _refreshing = true;
        try
        {
            switch (_page)
            {
                case "processes": RefreshProcesses(); break;
                case "files": await RefreshFilesAsync(); break;
                case "registry": await RefreshRegistryAsync(); break;
                case "logs": RefreshLogs(); break;
            }
        }
        catch (Exception ex) { ReportError("读取运行时状态", ex); }
        finally { _refreshing = false; }
    }

    private void RefreshProcesses()
    {
        var selected = ProcessList.SelectedIndex >= 0 && ProcessList.SelectedIndex < _processes.Length
            ? _processes[ProcessList.SelectedIndex].Id : -1;
        _processes = _session?.Runtime?.Processes.ToArray() ?? [];
        SetRows(ProcessList, _processes.Select(p =>
            $"{p.Id}　{Path.GetFileName(p.ImagePath)}　·　{ProcessState(p)}\n父进程 {p.ParentId?.ToString() ?? "宿主"}　线程 {p.Threads.Count}　句柄 {p.Handles.Count}"));
        ProcessList.SelectedIndex = _processes.Length == 0 ? -1
            : Math.Max(0, Array.FindIndex(_processes, p => p.Id == selected));
        ShowProcessDetails();
    }

    private static string ProcessState(ImaginedProcess p) => p.ExitCode is int code ? $"已退出 ({code})"
        : p.MainThread?.State.ToString() ?? "正在加载";

    private void OnProcessSelected(object sender, SelectionChangedEventArgs args) => ShowProcessDetails();

    private void ShowProcessDetails()
    {
        if (ProcessList.SelectedIndex < 0 || ProcessList.SelectedIndex >= _processes.Length)
        {
            ProcessDetails.Text = "暂无进程";
            return;
        }
        var p = _processes[ProcessList.SelectedIndex];
        var regions = p.Memory.Regions;
        var text = new StringBuilder()
            .AppendLine($"PID: {p.Id}    {ProcessState(p)}")
            .AppendLine($"映像: {p.ImagePath}")
            .AppendLine($"命令行: {p.CommandLine}")
            .AppendLine($"工作目录: {p.CurrentDirectory}")
            .AppendLine($"启动时间: {p.StartTime:yyyy-MM-dd HH:mm:ss}")
            .AppendLine($"已跟踪内存: {regions.Sum(r => (double)r.RegionSize) / 1048576:0.00} MiB")
            .AppendLine("\n线程");
        foreach (var t in p.Threads)
            text.AppendLine($"  {t.Id}  native={t.NativeId}  {t.State}  {t.Name}  exit={t.ExitCode?.ToString() ?? "-"}");
        text.AppendLine("\n模块");
        foreach (var m in p.LoadedModules) text.AppendLine($"  {m.Image.FileName}  0x{m.BaseAddress:X16}  {m.SizeOfImage:N0} B");
        text.AppendLine("\n句柄");
        foreach (var h in p.Handles.Snapshot())
            text.AppendLine($"  0x{h.Handle:X}  {h.Object.ObjectType}  {h.Object.FullPath ?? h.Name}");
        text.AppendLine("\n内存区域");
        foreach (var r in regions) text.AppendLine($"  {r}");
        ProcessDetails.Text = text.ToString();
    }

    private async Task RefreshFilesAsync()
    {
        var session = _session;
        var fs = session?.Runtime?.PrimaryProcess?.FileSystem;
        if (fs is null) return;
        var path = _filePath;
        var result = await Task.Run(() =>
        {
            var host = fs.MapPath(path);
            var files = HostFileSystem.EnumerateInfos(host)
                .Select(f => new FileEntry(f.Name, f.FullName, (f.Attributes & System.IO.FileAttributes.Directory) != 0,
                    f.IsDirectory ? 0 : f.Length, f.LastWriteTime))
                .OrderByDescending(f => f.IsDirectory).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            return (Files: files, Path: fs.GetFullPath(path));
        });
        if (_closed || session != _session || path != _filePath) return;
        _files = result.Files;
        _filePath = result.Path;
        SandboxPathText.Text = string.Join("　·　", fs.Drives.Select(d => $"{d.RootPath} → {d.HostDirectory}"));
        if (FilePathInput.FocusState != FocusState.Keyboard) FilePathInput.Text = _filePath;
        SetRows(FileList, _files.Select(f => $"{(f.IsDirectory ? "[目录]" : "[文件]")} {f.Name}\n{(f.IsDirectory ? "" : f.Length.ToString("N0") + " B　")}{f.Modified:yyyy-MM-dd HH:mm:ss}"));
        PageError.Text = "";
    }

    private async void OnFileGoClicked(object sender, RoutedEventArgs args) => await GoToFilePathAsync();

    private async Task GoToFilePathAsync()
    {
        _filePath = FilePathInput.Text;
        _files = [];
        FileList.ItemsSource = null;
        FilePreview.Text = "";
        await RefreshPageAsync();
    }

    private async void OnFilePathKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Enter) return;
        args.Handled = true;
        await GoToFilePathAsync();
    }

    private async void OnFileUpClicked(object sender, RoutedEventArgs args)
    {
        FilePathInput.Text = _filePath.TrimEnd('\\').Length == 2 ? _filePath : _filePath.TrimEnd('\\') + "\\..";
        await GoToFilePathAsync();
    }

    private async void OnFileOpened(object sender, DoubleTappedRoutedEventArgs args)
    {
        if (FileList.SelectedIndex < 0 || FileList.SelectedIndex >= _files.Length) return;
        var file = _files[FileList.SelectedIndex];
        if (!file.IsDirectory) return;
        FilePathInput.Text = _filePath.TrimEnd('\\') + "\\" + file.Name;
        await GoToFilePathAsync();
    }

    private async void OnFileSelected(object sender, SelectionChangedEventArgs args)
    {
        if (FileList.SelectedIndex < 0 || FileList.SelectedIndex >= _files.Length) return;
        var file = _files[FileList.SelectedIndex];
        if (file.IsDirectory)
        {
            FilePreview.Text = "双击打开目录";
            return;
        }
        try
        {
            var text = await Task.Run(() =>
            {
                if (Path.GetExtension(file.Name).ToLowerInvariant() is ".exe" or ".dll") return "PE 二进制文件";
                using var stream = HostFileSystem.Open(file.HostPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var chars = new char[8192];
                var count = reader.ReadBlock(chars, 0, chars.Length);
                return chars.AsSpan(0, count).Contains('\0') ? "二进制文件"
                    : new string(chars, 0, count) + (reader.EndOfStream ? "" : "\n[仅预览前 8192 个字符]");
            });
            if (!_closed && FileList.SelectedIndex >= 0 && FileList.SelectedIndex < _files.Length &&
                _files[FileList.SelectedIndex].HostPath == file.HostPath) FilePreview.Text = text;
        }
        catch (Exception ex) { ReportError("预览文件", ex); }
    }

    private async Task RefreshRegistryAsync()
    {
        var runtime = _session?.Runtime;
        if (runtime is null) return;
        var path = _registryPath;
        var root = runtime.PrimaryProcess?.FileSystem.Root;
        if (root is null) return;
        var key = await Task.Run(() => ImaginedRegistry.GetSnapshot(runtime, root, path));
        if (_closed || runtime != _session?.Runtime || path != _registryPath) return;
        if (key is null) throw new DirectoryNotFoundException($"注册表项不存在：{path}");
        SetRows(RegistryKeys, key.SubKeys);
        SetRows(RegistryValues, key.Values.Select(v =>
            $"{(v.Name.Length == 0 ? "(默认)" : v.Name)}　[{RegistryType(v.Type)}]\n{FormatRegistryValue(v)}"));
        RegistryValueHeader.Text = $"值 · {key.Values.Count} 项 · 名称 / 类型 / 数据";
        PageError.Text = "";
    }

    private static string RegistryType(uint type) => type switch
    {
        0 => "REG_NONE", 1 => "REG_SZ", 2 => "REG_EXPAND_SZ", 3 => "REG_BINARY",
        4 => "REG_DWORD", 5 => "REG_DWORD_BIG_ENDIAN", 7 => "REG_MULTI_SZ", 11 => "REG_QWORD",
        _ => type.ToString()
    };

    private static string FormatRegistryValue(RegistryValueSnapshot value)
    {
        var data = value.Data;
        if (value.Type is 1 or 2 or 7)
            return Encoding.Unicode.GetString(data, 0, Math.Min(data.Length, 4096) & ~1)
                .TrimEnd('\0').Replace("\0", " · ") + (data.Length > 4096 ? "…" : "");
        if (value.Type == 4 && data.Length == 4) return $"0x{BitConverter.ToUInt32(data):X8} ({BitConverter.ToUInt32(data)})";
        if (value.Type == 5 && data.Length == 4)
            return System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data).ToString();
        if (value.Type == 11 && data.Length == 8) return $"0x{BitConverter.ToUInt64(data):X16} ({BitConverter.ToUInt64(data)})";
        return Convert.ToHexString(data.AsSpan(0, Math.Min(data.Length, 64))) + (data.Length > 64 ? $"… ({data.Length:N0} B)" : "");
    }

    private async void OnRegistryRootChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_loaded) return;
        RegistryPathInput.Text = ((ComboBoxItem)RegistryRoot.SelectedItem).Content.ToString();
        await GoToRegistryPathAsync();
    }

    private async Task GoToRegistryPathAsync()
    {
        _registryPath = RegistryPathInput.Text.Trim().TrimEnd('\\');
        RegistryKeys.ItemsSource = null;
        RegistryValues.ItemsSource = null;
        await RefreshPageAsync();
    }

    private async void OnRegistryGoClicked(object sender, RoutedEventArgs args) => await GoToRegistryPathAsync();

    private async void OnRegistryPathKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Enter) return;
        args.Handled = true;
        await GoToRegistryPathAsync();
    }

    private async void OnRegistryUpClicked(object sender, RoutedEventArgs args)
    {
        var index = _registryPath.LastIndexOf('\\');
        if (index < 0) return;
        RegistryPathInput.Text = _registryPath[..index];
        await GoToRegistryPathAsync();
    }

    private async void OnRegistryKeyOpened(object sender, DoubleTappedRoutedEventArgs args)
    {
        if (RegistryKeys.SelectedItem is not string name) return;
        RegistryPathInput.Text = _registryPath + "\\" + name;
        await GoToRegistryPathAsync();
    }

    private void RefreshLogs()
    {
        SetRows(LogsPage, (_session?.Runtime?.Trace.Snapshot() ?? []).Concat(_log.Snapshot())
            .OrderBy(e => e.Timestamp).TakeLast(300)
            .Select(e => $"{e.Timestamp:HH:mm:ss.fff} [{e.Kind}] p{e.ProcessId}:t{e.ThreadId} {e.Message}"));
    }

    private static void SetRows(ListView list, IEnumerable<string> rows)
    {
        var items = rows.ToArray();
        if (list.ItemsSource is string[] old && old.SequenceEqual(items)) return;
        var selected = list.SelectedIndex;
        list.ItemsSource = items;
        if (selected >= 0 && selected < items.Length) list.SelectedIndex = selected;
    }

    private void ReportError(string action, Exception ex)
    {
        var message = $"{action}：{ex.Message}";
        if (PageError.Text != message) _log.Error($"{action}: {ex}");
        PageError.Text = message;
    }

    private sealed record FileEntry(string Name, string HostPath, bool IsDirectory, long Length, DateTime Modified);
}
