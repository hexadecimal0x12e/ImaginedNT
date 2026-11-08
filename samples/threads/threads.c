#include <windows.h>

static volatile LONG g_value;

__declspec(dllimport) void __stdcall ExitThread(DWORD);

static DWORD WINAPI Worker(LPVOID parameter)
{
    (void)parameter;
    g_value = 123;
    ExitThread(7); // exercises the managed guest-thread exit trampoline
    return 0;
}

void NtEntry(void)
{
    DWORD threadId = 0;
    DWORD exitCode = 0;
    HANDLE thread = CreateThread(0, 0, Worker, 0, 0, &threadId);
    if (thread == 0)
        ExitProcess(1);

    DWORD wait = WaitForSingleObject(thread, 5000);
    GetExitCodeThread(thread, &exitCode);
    CloseHandle(thread);

    if (wait == WAIT_OBJECT_0 && exitCode == 7 && g_value == 123)
        ExitProcess(55);
    ExitProcess(2);
}
