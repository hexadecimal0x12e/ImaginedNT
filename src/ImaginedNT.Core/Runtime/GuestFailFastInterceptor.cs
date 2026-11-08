using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Iced.Intel;
using ImaginedNT.Core.Native;
using ImaginedNT.Core.Nt;
using ImaginedNT.Core.Pe;

namespace ImaginedNT.Core.Runtime;

internal sealed unsafe class GuestFailFastInterceptor : IDisposable
{
    private static readonly object Gate = new();
    private static readonly ConcurrentDictionary<ulong, GuestFailFastInterceptor> Targets = new();
    private static IntPtr _handler;
    private static NativeExceptionFilter? _filter;
    private readonly ImaginedProcess _process;
    private readonly PeMappedImage _image;
    private readonly List<ulong> _patched = new();
    private bool _disposed;

    public GuestFailFastInterceptor(ImaginedProcess process, PeMappedImage image)
    {
        _process = process;
        _image = image;
        try
        {
            foreach (var section in image.Image.Sections)
            {
                if (!section.IsExecutable || section.SizeOfRawData == 0) continue;
                var length = (int)Math.Min(section.SizeOfRawData,
                    section.VirtualSize == 0 ? section.SizeOfRawData : section.VirtualSize);
                var decoder = Decoder.Create(64, new ByteArrayCodeReader(image.Image.ReadRva(section.VirtualAddress, length)));
                decoder.IP = image.BaseAddress + section.VirtualAddress;
                var end = decoder.IP + (uint)length;
                while (decoder.IP < end)
                {
                    decoder.Decode(out var instruction);
                    if (instruction.Code != Code.Int_imm8 || instruction.Immediate8 != 0x29 || instruction.Length != 2) continue;
                    Add(instruction.IP);
                }
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void Add(ulong address)
    {
        lock (Gate)
        {
            if (_handler == IntPtr.Zero)
            {
                delegate* unmanaged<SyscallInterceptor.EXCEPTION_POINTERS*, int> handler = &HandleException;
                _filter ??= new NativeExceptionFilter((nint)handler);
                _handler = NativeMethods.AddVectoredExceptionHandler(1, _filter.Address);
                if (_handler == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            if (!Targets.TryAdd(address, this)) throw new InvalidOperationException($"duplicate fail-fast trap at 0x{address:X16}");
            _patched.Add(address);
            _filter!.Add(address);
            // Intercept before INT 29h executes, since a real fast-fail bypasses VEH/SEH.
            WriteByte(address, 0xCC);
        }
    }

    private static void WriteByte(ulong address, byte value)
    {
        if (!NativeMethods.VirtualProtect((nint)address, (UIntPtr)1, NativeMethods.PAGE_EXECUTE_READWRITE, out var old))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"could not patch fail-fast instruction at 0x{address:X16}");
        *(byte*)address = value;
        NativeMethods.FlushInstructionCache(NativeMethods.GetCurrentProcess(), (nint)address, (UIntPtr)1);
        if (!NativeMethods.VirtualProtect((nint)address, (UIntPtr)1, old, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"could not restore fail-fast page protection at 0x{address:X16}");
    }

    [UnmanagedCallersOnly]
    private static int HandleException(SyscallInterceptor.EXCEPTION_POINTERS* pointers)
    {
        var handled = false;
        try
        {
            if (pointers == null || pointers->ExceptionRecord == null || pointers->ContextRecord == null ||
                pointers->ExceptionRecord->ExceptionCode != 0x80000003) return 0;
            var address = (ulong)pointers->ExceptionRecord->ExceptionAddress;
            if (!Targets.TryGetValue(address, out var trap)) return 0;
            var thread = trap._process.Runtime.GetCurrentThread();
            if (thread is null || thread.Process != trap._process || !thread.CanUnwindGuest) return 0;
            thread.RequestExit();
            pointers->ContextRecord->Rax = NtStatus.StackBufferOverrun;
            pointers->ContextRecord->Rip = thread.ExitTrampolineAddress;
            handled = true;
            trap._process.FailFast((uint)pointers->ContextRecord->Rcx, address,
                $"{trap._image.Image.FileName}+0x{address - trap._image.BaseAddress:X}");
            return -1;
        }
        catch
        {
            return handled ? -1 : 0;
        }
    }

    public void Dispose()
    {
        lock (Gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var address in _patched)
            {
                try { WriteByte(address, 0xCD); }
                catch (Exception ex) { _process.Trace.Error($"fail-fast trap cleanup at 0x{address:X16} failed: {ex}", _process.Id); }
                Targets.TryRemove(address, out _);
                _filter!.Remove(address);
            }
            _patched.Clear();
            if (!Targets.IsEmpty || _handler == IntPtr.Zero) return;
            NativeMethods.RemoveVectoredExceptionHandler(_handler);
            _handler = IntPtr.Zero;
        }
    }
}

internal class GuestThreadExitException(int exitCode) : Exception("guest thread requested exit")
{
    public int ExitCode { get; } = exitCode;
}

internal sealed class GuestProcessFaultException(int exitCode) : GuestThreadExitException(exitCode);
