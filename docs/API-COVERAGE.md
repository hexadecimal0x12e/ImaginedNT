# Managed API coverage

`ImaginedNT.Host apis` prints the authoritative runtime list, including aliases. This document
summarizes the semantic surface and the known gaps.

At the time of writing the registry contains **475 managed entry points** (aliases included) and
**346 native declarations**. The numbers grow as APIs are added; always use `apis` for the exact
list.

The managed security surface includes runtime-local user/group management (`NetUserAdd/Del/SetInfo`,
`NetLocalGroupAdd/AddMembers/DelMembers`), password logon, process/thread token opens, token duplication,
selected token information, `AdjustTokenPrivileges`, membership checks, impersonation and `CreateProcessAsUserA/W`.
Filesystem rules can select a user, group or SID; object ACLs and handle masks control resource access.
See [users and permissions](SECURITY.md) for supported information levels and token classes.

## Memory

| API | Notes |
|---|---|
| `NtAllocateVirtualMemory` | reserve/commit/reset flags, page/granularity alignment, writes back base+size |
| `NtFreeVirtualMemory` | decommit/release |
| `NtProtectVirtualMemory` | writes back old protection and rounded size |
| `NtQueryVirtualMemory` | `MemoryBasicInformation` (class 0), mapped-file-name shape (class 2) |
| `NtLockVirtualMemory` / `NtUnlockVirtualMemory` | host-backed page locking, rounded base/size; NT memory entries also expose Zw aliases |
| `VirtualAlloc` / `VirtualFree` / `VirtualProtect` / `VirtualQuery` | Win32 wrappers with `GetLastError`; Ex and NT entries resolve guest process handles and check access masks |
| `ReadProcessMemory` / `WriteProcessMemory`, `NtReadVirtualMemory` / `NtWriteVirtualMemory` | guest process handles, VM access checks, native transfer status and transferred byte counts |
| `VirtualLock` / `VirtualUnlock` / `GetLargePageMinimum` | host-backed locking and large-page minimum query |
| `HeapCreate` / `HeapDestroy`, `HeapAlloc` / `HeapReAlloc` / `HeapFree` / `HeapSize` | guest heap ownership, shared admission budgets, allocator mismatch checks; Rtl allocation/free/size aliases |
| `GetProcessHeaps`, `HeapValidate` / `RtlValidateHeap`, `HeapCompact` / `RtlCompactHeap`, `HeapWalk` | guest heap enumeration and validated native heap operations |
| `RtlCreateHeap` / `RtlDestroyHeap` | growable runtime-owned heaps; caller-provided heap storage, locks and parameter blocks are unsupported |
| `LocalAlloc` / `GlobalAlloc`, `LocalReAlloc` / `GlobalReAlloc`, `LocalFree` / `GlobalFree` | native fixed/movable handle semantics and accounting; compatibility buffers from the guest default heap stay with that heap |
| `LocalSize` / `GlobalSize`, `LocalLock` / `GlobalLock`, `LocalUnlock` / `GlobalUnlock`, `LocalHandle` / `GlobalHandle`, `LocalFlags` / `GlobalFlags` | paired handle operations; fixed guest buffers avoid host default-heap lookups |
| `RtlZeroMemory` / `RtlFillMemory` / `RtlCopyMemory` / `RtlMoveMemory` / `RtlCompareMemory` | chunked, bounds-safe |
| `ntdll!memcpy` / `memmove` / `memset`, `RtlSecureZeroMemory` | full SIZE_T lengths, chunked access, correct pointer returns; backward copies check source reads |
| `GlobalMemoryStatus` / `GlobalMemoryStatusEx`, `GetPhysicallyInstalledSystemMemory` | isolated runtime capacity and availability; shared guest commit/reservation budgets |
| `GetProcessMemoryInfo` / `K32GetProcessMemoryInfo`, `NtQueryInformationProcess` / `ZwQueryInformationProcess` | selected guest's allocation usage and peaks; `ProcessVmCounters` only for the NT query |
| `GetPerformanceInfo` / `K32GetPerformanceInfo` | environment-wide memory accounting in pages and guest handle/process/thread counts |

