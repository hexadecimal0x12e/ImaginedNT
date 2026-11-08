using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using ImaginedNT.Core.Memory;
using ImaginedNT.Core.Nt;

namespace ImaginedNT.Core.Runtime;

public static class EnvironmentApis
{
    private static readonly ConditionalWeakTable<ImaginedProcess, ConcurrentDictionary<ulong, bool>> Blocks = new();
    private static readonly Lazy<Encoding> Ansi = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding((int)GetACP());
    });

    [DllImport("kernel32.dll")]
    private static extern uint GetACP();

    private static bool Unicode(ApiCallContext ctx) => !ctx.Name.EndsWith('A') && ctx.Name != "GetEnvironmentStrings";
    private static Encoding TextEncoding(ApiCallContext ctx) => Unicode(ctx) ? Encoding.Unicode : Ansi.Value;

    private static string ReadString(ApiCallContext ctx, ulong address)
    {
        if (Unicode(ctx)) return ctx.ReadUnicodeString(address);
        var bytes = new List<byte>();
        for (var i = 0; i < 32767; i++)
        {
            var b = ctx.Read<byte>(address + (ulong)i);
            if (b == 0) break;
            bytes.Add(b);
        }
        return Ansi.Value.GetString(bytes.ToArray());
    }

    private static ulong Fail(ApiCallContext ctx, int error)
    {
        ctx.SetLastError(error);
        return 0;
    }

    [VirtualApi("kernel32.dll", "GetEnvironmentVariableW", ApiCategory.Process)]
    [VirtualApi("kernel32.dll", "GetEnvironmentVariableA", ApiCategory.Process)]
    private static ulong GetEnvironmentVariable(ApiCallContext ctx)
    {
        if (ctx.Arg(0) == 0 || (ctx.Arg(1) == 0 && ctx.Arg32(2) != 0)) return Fail(ctx, Win32Error.InvalidParameter);
        var name = ReadString(ctx, ctx.Arg(0));
        if (!ctx.Process.TryGetEnvironmentVariable(name, out var value)) return Fail(ctx, 203);
        var bytes = TextEncoding(ctx).GetBytes(value + '\0');
        var required = (ulong)bytes.Length / (Unicode(ctx) ? 2UL : 1UL);
        if (ctx.Arg32(2) < required) return required;
        ctx.WriteBytes(ctx.Arg(1), bytes);
        ctx.SetLastError(0);
        return required - 1;
    }

    [VirtualApi("kernel32.dll", "SetEnvironmentVariableW", ApiCategory.Process)]
    [VirtualApi("kernel32.dll", "SetEnvironmentVariableA", ApiCategory.Process)]
    private static ulong SetEnvironmentVariable(ApiCallContext ctx)
    {
        if (ctx.Arg(0) == 0) return Fail(ctx, Win32Error.InvalidParameter);
        var name = ReadString(ctx, ctx.Arg(0));
        var value = ctx.Arg(1) == 0 ? null : ReadString(ctx, ctx.Arg(1));
        try { ctx.Process.SetEnvironmentVariable(name, value); }
        catch (ArgumentException) { return Fail(ctx, Win32Error.InvalidParameter); }
        return 1;
    }

    [VirtualApi("kernel32.dll", "GetEnvironmentStringsW", ApiCategory.Process)]
    [VirtualApi("kernel32.dll", "GetEnvironmentStringsA", ApiCategory.Process)]
    [VirtualApi("kernel32.dll", "GetEnvironmentStrings", ApiCategory.Process)]
    private static ulong GetEnvironmentStrings(ApiCallContext ctx)
    {
        var values = ctx.Process.GetEnvironmentVariables();
        var text = string.Join('\0', values.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .Select(p => p.Key + "=" + p.Value)) + "\0\0";
        var bytes = TextEncoding(ctx).GetBytes(text);
        var status = ctx.Memory.TryAllocate(0, (ulong)bytes.Length, MemoryProtection.ReadWrite,
            AllocationType.Reserve | AllocationType.Commit, out var address, out _, "environment block");
        if (status != NtStatus.Success) return Fail(ctx, Win32Error.NotEnoughMemory);
        if (!ctx.Memory.TryWrite(address, bytes))
        {
            ctx.Memory.TryFree(address, 0, FreeType.Release);
            ctx.Runtime.Trace.Warn($"{ctx.Name}: could not write virtual environment block", ctx.Process.Id, ctx.Thread.Id);
            return Fail(ctx, 998); // ERROR_NOACCESS
        }
        Blocks.GetValue(ctx.Process, _ => new())[address] = Unicode(ctx);
        return address;
    }

    [VirtualApi("kernel32.dll", "FreeEnvironmentStringsW", ApiCategory.Process)]
    [VirtualApi("kernel32.dll", "FreeEnvironmentStringsA", ApiCategory.Process)]
    private static ulong FreeEnvironmentStrings(ApiCallContext ctx)
    {
        var blocks = Blocks.GetValue(ctx.Process, _ => new());
        if (!blocks.TryGetValue(ctx.Arg(0), out var unicode) || unicode != Unicode(ctx) ||
            !blocks.TryRemove(ctx.Arg(0), out _)) return Fail(ctx, Win32Error.InvalidParameter);
        var status = ctx.Memory.TryFree(ctx.Arg(0), 0, FreeType.Release);
        if (status == NtStatus.Success) return 1;
        blocks[ctx.Arg(0)] = unicode;
        return Fail(ctx, Win32Error.InvalidParameter);
    }

    [VirtualApi("kernel32.dll", "ExpandEnvironmentStringsW", ApiCategory.Process)]
    [VirtualApi("kernel32.dll", "ExpandEnvironmentStringsA", ApiCategory.Process)]
    private static ulong ExpandEnvironmentStrings(ApiCallContext ctx)
    {
        if (ctx.Arg(0) == 0 || (ctx.Arg(1) == 0 && ctx.Arg32(2) != 0)) return Fail(ctx, Win32Error.InvalidParameter);
        var bytes = TextEncoding(ctx).GetBytes(ctx.Process.ExpandEnvironmentVariables(ReadString(ctx, ctx.Arg(0))) + '\0');
        var required = (ulong)bytes.Length / (Unicode(ctx) ? 2UL : 1UL);
        if (ctx.Arg32(2) >= required) ctx.WriteBytes(ctx.Arg(1), bytes);
        return required;
    }

    [VirtualApi("kernel32.dll", "SetEnvironmentStringsW", ApiCategory.Process)]
    [VirtualApi("kernel32.dll", "SetEnvironmentStringsA", ApiCategory.Process)]
    private static ulong SetEnvironmentStrings(ApiCallContext ctx)
    {
        if (ctx.Arg(0) == 0) return Fail(ctx, Win32Error.InvalidParameter);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var address = ctx.Arg(0);
        for (var i = 0; i < 32767; i++)
        {
            var entry = ReadString(ctx, address);
            if (entry.Length == 0)
            {
                ctx.Process.ReplaceEnvironment(values);
                return 1;
            }
            var equals = entry.IndexOf('=', entry.StartsWith('=') ? 1 : 0);
            if (equals <= 0) return Fail(ctx, Win32Error.InvalidParameter);
            values[entry[..equals]] = entry[(equals + 1)..];
            address += (ulong)TextEncoding(ctx).GetByteCount(entry + '\0');
        }
        return Fail(ctx, Win32Error.InvalidParameter);
    }
}
