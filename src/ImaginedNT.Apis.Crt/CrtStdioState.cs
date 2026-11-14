using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ImaginedNT.Core.Io;
using ImaginedNT.Core.Nt;
using ImaginedNT.Core.Objects;
using ImaginedNT.Core.Runtime;
using Microsoft.Win32.SafeHandles;

namespace ImaginedNT.Apis.Crt;

// UCRT owns the FILE buffers and text/Unicode conversion. Descriptors, paths and lifetime belong to the guest.
internal sealed partial class CrtStdioState : IDisposable
{
    private static readonly ConditionalWeakTable<ImaginedProcess, CrtStdioState> States = new();
    private static readonly object PrintGate = new();
    private readonly ConcurrentDictionary<int, Descriptor> _descriptors = new();
    private readonly ConcurrentDictionary<int, OsHandle> _osHandles = new();
    private readonly ConcurrentDictionary<ulong, int> _streams = new();
    private readonly ConcurrentDictionary<ulong, byte> _temporary = new();
    private readonly ConcurrentDictionary<ulong, ImaginedProcess> _children = new();
    private readonly ulong[] _standard = new ulong[3];
    private readonly bool[] _standardInitialized = new bool[3];
    private readonly List<(AnonymousPipeServerStream Pipe, Thread Worker)> _pipes = new();
    private readonly object _gate = new();
    private readonly object _inputGate = new();
    private readonly SemaphoreSlim _inputSignal = new(0);
    private CancellationTokenSource? _inputCancellation;
    private int _inputReaders;
    private volatile bool _directInputPump;
    private int _maxStdio = 512, _printfCount;
    private int _disposing;
    private volatile bool _closed;
    private volatile bool _inputEnded;
    private readonly ImaginedProcess _process;

    private sealed record Descriptor(int Host, int Flags, string? Path, int Standard = -1);
    private sealed record OsHandle(ulong Value, NtObject Object);
    private int StreamCount => _streams.Count + _standardInitialized.Count(static initialized => !initialized);

    private CrtStdioState(ImaginedProcess process) => _process = process;

    internal static CrtStdioState Get(ApiCallContext ctx)
    {
        lock (States)
        {
            if (States.TryGetValue(ctx.Process, out var state)) return state;
            state = new CrtStdioState(ctx.Process);
            ctx.Process.Disposing += state.Dispose;
            States.Add(ctx.Process, state);
            return state;
        }
    }

    internal ulong StandardStream(ApiCallContext ctx, int id)
    {
        lock (_gate)
        {
            if (_closed || _standardInitialized[id]) return _standard[id];
            ctx.TryGetFile((ulong)id, out var file);
            var native = file?.GetNativeHandle() ?? 0;
            if (file is not null && native != 0)
            {
                var copy = Duplicate(native);
                var host = (int)Native.Call("_open_osfhandle", [(ulong)copy, id == 0 ? 0x4000UL : 0x4001UL]);
                if (host < 0)
                {
                    new SafeFileHandle(copy, true).Dispose();
                    return 0;
                }
                var direct = OpenDescriptorStream(host, id == 0 ? "r" : "w");
                if (direct == 0)
                {
                    Native.Call("_close", [(ulong)host]);
                    return 0;
                }
                _descriptors[id] = new(host, id == 0 ? 0 : 1, file.Path);
                _streams[direct] = id;
                _standard[id] = direct;
                _standardInitialized[id] = true;
                if (id != 0) Native.Call("setvbuf", [direct, 0, 4, 0]);
                return direct;
            }
            var pipe = new AnonymousPipeServerStream(id == 0 ? PipeDirection.Out : PipeDirection.In,
                HandleInheritability.None);
            var handle = Duplicate(pipe.ClientSafePipeHandle.DangerousGetHandle());
            pipe.DisposeLocalCopyOfClientHandle();
            var fd = (int)Native.Call("_open_osfhandle", [(ulong)handle, id == 0 ? 0x4000UL : 0x4001UL]);
            if (fd < 0)
            {
                new SafeFileHandle(handle, true).Dispose();
                pipe.Dispose();
                return 0;
            }
            _descriptors[id] = new(fd, id == 0 ? 0 : 1, null, id);
            var stream = OpenDescriptorStream(fd, id == 0 ? "r" : "w");
            if (stream == 0)
            {
                _descriptors.TryRemove(id, out _);
                Native.Call("_close", [(ulong)fd]);
                pipe.Dispose();
                return 0;
            }
            _streams[stream] = id;
            _standard[id] = stream;
            _standardInitialized[id] = true;
            if (id != 0) Native.Call("setvbuf", [stream, 0, 4, 0]);
            var worker = new Thread(() =>
            {
                if (id == 0) PumpInput(ctx, pipe).GetAwaiter().GetResult();
                else Pump(ctx, id, pipe);
            })
            {
                IsBackground = true,
                Name = $"CRT stdio {ctx.Process.Id}:{id}"
            };
            _pipes.Add((pipe, worker));
            worker.Start();
            return stream;
        }
    }

