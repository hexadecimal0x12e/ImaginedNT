namespace ImaginedNT.Core.Nt;

[Flags]
public enum MemoryProtection : uint
{
    NoAccess = 0x01,
    ReadOnly = 0x02,
    ReadWrite = 0x04,
    WriteCopy = 0x08,
    Execute = 0x10,
    ExecuteRead = 0x20,
    ExecuteReadWrite = 0x40,
    ExecuteWriteCopy = 0x80,
    Guard = 0x100,
    NoCache = 0x200,
    WriteCombine = 0x400
}

[Flags]
public enum AllocationType : uint
{
    Commit = 0x0000_1000,
    Reserve = 0x0000_2000,
    Decommit = 0x0000_4000,
    Release = 0x0000_8000,
    Free = 0x0001_0000,
    Private = 0x0002_0000,
    Mapped = 0x0004_0000,
    Reset = 0x0008_0000,
    TopDown = 0x0010_0000,
    WriteWatch = 0x0020_0000,
    Physical = 0x0040_0000,
    LargePages = 0x2000_0000
}

[Flags]
public enum FreeType : uint
{
    Decommit = 0x0000_4000,
    Release = 0x0000_8000
}

public enum MemoryState : uint
{
    Commit = 0x1000,
    Reserve = 0x2000,
    Free = 0x1_0000
}

public enum MemoryType : uint
{
    Private = 0x2_0000,
    Mapped = 0x4_0000,
    Image = 0x100_0000
}

[Flags]
public enum NtFileAccess : uint
{
    ReadData = 0x0001,
    WriteData = 0x0002,
    AppendData = 0x0004,
    ReadExtendedAttributes = 0x0008,
    WriteExtendedAttributes = 0x0010,
    Execute = 0x0020,
    DeleteChild = 0x0040,
    ReadAttributes = 0x0080,
    WriteAttributes = 0x0100,
    Delete = 0x1_0000,
    ReadControl = 0x2_0000,
    WriteDac = 0x4_0000,
    WriteOwner = 0x8_0000,
    Synchronize = 0x10_0000,
    GenericAll = 0x1000_0000,
    GenericExecute = 0x2000_0000,
    GenericWrite = 0x4000_0000,
    GenericRead = 0x8000_0000
}

public static class NtAccess
{
    public const uint GenericRead = 0x8000_0000;
    public const uint GenericWrite = 0x4000_0000;
    public const uint GenericExecute = 0x2000_0000;
    public const uint GenericAll = 0x1000_0000;
    public const uint MaximumAllowed = 0x0200_0000;
    public const uint Delete = 0x0001_0000;
    public const uint Synchronize = 0x0010_0000;

    public const uint EventQueryState = 0x0001;
    public const uint EventModifyState = 0x0002;
    public const uint MutantQueryState = 0x0001;
    public const uint MutantModifyState = 0x0001;
    public const uint SemaphoreQueryState = 0x0001;
    public const uint SemaphoreModifyState = 0x0002;

    public const uint FileReadData = 0x0001;
    public const uint FileWriteData = 0x0002;
    public const uint FileAppendData = 0x0004;
    public const uint FileExecute = 0x0020;
    public const uint FileReadAttributes = 0x0080;
    public const uint FileWriteAttributes = 0x0100;

    public const uint ProcessTerminate = 0x0001;
    public const uint ProcessSuspendResume = 0x0002;
    public const uint ProcessVmRead = 0x0010;
    public const uint ProcessVmWrite = 0x0020;
    public const uint ProcessQueryInformation = 0x0400;
    public const uint ProcessSetInformation = 0x0200;
    public const uint ProcessCreateThread = 0x0080;

    public const uint ThreadTerminate = 0x0001;
    public const uint ThreadSuspendResume = 0x0002;
    public const uint ThreadQueryInformation = 0x0040;
    public const uint ThreadSetInformation = 0x0020;

    public static uint MapGeneric(uint desired, uint specificMask)
    {
        uint result = desired & ~(GenericRead | GenericWrite | GenericExecute | GenericAll);
        if ((desired & GenericRead) != 0) result |= specificMask & 0x00FF_FFFF | (uint)(NtFileAccess.ReadData | NtFileAccess.ReadAttributes | NtFileAccess.ReadControl | NtFileAccess.Synchronize);
        if ((desired & GenericWrite) != 0) result |= (uint)(NtFileAccess.WriteData | NtFileAccess.AppendData | NtFileAccess.WriteAttributes | NtFileAccess.ReadControl | NtFileAccess.Synchronize);
        if ((desired & GenericExecute) != 0) result |= (uint)(NtFileAccess.Execute | NtFileAccess.ReadAttributes | NtFileAccess.ReadControl | NtFileAccess.Synchronize);
        if ((desired & GenericAll) != 0) result |= 0x01FF_FFFF;
        return result;
    }
}

