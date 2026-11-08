using System.Text;
using ImaginedNT.Core.Io;
using ImaginedNT.Core.Nt;
using ImaginedNT.Core.Objects;
using static ImaginedNT.Apis.ApiHelpers;

namespace ImaginedNT.Core.Runtime;

public static partial class FileApis
{
    private const uint VolumeFlags = 0x00000002 | 0x00000004 | 0x00000040;

    private static string? ReadVolumePath(ApiCallContext ctx, bool ansi, int arg = 0) =>
        ctx.Arg(arg) == 0 ? null : ansi ? ctx.ReadAnsiString(ctx.Arg(arg)) : ctx.ReadUnicodeString(ctx.Arg(arg));

    private static VirtualDrive ResolveVolume(ApiCallContext ctx, string? path) =>
        ctx.Process.FileSystem.GetDrive(path ?? "\\", ctx.Process.CurrentDirectory);

    private static VirtualDrive? VolumeFromHandle(ApiCallContext ctx)
    {
        if (!ctx.TryGetFile(ctx.Arg(0), out var file) || file is not (DiskFileObject or DirectoryFileObject)) return null;
        return ResolveVolume(ctx, file.Path);
    }

    private static bool WriteVolumeString(ApiCallContext ctx, ulong buffer, uint capacity, string value, bool ansi)
    {
        if (buffer == 0) return true;
        if (capacity <= value.Length) return false;
        if (ansi) WriteAnsi(ctx, buffer, (int)Math.Min(capacity, int.MaxValue), value);
        else WriteUtf16(ctx, buffer, (int)Math.Min(capacity, int.MaxValue), value);
        return true;
    }

    [VirtualApi("kernel32.dll", "GetVolumeInformationW", ApiCategory.File)]
    private static ulong GetVolumeInformationW(ApiCallContext ctx) => GetVolumeInformation(ctx, false);

    [VirtualApi("kernel32.dll", "GetVolumeInformationA", ApiCategory.File)]
    private static ulong GetVolumeInformationA(ApiCallContext ctx) => GetVolumeInformation(ctx, true);

    [VirtualApi("kernel32.dll", "GetVolumeInformationByHandleW", ApiCategory.File)]
    private static ulong GetVolumeInformationByHandleW(ApiCallContext ctx) => GetVolumeInformation(ctx, false, true);

    private static ulong GetVolumeInformation(ApiCallContext ctx, bool ansi, bool byHandle = false)
    {
        try
        {
            var drive = byHandle ? VolumeFromHandle(ctx) : ResolveVolume(ctx, ReadVolumePath(ctx, ansi));
            if (drive is null) return BoolResult(ctx, false, Win32Error.InvalidHandle);
            if (!WriteVolumeString(ctx, ctx.Arg(1), ctx.Arg32(2), drive.Label, ansi) ||
                !WriteVolumeString(ctx, ctx.Arg(6), ctx.Arg32(7), "NTFS", ansi))
                return BoolResult(ctx, false, 234);
            if (ctx.Arg(3) != 0) ctx.Memory.Write(ctx.Arg(3), drive.SerialNumber);
            if (ctx.Arg(4) != 0) ctx.Memory.Write(ctx.Arg(4), 255u);
            if (ctx.Arg(5) != 0) ctx.Memory.Write(ctx.Arg(5), VolumeFlags | (ctx.Process.FileSystem.AllowWrites ? 0u : 0x80000u));
            return 1;
        }
        catch (NtFileException ex) { return BoolResult(ctx, false, Win32FromStatus(ex.Status)); }
    }

    [VirtualApi("kernel32.dll", "GetDiskFreeSpaceExW", ApiCategory.File)]
    private static ulong GetDiskFreeSpaceExW(ApiCallContext ctx) => GetDiskFreeSpace(ctx, false, true);

    [VirtualApi("kernel32.dll", "GetDiskFreeSpaceExA", ApiCategory.File)]
    private static ulong GetDiskFreeSpaceExA(ApiCallContext ctx) => GetDiskFreeSpace(ctx, true, true);