    /// <summary>
    /// Keep the standard-input pipe fed even when the guest reads it directly through a handle
    /// returned by <c>_get_osfhandle(0)</c> instead of going through CRT <c>_read</c>/<c>fread</c>.
    /// cmd.exe does this, so relying on <see cref="BeginInput"/> alone leaves it blocked forever.
    /// </summary>
    private void EnsureDirectInputPump()
    {
        lock (_inputGate)
        {
            if (_inputEnded || _closed) return;
            _directInputPump = true;
            if (_inputSignal.CurrentCount == 0) _inputSignal.Release();
        }
    }

    private InputLease? BeginInput(ApiCallContext ctx, int fd)
    {
        if (_inputEnded || fd < 0 || !TryDescriptor(ctx, fd, out var descriptor) || descriptor.Standard != 0) return null;
        lock (_inputGate)
        {
            if (_inputReaders++ == 0)
            {
                _inputCancellation = new();
                if (_inputSignal.CurrentCount == 0) _inputSignal.Release();
            }
        }
        return new(this);
    }

    private sealed class InputLease(CrtStdioState state) : IDisposable
    {
        public void Dispose()
        {
            lock (state._inputGate)
                if (--state._inputReaders == 0) state._inputCancellation?.Cancel();
        }
    }

    private async Task PumpInput(ApiCallContext ctx, AnonymousPipeServerStream pipe)
    {
        try
        {
            var bytes = new byte[1];
            while (!_closed)
            {
                if (_directInputPump)
                {
                    // The guest obtained the native stdin-pipe handle; feed it until EOF or
                    // teardown, with no CRT reader involved.  Read in chunks rather than one byte
                    // at a time: cmd.exe treats a short pipe read as end-of-input.
                    var directBuffer = new byte[4096];
                    var directTotal = 0;
                    while (!_closed)
                    {
                        if (!ctx.TryGetFile(0, out var directFile) || directFile is null) return;
                        var directCount = await directFile.ReadAsync(directBuffer).ConfigureAwait(false);
                        if (directCount == 0)
                        {
                            ctx.Runtime.Trace.Warn($"CRT stdin pump: end of input after {directTotal} byte(s)",
                                ctx.Process.Id, ctx.Thread.Id);
                            return;
                        }
                        await pipe.WriteAsync(directBuffer.AsMemory(0, directCount)).ConfigureAwait(false);
                        directTotal += directCount;
                    }
                    return;
                }
                await _inputSignal.WaitAsync().ConfigureAwait(false);
                if (_closed) break;
                if (_directInputPump) continue;
                CancellationTokenSource cancellation;
                lock (_inputGate)
                {
                    if (_inputReaders == 0 || _inputCancellation is null) continue;
                    cancellation = _inputCancellation;
                }
                try
                {
                    while (!cancellation.IsCancellationRequested && !_closed)
                    {
                        if (!ctx.TryGetFile(0, out var file) || file is null) return;
                        var count = await file.ReadAsync(bytes, cancellation.Token).ConfigureAwait(false);
                        if (count == 0) return;
                        await pipe.WriteAsync(bytes.AsMemory(0, count)).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { }
                finally
                {
                    lock (_inputGate)
                    {
                        if (ReferenceEquals(_inputCancellation, cancellation)) _inputCancellation = null;
                        cancellation.Dispose();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            if (!_closed) ctx.Runtime.Trace.Warn($"CRT standard input: {ex}", ctx.Process.Id, ctx.Thread.Id);
        }
        finally
        {
            _inputEnded = true;
            pipe.Dispose();
        }
    }

    private void Pump(ApiCallContext ctx, int id, AnonymousPipeServerStream pipe)
    {
        try
        {
            var bytes = new byte[4096];
            while (!_closed)
            {
                if (!ctx.TryGetFile((ulong)id, out var file) || file is null) break;
                var count = pipe.Read(bytes);
                if (count == 0) break;
                file.Write(bytes.AsSpan(0, count));
                file.Flush();
            }
        }
        catch (Exception ex)
        {
            if (!_closed) ctx.Runtime.Trace.Warn($"CRT standard stream {id}: {ex}", ctx.Process.Id, ctx.Thread.Id);
        }
        finally { pipe.Dispose(); }
    }

    internal unsafe ulong Invoke(ApiCallContext ctx)
    {
        var name = ctx.Name.StartsWith("_o_", StringComparison.Ordinal) ? ctx.Name[3..] : ctx.Name;
        var previous = Native.SetInvalidHandler((nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, uint, nuint, void>)&InvalidParameter);
        Native.InvalidParameterSeen = false;
        try
        {
            if (_closed || _disposing != 0) return Error(ctx, name, 9);
            return Dispatch(ctx, name);
        }
        catch (Exception ex)
        {
            ctx.Runtime.Trace.Warn($"{ctx.Name}: {ex}", ctx.Process.Id, ctx.Thread.Id);
            return Error(ctx, name, ex switch
            {
                UnauthorizedAccessException => 13,
                OutOfMemoryException => 12,
                FileNotFoundException or DirectoryNotFoundException => 2,
                NtFileException file => FileErrno(file.Status),
                IOException => 5,
                _ => 22
            });
        }
        finally
        {
            if (Native.InvalidParameterSeen)
                ctx.Runtime.Trace.Warn($"{ctx.Name}: invalid CRT stdio arguments", ctx.Process.Id, ctx.Thread.Id);
            Native.SetInvalidHandler(previous);
        }
    }

    private static int FileErrno(uint status) => status switch
    {
        NtStatus.AccessDenied or NtStatus.SharingViolation => 13,
        NtStatus.ObjectNameNotFound or NtStatus.ObjectPathNotFound => 2,
        _ => 22
    };

    private ulong Dispatch(ApiCallContext ctx, string name)
    {
        Span<ulong> a = stackalloc ulong[8];
        for (var i = 0; i < a.Length; i++) a[i] = ctx.Arg(i);
        switch (name)
        {
            case "fopen": case "_wfopen": case "_fsopen": case "_wfsopen":
            case "fopen_s": case "_wfopen_s": case "freopen": case "_wfreopen":
            case "freopen_s": case "_wfreopen_s":
                return OpenStream(ctx, name);
            case "_open": case "_wopen": case "_sopen": case "_wsopen":
            case "_sopen_s": case "_wsopen_s": case "_sopen_dispatch": case "_wsopen_dispatch":
            case "_creat": case "_wcreat":
                return OpenFile(ctx, name);
            case "_wfdopen":
                if (!TryDescriptor(ctx, (int)a[0], out var descriptor)) return Error(ctx, name, 9);
                if (_streams.Values.Contains((int)a[0])) return Error(ctx, name, 22);
                if (StreamCount >= _maxStdio) return Error(ctx, name, 24);
                a[0] = (ulong)descriptor.Host;
                var attached = Native.Call(name, a);
                if (attached != 0) _streams[attached] = ctx.ArgInt32(0);
                return attached;
            case "fclose": case "_fclose_nolock": case "_pclose":
                return CloseStream(ctx, a[0], name == "_pclose");
            case "_fcloseall": case "_rmtmp":
                var count = 0;
                foreach (var stream in (name == "_rmtmp" ? _temporary.Keys : _streams.Keys).ToArray())
                    if (!_standard.Contains(stream) && CloseStream(ctx, stream, false) == 0) count++;
                return (ulong)count;
            case "fflush": case "_fflush_nolock": case "_flushall":
                if (name == "_flushall" || a[0] == 0)
                {
                    var result = 0;
                    foreach (var stream in _streams.Keys)
                        if ((int)Native.Call("fflush", [stream]) != 0) result = -1;
                    return name == "_flushall" ? (ulong)StreamCount : unchecked((uint)result);
                }
                break;
            case "_fileno":
                return _streams.TryGetValue(a[0], out var fd) ? (ulong)fd : Error(ctx, name, 9);
            case "_close":
                return CloseDescriptor(ctx, (int)a[0]);
            case "_dup":
                if (!TryDescriptor(ctx, (int)a[0], out var source)) return Error(ctx, name, 9);
                var copy = (int)Native.Call("_dup", [(ulong)source.Host]);
                if (copy < 0) return uint.MaxValue;
                var duplicated = AddDescriptor(copy, source.Flags, source.Path);
                _descriptors[duplicated] = source with { Host = copy };
                return (ulong)duplicated;
            case "_dup2":
                if (!TryDescriptor(ctx, (int)a[0], out source) || (int)a[1] is < 0 or >= 8192)
                    return Error(ctx, name, 9);
                if (a[0] == a[1]) return 0;
                if (TryDescriptor(ctx, (int)a[1], out var target))
                {
                    var result = Native.Call("_dup2", [(ulong)source.Host, (ulong)target.Host]);
                    if ((int)result == 0)
                    {
                        CloseOsHandle((int)a[1]);
                        _descriptors[(int)a[1]] = source with { Host = target.Host };
                    }
                    return result;
                }
                copy = (int)Native.Call("_dup", [(ulong)source.Host]);
                if (copy < 0) return uint.MaxValue;
                _descriptors[(int)a[1]] = source with { Host = copy };
                return 0;
            case "_get_osfhandle":
                return GetOsHandle(ctx, (int)a[0]);
            case "_open_osfhandle":
                if (!ctx.TryGetObject(a[0], out var obj) || obj is null || obj.GetNativeHandle() == 0)
                    return Error(ctx, name, 9);
                var handle = Duplicate(obj.GetNativeHandle());
                copy = (int)Native.Call(name, [(ulong)handle, a[1]]);
                if (copy >= 0)
                {
                    var imported = AddDescriptor(copy, (int)a[1], ctx.ResolveHandlePath(a[0]));
                    _osHandles[imported] = new(a[0], obj);
                    return (ulong)imported;
                }
                new SafeFileHandle(handle, true).Dispose();
                return uint.MaxValue;
            case "_pipe":
                return CreatePipe(ctx);
            case "_popen": case "_wpopen":
                return OpenProcess(ctx, name);
            case "_getmaxstdio": return (ulong)_maxStdio;
            case "_setmaxstdio":
                if ((int)a[0] is < 3 or > 8192) return Error(ctx, name, 22);
                if (a[0] > Native.Call("_getmaxstdio", []) && (int)Native.Call(name, a) < 0) return uint.MaxValue;
                _maxStdio = (int)a[0];
                return a[0];
            case "_get_printf_count_output": return (ulong)_printfCount;
            case "_set_printf_count_output":
                return (ulong)Interlocked.Exchange(ref _printfCount, a[0] == 0 ? 0 : 1);
            case "_kbhit":
                if (ctx.TryGetFile(0, out var console) && console is not null && console.InputAvailable) return 1;
                if (!TryDescriptor(ctx, 0, out descriptor)) return 0;
                var input = (nint)Native.Call("_get_osfhandle", [(ulong)descriptor.Host]);
                return Native.PeekNamedPipe(input, 0, 0, 0, out var available, 0) && available != 0 ? 1UL : 0;
            case "_isatty":
                if (!TryDescriptor(ctx, (int)a[0], out descriptor)) return Error(ctx, name, 9);
                return descriptor.Standard >= 0 ? 1UL : Native.Call(name, [(ulong)descriptor.Host]);
            case "_getcwd": case "_getdcwd":
                return CurrentDirectory(ctx, name);
            case "tmpfile": case "tmpfile_s": case "tmpnam": case "_wtmpnam":
            case "tmpnam_s": case "_wtmpnam_s": case "_tempnam": case "_wtempnam":
            case "_mktemp": case "_wmktemp": case "_mktemp_s": case "_wmktemp_s":
                return Temporary(ctx, name);
            case "getchar": case "_fgetchar":
                a[0] = StandardStream(ctx, 0); name = "fgetc"; break;
            case "getwchar": case "_fgetwchar":
                a[0] = StandardStream(ctx, 0); name = "fgetwc"; break;
            case "putchar": case "_fputchar":
                a[1] = StandardStream(ctx, 1); name = "fputc"; break;
            case "putwchar": case "_fputwchar":
                a[1] = StandardStream(ctx, 1); name = "fputwc"; break;
            case "puts": case "_putws":
                var output = StandardStream(ctx, 1);
                if (output == 0) return Error(ctx, name, 9);
                if ((int)Native.Call(name == "puts" ? "fputs" : "fputws", [a[0], output]) < 0)
                    return uint.MaxValue;
                return Native.Call(name == "puts" ? "fputc" : "fputwc", [10, output]);
            case "gets": case "gets_s": case "_getws": case "_getws_s":
                return GetLine(ctx, name);
        }

        if (IsDescriptorCall(name))
        {
            var fd = (int)a[0];
            if (!TryDescriptor(ctx, fd, out var descriptor)) return Error(ctx, name, 9);
            using var input = name == "_read" ? BeginInput(ctx, fd) : null;
            a[0] = (ulong)descriptor.Host;
            return Native.Call(name, a);
        }

        var streamArg = StreamArgument(name);
        var inputFd = -1;
        if (streamArg >= 0)
        {
            if (!_streams.TryGetValue(a[streamArg], out var fd)) return Error(ctx, name, 9);
            if (IsInput(name)) inputFd = fd;
        }
        using var inputLease = BeginInput(ctx, inputFd);
        if (name.Contains("printf", StringComparison.Ordinal))
        {
            lock (PrintGate)
            {
                var old = Native.Call("_set_printf_count_output", [(ulong)_printfCount]);
                try { return Native.Call(name, a); }
                finally { Native.Call("_set_printf_count_output", [old]); }
            }
        }
        return Native.Call(name, a);
    }

    private static bool IsDescriptorCall(string name) => name is "_chsize" or "_chsize_s" or "_commit" or
        "_eof" or "_filelength" or "_filelengthi64" or "_locking" or "_lseek" or "_lseeki64" or
        "_read" or "_setmode" or "_tell" or "_telli64" or "_write";

    private static bool IsInput(string name) => name.Contains("scanf", StringComparison.Ordinal) ||
        name is "fgetc" or "_fgetc_nolock" or "getc" or "_getc_nolock" or "fgetwc" or "_fgetwc_nolock" or
        "getwc" or "_getwc_nolock" or "fgets" or "fgetws" or "fread" or "fread_s" or "_fread_nolock" or
        "_fread_nolock_s" or "_getw";

    private static int StreamArgument(string name)
    {
        if (name.StartsWith("__stdio_common_vf", StringComparison.Ordinal)) return 1;
        return name switch
        {
            "fputs" or "fputws" or "fputc" or "_fputc_nolock" or "putc" or "_putc_nolock" or
            "fputwc" or "_fputwc_nolock" or "putwc" or "_putwc_nolock" or "ungetc" or "_ungetc_nolock" or
            "ungetwc" or "_ungetwc_nolock" or "_putw" => 1,
            "fgets" or "fgetws" => 2,
            "fread" or "_fread_nolock" or "fwrite" or "_fwrite_nolock" => 3,
            "fread_s" or "_fread_nolock_s" => 4,
            "clearerr" or "clearerr_s" or "feof" or "ferror" or "fflush" or "_fflush_nolock" or
            "fgetc" or "_fgetc_nolock" or "getc" or "_getc_nolock" or "fgetwc" or "_fgetwc_nolock" or
            "getwc" or "_getwc_nolock" or "fgetpos" or "fsetpos" or "fseek" or "_fseek_nolock" or
            "_fseeki64" or "_fseeki64_nolock" or "ftell" or "_ftell_nolock" or "_ftelli64" or
            "_ftelli64_nolock" or "rewind" or "setbuf" or "setvbuf" or "_getw" or
            "_get_stream_buffer_pointers" => 0,
            _ => -1
        };
    }

    private bool TryDescriptor(ApiCallContext ctx, int fd, out Descriptor descriptor)
    {
        if (fd is >= 0 and < 3 && !_standardInitialized[fd]) StandardStream(ctx, fd);
        return _descriptors.TryGetValue(fd, out descriptor!);
    }

    private int AddDescriptor(int host, int flags, string? path)
    {
        lock (_gate)
        {
            var fd = 3;
            while (!_descriptors.TryAdd(fd, new(host, flags, path))) fd++;
            return fd;
        }
    }

    private static unsafe ulong OpenDescriptorStream(int fd, string mode)
    {
        fixed (char* p = mode) return Native.Call("_wfdopen", [(ulong)fd, (ulong)p]);
    }

    private ulong CloseDescriptor(ApiCallContext ctx, int fd)
    {
        if (!TryDescriptor(ctx, fd, out var descriptor))
            return Error(ctx, "_close", 9);
        foreach (var stream in _streams)
            if (stream.Value == fd) return CloseStream(ctx, stream.Key, false);
        if (!_descriptors.TryRemove(fd, out _)) return Error(ctx, "_close", 9);
        CloseOsHandle(fd);
        return Native.Call("_close", [(ulong)descriptor.Host]);
    }

    private ulong CloseStream(ApiCallContext ctx, ulong stream, bool wait)
    {
        if (wait && !_children.ContainsKey(stream)) return Error(ctx, "_pclose", 22);
        if (!_streams.TryRemove(stream, out var fd)) return Error(ctx, "fclose", 9);
        var result = Native.Call("fclose", [stream]);
        _descriptors.TryRemove(fd, out _);
        CloseOsHandle(fd);
        _temporary.TryRemove(stream, out _);
        if (_children.TryRemove(stream, out var child))
        {
            if (wait)
            {
                child.WaitForExit(Timeout.Infinite);
                result = unchecked((uint)(child.ExitCode ?? -1));
            }
        }
        return result;
    }

    private ulong GetOsHandle(ApiCallContext ctx, int fd)
    {
        if (!TryDescriptor(ctx, fd, out var descriptor)) return Error(ctx, "_get_osfhandle", 9);
        if (descriptor.Standard == 0) EnsureDirectInputPump();
        if (_osHandles.TryGetValue(fd, out var cached) && ctx.TryGetObject(cached.Value, out var obj) &&
            ReferenceEquals(cached.Object, obj)) return cached.Value;
        var native = (nint)Native.Call("_get_osfhandle", [(ulong)descriptor.Host]);
        using var file = new StdioFileObject(new SafeFileHandle(Duplicate(native), true), descriptor.Flags, descriptor.Path);
        var handle = ctx.Handles.Create(file);
        _osHandles[fd] = new(handle, file);
        return handle;
    }

    private void CloseOsHandle(int fd)
    {
        if (_osHandles.TryRemove(fd, out var cached) && _process.Handles.TryGet(cached.Value, out var obj) &&
            ReferenceEquals(cached.Object, obj)) _process.Handles.Close(cached.Value);
    }

    private static nint Duplicate(nint handle)
    {
        if (!Native.DuplicateHandle(-1, handle, -1, out var copy, 0, false, 2))
            throw new IOException($"Cannot duplicate CRT file handle: {Marshal.GetLastWin32Error()}");
        return copy;
    }

    private static ulong Error(ApiCallContext ctx, string name, int errno)
    {
        CrtLocaleData.SetErrno(errno);
        if ((name.EndsWith("_s", StringComparison.Ordinal) || name.EndsWith("_dispatch", StringComparison.Ordinal) ||
            name == "_get_stream_buffer_pointers") && name is not "gets_s" and not "_getws_s" and
            not "fread_s" and not "_fread_nolock_s" && !name.StartsWith("__stdio_common_", StringComparison.Ordinal))
            return (ulong)errno;
        return name is "fopen" or "_wfopen" or "_fsopen" or "_wfsopen" or "freopen" or "_wfreopen" or
            "_wfdopen" or "_popen" or "_wpopen" or "tmpfile" or "tmpnam" or "_wtmpnam" or "_tempnam" or
            "_wtempnam" or "_mktemp" or "_wmktemp" or "_getcwd" or "_getdcwd" or "fgets" or "fgetws" or
            "gets" or "gets_s" or "_getws" or "_getws_s" or "fread" or "fread_s" or "_fread_nolock" or
            "_fread_nolock_s" or "fwrite" or "_fwrite_nolock" or "_isatty" or "feof" or "ferror" ? 0 :
            name is "_get_osfhandle" or "_lseeki64" or "_telli64" or "_filelengthi64" or "_ftelli64" or
            "_ftelli64_nolock" ? ulong.MaxValue : uint.MaxValue;
    }

    public unsafe void Dispose()
    {
        if (Interlocked.Exchange(ref _disposing, 1) != 0) return;
        var previous = Native.SetInvalidHandler((nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, uint, nuint, void>)&InvalidParameter);
        try { DisposeResources(); }
        finally { Native.SetInvalidHandler(previous); }
    }

    private void DisposeResources()
    {
        // Closing the writers lets output pumps drain before the guest's handles disappear.
        foreach (var stream in _streams.Keys)
            if (_streams.TryRemove(stream, out var fd))
            {
                Native.Call("fclose", [stream]);
                _descriptors.TryRemove(fd, out _);
            }
        foreach (var descriptor in _descriptors.Values) Native.Call("_close", [(ulong)descriptor.Host]);
        _descriptors.Clear();
        foreach (var fd in _osHandles.Keys) CloseOsHandle(fd);
        lock (_gate)
        {
            foreach (var item in _pipes)
                if (!item.Pipe.CanWrite && (item.Worker.ThreadState & System.Threading.ThreadState.Unstarted) == 0)
                    item.Worker.Join(1000);
            _closed = true;
            lock (_inputGate) _inputCancellation?.Cancel();
            _inputSignal.Release();
            foreach (var item in _pipes) item.Pipe.Dispose();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void InvalidParameter(nint expression, nint function, nint file, uint line, nuint reserved) =>
        Native.InvalidParameterSeen = true;

    private static class Native
    {
        private static readonly nint Library = NativeLibrary.Load("ucrtbase.dll");
        private static readonly ConcurrentDictionary<string, nint> Exports = new(StringComparer.Ordinal);
        [ThreadStatic] internal static bool InvalidParameterSeen;

        internal static unsafe ulong Call(string name, ReadOnlySpan<ulong> args)
        {
            Span<ulong> a = stackalloc ulong[8];
            a.Clear();
            args.CopyTo(a);
            var p = Exports.GetOrAdd(name, static n => NativeLibrary.GetExport(Library, n));
            // Every stdio contract here has at most eight integer/pointer arguments on Windows x64.
            return ((delegate* unmanaged[Cdecl]<ulong, ulong, ulong, ulong, ulong, ulong, ulong, ulong, ulong>)p)
                (a[0], a[1], a[2], a[3], a[4], a[5], a[6], a[7]);
        }

        [DllImport("ucrtbase.dll", EntryPoint = "_set_thread_local_invalid_parameter_handler", CallingConvention = CallingConvention.Cdecl)]
        internal static extern nint SetInvalidHandler(nint handler);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DuplicateHandle(nint process, nint handle, nint target, out nint copy,
            uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PeekNamedPipe(nint pipe, nint buffer, uint size, nint read, out uint available, nint left);
    }
}
