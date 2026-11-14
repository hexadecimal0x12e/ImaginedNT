using System.ComponentModel;
using ImaginedNT.AppShared.Gui;
using ImaginedNT.Core.Runtime;

namespace ImaginedNT.FullTrustApp.Gui;

internal sealed class GdiDrawing(WindowManager windows) : IWindowDrawing, IDisposable
{
    private sealed class SurfaceState(GdiSurface surface)
    {
        public GdiSurface Surface { get; } = surface;
        public bool Erase = true;
        public bool Invalid = true;
        public int DrawingThread = Environment.CurrentManagedThreadId;
        public Stack<int> PaintStates { get; } = new();
    }

    private readonly object _gate = new();
    private readonly Dictionary<ulong, SurfaceState> _surfaces = new();
    private bool _disposed;

    public ulong GetSystemBrush(int index) => (ulong)GdiNative.GetSysColorBrush(index);

    private SurfaceState? GetSurface(ulong hwnd)
    {
        if (_disposed) return null;
        GuiRect client;
        if (hwnd == 0) client = new(0, 0, windows.ScreenWidth, windows.ScreenHeight);
        else if (!windows.GetWindowRect(hwnd, out _, out client)) return null;
        if (!_surfaces.TryGetValue(hwnd, out var state))
        {
            state = new(new GdiSurface(client.Width, client.Height));
            _surfaces.Add(hwnd, state);
            EraseBackground(hwnd, (ulong)state.Surface.Dc);
        }
        else if (state.Surface.Width != Math.Max(1, client.Width) || state.Surface.Height != Math.Max(1, client.Height))
        {
            state.Surface.Resize(client.Width, client.Height);
            state.Erase = state.Invalid = true;
        }
        return state;
    }

    public ulong GetDC(ulong hwnd)
    {
        lock (_gate)
        {
            var state = GetSurface(hwnd);
            if (state is null) return 0;
            state.DrawingThread = Environment.CurrentManagedThreadId;
            return (ulong)state.Surface.Dc;
        }
    }

    public ulong BeginPaint(ApiCallContext ctx)
    {
        lock (_gate)
        {
            if (ctx.Arg(1) == 0) { ctx.SetLastError(87); return 0; }
            var hwnd = ctx.Arg(0);
            var state = GetSurface(hwnd);
            if (state is null) { ctx.SetLastError(1400); return 0; }
            var surface = state.Surface;
            state.DrawingThread = Environment.CurrentManagedThreadId;
            state.PaintStates.Push(GdiNative.SaveDC(surface.Dc));
            GdiNative.IntersectClipRect(surface.Dc, 0, 0, surface.Width, surface.Height);
            var erase = state.Erase;
            state.Erase = state.Invalid = false;
            var erased = !erase || WindowApis.SendCore(ctx, windows, hwnd, Win32.WM_ERASEBKGND, (ulong)surface.Dc, 0) != 0;
            ctx.Write(ctx.Arg(1), new NativePaintStruct
            {
                Hdc = (ulong)surface.Dc, Erase = erased ? 0 : 1,
                Paint = new NativeRect(0, 0, surface.Width, surface.Height)
            });
            return (ulong)surface.Dc;
        }
    }

    public bool EndPaint(ulong hwnd, ulong dc)
    {
        lock (_gate)
        {
            if (!_surfaces.TryGetValue(hwnd, out var state) || (ulong)state.Surface.Dc != dc || state.PaintStates.Count == 0) return false;
            var saved = state.PaintStates.Pop();
            if (saved != 0) GdiNative.RestoreDC(state.Surface.Dc, saved);
            state.Surface.Publish();
            return true;
        }
    }

    public bool ReleaseDC(ulong hwnd, ulong dc)
    {
        lock (_gate)
        {
            if (!_surfaces.TryGetValue(hwnd, out var state) || (ulong)state.Surface.Dc != dc) return false;
            state.Surface.Publish();
            return true;
        }
    }

    public bool EraseBackground(ulong hwnd, ulong dc)
    {
        if (!windows.TryGetWindow(hwnd, out var window) || window is null || window.BackgroundBrush == 0 ||
            !windows.GetWindowRect(hwnd, out _, out var client)) return false;
        var rect = new NativeRect(0, 0, client.Width, client.Height);
        var brush = window.BackgroundBrush <= 31 ? GetSystemBrush((int)window.BackgroundBrush - 1) : window.BackgroundBrush;
        return GdiNative.FillRect((nint)dc, ref rect, (nint)brush) != 0;
    }

    public void Invalidate(ulong hwnd, bool erase)
    {
        lock (_gate)
        {
            // UI invalidation must not replace a bitmap while the guest is drawing into its DC.
            if (_disposed || !_surfaces.TryGetValue(hwnd, out var state)) return;
            state.Invalid = true;
            state.Erase |= erase;
        }
    }

