#include <windows.h>

void NtEntry(void)
{
    SYSTEM_INFO systemInfo;
    GetSystemInfo(&systemInfo);

    unsigned char *memory = (unsigned char *)VirtualAlloc(0, systemInfo.dwPageSize * 2, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (memory == 0)
        ExitProcess(1);
    memory[0] = 'A';
    memory[systemInfo.dwPageSize * 2 - 1] = 'Z';

    DWORD oldProtect = 0;
    if (!VirtualProtect(memory, systemInfo.dwPageSize, PAGE_READONLY, &oldProtect))
        ExitProcess(2);

    MEMORY_BASIC_INFORMATION info;
    if (VirtualQuery(memory, &info, sizeof(info)) == 0)
        ExitProcess(3);
    int committed = info.State == MEM_COMMIT && memory[0] == 'A';

    if (!VirtualFree(memory, 0, MEM_RELEASE))
        ExitProcess(4);

    ExitProcess(committed ? 77 : 5);
}
