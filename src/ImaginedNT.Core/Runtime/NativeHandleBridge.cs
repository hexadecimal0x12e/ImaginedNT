using System.Runtime.InteropServices;
using ImaginedNT.Core.Native;
using ImaginedNT.Core.Nt;
using ImaginedNT.Core.Objects;
using ImaginedNT.Core.Thunks;

namespace ImaginedNT.Core.Runtime;

internal static unsafe class NativeHandleBridge
{
    internal static bool IsNtApi(string module, string name) =>
        ApiRegistry.NormalizeModule(module) == "ntdll" &&
        (name.StartsWith("Nt", StringComparison.Ordinal) || name.StartsWith("Zw", StringComparison.Ordinal));

    private static bool IsMultipleWait(string name) =>
        name is "NtWaitForMultipleObjects" or "WaitForMultipleObjects" or "WaitForMultipleObjectsEx";

    internal static ulong Invoke(ApiCallContext ctx, ulong target)
    {
        var nt = IsNtApi(ctx.Module, ctx.Name);
        ulong Fail(uint status)
        {
            if (nt) return status;
            ctx.SetLastError(status == NtStatus.AccessDenied ? 5 : status == NtStatus.InvalidHandle ? 6 :
                status == NtStatus.InvalidParameter ? 87 : 50);
            return ctx.Name.StartsWith("WaitFor", StringComparison.Ordinal) || ctx.Name == "SetFilePointer"
                ? uint.MaxValue : 0;
        }
        var name = NativeApiSignatures.Normalize(ctx.Name);
        if (!(nt ? NativeApiSignatures.TryGet(name, out var signature)
            : NativeApiSignatures.TryGetWin32(ctx.Module, name, out signature)))
        {
            return Fail(NtStatus.NotSupported);
        }
        if (name == "NtQueryInformationToken" && ctx.Arg32(1) == 19 ||
            name == "NtQueryInformationProcess" && ctx.Arg32(1) == 30)
            signature = signature with { OutputHandleArguments = [2] };
        // These information classes/attribute lists can carry handles inside opaque structures.
        if (name is "NtDeviceIoControlFile" or "NtFsControlFile" ||
            name == "NtSetInformationFile" && ctx.Arg32(4) is not (4 or 13 or 14 or 19 or 20 or 40 or 64) ||
            name == "NtQueryInformationFile" && ctx.Arg32(4) is not (4 or 5 or 6 or 7 or 8 or 9 or 14 or 16 or 17 or 18 or 19 or 20 or 22 or 28 or 34 or 35 or 48 or 59) ||
            name == "NtQueryInformationProcess" && ctx.Arg32(1) is not (0 or 26 or 27 or 29 or 30 or 43) ||
            name == "NtQueryInformationThread" && ctx.Arg32(1) is not (0 or 1 or 2 or 9 or 17) ||
            name == "NtCreateThreadEx" && ctx.Arg(10) != 0)
            return Fail(NtStatus.NotSupported);

        if ((name is "NtClose" or "CloseHandle") && ctx.Handles.TryGet(ctx.Arg(0), out _))
        {
            var status = ctx.Handles.Close(ctx.Arg(0));
            return nt ? status : status == NtStatus.Success ? 1UL : Fail(status);
        }

        // A guest handle table cannot represent handles installed into another host process.
        if (name == "NtDuplicateObject" &&
            (ctx.Arg(0) != ulong.MaxValue || ctx.Arg(2) != ulong.MaxValue)) return Fail(NtStatus.NotSupported);

        if (!nt && (name is "ReadFile" or "WriteFile") && ctx.Arg(4) != 0)
            return Fail(NtStatus.NotSupported);

        var args = stackalloc ulong[16];
        new Span<ulong>(args, 16).Clear();
        for (var i = 0; i < signature.ArgumentCount; i++) args[i] = ctx.Arg(i);
        var outputs = stackalloc ulong[16];
        new Span<ulong>(outputs, 16).Clear();
        int? lastError = null;
        var acquired = new List<ImaginedHandle>();
        var allocations = new List<nint>();
        uint Expand(ulong value, out ulong native)
        {
            native = value;
            if (value == 0 || value >= unchecked((ulong)-6L)) return NtStatus.Success;
            if (!ctx.Handles.TryGet(value, out _))
                return value >= 0x4000_0000 && value < 0x8000_0000 ? NtStatus.InvalidHandle : NtStatus.Success;
            var status = ctx.Handles.AcquireNative(value, ctx.Runtime.Options.ExpandManagedHandles, out var obj);
            if (status != NtStatus.Success) return status;
            acquired.Add(obj!);
            native = (ulong)obj!.GetNativeHandle();
            return NtStatus.Success;
        }

        try
        {
            if (name == "GetHandleInformation" && ctx.Handles.TryGetInformation(ctx.Arg(0), out var flags))
            {
                ctx.Write(ctx.Arg(1), flags);
                return 1;
            }
            if (name == "SetHandleInformation" && ctx.Handles.TryGet(ctx.Arg(0), out _))
                return ctx.Handles.SetInformation(ctx.Arg(0), ctx.Arg32(1), ctx.Arg32(2)) ? 1UL : Fail(NtStatus.InvalidHandle);
            if (name == "DuplicateHandle" && ctx.Handles.TryGet(ctx.Arg(1), out _))
            {
                if (ctx.Arg(0) != ulong.MaxValue || ctx.Arg(2) != ulong.MaxValue) return Fail(NtStatus.NotSupported);
                if (ctx.Arg(3) == 0) return Fail(NtStatus.InvalidParameter);
                var status = ctx.Handles.Duplicate(ctx.Arg(1), (ctx.Arg32(6) & 2) != 0 ? 0 : ctx.Arg32(4),
                    out var duplicate, ctx.ArgBool(5));
                if ((ctx.Arg32(6) & 1) != 0) ctx.Handles.Close(ctx.Arg(1));
                if (status != NtStatus.Success) return Fail(status);
                try { ctx.Write(ctx.Arg(3), duplicate); }
                catch { ctx.Handles.Close(duplicate); throw; }
                return 1;
            }
            foreach (var i in signature.HandleArguments)
            {
                var status = Expand(args[i], out var value);
                if (status != NtStatus.Success) return Fail(status);
                args[i] = value;
            }
            foreach (var i in signature.ObjectAttributesArguments)
            {
                if (args[i] == 0) continue;
                if (ctx.Read<uint>(args[i]) != 48) return Fail(NtStatus.InvalidParameter);
                var copy = (ulong)NativeMemory.Alloc(48);
                allocations.Add((nint)copy);
                ctx.ReadBytes(args[i], 48).CopyTo(new Span<byte>((void*)copy, 48));
                var status = Expand(*(ulong*)(copy + 8), out var root);
                if (status != NtStatus.Success) return Fail(status);
                *(ulong*)(copy + 8) = root;
                args[i] = copy;
            }
            if (IsMultipleWait(name))
            {
                if (args[0] is 0 or > 64 || args[1] == 0) return Fail(NtStatus.InvalidParameter);
                var copy = (ulong*)NativeMemory.Alloc((nuint)(args[0] * 8));
                allocations.Add((nint)copy);
                for (var i = 0; i < (int)args[0]; i++)
                {
                    var status = Expand(ctx.Read<ulong>(args[1] + (ulong)i * 8), out var value);
                    if (status != NtStatus.Success) return Fail(status);
                    copy[i] = value;
                }
                args[1] = (ulong)copy;
            }
            foreach (var i in signature.OutputHandleArguments)
                if (args[i] != 0) args[i] = (ulong)(outputs + i);
            if (name == "NtDuplicateObject" && ctx.Handles.TryGet(ctx.Arg(1), out _)) args[6] &= ~1UL;

            // NT exports use integer/pointer arguments and NTSTATUS on the Windows x64 ABI.
            var call = (delegate* unmanaged<ulong, ulong, ulong, ulong, ulong, ulong, ulong, ulong,
                ulong, ulong, ulong, ulong, ulong, ulong, ulong, ulong, uint>)(nint)target;
            var result = ctx.Thread.IsManagedHosted
                ? (uint)ctx.Thread.InvokeGuest(target, new ReadOnlySpan<ulong>(args, 16))
                : call(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7],
                    args[8], args[9], args[10], args[11], args[12], args[13], args[14], args[15]);
            if (!nt) lastError = NativeMethods.GetLastError();
            if (name == "NtDuplicateObject" && (ctx.Arg(6) & 1) != 0 && ctx.Handles.TryGet(ctx.Arg(1), out _))
                ctx.Handles.Close(ctx.Arg(1));
            if (nt ? !NtStatus.IsSuccess(result) : result == 0) return result;

            foreach (var i in signature.OutputHandleArguments)
            {
                if (ctx.Arg(i) == 0) continue;
                var value = outputs[i];
                if (value != 0 && value < unchecked((ulong)-6L) && ctx.Runtime.Options.WrapNativeHandles)
                {
                    var inherit = name == "NtDuplicateObject" && (ctx.Arg(5) & 2) != 0 ||
                        name == "DuplicateHandle" && ctx.ArgBool(5) ||
                        name == "NtOpenProcessTokenEx" && (ctx.Arg(2) & 2) != 0 ||
                        name == "NtOpenThreadTokenEx" && (ctx.Arg(3) & 2) != 0;
                    foreach (var a in signature.ObjectAttributesArguments)
                        if (ctx.Arg(a) != 0 && (ctx.Read<uint>(ctx.Arg(a) + 24) & 2) != 0) inherit = true;
                    outputs[i] = 0;
                    var wrapped = ctx.Handles.WrapNative((nint)value, inheritable: inherit);
                    try { ctx.Write(ctx.Arg(i), wrapped); }
                    catch { ctx.Handles.Close(wrapped); throw; }
                }
                else
                {
                    outputs[i] = 0;
                    try { ctx.Write(ctx.Arg(i), value); }
                    catch
                    {
                        if (value != 0 && value < unchecked((ulong)-6L)) NativeMethods.CloseHandle((nint)value);
                        throw;
                    }
                }
            }
            return result;
        }
        catch (GuestThreadExitException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ctx.Runtime.Trace.Error($"native handle conversion for {ctx.Name} failed: {ex.GetType().Name}: {ex.Message}",
                ctx.Process.Id, ctx.Thread.Id);
            lastError = null;
            return Fail(NtStatus.InvalidParameter);
        }
        finally
        {
            foreach (var i in signature.OutputHandleArguments)
                if (outputs[i] != 0 && outputs[i] < unchecked((ulong)-6L)) NativeMethods.CloseHandle((nint)outputs[i]);
            foreach (var allocation in allocations) NativeMemory.Free((void*)allocation);
            foreach (var obj in acquired) obj.Release();
            if (lastError is int error) NativeMethods.SetLastError(error);
        }
    }

    internal static ulong InvokeFrame(ImaginedProcess process, ImaginedThread thread, string module, string name,
        ulong target, NativeCallFrame* frame)
    {
        var known = IsNtApi(module, name) ? NativeApiSignatures.TryGet(name, out var signature)
            : NativeApiSignatures.TryGetWin32(module, name, out signature);
        var args = new ulong[known ? signature.ArgumentCount : 0];
        for (var i = 0; i < args.Length; i++) args[i] = frame->Arg(i);
        var context = new ApiCallContext(process, thread, module, name, frame->ApiId, frame->ReturnAddress, args,
            frame->StackArgs >= 0x28 ? frame->StackArgs - 0x28 : 0);
        try { return Invoke(context, target); }
        catch (GuestThreadExitException ex)
        {
            context.PendingThreadExitCode = ex.ExitCode;
            return unchecked((uint)ex.ExitCode);
        }
        finally
        {
            if (process.FaultExitCode is int code) context.PendingThreadExitCode = code;
            if (context.PendingThreadExitCode is not null) context.TryRedirectThreadExit();
        }
    }
}
