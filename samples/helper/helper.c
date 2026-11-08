#include <windows.h>

__declspec(dllexport) int __stdcall Add(int left, int right)
{
    return left + right;
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved)
{
    (void)instance;
    (void)reason;
    (void)reserved;
    return TRUE;
}
