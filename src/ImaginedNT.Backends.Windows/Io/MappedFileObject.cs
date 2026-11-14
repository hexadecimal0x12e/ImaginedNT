using ImaginedNT.Core.Nt;

namespace ImaginedNT.Core.Io;

public sealed class MappedFileObject : FileObject
{
    private readonly FileStream? _read, _write;
    private readonly bool _discard;
    private readonly Action _delete;
    private long _position;
    public MappedFileObject(string path, FileStream? read, FileStream? write, bool discard, Action delete)
    { FullPath = Name = path; _read = read; _write = write; _discard = discard; _delete = delete; }
    public override string? Path => FullPath;
    public override long Position { get => _position; set => _position = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value)); }
    public string? HostPath => _read?.Name ?? _write?.Name;
    public override long Length => _read?.Length ?? _write?.Length ?? 0;
    public override bool CanRead => _read is not null;
    public override bool CanWrite => _write is not null || _discard;
    public override bool CanSeek => true;
    public override int Read(Span<byte> buffer)
    {
        if (_read is null) throw new NtFileException(NtStatus.AccessDenied, "handle is not readable");
        _read.Position = Position;
        var count = _read.Read(buffer); Position += count; return count;
    }
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (!CanWrite) throw new NtFileException(NtStatus.AccessDenied, "handle is not writable");
        if (!_discard) { _write!.Position = Position; _write.Write(buffer); }
        Position += buffer.Length;
    }
    public void SetLength(long length)
    {
        if (!CanWrite) throw new NtFileException(NtStatus.AccessDenied, "handle is not writable");
        if (!_discard) _write!.SetLength(length);
    }
    public override long Seek(long offset, SeekOrigin origin) => Position = checked((origin == SeekOrigin.Begin ? 0 : origin == SeekOrigin.Current ? Position : Length) + offset);
    public override void Flush() => _write?.Flush();
    public override nint GetNativeHandle()
    {
        if (_discard || (_read is not null && _write is not null && !ReferenceEquals(_read, _write))) return 0;
        Flush();
        return (_read ?? _write)?.SafeFileHandle.DangerousGetHandle() ?? 0;
    }
    protected override void OnDispose()
    {
        _read?.Dispose(); if (!ReferenceEquals(_read, _write)) _write?.Dispose();
        if (DeleteOnClose) _delete();
    }
}
