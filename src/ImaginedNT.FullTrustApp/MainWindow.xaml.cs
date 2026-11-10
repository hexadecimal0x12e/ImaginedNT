using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace ImaginedNT.FullTrustApp;

public sealed partial class MainWindow : Window
{
    private bool _closing;
    private bool _canClose;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 900));
        AppWindow.Closing += OnClosing;
        RootFrame.Navigate(typeof(MainPage));
    }

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_canClose) return;
        args.Cancel = true;
        if (_closing) return;
        _closing = true;
        try
        {
            if (RootFrame.Content is MainPage page) await page.ShutdownAsync();
        }
        finally
        {
            _canClose = true;
            Close();
        }
    }
}
