# Native samples

These are tiny, CRT-free Windows x64 PEs used as end-to-end fixtures. They are built without
the C runtime (`/NODEFAULTLIB`) and use a custom entry point named `NtEntry`, so they exercise
the PE loader and IAT patching rather than the MSVC CRT.
`failfast.exe` additionally imports fatal CRT functions from `ucrt.lib`, while retaining the
custom entry point and avoiding CRT startup.

| Sample | Imports | Demonstrates | Exit code |
|---|---|---|---|
| `hello.exe` | `GetStdHandle`, `WriteFile`, `ExitProcess` | managed console write through the IAT | 42 |
| `threads.exe` | `CreateThread`, `WaitForSingleObject`, `GetExitCodeThread`, `CloseHandle`, `ExitProcess` | guest thread creation, wait and exit code | 55 |
| `fileio.exe` | `CreateFileW`, `WriteFile`, `ReadFile`, `CloseHandle`, `ExitProcess` | sandboxed file create/read/write | 66 |
| `memory.exe` | `GetSystemInfo`, `VirtualAlloc`, `VirtualProtect`, `VirtualQuery`, `VirtualFree`, `GlobalMemoryStatusEx`, `GetProcessMemoryInfo`, `NtQueryInformationProcess`, `ExitProcess` | virtual memory; process and environment memory reports before allocation, after allocation and after release | 77 |
| `native.exe` | `GetACP`, `OpenProcess`, `GetLastError`, `CreateFileW`, `ExitProcess` | native forwarding + last-error bridging between native and managed calls | 89 |
| `reloc.exe` | `ExitProcess` | base relocation at a forced load address | 99 |
| `syscall.exe` | `ExitProcess` | direct `syscall` instruction trapped by the VEH | 42 |
| `dllhost.exe` + `helper.dll` | `LoadLibraryW`, `GetProcAddress`, `GetModuleHandleW`, `ExitProcess` | managed guest DLL mapping, `DllMain`, export lookup | 70 |
| `directsyscall.exe` | `CreateEventW`, `GetModuleHandleW`, `GetProcAddress`, `CloseHandle`, `ExitProcess` | reads the real `NtClose` syscall number from ntdll, executes `syscall` in guest code, expects managed `NtClose` to close the handle | 80 |
| `spawn.exe` + `child.exe` | `CreateProcessW`, `ResumeThread`, `WaitForSingleObject`, `GetExitCodeProcess`, `CloseHandle`, `ExitProcess` | creates a `CREATE_SUSPENDED` child process, resumes it, waits for it and checks its exit code; both run on separate C# managed threads in one host | 101 |
| `comtest.exe` | `CoInitializeEx`, `CoCreateInstance`, `CoUninitialize`, `SysFreeString` (by ordinal), `ExitProcess` | real COM ABI against the built-in managed COM class: vtable calls, out parameters, BSTR, `QueryInterface`/`AddRef`/`Release` | 42 |
| `comserver.dll` | `SysAllocString` | CRT-free in-process COM server exporting `DllGetClassObject`/`DllCanUnloadNow`; used by the COM tests through `CoGetClassObject` | n/a |
| `shell.exe` | `GetStdHandle`, `ReadFile`, `WriteFile`, `CreateFileW`, `FindFirstFileW`, `FindNextFileW`, `FindClose`, `CreateDirectoryW`, `RemoveDirectoryW`, `DeleteFileW`, `MoveFileExW`, `Get/SetCurrentDirectoryW`, `CreateProcessW`, waits/exit codes | minimal cmd.exe-style guest shell: `dir`, `cd`, `pwd`, `type`, `echo`, `mkdir`, `rmdir`, `del`, `copy`, `move`, `run`, `cls`, `exit` | 0 on `exit` |
| `window.exe` | `RegisterClassExW`, `CreateWindowExW`, `ShowWindow`, `UpdateWindow`, `GetMessageW`, `TranslateMessage`, `DispatchMessageW`, `DefWindowProcW`, `SetWindowTextW`, `DestroyWindow`, `PostQuitMessage` | two guest top-level windows plus `STATIC`/`BUTTON` child controls; the App's XAML DWM compositor renders them and forwards mouse/keyboard messages. No GDI. | 0 on close |
| `spin.exe` | `GetTickCount64` | infinite CPU-bound loop; demonstrates the host watchdog and `--timeout` termination | 124 (host timeout, never exits by itself) |
| `failfast.exe` + `failfastdll.dll` | process/thread APIs, `RaiseFailFastException`, `FatalAppExitA/W`, fatal CRT imports, dynamic NT exports | 21 fatal-exit cases in virtual children; checks exit codes, thread completion and parent survival | 0 if all selected cases pass |

Build them with:

```powershell
pwsh -File samples/build-samples.ps1
```

The script locates `vcvars64.bat` (or use `$env:IMAGINEDNT_VCVARS`) and builds the C samples with
`cl`/`link`, and the assembly sample with `ml64`. `samples/bin` is generated output.

Run them through the host:

