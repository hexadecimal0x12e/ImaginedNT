using System.Runtime.InteropServices;
using System.Text;
using ImaginedNT.Core.Nt;
using ImaginedNT.Core.Runtime;

namespace ImaginedNT.App.Gui;

/// <summary>
/// Minimal managed USER32 backed by <see cref="WindowManager"/>.  It deliberately implements
/// only windowless window management: window classes, creation/show/destroy, the message loop,
/// geometry, z-order, keyboard/mouse messages and the small system-colour/cursor surface a
/// WM_PAINT-less guest needs.  GDI and other drawing APIs are intentionally out of scope; the
/// compositor draws the window chrome and background with App controls.
/// </summary>
public static class WindowApis
{
    private const ulong CursorHandle = 0x0000_0000_0000_1000;
    private const ulong IconHandle = 0x0000_0000_0000_1001;

    private static WindowManager Manager(ApiCallContext ctx) => GuiLibrary.GetManager(ctx.Runtime);

    private static bool TryWindow(ApiCallContext ctx, int argument, out ulong handle, out GuestWindow? window)
    {
        handle = ctx.Arg(argument);
        var manager = Manager(ctx);
        return manager.TryGetWindow(handle, out window) && window is not null;
    }

    // --- class registration -----------------------------------------------------

    [VirtualApi("user32.dll", "RegisterClassExW", ApiCategory.Gui)]
    private static ulong RegisterClassExW(ApiCallContext ctx) => RegisterClassExCore(ctx, ansi: false);

    [VirtualApi("user32.dll", "RegisterClassExA", ApiCategory.Gui)]
    private static ulong RegisterClassExA(ApiCallContext ctx) => RegisterClassExCore(ctx, ansi: true);

    [VirtualApi("user32.dll", "RegisterClassW", ApiCategory.Gui)]
    private static ulong RegisterClassW(ApiCallContext ctx) => RegisterClassCore(ctx, ansi: false);

    [VirtualApi("user32.dll", "RegisterClassA", ApiCategory.Gui)]
    private static ulong RegisterClassA(ApiCallContext ctx) => RegisterClassCore(ctx, ansi: true);

    private static ulong RegisterClassExCore(ApiCallContext ctx, bool ansi)
    {
        var address = ctx.Arg(0);
        if (address == 0)
        {
            ctx.SetLastError(Win32Error.InvalidParameter);
            return 0;
        }
        var size = ctx.Read<uint>(address);
        if (size < 72)
        {
            ctx.SetLastError(Win32Error.InvalidParameter);
            return 0;
        }
        return RegisterClassAt(ctx, address, ansi);
    }

    private static ulong RegisterClassCore(ApiCallContext ctx, bool ansi)
    {
        var address = ctx.Arg(0);
        if (address == 0)
        {
            ctx.SetLastError(Win32Error.InvalidParameter);
            return 0;
        }
        return RegisterClassAt(ctx, address, ansi);
    }

    private static ulong RegisterClassAt(ApiCallContext ctx, ulong address, bool ansi)
    {
        // WNDCLASSEXW and WNDCLASSW share the same first 72 bytes on x64.
        var style = ctx.Read<uint>(address + 4);
        var wndProc = ctx.Read<ulong>(address + 8);
        var instance = ctx.Read<ulong>(address + 24);
        var background = ctx.Read<ulong>(address + 48);
        var menuPointer = ctx.Read<ulong>(address + 56);
        var namePointer = ctx.Read<ulong>(address + 64);
        if (namePointer == 0)
        {
            ctx.SetLastError(Win32Error.InvalidParameter);
            return 0;
        }
        var name = ansi ? ctx.ReadAnsiString(namePointer) : ctx.ReadUnicodeString(namePointer);
        var menu = menuPointer == 0 ? null : ansi ? ctx.ReadAnsiString(menuPointer) : ctx.ReadUnicodeString(menuPointer);
        var info = Manager(ctx).RegisterClass(ctx.Process.Id, name, wndProc, style, background, instance, menu);
        if (info is null)
        {
            // CLASS_ALREADY_EXISTS (1410) for duplicates, otherwise invalid parameter.
            ctx.SetLastError(1410);
            return 0;
        }
        return info.Atom;
    }

    [VirtualApi("user32.dll", "UnregisterClassW", ApiCategory.Gui)]
    private static ulong UnregisterClassW(ApiCallContext ctx) => UnregisterClassCore(ctx, ansi: false);

    [VirtualApi("user32.dll", "UnregisterClassA", ApiCategory.Gui)]
    private static ulong UnregisterClassA(ApiCallContext ctx) => UnregisterClassCore(ctx, ansi: true);

    private static ulong UnregisterClassCore(ApiCallContext ctx, bool ansi)
    {
        var pointer = ctx.Arg(0);
        string name;
        if (pointer == 0)
        {
            ctx.SetLastError(Win32Error.InvalidParameter);
            return 0;
        }
        if (pointer <= 0xFFFF) name = "#" + pointer;
        else name = ansi ? ctx.ReadAnsiString(pointer) : ctx.ReadUnicodeString(pointer);
        return Manager(ctx).UnregisterClass(ctx.Process.Id, name) ? 1UL : 0UL;
    }

    // --- window creation and lifetime -------------------------------------------

    [VirtualApi("user32.dll", "CreateWindowExW", ApiCategory.Gui)]
    private static ulong CreateWindowExW(ApiCallContext ctx) => CreateWindowExCore(ctx, ansi: false);

    [VirtualApi("user32.dll", "CreateWindowExA", ApiCategory.Gui)]
    private static ulong CreateWindowExA(ApiCallContext ctx) => CreateWindowExCore(ctx, ansi: true);

