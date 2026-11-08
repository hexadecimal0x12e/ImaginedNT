#include <windows.h>
#include <intrin.h>
#include <stdlib.h>

#pragma intrinsic(__fastfail)

extern void __cdecl terminate(void);

#define FAST_FAIL_STATUS 0xC0000409u
#define RAISE_FAIL_FAST_STATUS 0xC0000602u
#define FATAL_APP_EXIT_STATUS 0x40000015u
#define UNEXPECTED_RETURN 92u

typedef void (__cdecl *VoidFunction)(void);
typedef void (WINAPI *RaiseFunction)(PEXCEPTION_RECORD, PCONTEXT, DWORD);
typedef void (WINAPI *CodeFunction)(DWORD);
typedef void (WINAPI *ReportFunction)(DWORD, PEXCEPTION_RECORD, PCONTEXT);
typedef void (__cdecl *WatsonFunction)(const wchar_t *, const wchar_t *, const wchar_t *, unsigned int, ULONG_PTR);

typedef struct TestCase
{
    const char *name;
    DWORD expected;
    VoidFunction trigger;
    BOOL worker;
} TestCase;

static WCHAR g_image[MAX_PATH];
static WCHAR g_command[MAX_PATH + 80];
static volatile LONG g_marker;
static volatile LONG *g_parentMarker;

static void Out(const char *text)
{
    DWORD length = 0;
    DWORD written;
    while (text[length]) length++;
    WriteFile(GetStdHandle(STD_OUTPUT_HANDLE), text, length, &written, 0);
}

static void OutHex(DWORD value)
{
    char text[11] = "0x00000000";
    for (int i = 0; i < 8; i++) text[9 - i] = "0123456789ABCDEF"[(value >> (i * 4)) & 15];
    Out(text);
}

static void Clear(void *buffer, DWORD length)
{
    volatile BYTE *p = (volatile BYTE *)buffer;
    while (length--) *p++ = 0;
}

static BOOL Equal(const char *left, const char *right)
{
    while (*left && *left == *right) { left++; right++; }
    return *left == *right;
}

static BOOL ReadArgument(const char **cursor, char *text, int capacity)
{
    const char *p = *cursor;
    int length = 0;
    BOOL quoted = FALSE;
    while (*p == ' ' || *p == '\t') p++;
    BOOL found = *p != 0;
    while (*p && (quoted || (*p != ' ' && *p != '\t')))
    {
        if (*p == '"') quoted = !quoted;
        else if (length < capacity - 1) text[length++] = *p;
        p++;
    }
    text[length] = 0;
    *cursor = p;
    return found;
}

static FARPROC Resolve(LPCWSTR module, const char *name)
{
    HMODULE handle = GetModuleHandleW(module);
    FARPROC function = handle ? GetProcAddress(handle, name) : 0;
    if (!function)
    {
        Out("failfast: missing export ");
        Out(name);
        Out("\r\n");
        ExitProcess(91);
    }
    return function;
}

static ULONG_PTR ReadHex(const char *text)
{
    ULONG_PTR value = 0;
    for (; *text; text++)
    {
        int digit = *text >= '0' && *text <= '9' ? *text - '0' :
            *text >= 'A' && *text <= 'F' ? *text - 'A' + 10 : -1;
        if (digit < 0) return 0;
        value = (value << 4) | digit;
    }
    return value;
}

static void Int29(void) { __fastfail(FAST_FAIL_FATAL_APP_EXIT); }
static void GsCookie(void) { __fastfail(FAST_FAIL_STACK_COOKIE_CHECK_FAILURE); }

// Volatile function pointers preserve the check for an unexpected return from a noreturn API.
static void Raise(void)
{
    RaiseFunction volatile f = RaiseFailFastException;
    f(0, 0, 0);
}

static void RaiseRecord(void)
{
    EXCEPTION_RECORD record;
    Clear(&record, sizeof(record));
    record.ExceptionCode = FAST_FAIL_STATUS;
    record.ExceptionFlags = EXCEPTION_NONCONTINUABLE;
    record.NumberParameters = 1;
    record.ExceptionInformation[0] = FAST_FAIL_STACK_COOKIE_CHECK_FAILURE;
    RaiseFunction volatile f = RaiseFailFastException;
    f(&record, 0, 0);
}

static void RaiseDynamic(void)
{
    ((RaiseFunction)Resolve(L"kernel32.dll", "RaiseFailFastException"))(0, 0, 0);
}

static void TriggerRtlFailFast(void)
{
    ((CodeFunction)Resolve(L"ntdll.dll", "RtlFailFast"))(FAST_FAIL_FATAL_APP_EXIT);
}

