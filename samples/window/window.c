// A CRT-free guest window used by the App's XAML DWM compositor.
//
// It imports only USER32 window-management APIs: RegisterClassExW, CreateWindowExW,
// ShowWindow, UpdateWindow, GetMessageW, TranslateMessage, DispatchMessageW,
// DefWindowProcW, SetWindowTextW, DestroyWindow and PostQuitMessage.  Nothing is painted with
// GDI; the compositor renders the caption, background and child controls with App controls and
// forwards pointer/keyboard messages back into this message loop.

#include <windows.h>

static HWND g_main;
static HWND g_status;
static int g_creatingMain;
static WCHAR g_statusText[192];

static WCHAR *CopyText(WCHAR *destination, const WCHAR *source)
{
    while (*source) *destination++ = *source++;
    *destination = 0;
    return destination;
}

static WCHAR *AppendDecimal(WCHAR *destination, unsigned int value)
{
    WCHAR digits[16];
    int count = 0;
    if (value == 0)
    {
        *destination++ = L'0';
        *destination = 0;
        return destination;
    }
    while (value != 0 && count < 16)
    {
        digits[count++] = (WCHAR)(L'0' + (value % 10));
        value /= 10;
    }
    while (count > 0) *destination++ = digits[--count];
    *destination = 0;
    return destination;
}

static WCHAR *AppendHex(WCHAR *destination, unsigned int value)
{
    static const WCHAR hex[] = L"0123456789ABCDEF";
    WCHAR digits[16];
    int count = 0;
    if (value == 0)
    {
        *destination++ = L'0';
        *destination = 0;
        return destination;
    }
    while (value != 0 && count < 16)
    {
        digits[count++] = hex[value & 0xF];
        value >>= 4;
    }
    while (count > 0) *destination++ = digits[--count];
    *destination = 0;
    return destination;
}

static void SetStatus(const WCHAR *prefix, int value, int useHex)
{
    WCHAR *end = CopyText(g_statusText, prefix);
    if (value >= 0)
    {
        if (useHex)
        {
            end = CopyText(end, L"0x");
            end = AppendHex(end, (unsigned int)value);
        }
        else
        {
            end = AppendDecimal(end, (unsigned int)value);
        }
        (void)end;
    }
    SetWindowTextW(g_status, g_statusText);
}

static LRESULT CALLBACK WndProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam)
{
    switch (message)
    {
        case WM_CREATE:
            if (g_creatingMain)
            {
                g_status = CreateWindowExW(0, L"STATIC", L"客户端由 App 控件合成；这里没有 GDI。",
                                           WS_CHILD | WS_VISIBLE, 24, 72, 680, 28, hwnd, (HMENU)100, 0, 0);
                CreateWindowExW(0, L"STATIC", L"鼠标点击、键盘输入、标题栏拖动都会转发到消息队列。",
                                WS_CHILD | WS_VISIBLE, 24, 106, 680, 28, hwnd, (HMENU)102, 0, 0);
                CreateWindowExW(0, L"BUTTON", L"点击我（WM_COMMAND）", WS_CHILD | WS_VISIBLE | WS_TABSTOP,
                                24, 158, 360, 46, hwnd, (HMENU)101, 0, 0);
            }
            return 0;

        case WM_COMMAND:
            if (LOWORD(wParam) == 101 && HIWORD(wParam) == BN_CLICKED)
            {
                SetWindowTextW(g_main, L"按钮已点击 — WM_COMMAND 已送达");
                SetStatus(L"收到 WM_COMMAND，控件 ID=", LOWORD(wParam), 0);
            }
            return 0;

        case WM_LBUTTONDOWN:
        {
            int x = (short)LOWORD(lParam);
            int y = (short)HIWORD(lParam);
            WCHAR *end = CopyText(g_statusText, L"WM_LBUTTONDOWN 客户区坐标 x=");
            end = AppendDecimal(end, (unsigned int)x);
            end = CopyText(end, L", y=");
            AppendDecimal(end, (unsigned int)y);
            SetWindowTextW(g_main, L"鼠标按下 — WM_LBUTTONDOWN 已送达");
            SetWindowTextW(g_status, g_statusText);
            return 0;
        }

        case WM_KEYDOWN:
            SetWindowTextW(g_main, L"键盘按下 — WM_KEYDOWN 已送达");
            SetStatus(L"WM_KEYDOWN 虚拟键 VK=", (int)wParam, 1);
            return 0;

        case WM_CHAR:
            SetWindowTextW(g_main, L"字符输入 — WM_CHAR 已送达");
            if (wParam >= 32 && wParam < 127) SetStatus(L"WM_CHAR 字符代码=", (int)wParam, 0);
            else SetStatus(L"WM_CHAR 控制字符代码=", (int)wParam, 0);
            return 0;

        case WM_CLOSE:
            DestroyWindow(hwnd);
            return 0;

        case WM_DESTROY:
            if (hwnd == g_main) PostQuitMessage(0);
            return 0;

        default:
            return DefWindowProcW(hwnd, message, wParam, lParam);
    }
}

void NtEntry(void)
{
    WNDCLASSEXW windowClass;
    MSG message;

    windowClass.cbSize = sizeof(WNDCLASSEXW);
    windowClass.style = CS_HREDRAW | CS_VREDRAW;
    windowClass.lpfnWndProc = WndProc;
    windowClass.cbClsExtra = 0;
    windowClass.cbWndExtra = 0;
    windowClass.hInstance = 0;
    windowClass.hIcon = 0;
    windowClass.hCursor = LoadCursorW(0, MAKEINTRESOURCEW(32512));
    windowClass.hbrBackground = (HBRUSH)(COLOR_WINDOW + 1);
    windowClass.lpszMenuName = 0;
    windowClass.lpszClassName = L"ImaginedNTWindowDemo";
    windowClass.hIconSm = 0;

    if (!RegisterClassExW(&windowClass)) return;

    g_creatingMain = 1;
    g_main = CreateWindowExW(0, L"ImaginedNTWindowDemo", L"ImaginedNT 窗口示例",
                             WS_OVERLAPPEDWINDOW | WS_VISIBLE, 72, 48, 760, 520, 0, 0, 0, 0);
    g_creatingMain = 0;
    if (!g_main) return;

    // A second top-level window demonstrates session-wide composition and z-order.
    CreateWindowExW(0, L"ImaginedNTWindowDemo", L"第二个顶层窗口 — DWM 合成",
                    WS_POPUP | WS_CAPTION | WS_SYSMENU | WS_VISIBLE, 560, 310, 430, 230, 0, 0, 0, 0);

    ShowWindow(g_main, SW_SHOW);
    UpdateWindow(g_main);

    while (GetMessageW(&message, 0, 0, 0) > 0)
    {
        TranslateMessage(&message);
        DispatchMessageW(&message);
    }
}
