using System.Reflection;

namespace ImaginedNT.Core.Runtime;

/// <summary>根来宾进程调用 ExitProcess 时的终止方式。</summary>
public enum ProcessTerminationMode
{
    /// <summary>终止整个 ImaginedNT 宿主进程。</summary>
    HostExit,
    /// <summary>仅终止调用 ExitProcess 的线程，适用于宿主内运行。</summary>
    ThreadExit,
    /// <summary>记录退出码后返回来宾代码，仅供测试使用，不符合真实应用的退出语义。</summary>
    ReturnToHost
}

/// <summary>解析来宾导入函数时，托管实现与本机导出的选择策略。</summary>
public enum ImportPolicy
{
    /// <summary>优先使用托管实现，缺失时尝试转发到本机 DLL。</summary>
    ManagedFirst,
    /// <summary>仅使用托管实现，不尝试本机转发。</summary>
    ManagedOnly,
    /// <summary>优先使用本机导出，缺失时回退到托管实现；DLL 加载、符号查询和 Partially Imagined 实现仍由运行时处理。</summary>
    NativeFirst
}

/// <summary>本机转发是否只允许声明表中的导出。</summary>
public enum NativeApiPolicy
{
    /// <summary>允许转发任何可解析的本机导出，保留原有行为。</summary>
    AllowAll,
    /// <summary>只允许转发通过 <see cref="PartiallyImaginedNativeApiAttribute"/> 声明的本机导出。</summary>
    DeclaredOnly
}

/// <summary>
/// 描述待创建的来宾进程；<see cref="ImagePath"/> 应由调用方预先解析为宿主文件路径。
/// </summary>
public sealed class ProcessCreateOptions
{
    /// <summary>来宾可执行映像的宿主文件路径，通常由父进程的沙箱解析得到。</summary>
    public required string ImagePath { get; init; }

    /// <summary>来宾命令行；未指定时使用加双引号的映像完整路径。</summary>
    public string? CommandLine { get; init; }

    /// <summary>来宾当前目录；未指定时继承父进程目录，无父进程时使用来宾根目录。</summary>
    public string? CurrentDirectory { get; init; }

    /// <summary>沙箱根目录的宿主路径；未指定时继承父进程沙箱，否则使用运行时的有效沙箱根目录。</summary>
    public string? SandboxRoot { get; init; }

    /// <summary>同一运行时内父来宾进程的 ID；未指定时不关联父进程。</summary>
    public int? ParentId { get; init; }

    /// <summary>是否作为根进程；未指定时，仅在运行时尚无来宾进程的情况下作为根进程。</summary>
    public bool? IsRoot { get; init; }

    /// <summary>来宾环境变量；未指定时继承父进程环境，无父进程时由运行时初始化。</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }
}

/// <summary>ImaginedNT 运行时及其来宾进程的配置。</summary>
public sealed class RuntimeOptions
{
    /// <summary>是否为来宾模拟 MSIX 包身份，默认无包身份。</summary>
    public bool IsPackaged { get; set; }

    public PackageIdentity PackageIdentity { get; set; } = new();

    /// <summary>使用运行时跟踪日志创建本机 API 转发器的工厂；未指定时使用默认转发器。</summary>
    public Func<Diagnostics.TraceLog, INativeForwarder>? NativeForwarderFactory { get; set; }

    /// <summary>默认沙箱根目录的宿主路径；未指定时在当前宿主目录下按来宾进程 ID 创建沙箱路径。</summary>
    public string? SandboxRoot { get; set; }

    /// <summary>来宾盘符到宿主目录及模拟卷信息的映射；C: 默认使用 SandboxRoot。</summary>
    public Dictionary<char, Io.VirtualDriveOptions> Drives { get; set; } = new();

    /// <summary>来宾标准输出流，默认为宿主标准输出。</summary>
    public Stream StandardOutput { get; set; } = Console.OpenStandardOutput();

    /// <summary>来宾标准错误流，默认为宿主标准错误。</summary>
    public Stream StandardError { get; set; } = Console.OpenStandardError();

    /// <summary>来宾标准输入流，默认为宿主标准输入。</summary>
    public Stream StandardInput { get; set; } = new Io.ConsoleInputStream();

    /// <summary>启用控制台跟踪时的诊断输出目标；默认为宿主标准错误，设为 null 时仍回退到该目标。</summary>
    public TextWriter? DiagnosticWriter { get; set; } = Console.Error;