static void TriggerRtlFailFast2(void)
{
    ((RaiseFunction)Resolve(L"ntdll.dll", "RtlFailFast2"))(0, 0, 0);
}

static void CriticalFailure(void)
{
    ((ReportFunction)Resolve(L"ntdll.dll", "RtlReportCriticalFailure"))(FAST_FAIL_STATUS, 0, 0);
}

static void FatalFailure(void)
{
    ((ReportFunction)Resolve(L"ntdll.dll", "RtlReportFatalFailure"))(FAST_FAIL_STATUS, 0, 0);
}

static void FatalAppA(void)
{
    void (WINAPI *volatile f)(UINT, LPCSTR) = FatalAppExitA;
    f(0, "failfast sample");
}

static void FatalAppW(void)
{
    void (WINAPI *volatile f)(UINT, LPCWSTR) = FatalAppExitW;
    f(0, L"failfast sample");
}

static void Abort(void) { VoidFunction volatile f = abort; f(); }
static void Terminate(void) { VoidFunction volatile f = terminate; f(); }
static void InvalidParameter(void) { VoidFunction volatile f = _invalid_parameter_noinfo_noreturn; f(); }

static void Watson(void)
{
    WatsonFunction volatile f = _invoke_watson;
    f(L"failfast sample", L"Watson", L"failfast.c", 1, 0);
}

static void DllAttach(void)
{
    if (!LoadLibraryW(L"failfastdll.dll")) ExitProcess(93);
}

static void DllInt29(void)
{
    HMODULE module = LoadLibraryW(L"failfastdll.dll");
    CodeFunction f = module ? (CodeFunction)GetProcAddress(module, "TriggerInt29") : 0;
    if (!f) ExitProcess(93);
    f(FAST_FAIL_FATAL_APP_EXIT);
}

static void DllRaise(void)
{
    HMODULE module = LoadLibraryW(L"failfastdll.dll");
    VoidFunction f = module ? (VoidFunction)GetProcAddress(module, "TriggerRaise") : 0;
    if (!f) ExitProcess(93);
    f();
}

static const TestCase g_cases[] =
{
    { "int29",             FAST_FAIL_STATUS,       Int29,            FALSE },
    { "gs-cookie",         FAST_FAIL_STATUS,       GsCookie,         FALSE },
    { "raise",             RAISE_FAIL_FAST_STATUS, Raise,            FALSE },
    { "raise-record",      FAST_FAIL_STATUS,       RaiseRecord,      FALSE },
    { "raise-dynamic",     RAISE_FAIL_FAST_STATUS, RaiseDynamic,     FALSE },
    { "rtl",               FAST_FAIL_STATUS,       TriggerRtlFailFast,  FALSE },
    { "rtl2",              RAISE_FAIL_FAST_STATUS, TriggerRtlFailFast2, FALSE },
    { "critical",          FAST_FAIL_STATUS,       CriticalFailure,  FALSE },
    { "fatal",             FAST_FAIL_STATUS,       FatalFailure,     FALSE },
    { "fatal-app-a",       FATAL_APP_EXIT_STATUS,  FatalAppA,        FALSE },
    { "fatal-app-w",       FATAL_APP_EXIT_STATUS,  FatalAppW,        FALSE },
    { "abort",             FAST_FAIL_STATUS,       Abort,            FALSE },
    { "terminate",         FAST_FAIL_STATUS,       Terminate,        FALSE },
    { "invalid-parameter", FAST_FAIL_STATUS,       InvalidParameter, FALSE },
    { "watson",            FAST_FAIL_STATUS,       Watson,           FALSE },
    { "dll-attach",        FAST_FAIL_STATUS,       DllAttach,        FALSE },
    { "dll-int29",         FAST_FAIL_STATUS,       DllInt29,         FALSE },
    { "dll-raise",         RAISE_FAIL_FAST_STATUS, DllRaise,         FALSE },
    { "thread-int29",      FAST_FAIL_STATUS,       Int29,            TRUE  },
    { "thread-raise",      RAISE_FAIL_FAST_STATUS, Raise,            TRUE  },
    { "thread-abort",      FAST_FAIL_STATUS,       Abort,            TRUE  }
};

static void Trigger(const TestCase *test)
{
    VoidFunction volatile f = test->trigger;
    if (g_parentMarker) *g_parentMarker = 1;
    f();
    if (g_parentMarker) *g_parentMarker = 2;
    Out("FAIL: fatal operation returned: ");
    Out(test->name);
    Out("\r\n");
    ExitProcess(UNEXPECTED_RETURN);
}

static DWORD WINAPI Worker(LPVOID parameter)
{
    Trigger((const TestCase *)parameter);
    return UNEXPECTED_RETURN;
}

