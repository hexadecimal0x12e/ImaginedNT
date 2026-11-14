using System.Threading.Channels;
using ImaginedNT.Core.Runtime;
using ImaginedNT.Core.Security;
using ImaginedNT.Core.Io;

namespace ImaginedNT.FullTrustApp.MockWindows;

internal enum VirtualSessionState { Starting, SignedOut, SigningIn, Active, Locked, SigningOut, Stopped }

internal sealed class VirtualInteractiveSession(uint id)
{
    internal uint Id { get; } = id;
    internal string UserName => UserToken?.User.Name ?? "";
    internal AccessToken? UserToken { get; set; }
    internal VirtualSessionState State { get; set; } = VirtualSessionState.Starting;
    internal ImaginedProcess? Winlogon { get; set; }
    internal ImaginedProcess? LogonUI { get; set; }
    internal ImaginedProcess? Dwm { get; set; }
    internal ImaginedProcess? Shell { get; set; }
    internal DesktopObject Desktop { get; set; } = null!;
    internal DesktopObject SecureDesktop { get; set; } = null!;
    internal Channel<SessionCommand> Commands { get; } = Channel.CreateUnbounded<SessionCommand>(new() { SingleReader = true });
}

internal sealed record VirtualSessionSnapshot(uint Id, string UserName, VirtualSessionState State, int? ShellId);
internal sealed record SessionCommand(Func<ImaginedProcessContext, CancellationToken, Task> Execute,
    TaskCompletionSource Completion);

/// <summary>模拟系统进程和会话生命周期；所有组件运行在 ImaginedProcess 中。</summary>
internal sealed class VirtualWindowsSystem
{
    private readonly ImaginedRuntime _runtime;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<VirtualInteractiveSession> _sessions = [];
    private readonly HashSet<ImaginedProcess> _critical = [];
    private readonly Channel<SessionCommand> _commands = Channel.CreateUnbounded<SessionCommand>(new() { SingleReader = true });
    private readonly Channel<SessionCommand> _lsaCommands = Channel.CreateUnbounded<SessionCommand>(new() { SingleReader = true });
    private TaskCompletionSource _logonChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ImaginedProcess? _lsass;
    private int _stopping;
    private Task? _boot;
    private Task? _stop;

    internal VirtualWindowsSystem(ImaginedRuntime runtime)
    {
        _runtime = runtime;
        VirtualSessionApis.Systems.Add(runtime, this);
        runtime.ProcessExited += OnProcessExited;
    }

    internal bool SecurityReady => _lsass is { ExitCode: null } && Volatile.Read(ref _stopping) == 0;
    internal bool IsStopping => Volatile.Read(ref _stopping) != 0;
    internal bool NeedsAccount => !_runtime.Security.Users.Any(u => u.Sid != "S-1-5-18");
    internal IReadOnlyList<VirtualSessionSnapshot> Sessions
    {
        get
        {
            lock (_gate) return _sessions.Select(s => new VirtualSessionSnapshot(s.Id, s.UserName, s.State,
                s.Shell is { ExitCode: null } p ? p.Id : null)).ToArray();
        }
    }

    internal Task StartAsync() => _boot ??= BootAsync();

    private async Task BootAsync()
    {
        _runtime.Trace.Info("virtual Windows boot: SMSS starting");
        await StartAsync("smss", null, 0, "WinSta0\\Default", async (c, token) =>
        {
            // Session 0 只承载系统组件，交互用户使用独立会话。
            c.Process.SetEnvironmentVariable("SESSIONNAME", "Services");
            var station = _runtime.WindowStations.CreateStation(0, "Service-0x0-3e7$");
            _runtime.WindowStations.CreateDesktop(station, "Default");
            await StartAsync("smss", c.Process, 0, "WinSta0\\Default", async (smss, ct) =>
            {
                await StartCsrssAsync(smss.Process, 0, "Service-0x0-3e7$\\Default", ct);
                await StartAsync("wininit", smss.Process, 0, "Service-0x0-3e7$\\Default", async (wininit, wt) =>
                {
                    await StartAsync("services", wininit.Process, 0, "Service-0x0-3e7$\\Default", (services, _) =>
                    {
                        services.Objects.EnsureDirectory(@"\BaseNamedObjects\ServiceControlManager");
                        return Task.CompletedTask;
                    }, true, ct: wt);
                    _lsass = await StartAsync("lsass", wininit.Process, 0, "Service-0x0-3e7$\\Default", (lsa, _) =>
                    {
                        lsa.Objects.EnsureDirectory(@"\BaseNamedObjects\LsaAuthentication");
                        return Task.CompletedTask;
                    }, true, body: (lsa, lt) => ReadCommandsAsync(_lsaCommands, lsa, lt), ct: wt);
                    await StartAsync("lsm", wininit.Process, 0, "Service-0x0-3e7$\\Default", EmptyAsync, true, ct: wt);
                }, true, ct: ct);
            }, transient: true, ct: token);
            await CreateSessionAsync(c, token);
        }, true, body: (c, token) => ReadCommandsAsync(_commands, c, token), ct: _shutdown.Token);

        _runtime.Trace.Info("virtual Windows boot complete");
    }

