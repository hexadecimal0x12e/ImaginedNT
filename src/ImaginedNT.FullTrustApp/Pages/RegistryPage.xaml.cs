using System.Text;
using ImaginedNT.Core.Runtime;
using ImaginedNT.FullTrustApp.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace ImaginedNT.FullTrustApp.Pages;

public sealed partial class RegistryPage : Page
{
    private string _registryPath = "HKEY_CURRENT_USER";
    private bool _refreshing;

    public RegistryPage() => InitializeComponent();

    internal async Task RefreshAsync()
    {
        if (_refreshing || !AppRuntime.IsRunning) return;
        _refreshing = true;
        try { await RefreshRegistryAsync(); }
        catch (Exception ex) { AppRuntime.ReportError("读取注册表", ex); }
        finally { _refreshing = false; }
    }

    private async Task RefreshRegistryAsync()
    {
        var runtime = AppRuntime.Session?.Runtime;
        var root = runtime?.PrimaryProcess?.FileSystem.Root;
        if (runtime is null || root is null) return;
        var path = _registryPath;
        var key = await Task.Run(() => ImaginedRegistry.GetSnapshot(runtime, root, path));
        if (!AppRuntime.IsRunning || runtime != AppRuntime.Session?.Runtime || path != _registryPath) return;
        if (key is null) throw new DirectoryNotFoundException($"注册表项不存在：{path}");
        PageRows.Set(RegistryKeys, key.SubKeys);
        PageRows.Set(RegistryValues, key.Values.Select(v => $"{(v.Name.Length == 0 ? "(默认)" : v.Name)} [{RegistryType(v.Type)}]\n{FormatRegistryValue(v)}"));
        RegistryValueHeader.Text = $"值 · {key.Values.Count} 项 · 名称 / 类型 / 数据";
    }

    private static string RegistryType(uint type) => type switch
    {
        0 => "REG_NONE", 1 => "REG_SZ", 2 => "REG_EXPAND_SZ", 3 => "REG_BINARY",
        4 => "REG_DWORD", 5 => "REG_DWORD_BIG_ENDIAN", 7 => "REG_MULTI_SZ", 11 => "REG_QWORD",
        _ => type.ToString()
    };

    private static string FormatRegistryValue(RegistryValueSnapshot v)
    {
        if (v.Type is 1 or 2 or 7) return Encoding.Unicode.GetString(v.Data, 0, Math.Min(v.Data.Length, 4096) & ~1).TrimEnd('\0').Replace("\0", " · ");
        if (v.Type == 4 && v.Data.Length == 4) return $"0x{BitConverter.ToUInt32(v.Data):X8} ({BitConverter.ToUInt32(v.Data)})";
        if (v.Type == 11 && v.Data.Length == 8) return $"0x{BitConverter.ToUInt64(v.Data):X16} ({BitConverter.ToUInt64(v.Data)})";
        return Convert.ToHexString(v.Data.AsSpan(0, Math.Min(v.Data.Length, 64))) + (v.Data.Length > 64 ? $"… ({v.Data.Length:N0} B)" : "");
    }
    private async Task GoToRegistryPathAsync()
    {
        _registryPath = RegistryPathInput.Text.Trim().TrimEnd('\\');
        RegistryKeys.ItemsSource = RegistryValues.ItemsSource = null;
        await RefreshAsync();
    }

    private async void OnRegistryRootChanged(object sender, SelectionChangedEventArgs args)
    {
        if (RegistryPathInput is null || RegistryRoot.SelectedItem is not ComboBoxItem item) return;
        RegistryPathInput.Text = item.Content.ToString();
        await GoToRegistryPathAsync();
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
        var i = _registryPath.LastIndexOf('\\');
        if (i < 0) return;
        RegistryPathInput.Text = _registryPath[..i];
        await GoToRegistryPathAsync();
    }

    private async void OnRegistryKeyOpened(object sender, DoubleTappedRoutedEventArgs args)
    {
        if (RegistryKeys.SelectedItem is not string name) return;
        RegistryPathInput.Text = _registryPath + "\\" + name;
        await GoToRegistryPathAsync();
    }
}
