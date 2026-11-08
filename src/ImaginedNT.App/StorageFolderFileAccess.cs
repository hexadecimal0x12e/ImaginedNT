using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using ImaginedNT.Core.Diagnostics;
using ImaginedNT.Core.Io;
using Microsoft.Win32.SafeHandles;
using Windows.Foundation;
using Windows.Storage;
using WinRT;

namespace ImaginedNT.App;

internal sealed class StorageFolderFileAccess : IHostFileSystemAccess
{
    private sealed record Grant(string Path, StorageFolder Folder);
    private readonly Dictionary<char, Grant> _roots = new();
    private static readonly object ExecutableGate = new();
    private readonly TraceLog? _trace;

    public StorageFolderFileAccess(TraceLog? trace = null) => _trace = trace;

    public void Register(char letter, StorageFolder folder)
    {
        lock (_roots) _roots[letter] = new(Normalize(folder.Path), folder);
    }

    public void Remove(char letter)
    {
        lock (_roots) _roots.Remove(letter);
    }

    public void Clear()
    {
        lock (_roots) _roots.Clear();
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private Grant? FindRoot(string path)
    {
        path = Normalize(path);
        lock (_roots)
            return _roots.Values.Where(r => path.Equals(r.Path, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(Path.EndsInDirectorySeparator(r.Path) ? r.Path : r.Path + "\\", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(r => r.Path.Length).FirstOrDefault();
    }

    public bool CanAccess(string path) => FindRoot(path) is not null;

    private StorageFolder GetFolder(string path)
    {
        path = Normalize(path);
        var root = FindRoot(path);
        if (root is null) return Wait(StorageFolder.GetFolderFromPathAsync(path), path);
        var relative = Path.GetRelativePath(root.Path, path);
        return relative == "." ? root.Folder : Wait(root.Folder.GetFolderAsync(relative), path);
    }

    public HostFileInfo? TryGetInfo(string path)
    {
        path = Normalize(path);
        if (IsExecutable(path))
        {
            lock (ExecutableGate)
            {
                using var renamed = new TemporaryExecutable(this, path);
                return renamed.File is null ? null : GetInfo(renamed.File) with { FullName = path };
            }
        }
        var root = FindRoot(path);
        IStorageItem? item;
        if (root is not null)
        {
            var relative = Path.GetRelativePath(root.Path, path);
            item = relative == "." ? root.Folder : Wait(root.Folder.TryGetItemAsync(relative), path);
        }
        else
        {
            item = Wait(GetFolder(Path.GetDirectoryName(path)!).TryGetItemAsync(Path.GetFileName(path)), path);
        }
        return item is null ? null : GetInfo(item);
    }

    public SafeFileHandle OpenHandle(string path, FileMode mode, FileAccess access, FileShare share, FileOptions options)
    {
        path = Normalize(path);
        if (!IsExecutable(path)) return OpenHandleCore(path, mode, access, share, options);
        lock (ExecutableGate)
        {
            using var renamed = new TemporaryExecutable(this, path);
            var actualMode = renamed.File is null && mode is (FileMode.Create or FileMode.OpenOrCreate or FileMode.Append)
                ? FileMode.CreateNew : mode;
            var handle = OpenHandleCore(renamed.Path, actualMode, access, share, options & ~FileOptions.DeleteOnClose, true);
            try
            {
                renamed.Restore(handle);
                if ((options & FileOptions.DeleteOnClose) != 0)
                {
                    if (!SetFileInformationByHandle(handle, 4, [1], 1))
                        throw new IOException($"无法设置文件关闭时删除：{path}", new Win32Exception(Marshal.GetLastPInvokeError()));
                }
                return handle;
            }
            catch { handle.Dispose(); throw; }
        }
    }

    private SafeFileHandle OpenHandleCore(string path, FileMode mode, FileAccess access, FileShare share,
        FileOptions options, bool allowRename = false)
    {
        var folder = GetFolder(Path.GetDirectoryName(path)!);
        uint creation = mode switch
        {
            FileMode.CreateNew => 1,
            FileMode.Create => 2,
            FileMode.Open => 3,
            FileMode.OpenOrCreate or FileMode.Append => 4,
            FileMode.Truncate => 5,
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
        uint desired = access switch
        {
            FileAccess.Read => 0x120089,
            FileAccess.Write => 0x120116,
            FileAccess.ReadWrite => 0x12019F,
            _ => throw new ArgumentOutOfRangeException(nameof(access))
        };
        if (allowRename || (options & FileOptions.DeleteOnClose) != 0) desired |= 0x10000;
        // Use the granted folder's COM interface, rather than reopening its absolute path.
        var hr = folder.As<IStorageFolderHandleAccess>().Create(Path.GetFileName(path), creation, desired,
            (uint)(share & ~FileShare.Inheritable), (uint)options, 0, out var handle);
        if (hr < 0)
        {
            if (handle != 0 && handle != -1) new SafeFileHandle(handle, true).Dispose();
            Marshal.ThrowExceptionForHR(hr);
        }
        return new SafeFileHandle(handle, true);
    }

    public HostFileInfo GetInfo(string path) => TryGetInfo(path) ??
        throw new FileNotFoundException($"找不到文件或目录：{path}", path);

    private static HostFileInfo GetInfo(IStorageItem item)
    {
        var properties = Wait(item.GetBasicPropertiesAsync(), item.Path);
        var attributes = (System.IO.FileAttributes)item.Attributes;
        if (attributes == 0) attributes = System.IO.FileAttributes.Normal;
        return new(item.Path, attributes, item.IsOfType(StorageItemTypes.Folder) ? 0 : checked((long)properties.Size),
            item.DateCreated.UtcDateTime, properties.DateModified.UtcDateTime, properties.DateModified.UtcDateTime);
    }

    public IEnumerable<HostFileInfo> EnumerateInfos(string path)
    {
        var folder = GetFolder(path);
        lock (ExecutableGate)
        {
            var items = new List<IStorageItem>();
            for (uint start = 0; ; start += 256)
            {
                var page = Wait(folder.GetItemsAsync(start, 256), path);
                items.AddRange(page);
                if (page.Count < 256) break;
            }
            return items.Select(item => item.IsOfType(StorageItemTypes.File) && IsExecutable(item.Path)
                ? GetInfo(item.Path) : GetInfo(item)).ToArray();
        }
    }

    private static bool IsExecutable(string path) => Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase);

    private sealed class TemporaryExecutable : IDisposable
    {
        private readonly StorageFolderFileAccess _owner;
        private readonly string _originalPath;
        private bool _needsRestore;
        public string Path { get; }
        public StorageFile? File { get; }

        public TemporaryExecutable(StorageFolderFileAccess owner, string path)
        {
            _owner = owner;
            _originalPath = path;
            Path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, $".imaginednt-{Guid.NewGuid():N}.exec");
            try { File = Wait(owner.GetFolder(System.IO.Path.GetDirectoryName(path)!).GetFileAsync(System.IO.Path.GetFileName(path)), path); }
            catch (FileNotFoundException) { return; }
            Wait(File.RenameAsync(System.IO.Path.GetFileName(Path), NameCollisionOption.FailIfExists));
            _needsRestore = true;
        }

        public void Restore(SafeFileHandle handle)
        {
            _needsRestore = true;
            // Restore through the open handle so FileShare.None remains usable.
            var offset = IntPtr.Size == 8 ? 20 : 12;
            var name = Encoding.Unicode.GetBytes(_originalPath);
            var buffer = new byte[offset + name.Length + 2];
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset - 4), name.Length);
            name.CopyTo(buffer, offset);
            if (!SetFileInformationByHandle(handle, 3, buffer, (uint)buffer.Length))
                throw new IOException($"无法恢复文件名：'{Path}' -> '{_originalPath}'", new Win32Exception(Marshal.GetLastPInvokeError()));
            _needsRestore = false;
        }

        public void Dispose()
        {
            if (!_needsRestore) return;
            try
            {
                var file = File ?? Wait(_owner.GetFolder(System.IO.Path.GetDirectoryName(Path)!).GetFileAsync(System.IO.Path.GetFileName(Path)), Path);
                Wait(file.RenameAsync(System.IO.Path.GetFileName(_originalPath), NameCollisionOption.FailIfExists));
                _needsRestore = false;
            }
            catch (Exception ex)
            {
                _owner._trace?.Error($"executable name restore failed: '{Path}' -> '{_originalPath}': {ex}");
                throw new IOException($"临时文件名恢复失败，文件保留在 '{Path}'，原路径为 '{_originalPath}'。", ex);
            }
        }
    }

    public void CreateDirectory(string path)
    {
        var root = FindRoot(path) ?? throw new UnauthorizedAccessException($"目录未授权：{path}");
        var relative = Path.GetRelativePath(root.Path, Path.GetFullPath(path));
        if (relative == ".") return;
        var folder = root.Folder;
        foreach (var part in relative.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
            folder = Wait(folder.CreateFolderAsync(part, CreationCollisionOption.OpenIfExists));
    }

    public void DeleteFile(string path)
    {
        lock (ExecutableGate)
        {
            StorageFile file;
            try { file = Wait(GetFolder(Path.GetDirectoryName(path)!).GetFileAsync(Path.GetFileName(path))); }
            catch (FileNotFoundException) { return; }
            Wait(file.DeleteAsync(StorageDeleteOption.PermanentDelete));
        }
    }

    public void DeleteDirectory(string path)
    {
        var folder = GetFolder(path);
        if (Wait(folder.GetItemsAsync(0, 1)).Count != 0)
            throw new IOException($"目录不为空：{path}", unchecked((int)0x80070091));
        Wait(folder.DeleteAsync(StorageDeleteOption.PermanentDelete));
    }

    public void Move(string source, string target, bool overwrite)
    {
        lock (ExecutableGate)
        {
            var file = Wait(GetFolder(Path.GetDirectoryName(source)!).GetFileAsync(Path.GetFileName(source)));
            Wait(file.MoveAsync(GetFolder(Path.GetDirectoryName(target)!), Path.GetFileName(target),
                overwrite ? NameCollisionOption.ReplaceExisting : NameCollisionOption.FailIfExists));
        }
    }

    private static T Wait<T>(IAsyncOperation<T> operation, string? path = null)
    {
        try { return AwaitAsync(operation).GetAwaiter().GetResult(); }
        catch (FileNotFoundException ex) when (path is not null)
        {
            throw new FileNotFoundException($"找不到文件或目录：{path}", path, ex);
        }
        catch (UnauthorizedAccessException ex) when (path is not null)
        {
            throw new UnauthorizedAccessException($"访问文件或目录被拒绝：{path}", ex);
        }
    }

    public static async Task<T> AwaitAsync<T>(IAsyncOperation<T> operation)
    {
        var completion = new TaskCompletionSource<AsyncStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        operation.Completed = (_, status) => { completion.TrySetResult(status); };
        if (await completion.Task.ConfigureAwait(false) == AsyncStatus.Canceled)
        {
            operation.Close();
            throw new OperationCanceledException();
        }
        // Retrieve broker results off both the completion callback and the UI thread.
        return await Task.Run(() =>
        {
            try { return operation.GetResults(); }
            finally { operation.Close(); }
        }).WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
    }

    private static void Wait(IAsyncAction operation)
    {
        var completion = new TaskCompletionSource<AsyncStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        operation.Completed = (_, status) => { completion.TrySetResult(status); };
        if (completion.Task.GetAwaiter().GetResult() == AsyncStatus.Canceled)
        {
            operation.Close();
            throw new OperationCanceledException();
        }
        Task.Run(() =>
        {
            try { operation.GetResults(); }
            finally { operation.Close(); }
        }).WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, byte[] information, uint size);

    [ComImport, Guid("DF19938F-5462-48A0-BE65-D2A3271A08D6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IStorageFolderHandleAccess
    {
        [PreserveSig]
        int Create([MarshalAs(UnmanagedType.LPWStr)] string name, uint creation, uint access, uint share,
            uint options, nint oplockHandler, out nint handle);
    }
}