    private static ulong CreateWindowExCore(ApiCallContext ctx, bool ansi)
    {
        var manager = Manager(ctx);
        var exStyle = ctx.Arg32(0);
        var classPointer = ctx.Arg(1);
        var titlePointer = ctx.Arg(2);
        var style = ctx.Arg32(3);
        var x = unchecked((int)ctx.Arg32(4));
        var y = unchecked((int)ctx.Arg32(5));
        var width = unchecked((int)ctx.Arg32(6));
        var height = unchecked((int)ctx.Arg32(7));
        var parent = ctx.Arg(8);
        var menu = ctx.Arg(9);
        var instance = ctx.Arg(10);
        var createParam = ctx.Arg(11);

        WindowClassInfo? windowClass;
        if (classPointer == 0)
        {
            ctx.SetLastError(Win32Error.InvalidParameter);
            return 0;
        }
        if (classPointer <= 0xFFFF)
        {
            windowClass = manager.FindClass(ctx.Process.Id, (ushort)classPointer);
        }
        else
        {
            var className = ansi ? ctx.ReadAnsiString(classPointer) : ctx.ReadUnicodeString(classPointer);
            windowClass = manager.FindClass(ctx.Process.Id, className);
        }
        if (windowClass is null)
        {
            ctx.SetLastError(1410); // ERROR_CLASS_DOES_NOT_EXIST
            return 0;
        }

        var title = titlePointer == 0 ? string.Empty
            : ansi ? ctx.ReadAnsiString(titlePointer)
            : ctx.ReadUnicodeString(titlePointer);

        var window = manager.CreateWindow(ctx.Process.Id, ctx.Thread.Id, windowClass, title, style, exStyle,
            x, y, width, height, parent, menu, instance);
        if (window is null)
        {
            ctx.SetLastError(Win32Error.InvalidParameter);
            return 0;
        }

        var createStructAddress = ctx.Process.Heap.Allocate((ulong)Marshal.SizeOf<NativeCreateStruct>(),
            zero: true, threadId: ctx.Thread.Id);
        try
        {
            if (createStructAddress != 0)
            {
                ctx.Write(createStructAddress, new NativeCreateStruct
                {
                    CreateParams = createParam,
                    Instance = instance,
                    Menu = menu,
                    Parent = parent,
                    Height = height,
                    Width = width,
                    Y = y,
                    X = x,
                    Style = style,
                    Name = titlePointer,
                    ClassName = classPointer,
                    ExStyle = exStyle
                });
            }
            if (SendCore(ctx, manager, window.Handle, Win32.WM_NCCREATE, 0, unchecked((long)createStructAddress)) == 0)
            {
                manager.RemoveWindow(window.Handle);
                return 0;
            }
            var created = SendCore(ctx, manager, window.Handle, Win32.WM_CREATE, 0,
                unchecked((long)createStructAddress));
            if (created == unchecked((ulong)(long)-1))
            {
                DestroyWindowCore(ctx, manager, window.Handle);
                return 0;
            }
        }
        finally
        {
            if (createStructAddress != 0) ctx.Process.Heap.Free(createStructAddress, ctx.Thread.Id);
        }

        if ((style & Win32.WS_VISIBLE) != 0)
            SendCore(ctx, manager, window.Handle, Win32.WM_SHOWWINDOW, 1, 0);
        return window.Handle;
    }

    [VirtualApi("user32.dll", "DestroyWindow", ApiCategory.Gui)]
    private static ulong DestroyWindow(ApiCallContext ctx)
    {
        var manager = Manager(ctx);
        var handle = ctx.Arg(0);
        if (!manager.TryGetWindow(handle, out _))
        {
            ctx.SetLastError(1400); // ERROR_INVALID_WINDOW_HANDLE
            return 0;
        }
        DestroyWindowCore(ctx, manager, handle);
        return 1;
    }

    internal static void DestroyWindowCore(ApiCallContext ctx, WindowManager manager, ulong handle)
    {
        if (!manager.TryGetWindow(handle, out _)) return;
        foreach (var child in manager.GetChildren(handle))
            DestroyWindowCore(ctx, manager, child.Handle);
        SendCore(ctx, manager, handle, Win32.WM_DESTROY, 0, 0);
        SendCore(ctx, manager, handle, Win32.WM_NCDESTROY, 0, 0);
        manager.RemoveWindow(handle);
    }

    // --- window procedure dispatch ---------------------------------------------

    [VirtualApi("user32.dll", "DefWindowProcW", ApiCategory.Gui)]
    private static ulong DefWindowProcW(ApiCallContext ctx) => DefaultProcCore(ctx, Manager(ctx), ctx.Arg(0),
        ctx.Arg32(1), ctx.Arg(2), unchecked((long)ctx.Arg(3)));

    [VirtualApi("user32.dll", "DefWindowProcA", ApiCategory.Gui)]
    private static ulong DefWindowProcA(ApiCallContext ctx) => DefaultProcCore(ctx, Manager(ctx), ctx.Arg(0),
        ctx.Arg32(1), ctx.Arg(2), unchecked((long)ctx.Arg(3)));

    [VirtualApi("user32.dll", "CallWindowProcW", ApiCategory.Gui)]
    private static ulong CallWindowProcW(ApiCallContext ctx) => CallProcCore(ctx, Manager(ctx), ctx.Arg(0),
        ctx.Arg(1), ctx.Arg32(2), ctx.Arg(3), unchecked((long)ctx.Arg(4)));

    [VirtualApi("user32.dll", "CallWindowProcA", ApiCategory.Gui)]
    private static ulong CallWindowProcA(ApiCallContext ctx) => CallProcCore(ctx, Manager(ctx), ctx.Arg(0),
        ctx.Arg(1), ctx.Arg32(2), ctx.Arg(3), unchecked((long)ctx.Arg(4)));

    private static ulong CallProcCore(ApiCallContext ctx, WindowManager manager, ulong wndProc, ulong handle,
        uint message, ulong wParam, long lParam)
    {
        if (wndProc == 0) return DefaultProcCore(ctx, manager, handle, message, wParam, lParam);
        if (ctx.Thread.IsManagedHosted && !ctx.Process.IsTerminating)
        {
            var result = ctx.Thread.InvokeGuest(wndProc, [handle, message, wParam, unchecked((ulong)lParam)]);
            manager.Drawing?.Present(handle);
            return result;
        }
        return DefaultProcCore(ctx, manager, handle, message, wParam, lParam);
    }

    internal static ulong SendCore(ApiCallContext ctx, WindowManager manager, ulong handle, uint message,
        ulong wParam, long lParam)
    {
        if (handle == 0) return 0;
        if (!manager.TryGetWindow(handle, out var window) || window is null) return 0;
        var wndProc = window.WndProc;
        if (wndProc != 0 && ctx.Thread.IsManagedHosted && !ctx.Process.IsTerminating)
        {
            var result = ctx.Thread.InvokeGuest(wndProc, [handle, message, wParam, unchecked((ulong)lParam)]);
            manager.Drawing?.Present(handle);
            return result;
        }
        return DefaultProcCore(ctx, manager, handle, message, wParam, lParam);
    }

