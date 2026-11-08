# Limitations and security model

> **ImaginedNT is not a security sandbox for hostile native code.** It is a managed NT
> personality and a research platform. This document states exactly what is and is not enforced.

## 1. Threat model

The intended deployment is: a semi-trusted Windows PE (a test binary, a legacy utility, a
plugin) is loaded into a dedicated `ImaginedNT.Host` process, with managed file APIs jailed to a
per-process sandbox and with an explicit import policy. The host process can be constrained by
an OS job object and, in a hardened deployment, by an AppContainer/restricted token or a VM.

The layer assumes **the guest code is arbitrary native x64 machine code**. It does not assume the
guest respects the API layer.

## 2. What is enforced

| Control | Enforcement |
|---|---|
| Guest application containment | Each application runs in its own host process; killing/limiting the host contains it and all of its `CreateProcess` children |
| Managed process creation policy | `AllowProcessCreation` / `--no-process` rejects `CreateProcessW`/`A` and `WinExec` |
| Managed file path jail | `SandboxFileSystem` canonicalizes guest paths and rejects escapes, UNC, device namespaces, ADS |
| Managed import policy | `ManagedOnly` / `--strict` refuses unresolved imports and native fallback |
| Managed API surface | Dangerous APIs can be left unregistered or replaced with fail-thunks in strict profiles |
| Direct syscall emulation | `--syscalls` traps `0F 05` and only dispatches implemented managed syscalls |
| Resource backstop | `--job` applies job-object memory/process limits and kill-on-close |
| Object/handle access masks | Managed `HandleTable` validates requested access bits |
| Guest memory safety | Managed reads/writes use `ReadProcessMemory`/`WriteProcessMemory`, avoiding AVs on invalid pointers |

## 3. What is **not** enforced

1. **Same-address-space execution.** Guest code executes in the host process. It can read and
   write the managed runtime's heap, the host's loaded DLLs, and ImaginedNT's own data structures
   with ordinary memory instructions. The managed layer cannot prevent this.
2. **Native forwarding bypasses the jail.** If an import is forwarded to `kernel32!CreateFileW`
   (or any other native API), the guest gets the host process's real token and full filesystem
   access for that call. The managed sandbox only covers managed APIs. Use `--strict` and a
   complete managed API set if file confinement is required.
3. **Unregistered native APIs.** Any API imported from a DLL that is not in the managed registry
   is patched directly to the host export by default. This includes `NtCreateUserProcess`,
   token-based process creation, registry, network, and UI APIs.
   `CreateProcessW`/`CreateProcessA`/`WinExec` are managed, but the children they create are
   **not** isolated from each other or from the root: they share the host address space, the
   runtime, the thunk arena, the object namespace and the sandbox root, and guest code can
   read/write all of it.

   `NativeApiPolicy.DeclaredOnly` narrows **import resolution** to the audited `[NativeApi]`
   declarations, but it is not a sandbox: guest machine code can still parse a loaded module and
   call an export directly. Use a separate host process for isolation.
4. **Direct syscalls inside native DLLs.** The VEH patch only rewrites `syscall` instructions in
   the guest image. If a forwarded ntdll function executes a syscall, it reaches the real kernel.
5. **Kernel attack surface.** The guest can use whatever syscall numbers the host kernel accepts
   if it reaches them (see 4); the managed trap is not a kernel filter.
6. **Filesystem symlinks/reparse points.** The jail checks canonical host paths, but a symlink
   inside the sandbox that points outside could still be followed by managed file opens. Do not
   place untrusted reparse points in the sandbox; a future version should reject them.
7. **Environment/process metadata.** The guest sees a virtual PID/TID and sandbox paths, but
   native calls can query the real process/OS state.
8. **Denial of service.** Guest code can spin, allocate unlimited managed memory, block threads,
   or call `TerminateThread`-style APIs. Use `--timeout` (the watchdog terminates the guest with
   code 124), `--job`, and process isolation. The included `samples/bin/spin.exe` is an
   intentionally CPU-bound fixture for testing this path.
9. **Confidentiality.** The guest can read command lines, environment variables, and memory of
   the host process. Do not run secrets in the same host process.

## 4. Execution and termination caveats

- The guest image executes at real host virtual addresses. `NtAllocateVirtualMemory` allocates
  real host pages. A future emulator/hypervisor mode could provide a separate address space, but
  this version does not.