[Flags]
public enum ShareAccess : uint
{
    None = 0,
    Read = 1,
    Write = 2,
    Delete = 4
}

public enum CreateDisposition : uint
{
    Supersede = 0,
    Open = 1,
    Create = 2,
    OpenIf = 3,
    Overwrite = 4,
    OverwriteIf = 5
}

[Flags]
public enum CreateOptions : uint
{
    None = 0,
    DirectoryFile = 0x0000_0001,
    WriteThrough = 0x0000_0002,
    SequentialOnly = 0x0000_0004,
    NoIntermediateBuffering = 0x0000_0008,
    SynchronousIoAlert = 0x0000_0010,
    SynchronousIoNonAlert = 0x0000_0020,
    NonDirectoryFile = 0x0000_0040,
    CreateTreeConnection = 0x0000_0080,
    CompleteIfOplocked = 0x0000_0100,
    OpenForRecovery = 0x0000_0200,
    NoEaKnowledge = 0x0000_0400,
    DeleteOnClose = 0x0000_1000,
    OpenNoRecall = 0x0000_1000,
    OpenForFreeSpaceQuery = 0x0000_2000,
    OpenReparsePoint = 0x0020_0000,
    OpenNoReparsePoint = 0x0040_0000,
    OpenRequiringOplock = 0x0080_0000,
    DisableCompression = 0x0000_8000
}

[Flags]
public enum ObjectAttributesFlags : uint
{
    Inherit = 0x0002,
    Permanent = 0x0010,
    Exclusive = 0x0020,
    CaseInsensitive = 0x0040,
    OpenIf = 0x0080,
    OpenLink = 0x0100,
    KernelHandle = 0x0200,
    ForceAccessCheck = 0x0400,
    IgnoreImpersonatedDeviceMap = 0x1000,
    DoNotReparse = 0x2000
}

public enum WaitResult : uint
{
    Object0 = 0,
    Timeout = 0x102,
    Abandoned = 0x80,
    Failed = 0xFFFFFFFF
}

public enum FileInformationClass
{
    FileDirectoryInformation = 1,
    FileFullDirectoryInformation = 2,
    FileBothDirectoryInformation = 3,
    FileBasicInformation = 4,
    FileStandardInformation = 5,
    FileInternalInformation = 6,
    FileEaInformation = 7,
    FileAccessInformation = 8,
    FileNameInformation = 9,
    FileRenameInformation = 10,
    FileDispositionInformation = 13,
    FilePositionInformation = 14,
    FileFullEaInformation = 15,
    FileModeInformation = 16,
    FileAlignmentInformation = 17,
    FileAllInformation = 18,
    FileEndOfFileInformation = 20,
    FileStreamInformation = 22,
    FileVolumeInformation = 24,
    FileStandardLinkInformation = 54
}

public enum ProcessInformationClass
{
    ProcessBasicInformation = 0,
    ProcessDebugPort = 7,
    ProcessWow64Information = 26,
    ProcessImageFileName = 27,
    ProcessBreakOnTermination = 29,
    ProcessCommandLineInformation = 60
}

public static class NtFileAttributes
{
    public const uint ReadOnly = 0x0000_0001;
    public const uint Hidden = 0x0000_0002;
    public const uint System = 0x0000_0004;
    public const uint Directory = 0x0000_0010;
    public const uint Archive = 0x0000_0020;
    public const uint Normal = 0x0000_0080;
    public const uint Temporary = 0x0000_0100;
}

public static class NtProtection
{
    public const uint PageNoAccess = 0x01;
    public const uint PageReadOnly = 0x02;
    public const uint PageReadWrite = 0x04;
    public const uint PageWriteCopy = 0x08;
    public const uint PageExecute = 0x10;
    public const uint PageExecuteRead = 0x20;
    public const uint PageExecuteReadWrite = 0x40;
    public const uint PageExecuteWriteCopy = 0x80;
    public const uint PageGuard = 0x100;
    public const uint PageNoCache = 0x200;
    public const uint PageWriteCombine = 0x400;

    public static string Describe(uint protection)
    {
        if ((protection & 0xFF) == 0) return "PAGE_NOACCESS?";
        var baseProt = protection & 0xFF;
        var name = baseProt switch
        {
            0x01 => "R",
            0x02 => "R",
            0x04 => "RW",
            0x08 => "WC",
            0x10 => "X",
            0x20 => "RX",
            0x40 => "RWX",
            0x80 => "XWC",
            _ => "?"
        };
        if ((protection & PageGuard) != 0) name += "|GUARD";
        if ((protection & PageNoCache) != 0) name += "|NOCACHE";
        if ((protection & PageWriteCombine) != 0) name += "|WC";
        return name;
    }
}
