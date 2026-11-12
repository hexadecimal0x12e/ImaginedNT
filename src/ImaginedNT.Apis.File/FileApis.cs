using static ImaginedNT.Apis.ApiHelpers;
using System.IO.Enumeration;
using ImaginedNT.Core.Io;
using ImaginedNT.Core.Memory;
using ImaginedNT.Core.Nt;
using ImaginedNT.Core.Objects;

namespace ImaginedNT.Core.Runtime;

public static partial class FileApis
{
    private const uint GENERIC_READ = 0x8000_0000;
    private const uint GENERIC_WRITE = 0x4000_0000;
    private const uint FILE_APPEND_DATA = 0x0004;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x0200_0000;
    private const uint FILE_ATTRIBUTE_READONLY = 0x0000_0001;
    private const uint FILE_ATTRIBUTE_HIDDEN = 0x0000_0002;
    private const uint FILE_ATTRIBUTE_SYSTEM = 0x0000_0004;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x0000_0010;
    private const uint FILE_ATTRIBUTE_ARCHIVE = 0x0000_0020;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x0000_0080;
    private const int Win32FindDataSize = 592;      // WIN32_FIND_DATAW on x64
    private const int Win32FindDataFileNameOffset = 44;
    private const int MaxPathChars = 260;

    private static CreateDisposition Win32DispositionToNt(uint disposition) => disposition switch
    {
        1 => CreateDisposition.Create,        // CREATE_NEW
        2 => CreateDisposition.OverwriteIf,   // CREATE_ALWAYS
        3 => CreateDisposition.Open,          // OPEN_EXISTING
        4 => CreateDisposition.OpenIf,        // OPEN_ALWAYS
        5 => CreateDisposition.Overwrite,     // TRUNCATE_EXISTING
        _ => CreateDisposition.Open
    };

    private static uint Win32ShareToNt(uint share) => share & 0x7;

    private static ulong OpenManagedFile(ApiCallContext ctx, string path, uint desiredAccess, uint share,
        CreateDisposition disposition, CreateOptions options)
    {
        var file = ctx.Process.FileSystem.CreateOrOpen(path, desiredAccess, (ShareAccess)Win32ShareToNt(share),
            disposition, options, ctx.Process.CurrentDirectory);
        var handle = ctx.Handles.Create(file, desiredAccess == 0 ? 0x0012_019F : desiredAccess);
        file.Release();
        return handle;
    }

    // --- standard handles -----------------------------------------------------------
    [VirtualApi("kernel32.dll", "GetStdHandle", ApiCategory.Console)]
    private static ulong GetStdHandle(ApiCallContext ctx)
    {
        var kind = ctx.ArgInt32(0);
        return kind switch
        {
            -10 => 0,
            -11 => 1,
            -12 => 2,
            _ => InvalidHandleValue
        };
    }

    [VirtualApi("kernel32.dll", "SetStdHandle", ApiCategory.Console)]
    private static ulong SetStdHandle(ApiCallContext ctx)
    {
        var kind = ctx.ArgInt32(0);
        var handle = ctx.Arg(1);
        var slot = kind switch { -10 => 0UL, -11 => 1UL, -12 => 2UL, _ => ulong.MaxValue };
        if (slot == ulong.MaxValue) return BoolResult(ctx, false, Win32Error.InvalidParameter);
        if (handle == InvalidHandleValue)
        {
            ctx.Handles.Close(slot);
            return 1;
        }
        if (!ctx.Handles.TryGet(handle, out var obj) || obj is null)
            return BoolResult(ctx, false, Win32Error.InvalidHandle);
        ctx.Handles.Seed(slot, obj, 0x0012_019F);
        return 1;
    }

