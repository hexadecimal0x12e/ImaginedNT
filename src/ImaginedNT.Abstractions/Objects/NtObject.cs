namespace ImaginedNT.Core.Objects;

public enum ObjectType
{
    Directory,
    SymbolicLink,
    Event,
    Mutant,
    Semaphore,
    Timer,
    File,
    Section,
    Process,
    Thread,
    Token,
    RegistryKey,
    Device,
    Driver,
    IoCompletion,
    KeyedEvent,
    Unknown
}

/// <summary>Base class for every object that can live in the NT object namespace or a handle table.</summary>
public abstract class NtObject : IDisposable
{
    private int _references = 1;
    private int _disposed;

    public abstract ObjectType ObjectType { get; }
    public virtual string TypeName => ObjectType.ToString();
    public string? Name { get; internal set; }
    public string? FullPath { get; internal set; }
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    public virtual bool SupportsWait => false;
    public virtual bool IsSignaled => false;
    public virtual string? Info => null;
    /// <summary>Borrowed host handle; zero when the object cannot be represented natively.</summary>
    public virtual nint GetNativeHandle() => 0;

    public int ReferenceCount => Volatile.Read(ref _references);
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public void AddRef() => Interlocked.Increment(ref _references);

    public void Release()
    {
        if (Interlocked.Decrement(ref _references) != 0) return;
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { OnDispose(); }
        catch { /* disposal must never escape into a native callback */ }
    }

    public void Dispose() => Release();

    protected virtual void OnDispose() { }

    public override string ToString() => $"{TypeName} '{FullPath ?? Name ?? "<unnamed>"}' refs={ReferenceCount}";
}
