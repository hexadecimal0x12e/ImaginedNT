using System.Globalization;
using ImaginedNT.FullTrustApp.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ImaginedNT.FullTrustApp.Pages;

public sealed partial class DesktopPage : Page
{
    private HostSession? _session;

    public DesktopPage() => InitializeComponent();

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        AppRuntime.Changed += UpdateSession;
        UpdateSession();
        Sync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        AppRuntime.Changed -= UpdateSession;
        MessageFlyout.Hide();
        _session = null;
        Compositor.Attach(null);
    }

    private void UpdateSession()
    {
        var session = AppRuntime.IsRunning ? AppRuntime.Session : null;
        RuntimeButton.Content = AppRuntime.IsStopping ? "正在停止…" : session is not null ? "停止运行时" : "启动运行时";
        RuntimeButton.IsEnabled = AppRuntime.IsRunning || AppRuntime.CanConfigure;
        BroadcastButton.IsEnabled = session is not null;
        if (session is null) MessageFlyout.Hide();
        if (ReferenceEquals(_session, session)) return;
        _session = session;
        MessageResult.Text = "";
        Compositor.Attach(session?.Windows);
    }

    private async void OnRuntimeClicked(object sender, RoutedEventArgs args)
    {
        if (AppRuntime.IsRunning) await AppRuntime.StopAsync();
        else if (AppRuntime.CanConfigure) await AppRuntime.StartAsync();
    }

    private void OnBroadcastClicked(object sender, RoutedEventArgs args)
    {
        if (!AppRuntime.IsRunning || _session is null) return;
        var text = MessageInput.Text;
        if (MessageInput.SelectedItem is ComboBoxItem item && text == item.Content?.ToString())
            text = (string)item.Tag;
        if (!TryParseInteger(text, out var message) || message > uint.MaxValue)
        {
            MessageResult.Text = "消息 ID 必须是 0 到 0xFFFFFFFF 之间的整数。";
            return;
        }
        if (!TryParseInteger(WParamInput.Text, out var wParam) || !TryParseInteger(LParamInput.Text, out var lParam))
        {
            MessageResult.Text = "wParam 和 lParam 必须是有效的 64 位整数。";
            return;
        }
        try
        {
            var count = _session.Windows.BroadcastMessage((uint)message, wParam, unchecked((long)lParam));
            AppRuntime.Log.Info($"broadcast window message: message=0x{message:X8}, wParam=0x{wParam:X16}, lParam=0x{lParam:X16}, windows={count}");
            MessageResult.Text = $"已向 {count} 个窗口投递消息。";
        }
        catch (Exception ex)
        {
            AppRuntime.ReportError("发送全局窗口消息", ex);
            MessageResult.Text = $"发送失败：{ex.Message}";
        }
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

    internal void Sync() => Compositor.Sync();
}
