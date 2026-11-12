namespace ImaginedNT.Core.Diagnostics;

public enum TraceKind
{
    Info,
    Warning,
    Error,
    Image,
    Import,
    Api,
    Syscall,
    Memory,
    Object,
    File,
    Thread,
    Process,
    Wait,
    Security
}

public readonly record struct TraceEvent(
    DateTimeOffset Timestamp,
    TraceKind Kind,
    int ProcessId,
    int ThreadId,
    string Message);

/// <summary>
/// A thread-safe, in-memory and optionally on-disk trace of everything the NT layer observed.
/// The host turns this into a human-readable log; tests assert against <see cref="Snapshot"/>.
/// </summary>
public sealed class TraceLog : IDisposable
{
    private readonly object _gate = new();
    private readonly List<TraceEvent> _events = new();
    private readonly TextWriter? _console;
    private StreamWriter? _file;
    private bool _disposed;

    public TraceLog(TextWriter? console = null, string? filePath = null)
    {
        _console = console;
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            var full = Path.GetFullPath(filePath);
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            _file = new StreamWriter(new FileStream(full, FileMode.Create, System.IO.FileAccess.Write, FileShare.Read))
            {
                AutoFlush = true
            };
        }
    }

    public int Count
    {
        get { lock (_gate) return _events.Count; }
    }

    public void Emit(TraceKind kind, string message, int processId = 0, int threadId = 0)
    {
        var ev = new TraceEvent(DateTimeOffset.Now, kind, processId, threadId, message);
        lock (_gate)
        {
            if (_disposed) return;
            _events.Add(ev);
            var line = $"{ev.Timestamp:HH:mm:ss.fff} [p{ev.ProcessId}:t{ev.ThreadId}] {kind,-9} {message}";
            _console?.WriteLine(line);
            if (_file is not null)
            {
                _file.WriteLine($"{{\"ts\":\"{ev.Timestamp:O}\",\"pid\":{ev.ProcessId},\"tid\":{ev.ThreadId},\"kind\":\"{kind}\",\"msg\":{JsonEscape(message)}}}");
            }
        }
    }

    public void Info(string message, int pid = 0, int tid = 0) => Emit(TraceKind.Info, message, pid, tid);
    public void Warn(string message, int pid = 0, int tid = 0) => Emit(TraceKind.Warning, message, pid, tid);
    public void Error(string message, int pid = 0, int tid = 0) => Emit(TraceKind.Error, message, pid, tid);

    public IReadOnlyList<TraceEvent> Snapshot()
    {
        lock (_gate) return _events.ToArray();
    }

    public bool Contains(string substring, TraceKind? kind = null)
    {
        lock (_gate)
        {
            foreach (var e in _events)
            {
                if (kind is not null && e.Kind != kind) continue;
                if (e.Message.Contains(substring, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }

    public void Flush()
    {
        lock (_gate)
        {
            _console?.Flush();
            _file?.Flush();
        }
    }

    /// <summary>Release the log file while allowing late events to remain available in memory.</summary>
    public void CloseFile()
    {
        lock (_gate)
        {
            try { _file?.Dispose(); }
            finally { _file = null; }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            CloseFile();
        }
    }

    private static string JsonEscape(string value)
    {
        var sb = new System.Text.StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\r': sb.Append("\\r"); break;
                case '\n': sb.Append("\\n"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append($"\\u{(int)c:X4}");
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }
}
