using System.Text;
using ImaginedNT.Core.Io;
using ImaginedNT.Core.Memory;
using ImaginedNT.Core.Nt;
using ImaginedNT.Core.Runtime;

namespace ImaginedNT.Apis.Crt;

internal sealed partial class CrtProcessState
{
    private ulong _arguments;
    private readonly ulong[] _initialEnvironments = new ulong[2];
    private ulong _environmentPointers;
    private readonly ulong[] _currentEnvironments = new ulong[2];
    private readonly string?[] _environmentContents = new string?[2];

    internal static ulong GetEnvironmentPointer(ApiCallContext ctx, int width)
    {
        var state = States.GetValue(ctx.Process, static _ => new CrtProcessState());
        lock (state._gate)
        {
            if (state._environmentPointers == 0)
            {
                var status = ctx.Memory.TryAllocate(0, 16, MemoryProtection.ReadWrite,
                    AllocationType.Reserve | AllocationType.Commit, out state._environmentPointers, out _, "CRT environment pointers");
                if (status != NtStatus.Success) return 0;
            }
            var values = ctx.Process.GetEnvironmentVariables().OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .Select(p => p.Key + "=" + p.Value).ToArray();
            var content = string.Join('\0', values);
            var i = width - 1;
            var pointer = state._environmentPointers + (ulong)i * 8;
            if (state._environmentContents[i] == content) return pointer;
            var address = WriteStringArray(ctx, values, width);
            if (address == 0) return 0;
            if (!CrtMemory.Write(ctx, pointer, address))
            {
                ctx.Memory.TryFree(address, 0, FreeType.Release);
                return 0;
            }
            if (state._currentEnvironments[i] != 0) ctx.Memory.TryFree(state._currentEnvironments[i], 0, FreeType.Release);
            state._currentEnvironments[i] = address;
            state._environmentContents[i] = content;
            return pointer;
        }
    }

    internal static void RefreshEnvironment(ApiCallContext ctx)
    {
        if (!States.TryGetValue(ctx.Process, out var state)) return;
        lock (state._gate)
            for (var i = 0; i < 2; i++)
                if (state._currentEnvironments[i] != 0 && GetEnvironmentPointer(ctx, i + 1) == 0)
                    ctx.Runtime.Trace.Warn($"{ctx.Name}: could not refresh CRT environment", ctx.Process.Id, ctx.Thread.Id);
    }

    internal static ulong GetArgumentPointer(ApiCallContext ctx, ulong offset)
    {
        var state = States.GetValue(ctx.Process, static _ => new CrtProcessState());
        lock (state._gate)
        {
            if (state._arguments == 0)
            {
                var status = ctx.Memory.TryAllocate(0, 24, MemoryProtection.ReadWrite,
                    AllocationType.Reserve | AllocationType.Commit, out state._arguments, out _, "CRT argc/argv");
                if (status != NtStatus.Success)
                {
                    ctx.Runtime.Trace.Warn($"{ctx.Name}: cannot allocate argc/argv: {NtStatus.NameOf(status)}", ctx.Process.Id, ctx.Thread.Id);
                    return 0;
                }
            }
            return state._arguments + offset;
        }
    }

    internal static ulong ConfigureArguments(ApiCallContext ctx, int width)
    {
        var mode = ctx.ArgInt32(0);
        if (mode == 0) return 0;
        if (mode is not (1 or 2))
        {
            ctx.Runtime.Trace.Warn($"{ctx.Name}: invalid argv mode {mode}", ctx.Process.Id, ctx.Thread.Id);
            return CrtMemory.EInvalid;
        }
        var arguments = ParseArguments(string.IsNullOrEmpty(ctx.Process.CommandLine) ? ctx.Process.ImagePath : ctx.Process.CommandLine);
        if (mode == 2) arguments = ExpandArguments(ctx, arguments);
        var state = States.GetValue(ctx.Process, static _ => new CrtProcessState());
        lock (state._gate)
        {
            var header = GetArgumentPointer(ctx, 0);
            if (header == 0) return CrtMemory.ENoMemory;
            var argv = WriteStringArray(ctx, arguments, width);
            if (argv == 0) return CrtMemory.ENoMemory;
            if (CrtMemory.Write(ctx, header + (width == 1 ? 8UL : 16UL), argv) &&
                CrtMemory.Write(ctx, header, arguments.Count)) return 0;
            ctx.Runtime.Trace.Warn($"{ctx.Name}: cannot update argc/argv at 0x{header:X}", ctx.Process.Id, ctx.Thread.Id);
            return CrtMemory.EInvalid;
        }
    }

