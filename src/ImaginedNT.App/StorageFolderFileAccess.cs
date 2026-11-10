using System.Runtime.InteropServices;
using ImaginedNT.Core.Diagnostics;
using ImaginedNT.Core.Io;
using Microsoft.Win32.SafeHandles;
using Windows.Foundation;
using Windows.Storage;
using WinRT;

namespace ImaginedNT.App;

/// <summary>
/// Host file access for folders granted by the folder picker.  The WinRT storage broker refuses
/// to create or open some executable file types, so the virtual view keeps the normal
/// <c>.exe</c>/<c>.dll</c> names while the broker only ever sees <c>.exec</c>/<c>.dllb</c>.
/// Every path that crosses this boundary is translated in both directions; a legacy
/// <c>.exe</c>/<c>.dll</c> that already exists is permanently renamed on first access so the
/// broker never has to see it again.
/// </summary>
internal sealed class StorageFolderFileAccess : IHostFileSystemAccess
{
    private const string VirtualExecutableExtension = ".exe";
    private const string BrokerExecutableExtension = ".exec";
    private const string VirtualLibraryExtension = ".dll";
    private const string BrokerLibraryExtension = ".dllb";

    private sealed record Grant(string Path, StorageFolder Folder);
    private readonly Dictionary<char, Grant> _roots = new();
    private readonly TraceLog? _trace;
    private static readonly object MigrationGate = new();

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

    /// <summary>Map a virtual <c>.exe</c>/<c>.dll</c> path to the broker-visible <c>.exec</c>/<c>.dllb</c> name.</summary>
    private static string ToBrokerPath(string path)
    {
        var extension = Path.GetExtension(path);
        if (string.Equals(extension, VirtualExecutableExtension, StringComparison.OrdinalIgnoreCase))
            return Path.ChangeExtension(path, BrokerExecutableExtension)!;
        if (string.Equals(extension, VirtualLibraryExtension, StringComparison.OrdinalIgnoreCase))
            return Path.ChangeExtension(path, BrokerLibraryExtension)!;
        return path;
    }

    /// <summary>Map a broker-returned <c>.exec</c>/<c>.dllb</c> name back to the virtual view.</summary>
    private static string ToVirtualPath(string path)
    {
        var extension = Path.GetExtension(path);
        if (string.Equals(extension, BrokerExecutableExtension, StringComparison.OrdinalIgnoreCase))
            return Path.ChangeExtension(path, VirtualExecutableExtension)!;
        if (string.Equals(extension, BrokerLibraryExtension, StringComparison.OrdinalIgnoreCase))
            return Path.ChangeExtension(path, VirtualLibraryExtension)!;
        return path;
    }

    private static bool IsBrokerName(string path)
    {
        var extension = Path.GetExtension(path);
        return string.Equals(extension, BrokerExecutableExtension, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, BrokerLibraryExtension, StringComparison.OrdinalIgnoreCase);
    }

