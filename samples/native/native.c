#include <windows.h>

void NtEntry(void)
{
    UINT codePage = GetACP();
    if (codePage == 0)
        ExitProcess(1);

    // Native-forwarded OpenProcess must fail with ERROR_INVALID_PARAMETER.
    HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, 0xFFFFFFF0);
    DWORD nativeError = GetLastError();
    if (process != 0 || nativeError != 87)
        ExitProcess(2);

    // A managed API must surface its own last error after the dispatch trace is written.
    HANDLE escaped = CreateFileW(L"C:\\..\\..\\escape.txt", GENERIC_READ, 0, 0, OPEN_EXISTING, 0, 0);
    DWORD managedError = GetLastError();
    if (escaped != INVALID_HANDLE_VALUE || managedError != 5)
        ExitProcess(3);

    ExitProcess(89);
}
