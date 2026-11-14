using System.Runtime.InteropServices;
using ImaginedNT.AppShared.Terminal;
using ImaginedNT.Core.Diagnostics;
using ImaginedNT.Core.Runtime;
using ImaginedNT.Core.Io;

namespace ImaginedNT.App;

internal sealed class ShellSession(RuntimeOptions options, TraceLog log) : IAsyncDisposable
{
    private readonly TerminalInput _input = new();
    private readonly TerminalOutput _output = new();
    private ImaginedRuntime? _runtime;
    private Task<ProcessExitInfo>? _run;
    private readonly TaskCompletionSource<ProcessExitInfo> _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ImaginedRuntime? Runtime => Volatile.Read(ref _runtime);
    public bool IsRunning => _run is { IsCompleted: false };
    public string ReadOutput() => _output.Drain();
    public void Send(string command) => _input.Send(command);

    public Task<ProcessExitInfo> RunAsync()
    {
        _run = Task.Run(async () =>
        {
            if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
                throw new PlatformNotSupportedException("ImaginedNT 运行时需要 x64，请使用 x64 配置启动。");

            var sandboxRoot = options.SandboxRoot!;
            var shell = Path.Combine(sandboxRoot, "shell.exec");
            //if (!HostFileSystem.FileExists(shell))
            //    throw new FileNotFoundException("未找到 shell.exe，请先生成原生示例并将其包含在应用包中。", shell);

            shell = Path.Combine(sandboxRoot, "shell.exec");
            options.StandardInput = _input;
            options.StandardOutput = _output;
            options.StandardError = _output;
            var runtime = BuiltinApis.CreateRuntime(options);
            Volatile.Write(ref _runtime, runtime);
            if (!options.ExecuteImage) return await _idle.Task.ConfigureAwait(false);
            var process = runtime.CreateProcess(shell, "\"C:\\shell.exe\"", "C:\\");
            try
            {
                process.LoadImage(shell);
                log.Info($"shell session started: pid={process.Id}, sandbox={sandboxRoot}");
                return process.Run();
            }
            catch
            {
                process.Terminate(-1);
                throw;
            }
        });
        return _run;
    }

    public async Task StopAsync()
    {
        _input.Complete();
        _idle.TrySetResult(new ProcessExitInfo(0, false, TimeSpan.Zero));
        if (_run is null) return;
        await Task.WhenAny(_run, Task.Delay(300));
        await Task.Run(() =>
        {
            foreach (var p in Runtime?.Processes.Reverse() ?? [])
                if (p.ExitCode is null) p.Terminate(0);
        });
        try { await _run; }
        catch (Exception ex) { log.Error($"shell session failed: {ex}"); }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await Task.Run(() => Runtime?.Dispose());
        _input.Dispose();
        _output.Dispose();
    }
}
