# Microsoft Visual C++ CRT API sets and Partially Imagined

**Scope:** the UCRT API-set contracts whose names start with `api-ms-win-crt-`.
This is an analysis of which exports can be replaced by `[PartiallyImaginedApi]` today,
which can safely remain native forwards, and which need a new runtime component.

**Inventory source:** Windows 11 24H2/26100 x64 UCRT redist
(`C:\Program Files (x86)\Windows Kits\10\Redist\10.0.26100.0\ucrt\DLLs\x64`) plus
`C:\Windows\System32\ucrtbase.dll` 10.0.26100.9711. The `l1-1-0` API-set export set is
stable in practice across Windows 10/11; the private set is included only for completeness.

**Data:** [`crt-api-classification.csv`](crt-api-classification.csv) contains all
**2,485** exports with status, reason, ordinal and forwarder.

**Implementation:** `ImaginedNT.Apis.Crt` implements all **125** managed exports in §5
with `[PartiallyImaginedApi]`, and declares the **346** safe-native exports in §6 as
assembly-level `[NativeApi]` entries. `BuiltinApis` includes this assembly by default. Each
managed export is registered for its exact API-set module and for `ucrtbase.dll` (250
registrations total); each native declaration also has a `ucrtbase!name` alias. Equivalent
exports sharing a native entry point share one managed handler.

String scans use page-bounded chunks without a fixed string-length cap. Secure functions
return CRT error codes directly, including `STRUNCATE` (80) for `_TRUNCATE`, without invoking
the host invalid-parameter handler. Environment/program-path buffers are cached per process
and released with its virtual memory; environment buffers are reused or replaced when values
change. Empty `_putenv_s`/`_wputenv_s` values remove the variable. Narrow environment and
program-path strings use the host ANSI code page; narrow path transforms preserve raw bytes
and skip DBCS trailing bytes when locating separators, using that same code page.

## 1. Bottom line

- **125 exports are pure-managed candidates** using only the current base
  components. They are dominated by memory/string (55), integer formatting (20), utility
  arithmetic (15), time formatting (13), path strings (10), environment (6) and virtual
  process/thread identity (6). See §5.
- **346 exports are safe to leave native.** 285 are floating-point/complex
  math, 35 are numeric parsing/formatting, 6 are buffer-target stdio engines, 15 are
  time/TZ conversions, and 5 are integer division/random fill. See §6.
- **8 are callback-native** (`qsort`/`bsearch`/`_lfind`/`_lsearch`).
- **19 heap exports are family-native**; they work only as a complete
  native heap and are not process-isolated.
- **325 must not be forwarded**, **526 need a new
  component**, **3 are data exports**, and **1133 are the
  private UCRT contract**. See §7.
- **Partially Imagined is not a CRT.** Replacing the qualified exports does not by itself make
  an arbitrary MSVC executable run. It does not add `_initterm`, argv/environment blocks,
  `errno`, locale, a `FILE`/fd table, a guest heap, SEH, TLS or CRT startup (§8).

The machine-readable status for every name is in the CSV; the rest of this document explains
the decision rules and gives the actionable lists.

## 2. Why this list is much smaller than the CRT headers

The API sets are not a one-to-one map of the C runtime. They export the implementation entry
points that the UCRT headers and import library use, and many familiar names are inline
wrappers or private exports:

| Familiar API | What the API set actually exports |
|---|---|
| `printf` / `sprintf` / `scanf` | inline wrappers around `__stdio_common_vfprintf` / `__stdio_common_vsprintf` / `__stdio_common_vsscanf` |
| `time` | not exported; `<time.h>` calls `_time64` |
| `memcpy` / `memmove` / `memcmp` / `memchr` | in the **private** API set and `ucrtbase.dll`; the public string set exports `memcpy_s`, `memmove_s`, `memset` |
| `strcpy` etc. | exported by `api-ms-win-crt-string-l1-1-0` |
| `malloc` / `free` | exported by `api-ms-win-crt-heap-l1-1-0` |

Consequences:

- A guest that imports `printf` from `ucrtbase.dll` directly does **not** import
  `api-ms-win-crt-stdio-l1-1-0!printf`; there is no such export. It imports
  `__stdio_common_vfprintf` or a `_vfprintf_l`-style entry.
- If you target an API-set name, register the exact module name. If you also want direct
  `ucrtbase!name` imports, add a `ucrtbase!name` alias (see §8).
- The private API set is not a supported guest contract; it is excluded from the counts above
  and classified as `internal` in the CSV.

## 3. What Partially Imagined can and cannot do