    internal static ulong DefaultProcCore(ApiCallContext ctx, WindowManager manager, ulong handle, uint message,
        ulong wParam, long lParam)
    {
        if (handle == 0 || !manager.TryGetWindow(handle, out var window) || window is null) return 0;
        switch (message)
        {
            case Win32.WM_NCCREATE:
                return 1;
            case Win32.WM_CREATE:
            case Win32.WM_NCCALCSIZE:
            case Win32.WM_GETMINMAXINFO:
            case Win32.WM_DESTROY:
            case Win32.WM_MOVE:
            case Win32.WM_SIZE:
            case Win32.WM_ACTIVATE:
            case Win32.WM_SHOWWINDOW:
            case Win32.WM_COMMAND:
            case Win32.WM_TIMER:
            case Win32.WM_KEYDOWN:
            case Win32.WM_KEYUP:
            case Win32.WM_CHAR:
            case Win32.WM_SYSKEYDOWN:
            case Win32.WM_SYSKEYUP:
            case Win32.WM_SYSCHAR:
            case Win32.WM_MOUSEMOVE:
            case Win32.WM_LBUTTONDOWN:
            case Win32.WM_LBUTTONUP:
            case Win32.WM_RBUTTONDOWN:
            case Win32.WM_RBUTTONUP:
            case Win32.WM_MBUTTONDOWN:
            case Win32.WM_MBUTTONUP:
            case Win32.WM_MOUSEWHEEL:
            case Win32.WM_NCMOUSEMOVE:
            case Win32.WM_NCLBUTTONDOWN:
            case Win32.WM_NCLBUTTONUP:
                return 0;
            case Win32.WM_CLOSE:
                DestroyWindowCore(ctx, manager, handle);
                return 0;
            case Win32.WM_SYSCOMMAND:
                if ((wParam & 0xFFF0) == Win32.SC_CLOSE)
                {
                    DestroyWindowCore(ctx, manager, handle);
                }
                return 0;
            case Win32.WM_SETTEXT:
                manager.SetTitle(handle, lParam == 0 ? string.Empty : ctx.ReadUnicodeString(unchecked((ulong)lParam)));
                return 1;
            case Win32.WM_GETTEXT:
                return WriteGuestString(ctx, wParam, (int)lParam, manager.GetTitle(handle), ansi: false);
            case Win32.WM_GETTEXTLENGTH:
                return (ulong)manager.GetTitle(handle).Length;
            case Win32.WM_ERASEBKGND:
                return manager.Drawing is null || manager.Drawing.EraseBackground(handle, wParam) ? 1UL : 0UL;
            case Win32.WM_PAINT:
                manager.Drawing?.Validate(handle);
                return 0;
            case Win32.WM_MOUSEACTIVATE:
                return Win32.MA_ACTIVATE;
            case Win32.WM_SETCURSOR:
                return 1;
            case Win32.WM_NCHITTEST:
            {
                var x = Win32.PointX(lParam);
                var y = Win32.PointY(lParam);
                var hit = manager.HitTest(x, y);
                return hit.Region switch
                {
                    WindowHitRegion.Caption => Win32.HTCAPTION,
                    WindowHitRegion.Client => Win32.HTCLIENT,
                    WindowHitRegion.Border => Win32.HTBORDER,
                    _ => Win32.HTNOWHERE
                };
            }
            default:
                return 0;
        }
    }

    // --- show, move and geometry ------------------------------------------------

    [VirtualApi("user32.dll", "ShowWindow", ApiCategory.Gui)]
    private static ulong ShowWindow(ApiCallContext ctx) => ShowWindowCore(ctx);

    [VirtualApi("user32.dll", "ShowWindowAsync", ApiCategory.Gui)]
    private static ulong ShowWindowAsync(ApiCallContext ctx) => ShowWindowCore(ctx);

    private static ulong ShowWindowCore(ApiCallContext ctx)
    {
        var manager = Manager(ctx);
        var handle = ctx.Arg(0);
        var command = unchecked((int)ctx.Arg32(1));
        if (!manager.TryGetWindow(handle, out var window) || window is null)
        {
            ctx.SetLastError(1400);
            return 0;
        }
        var previous = manager.IsVisible(handle);
        var visible = command != Win32.SW_HIDE;
        manager.SetVisible(handle, visible);
        SendCore(ctx, manager, handle, Win32.WM_SHOWWINDOW, visible ? 1UL : 0UL, 0);
        if (visible && command is Win32.SW_SHOWMAXIMIZED)
            SendCore(ctx, manager, handle, Win32.WM_SIZE, Win32.SIZE_MAXIMIZED,
                Win32.MakeSize(window.Width, window.Height));
        return previous ? 1UL : 0UL;
    }

    [VirtualApi("user32.dll", "UpdateWindow", ApiCategory.Gui)]
    private static ulong UpdateWindow(ApiCallContext ctx)
    {
        var manager = Manager(ctx);
        var handle = ctx.Arg(0);
        if (!manager.IsVisible(handle)) return 0;
        SendCore(ctx, manager, handle, Win32.WM_PAINT, 0, 0);
        return 1;
    }

    [VirtualApi("user32.dll", "MoveWindow", ApiCategory.Gui)]
    private static ulong MoveWindow(ApiCallContext ctx)
    {
        var manager = Manager(ctx);
        var handle = ctx.Arg(0);
        if (!manager.GetWindowRect(handle, out var previous, out _))
        {
            ctx.SetLastError(1400);
            return 0;
        }
        var x = unchecked((int)ctx.Arg32(1));
        var y = unchecked((int)ctx.Arg32(2));
        var width = unchecked((int)ctx.Arg32(3));
        var height = unchecked((int)ctx.Arg32(4));
        var repaint = ctx.ArgBool(5);
        manager.MoveWindow(handle, x, y, width, height);
        if (x != previous.X || y != previous.Y)
            SendCore(ctx, manager, handle, Win32.WM_MOVE, 0, Win32.MakePoint(x, y));
        if (width != previous.Width || height != previous.Height)
            SendCore(ctx, manager, handle, Win32.WM_SIZE, Win32.SIZE_RESTORED, Win32.MakeSize(width, height));
        if (repaint) SendCore(ctx, manager, handle, Win32.WM_PAINT, 0, 0);
        return 1;
    }

