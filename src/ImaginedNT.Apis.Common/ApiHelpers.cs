using ImaginedNT.Core.Runtime;
using ImaginedNT.Core.Nt;
using ImaginedNT.Core.Objects;

namespace ImaginedNT.Apis;

public static class ApiHelpers
{
    public const ulong CurrentProcessPseudoHandle = unchecked((ulong)(-1));
    public const ulong CurrentThreadPseudoHandle = unchecked((ulong)(-2));
    public const ulong InvalidHandleValue = unchecked((ulong)(-1));

    public static ulong BoolResult(ApiCallContext ctx, bool success, int error = 0)
    {
        if (!success) ctx.SetLastError(error);
        return success ? 1UL : 0UL;
    }

    public static NtObject? GetObject(ApiCallContext ctx, ulong handle, uint access = 0)
    {
        if (handle == CurrentProcessPseudoHandle || handle == CurrentThreadPseudoHandle) return null;
        ctx.Handles.TryGet(handle, access, out var obj);
        return obj;
    }

    public static ulong NtStatusOr(ApiCallContext ctx, uint status) => status;

    public static void WriteNtStatus(ApiCallContext ctx, ulong ioStatus, uint status, ulong information = 0)
    {
        if (ioStatus != 0)
        {
            ctx.Memory.Write(ioStatus, status);
            ctx.Memory.Write(ioStatus + 8, information);
        }
    }

    public static int Win32FromStatus(uint status) => status switch
    {
        NtStatus.Success => 0,
        NtStatus.ObjectNameNotFound => Win32Error.FileNotFound,
        NtStatus.ObjectPathNotFound => Win32Error.PathNotFound,
        NtStatus.ObjectNameCollision => Win32Error.AlreadyExists,
        NtStatus.AccessDenied => Win32Error.AccessDenied,
        NtStatus.InvalidHandle => Win32Error.InvalidHandle,
        NtStatus.InvalidParameter => Win32Error.InvalidParameter,
        NtStatus.NoMemory => Win32Error.NotEnoughMemory,
        NtStatus.SharingViolation => Win32Error.SharingViolation,
        NtStatus.EndOfFile => Win32Error.HandleEof,
        NtStatus.FileIsADirectory => 267,
        NtStatus.NotADirectory => 267,
        NtStatus.DirectoryNotEmpty => 145,
        NtStatus.ObjectNameInvalid => 123,
        NtStatus.ObjectNameTooLong => Win32Error.FilenameExcedRange,
        NtStatus.NotSupported or NtStatus.NotImplemented => Win32Error.CallNotImplemented,
        _ => (int)(status & 0xFFFF)
    };

    public static string? ReadUnicodeArg(ApiCallContext ctx, int index)
    {
        var ptr = ctx.Arg(index);
        return ptr == 0 ? null : ctx.ReadUnicodeString(ptr);
    }

    public static ulong WriteUtf16(ApiCallContext ctx, ulong buffer, int bufferChars, string value, bool includeNull = true)
    {
        var bytes = System.Text.Encoding.Unicode.GetBytes(value);
        var required = bytes.Length + (includeNull ? 2 : 0);
        if (buffer == 0 || bufferChars <= 0) return (ulong)required / 2;
        var capacity = Math.Min(bytes.Length, Math.Max(0, bufferChars * 2 - (includeNull ? 2 : 0)));
        if (capacity > 0) ctx.Memory.WriteBytes(buffer, bytes.AsSpan(0, capacity));
        if (includeNull && bufferChars * 2 >= bytes.Length + 2)
            ctx.Memory.Write<ushort>(buffer + (ulong)bytes.Length, 0);
        return (ulong)(bytes.Length / 2);
    }

    public static ulong WriteAnsi(ApiCallContext ctx, ulong buffer, int bufferChars, string value, bool includeNull = true)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(value);
        var required = bytes.Length + (includeNull ? 1 : 0);
        if (buffer == 0 || bufferChars <= 0) return (ulong)required;
        var capacity = Math.Min(bytes.Length, Math.Max(0, bufferChars - (includeNull ? 1 : 0)));
        if (capacity > 0) ctx.Memory.WriteBytes(buffer, bytes.AsSpan(0, capacity));
        if (includeNull && bufferChars > bytes.Length) ctx.Memory.Write(buffer + (ulong)bytes.Length, (byte)0);
        return (ulong)bytes.Length;
    }

    public static int CurrentVirtualThreadId(ApiCallContext ctx) => ctx.Thread.Id;

    public static long ToFileTime(DateTime utc)
    {
        try { return utc.ToFileTimeUtc(); }
        catch { return 0; }
    }
}
