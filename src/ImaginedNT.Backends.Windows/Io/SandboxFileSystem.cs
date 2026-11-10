using ImaginedNT.Core.Diagnostics;
using ImaginedNT.Core.Nt;

namespace ImaginedNT.Core.Io;

/// <summary>
/// A path-jailed view of the host file system.  Guest paths such as
/// \??\C:\Users\test\a.txt are translated into a private sandbox directory; every
/// translation is canonicalised and checked again before any host I/O happens.
/// </summary>
public sealed class SandboxFileSystem
{
    private readonly TraceLog? _trace;
    private readonly int _pid;
    private readonly Dictionary<char, VirtualDrive> _drives = new();

    public SandboxFileSystem(string root, bool allowWrites = true, TraceLog? trace = null, int pid = 0,
        IReadOnlyDictionary<char, VirtualDriveOptions>? drives = null)
    {
        AllowWrites = allowWrites;
        _trace = trace;
        _pid = pid;
        var mappings = new Dictionary<char, VirtualDriveOptions> { ['C'] = new(root) };
        if (drives is not null)
            foreach (var pair in drives)
            {
                var letter = char.ToUpperInvariant(pair.Key);
                if (letter < 'A' || letter > 'Z') throw new ArgumentException("drive letter must be A-Z");
                mappings[letter] = pair.Value;
            }
        foreach (var pair in mappings.OrderBy(p => p.Key))
        {
            var drive = new VirtualDrive(pair.Key, pair.Value);
            if (drive.MapTable is null) HostFileSystem.CreateDirectory(drive.HostDirectory);
            _drives.Add(pair.Key, drive);
            _trace?.Emit(TraceKind.Security, $"file sandbox {pair.Key}: -> {drive.HostDirectory} writes={allowWrites}", pid);
        }
        Root = _drives['C'].HostDirectory;
    }

    public string Root { get; }
    public bool AllowWrites { get; }
    public IReadOnlyCollection<VirtualDrive> Drives => _drives.Values;

