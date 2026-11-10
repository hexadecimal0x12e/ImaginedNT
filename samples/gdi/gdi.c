#include <windows.h>

static int alternate;
static WNDCLASSEXW windowClass;
static MSG message;

static LRESULT CALLBACK WindowProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
{
    switch (msg)
    {
        case WM_PAINT:
        {
            PAINTSTRUCT ps;
            RECT client;
            HDC dc = BeginPaint(hwnd, &ps);
            if (!dc) return 0;
            GetClientRect(hwnd, &client);
            HDC buffer = CreateCompatibleDC(dc);
            HBITMAP bitmap = CreateCompatibleBitmap(dc, client.right > 0 ? client.right : 1,
                client.bottom > 0 ? client.bottom : 1);
            if (buffer && bitmap)
            {
                HGDIOBJ oldBitmap = SelectObject(buffer, bitmap);
                HBRUSH background = CreateSolidBrush(RGB(24, 30, 40));
                FillRect(buffer, &client, background);
                DeleteObject(background);

                HBRUSH brush = CreateSolidBrush(alternate ? RGB(240, 140, 60) : RGB(50, 140, 240));
                HPEN pen = CreatePen(PS_SOLID, 3, RGB(220, 235, 255));
                HGDIOBJ oldBrush = SelectObject(buffer, brush);
                HGDIOBJ oldPen = SelectObject(buffer, pen);
                Rectangle(buffer, 32, 72, 220, 200);
                Ellipse(buffer, 260, 72, 450, 200);
                MoveToEx(buffer, 32, 224, NULL);
                LineTo(buffer, 450, 224);
                SelectObject(buffer, oldPen);
                SelectObject(buffer, oldBrush);
                DeleteObject(pen);
                DeleteObject(brush);

                SetBkMode(buffer, TRANSPARENT);
                SetTextColor(buffer, RGB(235, 240, 250));
                RECT title = { 24, 20, client.right - 24, 60 };
                DrawTextW(buffer, L"System GDI -> offscreen DIB -> XAML", -1, &title, DT_LEFT | DT_SINGLELINE);
                TextOutW(buffer, 32, 250, L"Click to change color; Esc to close", 34);
                BitBlt(dc, 0, 0, client.right, client.bottom, buffer, 0, 0, SRCCOPY);
                SelectObject(buffer, oldBitmap);
            }
            if (bitmap) DeleteObject(bitmap);
            if (buffer) DeleteDC(buffer);
            EndPaint(hwnd, &ps);
            return 0;
        }
        case WM_LBUTTONDOWN:
            alternate = !alternate;
            InvalidateRect(hwnd, NULL, FALSE);
            return 0;
        case WM_SIZE:
            InvalidateRect(hwnd, NULL, TRUE);
            return 0;
        case WM_KEYDOWN:
            if (wp == VK_ESCAPE) DestroyWindow(hwnd);
            return 0;
        case WM_DESTROY:
            PostQuitMessage(0);
            return 0;
    }
    return DefWindowProcW(hwnd, msg, wp, lp);
}

void NtEntry(void)
{
    windowClass.cbSize = sizeof(windowClass);
    windowClass.lpfnWndProc = WindowProc;
    windowClass.hInstance = GetModuleHandleW(NULL);
    windowClass.hbrBackground = GetSysColorBrush(COLOR_WINDOW);
    windowClass.lpszClassName = L"ImaginedNT.GdiSample";
    if (!RegisterClassExW(&windowClass)) ExitProcess(1);
    HWND hwnd = CreateWindowExW(0, windowClass.lpszClassName, L"ImaginedNT - GDI",
        WS_OVERLAPPEDWINDOW, 60, 50, 560, 380, NULL, NULL, windowClass.hInstance, NULL);
    if (!hwnd) ExitProcess(2);
    ShowWindow(hwnd, SW_SHOW);
    UpdateWindow(hwnd);
    while (GetMessageW(&message, NULL, 0, 0) > 0)
    {
        TranslateMessage(&message);
        DispatchMessageW(&message);
    }
    ExitProcess((UINT)message.wParam);
}
