using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Storage;
using Windows.UI.Xaml;

namespace ImaginedNT.App;

public sealed partial class App : Application
{
    private MainPage? _page;

    public App()
    {
        ImaginedNT.Core.Io.HostFileSystem.SetDirectAccessRoots(Package.Current.InstalledLocation.Path,
            ApplicationData.Current.LocalFolder.Path, ApplicationData.Current.TemporaryFolder.Path);
        ImaginedNT.Core.Io.HostFileSystem.UseAppContainerApis = true;
        InitializeComponent();
        Suspending += OnSuspending;
        RequiresPointerMode = ApplicationRequiresPointerMode.WhenRequested;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (_page is null)
        {
            _page = new MainPage();
            Window.Current.Content = _page;
            Window.Current.Closed += async (_, _) => await _page.ShutdownAsync();
        }
        Window.Current.Activate();
    }

    private async void OnSuspending(object sender, SuspendingEventArgs args)
    {
        var deferral = args.SuspendingOperation.GetDeferral();
        try
        {
            if (_page is not null) await _page.StopShellAsync();
        }
        finally
        {
            deferral.Complete();
        }
    }
}