    internal Task CreateSessionAsync() => EnqueueAsync(_commands, async (c, token) => { await CreateSessionAsync(c, token); });

    private async Task CreateSessionAsync(ImaginedProcessContext c, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var session = new VirtualInteractiveSession(_runtime.CreateSession(name: "Console").Id);
        lock (_gate) _sessions.Add(session);
        try
        {
            await StartAsync("smss", c.Process, session.Id, "WinSta0\\Default", async (smss, ct) =>
            {
                await StartCsrssAsync(smss.Process, session.Id, "WinSta0\\Default", ct);
                session.Winlogon = await StartAsync("winlogon", smss.Process, session.Id, "WinSta0\\Default", async (winlogon, wt) =>
                {
                    session.Desktop = CreateDesktop(winlogon, "Default");
                    session.SecureDesktop = CreateDesktop(winlogon, "Winlogon");
                    SetDesktopSecurity(session);
                    var handle = VirtualSessionApis.CallWithName(winlogon, "user32.dll", "OpenDesktopW", "Winlogon", 0, 0, 0x10000000);
                    Require(handle);
                    Require(VirtualSessionApis.Call(winlogon, "user32.dll", "SetThreadDesktop", handle));
                    CreateDesktop(winlogon, "Disconnect").Security = SystemSecurity();
                    session.Dwm = await StartDwmAsync(session, winlogon, wt);
                    SwitchDesktop(winlogon, session.SecureDesktop);
                    await ShowLogonAsync(session, winlogon, wt);
                    SetState(session, VirtualSessionState.SignedOut);
                }, true, body: (winlogon, wt) => ReadCommandsAsync(session.Commands, winlogon, wt), ct: ct);
            }, transient: true, ct: token);
        }
        catch
        {
            SetState(session, VirtualSessionState.Stopped);
            session.Commands.Writer.TryComplete();
            throw;
        }
        _runtime.SetActiveConsoleSession(session.Id);
        _runtime.Trace.Info($"virtual console session ready: session={session.Id}");
    }

    private Task<ImaginedProcess> StartCsrssAsync(ImaginedProcess parent, uint id, string desktop, CancellationToken token) =>
        StartAsync("csrss", parent, id, desktop, (c, _) =>
        {
            Require(VirtualSessionApis.Call(c, VirtualSessionApis.Module, "CsrInitializeSession"));
            return Task.CompletedTask;
        }, true, ct: token);

    private Task<ImaginedProcess> StartDwmAsync(VirtualInteractiveSession s, ImaginedProcessContext c, CancellationToken token) =>
        StartAsync("dwm", c.Process, s.Id, "WinSta0\\Default", (dwm, _) =>
        {
            Require(VirtualSessionApis.Call(dwm, "user32.dll", "GetDesktopWindow"));
            return Task.CompletedTask;
        }, ct: token);