    [VirtualApi("user32.dll", "SetWindowPos", ApiCategory.Gui)]
    private static ulong SetWindowPos(ApiCallContext ctx)
    {
        var manager = Manager(ctx);
        var handle = ctx.Arg(0);
        if (!manager.GetWindowRect(handle, out var previous, out _))
        {
            ctx.SetLastError(1400);
            return 0;
        }
        var insertAfter = ctx.Arg(1);
        var x = unchecked((int)ctx.Arg32(2));
        var y = unchecked((int)ctx.Arg32(3));
        var width = unchecked((int)ctx.Arg32(4));
        var height = unchecked((int)ctx.Arg32(5));
        var flags = ctx.Arg32(6);

        if ((flags & Win32.SWP_NOMOVE) != 0)
        {
            x = previous.X;
            y = previous.Y;
        }
        if ((flags & Win32.SWP_NOSIZE) != 0)
        {
            width = previous.Width;
            height = previous.Height;
        }
        manager.MoveWindow(handle, x, y, width, height);
        if ((flags & Win32.SWP_NOZORDER) == 0) manager.SetZOrder(handle, insertAfter);
        if ((flags & Win32.SWP_SHOWWINDOW) != 0) manager.SetVisible(handle, true);
        if ((flags & Win32.SWP_HIDEWINDOW) != 0) manager.SetVisible(handle, false);
        if (x != previous.X || y != previous.Y)
            SendCore(ctx, manager, handle, Win32.WM_MOVE, 0, Win32.MakePoint(x, y));
        if (width != previous.Width || height != previous.Height)
            SendCore(ctx, manager, handle, Win32.WM_SIZE, Win32.SIZE_RESTORED, Win32.MakeSize(width, height));
        return 1;
    }

    [VirtualApi("user32.dll", "GetWindowRect", ApiCategory.Gui)]
    private static ulong GetWindowRect(ApiCallContext ctx)
    {
        var manager = Manager(ctx);
        if (!manager.GetWindowRect(ctx.Arg(0), out var rect, out _))
        {
            ctx.SetLastError(1400);
            return 0;
        }
        var output = ctx.Arg(1);
        if (output == 0) return 0;
        ctx.Write(output, new NativeRect(rect.X, rect.Y, rect.Right, rect.Bottom));
        return 1;
    }

    [VirtualApi("user32.dll", "GetClientRect", ApiCategory.Gui)]
    private static ulong GetClientRect(ApiCallContext ctx)
    {
        var manager = Manager(ctx);
        if (!manager.GetWindowRect(ctx.Arg(0), out _, out var client))
        {
            ctx.SetLastError(1400);
            return 0;
        }
        var output = ctx.Arg(1);
        if (output == 0) return 0;
        ctx.Write(output, new NativeRect(0, 0, client.Width, client.Height));
        return 1;
    }

    [VirtualApi("user32.dll", "ClientToScreen", ApiCategory.Gui)]
    private static ulong ClientToScreen(ApiCallContext ctx)
    {
        var manager = Manager(ctx);
        var output = ctx.Arg(1);
        if (output == 0 || !manager.GetWindowRect(ctx.Arg(0), out _, out var client))
        {
            ctx.SetLastError(Win32Error.InvalidParameter);
            return 0;
        }
        var point = ctx.Read<NativePoint>(output);
        ctx.Write(output, new NativePoint { X = point.X + client.X, Y = point.Y + client.Y });
        return 1;
    }

    [VirtualApi("user32.dll", "ScreenToClient", ApiCategory.Gui)]
    private static ulong ScreenToClient(ApiCallContext ctx)
    {
        var manager = Manager(ctx);
        var output = ctx.Arg(1);
        if (output == 0 || !manager.GetWindowRect(ctx.Arg(0), out _, out var client))
        {
            ctx.SetLastError(Win32Error.InvalidParameter);
            return 0;
        }
        var point = ctx.Read<NativePoint>(output);
        ctx.Write(output, new NativePoint { X = point.X - client.X, Y = point.Y - client.Y });
        return 1;
    }

    // --- text and window state --------------------------------------------------

    [VirtualApi("user32.dll", "SetWindowTextW", ApiCategory.Gui)]
    private static ulong SetWindowTextW(ApiCallContext ctx)
    {
        var manager = Manager(ctx);
        var handle = ctx.Arg(0);
        if (!manager.TryGetWindow(handle, out _))
        {
            ctx.SetLastError(1400);
            return 0;
        }
        return SendCore(ctx, manager, handle, Win32.WM_SETTEXT, 0, unchecked((long)ctx.Arg(1)));
    }

    [VirtualApi("user32.dll", "SetWindowTextA", ApiCategory.Gui)]
    private static ulong SetWindowTextA(ApiCallContext ctx)
    {
        var manager = Manager(ctx);
        var handle = ctx.Arg(0);
        if (!manager.TryGetWindow(handle, out _))
        {
            ctx.SetLastError(1400);
            return 0;
        }
        var text = ctx.Arg(1) == 0 ? string.Empty : ctx.ReadAnsiString(ctx.Arg(1));
        manager.SetTitle(handle, text);
        return 1;
    }

    [VirtualApi("user32.dll", "GetWindowTextW", ApiCategory.Gui)]
    private static ulong GetWindowTextW(ApiCallContext ctx) => GetWindowTextCore(ctx, ansi: false);

    [VirtualApi("user32.dll", "GetWindowTextA", ApiCategory.Gui)]
    private static ulong GetWindowTextA(ApiCallContext ctx) => GetWindowTextCore(ctx, ansi: true);

    private static ulong GetWindowTextCore(ApiCallContext ctx, bool ansi)
    {
        var title = Manager(ctx).GetTitle(ctx.Arg(0));
        return WriteGuestString(ctx, ctx.Arg(1), unchecked((int)ctx.Arg32(2)), title, ansi);
    }

    [VirtualApi("user32.dll", "GetWindowTextLengthW", ApiCategory.Gui)]
    private static ulong GetWindowTextLengthW(ApiCallContext ctx) => (ulong)Manager(ctx).GetTitle(ctx.Arg(0)).Length;

    [VirtualApi("user32.dll", "GetWindowTextLengthA", ApiCategory.Gui)]
    private static ulong GetWindowTextLengthA(ApiCallContext ctx) => (ulong)Manager(ctx).GetTitle(ctx.Arg(0)).Length;

