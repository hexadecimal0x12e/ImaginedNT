using System.Runtime.InteropServices;

namespace ImaginedNT.AppShared.Gui;

/// <summary>
/// Win32 USER32 constants and small guest-memory layouts used by the App-side window manager.
/// This file is deliberately free of UWP types so the window model and the APIs can also be
/// compiled into a headless test host.
/// </summary>
internal static class Win32
{
    // Window styles.
    public const uint WS_OVERLAPPED = 0x00000000;
    public const uint WS_POPUP = 0x80000000;
    public const uint WS_CHILD = 0x40000000;
    public const uint WS_MINIMIZE = 0x20000000;
    public const uint WS_VISIBLE = 0x10000000;
    public const uint WS_DISABLED = 0x08000000;
    public const uint WS_CLIPSIBLINGS = 0x04000000;
    public const uint WS_CLIPCHILDREN = 0x02000000;
    public const uint WS_MAXIMIZE = 0x01000000;
    public const uint WS_CAPTION = 0x00C00000;
    public const uint WS_BORDER = 0x00800000;
    public const uint WS_DLGFRAME = 0x00400000;
    public const uint WS_VSCROLL = 0x00200000;
    public const uint WS_HSCROLL = 0x00100000;
    public const uint WS_SYSMENU = 0x00080000;
    public const uint WS_THICKFRAME = 0x00040000;
    public const uint WS_GROUP = 0x00020000;
    public const uint WS_MINIMIZEBOX = 0x00020000;
    public const uint WS_MAXIMIZEBOX = 0x00010000;
    public const uint WS_TABSTOP = 0x00010000;
    public const uint WS_OVERLAPPEDWINDOW = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_THICKFRAME |
                                             WS_MINIMIZEBOX | WS_MAXIMIZEBOX;

    // Extended window styles.
    public const uint WS_EX_TOPMOST = 0x00000008;
    public const uint WS_EX_CLIENTEDGE = 0x00000200;
    public const uint WS_EX_STATICEDGE = 0x00020000;
    public const uint WS_EX_NOACTIVATE = 0x08000000;
    public const uint WS_EX_LAYERED = 0x00080000;
    public const uint WS_EX_TRANSPARENT = 0x00000020;
    public const uint WS_EX_TOOLWINDOW = 0x00000080;

    // Window messages.
    public const uint WM_NULL = 0x0000;
    public const uint WM_CREATE = 0x0001;
    public const uint WM_DESTROY = 0x0002;
    public const uint WM_MOVE = 0x0003;
    public const uint WM_SIZE = 0x0005;
    public const uint WM_ACTIVATE = 0x0006;
    public const uint WM_SETFOCUS = 0x0007;
    public const uint WM_KILLFOCUS = 0x0008;
    public const uint WM_ENABLE = 0x000A;
    public const uint WM_SETTEXT = 0x000C;
    public const uint WM_GETTEXT = 0x000D;
    public const uint WM_GETTEXTLENGTH = 0x000E;
    public const uint WM_PAINT = 0x000F;
    public const uint WM_CLOSE = 0x0010;
    public const uint WM_QUIT = 0x0012;
    public const uint WM_ERASEBKGND = 0x0014;
    public const uint WM_SHOWWINDOW = 0x0018;
    public const uint WM_ACTIVATEAPP = 0x001C;
    public const uint WM_SETCURSOR = 0x0020;
    public const uint WM_MOUSEACTIVATE = 0x0021;
    public const uint WM_CHILDACTIVATE = 0x0022;
    public const uint WM_GETMINMAXINFO = 0x0024;
    public const uint WM_NCCREATE = 0x0081;
    public const uint WM_NCDESTROY = 0x0082;
    public const uint WM_NCCALCSIZE = 0x0083;
    public const uint WM_NCHITTEST = 0x0084;
    public const uint WM_NCACTIVATE = 0x0086;
    public const uint WM_GETDLGCODE = 0x0087;
    public const uint WM_NCMOUSEMOVE = 0x00A0;
    public const uint WM_NCLBUTTONDOWN = 0x00A1;
    public const uint WM_NCLBUTTONUP = 0x00A2;
    public const uint WM_NCLBUTTONDBLCLK = 0x00A3;
    public const uint WM_KEYFIRST = 0x0100;
    public const uint WM_KEYDOWN = 0x0100;
    public const uint WM_KEYUP = 0x0101;
    public const uint WM_CHAR = 0x0102;
    public const uint WM_DEADCHAR = 0x0103;
    public const uint WM_SYSKEYDOWN = 0x0104;
    public const uint WM_SYSKEYUP = 0x0105;
    public const uint WM_SYSCHAR = 0x0106;
    public const uint WM_SYSDEADCHAR = 0x0107;
    public const uint WM_COMMAND = 0x0111;
    public const uint WM_SYSCOMMAND = 0x0112;
    public const uint WM_TIMER = 0x0113;
    public const uint WM_MOUSEMOVE = 0x0200;
    public const uint WM_LBUTTONDOWN = 0x0201;
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_LBUTTONDBLCLK = 0x0203;
    public const uint WM_RBUTTONDOWN = 0x0204;
    public const uint WM_RBUTTONUP = 0x0205;
    public const uint WM_RBUTTONDBLCLK = 0x0206;
    public const uint WM_MBUTTONDOWN = 0x0207;
    public const uint WM_MBUTTONUP = 0x0208;
    public const uint WM_MBUTTONDBLCLK = 0x0209;
    public const uint WM_MOUSEWHEEL = 0x020A;
    public const uint WM_PARENTNOTIFY = 0x0210;
    public const uint WM_CAPTURECHANGED = 0x0215;
    public const uint WM_ENTERSIZEMOVE = 0x0231;
    public const uint WM_EXITSIZEMOVE = 0x0232;
    public const uint WM_MOUSELEAVE = 0x02A3;
    public const uint WM_NCMOUSELEAVE = 0x02A2;
    public const uint WM_DPICHANGED = 0x02E0;
    public const uint WM_APP = 0x8000;

