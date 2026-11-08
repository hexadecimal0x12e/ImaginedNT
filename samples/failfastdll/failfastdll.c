#include <windows.h>
#include <intrin.h>

#pragma intrinsic(__fastfail)

__declspec(dllexport) void WINAPI TriggerInt29(DWORD code)
{
    __fastfail(code);
}

__declspec(dllexport) void __cdecl TriggerRaise(void)
{
    void (WINAPI *volatile f)(PEXCEPTION_RECORD, PCONTEXT, DWORD) = RaiseFailFastException;
    f(0, 0, 0);
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved)
{
    (void)instance;
    (void)reserved;
    if (reason == DLL_PROCESS_ATTACH)
    {
        for (const char *p = GetCommandLineA(); *p; p++)
        {
            const char *left = p;
            const char *right = "--child dll-attach";
            while (*right && *left == *right) { left++; right++; }
            if (!*right) __fastfail(FAST_FAIL_FATAL_APP_EXIT);
        }
    }
    return TRUE;
}
