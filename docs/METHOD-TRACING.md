# Method-entry tracing (debug diagnostics)

The normal `--trace` / `--verbose` stream records every API dispatch, import resolution and
image event.  When that is not enough — for example when a lazy import loads a system DLL and the
failure happens *inside* `DllMain` — you can run a **method-instrumented copy** of the runtime that
logs the entry of every managed method with its arguments.

The instrumentation is built by `tools/ImaginedNT.Weaver` (Mono.Cecil, IL rewriting) and is
completely opt-in:

* it rewrites a **copy** of the host output under `artifacts/method-trace`, so the normal
  `bin/Debug` output is untouched;
* the injected prologue checks `MethodTrace.Enabled` first, so a normal (uninstrumented) build and
  an instrumented build with tracing off behave the same;
* it is **entry-only**: it does not wrap bodies in `try/finally`, so exception handlers, returns,
  `yield`/`async` state machines and locals are not modified.

## Build and run

```powershell
pwsh -File scripts/build-method-trace.ps1

dotnet artifacts/method-trace/ImaginedNT.Host.dll run samples/bin/hello.exe --strict `
    --trace-methods --trace-methods-filter "LoadImage,InvokeGuest,WriteFile"
```

Output goes to stderr by default:

```text
#000001 t02 > ImaginedNT.Core.Runtime.ImaginedProcess.LoadImage("samples/bin/hello.exe", 0)
#000002 t04 > ImaginedNT.Core.Runtime.ImaginedThread.InvokeGuest(5368713216, null, false)
#000003 t04 > ImaginedNT.Core.Runtime.FileApis.WriteFile(ApiCallContext)
```

`--trace-methods-file <path>` writes to a file instead (auto-flushed, so it survives a guest
fail-fast), and `--trace-methods-filter <list>` takes comma-separated substrings matched against
`Type.Method`:

```powershell
dotnet artifacts/method-trace/ImaginedNT.Host.dll run c:\windows\System32\cmd.exe `
    --trace-methods --trace-methods-file artifacts\cmd-methods.log `
    --trace-methods-filter "LoadManagedLibrary,InitializeLibrary,TryResolveImport,RaiseFailFastException"
```

The same controls are available through the environment:

| Variable | Meaning |
|---|---|
| `IMAGINEDNT_TRACE_METHODS=1` | enable method tracing |
| `IMAGINEDNT_TRACE_METHODS_FILTER` | comma-separated `Type.Method` substrings |
| `IMAGINEDNT_TRACE_METHODS_FILE` | write to this file instead of stderr |

## Example: diagnosing a lazily loaded system DLL

Running the instrumented host against a real `cmd.exe` shows the loader resolving its CRT,
`ntdll` and then `Wldp.dll`/`msvcp_win.dll` during `DllMain`:

```text
#000183 t02 > ...ImaginedProcess.LoadManagedLibrary("c:\windows\System32\ntdll.dll")
#000574 t05 > ...ImaginedProcess.InitializeLibraries()
#000575 t05 > ...ImaginedProcess.InitializeLibrary(PeMappedImage)
#000579 t05 > ...ImaginedProcess.LoadManagedLibrary("c:\windows\System32\Wldp.dll")
#000582 t05 > ...ImaginedProcess.LoadManagedLibrary("c:\windows\System32\msvcp_win.dll")
```

If the guest then calls `RaiseFailFastException`, the trace ends with the managed handler:

```text
#001355 t05 > ...ImaginedRuntime.TryResolveImport(api-ms-win-core-libraryloader-l1-2-0.dll!RaiseFailFastException @IAT 0x0, ...)
#001356 t05 > ...SystemApis.RaiseFailFastException(ApiCallContext)
#001357 t05 > ...ApiCallContext.FailFast(7, 3221226505)
#001358 t05 > ...ImaginedProcess.FailFast(7, 2036475671060, "kernel32!RaiseFailFastException", 3221226505)
```

That pattern identifies a fatal path entered from a lazily loaded **system DLL's `DllMain`**, not
from the guest image's own code.  Pair it with the existing `--trace` stream and a crash dump to
see the native stack inside the DLL.

For failures that happen inside a native forward (blocking waits, wrong return values), add
`--trace-native`; it logs every forwarded call with its first four arguments and the guest return
address, *before* the native function runs, so a call that never returns is still visible.

## Weaver options

```text
ImaginedNT.Weaver <directory-or-assembly> [options]

  --include-generated   also instrument compiler-generated state machines/lambdas
  --no-accessors        skip property/event accessors
  --force               re-instrument assemblies that already carry the marker
  --dry-run             report what would be instrumented without writing
```

A directory must contain `ImaginedNT.Abstractions.dll` (the host output does).  Assemblies that
were already instrumented carry an `AssemblyMetadata("ImaginedNT.Instrumented", "method-trace")`
marker and are skipped unless `--force` is passed.

## Limitations

* Entry-only: return values and exceptions are not logged by the weaver.  The normal `--trace`
  API log and crash dumps still provide those.
* The filter is applied inside `MethodTrace.Enter`, after the argument array has been built.  A
  narrow filter reduces output but not the per-call boxing overhead while tracing is enabled.
* Reference, pointer and by-ref-like (`Span<T>`, `ReadOnlySpan<T>`, …) arguments are logged as
  `null`; compiler-generated types, `[UnmanagedCallersOnly]` methods and P/Invokes are not
  instrumented by default.
* The instrumented copy has no matching portable PDB, so stack traces show method names but not
  source line numbers.
* Instrumentation is for diagnosis only; do not treat the instrumented assemblies as a supported
  distribution.
