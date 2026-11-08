using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using ImaginedNT.Core.Diagnostics;
using ImaginedNT.Core.Native;

namespace ImaginedNT.Core.Pe;

/// <summary>在来宾导出入口使用单字节断点转入托管 thunk，保留相邻代码和调用现场。</summary>
public sealed unsafe class PeExportRedirector(TraceLog trace) : IDisposable
{
    private static readonly object Gate = new();
    private static readonly ConcurrentDictionary<ulong, ulong> Targets = new();
    private static IntPtr _handler;
    private static NativeExceptionFilter? _filter;
    private readonly Dictionary<ulong, byte> _originals = new();
    private bool _disposed;

    public void Add(ulong address, ulong target)
    {
        lock (Gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (Targets.TryGetValue(address, out var existing))
            {
                if (existing == target && _originals.ContainsKey(address)) return;
                throw new InvalidOperationException($"conflicting export redirects at 0x{address:X16}");
            }
            if (_handler == IntPtr.Zero)
            {
                delegate* unmanaged<ExceptionPointers*, int> handler = &HandleException;
                _filter ??= new NativeExceptionFilter((nint)handler);
                _handler = NativeMethods.AddVectoredExceptionHandler(1, _filter.Address);
                if (_handler == IntPtr.Zero)
                    throw new InvalidOperationException("could not install the Partially Imagined exception handler");
            }

            var original = *(byte*)address;
            Targets[address] = target;
            try
            {
                _filter!.Add(address);
                WriteByte(address, 0xCC);
                _originals.Add(address, original);
            }
            catch
            {
                try { WriteByte(address, original); }
                catch (Exception ex) { trace.Error($"export redirect rollback at 0x{address:X16} failed: {ex}"); }
                Targets.TryRemove(address, out _);
                _filter!.Remove(address);
                RemoveUnusedHandler();
                throw;
            }
        }
    }

    private static void WriteByte(ulong address, byte value)
    {
        if (!NativeMethods.VirtualProtect((IntPtr)(long)address, (UIntPtr)1,
                NativeMethods.PAGE_EXECUTE_READWRITE, out var old))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"could not write export entry at 0x{address:X16}");
        *(byte*)address = value;
        NativeMethods.FlushInstructionCache(NativeMethods.GetCurrentProcess(), (IntPtr)(long)address, (UIntPtr)1);
        if (!NativeMethods.VirtualProtect((IntPtr)(long)address, (UIntPtr)1, old, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"could not restore export protection at 0x{address:X16}");
    }

    [UnmanagedCallersOnly]
    private static int HandleException(ExceptionPointers* pointers)
    {
        if (pointers == null || pointers->Record == null || pointers->Context == null ||
            pointers->Record->Code != 0x80000003) return 0;
        if (!Targets.TryGetValue((ulong)(long)pointers->Record->Address, out var target)) return 0;
        pointers->Context->Rip = target;
        return -1;
    }

    private static void RemoveUnusedHandler()
    {
        if (!Targets.IsEmpty || _handler == IntPtr.Zero) return;
        NativeMethods.RemoveVectoredExceptionHandler(_handler);
        _handler = IntPtr.Zero;
    }

    public void Dispose()
    {
        lock (Gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var (address, original) in _originals)
            {
                try { WriteByte(address, original); }
                catch (Exception ex) { trace.Error($"export redirect cleanup at 0x{address:X16} failed: {ex}"); }
                Targets.TryRemove(address, out _);
                _filter!.Remove(address);
            }
            _originals.Clear();
            RemoveUnusedHandler();
        }
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct ExceptionRecord
    {
        [FieldOffset(0)] public uint Code;
        [FieldOffset(16)] public IntPtr Address;
    }

    [StructLayout(LayoutKind.Explicit, Size = 0x4D0)]
    private struct Context
    {
        [FieldOffset(0xF8)] public ulong Rip;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExceptionPointers
    {
        public ExceptionRecord* Record;
        public Context* Context;
    }
}
