using ImaginedNT.Core.Io;
using ImaginedNT.Core.Nt;
using ImaginedNT.Core.Objects;
using ImaginedNT.Core.Thunks;

namespace ImaginedNT.Core.Runtime;

/// <summary>Everything a managed API implementation needs: arguments, virtual memory and object handles.</summary>
public sealed unsafe class ApiCallContext
{
    private readonly ulong[] _args;

    public ApiCallContext(ImaginedProcess process, ImaginedThread thread, string module, string name, ulong apiId,
        ulong returnAddress, ReadOnlySpan<ulong> args, ulong returnAddressSlot = 0)
    {
        Process = process;
        Thread = thread;
        Runtime = process.Runtime;
        Module = module;
        Name = name;
        ApiId = apiId;
        ReturnAddress = returnAddress;
        ReturnAddressSlot = returnAddressSlot;
        _args = new ulong[16];
        args[..Math.Min(args.Length, 16)].CopyTo(_args);
    }

    public ImaginedProcess Process { get; }
    public ImaginedThread Thread { get; }
    public ImaginedRuntime Runtime { get; }
    public string Module { get; }
    public string Name { get; }
    public ulong ApiId { get; }
    public ulong ReturnAddress { get; }

    /// <summary>Guest stack slot holding the return address of this import call, or 0 if unknown.</summary>
    public ulong ReturnAddressSlot { get; }

    /// <summary>Set when the handler wants the guest thread to terminate after returning.</summary>
    public int? PendingThreadExitCode { get; set; }

    public bool CanRedirectThreadExit =>
        ReturnAddressSlot != 0 && Thread.ExitTrampolineAddress != 0 && Thread.CanUnwindGuest;

    public ulong FailFast(uint code, uint status = NtStatus.StackBufferOverrun)
    {
        Process.FailFast(code, ReturnAddress, $"{Module}!{Name}", status);
        PendingThreadExitCode = unchecked((int)status);
        return status;
    }

    /// <summary>
    /// Arrange for the universal stub's `ret` to land on the unmanaged thread-exit trampoline.
    /// The dispatcher still has to return the exit code in RAX (the trampoline uses EAX).
    /// </summary>
    public bool TryRedirectThreadExit()
    {
        if (PendingThreadExitCode is not int exitCode || !CanRedirectThreadExit) return false;
        try
        {
            Thread.RequestExit();
            Runtime.Trace.Emit(Diagnostics.TraceKind.Thread,
                $"thread {Thread.Id} exit={exitCode} via guest-stack trampoline 0x{Thread.ExitTrampolineAddress:X16}",
                Process.Id, Thread.Id);
            Memory.Write(ReturnAddressSlot, Thread.ExitTrampolineAddress);
            return true;
        }
        catch (Exception ex)
        {
            Runtime.Trace.Warn($"could not redirect thread exit: {ex.GetType().Name}: {ex.Message}", Process.Id, Thread.Id);
            return false;
        }
    }
    public Memory.VirtualMemoryManager Memory => Process.Memory;
    public ObjectManager ObjectManager => Process.ObjectManager;
    public HandleTable Handles => Process.Handles;

    public ulong Arg(int index) => (uint)index < 16 ? _args[index] : 0;
    public ulong ArgPtr(int index) => Arg(index);
    public uint Arg32(int index) => (uint)Arg(index);
    public int ArgInt32(int index) => unchecked((int)(uint)Arg(index));
    public bool ArgBool(int index) => Arg(index) != 0;
    public nint HandleArg(int index) => (nint)(long)Arg(index);

    public int LastError
    {
        get
        {
            Thread.LastError = Native.NativeMethods.GetLastError();
            return Thread.LastError;
        }
        set
        {
            Thread.LastError = value;
            Native.NativeMethods.SetLastError(value);
        }
    }

    public T Read<T>(ulong address) where T : unmanaged => Memory.Read<T>(address);
    public byte[] ReadBytes(ulong address, int length) => Memory.ReadBytes(address, length);
    public void Write<T>(ulong address, T value) where T : unmanaged => Memory.Write(address, value);
    public void WriteBytes(ulong address, ReadOnlySpan<byte> bytes) => Memory.WriteBytes(address, bytes);
    public string ReadUnicodeString(ulong address) => Memory.ReadUnicodeString(address);
    public string ReadAnsiString(ulong address) => Memory.ReadAnsiString(address);

    public void SetLastError(int error)
    {
        Thread.LastError = error;
        // Bridge the managed value to the host thread's last-error slot so that a native
        // forwarded call and a managed call observe the same value on the same guest thread.
        Native.NativeMethods.SetLastError(error);
    }

    public bool TryGetObject(ulong handle, out NtObject? obj, uint desiredAccess = 0)
    {
        var status = Handles.TryGet(handle, desiredAccess, out obj);
        return status == NtStatus.Success;
    }

    public bool TryGetFile(ulong handle, out FileObject? file, uint desiredAccess = 0)
    {
        file = null;
        if (handle == unchecked((ulong)(-1)) || handle == unchecked((ulong)(-2))) return false;
        if (Handles.TryGet(handle, desiredAccess, out var obj) != NtStatus.Success) return false;
        file = obj as FileObject;
        return file is not null;
    }

    public string? ResolveHandlePath(ulong handle)
    {
        if (handle == unchecked((ulong)(-1))) return "\\Device\\ImaginedProcess";
        if (handle == unchecked((ulong)(-2))) return "\\Device\\ImaginedThread";
        return Handles.TryGet(handle, out var obj) ? obj?.FullPath : null;
    }

    public ObjectAttributes ReadObjectAttributes(ulong address) =>
        ObjectAttributes.Read(Memory, ResolveHandlePath, address);

    public ulong WriteIoStatus(ulong ioStatusBlock, uint status, ulong information)
    {
        if (ioStatusBlock != 0)
        {
            Memory.Write(ioStatusBlock, status);
            Memory.Write(ioStatusBlock + 8, information);
        }
        return status;
    }

    /// <summary>Read a Win32 BOOL/DWORD pointer if it is non-null.</summary>
    public void WriteUInt32IfNotNull(ulong address, uint value)
    {
        if (address != 0) Memory.Write(address, value);
    }

    public ulong Complete(ulong result)
    {
        if (Runtime.Options.TraceApiArguments)
        {
            var a = $"{Arg(0):X},{Arg(1):X},{Arg(2):X},{Arg(3):X}";
            Runtime.Trace.Emit(Diagnostics.TraceKind.Api,
                $"{Module}!{Name}({a}) -> 0x{result:X} caller=0x{ReturnAddress:X16}",
                Process.Id, Thread.Id);
        }
        return result;
    }
}
