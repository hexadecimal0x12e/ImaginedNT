using ImaginedNT.Core.Nt;
using ImaginedNT.Core.Objects;

namespace ImaginedNT.Core.Runtime;

public sealed class ThreadObject : NtObject
{
    public ThreadObject(ImaginedThread thread, string? name = null)
    {
        Thread = thread;
        Name = name ?? $"thread:{thread.Id}";
    }

    public ImaginedThread Thread { get; }
    public override ObjectType ObjectType => ObjectType.Thread;
    public override bool SupportsWait => true;
    public override bool IsSignaled => Thread.State is ThreadState.Exited or ThreadState.Terminated;
    public override string? Info => Thread.ToString();

    public uint Wait(uint timeoutMilliseconds)
    {
        var timeout = timeoutMilliseconds == uint.MaxValue ? Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(timeoutMilliseconds);
        return Thread.Wait(timeout, out _) ? NtStatus.Wait0 : NtStatus.WaitTimeout;
    }
}

public sealed class ProcessObject : NtObject
{
    public ProcessObject(ImaginedProcess process, string? name = null)
    {
        Process = process;
        Name = name ?? $"process:{process.Id}";
    }

    public ImaginedProcess Process { get; }
    public override ObjectType ObjectType => ObjectType.Process;
    public override bool SupportsWait => true;
    public override bool IsSignaled => Process.IsTerminating;
    public override string? Info => $"pid={Process.Id} image={Process.ImagePath}";

    public uint Wait(uint timeoutMilliseconds)
    {
        var infinite = timeoutMilliseconds == uint.MaxValue;
        var milliseconds = infinite ? Timeout.Infinite : (int)Math.Min(timeoutMilliseconds, int.MaxValue);
        return Process.WaitForExit(milliseconds) ? NtStatus.Wait0 : NtStatus.WaitTimeout;
    }
}
