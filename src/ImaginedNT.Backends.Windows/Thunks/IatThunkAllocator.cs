using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ImaginedNT.Core.Diagnostics;
using ImaginedNT.Core.Native;

namespace ImaginedNT.Core.Thunks;

/// <summary>
/// Owns executable pages containing universal dispatcher stubs, the guest-thread launcher,
/// a shared register-restore epilogue, one per-thread exit trampoline, and one 16-byte entry stub
/// per registered API.  Every resolved managed import points its IAT slot at a stub.
/// </summary>
public sealed unsafe class IatThunkAllocator : IDisposable
{
    public const int PageSize = 0x10000;
    public const int LauncherOffset = 0x100;
    public const int RestoreStubOffset = 0x300;
    public const int ApiStubsOffset = 0x400;
    public const int StubSize = 16;
    public const int ThreadTrampolineOffset = 0x8000;
    // Initial-page capacity; additional API pages are allocated on demand.
    public const int MaxApis = (ThreadTrampolineOffset - ApiStubsOffset) / StubSize;
    public const int ThreadTrampolineSize = 32;
    public const int MaxThreadTrampolines = (PageSize - ThreadTrampolineOffset) / ThreadTrampolineSize;
    private const int ApiStubsPerPage = (PageSize - ApiStubsOffset) / StubSize;

    public delegate ulong ThunkHandler(NativeCallFrame* frame);

    private sealed class Entry
    {
        public required ThunkHandler Handler { get; init; }
        public required string Name { get; init; }
        public required TraceLog? Trace { get; init; }
        public required IatThunkAllocator Owner { get; init; }
    }

    private static readonly ConcurrentDictionary<ulong, Entry> Entries = new();
    private static long _nextApiId;

    private readonly List<ulong> _ownedApiIds = new();
    private readonly object _gate = new();
    private readonly TraceLog? _trace;
    private readonly ulong _pageAddress;
    private readonly List<ulong> _apiPages = new();
    private ulong _apiPageAddress;
    private readonly ulong _launcherAddress;
    private readonly ulong _restoreStubAddress;
    private int _nextStub;
    private int _nextThreadTrampoline;
    private readonly List<ulong> _nativePages = new();
    private int _nextNativeOffset = PageSize;
    private ulong _lazyFailureThunk;
    private bool _disposed;

    public IatThunkAllocator(TraceLog? trace)
    {
        _trace = trace;
        var page = NativeMethods.VirtualAlloc(IntPtr.Zero, (UIntPtr)PageSize,
            NativeMethods.MEM_RESERVE | NativeMethods.MEM_COMMIT, NativeMethods.PAGE_EXECUTE_READWRITE);
        if (page == IntPtr.Zero)
            throw new OutOfMemoryException($"IAT thunk page allocation failed (err={Marshal.GetLastWin32Error()})");

        _pageAddress = (ulong)(long)page;
        _apiPageAddress = _pageAddress;

        delegate* unmanaged<ulong, NativeCallFrame*, ulong> dispatcher = &Dispatch;
        var stub = X64StubEmitter.EmitUniversalStub((ulong)(nint)dispatcher);
        if (stub.Length > LauncherOffset)
            throw new InvalidOperationException($"universal stub ({stub.Length} bytes) overlaps the guest launcher at 0x{LauncherOffset:X}");
        fixed (byte* p = stub)
        {
            Buffer.MemoryCopy(p, (void*)(long)_pageAddress, PageSize, stub.Length);
        }

        var launcher = X64StubEmitter.EmitGuestLauncher();
        if (launcher.Length > RestoreStubOffset - LauncherOffset)
            throw new InvalidOperationException("guest launcher overlaps the restore stub");
        _launcherAddress = _pageAddress + LauncherOffset;
        fixed (byte* p = launcher)
        {
            Buffer.MemoryCopy(p, (void*)(long)_launcherAddress, PageSize, launcher.Length);
        }
        NativeMethods.FlushInstructionCache(NativeMethods.GetCurrentProcess(),
            (IntPtr)(long)_launcherAddress, (UIntPtr)launcher.Length);

        var restore = X64StubEmitter.EmitThreadRestoreStub();
        if (restore.Length > ApiStubsOffset - RestoreStubOffset)
            throw new InvalidOperationException("thread restore stub overlaps the API stub arena");
        _restoreStubAddress = _pageAddress + RestoreStubOffset;
        fixed (byte* p = restore)
        {
            Buffer.MemoryCopy(p, (void*)(long)_restoreStubAddress, PageSize, restore.Length);
        }
        NativeMethods.FlushInstructionCache(NativeMethods.GetCurrentProcess(),
            (IntPtr)(long)_restoreStubAddress, (UIntPtr)restore.Length);

        _nextStub = 0;
        _trace?.Emit(TraceKind.Info,
            $"IAT thunk arena at 0x{_pageAddress:X16} (dispatcher 0x{(ulong)(nint)dispatcher:X16} launcher 0x{_launcherAddress:X16} restore 0x{_restoreStubAddress:X16})");
    }

