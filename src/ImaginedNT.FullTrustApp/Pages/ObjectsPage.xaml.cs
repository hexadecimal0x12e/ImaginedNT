using System.Text;
using ImaginedNT.Core.Io;
using ImaginedNT.Core.Objects;
using ImaginedNT.Core.Runtime;
using ImaginedNT.FullTrustApp.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace ImaginedNT.FullTrustApp.Pages;

public sealed partial class ObjectsPage : Page
{
    private sealed class ObjectRow(NtObject obj)
    {
        public NtObject Object { get; } = obj;
        public List<string> Paths { get; } = [];
        public List<string> Handles { get; } = [];
        public string Display(bool nameOnly = false) => $"{(Object is DirectoryObject ? "[目录] " : "")}{(nameOnly && Paths.Count > 0 ? ObjectManager.GetName(Paths[0]) : Object.FullPath ?? Object.Name ?? "（未命名）")}　·　{Object.TypeName}\n命名空间 {Paths.Count}　句柄 {Handles.Count}";
        public bool Matches(string query) => Display().Contains(query, StringComparison.OrdinalIgnoreCase) ||
            Paths.Concat(Handles).Any(s => s.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private ImaginedRuntime? _runtime;
    private ObjectRow[] _objects = [];
    private ObjectRow[] _visible = [];
    private NtObject? _detailErrorObject;
    private string _directory = "\\";
    private bool _updating;

    public ObjectsPage() => InitializeComponent();

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        AppRuntime.Changed += OnRuntimeChanged;
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        AppRuntime.Changed -= OnRuntimeChanged;
        _runtime = null;
        _objects = [];
        _detailErrorObject = null;
        ApplyFilter();
    }

    private void OnRuntimeChanged()
    {
        if (!ReferenceEquals(_runtime, AppRuntime.IsRunning ? AppRuntime.Session?.Runtime : null)) Refresh();
    }

    private void OnRefreshClicked(object sender, RoutedEventArgs args)
    {
        try { Refresh(); }
        catch (Exception ex) { AppRuntime.ReportError("读取运行时对象", ex); }
    }

    internal void Refresh()
    {
        _runtime = AppRuntime.IsRunning ? AppRuntime.Session?.Runtime : null;
        var rows = new Dictionary<NtObject, ObjectRow>(ReferenceEqualityComparer.Instance);
        if (_runtime is not null)
        {
            foreach (var (path, obj) in _runtime.Objects.Enumerate())
            {
                if (!rows.TryGetValue(obj, out var row)) rows[obj] = row = new ObjectRow(obj);
                row.Paths.Add(path);
            }
            foreach (var p in _runtime.Processes)
            {
                foreach (var h in p.Handles.Snapshot())
                {
                    if (!rows.TryGetValue(h.Object, out var row)) rows[h.Object] = row = new ObjectRow(h.Object);
                    row.Handles.Add($"PID {p.Id} ({Path.GetFileName(p.ImagePath)})　0x{h.Handle:X}　访问 0x{h.GrantedAccess:X8}　继承 {(h.Inheritable ? "是" : "否")}　{h.Name}");
                }
            }
        }
        _objects = rows.Values.OrderBy(r => r.Object.FullPath ?? r.Object.Name ?? "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Object.TypeName).ThenBy(r => r.Object.CreatedAt).ToArray();
        while (_runtime is not null && _directory != "\\" && !_objects.Any(r => r.Object.IsContainer &&
                   r.Paths.Contains(_directory, StringComparer.OrdinalIgnoreCase)))
            _directory = ObjectManager.GetParent(_directory);
        ApplyFilter();
    }

    private void OnViewChanged(object sender, SelectionChangedEventArgs args)
    {
        if (ObjectList is not null) ApplyFilter();
    }

    private void OnUpClicked(object sender, RoutedEventArgs args) => NavigateDirectory(ObjectManager.GetParent(_directory));

    private void OnGoClicked(object sender, RoutedEventArgs args) => NavigateDirectory(PathInput.Text);

    private void OnPathKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Enter) return;
        args.Handled = true;
        NavigateDirectory(PathInput.Text);
    }

