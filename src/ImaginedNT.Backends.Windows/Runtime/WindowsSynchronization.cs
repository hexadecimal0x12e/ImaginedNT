using System.Runtime.InteropServices;

namespace ImaginedNT.Core.Runtime;

public static class WindowsSynchronization
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool InitializeCriticalSectionEx(nint section, uint spinCount, uint flags);

    [DllImport("kernel32.dll")]
    public static extern void InitializeCriticalSection(nint section);

    [DllImport("kernel32.dll")]
    public static extern void EnterCriticalSection(nint section);

    [DllImport("kernel32.dll")]
    public static extern void LeaveCriticalSection(nint section);

    [DllImport("kernel32.dll")]
    public static extern void DeleteCriticalSection(nint section);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TryEnterCriticalSection(nint section);

    [DllImport("kernel32.dll")]
    public static extern void InitializeSListHead(nint head);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool InitOnceBeginInitialize(nint once, uint flags,
        [MarshalAs(UnmanagedType.Bool)] out bool pending, out ulong context);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool InitOnceComplete(nint once, uint flags, ulong context);
}
