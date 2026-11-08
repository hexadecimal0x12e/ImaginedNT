#include <windows.h>

static const char kMessage[] = "relocated";
static const char *volatile g_pointer = kMessage;

void NtEntry(void)
{
    ExitProcess(*g_pointer == 'r' ? 99 : 1);
}
