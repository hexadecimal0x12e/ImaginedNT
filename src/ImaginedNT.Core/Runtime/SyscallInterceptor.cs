using System.Runtime.InteropServices;
using ImaginedNT.Core.Diagnostics;
using ImaginedNT.Core.Native;
using ImaginedNT.Core.Nt;
using ImaginedNT.Core.Pe;

namespace ImaginedNT.Core.Runtime;

/// <summary>
/// Intercepts direct <c>syscall</c> instructions in the guest image.  The loader rewrites
/// <c>0F 05</c> to <c>CC CC</c> and a vectored exception handler emulates the syscall from the
/// register context.  Two INT3 bytes are used so the handler can always resume at address+2.
///
/// This is intentionally opt-in.  A byte-pattern scan cannot prove instruction alignment, and
/// the operating system still owns the real syscall ABI; imports routed to the native DLLs
/// therefore still execute native syscalls.  The managed NT layer is the supported path.
/// </summary>
public sealed unsafe class SyscallInterceptor : IDisposable
{
    private const uint EXCEPTION_BREAKPOINT = 0x8000_0003;
    private const int EXCEPTION_CONTINUE_EXECUTION = -1;
    private const int EXCEPTION_CONTINUE_SEARCH = 0;

    private static readonly object ActiveGate = new();
    private static readonly List<SyscallInterceptor> Active = new();
    private static IntPtr _vectoredHandler;
    private static NativeExceptionFilter? _filter;
    private static int _referenceCount;

    private readonly object _gate = new();
    private readonly Dictionary<ulong, byte> _patched = new();
    private readonly List<(ulong Address, ulong Size, uint OldProtect)> _pages = new();
    private readonly ImaginedProcess _process;
    private readonly ImaginedRuntime _runtime;
    private readonly TraceLog _trace;
    private readonly PeMappedImage _image;
    private bool _disposed;

    public SyscallInterceptor(ImaginedProcess process, PeMappedImage image, TraceLog trace)
    {
        _process = process;
        _runtime = process.Runtime;
        _trace = trace;
        _image = image;

        lock (ActiveGate)
        {
            if (_referenceCount == 0)
            {
                delegate* unmanaged<EXCEPTION_POINTERS*, int> handler = &VectoredHandler;
                _filter ??= new NativeExceptionFilter((nint)handler);
                _vectoredHandler = NativeMethods.AddVectoredExceptionHandler(1, _filter.Address);
                if (_vectoredHandler == IntPtr.Zero)
                    throw new InvalidOperationException("AddVectoredExceptionHandler failed");
            }
            _referenceCount++;
            Active.Add(this);
        }

        try
        {
            ScanAndPatch();
        }
        catch
        {
            lock (ActiveGate)
            {
                Active.Remove(this);
                _referenceCount--;
                if (_referenceCount == 0 && _vectoredHandler != IntPtr.Zero)
                {
                    NativeMethods.RemoveVectoredExceptionHandler(_vectoredHandler);
                    _vectoredHandler = IntPtr.Zero;
                }
            }
            RestoreAll();
            throw;
        }
    }

    public int PatchedCount
    {
        get { lock (_gate) return _patched.Count; }
    }

