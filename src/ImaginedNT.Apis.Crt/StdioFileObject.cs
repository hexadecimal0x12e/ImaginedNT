using System.Runtime.InteropServices;
using ImaginedNT.Core.Io;
using Microsoft.Win32.SafeHandles;

namespace ImaginedNT.Apis.Crt;

// Win32 access to a CRT descriptor must share the actual OS file position, without FileStream read-ahead.
internal sealed class StdioFileObject(SafeFileHandle handle, int flags, string? path) : FileObject
{
    public override string? Path => path;
    // _open_osfhandle accepts _O_RDONLY (0), _O_WRONLY (1), _O_RDWR (2) and _O_APPEND (0x8).
    // With only _O_APPEND the descriptor is still writable even though the low two bits are 0.
    private int Access => flags & 3;
    public override bool CanRead => Access == 2 || (Access == 0 && (flags & 0x8) == 0);
    public override bool CanWrite => Access is 1 or 2 || (flags & 0x8) != 0;
    public override nint GetNativeHandle() => handle.DangerousGetHandle();
    public override long Position { get => Seek(0, SeekOrigin.Current); set => Seek(value, SeekOrigin.Begin); }
    public override long Length => GetFileSizeEx(handle, out var length) ? length : throw Failure();

    public override long Seek(long offset, SeekOrigin origin)
    {
        // Anonymous pipes and character devices have no file position.  Windows fails these
        // seeks; succeeding would make consumers (cmd.exe in particular) treat redirected
        // stdin as a seekable disk file.
        if (GetFileType(handle) is 2 or 3) throw new NotSupportedException("CRT handle is not seekable");
        return SetFilePointerEx(handle, offset, out var position, (uint)origin) ? position : throw Failure();
    }

    public override unsafe int Read(Span<byte> buffer)
    {
        fixed (byte* p = buffer)
        {
            if (ReadFile(handle, p, (uint)buffer.Length, out var read, 0)) return (int)read;
            if (Marshal.GetLastWin32Error() == 109) return 0;
            throw Failure();
        }
    }

    public override unsafe void Write(ReadOnlySpan<byte> buffer)
    {
        fixed (byte* p = buffer)
        {
            var written = 0;
            while (written < buffer.Length)
            {
                if (!WriteFile(handle, p + written, (uint)(buffer.Length - written), out var count, 0) || count == 0)
                    throw Failure();
                written += (int)count;
            }
        }
    }

    public override void Flush()
    {
        if (GetFileType(handle) == 1 && !FlushFileBuffers(handle)) throw Failure();
    }

    protected override void OnDispose() => handle.Dispose();
    private static IOException Failure() => new($"CRT file handle I/O failed: {Marshal.GetLastWin32Error()}");

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool ReadFile(SafeFileHandle file, byte* buffer, uint size, out uint read, nint overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool WriteFile(SafeFileHandle file, byte* buffer, uint size, out uint written, nint overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFilePointerEx(SafeFileHandle file, long offset, out long position, uint origin);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileSizeEx(SafeFileHandle file, out long size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushFileBuffers(SafeFileHandle file);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(SafeFileHandle file);
}