## Files and I/O

| API | Notes |
|---|---|
| `NtCreateFile`, `NtOpenFile` | `OBJECT_ATTRIBUTES`, share/access/disposition/options, sandbox translation |
| `NtReadFile`, `NtWriteFile` | IOSB status+information, optional byte offset, console and disk objects |
| `NtQueryInformationFile` | basic, standard, position, name, end-of-file |
| `NtSetInformationFile` | position, disposition (delete-on-close), end-of-file |
| `NtQueryAttributesFile`, `NtQueryFullAttributesFile` | basic/network-open info |
| `NtQueryVolumeInformationFile` | minimal volume information |
| `NtFlushBuffersFile`, `NtDeleteFile` | |
| `CreateFileW`, `CreateFileA` | `C:\...` and relative guest paths |
| `ReadFile`, `WriteFile` | sequential I/O; console handles 0/1/2 |
| `SetFilePointer`, `SetFilePointerEx`, `GetFileSize`, `GetFileSizeEx` | |
| `GetFileAttributesW`, `DeleteFileW`, `CreateDirectoryW`, `RemoveDirectoryW`, `MoveFileExW` | sandboxed |
| `FindFirstFileW`, `FindNextFileW`, `FindClose` | snapshot directory enumeration, `*`/`?` wildcards, `WIN32_FIND_DATAW`; results sorted case-insensitively for deterministic scripts |
| `GetCurrentDirectoryW`, `SetCurrentDirectoryW`, `GetFullPathNameW`, `GetTempPathW` | virtual cwd and temp; `SetCurrentDirectoryW` validates the target and normalizes to `C:\...` |
| `GetFileType`, `FlushFileBuffers`, `GetDiskFreeSpaceExW` | |
| `GetStdHandle`, `SetStdHandle` | virtual handles 0/1/2 |

Not implemented: `NtQueryDirectoryFile` and `FindFirstFileExW`, section objects,
asynchronous/overlapped I/O, change notifications, file locking, reparse points.  `samples/bin/shell.exe`
uses the managed `FindFirstFileW`/`FindNextFileW` path for its `dir` command.

## Registry

`ImaginedNT.Apis.Registry` supplies `ImaginedRegistry` and is included by `BuiltinApis`.
Exports are registered for `advapi32.dll` and `kernelbase.dll`; registry API sets also resolve
to the managed handlers. Functions return Win32 error codes directly.

| API | Notes |
|---|---|
| `RegOpenKeyW/A`, `RegOpenKeyExW/A` | relative paths, predefined roots, per-process handles and access masks |
| `RegCreateKeyW/A`, `RegCreateKeyExW/A` | creates intermediate keys; reports created/opened disposition |
| `RegSetValueExW/A`, `RegSetKeyValueW/A` | named/default values; type and raw data preserved |
| `RegQueryValueExW/A`, `RegGetValueW/A` | size queries, `ERROR_MORE_DATA`; `RegGetValue` terminates strings and expands guest environment variables |
| `RegDeleteValueW/A`, `RegDeleteKeyValueW/A` | deletes named/default values |
| `RegDeleteKeyW/A`, `RegDeleteKeyExW/A`, `RegDeleteTreeW/A` | leaf or recursive deletion; existing handles to deleted keys report `ERROR_KEY_DELETED` |
| `RegEnumKeyExW/A`, `RegEnumValueW/A`, `RegQueryInfoKeyW/A` | sorted enumeration, counts, maximum lengths, key class and last-write time |
| `RegCloseKey`, `RegFlushKey` | handle-table lifetime; explicit disk flush |