    public bool GetUpdateRect(ulong hwnd, out NativeRect rect)
    {
        lock (_gate)
        {
            var state = GetSurface(hwnd);
            rect = state is { Invalid: true } ? new(0, 0, state.Surface.Width, state.Surface.Height) : default;
            return state is { Invalid: true };
        }
    }

    public void Validate(ulong hwnd)
    {
        lock (_gate)
            if (_surfaces.TryGetValue(hwnd, out var state)) state.Invalid = state.Erase = false;
    }

    public void Present(ulong hwnd)
    {
        lock (_gate)
        {
            if (!_surfaces.TryGetValue(hwnd, out var state) || state.PaintStates.Count != 0) return;
            state.Surface.Publish();
        }
    }

    public GdiFrame? GetFrame(ulong hwnd)
    {
        lock (_gate) return _surfaces.GetValueOrDefault(hwnd)?.Surface.Frame;
    }

    public ulong WindowFromDC(ulong dc)
    {
        lock (_gate) return _surfaces.FirstOrDefault(p => (ulong)p.Value.Surface.Dc == dc).Key;
    }

    public void Flush()
    {
        lock (_gate)
            foreach (var state in _surfaces.Values)
                if (state.DrawingThread == Environment.CurrentManagedThreadId) state.Surface.Publish();
    }

    public void Prune(IReadOnlyList<WindowSnapshot> snapshots)
    {
        var handles = snapshots.Select(s => s.Handle).ToHashSet();
        lock (_gate)
            foreach (var hwnd in _surfaces.Keys.Where(h => h != 0 && !handles.Contains(h) && !windows.TryGetWindow(h, out _)).ToArray())
            {
                _surfaces[hwnd].Surface.Dispose();
                _surfaces.Remove(hwnd);
            }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (var state in _surfaces.Values) state.Surface.Dispose();
            _surfaces.Clear();
        }
    }
}

internal static class GdiApis
{
    private static GdiDrawing Drawing(ApiCallContext ctx) => (GdiDrawing)GuiLibrary.GetManager(ctx.Runtime).Drawing!;

    [VirtualApi("user32.dll", "GetDC", ApiCategory.Gui)]
    private static ulong GetDC(ApiCallContext ctx)
    {
        try
        {
            var dc = Drawing(ctx).GetDC(ctx.Arg(0));
            if (dc == 0) ctx.SetLastError(1400);
            return dc;
        }
        catch (Win32Exception ex)
        {
            ctx.Runtime.Trace.Error($"GetDC failed: {ex}", ctx.Process.Id, ctx.Thread.Id);
            ctx.SetLastError(ex.NativeErrorCode);
            return 0;
        }
    }

    [VirtualApi("user32.dll", "GetDCEx", ApiCategory.Gui)]
    private static ulong GetDCEx(ApiCallContext ctx)
    {
        if (ctx.Arg(1) != 0 || (ctx.Arg32(2) & (1 | 0x40 | 0x80)) != 0)
        {
            ctx.SetLastError(50);
            return 0;
        }
        return GetDC(ctx);
    }

    [VirtualApi("user32.dll", "BeginPaint", ApiCategory.Gui)]
    private static ulong BeginPaint(ApiCallContext ctx) => Drawing(ctx).BeginPaint(ctx);

    [VirtualApi("user32.dll", "EndPaint", ApiCategory.Gui)]
    private static ulong EndPaint(ApiCallContext ctx) => ctx.Arg(1) != 0 &&
        Drawing(ctx).EndPaint(ctx.Arg(0), ctx.Read<NativePaintStruct>(ctx.Arg(1)).Hdc) ? 1UL : 0UL;

    [VirtualApi("user32.dll", "ReleaseDC", ApiCategory.Gui)]
    private static ulong ReleaseDC(ApiCallContext ctx) => Drawing(ctx).ReleaseDC(ctx.Arg(0), ctx.Arg(1)) ? 1UL : 0UL;

    [VirtualApi("user32.dll", "WindowFromDC", ApiCategory.Gui)]
    private static ulong WindowFromDC(ApiCallContext ctx) => Drawing(ctx).WindowFromDC(ctx.Arg(0));

    [VirtualApi("user32.dll", "GetUpdateRect", ApiCategory.Gui)]
    private static ulong GetUpdateRect(ApiCallContext ctx)
    {
        var invalid = Drawing(ctx).GetUpdateRect(ctx.Arg(0), out var rect);
        if (ctx.Arg(1) != 0) ctx.Write(ctx.Arg(1), rect);
        return invalid ? 1UL : 0UL;
    }

    [VirtualApi("gdi32.dll", "GdiFlush", ApiCategory.Gui)]
    private static ulong Flush(ApiCallContext ctx)
    {
        var result = GdiNative.GdiFlush();
        Drawing(ctx).Flush();
        return result ? 1UL : 0UL;
    }
}
