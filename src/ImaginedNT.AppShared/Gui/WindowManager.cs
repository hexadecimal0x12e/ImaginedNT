using ImaginedNT.Core.Diagnostics;
using ImaginedNT.Core.Runtime;

namespace ImaginedNT.AppShared.Gui;

/// <summary>
/// The App-side window manager and the simplest possible DWM model.  It owns window classes,
/// window geometry, z-order, focus, the virtual screen and the per-guest-thread message queues.
/// It contains no UWP types so the compositor, input forwarding and the USER32 shims can be
/// tested without a XAML host.
/// </summary>
public sealed class WindowManager : IDisposable
{
    public IWindowDrawing? Drawing { get; set; }
    private static readonly string[] BuiltInClasses = ["STATIC", "BUTTON", "EDIT", "COMBOBOX", "LISTBOX", "SCROLLBAR"];

    private static readonly uint[] LightColorsArgb =
    [
        0xFFC8C8C8, // COLOR_SCROLLBAR
        0xFFE8EBEF, // COLOR_BACKGROUND / DESKTOP
        0xFFF3F3F3, // COLOR_ACTIVECAPTION
        0xFFF3F3F3, // COLOR_INACTIVECAPTION
        0xFFF0F0F0, // COLOR_MENU
        0xFFFFFFFF, // COLOR_WINDOW
        0xFF808080, // COLOR_WINDOWFRAME
        0xFF000000, // COLOR_MENUTEXT
        0xFF000000, // COLOR_WINDOWTEXT
        0xFF000000, // COLOR_CAPTIONTEXT
        0xFF808080, // COLOR_ACTIVEBORDER
        0xFFB8B8B8, // COLOR_INACTIVEBORDER
        0xFFABABAB, // COLOR_APPWORKSPACE
        0xFF0078D7, // COLOR_HIGHLIGHT
        0xFFFFFFFF, // COLOR_HIGHLIGHTTEXT
        0xFFF0F0F0, // COLOR_BTNFACE
        0xFFA0A0A0, // COLOR_BTNSHADOW
        0xFF6D6D6D, // COLOR_GRAYTEXT
        0xFF000000, // COLOR_BTNTEXT
        0xFF777777, // COLOR_INACTIVECAPTIONTEXT
        0xFFFFFFFF, // COLOR_BTNHIGHLIGHT
        0xFF696969, // COLOR_3DDKSHADOW
        0xFFE3E3E3, // COLOR_3DLIGHT
        0xFF000000, // COLOR_INFOTEXT
        0xFFFFFFE1, // COLOR_INFOBK
        0xFFF0F0F0, // COLOR_25 (unused)
        0xFF0066CC, // COLOR_HOTLIGHT
        0xFFF3F3F3, // COLOR_GRADIENTACTIVECAPTION
        0xFFF3F3F3, // COLOR_GRADIENTINACTIVECAPTION
        0xFF0078D7, // COLOR_MENUHILIGHT
        0xFFF0F0F0  // COLOR_MENUBAR
    ];

    private static readonly uint[] DarkColorsArgb =
    [
        0xFF2A2E35, // COLOR_SCROLLBAR
        0xFF101318, // COLOR_BACKGROUND / DESKTOP
        0xFF1F6FEB, // COLOR_ACTIVECAPTION
        0xFF30363D, // COLOR_INACTIVECAPTION
        0xFF20242B, // COLOR_MENU
        0xFF1B1F24, // COLOR_WINDOW
        0xFF30363D, // COLOR_WINDOWFRAME
        0xFFE6EDF3, // COLOR_MENUTEXT
        0xFFE6EDF3, // COLOR_WINDOWTEXT
        0xFFF0F6FC, // COLOR_CAPTIONTEXT
        0xFF388BFD, // COLOR_ACTIVEBORDER
        0xFF30363D, // COLOR_INACTIVEBORDER
        0xFF0D1117, // COLOR_APPWORKSPACE
        0xFF1F6FEB, // COLOR_HIGHLIGHT
        0xFFFFFFFF, // COLOR_HIGHLIGHTTEXT
        0xFF2D333B, // COLOR_BTNFACE
        0xFF161B22, // COLOR_BTNSHADOW
        0xFF8B949E, // COLOR_GRAYTEXT
        0xFFE6EDF3, // COLOR_BTNTEXT
        0xFF8B949E, // COLOR_INACTIVECAPTIONTEXT
        0xFF444C56, // COLOR_BTNHIGHLIGHT
        0xFF010409, // COLOR_3DDKSHADOW
        0xFF444C56, // COLOR_3DLIGHT
        0xFFE6EDF3, // COLOR_INFOTEXT
        0xFF20242B, // COLOR_INFOBK
        0xFF20242B, // COLOR_25 (unused)
        0xFF58A6FF, // COLOR_HOTLIGHT
        0xFF1F6FEB, // COLOR_GRADIENTACTIVECAPTION
        0xFF30363D, // COLOR_GRADIENTINACTIVECAPTION
        0xFF1F6FEB, // COLOR_MENUHILIGHT
        0xFF20242B  // COLOR_MENUBAR
    ];

    private readonly uint[] _systemColorsArgb;
    private readonly bool _respectWindowBorderStyle;
    private readonly ImaginedRuntime _runtime;
    private readonly object _gate = new();
    private readonly Dictionary<ulong, GuestWindow> _windows = new();
    private readonly Dictionary<int, Dictionary<string, WindowClassInfo>> _classes = new();
    private readonly Dictionary<(int ProcessId, ushort Atom), WindowClassInfo> _atoms = new();
    private readonly Dictionary<int, ThreadMessageQueue> _queues = new();
    private readonly Dictionary<(ulong Hwnd, ulong Id), TimerEntry> _timers = new();
    private readonly List<GuestWindow> _topLevels = new(); // bottom to top
    private readonly bool[] _keysDown = new bool[256];
    private readonly bool[] _keysToggled = new bool[256];
    private ulong _nextHandle = 0x0000_0000_0001_0000;
    private ushort _nextAtom = 0xC000;
    private int _version;
    private int _screenWidth = 1280;
    private int _screenHeight = 800;
    private int _cascade;
    private ulong _focusHwnd;
    private ulong _foregroundHwnd;
    private ulong _hoverHwnd;
    private int _mouseX;
    private int _mouseY;
    private ulong _mouseDownHwnd;
    private WindowHitRegion _mouseDownRegion;
    private int _pressedButton = -1;
    private DesktopObject? _viewDesktop;
    private DesktopObject? _displayDesktop;
    private DesktopObject? _inputDesktop;
    private readonly Dictionary<DesktopObject, (ulong Focus, ulong Foreground)> _desktopFocus = new();
    private readonly Dictionary<DesktopObject, ulong> _desktopHandles = new();

    public ulong GetDesktopWindow(DesktopObject desktop)
    {
        lock (_gate)
        {
            if (!_runtime.WindowStations.Contains(desktop)) return 0;
            if (!_desktopHandles.TryGetValue(desktop, out var handle)) _desktopHandles[desktop] = handle = _nextHandle++;
            return handle;
        }
    }

    public bool IsDesktopWindow(ulong handle)
    {
        lock (_gate) return _desktopHandles.Any(p => p.Value == handle && _runtime.WindowStations.Contains(p.Key));
    }

    public DesktopObject? DisplayDesktop
    {
        get { lock (_gate) { SyncDesktopLocked(); return _displayDesktop; } }
    }

    public bool IsInputDesktop
    {
        get
        {
            lock (_gate)
            {
                SyncDesktopLocked();
                return _displayDesktop is not null && _displayDesktop == _inputDesktop;
            }
        }
    }

