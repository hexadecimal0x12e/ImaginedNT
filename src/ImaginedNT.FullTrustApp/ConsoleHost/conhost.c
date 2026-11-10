#include "conhost.h"
#include <intrin.h>

#pragma intrinsic(__movsb)

#define HISTORY 512
#define COLUMNS 240
#define INPUT_MAX 512
#define INPUT_BYTES ((INPUT_MAX + 2) * 4)
#define INPUT_QUEUE 8
#define SHELL_INPUT_ERROR (WM_APP + 3)

static WCHAR lines[HISTORY][COLUMNS];
static int lengths[HISTORY];
static int first, count = 1, column, columns = 100, scroll;
static WCHAR input[INPUT_MAX + 2];
static int inputLength;
static HWND window, receiver;
static HFONT font;
static int cellWidth = 9, cellHeight = 20;
static WNDCLASSEXW windowClass;
static MSG message;
static int escapeState, escapeValue;
static HANDLE shell, shellInput, shellOutput, inputThread, inputReady;
static CRITICAL_SECTION inputLock;
static volatile LONG stopping;
static struct { char bytes[INPUT_BYTES]; int length; } inputQueue[INPUT_QUEUE];
static int inputFirst, inputCount;
static char writeBuffer[INPUT_BYTES], readBuffer[4100];
static WCHAR outputText[4100];
static int readPending;
static UINT codePage;

static void Clear(void)
{
    first = column = scroll = 0;
    count = 1;
    lengths[0] = 0;
    escapeState = escapeValue = 0;
}

static int CurrentLine(void)
{
    return (first + count - 1) % HISTORY;
}

static void NewLine(void)
{
    if (count == HISTORY) first = (first + 1) % HISTORY;
    else count++;
    lengths[CurrentLine()] = 0;
    column = 0;
}

static int CellCount(WCHAR c)
{
    if ((c >= 0xDC00 && c <= 0xDFFF) || (c >= 0x0300 && c <= 0x036F)) return 0;
    if ((c >= 0x1100 && c <= 0x115F) || (c >= 0x2E80 && c <= 0xA4CF) ||
        (c >= 0xAC00 && c <= 0xD7A3) || (c >= 0xD800 && c <= 0xDBFF) ||
        (c >= 0xF900 && c <= 0xFAFF) || (c >= 0xFE10 && c <= 0xFE6F) ||
        (c >= 0xFF01 && c <= 0xFF60) || (c >= 0xFFE0 && c <= 0xFFE6)) return 2;
    return 1;
}

static void Put(WCHAR c)
{
    int row = CurrentLine(), cells = 0, i;
    for (i = 0; i < column; i++) cells += CellCount(lines[row][i]);
    if (column >= COLUMNS || (column && cells + CellCount(c) > columns) ||
        (column == COLUMNS - 1 && c >= 0xD800 && c <= 0xDBFF)) NewLine();
    row = CurrentLine();
    lines[row][column++] = c;
    if (lengths[row] < column) lengths[row] = column;
}

static void WriteText(const WCHAR *text, int length)
{
    int i;
    for (i = 0; i < length; i++)
    {
        WCHAR c = text[i];
        /* Recognize shell's clear sequence even when split across messages. */
        if (escapeState)
        {
            if (escapeState == 1)
            {
                escapeState = c == L'[' ? 2 : 0;
                escapeValue = 0;
            }
            else if (c >= L'0' && c <= L'9')
                escapeValue = escapeValue < 1000 ? escapeValue * 10 + c - L'0' : 1000;
            else if (c >= L'@' && c <= L'~')
            {
                if (c == L'J' && escapeValue == 2) Clear();
                else if (c == L'H') column = 0;
                escapeState = 0;
            }
            continue;
        }
        switch (c)
        {
            case 27: escapeState = 1; break;
            case L'\f': Clear(); break;
            case L'\r': column = 0; break;
            case L'\n': NewLine(); break;
            case L'\b': if (column > 0) column--; break;
            case L'\t':
            {
                int spaces = 8 - column % 8;
                while (spaces-- > 0) Put(L' ');
                break;
            }
            default: if (c >= L' ') Put(c); break;
        }
    }
    scroll = 0;
    InvalidateRect(window, NULL, FALSE);
}

