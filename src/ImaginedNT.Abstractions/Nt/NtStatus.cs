namespace ImaginedNT.Core.Nt;

/// <summary>
/// NTSTATUS values used by the managed NT layer.  Keeping these as constants (rather than an enum)
/// mirrors the NT ABI where every operation returns a 32 bit NTSTATUS regardless of the operation.
/// </summary>
public static class NtStatus
{
    public const uint Success = 0x00000000;
    public const uint Wait0 = 0x00000000;
    public const uint Wait1 = 0x00000001;
    public const uint Wait2 = 0x00000002;
    public const uint Wait63 = 0x0000003F;
    public const uint WaitTimeout = 0x00000102;
    public const uint Pending = 0x00000103;
    public const uint MoreEntries = 0x00000105;
    public const uint NotAllAssigned = 0x0000010A;
    public const uint SomeNotMapped = 0x0000010B;

    public const uint NotImplemented = 0xC0000002;
    public const uint InvalidInfoClass = 0xC0000003;
    public const uint InfoLengthMismatch = 0xC0000004;
    public const uint AccessViolation = 0xC0000005;
    public const uint InPageError = 0xC0000006;
    public const uint InvalidHandle = 0xC0000008;
    public const uint InvalidCid = 0xC000000B;
    public const uint InvalidParameter = 0xC000000D;
    public const uint NoMemory = 0xC0000017;
    public const uint AlreadyCommitted = 0xC0000018;
    public const uint AccessDenied = 0xC0000022;
    public const uint BufferTooSmall = 0xC0000023;
    public const uint ObjectTypeMismatch = 0xC0000024;
    public const uint NotCommitted = 0xC0000026;
    public const uint InvalidImageFormat = 0xC000007B;
    public const uint InsufficientResources = 0xC000009A;
    public const uint InvalidLockSequence = 0xC00001AD;
    public const uint FileLockConflict = 0xC0000054;
    public const uint CantWait = 0xC00000DB;
    public const uint DllNotFound = 0xC0000135;
    public const uint EntrypointNotFound = 0xC0000139;
    public const uint StackBufferOverrun = 0xC0000409;
    public const uint FailFastException = 0xC0000602;
    public const uint FatalAppExit = 0x40000015;
    public const uint ProcNotFound = 0xC000010F;
    public const uint ThreadIsTerminating = 0xC000014A;
    public const uint ProcessIsTerminating = 0xC000014A;
    public const uint UnhandledException = 0xC0000022;
    public const uint NotSupported = 0xC00000BB;
    public const uint IllegalFunction = 0xC00000AF;
    public const uint EndOfFile = 0xC0000011;
    public const uint ObjectNameInvalid = 0xC0000033;
    public const uint ObjectNameNotFound = 0xC0000034;
    public const uint ObjectNameCollision = 0xC0000035;
    public const uint ObjectPathNotFound = 0xC000003A;
    public const uint ObjectPathSyntaxBad = 0xC000003B;
    public const uint ObjectNameTooLong = 0xC0000106;
    public const uint HandleNotClosable = 0xC0000235;
    public const uint DeletePending = 0xC0000056;
    public const uint DirectoryNotEmpty = 0xC0000101;
    public const uint FileIsADirectory = 0xC00000BA;
    public const uint NotADirectory = 0xC0000103;
    public const uint Timeout = 0xC000012A;
    public const uint Cancelled = 0xC0000120;
    public const uint Alerted = 0xC00000DF;
    public const uint SectionTooBig = 0xC0000098;
    public const uint InvalidDeviceRequest = 0xC0000010;
    public const uint NoSuchFile = 0xC000000F;
    public const uint FileClosed = 0xC0000128;
    public const uint InvalidAddress = 0xC0000141;
    public const uint InvalidOwner = 0xC000005A;
    public const uint PrivilegeNotHeld = 0xC0000061;
    public const uint Retry = 0xC000022D;
    public const uint NotSameDevice = 0xC00000D4;
    public const uint MediaWriteProtected = 0xC00000A2;
    public const uint SharingViolation = 0xC0000043;
    public const uint PipeNotConnected = 0xC00000B0;
    public const uint PipeBroken = 0xC000014B;
    public const uint IoTimeout = 0xC00000B5;
    public const uint OperationAborted = 0xC0000244;
    public const uint WorkingSetLimitExceeded = 0xC0000041;
    public const uint QuotaExceeded = 0xC0000044;