    /// <summary>跟踪日志文件路径；未指定时不写入日志文件。</summary>
    public string? TraceFile { get; set; }

    /// <summary>是否向诊断输出目标写入跟踪日志，默认为 false。</summary>
    public bool TraceToConsole { get; set; }

    /// <summary>是否执行来宾映像，默认为 true；当前运行路径尚未使用此开关。</summary>
    public bool ExecuteImage { get; set; } = true;

    /// <summary>是否修补已加载映像中的直接系统调用指令以进行拦截，默认为 false。</summary>
    public bool InterceptSyscalls { get; set; }

    /// <summary>根来宾进程调用 ExitProcess 时的终止方式，默认终止整个宿主进程。</summary>
    public ProcessTerminationMode TerminationMode { get; set; } = ProcessTerminationMode.HostExit;

    /// <summary>导入函数解析策略，默认优先使用托管实现。</summary>
    public ImportPolicy ImportPolicy { get; set; } = ImportPolicy.ManagedFirst;

    /// <summary>按 DLL 文件名重定向到另一个 DLL 名称或宿主路径；在创建运行时时读取。</summary>
    public Dictionary<string, string> DllRedirects { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 注册为进程内 COM 服务器的 DLL（来宾沙箱路径或宿主路径）。服务器在首次解析 CLSID 时
    /// 惰性加载，并通过其 <c>DllGetClassObject</c>/<c>DllCanUnloadNow</c> 导出参与
    /// <c>CoGetClassObject</c> 与 <c>CoFreeUnusedLibraries</c>。
    /// </summary>
    public List<string> ComServerPaths { get; } = new();

    /// <summary>是否在导入无法解析时使加载失败；默认为 false，此时生成返回入口点未找到状态的占位函数。</summary>
    public bool FailOnUnresolvedImport { get; set; }

    /// <summary>等待来宾主线程结束的默认超时时间，默认无限；超时后终止该来宾进程的线程。</summary>
    public TimeSpan ExecutionTimeout { get; set; } = Timeout.InfiniteTimeSpan;

    /// <summary>是否在创建根来宾进程时，将宿主加入限制内存和活动进程数的 Windows 作业对象，默认为 false。</summary>
    public bool LimitJobObject { get; set; }

    /// <summary>启用作业对象限制时，每个宿主进程的内存上限，单位为字节，默认为 512 MiB。</summary>
    public ulong JobMemoryLimitBytes { get; set; } = 512UL * 1024 * 1024;

    /// <summary>同一运行时内所有来宾的已提交内存额度（字节），不包含宿主开销；0 不限制。</summary>
    public ulong MaximumPhysicalMemoryBytes { get; set; }

    /// <summary>同一运行时内所有来宾的虚拟内存预留额度（字节），不包含宿主预留空间；0 不限制。</summary>
    public ulong MaximumVirtualMemoryBytes { get; set; }

    /// <summary>启用作业对象限制时，作业中的最大活动进程数，默认为 1；设为 0 时仍按 1 处理。</summary>
    public uint JobActiveProcessLimit { get; set; } = 1;

    /// <summary>是否允许将 API 调用转发到宿主本机 DLL，默认为 true，仍受导入解析策略约束。</summary>
    public bool AllowNativeForwarding { get; set; } = true;

    /// <summary>
    /// 本机转发的声明策略，默认为 <see cref="NativeApiPolicy.AllowAll"/>。
    /// 设为 <see cref="NativeApiPolicy.DeclaredOnly"/> 时，只有通过
    /// <see cref="PartiallyImaginedNativeApiAttribute"/> 声明的模块/导出才允许转发；序数导入没有名称，默认拒绝。
    /// </summary>
    public NativeApiPolicy NativeApiPolicy { get; set; } = NativeApiPolicy.AllowAll;

    /// <summary>是否将受支持的本机 API 返回的句柄包装为来宾句柄表中的句柄，默认为 false。</summary>
    public bool WrapNativeHandles { get; set; }

    /// <summary>是否允许为托管来宾句柄创建本机表示，以供受支持的本机 API 使用，默认为 false。</summary>
    public bool ExpandManagedHandles { get; set; }

    /// <summary>是否允许通过托管沙箱文件系统执行写操作，默认为 true。</summary>
    public bool AllowFileWrites { get; set; } = true;

    /// <summary>
    /// 是否允许来宾通过 CreateProcessW/A 或 WinExec 创建更多来宾进程，默认为 true。
    /// 这些进程仍与宿主共享地址空间，此选项仅控制策略，不构成隔离边界。
    /// </summary>
    public bool AllowProcessCreation { get; set; } = true;

    /// <summary>
    /// 来宾通过 <c>GetVersion</c>/<c>GetVersionEx</c>/<c>RtlGetVersion</c> 看到的 Windows 版本。
    /// 为 null 时报告宿主的真实版本（不经过宿主进程 manifest 的兼容性版本谎言）；设置后可用于
    /// 模拟指定版本，例如 <c>new Version(10, 0, 19045, 0)</c>。
    /// </summary>
    public Version? SimulatedWindowsVersion { get; set; }

    /// <summary>
    /// 覆盖来宾通过 <c>%WINDOWS_COPYRIGHT%</c> 等 brand 字符串看到的版权文本；为 null 时使用宿主
    /// winbrand 的正常结果。
    /// </summary>
    public string? CopyrightText { get; set; }

    /// <summary>
    /// 覆盖 brand 字符串中的 Windows 产品名（<c>%WINDOWS_LONG%</c>/<c>%WINDOWS_SHORT%</c>）；
    /// 为 null 时使用基于 <see cref="SimulatedWindowsVersion"/> 的默认名称。
    /// </summary>
    public string? ProductName { get; set; } = "ImaginedNT Simulator";

    /// <summary>
    /// 按名字导入系统 DLL（kernel32、ntdll、msvcrt 等）时优先使用宿主 Windows 的
    /// System32 版本，而不是沙盒中的同名 DLL。用于运行 ReactOS 等自带系统 DLL 的 PE，
    /// 避免执行未初始化的来宾 ntdll/kernel32；沙盒中不存在的 DLL 仍会回退到沙盒。
    /// </summary>
    public bool PreferHostSystemDlls { get; set; }

    /// <summary>是否在 API 调用日志中记录参数，默认为 true。</summary>
    public bool TraceApiArguments { get; set; } = true;

    /// <summary>
    /// 是否记录每一次本机转发调用（参数、调用者返回地址），默认为 false。用于诊断本机 DLL
    /// 内部阻塞或返回错误的情况；输出量较大。也可由 <c>--trace-native</c> 启用。
    /// </summary>
    public bool TraceNativeCalls { get; set; }

    /// <summary>是否按 PE 节属性设置已映射内存的访问保护，默认为 true。</summary>
    public bool ApplySectionProtections { get; set; } = true;

    /// <summary>
    /// 允许在不禁用 CET 的宿主上运行 ImaginedNT 来宾进程，默认为 false。
    /// </summary>
    public bool AllowWithCET { get; set; } = false;

    /// <summary>
    /// 是否在根来宾进程调用 Environment.FailFast 时，包装为宿主异常并返回给宿主，而不是直接终止宿主进程，默认为 true。
    /// </summary>
    public bool WrapFailFast { get; set; } = true;

    /// <summary>获取指定来宾进程使用的默认沙箱根目录的宿主绝对路径，不创建目录。</summary>
    /// <param name="pid">用于生成默认沙箱目录名的来宾进程 ID。</param>
    /// <returns>配置的沙箱绝对路径；未配置时为当前宿主目录下的 sandbox/process-{pid}。</returns>
    public string EffectiveSandboxRoot(int pid)
    {
        if (!string.IsNullOrWhiteSpace(SandboxRoot)) return Path.GetFullPath(SandboxRoot);
        return Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "sandbox", $"process-{pid}"));
    }

    /// <summary>创建测试配置，使用唯一的临时沙箱路径、内存流和返回来宾代码的退出方式，并启用系统调用拦截。</summary>
    /// <param name="stdout">来宾标准输出流；未指定时创建内存流。</param>
    /// <returns>关闭控制台跟踪的测试运行时配置，其余选项保留默认值。</returns>
    public static RuntimeOptions ForTests(Stream? stdout = null) => new()
    {
        SandboxRoot = Path.Combine(Path.GetTempPath(), "imaginednt-tests", Guid.NewGuid().ToString("N")),
        StandardOutput = stdout ?? new MemoryStream(),
        StandardError = new MemoryStream(),
        StandardInput = new MemoryStream(),
        TerminationMode = ProcessTerminationMode.ReturnToHost,
        InterceptSyscalls = true,
        TraceToConsole = false,
        DiagnosticWriter = null,
        ApplySectionProtections = true
    };
}
