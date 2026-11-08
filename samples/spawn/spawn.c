#include <windows.h>

static const char kParent[] = "spawn: parent\r\n";

__declspec(dllimport) DWORD __stdcall GetCurrentProcessId(void);
__declspec(dllimport) HANDLE __stdcall GetStdHandle(DWORD);
__declspec(dllimport) BOOL __stdcall WriteFile(HANDLE, const void *, DWORD, DWORD *, void *);
__declspec(dllimport) BOOL __stdcall CreateProcessW(LPCWSTR, LPWSTR, LPSECURITY_ATTRIBUTES, LPSECURITY_ATTRIBUTES,
                                                  BOOL, DWORD, LPVOID, LPCWSTR, LPSTARTUPINFOW, LPPROCESS_INFORMATION);
__declspec(dllimport) DWORD __stdcall ResumeThread(HANDLE);
__declspec(dllimport) DWORD __stdcall WaitForSingleObject(HANDLE, DWORD);
__declspec(dllimport) BOOL __stdcall GetExitCodeProcess(HANDLE, DWORD *);
__declspec(dllimport) BOOL __stdcall CloseHandle(HANDLE);
__declspec(dllimport) void __stdcall ExitProcess(UINT);

void NtEntry(void)
{
    DWORD written = 0;
    HANDLE out = GetStdHandle(STD_OUTPUT_HANDLE);
    WriteFile(out, kParent, sizeof(kParent) - 1, &written, 0);

    STARTUPINFOW startup;
    startup.cb = (DWORD)sizeof(startup);
    startup.lpReserved = 0;
    startup.lpDesktop = 0;
    startup.lpTitle = 0;
    startup.dwX = 0;
    startup.dwY = 0;
    startup.dwXSize = 0;
    startup.dwYSize = 0;
    startup.dwXCountChars = 0;
    startup.dwYCountChars = 0;
    startup.dwFillAttribute = 0;
    startup.dwFlags = 0;
    startup.wShowWindow = 0;
    startup.cbReserved2 = 0;
    startup.lpReserved2 = 0;
    startup.hStdInput = 0;
    startup.hStdOutput = 0;
    startup.hStdError = 0;

    PROCESS_INFORMATION child;
    child.hProcess = 0;
    child.hThread = 0;
    child.dwProcessId = 0;
    child.dwThreadId = 0;

    WCHAR commandLine[] = L"child.exe";
    if (!CreateProcessW(L"child.exe", commandLine, 0, 0, FALSE, CREATE_SUSPENDED, 0, 0, &startup, &child))
        ExitProcess(1);

    if (child.hProcess == 0 || child.hThread == 0 || child.dwThreadId == 0)
        ExitProcess(3);
    if (child.dwProcessId == GetCurrentProcessId())
        ExitProcess(4);
    if (ResumeThread(child.hThread) == 0xFFFFFFFFu)
        ExitProcess(5);

    DWORD wait = WaitForSingleObject(child.hProcess, 10000);
    DWORD exitCode = 0;
    BOOL gotCode = GetExitCodeProcess(child.hProcess, &exitCode);
    CloseHandle(child.hThread);
    CloseHandle(child.hProcess);

    if (wait == WAIT_OBJECT_0 && gotCode && exitCode == 123)
        ExitProcess(101);
    ExitProcess(2);
}
