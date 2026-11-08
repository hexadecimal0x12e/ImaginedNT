# ImaginedNT

> [!WARNING]
> 
> Still under development ~~vibing~~. 

A User-State NT Kernel **simulator**, which can run real Windows NT PE images (including Microsoft Windows, Wine and ReactOS) in a managed sandbox. 

## What it is

ImaginedNT loads a real Windows NT PE image, maps it into a managed address space, applies
base relocations, resolves and patches the IAT, and routes every intercepted API call or direct
`syscall` into the managed layer **`ImaginedNT.Core`**. The layer either implements the call in
C# or forwards it to the host's real `kernel32`/`ntdll`/other DLLs.

This is a working proof-of-concept, not a drop-in Wine replacement. It runs real
MSVC-generated, CRT-free PE executables end to end, including a multi-threaded guest, managed
file I/O, virtual memory, IAT shadowing, base relocation, native forwarding, and direct syscall
interception.

## Quick start

```powershell
# Build the managed layer and runner (requires .NET 10 SDK)
dotnet build ImaginedNT.slnx

# Build the native sample PEs (requires MSVC + ml64; optional if samples/bin exists)
pwsh -File samples/build-samples.ps1

# Run a real PE through the managed NT layer
dotnet src/ImaginedNT.Host/bin/Debug/net10.0/ImaginedNT.Host.dll run samples/bin/hello.exe --strict --verbose
# -> Hello from ImaginedNT (managed NT layer)!
# -> guest exit code 42

# Run the minimal cmd.exe-style guest shell (dir/cd/type/copy/mkdir/run/exit)
dotnet src/ImaginedNT.Host/bin/Debug/net10.0/ImaginedNT.Host.dll run samples/bin/shell.exe --strict --sandbox sandbox\shell
# One-shot command mode (no stdin, exits instead of waiting for input):
dotnet src/ImaginedNT.Host/bin/Debug/net10.0/ImaginedNT.Host.dll run samples/bin/shell.exe --strict --sandbox sandbox\shell -- /c "dir"
# or script it from stdin:
"dir`nrun hello.exe`nexit" | dotnet src/ImaginedNT.Host/bin/Debug/net10.0/ImaginedNT.Host.dll run samples/bin/shell.exe --strict --sandbox sandbox\shell

# Run the real Windows command interpreter (builtin commands and pipe stdin work;
# external programs still depend on the APIs their own images import)
"ver`ndir`necho hello`nexit" | dotnet src/ImaginedNT.Host/bin/Debug/net10.0/ImaginedNT.Host.dll run c:\windows\System32\cmd.exe --sandbox sandbox\cmd
dotnet src/ImaginedNT.Host/bin/Debug/net10.0/ImaginedNT.Host.dll run c:\windows\System32\cmd.exe -- /c "echo hello"

# The shell is interactive by default and waits on stdin when no terminal/pipe input is available.
# Use `-- /c ...` or pipe commands for non-interactive runs.  There is no default timeout; pass
# `--timeout <seconds>` to enable the watchdog for long-running guests.

# Trap and emulate a direct syscall instruction
dotnet src/ImaginedNT.Host/bin/Debug/net10.0/ImaginedNT.Host.dll run samples/bin/syscall.exe --strict --syscalls --verbose
# -> trapped syscall 0x1000 (NtImaginedTest) ... exit 42

# CPU-bound guest: the watchdog terminates it and reports exit code 124
dotnet src/ImaginedNT.Host/bin/Debug/net10.0/ImaginedNT.Host.dll run samples/bin/spin.exe --strict --timeout 2 --verbose

# CreateProcessW: the parent creates a suspended child, resumes it, waits and checks its exit code
dotnet src/ImaginedNT.Host/bin/Debug/net10.0/ImaginedNT.Host.dll run samples/bin/spawn.exe --strict --verbose

# Inspect a PE without executing it
dotnet src/ImaginedNT.Host/bin/Debug/net10.0/ImaginedNT.Host.dll inspect samples/bin/hello.exe

# List the managed API surface
dotnet src/ImaginedNT.Host/bin/Debug/net10.0/ImaginedNT.Host.dll apis

# Run the test suite
dotnet tests/ImaginedNT.Tests/bin/Debug/net10.0/ImaginedNT.Tests.dll

