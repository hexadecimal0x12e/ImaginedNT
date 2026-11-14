using ImaginedNT.Core.Nt;
using ImaginedNT.Core.Objects;

namespace ImaginedNT.Core.Io;

public sealed class DiskFileObject : FileObject
{
    private readonly FileStream _stream;

    public DiskFileObject(string hostPath, string ntPath, FileStream stream, uint grantedAccess)
    {
        HostPath = hostPath;
        NtPath = ntPath;
        _stream = stream;
        Name = ntPath;
        FullPath = ntPath;
        GrantedAccess = grantedAccess;
    }

    public string HostPath { get; }
    public string NtPath { get; }
    public uint GrantedAccess { get; }
    public override string? Path => NtPath;
    public override long Position { get => _stream.Position; set => _stream.Position = value; }
    public override long Length => _stream.Length;
    public override bool CanRead => _stream.CanRead;
    public override bool CanWrite => _stream.CanWrite;
    public override bool CanSeek => _stream.CanSeek;
    public override nint GetNativeHandle()
    {
        _stream.Flush();
        return _stream.SafeFileHandle.DangerousGetHandle();
    }
    public override int Read(Span<byte> buffer) => _stream.Read(buffer);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _stream.ReadAsync(buffer, cancellationToken);
    public override void Write(ReadOnlySpan<byte> buffer) => _stream.Write(buffer);
    public override void Flush() => _stream.Flush();
    protected override Stream GetSeekableStream() => _stream;
    public override string? Info => $"{HostPath} pos={Position} len={Length}";

    protected override void OnDispose()
    {
        _stream.Dispose();
        if (DeleteOnClose)
        {
            try { HostFileSystem.DeleteFile(HostPath); } catch { /* best effort, matching FILE_DELETE_ON_CLOSE */ }
        }
    }
}

public sealed class DirectoryFileObject : FileObject
{
    private readonly string _hostPath;

    public DirectoryFileObject(string hostPath, string ntPath)
    {
        _hostPath = hostPath;
        NtPath = ntPath;
        Name = ntPath;
        FullPath = ntPath;
        IsDirectory = true;
    }

    public string NtPath { get; }
    public string HostPath => _hostPath;
    public override string? Path => NtPath;
    public override long Position { get => 0; set { } }
    public override long Length => 0;
    public override bool CanRead => false;
    public override bool CanWrite => false;
    public override int Read(Span<byte> buffer) => throw new NotSupportedException("directory handle cannot be read");
    public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException("directory handle cannot be written");
    public override void Flush() { }
    public override string? Info => _hostPath;
}

public sealed class ConsoleFileObject : FileObject
{
    private readonly Stream _stream;
    private readonly bool _canRead;
    private readonly bool _canWrite;

    public ConsoleFileObject(string name, Stream stream, bool canRead, bool canWrite)
    {
        Name = name;
        FullPath = name;
        _stream = stream;
        _canRead = canRead;
        _canWrite = canWrite;
    }

    public override string? Path => FullPath;
    public override long Position { get; set; }
    public override long Length => 0;
    public override bool CanRead => _canRead;
    public override bool CanWrite => _canWrite;
    public override bool InputAvailable => _canRead && (_stream is IInputAvailability input
        ? input.InputAvailable : _stream.CanSeek ? _stream.Position < _stream.Length :
        !Console.IsInputRedirected && Console.KeyAvailable);

    // Several imagined processes can share the runtime's standard streams.  Serialize access so
    // concurrent child output cannot corrupt the underlying stream.
    public override int Read(Span<byte> buffer)
    {
        lock (_stream) return _stream.Read(buffer);
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _stream.ReadAsync(buffer, cancellationToken);

    public override int ReadConsoleByte()
    {
        lock (_stream)
            return _stream is ConsoleInputStream input ? input.ReadConsoleByte() : _stream.ReadByte();
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        lock (_stream) _stream.Write(buffer);
    }

    public override void Flush()
    {
        lock (_stream) _stream.Flush();
    }

    public override string? Info => FullPath;
}
