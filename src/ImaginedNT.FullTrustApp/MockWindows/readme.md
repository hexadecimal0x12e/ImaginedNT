# 虚拟 Windows 会话

`VirtualWindowsSystem` 使用托管 ImaginedProcess 启动以下组件，不依赖这些组件的真实 PE 文件：

```text
smss（系统根进程）
├─ smss（Session 0 初始化，随后退出）
│  ├─ csrss
│  └─ wininit → services、lsass、lsm
└─ smss（每个交互会话初始化，随后退出）
   ├─ csrss
   └─ winlogon → dwm、LogonUI
                 └─ userinit → explorer
```

启动后停留在登录界面。用户库为空时，需要创建首个管理员账户；已有账户时输入用户名和密码登录。宿主配置的初始来宾程序等待成功登录后，以该用户身份启动。来宾程序退出后，系统会话继续运行。`userinit` 完成会话环境设置和 Shell 启动后退出。

`VirtualWindowsSession.xaml` 是独立 Window，提供桌面合成、程序启动、新建/切换会话、锁定、解锁、注销和停止运行时。关闭桌面窗口后，可从主窗口的“窗口桌面”重新打开；关闭主窗口会停止所有组件。

每个交互会话拥有 `WinSta0\Default`、`Winlogon`、`Disconnect` 桌面；登录和锁定使用 Winlogon 桌面，用户程序使用 Default 桌面。Session 0 的服务组件使用非交互窗口站。

桌面操作调用已注册的 USER32 VirtualAPI；`imagined.session.dll` 提供 CSRSS 初始化入口。登录请求交给 LSASS 的命令队列，通过 `runtime.Security.Logon` 校验密码、账户是否存在和是否启用。解锁要求认证结果的 SID 与当前登录用户一致。管理员登录后，可从工具栏创建普通用户。系统组件使用 SYSTEM，userinit、explorer 和用户子进程使用登录用户令牌；用户名和配置目录由令牌初始化。

普通桌面和窗口站仅授权 SYSTEM 与当前登录用户，Winlogon 和 Disconnect 桌面仅授权 SYSTEM。锁定时，用户进程不能切走安全桌面。用户程序启动、锁定和注销通过同一会话队列串行处理；未登录或已锁定时不能从宿主启动用户程序。注销结束用户进程后清除会话令牌和用户桌面授权。账户库随运行时销毁，详见 [用户和权限](../../../docs/SECURITY.md)。

Explorer/DWM 意外退出会尝试重启；SMSS、CSRSS、Wininit 等关键组件意外退出会触发运行时 BugCheck。启动阶段、状态切换和异常通过运行时 `TraceLog` 记录。

流程参考：[Windows 启动进程](https://techcommunity.microsoft.com/blog/askperf/ws2008-startup-processes-and-delayed-automatic-start/372692)、[Winlogon 初始化](https://learn.microsoft.com/en-us/windows/win32/secauthn/initializing-winlogon)。
