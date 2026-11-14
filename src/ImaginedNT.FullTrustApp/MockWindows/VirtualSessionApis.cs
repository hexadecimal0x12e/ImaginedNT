using System.Runtime.CompilerServices;
using System.Text;
using ImaginedNT.Core.Diagnostics;
using ImaginedNT.Core.Runtime;

namespace ImaginedNT.FullTrustApp.MockWindows;

internal static class VirtualSessionApis
{
    internal const string Module = "imagined.session.dll";
    internal static readonly ConditionalWeakTable<ImaginedRuntime, VirtualWindowsSystem> Systems = new();

    [VirtualApi(Module, "CsrInitializeSession", ApiCategory.Process)]
    private static ulong CsrInitializeSession(ApiCallContext c)
    {
        if (!Systems.TryGetValue(c.Runtime, out _) || !c.Process.IsImaginedProcess ||
            !Path.GetFileName(c.Process.ImagePath).Equals("csrss.exe", StringComparison.OrdinalIgnoreCase)) return 0;
        c.ObjectManager.EnsureDirectory($@"\Sessions\{c.Process.SessionId}\Windows\Subsystems\Windows");
        return 1;
    }

    internal static ulong Call(ImaginedProcessContext c, string module, string name, params ulong[] args)
    {
        if (!c.Runtime.Apis.TryGet(module, name, out var api) || api is null)
            throw new InvalidOperationException($"缺少虚拟 API：{module}!{name}");
        var result = api.Handler(new ApiCallContext(c.Process, c.Thread, module, name, 0, 0, args));
        c.Runtime.Trace.Emit(TraceKind.Api, $"{module}!{name} => 0x{result:X}", c.Process.Id, c.Thread.Id);
        return result;
    }

    internal static ulong CallWithName(ImaginedProcessContext c, string module, string name, string value, params ulong[] args)
    {
        var bytes = Encoding.Unicode.GetBytes(value + '\0');
        var address = c.Heap.Allocate((ulong)bytes.Length);
        if (address == 0) throw new OutOfMemoryException();
        try
        {
            c.Memory.WriteBytes(address, bytes);
            return Call(c, module, name, [address, .. args]);
        }
        finally { c.Heap.Free(address); }
    }
}
