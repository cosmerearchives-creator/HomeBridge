using System.Windows;
using System.Windows.Controls;
using Forms = System.Windows.Forms;

namespace HomeBridge.Desktop;

public sealed partial class MainWindow
{
    private Forms.NotifyIcon? _tray;
    private bool _exit;
    private bool _shuttingDown;
    private readonly HashSet<string> _pendingExplorerSend = new(StringComparer.OrdinalIgnoreCase);

    private void ConfigureLifetime(TabControl tabs)
    {
        _tray = new Forms.NotifyIcon { Text = "HomeBridge", Icon = System.Drawing.SystemIcons.Application, Visible = _settings.KeepSharingInTray };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open HomeBridge", null, (_, _) => Dispatcher.BeginInvoke(ShowHomeBridge));
        menu.Items.Add("Exit HomeBridge", null, (_, _) => Dispatcher.BeginInvoke(ExitHomeBridge));
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(ShowHomeBridge);
        Closing += (_, e) =>
        {
            if (_exit) return;
            if (_settings.KeepSharingInTray)
            {
                e.Cancel = true;
                Hide();
            }
        };
        Closed += async (_, _) =>
        {
            if (_shuttingDown) return;
            _shuttingDown = true;
            _tray.Dispose();
            _closing.Cancel();
            await _network.DisposeAsync();
            _updater.Dispose();
            _identity.Dispose();
            Application.Current.Shutdown();
        };
        Loaded += async (_, _) => { _ = ExplorerCommands.Listen(command => HandleExplorer(command, tabs), _closing.Token); if (StartupCommand is { } command) await HandleExplorer(command, tabs); };
    }

    internal ExplorerCommand? StartupCommand { private get; set; }

    private Task HandleExplorer(ExplorerCommand command, TabControl tabs)
    {
        ShowHomeBridge();
        if (command.Verb == "activate") return Task.CompletedTask;
        tabs.SelectedItem = tabs.Items.OfType<TabItem>().First(t => Equals(t.Header, "Files"));
        try
        {
            switch (command)
            {
                case { Verb: "share", Path: { } path }:
                    AddShares([path], false);
                    break;
                case { Verb: "stop", Path: { } path }:
                    RemoveShares([System.IO.Path.GetFullPath(path)]);
                    break;
                case { Verb: "send", Path: { } path }:
                    _pendingExplorerSend.Add(path);
                    _fileStatus.Text = "Choose a paired PC, open its upload folder, then click Send Explorer selection.";
                    break;
            }
        }
        catch (Exception ex) { _fileStatus.Text = ex.Message; }
        return Task.CompletedTask;
    }

    private void ShowHomeBridge()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void ToggleTraySharing()
    {
        lock (_settings)
        {
            var old = _settings.KeepSharingInTray;
            _settings.KeepSharingInTray = !old;
            try { _store.Save(_settings); }
            catch { _settings.KeepSharingInTray = old; throw; }
        }
        _tray!.Visible = _settings.KeepSharingInTray;
        MessageBox.Show(this, _settings.KeepSharingInTray ? "Closing the window keeps HomeBridge in the notification area. Use Exit HomeBridge there to stop sharing." : "Closing the window now exits HomeBridge.", "HomeBridge");
    }

    private void ExitHomeBridge()
    {
        _exit = true;
        Close();
    }
}
