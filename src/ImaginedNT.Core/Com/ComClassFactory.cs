namespace ImaginedNT.Core.Com;

/// <summary>
/// Managed <c>IClassFactory</c> returned by <c>CoGetClassObject</c>. It knows the CLSID of the
/// managed class it produces and participates in the runtime's lock/unload accounting.
/// </summary>
internal sealed class ManagedClassFactory
{
    private readonly ComRuntime _runtime;
    private readonly ComClassDescriptor _target;

    public ManagedClassFactory(ComRuntime runtime, ComClassDescriptor target)
    {
        _runtime = runtime;
        _target = target;
    }

    public ulong CreateInstance(ComCallContext context)
    {
        if (context.Arg(3) == 0) return ComHResult.AsReturnValue(ComHResult.E_POINTER);
        if (!ComMarshaling.TryReadGuid(context.Memory, context.Arg(2), out var iid))
            return ComHResult.AsReturnValue(ComHResult.E_INVALIDARG);

        var hr = _runtime.CreateInstance(context.Thread, _target.ClassId, context.Arg(1),
            (uint)ComClassContext.InprocServer, iid, out var pointer);
        if (ComHResult.Succeeded(hr)) context.TryWritePointer(3, pointer);
        return ComHResult.AsReturnValue(hr);
    }

    public ulong LockServer(ComCallContext context)
    {
        if (context.ArgBool(1)) Interlocked.Increment(ref _target.LockCount);
        else Interlocked.Decrement(ref _target.LockCount);
        return ComHResult.AsReturnValue(ComHResult.S_OK);
    }
}
