# VirtFS YAML 卷

CLI 保留 `--drive D=<目录>`，同时接受：

```text
--drive D=<配置.yaml>
--drive=D=<配置.yaml>
--drive=<配置.yaml>
```

裸 YAML 路径按参数顺序分配 D–Z、A–B 中的空闲盘符。显式指定 C 可替换 C 卷；重复指定盘符报错。FullTrust GUI 的“沙盒根（C:）”区域可直接点击“选择 YAML 定义”配置 C 卷；其他卷在列表选中后点击“加载 YAML”；未选中已有卷时使用盘符下拉框。停止来宾后才能修改配置，下次启动重新读取原 YAML。

参见 `samples/virtfs_maptable.yaml`。文件头为 `ImaginedNT VirtFS MapTable: v1`，支持 `Label`、十六进制 `Serial`、字节数 `MaximumSpace`、`Readonly`、`Inherit`、`Default`、`HostRoot`、`Initials` 和 `FS`。容量为模拟值，不执行存储配额。相对 HostRoot 和主机重映射路径以配置文件所在目录为基准。

`FS` 是映射节点，规则名称以 `-` 开头；权限表达式用逗号分隔：

- `+read/-read`、`+write/-write`、`+enum/-enum`、`+execute/-execute`、`+delete/-delete`。属性查询允许使用 `read` 或 `enum` 权限；切换当前目录要求 `enum` 权限。
- `exclude` 或 `+exclude` 排除文件或目录；`-exclude` 恢复可见性，可覆盖上级的排除配置。可用于 Default、模板、`<self>`、`<left>` 和路径规则，遵循相同的继承与覆盖顺序。排除的项目不出现在枚举结果中，直接访问返回不存在；若后代显式设置了 `-exclude`，且目录具有 `+enum` 权限，则允许列出该目录中可见的项目及通往可见后代的中间目录。枚举仅要求 `enum` 权限，读取内容仍须满足 `read` 权限。
- `<模板>` 展开 Initials 中的模板；`-<模板>` 反转权限，`<default>` 引用 Default。
- `<self>` 表示当前目录，`<left>` 表示本层没有命中其他规则的项目。
- `*` 匹配单层，`**` 跨目录匹配。忽略大小写，先应用通配规则，再应用精确规则；同类按文件顺序覆盖。
- 父级规则默认继承，`Inherit: false` 时子级从 Default 开始。
- `+read:remap_host@"d:\source"`、`+write:remap_virt@"D:\target"` 分别指定主机和虚拟目标；目录规则将剩余子路径追加到目标。虚拟目标仍检查自身权限，并限制递归深度。
- `+write:discard` 写入成功且推进文件位置，不保存内容。读取仍走读目标。

未声明权限默认为拒绝。卷 Readonly 和运行时禁止写入优先于路径权限。Win32/NT 文件对象允许读写目标不同并共享逻辑文件位置，写入不会自动复制读目标。

当前 CRT 使用原生 FILE 和描述符，仅支持可表达为单个主机文件的映射；读写目标不同或 discard 返回不支持。无法用单个主机文件表示的映射对象不向原生转发器提供主机句柄。