Data is loaded lazily from `<sandbox-root>/registry.json`. Every mutation saves through a
temporary file and replaces the JSON file before reporting success; failed saves roll back
the corresponding in-memory change and log the exception. Invalid JSON returns `ERROR_BADDB`
without replacing the file. The versioned document stores nested `SubKeys` and `Values`,
with a numeric `Type` and base64 `Data` for each value. String data is stored as UTF-16;
ANSI calls convert through the host ANSI code page.

Within one runtime, processes sharing a sandbox root share a synchronized registry store;
separate sandbox roots have separate stores. Keys and value names are case-insensitive.
`HKEY_CLASSES_ROOT`, `HKEY_CURRENT_USER`, `HKEY_LOCAL_MACHINE`, `HKEY_USERS` and
`HKEY_CURRENT_CONFIG` are independent simulated roots. `AllowFileWrites = false` also
rejects registry mutations.

Not implemented: native `Nt*` registry APIs, ACLs, merged/aliased hive views, volatile keys,
32-bit registry views, remote registry, notifications, transactions, hive import/export and
concurrent writers in separate runtimes. Unsupported create options and 32-bit view flags
return `ERROR_NOT_SUPPORTED`.

## Synchronization

| API | Notes |
|---|---|
| `NtCreateEvent`, `NtOpenEvent`, `NtSetEvent`, `NtResetEvent`, `NtPulseEvent` | manual/auto-reset; named events under `\BaseNamedObjects` |
| `NtCreateMutant`, `NtOpenMutant`, `NtReleaseMutant` | recursive owner-thread semantics |
| `NtCreateSemaphore`, `NtOpenSemaphore`, `NtReleaseSemaphore` | counted semaphore |
| `NtWaitForSingleObject`, `NtWaitForMultipleObjects` | events, mutants, semaphores, threads, processes; NT timeout conversion |
| `CreateEventW`, `CreateEventExW`, `OpenEventW`, `SetEvent`, `ResetEvent`, `PulseEvent` | |
| `CreateMutexW`, `OpenMutexW`, `ReleaseMutex` | |
| `CreateSemaphoreW`, `OpenSemaphoreW`, `ReleaseSemaphore` | |
| `WaitForSingleObject`, `WaitForSingleObjectEx`, `WaitForMultipleObjects`, `SignalObjectAndWait` | |
| `CloseHandle`, `DuplicateHandle`, `CompareObjectHandles`, `NtClose` | |

The object namespace is session-wide for one `ImaginedRuntime`: a child created with
`CreateProcessW` can open a named event/mutant/semaphore that its parent created. Handles and
access masks stay per process.

Not implemented: APCs, alertable waits, timers, keyed events, IO completion ports, wait
abandonment (`WAIT_ABANDONED`), critical section internals (forwarded to natively installed
kernel32 by default).

## Process and thread

| API | Notes |
|---|---|
| `GetCurrentProcessId`, `GetCurrentThreadId`, `GetCurrentProcess`, `GetCurrentThread` | virtual IDs; pseudo handles `-1`/`-2` |
| `GetProcessId`, `GetThreadId`, `GetExitCodeProcess`, `GetExitCodeThread` | handle-aware, including child-process handles |
| `ExitProcess`, `ExitThread`, `TerminateProcess`, `TerminateThread` | child `ExitProcess` terminates only that child |
| `CreateThread`, `NtCreateThreadEx`, `ResumeThread`, `SuspendThread` | guest threads hosted on C# managed threads |
| `CreateProcessW`, `CreateProcessA`, `WinExec` | managed child processes in the same host; `PROCESS_INFORMATION`, `CREATE_SUSPENDED`, `STARTF_USESTDHANDLES`, inherited handles, environment/current-directory |
| `SetHandleInformation`, `GetHandleInformation` | `HANDLE_FLAG_INHERIT` used by `CreateProcess` |
| `Sleep`, `SleepEx`, `SwitchToThread` | |
| `GetLastError`, `SetLastError` | per virtual thread |
| `GetCommandLineW/A`, `GetEnvironmentVariableW`, `SetEnvironmentVariableW` | virtual process environment |
| `GetModuleFileNameW/A`, `GetModuleHandleW/A`, `GetProcAddress`, `LoadLibraryW/A`, `FreeLibrary` | main image exports are managed; other modules forward to the host |
| `RtlNtStatusToDosError` | common mappings |
| `GetSystemInfo`, `GetNativeSystemInfo` | synthetic x64 system information |
| `GetTickCount(64)`, `QueryPerformanceCounter/Frequency`, `GetSystemTime`, `GetLocalTime`, `GetSystemTimeAsFileTime` | host clock |
| `IsDebuggerPresent`, `SetUnhandledExceptionFilter`, `OutputDebugStringA/W` | no-op/trace |