static void ReportError(const WCHAR *action, DWORD error)
{
    WCHAR number[16];
    int n = 0;
    OutputDebugStringW(action);
    WriteText(L"\r\n[", 3);
    WriteText(action, lstrlenW(action));
    WriteText(L": ", 2);
    do { number[n++] = (WCHAR)(L'0' + error % 10); error /= 10; } while (error);
    while (n) WriteText(&number[--n], 1);
    WriteText(L"]\r\n", 3);
}

static DWORD WINAPI WriteInput(void *unused)
{
    (void)unused;
    for (;;)
    {
        WaitForSingleObject(inputReady, INFINITE);
        for (;;)
        {
            int length;
            DWORD sent, offset = 0;
            EnterCriticalSection(&inputLock);
            if (stopping || !inputCount)
            {
                LeaveCriticalSection(&inputLock);
                if (stopping) return 0;
                break;
            }
            length = inputQueue[inputFirst].length;
            __movsb((unsigned char *)writeBuffer, (const unsigned char *)inputQueue[inputFirst].bytes, (size_t)length);
            inputFirst = (inputFirst + 1) % INPUT_QUEUE;
            inputCount--;
            LeaveCriticalSection(&inputLock);
            while (offset < (DWORD)length && !stopping)
            {
                if (!WriteFile(shellInput, writeBuffer + offset, (DWORD)length - offset, &sent, NULL) || !sent)
                {
                    if (!stopping) PostMessageW(window, SHELL_INPUT_ERROR, GetLastError(), 0);
                    return 0;
                }
                offset += sent;
            }
        }
    }
}

static BOOL StartShell(void)
{
    SECURITY_ATTRIBUTES sa;
    static STARTUPINFOW si;
    static PROCESS_INFORMATION pi;
    HANDLE childInput = NULL, childOutput = NULL;
    static WCHAR command[] = L"\"C:\\Windows\\System32\\cmd.exe\" /D /Q /A";
    const WCHAR *step = L"CreatePipe(stdin)";
    DWORD error;
    sa.nLength = sizeof(sa);
    sa.lpSecurityDescriptor = NULL;
    sa.bInheritHandle = TRUE;
    codePage = GetOEMCP();
    if (!CreatePipe(&childInput, &shellInput, &sa, 65536)) goto failed;
    step = L"CreatePipe(stdout)";
    if (!CreatePipe(&shellOutput, &childOutput, &sa, 65536)) goto failed;
    step = L"SetHandleInformation(stdin)";
    if (!SetHandleInformation(shellInput, HANDLE_FLAG_INHERIT, 0)) goto failed;
    step = L"SetHandleInformation(stdout)";
    if (!SetHandleInformation(shellOutput, HANDLE_FLAG_INHERIT, 0)) goto failed;
    si.cb = sizeof(si);
    si.dwFlags = STARTF_USESTDHANDLES;
    si.hStdInput = childInput;
    si.hStdOutput = si.hStdError = childOutput;
    step = L"CreateProcessW(cmd)";
    if (!CreateProcessW(L"C:\\Windows\\System32\\cmd.exe", command, NULL, NULL, TRUE,
        CREATE_NO_WINDOW, NULL, NULL, &si, &pi)) goto failed;
    shell = pi.hProcess;
    CloseHandle(pi.hThread);
    CloseHandle(childInput);
    CloseHandle(childOutput);
    step = L"CreateEventW(input)";
    inputReady = CreateEventW(NULL, FALSE, FALSE, NULL);
    if (!inputReady) goto failed;
    step = L"CreateThread(input)";
    inputThread = CreateThread(NULL, 0, WriteInput, NULL, 0, NULL);
    if (!inputThread) goto failed;
    return TRUE;
failed:
    error = GetLastError();
    if (!shell)
    {
        if (childInput) CloseHandle(childInput);
        if (childOutput) CloseHandle(childOutput);
    }
    ReportError(step, error);
    return FALSE;
}