# Build an instrumented copy that logs every managed method entry (debug diagnostics)
pwsh -File scripts/build-method-trace.ps1
dotnet artifacts/method-trace/ImaginedNT.Host.dll run samples/bin/hello.exe --strict --trace-methods

# Log every native-forwarded call with arguments (useful when a guest blocks inside a host DLL)
dotnet src/ImaginedNT.Host/bin/Debug/net10.0/ImaginedNT.Host.dll run c:\windows\System32\cmd.exe --trace-native --verbose
```

See [docs/METHOD-TRACING.md](docs/METHOD-TRACING.md) for filters, file output and the weaver's
options.

Guest drives can map to separate host directories. `--sandbox` backs C:; repeat
`--drive <letter>=<directory>` to add drives or override C:.

```powershell
ImaginedNT.Host run app.exe --sandbox sandbox\system --drive D=D:\GuestData --drive E=E:\GuestTools
```

Volume information, disk space, drive enumeration, DOS device queries and volume GUID queries
use simulated metadata, including under `--native-first`. Each drive has a stable synthetic
serial number and volume GUID; defaults report 128 GiB total and 64 GiB free. These are fixed
reported values, not storage quotas, and do not track host free space or guest writes.
`NtQueryVolumeInformationFile` supports volume, size, device, attributes, full size and sector
size information; other information classes return `STATUS_INVALID_INFO_CLASS`.
The Win32 API roles follow [Obtaining Volume Information](https://learn.microsoft.com/en-us/windows/win32/fileio/obtaining-volume-information).

Managed hosts can customize each drive's metadata:

```csharp
options.Drives['D'] = new ImaginedNT.Core.Io.VirtualDriveOptions(@"D:\GuestData")
{
    Label = "Data",
    SerialNumber = 0x12345678,
    TotalBytes = 256UL * 1024 * 1024 * 1024,
    FreeBytes = 100UL * 1024 * 1024 * 1024
};
```

Guest environment variables are isolated from the host. Win32 environment APIs (ANSI and
Unicode), environment blocks, expansion and CRT environment functions use guest state.
Temporary changes belong to the process and are inherited by its children; an explicit
`ProcessCreateOptions.Environment` replaces the inherited environment, including when empty.

Persistent variables use the sandbox's `registry.json`, shared with the virtual registry APIs:
`HKEY_CURRENT_USER\Environment` for user variables and
`HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Environment` for machine
variables. New processes without a parent or an explicit environment load these values over
virtual defaults, expand `REG_EXPAND_SZ` values, and append the user PATH to the machine PATH.
Registry changes affect subsequent independent launches; existing processes retain their current
environment. Reuse the same sandbox directory to retain persistent variables between runs.

Managed callers can use `process.SetEnvironmentVariable(name, value, target)` and
`process.GetEnvironmentVariables(target)`, where `target` is `EnvironmentVariableTarget.Process`
(the default), `User`, or `Machine`. A null value deletes a variable; an empty string remains a value.

Or: `pwsh -File scripts/build.ps1` / `pwsh -File scripts/test.ps1`.

Use repeatable `--import <dll>` options to add external modules before loading the guest:

```powershell
ImaginedNT.Host run app.exe --import plugins/MyApis.dll --import native/helper.dll
ImaginedNT.Host apis --import plugins/MyApis.dll
```

.NET assemblies register all methods annotated with `[VirtualApi]` or `[PartiallyImaginedApi]`; their dependencies are
resolved from their dependency manifest or directory, sharing the host's API contract assemblies.
A missing dependency or duplicate API declaration fails the import. Native Windows x64 DLLs are
registered by filename and loaded on demand through static imports or the guest's `LoadLibraryW/A` path, including
in child processes. They are mapped and have their imports patched using the current runtime
policy; importing alone does not execute their `DllMain`. Pass `--import` before the `--` guest
argument separator.

Use `[PartiallyImaginedApi]` to replace selected functions completely while keeping the other
exports in the original native DLL:

```csharp
using ImaginedNT.Core.Runtime;

