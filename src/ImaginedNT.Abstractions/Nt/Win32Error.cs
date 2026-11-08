namespace ImaginedNT.Core.Nt;

/// <summary>Win32 error codes returned through GetLastError/SetLastError.</summary>
public static class Win32Error
{
    public const int Success = 0;
    public const int InvalidFunction = 1;
    public const int FileNotFound = 2;
    public const int PathNotFound = 3;
    public const int TooManyOpenFiles = 4;
    public const int AccessDenied = 5;
    public const int InvalidHandle = 6;
    public const int NotEnoughMemory = 8;
    public const int InsufficientBuffer = 122;
    public const int InvalidDrive = 15;
    public const int NoMoreFiles = 18;
    public const int WriteProtect = 19;
    public const int NotReady = 21;
    public const int SharingViolation = 32;
    public const int LockViolation = 33;
    public const int HandleEof = 38;
    public const int HandleDiskFull = 39;
    public const int InvalidParameter = 87;
    public const int BrokenPipe = 109;
    public const int OpenFailed = 110;
    public const int DiskFull = 112;
    public const int SemaphoreTimeout = 121;
    public const int FilenameExcedRange = 206;
    public const int AlreadyExists = 183;
    public const int DirectoryNotEmpty = 145;
    public const int BadExeFormat = 193;
    public const int FileExists = 80;
    public const int EnvVarNotFound = 203;
    public const int NotSupported = 50;
    public const int OldWinVersion = 1150;
    public const int PrivilegeNotHeld = 1314;
    public const int CallNotImplemented = 120;
    public const int ProcessAborted = 1067;
}