static void ProbeManagedException(void)
{
    DWORD written;
    // ReadProcessMemory rejects this buffer, exercising a caught CLR exception while VEH is armed.
    WriteFile(GetStdHandle(STD_OUTPUT_HANDLE), (LPCVOID)(ULONG_PTR)1, 1, &written, 0);
}

static BOOL RunCase(const TestCase *test)
{
    int pos = 0;
    g_command[pos++] = L'"';
    for (int i = 0; g_image[i]; i++) g_command[pos++] = g_image[i];
    for (int i = 0; L"\" --child "[i]; i++) g_command[pos++] = L"\" --child "[i];
    for (int i = 0; test->name[i]; i++) g_command[pos++] = (WCHAR)test->name[i];
    // Both guests share the host address space; observe a return before another API can stop the child.
    g_marker = 0;
    g_command[pos++] = L' ';
    for (int i = 15; i >= 0; i--)
        g_command[pos++] = L"0123456789ABCDEF"[((ULONG_PTR)&g_marker >> (i * 4)) & 15];
    g_command[pos] = 0;

    STARTUPINFOW startup;
    PROCESS_INFORMATION child;
    Clear(&startup, sizeof(startup));
    Clear(&child, sizeof(child));
    startup.cb = sizeof(startup);
    if (!CreateProcessW(g_image, g_command, 0, 0, FALSE, 0, 0, 0, &startup, &child))
    {
        DWORD error = GetLastError();
        Out("FAIL: ");
        Out(test->name);
        Out(" CreateProcessW error=");
        OutHex(error);
        Out("\r\n");
        return FALSE;
    }

    DWORD code = 0;
    DWORD processWait = WaitForSingleObject(child.hProcess, 10000);
    DWORD threadWait = WaitForSingleObject(child.hThread, processWait == WAIT_OBJECT_0 ? 10000 : 0);
    BOOL gotCode = GetExitCodeProcess(child.hProcess, &code);
    BOOL passed = processWait == WAIT_OBJECT_0 && threadWait == WAIT_OBJECT_0 && gotCode &&
        code == test->expected && g_marker == 1;
    CloseHandle(child.hThread);
    CloseHandle(child.hProcess);
    Out(passed ? "PASS: " : "FAIL: ");
    Out(test->name);
    Out(" exit=");
    OutHex(code);
    if (!passed)
    {
        Out(" expected="); OutHex(test->expected);
        Out(" processWait="); OutHex(processWait);
        Out(" threadWait="); OutHex(threadWait);
        Out(" marker="); OutHex((DWORD)g_marker);
    }
    Out("\r\n");
    return passed;
}

void NtEntry(void)
{
    char name[64];
    const char *command = GetCommandLineA();
    ReadArgument(&command, name, sizeof(name));
    BOOL hasName = ReadArgument(&command, name, sizeof(name));
    BOOL child = hasName && Equal(name, "--child");
    if (child) hasName = ReadArgument(&command, name, sizeof(name));

    if (child)
    {
        char marker[32];
        if (ReadArgument(&command, marker, sizeof(marker))) g_parentMarker = (volatile LONG *)ReadHex(marker);
        for (UINT i = 0; hasName && i < ARRAYSIZE(g_cases); i++)
        {
            if (!Equal(name, g_cases[i].name)) continue;
            Out("failfast: triggering ");
            Out(name);
            Out("\r\n");
            if (!g_cases[i].worker) Trigger(&g_cases[i]);
            HANDLE thread = CreateThread(0, 0, Worker, (LPVOID)&g_cases[i], 0, 0);
            if (!thread) ExitProcess(94);
            WaitForSingleObject(thread, 10000);
            CloseHandle(thread);
            ExitProcess(UNEXPECTED_RETURN);
        }
        ExitProcess(2);
    }

    DWORD length = GetModuleFileNameW(0, g_image, ARRAYSIZE(g_image));
    if (!length || length >= ARRAYSIZE(g_image)) ExitProcess(3);
    BOOL all = !hasName || Equal(name, "all");
    BOOL selected = FALSE;
    ProbeManagedException();
    for (UINT i = 0; i < ARRAYSIZE(g_cases); i++)
    {
        if (!all && !Equal(name, g_cases[i].name)) continue;
        selected = TRUE;
        if (!RunCase(&g_cases[i])) ExitProcess(1);
    }
    if (!selected)
    {
        Out("usage: failfast.exe [all|case] (cases:");
        for (UINT i = 0; i < ARRAYSIZE(g_cases); i++) { Out(" "); Out(g_cases[i].name); }
        Out(")\r\n");
        ExitProcess(2);
    }
    ProbeManagedException();
    Out("failfast: all selected cases passed; parent runtime survived\r\n");
    ExitProcess(0);
}
