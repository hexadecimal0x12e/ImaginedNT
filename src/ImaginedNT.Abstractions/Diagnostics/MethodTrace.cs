using System.Text;

namespace ImaginedNT.Core.Diagnostics;

/// <summary>
/// Entry-point logger for the debug-only method instrumentation produced by
/// <c>tools/ImaginedNT.Weaver</c>.  The weaver rewrites every method body so that it first calls
/// <see cref="Enter(string, string, object?[])"/>; the injected prologue checks the
/// <see cref="Enabled"/> field first, so an instrumented build with tracing turned off only pays
/// for one static-field read and a branch per call.
///
/// <para>
/// Tracing is opt-in: <c>--trace-methods</c> (or <c>IMAGINEDNT_TRACE_METHODS=1</c>) enables it.
/// Use <c>--trace-methods-filter</c> / <c>IMAGINEDNT_TRACE_METHODS_FILTER</c> to log only methods
/// whose <c>Type.Method</c> name contains one of the comma-separated substrings.  The optional
/// <c>--trace-methods-file</c> / <c>IMAGINEDNT_TRACE_METHODS_FILE</c> redirects the log to a file.
/// </para>
/// </summary>
public static class MethodTrace
{
    /// <summary>Guard read by the injected method prologue.</summary>
    public static bool Enabled;

    private static readonly object Gate = new();
    private static string[] _filters = Array.Empty<string>();
    private static TextWriter? _writer;
    private static bool _ownsWriter;
    private static long _sequence;
    private static int _dropped;

    [ThreadStatic]
    private static bool _formatting;

    static MethodTrace()
    {
        try
        {
            var value = Environment.GetEnvironmentVariable("IMAGINEDNT_TRACE_METHODS");
            if (value is "1" or "true" or "TRUE" or "on" or "all")
            {
                Enable(
                    Environment.GetEnvironmentVariable("IMAGINEDNT_TRACE_METHODS_FILTER"),
                    Environment.GetEnvironmentVariable("IMAGINEDNT_TRACE_METHODS_FILE"));
            }
        }
        catch
        {
            // A bad environment variable must not turn into a TypeInitializationException at the
            // first instrumented method.
            Enabled = false;
        }
    }

    /// <summary>Turn method tracing on, optionally restricting it to matching Type.Method names.</summary>
    public static void Enable(string? filter = null, string? file = null)
    {
        lock (Gate)
        {
            ReleaseOwnedWriter();

            if (!string.IsNullOrWhiteSpace(file))
            {
                var full = Path.GetFullPath(file);
                var dir = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                _writer = new StreamWriter(new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    AutoFlush = true
                };
                _ownsWriter = true;
            }
            else
            {
                _writer = Console.Error;
            }

            _filters = ParseFilters(filter);
            _dropped = 0;
            Enabled = true;
        }
    }

    /// <summary>Turn method tracing on with an explicit writer (embedding hosts and tests).</summary>
    public static void Enable(TextWriter writer, string? filter = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        lock (Gate)
        {
            ReleaseOwnedWriter();
            _writer = writer;
            _filters = ParseFilters(filter);
            _dropped = 0;
            Enabled = true;
        }
    }

    private static string[] ParseFilters(string? filter) =>
        string.IsNullOrWhiteSpace(filter)
            ? Array.Empty<string>()
            : filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static void ReleaseOwnedWriter()
    {
        if (!_ownsWriter) return;
        try { _writer?.Dispose(); } catch { /* ignore */ }
        _writer = null;
        _ownsWriter = false;
    }

    /// <summary>Turn tracing off without discarding the configured filter/writer.</summary>
    public static void Disable() => Enabled = false;

    /// <summary>Called by the injected method prologue.</summary>
    public static void Enter(string type, string method, object?[] arguments)
    {
        if (!Enabled || _formatting) return;
        var name = string.Concat(type, ".", method);
        if (!Matches(name)) return;

        _formatting = true;
        try
        {
            var writer = _writer;
            if (writer is null) return;

            var line = new StringBuilder(160);
            line.Append('#').Append(Interlocked.Increment(ref _sequence).ToString("D6"));
            line.Append(" t").Append(Environment.CurrentManagedThreadId.ToString("D2"));
            line.Append(" > ").Append(type).Append('.').Append(method).Append('(');
            for (var i = 0; i < arguments.Length; i++)
            {
                if (i != 0) line.Append(", ");
                AppendArgument(line, arguments[i]);
            }
            line.Append(')');

            lock (Gate) writer.WriteLine(line.ToString());
        }
        catch
        {
            // A broken ToString()/writer must never take the guest down.
            Interlocked.Increment(ref _dropped);
        }
        finally
        {
            _formatting = false;
        }
    }

    /// <summary>Number of entries dropped because formatting or writing threw.</summary>
    public static int DroppedCount => Volatile.Read(ref _dropped);

    private static bool Matches(string name)
    {
        var filters = _filters;
        if (filters.Length == 0) return true;
        foreach (var filter in filters)
            if (name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static void AppendArgument(StringBuilder builder, object? value)
    {
        switch (value)
        {
            case null:
                builder.Append("null");
                return;
            case string text:
                builder.Append('"').Append(Truncate(Escape(text), 80)).Append('"');
                return;
            case char character:
                builder.Append('\'').Append(character).Append('\'');
                return;
            case bool boolean:
                builder.Append(boolean ? "true" : "false");
                return;
            case Type type:
                builder.Append(type.FullName);
                return;
            case Array array:
                builder.Append(array.GetType().GetElementType()?.Name ?? "Array")
                    .Append('[').Append(array.Length).Append(']');
                return;
        }

        string? formatted;
        try { formatted = value.ToString(); }
        catch (Exception ex) { formatted = '<' + ex.GetType().Name + '>'; }

        if (string.IsNullOrEmpty(formatted) || formatted == value.GetType().FullName)
            builder.Append(value.GetType().Name);
        else
            builder.Append(Truncate(Escape(formatted), 96));
    }

    private static string Escape(string value) =>
        value.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "...";
}