`CreateProcessW`/`A` resolve the image against the parent's sandbox, the parent image directory
and the guest `PATH`; host absolute paths are only accepted below the sandbox or next to the
parent image. Each child gets its own `ImaginedProcess` (memory manager, handle table) but
inherits the parent's sandbox root and runs on its own C# managed thread. Processes in
one runtime share the session object namespace, so named events/mutants/semaphores created by the
parent are visible to children; handles remain per process. Pass `--no-process` (or
`RuntimeOptions.AllowProcessCreation = false`) to reject creation. Under the
default import policy, the unimplemented entry points below still resolve to the host DLLs (and
would create a real OS process); use `--strict` to keep the guest inside the managed surface.

Not implemented: `NtCreateUserProcess`/`RtlCreateUserProcess`, token-based creation
(`CreateProcessAsUserW`, `CreateProcessWithTokenW`, `CreateProcessWithLogonW`), job objects as
guest-visible handles, tokens, process mitigations, `NtQueryInformationProcess`,
thread priorities, fibers, TLS (the `Tls*` entries are placeholders), APC delivery.

## Feature configuration notifications

`RtlRegisterFeatureConfigurationChangeNotification` and
`RtlUnregisterFeatureConfigurationChangeNotification` maintain process-local registrations.
`RtlQueryFeatureConfigurationChangeStamp` returns the fixed virtual stamp `1`.
Registration with a different observed stamp invokes the callback with its context on the
calling guest thread. Host feature changes are not subscribed to.

## Library loading

Static imports and `LoadLibraryW/A` first check DLLs explicitly registered with `--import`, then look for a guest
module in the sandbox, next to the importing DLL and next to the guest image. If found, `ImaginedProcess.LoadManagedLibrary` parses the DLL, maps it at its
preferred base (relocating if necessary), patches its IAT with the same managed/native policy,
calls `DllMain(DLL_PROCESS_ATTACH)`, and records the module. `GetProcAddress` resolves exports
from that module's `PeImage`; `GetModuleHandleW` and `GetModuleFileNameW` are module-aware;
`FreeLibrary` keeps modules resident for the process lifetime. Helper DLLs are demonstrated by
`samples/helper.dll` + `samples/dllhost.exe`.

`[PartiallyImaginedApi]` selects complete managed replacements for individual native exports.
Named and ordinal lookup, export forwarders, imports from other guest DLLs and direct calls to
those function entries inside the DLL share the replacement. Other exports keep their native
implementations. DLL mappings and entry redirects are owned by each guest process.

If no guest module path matches, `LoadLibraryW/A` forwards to the host loader and returns a native
`HMODULE`; `GetProcAddress` works on that handle through the host. The guest image itself is
also a managed module for export resolution. Managed DLLs do not receive TLS callbacks, loader
notifications, or a real PEB; `DllMain` is invoked synchronously from the calling guest thread.

## COM

`ImaginedNT.Apis.Com` is included by `BuiltinApis`. COM API-set names
(`api-ms-win-core-com-l1-*`, `ext-ms-win-com-*`) and `combase` resolve to the `ole32`
registrations. Interfaces, classes and methods are discovered from `[VirtualComInterface]`,
`[VirtualComMethod]`, `[VirtualComClass]` and `[PartiallyImaginedComClass]` attributes when an
API assembly is registered.

