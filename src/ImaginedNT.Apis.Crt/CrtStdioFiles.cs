using ImaginedNT.Core.Io;
using ImaginedNT.Core.Nt;
using ImaginedNT.Core.Runtime;

namespace ImaginedNT.Apis.Crt;

internal sealed partial class CrtStdioState
{
    private unsafe ulong OpenStream(ApiCallContext ctx, string name)
    {
        var secure = name.EndsWith("_s", StringComparison.Ordinal);
        var reopen = name.Contains("freopen", StringComparison.Ordinal);
        var width = name.StartsWith("_w", StringComparison.Ordinal) ? 2 : 1;
        var first = secure ? 1 : 0;
        if (secure && !CrtMemory.Write(ctx, ctx.Arg(0), 0UL)) return Error(ctx, name, 22);
        if (!CrtMemory.ReadText(ctx, ctx.Arg(first), width, out var path, ansi: true) ||
            !CrtMemory.ReadText(ctx, ctx.Arg(first + 1), width, out var mode, ansi: true) || mode.Length == 0)
            return Error(ctx, name, 22);
        if (!ctx.Process.FileSystem.AllowWrites && (mode[0] != 'r' || mode.Contains('+') || mode.Contains('D')))
            return Error(ctx, name, 13);
        var old = reopen ? ctx.Arg(first + 2) : 0;
        if (reopen && !_streams.ContainsKey(old)) return Error(ctx, name, 9);
        if (!reopen && StreamCount >= _maxStdio) return Error(ctx, name, 24);
        var status = ctx.Process.FileSystem.TryMapNativePath(path, ctx.Process.CurrentDirectory, mode[0] == 'r' || mode.Contains('+'), mode[0] != 'r' || mode.Contains('+'), out var hostPath);
        if (status != NtStatus.Success) return Error(ctx, name, FileErrno(status));
        if (!mode.Contains('b') && !mode.Contains('t') &&
            CrtMemory.Read(ctx, CrtProcessState.GetFileModes(ctx), out int defaultMode) && defaultMode == 0x8000)
            mode += "b";
        var previousFd = reopen ? _streams[old] : -1;
        ulong stream;
        if (HostFileSystem.UseAppContainerApis)
        {
            if (reopen) Native.Call("fclose", [old]);
            stream = OpenAppStream(ctx, hostPath, mode, name is "_fsopen" or "_wfsopen" ? ctx.ArgInt32(2) : 0x40);
        }
        else
        {
            fixed (char* p = hostPath, m = mode)
                stream = reopen ? Native.Call("_wfreopen", [(ulong)p, (ulong)m, old]) :
                    name is "_fsopen" or "_wfsopen" ? Native.Call("_wfsopen", [(ulong)p, (ulong)m, ctx.Arg(2)]) :
                    Native.Call("_wfopen", [(ulong)p, (ulong)m]);
        }
        if (reopen)
        {
            if (_streams.TryRemove(old, out var previous)) _descriptors.TryRemove(previous, out _);
            CloseOsHandle(previousFd);
            _temporary.TryRemove(old, out _);
        }
        if (stream != 0)
        {
            var host = (int)Native.Call("_fileno", [stream]);
            var flags = mode.Contains('+') ? 2 : mode[0] == 'r' ? 0 : 1;
            var fd = reopen ? previousFd : AddDescriptor(host, flags, path);
            if (reopen) _descriptors[fd] = new(host, flags, path);
            _streams[stream] = fd;
            if (reopen)
                for (var i = 0; i < 3; i++)
                    if (_standard[i] == old) _standard[i] = stream;
        }
        if (!secure) return stream;
        if (!CrtMemory.Write(ctx, ctx.Arg(0), stream))
        {
            if (stream != 0) CloseStream(ctx, stream, false);
            return Error(ctx, name, 22);
        }
        return stream == 0 ? (ulong)GetErrno() : 0;
    }

