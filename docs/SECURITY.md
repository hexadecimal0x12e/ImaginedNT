# 用户和权限

每个 ImaginedRuntime 拥有独立的 `SecurityAccounts`，通过 `runtime.Security` 管理。内置 SYSTEM（S-1-5-18）、Everyone、Authenticated Users、Users 和 Administrators；普通新用户默认属于 Users，所有账户默认拥有已启用的 SeChangeNotifyPrivilege。用户密码以随机盐和 PBKDF2-SHA256 保存于运行时内存，不使用宿主账户。当前账户库随运行时销毁，重新启动后需重新配置；已有令牌保留登录时的用户组和权限快照，组成员或权限变更在重新登录后生效。

```csharp
runtime.Security.CreateUser("Alice", "password");
runtime.Security.CreateGroup("Developers");
runtime.Security.SetGroupMembership("Alice", "Developers");
runtime.Security.SetPrivilege("Alice", "SeDebugPrivilege");
var token = runtime.Security.Logon("Alice", "password");
var process = runtime.CreateProcess(new ProcessCreateOptions
{
    ImagePath = imagePath,
    Token = token
});
```

`SetPassword` 修改密码，`SetEnabled` 启用或禁用账户，`DeleteUser` 删除用户；SYSTEM 禁止密码登录、禁用和删除。`CreateToken` 是供受信任宿主和系统组件使用的入口，不验证密码。未指定 Token 的子进程继承父进程主令牌，无父进程时使用 SYSTEM。`thread.ImpersonationToken` 只覆盖该线程的身份，`thread.EffectiveToken` 返回访问资源时使用的身份。

`NtObject.Security` 可设置 `ResourceSecurity`，由句柄表在创建句柄时检查。未设置时保留原策略，空 ACL 拒绝非零权限；显式拒绝优先于允许，Access 使用对应对象的访问掩码。例如允许 Alice 和 SYSTEM 打开某个对象：

```csharp
obj.Security = new ResourceSecurity([
    new AccessRule("Alice", 0x001F0003),
    new AccessRule("S-1-5-18", uint.MaxValue)
]);
```

新建的同步对象默认只允许创建者和 SYSTEM 访问；可通过 `NtObject.Security` 更改。等待、事件修改、互斥体和信号量操作还会检查句柄权限。句柄复制不能增加原句柄的权限。跨用户打开进程或进程令牌要求 SYSTEM 或 SeDebugPrivilege。用户环境变量和 HKEY_CURRENT_USER 按用户 SID 隔离；普通用户不能修改机器环境变量和机器注册表，不能通过 HKEY_USERS 访问其他用户的配置。

来宾 API 包括：

- `GetUserNameA/W`、`GetUserNameExA/W`（本地账户的 NameSamCompatible）、`LookupAccountSidA/W`、`LogonUserA/W`、`OpenProcessToken`、`OpenThreadToken`、`DuplicateToken/Ex`、`ImpersonateLoggedOnUser`、`RevertToSelf`、`CheckTokenMembership`。账户名称与 SID 查询均使用模拟用户库和当前令牌。
- `GetTokenInformation` 支持 User、Groups、Privileges、Owner、PrimaryGroup、Type、ImpersonationLevel、Statistics、RestrictedSids、ElevationType、Elevation、HasRestrictions、VirtualizationAllowed、IntegrityLevel、UIAccess 和 IsAppContainer；UserClaimAttributes 和 DeviceClaimAttributes 返回 v1 空声明结构。完整性级别为身份对应的模拟查询值。NT 对应入口支持 NtOpenProcessToken、NtOpenThreadToken、NtQueryInformationToken 及其 Zw 别名。
- `AdjustTokenPrivileges` 支持启用、禁用和移除令牌已有权限，以及查询和恢复先前状态；请求未授予的权限时返回成功并设置 ERROR_NOT_ALL_ASSIGNED。权限状态反映在 `GetTokenInformation` 和访问检查中，复制令牌独立保存权限状态。
- `CreateProcessAsUserA/W` 接受主令牌；创建其他用户的进程要求 SYSTEM 或 SeAssignPrimaryTokenPrivilege。
- `NetUserAdd` 支持 USER_INFO_1，`NetUserSetInfo` 支持密码（1003）和禁用标志（1008），以及 `NetUserDel`、`NetLocalGroupAdd`（0/1）、`NetLocalGroupAddMembers/DelMembers`（3）。这些管理入口要求 SYSTEM 或 Administrators 成员，作用域仅限本地模拟账户。

文件系统权限通过 [VirtFS YAML](VIRTFS.md) 配置。SYSTEM 和 Administrators 同样遵守 YAML 规则，不自动绕过文件访问限制。

FullTrust 模拟会话启动后显示登录界面，账户库为空时先创建管理员账户。LSASS 使用运行时用户库验证登录和解锁凭据，解锁还要求用户 SID 一致。后续普通用户由已登录的管理员创建。系统组件使用 SYSTEM，userinit、explorer 及用户启动的程序使用登录用户令牌；初始程序等待用户登录后启动。注销会清除该会话的用户进程、令牌和桌面授权。
