# ImaginedNT architecture

## 1. Design goals and non-goals

**Goals**

1. Load a real Windows NT PE32+ image and make it execute on a host thread.
2. Patch the IAT so that every imported function enters a managed dispatch path first.
3. Let the managed layer decide, per import, whether to implement the API in C# or forward it
   to the host's real NT/user32 implementation.
4. Provide enough managed NT semantics to run non-trivial programs: virtual memory, objects,
   handles, files, threads, waits, exit codes, last-error state.
5. Intercept direct `syscall` instructions and route known syscalls back into the managed NT
   APIs.
6. Keep the design understandable enough to extend: APIs and syscalls are registrations, not
   giant switch statements.

**Non-goals (for this version)**

- A complete Win32/Win64 personality. There is no PEB/TEB setup, no CRT startup, no SEH/unwind
  metadata installation, no COM/GDI/USER32, no 32-bit images.
- A security sandbox in the kernel sense. See `LIMITATIONS-AND-SECURITY.md`.
- Running arbitrary Windows applications unchanged. The samples are CRT-free PE executables
  linked with `/NODEFAULTLIB` and a custom entry point.

## 2. Process and host model

```
ImaginedNT.Host process
 ├─ ImaginedRuntime
 │   ├─ ApiRegistry          148 managed entry points
 │   ├─ IatThunkAllocator    one 64 KiB RWX page, universal stub + 16-byte API stubs
 │   ├─ NativeForwarder      LoadLibrary/GetProcAddress cache
 │   ├─ SyscallDispatcher    virtual numbers + host ntdll numbers
 │   ├─ ObjectManager        session object namespace (\BaseNamedObjects, symlinks)
 │   ├─ thread registry      native host thread id -> owning ImaginedThread
 │   └─ ImaginedProcess[]    one entry per guest process (root + CreateProcess children)
 │       ├─ VirtualMemoryManager   tracked VirtualAlloc/Free/Protect/Query
 │       ├─ HandleTable            per-process handles (0/1/2 = stdio)
 │       ├─ SandboxFileSystem      guest C:\ -> host sandbox directory (shared by children)
 │       └─ ImaginedThread[]       guest threads hosted on C# managed threads
 └─ JobObjectController (optional, installed by the root process only)
```

A guest application and the processes it creates with `CreateProcessW` live in **one** host
process. Each `ImaginedProcess` has independent address-space bookkeeping and handles and shares
the runtime's session object namespace, so a named event opened in a child refers to the same
object as the parent's. All of them share the host virtual address space. Running a separate
application in a separate `ImaginedNT.Host` process remains the isolation boundary; the test
suite verifies three host processes run in parallel.

Every guest thread is a `System.Threading.Thread`. Its managed body calls the guest start address
as an unmanaged function pointer. When a managed import is dispatched, the runtime looks up the
current native thread id in the thread registry, obtains the owning `ImaginedThread`, and builds
the `ApiCallContext` for `thread.Process`. This is what lets one runtime dispatch APIs for several
coexisting guest processes; there is no global "current process" in the dispatch path.

## 3. Boot sequence

1. `PeImage.Load` parses the DOS/NT headers, section table, data directories, imports, exports,
   base relocations and TLS directory. All reads are bounds-checked; a malformed image throws
   `BadImageFormatException`.
2. `ImaginedProcess.LoadImage` invokes `PeLoader`:
   - `VirtualMemoryManager.MapImage` calls `VirtualAlloc(preferred, SizeOfImage,
     MEM_RESERVE|MEM_COMMIT, PAGE_EXECUTE_READWRITE)`. If the preferred base is unavailable it
     retries anywhere; a later relocation pass fixes up the image.
   - Copy `SizeOfHeaders` and every section's raw bytes.
   - If the actual base differs from `ImageBase`, apply base relocations (sections are copied
     first, so relocation writes are not overwritten).
   - Resolve every import. Managed imports get an IAT thunk address; native imports get the real
     export address; unresolved imports get a fail-thunk (or fail the load under `--strict`).
   - Write the resolved address into each IAT slot.
   - Apply final section protections: executable → RX, writable → RW, read-only data → R,
     headers → R. `--no-protect` keeps RWX for diagnostics.
   - Flush the instruction cache.