    // Command/notification values.
    public const uint SC_CLOSE = 0xF060;
    public const uint SC_MINIMIZE = 0xF020;
    public const uint SC_MAXIMIZE = 0xF030;
    public const uint SC_RESTORE = 0xF120;
    public const int BN_CLICKED = 0;

    // ShowWindow commands.
    public const int SW_HIDE = 0;
    public const int SW_SHOWNORMAL = 1;
    public const int SW_SHOWMINIMIZED = 2;
    public const int SW_SHOWMAXIMIZED = 3;
    public const int SW_SHOWNOACTIVATE = 4;
    public const int SW_SHOW = 5;
    public const int SW_MINIMIZE = 6;
    public const int SW_SHOWMINNOACTIVE = 7;
    public const int SW_SHOWNA = 8;
    public const int SW_RESTORE = 9;
    public const int SW_SHOWDEFAULT = 10;

    // SetWindowPos flags.
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOREDRAW = 0x0008;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_HIDEWINDOW = 0x0080;
    public const uint SWP_NOOWNERZORDER = 0x0200;
    public const uint SWP_NOSENDCHANGING = 0x0400;

    // GetWindowLongPtr indices.
    public const int GWL_WNDPROC = -4;
    public const int GWL_HINSTANCE = -6;
    public const int GWL_HWNDPARENT = -8;
    public const int GWL_ID = -12;
    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;
    public const int GWL_USERDATA = -21;
    public const int GWLP_WNDPROC = -4;
    public const int GWLP_HINSTANCE = -6;
    public const int GWLP_HWNDPARENT = -8;
    public const int GWLP_USERDATA = -21;

    // GetWindow commands.
    public const uint GW_HWNDFIRST = 0;
    public const uint GW_HWNDLAST = 1;
    public const uint GW_HWNDNEXT = 2;
    public const uint GW_HWNDPREV = 3;
    public const uint GW_OWNER = 4;
    public const uint GW_CHILD = 5;

    // Hit-test / mouse-activate results.
    public const int HTNOWHERE = 0;
    public const int HTCLIENT = 1;
    public const int HTCAPTION = 2;
    public const int HTBORDER = 18;
    public const int MA_ACTIVATE = 1;

    // Size message values.
    public const uint SIZE_RESTORED = 0;
    public const uint SIZE_MINIMIZED = 1;
    public const uint SIZE_MAXIMIZED = 2;

    // System colors.
    public const int COLOR_SCROLLBAR = 0;
    public const int COLOR_BACKGROUND = 1;
    public const int COLOR_ACTIVECAPTION = 2;
    public const int COLOR_INACTIVECAPTION = 3;
    public const int COLOR_MENU = 4;
    public const int COLOR_WINDOW = 5;
    public const int COLOR_WINDOWFRAME = 6;
    public const int COLOR_MENUTEXT = 7;
    public const int COLOR_WINDOWTEXT = 8;
    public const int COLOR_CAPTIONTEXT = 9;
    public const int COLOR_ACTIVEBORDER = 10;
    public const int COLOR_INACTIVEBORDER = 11;
    public const int COLOR_APPWORKSPACE = 12;
    public const int COLOR_HIGHLIGHT = 13;
    public const int COLOR_HIGHLIGHTTEXT = 14;
    public const int COLOR_BTNFACE = 15;
    public const int COLOR_BTNSHADOW = 16;
    public const int COLOR_GRAYTEXT = 17;
    public const int COLOR_BTNTEXT = 18;
    public const int COLOR_INACTIVECAPTIONTEXT = 19;
    public const int COLOR_BTNHIGHLIGHT = 20;
    public const int COLOR_3DDKSHADOW = 21;
    public const int COLOR_3DLIGHT = 22;
    public const int COLOR_INFOTEXT = 23;
    public const int COLOR_INFOBK = 24;
    public const int COLOR_HOTLIGHT = 26;
    public const int COLOR_GRADIENTACTIVECAPTION = 27;
    public const int COLOR_GRADIENTINACTIVECAPTION = 28;
    public const int COLOR_MENUHILIGHT = 29;
    public const int COLOR_MENUBAR = 30;

