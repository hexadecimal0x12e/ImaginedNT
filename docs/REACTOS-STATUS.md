# ReactOS binaries (experimental)

ReactOS's own `cmd.exe` and system DLLs rely on the CSR server, a guest PEB/TEB, loader
initialization, TLS callbacks and the ReactOS msvcrt.  These are not fully implemented yet.
Use `--host-system-dlls` to resolve by-name system imports from the host's fully initialized
System32 DLLs while keeping managed APIs in front; this gets ReactOS cmd past its
kernel32 `DllMain` and into NLS/CRT startup, but it still does not reach a prompt.  The detailed
failure chain, the APIs already added and the remaining gaps are in
[docs/REACTOS-STATUS.md](docs/REACTOS-STATUS.md).

# Running ReactOS binaries: current status and API gaps

This note records what happens when the ReactOS `system32\cmd.exe` (and its system DLLs) is
loaded through ImaginedNT, what has already been fixed, and which APIs/subsystems still need
work.  It is a diagnostic log, not a claim that ReactOS is fully supported.

## Reproduction

```powershell
# ReactOS tree used during the investigation
$sandbox = 'D:\code\playground\reactos'

dotnet src\ImaginedNT.Host\bin\Debug\net10.0\ImaginedNT.Host.dll run `
    "$sandbox\system32\cmd.exe" --sandbox $sandbox `
    --host-system-dlls --trace-native --verbose --trace artifacts\reactos-trace.jsonl
```

`--host-system-dlls` resolves by-name imports of `kernel32`, `kernelbase`, `ntdll`, `msvcrt`,
`user32`, etc. to the host's fully initialized System32 DLLs (managed APIs still win).  Without
it the loader maps ReactOS's own ntdll/kernel32 as guest images; those images expect a loader
entry point that ImaginedNT does not run.

## What the original crash was

The 0xC00000FD (`STATUS_STACK_OVERFLOW`) seen under Visual Studio is a downstream symptom.  Most
reproducible failures are earlier and different:

1. ReactOS kernel32's `DllMain` calls `CsrClientConnectToServer`.  A failed CSR connection makes
   it `DbgPrint` and call `NtTerminateProcess(STATUS_PORT_NOT_SET)` (0xC00000BB).  The 0xC00000FD
   is typically a guest SEH/stack path after an earlier failure (often an uninitialized loader
   list or a bad `__security_check_cookie`).
2. When CSR is forced successful, kernel32 reads
   `NtCurrentPeb()->ReadOnlyStaticServerData[BASESRV_SERVERDLL_INDEX]`.  ReactOS expects a
   `BASE_STATIC_SERVER_DATA` there; the host PEB entry is either absent or a Windows-layout
   object, and reads at `+0x18` / `+0x28` fault.
3. ReactOS ntdll's VEH list is initialized by its loader entry point, not by its PE entry point
   (it has no entry point).  `RtlAddVectoredExceptionHandler` walks a zeroed list and faults at
   `RtlpAddVectoredHandler+0x72` (`mov eax,[rax+0x18]`).
4. ReactOS cmd has TLS callbacks (`tls=2`).  Skipping them leaves the CRT security cookie
   uninitialized; `__security_check_cookie` then reports a GS failure / fail-fast 0xC0000409.
5. ReactOS msvcrt's `DllMain` requires real TLS slots and writes per-thread CRT state.  Its
   `TlsAlloc()` failure path returns FALSE from `DllMain` and aborts kernel initialization.
6. Host kernelbase/msvcrt `DllMain` calls `RtlAllocateHeap` with a **host** process heap handle,
   not an ImaginedNT guest heap handle.  The managed heap table rejected it.

## Fixes already in the runtime

