using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ImaginedNT.Core.Io;

public sealed unsafe class PipeFileObject : FileObject
{
    private readonly SafeFileHandle _handle;
    private readonly bool _read;

    private PipeFileObject(SafeFileHandle handle, bool read)
    {
        _handle = handle;
        _read = read;
        Name = FullPath = "\\Device\\NamedPipe\\Anonymous";
    }

    public static void Create(uint size, out PipeFileObject read, out PipeFileObject write)
    {
        // Only the guest handle-table entries inherit; host handles stay private.
        if (!CreatePipe(out var r, out var w, 0, size))
        {
            var error = Marshal.GetLastWin32Error();
            r?.Dispose();
            w?.Dispose();
            throw new Win32Exception(error);
        }
        read = new(r, true);
        write = new(w, false);
    }

    public override string? Path => FullPath;
    public override long Position { get => 0; set => throw new NotSupportedException("pipe is not seekable"); }
    public override long Length => 0;
    public override bool CanRead => _read;
    public override bool CanWrite => !_read;
    public override nint GetNativeHandle() => _handle.DangerousGetHandle();
    public override bool InputAvailable => _read &&
        PeekNamedPipe(_handle, null, 0, out _, out var available, out _) && available != 0;

    public uint Peek(Span<byte> buffer, out uint read, out uint left)
    {
        fixed (byte* p = buffer)
        {
            if (!PeekNamedPipe(_handle, p, (uint)buffer.Length, out read, out var available, out left))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return available;
        }
    }

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty) return 0;
        fixed (byte* p = buffer)
        {
            if (!ReadFile(_handle, p, (uint)buffer.Length, out var read, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return (int)read;
        }
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        fixed (byte* p = buffer)
        {
            var offset = 0;
            while (offset < buffer.Length)
            {
                if (!WriteFile(_handle, p + offset, (uint)(buffer.Length - offset), out var written, 0))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                if (written == 0) throw new Win32Exception(29);
                offset += (int)written;
            }
        }
    }

    public override void Flush() { }
    protected override void OnDispose() => _handle.Dispose();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, nint attributes, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PeekNamedPipe(SafeFileHandle pipe, byte* buffer, uint size, out uint read, out uint available, out uint left);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeFileHandle pipe, byte* buffer, uint size, out uint read, nint overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(SafeFileHandle pipe, byte* buffer, uint size, out uint written, nint overlapped);
}