`[PartiallyImaginedApi]` replaces an export **entry point**; it is discovered by the same scanner
as `[VirtualApi]`, always uses the runtime implementation, and patches the guest mapping with an `INT3`
redirector. The hard limits below are properties of the current dispatcher, not of the
attribute.

| Constraint | Consequence |
|---|---|
| **Function exports only** | `ImaginedRuntime.RedirectExports` rejects an export whose RVA is not in an executable section. `_wctype`, `_mbcasemap` and `__dcrt_initial_narrow_environment` are data and cannot be replaced. |
| **Integer ABI only** | The universal stub saves `XMM0-3` in `NativeCallFrame`, but `DispatchManaged` copies only `RCX/RDX/R8/R9` and stack arguments into `ApiCallContext`. A managed handler cannot read a `double`/`float`/complex argument. |
| **Return is `RAX` only** | The stub restores the saved `XMM0-3` after dispatch, so a managed handler cannot synthesize a floating-point or vector return. `float`/`double`/complex functions must stay native. Small 8-byte structs (`div`) could be packed into `RAX`; 16-byte structs and hidden-pointer returns need more work. |
| **Variadic arguments** | `ApiCallContext.Arg(0..15)` covers the first 16 integer/pointer slots. Floating varargs live in `XMM1-3` and stack slots and are not exposed. A managed `__stdio_common_v*` implementation cannot correctly parse `%f`/`%Lf`. Native forwarding preserves the full variadic frame. |
| **No guest callback ABI** | There is no public helper that calls a guest function pointer from managed code. `qsort`/`bsearch` can only work by letting native code call the guest comparator; `_initterm`/`atexit` need a managed guest-call path. |
| **Exact module lookup** | Once a module has any `PartiallyImagined` registration, `ApiRegistry.TryGet` uses exact lookup for that module only. Other exports fall through to native, but they no longer inherit a similarly named managed API from `kernel32`/`ntdll`. |
| **Aliases are separate registrations** | `api-ms-win-crt-string-l1-1-0!strlen` and `ucrtbase!strlen` are different keys. Register both if both import styles must be redirected. |
| **No errno/locale/FILE/fd/heap/argv state** | `Thread.LastError` is Win32 last-error, not `errno`. There is no per-thread `errno`, locale object, `FILE` table, fd table, guest heap, PEB/TEB or CRT startup block. Functions that need them are `needs-component`, not `managed`. |
| **Secure CRT variants are special** | `*_s` and other secure functions call the process-wide invalid-parameter handler on bad arguments. The default handler terminates the **host**. Native-forwarding a secure function can therefore kill the host on a guest bug. They are `host-unsafe` unless implemented managed. |
| **Only guest-visible call sites** | Redirects cover guest IAT slots, `GetProcAddress`/`LdrGetProcedureAddress`, and calls to replaced exports inside guest-mapped DLLs. Calls made **inside host-loaded `ucrtbase`/`kernel32`** remain native. A managed `strlen` does not replace the `strlen` called by native `strtoul`. |

Native forwarding itself is ABI-safe for ordinary calls: `EmitNativeForwarder` preserves the
integer volatile registers, `XMM0-5`, the stack and the host last-error slot around the observer
call, then jumps to the real `ucrtbase` entry. That is why the `safe-native` set is viable.

## 4. Status counts

| Module | Exports | managed | safe-native | callback | heap family | host-unsafe | needs component | data |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| `api-ms-win-crt-conio-l1-1-0.dll` | 30 | 0 | 0 | 0 | 0 | 30 | 0 | 0 |
| `api-ms-win-crt-convert-l1-1-0.dll` | 122 | 20 | 35 | 0 | 0 | 6 | 61 | 0 |
| `api-ms-win-crt-environment-l1-1-0.dll` | 18 | 6 | 0 | 0 | 0 | 2 | 10 | 0 |
| `api-ms-win-crt-filesystem-l1-1-0.dll` | 65 | 10 | 0 | 0 | 0 | 0 | 55 | 0 |
| `api-ms-win-crt-heap-l1-1-0.dll` | 27 | 0 | 0 | 0 | 19 | 8 | 0 | 0 |
| `api-ms-win-crt-locale-l1-1-0.dll` | 20 | 0 | 0 | 0 | 0 | 3 | 17 | 0 |
| `api-ms-win-crt-math-l1-1-0.dll` | 290 | 0 | 285 | 0 | 0 | 5 | 0 | 0 |
| `api-ms-win-crt-multibyte-l1-1-0.dll` | 200 | 0 | 0 | 0 | 0 | 2 | 197 | 1 |
| `api-ms-win-crt-private-l1-1-0` (private) | 1134 | 0 | 0 | 0 | 0 | 0 | 0 | 1 |
| `api-ms-win-crt-process-l1-1-0.dll` | 36 | 0 | 0 | 0 | 0 | 3 | 33 | 0 |
| `api-ms-win-crt-runtime-l1-1-0.dll` | 104 | 6 | 0 | 0 | 0 | 79 | 19 | 0 |
| `api-ms-win-crt-stdio-l1-1-0.dll` | 159 | 0 | 6 | 0 | 0 | 153 | 0 | 0 |
| `api-ms-win-crt-string-l1-1-0.dll` | 178 | 55 | 0 | 0 | 0 | 0 | 122 | 1 |
| `api-ms-win-crt-time-l1-1-0.dll` | 72 | 13 | 15 | 0 | 0 | 34 | 10 | 0 |
| `api-ms-win-crt-utility-l1-1-0.dll` | 30 | 15 | 5 | 8 | 0 | 0 | 2 | 0 |
| **Total** | **2485** | **125** | **346** | **8** | **19** | **325** | **526** | **3** |