| API | Notes |
|---|---|
| `CoInitialize`, `CoInitializeEx`, `CoUninitialize`, `CoGetApartmentType` | per guest-thread apartment state; `S_FALSE` on re-init, `RPC_E_CHANGED_MODE` on a different model |
| `CoInitializeSecurity`, `CoSetProxyBlanket`, `CoGetCallerTID` | no-op success |
| `CoCreateInstance`, `CoCreateInstanceEx` | managed classes, guest factories, registered servers, then host fallback; `CoCreateInstanceEx` accepts local `MULTI_QI` arrays |
| `CoGetClassObject` | managed/partial class factory, guest factory `QueryInterface`, server `DllGetClassObject`, or host fallback; supports `IID_IClassFactory` and `IID_IUnknown` |
| `CoRegisterClassObject`, `CoRevokeClassObject` | guest `IClassFactory` pointers, held with `AddRef`/`Release` |
| `CoTaskMemAlloc`, `CoTaskMemFree`, `CoTaskMemRealloc` | host COM task allocator, SIZE_T lengths, zero-size allocations and accounting; failures preserve the old block |
| `CoCreateGuid`, `StringFromCLSID`, `StringFromIID`, `CLSIDFromString`, `IIDFromString` | GUID conversion; returned strings are task-allocated |
| `CoLoadLibrary`, `CoFreeLibrary`, `CoFreeUnusedLibraries` | guest or host in-process server DLLs; invokes `DllGetClassObject`/`DllCanUnloadNow` where present |
| `CoGetCurrentProcess`, `CoGetCurrentLogicalThreadId` | virtual process/thread identity |
| `SysAllocString` / `SysAllocStringLen` / `SysAllocStringByteLen`, `SysReAllocString` / `SysReAllocStringLen`, `SysFreeString`, `SysStringLen` / `SysStringByteLen` | native BSTR allocator, null/empty and byte-length semantics, accounting; classic ordinals `#2`–`#7` |

Fully managed classes are registered by CLSID and implement their interface methods as
`ulong Method(ComCallContext)`. The runtime allocates one guest-readable interface block per
interface and a vtable of managed thunks; `IUnknown` and reference counting are implemented by the
runtime. `ComCallContext` exposes argument accessors, GUID/interface/BSTR marshaling and the
process memory manager. `ComThreadingModel.Apartment` objects reject calls from other threads with
`RPC_E_WRONG_THREAD`; `Free`, `Both` and `Neutral` are unrestricted.

Partially imagined classes implement selected slots in C# and emit `this`-rewriting native
trampolines for the remaining slots. The host object comes from the host CLSID via real
`ole32!CoCreateInstance` or from `ComRuntime.RegisterHostClass(hostClsid, iid => pointer)`. A CLR
CCW used as the host must mark its interface methods `[PreserveSig]` so the native vtable matches.
Aggregation and interfaces not declared on the managed class return `CLASS_E_NOAGGREGATION` /
`E_NOINTERFACE`.

Not implemented: DCOM/out-of-process activation, proxies and marshaling, `IDispatch`/type
libraries, OLE embedding/drag-and-drop, cross-apartment marshaling, class registration in the host
registry, `CoGetMalloc`, `RoInitialize`/WinRT, and `COSERVERINFO` targets in `CoCreateInstanceEx`.
Guest server DLLs remain mapped for the process lifetime even after `CoFreeLibrary`; host server
DLLs are released when `DllCanUnloadNow` returns `S_OK`.

## Window manager and DWM (`ImaginedNT.AppShared`)

These registrations live in the shared class library (`src/ImaginedNT.AppShared/Gui`), not in
`ImaginedNT.Apis`: the window personality is an App-layer interface that needs a host compositor.
`GuiSession` creates a runtime whose API set is `BuiltinApis.Assemblies` plus the AppShared assembly,
and attaches a `WindowManager` that `Controls/DwmCompositor.xaml` renders.

