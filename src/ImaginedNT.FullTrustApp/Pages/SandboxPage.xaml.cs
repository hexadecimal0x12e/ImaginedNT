using ImaginedNT.FullTrustApp.Hosting;
using ImaginedNT.Core.Io;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace ImaginedNT.FullTrustApp.Pages;

public sealed partial class SandboxPage : Page
{
    public SandboxPage()
    {
        InitializeComponent();
        UpdateRootInput();
        UpdateDriveControls();
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        AppRuntime.Changed += UpdateState;
        UpdateRootInput();
        UpdateState();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args) => AppRuntime.Changed -= UpdateState;

    private void UpdateState()
    {
        SandboxControls.IsEnabled = AppRuntime.CanConfigure;
        RemoveDriveButton.IsEnabled = DriveList.Items.Count > 0 && AppRuntime.CanConfigure;
        AddDriveButton.IsEnabled = DriveLetterInput.SelectedIndex >= 0 && AppRuntime.CanConfigure;
    }

    private void UpdateDriveControls()
    {
        DriveList.ItemsSource = AppRuntime.Drives.OrderBy(p => p.Key).Select(p => $"{p.Key}:\\ → {AppRuntime.DriveYaml.GetValueOrDefault(p.Key) ?? p.Value}").ToArray();
        DriveLetterInput.ItemsSource = Enumerable.Range('A', 26).Select(c => (char)c)
            .Where(c => c != 'C' && !AppRuntime.Drives.ContainsKey(c)).Select(c => c.ToString()).ToArray();
        DriveLetterInput.SelectedIndex = DriveLetterInput.Items.Count == 0 ? -1 : 0;
        UpdateState();
    }

    private void UpdateRootInput()
    {
        var yaml = AppRuntime.DriveYaml.GetValueOrDefault('C');
        SandboxInput.Text = yaml ?? AppRuntime.SandboxRoot;
        RootDefinitionText.Text = yaml is null ? "文件夹映射" : $"YAML 定义 · 主机根目录：{AppRuntime.SandboxRoot}";
    }

    private void SaveConfig()
    {
        AppRuntime.SaveConfig();
        UpdateRootInput();
    }

    private void OnSaveConfigClicked(object sender, RoutedEventArgs args)
    {
        if (!AppRuntime.CanConfigure) { AppRuntime.SetMessage("请先停止来宾。"); return; }
        try { SaveConfig(); }
        catch (Exception ex) { AppRuntime.ReportError("保存配置", ex); }
    }

    private static async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(((App)Application.Current).MainWindow!));
        return (await picker.PickSingleFolderAsync())?.Path;
    }

    private async void OnChooseRootClicked(object sender, RoutedEventArgs args)
    {
        if (!AppRuntime.CanConfigure) return;
        try
        {
            var path = await PickFolderAsync();
            if (path is null || !AppRuntime.CanConfigure) return;
            AppRuntime.DriveYaml.Remove('C');
            AppRuntime.SandboxRoot = Path.GetFullPath(path);
            SaveConfig();
        }
        catch (Exception ex) { AppRuntime.ReportError("选择沙盒目录", ex); }
    }

    private async void OnAddDriveClicked(object sender, RoutedEventArgs args)
    {
        if (!AppRuntime.CanConfigure || DriveLetterInput.SelectedItem is not string letter) return;
        try
        {
            var path = await PickFolderAsync();
            if (path is null || !AppRuntime.CanConfigure) return;
            AppRuntime.DriveYaml.Remove(letter[0]);
            AppRuntime.Drives[letter[0]] = Path.GetFullPath(path);
            UpdateDriveControls();
            SaveConfig();
        }
        catch (Exception ex) { AppRuntime.ReportError("添加虚拟驱动器", ex); }
    }

    private async void OnChooseRootYamlClicked(object sender, RoutedEventArgs args) => await LoadYamlAsync('C');

    private async void OnLoadYamlClicked(object sender, RoutedEventArgs args)
    {
        if (DriveList.SelectedItem is string selected) await LoadYamlAsync(selected[0]);
        else if (DriveLetterInput.SelectedItem is string letter) await LoadYamlAsync(letter[0]);
    }

    private async Task LoadYamlAsync(char letter)
    {
        if (!AppRuntime.CanConfigure) return;
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".yaml");
            picker.FileTypeFilter.Add(".yml");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(((App)Application.Current).MainWindow!));
            var file = await picker.PickSingleFileAsync();
            if (file is null || !AppRuntime.CanConfigure) return;
            var table = DriveMapTable.Load(file.Path);
            if (letter == 'C') AppRuntime.SandboxRoot = table.Options.HostDirectory;
            else AppRuntime.Drives[letter] = table.Options.HostDirectory;
            AppRuntime.DriveYaml[letter] = file.Path;
            SaveConfig();
            UpdateDriveControls();
            AppRuntime.Log.Info($"drive {letter}: loaded YAML {file.Path}");
        }
        catch (Exception ex) { AppRuntime.ReportError("加载卷 YAML", ex); }
    }

    private void OnRemoveDriveClicked(object sender, RoutedEventArgs args)
    {
        if (!AppRuntime.CanConfigure || DriveList.SelectedItem is not string selected) return;
        if (selected[0] == 'C') return;
        AppRuntime.DriveYaml.Remove(selected[0]);
        AppRuntime.Drives.Remove(selected[0]);
        UpdateDriveControls();
        try { SaveConfig(); }
        catch (Exception ex) { AppRuntime.ReportError("移除虚拟驱动器", ex); }
    }

    private async void OnOpenSandboxClicked(object sender, RoutedEventArgs args)
    {
        try
        {
            if (!AppRuntime.DriveYaml.ContainsKey('C')) Directory.CreateDirectory(AppRuntime.SandboxRoot);
            await Windows.System.Launcher.LaunchFolderPathAsync(AppRuntime.SandboxRoot);
        }
        catch (Exception ex) { AppRuntime.ReportError("打开沙盒目录", ex); }
    }
}