    private (VirtualDrive Drive, string Path) ResolvePath(string ntPath, string currentDirectory)
    {
        if (string.IsNullOrWhiteSpace(ntPath) || ntPath.IndexOf('\0') >= 0)
            throw new NtFileException(NtStatus.ObjectNameInvalid, "invalid path");
        var path = ntPath.Replace('/', '\\');
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\??\", StringComparison.Ordinal)) path = path[4..];
        foreach (var d in _drives.Values)
            if (path.StartsWith(d.VolumeName[4..], StringComparison.OrdinalIgnoreCase))
            {
                path = d.RootPath + path[(d.VolumeName.Length - 4)..];
                break;
            }
        if (path.StartsWith("Volume{", StringComparison.OrdinalIgnoreCase))
            throw new NtFileException(NtStatus.ObjectPathNotFound, "volume is not mapped");
        if (path.StartsWith(@"\DosDevices\", StringComparison.OrdinalIgnoreCase)) path = path[12..];
        foreach (var d in _drives.Values)
        {
            if (path.Equals(d.DevicePath, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(d.DevicePath + "\\", StringComparison.OrdinalIgnoreCase))
            {
                path = d.Letter + ":" + path[d.DevicePath.Length..];
                if (path.Length == 2) path += "\\";
                break;
            }
        }
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase))
            throw new NtFileException(NtStatus.ObjectNameInvalid, "unmapped device or UNC path");
        var current = currentDirectory.Replace('/', '\\');
        var currentDrive = current.Length >= 2 && current[1] == ':' ? char.ToUpperInvariant(current[0]) : 'C';
        var letter = path.Length >= 2 && path[1] == ':' ? char.ToUpperInvariant(path[0]) : currentDrive;
        if (!_drives.TryGetValue(letter, out var drive))
            throw new NtFileException(NtStatus.ObjectPathNotFound, $"drive {letter}: is not mapped");
        if (path.Length >= 2 && path[1] == ':') path = path[2..];
        if (!path.StartsWith('\\'))
        {
            if (current.Length >= 2 && current[1] == ':') current = current[2..];
            path = (letter == currentDrive ? current : "\\").TrimEnd('\\') + "\\" + path;
        }
        var relative = path.TrimStart('\\');
        if (relative.Contains(':')) throw new NtFileException(NtStatus.ObjectNameInvalid, "alternate data streams are blocked");
        var host = Path.GetFullPath(Path.Combine(drive.HostDirectory, relative));
        var root = drive.HostDirectory.TrimEnd('\\', '/');
        if (!host.Equals(root, StringComparison.OrdinalIgnoreCase) && !host.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            _trace?.Emit(TraceKind.Security, $"blocked sandbox escape: '{ntPath}'", _pid);
            throw new NtFileException(NtStatus.AccessDenied, "path escapes mapped drive");
        }
        return (drive, host);
    }

    public string MapPath(string ntPath, string currentDirectory = "\\", FileOperation operation = FileOperation.Read) =>
        ResolveAccess(ntPath, currentDirectory, operation).Path;

    public string MapImagePath(string host)
    {
        host = Path.GetFullPath(host);
        foreach (var drive in _drives.Values)
        {
            var relative = Path.GetRelativePath(drive.HostDirectory, host);
            if (relative == ".." || relative.StartsWith("..\\") || Path.IsPathRooted(relative)) continue;
            var guest = drive.RootPath + (relative == "." ? "" : relative);
            _ = MapPath(guest, drive.RootPath, FileOperation.Execute);
            return MapPath(guest, drive.RootPath, FileOperation.Read);
        }
        return host;
    }

    public bool HasMapTables => _drives.Values.Any(d => d.MapTable is not null);

    public (string Path, bool Discard) ResolveAccess(string path, string current, FileOperation operation, int depth = 0)
    {
        if (depth > 32) throw new NtFileException(NtStatus.AccessDenied, "cyclic file remapping");
        var resolved = ResolvePath(path, current);
        var table = resolved.Drive.MapTable;
        FileRule? rule = null;
        var suffix = "";
        if (table is not null)
        {
            var relative = Path.GetRelativePath(resolved.Drive.HostDirectory, resolved.Path);
            (rule, suffix) = table.Resolve(relative == "." ? "" : relative, operation, out var excluded);
            if (excluded) throw new NtFileException(NtStatus.ObjectNameNotFound, $"path excluded: {path}");
        }
        if ((operation is FileOperation.Write or FileOperation.Delete) && (!AllowWrites || table?.Readonly == true))
            throw new NtFileException(NtStatus.AccessDenied, "volume is read-only");
        if (rule is null) return (resolved.Path, false);
        if (!rule.Allowed)
        {
            _trace?.Emit(TraceKind.Security, $"denied {operation}: {path}", _pid);
            throw new NtFileException(NtStatus.AccessDenied, $"{operation} denied: {path}");
        }
        if (rule.Discard) return (resolved.Path, true);
        if (rule.Target is null) return (resolved.Path, false);
        var target = suffix.Length == 0 ? rule.Target : rule.Target.TrimEnd('\\', '/') + "\\" + suffix;
        if (rule.Virtual) return ResolveAccess(target, resolved.Drive.RootPath, operation, depth + 1);
        return (Path.GetFullPath(target), false);
    }

    public VirtualDrive GetDrive(string path, string currentDirectory = "\\") => ResolvePath(path, currentDirectory).Drive;

    public string GetFullPath(string path, string currentDirectory = "\\")
    {
        var resolved = ResolvePath(path, currentDirectory);
        var relative = Path.GetRelativePath(resolved.Drive.HostDirectory, resolved.Path);
        return resolved.Drive.RootPath + (relative == "." ? "" : relative.Replace('/', '\\'));
    }

    public FileObject CreateOrOpen(string ntPath, uint desiredAccess, ShareAccess share, CreateDisposition disposition,
        CreateOptions options, string currentDirectory = "\\")
    {
        ntPath = GetFullPath(ntPath, currentDirectory);
        var hostPath = ResolvePath(ntPath, currentDirectory).Path;
        var isDirectoryRequest = (options & CreateOptions.DirectoryFile) != 0;
        var existingDirectory = HostFileSystem.DirectoryExists(hostPath);
        var existingFile = HostFileSystem.FileExists(hostPath);
        var wantsWrite = (desiredAccess & (NtAccess.FileWriteData | NtAccess.FileAppendData | NtAccess.GenericWrite | NtAccess.GenericAll)) != 0;
        var wantsRead = (desiredAccess & (NtAccess.FileReadData | NtAccess.FileExecute | NtAccess.GenericRead | NtAccess.GenericExecute | NtAccess.GenericAll)) != 0;

        if (GetDrive(ntPath, currentDirectory).MapTable is not null)
        {
            try { return OpenMapped(ntPath, desiredAccess, share, disposition, options, currentDirectory, wantsRead, wantsWrite); }
            catch (UnauthorizedAccessException ex) { throw new NtFileException(NtStatus.AccessDenied, ex.Message); }
            catch (DirectoryNotFoundException ex) { throw new NtFileException(NtStatus.ObjectPathNotFound, ex.Message); }
            catch (FileNotFoundException ex) { throw new NtFileException(NtStatus.ObjectNameNotFound, ex.Message); }
            catch (IOException ex) { throw new NtFileException(NtStatus.SharingViolation, ex.Message); }
        }

        if (wantsWrite && !AllowWrites)
            throw new NtFileException(NtStatus.AccessDenied, "the sandbox is read-only");

        if (existingDirectory && !isDirectoryRequest && (options & CreateOptions.NonDirectoryFile) != 0)
            throw new NtFileException(NtStatus.FileIsADirectory, $"is a directory: {ntPath}");

        if (isDirectoryRequest)
        {
            if ((disposition is CreateDisposition.Open or CreateDisposition.OpenIf) && !existingDirectory)
                throw new NtFileException(NtStatus.ObjectNameNotFound, $"directory not found: {ntPath}");
            if (disposition is CreateDisposition.Create or CreateDisposition.OverwriteIf)
                HostFileSystem.CreateDirectory(hostPath);
            if (!HostFileSystem.DirectoryExists(hostPath))
                throw new NtFileException(NtStatus.ObjectNameNotFound, $"directory not found: {ntPath}");
            return new DirectoryFileObject(hostPath, ntPath);
        }

        if ((disposition is CreateDisposition.Open or CreateDisposition.Overwrite) && !existingFile)
        {
            if (existingDirectory) throw new NtFileException(NtStatus.FileIsADirectory, $"is a directory: {ntPath}");
            throw new NtFileException(NtStatus.ObjectNameNotFound, $"file not found: {ntPath}");
        }

        if ((disposition is CreateDisposition.Create) && existingFile)
            throw new NtFileException(NtStatus.ObjectNameCollision, $"file exists: {ntPath}");

        var fileMode = disposition switch
        {
            CreateDisposition.Supersede => FileMode.Create,
            CreateDisposition.Open => FileMode.Open,
            CreateDisposition.Create => FileMode.CreateNew,
            CreateDisposition.OpenIf => FileMode.OpenOrCreate,
            CreateDisposition.Overwrite => FileMode.Truncate,
            CreateDisposition.OverwriteIf => FileMode.Create,
            _ => FileMode.Open
        };

        var access = System.IO.FileAccess.Read;
        if (wantsWrite) access = wantsRead ? System.IO.FileAccess.ReadWrite : System.IO.FileAccess.Write;
        else if (!wantsRead) access = System.IO.FileAccess.Read;

        if (fileMode is FileMode.Open or FileMode.Truncate && !existingFile)
            throw new NtFileException(NtStatus.ObjectNameNotFound, $"file not found: {ntPath}");

        var shareMode = FileShare.ReadWrite | FileShare.Delete;
        if ((share & ShareAccess.Read) == 0) shareMode &= ~FileShare.Read;
        if ((share & ShareAccess.Write) == 0) shareMode &= ~FileShare.Write;
        if ((share & ShareAccess.Delete) == 0) shareMode &= ~FileShare.Delete;

        try
        {
            var stream = HostFileSystem.Open(hostPath, fileMode, access, shareMode, 4096, System.IO.FileOptions.None);
            var obj = new DiskFileObject(hostPath, ntPath, stream, desiredAccess);
            _trace?.Emit(TraceKind.File, $"open '{ntPath}' -> {hostPath} mode={fileMode} access={access} share={shareMode}", _pid);
            return obj;
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new NtFileException(NtStatus.AccessDenied, ex.Message);
        }
        catch (DirectoryNotFoundException ex)
        {
            throw new NtFileException(NtStatus.ObjectPathNotFound, ex.Message);
        }
        catch (FileNotFoundException ex)
        {
            throw new NtFileException(NtStatus.ObjectNameNotFound, ex.Message);
        }
        catch (IOException ex)
        {
            throw new NtFileException(NtStatus.SharingViolation, ex.Message);
        }
    }

    private FileObject OpenMapped(string path, uint access, ShareAccess share, CreateDisposition disposition,
        CreateOptions options, string current, bool read, bool write)
    {
        write |= disposition != CreateDisposition.Open;
        if ((access & (NtAccess.FileExecute | NtAccess.GenericExecute)) != 0) _ = MapPath(path, current, FileOperation.Execute);
        var r = read || !write ? ResolveAccess(path, current, FileOperation.Read) : default;
        var w = write ? ResolveAccess(path, current, FileOperation.Write) : default;
        var host = read || !write ? r.Path : w.Path;
        if ((options & CreateOptions.DirectoryFile) != 0 || HostFileSystem.DirectoryExists(host))
        {
            _ = MapPath(path, current, FileOperation.Enum);
            if ((options & CreateOptions.NonDirectoryFile) != 0) throw new NtFileException(NtStatus.FileIsADirectory, path);
            if (write && !w.Discard && disposition != CreateDisposition.Open) HostFileSystem.CreateDirectory(w.Path);
            if (!HostFileSystem.DirectoryExists(host) && !(write && w.Discard)) throw new NtFileException(NtStatus.ObjectPathNotFound, path);
            return new DirectoryFileObject(host, path);
        }
        var mode = disposition switch
        {
            CreateDisposition.Create => FileMode.CreateNew,
            CreateDisposition.Supersede or CreateDisposition.OverwriteIf => FileMode.Create,
            CreateDisposition.Overwrite => FileMode.Truncate,
            CreateDisposition.OpenIf => FileMode.OpenOrCreate,
            _ => FileMode.Open
        };
        var sharing = (FileShare)((uint)share & 7);
        FileStream? rs = null, ws = null;
        try
        {
            if (write && !w.Discard && read && string.Equals(r.Path, w.Path, StringComparison.OrdinalIgnoreCase))
                rs = ws = HostFileSystem.Open(w.Path, mode, FileAccess.ReadWrite, sharing);
            else
            {
                if (read || !write) rs = HostFileSystem.Open(r.Path, FileMode.Open, FileAccess.Read, sharing);
                if (write && !w.Discard) ws = HostFileSystem.Open(w.Path, mode, FileAccess.Write, sharing);
            }
            return new MappedFileObject(path, rs, ws, write && w.Discard, () => Delete(path, current));
        }
        catch { rs?.Dispose(); if (!ReferenceEquals(rs, ws)) ws?.Dispose(); throw; }
    }

    public IEnumerable<HostFileInfo> Enumerate(string path, string current)
    {
        var host = MapPath(path, current, FileOperation.Enum);
        var resolved = ResolvePath(path, current);
        var relative = Path.GetRelativePath(resolved.Drive.HostDirectory, resolved.Path);
        var names = (HostFileSystem.DirectoryExists(host) ? HostFileSystem.EnumerateInfos(host).Select(i => i.Name) : Enumerable.Empty<string>())
            .Concat(resolved.Drive.MapTable?.Children(relative == "." ? "" : relative) ?? []).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            HostFileInfo? info = null;
            try
            {
                var guest = GetFullPath(path, current).TrimEnd('\\') + "\\" + name;
                _ = MapPath(guest, current, FileOperation.Enum);
                var target = MapPath(guest, current);
                if (HostFileSystem.FileExists(target) || HostFileSystem.DirectoryExists(target)) info = HostFileSystem.GetInfo(target) with { FullName = Path.Combine(host, name) };
                else if (resolved.Drive.MapTable?.Children(relative == "." ? name : relative + "\\" + name).Any() == true)
                    info = new(Path.Combine(host, name), FileAttributes.Directory, 0, DateTime.UnixEpoch, DateTime.UnixEpoch, DateTime.UnixEpoch);
            }
            catch (NtFileException) { }
            if (info is not null) yield return info;
        }
    }

