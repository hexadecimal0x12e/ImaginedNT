using System.Globalization;
using ImaginedNT.Core.Runtime;
using ImaginedNT.FullTrustApp.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace ImaginedNT.FullTrustApp.MockWindows;

public sealed partial class VirtualWindowsSession : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private HostSession? _session;
    private VirtualSessionSnapshot[] _sessions = [];
    private bool _updating;
    private bool _busy;
    private bool _closed;
    private uint? _credentialSession;
    private VirtualSessionState? _credentialState;
    private VirtualSessionSnapshot? Selected => SessionSelector.SelectedIndex >= 0 && SessionSelector.SelectedIndex < _sessions.Length
        ? _sessions[SessionSelector.SelectedIndex] : null;

    public VirtualWindowsSession()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1360, 900));
        Compositor.SetHostWindow(this);
        AppRuntime.Changed += UpdateSession;
        _timer.Tick += OnTick;
        Closed += OnClosed;
        _timer.Start();
        UpdateSession();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _closed = true;
        _timer.Stop();
        AppRuntime.Changed -= UpdateSession;
        PasswordInput.Password = ConfirmPasswordInput.Password = "";
        MessageFlyout.Hide();
        Compositor.Attach(null);
        Compositor.SetHostWindow(null);
        _session = null;
    }

    private void OnTick(object? sender, object args)
    {
        try { Sync(); }
        catch (Exception ex)
        {
            _timer.Stop();
            AppRuntime.ReportError("刷新虚拟会话桌面", ex);
        }
    }

    private void UpdateSession()
    {
        var session = AppRuntime.IsRunning ? AppRuntime.Session : null;
        RuntimeButton.Content = AppRuntime.IsStopping ? "正在停止…" : session is not null ? "停止运行时" : "启动运行时";
        RuntimeButton.IsEnabled = AppRuntime.IsRunning || AppRuntime.CanConfigure;
        if (!ReferenceEquals(_session, session))
        {
            MessageFlyout.Hide();
            UserInput.Text = PasswordInput.Password = ConfirmPasswordInput.Password = "";
            _session = session;
            Compositor.Attach(session?.Windows);
        }
        Sync();
    }

    private void Sync()
    {
        if (_closed) return;
        var runtime = _session?.Runtime;
        _updating = true;
        try
        {
            _sessions = _session?.System.Sessions.ToArray() ?? [];
            var names = _sessions.Select(s => $"会话 {s.Id} · {(s.UserName.Length == 0 ? "未登录" : s.UserName)}").ToArray();
            if (SessionSelector.ItemsSource is not string[] old || !old.SequenceEqual(names)) SessionSelector.ItemsSource = names;
            SessionSelector.SelectedIndex = Array.FindIndex(_sessions, s => s.Id == runtime?.ActiveConsoleSessionId);
        }
        finally { _updating = false; }
        var selected = Selected;
        if (_credentialSession != selected?.Id || _credentialState != selected?.State)
        {
            PasswordInput.Password = ConfirmPasswordInput.Password = "";
            if (selected?.State == VirtualSessionState.SignedOut) UserInput.Text = "";
            _credentialSession = selected?.Id;
            _credentialState = selected?.State;
        }
        var setup = _session?.System.NeedsAccount == true && selected?.State == VirtualSessionState.SignedOut;
        var desktop = _session?.Windows.DisplayDesktop;
        var signedIn = selected is { State: VirtualSessionState.Active, ShellId: not null };
        var inputDesktop = runtime?.ActiveConsoleSessionId is uint id ? runtime.WindowStations.GetInputDesktop(id) : null;
        var secure = selected is null || (desktop == inputDesktop && selected.State != VirtualSessionState.Active);
        LogonPanel.Visibility = secure ? Visibility.Visible : Visibility.Collapsed;
        LogonStatus.Text = selected?.State switch
        {
            VirtualSessionState.Locked => $"{selected.UserName} 已锁定",
            VirtualSessionState.SignedOut => setup ? "创建首个账户" : "登录虚拟会话",
            VirtualSessionState.SigningIn => "正在登录…",
            VirtualSessionState.SigningOut => "正在注销…",
            _ => _session is null ? "运行时未启动" : "正在初始化虚拟 Windows…"
        };
        SignInButton.Visibility = setup ? Visibility.Collapsed : Visibility.Visible;
        SignInButton.IsEnabled = !_busy && !setup && (selected?.State is VirtualSessionState.SignedOut or VirtualSessionState.Locked);
        SignInButton.Content = selected?.State == VirtualSessionState.Locked ? "解锁" : "登录";
        UserInput.IsEnabled = !_busy && selected?.State == VirtualSessionState.SignedOut;
        PasswordInput.IsEnabled = !_busy && (selected?.State is VirtualSessionState.SignedOut or VirtualSessionState.Locked);
        ConfirmPasswordInput.IsEnabled = !_busy && setup;
        ConfirmPasswordInput.Visibility = AccountSetupHint.Visibility = CreateUserButton.Visibility = setup ? Visibility.Visible : Visibility.Collapsed;
        CreateUserButton.IsEnabled = !_busy && setup;
        AddUserButton.IsEnabled = !_busy && selected is not null && _session?.System.CanManageUsers(selected.Id) == true;
        if (selected?.State == VirtualSessionState.Locked) UserInput.Text = selected.UserName;
        LockButton.IsEnabled = !_busy && signedIn;
        SignOutButton.IsEnabled = !_busy && (selected?.State is VirtualSessionState.Active or VirtualSessionState.Locked or VirtualSessionState.SigningOut);
        RunButton.IsEnabled = !_busy && signedIn && desktop == inputDesktop;
        NewSessionButton.IsEnabled = !_busy && _session is not null && _sessions.Length != 0;
        SessionSelector.IsEnabled = !_busy;
        BroadcastButton.IsEnabled = _session is not null;
        FollowInputButton.IsEnabled = _session is not null;
        DesktopTitle.Text = desktop is null ? "虚拟 Windows 桌面" : $"{desktop.WindowStation.Name}\\{desktop.Name}";
        SessionStatus.Text = selected is null ? "" : $"会话 {selected.Id} · {selected.UserName} · {StateText(selected.State)}" +
            (selected.ShellId is int pid ? $" · Explorer PID {pid}" : "");
        ErrorText.Text = AppRuntime.Error;
        Compositor.Sync();
    }

    private static string StateText(VirtualSessionState state) => state switch
    {
        VirtualSessionState.Starting => "初始化中",
        VirtualSessionState.SignedOut => "未登录",
        VirtualSessionState.SigningIn => "正在登录",
        VirtualSessionState.Active => "桌面就绪",
        VirtualSessionState.Locked => "已锁定",
        VirtualSessionState.SigningOut => "正在注销",
        _ => "已停止"
    };

    private async Task ExecuteAsync(string action, Func<HostSession, Task> execute)
    {
        if (_busy || _session is not { } session) return;
        _busy = true;
        Sync();
        try
        {
            await execute(session);
            if (!ReferenceEquals(_session, session)) return;
            AppRuntime.SetMessage("");
            session.Windows.ViewDesktop(null);
            if (Selected?.State == VirtualSessionState.Active) Compositor.Focus(FocusState.Programmatic);
        }
        catch (OperationCanceledException) when (!AppRuntime.IsRunning) { }
        catch (Exception ex) { AppRuntime.ReportError(action, ex); }
        finally { _busy = false; Sync(); }
    }

    private async void OnSignIn(object sender, RoutedEventArgs args)
    {
        if (Selected is not { } selected) return;
        var user = UserInput.Text.Trim();
        var password = PasswordInput.Password;
        PasswordInput.Password = "";
        await ExecuteAsync("登录虚拟会话", s => s.System.SignInAsync(selected.Id, user, password));
    }

    private async void OnCreateUser(object sender, RoutedEventArgs args)
    {
        if (Selected is not { } selected || _session?.System.NeedsAccount != true) return;
        var user = UserInput.Text.Trim();
        var password = PasswordInput.Password;
        var matches = password == ConfirmPasswordInput.Password;
        PasswordInput.Password = ConfirmPasswordInput.Password = "";
        if (!matches) { AppRuntime.SetMessage("两次输入的密码不一致。"); return; }
        await ExecuteAsync("创建账户", async s =>
        {
            await s.System.CreateUserAsync(selected.Id, user, password);
            await s.System.SignInAsync(selected.Id, user, password);
        });
    }

    private async void OnAddUser(object sender, RoutedEventArgs args)
    {
        if (Selected is not { } selected) return;
        await ExecuteAsync("创建用户", async s =>
        {
            var name = new TextBox { Header = "用户名", MaxLength = 64 };
            var password = new PasswordBox { Header = "密码" };
            var confirm = new PasswordBox { Header = "确认密码" };
            var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
            var content = new StackPanel { Spacing = 12 };
            content.Children.Add(name);
            content.Children.Add(password);
            content.Children.Add(confirm);
            content.Children.Add(error);
            var dialog = new ContentDialog
            {
                XamlRoot = ((FrameworkElement)Content).XamlRoot, Title = "创建用户", Content = content,
                PrimaryButtonText = "创建", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary
            };
            dialog.PrimaryButtonClick += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(name.Text) && password.Password == confirm.Password) return;
                e.Cancel = true;
                error.Text = string.IsNullOrWhiteSpace(name.Text) ? "请输入用户名。" : "两次输入的密码不一致。";
            };
            try
            {
                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                    await s.System.CreateUserAsync(selected.Id, name.Text.Trim(), password.Password);
            }
            finally { password.Password = confirm.Password = ""; }
        });
    }

    private async void OnLock(object sender, RoutedEventArgs args)
    {
        if (Selected is { } selected) await ExecuteAsync("锁定虚拟会话", s => s.System.LockAsync(selected.Id));
    }

    private async void OnSignOut(object sender, RoutedEventArgs args)
    {
        if (Selected is { } selected) await ExecuteAsync("注销虚拟会话", s => s.System.SignOutAsync(selected.Id));
    }

    private async void OnNewSession(object sender, RoutedEventArgs args) =>
        await ExecuteAsync("新建虚拟会话", s => s.System.CreateSessionAsync());

    private void OnSessionSelected(object sender, SelectionChangedEventArgs args)
    {
        if (_updating || Selected is not { } selected || _session is null) return;
        _session.Runtime.SetActiveConsoleSession(selected.Id);
        _session.Windows.ViewDesktop(null);
        Sync();
    }

    private async void OnRunProgram(object sender, RoutedEventArgs args) => await ExecuteAsync("在虚拟桌面运行程序", async s =>
    {
        var desktop = s.Windows.DisplayDesktop;
        if (s.System.GetShell(desktop) is null) return;
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
        picker.FileTypeFilter.Add(".exe");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file is not null && ReferenceEquals(AppRuntime.Session, s) && AppRuntime.IsRunning && s.System.GetShell(desktop) is not null)
            await s.StartProcessAsync(file.Path, desktop);
    });

    private async void OnRuntimeClicked(object sender, RoutedEventArgs args)
    {
        if (AppRuntime.IsRunning) await AppRuntime.StopAsync();
        else if (AppRuntime.CanConfigure) await AppRuntime.StartAsync();
    }

    private void OnManageStations(object sender, RoutedEventArgs args)
    {
        AppRuntime.Navigate("stations");
        ((App)Application.Current).MainWindow?.Activate();
    }

    private void OnFollowInput(object sender, RoutedEventArgs args)
    {
        _session?.Windows.ViewDesktop(null);
        Sync();
    }

    private void OnBroadcastClicked(object sender, RoutedEventArgs args)
    {
        if (_session is null) return;
        var text = MessageInput.Text;
        if (MessageInput.SelectedItem is ComboBoxItem item && text == item.Content?.ToString()) text = (string)item.Tag;
        if (!TryParseInteger(text, out var message) || message > uint.MaxValue ||
            !TryParseInteger(WParamInput.Text, out var wParam) || !TryParseInteger(LParamInput.Text, out var lParam))
        {
            MessageResult.Text = "请输入有效的消息 ID（uint32）和参数（64 位整数）。";
            return;
        }
        try
        {
            var count = _session.Windows.BroadcastMessage((uint)message, wParam, unchecked((long)lParam));
            AppRuntime.Log.Info($"broadcast window message: message=0x{message:X8}, windows={count}");
            MessageResult.Text = $"已向 {count} 个窗口投递消息。";
        }
        catch (Exception ex) { AppRuntime.ReportError("发送全局窗口消息", ex); }
    }

    private static bool TryParseInteger(string text, out ulong value)
    {
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return ulong.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
        if (ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return true;
        if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var signed)) return false;
        value = unchecked((ulong)signed);
        return true;
    }
}
