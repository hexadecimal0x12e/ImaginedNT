using ImaginedNT.FullTrustApp.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace ImaginedNT.FullTrustApp.Pages;

public sealed partial class TerminalPage : Page
{
    private string _rootProcessPath = AppRuntime.RootProcessPath;
    private string _rootProcessArguments = AppRuntime.RootProcessArguments;

    public TerminalPage()
    {
        InitializeComponent();
        ImageInput.Text = _rootProcessPath;
        ArgumentsInput.Text = _rootProcessArguments;
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        AppRuntime.Changed += UpdateState;
        UpdateState();
        RefreshOutput();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args) => AppRuntime.Changed -= UpdateState;

    private void UpdateState()
    {
        if (_rootProcessPath != AppRuntime.RootProcessPath || _rootProcessArguments != AppRuntime.RootProcessArguments)
        {
            ImageInput.Text = _rootProcessPath = AppRuntime.RootProcessPath;
            ArgumentsInput.Text = _rootProcessArguments = AppRuntime.RootProcessArguments;
        }
        StartButton.IsEnabled = WindowButton.IsEnabled = AppRuntime.CanConfigure;
        ImageInput.IsEnabled = ArgumentsInput.IsEnabled = AppRuntime.CanConfigure;
        StopButton.IsEnabled = CommandInput.IsEnabled = SendButton.IsEnabled = AppRuntime.IsRunning;
    }

    internal Task StartAsync() => AppRuntime.StartAsync(ImageInput.Text, ArgumentsInput.Text);

    internal void RefreshOutput()
    {
        if (TerminalText.Text == AppRuntime.TerminalOutput) return;
        TerminalText.Text = AppRuntime.TerminalOutput;
        TerminalScroll.ChangeView(null, TerminalScroll.ScrollableHeight, null);
    }

    private void SendCommand()
    {
        if (!AppRuntime.IsRunning) return;
        AppRuntime.SendCommand(CommandInput.Text);
        CommandInput.Text = "";
    }

    private async void OnStartClicked(object sender, RoutedEventArgs args) => await StartAsync();

    private async void OnWindowClicked(object sender, RoutedEventArgs args)
    {
        await AppRuntime.StartAsync("gdi.exe", "");
    }

    private async void OnStopClicked(object sender, RoutedEventArgs args) => await AppRuntime.StopAsync();
    private void OnSendClicked(object sender, RoutedEventArgs args) => SendCommand();

    private void OnClearClicked(object sender, RoutedEventArgs args)
    {
        AppRuntime.ClearOutput();
        RefreshOutput();
    }

    private void OnCommandKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Enter) return;
        SendCommand();
        args.Handled = true;
    }
}