| Area | Change | Location |
|---|---|---|
| CSR DllMain | `CsrClientConnectToServer` reports a virtual successful connection instead of failing `DllMain` | [NtdllInitializationApis.cs](../src/ImaginedNT.Apis.System/NtdllInitializationApis.cs#L115) |
| TLS callbacks | Process-attach callbacks for the main image and every loaded DLL are invoked (DLL_THREAD_* still TODO) | [ImaginedProcess.cs](../src/ImaginedNT.Core/Runtime/ImaginedProcess.cs#L356) |
| TLS API | Real `TlsAlloc`/`TlsFree`/`TlsSetValue`/`TlsGetValue` with per-process slots and per-thread values | [SynchronizationApis.cs](../src/ImaginedNT.Apis.Synchronization/SynchronizationApis.cs#L601) |
| Guest heaps | `RtlAllocateHeap`/`RtlFreeHeap`/`RtlReAllocateHeap`/`RtlSizeHeap` fall back to the host heap when the handle is a host PEB heap | [HeapApis.cs](../src/ImaginedNT.Apis.Memory/HeapApis.cs#L37) |
| Guest VEH | `RtlAddVectoredExceptionHandler`/`RtlRemoveVectoredExceptionHandler` are no-ops (guest list is not initialized) | [NtdllInitializationApis.cs](../src/ImaginedNT.Apis.System/NtdllInitializationApis.cs#L90) |
| Debug output | `DbgPrint`/`vDbgPrintExWithPrefix`/`RtlAssert` are routed to the trace instead of executing uninitialized ntdll code | [NtdllInitializationApis.cs](../src/ImaginedNT.Apis.System/NtdllInitializationApis.cs#L60) |
| Host DLL preference | `--host-system-dlls` resolves core system imports from the host System32; managed handlers still take precedence | [ImaginedRuntime.cs](../src/ImaginedNT.Core/Runtime/ImaginedRuntime.cs#L452), [RuntimeOptions.cs](../src/ImaginedNT.Core/Runtime/RuntimeOptions.cs#L177) |
| CRT invalid-parameter | `_set_invalid_parameter_handler` and thread-local variants prevent a host-msvcrt fallback into guest msvcrt | [CrtRuntimeApis.cs](../src/ImaginedNT.Apis.Crt/CrtRuntimeApis.cs#L57) |
| Version | `GetVersion`/`GetVersionEx`/`RtlGetVersion`/`RtlGetNtVersionNumbers`/`VerifyVersionInfo` are managed and configurable with `--windows-version` | [SystemApis.cs](../src/ImaginedNT.Apis.System/SystemApis.cs#L323) |
| Path conversion | `RtlDosPathNameToRelativeNtPathName_U_WithStatus` is implemented for guest paths | [FileApis.cs](../src/ImaginedNT.Apis.File/FileApis.cs#L571) |

## Remaining API gaps (in the order they are reached)

1. **Virtual CSR server**
   `CsrClientConnectToServer` currently returns success but does not populate
   `BASESRV_API_CONNECTINFO` or `Peb->ReadOnlyStaticServerData`.  A real virtual CSR client
   needs `CsrClientCallServer`, `CsrAllocateCaptureBuffer`, `CsrAllocateMessagePointer`,
   `CsrCaptureMessage*`, `CsrFreeCaptureBuffer`, `CsrNewThread`, `CsrIdentifyAlertableThread`,
   `CsrSetPriorityClass`, `CsrGetProcessId`, and the BASE/USER API messages that ReactOS
   kernel32, console and process creation use.

2. **Guest PEB/TEB and loader state**
   `Peb->ReadOnlyStaticServerData[1]` must point at a valid `BASE_STATIC_SERVER_DATA`
   (WindowsDirectory, WindowsSystemDirectory, NamedObjectDirectory, CSDVersion, NLS user info,
   system time, time zone).  ReactOS ntdll also needs its loader globals initialized
   (VEH list, heap, NLS, activation context, `LdrpInitialize`/`LdrInitializeThunk`,
   `RtlUserThreadStart`).  Mapping a guest ntdll copy without running its loader entry is the
   root cause of many later faults.

3. **TLS and CRT**
   `TlsAlloc` now works, but ReactOS msvcrt writes directly into per-thread CRT tables
   (`TEB->TlsSlots`/static TLS) and expects `_initterm`/`_setlocale`/`_set_invalid_parameter_handler`
   initialization.  DLL_THREAD_ATTACH/DETACH callbacks and static TLS are not implemented.
   A guest msvcrt copy can still be loaded if one host export is missing; prefer the host msvcrt
   or implement the remaining msvcrt exports.

4. **NLS / locale**
   `NtGetNlsSectionPtr`, `RtlGetLocaleFileMappingAddress`, `RtlInitNlsTables`,
   `NtInitializeNlsFiles`, and the `NLS_USER_INFO` part of `BASE_STATIC_SERVER_DATA` are needed.
   ReactOS kernel32 and msvcrt initialize locale tables there.

5. **Heaps / memory**
   Host heap fallback is implemented, but `RtlCreateHeap`/`RtlDestroyHeap`/`RtlValidateHeap` and
   heap flags used by ReactOS's loader/CRT are still incomplete.

6. **Exception handling**
   Guest VEH registration is currently ignored.  Real support needs a guest VEH list and
   dispatch (or forwarding to host VEH), `RtlAddVectoredContinueHandler`,
   `RtlSetUnhandledExceptionFilter`, `RtlUnwindEx`/`RtlVirtualUnwind`, and SEH scope tables.
   ReactOS's `__C_specific_handler` and `RtlRaiseException` should then be routed correctly.

7. **ETW / WER / debug**
   `EtwEventRegister`, `EtwEventWrite`, `RtlWerpReportException`, `WerRegister*` and
   `NtDeviceIoControlFile` for WER/console are reached during ReactOS startup.  Some can be
   no-ops, but ETW registration currently ends in a host ntdll access violation when a guest
   ntdll copy is used.

8. **Path and file APIs**
   `RtlIsDosDeviceName_U`, `RtlGetFullPathName_U`, `RtlDosPathNameToNtPathName_U`,
   `NtCreateFile`/`NtOpenFile`/`NtQueryInformationFile`/`NtSetInformationFile`,
   `NtDeviceIoControlFile`, and ReactOS-specific `OBJECT_ATTRIBUTES`/`IO_STATUS_BLOCK` layouts.
   `del` and `copy` in the Windows cmd work; the ReactOS shell reaches `C:\Connect` and
   `NtDeviceIoControlFile` before the current fault.

9. **Console**
   ReactOS kernel32's console client uses CSR (`ReadConsoleInputW`, `WriteConsoleInputW`,
   `GetConsoleScreenBufferInfo`, `SetConsoleMode`, `CreatePipe`, `CreateProcessW`).  The managed
   console/file layer needs to serve those requests, or a virtual csrss/console server is needed.

10. **Process/thread startup**
    `LdrInitializeThunk`, `RtlUserThreadStart`, TEB fields, activation-context/SxS helpers
    (`RtlQueryActivationContextApplicationSettings`), `RtlGetCurrentServiceSessionId`, and
    `NtSetInformationProcess` were all reached in the traces.

## Current observed state

With the fixes above and `--host-system-dlls`, ReactOS cmd no longer dies in kernel32 `DllMain`.
It gets through kernel32/kernelbase initialization, CSR, heap, NLS setup, `NtCreateFile("C:\\Connect")`,
`NtDeviceIoControlFile` and starts executing ReactOS cmd's own startup code against the host
msvcrt.  It still faults later (host ntdll/kernelbase at `EtwEventRegister`/CRT startup or in
ReactOS msvcrt's per-thread tables).  Full support needs the loader/CSR/TLS work listed above,
not a single API fix.

The Windows `cmd.exe` path remains the supported scenario and is unaffected by
`--host-system-dlls` (the option is opt-in).