    public static bool IsSuccess(uint status) => (status & 0x8000_0000u) == 0;

    public static bool IsError(uint status) => !IsSuccess(status);

    public static int ToHResult(uint status) => unchecked((int)status);

    public static string NameOf(uint status) => status switch
    {
        Success => "STATUS_SUCCESS",
        WaitTimeout => "STATUS_TIMEOUT",
        Pending => "STATUS_PENDING",
        NotImplemented => "STATUS_NOT_IMPLEMENTED",
        InvalidHandle => "STATUS_INVALID_HANDLE",
        PipeBroken => "STATUS_PIPE_BROKEN",
        InvalidParameter => "STATUS_INVALID_PARAMETER",
        AccessDenied => "STATUS_ACCESS_DENIED",
        NoMemory => "STATUS_NO_MEMORY",
        AccessViolation => "STATUS_ACCESS_VIOLATION",
        ObjectNameNotFound => "STATUS_OBJECT_NAME_NOT_FOUND",
        ObjectNameCollision => "STATUS_OBJECT_NAME_COLLISION",
        ObjectPathNotFound => "STATUS_OBJECT_PATH_NOT_FOUND",
        ObjectNameInvalid => "STATUS_OBJECT_NAME_INVALID",
        InvalidImageFormat => "STATUS_INVALID_IMAGE_FORMAT",
        DllNotFound => "STATUS_DLL_NOT_FOUND",
        EntrypointNotFound => "STATUS_ENTRYPOINT_NOT_FOUND",
        StackBufferOverrun => "STATUS_STACK_BUFFER_OVERRUN",
        FailFastException => "STATUS_FAIL_FAST_EXCEPTION",
        FatalAppExit => "STATUS_FATAL_APP_EXIT",
        BufferTooSmall => "STATUS_BUFFER_TOO_SMALL",
        InvalidInfoClass => "STATUS_INVALID_INFO_CLASS",
        InfoLengthMismatch => "STATUS_INFO_LENGTH_MISMATCH",
        EndOfFile => "STATUS_END_OF_FILE",
        NotSupported => "STATUS_NOT_SUPPORTED",
        SharingViolation => "STATUS_SHARING_VIOLATION",
        DirectoryNotEmpty => "STATUS_DIRECTORY_NOT_EMPTY",
        NotADirectory => "STATUS_NOT_A_DIRECTORY",
        FileIsADirectory => "STATUS_FILE_IS_A_DIRECTORY",
        _ => $"0x{status:X8}"
    };

    /// <summary>Best-effort mapping from a Win32 error code to an NTSTATUS.</summary>
    public static uint FromWin32(int error) => error switch
    {
        0 => Success,
        2 => ObjectNameNotFound,
        3 => ObjectPathNotFound,
        5 => AccessDenied,
        6 => InvalidHandle,
        8 => NoMemory,
        32 => SharingViolation,
        38 => EndOfFile,
        80 or 183 => ObjectNameCollision,
        87 => InvalidParameter,
        109 => PipeBroken,
        123 => ObjectNameInvalid,
        145 => DirectoryNotEmpty,
        206 => ObjectNameTooLong,
        267 => NotADirectory,
        487 => InvalidAddress,
        998 => AccessViolation,
        1453 => WorkingSetLimitExceeded,
        1455 => NoMemory,
        _ => 0xC0000000u | (uint)error
    };
}
