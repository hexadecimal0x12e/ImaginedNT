#include <windows.h>

extern NTSTATUS __stdcall NtSyscall(HANDLE handle, ULONG number);

static ULONG FindSyscallNumber(FARPROC function)
{
    unsigned char *code = (unsigned char *)function;
    for (int i = 0; i < 32; i++)
    {
        if (code[i] == 0xB8)
        {
            ULONG value = *(ULONG *)(code + i + 1);
            if (value > 0 && value < 0x4000)
                return value;
        }
    }
    return 0;
}

void NtEntry(void)
{
    HANDLE event = CreateEventW(0, TRUE, FALSE, 0);
    if (event == 0)
        ExitProcess(1);

    HMODULE ntdll = GetModuleHandleW(L"ntdll.dll");
    FARPROC ntClose = GetProcAddress(ntdll, "NtClose");
    if (ntClose == 0)
        ExitProcess(2);

    ULONG number = FindSyscallNumber(ntClose);
    if (number == 0)
        ExitProcess(3);

    NTSTATUS status = NtSyscall(event, number);
    if (status != 0)
        ExitProcess(4);

    // The handle was closed by the managed NtClose dispatcher.
    if (CloseHandle(event))
        ExitProcess(5);

    ExitProcess(80);
}
