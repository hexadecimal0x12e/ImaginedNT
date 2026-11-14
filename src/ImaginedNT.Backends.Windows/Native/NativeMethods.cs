using System.Runtime.InteropServices;

namespace ImaginedNT.Core.Native;

internal static unsafe class NativeMethods
{
    public const uint MEM_COMMIT = 0x0000_1000;
    public const uint MEM_RESERVE = 0x0000_2000;
    public const uint MEM_DECOMMIT = 0x0000_4000;
    public const uint MEM_RELEASE = 0x0000_8000;
    public const uint MEM_FREE = 0x0001_0000;
    public const uint MEM_PRIVATE = 0x0002_0000;
    public const uint MEM_MAPPED = 0x0004_0000;
    public const uint MEM_IMAGE = 0x0100_0000;

    public const uint PAGE_NOACCESS = 0x01;
    public const uint PAGE_READONLY = 0x02;
    public const uint PAGE_READWRITE = 0x04;
    public const uint PAGE_WRITECOPY = 0x08;
    public const uint PAGE_EXECUTE = 0x10;
    public const uint PAGE_EXECUTE_READ = 0x20;
    public const uint PAGE_EXECUTE_READWRITE = 0x40;
    public const uint PAGE_EXECUTE_WRITECOPY = 0x80;
    public const uint PAGE_GUARD = 0x100;
    public const uint PAGE_NOCACHE = 0x200;
    public const uint PAGE_WRITECOMBINE = 0x400;

    public const uint INFINITE = 0xFFFF_FFFF;
    public const uint WAIT_OBJECT_0 = 0;
    public const uint WAIT_TIMEOUT = 0x102;
    public const uint WAIT_FAILED = 0xFFFF_FFFF;
    public const uint CREATE_SUSPENDED = 0x0000_0004;
    public const uint STILL_ACTIVE = 259;
    public const uint THREAD_TERMINATE = 0x0001;
    public const uint THREAD_SUSPEND_RESUME = 0x0002;
    public const uint THREAD_QUERY_INFORMATION = 0x0040;
    public const uint SYNCHRONIZE = 0x0010_0000;
    public const uint THREAD_ALL_ACCESS = 0x001F_FFFF;

    public const int EXCEPTION_CONTINUE_SEARCH = 0;
    public const int EXCEPTION_CONTINUE_EXECUTION = -1;
    public const int EXCEPTION_EXECUTE_HANDLER = 1;
    public const int ProcessUserShadowStackPolicy = 15;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr VirtualAlloc(IntPtr lpAddress, UIntPtr dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr HeapCreate(uint options, UIntPtr initialSize, UIntPtr maximumSize);

    [DllImport("kernel32.dll")]
    public static extern IntPtr HeapAlloc(IntPtr heap, uint flags, UIntPtr size);

    [DllImport("kernel32.dll")]
    public static extern IntPtr HeapReAlloc(IntPtr heap, uint flags, IntPtr block, UIntPtr size);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool HeapFree(IntPtr heap, uint flags, IntPtr block);

    [DllImport("kernel32.dll")]
    public static extern UIntPtr HeapSize(IntPtr heap, uint flags, IntPtr block);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool HeapDestroy(IntPtr heap);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool RtlAddFunctionTable(IntPtr table, uint count, ulong imageBase);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool RtlDeleteFunctionTable(IntPtr table);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool VirtualFree(IntPtr lpAddress, UIntPtr dwSize, uint dwFreeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool VirtualProtect(IntPtr lpAddress, UIntPtr dwSize, uint flNewProtect, out uint lpflOldProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern UIntPtr VirtualQuery(IntPtr lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer, UIntPtr dwLength);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FlushInstructionCache(IntPtr hProcess, IntPtr lpBaseAddress, UIntPtr dwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr CreateThread(IntPtr lpThreadAttributes, UIntPtr dwStackSize, IntPtr lpStartAddress, IntPtr lpParameter, uint dwCreationFlags, out uint lpThreadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenThread(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwThreadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint SuspendThread(IntPtr hThread);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint ResumeThread(IntPtr hThread);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetExitCodeThread(IntPtr hThread, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll")]
    public static extern void ExitThread(uint dwExitCode);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentProcessId();

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetProcessMitigationPolicy(IntPtr process, int policy, out uint buffer, UIntPtr length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TerminateThread(IntPtr hThread, uint dwExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CancelSynchronousIo(IntPtr hThread);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr LoadLibraryW(string lpFileName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    public static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GetProcAddress(IntPtr hModule, IntPtr ordinal);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint GetModuleFileNameW(IntPtr module, System.Text.StringBuilder fileName, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FreeLibrary(IntPtr hModule);

    [DllImport("kernel32.dll")]
    public static extern IntPtr AddVectoredExceptionHandler(uint first, IntPtr handler);

    [DllImport("kernel32.dll")]
    public static extern uint RemoveVectoredExceptionHandler(IntPtr handle);

    // Do NOT use SetLastError=true here: the .NET marshaller clears the host thread's last
    // error before calling the P/Invoke when that flag is set, which would make GetLastError
    // always return 0.
    [DllImport("kernel32.dll")]
    public static extern int GetLastError();

    [DllImport("kernel32.dll")]
    public static extern void SetLastError(int dwErrCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, void* lpBuffer, UIntPtr nSize, out UIntPtr lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, void* lpBuffer, UIntPtr nSize, out UIntPtr lpNumberOfBytesWritten);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryPerformanceCounter(out long lpPerformanceCount);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryPerformanceFrequency(out long lpFrequency);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetInformationJobObject(IntPtr hJob, int JobObjectInfoClass, void* lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public ushort Reserved;
        public UIntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    public static uint ToPageProtection(uint protection)
    {
        var p = protection & 0xFF;
        var guard = protection & 0x100;
        var result = p switch
        {
            0x01 => PAGE_NOACCESS,
            0x02 => PAGE_READONLY,
            0x04 => PAGE_READWRITE,
            0x08 => PAGE_WRITECOPY,
            0x10 => PAGE_EXECUTE,
            0x20 => PAGE_EXECUTE_READ,
            0x40 => PAGE_EXECUTE_READWRITE,
            0x80 => PAGE_EXECUTE_WRITECOPY,
            _ => PAGE_READWRITE
        };
        if (guard != 0) result |= PAGE_GUARD;
        if ((protection & 0x200) != 0) result |= PAGE_NOCACHE;
        if ((protection & 0x400) != 0) result |= PAGE_WRITECOMBINE;
        return result;
    }

    public static uint ToNtProtection(uint pageProtection)
    {
        var result = pageProtection & 0xFF;
        if ((pageProtection & PAGE_GUARD) != 0) result |= 0x100;
        if ((pageProtection & PAGE_NOCACHE) != 0) result |= 0x200;
        if ((pageProtection & PAGE_WRITECOMBINE) != 0) result |= 0x400;
        return result;
    }
}
