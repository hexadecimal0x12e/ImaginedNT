using System.Runtime.InteropServices;
using ImaginedNT.App;
using ImaginedNT.App.Gui;
using ImaginedNT.Core.Diagnostics;
using ImaginedNT.Core.Runtime;
using ImaginedNT.FullTrustApp.Gui;

namespace ImaginedNT.FullTrustApp.Hosting;

internal sealed class HostSession : IAsyncDisposable
{
    private readonly TerminalInput _input = new();
    private readonly TerminalOutput _output = new();
    private readonly TraceLog _log;
    private Task<ProcessExitInfo>? _run;
    private readonly TaskCompletionSource<ProcessExitInfo> _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task<ImaginedProcess>? _insert;
    private Task? _stop;
    private bool _disposed;
    public ImaginedRuntime Runtime { get; }
    public WindowManager Windows { get; }
    private GdiDrawing Drawing { get; }

    public HostSession(RuntimeOptions options, TraceLog log)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("ImaginedNT 运行时需要 x64。");
        _log = log;
        Directory.CreateDirectory(options.SandboxRoot!);
        options.StandardInput = _input;
        options.StandardOutput = _output;
        options.StandardError = _output;
        Runtime = new ImaginedRuntime(options, BuiltinApis.Assemblies.Append(typeof(WindowApis).Assembly).ToArray());
        Windows = GuiLibrary.Attach(Runtime, new WindowManager(Runtime));
        Drawing = new GdiDrawing(Windows);
        Windows.Drawing = Drawing;
    }

    public string ReadOutput() => _output.Drain();
    public void Send(string command) => _input.Send(command);

    public Task<ProcessExitInfo> RunAsync(string image, string arguments)
    {
        if (_run is not null) throw new InvalidOperationException("此会话已经启动。");
        if (!Runtime.Options.ExecuteImage) return _run = _idle.Task;
        if (!File.Exists(image)) throw new FileNotFoundException("未找到来宾程序。", image);
        var guestImage = GetGuestImagePath(image);
        _run = Task.Run(() =>
        {
            var process = Runtime.CreateProcess(image, $"\"{guestImage}\" {arguments}".TrimEnd(), "C:\\");
            try
            {
                process.LoadImage(image);
                _log.Info($"host started: pid={process.Id}, image={image}"); 
                new WindowsSimulation.ImaginedSMSS().Start(Runtime, new ProcessCreateOptions { ImagePath = @"c:\windows\system32\smss.exe", CurrentDirectory = @"c:\" });
                return process.Run();
            }
            catch
            {
                process.Terminate(-1);
                throw;
            }
            finally
            {
                Windows.CleanupProcess(process.Id);
                _log.Info($"host stopped: pid={process.Id}, exit={process.ExitCode}");
            }
        });
        return _run;
    }

    private string GetGuestImagePath(string image)
    {
        var relative = Path.GetRelativePath(Runtime.Options.SandboxRoot!, image);
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar) && !Path.IsPathRooted(relative)
            ? "C:\\" + relative.Replace('/', '\\') : "C:\\" + Path.GetFileName(image);
    }

    public Task<ImaginedProcess> StartProcessAsync(string image, DesktopObject? desktop = null, string arguments = "")
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_stop is not null) throw new InvalidOperationException("此会话正在停止。");
        if (_insert is { IsCompleted: false }) throw new InvalidOperationException("正在添加进程。");
        if (desktop is not null && !Runtime.WindowStations.Contains(desktop)) throw new ArgumentException("桌面不存在或已被删除。");
        return _insert = Task.Run(() =>
        {
            var process = Runtime.CreateProcess(new ProcessCreateOptions
            {
                ImagePath = image,
                CommandLine = $"\"{GetGuestImagePath(image)}\" {arguments}".TrimEnd(),
                CurrentDirectory = "C:\\",
                ParentId = null,
                SessionId = desktop?.SessionId,
                Desktop = desktop is null ? null : $"{desktop.WindowStation.Name}\\{desktop.Name}",
                IsRoot = false
            });
            try
            {
                process.LoadImage(image);
                if (!process.EnsureMainThread().Start()) throw new InvalidOperationException("无法启动进程主线程。");
                _log.Info($"host inserted process: pid={process.Id}, image={image}");
                return process;
            }
            catch
            {
                process.Terminate(-1);
                throw;
            }
        });
    }

    public Task StopAsync() => _stop ??= StopCoreAsync();

    private async Task StopCoreAsync()
    {
        _input.Complete();
        _idle.TrySetResult(new ProcessExitInfo(0, false, TimeSpan.Zero));
        if (_insert is not null)
            try { await _insert; }
            catch (Exception ex) { _log.Error($"inserted guest failed: {ex}"); }
        Windows.CloseAll();
        if (_run is not null) await Task.WhenAny(_run, Task.Delay(300));
        await Task.Run(() =>
        {
            foreach (var process in Runtime.Processes.Reverse())
                if (process.ExitCode is null) process.Terminate(0);
        });
        if (_run is not null)
            try { await _run; }
            catch (Exception ex) { _log.Error($"guest failed: {ex}"); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { await StopAsync(); }
        finally
        {
            try { await Task.Run(Runtime.Dispose); }
            finally
            {
                Drawing.Dispose();
                Windows.Dispose();
                _input.Dispose();
                _output.Dispose();
            }
        }
    }
}
