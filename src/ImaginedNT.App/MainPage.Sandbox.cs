using ImaginedNT.Core.Io;
using System.Text.Json;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.AccessCache;
using Windows.Storage.Pickers;
using Windows.UI.Xaml;

namespace ImaginedNT.App;

public sealed partial class MainPage
{
    private bool _choosingFolder;
    private readonly StorageFolderFileAccess _storageAccess;
    private readonly string _sandboxSettingsPath = Path.Combine(ApplicationData.Current.LocalFolder.Path, "sandbox-drives.json");

    private void LoadSandboxSettings()
    {
        if (HostFileSystem.FileExists(_sandboxSettingsPath))
        {
            var mappings = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_sandboxSettingsPath))
                ?? throw new InvalidDataException("沙盒配置为空。");
            if (!mappings.ContainsKey("C") || mappings.Any(d =>
                d.Key.Length != 1 || d.Key[0] < 'A' || d.Key[0] > 'Z' || !Path.IsPathFullyQualified(d.Value)))
                throw new InvalidDataException("沙盒驱动器配置无效。");
            _sandboxRoot = mappings["C"];
            foreach (var d in mappings)
                if (d.Key != "C") _drives[d.Key[0]] = d.Value;
        }
        UpdateSandboxSettings();
    }

    private async Task RestoreSandboxAccessAsync()
    {
        _storageAccess.Clear();
        foreach (var letter in _drives.Keys.Prepend('C').ToArray())
        {
            var token = $"SandboxDrive{letter}";
            if (StorageApplicationPermissions.FutureAccessList.ContainsItem(token))
            {
                var folder = await AwaitStorageOperationAsync(StorageApplicationPermissions.FutureAccessList.GetFolderAsync(token));
                _storageAccess.Register(letter, folder);
                if (letter == 'C') _sandboxRoot = folder.Path;
                else _drives[letter] = folder.Path;
                _log.Info($"sandbox folder access restored: {letter}: -> {folder.Path}");
            }
        }
        SaveSandboxSettings();
        UpdateSandboxSettings();
    }

    private void SaveSandboxSettings()
    {
        var mappings = _drives.ToDictionary(d => d.Key.ToString(), d => d.Value);
        mappings["C"] = _sandboxRoot;
        File.WriteAllText(_sandboxSettingsPath + ".tmp", JsonSerializer.Serialize(mappings));
        HostFileSystem.Move(_sandboxSettingsPath + ".tmp", _sandboxSettingsPath, overwrite: true);
    }

    private void UpdateSandboxSettings()
    {
        SandboxRootInput.Text = _sandboxRoot;
        DriveList.ItemsSource = _drives.OrderBy(d => d.Key).Select(d => $"{d.Key}:\\ → {d.Value}").ToArray();
        DriveLetterInput.ItemsSource = Enumerable.Range('A', 26).Select(c => (char)c)
            .Where(c => c != 'C' && !_drives.ContainsKey(c)).Select(c => c.ToString()).ToArray();
        DriveLetterInput.SelectedItem = _drives.ContainsKey('D') ? null : "D";
        if (DriveLetterInput.SelectedIndex < 0 && DriveLetterInput.Items.Count > 0)
            DriveLetterInput.SelectedIndex = 0;
        SandboxPathText.Text = $"C:\\ → {_sandboxRoot}";
    }

    private async void OnChooseSandboxRootClicked(object sender, RoutedEventArgs args) => await ChooseDriveFolderAsync('C');

    private async void OnAddDriveClicked(object sender, RoutedEventArgs args)
    {
        if (DriveLetterInput.SelectedItem is string letter) await ChooseDriveFolderAsync(letter[0]);
    }

    private async Task ChooseDriveFolderAsync(char letter)
    {
        if (_closed || _stopping || _choosingFolder || _session?.IsRunning == true) return;
        _choosingFolder = true;
        SandboxControls.IsEnabled = false;
        StartButton.IsEnabled = false;
        try
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
            picker.FileTypeFilter.Add("*");
            var folder = await AwaitStorageOperationAsync(picker.PickSingleFolderAsync());
            if (folder is null || _closed) return;
            if (string.IsNullOrWhiteSpace(folder.Path)) throw new IOException("请选择本地文件系统中的文件夹。");
            await Task.Run(() =>
            {
                var access = new StorageFolderFileAccess(_log);
                access.Register(letter, folder);
                using var entries = access.EnumerateInfos(folder.Path).GetEnumerator();
                entries.MoveNext();
                using var probe = new FileStream(access.OpenHandle(Path.Combine(folder.Path, $".imaginednt-{Guid.NewGuid():N}.tmp"),
                    FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, FileOptions.DeleteOnClose), FileAccess.ReadWrite, 1);
                probe.WriteByte(0);
                probe.Position = 0;
                if (probe.ReadByte() != 0) throw new IOException("无法读写所选目录。");
            });
            if (_closed) return;
            StorageApplicationPermissions.FutureAccessList.AddOrReplace($"SandboxDrive{letter}", folder);
            _storageAccess.Register(letter, folder);
            if (letter == 'C') _sandboxRoot = folder.Path;
            else _drives[letter] = folder.Path;
            SaveSandboxSettings();
            UpdateSandboxSettings();
            PageError.Text = "";
            _log.Info($"sandbox drive configured: {letter}: -> {folder.Path}");
        }
        catch (Exception ex) { ReportError("选择沙盒目录", ex); }
        finally
        {
            _choosingFolder = false;
            SandboxControls.IsEnabled = !_closed && !_stopping;
            StartButton.IsEnabled = !_closed && !_stopping;
        }
    }

    private static Task<T> AwaitStorageOperationAsync<T>(IAsyncOperation<T> operation) =>
        StorageFolderFileAccess.AwaitAsync(operation);

    private void OnRemoveDriveClicked(object sender, RoutedEventArgs args)
    {
        if (_closed || _stopping || _session?.IsRunning == true || DriveList.SelectedItem is not string selected) return;
        try
        {
            _drives.Remove(selected[0]);
            SaveSandboxSettings();
            var token = $"SandboxDrive{selected[0]}";
            if (StorageApplicationPermissions.FutureAccessList.ContainsItem(token))
                StorageApplicationPermissions.FutureAccessList.Remove(token);
            _storageAccess.Remove(selected[0]);
            UpdateSandboxSettings();
            PageError.Text = "";
            _log.Info($"sandbox drive removed: {selected[0]}:");
        }
        catch (Exception ex) { ReportError("移除驱动器", ex); }
    }
}