public static class HelperApis
{
    [PartiallyImaginedApi("helper.dll", "Add")]
    private static ulong Add(ApiCallContext ctx) => ctx.Arg(0) + ctx.Arg(1) + 100;
}
```

Import both the managed implementation assembly and the native DLL with `--import`. The same
attribute accepts a `ushort` ordinal instead of a name, and supports `module!name` aliases like
`VirtualApi`. `apis` marks these methods with `[Partially Imagined]`; embedding hosts can call
`runtime.Apis.RegisterPartiallyImagined(...)` directly before loading guest code.

Delay imports bind on their first call, then cache the resolved thunk without modifying the
protected IAT. Unused delay dependencies are not loaded or initialized.

These replacements take priority even under `native-first`. Static/delay imports,
`GetProcAddress`/`LdrGetProcedureAddress`, export forwarders, and calls to a replaced export from
inside a guest-mapped DLL all use the same implementation. The original function body is never
called for a replaced export. Other exports use the native code in that process's mapped DLL,
subject to `AllowNativeForwarding` and `managed-only`. Non-delay DLL imports are resolved before
`DllMain`, including circular imports; each process has its own DLL mappings. Named and ordinal
lookups of the same native entry share its replacement. Conflicting replacements for a shared
entry fail loading.

Internal calls are redirected at the exported function entry using a one-byte breakpoint, so
short functions and adjacent exports remain intact. Inlined code and Windows-loaded host DLLs
are outside this guest-mapping boundary.


## DLL redirects

Use repeatable `--redirect-dll <source>=<target>` options before the `--` guest argument separator:

```powershell
ImaginedNT.Host run app.exe --redirect-dll old.dll=new.dll
ImaginedNT.Host run app.exe --redirect-dll "old.dll=D:\compat\replacement.dll"
```

Source DLLs match by filename, ignoring case and an optional `.dll` suffix. Targets can be
DLL names (using the normal DLL search) or host paths. Relative host paths such as
`./compat/replacement.dll` resolve against the host working directory when the runtime is
created. An explicit target path takes priority over `--import` and fails if missing.
Repeated rules for the same source in the CLI use the last value. Redirect chains are supported;
cycles are rejected, and redirecting a DLL to a host path with the same filename is supported.

Redirection applies before API resolution, so managed APIs and native declarations are selected
for the target DLL under the existing import policy. Static imports, delay imports, export
forwarders, `LoadLibraryW/A`, and module-name lookup share the rules, including in child
processes. `GetProcAddress`/`LdrGetProcedureAddress` use the resulting module's exports; export
names and ordinals are unchanged. Calls internal to Windows-loaded host DLLs remain outside
this runtime's resolution path.

Embedding hosts configure `RuntimeOptions.DllRedirects` before creating the runtime:

```csharp
var options = new RuntimeOptions();
options.DllRedirects["old.dll"] = @"D:\compat\replacement.dll";
using var runtime = BuiltinApis.CreateRuntime(options);
```

## Simulated MSIX package identity

Guests have no package identity by default, independently of the host's packaging.
Use `--packaged` for a default identity, `--package-identity <json>` to load an identity
and enable simulation, or `--unpackaged` to disable it. Options are applied in order.

```powershell
dotnet run --project src/ImaginedNT.Host -- run app.exe --package-identity samples/package-identity.json
```

The JSON supports `Name`, `Publisher`, optional `PublisherId`, four-part `Version`,
`Architecture` (`x86`, `x64`, `arm`, `arm64`, `neutral`), `ResourceId`, `InstallPath`,
and `ApplicationId`. Full name, family name and AUMID are derived from these fields;
omitting `PublisherId` derives it from the publisher's SHA-256 hash. `InstallPath` is
a guest-visible Windows path; create any required files separately in the sandbox.

Embedding hosts configure the same behavior through `RuntimeOptions`:

```csharp
using var runtime = BuiltinApis.CreateRuntime(new RuntimeOptions
{
    IsPackaged = true,
    PackageIdentity = new PackageIdentity
    {
        Name = "Example.Application",
        Publisher = "CN=Example",
        Version = new Version(1, 0, 0, 0),
        InstallPath = @"C:\Program Files\WindowsApps\Example.Application",
        ApplicationId = "App"
    }
});
```

`ImaginedNT.Apis.Package` implements `GetPackageFullName`, `GetPackageFamilyName`,
`GetPackageId`, their `GetCurrent*` counterparts, package path and application ID queries,
package info references, application ID lists, and package-family enumeration.
Package queries expose only the configured package (one application, no dependencies).
Name/ID conversions and validation reuse Windows functions that operate on caller data;
`PackageIdFromFullName` with `PACKAGE_INFORMATION_FULL` reads the simulated package.
The API-set aliases, including `ext-ms-win-kernel32-package-l1-1-0.dll`, resolve through
the existing registry. These APIs stay virtual under `native-first`, including dynamic lookup.
Calls internal to Windows-loaded host DLLs and WinRT package objects remain outside this surface.

## Simulated Windows version and branding

`GetVersion`, `GetVersionExW/A`, `RtlGetVersion`, `RtlGetNtVersionNumbers` and
`VerifyVersionInfoW/A` are implemented by the managed layer.  By default the guest sees the
host's **real** version (`RtlGetVersion`), not the `GetVersion` compatibility lie that Windows
applies to processes without a Windows 10/11 manifest.  Without this, a host executable lacking
`<supportedOS>` entries makes `GetVersion` return `6.2.9200`, and `cmd.exe` prints the classic
`Microsoft Windows [Version 6.2.09200.0]` instead of the actual OS version.

Use `--windows-version` to simulate any version, and `--copyright`/`--product-name` to replace the
branding strings that `BrandingFormatString` returns (this is where the `(c) Microsoft Corporation`
line in `cmd.exe` comes from):

```powershell
dotnet src/ImaginedNT.Host/bin/Debug/net10.0/ImaginedNT.Host.dll run app.exe --windows-version 10.0.19045.0
dotnet src/ImaginedNT.Host/bin/Debug/net10.0/ImaginedNT.Host.dll run app.exe `
    --windows-version 6.2.9200.0 `
    --copyright "Copyright (C) Example Corp. All rights reserved." `
    --product-name "ExampleOS"
```

