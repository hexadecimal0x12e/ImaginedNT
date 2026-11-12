# Imagined Process

`ImaginedRuntime.CreateImaginedProcess` 创建一个运行托管方法的进程，不加载 PE。
`StartImaginedProcess` 同时启动该进程。两者均支持同步返回 `int` 或异步返回
`Task<int>` 的入口，接收 `ImaginedProcessContext` 和 `CancellationToken`。

```csharp
using ImaginedNT.Core.Runtime;

using var runtime = new ImaginedRuntime();
using var process = runtime.StartImaginedProcess(
    new ProcessCreateOptions { ImagePath = @"C:\Imagined\worker.exe" },
    async (context, cancellationToken) =>
    {
        // context 提供 Runtime、Process、Thread、Memory、Heap、Handles、FileSystem、Objects。
        await Task.Delay(1000, cancellationToken);
        return 42;
    });

process.WaitForExit(Timeout.Infinite);
Console.WriteLine(process.ExitCode); // 42
```

作为运行时根进程时，使用 `CreateRootImaginedProcess` 或 `StartRootImaginedProcess`：

```csharp
using var rootRuntime = new ImaginedRuntime();
using var root = rootRuntime.StartRootImaginedProcess(
    new ProcessCreateOptions { ImagePath = @"C:\Imagined\init.exe" },
    async (context, cancellationToken) =>
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return 0;
    });
```

根进程接口须在运行时尚无进程时调用；`ParentId` 必须为空，`IsRoot` 不可为 false。
创建后 `runtime.Process`、`runtime.PrimaryProcess` 均指向该进程，`root.IsRoot` 为 true。
同步与异步入口、取消及退出码语义与普通 Imagined Process 相同；显式启动不受
`RuntimeOptions.ExecuteImage` 限制。子进程可通过 `ProcessCreateOptions.ParentId = root.Id`
关联该根进程。

`ImagePath` 是用于进程枚举和显示的标识路径，无需存在对应文件。父进程、会话、桌面、
环境变量和沙箱配置沿用 `ProcessCreateOptions`。进程拥有正常的 PID、主线程及资源表，
现有进程枚举、进程句柄和等待 API 均可观察它。异步入口在 `await` 后仍可通过
`context.Runtime.GetCurrentThread()` 获取对应的虚拟主线程。

- 入口仍在执行（包括异步等待）时，进程保持运行，`ExitCode` 为 null。
- 入口正常返回时退出，返回的 `int` 成为进程退出码；退出时也会取消 Token。
- `process.Terminate(code)` 或终止主线程会取消入口收到的 Token，不提前发出退出通知。
  若入口捕获取消后正常返回，仍采用返回值；若取消传播为 `OperationCanceledException`，
  则采用首次终止请求的退出码。未处理的其他异常会记录日志并以失败状态退出。
- 入口忽略取消并继续执行时，进程继续存在，`CancellationRequested` 为 true。
- `Dispose` 请求取消；入口未完成时保留资源，实际完成后再释放。
  运行时销毁时也会保留仍被入口使用的共享资源。

只创建而不立即启动时，可调用 `process.EnsureMainThread().Start()`，或通过
`process.Run()` / `RunAsync()` 启动并等待。`Run` 超时会请求取消并返回
`TimedOut = true`，进程仍需等待入口完成后才真正退出。
进程退出后保留现有的退出快照行为，调用 `Dispose` 后从运行时注册表移除。

`RuntimeOptions.ExecuteImage = false` 时，会话启动仅初始化环境，不创建默认来宾进程；
环境持续运行直到持有方停止。`ImaginedRuntime.Run` 此时直接返回 0。
持有方仍可调用 `CreateProcess` 后 `LoadImage`、`Run`，或调用
`StartImaginedProcess` 启动托管进程，这些显式接口不受该开关限制。