3. If `--syscalls`, the syscall interceptor scans executable sections, rewrites each `0F 05` to
   `CC CC`, and registers a vectored exception handler.
4. `ImaginedProcess.Run` creates the main guest thread as a C# `Thread` whose body starts at the
   guest entry point. The body registers the `ImaginedThread` with the runtime (so
   `GetCurrentThreadId`, last-error state and API dispatch work before the first instruction),
   then calls the entry point. `CREATE_SUSPENDED` and `CreateProcessW` use the same primitive:
   the managed thread is constructed but not started until `ResumeThread`/`Start`.
5. `ExitProcess` terminates according to `ProcessTerminationMode`:
   - `HostExit` (default): flush trace/output and `Environment.Exit(code)`.
   - `ThreadExit`: redirect the guest return address to the thread exit trampoline; the C# thread
     and its managed frames finish normally.
   - `ReturnToHost`: record the code and return; used only by tests for images that safely
     return after `ExitProcess` (a real program often has `int3`/unreachable code after it).
   - In a child process all three modes stop only that child (the child never terminates the
     host).
6. If the main thread does not finish within `RuntimeOptions.ExecutionTimeout` (`--timeout`),
   the process records exit code **124**, requests thread termination, and briefly waits for the
   guest to unwind. A managed guest thread that cannot be stopped with `TerminateThread` (the
   normal CLR case) unwinds at its next managed API call through the per-thread exit trampoline;
   other running threads are asked to stop, the trace is flushed, and `Run` returns. This is how
   the CPU-spinner sample (`samples/spin/spin.exe`) is stopped without hanging the host.
7. `CreateProcessW`/`A` resolve the guest image, create and register a new `ImaginedProcess` with
   the parent's sandbox root, map the image (relocating it if its preferred base is occupied),
   copy inheritable handles/`STARTF_USESTDHANDLES`, create the suspended main managed thread, and
   write `PROCESS_INFORMATION`. The child returns to the parent's guest code as two handles (and
   two virtual IDs); it does not return to the host CLI. A child `ExitProcess` stops only the
   child's own threads and signals its process object.

## 4. The IAT universal thunk

Every managed import points at a 16-byte stub:

```
49 BB <apiId:u64>   mov r11, apiId
E9 <rel32>          jmp universalStub
90                  nop padding
```

The universal stub saves the guest's integer argument registers, XMM0–3, the stack-argument
pointer and the return address, then calls a managed `[UnmanagedCallersOnly]` dispatcher:

```
push rbp
mov rbp, rsp
sub rsp, 0x80
mov [rbp-0x80], rcx        ; frame.Rcx
mov [rbp-0x78], rdx
mov [rbp-0x70], r8
mov [rbp-0x68], r9
lea rax, [rbp+0x30]        ; first stack argument (return addr + 32B shadow)
mov [rbp-0x60], rax
movdqu [rbp-0x58], xmm0
movdqu [rbp-0x50], xmm1
movdqu [rbp-0x38], xmm2
movdqu [rbp-0x28], xmm3
mov rax, [rbp+8]           ; return address
mov [rbp-0x18], rax
mov [rbp-0x10], r11        ; apiId
sub rsp, 0x20              ; Win64 shadow space for the call
mov rcx, r11               ; Dispatch(apiId, &frame)
lea rdx, [rbp-0x80]
mov rax, dispatcher
call rax
movdqu xmm0, [rbp-0x58]    ; restore (dispatcher may synthesize float returns)
...
leave
ret
```

Frame layout (`NativeCallFrame`, 128 bytes at `rbp-0x80`):

| Offset | Field |
|---|---|
| 0 | Rcx |
| 8 | Rdx |
| 16 | R8 |
| 24 | R9 |
| 32 | pointer to stack arguments |
| 40 | XMM0 (16 bytes) |
| 56 | XMM1 |
| 72 | XMM2 |
| 88 | XMM3 |
| 104 | return address |
| 112 | apiId |