The version affects `VerifyVersionInfo` too, so applications that gate features on the OS
version will follow the simulated value.  Without `--copyright`/`--product-name`, branding is
left to the host's `winbrand.dll`.

## What works

| Area | Status |
|---|---|
| PE32+ parsing (headers, sections, imports, exports, relocations, TLS directory) | ✅ |
| Map image, copy sections, apply `DIR64`/`HIGHLOW`/`HIGH`/`LOW` relocations | ✅ |
| IAT patching to managed thunks or native exports | ✅ |
| Managed↔native import policy (`managed-first`, `managed-only`, `native-first`) | ✅ |
| Real MSVC CRT-free PE execution on a real host thread | ✅ |
| Managed `WriteFile`/`GetStdHandle`/`ExitProcess` end to end | ✅ |
| Virtual memory reserve/commit/protect/query/free, `VirtualAlloc`/`VirtualQuery` | ✅ |
| NT object namespace, symbolic links, handle table, access masks | ✅ |
| Events, mutants, semaphores, named objects, waits | ✅ |
| Sandboxed `C:` filesystem, NT file handles, read/write/query information | ✅ |
| Directory enumeration (`FindFirstFileW`/`FindNextFileW`/`FindClose`) | ✅ |
| Guest mini cmd shell (`samples/bin/shell.exe`) for FS/process testing | ✅ |
| Real Windows `cmd.exe` startup, builtin commands (`echo`, `dir`, `cd`, `ver`, `type`, `set`), `/c` and redirected stdin | ✅ |
| Guest threads (`CreateThread`, wait, exit codes), per-thread last error | ✅ |
| Guest threads hosted on C# managed threads, one owner process per thread | ✅ |
| `CreateProcessA`/`CreateProcessW`/`WinExec`, `PROCESS_INFORMATION`, `CREATE_SUSPENDED` | ✅ |
| Multiple imagined processes in one host, process/thread handles and waits | ✅ |
| CPU-bound guest watchdog (`--timeout`, exit code 124) | ✅ |
| Direct `syscall` trap via VEH + real ntdll syscall-number discovery | ✅ |
| Real `NtClose` syscall number read from ntdll and emulated by managed `NtClose` | ✅ |
| Guest DLL loading: map, relocate, patch imports, call `DllMain`, resolve exports | ✅ |
| Native forwarding for APIs not implemented in C# | ✅ |
| OS job-object backstop (memory/process limits, kill-on-close) | ✅ (opt-in) |
| Multi-process isolation by running each guest app in its own host process | ✅ |
| In-host child processes share one address space (not an isolation boundary) | ⚠️ by design |
| Full CRT/PEB/TEB/SEH/registry/COM/GDI, 32-bit, cross-host process trees | ❌ |