    [VirtualApi("user32.dll", "GetClassNameW", ApiCategory.Gui)]
    private static ulong GetClassNameW(ApiCallContext ctx) => GetClassNameCore(ctx, ansi: false);

    [VirtualApi("user32.dll", "GetClassNameA", ApiCategory.Gui)]
    private static ulong GetClassNameA(ApiCallContext ctx) => GetClassNameCore(ctx, ansi: true);

    private static ulong GetClassNameCore(ApiCallContext ctx, bool ansi)
    {
        if (!Manager(ctx).TryGetWindow(ctx.Arg(0), out var window) || window is null) return 0;
        return WriteGuestString(ctx, ctx.Arg(1), unchecked((int)ctx.Arg32(2)), window.ClassName, ansi);
    }

    [VirtualApi("user32.dll", "SetWindowLongPtrW", ApiCategory.Gui)]
    private static ulong SetWindowLongPtrW(ApiCallContext ctx) => SetWindowLongCore(ctx, usesPointer: true);

    [VirtualApi("user32.dll", "SetWindowLongPtrA", ApiCategory.Gui)]
    private static ulong SetWindowLongPtrA(ApiCallContext ctx) => SetWindowLongCore(ctx, usesPointer: true);

    [VirtualApi("user32.dll", "SetWindowLongW", ApiCategory.Gui)]
    private static ulong SetWindowLongW(ApiCallContext ctx) => SetWindowLongCore(ctx, usesPointer: false);

    private static ulong SetWindowLongCore(ApiCallContext ctx, bool usesPointer)
    {
        var manager = Manager(ctx);
        var handle = ctx.Arg(0);
        var index = unchecked((int)ctx.Arg32(1));
        var value = ctx.Arg(2);
        if (!manager.TryGetWindow(handle, out var window) || window is null)
        {
            ctx.SetLastError(1400);
            return 0;
        }
        switch (index)
        {
            case Win32.GWL_WNDPROC:
            {
                var old = window.WndProc;
                manager.SetWindowProc(handle, value);
                return old;
            }
            case Win32.GWL_STYLE:
            {
                var old = window.Style;
                manager.SetStyle(handle, unchecked((uint)value));
                return old;
            }
            case Win32.GWL_EXSTYLE:
            {
                var old = window.ExStyle;
                manager.SetExStyle(handle, (uint)value);
                return old;
            }
            case Win32.GWL_USERDATA:
            {
                var old = window.UserData;
                manager.SetUserData(handle, value);
                return old;
            }
            case Win32.GWL_HINSTANCE:
                return window.Instance;
            case Win32.GWL_HWNDPARENT:
                return window.Owner;
            case Win32.GWL_ID:
                return (ulong)window.ControlId;
            default:
                return 0;
        }
    }

    [VirtualApi("user32.dll", "GetWindowLongPtrW", ApiCategory.Gui)]
    private static ulong GetWindowLongPtrW(ApiCallContext ctx) => GetWindowLongCore(ctx);

    [VirtualApi("user32.dll", "GetWindowLongPtrA", ApiCategory.Gui)]
    private static ulong GetWindowLongPtrA(ApiCallContext ctx) => GetWindowLongCore(ctx);

    [VirtualApi("user32.dll", "GetWindowLongW", ApiCategory.Gui)]
    private static ulong GetWindowLongW(ApiCallContext ctx) => GetWindowLongCore(ctx);

    private static ulong GetWindowLongCore(ApiCallContext ctx)
    {
        if (!Manager(ctx).TryGetWindow(ctx.Arg(0), out var window) || window is null) return 0;
        return unchecked((int)ctx.Arg32(1)) switch
        {
            Win32.GWL_WNDPROC => window.WndProc,
            Win32.GWL_STYLE => window.Style,
            Win32.GWL_EXSTYLE => window.ExStyle,
            Win32.GWL_USERDATA => window.UserData,
            Win32.GWL_HINSTANCE => window.Instance,
            Win32.GWL_HWNDPARENT => window.Owner,
            Win32.GWL_ID => (ulong)window.ControlId,
            _ => 0
        };
    }

    [VirtualApi("user32.dll", "EnableWindow", ApiCategory.Gui)]
    private static ulong EnableWindow(ApiCallContext ctx)
    {
        var manager = Manager(ctx);
        var handle = ctx.Arg(0);
        if (!manager.TryGetWindow(handle, out _))
        {
            ctx.SetLastError(1400);
            return 0;
        }
        var wasEnabled = manager.IsEnabled(handle);
        var enable = ctx.ArgBool(1);
        manager.SetEnabled(handle, enable);
        SendCore(ctx, manager, handle, Win32.WM_ENABLE, enable ? 1UL : 0UL, 0);
        return wasEnabled ? 0UL : 1UL;
    }

    [VirtualApi("user32.dll", "IsWindow", ApiCategory.Gui)]
    private static ulong IsWindow(ApiCallContext ctx) =>
        Manager(ctx).TryGetWindow(ctx.Arg(0), out _) ? 1UL : 0UL;

    [VirtualApi("user32.dll", "IsWindowVisible", ApiCategory.Gui)]
    private static ulong IsWindowVisible(ApiCallContext ctx) =>
        Manager(ctx).IsVisible(ctx.Arg(0)) ? 1UL : 0UL;

    [VirtualApi("user32.dll", "IsWindowEnabled", ApiCategory.Gui)]
    private static ulong IsWindowEnabled(ApiCallContext ctx) =>
        Manager(ctx).IsEnabled(ctx.Arg(0)) ? 1UL : 0UL;

    [VirtualApi("user32.dll", "IsWindowUnicode", ApiCategory.Gui)]
    private static ulong IsWindowUnicode(ApiCallContext ctx) =>
        Manager(ctx).TryGetWindow(ctx.Arg(0), out _) ? 1UL : 0UL;

    [VirtualApi("user32.dll", "GetDlgCtrlID", ApiCategory.Gui)]
    private static ulong GetDlgCtrlID(ApiCallContext ctx) =>
        Manager(ctx).TryGetWindow(ctx.Arg(0), out var window) && window is not null
            ? (ulong)window.ControlId : 0;

    // --- search, ownership, focus and z-order ----------------------------------

    [VirtualApi("user32.dll", "FindWindowW", ApiCategory.Gui)]
    private static ulong FindWindowW(ApiCallContext ctx) => FindWindowCore(ctx, ansi: false);