    private unsafe ulong OpenFile(ApiCallContext ctx, string name)
    {
        var secure = name.EndsWith("_s", StringComparison.Ordinal);
        var dispatch = name.EndsWith("_dispatch", StringComparison.Ordinal);
        var create = name is "_creat" or "_wcreat";
        var shared = name.Contains("sopen", StringComparison.Ordinal);
        var first = secure ? 1 : 0;
        var output = secure ? ctx.Arg(0) : dispatch ? ctx.Arg(4) : 0;
        if ((secure || dispatch) && !CrtMemory.Write(ctx, output, -1)) return Error(ctx, "_sopen_s", 22);
        if (!CrtMemory.ReadText(ctx, ctx.Arg(first), name.StartsWith("_w", StringComparison.Ordinal) ? 2 : 1,
            out var path, ansi: true)) return Error(ctx, name, 22);
        var flags = create ? 0x301 : ctx.ArgInt32(first + 1);
        if (!ctx.Process.FileSystem.AllowWrites && ((flags & 3) != 0 || (flags & 0x740) != 0))
            return Error(ctx, name, 13);
        if ((flags & 0x74000) == 0 && CrtMemory.Read(ctx, CrtProcessState.GetFileModes(ctx), out int defaultMode))
            flags |= defaultMode;
        var sharing = shared ? ctx.ArgInt32(first + 2) : 0x40;
        var permission = ctx.ArgInt32(first + (create ? 1 : shared ? 3 : 2));
        var status = ctx.Process.FileSystem.TryMapNativePath(path, ctx.Process.CurrentDirectory, (flags & 3) != 1, (flags & 3) != 0 || (flags & 0x740) != 0, out var hostPath);
        if (status != NtStatus.Success) return Error(ctx, name, FileErrno(status));
        var host = -1;
        int result;
        if (HostFileSystem.UseAppContainerApis)
        {
            host = OpenAppDescriptor(ctx, hostPath, flags, sharing);
            result = host < 0 ? GetErrno() : 0;
        }
        else
        {
            fixed (char* p = hostPath)
                result = (int)Native.Call("_wsopen_dispatch", [(ulong)p, unchecked((uint)flags),
                    unchecked((uint)sharing), unchecked((uint)permission), (ulong)&host,
                    secure ? 1UL : dispatch ? ctx.Arg(5) : 0]);
        }
        if (result != 0) return secure || dispatch ? unchecked((uint)result) : uint.MaxValue;
        var fd = AddDescriptor(host, flags, path);
        if (!secure && !dispatch) return (ulong)fd;
        if (CrtMemory.Write(ctx, output, fd)) return 0;
        CloseDescriptor(ctx, fd);
        return Error(ctx, "_sopen_s", 22);
    }

    private ulong CurrentDirectory(ApiCallContext ctx, string name)
    {
        var drive = name == "_getdcwd";
        if (drive && ctx.ArgInt32(0) is not (0 or 3)) return Error(ctx, name, 13);
        var dest = ctx.Arg(drive ? 1 : 0);
        var capacity = ctx.ArgInt32(drive ? 2 : 1);
        if (capacity < 0 || dest != 0 && capacity == 0) return Error(ctx, name, 22);
        var path = ctx.Process.CurrentDirectory;
        if (!path.StartsWith("C:", StringComparison.OrdinalIgnoreCase)) path = "C:" + path;
        var bytes = CrtMemory.Encode(path, 1, ansi: true);
        if (capacity > 0 && capacity < bytes.Length) return Error(ctx, name, 34);
        if (dest == 0) dest = ctx.Process.Heap.Allocate((ulong)Math.Max(capacity, bytes.Length), false, ctx.Thread.Id);
        return dest != 0 && ctx.Memory.TryWrite(dest, bytes) ? dest : Error(ctx, name, 12);
    }