See [docs/LIMITATIONS-AND-SECURITY.md](docs/LIMITATIONS-AND-SECURITY.md) before using this with
untrusted code, and [docs/PORTING.md](docs/PORTING.md) for how the layers map onto another host
OS or an emulator backend.

### CRT API sets and `Partially Imagined`

`ImaginedNT.Apis.Crt` supplies managed implementations of all 125 pure-managed CRT exports,
registered for their exact API-set modules and `ucrtbase.dll` aliases. It is included by
`BuiltinApis` and covers strings/memory, integer conversion/utilities, path transforms,
environment, process/thread identity, and time.

[docs/CRT-API-CLASSIFICATION.md](docs/CRT-API-CLASSIFICATION.md) classifies every export of the
`api-ms-win-crt-*` UCRT contracts: which ones can be implemented as pure managed functions with
the current `ApiCallContext`/process services, which ones are safe to leave as native
`ucrtbase` forwards, and which ones need a new `errno`/locale/`FILE`/heap/CRT-startup component.
The companion CSV is [docs/crt-api-classification.csv](docs/crt-api-classification.csv); the
inventory can be regenerated with `scripts/crt-export-inventory.ps1`.

The 346 safe-native exports are declared in
[src/ImaginedNT.Apis.Crt/CrtNativeApis.cs](src/ImaginedNT.Apis.Crt/CrtNativeApis.cs) and can be
enforced with `--native-declared-only` (or `--native-policy declared-only`). The default
`--native-policy allow-all` keeps the existing behavior; `--strict` still disables native
forwarding entirely.

## How it works

```
                         ┌──────────────────────────────────────────────┐
  hello.exe ──parse──▶   │ PeImage: headers, sections, imports, relocs  │
                         └──────────────────────┬───────────────────────┘
                                                │
                         ┌──────────────────────▼───────────────────────┐
                         │ PeLoader                                     │
                         │  map at preferred or forced base             │
                         │  copy sections, apply base relocations       │
                         │  resolve imports -> IAT slots                │
                         │  apply per-section RW/RX protections         │
                         └───────┬───────────────────────┬──────────────┘
                                 │ managed import        │ native import
                                 ▼                       ▼
                    ┌──────────────────────┐   ┌────────────────────────┐
                    │ 16-byte IAT thunk    │   │ real kernel32/ntdll    │
                    │ mov r11, apiId       │   │ export address         │
                    │ jmp universalStub    │   └────────────────────────┘
                    └──────────┬───────────┘
                               ▼
                    ┌──────────────────────┐        ┌──────────────────────────┐
                    │ universal x64 stub   │───────▶│ ApiRegistry dispatch     │
                    │ save args/XMM/ret    │        │  ├ BuiltinApis (C#)      │
                    │ call Dispatch(...)   │        │  └ trace + last error    │
                    └──────────────────────┘        └────────────┬─────────────┘
                                                                 ▼
                              ┌──────────────────────────────────────────────────┐
                              │ ImaginedRuntime                                  │
                              │  native thread id -> ImaginedThread -> Process    │
                              │  shared ObjectManager (session namespace)         │
                              │  ImaginedProcess[] (root + CreateProcess children)│
                              │   each: VirtualMemoryManager · HandleTable ·      │
                              │         SandboxFileSystem · ImaginedThread[]      │
                              └──────────────────────────────────────────────────┘

  direct syscall: guest 0F 05 --loader--> CC CC --#BP--> VEH --> SyscallDispatcher
                  --> virtual number table or real ntdll number -> managed NT API
```

The full design is in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Repository layout

```
src/ImaginedNT.Abstractions/    NT types, object/file contracts, API attributes, PE parser, tracing
src/ImaginedNT.Backends.Windows/ Windows interop, memory, filesystem, objects, PE mapping, thunks
src/ImaginedNT.Core/            Runtime, processes/threads, API registry/scanner, syscall dispatch
src/ImaginedNT.Apis.Common/     Shared API argument/result helpers
src/ImaginedNT.Apis.Crt/        Partially Imagined CRT APIs and per-process guest string caches
src/ImaginedNT.Apis.System/     Identity, console, environment, time, guest DLL APIs
src/ImaginedNT.Apis.Package/    Simulated MSIX identity, package queries and ID conversions
src/ImaginedNT.Apis.File/       File and directory APIs
src/ImaginedNT.Apis.Memory/     Virtual memory and memory-copy APIs
src/ImaginedNT.Apis.Process/    Process creation and handle information APIs
src/ImaginedNT.Apis.Registry/   ImaginedRegistry APIs and JSON registry persistence
src/ImaginedNT.Apis.Synchronization/ Waits, synchronization objects, thread creation, TLS APIs
src/ImaginedNT.Apis/            Optional composition of all built-in API libraries
src/ImaginedNT.Host/        `wine app.exe`-style CLI runner
tools/ImaginedNT.Weaver/    Debug-only IL weaver for method-entry tracing
tests/ImaginedNT.Tests/     Dependency-free test runner (41 tests)
samples/                    Native CRT-free PE samples + MSVC build script
docs/                       Architecture, API coverage, security/limitations
scripts/                    Build/test/sample helpers
```

