# NT kernel feature gap matrix

> Status snapshot for ImaginedNT's managed NT personality.
>
> Scope: NT executive/system-service features as observed through `ntdll.dll` and the
> Win32 APIs built on top of it. ImaginedNT is a **user-mode NT simulator**, not a real
> kernel, so kernel-only facilities (HAL, IRQL, DPC, scheduler, page tables, PnP/power
> driver stack, bugcheck, kernel debugger, ...) are listed separately as out-of-scope
> rather than as missing implementation details.

## 1. Summary

- The runtime is a PE32+ loader plus a managed API dispatch layer.
- Managed registrations cover a broad Win32/CRT/COM surface (see `API-COVERAGE.md`),
  but the native NT system-service surface is much smaller.
- A source scan finds **34 distinct managed `Nt*` entry points**.
- The host `C:\Windows\System32\ntdll.dll` exports **495 distinct `Nt*` names**
  (including internal/undocumented APIs). Most of those are therefore not implemented
  by the managed layer.
- With the default import policy, an unregistered import is forwarded to the host's
  real DLL. Under `--strict` / `managed-only`, the same call is unresolved or
  `STATUS_NOT_IMPLEMENTED`. This is the key difference between "missing in managed
  layer" and "missing in the running guest".

> The counts above are a diagnostic metric, not a compatibility target: many ntdll
> exports are internal, build-specific, or not part of the documented NT executive
> API. The items in the subsystem matrix below are the ones that matter for a
> practical NT personality.

## 2. Gap matrix

| Area | Current state | Main gaps | Priority |
|---|---|---|---|
| Memory manager (Mm) | Host-backed reserve/commit/protect/query/free, guest memory accounting and shared allocation budgets | Sections, file mapping, working sets, pagefile, NUMA, AWE, large-page semantics, COW/guard/write-watch, more `NtQueryVirtualMemory` classes | P0/P1 |
| I/O manager (Io) | Sandboxed synchronous file create/read/write/query/enumerate | `NtDeviceIoControlFile`, `NtFsControlFile`, IRP/device/driver model, async/overlapped I/O, IO completion, directory-change notifications, locks/oplocks/reparse/EA/streams | P0/P1 |
| Object manager (Ob) | Small namespace with directories, symbolic links, Event/Mutant/Semaphore | Object-type registration, remaining object types, security descriptors, access checks, quotas, callbacks, private namespaces, kernel handles | P1 |
| Process/thread manager (Ps) | Managed `CreateProcessW/A`, `CreateThread`, waits, exit, TLS/FLS | `NtCreateUserProcess`, `NtCreateProcess/Thread`, most query/set information classes, tokens, jobs, APC, fibers, priorities/affinity, PEB/TEB, Wow64, mitigation | P0/P1 |
| Synchronization (Ke) | Event/Mutant/Semaphore, basic waits, critical sections | Waitable timers, keyed events, IO completion, APC/alertable waits, wait abandonment, SRW/condition variables, wait chains | P1 |
| Registry (Cm) | Win32 `Reg*` over a JSON-backed virtual store | Native `Nt*` registry APIs, key objects, ACLs, transactions, notifications, volatile keys, 32-bit views, hive load/unload, remote registry | P1 |
| Security (Se) | Simplified handle access masks | Tokens, SIDs, ACLs, privileges, impersonation, integrity, AppContainer, audit, security descriptors | P1 |
| IPC | Named synchronization objects shared per runtime | ALPC/LPC, named pipes, mailslots, RPC, shared-memory sections | P1/P2 |
| Loader / exceptions | PE mapping, imports, exports, relocations, process-attach TLS callbacks, partial loader | PEB/TEB, `LdrInitializeThunk`, `RtlUserThreadStart`, activation contexts/SxS, loader notifications, thread-attach/detach TLS, SEH/unwind, 64-bit CONTEXT | P0/P1 |
| Diagnostics / tracing | TraceLog and basic OutputDebugString handling | Guest VEH dispatch, unhandled-exception filter, ETW, WER, WMI, crash dumps | P2 |
| USER32 / GDI / win32k | Small App-layer window manager in `ImaginedNT.App`; no GDI, no win32k syscall layer | Dialogs, menus, hooks, clipboard, DPI/monitor APIs, scrollbars, layered windows, input injection; all GDI; `NtUser*`/`NtGdi*`; session/desktop/window-station objects | P1/P2 |
| Kernel-only facilities | Not implemented by design | HAL/ACPI/APIC, IRQL/DPC, scheduler, page tables/PFN database, drivers/PnP/power/DMA, bugcheck, kernel debugger, win32k/GDI | Out of scope |
| 32-bit images | AMD64 only | PE32/i386, Wow64, 32-bit registry views | P2 |

