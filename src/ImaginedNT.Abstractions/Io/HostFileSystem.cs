using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ImaginedNT.Core.Io;

// The frontend supplies access through the folders granted by the picker.
public static class HostFileSystem
{
    private static string[] _directAccessRoots = [];
    public static bool UseAppContainerApis { get; set; }
    public static IHostFileSystemAccess? GrantedAccess { get; set; }

    private static IHostFileSystemAccess? GetGrantedAccess(string path) =>
        GrantedAccess is { } access && access.CanAccess(path) ? access : null;

    public static void SetDirectAccessRoots(params string[] paths)
    {
        _directAccessRoots = paths.Select(p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p))).ToArray();
        Debug.WriteLine($"Direct access roots: {string.Join(", ", _directAccessRoots)}");
    }

    public static SafeFileHandle OpenHandle(string path, FileMode mode, FileAccess access,
        FileShare share, FileOptions options = FileOptions.None)
    {
        if (GetGrantedAccess(path) is { } granted) return granted.OpenHandle(path, mode, access, share, options);
        try
        {
            return File.OpenHandle(path, mode, access, share, options);
        }
        catch { }
        uint desired = access == FileAccess.Read ? 0x80000000u : access == FileAccess.Write ? 0x40000000u : 0xC0000000u;
        if ((options & FileOptions.DeleteOnClose) != 0) desired |= 0x10000;
        uint disposition = mode switch
        {
            FileMode.CreateNew => 1,
            FileMode.Create => 2,
            FileMode.Open => 3,
            FileMode.OpenOrCreate or FileMode.Append => 4,
            FileMode.Truncate => 5,
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
        var handle = CreateFileFromAppW(path, desired, (uint)(share & ~FileShare.Inheritable), 0,
            disposition, (uint)options | 0x80u, 0);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastPInvokeError();
        handle.Dispose();
        throw Error(path, error);
    }

    public static FileStream Open(string path, FileMode mode, FileAccess access, FileShare share,
        int bufferSize = 4096, FileOptions options = FileOptions.None)
    {
        if (GetGrantedAccess(path) is null)
        {
            try { return new FileStream(path, mode, access, share, bufferSize, options); }
            catch { }
        }
        var handle = OpenHandle(path, mode, access, share, options);
        try
        {
            var stream = new FileStream(handle, access, bufferSize, (options & FileOptions.Asynchronous) != 0);
            if (mode == FileMode.Append) stream.Seek(0, SeekOrigin.End);
            return stream;
        }
        catch { handle.Dispose(); throw; }
    }

    public static FileStream OpenRead(string path) => Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);

    public static byte[] ReadAllBytes(string path)
    {
        if (GetGrantedAccess(path) is null)
        {
            try { return File.ReadAllBytes(path); }
            catch { }
        }
        using var stream = OpenRead(path);
        var result = new byte[checked((int)stream.Length)];
        stream.ReadExactly(result);
        return result;
    }

    public static bool FileExists(string path) => Exists(path, false);
    public static bool DirectoryExists(string path) => Exists(path, true);

    private static bool Exists(string path, bool directory)
    {
        try
        {
            if (GetGrantedAccess(path) is { } granted) return granted.TryGetInfo(path)?.IsDirectory == directory;
            if (directory ? Directory.Exists(path) : File.Exists(path)) return true;
            return GetFileAttributesExFromAppW(Path.GetFullPath(path), 0, out var data) &&
                ((data.Attributes & FileAttributes.Directory) != 0) == directory;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    public static HostFileInfo GetInfo(string path)
    {
        if (GetGrantedAccess(path) is { } granted) return granted.GetInfo(path);
        try
        {
            var attributes = File.GetAttributes(path);
            FileSystemInfo info = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(path) : new FileInfo(path);
            return new(path, attributes, info is FileInfo file ? file.Length : 0,
                info.CreationTimeUtc, info.LastAccessTimeUtc, info.LastWriteTimeUtc);
        }
        catch { }
        if (!GetFileAttributesExFromAppW(path, 0, out var data)) throw Error(path, Marshal.GetLastPInvokeError());
        return new(path, data.Attributes, ((long)data.SizeHigh << 32) | data.SizeLow,
            Time(data.Creation), Time(data.Access), Time(data.Write));
    }

    public static FileAttributes GetAttributes(string path) => GetInfo(path).Attributes;

    public static IEnumerable<HostFileInfo> EnumerateInfos(string path)
    {
        if (GetGrantedAccess(path) is { } granted)
        {
            foreach (var item in granted.EnumerateInfos(path)) yield return item;
            yield break;
        }
        IEnumerable<HostFileInfo> directs = null;
        try
        {
            directs = (new DirectoryInfo(path).EnumerateFileSystemInfos()).Select(info => new HostFileInfo(info.FullName, info.Attributes, info is FileInfo file ? file.Length : 0,
                    info.CreationTimeUtc, info.LastAccessTimeUtc, info.LastWriteTimeUtc));
        }
        catch { }
        if (directs is not null && directs.Any())
        {
            foreach (var item in directs)
            {
                yield return item;
            }
            yield break;
        }
        var handle = FindFirstFileExFromAppW(Path.Combine(path, "*"), 1, out var data, 0, 0, 0);
        if (handle == -1)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == 2 && DirectoryExists(path)) yield break;
            throw Error(path, error);
        }
        try
        {
            do
            {
                if (data.Name is "." or "..") continue;
                yield return new(Path.Combine(path, data.Name), data.Attributes,
                    ((long)data.SizeHigh << 32) | data.SizeLow, Time(data.Creation), Time(data.Access), Time(data.Write));
            } while (FindNextFileW(handle, out data));
            var error = Marshal.GetLastPInvokeError();
            if (error != 18) throw Error(path, error);
        }
        finally { FindClose(handle); }
    }

    public static IEnumerable<string> EnumerateFiles(string path) => EnumerateInfos(path).Where(f => !f.IsDirectory).Select(f => f.FullName);
    public static IEnumerable<string> EnumerateFileSystemEntries(string path) => EnumerateInfos(path).Select(f => f.FullName);
    public static string[] GetFileSystemEntries(string path, string pattern) => EnumerateInfos(path)
        .Where(f => System.IO.Enumeration.FileSystemName.MatchesWin32Expression(pattern, f.Name, true))
        .Select(f => f.FullName).ToArray();

    public static void CreateDirectory(string path)
    {
        if (GetGrantedAccess(path) is { } granted)
        {
            granted.CreateDirectory(path);
            return;
        }
        try
        {
            Directory.CreateDirectory(path);
            return;
        }
        catch { }
        path = Path.GetFullPath(path);
        if (DirectoryExists(path)) return;
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path));
        if (parent is not null && !DirectoryExists(parent)) CreateDirectory(parent);
        if (!CreateDirectoryFromAppW(path, 0))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error != 183 || !DirectoryExists(path)) throw Error(path, error);
        }
    }

    public static void DeleteFile(string path)
    {
        if (GetGrantedAccess(path) is { } granted)
        {
            granted.DeleteFile(path);
            return;
        }
        try
        {
            File.Delete(path);
            return;
        }
        catch { }
        if (!DeleteFileFromAppW(path))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error != 2) throw Error(path, error);
        }
    }

    public static void DeleteDirectory(string path)
    {
        if (GetGrantedAccess(path) is { } granted)
        {
            granted.DeleteDirectory(path);
            return;
        }
        try
        {
            Directory.Delete(path, recursive: false);
            return;
        }
        catch { }
        if (!RemoveDirectoryFromAppW(path)) throw Error(path, Marshal.GetLastPInvokeError());
    }

    public static void Copy(string source, string target, bool overwrite = false)
    {
        if (GetGrantedAccess(source) is null && GetGrantedAccess(target) is null)
        {
            try
            {
                File.Copy(source, target, overwrite);
                return;
            }
            catch { }
        }
        using var input = OpenRead(source);
        using var output = Open(target, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
    }

    public static void Move(string source, string target, bool overwrite = false)
    {
        if ((GetGrantedAccess(source) ?? GetGrantedAccess(target)) is { } granted)
        {
            granted.Move(source, target, overwrite);
            return;
        }
        try
        {
            File.Move(source, target, overwrite);
            return;
        }
        catch { }
        if (overwrite && FileExists(target))
        {
            if (!ReplaceFileFromAppW(target, source, null, 0, 0, 0)) throw Error(target, Marshal.GetLastPInvokeError());
        }
        else if (!MoveFileFromAppW(source, target)) throw Error(source, Marshal.GetLastPInvokeError());
    }

    private static DateTime Time(System.Runtime.InteropServices.ComTypes.FILETIME value) =>
        DateTime.FromFileTimeUtc(((long)value.dwHighDateTime << 32) | (uint)value.dwLowDateTime);

    private static Exception Error(string path, int error)
    {
        var message = $"{new Win32Exception(error).Message}: '{path}'";
        return error switch
        {
            2 => new FileNotFoundException(message, path),
            3 => new DirectoryNotFoundException(message),
            5 => new UnauthorizedAccessException(message),
            _ => new IOException(message, unchecked((int)(0x80070000u | (uint)error)))
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeData
    {
        public FileAttributes Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint SizeHigh, SizeLow;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FindData
    {
        public FileAttributes Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint SizeHigh, SizeLow, Reserved0, Reserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string AlternateName;
    }

    private const string StorageApi = "api-ms-win-core-file-fromapp-l1-1-0.dll";
    [DllImport(StorageApi, CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileFromAppW(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [DllImport(StorageApi, CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool GetFileAttributesExFromAppW(string path, int level, out AttributeData data);
    [DllImport(StorageApi, CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern nint FindFirstFileExFromAppW(string pattern, int level, out FindData data, int search, nint filter, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool FindNextFileW(nint handle, out FindData data);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern bool FindClose(nint handle);
    [DllImport(StorageApi, CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool CreateDirectoryFromAppW(string path, nint security);
    [DllImport(StorageApi, CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool DeleteFileFromAppW(string path);
    [DllImport(StorageApi, CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool RemoveDirectoryFromAppW(string path);
    [DllImport(StorageApi, CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool MoveFileFromAppW(string source, string target);
    [DllImport(StorageApi, CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool ReplaceFileFromAppW(string target, string source, string? backup, uint flags, nint exclude, nint reserved);
}

public interface IHostFileSystemAccess
{
    bool CanAccess(string path);
    SafeFileHandle OpenHandle(string path, FileMode mode, FileAccess access, FileShare share, FileOptions options);
    HostFileInfo GetInfo(string path);
    HostFileInfo? TryGetInfo(string path);
    IEnumerable<HostFileInfo> EnumerateInfos(string path);
    void CreateDirectory(string path);
    void DeleteFile(string path);
    void DeleteDirectory(string path);
    void Move(string source, string target, bool overwrite);
}

public sealed record HostFileInfo(string FullName, FileAttributes Attributes, long Length,
    DateTime CreationTimeUtc, DateTime LastAccessTimeUtc, DateTime LastWriteTimeUtc)
{
    public string Name => Path.GetFileName(Path.TrimEndingDirectorySeparator(FullName));
    public bool IsDirectory => (Attributes & FileAttributes.Directory) != 0;
    public DateTime LastWriteTime => LastWriteTimeUtc.ToLocalTime();
}