    public ulong BaseAddress => _pageAddress;
    public ulong LauncherAddress => _launcherAddress;
    public int RegisteredCount => _ownedApiIds.Count;

    /// <summary>Allocate a per-thread trampoline that restores the launcher's stack frame.</summary>
    public ulong AllocateThreadExitTrampoline(ulong contextAddress)
    {
        lock (_gate)
        {
            if (_disposed || contextAddress == 0) return 0;
            if (_nextThreadTrampoline >= MaxThreadTrampolines) return 0;
            var address = _pageAddress + ThreadTrampolineOffset + (ulong)(_nextThreadTrampoline++ * ThreadTrampolineSize);
            var trampoline = X64StubEmitter.EmitThreadExitTrampoline(address, contextAddress, _restoreStubAddress);
            fixed (byte* p = trampoline)
            {
                Buffer.MemoryCopy(p, (void*)(long)address, ThreadTrampolineSize, trampoline.Length);
            }
            NativeMethods.FlushInstructionCache(NativeMethods.GetCurrentProcess(),
                (IntPtr)(long)address, (UIntPtr)trampoline.Length);
            return address;
        }
    }

    /// <summary>Register a managed implementation and return the IAT address that points at it.</summary>
    public ulong Register(ThunkHandler handler, string name)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_nextStub >= (_apiPages.Count == 0 ? MaxApis : ApiStubsPerPage))
            {
                var page = NativeMethods.VirtualAlloc(IntPtr.Zero, (UIntPtr)PageSize,
                    NativeMethods.MEM_RESERVE | NativeMethods.MEM_COMMIT, NativeMethods.PAGE_EXECUTE_READWRITE);
                if (page == IntPtr.Zero)
                    throw new OutOfMemoryException($"IAT thunk page allocation failed after {_ownedApiIds.Count} APIs (err={Marshal.GetLastWin32Error()})");

                // Each page needs a local dispatcher to keep API jumps within rel32 range.
                Buffer.MemoryCopy((void*)(long)_pageAddress, (void*)page, PageSize, LauncherOffset);
                NativeMethods.FlushInstructionCache(NativeMethods.GetCurrentProcess(), page, (UIntPtr)LauncherOffset);
                _apiPageAddress = (ulong)(long)page;
                _apiPages.Add(_apiPageAddress);
                _nextStub = 0;
                _trace?.Info($"IAT thunk arena expanded at 0x{_apiPageAddress:X16} (capacity={MaxApis + (long)_apiPages.Count * ApiStubsPerPage})");
            }
            var apiId = (ulong)Interlocked.Increment(ref _nextApiId);
            var offset = ApiStubsOffset + _nextStub * StubSize;
            var stubAddress = _apiPageAddress + (ulong)offset;
            var bytes = X64StubEmitter.EmitApiStub(apiId, stubAddress, _apiPageAddress);
            fixed (byte* p = bytes)
            {
                Buffer.MemoryCopy(p, (void*)(long)stubAddress, StubSize, bytes.Length);
            }

            NativeMethods.FlushInstructionCache(NativeMethods.GetCurrentProcess(), (IntPtr)(long)stubAddress, (UIntPtr)StubSize);
            Entries[apiId] = new Entry { Handler = handler, Name = name, Trace = _trace, Owner = this };
            _ownedApiIds.Add(apiId);
            _nextStub++;
            return (ulong)stubAddress;
        }
    }

    public ulong RegisterNativeForwarder(ulong target, ThunkHandler observer, string name)
    {
        // DLL exports can also be data; replacing those addresses with code corrupts callers.
        if (NativeMethods.VirtualQuery((IntPtr)(long)target, out var info,
                (UIntPtr)sizeof(NativeMethods.MEMORY_BASIC_INFORMATION)) == UIntPtr.Zero ||
            (info.Protect & 0xF0) == 0)
            return target;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var address = AllocateForwarderSlot();
            WriteForwarder(address, X64StubEmitter.EmitNativeForwarder(Register(observer, name), target));
            return address;
        }
    }

    public ulong RegisterLazyForwarder(Func<ulong> resolver, string name)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_lazyFailureThunk == 0)
                _lazyFailureThunk = Register(_ => Nt.NtStatus.EntrypointNotFound, "failed delay import");
            var address = AllocateForwarderSlot();
            var callback = Register(_ =>
            {
                ulong target;
                try
                {
                    target = resolver();
                    if (target == 0) throw new EntryPointNotFoundException(name);
                }
                catch (Exception ex)
                {
                    _trace?.Error($"delay import {name} failed: {ex}");
                    target = _lazyFailureThunk;
                }
                Interlocked.Exchange(ref *(long*)(long)(address + 8), (long)target);
                return 0;
            }, $"delay:{name}");
            WriteForwarder(address, X64StubEmitter.EmitLazyForwarder(callback, address));
            return address;
        }
    }

    public ulong RegisterEntryStub(byte[] code)
    {
        if (code.Length is 0 or > 256)
            throw new ArgumentException("entry stub must contain 1 to 256 bytes", nameof(code));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var address = AllocateForwarderSlot();
            WriteForwarder(address, code);
            return address;
        }
    }

    private ulong AllocateForwarderSlot()
    {
        if (_nextNativeOffset + 256 > PageSize)
        {
            var page = NativeMethods.VirtualAlloc(IntPtr.Zero, (UIntPtr)PageSize,
                NativeMethods.MEM_RESERVE | NativeMethods.MEM_COMMIT, NativeMethods.PAGE_EXECUTE_READWRITE);
            if (page == IntPtr.Zero) throw new OutOfMemoryException($"forwarding thunk allocation failed (err={Marshal.GetLastWin32Error()})");
            _nativePages.Add((ulong)(long)page);
            _nextNativeOffset = 0;
        }
        var address = _nativePages[^1] + (ulong)_nextNativeOffset;
        _nextNativeOffset += 256;
        return address;
    }

    private static void WriteForwarder(ulong address, byte[] bytes)
    {
        if (bytes.Length > 256) throw new InvalidOperationException("forwarding thunk exceeds its slot");
        fixed (byte* p = bytes)
            Buffer.MemoryCopy(p, (void*)(long)address, 256, bytes.Length);
        NativeMethods.FlushInstructionCache(NativeMethods.GetCurrentProcess(), (IntPtr)(long)address, (UIntPtr)bytes.Length);
    }

    [UnmanagedCallersOnly]
    private static ulong Dispatch(ulong apiId, NativeCallFrame* frame)
    {
        if (!Entries.TryGetValue(apiId, out var entry))
        {
            return Nt.NtStatus.NotImplemented;
        }

        try
        {
            return entry.Handler(frame);
        }
        catch (Exception ex)
        {
            entry.Trace?.Error($"managed API {entry.Name} threw {ex.GetType().Name}: {ex.Message}");
            return Nt.NtStatus.NotImplemented;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var id in _ownedApiIds) Entries.TryRemove(id, out _);
            _ownedApiIds.Clear();
            foreach (var page in _apiPages)
                NativeMethods.VirtualFree((IntPtr)(long)page, UIntPtr.Zero, NativeMethods.MEM_RELEASE);
            _apiPages.Clear();
            foreach (var page in _nativePages)
                NativeMethods.VirtualFree((IntPtr)(long)page, UIntPtr.Zero, NativeMethods.MEM_RELEASE);
            _nativePages.Clear();
            if (_pageAddress != 0)
            {
                NativeMethods.VirtualFree((IntPtr)(long)_pageAddress, UIntPtr.Zero, NativeMethods.MEM_RELEASE);
            }
        }
    }
}