    [VirtualApi("user32.dll", "FindWindowA", ApiCategory.Gui)]
    private static ulong FindWindowA(ApiCallContext ctx) => FindWindowCore(ctx, ansi: true);

    private static ulong FindWindowCore(ApiCallContext ctx, bool ansi)
    {
        var classPointer = ctx.Arg(0);
        var titlePointer = ctx.Arg(1);
        string? className = null;
        if (classPointer != 0 && classPointer > 0xFFFF)
            className = ansi ? ctx.ReadAnsiString(classPointer) : ctx.ReadUnicodeString(classPointer);
        string? title = titlePointer == 0 ? null
            : ansi ? ctx.ReadAnsiString(titlePointer) : ctx.ReadUnicodeString(titlePointer);
        return Manager(ctx).FindWindow(className, title)?.Handle ?? 0;
    }

    [VirtualApi("user32.dll", "GetParent", ApiCategory.Gui)]
    private static ulong GetParent(ApiCallContext ctx) =>
        Manager(ctx).TryGetWindow(ctx.Arg(0), out var window) && window is not null
            ? window.Parent != 0 ? window.Parent : window.Owner : 0;

    [VirtualApi("user32.dll", "SetParent", ApiCategory.Gui)]
    private static ulong SetParent(ApiCallContext ctx)
    {
        var manager = Manager(ctx);
        var handle = ctx.Arg(0);
        if (!manager.TryGetWindow(handle, out var window) || window is null) return 0;
        var old = window.Parent;
        return manager.SetParent(handle, ctx.Arg(1)) ? old : 0;
    }

    [VirtualApi("user32.dll", "GetForegroundWindow", ApiCategory.Gui)]
    private static ulong GetForegroundWindow(ApiCallContext ctx) => Manager(ctx).ForegroundWindow;

    [VirtualApi("user32.dll", "SetForegroundWindow", ApiCategory.Gui)]
    private static ulong SetForegroundWindow(ApiCallContext ctx)
    {
        var manager = Manager(ctx);
        if (!manager.TryGetWindow(ctx.Arg(0), out _)) return 0;
        manager.Activate(ctx.Arg(0));
        return 1;
    }

    [VirtualApi("user32.dll", "GetActiveWindow", ApiCategory.Gui)]
    private static ulong GetActiveWindow(ApiCallContext ctx) => Manager(ctx).ForegroundWindow;

    [VirtualApi("user32.dll", "SetActiveWindow", ApiCategory.Gui)]
    private static ulong SetActiveWindow(ApiCallContext ctx)
    {
        var manager = Manager(ctx);
        var previous = manager.ForegroundWindow;
        if (!manager.TryGetWindow(ctx.Arg(0), out _)) return previous;
        manager.Activate(ctx.Arg(0));
        return previous;
    }

    [VirtualApi("user32.dll", "GetFocus", ApiCategory.Gui)]
    private static ulong GetFocus(ApiCallContext ctx) => Manager(ctx).FocusWindow;

    [VirtualApi("user32.dll", "SetFocus", ApiCategory.Gui)]
    private static ulong SetFocus(ApiCallContext ctx)
    {
        var manager = Manager(ctx);
        var previous = manager.FocusWindow;
        if (ctx.Arg(0) != 0 && !manager.TryGetWindow(ctx.Arg(0), out _)) return previous;
        manager.SetFocus(ctx.Arg(0));
        return previous;
    }

    [VirtualApi("user32.dll", "BringWindowToTop", ApiCategory.Gui)]
    private static ulong BringWindowToTop(ApiCallContext ctx)
    {
        var manager = Manager(ctx);
        if (!manager.TryGetWindow(ctx.Arg(0), out _)) return 0;
        manager.BringToFront(ctx.Arg(0));
        return 1;
    }

    [VirtualApi("user32.dll", "GetDesktopWindow", ApiCategory.Gui)]
    private static ulong GetDesktopWindow(ApiCallContext ctx) => 0;

    [VirtualApi("user32.dll", "GetWindowThreadProcessId", ApiCategory.Gui)]
    private static ulong GetWindowThreadProcessId(ApiCallContext ctx)
    {
        var manager = Manager(ctx);
        if (!manager.TryGetWindow(ctx.Arg(0), out var window) || window is null) return 0;
        if (ctx.Arg(1) != 0) ctx.Write(ctx.Arg(1), (uint)window.ProcessId);
        return (ulong)window.ThreadId;
    }

    // --- message loop -----------------------------------------------------------

    [VirtualApi("user32.dll", "GetMessageW", ApiCategory.Gui)]
    private static ulong GetMessageW(ApiCallContext ctx) => GetMessageCore(ctx);

    [VirtualApi("user32.dll", "GetMessageA", ApiCategory.Gui)]
    private static ulong GetMessageA(ApiCallContext ctx) => GetMessageCore(ctx);

    private static ulong GetMessageCore(ApiCallContext ctx)
    {
        var messagePointer = ctx.Arg(0);
        if (messagePointer == 0)
        {
            ctx.SetLastError(Win32Error.InvalidParameter);
            return unchecked((ulong)(long)-1);
        }
        var manager = Manager(ctx);
        var cancelled = () => ctx.Thread.TerminationRequested || ctx.Process.IsTerminating;
        if (!manager.GetMessage(ctx.Thread.Id, ctx.Arg(1), ctx.Arg32(2), ctx.Arg32(3), cancelled, out var message))
            return 0;
        WriteMessage(ctx, messagePointer, message);
        return message.Message == Win32.WM_QUIT ? 0UL : 1UL;
    }

    [VirtualApi("user32.dll", "PeekMessageW", ApiCategory.Gui)]
    private static ulong PeekMessageW(ApiCallContext ctx) => PeekMessageCore(ctx);

    [VirtualApi("user32.dll", "PeekMessageA", ApiCategory.Gui)]
    private static ulong PeekMessageA(ApiCallContext ctx) => PeekMessageCore(ctx);

    private static ulong PeekMessageCore(ApiCallContext ctx)
    {
        var messagePointer = ctx.Arg(0);
        if (messagePointer == 0)
        {
            ctx.SetLastError(Win32Error.InvalidParameter);
            return 0;
        }
        var remove = (ctx.Arg32(4) & Win32.PM_REMOVE) != 0;
        if (!Manager(ctx).PeekMessage(ctx.Thread.Id, ctx.Arg(1), ctx.Arg32(2), ctx.Arg32(3), remove, out var message))
            return 0;
        WriteMessage(ctx, messagePointer, message);
        return 1;
    }

