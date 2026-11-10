using System.ComponentModel;
using ImaginedNT.Core.Io;
using ImaginedNT.Core.Nt;
using static ImaginedNT.Apis.ApiHelpers;

namespace ImaginedNT.Core.Runtime;

public static partial class FileApis
{
    [VirtualApi("kernel32.dll", "CreatePipe", ApiCategory.File)]
    private static ulong CreatePipe(ApiCallContext ctx)
    {
        if (ctx.Arg(0) == 0 || ctx.Arg(1) == 0)
            return BoolResult(ctx, false, Win32Error.InvalidParameter);
        ctx.Write(ctx.Arg(0), 0UL);
        ctx.Write(ctx.Arg(1), 0UL);
        var inherit = false;
        if (ctx.Arg(2) != 0)
        {
            if (ctx.Read<uint>(ctx.Arg(2)) < 24)
                return BoolResult(ctx, false, Win32Error.InvalidParameter);
            inherit = ctx.Read<int>(ctx.Arg(2) + 16) != 0;
        }
        try
        {
            PipeFileObject.Create(ctx.Arg32(3), out var read, out var write);
            using (read)
            using (write)
            {
                var r = ctx.Handles.Create(read, 0x8010_0000, inheritable: inherit);
                ulong w = 0;
                var success = false;
                try
                {
                    w = ctx.Handles.Create(write, 0x4010_0000, inheritable: inherit);
                    ctx.Write(ctx.Arg(0), r);
                    ctx.Write(ctx.Arg(1), w);
                    success = true;
                    return 1;
                }
                finally
                {
                    if (!success)
                    {
                        ctx.Handles.Close(r);
                        if (w != 0) ctx.Handles.Close(w);
                    }
                }
            }
        }
        catch (Win32Exception ex)
        {
            ctx.Runtime.Trace.Warn($"CreatePipe failed: {ex}", ctx.Process.Id, ctx.Thread.Id);
            return BoolResult(ctx, false, ex.NativeErrorCode);
        }
    }

    [VirtualApi("kernel32.dll", "PeekNamedPipe", ApiCategory.File)]
    private static ulong PeekNamedPipe(ApiCallContext ctx)
    {
        if (ctx.Arg(3) != 0) ctx.Write(ctx.Arg(3), 0u);
        if (ctx.Arg(4) != 0) ctx.Write(ctx.Arg(4), 0u);
        if (ctx.Arg(5) != 0) ctx.Write(ctx.Arg(5), 0u);
        if (!ctx.TryGetFile(ctx.Arg(0), out var file) || file is not PipeFileObject pipe || !pipe.CanRead)
            return BoolResult(ctx, false, Win32Error.InvalidHandle);
        var length = ctx.Arg(1) == 0 ? 0u : ctx.Arg32(2);
        if (length > int.MaxValue)
            return BoolResult(ctx, false, Win32Error.InvalidParameter);
        try
        {
            var available = pipe.Peek(Span<byte>.Empty, out var read, out var left);
            if (length != 0 && available != 0)
            {
                var buffer = new byte[(int)Math.Min(length, available)];
                available = pipe.Peek(buffer, out read, out left);
                if (read != 0) ctx.Memory.WriteBytes(ctx.Arg(1), buffer.AsSpan(0, (int)read));
            }
            if (ctx.Arg(3) != 0) ctx.Write(ctx.Arg(3), read);
            if (ctx.Arg(4) != 0) ctx.Write(ctx.Arg(4), available);
            if (ctx.Arg(5) != 0) ctx.Write(ctx.Arg(5), left);
            return 1;
        }
        catch (Win32Exception ex)
        {
            if (ex.NativeErrorCode != Win32Error.BrokenPipe)
                ctx.Runtime.Trace.Warn($"PeekNamedPipe failed: {ex}", ctx.Process.Id, ctx.Thread.Id);
            return BoolResult(ctx, false, ex.NativeErrorCode);
        }
    }
}