    internal static ulong GetInitialEnvironment(ApiCallContext ctx, int width)
    {
        var state = States.GetValue(ctx.Process, static _ => new CrtProcessState());
        lock (state._gate)
        {
            if (state._initialEnvironments[width - 1] == 0)
                state._initialEnvironments[width - 1] = WriteStringArray(ctx,
                    ctx.Process.EnvironmentVariables.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                        .Select(p => p.Key + "=" + p.Value).ToArray(), width);
            return state._initialEnvironments[width - 1];
        }
    }

    private static ulong WriteStringArray(ApiCallContext ctx, IReadOnlyList<string> values, int width)
    {
        var strings = values.Select(v => CrtMemory.Encode(v, width, ansi: true)).ToArray();
        var offset = ((ulong)strings.Length + 1) * 8;
        var status = ctx.Memory.TryAllocate(0, offset + (ulong)strings.Sum(s => (long)s.Length), MemoryProtection.ReadWrite,
            AllocationType.Reserve | AllocationType.Commit, out var address, out _, "CRT string array");
        if (status != NtStatus.Success)
        {
            ctx.Runtime.Trace.Warn($"{ctx.Name}: cannot allocate string array: {NtStatus.NameOf(status)}", ctx.Process.Id, ctx.Thread.Id);
            return 0;
        }
        for (var i = 0; i < strings.Length; i++)
        {
            if (!CrtMemory.Write(ctx, address + (ulong)i * 8, address + offset) ||
                !ctx.Memory.TryWrite(address + offset, strings[i]))
            {
                ctx.Memory.TryFree(address, 0, FreeType.Release);
                ctx.Runtime.Trace.Warn($"{ctx.Name}: cannot write string array at 0x{address:X}", ctx.Process.Id, ctx.Thread.Id);
                return 0;
            }
            offset += (ulong)strings[i].Length;
        }
        return address;
    }

    private static List<string> ParseArguments(string text)
    {
        var end = text.IndexOf('\0');
        if (end >= 0) text = text[..end];
        var result = new List<string>();
        var value = new StringBuilder();
        var i = 0;
        var quoted = false;
        // argv[0] treats quotes specially and does not interpret backslash escapes.
        while (i < text.Length && (quoted || text[i] is not (' ' or '\t')))
        {
            if (text[i] == '"') quoted = !quoted;
            else value.Append(text[i]);
            i++;
        }
        result.Add(value.ToString());
        while (i < text.Length)
        {
            while (i < text.Length && (text[i] is ' ' or '\t')) i++;
            if (i == text.Length) break;
            value.Clear();
            quoted = false;
            while (i < text.Length)
            {
                var slashes = 0;
                while (i < text.Length && text[i] == '\\')
                {
                    slashes++;
                    i++;
                }
                if (i < text.Length && text[i] == '"')
                {
                    value.Append('\\', slashes / 2);
                    if (slashes % 2 != 0)
                    {
                        value.Append('"');
                        i++;
                    }
                    else if (quoted && i + 1 < text.Length && text[i + 1] == '"')
                    {
                        value.Append('"');
                        i += 2;
                    }
                    else
                    {
                        quoted = !quoted;
                        i++;
                    }
                    continue;
                }
                value.Append('\\', slashes);
                if (i == text.Length || (!quoted && (text[i] is ' ' or '\t'))) break;
                value.Append(text[i++]);
            }
            result.Add(value.ToString());
        }
        return result;
    }

    private static List<string> ExpandArguments(ApiCallContext ctx, List<string> arguments)
    {
        var result = new List<string> { arguments[0] };
        foreach (var argument in arguments.Skip(1))
        {
            if (!argument.Contains('*') && !argument.Contains('?'))
            {
                result.Add(argument);
                continue;
            }
            try
            {
                if (ctx.Process.FileSystem.TryMapPath(argument, ctx.Process.CurrentDirectory, FileOperation.Read, out var path) != NtStatus.Success)
                {
                    result.Add(argument);
                    continue;
                }
                var matches = HostFileSystem.GetFileSystemEntries(Path.GetDirectoryName(path)!, Path.GetFileName(path));
                if (matches.Length != 0)
                {
                    Array.Sort(matches, StringComparer.OrdinalIgnoreCase);
                    var prefix = argument[..(argument.LastIndexOfAny(['\\', '/', ':']) + 1)];
                    result.AddRange(matches.Select(p => prefix + Path.GetFileName(p)));
                    continue;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NtFileException)
            {
                ctx.Runtime.Trace.Warn($"{ctx.Name}: cannot expand argument '{argument}': {ex.Message}", ctx.Process.Id, ctx.Thread.Id);
            }
            result.Add(argument);
        }
        return result;
    }
}