    [VirtualApi("kernel32.dll", "GetDiskFreeSpaceW", ApiCategory.File)]
    private static ulong GetDiskFreeSpaceW(ApiCallContext ctx) => GetDiskFreeSpace(ctx, false, false);

    [VirtualApi("kernel32.dll", "GetDiskFreeSpaceA", ApiCategory.File)]
    private static ulong GetDiskFreeSpaceA(ApiCallContext ctx) => GetDiskFreeSpace(ctx, true, false);

    private static ulong GetDiskFreeSpace(ApiCallContext ctx, bool ansi, bool extended)
    {
        try
        {
            var drive = ResolveVolume(ctx, ReadVolumePath(ctx, ansi));
            if (extended)
            {
                if (ctx.Arg(1) != 0) ctx.Memory.Write(ctx.Arg(1), drive.FreeBytes);
                if (ctx.Arg(2) != 0) ctx.Memory.Write(ctx.Arg(2), drive.TotalBytes);
                if (ctx.Arg(3) != 0) ctx.Memory.Write(ctx.Arg(3), drive.FreeBytes);
            }
            else
            {
                if (ctx.Arg(1) != 0) ctx.Memory.Write(ctx.Arg(1), 8u);
                if (ctx.Arg(2) != 0) ctx.Memory.Write(ctx.Arg(2), 512u);
                if (ctx.Arg(3) != 0) ctx.Memory.Write(ctx.Arg(3), (uint)(drive.FreeBytes / 4096));
                if (ctx.Arg(4) != 0) ctx.Memory.Write(ctx.Arg(4), (uint)(drive.TotalBytes / 4096));
            }
            return 1;
        }
        catch (NtFileException ex) { return BoolResult(ctx, false, Win32FromStatus(ex.Status)); }
    }

    [VirtualApi("kernel32.dll", "GetLogicalDrives", ApiCategory.File)]
    private static ulong GetLogicalDrives(ApiCallContext ctx)
    {
        uint mask = 0;
        foreach (var drive in ctx.Process.FileSystem.Drives) mask |= 1u << (drive.Letter - 'A');
        return mask;
    }

    [VirtualApi("kernel32.dll", "GetLogicalDriveStringsW", ApiCategory.File)]
    private static ulong GetLogicalDriveStringsW(ApiCallContext ctx) => GetLogicalDriveStrings(ctx, false);

    [VirtualApi("kernel32.dll", "GetLogicalDriveStringsA", ApiCategory.File)]
    private static ulong GetLogicalDriveStringsA(ApiCallContext ctx) => GetLogicalDriveStrings(ctx, true);

    private static ulong GetLogicalDriveStrings(ApiCallContext ctx, bool ansi)
    {
        var value = string.Concat(ctx.Process.FileSystem.Drives.Select(d => d.RootPath + "\0"));
        if (ctx.Arg32(0) <= value.Length) return (ulong)value.Length + 1;
        if (ctx.Arg(1) == 0) return BoolResult(ctx, false, Win32Error.InvalidParameter);
        WriteVolumeString(ctx, ctx.Arg(1), ctx.Arg32(0), value, ansi);
        return (ulong)value.Length;
    }

    [VirtualApi("kernel32.dll", "GetDriveTypeW", ApiCategory.File)]
    private static ulong GetDriveTypeW(ApiCallContext ctx) => GetDriveType(ctx, false);

    [VirtualApi("kernel32.dll", "GetDriveTypeA", ApiCategory.File)]
    private static ulong GetDriveTypeA(ApiCallContext ctx) => GetDriveType(ctx, true);

    private static ulong GetDriveType(ApiCallContext ctx, bool ansi)
    {
        try { ResolveVolume(ctx, ReadVolumePath(ctx, ansi)); return 3; }
        catch (NtFileException) { return 1; }
    }

    [VirtualApi("kernel32.dll", "GetVolumePathNameW", ApiCategory.File)]
    private static ulong GetVolumePathNameW(ApiCallContext ctx) => GetVolumePathName(ctx, false);

