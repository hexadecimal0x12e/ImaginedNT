#include <windows.h>

static const char kMessage[] = "Hello from ImaginedNT (managed NT layer)!\r\n";

__declspec(dllimport) DWORD __stdcall GetCurrentProcessId(void);
__declspec(dllimport) DWORD __stdcall GetCurrentThreadId(void);
__declspec(dllimport) HANDLE __stdcall GetStdHandle(DWORD);
__declspec(dllimport) BOOL __stdcall WriteFile(HANDLE, const void *, DWORD, DWORD *, void *);
__declspec(dllimport) void __stdcall ExitProcess(UINT);

void NtEntry(void)
{
    DWORD written = 0;
    HANDLE out = GetStdHandle(STD_OUTPUT_HANDLE);
    WriteFile(out, kMessage, sizeof(kMessage) - 1, &written, 0);
    ExitProcess(42);
}