```powershell
dotnet src/ImaginedNT.Host/bin/Debug/net10.0/ImaginedNT.Host.dll run samples/bin/hello.exe --strict --verbose
dotnet src/ImaginedNT.Host/bin/Debug/net10.0/ImaginedNT.Host.dll run samples/bin/syscall.exe --strict --syscalls --verbose
dotnet src/ImaginedNT.Host/bin/Debug/net10.0/ImaginedNT.Host.dll run samples/bin/reloc.exe --strict --base 180000000 --verbose

# Guest DLL loading: copy the DLL into the sandbox, then run the host
$sandbox = Join-Path $PWD sandbox\dll-demo
New-Item -ItemType Directory -Force $sandbox | Out-Null
Copy-Item samples/bin/helper.dll $sandbox
dotnet src/ImaginedNT.Host/bin/Debug/net10.0/ImaginedNT.Host.dll run samples/bin/dllhost.exe --strict --sandbox $sandbox --verbose

# Direct real syscall: the guest reads ntdll's NtClose number and executes syscall
dotnet src/ImaginedNT.Host/bin/Debug/net10.0/ImaginedNT.Host.dll run samples/bin/directsyscall.exe --strict --syscalls --verbose

# CreateProcessW: spawn.exe creates child.exe in the same host (child.exe also exists next to it)
dotnet src/ImaginedNT.Host/bin/Debug/net10.0/ImaginedNT.Host.dll run samples/bin/spawn.exe --strict --verbose
# Deny child creation instead:
dotnet src/ImaginedNT.Host/bin/Debug/net10.0/ImaginedNT.Host.dll run samples/bin/spawn.exe --strict --no-process --verbose

# Managed COM: the guest creates ImaginedNT's built-in COM class and calls its vtable
dotnet src/ImaginedNT.Host/bin/Debug/net10.0/ImaginedNT.Host.dll run samples/bin/comtest.exe --strict --verbose

# CPU spinner: never returns; the watchdog kills it after two seconds
dotnet src/ImaginedNT.Host/bin/Debug/net10.0/ImaginedNT.Host.dll run samples/bin/spin.exe --strict --timeout 2 --verbose

# Mini cmd shell: interactive, scripted by piping stdin, or one-shot /c
dotnet src/ImaginedNT.Host/bin/Debug/net10.0/ImaginedNT.Host.dll run samples/bin/shell.exe --strict --sandbox sandbox\shell
dotnet src/ImaginedNT.Host/bin/Debug/net10.0/ImaginedNT.Host.dll run samples/bin/shell.exe --strict --sandbox sandbox\shell -- /c "dir"
"dir`nrun hello.exe`nexit" | dotnet src/ImaginedNT.Host/bin/Debug/net10.0/ImaginedNT.Host.dll run samples/bin/shell.exe --strict --sandbox sandbox\shell
```

`window.exe` needs the App's USER32/DWM implementation, which lives in `src/ImaginedNT.AppShared/Gui`
(not in the Host API set).  Build it, then run it from the App's `窗口桌面` page; running it
through `ImaginedNT.Host` will still create the window state but there is no compositor to show it.

Without `-- /c` and without piped input the shell waits for a command on stdin.  The CLI has no
default timeout, so the shell stays alive until you type `exit`, close stdin, or pass
`--timeout <seconds>` to enable the guest watchdog (exit code 124).  The shell runs entirely
inside the managed layer under `--strict`: directory enumeration, relative paths, file
copy/delete/move and `CreateProcessW` all go through `ImaginedNT.Core` rather than the host's
kernel32.

The existing end-to-end fixtures are checked by `tests/ImaginedNT.Tests`.
The fail-fast sample performs its own checks as described below.

## Fail-fast regression sample

```powershell
pwsh -File scripts/run-sample.ps1 -Sample failfast -Verbose

# Select one case; the parent still launches and checks a virtual child.
dotnet src/ImaginedNT.Host/bin/Debug/net10.0/ImaginedNT.Host.dll run samples/bin/failfast.exe --strict --verbose -- abort
```

With no arguments (or `all`), the sample runs all cases below. Each case starts a new virtual
child in the same host, waits for both its process and main thread, and checks the process exit
code. A returned fatal API, missing export/DLL, timeout or unexpected status fails the sample.
The parent supplies a shared marker: 1 means the trigger was reached, and 2 means the fatal
operation returned. This detects a return before a subsequent API can stop the failing child.
Success prints `failfast: all selected cases passed; parent runtime survived` and exits with 0.
The sample also performs invalid-buffer `WriteFile` calls before and after the cases, exercising
caught managed exceptions while the guest VEH filters are installed.

| Cases | Trigger | Expected child status |
|---|---|---|
| `int29`, `gs-cookie` | `__fastfail(7)` / `__fastfail(2)` in the EXE | `0xC0000409` |
| `raise`, `raise-dynamic` | imported / `GetProcAddress`-resolved `RaiseFailFastException` | `0xC0000602` |
| `raise-record` | `RaiseFailFastException` with a supplied exception record and reason 2 | `0xC0000409` |
| `rtl`, `critical`, `fatal` | dynamically resolved `RtlFailFast`, `RtlReportCriticalFailure`, `RtlReportFatalFailure` | `0xC0000409` |
| `rtl2` | dynamically resolved `RtlFailFast2` | `0xC0000602` |
| `fatal-app-a`, `fatal-app-w` | imported `FatalAppExitA/W` | `0x40000015` |
| `abort`, `terminate`, `invalid-parameter`, `watson` | imported CRT fatal functions | `0xC0000409` |
| `dll-attach`, `dll-int29` | `__fastfail` in `DllMain(DLL_PROCESS_ATTACH)` / a DLL export | `0xC0000409` |
| `dll-raise` | `RaiseFailFastException` called by a DLL export | `0xC0000602` |
| `thread-int29`, `thread-raise`, `thread-abort` | fatal operations on a worker guest thread while its main thread waits | same status as the corresponding main-thread case |

`failfast.exe --child <case>` directly triggers a single operation for debugging. Run it through
ImaginedNT. Native debuggers may pause on the runtime's replacement breakpoints; continue to
allow the VEH to handle them. The helper DLL is built alongside the EXE and is copied into the
sandbox by `run-sample.ps1` and the app's existing sample-copying path.