static void DecodeOutput(BOOL flush)
{
    int i = 0, length, n;
    while (i < readPending)
    {
        unsigned char c = (unsigned char)readBuffer[i];
        length = 1;
        if (codePage == CP_UTF8)
        {
            if (c >= 0xC2 && c <= 0xDF) length = 2;
            else if (c >= 0xE0 && c <= 0xEF) length = 3;
            else if (c >= 0xF0 && c <= 0xF4) length = 4;
        }
        else if (IsDBCSLeadByteEx(codePage, c)) length = 2;
        if (i + length > readPending && !flush) break;
        i += length;
    }
    if (i > readPending) i = readPending;
    if (!i) return;
    n = MultiByteToWideChar(codePage, 0, readBuffer, i, outputText, 4100);
    if (n) WriteText(outputText, n);
    else ReportError(L"cmd output decoding failed", GetLastError());
    readPending -= i;
    __movsb((unsigned char *)readBuffer, (const unsigned char *)readBuffer + i, (size_t)readPending);
}

static void ReadOutput(void)
{
    DWORD available, read, error;
    int rounds;
    /* Bound each timer tick so continuous output does not starve window messages. */
    for (rounds = 0; shellOutput && rounds < 4; rounds++)
    {
        if (!PeekNamedPipe(shellOutput, NULL, 0, NULL, &available, NULL))
        {
            error = GetLastError();
            if (error != ERROR_BROKEN_PIPE) ReportError(L"cmd output pipe failed", error);
            DecodeOutput(TRUE);
            CloseHandle(shellOutput);
            shellOutput = NULL;
            break;
        }
        if (!available) break;
        if (available > 4096) available = 4096;
        if (!ReadFile(shellOutput, readBuffer + readPending, available, &read, NULL))
        {
            error = GetLastError();
            if (error != ERROR_BROKEN_PIPE) ReportError(L"cmd output read failed", error);
            DecodeOutput(TRUE);
            CloseHandle(shellOutput);
            shellOutput = NULL;
            break;
        }
        readPending += (int)read;
        DecodeOutput(FALSE);
    }
    if (shell && WaitForSingleObject(shell, 0) == WAIT_OBJECT_0 && !shellOutput)
    {
        KillTimer(window, 1);
        SetWindowTextW(window, L"ImaginedNT Console - cmd exited");
    }
}

static void StopShell(void)
{
    InterlockedExchange(&stopping, 1);
    if (shell && WaitForSingleObject(shell, 0) == WAIT_TIMEOUT && !TerminateProcess(shell, 0))
        OutputDebugStringW(L"ImaginedNT conhost: cmd termination failed.\n");
    if (shellOutput) { CloseHandle(shellOutput); shellOutput = NULL; }
    if (inputReady) SetEvent(inputReady);
    if (inputThread)
    {
        CancelSynchronousIo(inputThread);
        if (WaitForSingleObject(inputThread, 1000) != WAIT_OBJECT_0)
        {
            OutputDebugStringW(L"ImaginedNT conhost: input thread did not stop.\n");
            return;
        }
        CloseHandle(inputThread);
        inputThread = NULL;
    }
    if (shellInput) { CloseHandle(shellInput); shellInput = NULL; }
    if (inputReady) { CloseHandle(inputReady); inputReady = NULL; }
    if (shell) { CloseHandle(shell); shell = NULL; }
    DeleteCriticalSection(&inputLock);
}

static void Resize(void)
{
    RECT rect;
    GetClientRect(window, &rect);
    columns = (rect.right - 16) / cellWidth;
    if (columns < 1) columns = 1;
    if (columns > COLUMNS) columns = COLUMNS;
    InvalidateRect(window, NULL, FALSE);
}