## 3. Detailed findings

### 3.1 Memory manager (Mm)

Implemented:

- `NtAllocateVirtualMemory`, `NtFreeVirtualMemory`, `NtProtectVirtualMemory`,
  `NtQueryVirtualMemory`, `NtLockVirtualMemory`, `NtUnlockVirtualMemory`.
- Win32 wrappers: `VirtualAlloc`, `VirtualFree`, `VirtualProtect`, `VirtualQuery`,
  their current-process `Ex` variants, and host-backed `VirtualLock`/`VirtualUnlock`.
- `GlobalMemoryStatus`/`Ex`, `GetPhysicallyInstalledSystemMemory`, and
  `GetPerformanceInfo`/`K32GetPerformanceInfo` report the isolated runtime's capacity,
  usage and availability. `GetProcessMemoryInfo`/`K32GetProcessMemoryInfo` and
  `NtQueryInformationProcess`/`ZwQueryInformationProcess(ProcessVmCounters)` report
  the selected guest process's allocations and peaks. `NtQueryVirtualMemory` reports real allocation protection
  and mapped-file names; querying address zero can enumerate the initial free region.
- `RuntimeOptions.MaximumPhysicalMemoryBytes` and `MaximumVirtualMemoryBytes`
  (bytes, zero means unlimited) are captured when the runtime is created. CLI equivalents:
  `--max-physical-memory <bytes>` and `--max-virtual-memory <bytes>`.
  Managed virtual allocations, image mappings and heap growth share one allocation gate.
  The physical budget charges guest committed memory; the virtual budget charges guest
  reservations, including committed allocations. Images and guest-visible runtime buffers
  are included; CLR, UI and other host overhead are excluded. Repeated commits are charged
  once; decommit, release, heap shrinking and destruction return the corresponding quota.
- Guest heaps account for 16-byte-aligned live blocks, with page-aligned committed capacity
  bounded below by their initial size (at least one page). Reservations are rounded to
  allocation granularity and bounded below by the heap's maximum size. Host-heap compatibility
  calls account only for intercepted blocks and preserve their original guest ownership.
- `ImaginedRuntime.Memory.GetSnapshot()` returns environment-wide capacity, usage and peaks;
  `GetSnapshot(pid)` selects the process counters while keeping environment-wide totals.
  Configured budgets are also the reported capacities. With a zero budget, allocation remains
  unlimited and reported capacity starts at 8 GiB physical / 128 TiB virtual, growing if guest
  peaks exceed these values. No host memory counters are read.

All imagined processes share the host address space, but their allocation accounts are separate.
The reported working set and private commit both represent accounted guest committed memory;
unmodeled page faults and kernel-pool counters are zero. These are allocation admission budgets,
not an OS physical-page quota: unmediated native allocations are not tracked. Guest allocation
and reporting APIs remain managed under native-first policy. `LimitJobObject` is the separate host-wide OS
commit backstop. `HeapReAlloc` checks net growth and leaves the old allocation charged on failure.

