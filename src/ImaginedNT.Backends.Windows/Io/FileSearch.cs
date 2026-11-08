using ImaginedNT.Core.Objects;

namespace ImaginedNT.Core.Io;

/// <summary>One entry captured by <c>FindFirstFileW</c>; the host filesystem is snapshotted per search.</summary>
public sealed class FileSearchEntry
{
    public required string Name { get; init; }
    public required string HostPath { get; init; }
    public bool IsDirectory { get; init; }
    public long Size { get; init; }
    public uint Attributes { get; init; }
    public long CreationTimeUtc { get; init; }
    public long LastAccessTimeUtc { get; init; }
    public long LastWriteTimeUtc { get; init; }
}

/// <summary>
/// Managed equivalent of a Win32 find handle: a snapshot of a directory listing plus a cursor for
/// <c>FindNextFileW</c>.  It lives in the process handle table so <c>FindClose</c> and
/// <c>CloseHandle</c> both release it.
/// </summary>
public sealed class FileSearchObject : NtObject
{
    private readonly FileSearchEntry[] _entries;
    private int _index;

    public FileSearchObject(string pattern, FileSearchEntry[] entries)
    {
        Pattern = pattern;
        _entries = entries;
        Name = pattern;
        FullPath = pattern;
    }

    public string Pattern { get; }
    public int Count => _entries.Length;
    public int Index => _index;

    public bool TryNext(out FileSearchEntry entry)
    {
        if (_index < _entries.Length)
        {
            entry = _entries[_index++];
            return true;
        }

        entry = null!;
        return false;
    }

    public override ObjectType ObjectType => ObjectType.Unknown;
    public override string TypeName => "FileSearch";
    public override string? Info => $"find '{Pattern}' ({_entries.Length} entries, {_index} consumed)";
}
