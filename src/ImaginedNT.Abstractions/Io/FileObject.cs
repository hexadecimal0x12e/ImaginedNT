using ImaginedNT.Core.Nt;
using ImaginedNT.Core.Objects;

namespace ImaginedNT.Core.Io;

public interface IInputAvailability
{
    bool InputAvailable { get; }
}

/// <summary>NT file object backed by a host file stream inside the process sandbox.</summary>
public abstract class FileObject : NtObject
{
    public abstract string? Path { get; }
    public abstract long Position { get; set; }
    public abstract long Length { get; }
    public bool IsDirectory { get; init; }
    public virtual bool CanRead => true;
    public virtual bool CanWrite => true;
    public virtual bool CanSeek => false;
    public virtual bool InputAvailable => false;
    public virtual bool DeleteOnClose { get; set; }

    public abstract int Read(Span<byte> buffer);
    public virtual int ReadConsoleByte()
    {
        Span<byte> buffer = stackalloc byte[1];
        return Read(buffer) == 0 ? -1 : buffer[0];
    }
    public virtual ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Read(buffer.Span));
    }
    public abstract void Write(ReadOnlySpan<byte> buffer);
    public abstract void Flush();

    public override ObjectType ObjectType => ObjectType.File;

    public virtual long Seek(long offset, SeekOrigin origin)
    {
        var stream = GetSeekableStream();
        var position = stream.Seek(offset, origin);
        Position = position;
        return position;
    }

    protected virtual Stream GetSeekableStream() => throw new NotSupportedException("file is not seekable");
}

