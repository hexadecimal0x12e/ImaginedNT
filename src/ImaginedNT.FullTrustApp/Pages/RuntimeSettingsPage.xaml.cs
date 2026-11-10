using ImaginedNT.App.Settings;
using ImaginedNT.FullTrustApp.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace ImaginedNT.FullTrustApp.Pages;

public sealed partial class RuntimeSettingsPage : Page
{
    private readonly RuntimeSettingsView _view;

    public RuntimeSettingsPage()
    {
        InitializeComponent();
        RootProcessInput.Text = AppRuntime.RootProcessPath;
        RootArgumentsInput.Text = AppRuntime.RootProcessArguments;
        _view = new RuntimeSettingsView(AppRuntime.Settings, AppRuntime.Log,
            AppRuntime.GetDrivePaths, () => AppRuntime.Navigate("sandbox"));
        SettingsHost.Content = _view;
    }

    private void OnLoaded(object sender, RoutedEventArgs args) => _view.RefreshSandbox();

    private async void OnChooseRootProcessClicked(object sender, RoutedEventArgs args)
    {
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
            picker.FileTypeFilter.Add(".exe");
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(((App)Application.Current).MainWindow!));
            if (await picker.PickSingleFileAsync() is { } file) RootProcessInput.Text = file.Path;
        }
        catch (Exception ex) { AppRuntime.ReportError("选择根进程", ex); }
    }

    private void OnSaveRootProcessClicked(object sender, RoutedEventArgs args)
    {
        try
        {
            var path = RootProcessInput.Text.Trim();
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("请选择根进程程序。");
            _ = Path.GetFullPath(path, AppRuntime.SandboxRoot);
            AppRuntime.SaveConfig(path, RootArgumentsInput.Text);
            RootProcessStatus.Text = "根进程已保存，下次启动时生效。";
        }
        catch (Exception ex)
        {
            RootProcessStatus.Text = $"保存根进程失败：{ex.Message}";
            AppRuntime.ReportError("保存根进程", ex);
        }
    }
}
