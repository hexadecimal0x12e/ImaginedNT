#include <windows.h>
#define PSAPI_VERSION 2
#include <psapi.h>

__declspec(dllimport) LONG NTAPI NtQueryInformationProcess(HANDLE, ULONG, PVOID, ULONG, PULONG);

typedef struct VmCounters {
    SIZE_T PeakVirtualSize;
    SIZE_T VirtualSize;
    ULONG PageFaultCount;
    SIZE_T PeakWorkingSetSize;
    SIZE_T WorkingSetSize;
    SIZE_T QuotaPeakPagedPoolUsage;
    SIZE_T QuotaPagedPoolUsage;
    SIZE_T QuotaPeakNonPagedPoolUsage;
    SIZE_T QuotaNonPagedPoolUsage;
    SIZE_T PagefileUsage;
    SIZE_T PeakPagefileUsage;
    SIZE_T PrivateUsage;
} VmCounters;

static void Print(const char *text)
{
    DWORD length = 0;
    DWORD written;
    while (text[length]) ++length;
    while (length != 0) {
        if (!WriteFile(GetStdHandle(STD_OUTPUT_HANDLE), text, length, &written, 0) || written == 0)
            ExitProcess(7);
        text += written;
        length -= written;
    }
}

static void PrintValue(const char *label, ULONGLONG value, const char *unit)
{
    char digits[21];
    unsigned int i = sizeof(digits) - 1;
    digits[i] = 0;
    do {
        digits[--i] = '0' + (char)(value % 10);
        value /= 10;
    } while (value != 0);
    Print(label);
    Print(digits + i);
    Print(unit);
    Print("\r\n");
}

static void PrintMemory(const char *stage)
{
    MEMORYSTATUSEX environment;
    PROCESS_MEMORY_COUNTERS_EX process;
    VmCounters vm;
    LONG status;

    Print(stage);
    Print("\r\n");
    environment.dwLength = sizeof(environment);
    if (!GlobalMemoryStatusEx(&environment)) {
        PrintValue("GlobalMemoryStatusEx failed, Win32 error: ", GetLastError(), "");
        ExitProcess(6);
    }
    if (!GetProcessMemoryInfo(GetCurrentProcess(), (PROCESS_MEMORY_COUNTERS *)&process, sizeof(process))) {
        PrintValue("GetProcessMemoryInfo failed, Win32 error: ", GetLastError(), "");
        ExitProcess(6);
    }
    status = NtQueryInformationProcess(GetCurrentProcess(), 3, &vm, sizeof(vm), 0);
    if (status < 0) {
        PrintValue("NtQueryInformationProcess failed, NTSTATUS (decimal): ", (ULONG)status, "");
        ExitProcess(6);
    }

    Print("Process:\r\n");
    PrintValue("  Working set:        ", process.WorkingSetSize, " bytes");
    PrintValue("  Peak working set:   ", process.PeakWorkingSetSize, " bytes");
    PrintValue("  Private commit:     ", process.PrivateUsage, " bytes");
    PrintValue("  Peak commit:        ", process.PeakPagefileUsage, " bytes");
    PrintValue("  Virtual size:       ", vm.VirtualSize, " bytes");
    PrintValue("  Peak virtual size:  ", vm.PeakVirtualSize, " bytes");
    PrintValue("  Page faults:        ", process.PageFaultCount, "");

    Print("Environment (guest-visible totals and availability):\r\n");
    PrintValue("  Physical total:     ", environment.ullTotalPhys, " bytes");
    PrintValue("  Physical used:      ", environment.ullTotalPhys - environment.ullAvailPhys, " bytes");
    PrintValue("  Physical available: ", environment.ullAvailPhys, " bytes");
    PrintValue("  Memory load:        ", environment.dwMemoryLoad, "%");
    PrintValue("  Commit limit:       ", environment.ullTotalPageFile, " bytes");
    PrintValue("  Commit used:        ", environment.ullTotalPageFile - environment.ullAvailPageFile, " bytes");
    PrintValue("  Commit available:   ", environment.ullAvailPageFile, " bytes");
    PrintValue("  Virtual total:      ", environment.ullTotalVirtual, " bytes");
    PrintValue("  Virtual used:       ", environment.ullTotalVirtual - environment.ullAvailVirtual, " bytes");
    PrintValue("  Virtual available:  ", environment.ullAvailVirtual, " bytes");
    Print("\r\n");
}

void NtEntry(void)
{
    SYSTEM_INFO systemInfo;
    GetSystemInfo(&systemInfo);
    Print("ImaginedNT process counters track guest allocations; environment totals cover all guests.\r\n\r\n");
    PrintMemory("Before allocation");

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
    PrintMemory("After allocation and protection");

    if (!VirtualFree(memory, 0, MEM_RELEASE))
        ExitProcess(4);

    PrintMemory("After release");

    ExitProcess(committed ? 77 : 5);
}
