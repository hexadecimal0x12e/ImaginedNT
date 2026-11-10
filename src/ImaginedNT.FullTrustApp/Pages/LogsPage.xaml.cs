using ImaginedNT.FullTrustApp.Hosting;
using Microsoft.UI.Xaml.Controls;

namespace ImaginedNT.FullTrustApp.Pages;

public sealed partial class LogsPage : Page
{
    public LogsPage() => InitializeComponent();

    internal void Refresh() => PageRows.Set(LogList,
        (AppRuntime.Session?.Runtime.Trace.Snapshot() ?? []).Concat(AppRuntime.Log.Snapshot())
            .OrderBy(e => e.Timestamp).TakeLast(300)
            .Select(e => $"{e.Timestamp:HH:mm:ss.fff} [{e.Kind}] p{e.ProcessId}:t{e.ThreadId} {e.Message}"));
}
