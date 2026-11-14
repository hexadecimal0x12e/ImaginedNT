using System.Text;

namespace ImaginedNT.AppShared.Terminal;

public sealed class TerminalInput : Stream, ImaginedNT.Core.Io.IInputAvailability
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

