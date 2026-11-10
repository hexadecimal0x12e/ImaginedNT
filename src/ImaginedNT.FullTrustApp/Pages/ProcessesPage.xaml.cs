using System.Text;
using ImaginedNT.Core.Runtime;
using ImaginedNT.FullTrustApp.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace ImaginedNT.FullTrustApp.Pages;

public sealed partial class ProcessesPage : Page
{
    private ImaginedProcess[] _processes = [];
    private bool _adding;

    public ProcessesPage() => InitializeComponent();

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        AppRuntime.Changed += UpdateState;
        UpdateState();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args) => AppRuntime.Changed -= UpdateState;

    private void UpdateState() => AddProcessButton.IsEnabled = AppRuntime.IsRunning && !_adding;

    private async void OnAddProcessClicked(object sender, RoutedEventArgs args)
    {
        if (_adding || !AppRuntime.IsRunning || AppRuntime.Session is not { } session) return;
        _adding = true;
        UpdateState();
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
            picker.FileTypeFilter.Add(".exe");
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(((App)Application.Current).MainWindow!));
            var file = await picker.PickSingleFileAsync();
            if (file is null || !AppRuntime.IsRunning || !ReferenceEquals(AppRuntime.Session, session)) return;
            var process = await session.StartProcessAsync(file.Path);
            if (!AppRuntime.IsRunning || !ReferenceEquals(AppRuntime.Session, session)) return;
            Refresh();
            ProcessList.SelectedIndex = Array.FindIndex(_processes, p => p.Id == process.Id);
        }
        catch (Exception ex) { AppRuntime.ReportError("添加进程", ex); }
        finally
        {
            _adding = false;
            UpdateState();
        }
    }

    internal void Refresh()
    {
        var selected = ProcessList.SelectedIndex >= 0 && ProcessList.SelectedIndex < _processes.Length ? _processes[ProcessList.SelectedIndex].Id : -1;
        _processes = AppRuntime.Session?.Runtime.Processes.ToArray() ?? [];
        PageRows.Set(ProcessList, _processes.Select(p => $"{p.Id}　{Path.GetFileName(p.ImagePath)}　·　{(p.ExitCode is int code ? $"已退出 ({code})" : p.MainThread?.State.ToString() ?? "正在加载")}\n父进程 {p.ParentId?.ToString() ?? "宿主"}　线程 {p.Threads.Count}　句柄 {p.Handles.Count}"));
        ProcessList.SelectedIndex = _processes.Length == 0 ? -1 : Math.Max(0, Array.FindIndex(_processes, p => p.Id == selected));
        ShowProcessDetails();
    }

    private void OnProcessSelected(object sender, SelectionChangedEventArgs args) => ShowProcessDetails();
    private void ShowProcessDetails()
    {
        if (ProcessList.SelectedIndex < 0 || ProcessList.SelectedIndex >= _processes.Length) { ProcessDetails.Text = "暂无进程"; return; }
        var p = _processes[ProcessList.SelectedIndex];
        var regions = p.Memory.Regions;
        var text = new StringBuilder().AppendLine($"PID: {p.Id}    {p.MainThread?.State}")
            .AppendLine($"映像: {p.ImagePath}").AppendLine($"命令行: {p.CommandLine}")
            .AppendLine($"工作目录: {p.CurrentDirectory}").AppendLine($"启动时间: {p.StartTime:yyyy-MM-dd HH:mm:ss}")
            .AppendLine($"已跟踪内存: {regions.Sum(r => (double)r.RegionSize) / 1048576:0.00} MiB").AppendLine("\n线程");
        foreach (var t in p.Threads) text.AppendLine($"  {t.Id} native={t.NativeId} {t.State} {t.Name} exit={t.ExitCode?.ToString() ?? "-"}");
        text.AppendLine("\n模块");
        foreach (var m in p.LoadedModules) text.AppendLine($"  {m.Image.FileName} 0x{m.BaseAddress:X16} {m.SizeOfImage:N0} B");
        text.AppendLine("\n句柄");
        foreach (var h in p.Handles.Snapshot()) text.AppendLine($"  0x{h.Handle:X} {h.Object.ObjectType} {h.Object.FullPath ?? h.Name}");
        text.AppendLine("\n内存区域");
        foreach (var r in regions) text.AppendLine($"  {r}");
        ProcessDetails.Text = text.ToString();
    }
}
