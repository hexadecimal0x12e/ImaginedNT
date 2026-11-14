using static ImaginedNT.Apis.ApiHelpers;
using System.Text;
using ImaginedNT.Core.Diagnostics;
using ImaginedNT.Core.Security;
using ImaginedNT.Core.Io;
using ImaginedNT.Core.Memory;
using ImaginedNT.Core.Nt;

namespace ImaginedNT.Core.Runtime;

public static class ProcessApis
{
    private const uint StartfUseStdHandles = 0x0000_0100;
    private const uint CreateUnicodeEnvironment = 0x0000_0400;
    private const uint CreateSuspended = 0x0000_0004;
    private const int ProcessInformationSize = 24;
    private const int StartupInfoSize = 104;

    private readonly record struct GuestStartupInfo(bool UseStandardHandles, ulong StandardInput, ulong StandardOutput, ulong StandardError);

    // --- process creation -----------------------------------------------------------

    [VirtualApi("kernel32.dll", "CreateProcessW", ApiCategory.Process)]
    private static ulong CreateProcessW(ApiCallContext ctx) => CreateProcessCore(ctx, unicode: true);

    [VirtualApi("kernel32.dll", "CreateProcessA", ApiCategory.Process)]
    private static ulong CreateProcessA(ApiCallContext ctx) => CreateProcessCore(ctx, unicode: false);

    [VirtualApi("advapi32.dll", "CreateProcessAsUserW", ApiCategory.Security)]
    [VirtualApi("advapi32.dll", "CreateProcessAsUserA", ApiCategory.Security)]
    private static ulong CreateProcessAsUser(ApiCallContext ctx)
    {
        var status = ctx.Handles.TryGet(ctx.Arg(0), 0xB, out var obj);
        if (status != NtStatus.Success) return BoolResult(ctx, false, Win32FromStatus(status));
        if (obj is not TokenObject token) return BoolResult(ctx, false, Win32Error.InvalidHandle);
        if (token.Impersonation) return BoolResult(ctx, false, 1349);
        if (token.Token.User.Sid != ctx.Process.Token.User.Sid && !ctx.Thread.EffectiveToken.IsSystem &&
            !ctx.Thread.EffectiveToken.HasPrivilege("SeAssignPrimaryTokenPrivilege"))
            return BoolResult(ctx, false, Win32Error.PrivilegeNotHeld);
        var args = Enumerable.Range(1, 10).Select(ctx.Arg).ToArray();
        return CreateProcessCore(new ApiCallContext(ctx.Process, ctx.Thread, ctx.Module, ctx.Name, ctx.ApiId,
            ctx.ReturnAddress, args, ctx.ReturnAddressSlot), ctx.Name.EndsWith('W'), token.Token);
    }

    /// <summary>Legacy WinExec: create a child and return an instance handle greater than 31.</summary>
    [VirtualApi("kernel32.dll", "WinExec", ApiCategory.Process)]
    private static ulong WinExec(ApiCallContext ctx)
    {
        if (!ctx.Runtime.Options.AllowProcessCreation)
        {
            ctx.Runtime.Trace.Emit(TraceKind.Security, "WinExec blocked by AllowProcessCreation=false", ctx.Process.Id, ctx.Thread.Id);
            ctx.SetLastError(Win32Error.AccessDenied);
            return 0;
        }

        var commandLine = ctx.Arg(0) == 0 ? null : ctx.ReadAnsiString(ctx.Arg(0));
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            ctx.SetLastError(Win32Error.InvalidParameter);
            return 0;
        }

