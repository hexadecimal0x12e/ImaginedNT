# ImaginedNT ConsoleHost

原生 C / Win32 GDI 控制台窗口，x64 PE，使用 `NtEntry` 入口和 `/NODEFAULTLIB`，不依赖 CRT 或 CoreCLR。作为 FullTrustApp 的默认来宾进程随应用打包。

```powershell
pwsh -File src/ImaginedNT.FullTrustApp/ConsoleHost/build.ps1
```

生成 `ConsoleHost/bin/conhost.exe`。需要 MSVC x64 工具和 Windows SDK，可设置 `IMAGINEDNT_VCVARS` 指向 `vcvars64.bat`。

启动后自动运行 `C:\Windows\System32\cmd.exe /D /Q /A`，隐藏其控制台窗口。通过匿名管道发送输入，接收合并的标准输出和错误流；回车提交整行，输入后台写入。关闭窗口时终止仍在运行的 cmd。cmd 退出后保留输出供查看。

管道文本按系统 OEM 代码页转码，保留跨读取的多字节字符；不自动跟随 `chcp` 或外部程序自行切换的输出编码。

支持自动换行、512 行滚动历史、退格、Esc 清除输入和鼠标滚轮。输出处理 CR/LF、制表符、退格、换页及 `ESC[2J` 清屏。输入最多 512 个 UTF-16 代码单元。

后续连接使用 [conhost.h](conhost.h) 中的消息协议：

- 向 `ImaginedNT.ConsoleHost` 窗口发送 `CONHOST_ATTACH`，`wParam` 指定接收输入的窗口，覆盖默认的 cmd 输入目标；传 `NULL` 恢复向 cmd 发送输入。
- 输出使用 `WM_COPYDATA`，`COPYDATASTRUCT.dwData = CONHOST_OUTPUT`，数据为 UTF-16，`cbData` 不含终止 NUL，单次最多 64 KiB。
- 用户回车后，绑定窗口收到 `WM_COPYDATA`，`dwData = CONHOST_INPUT`，数据为带 CRLF 的 UTF-16 输入行；接收方复制数据并返回 `TRUE`。
- `CONHOST_CLEAR` 清除输出历史。

当前协议只承载文本和整行输入；运行时后续负责消息投递及控制台、进程和管道 API 适配。失败通过 `OutputDebugStringW` 记录，并在窗口显示错误码。
