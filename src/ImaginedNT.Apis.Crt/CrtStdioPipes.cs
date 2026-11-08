using ImaginedNT.Core.Runtime;
using Microsoft.Win32.SafeHandles;

namespace ImaginedNT.Apis.Crt;

internal sealed partial class CrtStdioState
{
    private unsafe ulong CreatePipe(ApiCallContext ctx)
    {
        if (!CrtMemory.Write(ctx, ctx.Arg(0), -1) || !CrtMemory.Write(ctx, ctx.Arg(0) + 4, -1))
            return Error(ctx, "_pipe", 22);
        var fds = stackalloc int[2];
        if ((int)Native.Call("_pipe", [(ulong)fds, ctx.Arg(1), ctx.Arg(2)]) != 0) return uint.MaxValue;
        var read = AddDescriptor(fds[0], (int)ctx.Arg(2), null);
        var write = AddDescriptor(fds[1], (int)ctx.Arg(2) | 1, null);
        if (CrtMemory.Write(ctx, ctx.Arg(0), read) && CrtMemory.Write(ctx, ctx.Arg(0) + 4, write)) return 0;
        CloseDescriptor(ctx, read);
        CloseDescriptor(ctx, write);
        return Error(ctx, "_pipe", 22);
    }

    private unsafe ulong OpenProcess(ApiCallContext ctx, string name)
    {
        var width = name == "_wpopen" ? 2 : 1;
        if (!CrtMemory.ReadText(ctx, ctx.Arg(0), width, out var command, ansi: true) ||
            !CrtMemory.ReadText(ctx, ctx.Arg(1), width, out var mode, ansi: true) ||
            mode.Length is < 1 or > 2 || mode[0] is not ('r' or 'w') ||
            mode.Length == 2 && mode[1] is not ('b' or 't')) return Error(ctx, name, 22);
        if (StreamCount >= _maxStdio) return Error(ctx, name, 24);
        var fds = stackalloc int[2];
        var flags = mode.Contains('b') ? 0x8000 : 0x4000;
        if ((int)Native.Call("_pipe", [(ulong)fds, 4096, (ulong)flags]) != 0) return 0;
        var reading = mode[0] == 'r';
        var parentFd = reading ? fds[0] : fds[1];
        var childFd = reading ? fds[1] : fds[0];
        var stream = OpenDescriptorStream(parentFd, mode);
        if (stream == 0)
        {
            Native.Call("_close", [(ulong)parentFd]);
            Native.Call("_close", [(ulong)childFd]);
            return 0;
        }
        var fd = AddDescriptor(parentFd, reading ? 0 : 1, null);
        _streams[stream] = fd;
        ImaginedProcess? child = null;
        try
        {
            var native = Duplicate((nint)Native.Call("_get_osfhandle", [(ulong)childFd]));
            using var file = new StdioFileObject(new SafeFileHandle(native, true), reading ? 1 : 0, null);
            var pipeHandle = ctx.Handles.Create(file);
            try
            {
                var shell = ctx.Process.TryGetEnvironmentVariable("COMSPEC", out var comspec)
                    ? comspec : "C:\\Windows\\System32\\cmd.exe";
                var image = ctx.Process.FileSystem.MapPath(shell, ctx.Process.CurrentDirectory);
                if (!File.Exists(image)) image = Path.Combine(Environment.SystemDirectory, "cmd.exe");
                child = ctx.Runtime.CreateProcess(new ProcessCreateOptions
                {
                    ImagePath = image,
                    CommandLine = $"\"{shell}\" /d /s /c \"{command}\"",
                    CurrentDirectory = ctx.Process.CurrentDirectory,
                    SandboxRoot = ctx.Process.FileSystem.Root,
                    ParentId = ctx.Process.Id,
                    IsRoot = false,
                    Environment = new Dictionary<string, string>(ctx.Process.EnvironmentVariables)
                });
                child.LoadImage(image);
                child.ApplyStartupHandles(ctx.Process, false, true, reading ? 0 : pipeHandle,
                    reading ? pipeHandle : 1, 2);
                var process = child;
                _children[stream] = process;
                new Thread(() =>
                {
                    try { process.Run(); }
                    catch (Exception ex)
                    {
                        ctx.Runtime.Trace.Warn($"CRT popen child {process.Id}: {ex}", ctx.Process.Id, ctx.Thread.Id);
                    }
                    finally { process.Dispose(); }
                }) { IsBackground = true, Name = $"CRT popen {process.Id}" }.Start();
            }
            finally { ctx.Handles.Close(pipeHandle); }
            return stream;
        }
        catch
        {
            _children.TryRemove(stream, out _);
            CloseStream(ctx, stream, false);
            child?.Dispose();
            throw;
        }
        finally { Native.Call("_close", [(ulong)childFd]); }
    }
}