The memory layouts and counter meanings follow Microsoft's
[MEMORYSTATUSEX](https://learn.microsoft.com/windows/win32/api/sysinfoapi/ns-sysinfoapi-memorystatusex)
and [PROCESS_MEMORY_COUNTERS_EX](https://learn.microsoft.com/windows/win32/api/psapi/ns-psapi-process_memory_counters_ex)
contracts.
- A tracked `MemoryRegion` list over the real host address space
  (`src/ImaginedNT.Backends.Windows/Memory/VirtualMemoryManager.cs`).

Not implemented or only a facade:

- Section objects and file mappings:
  `NtCreateSection`, `NtOpenSection`, `NtMapViewOfSection`,
  `NtUnmapViewOfSection`, `NtMapViewOfSectionEx`, `NtUnmapViewOfSectionEx`.
- Shared memory, image sections and section-backed file I/O.
- Guest-specific working sets, physical-page/pagefile control, NUMA and memory-compression semantics.
- AWE (`AllocateUserPhysicalPages`, `MapUserPhysicalPages`) and real large-page AWE.
- COW, guard-page faults, write-watch, memory priority, page protection combos.
- Most `NtQueryVirtualMemory` information classes; only a small subset is handled.
- Complete heap-manager surface: `RtlCreateHeap` / `RtlDestroyHeap` /
  `RtlValidateHeap` and several loader/CRT heap flags are still incomplete
  (see `docs/REACTOS-STATUS.md`, item 5).

### 3.2 I/O manager (Io)

Implemented:

- `NtCreateFile`, `NtOpenFile`, `NtReadFile`, `NtWriteFile`,
  `NtQueryInformationFile`, `NtSetInformationFile`, `NtFlushBuffersFile`,
  `NtDeleteFile`, `NtQueryAttributesFile`, `NtQueryFullAttributesFile`,
  `NtQueryVolumeInformationFile`.
- Win32 file/volume wrappers and `FindFirstFileW`/`FindNextFileW` snapshot
  enumeration (`src/ImaginedNT.Apis.File/FileApis.cs`, `VolumeApis.cs`).

Not implemented:

- `NtDeviceIoControlFile`, `NtFsControlFile`, `NtQueryDirectoryFile`,
  `NtNotifyChangeDirectoryFile` / `Ex`, `NtReadFileScatter`,
  `NtWriteFileGather`.
- IRP model, DriverObject/DeviceObject stacks, filter drivers, Fast I/O.
- Asynchronous/overlapped I/O, APC completion, cancellation, IO completion ports.
- File locking (`NtLockFile`/`NtUnlockFile`), oplocks, reparse points,
  extended attributes, alternate data streams, sparse/compressed files.
- Cache Manager, Mount Manager, PnP/power/DMA.
- `NtQueryVolumeInformationFile` covers only a small set of information classes;
  others return `STATUS_INVALID_INFO_CLASS`.

### 3.3 Object manager (Ob)

Implemented:

- One case-insensitive namespace with directories and symbolic links:
  `\BaseNamedObjects`, `\KnownDlls`, `\Sessions\0`, `\Device\ImaginedDrive0`,
  `\??\C:` (`src/ImaginedNT.Backends.Windows/Objects/ObjectManager.cs`).
- Named Event/Mutant/Semaphore objects and a per-process handle table.

Not implemented:

- Full object types behind the enum in
  `src/ImaginedNT.Abstractions/Objects/NtObject.cs`: Timer, Section, Token,
  RegistryKey, Job, IoCompletion, KeyedEvent, Device, Driver are names only.
- `NtCreateDirectoryObject`, `NtOpenDirectoryObject`, `NtQueryDirectoryObject`.
- Object-type registration, object callbacks, quotas, permanent objects.
- Security descriptors, access checks, audit alarms, kernel handles.
- Private namespaces and boundary descriptors.
- Cross-process handle duplication semantics beyond the current simplified path.

### 3.4 Process and thread manager (Ps)

Implemented:

- `CreateProcessW/A`, `WinExec`, `CreateThread`, `NtCreateThreadEx`,
  `ResumeThread`, `SuspendThread` (stub), waits, exit, per-thread last error,
  `TlsAlloc/TlsFree/TlsSetValue/TlsGetValue`, `Fls*`.
- Managed processes run on C# managed threads and share the host address space.

Not implemented:

- `NtCreateUserProcess`, `RtlCreateUserProcess`, `NtCreateProcess`,
  `NtCreateProcessEx`, `NtCreateThread`.
- `NtQueryInformationProcess`, `NtSetInformationProcess`,
  `NtQueryInformationThread`, `NtSetInformationThread` and their information
  classes.
- Tokens and token-based creation: `CreateProcessAsUserW`,
  `CreateProcessWithTokenW`, `CreateProcessWithLogonW`.
- Job objects as guest-visible handles; current job support is a host-side
  backstop (`JobObjectController`).
- APC delivery, alertable waits, real thread suspend/resume, fibers.
- Thread priorities, affinity, CPU Sets, process mitigation policies,
  protected processes, Wow64.
- PEB/TEB, process parameters, real static TLS, `DLL_THREAD_ATTACH/DETACH`
  callbacks. Process-attach callbacks exist in
  `ImaginedProcess.RunTlsCallbacks`, but thread-attach/detach does not.

### 3.5 Synchronization (Ke)

Implemented:

- Event, mutant, semaphore, `NtWaitForSingleObject`,
  `NtWaitForMultipleObjects`, `SignalObjectAndWait`, critical sections,
  `InitOnceExecuteOnce`, `InitializeSListHead`, TLS/FLS.

Not implemented:

- Waitable timers (`NtCreateTimer`, `NtSetTimer`), keyed events.
- IO completion ports (`NtCreateIoCompletion`, `NtSetIoCompletion`,
  `NtRemoveIoCompletion`).
- APCs and alertable waits (`NtQueueApcThread`, `NtTestAlert`,
  `NtWaitForAlertByThreadId`).
- Full `WAIT_ABANDONED` reporting semantics.
- SRW locks and condition variables as kernel dispatcher objects.
- Wait chains, priority inheritance and the complete dispatcher model.

### 3.6 Registry (Cm)

Implemented:

- Win32 `RegOpenKey*`, `RegCreateKey*`, `RegSetValue*`, `RegQueryValue*`,
  `RegDelete*`, `RegEnum*`, `RegQueryInfoKey*`, backed by a JSON document in
  the sandbox (`src/ImaginedNT.Apis.Registry/ImaginedRegistry.cs`).

Not implemented:

- Native `Nt*` registry APIs: `NtCreateKey`, `NtOpenKey`, `NtQueryKey`,
  `NtQueryValueKey`, `NtSetValueKey`, `NtEnumerateKey`, `NtEnumerateValueKey`,
  `NtDeleteKey`, `NtDeleteValueKey`, `NtNotifyChangeKey`, key transactions.
- ACLs and security descriptors, volatile keys, 32-bit views, merged/aliased
  hive views, hive import/export, remote registry.
- Registry filtering and callbacks.

### 3.7 Security (Se)

Not implemented:

- Token objects (`NtCreateToken`, `NtOpenProcessToken`,
  `NtOpenThreadToken`, `NtDuplicateToken`, `NtAdjustPrivilegesToken`).
- SIDs, ACLs, security descriptors, access checks.
- Impersonation, integrity levels, AppContainer / low-box tokens.
- Audit and alarm APIs (`NtAccessCheckAndAuditAlarm`,
  `NtOpenObjectAuditAlarm`, ...).
- The object manager currently checks simplified access masks, not the Windows
  security reference monitor.

### 3.8 IPC and cross-process facilities

Not implemented:

- ALPC/LPC: `NtCreatePort`, `NtConnectPort`, `NtRequestPort`, `NtReplyPort`,
  `NtAcceptConnectPort`, `NtCompleteConnectPort`, `NtReadRequestData`,
  `NtWriteRequestData`.
- Named pipes and mailslots.
- RPC runtime integration.
- Shared-memory sections (blocked by the Mm section gap).
- Named Event/Mutant/Semaphore objects are shared by processes in one runtime;
  handles remain per process.

### 3.9 Loader, PE and exception handling

Implemented:

- PE32+ parsing, sections, imports, exports, relocations, delay imports,
  export forwarders, IAT patching, managed DLL loading, `DllMain`.
- Process-attach TLS callbacks for the main image and loaded DLLs.
- Direct-syscall interception via VEH for the guest image.

Not implemented:

- PEB/TEB setup and process parameters.
- `LdrInitializeThunk`, `RtlUserThreadStart`, loader notifications.
- Activation contexts / SxS / manifest semantics.
- `DLL_THREAD_ATTACH` / `DLL_THREAD_DETACH` TLS callbacks and static TLS.
- Guest VEH/SEH dispatch and unwind:
  `RtlAddVectoredExceptionHandler` currently returns 1 without registering a
  handler (`src/ImaginedNT.Apis.System/NtdllInitializationApis.cs:83-89`);
  `SetUnhandledExceptionFilter` is a no-op
  (`src/ImaginedNT.Apis.System/SystemApis.cs:495`).
- 64-bit `CONTEXT`, `RtlUnwindEx`, `RtlVirtualUnwind`, `__C_specific_handler`.
- PE32/i386 and Wow64.
- ETW, WER, WMI, crash dump.

### 3.10 GUI / win32k

Implemented (App layer only):

- A window-manager subset: class registration, window creation, message
  queue, basic controls, DWM stubs (`src/ImaginedNT.App/Gui`).

Not implemented:

- GDI: `BeginPaint`, `GetDC`, `FillRect`, `BitBlt`, text rendering, etc.
- Full USER32 message/hook/input/desktop/window-station semantics.
- win32k.sys kernel-mode graphics.

### 3.11 Kernel-only facilities (out of scope for this simulator)

These are not "missing functions" in the current architecture; they would
require a real kernel or hypervisor:

- HAL/ACPI/APIC, interrupt and exception dispatch at IRQL.
- Scheduler, DPCs, timers, thread context switching.
- Page tables, PFN database, physical memory manager.
- Driver model, IRP dispatch, PnP/power/DMA.
- BugCheck, kernel debugger, kernel ETW/WMI.

## 4. Known no-op or placeholder implementations

| Location | Current behavior |
|---|---|
| `src/ImaginedNT.Apis.System/NtdllInitializationApis.cs:83-89` | `RtlAddVectoredExceptionHandler` / `RtlAddVectoredContinueHandler` return 1; removal returns 1 |
| `src/ImaginedNT.Apis.System/SystemApis.cs:491-495` | `IsDebuggerPresent` returns 0; `SetUnhandledExceptionFilter` returns 0 |
| `src/ImaginedNT.Apis.Synchronization/SynchronizationApis.cs:598` | `SuspendThread` returns 1 without suspending |
| `src/ImaginedNT.App/Gui/WindowApis.cs:1013` | Callback-based `SetTimer` is intentionally unsupported |
| `src/ImaginedNT.Apis.System/NtdllInitializationApis.cs:115` | `CsrClientConnectToServer` reports a virtual success but does not populate CSR/BASESRV data |
| `src/ImaginedNT.Apis.System/NtdllInitializationApis.cs:98` | `LdrSetDllManifestProber` only stores callbacks, no prober |
| `src/ImaginedNT.Apis.System/NtdllInitializationApis.cs:106` | `LdrDisableThreadCalloutsForDll` returns `STATUS_NOT_SUPPORTED` for images with TLS |

## 5. Documentation inconsistencies to fix

- `docs/LIMITATIONS-AND-SECURITY.md` still says TLS callbacks are not run and
  `Tls*` entries are placeholders. The code and `docs/REACTOS-STATUS.md` show
  real `TlsAlloc/TlsFree/TlsSetValue/TlsGetValue` plus process-attach TLS
  callbacks; only thread-attach/detach callbacks are missing.
- `docs/LIMITATIONS-AND-SECURITY.md` says guest DLL loading forwards to the
  host; the current runtime has a managed DLL loader
  (`ImaginedProcess.LoadManagedLibrary`).
- `docs/ARCHITECTURE.md` says "148 managed entry points"; `API-COVERAGE.md`
  reports 475 aliases included. Use `ImaginedNT.Host apis` as the authoritative
  list.

## 6. USER32 / win32k / other core DLL coverage

### 6.1 Where the Win32 GUI path stops

A normal NT GUI stack is roughly:

```
guest app
  -> user32.dll / gdi32.dll (user-mode Win32 API)
  -> win32u.dll (user-mode NtUser*/NtGdi* syscall stubs)
  -> win32kbase.sys / win32kfull.sys (kernel-mode USER/GDI implementation)
```

ImaginedNT currently implements only a small **user32 API subset inside the App
layer**. There is no `win32u.dll` interception, no `NtUser*`/`NtGdi*` dispatch and no
win32k session/desktop object model. `ImaginedNT.Host` itself does not register the
window APIs; only `ImaginedNT.App.Gui.GuiSession` adds `WindowApis` to its runtime,
with a XAML compositor attached through `WindowManager`.

Evidence:

- `src/ImaginedNT.App/Gui/WindowApis.cs` contains the user32 registrations.
- `src/ImaginedNT.App/Gui/WindowManager.cs` owns classes, windows, geometry, z-order,
  focus and per-thread message queues; it intentionally contains no UWP types.
- `BuiltinApis.Assemblies` does not include the App GUI assembly; the GUI is opt-in.
- `NativeSyscallMap` parses only `%SystemRoot%\System32\ntdll.dll`. `win32u.dll` is
  not part of the syscall map, so a direct `NtUser*`/`NtGdi*` syscall is not routed
  into the managed layer.

### 6.2 Host-export vs managed-registration snapshot

These counts are a rough diagnostic only: host DLL exports include forwarders,
internal/undocumented APIs and legacy aliases, and some managed APIs have aliases in
other modules.

| Module | Host exports | Managed registrations (source scan) | Notes |
|---|---:|---:|---|
| `user32.dll` | 1046 | 94 | Window manager subset only; 92 names match exactly |
| `gdi32.dll` | 700 | 0 | No GDI/DC/bitmap/font/path/region implementation |
| `win32u.dll` | 1555 (767 `NtUser*`, 535 `NtGdi*`) | 0 | No Win32k syscall routing |
| `win32kbase.sys` / `win32kfull.sys` | kernel drivers | 0 | Not loaded; no win32k kernel object model |
| `dwmapi.dll` | 44 | 13 | Composition is simulated; thumbnail/timing APIs return `E_NOTIMPL` |
| `advapi32.dll` | 757 | 34 | Mostly registry; no services/LSA/security/event-log/crypto surface |
| `kernel32.dll` | 1487 | 180 + 31 alias entries | Broad but still far from complete |
| `kernelbase.dll` | 1933 | 0 direct | Reached through `kernel32` aliases/host forwarding |
| `ntdll.dll` | 2522 | 65 (34 `Nt*`) | Core NT syscall surface is mostly unmanaged |
| `ole32.dll` / `combase.dll` | 252 / 466 | 25 | In-process COM subset only |
| `oleaut32.dll` | 417 | 9 | BSTR helpers only; no VARIANT/SAFEARRAY/IDispatch/typelib |
| `shell32.dll` | 475 | 0 | No shell namespace, ShellExecute, file dialogs, known folders |
| `shlwapi.dll` | 375 | 0 | No path/URL/registry string helpers |
| `comctl32.dll` | 119 | 0 | No common controls, property sheets, task dialogs, image lists |
| `uxtheme.dll` | 85 | 0 | No visual styles/themes |
| `ws2_32.dll` | 195 | 0 | No Winsock/network stack integration |
| `bcrypt.dll` | 60 | 0 | No CNG/BCrypt crypto |
| `userenv.dll` | 59 | 0 | No user profile/environment hive APIs |
| `rpcrt4.dll` | 553 | 0 | No RPC runtime or marshaling |

### 6.3 USER32 gaps

Implemented subset:

- Class registration: `RegisterClass*`, `UnregisterClass*`, built-in
  `STATIC`/`BUTTON`/`EDIT`/`COMBOBOX`/`LISTBOX`/`SCROLLBAR`.
- Window lifetime: `CreateWindowEx*`, `DestroyWindow`, `DefWindowProc*`,
  `CallWindowProc*`.
- Show/move/z-order: `ShowWindow*`, `UpdateWindow`, `MoveWindow`, `SetWindowPos`,
  geometry queries.
- Text/state: `Set/GetWindowText*`, `GetClassName*`, `Set/GetWindowLong*`,
  `EnableWindow`, `IsWindow*`, `Get/SetParent`.
- Message loop: `GetMessage*`, `PeekMessage*`, `TranslateMessage`,
  `DispatchMessage*`, `Post/SendMessage*`, `PostQuitMessage`, `WaitMessage`.
- Focus/activation: `Get/SetForegroundWindow`, `Get/SetActiveWindow`,
  `Get/SetFocus`, `BringWindowToTop`.
- Input state: `GetKeyState`, `GetAsyncKeyState`, `GetKeyboardState`,
  cursor/icon loading stubs.
- Timers: `SetTimer`/`KillTimer` only for window timers; callback timers are
  explicitly rejected (`WindowApis.cs:1013`).
- Invalidation: `InvalidateRect`, `ValidateRect`, `RedrawWindow`.
- Colours/beep: `GetSysColor`, `GetSysColorBrush`, `MessageBeep`.

Not implemented (major groups):

- Dialogs and message boxes: `DialogBoxParam*`, `DialogBoxIndirectParam*`,
  `CreateDialog*`, `EndDialog`, `MessageBox*`, `MessageBoxIndirect*`.
- Menus and accelerators: `CreateMenu`, `LoadMenu*`, `TrackPopupMenu*`,
  `SetMenu`, `EnableMenuItem`, `TranslateAccelerator*`.
- Hooks and WinEvents: `SetWindowsHookEx*`, `CallNextHookEx`,
  `UnhookWindowsHookEx`, `SetWinEventHook`, `UnhookWinEvent`.
- Clipboard: `OpenClipboard`, `Get/SetClipboardData`, `RegisterClipboardFormat*`,
  clipboard viewer chain.
- DPI/monitor/display: `SetProcessDPIAware`, `SetThreadDpiAwarenessContext`,
  `GetDpiForWindow`, `AdjustWindowRect*`, `MonitorFrom*`,
  `EnumDisplayMonitors`, `ChangeDisplaySettings*`.
- Window extras: `SetWindowPlacement`, `GetWindowInfo`, `SetWindowRgn`,
  `UpdateLayeredWindow`, `SetLayeredWindowAttributes`,
  `SetWindowCompositionAttribute`, scrollbar APIs.
- Sending variants: `SendMessageTimeout*`, `SendNotifyMessage*`,
  `SendMessageCallback*`, `PostThreadMessage` timeout semantics.
- Input injection/hotkeys: `SendInput`, `keybd_event`, `mouse_event`,
  `RegisterHotKey`, `MapVirtualKey*`, `ToUnicode*`.
- Window stations/desktops: `CreateWindowStation*`, `OpenWindowStation`,
  `CreateDesktop*`, `OpenDesktop`, `SetProcessWindowStation`.
- Thread/queue controls: `MsgWaitForMultipleObjects*`, `GetQueueStatus`,
  `AttachThreadInput`, `GetGUIThreadInfo`, `GetLastInputInfo`.
- IME/accessibility/UI Automation/raw input/touch/pointer messages.
- USER object security, cross-process hooks, session isolation.

### 6.4 GDI32 / win32k gaps

There is **no GDI implementation at all**:

- Device contexts: `GetDC`, `GetDCEx`, `GetWindowDC`, `ReleaseDC`, `CreateCompatibleDC`,
  `SaveDC`, `RestoreDC`.
- Painting: `BeginPaint`, `EndPaint`, `FillRect`, `FrameRect`, `BitBlt`,
  `StretchBlt`, `PatBlt`, `MaskBlt`, `AlphaBlend`, `TransparentBlt`.
- Primitives/paths: `MoveToEx`, `LineTo`, `Rectangle`, `Ellipse`, `Polygon`,
  `Polyline`, `Arc`, `Chord`, `Pie`, `BeginPath`, `EndPath`, `StrokePath`.
- Objects: pens, brushes, fonts, bitmaps, DIBs, palettes, regions, clipping.
- Text: `TextOut`, `ExtTextOut`, `DrawText`, `GetTextExtentPoint32`,
  `GetTextMetrics`, font enumeration/rasterization.
- Transforms: world transform, viewport/window mapping, metafiles, printing.
- `NtGdi*` system services, GDI handle tables, win32k object security and
  cross-process GDI semantics.

The current compositor deliberately draws window chrome/background with XAML and
only exposes text/child-control visuals; `BeginPaint`/`GetDC`/`FillRect` are absent
from the managed registry.

### 6.5 Other core-library gaps

- `advapi32.dll`: registry APIs are present, but services (`OpenSCManager`,
  `CreateService`, `StartService`), LSA/token/security, event logging, performance
  counters, crypto and registry security are not.
- `kernel32.dll`: missing groups include console internals, pipes/mailslots,
  memory-mapped files/sections, IO completion, thread-pool APIs, job objects,
  Toolhelp snapshots, debug APIs, fibers, locale/NLS, resource APIs, waitable
  timers and many process/thread information APIs.
- `ole32`/`combase`/`oleaut32`: in-process activation/vtables/BSTR subset only;
  no DCOM, proxies/marshaling, `IDispatch`/typelib, VARIANT/SAFEARRAY, OLE
  embedding, drag-and-drop, `RoInitialize`/WinRT.
- `shell32`/`shlwapi`: completely absent; no `ShellExecute`, known folders,
  file dialogs, shell namespace, path/URL helpers.
- `comctl32`/`uxtheme`: completely absent; no common controls, property sheets,
  task dialogs, image lists, visual styles.
- `ws2_32`/`bcrypt`/`userenv`/`rpcrt4`: completely absent; no network, CNG
  crypto, profile APIs or RPC runtime.
- API-set names are mapped only through a small alias table in the native
  forwarder. A missing API-set export either falls through to the host or fails
  under `--strict`.

### 6.6 Native-forwarding caveat

These gaps are not always visible in a default run. With
`ImportPolicy.ManagedFirst`/`AllowNativeForwarding`, a missing USER32/GDI/ADVAPI32
import can execute the real host implementation. That means:

- the call is outside the sandbox and outside the managed tracing model;
- behavior depends on the host Windows build;
- `--strict` / `managed-only` is required to make the gap fail closed;
- "not implemented" and "works on this machine" can both be true at the same time.

For a GUI guest, a missing `gdi32!BeginPaint` or `user32!MessageBoxW` will usually be
forwarded to the host and may render to an invisible/real desktop rather than to the
ImaginedNT compositor. This is the main reason the GUI personality is currently a
windowless window-manager subset, not a Win32 GUI compatibility layer.
## 7. Suggested priority

### P0 - semantic correctness / trust boundary

1. Add section objects and file mapping (`NtCreateSection`,
   `NtMapViewOfSection`, `NtUnmapViewOfSection`).
2. Add `NtDeviceIoControlFile` / `NtFsControlFile` and a minimal
   DeviceObject/IRP path, or fail closed with `STATUS_NOT_IMPLEMENTED`.
3. Implement common `NtQueryInformationProcess` / `NtQueryInformationThread`
   information classes.
4. Decide on `NtCreateUserProcess` support or explicitly document that only
   managed `CreateProcessW` children are supported.
5. Ensure every missing `Nt*` import a guest can reach is either managed or
   clearly rejected under `--strict`; do not rely on silent native forwarding.

### P1 - executive completeness

- APC/alertable waits, waitable timers, IO completion.
- PEB/TEB, loader initialization, SEH/VEH, TLS thread callbacks.
- Native registry API, token/security basics, job handles.
- ALPC/LPC or an explicit unsupported contract.

### P2 - compatibility expansion

- ETW/WER, GDI, 32-bit PE, NUMA/AWE/large pages, DCOM/proxies,
  full win32k.
- USER32 dialogs/menus/hooks/clipboard/DPI/monitor APIs and input injection.
- `comctl32`/`uxtheme` visual styles and common controls.
- `shell32`/`shlwapi`, `ws2_32`, `bcrypt`, `userenv`, `rpcrt4`.

## 8. How to regenerate the metric

```powershell
# Managed Nt* registrations (source scan)
$reg = Select-String -Path D:\code\ImaginedNT\src\*\*.cs `
  -Pattern '"(Nt[A-Z][A-Za-z0-9_]*)"' -AllMatches |
  ForEach-Object { $_.Matches } |
  ForEach-Object { $_.Groups[1].Value } |
  Sort-Object -Unique
$reg.Count

# Host ntdll Nt* exports (requires dumpbin)
$dumpbin = 'C:\Program Files\Microsoft Visual Studio\18\Insiders\VC\Tools\MSVC\14.52.37025\bin\Hostx64\x64\dumpbin.exe'
& $dumpbin /nologo /exports C:\Windows\System32\ntdll.dll > $env:TEMP\ntdll-exports.txt
$nt = Select-String -Path $env:TEMP\ntdll-exports.txt `
  -Pattern '^\s+\d+\s+[0-9A-F]+\s+[0-9A-F]+\s+Nt[A-Za-z0-9_]+' |
  ForEach-Object { ($_.Line -split '\s+')[-1] } |
  Sort-Object -Unique
$nt.Count
```

The exact `dumpbin` path and host ntdll build vary by machine; the count is
diagnostic only.

