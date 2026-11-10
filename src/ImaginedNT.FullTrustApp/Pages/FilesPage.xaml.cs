using System.Text;
using ImaginedNT.FullTrustApp.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace ImaginedNT.FullTrustApp.Pages;

public sealed partial class FilesPage : Page
{
    private FileEntry[] _files = [];
    private string _filePath = "C:\\";
    private bool _refreshing;

    public FilesPage() => InitializeComponent();

    internal async Task RefreshAsync()
    {
        if (_refreshing || !AppRuntime.IsRunning) return;
        _refreshing = true;
        try { await RefreshFilesAsync(); }
        catch (Exception ex) { AppRuntime.ReportError("读取文件系统", ex); }
        finally { _refreshing = false; }
    }

    private async Task RefreshFilesAsync()
    {
        var session = AppRuntime.Session;
        var fs = session?.Runtime.PrimaryProcess?.FileSystem;
        if (fs is null) return;
        var path = _filePath;
        var result = await Task.Run(() =>
        {
            try
            {
                var host = fs.MapPath(path);
                var files = Directory.EnumerateFileSystemEntries(host).Select(f =>
                {
                    var directory = Directory.Exists(f);
                    return new FileEntry(Path.GetFileName(f), f, directory, directory ? 0 : new FileInfo(f).Length,
                        directory ? Directory.GetLastWriteTime(f) : File.GetLastWriteTime(f));
                }).OrderByDescending(f => f.IsDirectory).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToArray();
                return (Files: files, Path: fs.GetFullPath(path));
            }
            catch (Exception ex) { AppRuntime.ReportError("读取文件系统", ex); return (Files: Array.Empty<FileEntry>(), Path: path); }
        });
        if (!AppRuntime.IsRunning || session != AppRuntime.Session || path != _filePath) return;
        _files = result.Files;
        _filePath = result.Path;
        SandboxPathText.Text = string.Join("　·　", fs.Drives.Select(d => $"{d.RootPath} → {d.HostDirectory}"));
        if (FilePathInput.FocusState != FocusState.Keyboard) FilePathInput.Text = _filePath;
        PageRows.Set(FileList, _files.Select(f => $"{(f.IsDirectory ? "[目录]" : "[文件]")} {f.Name}\n{(f.IsDirectory ? "" : f.Length.ToString("N0") + " B　")}{f.Modified:yyyy-MM-dd HH:mm:ss}"));
    }

    private async Task GoToFilePathAsync()
    {
        _filePath = FilePathInput.Text;
        _files = [];
        FileList.ItemsSource = null;
        FilePreview.Text = "";
        await RefreshAsync();
    }

    private async void OnFileGoClicked(object sender, RoutedEventArgs args) => await GoToFilePathAsync();

    private async void OnFilePathKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Enter) return;
        args.Handled = true;
        await GoToFilePathAsync();
    }

    private async void OnFileUpClicked(object sender, RoutedEventArgs args)
    {
        FilePathInput.Text = _filePath.TrimEnd('\\').Length == 2 ? _filePath : _filePath.TrimEnd('\\') + "\\..";
        await GoToFilePathAsync();
    }

    private async void OnFileOpened(object sender, DoubleTappedRoutedEventArgs args)
    {
        if (FileList.SelectedIndex < 0 || FileList.SelectedIndex >= _files.Length || !_files[FileList.SelectedIndex].IsDirectory) return;
        FilePathInput.Text = _filePath.TrimEnd('\\') + "\\" + _files[FileList.SelectedIndex].Name;
        await GoToFilePathAsync();
    }

    private async void OnFileSelected(object sender, SelectionChangedEventArgs args)
    {
        if (FileList.SelectedIndex < 0 || FileList.SelectedIndex >= _files.Length) return;
        var f = _files[FileList.SelectedIndex];
        if (f.IsDirectory) { FilePreview.Text = "双击打开目录"; return; }
        var session = AppRuntime.Session;
        try
        {
            var text = await Task.Run(() =>
            {
                if (Path.GetExtension(f.Name).ToLowerInvariant() is ".exe" or ".dll") return "PE 二进制文件";
                using var reader = new StreamReader(f.HostPath, Encoding.UTF8, true);
                var chars = new char[8192];
                var count = reader.ReadBlock(chars, 0, chars.Length);
                return chars.AsSpan(0, count).Contains('\0') ? "二进制文件"
                    : new string(chars, 0, count) + (reader.EndOfStream ? "" : "\n[仅预览前 8192 个字符]");
            });
            if (session == AppRuntime.Session && !AppRuntime.IsClosed && FileList.SelectedIndex >= 0 &&
                FileList.SelectedIndex < _files.Length && f.HostPath == _files[FileList.SelectedIndex].HostPath)
                FilePreview.Text = text;
        }
        catch (Exception ex) { AppRuntime.ReportError("预览文件", ex); }
    }

    private sealed record FileEntry(string Name, string HostPath, bool IsDirectory, long Length, DateTime Modified);
}
