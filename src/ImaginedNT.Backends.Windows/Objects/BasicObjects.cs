using ImaginedNT.Core.Nt;

namespace ImaginedNT.Core.Objects;

public sealed class DirectoryObject : NtObject
{
    public override ObjectType ObjectType => ObjectType.Directory;
}

public sealed class SymbolicLinkObject : NtObject
{
    public required string Target { get; init; }
    public override ObjectType ObjectType => ObjectType.SymbolicLink;
    public override string? Info => Target;
}

public sealed class EventObject : NtObject
{
    private readonly object _gate = new();
    private EventWaitHandle? _event;
    private bool _signaled;

    public EventObject(bool manualReset, bool initialState, string? name = null)
    {
        ManualReset = manualReset;
        Name = name;
        _signaled = initialState;
    }

    public bool ManualReset { get; }
    public override ObjectType ObjectType => ObjectType.Event;
    public override bool SupportsWait => true;
    private EventWaitHandle GetEvent()
    {
        lock (_gate) return _event ??= new EventWaitHandle(_signaled,
            ManualReset ? EventResetMode.ManualReset : EventResetMode.AutoReset);
    }

    public override nint GetNativeHandle() => GetEvent().SafeWaitHandle.DangerousGetHandle();
    public override bool IsSignaled
    {
        get
        {
            lock (_gate)
            {
                if (_event is null) return _signaled;
                var status = NtQueryEvent(GetNativeHandle(), 0, out var info, 8, out _);
                return NtStatus.IsSuccess(status) && info.State != 0;
            }
        }
    }

    public bool Set()
    {
        lock (_gate)
        {
            var previous = IsSignaled;
            _signaled = true;
            _event?.Set();
            return previous;
        }
    }

    public bool Reset()
    {
        lock (_gate)
        {
            var previous = IsSignaled;
            _signaled = false;
            _event?.Reset();
            return previous;
        }
    }

    public bool Pulse()
    {
        lock (_gate)
        {
            var previous = IsSignaled;
            _event?.Set();
            _signaled = false;
            _event?.Reset();
            return previous;
        }
    }

    public uint Wait(uint timeoutMilliseconds) =>
        GetEvent().WaitOne(timeoutMilliseconds == uint.MaxValue ? Timeout.Infinite :
            (int)Math.Min(timeoutMilliseconds, int.MaxValue)) ? NtStatus.Wait0 : NtStatus.WaitTimeout;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct EventInformation
    {
        public int Type;
        public int State;
    }

    [System.Runtime.InteropServices.DllImport("ntdll.dll")]
    private static extern uint NtQueryEvent(nint handle, int informationClass,
        out EventInformation info, uint length, out uint returnedLength);

    protected override void OnDispose() => _event?.Dispose();
}

/// <summary>A recursive mutex with NT mutant semantics (owner thread and recursion count).</summary>
public sealed class MutantObject : NtObject
{
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _available = new(true);
    private int _ownerThreadId;
    private int _recursion;
    private bool _abandoned;

    public MutantObject(bool ownedByCaller, int ownerThreadId = 0, string? name = null)
    {
        Name = name;
        if (ownedByCaller)
        {
            _ownerThreadId = ownerThreadId;
            _recursion = 1;
            _available.Reset();
        }
    }

    public override ObjectType ObjectType => ObjectType.Mutant;
    public override bool SupportsWait => true;
    public override bool IsSignaled
    {
        get { lock (_gate) return _ownerThreadId == 0; }
    }

    public int OwnerThreadId { get { lock (_gate) return _ownerThreadId; } }
    public int RecursionCount { get { lock (_gate) return _recursion; } }

    public uint Wait(int threadId, uint timeoutMilliseconds)
    {
        lock (_gate)
        {
            if (_ownerThreadId == threadId)
            {
                _recursion++;
                return NtStatus.Wait0;
            }
            if (_ownerThreadId == 0)
            {
                _ownerThreadId = threadId;
                _recursion = 1;
                _available.Reset();
                return NtStatus.Wait0;
            }
        }

        if (!_available.Wait((int)Math.Min(timeoutMilliseconds, int.MaxValue))) return NtStatus.WaitTimeout;
        lock (_gate)
        {
            _ownerThreadId = threadId;
            _recursion = 1;
            _available.Reset();
            _abandoned = false;
        }
        return NtStatus.Wait0;
    }

    public uint Release(int threadId)
    {
        lock (_gate)
        {
            if (_ownerThreadId != threadId) return NtStatus.AccessDenied;
            _recursion--;
            if (_recursion > 0) return NtStatus.Success;
            _ownerThreadId = 0;
            _recursion = 0;
            _available.Set();
            return NtStatus.Success;
        }
    }

    public void Abandon(int threadId)
    {
        lock (_gate)
        {
            if (_ownerThreadId != threadId) return;
            _ownerThreadId = 0;
            _recursion = 0;
            _abandoned = true;
            _available.Set();
        }
    }

    public bool ConsumeAbandoned()
    {
        lock (_gate)
        {
            var value = _abandoned;
            _abandoned = false;
            return value;
        }
    }

    protected override void OnDispose() => _available.Dispose();
}

public sealed class SemaphoreObject : NtObject
{
    private readonly SemaphoreSlim _semaphore;
    private readonly int _maximum;

    public SemaphoreObject(int initial, int maximum, string? name = null)
    {
        if (maximum <= 0 || initial < 0 || initial > maximum) throw new ArgumentOutOfRangeException(nameof(initial));
        _maximum = maximum;
        Name = name;
        _semaphore = new SemaphoreSlim(initial, maximum);
    }

    public override ObjectType ObjectType => ObjectType.Semaphore;
    public override bool SupportsWait => true;
    public int CurrentCount => _semaphore.CurrentCount;
    public override bool IsSignaled => _semaphore.CurrentCount > 0;

    public uint Wait(uint timeoutMilliseconds) =>
        _semaphore.Wait((int)Math.Min(timeoutMilliseconds, int.MaxValue)) ? NtStatus.Wait0 : NtStatus.WaitTimeout;

    public uint Release(int count, out int previous)
    {
        previous = _semaphore.CurrentCount;
        try
        {
            _semaphore.Release(count);
            return NtStatus.Success;
        }
        catch (SemaphoreFullException)
        {
            return NtStatus.InvalidParameter;
        }
    }

    protected override void OnDispose() => _semaphore.Dispose();
}