static void Paint(HWND hwnd)
{
    PAINTSTRUCT ps;
    RECT rect;
    HDC dc = BeginPaint(hwnd, &ps);
    HGDIOBJ oldFont;
    int rows, start, end, i, offset;
    SIZE size;
    if (!dc) return;
    GetClientRect(hwnd, &rect);
    FillRect(dc, &rect, (HBRUSH)GetStockObject(BLACK_BRUSH));
    oldFont = SelectObject(dc, font);
    SetBkMode(dc, TRANSPARENT);
    SetTextColor(dc, RGB(220, 225, 230));
    rows = (rect.bottom - 16) / cellHeight - 1;
    if (rows < 0) rows = 0;
    if (scroll > count - 1) scroll = count - 1;
    end = count - scroll;
    start = end > rows ? end - rows : 0;
    for (i = start; i < end; i++)
    {
        int row = (first + i) % HISTORY;
        TextOutW(dc, 8, 8 + (i - start) * cellHeight, lines[row], lengths[row]);
    }
    /* Keep the editable line visible independently of output and scrollback. */
    offset = inputLength > columns - 3 ? inputLength - (columns > 3 ? columns - 3 : 1) : 0;
    if (offset < 0) offset = 0;
    if (offset < inputLength && input[offset] >= 0xDC00 && input[offset] <= 0xDFFF) offset++;
    TextOutW(dc, 8, 8 + rows * cellHeight, L"> ", 2);
    size.cx = 0;
    GetTextExtentPoint32W(dc, input + offset, inputLength - offset, &size);
    while (offset < inputLength && size.cx > rect.right - 24 - cellWidth * 2)
    {
        offset++;
        if (offset < inputLength && input[offset] >= 0xDC00 && input[offset] <= 0xDFFF) offset++;
        GetTextExtentPoint32W(dc, input + offset, inputLength - offset, &size);
    }
    TextOutW(dc, 8 + cellWidth * 2, 8 + rows * cellHeight, input + offset, inputLength - offset);
    TextOutW(dc, 8 + cellWidth * 2 + size.cx, 8 + rows * cellHeight, L"_", 1);
    SelectObject(dc, oldFont);
    EndPaint(hwnd, &ps);
}

static void Submit(void)
{
    COPYDATASTRUCT data;
    int slot, length;
    if (!receiver)
    {
        if (!shell || WaitForSingleObject(shell, 0) != WAIT_TIMEOUT ||
            !inputThread || WaitForSingleObject(inputThread, 0) != WAIT_TIMEOUT) return;
        input[inputLength] = L'\r';
        input[inputLength + 1] = L'\n';
        EnterCriticalSection(&inputLock);
        if (inputCount == INPUT_QUEUE)
        {
            LeaveCriticalSection(&inputLock);
            ReportError(L"cmd input queue is full", ERROR_BUSY);
            return;
        }
        slot = (inputFirst + inputCount) % INPUT_QUEUE;
        length = WideCharToMultiByte(codePage, 0, input, inputLength + 2,
            inputQueue[slot].bytes, INPUT_BYTES, NULL, NULL);
        if (!length)
        {
            DWORD error = GetLastError();
            LeaveCriticalSection(&inputLock);
            ReportError(L"cmd input encoding failed", error);
            return;
        }
        inputQueue[slot].length = length;
        inputCount++;
        LeaveCriticalSection(&inputLock);
    }
    WriteText(input, inputLength);
    WriteText(L"\r\n", 2);
    input[inputLength] = L'\r';
    input[inputLength + 1] = L'\n';
    data.dwData = CONHOST_INPUT;
    data.cbData = (DWORD)((inputLength + 2) * sizeof(WCHAR));
    data.lpData = input;
    if (receiver && !SendMessageW(receiver, WM_COPYDATA, (WPARAM)window, (LPARAM)&data))
    {
        OutputDebugStringW(L"ImaginedNT conhost: input delivery failed.\n");
        WriteText(L"[input delivery failed]\r\n", 25);
    }
    inputLength = 0;
    InvalidateRect(window, NULL, FALSE);
    if (!receiver) SetEvent(inputReady);
}