    [VirtualApi("user32.dll", "TranslateMessage", ApiCategory.Gui)]
    private static ulong TranslateMessage(ApiCallContext ctx) => 0;

    [VirtualApi("user32.dll", "DispatchMessageW", ApiCategory.Gui)]
    private static ulong DispatchMessageW(ApiCallContext ctx) => DispatchMessageCore(ctx);

    [VirtualApi("user32.dll", "DispatchMessageA", ApiCategory.Gui)]
    private static ulong DispatchMessageA(ApiCallContext ctx) => DispatchMessageCore(ctx);

    private static ulong DispatchMessageCore(ApiCallContext ctx)
    {
        var pointer = ctx.Arg(0);
        if (pointer == 0) return 0;
        var message = ctx.Read<NativeMessage>(pointer);
        if (message.Hwnd == 0) return 0;
        return SendCore(ctx, Manager(ctx), message.Hwnd, message.Message, message.WParam, message.LParam);
    }

    [VirtualApi("user32.dll", "PostMessageW", ApiCategory.Gui)]
    private static ulong PostMessageW(ApiCallContext ctx) => PostMessageCore(ctx);

    [VirtualApi("user32.dll", "PostMessageA", ApiCategory.Gui)]
    private static ulong PostMessageA(ApiCallContext ctx) => PostMessageCore(ctx);

    private static ulong PostMessageCore(ApiCallContext ctx)
    {
        var manager = Manager(ctx);
        var handle = ctx.Arg(0);
        var message = ctx.Arg32(1);
        var wParam = ctx.Arg(2);
        var lParam = ctx.Arg(3);
        if (handle == 0)
            return manager.PostThreadMessage(ctx.Thread.Id, message, wParam, unchecked((long)lParam)) ? 1UL : 0UL;
        return manager.PostMessage(handle, message, wParam, unchecked((long)lParam)) ? 1UL : 0UL;
    }

    [VirtualApi("user32.dll", "PostThreadMessageW", ApiCategory.Gui)]
    private static ulong PostThreadMessageW(ApiCallContext ctx) => PostThreadMessageCore(ctx);

    [VirtualApi("user32.dll", "PostThreadMessageA", ApiCategory.Gui)]
    private static ulong PostThreadMessageA(ApiCallContext ctx) => PostThreadMessageCore(ctx);

    private static ulong PostThreadMessageCore(ApiCallContext ctx)
    {
        var threadId = unchecked((int)ctx.Arg32(0));
        if (threadId == 0) return 0;
        return Manager(ctx).PostThreadMessage(threadId, ctx.Arg32(1), ctx.Arg(2),
            unchecked((long)ctx.Arg(3))) ? 1UL : 0UL;
    }

    [VirtualApi("user32.dll", "SendMessageW", ApiCategory.Gui)]
    private static ulong SendMessageW(ApiCallContext ctx) => SendMessageCore(ctx);

    [VirtualApi("user32.dll", "SendMessageA", ApiCategory.Gui)]
    private static ulong SendMessageA(ApiCallContext ctx) => SendMessageCore(ctx);

    private static ulong SendMessageCore(ApiCallContext ctx) =>
        SendCore(ctx, Manager(ctx), ctx.Arg(0), ctx.Arg32(1), ctx.Arg(2), unchecked((long)ctx.Arg(3)));

    [VirtualApi("user32.dll", "PostQuitMessage", ApiCategory.Gui)]
    private static ulong PostQuitMessage(ApiCallContext ctx)
    {
        Manager(ctx).PostQuitMessage(ctx.Thread.Id, unchecked((int)ctx.Arg32(0)));
        return 0;
    }

    [VirtualApi("user32.dll", "GetMessagePos", ApiCategory.Gui)]
    private static ulong GetMessagePos(ApiCallContext ctx)
    {
        var (x, y) = Manager(ctx).GetCursorPosition();
        return (uint)(ushort)x | ((uint)(ushort)y << 16);
    }

    [VirtualApi("user32.dll", "GetMessageTime", ApiCategory.Gui)]
    private static ulong GetMessageTime(ApiCallContext ctx) => unchecked((uint)Environment.TickCount64);

    [VirtualApi("user32.dll", "WaitMessage", ApiCategory.Gui)]
    private static ulong WaitMessage(ApiCallContext ctx) => 1;

    // --- system metrics and cursor ---------------------------------------------

    [VirtualApi("user32.dll", "GetSystemMetrics", ApiCategory.Gui)]
    private static ulong GetSystemMetrics(ApiCallContext ctx)
    {
        var manager = Manager(ctx);
        return ctx.ArgInt32(0) switch
        {
            Win32.SM_CXSCREEN => (ulong)manager.ScreenWidth,
            Win32.SM_CYSCREEN => (ulong)manager.ScreenHeight,
            Win32.SM_CXVSCROLL => 17,
            Win32.SM_CYHSCROLL => 17,
            Win32.SM_CYCAPTION => WindowGeometry.CaptionHeight,
            Win32.SM_CXBORDER => WindowGeometry.BorderWidth,
            Win32.SM_CYBORDER => WindowGeometry.BorderWidth,
            Win32.SM_CXFRAME => 4,
            Win32.SM_CYFRAME => 4,
            Win32.SM_CXMIN => 112,
            Win32.SM_CYMIN => 28,
            Win32.SM_CXMAXIMIZED => (ulong)manager.ScreenWidth,
            Win32.SM_CYMAXIMIZED => (ulong)manager.ScreenHeight,
            _ => 0
        };
    }

    [VirtualApi("user32.dll", "GetCursorPos", ApiCategory.Gui)]
    private static ulong GetCursorPos(ApiCallContext ctx)
    {
        var output = ctx.Arg(0);
        if (output == 0) return 0;
        var (x, y) = Manager(ctx).GetCursorPosition();
        ctx.Write(output, new NativePoint { X = x, Y = y });
        return 1;
    }

    [VirtualApi("user32.dll", "SetCursorPos", ApiCategory.Gui)]
    private static ulong SetCursorPos(ApiCallContext ctx)
    {
        Manager(ctx).SetCursorPosition(ctx.ArgInt32(0), ctx.ArgInt32(1));
        return 1;
    }