    private void NavigateDirectory(string path)
    {
        if (_runtime is null) return;
        try
        {
            path = ObjectManager.NormalizePath(path);
            NtObject? obj = null;
            for (var i = 0; i < 32; i++)
            {
                obj = _runtime.Objects.Resolve(path);
                if (obj is not SymbolicLinkObject link) break;
                path = link.Target.StartsWith('\\') ? link.Target : ObjectManager.Combine(ObjectManager.GetParent(link.FullPath ?? path), link.Target);
            }
            if (obj is not { IsContainer: true } || obj.IsDisposed)
            {
                AppRuntime.SetMessage($"对象目录不存在或无法打开: {path}");
                return;
            }
            _directory = obj.FullPath!;
            PathInput.Text = _directory;
            SearchBox.Text = "";
            Refresh();
            AppRuntime.SetMessage("");
        }
        catch (Exception ex) { AppRuntime.ReportError("打开对象目录", ex); }
    }

    private void OnObjectOpened(object sender, DoubleTappedRoutedEventArgs args) => OpenSelectedObject();

    private void OnObjectKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Enter) return;
        args.Handled = true;
        OpenSelectedObject();
    }

    private void OpenSelectedObject()
    {
        if (ObjectList.SelectedIndex < 0 || ObjectList.SelectedIndex >= _visible.Length) return;
        var row = _visible[ObjectList.SelectedIndex];
        if (!row.Object.IsContainer && row.Object is not SymbolicLinkObject) return;
        ViewSelector.SelectedIndex = 0;
        NavigateDirectory(row.Paths.FirstOrDefault() ?? row.Object.FullPath ?? "\\");
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs args)
    {
        if (ObjectList is not null) ApplyFilter();
    }

    private void ApplyFilter()
    {
        var selected = ObjectList.SelectedIndex >= 0 && ObjectList.SelectedIndex < _visible.Length
            ? _visible[ObjectList.SelectedIndex].Object : null;
        _updating = true;
        try
        {
            var rows = ViewSelector.SelectedIndex switch
            {
                0 => _objects.Where(r => r.Paths.Any(p => p != "\\" &&
                    string.Equals(ObjectManager.GetParent(p), _directory, StringComparison.OrdinalIgnoreCase))),
                1 => _objects.Where(r => r.Paths.Count == 0),
                _ => _objects.AsEnumerable()
            };
            _visible = rows.Where(r => r.Matches(SearchBox.Text.Trim()))
                .OrderByDescending(r => r.Object.IsContainer).ToArray();
            PageRows.Set(ObjectList, _visible.Select(r => r.Display(ViewSelector.SelectedIndex == 0)));
            ObjectList.SelectedIndex = _visible.Length == 0 ? -1 :
                Math.Max(0, Array.FindIndex(_visible, r => ReferenceEquals(r.Object, selected)));
        }
        finally { _updating = false; }
        PathInput.IsEnabled = GoButton.IsEnabled = _runtime is not null && ViewSelector.SelectedIndex == 0;
        UpButton.IsEnabled = PathInput.IsEnabled && _directory != "\\";
        if (PathInput.FocusState != FocusState.Keyboard) PathInput.Text = _directory;
        SummaryText.Text = _runtime is null ? "运行时未启动" :
            $"显示 {_visible.Length} / {_objects.Length} 个对象　·　命名空间 {_objects.Sum(r => r.Paths.Count)} 项　·　句柄 {_objects.Sum(r => r.Handles.Count)} 个";
        ShowObjectDetails();
    }

    private void OnObjectSelected(object sender, SelectionChangedEventArgs args)
    {
        if (!_updating) ShowObjectDetails();
    }

    private void ShowObjectDetails()
    {
        if (ObjectList.SelectedIndex < 0 || ObjectList.SelectedIndex >= _visible.Length)
        {
            ObjectDetails.Text = _runtime is null ? "运行时未启动" : "当前列表为空";
            return;
        }
        var row = _visible[ObjectList.SelectedIndex];
        var obj = row.Object;
        var text = new StringBuilder().AppendLine($"类型: {obj.TypeName} ({obj.ObjectType})")
            .AppendLine($"实现: {obj.GetType().FullName}")
            .AppendLine($"名称: {obj.Name ?? "（未命名）"}")
            .AppendLine($"完整路径: {obj.FullPath ?? "-"}")
            .AppendLine($"创建时间: {obj.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff zzz}")
            .AppendLine($"引用计数: {obj.ReferenceCount}")
            .AppendLine($"已释放: {(obj.IsDisposed ? "是" : "否")}")
            .AppendLine($"支持等待: {(obj.SupportsWait ? "是" : "否")}");
        try
        {
            if (!obj.IsDisposed)
            {
                if (obj.SupportsWait && obj is not ImaginedHandle)
                    text.AppendLine($"信号状态: {(obj.IsSignaled ? "有信号" : "无信号")}");
                if (obj.Info is { Length: > 0 } info) text.AppendLine($"信息: {info}");
                switch (obj)
                {
                    case EventObject e:
                        text.AppendLine($"重置方式: {(e.ManualReset ? "手动" : "自动")}");
                        break;
                    case MutantObject m:
                        text.AppendLine($"所有者线程: {m.OwnerThreadId}").AppendLine($"递归次数: {m.RecursionCount}");
                        break;
                    case SemaphoreObject s:
                        text.AppendLine($"当前计数: {s.CurrentCount}");
                        break;
                    case FileObject f:
                        text.AppendLine($"文件路径: {f.Path}").AppendLine($"目录: {(f.IsDirectory ? "是" : "否")}")
                            .AppendLine($"可读: {(f.CanRead ? "是" : "否")}　可写: {(f.CanWrite ? "是" : "否")}")
                            .AppendLine($"关闭时删除: {(f.DeleteOnClose ? "是" : "否")}");
                        try { text.AppendLine($"位置: {f.Position}　长度: {f.Length}"); }
                        catch (NotSupportedException) { text.AppendLine("位置 / 长度: 不支持查询"); }
                        break;
                    case ProcessObject p:
                        text.AppendLine($"进程 ID: {p.Process.Id}").AppendLine($"会话 ID: {p.Process.SessionId}")
                            .AppendLine($"映像: {p.Process.ImagePath}").AppendLine($"退出码: {p.Process.ExitCode?.ToString() ?? "-"}");
                        break;
                    case ThreadObject t:
                        text.AppendLine($"线程 ID: {t.Thread.Id}").AppendLine($"本机线程 ID: {t.Thread.NativeId}")
                            .AppendLine($"线程状态: {t.Thread.State}");
                        break;
                }
            }
            _detailErrorObject = null;
        }
        catch (ObjectDisposedException) { text.AppendLine("对象已在刷新后释放"); }
        catch (Exception ex)
        {
            text.AppendLine($"读取对象状态失败: {ex.Message}");
            if (!ReferenceEquals(_detailErrorObject, obj))
            {
                _detailErrorObject = obj;
                AppRuntime.ReportError("读取对象详情", ex);
            }
        }
        text.AppendLine("\n命名空间路径");
        foreach (var path in row.Paths) text.AppendLine($"  {path}");
        if (row.Paths.Count == 0) text.AppendLine("  未注册到命名空间");
        if (obj.IsContainer)
        {
            text.AppendLine("\n直接子对象");
            foreach (var child in _objects)
                foreach (var path in child.Paths)
                    if (path != obj.FullPath && string.Equals(ObjectManager.GetParent(path), obj.FullPath, StringComparison.OrdinalIgnoreCase))
                        text.AppendLine($"  {path}　{child.Object.TypeName}");
        }
        text.AppendLine($"\n持有句柄 ({row.Handles.Count})");
        foreach (var handle in row.Handles) text.AppendLine($"  {handle}");
        var details = text.ToString();
        if (ObjectDetails.Text != details) ObjectDetails.Text = details;
    }
}