    public void ViewDesktop(DesktopObject? desktop)
    {
        lock (_gate)
        {
            if (desktop is not null && !_runtime.WindowStations.Contains(desktop))
                throw new ArgumentException("桌面不属于此运行时。");
            _viewDesktop = desktop;
            SyncDesktopLocked();
        }
    }

    private void SyncDesktopLocked()
    {
        if (_viewDesktop is not null && !_runtime.WindowStations.Contains(_viewDesktop)) _viewDesktop = null;
        var input = _runtime.ActiveConsoleSessionId is uint id ? _runtime.WindowStations.GetInputDesktop(id) : null;
        var desktop = _viewDesktop ?? input;
        if (_displayDesktop == desktop && _inputDesktop == input) return;
        ReleaseKeyboard();
        if (_displayDesktop is not null) _desktopFocus[_displayDesktop] = (_focusHwnd, _foregroundHwnd);
        SetFocusLocked(0);
        _displayDesktop = desktop;
        _inputDesktop = input;
        _foregroundHwnd = 0;
        _hoverHwnd = 0;
        _mouseDownHwnd = 0;
        _pressedButton = -1;
        Array.Clear(_keysToggled);
        if (desktop is not null && _desktopFocus.TryGetValue(desktop, out var focus))
        {
            if (_windows.ContainsKey(focus.Foreground)) _foregroundHwnd = focus.Foreground;
            if (_windows.ContainsKey(focus.Focus)) SetFocusLocked(focus.Focus);
        }
        BumpVersionLocked();
    }

    private bool CanInputLocked(ulong handle)
    {
        SyncDesktopLocked();
        return _displayDesktop is not null && _displayDesktop == _inputDesktop &&
            _windows.TryGetValue(handle, out var window) && window.Desktop == _displayDesktop;
    }

    private sealed class TimerEntry(ulong id, ulong hwnd, Timer timer) : IDisposable
    {
        public ulong Id { get; } = id;
        public ulong Hwnd { get; } = hwnd;
        public Timer Timer { get; } = timer;
        public void Dispose() => Timer.Dispose();
    }