`managed + safe-native = 471` of the 1351 public
exports are actionable today: 125 managed and 346 left
native. The remaining 880 public exports
need a new component, a guest callback path, or must not be forwarded.

## 5. Pure-managed candidates (125)

These are the functions that can be implemented with the current base components. They use only
guest memory (`ctx.Memory`), the process environment dictionary, the virtual process/thread
identity, the host clock, and the BCL. They do not need a new `errno`, locale, `FILE`, fd, heap
or startup block.

### 5.1 String and memory — `api-ms-win-crt-string-l1-1-0` (55)

`__isascii`, `__iscsym`, `__iscsymf`, `__iswcsym`, `__iswcsymf`, `__strncnt`, `__wcsncnt`, `_memccpy`, `_strnset`, `_strnset_s`, `_strrev`, `_strset`, `_strset_s`, `_wcsnset`, `_wcsnset_s`, `_wcsrev`, `_wcsset`, `_wcsset_s`, `memcpy_s`, `memmove_s`, `memset`, `strcat`, `strcat_s`, `strcmp`, `strcpy`, `strcpy_s`, `strcspn`, `strlen`, `strncat`, `strncat_s`, `strncmp`, `strncpy`, `strncpy_s`, `strnlen`, `strpbrk`, `strspn`, `strtok_s`, `wcscat`, `wcscat_s`, `wcscmp`, `wcscpy`, `wcscpy_s`, `wcscspn`, `wcslen`, `wcsncat`, `wcsncat_s`, `wcsncmp`, `wcsncpy`, `wcsncpy_s`, `wcsnlen`, `wcspbrk`, `wcsspn`, `wcstok_s`, `wmemcpy_s`, `wmemmove_s`

Notes:

- `memset`, `strcpy`, `strncpy`, `strcat`, `strncat`, `strcmp`, `strncmp`, `strspn`,
  `strcspn`, `strpbrk`, `strrev`, `strset`, `strnset` and the wide equivalents are byte/UTF-16
  scans; use `TryRead`/`TryWrite` in chunks, not one page-crossing read per character.
- The secure variants (`*_s`, `_strset_s`, `_strnset_s`, `wmemcpy_s`, ...) return `errno_t`
  directly; they must validate null/size/overlap and must **not** throw.
- `strtok_s`/`wcstok_s` take the caller's context pointer and are stateless; the legacy
  `strtok`/`wcstok` are **not** in this set because they store a guest pointer in host CRT
  thread-local state.
- `__isascii`, `__iscsym`, `__iscsymf`, `__iswcsym`, `__iswcsymf` are ASCII-only checks.
- `__strncnt`/`__wcsncnt` are bounded internal count helpers.
- `memcpy`/`memmove`/`memcmp`/`memchr` are **not** in the public string API set. Neither are
  `strchr`/`strrchr`/`strstr` or their wide equivalents; they are private/`ucrtbase`-only.
  If you also want direct `ucrtbase!name` imports, add them as separate aliases.

### 5.2 Integer formatting — `api-ms-win-crt-convert-l1-1-0` (20)

`_i64toa`, `_i64toa_s`, `_i64tow`, `_i64tow_s`, `_itoa`, `_itoa_s`, `_itow`, `_itow_s`, `_ltoa`, `_ltoa_s`, `_ltow`, `_ltow_s`, `_ui64toa`, `_ui64toa_s`, `_ui64tow`, `_ui64tow_s`, `_ultoa`, `_ultoa_s`, `_ultow`, `_ultow_s`

