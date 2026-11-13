using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using HomeBridge.Core;
using Microsoft.Win32;

namespace HomeBridge.Desktop;

public sealed partial class MainWindow
{
    private readonly ComboBox _filePeer = new() { Width = 220, DisplayMemberPath = nameof(TrustedPeer.Name) };
    private readonly ListBox _shares = new() { Height = 100, SelectionMode = SelectionMode.Extended };
    private readonly ListBox _remote = new() { Height = 230, SelectionMode = SelectionMode.Extended, DisplayMemberPath = nameof(SharedEntry.Name) };
    private readonly TextBlock _fileStatus = new() { TextWrapping = TextWrapping.Wrap };
    private Guid _remoteRoot;
    private string _remotePath = "";
    private bool _remoteUpload;
    private CancellationTokenSource? _fileOperation;

    private UIElement FilesPage()
    {
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(Label("Shared on this PC"));
        panel.Children.Add(new TextBlock { Text = "Shares are visible to paired PCs. Select a separate folder to allow incoming uploads. Existing files are never replaced.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(_shares);
        panel.Children.Add(Row(Action("Share files…", () =>
        {
            var picker = new OpenFileDialog { Multiselect = true };
            if (picker.ShowDialog(this) == true) AddShares(picker.FileNames, false);
        }), Action("Share folder…", () => ChooseShare(false)), Action("Allow uploads to folder…", () => ChooseShare(true)),
            Action("Stop sharing selected", () => RemoveShares(_shares.SelectedItems.Cast<string>().ToArray()))));
        panel.Children.Add(Label("Shared by a paired PC"));
        panel.Children.Add(Row(_filePeer, FileAction("Open shared files", async (peer, ct) =>
        {
            _remoteRoot = default;
            _remotePath = "";
            _remoteUpload = false;
            await ShowRemote(peer, ct);
        }), FileAction("Open folder", async (peer, ct) =>
        {
            if (_remote.SelectedItem is not SharedEntry { Folder: true } item) throw new InvalidOperationException("Select a folder.");
            if (_remoteRoot == default) { _remoteRoot = item.Share; _remoteUpload = item.Upload; }
            else _remotePath = Join(_remotePath, item.Name);
            await ShowRemote(peer, ct);
        }), FileAction("Back", async (peer, ct) =>
        {
            if (!_remotePath.Contains('/'))
            {
                if (_remotePath.Length == 0) { _remoteRoot = default; _remoteUpload = false; }
                _remotePath = "";
            }
            else _remotePath = _remotePath[.._remotePath.LastIndexOf('/')];
            await ShowRemote(peer, ct);
        })));
        _filePeer.SelectionChanged += (_, _) =>
        {
            _fileOperation?.Cancel();
            _remoteRoot = default;
            _remotePath = "";
            _remoteUpload = false;
            _remote.ItemsSource = null;
        };
        _remote.MouseDoubleClick += async (_, _) =>
        {
            if (_remote.SelectedItem is not SharedEntry { Folder: true } item) return;
            await RunFile(async (peer, ct) =>
            {
                if (_remoteRoot == default) { _remoteRoot = item.Share; _remoteUpload = item.Upload; }
                else _remotePath = Join(_remotePath, item.Name);
                await ShowRemote(peer, ct);
            });
        };
        panel.Children.Add(_remote);
        panel.Children.Add(Row(FileAction("Download selected…", DownloadSelected), FileAction("Upload files…", (peer, ct) => UploadSelected(peer, false, ct)),
            FileAction("Upload folder…", (peer, ct) => UploadSelected(peer, true, ct)), Action("Cancel transfer", () => _fileOperation?.Cancel())));
        panel.Children.Add(FileAction("Send Explorer selection", async (peer, ct) =>
        {
            var paths = _pendingExplorerSend.ToArray();
            if (paths.Length == 0) throw new InvalidOperationException("Select Send to Computer in Windows Explorer first.");
            await UploadPaths(peer, paths, ct);
            foreach (var path in paths) _pendingExplorerSend.Remove(path);
        }));
        panel.Children.Add(_fileStatus);
        RefreshShares();
        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private Button FileAction(string title, Func<TrustedPeer, CancellationToken, Task> work) => NetworkAction(title, () => RunFile(work), _fileStatus);

    private async Task RunFile(Func<TrustedPeer, CancellationToken, Task> work)
    {
        if (_fileOperation is not null) return;
        if (_filePeer.SelectedItem is not TrustedPeer peer) { _fileStatus.Text = "Select a paired PC first."; return; }
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        _fileOperation = operation;
        try { await work(peer, operation.Token); }
        catch (OperationCanceledException) { _fileStatus.Text = "Canceled or timed out. Completed files are kept."; }
        catch (Exception ex) { _fileStatus.Text = ex.Message; }
        finally { _fileOperation = null; }
    }

    private async Task ShowRemote(TrustedPeer peer, CancellationToken ct)
    {
        _remote.ItemsSource = null;
        _fileStatus.Text = "Opening shared files…";
        var entries = await _network.Browse(peer, _remoteRoot, _remotePath, ct);
        ct.ThrowIfCancellationRequested();
        _remote.ItemsSource = entries.OrderByDescending(e => e.Folder).ThenBy(e => e.Name).ToList();
        _fileStatus.Text = _remoteRoot == default ? "Select a share to open or download." : _remoteUpload ? "This folder accepts uploads." : "This folder is read-only.";
    }

    private void ChooseShare(bool upload)
    {
        var picker = new OpenFolderDialog();
        if (picker.ShowDialog(this) == true) AddShares([picker.FolderName], upload);
    }

    private void AddShares(IEnumerable<string> paths, bool upload)
    {
        var selected = paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var path in selected)
        {
            SharePaths.CheckLocal(path);
            if (Path.GetPathRoot(path) == path || ContainsPath(path, _store.DirectoryPath) || ContainsPath(_store.DirectoryPath, path))
                throw new InvalidDataException("Choose a specific file or folder outside HomeBridge’s private settings.");
        }
        var prompt = upload ? "Allow all paired PCs to browse this folder and add new files?" : "Allow all paired PCs to read the selected files or folders?";
        if (MessageBox.Show(this, prompt, "Confirm sharing", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        _network.Start();
        lock (_settings)
        {
            var previous = _settings.Shares.ToList();
            foreach (var path in selected)
            {
                _settings.Shares.RemoveAll(s => string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase));
                _settings.Shares.Add(new(Guid.NewGuid(), path, upload));
            }
            try { _store.Save(_settings); } catch { _settings.Shares = previous; throw; }
        }
        RefreshShares();
    }

    private static bool ContainsPath(string parent, string path)
    {
        var relative = Path.GetRelativePath(parent, path);
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(@"..\", StringComparison.Ordinal);
    }

    private void RemoveShares(IEnumerable<string> paths)
    {
        var selected = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        lock (_settings)
        {
            var previous = _settings.Shares.ToList();
            _settings.Shares.RemoveAll(s => selected.Contains(s.Path));
            try { _store.Save(_settings); } catch { _settings.Shares = previous; throw; }
        }
        RefreshShares();
    }

    private void RefreshShares()
    {
        lock (_settings) _shares.ItemsSource = _settings.Shares.Select(s => s.Path).ToArray();
    }

    private static string Join(string folder, string name) => folder.Length == 0 ? name : folder + "/" + name;

    private IProgress<long> TransferProgress(string name)
    {
        var elapsed = Stopwatch.StartNew();
        var next = TimeSpan.Zero;
        return new Progress<long>(bytes =>
        {
            if (elapsed.Elapsed < next) return;
            next = elapsed.Elapsed.Add(TimeSpan.FromMilliseconds(250));
            _fileStatus.Text = $"{name}   {bytes / 1048576d:F1} MB   {bytes / 1048576d / Math.Max(0.1, elapsed.Elapsed.TotalSeconds):F1} MB/s";
        });
    }

    private async Task DownloadSelected(TrustedPeer peer, CancellationToken ct)
    {
        var entries = _remote.SelectedItems.Cast<SharedEntry>().ToArray();
        var share = _remoteRoot;
        var path = _remotePath;
        if (entries.Length == 0) throw new InvalidOperationException("Select files or folders to download.");
        var picker = new OpenFolderDialog();
        if (picker.ShowDialog(this) != true) return;
        SharePaths.CheckLocal(picker.FolderName);
        var count = 0;
        async Task Receive(SharedEntry entry, Guid id, string relative, string directory, int depth)
        {
            ct.ThrowIfCancellationRequested();
            if (++count > 10000 || depth > 32) throw new InvalidDataException("This folder transfer exceeds the entry or depth limit.");
            var target = SharePaths.Child(directory, entry.Name);
            if (entry.Folder)
            {
                Directory.CreateDirectory(target);
                SharePaths.CheckLocal(target);
                foreach (var child in await _network.Browse(peer, id, relative, ct))
                    await Receive(child, id, Join(relative, child.Name), target, depth + 1);
            }
            else
                await _network.Download(peer, id, relative, target, TransferProgress(entry.Name), ct);
        }
        foreach (var item in entries)
            await Receive(item, share == default ? item.Share : share, share == default ? "" : Join(path, item.Name), picker.FolderName, 0);
        _fileStatus.Text = "Download complete.";
    }

    private async Task UploadSelected(TrustedPeer peer, bool folder, CancellationToken ct)
    {
        if (!_remoteUpload || _remoteRoot == default) throw new InvalidOperationException("Open a folder approved for uploads on the other PC.");
        string[] sources;
        if (folder)
        {
            var picker = new OpenFolderDialog();
            if (picker.ShowDialog(this) != true) return;
            sources = [picker.FolderName];
        }
        else
        {
            var picker = new OpenFileDialog { Multiselect = true };
            if (picker.ShowDialog(this) != true) return;
            sources = picker.FileNames;
        }
        await UploadPaths(peer, sources, ct);
    }

    private async Task UploadPaths(TrustedPeer peer, string[] sources, CancellationToken ct)
    {
        if (!_remoteUpload || _remoteRoot == default) throw new InvalidOperationException("Open an upload-enabled folder on the other PC first.");
        var share = _remoteRoot;
        var path = _remotePath;
        var count = 0;
        async Task Send(string source, string relative, int depth)
        {
            ct.ThrowIfCancellationRequested();
            if (++count > 10000 || depth > 32) throw new InvalidDataException("This folder transfer exceeds the entry or depth limit.");
            SharePaths.CheckLocal(source);
            if (Directory.Exists(source))
            {
                await _network.CreateUploadFolder(peer, share, relative, ct);
                foreach (var child in Directory.EnumerateFileSystemEntries(source))
                    await Send(child, Join(relative, Path.GetFileName(child)), depth + 1);
            }
            else
                await _network.Upload(peer, share, relative, source, TransferProgress(Path.GetFileName(source)), ct);
        }
        foreach (var source in sources) await Send(source, Join(path, Path.GetFileName(source.TrimEnd('\\'))), 0);
        _fileStatus.Text = "Upload complete.";
    }
}