    private void ScanAndPatch()
    {
        foreach (var section in _image.Image.Sections)
        {
            if (!section.IsExecutable || section.SizeOfRawData == 0) continue;
            var length = (int)Math.Min(section.SizeOfRawData, section.VirtualSize == 0 ? section.SizeOfRawData : Math.Max(section.VirtualSize, 1u));
            if (length < 2) continue;
            var address = _image.BaseAddress + section.VirtualAddress;
            var size = (ulong)length;
            if (!NativeMethods.VirtualProtect((IntPtr)(long)address, (UIntPtr)size, NativeMethods.PAGE_EXECUTE_READWRITE, out var old))
            {
                _trace.Warn($"syscall scan: VirtualProtect failed for {section.Name}");
                continue;
            }

            var bytes = new byte[length];
            Marshal.Copy((IntPtr)(long)address, bytes, 0, length);
            var patchCount = 0;
            for (var i = 0; i + 1 < length; i++)
            {
                if (bytes[i] != 0x0F || bytes[i + 1] != 0x05) continue;
                var instructionAddress = address + (ulong)i;
                lock (_gate) _patched[instructionAddress] = 0x05;
                _filter!.Add(instructionAddress);
                *(byte*)(long)instructionAddress = 0xCC;
                *(byte*)(long)(instructionAddress + 1) = 0xCC;
                patchCount++;
            }

            NativeMethods.FlushInstructionCache(NativeMethods.GetCurrentProcess(), (IntPtr)(long)address, (UIntPtr)size);
            if (!NativeMethods.VirtualProtect((IntPtr)(long)address, (UIntPtr)size, old, out _))
                _trace.Warn($"syscall scan: failed to restore protection for {section.Name}");
            else
                lock (_gate) _pages.Add((address, size, old));
            if (patchCount > 0)
                _trace.Emit(TraceKind.Syscall, $"patched {patchCount} syscall instruction(s) in {_image.Image.FileName}!{section.Name}");
        }
    }

    internal bool TryHandle(ulong exceptionAddress, CONTEXT* context)
    {
        lock (_gate)
        {
            if (!_patched.ContainsKey(exceptionAddress)) return false;
        }

        var number = (uint)context->Rax;
        var args = new ulong[16];
        args[0] = context->R10;
        args[1] = context->Rdx;
        args[2] = context->R8;
        args[3] = context->R9;
        try
        {
            // The caller's stack arguments start after the return address and shadow space.
            var count = NativeSyscallMap.TryGetName(number, out var name) &&
                NativeApiSignatures.TryGet(name, out var signature) ? signature.ArgumentCount : 6;
            for (var i = 4; i < count; i++)
                args[i] = *(ulong*)(context->Rsp + 0x28 + (ulong)(i - 4) * 8);
        }
        catch
        {
            // no stack arguments; leave them zero
        }

        var nativeId = NativeMethods.GetCurrentThreadId();
        var thread = _process.FindThreadByNativeId(nativeId) ?? _process.GetOrRegisterHostThread(nativeId);
        _trace.Emit(TraceKind.Syscall,
            $"trapped syscall 0x{number:X} ({VirtualSyscallNumbers.NameOf(number)}) at 0x{exceptionAddress:X16} caller=0x{context->Rip:X16}",
            _process.Id, thread.Id);

        var status = _runtime.Syscalls.Dispatch(number, args, context->Rip, out var result, context->Rsp);
        if (thread.HasRequestedExit && thread.ExitTrampolineAddress != 0)
        {
            // The syscall path does not execute a guest `ret`, so redirect RIP to the same
            // per-thread exit trampoline the IAT dispatcher uses.  The trampoline restores the
            // launcher frame and returns into managed code.
            context->Rip = thread.ExitTrampolineAddress;
            return true;
        }
        context->Rax = NtStatus.IsSuccess(status) ? result : status;
        context->Rip = exceptionAddress + 2;
        return true;
    }

    private void RestoreAll()
    {
        List<(ulong Address, byte Original)> patches;
        List<(ulong Address, ulong Size, uint OldProtect)> pages;
        lock (_gate)
        {
            patches = _patched.Select(kv => (kv.Key, kv.Value)).ToList();
            _patched.Clear();
            pages = _pages.ToList();
            _pages.Clear();
        }

        foreach (var (address, _) in patches)
        {
            try
            {
                NativeMethods.VirtualProtect((IntPtr)(long)address, (UIntPtr)2, NativeMethods.PAGE_EXECUTE_READWRITE, out var old);
                *(byte*)(long)address = 0x0F;
                *(byte*)(long)(address + 1) = 0x05;
                NativeMethods.VirtualProtect((IntPtr)(long)address, (UIntPtr)2, old, out _);
            }
            catch { }
            _filter!.Remove(address);
        }

        foreach (var (address, size, old) in pages)
        {
            try { NativeMethods.VirtualProtect((IntPtr)(long)address, (UIntPtr)size, old, out _); } catch { }
        }
    }