Radix 2..36, lowercase digits, `_i64`/`_ui64` variants, narrow/wide, and the `_s` variants
(which return `errno_t`). `_itoa`/`_itow` return their `value` pointer; `_itoa_s` writes the
buffer and NUL-terminates.

### 5.3 Utility arithmetic — `api-ms-win-crt-utility-l1-1-0` (15)

`_abs64`, `_byteswap_uint64`, `_byteswap_ulong`, `_byteswap_ushort`, `_lrotl`, `_lrotr`, `_rotl`, `_rotl64`, `_rotr`, `_rotr64`, `_swab`, `abs`, `imaxabs`, `labs`, `llabs`

`_rotl`/`_rotr` and 64-bit variants, byte swaps, `_swab`, and `abs`/`labs`/`llabs`/`_abs64`/
`imaxabs`. `abs(INT_MIN)` is undefined in C; keep the same two's-complement result.

### 5.4 Path strings — `api-ms-win-crt-filesystem-l1-1-0` (10)

`_getdrive`, `_getdrives`, `_makepath`, `_makepath_s`, `_splitpath`, `_splitpath_s`, `_wmakepath`, `_wmakepath_s`, `_wsplitpath`, `_wsplitpath_s`

`_makepath`/`_splitpath` and the `_s`/wide variants are pure string transforms. `_getdrive`
returns the 1-based current drive (the sandbox models `C:` as 3), and `_getdrives` returns the
bitmask (`4` for `C:`). The path/file APIs that touch the sandbox (`_access`, `_mkdir`,
`remove`, `_stat`, `_findfirst`, ...) are not in this set because they also need a guest
`errno` (§7.4).

### 5.5 Environment — `api-ms-win-crt-environment-l1-1-0` (6)

`_putenv_s`, `_wgetenv`, `_wgetenv_s`, `_wputenv_s`, `getenv`, `getenv_s`

`getenv`/`_wgetenv` must return a stable pointer, so cache one guest string per name (or one
per process) instead of re-allocating on every call. `getenv_s`/`_wgetenv_s` write caller
buffers; `_putenv_s`/`_wputenv_s` update the process environment. All of them use
`ImaginedProcess.TryGetEnvironmentVariable` / `SetEnvironmentVariable`. The legacy
`_putenv`/`_wputenv` are excluded because they set `errno`; `_dupenv_s` is excluded because it
allocates with the CRT heap.

### 5.6 Virtual process/thread identity — `api-ms-win-crt-runtime-l1-1-0` (6)

`__threadhandle`, `__threadid`, `_get_pgmptr`, `_get_wpgmptr`, `_getpid`, `_sleep`

`_getpid` returns the virtual `ImaginedProcess.Id`; `_sleep` maps to a managed sleep;
`__threadid` returns the virtual thread id; `__threadhandle` returns the `-2` pseudo handle;
`_get_pgmptr`/`_get_wpgmptr` write a pointer to a cached UTF-16/ANSI copy of
`process.ImagePath`.

### 5.7 Time formatting — `api-ms-win-crt-time-l1-1-0` (13)

`_getsystime`, `_strdate`, `_strdate_s`, `_strtime`, `_strtime_s`, `_time32`, `_time64`, `_timespec32_get`, `_timespec64_get`, `_wstrdate`, `_wstrdate_s`, `_wstrtime`, `_wstrtime_s`

`_time32`/`_time64`, `_timespec32_get`/`_timespec64_get`, `_getsystime`, `_strdate`/`_strtime`
and their `_s`/wide variants can use the host clock, matching the existing `GetLocalTime` /
`GetSystemTimeAsFileTime` behavior. `_strdate_s`/`_strtime_s` return `errno_t`; the non-`_s`
versions write the caller's fixed-size buffer.

## 6. Safe-to-leave-native exports (346)

These are pure value or caller-buffer operations. They do not mutate host-global state, do not
retain a pointer after return, do not create a host resource, and do not call back into guest
code. Native forwarding preserves the complete x64 frame, including `XMM0-5`, so the
floating-point ABI is not a problem here.

Nothing in this set needs a `[PartiallyImaginedApi]` registration: this is the existing
`NativeForwarder` path. The list exists so you know what is safe to leave there and what to
audit with `--verbose`/`--trace`.

The 346 entries are declared as assembly-level `[NativeApi]` attributes in
[`../src/ImaginedNT.Apis.Crt/CrtNativeApis.cs`](../src/ImaginedNT.Apis.Crt/CrtNativeApis.cs)
(each also declares a `ucrtbase!name` alias). Set `RuntimeOptions.NativeApiPolicy` to
`NativeApiPolicy.DeclaredOnly` (CLI: `--native-declared-only` or
`--native-policy declared-only`) to allow native forwarding only for this audited set. The
default `AllowAll` preserves the old forwarding behavior. The declaration file is generated by
[`../scripts/generate-crt-native-declarations.ps1`](../scripts/generate-crt-native-declarations.ps1)
from the classification CSV.

