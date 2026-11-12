namespace ImaginedNT.Core.Runtime;

public enum SessionState
{
    Active = 0,
    Connected = 1,
    Disconnected = 4
}

/// <summary>运行时内的 NT 会话快照；不关联宿主 Windows 会话。</summary>
public sealed record SessionInformation
{
    public uint Id { get; init; }
    public required string Name { get; init; }
    public SessionState State { get; init; }
    public DateTimeOffset StartTime { get; init; } = DateTimeOffset.UtcNow;
    public string ObjectDirectory => $@"\Sessions\{Id}";
    public string BaseNamedObjectsPath => Id == 0 ? @"\BaseNamedObjects" : $@"\Sessions\{Id}\BaseNamedObjects";
}