    public WindowManager(ImaginedRuntime runtime, bool useLightTheme = false, bool respectWindowBorderStyle = false)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _systemColorsArgb = useLightTheme ? LightColorsArgb : DarkColorsArgb;
        _respectWindowBorderStyle = respectWindowBorderStyle;
        _runtime.ProcessExited += CleanupProcess;
        _runtime.ThreadExited += CleanupThread;
    }

    public int Version
    {
        get { lock (_gate) { SyncDesktopLocked(); return _version; } }
    }

    public int ScreenWidth
    {
        get { lock (_gate) return _screenWidth; }
    }

    public int ScreenHeight
    {
        get { lock (_gate) return _screenHeight; }
    }

    public void SetScreenSize(int width, int height)
    {
        ulong[] resized;
        lock (_gate)
        {
            width = Math.Max(1, width);
            height = Math.Max(1, height);
            if (_screenWidth == width && _screenHeight == height) return;
            _screenWidth = width;
            _screenHeight = height;
            resized = _topLevels.Where(w => w.Maximized).Select(w => w.Handle).ToArray();
            foreach (var handle in resized)
            {
                MoveWindow(handle, 0, 0, width, height);
                var client = ClientLocked(_windows[handle]);
                if (!_windows[handle].Minimized)
                    PostMessageLocked(handle, Win32.WM_SIZE, Win32.SIZE_MAXIMIZED, Win32.MakeSize(client.Width, client.Height));
            }
            BumpVersionLocked();
        }
        foreach (var handle in resized)
        {
            Drawing?.Invalidate(handle, erase: true);
            PostMessage(handle, Win32.WM_PAINT, 0, 0);
        }
    }

    // --- window classes ---------------------------------------------------------

    internal WindowClassInfo? RegisterClass(int processId, string name, ulong wndProc, uint style,
        ulong backgroundBrush, ulong instance, string? menuName)
    {
        name = name.Trim();
        if (name.Length == 0 || name.Length > 256) return null;
        lock (_gate)
        {
            var classes = GetClassMapLocked(processId);
            if (classes.ContainsKey(name) || BuiltInClasses.Contains(name, StringComparer.OrdinalIgnoreCase)) return null;
            var atom = _nextAtom++;
            if (_nextAtom == 0) _nextAtom = 0xC000;
            var info = new WindowClassInfo
            {
                ProcessId = processId,
                Name = name,
                Atom = atom,
                WndProc = wndProc,
                Style = style,
                BackgroundBrush = backgroundBrush,
                Instance = instance,
                MenuName = menuName
            };
            classes.Add(name, info);
            _atoms[(processId, atom)] = info;
            Trace($"registered window class '{name}' atom=0x{atom:X4} proc=0x{wndProc:X16}", processId);
            return info;
        }
    }

    internal WindowClassInfo? FindClass(int processId, string nameOrAtom)
    {
        if (string.IsNullOrWhiteSpace(nameOrAtom)) return null;
        if (nameOrAtom.StartsWith('#'))
        {
            if (ushort.TryParse(nameOrAtom.AsSpan(1), out var atom)) return FindClass(processId, atom);
        }
        lock (_gate)
        {
            if (GetClassMapLocked(processId).TryGetValue(nameOrAtom, out var registered)) return registered;
            if (BuiltInClasses.Contains(nameOrAtom, StringComparer.OrdinalIgnoreCase))
                return GetBuiltInClassLocked(processId, nameOrAtom);
            return null;
        }
    }

    internal WindowClassInfo? FindClass(int processId, ushort atom)
    {
        lock (_gate)
        {
            if (_atoms.TryGetValue((processId, atom), out var registered)) return registered;
            return null;
        }
    }

    public bool UnregisterClass(int processId, string nameOrAtom)
    {
        lock (_gate)
        {
            var classes = GetClassMapLocked(processId);
            WindowClassInfo? info = null;
            if (nameOrAtom.StartsWith('#'))
            {
                if (ushort.TryParse(nameOrAtom.AsSpan(1), out var atom))
                    _atoms.Remove((processId, atom), out info);
            }
            else
            {
                classes.Remove(nameOrAtom, out info);
            }
            if (info is null) return false;
            classes.Remove(info.Name);
            _atoms.Remove((processId, info.Atom));
            return true;
        }
    }

    private Dictionary<string, WindowClassInfo> GetClassMapLocked(int processId)
    {
        if (!_classes.TryGetValue(processId, out var classes))
        {
            classes = new Dictionary<string, WindowClassInfo>(StringComparer.OrdinalIgnoreCase);
            _classes.Add(processId, classes);
        }
        return classes;
    }

    private WindowClassInfo GetBuiltInClassLocked(int processId, string name)
    {
        var classes = GetClassMapLocked(processId);
        if (classes.TryGetValue(name, out var existing)) return existing;
        var info = new WindowClassInfo
        {
            ProcessId = processId,
            Name = name,
            Atom = _nextAtom++,
            WndProc = 0,
            BackgroundBrush = name.Equals("BUTTON", StringComparison.OrdinalIgnoreCase) ||
                              name.Equals("STATIC", StringComparison.OrdinalIgnoreCase)
                ? (uint)(Win32.COLOR_BTNFACE + 1)
                : (uint)(Win32.COLOR_WINDOW + 1),
            BuiltIn = true
        };
        classes.Add(name, info);
        return info;
    }

    // --- windows ----------------------------------------------------------------

    internal GuestWindow? CreateWindow(int processId, int threadId, WindowClassInfo windowClass, string title,
        uint style, uint exStyle, int x, int y, int width, int height, ulong parent, ulong menu, ulong instance)
    {
        lock (_gate)
        {
            if (_runtime.GetProcessById(processId) is not { IsTerminating: false } process ||
                process.FindThreadByVirtualId(threadId) is not { } thread) return null;
            if (thread.Desktop.WindowStation != process.WindowStation) return null;
            var isChild = (style & Win32.WS_CHILD) != 0 && parent != 0;
            GuestWindow? parentWindow = null;
            if (parent != 0 && _windows.TryGetValue(parent, out var foundParent) &&
                foundParent.ProcessId == processId && foundParent.Desktop == thread.Desktop)
            {
                parentWindow = foundParent;
            }
            if ((style & Win32.WS_CHILD) != 0 && parentWindow is null) return null;
            if (parent != 0 && parentWindow is null) return null;

            if (x == Win32.CW_USEDEFAULT)
                x = 40 + (_cascade % 12) * 28;
            if (y == Win32.CW_USEDEFAULT)
                y = 32 + (_cascade % 12) * 24;
            _cascade++;
            if (width <= 0) width = isChild ? 120 : 640;
            if (height <= 0) height = isChild ? 32 : 420;

            var handle = _nextHandle++;
            var window = new GuestWindow
            {
                Handle = handle,
                Thread = thread,
                Desktop = _runtime.WindowStations.RegisterWindow(thread),
                ProcessId = processId,
                ThreadId = threadId,
                ClassName = windowClass.Name,
                WndProc = windowClass.WndProc,
                Parent = parentWindow?.Handle ?? 0,
                Owner = isChild ? 0 : parent,
                Menu = menu,
                Instance = instance,
                Title = title,
                Style = style,
                ExStyle = exStyle,
                BackgroundBrush = windowClass.BackgroundBrush,
                X = x,
                Y = y,
                Width = width,
                Height = height,
                Visible = (style & Win32.WS_VISIBLE) != 0,
                Enabled = (style & Win32.WS_DISABLED) == 0
            };
            _windows.Add(handle, window);
            if (parentWindow is not null)
            {
                window.ZOrder = parentWindow.Children.Count;
                parentWindow.Children.Add(window);
            }
            else
            {
                window.ZOrder = _topLevels.Count;
                _topLevels.Add(window);
                if ((exStyle & Win32.WS_EX_TOPMOST) != 0) MoveToTopLocked(window);
            }
            BumpVersionLocked();
            EnsureQueueLocked(threadId);
            Trace($"created window 0x{handle:X} class='{windowClass.Name}' title='{title}' " +
                  $"rect=({x},{y},{width},{height}) style=0x{style:X8} ex=0x{exStyle:X8}", processId, threadId);
            return window;
        }
    }

    internal bool TryGetWindow(ulong handle, out GuestWindow? window)
    {
        lock (_gate) return _windows.TryGetValue(handle, out window);
    }

    internal IReadOnlyList<GuestWindow> GetChildren(ulong handle)
    {
        lock (_gate)
            return _windows.TryGetValue(handle, out var window) ? window.Children.ToArray() : [];
    }

    internal GuestWindow? GetTopLevel(ulong handle)
    {
        lock (_gate)
        {
            if (!_windows.TryGetValue(handle, out var window)) return null;
            while (window.IsChild && window.Parent != 0)
            {
                if (!_windows.TryGetValue(window.Parent, out var parent)) break;
                window = parent;
            }
            return window;
        }
    }

    public bool RemoveWindow(ulong handle)
    {
        lock (_gate)
        {
            if (!_windows.TryGetValue(handle, out var window)) return false;
            RemoveWindowLocked(window);
            BumpVersionLocked();
            return true;
        }
    }

    private void RemoveWindowLocked(GuestWindow window)
    {
        if (window.Destroyed) return;
        foreach (var child in window.Children.ToArray()) RemoveWindowLocked(child);
        window.Children.Clear();
        window.Destroyed = true;
        _runtime.WindowStations.UnregisterWindow(window.Thread, window.Desktop);
        _windows.Remove(window.Handle);
        if (_desktopFocus.TryGetValue(window.Desktop, out var focus))
            _desktopFocus[window.Desktop] = (focus.Focus == window.Handle ? 0 : focus.Focus,
                focus.Foreground == window.Handle ? 0 : focus.Foreground);
        if (window.Parent != 0 && _windows.TryGetValue(window.Parent, out var parent))
            parent.Children.Remove(window);
        else
            _topLevels.Remove(window);
        StopTimersLocked(window.Handle);
        if (_focusHwnd == window.Handle) _focusHwnd = 0;
        if (_foregroundHwnd == window.Handle) _foregroundHwnd = 0;
        if (_hoverHwnd == window.Handle) _hoverHwnd = 0;
        if (_mouseDownHwnd == window.Handle) _mouseDownHwnd = 0;
        ReindexLocked();
    }

    private void ReindexLocked()
    {
        for (var i = 0; i < _topLevels.Count; i++) _topLevels[i].ZOrder = i;
        foreach (var window in _windows.Values)
            for (var i = 0; i < window.Children.Count; i++) window.Children[i].ZOrder = i;
    }

    public bool SetTitle(ulong handle, string title)
    {
        lock (_gate)
        {
            if (!_windows.TryGetValue(handle, out var window)) return false;
            title ??= string.Empty;
            if (window.Title == title) return true;
            window.Title = title;
            BumpVersionLocked();
            return true;
        }
    }

    public string GetTitle(ulong handle)
    {
        lock (_gate) return _windows.TryGetValue(handle, out var window) ? window.Title : string.Empty;
    }

    public bool SetVisible(ulong handle, bool visible)
    {
        lock (_gate)
        {
            if (!_windows.TryGetValue(handle, out var window)) return false;
            if (window.Visible == visible) return true;
            window.Visible = visible;
            window.Style = visible ? window.Style | Win32.WS_VISIBLE : window.Style & ~Win32.WS_VISIBLE;
            if (!visible)
            {
                if (_focusHwnd == handle) SetFocusLocked(0);
                if (_hoverHwnd == handle) _hoverHwnd = 0;
            }
            BumpVersionLocked();
            return true;
        }
    }

    public bool IsVisible(ulong handle)
    {
        lock (_gate)
        {
            if (IsDesktopWindow(handle)) return true;
            if (!_windows.TryGetValue(handle, out var window)) return false;
            while (true)
            {
                if (!window.Visible || window.Minimized) return false;
                if (!window.IsChild || window.Parent == 0) return true;
                if (!_windows.TryGetValue(window.Parent, out var parent)) return false;
                window = parent;
            }
        }
    }

    public bool SetEnabled(ulong handle, bool enabled)
    {
        lock (_gate)
        {
            if (!_windows.TryGetValue(handle, out var window)) return false;
            window.Enabled = enabled;
            window.Style = enabled ? window.Style & ~Win32.WS_DISABLED : window.Style | Win32.WS_DISABLED;
            BumpVersionLocked();
            return true;
        }
    }

    public bool IsEnabled(ulong handle)
    {
        lock (_gate) return IsDesktopWindow(handle) || _windows.TryGetValue(handle, out var window) && window.Enabled;
    }

    public bool SetStyle(ulong handle, uint style)
    {
        lock (_gate)
        {
            if (!_windows.TryGetValue(handle, out var window)) return false;
            window.Style = style;
            window.Visible = (style & Win32.WS_VISIBLE) != 0;
            window.Enabled = (style & Win32.WS_DISABLED) == 0;
            BumpVersionLocked();
            return true;
        }
    }

    public bool SetExStyle(ulong handle, uint exStyle)
    {
        lock (_gate)
        {
            if (!_windows.TryGetValue(handle, out var window)) return false;
            window.ExStyle = exStyle;
            BumpVersionLocked();
            return true;
        }
    }

    public bool SetWindowProc(ulong handle, ulong wndProc)
    {
        lock (_gate)
        {
            if (!_windows.TryGetValue(handle, out var window)) return false;
            window.WndProc = wndProc;
            return true;
        }
    }

    public ulong GetWindowProc(ulong handle)
    {
        lock (_gate) return _windows.TryGetValue(handle, out var window) ? window.WndProc : 0;
    }

    public bool SetUserData(ulong handle, ulong value)
    {
        lock (_gate)
        {
            if (!_windows.TryGetValue(handle, out var window)) return false;
            window.UserData = value;
            return true;
        }
    }

    public ulong GetUserData(ulong handle)
    {
        lock (_gate) return _windows.TryGetValue(handle, out var window) ? window.UserData : 0;
    }

    public bool SetParent(ulong handle, ulong parent)
    {
        lock (_gate)
        {
            if (!_windows.TryGetValue(handle, out var window)) return false;
            if (parent != 0 && !_windows.ContainsKey(parent)) return false;
            if (parent != 0 && _windows[parent].Desktop != window.Desktop) return false;
            if (window.IsChild && _windows.TryGetValue(window.Parent, out var oldParent))
                oldParent.Children.Remove(window);
            else if (!window.IsChild) _topLevels.Remove(window);
            window.Parent = parent;
            window.Style = parent == 0 ? window.Style & ~Win32.WS_CHILD : window.Style | Win32.WS_CHILD;
            if (parent != 0 && _windows.TryGetValue(parent, out var newParent))
            {
                window.ZOrder = newParent.Children.Count;
                newParent.Children.Add(window);
            }
            else
            {
                window.ZOrder = _topLevels.Count;
                _topLevels.Add(window);
            }
            BumpVersionLocked();
            return true;
        }
    }

    public bool MoveWindow(ulong handle, int x, int y, int width, int height)
    {
        lock (_gate)
        {
            if (!_windows.TryGetValue(handle, out var window)) return false;
            width = Math.Max(0, width);
            height = Math.Max(0, height);
            if (window.X == x && window.Y == y && window.Width == width && window.Height == height) return true;
            window.X = x;
            window.Y = y;
            window.Width = width;
            window.Height = height;
            BumpVersionLocked();
            return true;
        }
    }

    public bool ResizeWindow(ulong handle, int x, int y, int width, int height)
    {
        lock (_gate)
        {
            if (!_windows.TryGetValue(handle, out var window) || !window.Enabled || window.Maximized ||
                window.Minimized || (window.Style & Win32.WS_THICKFRAME) == 0) return false;
            var previous = OuterLocked(window);
            MoveWindow(handle, x, y, width, height);
            if (previous == OuterLocked(window)) return true;
            var client = ClientLocked(window);
            if (previous.X != x || previous.Y != y)
                PostMessageLocked(handle, Win32.WM_MOVE, 0, Win32.MakePoint(client.X, client.Y));
            PostMessageLocked(handle, Win32.WM_SIZE, Win32.SIZE_RESTORED, Win32.MakeSize(client.Width, client.Height));
        }
        Drawing?.Invalidate(handle, erase: true);
        PostMessage(handle, Win32.WM_PAINT, 0, 0);
        return true;
    }

    internal bool ShowWindow(ulong handle, int command)
    {
        lock (_gate)
        {
            if (!_windows.TryGetValue(handle, out var window)) return false;
            var previous = OuterLocked(window);
            var wasMinimized = window.Minimized;
            var wasMaximized = window.Maximized;
            SetVisible(handle, command != Win32.SW_HIDE);
            switch (command)
            {
                case Win32.SW_SHOWMINIMIZED:
                case Win32.SW_MINIMIZE:
                case Win32.SW_SHOWMINNOACTIVE:
                    window.Minimized = true;
                    break;
                case Win32.SW_SHOWMAXIMIZED:
                    if (!window.Maximized) window.RestoreRect = previous;
                    window.Minimized = false;
                    window.Maximized = true;
                    MoveWindow(handle, 0, 0, _screenWidth, _screenHeight);
                    break;
                case Win32.SW_SHOWNORMAL:
                case Win32.SW_RESTORE:
                    window.Minimized = false;
                    // Restoring a minimized maximized window returns to its maximized state.
                    if (!wasMinimized || command == Win32.SW_SHOWNORMAL)
                    {
                        window.Maximized = false;
                        if (window.RestoreRect is { } restore)
                            MoveWindow(handle, restore.X, restore.Y, restore.Width, restore.Height);
                        window.RestoreRect = null;
                    }
                    break;
            }
            window.Style = window.Minimized ? window.Style | Win32.WS_MINIMIZE : window.Style & ~Win32.WS_MINIMIZE;
            window.Style = window.Maximized ? window.Style | Win32.WS_MAXIMIZE : window.Style & ~Win32.WS_MAXIMIZE;
            if (window.Minimized && _focusHwnd != 0 && _windows.TryGetValue(_focusHwnd, out var focused) &&
                TopLevelLocked(focused)?.Handle == handle) SetFocusLocked(0);
            if (!window.Minimized && command is (Win32.SW_SHOWNORMAL or Win32.SW_RESTORE or Win32.SW_SHOWMAXIMIZED))
                Activate(handle);
            if (wasMinimized != window.Minimized || wasMaximized != window.Maximized || previous != OuterLocked(window))
            {
                var client = ClientLocked(window);
                if (previous.X != window.X || previous.Y != window.Y)
                    PostMessageLocked(handle, Win32.WM_MOVE, 0, Win32.MakePoint(client.X, client.Y));
                PostMessageLocked(handle, Win32.WM_SIZE,
                    window.Minimized ? Win32.SIZE_MINIMIZED : window.Maximized ? Win32.SIZE_MAXIMIZED : Win32.SIZE_RESTORED,
                    window.Minimized ? 0 : Win32.MakeSize(client.Width, client.Height));
            }
            BumpVersionLocked();
        }
        if (command != Win32.SW_HIDE)
        {
            Drawing?.Invalidate(handle, erase: true);
            PostMessage(handle, Win32.WM_PAINT, 0, 0);
        }
        return true;
    }

    /// <summary>Tell the guest that the compositor moved one of its windows.</summary>
    public void NotifyWindowMoved(ulong handle, int x, int y)
    {
        lock (_gate)
        {
            if (!_windows.ContainsKey(handle)) return;
            PostMessageLocked(handle, Win32.WM_MOVE, 0, Win32.MakePoint(x, y));
        }
    }

    public bool SetZOrder(ulong handle, ulong insertAfter)
    {
        lock (_gate)
        {
            if (!_windows.TryGetValue(handle, out var window) || window.IsChild) return false;
            _topLevels.Remove(window);
            if (insertAfter == Win32.HwndTopmost)
            {
                window.ExStyle |= Win32.WS_EX_TOPMOST;
                _topLevels.Add(window);
            }
            else if (insertAfter == Win32.HwndNoTopmost)
            {
                window.ExStyle &= ~Win32.WS_EX_TOPMOST;
                _topLevels.Insert(0, window);
            }
            else if (insertAfter == Win32.HwndBottom)
            {
                _topLevels.Insert(0, window);
            }
            else if (insertAfter == Win32.HwndTop)
            {
                _topLevels.Add(window);
            }
            else if (_windows.TryGetValue(insertAfter, out var anchor) && !anchor.IsChild)
            {
                var index = _topLevels.IndexOf(anchor);
                _topLevels.Insert(index < 0 ? _topLevels.Count : index + 1, window);
            }
            else
            {
                _topLevels.Add(window);
            }
            ReindexLocked();
            BumpVersionLocked();
            return true;
        }
    }

    public void BringToFront(ulong handle)
    {
        lock (_gate)
        {
            if (!_windows.TryGetValue(handle, out var window) || window.IsChild) return;
            MoveToTopLocked(window);
            ReindexLocked();
            BumpVersionLocked();
        }
    }

    private void MoveToTopLocked(GuestWindow window)
    {
        _topLevels.Remove(window);
        _topLevels.Add(window);
    }

    public void Activate(ulong handle)
    {
        lock (_gate)
        {
            if (!_windows.TryGetValue(handle, out var window)) return;
            SyncDesktopLocked();
            var top = TopLevelLocked(window);
            if (top is not null)
            {
                MoveToTopLocked(top);
                if (window.Desktop == _displayDesktop) _foregroundHwnd = top.Handle;
                else _desktopFocus[window.Desktop] = (_desktopFocus.GetValueOrDefault(window.Desktop).Focus, top.Handle);
            }
            SetFocus(handle, window.Desktop);
            ReindexLocked();
            BumpVersionLocked();
        }
    }

    public void SetFocus(ulong handle, DesktopObject? desktop = null)
    {
        lock (_gate)
        {
            SyncDesktopLocked();
            if (handle != 0 && !_windows.ContainsKey(handle)) return;
            desktop ??= _displayDesktop;
            if (desktop is null || (handle != 0 && _windows[handle].Desktop != desktop)) return;
            if (desktop == _displayDesktop) SetFocusLocked(handle);
            else
            {
                var previous = _desktopFocus.GetValueOrDefault(desktop);
                if (previous.Focus == handle) return;
                _desktopFocus[desktop] = (handle, previous.Foreground);
                if (previous.Focus != 0) PostMessageLocked(previous.Focus, Win32.WM_KILLFOCUS, handle, 0);
                if (handle != 0) PostMessageLocked(handle, Win32.WM_SETFOCUS, previous.Focus, 0);
            }
            BumpVersionLocked();
        }
    }

    public (ulong Focus, ulong Foreground) GetDesktopFocus(DesktopObject desktop)
    {
        lock (_gate)
        {
            SyncDesktopLocked();
            return desktop == _displayDesktop ? (_focusHwnd, _foregroundHwnd) : _desktopFocus.GetValueOrDefault(desktop);
        }
    }

    internal ulong[] GetDesktopWindows(DesktopObject desktop, int? threadId = null)
    {
        lock (_gate) return _topLevels.Where(w => w.Desktop == desktop && !w.Destroyed &&
            (threadId is null || w.ThreadId == threadId)).Select(w => w.Handle).ToArray();
    }

    private void SetFocusLocked(ulong handle)
    {
        if (handle != 0 && (!_windows.TryGetValue(handle, out var window) || window.Desktop != _displayDesktop)) return;
        if (_focusHwnd == handle) return;
        var previous = _focusHwnd;
        _focusHwnd = handle;
        if (previous != 0) PostMessageLocked(previous, Win32.WM_KILLFOCUS, handle, 0);
        if (handle != 0) PostMessageLocked(handle, Win32.WM_SETFOCUS, previous, 0);
    }

    public void ClearFocus()
    {
        lock (_gate)
        {
            if (_focusHwnd == 0) return;
            var previous = _focusHwnd;
            _focusHwnd = 0;
            PostMessageLocked(previous, Win32.WM_KILLFOCUS, 0, 0);
            BumpVersionLocked();
        }
    }

    public ulong FocusWindow
    {
        get { lock (_gate) { SyncDesktopLocked(); return _focusHwnd; } }
    }

    public ulong ForegroundWindow
    {
        get { lock (_gate) { SyncDesktopLocked(); return _foregroundHwnd; } }
    }

    public bool IsActive(ulong handle) => handle != 0 && (FocusWindow == handle || ForegroundWindow == handle);

    private GuestWindow? TopLevelLocked(GuestWindow window)
    {
        while (window.IsChild && window.Parent != 0)
        {
            if (!_windows.TryGetValue(window.Parent, out var parent)) return window;
            window = parent;
        }
        return window;
    }

    /// <summary>
    /// Child coordinates in Win32 are relative to the parent's client area.  The compositor and
    /// hit-testing work in virtual screen coordinates, so convert at the boundary.
    /// </summary>
    private GuiRect OuterLocked(GuestWindow window)
    {
        if (!window.IsChild || window.Parent == 0 || !_windows.TryGetValue(window.Parent, out var parent))
            return WindowGeometry.Outer(window);
        var parentClient = ClientLocked(parent);
        return new(parentClient.X + window.X, parentClient.Y + window.Y,
            Math.Max(0, window.Width), Math.Max(0, window.Height));
    }

    private GuiRect ClientLocked(GuestWindow window) => WindowGeometry.Client(window, OuterLocked(window), _respectWindowBorderStyle);

    // --- snapshot and hit testing ----------------------------------------------

    public IReadOnlyList<WindowSnapshot> Snapshot()
    {
        lock (_gate)
        {
            SyncDesktopLocked();
            var result = new List<WindowSnapshot>(_windows.Count);
            for (var i = 0; i < _topLevels.Count; i++)
            {
                var top = _topLevels[i];
                AddSnapshotLocked(result, top, i);
                for (var j = 0; j < top.Children.Count; j++)
                    AddSnapshotLocked(result, top.Children[j], j);
            }
            return result;
        }
    }

    private void AddSnapshotLocked(List<WindowSnapshot> result, GuestWindow window, int zOrder)
    {
        var visible = window.Visible && !window.Minimized && window.Desktop == _displayDesktop;
        if (window.IsChild && window.Parent != 0 && _windows.TryGetValue(window.Parent, out var parent))
            visible &= parent.Visible && !parent.Minimized;
        var outer = OuterLocked(window);
        var client = ClientLocked(window);
        var background = ResolveBackground(window.BackgroundBrush);
        result.Add(new WindowSnapshot(
            window.Handle,
            window.Parent,
            window.ProcessId,
            window.ThreadId,
            window.ClassName,
            window.Title,
            window.Style,
            window.ExStyle,
            window.IsChild,
            window.HasCaption,
            window.ClassName.Equals("BUTTON", StringComparison.OrdinalIgnoreCase),
            window.ClassName.Equals("STATIC", StringComparison.OrdinalIgnoreCase),
            visible,
            window.Enabled,
            window.Handle == _focusHwnd || window.Handle == _foregroundHwnd,
            window.Minimized,
            window.Maximized,
            outer,
            client,
            background,
            ContrastText(background),
            zOrder,
            window.ControlId) { Desktop = window.Desktop });
    }

    public WindowHit HitTest(int screenX, int screenY)
    {
        lock (_gate) return HitTestCore(screenX, screenY);
    }

    public WindowSnapshot? FindWindow(string? className, string? title, DesktopObject? desktop = null)
    {
        lock (_gate)
        {
            SyncDesktopLocked();
            foreach (var window in _windows.Values)
            {
                if (window.Destroyed || window.IsChild || window.Desktop != (desktop ?? _displayDesktop)) continue;
                if (className is not null && !window.ClassName.Equals(className, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (title is not null && !window.Title.Equals(title, StringComparison.Ordinal))
                    continue;
                return SnapshotOfLocked(window);
            }
            return null;
        }
    }

    private WindowSnapshot? SnapshotOfLocked(GuestWindow window)
    {
        var list = new List<WindowSnapshot>(1);
        AddSnapshotLocked(list, window, window.ZOrder);
        return list.Count == 0 ? null : list[0];
    }

    public bool GetWindowRect(ulong handle, out GuiRect rect, out GuiRect clientRect)
    {
        lock (_gate)
        {
            if (IsDesktopWindow(handle))
            {
                rect = clientRect = new GuiRect(0, 0, _screenWidth, _screenHeight);
                return true;
            }
            if (!_windows.TryGetValue(handle, out var window))
            {
                rect = default;
                clientRect = default;
                return false;
            }
            rect = OuterLocked(window);
            clientRect = ClientLocked(window);
            return true;
        }
    }

    // --- messages ---------------------------------------------------------------

    private ThreadMessageQueue EnsureQueueLocked(int threadId)
    {
        if (!_queues.TryGetValue(threadId, out var queue))
        {
            queue = new ThreadMessageQueue(threadId);
            _queues.Add(threadId, queue);
        }
        return queue;
    }

    private WindowMessage BuildMessageLocked(ulong hwnd, uint message, ulong wParam, long lParam) =>
        new(hwnd, message, wParam, lParam, unchecked((uint)Environment.TickCount64), _mouseX, _mouseY);

    public bool PostMessage(ulong handle, uint message, ulong wParam, long lParam)
    {
        lock (_gate)
        {
            if (!_windows.TryGetValue(handle, out var window) || window.Destroyed) return false;
            EnsureQueueLocked(window.ThreadId).Post(BuildMessageLocked(handle, message, wParam, lParam));
            return true;
        }
    }

    public int BroadcastMessage(uint message, ulong wParam, long lParam)
    {
        lock (_gate)
        {
            var count = 0;
            foreach (var window in _windows.Values)
            {
                if (window.Destroyed) continue;
                PostMessageLocked(window.Handle, message, wParam, lParam);
                count++;
            }
            return count;
        }
    }

    public bool PostThreadMessage(int threadId, uint message, ulong wParam, long lParam)
    {
        lock (_gate)
        {
            EnsureQueueLocked(threadId).Post(BuildMessageLocked(0, message, wParam, lParam));
            return true;
        }
    }

    public void PostQuitMessage(int threadId, int exitCode)
    {
        lock (_gate) EnsureQueueLocked(threadId).Post(BuildMessageLocked(0, Win32.WM_QUIT, unchecked((ulong)(uint)exitCode), 0));
    }

    public bool GetMessage(int threadId, ulong hwndFilter, uint filterMin, uint filterMax, Func<bool> cancelled,
        out WindowMessage message)
    {
        ThreadMessageQueue queue;
        lock (_gate) queue = EnsureQueueLocked(threadId);
        return queue.WaitForMessage(MessageMatches(hwndFilter, filterMin, filterMax), cancelled, out message);
    }

    public bool PeekMessage(int threadId, ulong hwndFilter, uint filterMin, uint filterMax, bool remove,
        out WindowMessage message)
    {
        ThreadMessageQueue queue;
        lock (_gate) queue = EnsureQueueLocked(threadId);
        return queue.TryTake(MessageMatches(hwndFilter, filterMin, filterMax), remove, out message);
    }

    private static Func<WindowMessage, bool> MessageMatches(ulong hwndFilter, uint filterMin, uint filterMax) =>
        message =>
        {
            if (message.Message == Win32.WM_QUIT) return true;
            if (hwndFilter != 0 && message.Hwnd != hwndFilter) return false;
            if (filterMin != 0 || filterMax != 0)
            {
                if (message.Message < filterMin || message.Message > filterMax) return false;
            }
            return true;
        };

    public void CloseThread(int threadId)
    {
        lock (_gate)
        {
            foreach (var window in _windows.Values.Where(w => w.ThreadId == threadId).ToArray()) RemoveWindowLocked(window);
            if (_queues.Remove(threadId, out var queue)) queue.Close();
            BumpVersionLocked();
        }
    }

    private void CleanupThread(ImaginedThread thread) => CloseThread(thread.Id);

    public void CleanupProcess(int processId)
    {
        lock (_gate)
        {
            var removedThreads = new HashSet<int>();
            foreach (var window in _windows.Values.Where(w => w.ProcessId == processId).ToArray())
            {
                removedThreads.Add(window.ThreadId);
                RemoveWindowLocked(window);
            }
            foreach (var threadId in removedThreads)
                if (_queues.Remove(threadId, out var queue)) queue.Close();
            _classes.Remove(processId);
            foreach (var atom in _atoms.Keys.Where(k => k.ProcessId == processId).ToArray())
                _atoms.Remove(atom);
            BumpVersionLocked();
        }
    }

    private void CleanupProcess(ImaginedProcess process)
    {
        CleanupProcess(process.Id);
        foreach (var thread in process.Threads) CloseThread(thread.Id);
    }

    public void CloseAll()
    {
        lock (_gate)
        {
            foreach (var queue in _queues.Values) queue.Close();
            foreach (var timer in _timers.Values) timer.Dispose();
            _timers.Clear();
            _queues.Clear();
            foreach (var window in _windows.Values) _runtime.WindowStations.UnregisterWindow(window.Thread, window.Desktop);
            _windows.Clear();
            _desktopFocus.Clear();
            _desktopHandles.Clear();
            _topLevels.Clear();
            _classes.Clear();
            _atoms.Clear();
            _focusHwnd = 0;
            _foregroundHwnd = 0;
            _hoverHwnd = 0;
            _mouseDownHwnd = 0;
            BumpVersionLocked();
        }
    }

    // --- input -----------------------------------------------------------------

    public void SetPointerPosition(int x, int y)
    {
        lock (_gate)
        {
            _mouseX = x;
            _mouseY = y;
        }
    }

    public void PointerMove(int x, int y)
    {
        WindowHit hit;
        lock (_gate)
        {
            _mouseX = x;
            _mouseY = y;
            hit = HitTestLocked(x, y);
            var target = TargetFor(hit);
            if (target != _hoverHwnd)
            {
                if (_hoverHwnd != 0 && _windows.TryGetValue(_hoverHwnd, out var previous) &&
                    _windows.ContainsKey(_hoverHwnd))
                    PostMessageLocked(previous.Handle, Win32.WM_MOUSELEAVE, 0, 0);
                _hoverHwnd = target != 0 && _windows.ContainsKey(target) ? target : 0;
            }
            if (hit.IsValid)
            {
                if (hit.Region is WindowHitRegion.Caption or WindowHitRegion.Border)
                    PostMessageLocked(hit.TopLevelHandle, Win32.WM_NCMOUSEMOVE, 0, Win32.MakePoint(x, y));
                else
                    PostMessageLocked(hit.Handle, Win32.WM_MOUSEMOVE, (ulong)MouseKeyStateLocked(), Win32.MakePoint(hit.X, hit.Y));
            }
        }
    }

    public void PointerLeave()
    {
        lock (_gate)
        {
            if (_hoverHwnd != 0 && _windows.ContainsKey(_hoverHwnd))
                PostMessageLocked(_hoverHwnd, Win32.WM_MOUSELEAVE, 0, 0);
            _hoverHwnd = 0;
        }
    }

    public void MouseDown(WindowHit hit, int button)
    {
        lock (_gate)
        {
            if (!hit.IsValid || !CanInputLocked(hit.Handle)) return;
            var top = hit.TopLevelHandle;
            if (top != 0 && _windows.ContainsKey(top))
            {
                if (_windows.TryGetValue(top, out var topWindow)) MoveToTopLocked(topWindow);
                _foregroundHwnd = top;
                SetFocusLocked(hit.Handle);
            }
            _mouseDownHwnd = hit.Handle;
            _mouseDownRegion = hit.Region;
            _pressedButton = button;
            if (hit.Region is WindowHitRegion.Caption or WindowHitRegion.Border)
            {
                PostMessageLocked(hit.TopLevelHandle, Win32.WM_NCLBUTTONDOWN,
                    (ulong)(hit.Region == WindowHitRegion.Caption ? Win32.HTCAPTION : Win32.HTBORDER),
                    Win32.MakePoint(hit.X, hit.Y));
            }
            else
            {
                PostMessageLocked(hit.Handle, ButtonMessage(button, down: true), (ulong)MouseKeyStateLocked(), Win32.MakePoint(hit.X, hit.Y));
            }
            ReindexLocked();
            BumpVersionLocked();
        }
    }

    public void MouseUp(int x, int y, int button)
    {
        lock (_gate)
        {
            SyncDesktopLocked();
            _mouseX = x;
            _mouseY = y;
            var hit = HitTestLocked(x, y);
            var handle = hit.IsValid && hit.Handle == _mouseDownHwnd ? hit.Handle : _mouseDownHwnd;
            if (handle == 0 || !_windows.TryGetValue(handle, out var window)) return;
            if (_mouseDownRegion is WindowHitRegion.Caption or WindowHitRegion.Border)
            {
                PostMessageLocked(handle, Win32.WM_NCLBUTTONUP,
                    (ulong)(_mouseDownRegion == WindowHitRegion.Caption ? Win32.HTCAPTION : Win32.HTBORDER),
                    Win32.MakePoint(x, y));
            }
            else
            {
                var client = ClientLocked(window);
                PostMessageLocked(handle, ButtonMessage(button, down: false), (ulong)MouseKeyStateLocked(),
                    Win32.MakePoint(x - client.X, y - client.Y));
            }
            _mouseDownHwnd = 0;
            _pressedButton = -1;
        }
    }

    public void MouseDoubleClick(WindowHit hit, int button)
    {
        lock (_gate)
        {
            if (!hit.IsValid || !CanInputLocked(hit.Handle)) return;
            if (hit.Region == WindowHitRegion.Client)
                PostMessageLocked(hit.Handle, ButtonMessage(button, down: true, doubleClick: true),
                    (ulong)MouseKeyStateLocked(), Win32.MakePoint(hit.X, hit.Y));
        }
    }

    public void MouseWheel(WindowHit hit, int delta)
    {
        lock (_gate)
        {
            if (!hit.IsValid || !CanInputLocked(hit.Handle)) return;
            var target = TargetFor(hit);
            if (!_windows.ContainsKey(target)) return;
            var wParam = unchecked((ulong)((uint)(ushort)delta << 16)) | (uint)MouseKeyStateLocked();
            PostMessageLocked(target, Win32.WM_MOUSEWHEEL, wParam, Win32.MakePoint(_mouseX, _mouseY));
        }
    }

    public bool KeyDown(int virtualKey, uint scanCode, bool extended, bool wasDown) =>
        SetKey(virtualKey, down: true, scanCode, extended, wasDown);

    public bool KeyUp(int virtualKey, uint scanCode, bool extended) =>
        SetKey(virtualKey, down: false, scanCode, extended, wasDown: true);

    public void ReleaseKeyboard()
    {
        lock (_gate)
        {
            var target = _focusHwnd != 0 ? _focusHwnd : _foregroundHwnd;
            var alt = IsKeyDownLocked(Win32.VK_MENU);
            for (var i = 0; i < _keysDown.Length; i++)
            {
                if (!_keysDown[i]) continue;
                _keysDown[i] = false;
                PostMessageLocked(target, alt ? Win32.WM_SYSKEYUP : Win32.WM_KEYUP, (ulong)i,
                    1 | (1L << 30) | (1L << 31) | (alt ? 1L << 29 : 0));
            }
        }
    }

    private bool SetKey(int virtualKey, bool down, uint scanCode, bool extended, bool wasDown)
    {
        if (virtualKey < 0 || virtualKey > 255) return false;
        lock (_gate)
        {
            SyncDesktopLocked();
            if (!CanInputLocked(_focusHwnd != 0 ? _focusHwnd : _foregroundHwnd)) return false;
            SetKeyStateLocked(virtualKey, down);
            var target = _focusHwnd != 0 ? _focusHwnd : _foregroundHwnd;
            if (target == 0 || !_windows.ContainsKey(target)) return false;
            var alt = IsKeyDownLocked(Win32.VK_MENU);
            var message = down
                ? (alt ? Win32.WM_SYSKEYDOWN : Win32.WM_KEYDOWN)
                : (alt ? Win32.WM_SYSKEYUP : Win32.WM_KEYUP);
            long lParam = 1
                | ((long)(scanCode & 0xFF) << 16)
                | (extended ? 1L << 24 : 0)
                | (wasDown ? 1L << 30 : 0)
                | (down ? 0 : 1L << 31);
            PostMessageLocked(target, message, (ulong)virtualKey, lParam);
            return true;
        }
    }

    public bool Character(char character)
    {
        lock (_gate)
        {
            SyncDesktopLocked();
            var target = _focusHwnd != 0 ? _focusHwnd : _foregroundHwnd;
            if (!CanInputLocked(target)) return false;
            var alt = IsKeyDownLocked(Win32.VK_MENU);
            PostMessageLocked(target, alt ? Win32.WM_SYSCHAR : Win32.WM_CHAR, character, 1);
            return true;
        }
    }

    public bool IsKeyDown(int virtualKey)
    {
        lock (_gate) return IsKeyDownLocked(virtualKey);
    }

    private bool IsKeyDownLocked(int virtualKey)
    {
        if (virtualKey is < 0 or > 255) return false;
        return virtualKey switch
        {
            Win32.VK_SHIFT => _keysDown[Win32.VK_SHIFT] || _keysDown[Win32.VK_LSHIFT] || _keysDown[Win32.VK_RSHIFT],
            Win32.VK_CONTROL => _keysDown[Win32.VK_CONTROL] || _keysDown[Win32.VK_LCONTROL] || _keysDown[Win32.VK_RCONTROL],
            Win32.VK_MENU => _keysDown[Win32.VK_MENU] || _keysDown[Win32.VK_LMENU] || _keysDown[Win32.VK_RMENU],
            _ => _keysDown[virtualKey]
        };
    }

    private void SetKeyStateLocked(int virtualKey, bool down)
    {
        if (virtualKey is < 0 or > 255) return;
        _keysDown[virtualKey] = down;
        switch (virtualKey)
        {
            case Win32.VK_SHIFT:
                _keysDown[Win32.VK_LSHIFT] = down;
                break;
            case Win32.VK_CONTROL:
                _keysDown[Win32.VK_LCONTROL] = down;
                break;
            case Win32.VK_MENU:
                _keysDown[Win32.VK_LMENU] = down;
                break;
        }
    }

    public int GetKeyState(int virtualKey)
    {
        lock (_gate)
        {
            if (virtualKey is < 0 or > 255) return 0;
            var down = IsKeyDownLocked(virtualKey) ? 0x8000 : 0;
            return down | (_keysToggled[virtualKey] ? 1 : 0);
        }
    }

    public byte[] GetKeyboardState()
    {
        lock (_gate)
        {
            var state = new byte[256];
            for (var i = 0; i < state.Length; i++) state[i] = _keysDown[i] ? (byte)0x80 : (byte)0;
            return state;
        }
    }

    public (int X, int Y) GetCursorPosition()
    {
        lock (_gate) return (_mouseX, _mouseY);
    }

    public void SetCursorPosition(int x, int y)
    {
        lock (_gate)
        {
            _mouseX = x;
            _mouseY = y;
        }
    }

    public bool RequestClose(ulong handle) =>
        PostMessage(handle, Win32.WM_SYSCOMMAND, Win32.SC_CLOSE, 0);

    public bool PostCommand(ulong parentHandle, ulong childHandle)
    {
        lock (_gate)
        {
            if (!_windows.TryGetValue(parentHandle, out var parent) ||
                !_windows.TryGetValue(childHandle, out var child)) return false;
            var wParam = (ulong)(uint)((child.ControlId & 0xFFFF) | (Win32.BN_CLICKED << 16));
            PostMessageLocked(parentHandle, Win32.WM_COMMAND, wParam, unchecked((long)childHandle));
            return true;
        }
    }

    private int MouseKeyStateLocked()
    {
        var state = 0;
        if (_keysDown[Win32.VK_SHIFT] || _keysDown[Win32.VK_LSHIFT] || _keysDown[Win32.VK_RSHIFT]) state |= Win32.MK_SHIFT;
        if (_keysDown[Win32.VK_CONTROL] || _keysDown[Win32.VK_LCONTROL] || _keysDown[Win32.VK_RCONTROL]) state |= Win32.MK_CONTROL;
        if (_pressedButton == 0) state |= Win32.MK_LBUTTON;
        if (_pressedButton == 1) state |= Win32.MK_RBUTTON;
        if (_pressedButton == 2) state |= Win32.MK_MBUTTON;
        return state;
    }

    private ulong TargetFor(WindowHit hit) =>
        hit.Region is WindowHitRegion.Caption or WindowHitRegion.Border ? hit.TopLevelHandle : hit.Handle;

    private WindowHit HitTestLocked(int screenX, int screenY)
    {
        return HitTestCore(screenX, screenY);
    }

    private WindowHit HitTestCore(int screenX, int screenY)
    {
        SyncDesktopLocked();
        if (_displayDesktop is null || _displayDesktop != _inputDesktop) return WindowHit.None;
        for (var i = _topLevels.Count - 1; i >= 0; i--)
        {
            var top = _topLevels[i];
            if (top.Destroyed || !top.Visible || top.Minimized || top.Desktop != _displayDesktop) continue;
            var outer = OuterLocked(top);
            if (!outer.Contains(screenX, screenY)) continue;
            for (var j = top.Children.Count - 1; j >= 0; j--)
            {
                var child = top.Children[j];
                if (child.Destroyed || !child.Visible) continue;
                var childRect = OuterLocked(child);
                if (!childRect.Contains(screenX, screenY)) continue;
                return new WindowHit(child.Handle, top.Handle, WindowHitRegion.Client,
                    screenX - childRect.X, screenY - childRect.Y, childRect, childRect, true);
            }
            var client = ClientLocked(top);
            if (client.Contains(screenX, screenY))
                return new WindowHit(top.Handle, top.Handle, WindowHitRegion.Client,
                    screenX - client.X, screenY - client.Y, outer, client, false);
            if (top.HasCaption && screenX >= client.X && screenX < client.Right &&
                screenY >= outer.Y + WindowGeometry.BorderWidth && screenY < client.Y)
                return new WindowHit(top.Handle, top.Handle, WindowHitRegion.Caption,
                    screenX, screenY, outer, client, false);
            return new WindowHit(top.Handle, top.Handle, WindowHitRegion.Border,
                screenX, screenY, outer, client, false);
        }
        return WindowHit.None;
    }

    private static uint ButtonMessage(int button, bool down, bool doubleClick = false) => (button, down, doubleClick) switch
    {
        (0, true, false) => Win32.WM_LBUTTONDOWN,
        (0, true, true) => Win32.WM_LBUTTONDBLCLK,
        (0, false, _) => Win32.WM_LBUTTONUP,
        (1, true, false) => Win32.WM_RBUTTONDOWN,
        (1, true, true) => Win32.WM_RBUTTONDBLCLK,
        (1, false, _) => Win32.WM_RBUTTONUP,
        (2, true, false) => Win32.WM_MBUTTONDOWN,
        (2, true, true) => Win32.WM_MBUTTONDBLCLK,
        (2, false, _) => Win32.WM_MBUTTONUP,
        _ => Win32.WM_MOUSEMOVE
    };

    private void PostMessageLocked(ulong handle, uint message, ulong wParam, long lParam)
    {
        if (!_windows.TryGetValue(handle, out var window) || window.Destroyed) return;
        EnsureQueueLocked(window.ThreadId).Post(BuildMessageLocked(handle, message, wParam, lParam));
    }

    // --- timers ----------------------------------------------------------------

    public ulong SetTimer(ulong handle, ulong timerId, uint elapseMilliseconds)
    {
        if (elapseMilliseconds == 0)
        {
            KillTimer(handle, timerId);
            return 0;
        }
        lock (_gate)
        {
            if (!_windows.TryGetValue(handle, out var window) || window.Destroyed) return 0;
            if (timerId == 0) timerId = 0x4000 + (ulong)_timers.Count + 1;
            KillTimerLocked(handle, timerId);
            var entry = new TimerEntry(timerId, handle,
                new Timer(_ => PostTimer(handle, timerId), null, elapseMilliseconds, elapseMilliseconds));
            _timers[(handle, timerId)] = entry;
            return timerId;
        }
    }

    public bool KillTimer(ulong handle, ulong timerId)
    {
        lock (_gate) return KillTimerLocked(handle, timerId);
    }

    private bool KillTimerLocked(ulong handle, ulong timerId)
    {
        if (!_timers.Remove((handle, timerId), out var timer)) return false;
        timer.Dispose();
        return true;
    }

    private void StopTimersLocked(ulong handle)
    {
        foreach (var key in _timers.Keys.Where(k => k.Hwnd == handle).ToArray())
            if (_timers.Remove(key, out var timer)) timer.Dispose();
    }

    private void PostTimer(ulong handle, ulong timerId)
    {
        lock (_gate) PostMessageLocked(handle, Win32.WM_TIMER, timerId, 0);
    }

    // --- colors ----------------------------------------------------------------

    public uint SystemColorArgb(int index)
    {
        if (index < 0 || index >= _systemColorsArgb.Length) index = Win32.COLOR_WINDOW;
        return _systemColorsArgb[index];
    }

    public uint GetSysColorRef(int index)
    {
        var argb = SystemColorArgb(index);
        return ((argb >> 16) & 0xFF) | (argb & 0xFF00) | ((argb & 0xFF) << 16);
    }

    public uint ResolveBackground(ulong brush)
    {
        if (brush == 0) return SystemColorArgb(Win32.COLOR_WINDOW);
        var index = brush <= 31 ? (int)brush : 0;
        if (index is >= 1 and <= 30) return SystemColorArgb(index - 1);
        return SystemColorArgb(Win32.COLOR_WINDOW);
    }

    public static uint ContrastText(uint backgroundArgb)
    {
        var r = (backgroundArgb >> 16) & 0xFF;
        var g = (backgroundArgb >> 8) & 0xFF;
        var b = backgroundArgb & 0xFF;
        var luminance = (299 * r + 587 * g + 114 * b) / 1000;
        return luminance >= 140 ? 0xFF101318u : 0xFFF0F6FCu;
    }

    // --- helpers ---------------------------------------------------------------

    private void BumpVersionLocked() => _version++;

    private void Trace(string message, int processId = 0, int threadId = 0) =>
        _runtime.Trace.Emit(TraceKind.Api, message, processId, threadId);

    public void Dispose()
    {
        _runtime.ProcessExited -= CleanupProcess;
        _runtime.ThreadExited -= CleanupThread;
        CloseAll();
    }
}