    // System metrics.
    public const int SM_CXSCREEN = 0;
    public const int SM_CYSCREEN = 1;
    public const int SM_CXVSCROLL = 2;
    public const int SM_CYHSCROLL = 3;
    public const int SM_CYCAPTION = 4;
    public const int SM_CXBORDER = 5;
    public const int SM_CYBORDER = 6;
    public const int SM_CXFRAME = 32;
    public const int SM_CYFRAME = 33;
    public const int SM_CXMIN = 28;
    public const int SM_CYMIN = 29;
    public const int SM_CXMAXIMIZED = 61;
    public const int SM_CYMAXIMIZED = 62;

    // Mouse/key state.
    public const int MK_LBUTTON = 0x0001;
    public const int MK_RBUTTON = 0x0002;
    public const int MK_SHIFT = 0x0004;
    public const int MK_CONTROL = 0x0008;
    public const int MK_MBUTTON = 0x0010;
    public const int VK_BACK = 0x08;
    public const int VK_TAB = 0x09;
    public const int VK_RETURN = 0x0D;
    public const int VK_SHIFT = 0x10;
    public const int VK_CONTROL = 0x11;
    public const int VK_MENU = 0x12;
    public const int VK_ESCAPE = 0x1B;
    public const int VK_PRIOR = 0x21;
    public const int VK_DOWN = 0x28;
    public const int VK_INSERT = 0x2D;
    public const int VK_DELETE = 0x2E;
    public const int VK_F1 = 0x70;
    public const int VK_F24 = 0x87;
    public const int VK_LSHIFT = 0xA0;
    public const int VK_RSHIFT = 0xA1;
    public const int VK_LCONTROL = 0xA2;
    public const int VK_RCONTROL = 0xA3;
    public const int VK_LMENU = 0xA4;
    public const int VK_RMENU = 0xA5;

    // Message filters.
    public const uint PM_NOREMOVE = 0x0000;
    public const uint PM_REMOVE = 0x0001;
    public const uint PM_NOYIELD = 0x0002;

    // Geometry helpers.
    public const int CW_USEDEFAULT = unchecked((int)0x80000000);
    public const ulong HwndTop = 0;
    public const ulong HwndBottom = 1;
    public const ulong HwndTopmost = unchecked((ulong)(long)-1);
    public const ulong HwndNoTopmost = unchecked((ulong)(long)-2);

    public static int LoWord(nint value) => unchecked((ushort)((ulong)value & 0xFFFF));
    public static int HiWord(nint value) => unchecked((ushort)(((ulong)value >> 16) & 0xFFFF));
    public static nint MakeParam(int low, int high) =>
        unchecked((nint)((long)(ushort)low | ((long)(ushort)high << 16)));

    // Mouse/move/size messages pack two signed 16-bit values into the low 32 bits of lParam:
    // LOWORD is x (or width), HIWORD is y (or height).  This is NOT a POINT structure.
    public static long MakePoint(int x, int y) => (uint)(ushort)x | ((long)(ushort)y << 16);

    public static int PointX(long value) => (short)((ulong)value & 0xFFFF);
    public static int PointY(long value) => (short)(((ulong)value >> 16) & 0xFFFF);

    public static long MakeSize(int width, int height) => MakePoint(width, height);
}

/// <summary>MSG, 48 bytes on x64 (POINT starts at offset 36).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativeMessage
{
    public ulong Hwnd;
    public uint Message;
    public uint Padding;
    public ulong WParam;
    public long LParam;
    public uint Time;
    public int PointX;
    public int PointY;
    public uint Padding2;
}

/// <summary>CREATESTRUCTW, 80 bytes on x64.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativeCreateStruct
{
    public ulong CreateParams;
    public ulong Instance;
    public ulong Menu;
    public ulong Parent;
    public int Height;
    public int Width;
    public int Y;
    public int X;
    public uint Style;
    public ulong Name;
    public ulong ClassName;
    public uint ExStyle;
    public uint Padding;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeRect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public NativeRect(int left, int top, int right, int bottom)
    {
        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePoint
{
    public int X;
    public int Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeMargins
{
    public int Left;
    public int Right;
    public int Top;
    public int Bottom;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePaintStruct
{
    public ulong Hdc;
    public int Erase;
    public NativeRect Paint;
    public int Restore;
    public int IncUpdate;
    public ulong Reserved0;
    public ulong Reserved1;
    public ulong Reserved2;
    public ulong Reserved3;
}
