using ImaginedNT.FullTrustApp.Hosting;
using ImaginedNT.FullTrustApp.Pages;
using ImaginedNT.FullTrustApp.MockWindows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ImaginedNT.FullTrustApp;

public sealed partial class MainPage : Page
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly Dictionary<string, Page> _pages;
    private bool _loaded;
    private int _ticks;
    private VirtualWindowsSession? _desktopWindow;
    private HostSession? _desktopSession;

    public MainPage()
    {
        InitializeComponent();
        _pages = new()
        {
            ["terminal"] = new TerminalPage(),
            ["stations"] = new WindowStationsPage(),
            ["processes"] = new ProcessesPage(),
            ["objects"] = new ObjectsPage(),
            ["files"] = new FilesPage(),
            ["registry"] = new RegistryPage(),
            ["logs"] = new LogsPage(),
            ["sandbox"] = new SandboxPage(),
            ["settings"] = new RuntimeSettingsPage()
        };
        AppRuntime.BugChecked += OnBugChecked;
        AppRuntime.Changed += UpdateHeader;
        AppRuntime.NavigationRequested += Navigate;
        _timer.Tick += OnTick;
        Navigation.SelectedItem = Navigation.MenuItems.OfType<NavigationViewItem>().First(i => (string)i.Tag == "terminal");
        UpdateHeader();
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_loaded) return;
        _loaded = true;
        _timer.Start();
        await ((TerminalPage)_pages["terminal"]).StartAsync();
    }

    private void Navigate(string page)
    {
        if (page == "desktop") { ShowDesktop(); return; }
        Navigation.SelectedItem = Navigation.MenuItems.OfType<NavigationViewItem>().First(i => (string)i.Tag == page);
    }

    private void ShowDesktop()
    {
        if (_desktopWindow is null)
        {
            _desktopWindow = new VirtualWindowsSession();
            _desktopWindow.Closed += (_, _) => _desktopWindow = null;
        }
        _desktopWindow.Activate();
    }

    private void OnNavigationChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item || _pages is null) return;
        if ((string)item.Tag == "desktop")
        {
            ShowDesktop();
            Navigation.SelectedItem = Navigation.MenuItems.OfType<NavigationViewItem>().First(i =>
                _pages.TryGetValue((string)i.Tag, out var page) && ReferenceEquals(page, PageHost.Content));
            return;
        }
        PageHost.Content = _pages[(string)item.Tag];
        AppRuntime.SetMessage("");
        _ = RefreshPageAsync();
    }

    private void OnTick(object? sender, object args)
    {
        try
        {
            AppRuntime.DrainOutput();
            ((TerminalPage)_pages["terminal"]).RefreshOutput();
            if (++_ticks < 10) return;
            _ticks = 0;
            _ = RefreshPageAsync();
            AppRuntime.UpdateStatus();
        }
        catch (Exception ex) { AppRuntime.ReportError("刷新宿主界面", ex); }
    }

    private async Task RefreshPageAsync()
    {
        if (AppRuntime.IsClosed || AppRuntime.IsStopping) return;
        try
        {
            switch (PageHost.Content)
            {
                case ProcessesPage processes: processes.Refresh(); break;
                case WindowStationsPage stations: stations.Refresh(); break;
                case ObjectsPage objects: objects.Refresh(); break;
                case FilesPage files: await files.RefreshAsync(); break;
                case RegistryPage registry: await registry.RefreshAsync(); break;
                case LogsPage logs: logs.Refresh(); break;
            }
        }
        catch (Exception ex) { AppRuntime.ReportError("读取运行时状态", ex); }
    }

    private void OnBugChecked(string message) => DispatcherQueue.TryEnqueue(async () =>
    {
        try
        {
            _ = AppRuntime.StopAsync();
            await new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "发生了一个不可恢复错误（蓝屏）",
                Content = $"{message}",
                CloseButtonText = "确定"
            }.ShowAsync();
        }
        catch (Exception ex) { AppRuntime.ReportError("显示蓝屏提示", ex); }
    });

    private void UpdateHeader()
    {
        StatusText.Text = AppRuntime.Status;
        PageError.Text = AppRuntime.Error;
        if (AppRuntime.Session is null) _desktopSession = null;
        if (!AppRuntime.IsStopping && !AppRuntime.IsClosed && AppRuntime.Session is { } session && !ReferenceEquals(_desktopSession, session))
        {
            _desktopSession = session;
            ShowDesktop();
        }
    }

    public async Task ShutdownAsync()
    {
        _timer.Stop();
        try { await AppRuntime.ShutdownAsync(); }
        finally
        {
            _desktopWindow?.Close();
            _desktopWindow = null;
            AppRuntime.BugChecked -= OnBugChecked;
            AppRuntime.Changed -= UpdateHeader;
            AppRuntime.NavigationRequested -= Navigate;
        }
    }
}
