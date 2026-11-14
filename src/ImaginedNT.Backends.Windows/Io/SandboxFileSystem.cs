using ImaginedNT.Core.Diagnostics;
using ImaginedNT.Core.Nt;
using ImaginedNT.Core.Security;

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
    private readonly Func<AccessToken?>? _token;
    private readonly Dictionary<char, VirtualDrive> _drives = new();

    public SandboxFileSystem(string root, bool allowWrites = true, TraceLog? trace = null, int pid = 0,
        IReadOnlyDictionary<char, VirtualDriveOptions>? drives = null, Func<AccessToken?>? token = null)
    {
        AllowWrites = allowWrites;
        _trace = trace;
        _pid = pid;
        _token = token;
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

    public uint TryMapPath(string path, string current, FileOperation operation, out string host)
    {
        var status = TryResolveAccess(path, current, operation, out var access);
        host = status == NtStatus.Success ? access.Path : "";
        return status;
    }

    public string MapMetadataPath(string path, string currentDirectory = "\\")
    {
        var status = TryMapMetadataPath(path, currentDirectory, out var host);
        if (status != NtStatus.Success) throw new NtFileException(status, path);
        return host;
    }

    public uint TryMapMetadataPath(string path, string currentDirectory, out string host)
    {
        var resolved = ResolvePath(path, currentDirectory);
        var operation = FileOperation.Read;
        if (resolved.Drive.MapTable is { } table)
        {
            var relative = Path.GetRelativePath(resolved.Drive.HostDirectory, resolved.Path);
            var (rule, _) = table.Resolve(relative == "." ? "" : relative, FileOperation.Read, out var excluded, _token?.Invoke());
            if (excluded || !rule.Allowed) operation = FileOperation.Enum;
        }
        return TryMapPath(path, currentDirectory, operation, out host);
    }

    public string MapImagePath(string host)
    {
        var status = TryMapImagePath(host, out var mapped);
        if (status != NtStatus.Success) throw new NtFileException(status, host);
        return mapped;
    }

    public uint TryMapImagePath(string host, out string mapped)
    {
        host = Path.GetFullPath(host);
        mapped = "";
        foreach (var drive in _drives.Values)
        {
            var relative = Path.GetRelativePath(drive.HostDirectory, host);
            if (relative == ".." || relative.StartsWith("..\\") || Path.IsPathRooted(relative)) continue;
            var guest = drive.RootPath + (relative == "." ? "" : relative);
            var status = TryMapPath(guest, drive.RootPath, FileOperation.Execute, out _);
            if (status != NtStatus.Success) return status;
            return TryMapPath(guest, drive.RootPath, FileOperation.Read, out mapped);
        }
        mapped = host;
        return NtStatus.Success;
    }

    public bool HasMapTables => _drives.Values.Any(d => d.MapTable is not null);

    public (string Path, bool Discard) ResolveAccess(string path, string current, FileOperation operation, int depth = 0)
    {
        var status = TryResolveAccess(path, current, operation, out var result, depth);
        if (status != NtStatus.Success) throw new NtFileException(status, $"{operation} failed: {path}");
        return result;
    }

    public uint TryResolveAccess(string path, string current, FileOperation operation,
        out (string Path, bool Discard) result, int depth = 0)
    {
        result = default;
        if (depth > 32) return NtStatus.AccessDenied;
        var resolved = ResolvePath(path, current);
        var table = resolved.Drive.MapTable;
        FileRule? rule = null;
        var suffix = "";
        var excludedDirectory = false;
        if (table is not null)
        {
            var relative = Path.GetRelativePath(resolved.Drive.HostDirectory, resolved.Path);
            if (relative == ".") relative = "";
            var token = _token?.Invoke();
            (rule, suffix) = table.Resolve(relative, operation, out var excluded, token);
            if (excluded && (operation != FileOperation.Enum || !table.HasVisibleDescendants(relative, token)))
                return NtStatus.ObjectNameNotFound;
            excludedDirectory = excluded;
            // Enumeration uses the read mapping, but only requires enum permission.
            if (operation == FileOperation.Enum)
            {
                var mapped = table.Resolve(relative, FileOperation.Read, token);
                rule = mapped.Rule with { Allowed = rule.Allowed };
                suffix = mapped.Suffix;
            }
        }
        if ((operation is FileOperation.Write or FileOperation.Delete) && (!AllowWrites || table?.Readonly == true))
            return NtStatus.AccessDenied;
        if (rule is null)
        {
            result = (resolved.Path, false);
            return NtStatus.Success;
        }
        if (!rule.Allowed)
        {
            _trace?.Emit(TraceKind.Security, $"denied {operation}: {path} user={_token?.Invoke()?.User.Name ?? "<none>"}", _pid);
            return NtStatus.AccessDenied;
        }
        result = (resolved.Path, rule.Discard);
        if (rule.Discard) return NtStatus.Success;
        if (rule.Target is not null)
        {
            var target = suffix.Length == 0 ? rule.Target : rule.Target.TrimEnd('\\', '/') + "\\" + suffix;
            if (rule.Virtual)
            {
                var status = TryResolveAccess(target, resolved.Drive.RootPath, operation, out result, depth + 1);
                if (status != NtStatus.Success) return status;
            }
            else result = (Path.GetFullPath(target), false);
        }
        if (excludedDirectory && HostFileSystem.FileExists(result.Path))
            return NtStatus.ObjectNameNotFound;
        return NtStatus.Success;
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
        var status = TryCreateOrOpen(ntPath, desiredAccess, share, disposition, options, currentDirectory, out var file);
        if (status != NtStatus.Success) throw new NtFileException(status, ntPath);
        return file!;
    }

    public uint TryCreateOrOpen(string ntPath, uint desiredAccess, ShareAccess share, CreateDisposition disposition,
        CreateOptions options, string currentDirectory, out FileObject? file)
    {
        file = null;
        try
        {
            if ((desiredAccess & (NtAccess.Delete | NtAccess.GenericAll)) != 0 || (options & CreateOptions.DeleteOnClose) != 0)
            {
                var status = TryMapPath(ntPath, currentDirectory, FileOperation.Delete, out _);
                if (status != NtStatus.Success) return status;
            }
            uint openStatus;
            if (GetDrive(ntPath, currentDirectory).MapTable is not null)
            {
                var read = (desiredAccess & (NtAccess.FileReadData | NtAccess.FileExecute | NtAccess.GenericRead | NtAccess.GenericExecute | NtAccess.GenericAll)) != 0;
                var write = (desiredAccess & (NtAccess.FileWriteData | NtAccess.FileAppendData | (uint)NtFileAccess.WriteAttributes | (uint)NtFileAccess.WriteExtendedAttributes | NtAccess.GenericWrite | NtAccess.GenericAll)) != 0;
                openStatus = OpenMapped(GetFullPath(ntPath, currentDirectory), desiredAccess, share, disposition, options, currentDirectory, read, write, out file);
            }
            else openStatus = OpenUnmapped(ntPath, desiredAccess, share, disposition, options, currentDirectory, out file);
            if (openStatus == NtStatus.Success && (options & CreateOptions.DeleteOnClose) != 0) file!.DeleteOnClose = true;
            return openStatus;
        }
        catch (NtFileException ex) { return ex.Status; }
        catch (UnauthorizedAccessException) { return NtStatus.AccessDenied; }
        catch (DirectoryNotFoundException) { return NtStatus.ObjectPathNotFound; }
        catch (FileNotFoundException) { return NtStatus.ObjectNameNotFound; }
        catch (IOException) { return NtStatus.SharingViolation; }
    }

    private uint OpenUnmapped(string ntPath, uint desiredAccess, ShareAccess share, CreateDisposition disposition,
        CreateOptions options, string currentDirectory, out FileObject? file)
    {
        file = null;
        ntPath = GetFullPath(ntPath, currentDirectory);
        var hostPath = ResolvePath(ntPath, currentDirectory).Path;
        var isDirectoryRequest = (options & CreateOptions.DirectoryFile) != 0;
        var existingDirectory = HostFileSystem.DirectoryExists(hostPath);
        var existingFile = HostFileSystem.FileExists(hostPath);
        var wantsWrite = (desiredAccess & (NtAccess.FileWriteData | NtAccess.FileAppendData | NtAccess.GenericWrite | NtAccess.GenericAll)) != 0;
        var wantsRead = (desiredAccess & (NtAccess.FileReadData | NtAccess.FileExecute | NtAccess.GenericRead | NtAccess.GenericExecute | NtAccess.GenericAll)) != 0;

        if ((wantsWrite || disposition != CreateDisposition.Open) && !AllowWrites)
            return NtStatus.AccessDenied;

        if (existingDirectory && !isDirectoryRequest && (options & CreateOptions.NonDirectoryFile) != 0)
            return NtStatus.FileIsADirectory;

        if (isDirectoryRequest)
        {
            if ((disposition is CreateDisposition.Open or CreateDisposition.OpenIf) && !existingDirectory)
                return NtStatus.ObjectNameNotFound;
            if (disposition is CreateDisposition.Create or CreateDisposition.OverwriteIf)
                HostFileSystem.CreateDirectory(hostPath);
            if (!HostFileSystem.DirectoryExists(hostPath))
                return NtStatus.ObjectNameNotFound;
            file = new DirectoryFileObject(hostPath, ntPath);
            return NtStatus.Success;
        }

        if ((disposition is CreateDisposition.Open or CreateDisposition.Overwrite) && !existingFile)
            return existingDirectory ? NtStatus.FileIsADirectory : NtStatus.ObjectNameNotFound;

        if ((disposition is CreateDisposition.Create) && existingFile)
            return NtStatus.ObjectNameCollision;

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
            return NtStatus.ObjectNameNotFound;

        var shareMode = FileShare.ReadWrite | FileShare.Delete;
        if ((share & ShareAccess.Read) == 0) shareMode &= ~FileShare.Read;
        if ((share & ShareAccess.Write) == 0) shareMode &= ~FileShare.Write;
        if ((share & ShareAccess.Delete) == 0) shareMode &= ~FileShare.Delete;

        var stream = HostFileSystem.Open(hostPath, fileMode, access, shareMode, 4096, System.IO.FileOptions.None);
        file = new DiskFileObject(hostPath, ntPath, stream, desiredAccess);
        _trace?.Emit(TraceKind.File, $"open '{ntPath}' -> {hostPath} mode={fileMode} access={access} share={shareMode}", _pid);
        return NtStatus.Success;
    }

    private uint OpenMapped(string path, uint access, ShareAccess share, CreateDisposition disposition,
        CreateOptions options, string current, bool read, bool write, out FileObject? file)
    {
        file = null;
        write |= disposition != CreateDisposition.Open;
        uint status;
        if ((access & (NtAccess.FileExecute | NtAccess.GenericExecute | NtAccess.GenericAll)) != 0)
        {
            status = TryMapPath(path, current, FileOperation.Execute, out _);
            if (status != NtStatus.Success) return status;
        }
        (string Path, bool Discard) r = default;
        if (read || !write)
        {
            status = TryResolveAccess(path, current, (options & CreateOptions.DirectoryFile) != 0 ? FileOperation.Enum : FileOperation.Read, out r);
            if (status != NtStatus.Success)
            {
                if (write || status is not (NtStatus.ObjectNameNotFound or NtStatus.AccessDenied)) return status;
                var enumStatus = TryResolveAccess(path, current, FileOperation.Enum, out r);
                if (enumStatus != NtStatus.Success) return enumStatus;
                if (!HostFileSystem.DirectoryExists(r.Path)) return status;
            }
        }
        (string Path, bool Discard) w = default;
        if (write)
        {
            status = TryResolveAccess(path, current, FileOperation.Write, out w);
            if (status != NtStatus.Success) return status;
        }
        var host = read || !write ? r.Path : w.Path;
        if ((options & CreateOptions.DirectoryFile) != 0 || HostFileSystem.DirectoryExists(host))
        {
            status = TryMapPath(path, current, FileOperation.Enum, out _);
            if (status != NtStatus.Success) return status;
            if ((options & CreateOptions.NonDirectoryFile) != 0) return NtStatus.FileIsADirectory;
            if (write && !w.Discard && disposition != CreateDisposition.Open) HostFileSystem.CreateDirectory(w.Path);
            if (!HostFileSystem.DirectoryExists(host) && !(write && w.Discard)) return NtStatus.ObjectPathNotFound;
            file = new DirectoryFileObject(host, path);
            return NtStatus.Success;
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
            file = new MappedFileObject(path, rs, ws, write && w.Discard, () => Delete(path, current));
            return NtStatus.Success;
        }
        catch { rs?.Dispose(); if (!ReferenceEquals(rs, ws)) ws?.Dispose(); throw; }
    }

    public IEnumerable<HostFileInfo> Enumerate(string path, string current)
    {
        if (TryMapPath(path, current, FileOperation.Enum, out var host) != NtStatus.Success) yield break;
        var resolved = ResolvePath(path, current);
        var relative = Path.GetRelativePath(resolved.Drive.HostDirectory, resolved.Path);
        var names = (HostFileSystem.DirectoryExists(host) ? HostFileSystem.EnumerateInfos(host).Select(i => i.Name) : Enumerable.Empty<string>())
            .Concat(resolved.Drive.MapTable?.Children(relative == "." ? "" : relative, _token?.Invoke()) ?? []).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            HostFileInfo? info = null;
            try
            {
                var guest = GetFullPath(path, current).TrimEnd('\\') + "\\" + name;
                if (TryMapPath(guest, current, FileOperation.Enum, out var target) != NtStatus.Success) continue;
                if (HostFileSystem.FileExists(target) || HostFileSystem.DirectoryExists(target)) info = HostFileSystem.GetInfo(target) with { FullName = Path.Combine(host, name) };
                else if (resolved.Drive.MapTable?.Children(relative == "." ? name : relative + "\\" + name, _token?.Invoke()).Any() == true)
                    info = new(Path.Combine(host, name), FileAttributes.Directory, 0, DateTime.UnixEpoch, DateTime.UnixEpoch, DateTime.UnixEpoch);
            }
            catch (NtFileException) { }
            if (info is not null) yield return info;
        }
    }

    public string MapNativePath(string path, string current, bool read, bool write)
    {
        var status = TryMapNativePath(path, current, read, write, out var host);
        if (status != NtStatus.Success) throw new NtFileException(status, path);
        return host;
    }

    public uint TryMapNativePath(string path, string current, bool read, bool write, out string host)
    {
        host = "";
        (string Path, bool Discard) r = default, w = default;
        if (read)
        {
            var status = TryResolveAccess(path, current, FileOperation.Read, out r);
            if (status != NtStatus.Success) return status;
        }
        if (write)
        {
            var status = TryResolveAccess(path, current, FileOperation.Write, out w);
            if (status != NtStatus.Success) return status;
        }
        if (w.Discard || (read && write && !string.Equals(r.Path, w.Path, StringComparison.OrdinalIgnoreCase)))
            return NtStatus.InvalidDeviceRequest;
        host = write ? w.Path : r.Path;
        return NtStatus.Success;
    }

    public bool Exists(string ntPath, string currentDirectory = "\\")
    {
        try { return TryMapPath(ntPath, currentDirectory, FileOperation.Read, out var host) == NtStatus.Success && HostFileSystem.FileExists(host); }
        catch (NtFileException) { return false; }
    }

    public void Delete(string ntPath, string currentDirectory = "\\")
    {
        var status = TryDelete(ntPath, currentDirectory);
        if (status != NtStatus.Success) throw new NtFileException(status, ntPath);
    }

    public uint TryDelete(string ntPath, string currentDirectory)
    {
        if (!AllowWrites) return NtStatus.AccessDenied;
        var status = TryMapPath(ntPath, currentDirectory, FileOperation.Delete, out _);
        if (status != NtStatus.Success) return status;
        status = TryMapPath(ntPath, currentDirectory, FileOperation.Write, out var host);
        if (status != NtStatus.Success) return status;
        if (HostFileSystem.DirectoryExists(host)) HostFileSystem.DeleteDirectory(host);
        else if (HostFileSystem.FileExists(host)) HostFileSystem.DeleteFile(host);
        else return NtStatus.ObjectNameNotFound;
        return NtStatus.Success;
    }
}

public sealed class NtFileException : Exception
{
    public uint Status { get; }
    public NtFileException(uint status, string message) : base(message) => Status = status;
}
