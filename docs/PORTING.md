# Porting ImaginedNT to another host OS

The project is layered so that most of it is independent of Windows. This document records what
is portable, what is Windows-specific, and a realistic route to a Linux/macOS or emulator
backend.

## 1. Portable layers

| Layer | Portability | Notes |
|---|---|---|
| `Pe/` parser, sections, imports, exports, relocations, TLS directory | ✅ | Pure managed code, no Windows calls |
| `PeLoader` mapping/relocation/IAT patching | ⚠️ | Written against `VirtualMemoryManager`; the mapper itself is portable |
| `Objects/` object manager, handles, events/mutants/semaphores waits | ✅ | Uses .NET synchronization primitives |
| `Io/` sandbox filesystem, `FileObject` | ✅ | Path/path-jail logic uses `System.IO`; path syntax is Windows-flavoured |
| `Runtime/` API registry, policies, process/thread model | ⚠️ | Process/thread creation and context switching need a backend |
| `BuiltinApis` | ✅ | C# semantics; a few APIs return Windows-shaped data |
| `Thunks/` x64 stubs | ❌/✅ | x64 machine code is the same on Linux/macOS x64, but the ABI differs (System V vs Win64) and callbacks must follow the host ABI |
| `Syscalls/` VEH interception | ❌ | Windows-specific; replace with an emulator trap, `ptrace`, signal handling, or binary translation |
| `NativeForwarder` | ❌ | `LoadLibrary`/`GetProcAddress` are Windows-specific; replace with host shared-library shims |
| `JobObjectController` | ❌ | Use cgroups/rlimits/setrlimit/job equivalents |
| `VirtualMemoryManager` | ⚠️ | Replace `VirtualAlloc`/`VirtualProtect`/`ReadProcessMemory` with `mmap`/`mprotect`/`process_vm_readv` (or an emulator) |
| Host CLI | ⚠️ | CLI parsing is portable; process spawning/exit codes differ |

The API registry, object manager, sandbox, PE parser, and most managed API semantics are
already platform-neutral. The execution backend is the part that needs real work.

## 2. Execution options

### A. Same-ISA host with an ABI shim (Linux x64 example)

Guest x86-64 code can execute directly on a Linux x86-64 host, but all imported functions must be
emulated: the Win64 calling convention (rcx/rdx/r8/r9 + 32-byte shadow space) must be translated
to the host ABI, or every IAT thunk must be a custom assembly trampoline. The universal thunk
approach still works if the thunk is written for the host ABI. Windows APIs become Linux shims:

- `NtAllocateVirtualMemory` → `mmap`
- `NtCreateFile` → `openat` inside the sandbox
- events/mutants/semaphores → futexes/condvars
- `NtCreateThreadEx` → `clone`/pthread, with a TEB-like per-thread structure

The VEH cannot be used. Either:
- run the guest under a small emulator that decodes `syscall` and memory accesses, or
- patch direct syscalls to a host trap (`int3`/`ud2`/a call into a trampoline) and decode them
  in a signal handler, or
- rely on `ptrace`/`seccomp` to intercept syscalls (but the guest would be issuing **Windows**
  numbers, so they cannot be passed through; they must be emulated).

### B. Emulator/interpreter backend

If the goal is genuinely cross-platform and safe execution, run the guest under a user-mode CPU
emulator (or JIT) that provides virtual memory, register state, and a syscall trap. In that model:

- `VirtualMemoryManager` becomes a virtual address space, not a façade over the host.
- The IAT thunk is not machine code at all: it is an emulator hook.
- The VEH is unnecessary; `syscall` is decoded by the emulator.
- Native forwarding becomes explicit host shims, never raw host code.
- `ImaginedProcess`/`ImaginedThread` map to emulator contexts, not host threads.

This is the only route that offers a real security boundary. It is a much larger project: you
need an x86-64 decoder (or a library such as Iced), memory access hooks, an ELF-like loader for
PE, and a consistent Win64 ABI emulation for every API.

### C. Wine-style hybrid

Use parity with Wine: execute guest code directly, forward as much as possible to host libraries
(which may include reimplementations), and isolate with host primitives (namespaces, cgroups,
seccomp, AppContainer). ImaginedNT's managed layer can be the "policy + object + file" glue while
the host provides the ABI shims. This is pragmatic for compatibility, but in-process code remains
trusted at the host level.

## 3. Concrete port checklist

1. Introduce `IExecutionBackend`:
   - `MapMemory`, `Protect`, `Read`, `Write`, `CreateThread`, `Wait`, `ExitThread`.
   - Windows backend: current code.
   - Linux backend: `mmap`/`mprotect`/`process_vm_readv`/pthread; emulator backend: virtual.
2. Move Windows P/Invoke out of `Memory`/`Runtime` into `Platform/Windows`.
3. Rework `Thunks` for the host ABI (or make it emulator-only).
4. Replace `NativeForwarder` with a per-OS shim registry (`libc`/`libpthread`/etc.).
5. Replace `SyscallInterceptor` with the backend's syscall trap.
6. Replace `JobObjectController` with cgroups/rlimits or a host supervisor.
7. Add path translation for the host filesystem (`V:`, `/`, …) while keeping the guest namespace.
8. Add an ELF-style loader if you want to run on non-x64 hosts; otherwise use an emulator.
9. Run the existing 32 tests against the new backend; the PE/object/file tests should not change.

## 4. What would remain Windows-only

- Real Windows syscall semantics and SSDT numbers (the map is build-specific).
- Native forwarding to `kernel32`/`ntdll`/`user32`.
- Windows-specific structures (`PEB`, `TEB`, `CONTEXT`, `EXCEPTION_RECORD`, `SYSTEM_INFO`).
- TLS/SEH/unwind/PEB/TEB setup.
- `GetSystemTimeAsFileTime` and other time formats can be emulated portably.

## 5. Recommendation

For a first port, target **Wine-style**: keep direct execution on the same ISA, implement the
backend for Linux, and port the object/file/sandbox APIs first. Use an emulator only when the
security boundary is the primary goal; that is a different product with a different performance
profile.
