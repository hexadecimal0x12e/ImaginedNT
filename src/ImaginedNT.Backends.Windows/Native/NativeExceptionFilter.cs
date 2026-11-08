using System.ComponentModel;
using System.Runtime.InteropServices;
using ImaginedNT.Core.Thunks;

namespace ImaginedNT.Core.Native;

/// <summary>在进入 CLR 回调前，以纯原生代码筛选已登记的来宾断点。</summary>
internal sealed unsafe class NativeExceptionFilter
{
    private readonly object _gate = new();
    private readonly Dictionary<ulong, nint> _nodes = new();
    private readonly Stack<nint> _free = new();
    private readonly nint _head;

    public NativeExceptionFilter(nint handler)
    {
        _head = (nint)NativeMemory.AllocZeroed((nuint)sizeof(nint));
        Address = NativeMethods.VirtualAlloc(IntPtr.Zero, (UIntPtr)4096,
            NativeMethods.MEM_RESERVE | NativeMethods.MEM_COMMIT, NativeMethods.PAGE_READWRITE);
        try
        {
            if (Address == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            var code = X64StubEmitter.EmitExceptionFilter((ulong)handler, (ulong)_head);
            Marshal.Copy(code, 0, Address, code.Length);
            if (!NativeMethods.VirtualProtect(Address, (UIntPtr)4096, NativeMethods.PAGE_EXECUTE_READ, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            NativeMethods.FlushInstructionCache(NativeMethods.GetCurrentProcess(), Address, (UIntPtr)code.Length);
        }
        catch
        {
            if (Address != IntPtr.Zero) NativeMethods.VirtualFree(Address, UIntPtr.Zero, NativeMethods.MEM_RELEASE);
            NativeMemory.Free((void*)_head);
            throw;
        }
    }

    public nint Address { get; }

    // Removing a VEH does not drain native readers. Keep the code and linked nodes alive for
    // the process lifetime, and reuse cleared nodes without changing their next pointers.
    public void Add(ulong address)
    {
        if (address == 0) throw new ArgumentOutOfRangeException(nameof(address));
        lock (_gate)
        {
            if (_nodes.ContainsKey(address)) return;
            var reused = _free.TryPop(out var node);
            if (!reused)
            {
                node = (nint)NativeMemory.Alloc((nuint)16);
                *(nint*)node = Volatile.Read(ref *(nint*)_head);
            }
            _nodes.Add(address, node);
            Volatile.Write(ref ((long*)node)[1], unchecked((long)address));
            if (!reused) Volatile.Write(ref *(nint*)_head, node);
        }
    }

    public void Remove(ulong address)
    {
        lock (_gate)
        {
            if (!_nodes.Remove(address, out var node)) return;
            Volatile.Write(ref ((long*)node)[1], 0);
            _free.Push(node);
        }
    }
}