## Isolation model (short version)

- A guest application and all processes it creates with `CreateProcessW`/`CreateProcessA` live
  inside **one** `ImaginedNT.Host` process. Each imagined process has its own virtual-memory
  bookkeeping and handle table, while the session object namespace and sandbox root are shared
  (named events/mutants are visible across parent and child). All of them share the host address
  space. Guest threads are C# managed threads whose identity selects the owning process during
  API dispatch.
- Running each separate application in its own host process remains the isolation boundary;
  `CreateProcess` is a Windows-compatibility feature, not a security boundary.
- Managed file APIs are path-jailed to the application's sandbox directory; a child process
  inherits its parent's sandbox root, and traversal/UNC/device paths are rejected. Each process
  still has its own handle table, so handles only cross a process boundary through inheritance or
  `DuplicateHandle`.
- Imports are resolved through an explicit policy. `--strict` allows **only** managed APIs and
  refuses to load an image with an unresolved import. Default mode forwards unknown APIs to the
  host DLLs, which necessarily grants the guest the host's real privileges for those calls.
- Native function imports pass through a forwarding thunk. The first call to each API in each
  guest process emits `Warning: API not redirected` with its DLL, export and native address.
  Use `--verbose` to see these warnings on stderr or `--trace <file>` to save them as JSONL.
  Ordinary imports, eagerly resolved delay imports, `GetProcAddress` and
  `LdrGetProcedureAddress` use the same resolution policy. Symbol lookup stays managed even
  under `native-first`. Data exports retain their original addresses; calls made internally
  by Windows-loaded native DLLs are outside this interception boundary.
- `--wrap-native-handles` (`RuntimeOptions.WrapNativeHandles`, default `false`) wraps handles
  returned by intercepted native NT calls in per-process `ImaginedHandle` entries. Closing,
  duplication and process disposal release the owned native handles. Pseudo handles stay unchanged.
- `--expand-managed-handles` (`RuntimeOptions.ExpandManagedHandles`, default `false`) allows
  native NT calls to use supported virtual objects. Disk files duplicate their existing host
  file handle; events lazily create a shared native event. The representation is cached until
  the guest handle closes. Other managed object types return `STATUS_NOT_SUPPORTED`.
  Both switches can be enabled independently, before imports are resolved.
- Handle conversion covers named `Nt*`/`Zw*` imports, dynamic symbol lookup and trapped syscalls,
  including output handles, `OBJECT_ATTRIBUTES.RootDirectory` and wait arrays. Common Win32 file,
  event and wait operations also accept wrapped handles. Native DLL-internal calls remain outside
  interception. Unknown NT signatures, ordinal ntdll imports, unsupported nested handle structures,
  driver-specific control buffers, cross-host-process duplication and Win32 overlapped I/O return
  an unsupported-operation error.
  `NativeApiSignatures.Register` adds NT signatures with explicit argument and handle locations.
- `--syscalls` traps direct syscall instructions. Managed handlers are used first; with handle
  conversion and native forwarding enabled, supported native calls can also pass through the
  handle bridge. Otherwise unimplemented syscalls return `STATUS_NOT_IMPLEMENTED`.
- `--job` adds an OS-enforced job object (memory limit, active-process limit, kill-on-close).
- **In-process execution is not a security boundary.** Native forwarded code can read/write the
  host process, and the managed layer cannot fully contain arbitrary machine code. See the
  security document for the threat model and hardening options.

## Requirements

- Windows x64
- .NET 10 SDK / runtime
- MSVC + `ml64` only if you rebuild the sample PEs
- PowerShell 7 for the helper scripts (Windows PowerShell 5.1 is likely fine too)

