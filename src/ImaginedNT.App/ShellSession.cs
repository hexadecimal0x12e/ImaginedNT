using System.Runtime.InteropServices;
using System.Text;
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

    public ImaginedRuntime? Runtime => Volatile.Read(ref _runtime);
    public bool IsRunning => _run is { IsCompleted: false };
    public string ReadOutput() => _output.Drain();
    public void Send(string command) => _input.Send(command);

    public Task<ProcessExitInfo> RunAsync()
    {
        _run = Task.Run(() =>
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

internal sealed class TerminalInput : Stream, ImaginedNT.Core.Io.IInputAvailability
{
    private readonly object _gate = new();
    private readonly Queue<byte> _bytes = new();
    private TaskCompletionSource _inputAvailable = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _completed;
    public bool InputAvailable { get { lock (_gate) return _bytes.Count != 0; } }

    public void Send(string command)
    {
        lock (_gate)
        {
            if (_completed) return;
            foreach (var b in Encoding.UTF8.GetBytes(command + "\r\n")) _bytes.Enqueue(b);
            _inputAvailable.TrySetResult();
            Monitor.PulseAll(_gate);
        }
    }

    public void Complete()
    {
        lock (_gate)
        {
            _completed = true;
            _bytes.Clear();
            _inputAvailable.TrySetResult();
            Monitor.PulseAll(_gate);
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty) return 0;
        lock (_gate)
        {
            while (_bytes.Count == 0 && !_completed) Monitor.Wait(_gate);
            var count = Math.Min(buffer.Length, _bytes.Count);
            for (var i = 0; i < count; i++) buffer[i] = _bytes.Dequeue();
            return count;
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty) return 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task available;
            lock (_gate)
            {
                if (_bytes.Count != 0)
                {
                    var count = Math.Min(buffer.Length, _bytes.Count);
                    for (var i = 0; i < count; i++) buffer.Span[i] = _bytes.Dequeue();
                    return count;
                }
                if (_completed) return 0;
                if (_inputAvailable.Task.IsCompleted)
                    _inputAvailable = new(TaskCreationOptions.RunContinuationsAsynchronously);
                available = _inputAvailable.Task;
            }
            await available.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Complete();
        base.Dispose(disposing);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

internal sealed class TerminalOutput : Stream
{
    private const int MaxPendingCharacters = 64 * 1024;
    private readonly object _gate = new();
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _pending = new();

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        lock (_gate)
        {
            var chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
            _pending.Append(chars, 0, _decoder.GetChars(buffer, chars, flush: false));
            if (_pending.Length > MaxPendingCharacters)
                _pending.Remove(0, _pending.Length - MaxPendingCharacters);
        }
    }

    public string Drain()
    {
        lock (_gate)
        {
            var text = _pending.ToString();
            _pending.Clear();
            return text;
        }
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