- The generated guest trampolines do not support CET shadow-stack or exception-continuation
  validation. The Host, App and test apphosts set `CETCompat=false`; this opts these executables
  out of CET rather than changing system-wide protection. Runtime initialization checks the
  actual process policy and rejects active shadow-stack/context validation before allocating
  guest execution state. An embedding executable must use the same setting. See
  [the .NET CET compatibility change](https://learn.microsoft.com/en-us/dotnet/core/compatibility/interop/9.0/cet-support).
- Guest DLL entry points run synchronously on the loading thread. Managed guest threads use a
  nested native launcher boundary for `DllMain`, so intercepted fatal exits return through the
  innermost native call before leaving any managed API frames.
- Executable sections of guest EXEs and DLLs are decoded with Iced; decoded `int 29h`
  instructions are replaced with breakpoint traps before execution. These traps and known
  fatal APIs (`RaiseFailFastException`, `RtlFailFast*`, CRT `abort`/`terminate` and Watson
  handlers) record the failure and stop the virtual process without applying `HostExit`.
  All guest VEH callbacks have a native exception-code/address filter before entering CLR;
  unrelated host exceptions return `EXCEPTION_CONTINUE_SEARCH` without a managed callback.
  Under a native debugger, the replacement breakpoint may pause execution before VEH handles it.
  This covers loaded guest code on managed guest threads, not JIT/self-modifying code or
  fail-fast inside host DLLs. Ordinary access violations, corrupted host memory and native
  locks abandoned by a fatal exit are not isolated by this mechanism.
- Guest threads are C# managed threads. `ExitProcess`/`ExitThread` redirect the guest return
  address to a per-thread trampoline that restores the launcher's stack and nonvolatile Win64
  registers and returns into the managed thread body. This avoids terminating a CLR thread from
  inside a managed dispatch frame; the process/thread exit state is recorded before the
  trampoline runs.
- `ImaginedProcess.Dispose` asks running guest threads to stop. A thread that calls managed APIs
  unwinds through its per-thread exit trampoline; a runaway guest that never returns to the
  managed layer may be force-stopped with `TerminateThread`, which can leave CLR or trace locks
  held and is only safe at process teardown. A child that is still running when the host CLI exits
  is handled the same way.
- Intercepted fatal exits request cooperative termination of the remaining guest threads.
  If a thread cannot stop, its process memory and shared runtime resources are retained rather
  than freed while it can still execute.
- Child processes share the sandbox root and the runtime's session object namespace with their
  parent, so named events/mutants/semaphores can coordinate them. Handles and access masks remain
  per process. Named objects persist for the runtime lifetime; there is no per-process teardown of
  the namespace when a process exits.
- A root `ExitProcess` in `HostExit` mode ends the host and all imagined children; a child
  `ExitProcess` never terminates the host.
- Process lifetime is tied to the main guest thread: when it returns or is terminated, the
  runtime terminates the remaining threads and signals the process object. Windows would keep a
  process alive while any non-main thread is still running.
- `ExitProcess` in `HostExit` mode calls `Environment.Exit` from a managed reverse-P/Invoke
  callback. This terminates the host process; the host CLI flushes the trace and stdout first.
- `ReturnToHost` is a test-only mode. A real PE often has unreachable `int3` after `ExitProcess`;
  returning to it would raise a breakpoint. Tests that need in-process completion use the runner
  subprocess or images that return safely.

## 5. Unsupported features (current)

- 32-bit PE (PE32/i386). The mapper only executes AMD64 images.
- PEB/TEB setup, process parameters, environment block layout, command-line block.
- CRT startup, `_initterm`, security cookies, `/GS` support (samples are built `/GS-`).
- SEH/unwind metadata registration; managed exceptions never cross into guest code.
- TLS callback invocation (the directory is parsed and callback VAs are exposed, but callbacks
  are not run because there is no TEB).
- Delay-load imports, bound imports, API-set redirection beyond the small mapping table.
- Loader notifications, DLL loading of guest DLLs (`LoadLibraryW` forwards to the host, not the
  managed loader), `DllMain`.
- Section objects (`NtCreateSection`/`NtMapViewOfSection`), file mapping, async I/O, IO completion
  ports, APC delivery, alertable waits.
- Native registry (`Nt*`), registry ACLs/notifications/transactions, COM, USER32/GDI,
  networking, services, tokens/privileges, security descriptors. Common `Reg*` APIs use
  `ImaginedRegistry` with JSON storage inside the sandbox.
- `NtCreateUserProcess`/`RtlCreateUserProcess`, token-based creation (`CreateProcessAsUserW`,
  `CreateProcessWithTokenW`, `CreateProcessWithLogonW`), and job objects as guest-visible handles.
  `CreateProcessW`/`A` and `WinExec` create managed children in the same host; real OS-process
  isolation still requires launching an additional `ImaginedNT.Host` process.
- Real syscalls not registered as managed NT APIs: they return `STATUS_NOT_IMPLEMENTED` rather
  than being executed by the kernel.
- 64-bit `CONTEXT`/exception delivery for guest faults: a guest access violation takes down the
  host.

## 6. Hardening recommendations

1. Run each guest in a fresh host process and a fresh sandbox directory.
2. Use `--strict` with a managed API set that covers the application; audit every native
   forward (`--verbose`/`--trace` shows `[native]` resolutions). Add `--no-process` unless the
   application genuinely needs `CreateProcessW`/`WinExec`.
3. Add `--job` and consider a restricted token/AppContainer, low integrity, and a separate
   Windows user for the host. The managed layer does not require elevation.
4. Do not run as administrator. Do not put secrets in the host environment or command line.
5. Treat the sandbox directory as untrusted output; scan/clean it after execution.
6. Use a network-isolated VM for genuinely untrusted code. The managed layer is not a substitute
   for VM/AppContainer isolation.
7. Keep `--syscalls` enabled only when you need direct-syscall emulation; the byte scan can
   theoretically mis-patch data that looks like `0F 05`.

## 7. Reporting a security issue

This is a research prototype. If you use it beyond experiments, treat any crash or unexpected
native forwarding as a potential sandbox escape and fix the policy before exposing it to
untrusted input.