    private async Task<ImaginedProcess> StartAsync(string name, ImaginedProcess? parent, uint sessionId, string desktop,
        Func<ImaginedProcessContext, CancellationToken, Task> initialize, bool critical = false, bool transient = false,
        Func<ImaginedProcessContext, CancellationToken, Task>? body = null, CancellationToken ct = default,
        AccessToken? userToken = null)
    {
        ct.ThrowIfCancellationRequested();
        // 根 SMSS 的 Session 0 必须先存在。
        if (_runtime.GetSession(sessionId) is null) _runtime.CreateSession(sessionId, sessionId == 0 ? "Services" : null);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var process = _runtime.CreateImaginedProcess(new ProcessCreateOptions
        {
            ImagePath = name == "explorer" ? Path.Combine(_runtime.Options.SandboxRoot!, "Windows", "explorer.exe") :
                Path.Combine(_runtime.Options.SandboxRoot!, "Windows", "System32", name + ".exe"),
            CommandLine = name == "explorer" ? @"C:\Windows\explorer.exe" : $"C:\\Windows\\System32\\{name}.exe",
            CurrentDirectory = @"C:\Windows\System32",
            ParentId = parent?.Id,
            SessionId = sessionId,
            Desktop = desktop,
            IsRoot = parent is null,
            Token = userToken ?? parent?.Token ?? _runtime.Security.SystemToken
        }, async (c, token) =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _shutdown.Token);
            try
            {
                await initialize(c, linked.Token);
                linked.Token.ThrowIfCancellationRequested();
                ready.TrySetResult();
                if (body is not null) await body(c, linked.Token);
                else if (!transient) await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);
                return 0;
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                ready.TrySetCanceled(linked.Token);
                return 0;
            }
            catch (Exception ex)
            {
                ready.TrySetException(ex);
                _runtime.Trace.Error($"virtual {name} failed: {ex}", c.Process.Id);
                return -1;
            }
        });
        if (critical) lock (_gate) _critical.Add(process);
        try
        {
            if (!process.EnsureMainThread().Start()) throw new InvalidOperationException($"无法启动 {name}。");
            await ready.Task.WaitAsync(ct);
            if (transient)
            {
                await WaitForExitAsync(process);
                process.Dispose();
            }
            return process;
        }
        catch
        {
            process.Terminate(-1);
            throw;
        }
    }

    internal async Task<ImaginedProcess> WaitForLogonAsync()
    {
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                var shell = _sessions.FirstOrDefault(s => s.State == VirtualSessionState.Active && s.Shell is { ExitCode: null })?.Shell;
                if (shell is not null) return shell;
                changed = _logonChanged.Task;
            }
            await changed.WaitAsync(_shutdown.Token);
        }
    }

    internal Task CreateUserAsync(uint sessionId, string user, string password) => EnqueueAsync(_lsaCommands, (c, token) =>
    {
        var first = NeedsAccount;
        lock (_gate)
        {
            var s = _sessions.FirstOrDefault(s => s.Id == sessionId) ?? throw new ArgumentException("会话不存在。");
            if (first)
            {
                if (s.State != VirtualSessionState.SignedOut) throw new UnauthorizedAccessException("当前会话不能创建用户。");
            }
            else if (s.State != VirtualSessionState.Active || s.UserToken is null || s.UserToken.Match("Administrators") < 0)
                throw new UnauthorizedAccessException("创建用户需要以管理员登录。");
        }
        try { _runtime.Security.CreateUser(user, password); }
        catch (ArgumentException) { throw new ArgumentException("用户名无效，或账户名称已存在。"); }
        if (first) _runtime.Security.SetGroupMembership(user, "Administrators");
        return Task.CompletedTask;
    });

    private Task<AccessToken> AuthenticateAsync(string user, string password)
    {
        if (!SecurityReady) throw new InvalidOperationException("身份验证服务尚未就绪。");
        return EnqueueAsync(_lsaCommands, (c, token) => Task.FromResult(_runtime.Security.Logon(user, password)));
    }

    internal Task SignInAsync(uint id, string user, string password = "") => SendAsync(id, async (s, c, token) =>
    {
        if (s.State is not (VirtualSessionState.SignedOut or VirtualSessionState.Locked)) throw new InvalidOperationException("当前会话不能登录。");
        AccessToken userToken;
        try { userToken = await AuthenticateAsync(user, password); }
        catch (UnauthorizedAccessException) { throw new UnauthorizedAccessException("用户名或密码错误，或账户已禁用。"); }
        var unlocking = s.State == VirtualSessionState.Locked;
        if (unlocking && s.UserToken?.User.Sid != userToken.User.Sid) throw new UnauthorizedAccessException("登录账户与锁定会话的账户不一致。");
        SetState(s, VirtualSessionState.SigningIn);
        try
        {
            await StopProcessAsync(s.LogonUI);
            s.LogonUI = null;
            if (!unlocking)
            {
                lock (_gate) s.UserToken = userToken;
                SetDesktopSecurity(s);
                await StartShellAsync(s, c, token);
            }
            SwitchDesktop(c, s.Desktop);
            _runtime.SetActiveConsoleSession(id);
            SetState(s, VirtualSessionState.Active);
            _runtime.Trace.Info($"virtual user {(unlocking ? "unlocked" : "signed in")}: session={s.Id}, SID={userToken.User.Sid}");
        }
        catch
        {
            if (!unlocking)
            {
                await StopUserProcessesAsync(s, c.Process);
                ClearUser(s);
            }
            SwitchDesktop(c, s.SecureDesktop);
            await ShowLogonAsync(s, c, token);
            SetState(s, unlocking ? VirtualSessionState.Locked : VirtualSessionState.SignedOut);
            throw;
        }
    });

    private async Task StartShellAsync(VirtualInteractiveSession s, ImaginedProcessContext c, CancellationToken token)
    {
        await StartAsync("userinit", c.Process, s.Id, "WinSta0\\Default", async (userinit, ct) =>
        {
            userinit.Process.SetEnvironmentVariable("SESSIONNAME", $"Console-{s.Id}");
            var shell = await StartAsync("explorer", userinit.Process, s.Id, "WinSta0\\Default", (explorer, _) =>
            {
                Require(VirtualSessionApis.Call(explorer, "user32.dll", "GetDesktopWindow"));
                return Task.CompletedTask;
            }, ct: ct);
            lock (_gate) s.Shell = shell;
        }, transient: true, ct: token, userToken: s.UserToken ?? throw new InvalidOperationException("会话未登录。"));
    }

    internal Task LockAsync(uint id) => SendAsync(id, async (s, c, token) =>
    {
        if (s.State != VirtualSessionState.Active) return;
        await ShowLogonAsync(s, c, token);
        SwitchDesktop(c, s.SecureDesktop);
        SetState(s, VirtualSessionState.Locked);
    });

    internal Task SignOutAsync(uint id) => SendAsync(id, async (s, c, token) =>
    {
        if (s.State is not (VirtualSessionState.Active or VirtualSessionState.Locked or VirtualSessionState.SigningOut)) return;
        SetState(s, VirtualSessionState.SigningOut);
        SwitchDesktop(c, s.SecureDesktop);
        await StopUserProcessesAsync(s, c.Process);
        _runtime.Trace.Info($"virtual user signed out: session={s.Id}, SID={s.UserToken?.User.Sid}");
        ClearUser(s);
        await ShowLogonAsync(s, c, token);
        SetState(s, VirtualSessionState.SignedOut);
    });

    private static ResourceSecurity SystemSecurity() => new([new("S-1-5-18", uint.MaxValue)]);

    private static void SetDesktopSecurity(VirtualInteractiveSession s)
    {
        var rules = new List<AccessRule> { new("S-1-5-18", uint.MaxValue) };
        if (s.UserToken is { } token) rules.Add(new(token.User.Sid, uint.MaxValue));
        s.Desktop.Security = new(rules);
        s.Desktop.WindowStation.Security = new(rules);
        s.SecureDesktop.Security = SystemSecurity();
    }

    private void ClearUser(VirtualInteractiveSession s)
    {
        lock (_gate) { s.UserToken = null; s.Shell = null; }
        SetDesktopSecurity(s);
    }

    private async Task StopUserProcessesAsync(VirtualInteractiveSession s, ImaginedProcess winlogon)
    {
        while (true)
        {
            var processes = _runtime.Processes.Reverse().Where(p => p.SessionId == s.Id && p != winlogon && p != s.Dwm).ToArray();
            lock (_gate) processes = processes.Where(p => !_critical.Contains(p)).ToArray();
            if (processes.Length == 0) break;
            foreach (var p in processes) await StopProcessAsync(p);
        }
        s.LogonUI = null;
    }

    private async Task ShowLogonAsync(VirtualInteractiveSession s, ImaginedProcessContext c, CancellationToken token)
    {
        if (s.LogonUI is { ExitCode: null }) return;
        s.LogonUI = await StartAsync("LogonUI", c.Process, s.Id, "WinSta0\\Winlogon", EmptyAsync, ct: token);
    }

    internal ImaginedProcess? GetShell(DesktopObject? desktop)
    {
        lock (_gate) return _sessions.FirstOrDefault(s => s.Desktop == desktop && s.State == VirtualSessionState.Active)?.Shell
            is { ExitCode: null } p ? p : null;
    }

    internal bool CanManageUsers(uint id)
    {
        lock (_gate) return _sessions.Any(s => s.Id == id && s.State == VirtualSessionState.Active && s.UserToken?.Match("Administrators") >= 0);
    }

    internal Task<ImaginedProcess> StartUserProcessAsync(string image, DesktopObject? desktop, string commandLine)
    {
        var id = desktop?.SessionId ?? _runtime.ActiveConsoleSessionId;
        VirtualInteractiveSession session;
        lock (_gate)
        {
            session = _sessions.FirstOrDefault(s => s.Id == id)
                ?? throw new InvalidOperationException("没有可用的用户会话。");
            if (desktop is not null && desktop != session.Desktop) throw new UnauthorizedAccessException("用户程序只能在登录用户的桌面启动。");
        }
        return EnqueueAsync(session.Commands, async (c, token) =>
        {
            if (session.State != VirtualSessionState.Active || session.Shell is not { ExitCode: null } shell || session.UserToken is not { } userToken)
                throw new UnauthorizedAccessException("请先登录或解锁此会话。");
            var path = image;
            if (!Path.IsPathFullyQualified(image))
            {
                _ = shell.FileSystem.MapPath(image, "C:\\", FileOperation.Execute);
                path = shell.FileSystem.MapPath(image, "C:\\", FileOperation.Read);
            }
            var process = _runtime.CreateProcess(new ProcessCreateOptions
            {
                ImagePath = path, CommandLine = commandLine, CurrentDirectory = shell.CurrentDirectory,
                ParentId = shell.Id, SessionId = session.Id, Desktop = "WinSta0\\Default", IsRoot = false, Token = userToken
            });
            try
            {
                await Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();
                    process.LoadImage(path);
                    if (!process.EnsureMainThread().Start()) throw new InvalidOperationException("无法启动进程主线程。");
                }, token);
                return process;
            }
            catch
            {
                await StopProcessAsync(process);
                throw;
            }
        });
    }

    private Task SendAsync(uint id, Func<VirtualInteractiveSession, ImaginedProcessContext, CancellationToken, Task> execute)
    {
        VirtualInteractiveSession s;
        lock (_gate) s = _sessions.FirstOrDefault(s => s.Id == id) ?? throw new ArgumentException("会话不存在。");
        return EnqueueAsync(s.Commands, (c, token) => execute(s, c, token));
    }

    private async Task EnqueueAsync(Channel<SessionCommand> commands, Func<ImaginedProcessContext, CancellationToken, Task> execute)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await commands.Writer.WriteAsync(new(execute, completion), _shutdown.Token);
        await completion.Task.WaitAsync(_shutdown.Token);
    }

    private async Task<T> EnqueueAsync<T>(Channel<SessionCommand> commands, Func<ImaginedProcessContext, CancellationToken, Task<T>> execute)
    {
        T result = default!;
        await EnqueueAsync(commands, async (c, token) => { result = await execute(c, token); });
        return result;
    }

    private static async Task ReadCommandsAsync(Channel<SessionCommand> commands, ImaginedProcessContext c, CancellationToken token)
    {
        try
        {
            await foreach (var command in commands.Reader.ReadAllAsync(token))
            {
                try { await command.Execute(c, token); command.Completion.TrySetResult(); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { command.Completion.TrySetCanceled(token); throw; }
                catch (Exception ex) { command.Completion.TrySetException(ex); }
            }
        }
        finally
        {
            commands.Writer.TryComplete();
            while (commands.Reader.TryRead(out var command)) command.Completion.TrySetCanceled();
        }
    }

    private DesktopObject CreateDesktop(ImaginedProcessContext c, string name)
    {
        var handle = VirtualSessionApis.CallWithName(c, "user32.dll", "CreateDesktopW", name, 0, 0, 0, 0x10000000, 0);
        Require(handle);
        try { return _runtime.WindowStations.FindDesktop(c.Process.WindowStation, name)!; }
        finally { c.Handles.Close(handle); }
    }

    private static void SwitchDesktop(ImaginedProcessContext c, DesktopObject desktop)
    {
        var handle = VirtualSessionApis.CallWithName(c, "user32.dll", "OpenDesktopW", desktop.Name!, 0, 0, 0x10000000);
        Require(handle);
        try { Require(VirtualSessionApis.Call(c, "user32.dll", "SwitchDesktop", handle)); }
        finally { c.Handles.Close(handle); }
    }

    private void SetState(VirtualInteractiveSession s, VirtualSessionState state)
    {
        TaskCompletionSource changed;
        lock (_gate)
        {
            s.State = state;
            changed = _logonChanged;
            _logonChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        changed.TrySetResult();
        _runtime.Trace.Info($"virtual session state: session={s.Id}, state={state}");
    }

    private void OnProcessExited(ImaginedProcess p)
    {
        VirtualInteractiveSession? session;
        lock (_gate)
        {
            if (_stopping != 0) return;
            session = _sessions.FirstOrDefault(s => s.Dwm == p ||
                (s.Shell == p && (s.State is VirtualSessionState.Active or VirtualSessionState.Locked)));
            if (session is null && !_critical.Contains(p)) return;
        }
        if (session is not null) { _ = RestartSessionProcessAsync(session, p); return; }
        _ = Task.Run(() => _runtime.StopWithBugCheck($"虚拟关键进程退出：{Path.GetFileName(p.ImagePath)}，PID {p.Id}，会话 {p.SessionId}。"));
    }

    private async Task RestartSessionProcessAsync(VirtualInteractiveSession s, ImaginedProcess exited)
    {
        try
        {
            await EnqueueAsync(s.Commands, async (c, token) =>
            {
                var dwm = s.Dwm == exited;
                if (!dwm && (s.Shell != exited || s.State is not (VirtualSessionState.Active or VirtualSessionState.Locked))) return;
                await WaitForExitAsync(exited);
                exited.Dispose();
                if (dwm) s.Dwm = await StartDwmAsync(s, c, token);
                else
                {
                    await StartShellAsync(s, c, token);
                    SetState(s, s.State);
                }
                _runtime.Trace.Info($"virtual session process restarted: session={s.Id}, image={Path.GetFileName(exited.ImagePath)}");
            });
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception ex) { _runtime.Trace.Error($"virtual session process restart failed: session={s.Id}: {ex}"); }
    }

    internal Task StopAsync() => _stop ??= StopCoreAsync();

    internal void BeginStop()
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0) return;
        _runtime.ProcessExited -= OnProcessExited;
        _shutdown.Cancel();
        _commands.Writer.TryComplete();
        _lsaCommands.Writer.TryComplete();
    }

    private async Task StopCoreAsync()
    {
        BeginStop();
        if (_boot is not null)
            try { await _boot; }
            catch (Exception ex) { _runtime.Trace.Info($"virtual boot interrupted: {ex.Message}"); }
        var processes = _runtime.Processes.Reverse().ToArray();
        foreach (var p in processes) p.Terminate(0);
        await Task.WhenAll(processes.Select(StopProcessAsync));
        if (!_runtime.IsBugChecked)
            foreach (var s in Sessions) _runtime.DeleteSession(s.Id);
        lock (_gate)
            foreach (var s in _sessions) { s.Commands.Writer.TryComplete(); s.UserToken = null; s.Shell = null; s.State = VirtualSessionState.Stopped; }
        VirtualSessionApis.Systems.Remove(_runtime);
        _runtime.Trace.Info("virtual Windows stopped");
    }

    private static async Task StopProcessAsync(ImaginedProcess? process)
    {
        if (process is null) return;
        if (process.ExitCode is null) process.Terminate(0);
        await WaitForExitAsync(process);
        await Task.Run(process.Dispose);
    }

    private static Task WaitForExitAsync(ImaginedProcess process) => Task.Run(() =>
    {
        var started = Environment.TickCount64;
        foreach (var thread in process.Threads)
            if (!thread.Wait(TimeSpan.FromMilliseconds(Math.Max(0, 5000 - (Environment.TickCount64 - started))), out _))
                throw new TimeoutException($"虚拟进程未能退出：{Path.GetFileName(process.ImagePath)} ({process.Id})，线程 {thread.Id} (native {thread.NativeId})");
    });

    private static Task EmptyAsync(ImaginedProcessContext c, CancellationToken token) => Task.CompletedTask;
    private static void Require(ulong result)
    {
        if (result == 0) throw new InvalidOperationException("虚拟会话 API 调用失败。");
    }
}