        ImaginedProcess? child = null;
        try
        {
            var imagePath = ResolveExecutablePath(ctx, application: null, commandLine);
            if (imagePath is null)
            {
                ctx.SetLastError(Win32Error.FileNotFound);
                return 0;
            }

            child = ctx.Runtime.CreateProcess(new ProcessCreateOptions
            {
                ImagePath = imagePath,
                CommandLine = commandLine,
                ParentId = ctx.Process.Id,
                IsRoot = false
            });
            child.LoadImage(imagePath);
            var main = child.EnsureMainThread(suspended: true);
            if (!main.Start())
                throw new InvalidOperationException("the child main thread could not be started");

            ctx.Runtime.Trace.Emit(TraceKind.Process,
                $"WinExec('{commandLine}') created pid {child.Id}", ctx.Process.Id, ctx.Thread.Id);
            return 33; // a legacy HINSTANCE value; anything > 31 means success
        }
        catch (Exception ex)
        {
            if (child is not null)
            {
                ctx.Runtime.RemoveProcess(child);
                try { child.Terminate(-1); } catch { }
                try { child.Dispose(); } catch { }
            }
            ctx.Runtime.Trace.Warn($"WinExec('{commandLine}') failed: {ex.GetType().Name}: {ex.Message}", ctx.Process.Id, ctx.Thread.Id);
            ctx.SetLastError(Win32Error.FileNotFound);
            return 0;
        }
    }

    private static ulong CreateProcessCore(ApiCallContext ctx, bool unicode, AccessToken? token = null)
    {
        var processInformationPtr = ctx.Arg(9);
        if (processInformationPtr == 0)
        {
            ctx.SetLastError(Win32Error.InvalidParameter);
            return 0;
        }

        if (!ctx.Runtime.Options.AllowProcessCreation)
        {
            ctx.Runtime.Trace.Emit(TraceKind.Security, "CreateProcess blocked by AllowProcessCreation=false", ctx.Process.Id, ctx.Thread.Id);
            ctx.SetLastError(Win32Error.AccessDenied);
            return 0;
        }

        ImaginedProcess? child = null;
        ulong processHandle = 0;
        ulong threadHandle = 0;
        try
        {
            // Windows leaves PROCESS_INFORMATION untouched on failure but zeroing it first makes
            // the managed path deterministic for callers that checked the return value.
            ctx.Memory.WriteBytes(processInformationPtr, new byte[ProcessInformationSize]);

            var application = ReadGuestString(ctx, 0, unicode);
            var commandLine = ReadGuestString(ctx, 1, unicode);
            if (string.IsNullOrWhiteSpace(application) && string.IsNullOrWhiteSpace(commandLine))
            {
                ctx.SetLastError(Win32Error.InvalidParameter);
                return 0;
            }

            var inheritHandles = ctx.ArgBool(4);
            var creationFlags = ctx.Arg32(5);
            var environmentPtr = ctx.Arg(6);
            var currentDirectory = ReadGuestString(ctx, 7, unicode);
            var startupInfo = ReadStartupInfo(ctx, ctx.Arg(8));
            var environment = ReadEnvironmentBlock(ctx, environmentPtr,
                unicode && (creationFlags & CreateUnicodeEnvironment) != 0);
            var suspended = (creationFlags & CreateSuspended) != 0;

            string? imagePath;
            var previousToken = ctx.Thread.ImpersonationToken;
            try
            {
                if (token is not null) ctx.Thread.ImpersonationToken = token;
                imagePath = ResolveExecutablePath(ctx, application, commandLine);
            }
            finally { ctx.Thread.ImpersonationToken = previousToken; }
            if (imagePath is null)
            {
                ctx.SetLastError(Win32Error.FileNotFound);
                return 0;
            }

            child = ctx.Runtime.CreateProcess(new ProcessCreateOptions
            {
                ImagePath = imagePath,
                CommandLine = string.IsNullOrWhiteSpace(application)
                    ? commandLine
                    : (string.IsNullOrWhiteSpace(commandLine) ? $"\"{application}\"" : commandLine),
                CurrentDirectory = string.IsNullOrWhiteSpace(currentDirectory) ? null : currentDirectory,
                SandboxRoot = ctx.Process.FileSystem.Root,
                ParentId = ctx.Process.Id,
                IsRoot = false,
                Token = token,
                Desktop = ReadStartupDesktop(ctx, ctx.Arg(8), unicode),
                Environment = environment
            });

            child.LoadImage(imagePath);
            child.ApplyStartupHandles(ctx.Process, inheritHandles, startupInfo.UseStandardHandles,
                startupInfo.StandardInput, startupInfo.StandardOutput, startupInfo.StandardError);

            var main = child.EnsureMainThread(suspended: true);
            var processObject = new ProcessObject(child);
            processHandle = ctx.Handles.Create(processObject, 0x001F_FFFF);
            processObject.Release();
            var threadObject = new ThreadObject(main);
            threadHandle = ctx.Handles.Create(threadObject, 0x001F_FFFF);
            threadObject.Release();

            ctx.Memory.Write(processInformationPtr, processHandle);
            ctx.Memory.Write(processInformationPtr + 8, threadHandle);
            ctx.Memory.Write(processInformationPtr + 16, (uint)child.Id);
            ctx.Memory.Write(processInformationPtr + 20, (uint)main.Id);

            if (!suspended && !main.Start())
                throw new InvalidOperationException("the child main thread could not be started");

            ctx.Runtime.Trace.Emit(TraceKind.Process,
                $"CreateProcess{(unicode ? 'W' : 'A')} created child pid {child.Id} tid {main.Id} image='{imagePath}' suspended={suspended} inherit={inheritHandles}",
                ctx.Process.Id, ctx.Thread.Id);
            return 1;
        }
        catch (NtFileException ex)
        {
            CleanupFailedCreate(ctx, processInformationPtr, ref child, ref processHandle, ref threadHandle);
            ctx.SetLastError(Win32FromStatus(ex.Status));
            return 0;
        }
        catch (NtMemoryException ex)
        {
            CleanupFailedCreate(ctx, processInformationPtr, ref child, ref processHandle, ref threadHandle);
            ctx.SetLastError(Win32FromStatus(ex.Status));
            return 0;
        }
        catch (FileNotFoundException ex)
        {
            CleanupFailedCreate(ctx, processInformationPtr, ref child, ref processHandle, ref threadHandle);
            ctx.Runtime.Trace.Warn($"CreateProcess: {ex.Message}", ctx.Process.Id, ctx.Thread.Id);
            ctx.SetLastError(Win32Error.FileNotFound);
            return 0;
        }
        catch (BadImageFormatException ex)
        {
            CleanupFailedCreate(ctx, processInformationPtr, ref child, ref processHandle, ref threadHandle);
            ctx.Runtime.Trace.Warn($"CreateProcess: {ex.Message}", ctx.Process.Id, ctx.Thread.Id);
            ctx.SetLastError(Win32Error.BadExeFormat);
            return 0;
        }
        catch (Exception ex)
        {
            CleanupFailedCreate(ctx, processInformationPtr, ref child, ref processHandle, ref threadHandle);
            ctx.Runtime.Trace.Error($"CreateProcess failed: {ex.GetType().Name}: {ex.Message}", ctx.Process.Id, ctx.Thread.Id);
            ctx.SetLastError(Win32Error.ProcessAborted);
            return 0;
        }
    }

    private static void CleanupFailedCreate(ApiCallContext ctx, ulong processInformationPtr,
        ref ImaginedProcess? child, ref ulong processHandle, ref ulong threadHandle)
    {
        try { ctx.Memory.WriteBytes(processInformationPtr, new byte[ProcessInformationSize]); } catch { }
        if (processHandle != 0)
        {
            try { ctx.Handles.Close(processHandle); } catch { }
            processHandle = 0;
        }
        if (threadHandle != 0)
        {
            try { ctx.Handles.Close(threadHandle); } catch { }
            threadHandle = 0;
        }
        if (child is not null)
        {
            try { ctx.Runtime.RemoveProcess(child); } catch { }
            try { child.Terminate(-1); } catch { }
            try { child.Dispose(); } catch { }
            child = null;
        }
    }

    private static string? ReadGuestString(ApiCallContext ctx, int index, bool unicode)
    {
        var pointer = ctx.Arg(index);
        if (pointer == 0) return null;
        return unicode ? ctx.ReadUnicodeString(pointer) : ctx.ReadAnsiString(pointer);
    }

    private static GuestStartupInfo ReadStartupInfo(ApiCallContext ctx, ulong pointer)
    {
        if (pointer == 0) return default;
        var cb = ctx.Memory.Read<uint>(pointer);
        if (cb < 64) return default; // no dwFlags in this STARTUPINFO
        var flags = ctx.Memory.Read<uint>(pointer + 60);
        if ((flags & StartfUseStdHandles) == 0) return default;
        if (cb < StartupInfoSize) return new GuestStartupInfo(true, 0, 0, 0);
        return new GuestStartupInfo(
            true,
            ctx.Memory.Read<ulong>(pointer + 80),
            ctx.Memory.Read<ulong>(pointer + 88),
            ctx.Memory.Read<ulong>(pointer + 96));
    }

    private static string? ReadStartupDesktop(ApiCallContext ctx, ulong pointer, bool unicode)
    {
        if (pointer == 0 || ctx.Read<uint>(pointer) < 24) return null;
        var desktop = ctx.Read<ulong>(pointer + 16);
        return desktop == 0 ? null : unicode ? ctx.ReadUnicodeString(desktop) : ctx.ReadAnsiString(desktop);
    }

    private static IReadOnlyDictionary<string, string>? ReadEnvironmentBlock(ApiCallContext ctx, ulong pointer, bool unicode)
    {
        if (pointer == 0) return null;
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var offset = 0UL;
        for (var guard = 0; guard < 0x8000; guard++)
        {
            string entry;
            if (unicode)
            {
                entry = ctx.Memory.ReadUnicodeString(pointer + offset);
                offset += (ulong)(entry.Length + 1) * 2;
            }
            else
            {
                var bytes = new List<byte>();
                for (var i = 0; i < 32767; i++)
                {
                    var b = ctx.Read<byte>(pointer + offset++);
                    if (b == 0) break;
                    bytes.Add(b);
                }
                entry = (System.Text.CodePagesEncodingProvider.Instance.GetEncoding(0) ?? System.Text.Encoding.UTF8)
                    .GetString(bytes.ToArray());
            }
            if (entry.Length == 0) break;
            var equals = entry.IndexOf('=', entry.StartsWith('=') ? 1 : 0);
            if (equals > 0) environment[entry[..equals]] = entry[(equals + 1)..];
        }
        return environment;
    }

    /// <summary>
    /// Resolve a CreateProcess image name against the sandbox, the parent image directory and the
    /// guest PATH.  Host paths are only accepted inside the sandbox or next to the parent image.
    /// </summary>
    private static string? ResolveExecutablePath(ApiCallContext ctx, string? application, string? commandLine)
    {
        var token = !string.IsNullOrWhiteSpace(application) ? application.Trim() : FirstCommandLineToken(commandLine);
        if (string.IsNullOrWhiteSpace(token)) return null;

        var candidates = new List<string>();
        try
        {
            if (ctx.Process.FileSystem.TryMapPath(token, ctx.Process.CurrentDirectory, FileOperation.Read, out var mapped) == NtStatus.Success)
                AddExecutableCandidates(candidates, mapped);
        }
        catch (NtFileException)
        {
            // A host absolute path (or a path outside the virtual C:) is handled below.
        }

        if (Path.IsPathRooted(token))
            AddExecutableCandidates(candidates, token);

        if (!Path.IsPathRooted(token))
        {
            var imageDirectory = Path.GetDirectoryName(ctx.Process.ImagePath);
            if (!string.IsNullOrEmpty(imageDirectory))
                AddExecutableCandidates(candidates, Path.Combine(imageDirectory, token));

            if (ctx.Process.TryGetEnvironmentVariable("PATH", out var path))
            {
                foreach (var directory in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    try
                    {
                        if (ctx.Process.FileSystem.TryMapPath(directory, ctx.Process.CurrentDirectory, FileOperation.Read, out var mapped) == NtStatus.Success)
                            AddExecutableCandidates(candidates, Path.Combine(mapped, token));
                    }
                    catch (NtFileException)
                    {
                        // Host PATH entries are not part of the managed filesystem view.
                    }
                }
            }
        }

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (HostFileSystem.FileExists(candidate) && IsAllowedExecutablePath(ctx, candidate))
                    return Path.GetFullPath(candidate);
            }
            catch
            {
                // ignore malformed candidates
            }
        }
        return null;
    }

    private static void AddExecutableCandidates(List<string> candidates, string path)
    {
        try
        {
            candidates.Add(path);
            if (string.IsNullOrEmpty(Path.GetExtension(path))) candidates.Add(path + ".exe");
        }
        catch
        {
            // ignore malformed paths
        }
    }

    private static bool IsAllowedExecutablePath(ApiCallContext ctx, string path)
    {
        var full = Path.GetFullPath(path);
        if (ctx.Process.FileSystem.Drives.Any(d => IsUnder(full, d.HostDirectory))) return true;
        var imageDirectory = Path.GetDirectoryName(ctx.Process.ImagePath);
        return imageDirectory is not null && IsUnder(full, imageDirectory);
    }

    private static bool IsUnder(string path, string directory)
    {
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.GetFullPath(path);
        if (string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase)) return true;
        return candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string? FirstCommandLineToken(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return null;
        var index = 0;
        while (index < commandLine.Length && char.IsWhiteSpace(commandLine[index])) index++;
        if (index >= commandLine.Length) return null;

        var builder = new StringBuilder();
        if (commandLine[index] == '"')
        {
            index++;
            while (index < commandLine.Length && commandLine[index] != '"')
            {
                if (commandLine[index] == '\\' && index + 1 < commandLine.Length && commandLine[index + 1] == '"')
                {
                    builder.Append('"');
                    index += 2;
                    continue;
                }
                builder.Append(commandLine[index++]);
            }
        }
        else
        {
            while (index < commandLine.Length && !char.IsWhiteSpace(commandLine[index]))
                builder.Append(commandLine[index++]);
        }
        return builder.Length == 0 ? null : builder.ToString();
    }

    // --- handle inheritance ---------------------------------------------------------

    [VirtualApi("kernel32.dll", "SetHandleInformation", ApiCategory.Object)]
    private static ulong SetHandleInformation(ApiCallContext ctx)
    {
        if (!ctx.Handles.SetInformation(ctx.Arg(0), ctx.Arg32(1), ctx.Arg32(2)))
            return BoolResult(ctx, false, Win32Error.InvalidHandle);
        return 1;
    }

    [VirtualApi("kernel32.dll", "GetHandleInformation", ApiCategory.Object)]
    private static ulong GetHandleInformation(ApiCallContext ctx)
    {
        if (!ctx.Handles.TryGetInformation(ctx.Arg(0), out var flags))
            return BoolResult(ctx, false, Win32Error.InvalidHandle);
        if (ctx.Arg(1) != 0) ctx.Memory.Write(ctx.Arg(1), flags);
        return 1;
    }
}