The shadow-space `sub rsp, 0x20` is essential: without it the managed callee's home area
overlaps the saved frame and overwrites the arguments (the first implementation hit exactly
that bug).

`ApiCallContext` copies the first 16 arguments (4 registers + 12 stack slots) into managed
memory, exposes typed helpers, resolves handles, and logs the call/return through `TraceLog`.
The dispatcher catches all managed exceptions and turns them into `STATUS_NOT_IMPLEMENTED` so an
exception never unwinds through the guest's unannotated native frames.

## 5. Import policy

`ImaginedRuntime.ResolveImport` implements three policies:

| Policy | Behaviour |
|---|---|
| `ManagedFirst` (default) | Managed implementation if registered; otherwise native export; otherwise fail-thunk |
| `ManagedOnly` (`--strict`) | Managed only; unresolved imports fail the load |
| `NativeFirst` | Native export preferred; managed fallback if the host lacks the export |

Module names are normalized (case-insensitive, `.dll` optional) and API-set imports
(`api-ms-win-*`, `ext-ms-win-*`) are mapped to the classic `kernel32`/`ntdll` registrations.
Ordinal imports and forwarded exports fall back to `GetProcAddress`.

`NativeForwarder` caches `LoadLibrary`/`GetProcAddress` results. Native imports are patched
directly, so calls go straight to the host implementation and are not traced. This is the
explicit "let the real NT layer do it" decision point.

## 6. Direct syscall interception

Two cases are distinguished:

1. **Virtual syscall numbers** (`0x1000..`) implemented by `SyscallDispatcher`. These are used by
   the syscall demo and are fully controlled by ImaginedNT.
2. **Real host ntdll numbers**. `NativeSyscallMap` parses `%SystemRoot%\System32\ntdll.dll` with
   the same PE parser, walks every `Nt*`/`Zw*` export, reads the first bytes of the stub, and
   extracts the immediate from `mov eax, imm32`. This produces a number→name map for the
   *running* Windows build. When the trap sees such a number, it looks up the corresponding
   managed `ntdll!Nt*` registration, builds an argument vector from the NT syscall ABI
   (`r10, rdx, r8, r9, stack...`), calls the handler, and returns its NTSTATUS in `rax`.

The VEH handler only handles `EXCEPTION_BREAKPOINT` at addresses it patched. It advances
`RIP` by 2 (the two `CC` bytes) and resumes. Failures inside the handler are converted to
`STATUS_NOT_IMPLEMENTED` rather than deadlocking in the exception dispatcher.

Limitations: a byte-pattern scan cannot prove instruction alignment, so this is opt-in;
syscalls executed inside forwarded native DLLs are not patched (they reach the real kernel);
only managed-implemented syscalls are emulated.

## 7. Virtual memory

`VirtualMemoryManager` is a tracked façade over the host address space. Guests execute at real
host addresses, so the manager cannot use a separate page directory without an emulator or
hypervisor. It provides:

- reserve/commit/decommit/release with NT-style `MEM_*` flags and page granularity,
- `VirtualProtect` and protection metadata,
- `VirtualQuery` (native query plus ImaginedNT labels),
- safe guest-memory reads/writes through `ReadProcessMemory`/`WriteProcessMemory`, which return
  failure instead of raising an access violation for stale pointers,
- allocation tracking for cleanup in long-lived test hosts.

Reserved allocations are rounded up to the 64 KiB allocation granularity, matching Windows.
`NtAllocateVirtualMemory`, `NtFreeVirtualMemory`, `NtProtectVirtualMemory`,
`NtQueryVirtualMemory` and the kernel32 wrappers are implemented on top.

## 8. Object manager and handles