    [VirtualApi("user32.dll", "LoadCursorW", ApiCategory.Gui)]
    private static ulong LoadCursorW(ApiCallContext ctx) => CursorHandle;

    [VirtualApi("user32.dll", "LoadCursorA", ApiCategory.Gui)]
    private static ulong LoadCursorA(ApiCallContext ctx) => CursorHandle;

    [VirtualApi("user32.dll", "LoadIconW", ApiCategory.Gui)]
    private static ulong LoadIconW(ApiCallContext ctx) => IconHandle;

    [VirtualApi("user32.dll", "LoadIconA", ApiCategory.Gui)]
    private static ulong LoadIconA(ApiCallContext ctx) => IconHandle;

    [VirtualApi("user32.dll", "LoadImageW", ApiCategory.Gui)]
    private static ulong LoadImageW(ApiCallContext ctx) => IconHandle;

    [VirtualApi("user32.dll", "LoadImageA", ApiCategory.Gui)]
    private static ulong LoadImageA(ApiCallContext ctx) => IconHandle;

    [VirtualApi("user32.dll", "SetCursor", ApiCategory.Gui)]
    private static ulong SetCursor(ApiCallContext ctx) => 0;

    [VirtualApi("user32.dll", "GetCursor", ApiCategory.Gui)]
    private static ulong GetCursor(ApiCallContext ctx) => CursorHandle;

    // --- keyboard state ---------------------------------------------------------

    [VirtualApi("user32.dll", "GetKeyState", ApiCategory.Gui)]
    private static ulong GetKeyState(ApiCallContext ctx)
    {
        var state = Manager(ctx).GetKeyState(ctx.ArgInt32(0));
        return unchecked((ulong)(long)(short)state);
    }

    [VirtualApi("user32.dll", "GetAsyncKeyState", ApiCategory.Gui)]
    private static ulong GetAsyncKeyState(ApiCallContext ctx)
    {
        var state = Manager(ctx).GetKeyState(ctx.ArgInt32(0));
        return unchecked((ulong)(long)(short)state);
    }

    [VirtualApi("user32.dll", "GetKeyboardState", ApiCategory.Gui)]
    private static ulong GetKeyboardState(ApiCallContext ctx)
    {
        var output = ctx.Arg(0);
        if (output == 0) return 0;
        ctx.WriteBytes(output, Manager(ctx).GetKeyboardState());
        return 1;
    }

    // --- timers and invalidation -----------------------------------------------

    [VirtualApi("user32.dll", "SetTimer", ApiCategory.Gui)]
    private static ulong SetTimer(ApiCallContext ctx)
    {
        if (ctx.Arg(3) != 0) return 0; // Callback timers are intentionally unsupported.
        var timer = Manager(ctx).SetTimer(ctx.Arg(0), ctx.Arg(1), ctx.Arg32(2));
        return timer;
    }

    [VirtualApi("user32.dll", "KillTimer", ApiCategory.Gui)]
    private static ulong KillTimer(ApiCallContext ctx) =>
        Manager(ctx).KillTimer(ctx.Arg(0), ctx.Arg(1)) ? 1UL : 0UL;

    [VirtualApi("user32.dll", "InvalidateRect", ApiCategory.Gui)]
    private static ulong InvalidateRect(ApiCallContext ctx)
    {
        var manager = Manager(ctx);
        var handle = ctx.Arg(0);
        if (handle == 0) return 0;
        manager.Drawing?.Invalidate(handle, ctx.ArgBool(2));
        manager.PostMessage(handle, Win32.WM_PAINT, 0, 0);
        return 1;
    }

    [VirtualApi("user32.dll", "ValidateRect", ApiCategory.Gui)]
    private static ulong ValidateRect(ApiCallContext ctx)
    {
        Manager(ctx).Drawing?.Validate(ctx.Arg(0));
        return 1;
    }

    [VirtualApi("user32.dll", "RedrawWindow", ApiCategory.Gui)]
    private static ulong RedrawWindow(ApiCallContext ctx)
    {
        var manager = Manager(ctx);
        var handle = ctx.Arg(0);
        if (handle == 0) return 0;
        manager.Drawing?.Invalidate(handle, (ctx.Arg32(3) & 4) != 0);
        SendCore(ctx, manager, handle, Win32.WM_PAINT, 0, 0);
        return 1;
    }

    // --- system colours and misc -------------------------------------------------

    [VirtualApi("user32.dll", "GetSysColor", ApiCategory.Gui)]
    private static ulong GetSysColor(ApiCallContext ctx) => Manager(ctx).GetSysColorRef(ctx.ArgInt32(0));

    [VirtualApi("user32.dll", "GetSysColorBrush", ApiCategory.Gui)]
    private static ulong GetSysColorBrush(ApiCallContext ctx)
    {
        var index = Math.Clamp(ctx.ArgInt32(0), 0, 30);
        return Manager(ctx).Drawing?.GetSystemBrush(index) ?? (ulong)(index + 1);
    }

    [VirtualApi("user32.dll", "MessageBeep", ApiCategory.Gui)]
    private static ulong MessageBeep(ApiCallContext ctx) => 1;

    // --- helpers ----------------------------------------------------------------

    private static ulong WriteGuestString(ApiCallContext ctx, ulong buffer, int capacity, string text, bool ansi)
    {
        if (buffer == 0 || capacity <= 0) return 0;
        text ??= string.Empty;
        var count = Math.Min(text.Length, capacity - 1);
        if (ansi)
        {
            var bytes = Encoding.Latin1.GetBytes(text[..count]);
            ctx.WriteBytes(buffer, bytes);
            ctx.Write<byte>(buffer + (ulong)bytes.Length, 0);
        }
        else
        {
            var bytes = Encoding.Unicode.GetBytes(text[..count]);
            ctx.WriteBytes(buffer, bytes);
            ctx.Write<ushort>(buffer + (ulong)bytes.Length, 0);
        }
        return (ulong)count;
    }

    private static void WriteMessage(ApiCallContext ctx, ulong pointer, WindowMessage message)
    {
        ctx.Write(pointer, new NativeMessage
        {
            Hwnd = message.Hwnd,
            Message = message.Message,
            WParam = message.WParam,
            LParam = message.LParam,
            Time = message.Time,
            PointX = message.X,
            PointY = message.Y
        });
    }
}