    [VirtualApi("kernel32.dll", "GetVolumePathNameA", ApiCategory.File)]
    private static ulong GetVolumePathNameA(ApiCallContext ctx) => GetVolumePathName(ctx, true);

    private static ulong GetVolumePathName(ApiCallContext ctx, bool ansi)
    {
        if (ctx.Arg(0) == 0 || ctx.Arg(1) == 0) return BoolResult(ctx, false, Win32Error.InvalidParameter);
        try
        {
            return BoolResult(ctx, WriteVolumeString(ctx, ctx.Arg(1), ctx.Arg32(2),
                ResolveVolume(ctx, ReadVolumePath(ctx, ansi)).RootPath, ansi), 234);
        }
        catch (NtFileException ex) { return BoolResult(ctx, false, Win32FromStatus(ex.Status)); }
    }

    [VirtualApi("kernel32.dll", "QueryDosDeviceW", ApiCategory.File)]
    private static ulong QueryDosDeviceW(ApiCallContext ctx) => QueryDosDevice(ctx, false);

    [VirtualApi("kernel32.dll", "QueryDosDeviceA", ApiCategory.File)]
    private static ulong QueryDosDeviceA(ApiCallContext ctx) => QueryDosDevice(ctx, true);

    private static ulong QueryDosDevice(ApiCallContext ctx, bool ansi)
    {
        var name = ReadVolumePath(ctx, ansi);
        var drives = ctx.Process.FileSystem.Drives;
        string value;
        if (name is null) value = string.Concat(drives.Select(d => $"{d.Letter}:\0"));
        else
        {
            var drive = drives.FirstOrDefault(d => name.Equals($"{d.Letter}:", StringComparison.OrdinalIgnoreCase));
            if (drive is null) return BoolResult(ctx, false, Win32Error.FileNotFound);
            value = drive.DevicePath + "\0";
        }
        if (ctx.Arg(1) == 0) return BoolResult(ctx, false, Win32Error.InvalidParameter);
        if (!WriteVolumeString(ctx, ctx.Arg(1), ctx.Arg32(2), value, ansi))
            return BoolResult(ctx, false, Win32Error.InsufficientBuffer);
        return (ulong)value.Length + 1;
    }

    [VirtualApi("kernel32.dll", "GetVolumeNameForVolumeMountPointW", ApiCategory.File)]
    private static ulong GetVolumeNameForVolumeMountPointW(ApiCallContext ctx) => GetVolumeNameForVolumeMountPoint(ctx, false);

    [VirtualApi("kernel32.dll", "GetVolumeNameForVolumeMountPointA", ApiCategory.File)]
    private static ulong GetVolumeNameForVolumeMountPointA(ApiCallContext ctx) => GetVolumeNameForVolumeMountPoint(ctx, true);

    private static ulong GetVolumeNameForVolumeMountPoint(ApiCallContext ctx, bool ansi)
    {
        if (ctx.Arg(0) == 0 || ctx.Arg(1) == 0) return BoolResult(ctx, false, Win32Error.InvalidParameter);
        try
        {
            var path = ReadVolumePath(ctx, ansi)!;
            var drive = ResolveVolume(ctx, path);
            if (!ctx.Process.FileSystem.GetFullPath(path, ctx.Process.CurrentDirectory).Equals(drive.RootPath, StringComparison.OrdinalIgnoreCase))
                return BoolResult(ctx, false, Win32Error.PathNotFound);
            return BoolResult(ctx, WriteVolumeString(ctx, ctx.Arg(1), ctx.Arg32(2), drive.VolumeName, ansi), 234);
        }
        catch (NtFileException ex) { return BoolResult(ctx, false, Win32FromStatus(ex.Status)); }
    }

    [VirtualApi("kernel32.dll", "GetVolumePathNamesForVolumeNameW", ApiCategory.File)]
    private static ulong GetVolumePathNamesForVolumeNameW(ApiCallContext ctx) => GetVolumePathNamesForVolumeName(ctx, false);