`ObjectManager` is a case-insensitive namespace rooted at `\`, owned by the runtime and shared by
every imagined process in that application (handles themselves remain per process). On startup it creates
`\BaseNamedObjects`, `\KnownDlls`, `\Sessions\0`, `\Device\ImaginedDrive`, `\??`, and the
symbolic links `\DosDevices -> \??` and `\??\C: -> \Device\ImaginedDrive`.

- Objects are reference-counted (`NtObject.Release`); the namespace owns one reference and each
  handle owns one.
- `SymbolicLinkObject` resolution follows links iteratively for up to 32 hops, including relative
  targets.
- `HandleTable` allocates 4-byte-aligned handles; handles 0/1/2 are the stdio console objects.
  Access masks are checked on `TryGet`; `DuplicateHandle`, `CloseHandle`, `NtClose`,
  `CompareObjectHandles` are implemented.
- Waitable objects implement `SupportsWait`/`IsSignaled`/`Wait`: events (manual/auto-reset),
  mutants (recursive, owner thread), semaphores, thread/process wrappers.

`ObjectAttributes` is read from guest memory (x64 `OBJECT_ATTRIBUTES` + `UNICODE_STRING`) and
supports root-directory-relative names. Named `NtCreateEvent`/`NtCreateMutant`/`NtCreateSemaphore`
reuse existing objects by name and return a new handle, matching NT open-if-exists semantics.

## 9. Filesystem sandbox

`SandboxFileSystem` maps guest paths into a host directory:

| Guest path | Host path |
|---|---|
| `\??\C:\a\b.txt` | `<sandbox>\a\b.txt` |
| `C:\a\b.txt` | `<sandbox>\a\b.txt` |
| `\a\b.txt` | `<sandbox>\a\b.txt` |
| `b.txt` (cwd `\a`) | `<sandbox>\a\b.txt` |
| `\Device\ImaginedDrive\a` | `<sandbox>\a` |

Every mapping goes through `Path.GetFullPath` and must remain under the canonical sandbox root.
UNC paths, `\\.\` devices, alternate data streams and `..` escapes are rejected. A process
created by `CreateProcessW` inherits the parent's sandbox root, so parent and child see the same
virtual `C:\`; each still owns its own handle table and current-directory string. This is enforced
for managed file APIs. Native forwarded file APIs are outside this jail; use `--strict` for a
managed-only load, or a more complete managed API set, if that matters.

`DiskFileObject` wraps a `FileStream`; `ConsoleFileObject` wraps stdin/stdout/stderr streams;
`DirectoryFileObject` represents a directory handle. `NtCreateFile`, `NtReadFile`, `NtWriteFile`,
`NtQueryInformationFile`, `NtSetInformationFile`, `NtFlushBuffersFile`, `CreateFileW`, `ReadFile`,
`WriteFile`, `SetFilePointer`, `GetFileSize`, `GetFileAttributesW`, directory creation/deletion,
`MoveFileExW` and `GetFullPathNameW` are implemented.

## 10. Threads and waits

`ImaginedProcess.CreateThreadAt` constructs an `ImaginedThread` (virtual id, start address,
parameter, owning process) backed by a C# `Thread`. The managed body publishes the native thread
id, registers (`native id -> ImaginedThread`) in the process and runtime, opens a termination
handle, and enters guest code through a small unmanaged launcher stub. The launcher saves the
nonvolatile Win64 registers (RBX/RBP/RSI/RDI/R12-R15 and XMM6-XMM15), remembers its stack
pointer, and calls the guest entry as `uint (ulong)`. The guest start routine receives its
parameter in `rcx`, matching the Win32 x64 ABI. The main image entry point is invoked with a zero
parameter; returning from it exits the process with code 0.

When guest code calls `ExitProcess`/`ExitThread` in a child process (or `ThreadExit` mode), the
managed dispatcher writes a per-thread exit trampoline address over the *guest* return address of
the API call. The universal stub then `ret`s to that trampoline instead of returning to guest
code. The trampoline restores the launcher's stack pointer and registers and returns into the
managed thread body, which finishes normally. This matters because calling `ExitThread` from
inside a reverse P/Invoke dispatch frame would terminate the CLR thread without unwinding managed
frames, which is unsafe and aborts the process under .NET.

- `GetCurrentThreadId`/`GetCurrentProcessId` return virtual IDs. `GetCurrentThread` and
  `GetCurrentProcess` return the NT pseudo handles.
- `CreateThread`, `NtCreateThreadEx`, `ResumeThread`, `ExitThread`, `GetExitCodeThread`,
  `TerminateThread` are managed. `CREATE_SUSPENDED` means the managed thread exists but has not
  been started yet.
- `WaitForSingleObject`/`WaitForMultipleObjects`/`NtWaitForSingleObject` dispatch to events,
  mutants, semaphores, thread/process objects, or files (files do not signal in this version).
  `ImaginedThread.Wait` joins the host managed thread, so a waiter cannot observe a thread as
  finished while its guest frames are still running. `ProcessObject.Wait` blocks on the
  process-exit monitor.
- `ExitProcess` in a child terminates only that child: it signals the process object, terminates
  the other child threads and redirects the caller's guest return address to the thread exit
  trampoline (see the launcher above). The root process follows `ProcessTerminationMode`
  (`HostExit`/`ThreadExit`/`ReturnToHost`).
- `NtWaitForSingleObject` converts NT relative/absolute 100 ns timeouts to milliseconds.
- `LastError` is stored per `ImaginedThread` and mirrored to the host thread's last-error slot.
  `GetLastError` reads the host slot (so a native-forwarded API's error is visible), while the
  dispatcher restores the slot after writing the trace, so trace I/O cannot clobber it.
- On timeout or disposal, a running managed guest thread is asked to stop and unwinds at its next
  managed API call through the per-thread exit trampoline. `TerminateThread` is attempted first as
  a last resort for guest code that never returns to the managed layer; a successful
  `TerminateThread` also marks the thread terminated immediately.

## 10.1 Process creation

`kernel32!CreateProcessW`/`A` and `WinExec` are managed implementations. The flow is:

1. Validate `PROCESS_INFORMATION`, read `STARTUPINFO`, `lpEnvironment`, `lpCurrentDirectory` and
   the creation flags from guest memory, and resolve the image:
   - `lpApplicationName` (or the first command-line token when it is null);
   - the guest sandbox (`SandboxFileSystem.MapPath`), the parent image directory, then the guest
     `PATH`;
   - host absolute paths are accepted only under the sandbox or the parent image directory.
2. `ImaginedRuntime.CreateProcess` allocates a virtual PID and a new `ImaginedProcess` with its
   own memory manager, handle table, file view and environment. The runtime's session object
   namespace and the parent's sandbox root are shared, so named objects are visible across the
   process boundary and `C:\` maps to the same host directory.
3. `ImaginedProcess.LoadImage` parses and maps the child image. Two processes may map the same
   image at different bases; an image with no relocations is legal at an alternate base as long
   as `IMAGE_FILE_RELOCS_STRIPPED` is not set (there are no fixups to apply).
4. `ApplyStartupHandles` copies inheritable parent handles (honouring `HANDLE_FLAG_INHERIT`) and
   `STARTF_USESTDHANDLES` objects before the child can execute.
5. `EnsureMainThread` creates the suspended C# main thread; process/thread objects are inserted
   into the parent's handle table and `PROCESS_INFORMATION` is written.
6. Unless `CREATE_SUSPENDED` was requested, the main thread starts. `ResumeThread` on the returned
   thread handle starts a suspended child.

Created children remain owned by the runtime until it is disposed. `--no-process` /
`AllowProcessCreation = false` makes creation fail with `ERROR_ACCESS_DENIED`. The process exit
monitor is signalled when the child's main thread exits (or `TerminateProcess` is called); this
runtime also terminates the child's remaining threads at that point. The runtime's object
namespace is shared by all of its processes, so named events/mutants/semaphores are visible to
the parent and its children; handles and their access masks remain per process. Named objects
stay alive for the runtime lifetime once created.

## 11. Native forwarding and API sets

`NativeForwarder` resolves a module with `GetModuleHandle` first and `LoadLibraryW` second, then
`GetProcAddress` by name or ordinal. It mirrors Windows' own loaded-module state, so forwarded
exports that are themselves forwarders still work. Modules stay loaded for the host lifetime.

An API assembly may also declare native exports with assembly-level `[NativeApi]` attributes.
These declarations are collected by `NativeApiScanner` into `ImaginedRuntime.NativeDeclarations`
and do not create a managed handler. The default `NativeApiPolicy.AllowAll` preserves the classic
forward-anything behavior; `NativeApiPolicy.DeclaredOnly` permits native forwarding only for
declared module/name pairs. The built-in CRT declaration set is
`ImaginedNT.Apis.Crt/CrtNativeApis.cs` (346 safe-native UCRT exports, each with a `ucrtbase!name`
alias). Ordinal-only imports are denied under `DeclaredOnly` because they have no auditable name.
This is an import-resolution policy, not a security boundary: arbitrary guest machine code can
still parse a loaded module and call an export directly, just as it can execute any mapped code.
Keep host-process isolation for untrusted guests.

The API-set mapping in `ApiRegistry.CandidateModules` is intentionally small: API-set names map
to `kernel32`, `ntdll`, and (for security API sets) `advapi32`. Extend this table when adding a new
personality (for example, `api-ms-win-core-file-l1-1-0` is covered by the `kernel32` fallback).

## 11.1 Guest DLL loading

Static imports and `LoadLibraryW/A` use the process's module table. They search explicitly
imported DLLs, the sandbox, the importing image's directory and the main image's directory before
falling back to the host loader. A guest DLL is loaded with the same `PeLoader` path as the main image:

1. parse and validate the DLL,
2. map it at its preferred base (relocate if occupied),
3. publish the mapping before resolving dependencies, so circular imports reuse it,
4. patch its imports with the current policy and redirect Partially Imagined export entries,
5. apply section protections,
6. initialize dependencies and call `DllMain(HMODULE, DLL_PROCESS_ATTACH, 0)`
   on the guest thread. DLLs loaded with the main image are initialized before its entry point.

`GetProcAddress` / `GetModuleHandleW` / `GetModuleFileNameW` consult this module list first.
`FreeLibrary` deliberately keeps modules resident: unloading guest code while threads or
callbacks could still reference it is not safe in this in-process model.

`PartiallyImaginedApiAttribute` derives from `VirtualApiAttribute` and is discovered by the same
scanner. Its registrations set `ApiRegistration.IsPartiallyImagined`; these exports always use
the managed implementation, including under `NativeFirst`. Other exports retain the native code
in the guest mapping and follow the normal forwarding policy. Partial modules use exact module
lookup instead of inheriting similarly named APIs from unrelated DLLs.

`ImaginedRuntime.ResolveExport` handles mapped and host modules, named/ordinal exports and export
forwarders. A shared native entry has one managed replacement across all its names and ordinals;
different handlers claiming that entry fail loading. `PeExportRedirector` writes one `INT3` byte
at each replaced function entry. Its vectored handler only redirects RIP to the managed thunk,
preserving the caller's argument registers and stack. This also captures relative calls and
function pointers inside a guest DLL without overwriting short functions or adjacent exports.
Each process owns its redirectors and removes them before freeing its DLL mappings. Inlined
code and DLLs loaded by the host Windows loader remain outside this boundary.

## 12. Concurrency, GC and native callbacks

- `IatThunkAllocator` keeps every managed thunk handler in a static
  `ConcurrentDictionary<ulong, Entry>`. Entries are rooted until the allocator is disposed, so
  the GC cannot collect the closures while native code can still call them.
- The dispatcher runs on whatever guest thread invoked the API. Guest threads are C# managed
  threads, and the runtime's `ConcurrentDictionary` mapping native thread id to
  `ImaginedThread` is the single source of truth for the current process. Managed exceptions
  never cross the native boundary.
- `TraceLog`, `HandleTable`, `ObjectManager`, media managers and `VirtualMemoryManager` use locks
  around mutable state. The runtime process list, thread registry and per-process thread maps are
  lock-protected or concurrent.
- Disposal order is children → root process (handles → memory per process) → shared namespace →
  thunk page. Do not dispose a runtime while a guest thread is still executing;
  `ImaginedProcess.Dispose` terminates non-current running threads first in this version.
- A child process may outlive its parent (Windows semantics) until the runtime is disposed. The
  host CLI then terminates the remaining children; there is no separate reaper thread.
- `TerminateThread` is still a last resort: a thread killed while holding a CLR or trace lock can
  leave that lock held. Normal termination is `ExitProcess`/`ExitThread`, a returning entry point,
  or a cooperative unwind at the next managed API call; the watchdog prefers the cooperative path
  and only force-stops a guest that never returns to the managed layer.
- The host is single-use for a root application: `ExitProcess` in `HostExit` mode terminates the
  host, which is the cleanest way to dispose managed threads executing guest code.

## 13. Extending the layer

**Add a managed API**

Place the implementation in an API library referencing `ImaginedNT.Core` (or
`ImaginedNT.Apis.Common` for shared helpers). Annotate a static method with the module and
entry point it implements. Only explicitly selected assemblies are scanned:

```csharp
[VirtualApi("kernel32.dll", "MyApi", ApiCategory.System, "ntdll!NtMyApi")]
private static ulong MyApi(ApiCallContext ctx)
{
    var value = ctx.Arg(0);
    ctx.Runtime.Trace.Emit(TraceKind.Api, "custom call"); // optional
    return 1;                                             // BOOL / NTSTATUS / pointer
}
```

The method must be `static`, take a single `ApiCallContext` and return `ulong`. Apply the attribute
more than once when the same implementation backs several modules; use the extra `module!name`
arguments for alternate spellings. Duplicate or malformed declarations fail startup with the
offending method in the message. Then add the API to `README`/`docs/API-COVERAGE.md` if it is
user-visible. If the API has more than four arguments, read them with `ctx.Arg(4...)`; the
universal stub already captures stack arguments. Implementations must use `ctx.Process` /
`ctx.Thread` rather than caching a global process: the same import thunk can be called by any
guest process that loaded the image.

**Select API libraries and backends**

The dependency direction is `Host -> Apis -> Apis.* -> Apis.Common -> Core -> Backends.Windows
-> Abstractions`. `Core` also references `Abstractions` directly. Existing namespaces remain
`ImaginedNT.Core.*`; assembly boundaries determine which implementation libraries are deployed.

`ImaginedNT.Abstractions` contains NT constants/status codes, `NtObject`, `FileObject`,
`INativeForwarder`, virtual API metadata, PE parsing and tracing. `ImaginedNT.Backends.Windows`
contains the current concrete filesystem, object, memory, native forwarding, PE mapping and
machine-code thunk implementations. Process/thread execution and syscall interception remain
in `Core` and currently require Windows x64.

The CLI references `ImaginedNT.Apis` and uses `BuiltinApis.CreateRuntime(options)` to load all
built-in libraries. A consumer can reference just the API libraries it needs and select them:

```csharp
using ImaginedNT.Core.Runtime;