`DeclaredOnly` is an **import-resolution policy, not a security boundary**. Guest machine code
can still parse a loaded module and call an export directly. Use a separate host process and the
OS job object for isolation; see [LIMITATIONS-AND-SECURITY.md](LIMITATIONS-AND-SECURITY.md).

### 6.1 Math — `api-ms-win-crt-math-l1-1-0` (285 of 290)

All math exports are `safe-native` except these five:

- `__setusermatherr` — installs a host-global math-error handler.
- `_set_FMA3_enable` — mutates host CRT code-generation policy.
- `_get_FMA3_enable` — reads host CRT code-generation policy.
- `_fdopen` — a stdio/fd function that happens to live in this API set.
- `_except1` — host CRT floating-point exception/`matherr` machinery.

The remaining 285 are `safe-native`, including the `float`/`double`/`long double` routines,
the `_Cbuild`/`_Cmulcc`/`_Cmulcr` complex constructors, the `_d*`/`_fd*`/`_ld*` decomposition
helpers, `_finite`, `_isnan`, `_fpclass`, `_hypot`, `_j0`/`_j1`/`_jn`, `_y0`/`_y1`/`_yn`,
`_scalb`, `_nextafter`, `_logb`, `_copysign`, and the standard `<math.h>` and `<complex.h>`
surface. They are **not** managed candidates because the dispatcher cannot read or write
`XMM` values.

### 6.2 Numeric parsing and formatting — `api-ms-win-crt-convert-l1-1-0` (35)

`_atodbl`, `_atoflt`, `_atoi64`, `_atoldbl`, `_strtoi64`, `_strtoui64`, `_wcstoi64`, `_wcstoui64`, `_wtof`, `_wtoi`, `_wtoi64`, `_wtol`, `_wtoll`, `atof`, `atoi`, `atol`, `atoll`, `strtod`, `strtof`, `strtoimax`, `strtol`, `strtold`, `strtoll`, `strtoul`, `strtoull`, `strtoumax`, `wcstod`, `wcstof`, `wcstoimax`, `wcstol`, `wcstold`, `wcstoll`, `wcstoul`, `wcstoull`, `wcstoumax`

`strtod`/`strtol`/`strtoul`/`strtoimax`/... and `atof`/`atoi`/`_wtoi`/... are pure parsers.
They set the host thread's `errno` on overflow/invalid input; that is acceptable as long as
`_errno`/`_set_errno` are also left native. `_ecvt`/`_fcvt`/`_gcvt` are **not** in this list:
they return a pointer to a host static buffer. Their `_s` variants are also excluded because
secure validation can enter the host invalid-parameter handler; use managed implementations if
you need them.

### 6.3 Buffer-target stdio engines — `api-ms-win-crt-stdio-l1-1-0` (6)

`__stdio_common_vsprintf`, `__stdio_common_vsprintf_p`, `__stdio_common_vsscanf`, `__stdio_common_vswprintf`, `__stdio_common_vswprintf_p`, `__stdio_common_vswscanf`

These are the `sprintf`/`swprintf`/`sscanf`/`swscanf` engines that write to a caller-provided
buffer or scan from a caller-provided string. They take a `va_list` and pass the guest buffers
through unchanged. The secure `_s` variants and the `FILE*`/stdout engines are **not** safe
(§7.3).

### 6.4 Time/TZ conversion — `api-ms-win-crt-time-l1-1-0` (15)

`_Strftime`, `_Wcsftime`, `_difftime32`, `_difftime64`, `_ftime32`, `_ftime64`, `_mkgmtime32`, `_mkgmtime64`, `_mktime32`, `_mktime64`, `_strftime_l`, `_wcsftime_l`, `clock`, `strftime`, `wcsftime`

`_mktime*`, `_mkgmtime*`, `_ftime*` (non-`_s`), `strftime`/`wcsftime`, `_difftime*` and
`clock`. They read the host time zone/locale and write caller buffers or return a scalar. They
do not retain a pointer. The `_s`/`_ctime*_s`/`_localtime*_s`/`_get_timezone` group is excluded
because secure validation can terminate the host through the invalid-parameter handler.

### 6.5 Integer division and random fill — `api-ms-win-crt-utility-l1-1-0` (5)

`div`, `ldiv`, `lldiv`, `imaxdiv`, `rand_s`. The division functions are pure integer
arithmetic; `rand_s` fills caller storage from the OS RNG and keeps no CRT global state.

