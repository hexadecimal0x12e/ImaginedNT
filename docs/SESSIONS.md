# NT sessions

One `ImaginedRuntime` can contain multiple virtual NT sessions. Session IDs belong
to the guest runtime and are independent of the host's Windows sessions.
Session 0 always exists. `RuntimeOptions.DefaultSessionId` selects the session
created and connected to the virtual console at startup (default: 0).
The command-line runner exposes this as `--session <id>`.

```csharp
using var runtime = new ImaginedRuntime(options, BuiltinApis.Assemblies);
var session = runtime.CreateSession(name: "Desktop-2");
var process = runtime.CreateProcess(new ProcessCreateOptions
{
    ImagePath = imagePath,
    SessionId = session.Id
});
runtime.SetActiveConsoleSession(session.Id);
```

`ProcessCreateOptions.SessionId` must refer to an existing session. When omitted,
it inherits the parent's session, or uses the runtime default for host-created
processes. A process keeps its session for its lifetime. PID allocation and
process enumeration remain runtime-wide; the synthetic System process belongs
to session 0.

`Sessions` and `GetSession` return snapshots. `SetActiveConsoleSession` connects
the selected session and marks the previous console session disconnected.
Passing `null` detaches the console. Disconnecting does not terminate processes.
`DeleteSession` returns false for session 0, unknown sessions, or sessions with
processes that have not finished disposal. Deletion releases the session's
object namespace. Global objects remain until runtime disposal.

Named events, mutexes and semaphores use these paths:

| Win32 name | NT object path |
| --- | --- |
| `Example` / `Local\Example` in session 0 | `\BaseNamedObjects\Example` |
| `Example` / `Local\Example` in session N | `\Sessions\N\BaseNamedObjects\Example` |
| `Global\Example` in any session | `\BaseNamedObjects\Example` |

NT APIs retain absolute paths and object-directory-relative paths. Each session's
base directory also contains `Global` and `Local` symbolic links. Object creation
and lookup expand intermediate symbolic links consistently. Handles remain
per process. These implemented object APIs always use the runtime implementation.
Forwarded APIs use the native handle bridge when native handle conversion is enabled.

Guest queries expose virtual session IDs through `ProcessIdToSessionId`,
`NtQueryInformationProcess(ProcessSessionInformation)`, the NT process list and
WMI's `Win32_Process.SessionId`. Basic WTS support includes
`WTSGetActiveConsoleSessionId`, `WTSEnumerateSessionsA/W`,
`WTSQuerySessionInformationA/W` (`WTSSessionId`, `WTSWinStationName`,
`WTSConnectState`) and `WTSFreeMemory`. WTS calls support the current virtual
server only; unsupported information classes return `ERROR_NOT_SUPPORTED`.

This models kernel session membership and named synchronization objects. It does
not implement per-session USER/GDI desktops, authentication/token session
assignment, RDP transport, or additional kernel object types such as sections.

Namespace reference: [Microsoft Learn: Kernel object namespaces](https://learn.microsoft.com/en-us/windows/win32/termserv/kernel-object-namespaces).
