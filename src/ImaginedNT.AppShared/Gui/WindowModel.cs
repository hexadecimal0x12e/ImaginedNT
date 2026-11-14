namespace ImaginedNT.AppShared.Gui;

public interface IWindowDrawing
{
    ulong GetSystemBrush(int index);
    bool EraseBackground(ulong window, ulong dc);
    void Invalidate(ulong window, bool erase);
    void Present(ulong window);
    void Validate(ulong window);
}

/// <summary>A rectangle in virtual screen coordinates.</summary>
public readonly record struct GuiRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool Contains(int x, int y) => x >= X && y >= Y && x < Right && y < Bottom;
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

public enum WindowHitRegion
{
    None,
    Border,
    Caption,
    Client
}

/// <summary>Result of hit-testing the composited desktop.</summary>
public readonly record struct WindowHit(
    ulong Handle,
    ulong TopLevelHandle,
    WindowHitRegion Region,
    int X,
    int Y,
    GuiRect WindowRect,
    GuiRect ClientRect,
    bool IsChild)
{
    public static WindowHit None => default;
    public bool IsValid => Handle != 0 && Region != WindowHitRegion.None;
}

/// <summary>One queued guest window message.</summary>
public readonly record struct WindowMessage(
    ulong Hwnd,
    uint Message,
    ulong WParam,
    long LParam,
    uint Time,
    int X,
    int Y)
{
    public bool IsMouseMove => Message is Win32.WM_MOUSEMOVE or Win32.WM_NCMOUSEMOVE;
}

/// <summary>Immutable state consumed by the XAML DWM compositor.</summary>
public sealed record WindowSnapshot(
    ulong Handle,
    ulong Parent,
    int ProcessId,
    int ThreadId,
    string ClassName,
    string Title,
    uint Style,
    uint ExStyle,
    bool IsChild,
    bool HasCaption,
    bool IsButton,
    bool IsStatic,
    bool Visible,
    bool Enabled,
    bool Active,
    bool Minimized,
    bool Maximized,
    GuiRect Outer,
    GuiRect Client,
    uint BackgroundArgb,
    uint ForegroundArgb,
    int ZOrder,
    int ControlId)
{
    public ImaginedNT.Core.Runtime.DesktopObject Desktop { get; init; } = null!;
}

/// <summary>Window class registered by RegisterClassExW/A or one of the built-in classes.</summary>
internal sealed class WindowClassInfo
{
    public required int ProcessId { get; init; }
    public required string Name { get; init; }
    public ushort Atom { get; init; }
    public ulong WndProc { get; init; }
    public uint Style { get; init; }
    public ulong BackgroundBrush { get; init; }
    public ulong Instance { get; init; }
    public string? MenuName { get; init; }
    public bool BuiltIn { get; init; }
}

/// <summary>One guest-visible window (top-level or child).</summary>
internal sealed class GuestWindow
{
    public required ImaginedNT.Core.Runtime.ImaginedThread Thread { get; init; }
    public required ImaginedNT.Core.Runtime.DesktopObject Desktop { get; init; }
    public required ulong Handle { get; init; }
    public required int ProcessId { get; init; }
    public required int ThreadId { get; init; }
    public required string ClassName { get; init; }
    public ulong WndProc { get; set; }
    public ulong Parent { get; set; }
    public ulong Owner { get; set; }
    public ulong Menu { get; set; }
    public ulong Instance { get; set; }
    public ulong UserData { get; set; }
    public string Title { get; set; } = string.Empty;
    public uint Style { get; set; }
    public uint ExStyle { get; set; }
    public ulong BackgroundBrush { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Visible { get; set; }
    public bool Enabled { get; set; } = true;
    public bool Minimized { get; set; }
    public bool Maximized { get; set; }
    public GuiRect? RestoreRect { get; set; }
    public int ZOrder { get; set; }
    public int ControlId => (int)(Menu & 0xFFFF);
    public List<GuestWindow> Children { get; } = new();
    public bool Destroyed { get; set; }

    public bool IsChild => (Style & Win32.WS_CHILD) != 0 && Parent != 0;
    public bool HasCaption => !IsChild && (Style & Win32.WS_CAPTION) == Win32.WS_CAPTION;
}

/// <summary>Client rectangle for a guest window, matching the compositor chrome.</summary>
internal static class WindowGeometry
{
    // The App chrome is one pixel of frame plus a 30 px caption bar.
    public const int BorderWidth = 1;
    public const int CaptionHeight = 30;

