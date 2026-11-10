using Microsoft.UI.Xaml;

namespace ImaginedNT.FullTrustApp;

public partial class App : Application
{
    private Window? _window;
    internal Window? MainWindow => _window;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
