using System.Runtime.InteropServices;
using ImaginedNT.Core.Diagnostics;
using ImaginedNT.Core.Runtime;

namespace ImaginedNT.App.Gui;

/// <summary>
/// One windowed guest session.  It owns a separate <see cref="ImaginedRuntime"/> whose API set
/// includes the App-side USER32/DWM library, plus the <see cref="WindowManager"/> that the XAML
/// compositor renders and drives with input events.
/// </summary>
internal sealed class GuiSession(RuntimeOptions options, TraceLog log)
    : IAsyncDisposable
{
    private readonly string _sandboxRoot = options.SandboxRoot!;
    private readonly TraceLog _log = log;
    private ImaginedRuntime? _runtime;
    private WindowManager? _windows;
    private Task<ProcessExitInfo>? _run;
    private readonly TaskCompletionSource<ProcessExitInfo> _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _stopping;

    public ImaginedRuntime? Runtime => Volatile.Read(ref _runtime);
    public WindowManager? Windows => Volatile.Read(ref _windows);
    public bool IsRunning => _run is { IsCompleted: false };

    public Task<ProcessExitInfo> RunAsync(string imageName = "window.exec", string guestCommandLine = "\"C:\\window.exe\"")
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("ImaginedNT 运行时需要 x64，请使用 x64 配置启动。");

        options.StandardInput = Stream.Null;
        options.StandardOutput = Stream.Null;
        options.StandardError = Stream.Null;

        var assemblies = BuiltinApis.Assemblies.Append(typeof(WindowApis).Assembly).ToArray();
        var runtime = new ImaginedRuntime(options, assemblies);
        var windows = GuiLibrary.Attach(runtime, new WindowManager(runtime));
        Volatile.Write(ref _runtime, runtime);
        Volatile.Write(ref _windows, windows);

        if (!options.ExecuteImage) return _run = _idle.Task;
        var image = Path.Combine(_sandboxRoot, imageName);
        _run = Task.Run(() =>
        {
            var process = runtime.CreateProcess(image, guestCommandLine, "C:\\");
            try
            {
                process.LoadImage(image);
                _log.Info($"window session started: pid={process.Id}, image={image}");
                return process.Run();
            }
            catch
            {
                process.Terminate(-1);
                throw;
            }
            finally
            {
                // Publish the end of the session to the compositor even when the guest ended
                // without a message loop or crashed.
                windows.CleanupProcess(process.Id);
                _log.Info($"window session stopped: pid={process.Id}, exit={process.ExitCode?.ToString() ?? "?"}");
            }
        });
        return _run;
    }

    public async Task StopAsync()
    {
        if (_stopping) return;
        _stopping = true;
        _idle.TrySetResult(new ProcessExitInfo(0, false, TimeSpan.Zero));
        // Waking the message queues first lets a guest parked in GetMessageW unwind normally.
        Windows?.CloseAll();
        try
        {
            if (_run is not null)
                await Task.WhenAny(_run, Task.Delay(300));
            await Task.Run(() =>
            {
                foreach (var process in Runtime?.Processes.Reverse() ?? [])
                    if (process.ExitCode is null) process.Terminate(0);
            });
            if (_run is not null)
            {
                try { await _run; }
                catch (Exception ex) { _log.Error($"window session failed: {ex}"); }
            }
        }
        finally
        {
            _stopping = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        Windows?.Dispose();
        await Task.Run(() => Runtime?.Dispose());
        Volatile.Write(ref _windows, null);
        Volatile.Write(ref _runtime, null);
    }
}