static LRESULT CALLBACK WindowProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
{
    switch (msg)
    {
        case WM_TIMER:
            if (wp == 1) ReadOutput();
            return 0;
        case SHELL_INPUT_ERROR:
            ReportError(L"cmd input write failed", (DWORD)wp);
            return 0;
        case CONHOST_ATTACH:
            if (wp && ((HWND)wp == hwnd || !IsWindow((HWND)wp))) return FALSE;
            receiver = (HWND)wp;
            return TRUE;
        case CONHOST_CLEAR:
            Clear();
            InvalidateRect(hwnd, NULL, FALSE);
            return TRUE;
        case WM_COPYDATA:
        {
            const COPYDATASTRUCT *data = (const COPYDATASTRUCT *)lp;
            if (!data || data->dwData != CONHOST_OUTPUT || data->cbData > 65536 ||
                data->cbData % sizeof(WCHAR) || (data->cbData && !data->lpData)) return FALSE;
            WriteText((const WCHAR *)data->lpData, (int)(data->cbData / sizeof(WCHAR)));
            return TRUE;
        }
        case WM_CHAR:
            if (wp == L'\r') Submit();
            else if (wp == L'\b' && inputLength)
            {
                inputLength--;
                if (inputLength && input[inputLength] >= 0xDC00 && input[inputLength] <= 0xDFFF &&
                    input[inputLength - 1] >= 0xD800 && input[inputLength - 1] <= 0xDBFF) inputLength--;
            }
            else if (wp >= L' ' && wp <= 0xFFFF && inputLength < INPUT_MAX) input[inputLength++] = (WCHAR)wp;
            InvalidateRect(hwnd, NULL, FALSE);
            return 0;
        case WM_KEYDOWN:
            if (wp == VK_ESCAPE) inputLength = 0;
            InvalidateRect(hwnd, NULL, FALSE);
            return 0;
        case WM_MOUSEWHEEL:
            scroll += (short)HIWORD(wp) / WHEEL_DELTA * 3;
            if (scroll < 0) scroll = 0;
            if (scroll >= count) scroll = count - 1;
            InvalidateRect(hwnd, NULL, FALSE);
            return 0;
        case WM_SIZE:
            if (window) Resize();
            return 0;
        case WM_ERASEBKGND: return 1;
        case WM_PAINT: Paint(hwnd); return 0;
        case WM_DESTROY: PostQuitMessage(0); return 0;
    }
    return DefWindowProcW(hwnd, msg, wp, lp);
}

void NtEntry(void)
{
    HDC dc;
    HGDIOBJ oldFont;
    TEXTMETRICW metrics;
    int result;
    font = CreateFontW(-18, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
        OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY, FIXED_PITCH, L"Consolas");
    if (!font)
    {
        OutputDebugStringW(L"ImaginedNT conhost: font creation failed.\n");
        ExitProcess(1);
    }
    windowClass.cbSize = sizeof(windowClass);
    windowClass.style = CS_HREDRAW | CS_VREDRAW;
    windowClass.lpfnWndProc = WindowProc;
    windowClass.hInstance = GetModuleHandleW(NULL);
    windowClass.lpszClassName = CONHOST_CLASS;
    if (!RegisterClassExW(&windowClass))
    {
        OutputDebugStringW(L"ImaginedNT conhost: window class registration failed.\n");
        DeleteObject(font);
        ExitProcess(2);
    }
    window = CreateWindowExW(0, CONHOST_CLASS, L"ImaginedNT Console", WS_OVERLAPPEDWINDOW,
        40, 40, 960, 600, NULL, NULL, windowClass.hInstance, NULL);
    if (!window)
    {
        OutputDebugStringW(L"ImaginedNT conhost: window creation failed.\n");
        DeleteObject(font);
        ExitProcess(3);
    }
    dc = GetDC(window);
    if (dc)
    {
        oldFont = SelectObject(dc, font);
        if (GetTextMetricsW(dc, &metrics))
        {
            if (metrics.tmAveCharWidth > 0) cellWidth = metrics.tmAveCharWidth;
            if (metrics.tmHeight > 0) cellHeight = metrics.tmHeight + metrics.tmExternalLeading;
        }
        SelectObject(dc, oldFont);
        ReleaseDC(window, dc);
    }
    Resize();
    ShowWindow(window, SW_SHOW);
    UpdateWindow(window);
    InitializeCriticalSection(&inputLock);
    if (StartShell())
    {
        if (!SetTimer(window, 1, 30, NULL))
        {
            ReportError(L"cmd output timer failed", GetLastError());
            StopShell();
            DeleteObject(font);
            ExitProcess(5);
        }
    }
    else StopShell();
    while ((result = GetMessageW(&message, NULL, 0, 0)) > 0)
    {
        TranslateMessage(&message);
        DispatchMessageW(&message);
    }
    if (result < 0) OutputDebugStringW(L"ImaginedNT conhost: GetMessageW failed.\n");
    if (stopping == 0) StopShell();
    DeleteObject(font);
    ExitProcess(result < 0 ? 4 : (UINT)message.wParam);
}