    public static GuiRect Outer(GuestWindow window) =>
        new(window.X, window.Y, Math.Max(0, window.Width), Math.Max(0, window.Height));

    public static GuiRect Client(GuestWindow window, GuiRect outer, bool respectWindowBorderStyle)
    {
        if (window.IsChild) return outer;
        var border = BorderWidth;
        if (respectWindowBorderStyle && (window.Style & (Win32.WS_BORDER | Win32.WS_DLGFRAME | Win32.WS_THICKFRAME)) == 0) border = 0;
        var top = window.HasCaption ? CaptionHeight + border : border;
        return new(outer.X + border, outer.Y + top,
            Math.Max(0, window.Width - border * 2),
            Math.Max(0, window.Height - top - border));
    }
}

/// <summary>
/// Per-guest-thread message queue. Guest threads block in GetMessageW on this queue; the App's
/// compositor posts input and lifecycle messages from the UI thread.
/// </summary>
internal sealed class ThreadMessageQueue(int threadId)
{
    private const int MaximumMessages = 8192;
    private readonly object _gate = new();
    private readonly List<WindowMessage> _messages = new();
    private volatile bool _closed;

    public int ThreadId { get; } = threadId;
    public bool Closed => _closed;

    public int Count
    {
        get { lock (_gate) return _messages.Count; }
    }

    public void Post(WindowMessage message)
    {
        lock (_gate)
        {
            if (_closed) return;
            // Windows coalesces mouse movement. Doing the same keeps a fast pointer from
            // flooding a guest that is busy handling another message.
            if (message.IsMouseMove && TryReplaceMouseMove(message)) return;
            if (_messages.Count >= MaximumMessages)
            {
                var index = _messages.FindIndex(m => m.IsMouseMove || m.Message == Win32.WM_PAINT);
                if (index >= 0) _messages.RemoveAt(index);
                else if (message.IsMouseMove || message.Message == Win32.WM_PAINT) return;
                else _messages.RemoveAt(0);
            }
            _messages.Add(message);
            Monitor.PulseAll(_gate);
        }
    }

    private bool TryReplaceMouseMove(WindowMessage message)
    {
        for (var i = _messages.Count - 1; i >= 0; i--)
        {
            var existing = _messages[i];
            if (existing.Hwnd != message.Hwnd || existing.Message != message.Message) continue;
            if (i == _messages.Count - 1) _messages[i] = message;
            return true;
        }
        return false;
    }

    public bool TryTake(Func<WindowMessage, bool> filter, bool remove, out WindowMessage message)
    {
        lock (_gate)
        {
            for (var i = 0; i < _messages.Count; i++)
            {
                var candidate = _messages[i];
                if (!filter(candidate)) continue;
                if (remove) _messages.RemoveAt(i);
                message = candidate;
                return true;
            }
            message = default;
            return false;
        }
    }

    public bool WaitForMessage(Func<WindowMessage, bool> filter, Func<bool> cancelled, out WindowMessage message)
    {
        lock (_gate)
        {
            while (true)
            {
                for (var i = 0; i < _messages.Count; i++)
                {
                    var candidate = _messages[i];
                    if (!filter(candidate)) continue;
                    _messages.RemoveAt(i);
                    message = candidate;
                    return true;
                }
                if (_closed || cancelled())
                {
                    message = default;
                    return false;
                }
                // Wake periodically so process/thread termination requests can unwind a guest
                // that is parked in its message loop.
                Monitor.Wait(_gate, 50);
            }
        }
    }

    public void Close()
    {
        lock (_gate)
        {
            _closed = true;
            _messages.Clear();
            Monitor.PulseAll(_gate);
        }
    }
}
