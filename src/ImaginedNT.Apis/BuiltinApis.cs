using System.Reflection;
using System.Text;
using System.Text.Json;
using ImaginedNT.Core.Nt;

namespace ImaginedNT.Core.Runtime;

public static class BuiltinApis
{
    public static IReadOnlyList<Assembly> Assemblies { get; } = Array.AsReadOnly(new[]
    {
        typeof(BuiltinApis).Assembly,
        typeof(ImaginedNT.Apis.Com.ComApis).Assembly,
        typeof(ImaginedNT.Apis.Crt.CrtStringApis).Assembly,
        typeof(SystemApis).Assembly,
        typeof(PackageApis).Assembly,
        typeof(FileApis).Assembly,
        typeof(MemoryApis).Assembly,
        typeof(ProcessApis).Assembly,
        typeof(ImaginedRegistry).Assembly,
        typeof(SynchronizationApis).Assembly,
    });

    public static ImaginedRuntime CreateRuntime(RuntimeOptions? options = null) =>
        new(options, Assemblies);

    public static void Register(ImaginedRuntime runtime)
    {
        foreach (var assembly in Assemblies) runtime.RegisterApis(assembly);
    }

    [VirtualApi("imaginednt.dll", "IsImaginedNT", ApiCategory.System)]
    private static ulong IsImaginedNT(ApiCallContext ctx) => 1;

    [VirtualApi("imaginednt.dll", "GetRuntimeVersionW", ApiCategory.System)]
    private static ulong GetRuntimeVersionW(ApiCallContext ctx) => WriteString(ctx,
        typeof(ImaginedRuntime).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(ImaginedRuntime).Assembly.GetName().Version?.ToString() ?? "0.0.0.0");

    [VirtualApi("imaginednt.dll", "GetRuntimeConfigurationW", ApiCategory.System)]
    private static ulong GetRuntimeConfigurationW(ApiCallContext ctx)
    {
        var o = ctx.Runtime.Options;
        return WriteString(ctx, JsonSerializer.Serialize(new
        {
            SandboxRoot = ctx.Process.FileSystem.Root,
            o.TraceFile,
            o.TraceToConsole,
            o.ExecuteImage,
            o.InterceptSyscalls,
            TerminationMode = o.TerminationMode.ToString(),
            ImportPolicy = o.ImportPolicy.ToString(),
            NativeApiPolicy = o.NativeApiPolicy.ToString(),
            o.FailOnUnresolvedImport,
            ExecutionTimeoutMilliseconds = o.ExecutionTimeout.TotalMilliseconds,
            o.LimitJobObject,
            o.JobMemoryLimitBytes,
            o.MaximumPhysicalMemoryBytes,
            o.MaximumVirtualMemoryBytes,
            o.JobActiveProcessLimit,
            o.AllowNativeForwarding,
            o.AllowUnimplementedApiForwarding,
            o.AllowFileWrites,
            o.AllowProcessCreation,
            o.IsPackaged,
            PackageIdentity = ctx.Process.PackageIdentity,
            o.TraceApiArguments,
            o.ApplySectionProtections
        }));
    }

    [VirtualApi("imaginednt.dll", "GetSandboxRootW", ApiCategory.System)]
    private static ulong GetSandboxRootW(ApiCallContext ctx) => WriteString(ctx, ctx.Process.FileSystem.Root);

    // String APIs return the required UTF-16 capacity, including the terminator, without truncation.
    private static ulong WriteString(ApiCallContext ctx, string value)
    {
        var required = (ulong)value.Length + 1;
        if (ctx.Arg32(1) == 0) return required;
        if (ctx.Arg(0) == 0)
        {
            ctx.SetLastError(Win32Error.InvalidParameter);
            return 0;
        }
        if (ctx.Arg32(1) < required)
        {
            ctx.SetLastError(122); // ERROR_INSUFFICIENT_BUFFER
            return required;
        }
        ctx.WriteBytes(ctx.Arg(0), Encoding.Unicode.GetBytes(value));
        ctx.Write<ushort>(ctx.Arg(0) + (ulong)value.Length * 2, 0);
        return required;
    }
}