## 7. What is not qualified

### 7.1 Callback-native (8)

`_lfind`, `_lfind_s`, `_lsearch`, `_lsearch_s`, `bsearch`, `bsearch_s`, `qsort`, `qsort_s`

`qsort`/`bsearch`/`_lfind`/`_lsearch` take a guest comparison/key function pointer. Native code
can call that pointer in the current in-process model, and the guest code is mapped executable.
However, this is not a pure data forward: the native routine calls guest code, and any managed
API called by the comparator re-enters the dispatcher. Treat it as a conditional feature and
provide a managed implementation once there is a supported guest-call ABI.

### 7.2 Heap family (19)

`_aligned_free`, `_aligned_malloc`, `_aligned_msize`, `_aligned_offset_malloc`, `_aligned_offset_realloc`, `_aligned_offset_recalloc`, `_aligned_realloc`, `_aligned_recalloc`, `_calloc_base`, `_expand`, `_free_base`, `_malloc_base`, `_msize`, `_realloc_base`, `_recalloc`, `calloc`, `free`, `malloc`, `realloc`

Forwarding `malloc`/`free`/`realloc`/`_aligned_*` to `ucrtbase` works only if the **whole heap
family** stays native. The guest then receives host heap pointers; the heap is shared with the
host and all imagined processes, and a guest overflow can corrupt the host. It is not a
per-process heap and not a security boundary. A managed heap would need a new block allocator
on top of `VirtualMemoryManager`; that is a new component, not part of the current base.

The heap introspection/new-handler exports (`_heapchk`, `_heapmin`, `_heapwalk`,
`_get_heap_handle`, `_callnewh`, `_query_new_mode`, `_set_new_mode`, `_query_new_handler`) are
`host-unsafe`, not family-native.

### 7.3 Host-unsafe (325)

These must not be forwarded. They fall into these groups:

- **stdio / fd / console (153 stdio + 30 conio = 183).** `fopen`, `freopen`, `_open`, `_sopen`,
  `fread`, `fwrite`, `fseek`, `fclose`, `_get_osfhandle`, `_open_osfhandle`, `__acrt_iob_func`,
  `__p__commode`, `__p__fmode`, `_popen`, `_wpopen`, `_pclose`, `_isatty`, `_kbhit`,
  `_getmaxstdio`, `_setmaxstdio`, `_flushall`, `_fcloseall`, `_rmtmp`, `_set_fmode`,
  `_get_fmode`, `_set_printf_count_output`, `_get_printf_count_output`, all `_getch`/`_putch`
  variants, and the `__stdio_common_vfprintf`/`vfwprintf` engines. They either escape the
  sandbox (`fopen`/`_open`), hold host `FILE*`/fd state, or read/write the host console.
  The secure `__stdio_common_vsprintf_s`/`__stdio_common_vsnprintf_s`/
  `__stdio_common_vswprintf_s`/`__stdio_common_vsnwprintf_s` engines are here too: their
  validation can call the host invalid-parameter handler.
- **CRT runtime, process and handlers (79 runtime + 3 process = 82).** `exit`, `_exit`, `_Exit`,
  `quick_exit`, `abort`, `terminate`, `_cexit`, `_c_exit`, `atexit`, `_onexit`, `_crt_atexit`,
  `_register_onexit_function`, `_initterm`, `_initterm_e`, `_beginthread*`, `_endthread*`,
  `signal`, `raise`, `set_terminate`, `_set_new_handler`, SEH/`setjmp`/`longjmp`, the
  FP-environment functions (`_controlfp*`, `_clearfp`, `_statusfp`, `_fpreset`, `fesetround`,
  ...), `system`, `_wsystem`, `_loaddll`, `_unloaddll`, `_getdllprocaddr`, and the host
  pointer accessors (`__p___argc`, `__p___argv`, `__p__acmdln`, `__p__environ`, ...). These
  terminate or mutate the host, share host FP/SEH state with the CLR, or expose host pointers.
- **Time (34).** `_ctime*`/`_gmtime*`/`_localtime*`/`asctime` return pointers to host
  static/thread-local buffers; `_utime*`/`_futime*`/`_wutime*` write host file times;
  `_tzset` and `_setsystime` mutate host global state; the `_s` and `_get_timezone` group can
  enter the host invalid-parameter handler.
- **Heap introspection (8).** `_heapchk`, `_heapmin`, `_heapwalk`, `_get_heap_handle`,
  `_callnewh`, `_query_new_mode`, `_set_new_mode`, `_query_new_handler`.
