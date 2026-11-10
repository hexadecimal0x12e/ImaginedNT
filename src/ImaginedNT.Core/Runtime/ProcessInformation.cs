using ImaginedNT.Core.Memory;

namespace ImaginedNT.Core.Runtime;

/// <summary>来宾可见的进程快照，包括不执行来宾代码的 System 进程。</summary>
public sealed class ProcessInformation
{
    public const int SystemProcessId = 4;
    public const string SystemImagePath = @"C:\Windows\System32\ntoskrnl.exe";

    public int Id { get; }
    public int ParentId { get; }
    public string Name { get; }
    public string ImagePath { get; }
    public string CommandLine { get; }
    public DateTimeOffset StartTime { get; }
    public int? ExitCode { get; }
    public int HandleCount { get; }
    public int Priority => 8;
    public bool IsSystem => Id == SystemProcessId;
    public IReadOnlyList<ImaginedThread> Threads { get; }
    public RuntimeMemorySnapshot Memory { get; }

    public ProcessInformation(ImaginedProcess process)
    {
        Id = process.Id;
        ParentId = process.ParentId ?? 0;
        Name = Path.GetFileName(process.ImagePath);
        ImagePath = process.ImagePath;
        CommandLine = process.CommandLine;
        StartTime = process.StartTime;
        ExitCode = process.ExitCode;
        HandleCount = process.Handles.Count;
        Threads = process.Threads.Where(t => t.State is ThreadState.Created or ThreadState.Running).ToArray();
        Memory = process.Runtime.Memory.GetSnapshot(Id);
    }

    internal ProcessInformation(ImaginedRuntime runtime, DateTimeOffset startTime)
    {
        Id = SystemProcessId;
        Name = "System";
        ImagePath = SystemImagePath;
        CommandLine = SystemImagePath;
        StartTime = startTime;
        Threads = [];
        Memory = runtime.Memory.GetSystemSnapshot();
    }
}
