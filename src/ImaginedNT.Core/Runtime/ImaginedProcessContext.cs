using ImaginedNT.Core.Io;
using ImaginedNT.Core.Memory;
using ImaginedNT.Core.Objects;

namespace ImaginedNT.Core.Runtime;

/// <summary>托管进程入口使用的运行时上下文；异步入口返回前，进程资源保持有效。</summary>
public sealed class ImaginedProcessContext
{
    internal ImaginedProcessContext(ImaginedThread thread) => Thread = thread;

    public ImaginedThread Thread { get; }
    public ImaginedProcess Process => Thread.Process;
    public ImaginedRuntime Runtime => Process.Runtime;
    public Security.AccessToken Token => Thread.EffectiveToken;
    public VirtualMemoryManager Memory => Process.Memory;
    public GuestHeap Heap => Process.Heap;
    public HandleTable Handles => Process.Handles;
    public SandboxFileSystem FileSystem => Process.FileSystem;
    public ObjectManager Objects => Process.ObjectManager;
}
