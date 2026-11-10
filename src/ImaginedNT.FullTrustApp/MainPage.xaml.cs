using ImaginedNT.FullTrustApp.Hosting;
using ImaginedNT.FullTrustApp.Pages;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ImaginedNT.FullTrustApp;

public sealed partial class MainPage : Page
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly Dictionary<string, Page> _pages;
    private bool _loaded;
    private int _ticks;

    public MainPage()
    {
        InitializeComponent();
        _pages = new()
        {
            ["terminal"] = new TerminalPage(),
            ["desktop"] = new DesktopPage(),
            ["processes"] = new ProcessesPage(),
            ["files"] = new FilesPage(),
            ["registry"] = new RegistryPage(),
            ["logs"] = new LogsPage(),
            ["sandbox"] = new SandboxPage(),
            ["settings"] = new RuntimeSettingsPage()
        };
        AppRuntime.Changed += UpdateHeader;
        AppRuntime.NavigationRequested += Navigate;
        _timer.Tick += OnTick;
        Navigation.SelectedItem = Navigation.MenuItems[0];
        UpdateHeader();
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_loaded) return;
        _loaded = true;
        _timer.Start();
        await ((TerminalPage)_pages["terminal"]).StartAsync();
    }

    private void Navigate(string page) => Navigation.SelectedItem = Navigation.MenuItems
        .OfType<NavigationViewItem>().First(i => (string)i.Tag == page);

    private void OnNavigationChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item || _pages is null) return;
        PageHost.Content = _pages[(string)item.Tag];
        AppRuntime.SetMessage("");
        if (PageHost.Content is DesktopPage desktop) desktop.Sync();
        _ = RefreshPageAsync();
    }

    private void OnTick(object? sender, object args)
    {
        try
        {
            AppRuntime.DrainOutput();
            ((TerminalPage)_pages["terminal"]).RefreshOutput();
            if (PageHost.Content is DesktopPage desktop) desktop.Sync();
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
                case FilesPage files: await files.RefreshAsync(); break;
                case RegistryPage registry: await registry.RefreshAsync(); break;
                case LogsPage logs: logs.Refresh(); break;
            }
        }
        catch (Exception ex) { AppRuntime.ReportError("读取运行时状态", ex); }
    }

    private void UpdateHeader()
    {
        StatusText.Text = AppRuntime.Status;
        PageError.Text = AppRuntime.Error;
    }

    public async Task ShutdownAsync()
    {
        _timer.Stop();
        try { await AppRuntime.ShutdownAsync(); }
        finally
        {
            AppRuntime.Changed -= UpdateHeader;
            AppRuntime.NavigationRequested -= Navigate;
        }
    }
}
