#include <windows.h>

static const char kData[] = "imagined-file";

void NtEntry(void)
{
    DWORD written = 0;
    DWORD read = 0;
    char buffer[32] = {0};

    HANDLE file = CreateFileW(L"C:\\sandbox-test.txt", GENERIC_WRITE, 0, 0, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, 0);
    if (file == INVALID_HANDLE_VALUE)
        ExitProcess(1);
    if (!WriteFile(file, kData, sizeof(kData) - 1, &written, 0) || written != sizeof(kData) - 1)
        ExitProcess(2);
    CloseHandle(file);

    file = CreateFileW(L"C:\\sandbox-test.txt", GENERIC_READ, FILE_SHARE_READ, 0, OPEN_EXISTING, 0, 0);
    if (file == INVALID_HANDLE_VALUE)
        ExitProcess(3);
    if (!ReadFile(file, buffer, sizeof(buffer) - 1, &read, 0))
        ExitProcess(4);
    CloseHandle(file);

    if (read != sizeof(kData) - 1)
        ExitProcess(5);
    for (DWORD i = 0; i < read; i++)
    {
        if (buffer[i] != kData[i])
            ExitProcess(6);
    }
    ExitProcess(66);
}