    [UnmanagedCallersOnly]
    private static int VectoredHandler(EXCEPTION_POINTERS* pointers)
    {
        try
        {
            var record = pointers->ExceptionRecord;
            if (record is null || record->ExceptionCode != EXCEPTION_BREAKPOINT) return EXCEPTION_CONTINUE_SEARCH;
            var context = pointers->ContextRecord;
            if (context is null) return EXCEPTION_CONTINUE_SEARCH;
            var address = (ulong)(long)record->ExceptionAddress;
            SyscallInterceptor[] snapshot;
            lock (ActiveGate) snapshot = Active.ToArray();
            foreach (var interceptor in snapshot)
            {
                try
                {
                    if (interceptor.TryHandle(address, context)) return EXCEPTION_CONTINUE_EXECUTION;
                }
                catch
                {
                    // If the matched instruction cannot be emulated, skip it rather than
                    // deadlocking the process in an exception handler.
                    lock (interceptor._gate)
                    {
                        if (interceptor._patched.ContainsKey(address))
                        {
                            context->Rip = address + 2;
                            context->Rax = NtStatus.NotImplemented;
                            return EXCEPTION_CONTINUE_EXECUTION;
                        }
                    }
                }
            }
        }
        catch
        {
            // fall through to the next handler
        }
        return EXCEPTION_CONTINUE_SEARCH;
    }

    public void Dispose()
    {
        lock (ActiveGate)
        {
            if (_disposed) return;
            _disposed = true;
            Active.Remove(this);
            _referenceCount--;
            if (_referenceCount <= 0)
            {
                _referenceCount = 0;
                if (_vectoredHandler != IntPtr.Zero)
                {
                    NativeMethods.RemoveVectoredExceptionHandler(_vectoredHandler);
                    _vectoredHandler = IntPtr.Zero;
                }
            }
        }
        RestoreAll();
    }

    [StructLayout(LayoutKind.Explicit)]
    internal unsafe struct EXCEPTION_RECORD
    {
        [FieldOffset(0)] public uint ExceptionCode;
        [FieldOffset(4)] public uint ExceptionFlags;
        [FieldOffset(8)] public IntPtr ExceptionRecord;
        [FieldOffset(16)] public IntPtr ExceptionAddress;
        [FieldOffset(24)] public uint NumberParameters;
        [FieldOffset(32)] public fixed ulong ExceptionInformation[15];
    }

    [StructLayout(LayoutKind.Explicit)]
    internal unsafe struct EXCEPTION_POINTERS
    {
        [FieldOffset(0)] public EXCEPTION_RECORD* ExceptionRecord;
        [FieldOffset(8)] public CONTEXT* ContextRecord;
    }

    [StructLayout(LayoutKind.Explicit, Size = 0x4D0)]
    internal struct CONTEXT
    {
        [FieldOffset(0x78)] public ulong Rax;
        [FieldOffset(0x80)] public ulong Rcx;
        [FieldOffset(0x88)] public ulong Rdx;
        [FieldOffset(0x90)] public ulong Rbx;
        [FieldOffset(0x98)] public ulong Rsp;
        [FieldOffset(0xA0)] public ulong Rbp;
        [FieldOffset(0xA8)] public ulong Rsi;
        [FieldOffset(0xB0)] public ulong Rdi;
        [FieldOffset(0xB8)] public ulong R8;
        [FieldOffset(0xC0)] public ulong R9;
        [FieldOffset(0xC8)] public ulong R10;
        [FieldOffset(0xF8)] public ulong Rip;
    }
}