- **Math policy (5).** The five exceptions listed in §6.1.
- **Convert (6).** `_ecvt`/`_fcvt`/`_gcvt` return host static buffers; `_ecvt_s`/`_fcvt_s`/
  `_gcvt_s` are secure variants.
- **Locale/global pointers (3 + 2 + 2).** `setlocale`, `_wsetlocale`, `localeconv`;
  `__p__mbcasemap`/`__p__mbctype`; `__p__environ`/`__p__wenviron`.
- **Static errno strings (included in the runtime count above).** `strerror`, `_strerror`,
  `_wcserror`, `__wcserror`, `perror`, `_wperror` and their `_s` variants return/format host
  static state.

The CSV has the exact list and a per-name reason.

### 7.4 Needs a new component (526)

These are implementable in managed code, but not with today's state. The dominant groups are:

- **Locale / codepage / multibyte (197 multibyte + 17 locale + most of 122 string + 61 convert).**
  `_create_locale`, `_getmbcp`, `mbrtowc`, `mbstowcs`, `wcrtomb`, the `_mb*` family,
  `is*`/`to*`, `strcoll`/`strxfrm`, `_stricmp`, `_strlwr`/`_strupr`, and the `_l` variants.
  They need a per-process locale object and codepage table; native calls read the host-global
  locale. The mutating `setlocale`/`_wsetlocale` and the static `localeconv` are `host-unsafe`
  above, not merely missing a component.
- **Filesystem (55).** `_access`, `_chdir`, `_getcwd`, `_mkdir`, `_rmdir`, `_unlink`, `remove`,
  `rename`, `_stat*`, `_findfirst*`/`_findnext*`, `_chmod`, `_umask`, `_getdiskfree`,
  `_fullpath`, `_lock_file`/`_unlock_file`. The sandbox FS already exists, but these APIs need
  `errno`, `struct _stat`/`_finddata_t` layouts, an fd table, or metadata the FS does not
  expose yet.
- **Process creation (33).** `_spawn*`/`_exec*`/`_cwait`; the managed `CreateProcess` path
  exists, but the CRT child state, argv/environment and `errno` do not.
- **Runtime startup / argv / errno (19).** `_configure_narrow_argv`,
  `_initialize_narrow_environment`, `_get_narrow_winmain_command_line`, `_set_app_type`,
  `_query_app_type`, `_set_error_mode`, `_errno`/`_set_errno`/`_get_doserrno`, and friends.
- **Environment / legacy (10).** `_dupenv_s` (heap), `_putenv`/`_wputenv` (errno),
  `_searchenv*` (guest `PATH` search), `_getcwd`/`_getdcwd` (guest cwd + errno).
- **Time (10).** `_Getdays`/`_Getmonths`/`_Gettnames` and the `__daylight`/`__timezone`/
  `__tzname`/`__dstbias` accessors; they need a guest time-zone/locale model.
- **Utility (2).** `rand`/`srand` share a host CRT global PRNG; a managed implementation
  should use a per-process state and the MSVC algorithm if exact sequences matter.

### 7.5 Private contract (1133)

The `api-ms-win-crt-private-l1-1-0` module has 1,134 exports, including C++ EH,
`_o_*` wrappers, `__std_*`, `_except_handler*`, `__C_specific_handler`, `memcpy`, `strlen`-
adjacent helpers, and UCRT internals. Do not target it as a guest contract. If an image
really imports a private name, treat it as a compatibility exception and add an explicit
managed implementation or alias; do not assume the ABI is stable.

### 7.6 Data exports (3)

`_wctype` (string), `_mbcasemap` (multibyte), `__dcrt_initial_narrow_environment` (private).
They live in read-only data sections; the current `PartiallyImagined` redirector requires an
executable export and will reject them. If a guest needs a different value, it must be handled
before resolution or by the guest loader, not by the `INT3` redirector.

## 8. Implementation recipe

### 8.1 Attribute shape

```csharp
using ImaginedNT.Core.Runtime;

public static class CrtStringApis
{
    [PartiallyImaginedApi("api-ms-win-crt-string-l1-1-0.dll", "strlen",
        ApiCategory.Memory, "ucrtbase!strlen")]
    private static ulong Strlen(ApiCallContext ctx)
    {
        // ...
    }
}
```

- Use the **exact** API-set file name. Module normalization removes `.dll` and lowercases, so
  `api-ms-win-crt-string-l1-1-0` and `api-ms-win-crt-string-l1-1-0.dll` are the same key.
- Add a `ucrtbase!name` alias when the guest may import the backing DLL directly.
- Do not add a second handler for the same native entry with a different body; the runtime
  rejects conflicting replacements.
