namespace ImaginedNT.Core.Io;

public sealed record VirtualDriveOptions(string HostDirectory)
{
    public string Label { get; init; } = "ImaginedNT";
    public uint? SerialNumber { get; init; }
    public ulong TotalBytes { get; init; } = 128UL * 1024 * 1024 * 1024;
    public ulong FreeBytes { get; init; } = 64UL * 1024 * 1024 * 1024;
}

public sealed class VirtualDrive
{
    internal VirtualDrive(char letter, VirtualDriveOptions options)
    {
        if (options.FreeBytes > options.TotalBytes || options.TotalBytes / 4096 > uint.MaxValue)
            throw new ArgumentException("invalid simulated drive capacity");
        Letter = letter;
        HostDirectory = Path.GetFullPath(options.HostDirectory);
        Label = options.Label;
        SerialNumber = options.SerialNumber ?? (0x494E5400u + (uint)(letter - 'A'));
        TotalBytes = options.TotalBytes / 4096 * 4096;
        FreeBytes = options.FreeBytes / 4096 * 4096;
    }

    public char Letter { get; }
    public string RootPath => $"{Letter}:\\";
    public string DevicePath => $@"\Device\ImaginedDrive{(Letter == 'C' ? 0 : Letter - 'A' + 1)}";
    public string VolumeName => $@"\\?\Volume{{494e5400-0000-0000-0000-{(int)Letter:000000000000}}}\";
    public string HostDirectory { get; }
    public string Label { get; }
    public uint SerialNumber { get; }
    public ulong TotalBytes { get; }
    public ulong FreeBytes { get; }
}
