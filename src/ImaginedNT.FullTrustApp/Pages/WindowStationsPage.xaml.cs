using System.Text;
using ImaginedNT.Core.Runtime;
using ImaginedNT.FullTrustApp.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;
using ThreadState = ImaginedNT.Core.Runtime.ThreadState;

namespace ImaginedNT.FullTrustApp.Pages;

public sealed partial class WindowStationsPage : Page
{
    private SessionInformation[] _sessions = [];
    private WindowStationObject[] _stations = [];
    private DesktopObject[] _desktops = [];
    private bool _updating;
    private bool _starting;
    private WindowStationObject? Station => StationList.SelectedIndex >= 0 && StationList.SelectedIndex < _stations.Length
        ? _stations[StationList.SelectedIndex] : null;
    private DesktopObject? Desktop => DesktopList.SelectedIndex >= 0 && DesktopList.SelectedIndex < _desktops.Length
        ? _desktops[DesktopList.SelectedIndex] : null;

    public WindowStationsPage() => InitializeComponent();

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        AppRuntime.Changed += Refresh;
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args) => AppRuntime.Changed -= Refresh;

    internal void Refresh()
    {
        if (_updating) return;
        var runtime = AppRuntime.IsRunning ? AppRuntime.Session?.Runtime : null;
        var sessionId = SessionSelector.SelectedIndex >= 0 && SessionSelector.SelectedIndex < _sessions.Length
            ? _sessions[SessionSelector.SelectedIndex].Id : runtime?.ActiveConsoleSessionId;
        var station = Station;
        var desktop = Desktop;
        _updating = true;
        try
        {
            _sessions = runtime?.Sessions.ToArray() ?? [];
            var names = _sessions.Select(s => $"{s.Id} · {s.Name} · {s.State}").ToArray();
            if (SessionSelector.ItemsSource is not string[] old || !old.SequenceEqual(names)) SessionSelector.ItemsSource = names;
            SessionSelector.SelectedIndex = _sessions.Length == 0 ? -1 : Math.Max(0, Array.FindIndex(_sessions, s => s.Id == sessionId));
            _stations = runtime?.WindowStations.Stations.Where(s => SessionSelector.SelectedIndex >= 0 &&
                s.SessionId == _sessions[SessionSelector.SelectedIndex].Id).ToArray() ?? [];
            PageRows.Set(StationList, _stations.Select(s => $"{s.Name}\n{(s.IsInteractive ? "交互窗口站" : "非交互窗口站")} · {runtime!.WindowStations.GetDesktops(s).Count} 个桌面"));
            StationList.SelectedIndex = _stations.Length == 0 ? -1 : Math.Max(0, Array.IndexOf(_stations, station));
            _desktops = Station is { } selected ? runtime!.WindowStations.GetDesktops(selected).ToArray() : [];
            PageRows.Set(DesktopList, _desktops.Select(d => $"{d.Name}\n{d.WindowCount} 个窗口{(runtime!.WindowStations.GetInputDesktop(d.SessionId) == d ? " · 输入桌面" : "")}"));
            DesktopList.SelectedIndex = _desktops.Length == 0 ? -1 : Math.Max(0, Array.IndexOf(_desktops, desktop));
        }
        finally { _updating = false; }
        Management.IsEnabled = SessionSelector.IsEnabled = runtime is not null;
        SummaryText.Text = runtime is null ? "运行时未启动" : $"{runtime.WindowStations.Stations.Count} 个窗口站 · 当前控制台会话 {runtime.ActiveConsoleSessionId?.ToString() ?? "无"}";
        ShowDetails();
    }

    private void OnSessionSelected(object sender, SelectionChangedEventArgs args) { if (!_updating) Refresh(); }
    private void OnStationSelected(object sender, SelectionChangedEventArgs args) { if (!_updating) Refresh(); }
    private void OnDesktopSelected(object sender, SelectionChangedEventArgs args) { if (!_updating) ShowDetails(); }

    private void ShowDetails()
    {
        var runtime = AppRuntime.Session?.Runtime;
        var desktop = Desktop;
        CreateDesktopButton.IsEnabled = DeleteStationButton.IsEnabled = Station is not null;
        ViewButton.IsEnabled = DeleteDesktopButton.IsEnabled = desktop is not null;
        SwitchButton.IsEnabled = desktop?.WindowStation.IsInteractive == true;
        StartProcessButton.IsEnabled = desktop is not null && !_starting;
        if (runtime is null || desktop is null) { DetailsText.Text = "请选择桌面"; return; }
        var text = new StringBuilder().AppendLine($"路径: {desktop.FullPath}")
            .AppendLine($"窗口: {desktop.WindowCount}")
            .AppendLine($"输入桌面: {(runtime.WindowStations.GetInputDesktop(desktop.SessionId) == desktop ? "是" : "否")}")
            .AppendLine($"创建时间: {desktop.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}")
            .AppendLine("\n进程与线程");
        foreach (var p in runtime.Processes.Where(p => p.SessionId == desktop.SessionId && !p.IsTerminating))
        {
            var threads = p.Threads.Where(t => t.Desktop == desktop && t.State is ThreadState.Created or ThreadState.Running).ToArray();
            if (p.StartupDesktop != desktop && threads.Length == 0) continue;
            text.AppendLine($"PID {p.Id} · {Path.GetFileName(p.ImagePath)}");
            foreach (var t in threads) text.AppendLine($"  TID {t.Id} · {t.State}");
        }
        DetailsText.Text = text.ToString();
    }

    private void Change(string action, Action<ImaginedRuntime> change)
    {
        if (!AppRuntime.IsRunning || AppRuntime.Session is not { } session) return;
        try { change(session.Runtime); Refresh(); }
        catch (Exception ex) { AppRuntime.ReportError(action, ex); }
    }

    private void OnCreateStation(object sender, RoutedEventArgs args) => Change("创建窗口站", runtime =>
    {
        if (SessionSelector.SelectedIndex < 0) return;
        var station = runtime.WindowStations.CreateStation(_sessions[SessionSelector.SelectedIndex].Id, StationName.Text.Trim());
        runtime.WindowStations.CreateDesktop(station, "Default");
        StationName.Text = "";
        Refresh();
        StationList.SelectedIndex = Array.IndexOf(_stations, station);
    });

    private void OnCreateDesktop(object sender, RoutedEventArgs args) => Change("创建桌面", runtime =>
    {
        if (Station is not { } station) return;
        var desktop = runtime.WindowStations.CreateDesktop(station, DesktopName.Text.Trim());
        DesktopName.Text = "";
        Refresh();
        DesktopList.SelectedIndex = Array.IndexOf(_desktops, desktop);
    });

    private void OnDeleteStation(object sender, RoutedEventArgs args) => Change("删除窗口站", runtime =>
    {
        if (Station is { } station && !runtime.WindowStations.DeleteStation(station))
            AppRuntime.SetMessage("无法删除交互窗口站，或仍有进程、线程、窗口和句柄使用此窗口站及其桌面。");
    });

    private void OnDeleteDesktop(object sender, RoutedEventArgs args) => Change("删除桌面", runtime =>
    {
        if (Desktop is { } desktop && !runtime.WindowStations.DeleteDesktop(desktop))
            AppRuntime.SetMessage("无法删除默认或输入桌面，或仍有进程、线程、窗口和句柄使用此桌面。");
    });

    private void OnViewDesktop(object sender, RoutedEventArgs args) => Change("查看桌面", runtime =>
    {
        if (Desktop is not { } desktop) return;
        AppRuntime.Session!.Windows.ViewDesktop(desktop);
        AppRuntime.Navigate("desktop");
    });

    private void OnSwitchDesktop(object sender, RoutedEventArgs args) => Change("切换桌面", runtime =>
    {
        if (Desktop is not { } desktop) return;
        runtime.WindowStations.SwitchDesktop(desktop);
        runtime.SetActiveConsoleSession(desktop.SessionId);
        AppRuntime.Session!.Windows.ViewDesktop(null);
        AppRuntime.Navigate("desktop");
    });

    private async void OnStartProcess(object sender, RoutedEventArgs args)
    {
        if (_starting || !AppRuntime.IsRunning || AppRuntime.Session is not { } session || Desktop is not { } desktop) return;
        _starting = true;
        var arguments = ArgumentsInput.Text;
        ShowDetails();
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
            picker.FileTypeFilter.Add(".exe");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(((App)Application.Current).MainWindow!));
            var file = await picker.PickSingleFileAsync();
            if (file is null || !AppRuntime.IsRunning || !ReferenceEquals(AppRuntime.Session, session)) return;
            await session.StartProcessAsync(file.Path, desktop, arguments);
            Refresh();
        }
        catch (Exception ex) { AppRuntime.ReportError("在桌面中启动程序", ex); }
        finally { _starting = false; ShowDetails(); }
    }
}