- Wire the assembly into `BuiltinApis.Assemblies` (or import it with
  `ImaginedNT.Host run ... --import path/to/YourApis.dll`). Only assemblies listed there are
  scanned.

### 8.2 Buffer and string rules

- Use `ctx.Memory.TryRead`/`TryWrite` in chunks. Do not use `ReadUnicodeString` for an
  unbounded `wcslen`; its 0x8000-character cap is a policy choice, not C semantics.
- Return the C result exactly: `errno_t` in `EAX` (zero-extend), pointers in `RAX`, 32-bit
  `int` zero-extended. A handler that throws is caught by `DispatchManaged` and returns
  `STATUS_NOT_IMPLEMENTED` (`0xC0000002`) in `RAX`; that is not a valid CRT return. Validate
  and return the error code instead.
- For `getenv`-style APIs, cache the guest buffer in the process (or in a static
  `ConditionalWeakTable<ImaginedProcess, ...>`) so repeated calls return a stable pointer and
  do not leak virtual memory.
- For secure functions, implement the documented null/size/overlap checks and return
  `EINVAL`/`ERANGE`-style codes; do not call `ctx.SetLastError` instead of `errno`.
- Do not add `strtok`/`wcstok` until `ImaginedThread` has a per-thread CRT slot; use
  `strtok_s`/`wcstok_s`.

### 8.3 Tests

- Build a tiny MSVC guest that imports each target name from the API set and calls it with
  known inputs. The repository's `samples/build-samples.ps1` is the existing native-build
  path; a CRT-enabled sample is the right end-to-end test.
- Unit-test handlers through `ApiRegistry`/`ApiCallContext` with a real `ImaginedProcess` and
  virtual-memory buffers, including page-boundary strings and null/buffer-size cases.
- Test both `api-ms-win-crt-*!name` and `ucrtbase!name` imports when you add aliases.
- Test under `--strict` so a missing handler fails the load instead of silently falling back.
- Test a secure-function failure path and assert the host stays alive (this is the regression
  that catches accidental native forwarding of `*_s`).

## 9. Suggested order

1. **String/memory (55).** Highest value, no new state, easy to test.
2. **Integer format + utility + path (45).** Pure scalar/buffer work.
3. **Environment + virtual process/thread + time formatting (25).** Reuses existing process
   services; add stable guest buffers for `getenv`/`_get_pgmptr`.
4. **Add a per-thread `errno`.** This is a small `ImaginedThread` field, but it is a new base
   component. It unlocks `_access`, `_chdir`, `_getcwd`, `_mkdir`, `_rmdir`, `_unlink`,
   `remove`, `rename`, `_putenv`, and most of the filesystem group.
5. **Add a guest `FILE`/fd table and a guest heap.** This unlocks stdio and the heap family,
   and lets `_dupenv_s`/`strdup` work without host pointers.
6. **Add a locale/codepage model.** This unlocks roughly 390 locale-sensitive
   multibyte/ctype/convert names.
7. **Add CRT startup/argv/SEH/TLS.** This is the step that makes ordinary `/MD` MSVC
   executables plausible; `_initterm`, `_configure_narrow_argv`, `_initialize_narrow_environment`,
   `_set_app_type`, `_beginthread*` and `atexit` belong here.
8. **Keep the `safe-native` set native.** Audit it with `--trace`/`--verbose`; it is the
   intended fallback and does not need code.

## 10. Reproducing the inventory

The inventory was produced from the 26100 UCRT redist and the host `ucrtbase.dll`. A
PowerShell exporter for the API-set forwarder tables is in
[`scripts/crt-export-inventory.ps1`](../scripts/crt-export-inventory.ps1). It prints
`module,name,ordinal,forwarder`; the `ucrtbase` kind column and the status/reason columns are
the curation in this document.

```powershell
pwsh -File scripts/crt-export-inventory.ps1 `
  -Root 'C:\Program Files (x86)\Windows Kits\10\Redist\10.0.26100.0\ucrt\DLLs\x64' `
  -Out docs/crt-exports-raw.csv
```

Do not treat the counts as a permanent ABI: API-set versions are stable, but a future UCRT may
add exports. Re-run the inventory and re-check the new names against the status rules.

## Appendix A — full data

- [`crt-api-classification.csv`](crt-api-classification.csv) — all 2,485 rows:
  `module,name,ordinal,status,ucrt_kind,reason,forwarder`.
- [`API-COVERAGE.md`](API-COVERAGE.md) — the current managed NT/Win32 surface.
- [`ARCHITECTURE.md`](ARCHITECTURE.md) — Partially Imagined dispatch and native forwarding.
