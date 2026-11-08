using System.Runtime.InteropServices;

namespace ImaginedNT.Core.Io;

public sealed class ConsoleInputStream : Stream, IInputAvailability
{
    private readonly Stream _stream = Console.OpenStandardInput();

    public bool InputAvailable => !Console.IsInputRedirected && Console.KeyAvailable;
    public int ReadConsoleByte() => Console.IsInputRedirected ? _stream.ReadByte() : GetCh();
    public override int Read(byte[] buffer, int offset, int count) => _stream.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => _stream.Read(buffer);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _stream.ReadAsync(buffer, cancellationToken);
    public override bool CanRead => _stream.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _stream.Dispose();
        base.Dispose(disposing);
    }

    [DllImport("ucrtbase.dll", EntryPoint = "_getch", CallingConvention = CallingConvention.Cdecl)]
    private static extern int GetCh();
}