    public string MapNativePath(string path, string current, bool read, bool write)
    {
        var r = read ? ResolveAccess(path, current, FileOperation.Read) : default;
        var w = write ? ResolveAccess(path, current, FileOperation.Write) : default;
        if (w.Discard || (read && write && !string.Equals(r.Path, w.Path, StringComparison.OrdinalIgnoreCase)))
            throw new NtFileException(NtStatus.InvalidDeviceRequest, "native CRT handles cannot represent split or discarded I/O");
        return write ? w.Path : r.Path;
    }

    public bool Exists(string ntPath, string currentDirectory = "\\")
    {
        try { return HostFileSystem.FileExists(MapPath(ntPath, currentDirectory)); }
        catch (NtFileException) { return false; }
    }

    public void Delete(string ntPath, string currentDirectory = "\\")
    {
        if (!AllowWrites) throw new NtFileException(NtStatus.AccessDenied, "the sandbox is read-only");
        _ = MapPath(ntPath, currentDirectory, FileOperation.Delete);
        var host = MapPath(ntPath, currentDirectory, FileOperation.Write);
        if (HostFileSystem.DirectoryExists(host)) HostFileSystem.DeleteDirectory(host);
        else if (HostFileSystem.FileExists(host)) HostFileSystem.DeleteFile(host);
        else throw new NtFileException(NtStatus.ObjectNameNotFound, ntPath);
    }
}

public sealed class NtFileException : Exception
{
    public uint Status { get; }
    public NtFileException(uint status, string message) : base(message) => Status = status;
}
