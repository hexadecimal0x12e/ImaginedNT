using ImaginedNT.Core.Diagnostics;
using ImaginedNT.Core.Objects;

namespace ImaginedNT.Core.Runtime;

public sealed class WindowStationObject : NtObject
{
    public override ObjectType ObjectType => ObjectType.WindowStation;
    public override bool IsContainer => true;
    public uint SessionId { get; }
    public bool IsInteractive => string.Equals(Name, "WinSta0", StringComparison.OrdinalIgnoreCase);
    public override string Info => $"session={SessionId}, interactive={IsInteractive}";
    internal WindowStationObject(uint sessionId) => SessionId = sessionId;
}

public sealed class DesktopObject : NtObject
{
    public override ObjectType ObjectType => ObjectType.Desktop;
    public WindowStationObject WindowStation { get; }
    public uint SessionId => WindowStation.SessionId;
    public int WindowCount { get; internal set; }
    public override string Info => $"session={SessionId}, station={WindowStation.Name}, windows={WindowCount}";
    internal DesktopObject(WindowStationObject station) => WindowStation = station;
}

/// <summary>会话内的 USER 对象；命名空间保留对象，直到管理端删除或运行时销毁。</summary>
public sealed class WindowStationManager
{
    private readonly ImaginedRuntime _runtime;
    private readonly Dictionary<(uint Session, string Name), WindowStationObject> _stations = new();
    private readonly Dictionary<WindowStationObject, List<DesktopObject>> _desktops = new();
    private readonly Dictionary<uint, DesktopObject> _inputDesktops = new();
    private readonly Dictionary<ImaginedThread, int> _threadWindows = new();

    internal WindowStationManager(ImaginedRuntime runtime) => _runtime = runtime;
    private object Gate => _runtime.StateGate;

    public IReadOnlyList<WindowStationObject> Stations
    {
        get { lock (Gate) return _stations.Values.OrderBy(s => s.SessionId).ThenBy(s => s.Name).ToArray(); }
    }

    public IReadOnlyList<DesktopObject> GetDesktops(WindowStationObject station)
    {
        lock (Gate) return _desktops.TryGetValue(station, out var list) ? list.ToArray() : [];
    }

    public WindowStationObject? FindStation(uint sessionId, string name)
    {
        lock (Gate) return _stations.GetValueOrDefault((sessionId, name.ToUpperInvariant()));
    }