| API | Notes |
|---|---|
| `RegisterClassExW/A`, `RegisterClassW/A`, `UnregisterClassW/A` | per-process class table and atoms; built-in `STATIC`/`BUTTON`/`EDIT`/`COMBOBOX`/`LISTBOX`/`SCROLLBAR` |
| `CreateWindowExW/A`, `DestroyWindow` | child windows, parent/owner relationship, `WM_NCCREATE`/`WM_CREATE`/`WM_DESTROY`/`WM_NCDESTROY`, `CREATESTRUCTW` in guest memory |
| `ShowWindow`, `ShowWindowAsync`, `UpdateWindow`, `MoveWindow`, `SetWindowPos` | App-composited geometry; `WM_SHOWWINDOW`/`WM_MOVE`/`WM_SIZE`/`WM_PAINT` |
| `GetWindowRect`, `GetClientRect`, `ClientToScreen`, `ScreenToClient` | guest client area excludes the App-drawn caption/border; child coordinates convert from parent-client space |
| `SetWindowTextW/A`, `GetWindowTextW/A`, `GetWindowTextLengthW/A`, `GetClassNameW/A` | titles and class names; `SetWindowTextW` routes `WM_SETTEXT` through the guest wndproc |
| `SetWindowLongPtrW/A`, `GetWindowLongPtrW/A`, `SetWindowLongW`, `GetWindowLongW` | style, ex-style, wndproc, user data, instance, parent and control id |
| `ShowWindow`, `EnableWindow`, `IsWindow`, `IsWindowVisible`, `IsWindowEnabled`, `FindWindowW/A` | state queries and search |
| `Get/SetForegroundWindow`, `Get/SetActiveWindow`, `Get/SetFocus`, `GetDlgCtrlID`, `SetParent`, `BringWindowToTop` | App-side z-order and focus; focus changes post `WM_KILLFOCUS`/`WM_SETFOCUS` |
| `GetMessageW/A`, `PeekMessageW/A`, `TranslateMessage`, `DispatchMessageW/A`, `PostMessageW/A`, `PostThreadMessageW/A`, `SendMessageW/A`, `PostQuitMessage`, `DefWindowProcW/A`, `CallWindowProcW/A` | per-guest-thread queues; `GetMessageW` blocks and wakes on process/thread termination; `WM_QUIT` ends the loop |
| `GetMessagePos`, `GetMessageTime`, `WaitMessage`, `GetSystemMetrics` | virtual screen and message metadata |
| `Get/SetCursorPos`, `LoadCursorW/A`, `LoadIconW/A`, `LoadImageW/A`, `Set/GetCursor` | opaque non-zero handles; the XAML compositor owns the visible cursor |
| `GetKeyState`, `GetAsyncKeyState`, `GetKeyboardState` | state fed by the compositor's key path |
| `SetTimer`, `KillTimer`, `InvalidateRect`, `ValidateRect`, `RedrawWindow` | timer `WM_TIMER` and paint invalidation |
| `GetSysColor`, `GetSysColorBrush`, `MessageBeep` | dark simulated desktop palette so `COLOR_WINDOW+1` backgrounds are visible |
| `dwmapi!DwmIsCompositionEnabled`, `DwmEnableComposition`, `DwmExtendFrameIntoClientArea`, `DwmSet/GetWindowAttribute`, `DwmFlush`, thumbnail stubs | composition is always reported enabled; extended-frame bounds map to the App window rect |

The compositor forwards pointer press/move/release/wheel and keyboard key/character events using
the same hit-testing and message paths.  There is intentionally no GDI: `BeginPaint`, `GetDC`,
`FillRect`, `BitBlt` and friends are not implemented, so only the App-drawn chrome/background and
child-control text are visible.  A guest that only creates windows and runs a message loop is
fully usable; a GDI-rendered application is not yet a target.

## Runtime information (`imaginednt.dll`)