    private static bool NeedsMigration(string path)
    {
        var extension = Path.GetExtension(path);
        return string.Equals(extension, VirtualExecutableExtension, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, VirtualLibraryExtension, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolve a virtual file name to the name the broker is allowed to use.  Legacy
    /// <c>.exe</c>/<c>.dll</c> files are renamed once, permanently, rather than being renamed back
    /// after the operation finishes.
    /// </summary>
    private (string Path, bool Canonical) EnsureBrokerName(string virtualPath)
    {
        if (IsBrokerName(virtualPath)) return (virtualPath, true);
        if (!NeedsMigration(virtualPath)) return (virtualPath, false);

        var brokerPath = ToBrokerPath(virtualPath);
        lock (MigrationGate)
        {
            var brokerItem = TryGetItem(brokerPath);
            if (brokerItem is StorageFile) return (brokerPath, true);
            if (brokerItem is not null) return (brokerPath, false);

            var virtualItem = TryGetItem(virtualPath);
            // A directory that happens to end in .exe/.dll is still a directory, not a PE file.
            if (virtualItem is StorageFolder) return (virtualPath, true);
            if (virtualItem is not StorageFile legacyFile) return (brokerPath, false);

            Wait(legacyFile.RenameAsync(Path.GetFileName(brokerPath), NameCollisionOption.FailIfExists));
            _trace?.Emit(TraceKind.File, $"migrated WinRT file name: '{virtualPath}' -> '{brokerPath}'");
            return (brokerPath, true);
        }
    }

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

    private IStorageItem? TryGetItem(string path)
    {
        path = Normalize(path);
        var root = FindRoot(path);
        if (root is not null)
        {
            var relative = Path.GetRelativePath(root.Path, path);
            return relative == "." ? root.Folder : Wait(root.Folder.TryGetItemAsync(relative), path);
        }
        return Wait(GetFolder(Path.GetDirectoryName(path)!).TryGetItemAsync(Path.GetFileName(path)), path);
    }

    public HostFileInfo? TryGetInfo(string path)
    {
        path = Normalize(path);
        var (brokerPath, _) = EnsureBrokerName(path);
        var item = TryGetItem(brokerPath);
        return item is null ? null : GetInfo(item);
    }

    public SafeFileHandle OpenHandle(string path, FileMode mode, FileAccess access, FileShare share, FileOptions options)
    {
        path = Normalize(path);
        var (brokerPath, _) = EnsureBrokerName(path);
        if (!brokerPath.Equals(path, StringComparison.Ordinal))
            _trace?.Emit(TraceKind.File, $"WinRT storage name map: '{path}' -> '{brokerPath}'");
        return OpenHandleCore(brokerPath, mode, access, share, options);
    }

    private SafeFileHandle OpenHandleCore(string path, FileMode mode, FileAccess access, FileShare share,
        FileOptions options)
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
        if ((options & FileOptions.DeleteOnClose) != 0) desired |= 0x10000;
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

    private static HostFileInfo GetInfo(IStorageItem item, string? fullName = null)
    {
        var properties = Wait(item.GetBasicPropertiesAsync(), item.Path);
        var isFolder = item.IsOfType(StorageItemTypes.Folder);
        var attributes = (System.IO.FileAttributes)item.Attributes;
        if (attributes == 0) attributes = System.IO.FileAttributes.Normal;
        // Folders keep their real name; only files are hidden behind the broker's file-type suffixes.
        fullName ??= isFolder ? item.Path : ToVirtualPath(item.Path);
        return new(fullName, attributes, isFolder ? 0 : checked((long)properties.Size),
            item.DateCreated.UtcDateTime, properties.DateModified.UtcDateTime, properties.DateModified.UtcDateTime);
    }

    public IEnumerable<HostFileInfo> EnumerateInfos(string path)
    {
        var folder = GetFolder(path);
        var items = new List<IStorageItem>();
        for (uint start = 0; ; start += 256)
        {
            var page = Wait(folder.GetItemsAsync(start, 256), path);
            items.AddRange(page);
            if (page.Count < 256) break;
        }

        var result = new List<HostFileInfo>(items.Count);
        // A legacy .exe sitting next to the canonical .exec maps to the same virtual name.
        // Keep the canonical broker name so the virtual view never exposes duplicates.
        var virtualFiles = new Dictionary<string, (IStorageItem Item, string VirtualPath, bool Canonical)>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (item.IsOfType(StorageItemTypes.Folder))
            {
                result.Add(GetInfo(item));
                continue;
            }

            var (brokerPath, canonical) = EnsureBrokerName(item.Path);
            var virtualPath = ToVirtualPath(brokerPath);
            // Migration renames the file behind our back, so refresh the item before reading it.
            var finalItem = brokerPath.Equals(item.Path, StringComparison.Ordinal) ? item : TryGetItem(brokerPath);
            if (finalItem is null) continue;
            if (!virtualFiles.TryGetValue(virtualPath, out var existing) || (canonical && !existing.Canonical))
                virtualFiles[virtualPath] = (finalItem, virtualPath, canonical);
        }
        foreach (var entry in virtualFiles.Values) result.Add(GetInfo(entry.Item, entry.VirtualPath));
        return result;
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
        var (brokerPath, _) = EnsureBrokerName(Normalize(path));
        StorageFile file;
        try { file = Wait(GetFolder(Path.GetDirectoryName(brokerPath)!).GetFileAsync(Path.GetFileName(brokerPath))); }
        catch (FileNotFoundException) { return; }
        Wait(file.DeleteAsync(StorageDeleteOption.PermanentDelete));
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
        var (brokerSource, _) = EnsureBrokerName(Normalize(source));
        var (brokerTarget, _) = EnsureBrokerName(Normalize(target));
        var file = Wait(GetFolder(Path.GetDirectoryName(brokerSource)!).GetFileAsync(Path.GetFileName(brokerSource)));
        Wait(file.MoveAsync(GetFolder(Path.GetDirectoryName(brokerTarget)!), Path.GetFileName(brokerTarget),
            overwrite ? NameCollisionOption.ReplaceExisting : NameCollisionOption.FailIfExists));
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

    [ComImport, Guid("DF19938F-5462-48A0-BE65-D2A3271A08D6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IStorageFolderHandleAccess
    {
        [PreserveSig]
        int Create([MarshalAs(UnmanagedType.LPWStr)] string name, uint creation, uint access, uint share,
            uint options, nint oplockHandler, out nint handle);
    }
}