    public DesktopObject? FindDesktop(WindowStationObject station, string name)
    {
        lock (Gate) return GetDesktops(station).FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public WindowStationObject CreateStation(uint sessionId, string name)
    {
        ValidateName(name);
        lock (Gate)
        {
            if (_runtime.GetSession(sessionId) is null) throw new ArgumentException("会话不存在。");
            if (FindStation(sessionId, name) is { } existing) return existing;
            var station = new WindowStationObject(sessionId);
            _runtime.Objects.CreateObject($@"\Sessions\{sessionId}\Windows\WindowStations\{name}", station);
            station.Release(); // 命名空间持有对象。
            _stations.Add((sessionId, name.ToUpperInvariant()), station);
            _desktops.Add(station, []);
            _runtime.Trace.Emit(TraceKind.Info, $"created window station: session={sessionId}, name={name}");
            return station;
        }
    }

    public DesktopObject CreateDesktop(WindowStationObject station, string name)
    {
        ValidateName(name);
        lock (Gate)
        {
            EnsureStation(station);
            if (FindDesktop(station, name) is { } existing) return existing;
            var desktop = new DesktopObject(station);
            _runtime.Objects.CreateObject($@"{station.FullPath}\{name}", desktop);
            desktop.Release();
            _desktops[station].Add(desktop);
            _runtime.Trace.Emit(TraceKind.Info, $"created desktop: {desktop.FullPath}");
            return desktop;
        }
    }

    internal void InitializeSession(uint sessionId)
    {
        var desktop = CreateDesktop(CreateStation(sessionId, "WinSta0"), "Default");
        _inputDesktops.Add(sessionId, desktop);
    }

    public DesktopObject? GetInputDesktop(uint sessionId)
    {
        lock (Gate) return _inputDesktops.GetValueOrDefault(sessionId);
    }

    public void SwitchDesktop(DesktopObject desktop)
    {
        lock (Gate)
        {
            EnsureDesktop(desktop);
            if (!desktop.WindowStation.IsInteractive) throw new UnauthorizedAccessException("非交互窗口站不能接收桌面输入。");
            if (GetInputDesktop(desktop.SessionId) == desktop) return;
            _inputDesktops[desktop.SessionId] = desktop;
            _runtime.Trace.Emit(TraceKind.Info, $"switched input desktop: {desktop.FullPath}");
        }
    }

    internal DesktopObject ResolveStartupDesktop(uint sessionId, ImaginedProcess? parent, string? path)
    {
        lock (Gate)
        {
            var station = parent?.SessionId == sessionId ? parent.WindowStation : FindStation(sessionId, "WinSta0")!;
            if (string.IsNullOrEmpty(path))
            {
                if (parent?.SessionId == sessionId && parent.StartupDesktop.WindowStation == station) return parent.StartupDesktop;
                return FindDesktop(station, "Default") ?? throw new ArgumentException("窗口站中没有默认桌面。");
            }
            var parts = path.Split('\\');
            if (parts.Length == 2) station = FindStation(sessionId, parts[0]) ?? throw new ArgumentException("窗口站不存在。");
            else if (parts.Length != 1) throw new ArgumentException("桌面路径必须是 Desktop 或 WindowStation\\Desktop。");
            return FindDesktop(station, parts[^1]) ?? throw new ArgumentException("桌面不存在。");
        }
    }

    public void SetProcessWindowStation(ImaginedProcess process, WindowStationObject station)
    {
        lock (Gate)
        {
            EnsureStation(station);
            if (process.Runtime != _runtime) throw new ArgumentException("进程不属于此运行时。");
            if (station.SessionId != process.SessionId) throw new UnauthorizedAccessException("窗口站不属于当前会话。");
            process.WindowStation = station;
        }
    }

    public bool SetThreadDesktop(ImaginedThread thread, DesktopObject desktop)
    {
        lock (Gate)
        {
            EnsureDesktop(desktop);
            if (thread.Process.Runtime != _runtime) throw new ArgumentException("线程不属于此运行时。");
            if (desktop.WindowStation != thread.Process.WindowStation) throw new UnauthorizedAccessException("桌面不属于进程的窗口站。");
            if (thread.Desktop == desktop) return true;
            if (_threadWindows.GetValueOrDefault(thread) != 0) return false;
            thread.Desktop = desktop;
            thread.DesktopHandle = 0;
            return true;
        }
    }

    public DesktopObject RegisterWindow(ImaginedThread thread)
    {
        lock (Gate)
        {
            EnsureDesktop(thread.Desktop);
            if (thread.Desktop.WindowStation != thread.Process.WindowStation)
                throw new InvalidOperationException("线程桌面不属于进程的窗口站。");
            _threadWindows[thread] = _threadWindows.GetValueOrDefault(thread) + 1;
            thread.Desktop.WindowCount++;
            return thread.Desktop;
        }
    }

    public void UnregisterWindow(ImaginedThread thread, DesktopObject desktop)
    {
        lock (Gate)
        {
            var count = _threadWindows.GetValueOrDefault(thread);
            if (count <= 1) _threadWindows.Remove(thread);
            else _threadWindows[thread] = count - 1;
            if (desktop.WindowCount > 0) desktop.WindowCount--;
        }
    }

    public bool DeleteDesktop(DesktopObject desktop)
    {
        lock (Gate)
        {
            EnsureDesktop(desktop);
            if (desktop.Name!.Equals("Default", StringComparison.OrdinalIgnoreCase) ||
                desktop == GetInputDesktop(desktop.SessionId) || desktop.WindowCount != 0 || HasUsers(desktop)) return false;
            if (!_runtime.Objects.Delete(desktop.FullPath!)) return false;
            _desktops[desktop.WindowStation].Remove(desktop);
            _runtime.Trace.Emit(TraceKind.Info, $"deleted desktop: {desktop.FullPath}");
            return true;
        }
    }

    public bool DeleteStation(WindowStationObject station)
    {
        lock (Gate)
        {
            EnsureStation(station);
            if (station.IsInteractive || HasUsers(station) || _desktops[station].Any(d =>
                    d.WindowCount != 0 || d == GetInputDesktop(d.SessionId) || HasUsers(d))) return false;
            _runtime.Objects.DeleteTree(station.FullPath!);
            _desktops.Remove(station);
            _stations.Remove((station.SessionId, station.Name!.ToUpperInvariant()));
            _runtime.Trace.Emit(TraceKind.Info, $"deleted window station: {station.FullPath}");
            return true;
        }
    }

    private bool HasUsers(NtObject obj) => _runtime.Processes.Any(p =>
        (!p.IsTerminating && (p.WindowStation == obj || p.StartupDesktop == obj || p.Threads.Any(t =>
            t.State is ThreadState.Created or ThreadState.Running && t.Desktop == obj))) ||
        p.Handles.Snapshot().Any(h => h.Object == obj));

    internal void DeleteSession(uint sessionId)
    {
        lock (Gate)
        {
            foreach (var station in _stations.Values.Where(s => s.SessionId == sessionId).ToArray())
            {
                _desktops.Remove(station);
                _stations.Remove((sessionId, station.Name!.ToUpperInvariant()));
            }
            _inputDesktops.Remove(sessionId);
        }
    }

    public bool Contains(DesktopObject desktop)
    {
        lock (Gate) return !desktop.IsDisposed && _desktops.TryGetValue(desktop.WindowStation, out var list) && list.Contains(desktop);
    }

    private void EnsureDesktop(DesktopObject desktop)
    {
        if (!Contains(desktop)) throw new ArgumentException("桌面不属于此运行时或已被删除。");
    }

    private void EnsureStation(WindowStationObject station)
    {
        if (station.IsDisposed || FindStation(station.SessionId, station.Name!) != station)
            throw new ArgumentException("窗口站不属于此运行时或已被删除。");
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 256 || name.IndexOfAny(['\\', '/', '\0']) >= 0 || name != name.Trim())
            throw new ArgumentException("名称不能为空、超过 256 字符或包含路径分隔符。");
    }
}
