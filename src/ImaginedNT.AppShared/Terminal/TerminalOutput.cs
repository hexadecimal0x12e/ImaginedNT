using System.Text;

namespace ImaginedNT.AppShared.Terminal;

public sealed class TerminalOutput : Stream
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
