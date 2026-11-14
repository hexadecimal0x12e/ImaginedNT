using System.Runtime.InteropServices;
using ImaginedNT.AppShared.Terminal;
using ImaginedNT.AppShared.Gui;
using ImaginedNT.Core.Diagnostics;
using ImaginedNT.Core.Runtime;
using ImaginedNT.FullTrustApp.Gui;
using ImaginedNT.FullTrustApp.MockWindows;

namespace ImaginedNT.FullTrustApp.Hosting;

internal sealed class HostSession : IAsyncDisposable
{
    private readonly TerminalInput _input = new();
    private readonly TerminalOutput _output = new();
    private readonly TraceLog _log;
    private Task<ProcessExitInfo>? _run;
    private readonly TaskCompletionSource<ProcessExitInfo> _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task<ImaginedProcess>? _insert;
    private Task<ImaginedProcess>? _startup;
    private Task? _stop;
    private bool _disposed;
    public ImaginedRuntime Runtime { get; }
    public WindowManager Windows { get; }
    public VirtualWindowsSystem System { get; }
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
        Runtime = new ImaginedRuntime(options, BuiltinApis.Assemblies.Append(typeof(WindowApis).Assembly).Append(typeof(GdiApis).Assembly).ToArray());
        Windows = GuiLibrary.Attach(Runtime, new WindowManager(Runtime, useLightTheme: true, respectWindowBorderStyle: true));
        Drawing = new GdiDrawing(Windows);
        Windows.Drawing = Drawing;
        System = new VirtualWindowsSystem(Runtime);
    }

    public string ReadOutput() => _output.Drain();
    public void Send(string command) => _input.Send(command);

    public Task<ProcessExitInfo> RunAsync(string image, string arguments)
    {
        if (_run is not null) throw new InvalidOperationException("此会话已经启动。");
        return _run = RunCoreAsync(image, arguments);
    }

    private async Task<ProcessExitInfo> RunCoreAsync(string image, string arguments)
    {
        await System.StartAsync();
        if (System.IsStopping || !Runtime.Options.ExecuteImage) return await _idle.Task;
        try { await RunStartupProcessAsync(image, arguments); }
        catch (OperationCanceledException) when (System.IsStopping) { }
        catch (Exception ex) { AppRuntime.ReportError("启动初始来宾程序", ex); }
        return await _idle.Task;
    }

    private async Task RunStartupProcessAsync(string image, string arguments)
    {
        ImaginedProcess process;
        while (true)
        {
            var shell = await System.WaitForLogonAsync();
            try
            {
                process = await (_startup = StartProcessCoreAsync(image, shell.StartupDesktop, arguments));
                break;
            }
            catch (UnauthorizedAccessException) when (!System.IsStopping && System.GetShell(shell.StartupDesktop) is null) { }
        }
        try
        {
            await process.RunAsync();
        }
        finally { _log.Info($"startup guest stopped: pid={process.Id}, exit={process.ExitCode}"); }
    }

    private string GetGuestImagePath(string image)
    {
        if (!Path.IsPathFullyQualified(image)) return "C:\\" + image.TrimStart('\\', '/');
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
        return _insert = StartProcessCoreAsync(image, desktop, arguments);
    }

    private async Task<ImaginedProcess> StartProcessCoreAsync(string image, DesktopObject? desktop, string arguments)
    {
        await System.StartAsync();
        if (System.IsStopping) throw new OperationCanceledException();
        var process = await System.StartUserProcessAsync(image, desktop, $"\"{GetGuestImagePath(image)}\" {arguments}".TrimEnd());
        _log.Info($"host inserted process: pid={process.Id}, user={process.Token.User.Name}, image={image}");
        return process;
    }

    public Task StopAsync() => _stop ??= StopCoreAsync();

    private async Task StopCoreAsync()
    {
        System.BeginStop();
        _input.Complete();
        _idle.TrySetResult(new ProcessExitInfo(0, false, TimeSpan.Zero));
        if (_startup is not null)
            try { await _startup; }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _log.Error($"startup guest failed: {ex}"); }
        if (_insert is not null)
            try { await _insert; }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _log.Error($"inserted guest failed: {ex}"); }
        Windows.CloseAll();
        await System.StopAsync();
        if (_run is not null)
            try { await _run; }
            catch (OperationCanceledException) { }
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
