#include <windows.h>

typedef int(__stdcall *AddFunction)(int, int);

void NtEntry(void)
{
    HMODULE module = LoadLibraryW(L"helper.dll");
    if (module == 0)
        ExitProcess(1);

    AddFunction add = (AddFunction)GetProcAddress(module, "Add");
    if (add == 0)
        ExitProcess(2);

    int value = add(20, 22);
    if (GetModuleHandleW(L"helper.dll") != module)
        ExitProcess(3);
    ExitProcess(value == 42 ? 70 : 4);
}