using var runtime = new ImaginedRuntime(options, new[]
{
    typeof(SystemApis).Assembly,
    typeof(FileApis).Assembly,
    typeof(MemoryApis).Assembly,
    typeof(SynchronizationApis).Assembly
});
```

`new ImaginedRuntime(options)` creates an empty API registry. Before loading guest code,
`runtime.RegisterApis(typeof(MyApis).Assembly)` adds another library. Registering the same
assembly again is ignored; conflicting entry points across different libraries fail explicitly.
Selecting a library does not automatically register other libraries, so include all modules
needed by the guest's imports. The PE inspection command only needs `ImaginedNT.Abstractions`.

Set `RuntimeOptions.NativeForwarderFactory` to construct a custom `INativeForwarder` using the
runtime's `TraceLog`. The runtime owns and disposes the returned instance. Other backend services
continue to use the Windows implementations in this split.

**Add a syscall**

- Virtual: register a number in `SyscallDispatcher` (`0x1000..` is reserved for ImaginedNT).
- Real: register a managed `ntdll!NtXxx` API; `NativeSyscallMap` will find the host build's
  number and route it automatically.

**Map another module**

Register imports under the classic module name; add module aliases/API-set prefixes in
`ApiRegistry.CandidateModules` if the guest uses a different module name.

**Add a PE feature**

The parser/mapper is deliberately separate: add the data-directory parser in `PeImage`, then
apply it in `PeLoader` before protections are set. Section protections must be applied last.