    [VirtualApi("kernel32.dll", "GetFileType", ApiCategory.File)]
    private static ulong GetFileType(ApiCallContext ctx)
    {
        if (!ctx.TryGetFile(ctx.Arg(0), out var file) || file is null) return 0;
        // Prefer the real host file type.  A CRT stdio descriptor backed by an anonymous pipe
        // must report FILE_TYPE_PIPE: cmd.exe uses GetFileType to choose its input loop, and a
        // wrong FILE_TYPE_DISK makes it seek a pipe (and drop all but the first command).
        nint native = 0;
        try { native = file.GetNativeHandle(); }
        catch { /* Fall through to the managed shape below. */ }
        var hostType = native != 0 ? HostGetFileType(native) : 0;
        if (ctx.Runtime.Options.TraceNativeCalls)
            ctx.Runtime.Trace.Emit(ImaginedNT.Core.Diagnostics.TraceKind.File,
                $"GetFileType(0x{ctx.Arg(0):X}) {file.GetType().Name} native=0x{native:X} host={hostType}",
                ctx.Process.Id, ctx.Thread.Id);
        if (native != 0) return hostType;
        return file is ConsoleFileObject ? 2UL : file.IsDirectory ? 0UL : 1UL;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "GetFileType")]
    private static extern uint HostGetFileType(nint file);

    [VirtualApi("kernel32.dll", "GetConsoleMode", ApiCategory.Console)]
    private static ulong GetConsoleMode(ApiCallContext ctx)
    {
        if (!ctx.TryGetFile(ctx.Arg(0), out var file) || file is not ConsoleFileObject)
            return BoolResult(ctx, false, Win32Error.InvalidHandle);
        if (ctx.Arg(1) == 0) return BoolResult(ctx, false, Win32Error.InvalidParameter);
        ctx.Memory.Write(ctx.Arg(1), file.CanRead ? 0x1F7u : 0x3u);
        return 1;
    }

    [VirtualApi("kernel32.dll", "WriteConsoleW", ApiCategory.Console)]
    private static ulong WriteConsoleW(ApiCallContext ctx)
    {
        if (ctx.Arg(3) != 0) ctx.Write(ctx.Arg(3), 0u);
        if (!ctx.TryGetFile(ctx.Arg(0), out var file) || file is not ConsoleFileObject || !file.CanWrite)
            return BoolResult(ctx, false, Win32Error.InvalidHandle);
        var count = ctx.Arg32(2);
        if (count > int.MaxValue / 2 || (count != 0 && ctx.Arg(1) == 0))
            return BoolResult(ctx, false, Win32Error.InvalidParameter);
        var bytes = System.Text.Encoding.UTF8.GetBytes(
            System.Text.Encoding.Unicode.GetString(ctx.ReadBytes(ctx.Arg(1), (int)count * 2)));
        try
        {
            file.Write(bytes);
            file.Flush();
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException)
        {
            ctx.Runtime.Trace.Warn($"WriteConsoleW failed: {ex.Message}", ctx.Process.Id, ctx.Thread.Id);
            return BoolResult(ctx, false, Win32Error.InvalidHandle);
        }
        if (ctx.Arg(3) != 0) ctx.Write(ctx.Arg(3), count);
        return 1;
    }

    // --- win32 file API -------------------------------------------------------------
    [VirtualApi("kernel32.dll", "CreateFileW", ApiCategory.File)]
    private static ulong CreateFileW(ApiCallContext ctx)
    {
        var path = ReadUnicodeArg(ctx, 0);
        if (string.IsNullOrEmpty(path)) return FailInvalidHandle(ctx, Win32Error.PathNotFound);
        try
        {
            var access = ctx.Arg32(1);
            var share = ctx.Arg32(2);
            var disposition = Win32DispositionToNt(ctx.Arg32(4));
            var flags = ctx.Arg32(5);
            var options = CreateOptions.None;
            if ((flags & FILE_FLAG_BACKUP_SEMANTICS) != 0) options |= CreateOptions.DirectoryFile;
            var handle = OpenManagedFile(ctx, path, access, share, disposition, options);
            return handle;
        }
        catch (NtFileException ex)
        {
            ctx.SetLastError(Win32FromStatus(ex.Status));
            return InvalidHandleValue;
        }
    }

    [VirtualApi("kernel32.dll", "CreateFileA", ApiCategory.File)]
    private static ulong CreateFileA(ApiCallContext ctx)
    {
        var path = ctx.Arg(0) == 0 ? null : ctx.ReadAnsiString(ctx.Arg(0));
        if (string.IsNullOrEmpty(path)) return FailInvalidHandle(ctx, Win32Error.PathNotFound);
        try
        {
            var flags = ctx.Arg32(5);
            var options = (flags & FILE_FLAG_BACKUP_SEMANTICS) != 0 ? CreateOptions.DirectoryFile : CreateOptions.None;
            return OpenManagedFile(ctx, path, ctx.Arg32(1), ctx.Arg32(2), Win32DispositionToNt(ctx.Arg32(4)), options);
        }
        catch (NtFileException ex)
        {
            ctx.SetLastError(Win32FromStatus(ex.Status));
            return InvalidHandleValue;
        }
    }

    [VirtualApi("kernel32.dll", "ReadFile", ApiCategory.File)]
    private static ulong ReadFile(ApiCallContext ctx)
    {
        if (ctx.Arg(3) != 0) ctx.Write(ctx.Arg(3), 0u);
        if (!ctx.TryGetFile(ctx.Arg(0), out var file) || file is null || file.IsDirectory)
            return BoolResult(ctx, false, Win32Error.InvalidHandle);
        if (!file.CanRead) return BoolResult(ctx, false, Win32Error.AccessDenied);
        var length = (int)Math.Min(ctx.Arg32(2), 1 << 30);
        var buffer = new byte[length];
        int read;
        try { read = file.Read(buffer); }
        catch (System.ComponentModel.Win32Exception ex)
        {
            if (ex.NativeErrorCode != Win32Error.BrokenPipe)
                ctx.Runtime.Trace.Warn($"ReadFile failed: {ex}", ctx.Process.Id, ctx.Thread.Id);
            return BoolResult(ctx, false, ex.NativeErrorCode);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return BoolResult(ctx, false, Win32Error.InvalidHandle);
        }
        if (read > 0) ctx.Memory.WriteBytes(ctx.Arg(1), buffer.AsSpan(0, read));
        if (ctx.Arg(3) != 0) ctx.Memory.Write(ctx.Arg(3), (uint)read);
        if (ctx.Runtime.Options.TraceNativeCalls)
            ctx.Runtime.Trace.Emit(ImaginedNT.Core.Diagnostics.TraceKind.File,
                $"ReadFile(0x{ctx.Arg(0):X}) read {read} byte(s)" +
                (read is > 0 and <= 64 ? $": {Convert.ToHexString(buffer, 0, read)}" : string.Empty),
                ctx.Process.Id, ctx.Thread.Id);
        return 1;
    }

    [VirtualApi("kernel32.dll", "WriteFile", ApiCategory.Console)]
    private static ulong WriteFile(ApiCallContext ctx)
    {
        if (ctx.Arg(3) != 0) ctx.Write(ctx.Arg(3), 0u);
        if (!ctx.TryGetFile(ctx.Arg(0), out var file) || file is null || file.IsDirectory)
            return BoolResult(ctx, false, Win32Error.InvalidHandle);
        if (!file.CanWrite) return BoolResult(ctx, false, Win32Error.AccessDenied);
        var length = (int)Math.Min(ctx.Arg32(2), 1 << 30);
        var bytes = ctx.Memory.ReadBytes(ctx.Arg(1), length);
        try
        {
            file.Write(bytes);
            if (file is ConsoleFileObject) file.Flush();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            if (ex.NativeErrorCode != Win32Error.BrokenPipe)
                ctx.Runtime.Trace.Warn($"WriteFile failed: {ex}", ctx.Process.Id, ctx.Thread.Id);
            return BoolResult(ctx, false, ex.NativeErrorCode);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return BoolResult(ctx, false, Win32Error.InvalidHandle);
        }
        if (ctx.Arg(3) != 0) ctx.Memory.Write(ctx.Arg(3), (uint)length);
        return 1;
    }

    [VirtualApi("kernel32.dll", "FlushFileBuffers", ApiCategory.File)]
    private static ulong FlushFileBuffers(ApiCallContext ctx)
    {
        if (!ctx.TryGetFile(ctx.Arg(0), out var file) || file is null) return BoolResult(ctx, false, Win32Error.InvalidHandle);
        file.Flush();
        return 1;
    }

    [VirtualApi("kernel32.dll", "SetFilePointer", ApiCategory.File)]
    private static ulong SetFilePointer(ApiCallContext ctx)
    {
        if (!ctx.TryGetFile(ctx.Arg(0), out var file) || file is null)
            return FailInvalidHandle(ctx, Win32Error.InvalidHandle);
        var low = ctx.Arg32(1);
        var high = ctx.Arg(2) == 0 ? 0u : ctx.Memory.Read<uint>(ctx.Arg(2));
        var distance = (long)(((ulong)high << 32) | low);
        var method = ctx.Arg32(3);
        if (!TrySeek(file, distance, method, out var position))
            return FailInvalidHandle(ctx, Win32Error.InvalidParameter);
        if (ctx.Arg(2) != 0) ctx.Memory.Write(ctx.Arg(2), (uint)(position >> 32));
        return (ulong)(uint)position;
    }

    [VirtualApi("kernel32.dll", "SetFilePointerEx", ApiCategory.File)]
    private static ulong SetFilePointerEx(ApiCallContext ctx)
    {
        if (!ctx.TryGetFile(ctx.Arg(0), out var file) || file is null)
            return BoolResult(ctx, false, Win32Error.InvalidHandle);
        var distance = unchecked((long)ctx.Arg(1));
        if (!TrySeek(file, distance, ctx.Arg32(3), out var position))
            return BoolResult(ctx, false, Win32Error.InvalidParameter);
        if (ctx.Arg(2) != 0) ctx.Memory.Write(ctx.Arg(2), position);
        return 1;
    }

    [VirtualApi("kernel32.dll", "GetFileSize", ApiCategory.File)]
    private static ulong GetFileSize(ApiCallContext ctx)
    {
        if (!ctx.TryGetFile(ctx.Arg(0), out var file) || file is null)
            return FailInvalidHandle(ctx, Win32Error.InvalidHandle);
        var size = (ulong)file.Length;
        if (ctx.Arg(1) != 0) ctx.Memory.Write(ctx.Arg(1), (uint)(size >> 32));
        return (ulong)(uint)size;
    }

    [VirtualApi("kernel32.dll", "GetFileSizeEx", ApiCategory.File)]
    private static ulong GetFileSizeEx(ApiCallContext ctx)
    {
        if (!ctx.TryGetFile(ctx.Arg(0), out var file) || file is null)
            return BoolResult(ctx, false, Win32Error.InvalidHandle);
        if (ctx.Arg(1) != 0) ctx.Memory.Write(ctx.Arg(1), file.Length);
        return 1;
    }

    [VirtualApi("kernel32.dll", "CopyFileW", ApiCategory.File)]
    [VirtualApi("kernel32.dll", "CopyFileExW", ApiCategory.File)]
    private static ulong CopyFile(ApiCallContext ctx)
    {
        var from = ReadUnicodeArg(ctx, 0);
        var to = ReadUnicodeArg(ctx, 1);
        if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to))
            return BoolResult(ctx, false, Win32Error.InvalidParameter);
        try
        {
            var hostFrom = ctx.Process.FileSystem.MapPath(from, ctx.Process.CurrentDirectory);
            var hostTo = ctx.Process.FileSystem.MapPath(to, ctx.Process.CurrentDirectory, FileOperation.Write);
            // CopyFileW's third argument / CopyFileExW's dwCopyFlags: fail if the target exists.
            var failIfExists = (ctx.Name == "CopyFileExW" ? ctx.Arg32(5) : ctx.Arg32(2)) & 1;
            if (!HostFileSystem.FileExists(hostFrom))
                return BoolResult(ctx, false, HostFileSystem.DirectoryExists(hostFrom) ? Win32Error.AccessDenied : Win32Error.FileNotFound);
            if (failIfExists != 0 && HostFileSystem.FileExists(hostTo))
                return BoolResult(ctx, false, Win32Error.AlreadyExists);
            using var source = ctx.Process.FileSystem.CreateOrOpen(from, NtAccess.GenericRead, ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete, CreateDisposition.Open, 0, ctx.Process.CurrentDirectory);
            using var target = ctx.Process.FileSystem.CreateOrOpen(to, NtAccess.GenericWrite, ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete, failIfExists != 0 ? CreateDisposition.Create : CreateDisposition.OverwriteIf, 0, ctx.Process.CurrentDirectory);
            var copyBuffer = new byte[65536];
            int count;
            while ((count = source.Read(copyBuffer)) != 0) target.Write(copyBuffer.AsSpan(0, count));
            return 1;
        }
        catch (Exception ex) when (ex is NtFileException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return BoolResult(ctx, false, FileError(ex));
        }
    }

    [VirtualApi("kernel32.dll", "GetFileAttributesW", ApiCategory.File)]
    private static ulong GetFileAttributesW(ApiCallContext ctx)
    {
        var path = ReadUnicodeArg(ctx, 0);
        if (path is null) return 0xFFFF_FFFFUL;
        try
        {
            if (ctx.Runtime.Options.TraceNativeCalls)
                ctx.Runtime.Trace.Emit(ImaginedNT.Core.Diagnostics.TraceKind.File,
                    $"GetFileAttributesW(0x{ctx.Arg(0):X} '{path}') cwd='{ctx.Process.CurrentDirectory}' caller=0x{ctx.ReturnAddress:X16}",
                    ctx.Process.Id, ctx.Thread.Id);
            var host = ctx.Process.FileSystem.MapMetadataPath(path, ctx.Process.CurrentDirectory);
            if (HostFileSystem.DirectoryExists(host)) return FILE_ATTRIBUTE_DIRECTORY;
            if (HostFileSystem.FileExists(host)) return 0x80;
            ctx.SetLastError(Win32Error.FileNotFound);
            return 0xFFFF_FFFFUL;
        }
        catch (NtFileException ex)
        {
            ctx.SetLastError(Win32FromStatus(ex.Status));
            return 0xFFFF_FFFFUL;
        }
    }

    [VirtualApi("kernel32.dll", "GetFileAttributesExW", ApiCategory.File)]
    private static ulong GetFileAttributesExW(ApiCallContext ctx)
    {
        var path = ReadUnicodeArg(ctx, 0);
        if (string.IsNullOrEmpty(path) || ctx.Arg32(1) != 0 || ctx.Arg(2) == 0)
            return BoolResult(ctx, false, Win32Error.InvalidParameter);
        try
        {
            var host = ctx.Process.FileSystem.MapMetadataPath(path, ctx.Process.CurrentDirectory);
            var attributes = HostFileSystem.GetAttributes(host);
            var directory = (attributes & FileAttributes.Directory) != 0;
            var info = HostFileSystem.GetInfo(host);
            var size = directory ? 0UL : (ulong)info.Length;
            var buffer = ctx.Arg(2);
            ctx.Memory.Write(buffer, (uint)attributes);
            ctx.Memory.Write(buffer + 4, ToFileTime(info.CreationTimeUtc));
            ctx.Memory.Write(buffer + 12, ToFileTime(info.LastAccessTimeUtc));
            ctx.Memory.Write(buffer + 20, ToFileTime(info.LastWriteTimeUtc));
            ctx.Memory.Write(buffer + 28, (uint)(size >> 32));
            ctx.Memory.Write(buffer + 32, (uint)size);
            return 1;
        }
        catch (NtFileException ex) { return BoolResult(ctx, false, Win32FromStatus(ex.Status)); }
        catch (Exception ex) when (ex is NtFileException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            ctx.Runtime.Trace.Warn($"GetFileAttributesExW('{path}') failed: {ex.GetType().Name}: {ex.Message}", ctx.Process.Id, ctx.Thread.Id);
            return BoolResult(ctx, false, FileError(ex));
        }
    }

    private static int FileError(Exception ex) => ex switch
    {
        NtFileException nt => Win32FromStatus(nt.Status),
        FileNotFoundException => Win32Error.FileNotFound,
        DirectoryNotFoundException => Win32Error.PathNotFound,
        UnauthorizedAccessException => Win32Error.AccessDenied,
        PathTooLongException => Win32Error.FilenameExcedRange,
        ArgumentException or NotSupportedException => Win32Error.InvalidParameter,
        _ => ex.HResult & 0xFFFF
    };

    // --- directory enumeration -------------------------------------------------------
    [VirtualApi("kernel32.dll", "FindFirstFileW", ApiCategory.File)]
    private static ulong FindFirstFileW(ApiCallContext ctx) => FindFirstFile(ctx, ctx.Arg(1));

    [VirtualApi("kernel32.dll", "FindFirstFileExW", ApiCategory.File)]
    private static ulong FindFirstFileExW(ApiCallContext ctx)
    {
        if (ctx.Arg32(1) > 1 || ctx.Arg32(3) > 2 || ctx.Arg(4) != 0 || (ctx.Arg32(5) & ~7u) != 0)
            return FailInvalidHandle(ctx, Win32Error.InvalidParameter);
        if (ctx.Arg32(3) == 2) return FailInvalidHandle(ctx, Win32Error.NotSupported);
        var pattern = ReadUnicodeArg(ctx, 0);
        if (pattern is not null && (pattern.EndsWith('\\') || pattern.EndsWith('/')))
            return FailInvalidHandle(ctx, Win32Error.FileNotFound);
        return FindFirstFile(ctx, ctx.Arg(2), ctx.Arg32(3) == 1, (ctx.Arg32(5) & 1) != 0);
    }

    private static ulong FindFirstFile(ApiCallContext ctx, ulong findData, bool directoriesOnly = false, bool caseSensitive = false)
    {
        var pattern = ReadUnicodeArg(ctx, 0);
        if (string.IsNullOrEmpty(pattern) || findData == 0)
            return FailInvalidHandle(ctx, Win32Error.InvalidParameter);

        FileSearchEntry[] entries;
        try
        {
            entries = BuildSearchEntries(ctx, pattern, caseSensitive);
            if (directoriesOnly) entries = entries.Where(entry => entry.IsDirectory).ToArray();
        }
        catch (NtFileException ex)
        {
            return FailInvalidHandle(ctx, Win32FromStatus(ex.Status));
        }
        catch (Exception ex) when (ex is NtFileException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            ctx.Runtime.Trace.Warn($"{ctx.Name}('{pattern}') failed: {ex.GetType().Name}: {ex.Message}", ctx.Process.Id, ctx.Thread.Id);
            return FailInvalidHandle(ctx, FileError(ex));
        }

        if (entries.Length == 0)
        {
            ctx.SetLastError(Win32Error.FileNotFound);
            return InvalidHandleValue;
        }

        var search = new FileSearchObject(pattern, entries);
        var handle = ctx.Handles.Create(search, 0x0012_019F);
        search.Release();
        try
        {
            search.TryNext(out var first);
            WriteFindData(ctx, findData, first);
            return handle;
        }
        catch
        {
            ctx.Handles.Close(handle);
            throw;
        }
    }

    [VirtualApi("kernel32.dll", "FindNextFileW", ApiCategory.File)]
    private static ulong FindNextFileW(ApiCallContext ctx)
    {
        var findData = ctx.Arg(1);
        if (findData == 0) return BoolResult(ctx, false, Win32Error.InvalidParameter);
        if (!ctx.Handles.TryGet(ctx.Arg(0), out var obj) || obj is not FileSearchObject search)
            return BoolResult(ctx, false, Win32Error.InvalidHandle);
        if (!search.TryNext(out var entry))
            return BoolResult(ctx, false, Win32Error.NoMoreFiles);
        WriteFindData(ctx, findData, entry);
        return 1;
    }

    [VirtualApi("kernel32.dll", "FindClose", ApiCategory.File)]
    private static ulong FindClose(ApiCallContext ctx)
    {
        if (ctx.Handles.Close(ctx.Arg(0)) != NtStatus.Success)
            return BoolResult(ctx, false, Win32Error.InvalidHandle);
        return 1;
    }

    private static FileSearchEntry[] BuildSearchEntries(ApiCallContext ctx, string pattern, bool caseSensitive = false)
    {
        var normalized = pattern.Replace('/', '\\');
        var currentDirectory = ctx.Process.CurrentDirectory;

        if (normalized.IndexOfAny(['*', '?']) < 0 && !caseSensitive)
        {
            var host = ctx.Process.FileSystem.MapPath(normalized, currentDirectory, FileOperation.Enum);
            // FindFirstFileW takes a file-name pattern, not a bare directory path.  Windows
            // returns ERROR_FILE_NOT_FOUND for "C:\", "C:" and for "."/".." components; treating
            // them as "enumerate everything" makes cmd.exe interpret the first entry as the
            // current directory during startup.
            if (normalized.EndsWith('\\') || normalized.EndsWith(':'))
                throw new NtFileException(NtStatus.ObjectNameNotFound, pattern);
            var leaf = normalized[(normalized.LastIndexOf('\\') + 1)..];
            if (leaf is "." or ".." &&
                string.Equals(host, ctx.Process.FileSystem.Root, StringComparison.OrdinalIgnoreCase))
                throw new NtFileException(NtStatus.ObjectNameNotFound, pattern);

            if (HostFileSystem.DirectoryExists(host))
            {
                var name = Path.GetFileName(host.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                return [CreateEntry(HostFileSystem.GetInfo(host), string.IsNullOrEmpty(name) ? "\\" : name)];
            }
            if (HostFileSystem.FileExists(host))
                return [CreateEntry(HostFileSystem.GetInfo(host), Path.GetFileName(normalized))];
            throw new NtFileException(NtStatus.ObjectNameNotFound, pattern);
        }

        var separator = normalized.LastIndexOf('\\');
        string directoryPart;
        string namePattern;
        if (separator < 0)
        {
            directoryPart = ".";
            namePattern = normalized;
        }
        else
        {
            directoryPart = normalized[..separator];
            namePattern = normalized[(separator + 1)..];
            if (directoryPart.Length == 0) directoryPart = "\\";
            else if (directoryPart.EndsWith(':')) directoryPart += "\\";
        }

        var directoryHost = ctx.Process.FileSystem.MapPath(directoryPart, currentDirectory, FileOperation.Enum);
        if (!HostFileSystem.DirectoryExists(directoryHost)) throw new NtFileException(NtStatus.ObjectNameNotFound, pattern);
        return ctx.Process.FileSystem.Enumerate(directoryPart, currentDirectory).Where(i => MatchesFindPattern(namePattern, i.Name, caseSensitive)).Select(i => CreateEntry(i, i.Name)).ToArray();
    }

    private static FileSearchEntry[] EnumerateDirectory(string hostDirectory, string pattern, bool caseSensitive)
    {
        var results = new List<FileSearchEntry>();
        foreach (var info in HostFileSystem.EnumerateInfos(hostDirectory))
        {
            if (!MatchesFindPattern(pattern, info.Name, caseSensitive)) continue;
            results.Add(CreateEntry(info, info.Name));
        }

        // Windows does not define an enumeration order.  A stable order keeps scripts and tests
        // deterministic while still exposing the real host entries.
        results.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name));
        return results.ToArray();
    }

    private static bool MatchesFindPattern(string pattern, string name, bool caseSensitive)
    {
        // Win32 treats "*.*" as "anything" even when the name has no dot.  Normalizing this one
        // quirk keeps the .NET matcher's simpler wildcard rules predictable for the shell.
        if (pattern.Length == 0 || pattern is "*" or "*.*") return true;
        return FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: !caseSensitive);
    }

    private static FileSearchEntry CreateEntry(HostFileInfo info, string name)
    {
        var isDirectory = (info.Attributes & FileAttributes.Directory) != 0;
        var attributes = isDirectory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
        if ((info.Attributes & FileAttributes.ReadOnly) != 0) attributes |= FILE_ATTRIBUTE_READONLY;
        if ((info.Attributes & FileAttributes.Hidden) != 0) attributes |= FILE_ATTRIBUTE_HIDDEN;
        if ((info.Attributes & FileAttributes.System) != 0) attributes |= FILE_ATTRIBUTE_SYSTEM;
        if ((info.Attributes & FileAttributes.Archive) != 0) attributes |= FILE_ATTRIBUTE_ARCHIVE;

        long size = 0;
        if (!isDirectory)
        {
            try { size = info.Length; } catch { size = 0; }
        }

        DateTime creation, access, write;
        try
        {
            creation = info.CreationTimeUtc;
            access = info.LastAccessTimeUtc;
            write = info.LastWriteTimeUtc;
        }
        catch
        {
            creation = access = write = DateTime.UtcNow;
        }

        return new FileSearchEntry
        {
            Name = name,
            HostPath = info.FullName,
            IsDirectory = isDirectory,
            Size = size,
            Attributes = attributes,
            CreationTimeUtc = SafeFileTime(creation),
            LastAccessTimeUtc = SafeFileTime(access),
            LastWriteTimeUtc = SafeFileTime(write)
        };
    }

    private static long SafeFileTime(DateTime value)
    {
        try { return value.ToFileTimeUtc(); }
        catch { return 0; }
    }

    private static void WriteFindData(ApiCallContext ctx, ulong buffer, FileSearchEntry entry)
    {
        ctx.Memory.WriteBytes(buffer, new byte[Win32FindDataSize]);
        ctx.Memory.Write(buffer, entry.Attributes);
        ctx.Memory.Write(buffer + 4, entry.CreationTimeUtc);
        ctx.Memory.Write(buffer + 12, entry.LastAccessTimeUtc);
        ctx.Memory.Write(buffer + 20, entry.LastWriteTimeUtc);
        ctx.Memory.Write(buffer + 28, (uint)(entry.Size >> 32));
        ctx.Memory.Write(buffer + 32, (uint)entry.Size);
        ctx.Memory.Write(buffer + 36, 0u);
        ctx.Memory.Write(buffer + 40, 0u);

        var nameBytes = System.Text.Encoding.Unicode.GetBytes(entry.Name);
        var capacity = (MaxPathChars - 1) * 2;
        if (nameBytes.Length > capacity)
            nameBytes = nameBytes[..capacity];
        if (nameBytes.Length > 0)
            ctx.Memory.WriteBytes(buffer + Win32FindDataFileNameOffset, nameBytes);
        // cAlternateFileName stays empty (zero-filled above).
    }

    /// <summary>
    /// Convert a DOS path into the guest's NT form.  The native ntdll implementation walks host
    /// loader data and crashes when called on guest strings; cmd.exe uses this while deleting
    /// files, so provide the small managed equivalent and allocate from the host heap (the native
    /// RtlFreeUnicodeString that cmd calls afterwards frees it with the host process heap).
    /// </summary>
    [VirtualApi("ntdll.dll", "RtlDosPathNameToRelativeNtPathName_U_WithStatus", ApiCategory.File)]
    private static ulong RtlDosPathNameToRelativeNtPathName_U_WithStatus(ApiCallContext ctx)
    {
        var dosName = ctx.Arg(0);
        var ntName = ctx.Arg(1);
        var partName = ctx.Arg(2);
        if (dosName == 0 || ntName == 0) return NtStatus.ObjectNameInvalid;
        string guestPath;
        try
        {
            var text = ctx.ReadUnicodeString(dosName);
            guestPath = ctx.Process.FileSystem.GetFullPath(text, ctx.Process.CurrentDirectory);
        }
        catch (NtFileException)
        {
            return NtStatus.ObjectPathNotFound;
        }

        var ntPath = "\\??\\" + guestPath;
        var bytes = System.Text.Encoding.Unicode.GetByteCount(ntPath) + 2;
        var buffer = HostRtlAllocateHeap(HostGetProcessHeap(), 0, (nuint)bytes);
        if (buffer == 0) return NtStatus.NoMemory;
        var chars = ntPath.ToCharArray();
        System.Runtime.InteropServices.Marshal.Copy(chars, 0, buffer, chars.Length);
        System.Runtime.InteropServices.Marshal.WriteInt16(buffer, chars.Length * 2, 0);
        // UNICODE_STRING { USHORT Length; USHORT MaximumLength; PWSTR Buffer; }
        ctx.Memory.Write((ushort)ntName, (ushort)(chars.Length * 2));
        ctx.Memory.Write((ushort)(ntName + 2), (ushort)bytes);
        ctx.Memory.Write((ulong)(ntName + 8), (ulong)buffer);
        if (partName != 0)
        {
            var leaf = guestPath.LastIndexOf('\\');
            ctx.Memory.Write((ulong)partName, leaf < 0 ? 0UL : (ulong)buffer + (ulong)(leaf + 1) * 2);
        }
        return NtStatus.Success;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern nint HostGetProcessHeap();

    [System.Runtime.InteropServices.DllImport("ntdll.dll")]
    private static extern nint HostRtlAllocateHeap(nint heap, uint flags, nuint size);

    [VirtualApi("kernel32.dll", "DeleteFileW", ApiCategory.File)]
    private static ulong DeleteFileW(ApiCallContext ctx)
    {
        var path = ReadUnicodeArg(ctx, 0);
        if (path is null) return BoolResult(ctx, false, Win32Error.InvalidParameter);
        try
        {
            ctx.Process.FileSystem.Delete(path, ctx.Process.CurrentDirectory);
            return 1;
        }
        catch (NtFileException ex) { return BoolResult(ctx, false, Win32FromStatus(ex.Status)); }
        catch (IOException) { return BoolResult(ctx, false, Win32Error.SharingViolation); }
    }

    [VirtualApi("kernel32.dll", "CreateDirectoryW", ApiCategory.File)]
    private static ulong CreateDirectoryW(ApiCallContext ctx)
    {
        var path = ReadUnicodeArg(ctx, 0);
        if (path is null) return BoolResult(ctx, false, Win32Error.InvalidParameter);
        try
        {
            var host = ctx.Process.FileSystem.MapPath(path, ctx.Process.CurrentDirectory, FileOperation.Write);
            if (HostFileSystem.DirectoryExists(host)) return BoolResult(ctx, false, Win32Error.AlreadyExists);
            if (!ctx.Process.FileSystem.ResolveAccess(path, ctx.Process.CurrentDirectory, FileOperation.Write).Discard) HostFileSystem.CreateDirectory(host);
            return 1;
        }
        catch (NtFileException ex) { return BoolResult(ctx, false, Win32FromStatus(ex.Status)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return BoolResult(ctx, false, Win32Error.AccessDenied); }
    }

    [VirtualApi("kernel32.dll", "RemoveDirectoryW", ApiCategory.File)]
    private static ulong RemoveDirectoryW(ApiCallContext ctx)
    {
        var path = ReadUnicodeArg(ctx, 0);
        if (path is null) return BoolResult(ctx, false, Win32Error.InvalidParameter);
        try
        {
            var host = ctx.Process.FileSystem.MapPath(path, ctx.Process.CurrentDirectory, FileOperation.Delete);
            if (!HostFileSystem.DirectoryExists(host)) return BoolResult(ctx, false, Win32Error.PathNotFound);
            ctx.Process.FileSystem.Delete(path, ctx.Process.CurrentDirectory);
            return 1;
        }
        catch (NtFileException ex) { return BoolResult(ctx, false, Win32FromStatus(ex.Status)); }
        catch (IOException) { return BoolResult(ctx, false, Win32Error.DirectoryNotEmpty); }
        catch (UnauthorizedAccessException) { return BoolResult(ctx, false, Win32Error.AccessDenied); }
    }

    [VirtualApi("kernel32.dll", "MoveFileExW", ApiCategory.File)]
    private static ulong MoveFileExW(ApiCallContext ctx)
    {
        var from = ReadUnicodeArg(ctx, 0);
        var to = ReadUnicodeArg(ctx, 1);
        if (from is null || to is null) return BoolResult(ctx, false, Win32Error.InvalidParameter);
        try
        {
            var hostFrom = ctx.Process.FileSystem.MapPath(from, ctx.Process.CurrentDirectory);
            var hostTo = ctx.Process.FileSystem.MapPath(to, ctx.Process.CurrentDirectory, FileOperation.Write);
            var replace = (ctx.Arg32(2) & 0x1) != 0; // MOVEFILE_REPLACE_EXISTING
            if (HostFileSystem.FileExists(hostTo) && !replace) return BoolResult(ctx, false, Win32Error.AlreadyExists);
            _ = ctx.Process.FileSystem.MapPath(from, ctx.Process.CurrentDirectory, FileOperation.Delete);
            hostFrom = ctx.Process.FileSystem.MapNativePath(from, ctx.Process.CurrentDirectory, false, true);
            hostTo = ctx.Process.FileSystem.MapNativePath(to, ctx.Process.CurrentDirectory, false, true);
            HostFileSystem.Move(hostFrom, hostTo, replace);
            return 1;
        }
        catch (NtFileException ex) { return BoolResult(ctx, false, Win32FromStatus(ex.Status)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return BoolResult(ctx, false, Win32Error.AccessDenied); }
    }

    [VirtualApi("kernel32.dll", "GetFullPathNameW", ApiCategory.File)]
    private static ulong GetFullPathNameW(ApiCallContext ctx) => GetFullPathName(ctx, false);

    [VirtualApi("kernel32.dll", "GetFullPathNameA", ApiCategory.File)]
    private static ulong GetFullPathNameA(ApiCallContext ctx) => GetFullPathName(ctx, true);

    private static ulong GetFullPathName(ApiCallContext ctx, bool ansi)
    {
        var path = ReadVolumePath(ctx, ansi);
        if (string.IsNullOrEmpty(path)) return BoolResult(ctx, false, Win32Error.InvalidParameter);
        try
        {
            var value = ctx.Process.FileSystem.GetFullPath(path, ctx.Process.CurrentDirectory);
            if (ctx.Arg32(1) <= value.Length) return (ulong)value.Length + 1;
            if (ctx.Arg(2) == 0) return BoolResult(ctx, false, Win32Error.InvalidParameter);
            WriteVolumeString(ctx, ctx.Arg(2), ctx.Arg32(1), value, ansi);
            if (ctx.Arg(3) != 0)
                ctx.Memory.Write(ctx.Arg(3), value.EndsWith('\\') ? 0UL :
                    ctx.Arg(2) + (ulong)(value.LastIndexOf('\\') + 1) * (ansi ? 1UL : 2UL));
            return (ulong)value.Length;
        }
        catch (NtFileException ex) { return BoolResult(ctx, false, Win32FromStatus(ex.Status)); }
    }

    // --- native NT file API ---------------------------------------------------------
    [VirtualApi("ntdll.dll", "NtCreateFile", ApiCategory.File)]
    private static ulong NtCreateFile(ApiCallContext ctx)
    {
        var handlePtr = ctx.Arg(0);
        var desiredAccess = ctx.Arg32(1);
        var attributes = ctx.ReadObjectAttributes(ctx.Arg(2));
        var ioStatus = ctx.Arg(3);
        var share = ctx.Arg32(6);
        var disposition = (CreateDisposition)ctx.Arg32(7);
        var options = (CreateOptions)ctx.Arg32(8);
        var path = attributes.FullPath ?? attributes.Name;
        if (string.IsNullOrEmpty(path))
        {
            WriteNtStatus(ctx, ioStatus, NtStatus.ObjectNameInvalid);
            return NtStatus.ObjectNameInvalid;
        }
        try
        {
            var handle = OpenManagedFile(ctx, path, desiredAccess, share, disposition, options);
            if (handlePtr != 0) ctx.Memory.Write(handlePtr, handle);
            WriteNtStatus(ctx, ioStatus, NtStatus.Success);
            return NtStatus.Success;
        }
        catch (NtFileException ex)
        {
            WriteNtStatus(ctx, ioStatus, ex.Status);
            return ex.Status;
        }
    }

    [VirtualApi("ntdll.dll", "NtOpenFile", ApiCategory.File)]
    private static ulong NtOpenFile(ApiCallContext ctx)
    {
        var handlePtr = ctx.Arg(0);
        var desiredAccess = ctx.Arg32(1);
        var attributes = ctx.ReadObjectAttributes(ctx.Arg(2));
        var ioStatus = ctx.Arg(3);
        var share = ctx.Arg32(4);
        var options = (CreateOptions)ctx.Arg32(5);
        var path = attributes.FullPath ?? attributes.Name;
        if (string.IsNullOrEmpty(path))
        {
            WriteNtStatus(ctx, ioStatus, NtStatus.ObjectNameInvalid);
            return NtStatus.ObjectNameInvalid;
        }
        try
        {
            var handle = OpenManagedFile(ctx, path, desiredAccess, share, CreateDisposition.Open, options);
            if (handlePtr != 0) ctx.Memory.Write(handlePtr, handle);
            WriteNtStatus(ctx, ioStatus, NtStatus.Success);
            return NtStatus.Success;
        }
        catch (NtFileException ex)
        {
            WriteNtStatus(ctx, ioStatus, ex.Status);
            return ex.Status;
        }
    }

    [VirtualApi("ntdll.dll", "NtReadFile", ApiCategory.File)]
    private static ulong NtReadFile(ApiCallContext ctx) => NtReadWrite(ctx, write: false);

    [VirtualApi("ntdll.dll", "NtWriteFile", ApiCategory.File)]
    private static ulong NtWriteFile(ApiCallContext ctx) => NtReadWrite(ctx, write: true);

    [VirtualApi("ntdll.dll", "NtFlushBuffersFile", ApiCategory.File)]
    private static ulong NtFlushBuffersFile(ApiCallContext ctx)
    {
        if (!ctx.TryGetFile(ctx.Arg(0), out var file) || file is null) return NtStatus.InvalidHandle;
        file.Flush();
        WriteNtStatus(ctx, ctx.Arg(1), NtStatus.Success);
        return NtStatus.Success;
    }

    [VirtualApi("ntdll.dll", "NtQueryAttributesFile", ApiCategory.File)]
    private static ulong NtQueryAttributesFile(ApiCallContext ctx)
    {
        var attributes = ctx.ReadObjectAttributes(ctx.Arg(0));
        var path = attributes.FullPath ?? attributes.Name;
        if (string.IsNullOrEmpty(path)) return NtStatus.ObjectNameInvalid;
        try
        {
            var host = ctx.Process.FileSystem.MapMetadataPath(path, ctx.Process.CurrentDirectory);
            var info = HostFileSystem.FileExists(host) ? HostFileSystem.GetInfo(host) : HostFileSystem.DirectoryExists(host) ? null : throw new NtFileException(NtStatus.ObjectNameNotFound, path);
            var buffer = ctx.Arg(1);
            if (buffer != 0) WriteBasicInformation(ctx, buffer, host, info);
            return NtStatus.Success;
        }
        catch (NtFileException ex) { return ex.Status; }
    }

    [VirtualApi("ntdll.dll", "NtQueryFullAttributesFile", ApiCategory.File)]
    private static ulong NtQueryFullAttributesFile(ApiCallContext ctx)
    {
        var attributes = ctx.ReadObjectAttributes(ctx.Arg(0));
        var path = attributes.FullPath ?? attributes.Name;
        if (string.IsNullOrEmpty(path)) return NtStatus.ObjectNameInvalid;
        try
        {
            var host = ctx.Process.FileSystem.MapMetadataPath(path, ctx.Process.CurrentDirectory);
            if (!HostFileSystem.FileExists(host) && !HostFileSystem.DirectoryExists(host)) return NtStatus.ObjectNameNotFound;
            var buffer = ctx.Arg(1);
            if (buffer != 0) WriteNetworkOpenInformation(ctx, buffer, host);
            return NtStatus.Success;
        }
        catch (NtFileException ex) { return ex.Status; }
    }

    [VirtualApi("ntdll.dll", "NtDeleteFile", ApiCategory.File)]
    private static ulong NtDeleteFile(ApiCallContext ctx)
    {
        var attributes = ctx.ReadObjectAttributes(ctx.Arg(0));
        var path = attributes.FullPath ?? attributes.Name;
        if (string.IsNullOrEmpty(path)) return NtStatus.ObjectNameInvalid;
        try { ctx.Process.FileSystem.Delete(path, ctx.Process.CurrentDirectory); return NtStatus.Success; }
        catch (NtFileException ex) { return ex.Status; }
        catch (IOException) { return NtStatus.SharingViolation; }
    }

    private static ulong FailInvalidHandle(ApiCallContext ctx, int error)
    {
        ctx.SetLastError(error);
        return InvalidHandleValue;
    }

    private static bool TrySeek(FileObject file, long distance, uint method, out long position)
    {
        try
        {
            var origin = method switch
            {
                0 => SeekOrigin.Begin,
                1 => SeekOrigin.Current,
                2 => SeekOrigin.End,
                _ => (SeekOrigin)(-1)
            };
            if ((int)origin < 0) { position = 0; return false; }
            position = file.Seek(distance, origin);
            return true;
        }
        catch (NotSupportedException)
        {
            position = 0;
            return false;
        }
    }

    private static ulong NtReadWrite(ApiCallContext ctx, bool write)
    {
        var handle = ctx.Arg(0);
        var ioStatus = ctx.Arg(4);
        var buffer = ctx.Arg(5);
        var length = (int)Math.Min(ctx.Arg32(6), 1 << 30);
        var byteOffsetPtr = ctx.Arg(7);
        if (!ctx.TryGetFile(handle, out var file) || file is null || file.IsDirectory)
        {
            WriteNtStatus(ctx, ioStatus, NtStatus.InvalidHandle);
            return NtStatus.InvalidHandle;
        }
        if (write && !file.CanWrite) return FailIoStatus(ctx, ioStatus, NtStatus.AccessDenied);
        if (!write && !file.CanRead) return FailIoStatus(ctx, ioStatus, NtStatus.AccessDenied);

        long? originalPosition = null;
        try
        {
            if (byteOffsetPtr != 0 && file is (DiskFileObject or MappedFileObject))
            {
                originalPosition = file.Position;
                file.Position = ctx.Memory.Read<long>(byteOffsetPtr);
            }

            int transferred;
            if (write)
            {
                var bytes = ctx.Memory.ReadBytes(buffer, length);
                file.Write(bytes);
                if (file is ConsoleFileObject) file.Flush();
                transferred = bytes.Length;
            }
            else
            {
                var bytes = new byte[length];
                transferred = file.Read(bytes);
                if (transferred > 0) ctx.Memory.WriteBytes(buffer, bytes.AsSpan(0, transferred));
            }

            if (originalPosition is not null) file.Position = originalPosition.Value;
            WriteNtStatus(ctx, ioStatus, NtStatus.Success, (ulong)transferred);
            return NtStatus.Success;
        }
        catch (NtMemoryException ex)
        {
            return FailIoStatus(ctx, ioStatus, ex.Status);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            if (ex.NativeErrorCode != Win32Error.BrokenPipe)
                ctx.Runtime.Trace.Warn($"{ctx.Name} failed: {ex}", ctx.Process.Id, ctx.Thread.Id);
            return FailIoStatus(ctx, ioStatus, NtStatus.FromWin32(ex.NativeErrorCode));
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return FailIoStatus(ctx, ioStatus, NtStatus.InvalidDeviceRequest);
        }
        finally
        {
            if (originalPosition is not null)
            {
                try { file.Position = originalPosition.Value; } catch { }
            }
        }
    }

    private static ulong FailIoStatus(ApiCallContext ctx, ulong ioStatus, uint status)
    {
        WriteNtStatus(ctx, ioStatus, status);
        return status;
    }

    private static void WriteBasicInformation(ApiCallContext ctx, ulong buffer, string hostPath, HostFileInfo? info)
    {
        info ??= HostFileSystem.GetInfo(hostPath);
        ctx.Memory.Write(buffer, ToFileTime(info.CreationTimeUtc));
        ctx.Memory.Write(buffer + 8, ToFileTime(info.LastAccessTimeUtc));
        ctx.Memory.Write(buffer + 16, ToFileTime(info.LastWriteTimeUtc));
        ctx.Memory.Write(buffer + 24, ToFileTime(info.LastWriteTimeUtc));
        ctx.Memory.Write(buffer + 32, info.IsDirectory ? FILE_ATTRIBUTE_DIRECTORY : 0x80u);
        ctx.Memory.Write(buffer + 36, 0u);
    }

    private static void WriteNetworkOpenInformation(ApiCallContext ctx, ulong buffer, string hostPath)
    {
        var info = HostFileSystem.GetInfo(hostPath);
        var size = info.IsDirectory ? 0 : info.Length;
        ctx.Memory.Write(buffer, ToFileTime(info.CreationTimeUtc));
        ctx.Memory.Write(buffer + 8, ToFileTime(info.LastAccessTimeUtc));
        ctx.Memory.Write(buffer + 16, ToFileTime(info.LastWriteTimeUtc));
        ctx.Memory.Write(buffer + 24, ToFileTime(info.LastWriteTimeUtc));
        ctx.Memory.Write(buffer + 32, (ulong)size);
        ctx.Memory.Write(buffer + 40, (ulong)size);
        ctx.Memory.Write(buffer + 48, info.IsDirectory ? FILE_ATTRIBUTE_DIRECTORY : 0x80u);
        ctx.Memory.Write(buffer + 52, 0u);
    }

    [VirtualApi("ntdll.dll", "NtQueryInformationFile", ApiCategory.File)]
    private static ulong NtQueryInformationFile(ApiCallContext ctx)
    {
        var handle = ctx.Arg(0);
        var ioStatus = ctx.Arg(1);
        var buffer = ctx.Arg(2);
        var length = ctx.Arg32(3);
        var infoClass = (FileInformationClass)ctx.Arg32(4);
        if (!ctx.TryGetFile(handle, out var file) || file is null)
        {
            WriteNtStatus(ctx, ioStatus, NtStatus.InvalidHandle);
            return NtStatus.InvalidHandle;
        }

        switch (infoClass)
        {
            case FileInformationClass.FileBasicInformation:
            {
                if (length < 40) return FailIoStatus(ctx, ioStatus, NtStatus.BufferTooSmall);
                if (file is DiskFileObject disk) WriteBasicInformation(ctx, buffer, disk.HostPath, HostFileSystem.GetInfo(disk.HostPath));
                else if (file is MappedFileObject { HostPath: { } mappedPath }) WriteBasicInformation(ctx, buffer, mappedPath, HostFileSystem.GetInfo(mappedPath));
                else
                {
                    var now = ToFileTime(DateTime.UtcNow);
                    ctx.Memory.Write(buffer, now);
                    ctx.Memory.Write(buffer + 8, now);
                    ctx.Memory.Write(buffer + 16, now);
                    ctx.Memory.Write(buffer + 24, now);
                    ctx.Memory.Write(buffer + 32, 0x80u);
                    ctx.Memory.Write(buffer + 36, 0u);
                }
                WriteNtStatus(ctx, ioStatus, NtStatus.Success, 40);
                return NtStatus.Success;
            }
            case FileInformationClass.FileStandardInformation:
            {
                if (length < 24) return FailIoStatus(ctx, ioStatus, NtStatus.BufferTooSmall);
                var size = (ulong)Math.Max(0, file.Length);
                ctx.Memory.Write(buffer, size);
                ctx.Memory.Write(buffer + 8, size);
                ctx.Memory.Write(buffer + 16, 1u);
                ctx.Memory.Write(buffer + 20, (byte)0);
                ctx.Memory.Write(buffer + 21, (byte)(file.IsDirectory ? 1 : 0));
                ctx.Memory.Write(buffer + 22, (ushort)0);
                WriteNtStatus(ctx, ioStatus, NtStatus.Success, 24);
                return NtStatus.Success;
            }
            case FileInformationClass.FilePositionInformation:
            {
                if (length < 8) return FailIoStatus(ctx, ioStatus, NtStatus.BufferTooSmall);
                ctx.Memory.Write(buffer, file.Position);
                WriteNtStatus(ctx, ioStatus, NtStatus.Success, 8);
                return NtStatus.Success;
            }
            case FileInformationClass.FileNameInformation:
            {
                var name = file.Path ?? string.Empty;
                var bytes = System.Text.Encoding.Unicode.GetBytes(name);
                var required = 4 + bytes.Length + 2;
                if (length < required)
                {
                    if (length >= 4) ctx.Memory.Write(buffer, (uint)(bytes.Length + 2));
                    return FailIoStatus(ctx, ioStatus, NtStatus.BufferTooSmall);
                }
                ctx.Memory.Write(buffer, (uint)(bytes.Length + 2));
                ctx.Memory.WriteBytes(buffer + 4, bytes);
                ctx.Memory.Write(buffer + 4 + (ulong)bytes.Length, (ushort)0);
                WriteNtStatus(ctx, ioStatus, NtStatus.Success, (ulong)required);
                return NtStatus.Success;
            }
            case FileInformationClass.FileEndOfFileInformation:
            {
                if (length < 8) return FailIoStatus(ctx, ioStatus, NtStatus.BufferTooSmall);
                ctx.Memory.Write(buffer, file.Length);
                WriteNtStatus(ctx, ioStatus, NtStatus.Success, 8);
                return NtStatus.Success;
            }
            default:
                return FailIoStatus(ctx, ioStatus, NtStatus.InvalidInfoClass);
        }
    }

    [VirtualApi("ntdll.dll", "NtSetInformationFile", ApiCategory.File)]
    private static ulong NtSetInformationFile(ApiCallContext ctx)
    {
        var handle = ctx.Arg(0);
        var ioStatus = ctx.Arg(1);
        var buffer = ctx.Arg(2);
        var length = ctx.Arg32(3);
        var infoClass = (FileInformationClass)ctx.Arg32(4);
        if (!ctx.TryGetFile(handle, out var file) || file is null)
        {
            WriteNtStatus(ctx, ioStatus, NtStatus.InvalidHandle);
            return NtStatus.InvalidHandle;
        }

        switch (infoClass)
        {
            case FileInformationClass.FilePositionInformation:
                if (length < 8) return FailIoStatus(ctx, ioStatus, NtStatus.BufferTooSmall);
                file.Position = ctx.Memory.Read<long>(buffer);
                WriteNtStatus(ctx, ioStatus, NtStatus.Success);
                return NtStatus.Success;
            case FileInformationClass.FileDispositionInformation:
                if (length < 1) return FailIoStatus(ctx, ioStatus, NtStatus.BufferTooSmall);
                var delete = ctx.Memory.Read<byte>(buffer) != 0;
                if (delete && file.Path is { } deletePath) _ = ctx.Process.FileSystem.MapPath(deletePath, ctx.Process.CurrentDirectory, FileOperation.Delete);
                file.DeleteOnClose = delete;
                WriteNtStatus(ctx, ioStatus, NtStatus.Success);
                return NtStatus.Success;
            case FileInformationClass.FileEndOfFileInformation:
            {
                if (length < 8) return FailIoStatus(ctx, ioStatus, NtStatus.BufferTooSmall);
                if (file is not DiskFileObject && file is not MappedFileObject) return FailIoStatus(ctx, ioStatus, NtStatus.InvalidDeviceRequest);
                var newSize = ctx.Memory.Read<long>(buffer);
                if (newSize < 0) return FailIoStatus(ctx, ioStatus, NtStatus.InvalidParameter);
                if (file is MappedFileObject mapped) { mapped.SetLength(newSize); WriteNtStatus(ctx, ioStatus, NtStatus.Success); return NtStatus.Success; }
                var disk = (DiskFileObject)file;
                disk.Position = newSize;
                var stream = HostFileSystem.Open(disk.HostPath, FileMode.Open, System.IO.FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
                stream.SetLength(newSize);
                stream.Dispose();
                WriteNtStatus(ctx, ioStatus, NtStatus.Success);
                return NtStatus.Success;
            }
            default:
                return FailIoStatus(ctx, ioStatus, NtStatus.InvalidInfoClass);
        }
    }
}