    [VirtualApi("kernel32.dll", "GetVolumePathNamesForVolumeNameA", ApiCategory.File)]
    private static ulong GetVolumePathNamesForVolumeNameA(ApiCallContext ctx) => GetVolumePathNamesForVolumeName(ctx, true);

    private static ulong GetVolumePathNamesForVolumeName(ApiCallContext ctx, bool ansi)
    {
        var name = ReadVolumePath(ctx, ansi);
        var drive = ctx.Process.FileSystem.Drives.FirstOrDefault(d => d.VolumeName.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (drive is null) return BoolResult(ctx, false, Win32Error.FileNotFound);
        var value = drive.RootPath + "\0";
        if (ctx.Arg(3) != 0) ctx.Memory.Write(ctx.Arg(3), (uint)value.Length + 1);
        if (ctx.Arg32(2) <= value.Length) return BoolResult(ctx, false, 234);
        if (ctx.Arg(1) == 0) return BoolResult(ctx, false, Win32Error.InvalidParameter);
        WriteVolumeString(ctx, ctx.Arg(1), ctx.Arg32(2), value, ansi);
        return 1;
    }

    private sealed class VolumeSearch : NtObject
    {
        public override ObjectType ObjectType => ObjectType.Unknown;
        public required string[] Names { get; init; }
        public int Index { get; set; } = 1;
    }

    [VirtualApi("kernel32.dll", "FindFirstVolumeW", ApiCategory.File)]
    private static ulong FindFirstVolumeW(ApiCallContext ctx) => FindFirstVolume(ctx, false);

    [VirtualApi("kernel32.dll", "FindFirstVolumeA", ApiCategory.File)]
    private static ulong FindFirstVolumeA(ApiCallContext ctx) => FindFirstVolume(ctx, true);

    private static ulong FindFirstVolume(ApiCallContext ctx, bool ansi)
    {
        if (ctx.Arg(0) == 0) return FailInvalidHandle(ctx, Win32Error.InvalidParameter);
        var names = ctx.Process.FileSystem.Drives.Select(d => d.VolumeName).ToArray();
        if (!WriteVolumeString(ctx, ctx.Arg(0), ctx.Arg32(1), names[0], ansi))
            return FailInvalidHandle(ctx, Win32Error.InsufficientBuffer);
        var search = new VolumeSearch { Names = names };
        var handle = ctx.Handles.Create(search);
        search.Release();
        return handle;
    }

    [VirtualApi("kernel32.dll", "FindNextVolumeW", ApiCategory.File)]
    private static ulong FindNextVolumeW(ApiCallContext ctx) => FindNextVolume(ctx, false);

    [VirtualApi("kernel32.dll", "FindNextVolumeA", ApiCategory.File)]
    private static ulong FindNextVolumeA(ApiCallContext ctx) => FindNextVolume(ctx, true);

    private static ulong FindNextVolume(ApiCallContext ctx, bool ansi)
    {
        if (!ctx.Handles.TryGet(ctx.Arg(0), out var obj) || obj is not VolumeSearch search)
            return BoolResult(ctx, false, Win32Error.InvalidHandle);
        lock (search)
        {
            if (search.Index >= search.Names.Length) return BoolResult(ctx, false, Win32Error.NoMoreFiles);
            if (ctx.Arg(1) == 0) return BoolResult(ctx, false, Win32Error.InvalidParameter);
            if (!WriteVolumeString(ctx, ctx.Arg(1), ctx.Arg32(2), search.Names[search.Index], ansi))
                return BoolResult(ctx, false, Win32Error.InsufficientBuffer);
            search.Index++;
            return 1;
        }
    }

    [VirtualApi("kernel32.dll", "FindVolumeClose", ApiCategory.File)]
    private static ulong FindVolumeClose(ApiCallContext ctx)
    {
        if (!ctx.Handles.TryGet(ctx.Arg(0), out var obj) || obj is not VolumeSearch)
            return BoolResult(ctx, false, Win32Error.InvalidHandle);
        return BoolResult(ctx, ctx.Handles.Close(ctx.Arg(0)) == NtStatus.Success, Win32Error.InvalidHandle);
    }

    [VirtualApi("ntdll.dll", "NtQueryVolumeInformationFile", ApiCategory.File, "ntdll.dll!ZwQueryVolumeInformationFile")]
    private static ulong NtQueryVolumeInformationFile(ApiCallContext ctx)
    {
        var drive = VolumeFromHandle(ctx);
        if (drive is null) return FailIoStatus(ctx, ctx.Arg(1), NtStatus.InvalidHandle);
        var label = Encoding.Unicode.GetBytes(drive.Label);
        byte[] data;
        var textOffset = 0;
        switch (ctx.Arg32(4))
        {
            case 1: // FILE_FS_VOLUME_INFORMATION
                data = new byte[18 + label.Length];
                BitConverter.TryWriteBytes(data.AsSpan(8), drive.SerialNumber);
                BitConverter.TryWriteBytes(data.AsSpan(12), (uint)label.Length);
                label.CopyTo(data, 18);
                textOffset = 18;
                break;
            case 3: // FILE_FS_SIZE_INFORMATION
                data = new byte[24];
                BitConverter.TryWriteBytes(data.AsSpan(0), drive.TotalBytes / 4096);
                BitConverter.TryWriteBytes(data.AsSpan(8), drive.FreeBytes / 4096);
                BitConverter.TryWriteBytes(data.AsSpan(16), 8u);
                BitConverter.TryWriteBytes(data.AsSpan(20), 512u);
                break;
            case 4: // FILE_FS_DEVICE_INFORMATION
                data = new byte[8];
                BitConverter.TryWriteBytes(data.AsSpan(0), 7u);
                break;
            case 5: // FILE_FS_ATTRIBUTE_INFORMATION
                data = new byte[20];
                BitConverter.TryWriteBytes(data.AsSpan(0), VolumeFlags | (ctx.Process.FileSystem.AllowWrites ? 0u : 0x80000u));
                BitConverter.TryWriteBytes(data.AsSpan(4), 255u);
                BitConverter.TryWriteBytes(data.AsSpan(8), 8u);
                Encoding.Unicode.GetBytes("NTFS").CopyTo(data, 12);
                textOffset = 12;
                break;
            case 7: // FILE_FS_FULL_SIZE_INFORMATION
                data = new byte[32];
                BitConverter.TryWriteBytes(data.AsSpan(0), drive.TotalBytes / 4096);
                BitConverter.TryWriteBytes(data.AsSpan(8), drive.FreeBytes / 4096);
                BitConverter.TryWriteBytes(data.AsSpan(16), drive.FreeBytes / 4096);
                BitConverter.TryWriteBytes(data.AsSpan(24), 8u);
                BitConverter.TryWriteBytes(data.AsSpan(28), 512u);
                break;
            case 11: // FILE_FS_SECTOR_SIZE_INFORMATION
                data = new byte[28];
                for (var i = 0; i < 4; i++) BitConverter.TryWriteBytes(data.AsSpan(i * 4), 512u);
                BitConverter.TryWriteBytes(data.AsSpan(16), 3u);
                break;
            default:
                return FailIoStatus(ctx, ctx.Arg(1), NtStatus.InvalidInfoClass);
        }
        var length = ctx.Arg32(3);
        if (ctx.Arg(2) == 0 || length < (textOffset == 0 ? data.Length : textOffset))
            return FailIoStatus(ctx, ctx.Arg(1), NtStatus.BufferTooSmall);
        var written = (int)Math.Min(length, (uint)data.Length);
        ctx.Memory.WriteBytes(ctx.Arg(2), data.AsSpan(0, written));
        var status = written < data.Length ? 0x80000005u : NtStatus.Success;
        WriteNtStatus(ctx, ctx.Arg(1), status, (ulong)written);
        return status;
    }
}