    private unsafe ulong Temporary(ApiCallContext ctx, string name)
    {
        var width = name.StartsWith("_w", StringComparison.Ordinal) ? 2 : 1;
        var secure = name.EndsWith("_s", StringComparison.Ordinal);
        var make = name.Contains("mktemp", StringComparison.Ordinal);
        var allocated = name.Contains("tempnam", StringComparison.Ordinal);
        var file = name is "tmpfile" or "tmpfile_s";
        if (file && secure && !CrtMemory.Write(ctx, ctx.Arg(0), 0UL)) return Error(ctx, name, 22);
        var directory = "C:\\Temp";
        var prefix = "int";
        if (ctx.Process.TryGetEnvironmentVariable("TEMP", out var temp) && !string.IsNullOrWhiteSpace(temp)) directory = temp;
        if (allocated)
        {
            if (ctx.Arg(0) != 0 && !CrtMemory.ReadText(ctx, ctx.Arg(0), width, out directory, ansi: true))
                return Error(ctx, name, 22);
            if (ctx.Arg(1) != 0 && !CrtMemory.ReadText(ctx, ctx.Arg(1), width, out prefix, ansi: true))
                return Error(ctx, name, 22);
        }
        var template = "";
        if (make && (!CrtMemory.ReadText(ctx, ctx.Arg(0), width, out template, ansi: true,
            maxLength: secure ? Math.Max(0, ctx.ArgInt32(1) - 1) : 32766) ||
            !template.EndsWith("XXXXXX", StringComparison.Ordinal)))
        {
            if (ctx.Arg(0) != 0) CrtMemory.WriteChar(ctx, ctx.Arg(0), 0, width);
            return Error(ctx, name, 22);
        }
        string path;
        do
        {
            path = make ? template[..^6] + Guid.NewGuid().ToString("N")[..6] :
                directory.TrimEnd('\\', '/') + "\\" + prefix + Guid.NewGuid().ToString("N")[..12] + ".tmp";
        } while (ctx.Process.FileSystem.Exists(path, ctx.Process.CurrentDirectory));
        var status = ctx.Process.FileSystem.TryMapNativePath(path, ctx.Process.CurrentDirectory, true, true, out var hostPath);
        if (status != NtStatus.Success) return Error(ctx, name, FileErrno(status));
        if (file)
        {
            if (!ctx.Process.FileSystem.AllowWrites) return Error(ctx, name, 13);
            if (StreamCount >= _maxStdio) return Error(ctx, name, 24);
            HostFileSystem.CreateDirectory(Path.GetDirectoryName(hostPath)!);
            ulong stream;
            if (HostFileSystem.UseAppContainerApis) stream = OpenAppStream(ctx, hostPath, "w+bTD", 0x40);
            else
            {
                fixed (char* p = hostPath, m = "w+bTD") stream = Native.Call("_wfopen", [(ulong)p, (ulong)m]);
            }
            if (stream == 0) return secure ? (ulong)GetErrno() : 0;
            var fd = AddDescriptor((int)Native.Call("_fileno", [stream]), 2, path);
            _streams[stream] = fd;
            _temporary[stream] = 0;
            if (!secure) return stream;
            if (CrtMemory.Write(ctx, ctx.Arg(0), stream)) return 0;
            CloseStream(ctx, stream, false);
            return Error(ctx, name, 22);
        }
        var bytes = CrtMemory.Encode(path, width, ansi: true);
        if (!allocated && !make && bytes.Length / width > 260) return Error(ctx, name, 34);
        var dest = ctx.Arg(0);
        if (allocated)
        {
            dest = ctx.Process.Heap.Allocate((ulong)bytes.Length, false, ctx.Thread.Id);
            if (dest == 0) return Error(ctx, name, 12);
        }
        else if (secure && ctx.Arg(1) < (ulong)(bytes.Length / width))
        {
            if (dest != 0 && ctx.Arg(1) != 0) CrtMemory.WriteChar(ctx, dest, 0, width);
            return Error(ctx, name, 34);
        }
        else if (dest == 0 && !secure && !make)
            return CrtProcessState.GetString(ctx, $"stdio tmpnam {ctx.Thread.Id}", path, width, out var address) ? address : 0;
        if (dest == 0 || !ctx.Memory.TryWrite(dest, bytes))
        {
            if (allocated && dest != 0) ctx.Process.Heap.Free(dest, ctx.Thread.Id);
            return Error(ctx, name, 22);
        }
        return secure ? 0 : dest;
    }