These virtual exports can be imported directly by guest PE images; no physical
`imaginednt.dll` is required. They are registered by `BuiltinApis.CreateRuntime` or
`BuiltinApis.Register`.

```c
BOOL IsImaginedNT(void);
DWORD GetRuntimeVersionW(WCHAR *buffer, DWORD capacity);
DWORD GetRuntimeConfigurationW(WCHAR *buffer, DWORD capacity);
DWORD GetSandboxRootW(WCHAR *buffer, DWORD capacity);
```

`IsImaginedNT` returns TRUE. The string functions use the Windows x64 calling convention
and return the required capacity in UTF-16 code units, including the terminating NUL.
Call with `(NULL, 0)` to query the size, then allocate that many `WCHAR`s and call again.
An undersized buffer is left untouched and sets `ERROR_INSUFFICIENT_BUFFER` (122);
the return value remains the required capacity. A NULL buffer with nonzero capacity
returns zero and sets `ERROR_INVALID_PARAMETER` (87). Successful calls leave last error unchanged.

The version comes from the Core assembly's informational version, falling back to its
assembly version. Configuration is a JSON snapshot of the current `RuntimeOptions` scalar
settings, with enum names as strings and `ExecutionTimeoutMilliseconds` as a number;
streams, writers and the forwarder factory are omitted. `SandboxRoot` and
`GetSandboxRootW` report the calling process's actual filesystem root, including any
per-process override. If settings change between the size query and the write, resize
the buffer and retry as needed.

## CRT API sets

`ImaginedNT.Apis.Crt` implements the 125 exports classified as `managed` through
`PartiallyImaginedApi`, with matching `ucrtbase.dll` aliases (250 registrations).
`BuiltinApis` includes it by default. This covers strings/memory, integer formatting,
integer utilities, path transforms, guest environment and identity, and host-clock time APIs.
Environment and program-path pointers are cached in each guest process's virtual memory.
Secure variants validate buffers and return CRT error codes directly.

The Microsoft Visual C++ UCRT contracts (`api-ms-win-crt-*`) are catalogued separately in
[CRT-API-CLASSIFICATION.md](CRT-API-CLASSIFICATION.md). That document classifies all 2,485
exports of the 14 public API sets plus the private set, and explains which exports are pure
managed candidates, which are safe native `ucrtbase` forwards, and which require a new
`errno`/locale/`FILE`/heap/CRT-startup component. The machine-readable list is
[crt-api-classification.csv](crt-api-classification.csv).

## Native forwarding

Anything not in the managed list is resolved through `NativeForwarder` under the default policy:

- `kernel32.dll`, `ntdll.dll`, `user32.dll`, `advapi32.dll`, `ucrtbase.dll`, etc.
- API-set names (`api-ms-win-*`, `ext-ms-win-*`) map to the classic modules.
- Ordinal imports and forwarded exports use the host's `GetProcAddress`.
- `--strict` / `managed-only` disables this fallback and fails the load on an unresolved import.
- `--no-native-fallback` disables undeclared native fallback while keeping native forwards
  declared with `[PartiallyImaginedNativeApi]`; runtime implementations always take priority.
- `--native-declared-only` (or `--native-policy declared-only`) keeps native forwarding but only
  for exports declared with `[NativeApi]`. The built-in CRT declaration set is in
  `ImaginedNT.Apis.Crt/CrtNativeApis.cs`; undeclared or ordinal imports are rejected.

Resolution results are traced as `[managed]` or `[native]`; inspect them with `--verbose` or
`--trace <file>` before trusting a sandbox profile.

## Syscalls

- Virtual numbers `0x1000..0x1021` are defined in `VirtualSyscallNumbers` and fully implemented
  by the managed layer.
- Real host ntdll numbers are discovered at runtime. Any discovered number whose `Nt*` name is
  also a managed entry point is dispatched to that managed handler. Others return
  `STATUS_NOT_IMPLEMENTED`.
- The VEH trap is opt-in (`--syscalls`).
