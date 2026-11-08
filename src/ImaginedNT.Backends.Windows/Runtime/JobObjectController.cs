using System.Runtime.InteropServices;
using ImaginedNT.Core.Native;

namespace ImaginedNT.Core.Runtime;

/// <summary>
/// An OS-enforced backstop for the managed sandbox: a job object limits the host process's
/// memory/active-process count and kills any child processes when the host exits.  This is the
/// part of isolation that does not depend on the guest honouring the managed API layer.
/// </summary>
public sealed class JobObjectController : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_PROCESS_MEMORY = 0x0000_0100;
    private const uint JOB_OBJECT_LIMIT_ACTIVE_PROCESS = 0x0000_0008;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x0000_2000;

    private IntPtr _job;
    private bool _disposed;

    public JobObjectController(ulong memoryLimitBytes, uint activeProcessLimit)
    {
        _job = NativeMethods.CreateJobObjectW(IntPtr.Zero, null);
        if (_job == IntPtr.Zero)
        {
            Error = $"CreateJobObject failed: {Marshal.GetLastWin32Error()}";
            return;
        }

        var information = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JOB_OBJECT_LIMIT_PROCESS_MEMORY | JOB_OBJECT_LIMIT_ACTIVE_PROCESS | JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
                ActiveProcessLimit = Math.Max(1, activeProcessLimit)
            },
            ProcessMemoryLimit = (UIntPtr)memoryLimitBytes
        };

        unsafe
        {
            var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            if (!NativeMethods.SetInformationJobObject(_job, JobObjectExtendedLimitInformation, &information, (uint)size))
            {
                Error = $"SetInformationJobObject failed: {Marshal.GetLastWin32Error()}";
            }

            if (!NativeMethods.AssignProcessToJobObject(_job, NativeMethods.GetCurrentProcess()))
            {
                var error = Marshal.GetLastWin32Error();
                // ERROR_ACCESS_DENIED is common when the host is already nested in a job
                // (terminals, CI runners).  Keep the handle: child-process kill-on-close is
                // still useful, and the failure is reported to the trace.
                Error = $"AssignProcessToJobObject failed: {error}";
            }
        }
    }

    public bool IsAssigned => _job != IntPtr.Zero && Error is null;
    public string? Error { get; private set; }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_job != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(_job);
            _job = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