    private ulong GetLine(ApiCallContext ctx, string name)
    {
        var width = name.StartsWith("_getws", StringComparison.Ordinal) ? 2 : 1;
        var secure = name.EndsWith("_s", StringComparison.Ordinal);
        var capacity = secure ? ctx.Arg(1) : int.MaxValue;
        if (ctx.Arg(0) == 0 || capacity is 0 or > int.MaxValue) return Error(ctx, name, 22);
        var stream = StandardStream(ctx, 0);
        if (stream == 0) return Error(ctx, name, 9);
        using var input = BeginInput(ctx, 0);
        var result = Native.Call(width == 1 ? "fgets" : "fgetws", [ctx.Arg(0), capacity, stream]);
        if (result == 0) return 0;
        if (!CrtMemory.ReadText(ctx, result, width, out var line, maxLength: (int)capacity))
            return Error(ctx, name, 22);
        if (line.EndsWith('\n'))
        {
            if (!CrtMemory.WriteChar(ctx, result + (ulong)(line.Length - 1) * (ulong)width, 0, width))
                return Error(ctx, name, 22);
        }
        else if (secure && line.Length == (int)capacity - 1)
        {
            var next = (int)Native.Call(width == 1 ? "fgetc" : "fgetwc", [stream]);
            if (next != 10 && next != -1 && next != 0xFFFF)
            {
                do { next = (int)Native.Call(width == 1 ? "fgetc" : "fgetwc", [stream]); }
                while (next != 10 && next != -1 && next != 0xFFFF);
                CrtMemory.WriteChar(ctx, result, 0, width);
                ctx.Runtime.Trace.Warn($"{ctx.Name}: input exceeds buffer capacity", ctx.Process.Id, ctx.Thread.Id);
                return Error(ctx, name, 34);
            }
        }
        return result;
    }

    private static ulong OpenAppStream(ApiCallContext ctx, string path, string mode, int sharing)
    {
        if (mode[0] is not ('r' or 'w' or 'a'))
        {
            CrtLocaleData.SetErrno(22);
            return 0;
        }
        var flags = mode.Contains('+') ? 2 : mode[0] == 'r' ? 0 : 1;
        flags |= mode.Contains('b') ? 0x8000 : 0x4000;
        if (mode[0] == 'w') flags |= 0x300;
        if (mode[0] == 'a') flags |= 0x108;
        if (mode.Contains('x')) flags |= 0x400;
        if (mode.Contains('D')) flags |= 0x40;
        var fd = OpenAppDescriptor(ctx, path, flags, sharing);
        if (fd < 0) return 0;
        var stream = OpenDescriptorStream(fd, mode);
        if (stream == 0) Native.Call("_close", [(ulong)fd]);
        return stream;
    }

    private static int OpenAppDescriptor(ApiCallContext ctx, string path, int flags, int sharing)
    {
        try
        {
            var access = (flags & 3) switch
            {
                0 => FileAccess.Read,
                1 => FileAccess.Write,
                2 => FileAccess.ReadWrite,
                _ => throw new ArgumentException("Invalid file access mode.")
            };
            var mode = FileMode.Open;
            if ((flags & 0x100) != 0)
            {
                if ((flags & 0x400) != 0) mode = FileMode.CreateNew;
                else mode = (flags & 0x200) != 0 ? FileMode.Create : FileMode.OpenOrCreate;
            }
            else if ((flags & 0x200) != 0) mode = FileMode.Truncate;
            var share = sharing switch
            {
                0x10 => FileShare.None,
                0x20 => FileShare.Read,
                0x30 => FileShare.Write,
                0x40 => FileShare.ReadWrite,
                0x80 => access == FileAccess.Read ? FileShare.Read : FileShare.None,
                _ => throw new ArgumentException("Invalid file sharing mode.")
            };
            using var handle = HostFileSystem.OpenHandle(path, mode, access, share,
                (flags & 0x40) != 0 ? FileOptions.DeleteOnClose : FileOptions.None);
            var fd = (int)Native.Call("_open_osfhandle", [(ulong)handle.DangerousGetHandle(), unchecked((uint)(flags & ~0x740))]);
            if (fd >= 0) handle.SetHandleAsInvalid();
            return fd;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ctx.Runtime.Trace.Warn($"CRT open '{path}' failed: {ex}", ctx.Process.Id, ctx.Thread.Id);
            CrtLocaleData.SetErrno(ex is FileNotFoundException or DirectoryNotFoundException ? 2 :
                ex is UnauthorizedAccessException || (ex.HResult & 0xFFFF) == 32 ? 13 :
                (ex.HResult & 0xFFFF) is 80 or 183 ? 17 : 22);
            return -1;
        }
    }

    private static unsafe int GetErrno()
    {
        var value = 0;
        Native.Call("_get_errno", [(ulong)&value]);
        return value == 0 ? 22 : value;
    }
}
