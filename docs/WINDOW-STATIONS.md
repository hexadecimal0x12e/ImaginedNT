# 窗口站与桌面

`ImaginedRuntime.WindowStations` 管理运行时内的 USER 对象。每个会话初始化
`WinSta0\Default`，窗口站名称在会话内唯一，桌面名称在窗口站内唯一，均不区分大小写。
对象注册在 `\Sessions\<id>\Windows\WindowStations\<station>\<desktop>`，可从
FullTrustApp 的对象管理页展开查看。对象和句柄不对应宿主窗口站或桌面。

`ProcessCreateOptions.Desktop` 和来宾 `STARTUPINFO.lpDesktop` 接受 `Desktop` 或
`WindowStation\Desktop`。未指定时继承同会话父进程的启动桌面；无父进程时使用
`WinSta0\Default`。进程关联窗口站，线程关联桌面，窗口创建时固定所属桌面。
已有窗口的线程不能切换桌面，跨桌面父子关系会被拒绝。

FullTrustApp 的“窗口站与桌面”页支持创建、删除、查看、切换和在指定桌面启动程序。
只有当前控制台会话中 WinSta0 的输入桌面接收用户输入；其他桌面可只读查看。
“返回输入桌面”恢复跟随来宾的 `SwitchDesktop`。切换会清理按键和鼠标捕获状态，
保留各桌面的窗口、焦点和 GDI 内容。

已接入的来宾 USER32 接口包括窗口站和桌面的创建、打开、关闭、枚举，进程窗口站和
线程桌面的读取/设置，输入桌面的打开/切换，窗口枚举，以及 USER 对象名称、类型、
标志和输入状态查询。A/W 字符串接口分别使用 ANSI 和 UTF-16。

当前对象由运行时命名空间保留；关闭句柄不会自动删除对象。管理端只允许删除没有
进程、线程、窗口或句柄占用的对象，保留 WinSta0、Default 和输入桌面。
实现检查句柄访问掩码和会话归属，尚未实现 USER 对象安全描述符、桌面堆配额和
跨账户钩子；相关安全接口或不支持的参数返回失败，不转发宿主。
