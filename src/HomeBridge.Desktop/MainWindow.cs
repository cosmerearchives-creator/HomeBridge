using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HomeBridge.Core;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading.Tasks;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.IO.Pipes;
using System.Security.Principal;
using System.Threading;
using System.Reflection;

namespace HomeBridge.Desktop;

public sealed class MainWindow : Window
{
    private readonly StateStore _store = new();
    private readonly UserSettings _settings;
    private readonly X509Certificate2 _identity;
    private readonly NetworkAgent _network;
    private readonly ScreenPage _screen;
    private readonly CancellationTokenSource _closing = new();
    private readonly ListBox _discovered = new() { MinHeight = 100, MaxHeight = 150 };
    private readonly ListBox _trusted = new() { MinHeight = 80, MaxHeight = 130 };
    private readonly TextBlock _networkStatus = new() { Text = "Local networking is off.", TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _invitation = new() { TextWrapping = TextWrapping.Wrap, MaxLength = 2048, MinHeight = 65 };
    private readonly List<string> _files = new();
    private readonly TextBox _find = new() { MinWidth = 120 };
    private readonly TextBox _replace = new() { MinWidth = 120 };
    private readonly TextBox _prefix = new() { MinWidth = 120 };
    private readonly TextBox _suffix = new() { MinWidth = 120 };
    private readonly ListView _preview = new() { MinHeight = 250 };
    private IReadOnlyList<RenamePlan> _lastBatch = Array.Empty<RenamePlan>();
    private readonly List<(string Name, string Path)> _apps = new();
    private readonly ListBox _appsList = new();
    private readonly TextBlock _updateStatus = new() { Text = "Updates have not been checked.", TextWrapping = TextWrapping.Wrap };
    private readonly UpdateClient _updater = new();
    private readonly SemaphoreSlim _updateGate = new(1);
    private readonly Button _installUpdate = new() { Content = "Download and install update", IsEnabled = false, Padding = new Thickness(14, 7, 14, 7) };
    private UpdateRelease? _availableUpdate;
    private static string CurrentVersion => typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];

    public MainWindow()
    {
        _settings = _store.Load();
        _identity = Identity.Load(_store);
        _network = new(_store, _settings, _identity);
        _network.ConfirmNearbyPairing = async (prompt, ct) => {
            var approved = await PairingDialog.Confirm(this, prompt, ct);
            // Persistence completes just after confirmation; the periodic refresh shows the result.
            return approved;
        };
        _screen = new(_network, _settings);
        foreach (var entry in _settings.Applications) { _apps.Add((entry.Name, entry.Path)); _appsList.Items.Add(entry.Path); }
        Title = "HomeBridge — .NET 10 Preview";
        Width = 1050; Height = 720; MinWidth = 740; MinHeight = 530;
        Background = new SolidColorBrush(Color.FromRgb(20, 23, 32));
        Foreground = Brushes.White;
        var tabs = new TabControl { Margin = new Thickness(16) };
        tabs.Items.Add(Tab("Overview", Overview()));
        tabs.Items.Add(Tab("Bulk Rename", RenamePage()));
        tabs.Items.Add(Tab("Application Launcher", AppPage()));
        tabs.Items.Add(Tab("Devices", DevicesPage()));
        tabs.Items.Add(Tab("Settings", SettingsPage()));
        tabs.Items.Add(Tab("Files", Pending("Authenticated file sharing and transfers")));
        tabs.Items.Add(Tab("Screen", _screen));
        tabs.Items.Add(Tab("Roku Media", Pending("DLNA background media service")));
        Content = tabs;
        Closing += async (_, e) => {
            if (_closing.IsCancellationRequested) return;
            e.Cancel = true; IsEnabled = false; _screen.Shutdown(); _closing.Cancel();
            await _network.DisposeAsync(); _updater.Dispose(); _identity.Dispose(); Close();
        };
        Loaded += async (_, _) => {
            if (_settings.AutomaticUpdateChecks) await CheckUpdates(automatic: true);
        };
    }

    private static TabItem Tab(string header, UIElement content) => new() { Header = header, Content = content };
    private static TextBlock Label(string value) => new() { Text = value, Margin = new Thickness(0, 7, 10, 5), FontSize = 14 };
    private static Button Action(string text, Action action)
    {
        var button = new Button { Content = text, Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 4, 10, 4) };
        button.Click += (_, _) => { try { action(); } catch (Exception ex) { MessageBox.Show(ex.Message, "HomeBridge", MessageBoxButton.OK, MessageBoxImage.Warning); } };
        return button;
    }
    private static StackPanel Row(params UIElement[] items)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var item in items) row.Children.Add(item);
        return row;
    }
    private static UIElement Pending(string feature) =>
        new StackPanel { Margin = new Thickness(24), Children = {
            new TextBlock { Text = feature, FontSize = 22, Margin = new Thickness(0, 0, 0, 16) },
            new TextBlock { Text = "Migration pending. This feature is available only in the legacy HomeBridge prototype for now.", TextWrapping = TextWrapping.Wrap }
        } };
    private UIElement Overview() => new StackPanel { Margin = new Thickness(24), Children = {
        new TextBlock { Text = "HomeBridge Native", FontSize = 32, FontWeight = FontWeights.Bold },
        new TextBlock { Text = "C# / .NET 10 / WPF • Early migration build", FontSize = 17, Margin = new Thickness(0, 12, 0, 20) },
        new TextBlock { Text = "Local bulk rename, saved application shortcuts, secure pairing, and approved view-only screen sharing. The optional Windows service reports local status only. File sharing, remote launch and Roku remain pending.", TextWrapping = TextWrapping.Wrap, FontSize = 15 }
    } };

    private UIElement RenamePage()
    {
        var page = new DockPanel { Margin = new Thickness(18) };
        var top = new StackPanel();
        DockPanel.SetDock(top, Dock.Top);
        top.Children.Add(Label("Rename local files — extensions are preserved"));
        top.Children.Add(Row(Action("Choose files…", SelectFiles), Action("Clear", () => { _files.Clear(); _preview.ItemsSource = null; })));
        top.Children.Add(Row(Label("Find"), _find, Label("Replace"), _replace));
        top.Children.Add(Row(Label("Prefix"), _prefix, Label("Suffix"), _suffix));
        top.Children.Add(Row(Action("Preview", RefreshPreview), Action("Apply rename", ApplyRename), Action("Undo last batch", UndoRename)));
        page.Children.Add(top);
        _preview.Margin = new Thickness(0, 18, 0, 0);
        var view = new GridView();
        view.Columns.Add(new GridViewColumn { Header = "Original", DisplayMemberBinding = new System.Windows.Data.Binding(nameof(RenamePlan.Original)), Width = 395 });
        view.Columns.Add(new GridViewColumn { Header = "New", DisplayMemberBinding = new System.Windows.Data.Binding(nameof(RenamePlan.Destination)), Width = 395 });
        _preview.View = view;
        page.Children.Add(_preview);
        return page;
    }
    private void SelectFiles()
    {
        var dialog = new OpenFileDialog { Multiselect = true, Title = "Choose files to rename" };
        if (dialog.ShowDialog() == true)
        {
            foreach (var file in dialog.FileNames)
                if (!_files.Contains(file, StringComparer.OrdinalIgnoreCase)) _files.Add(file);
            RefreshPreview();
        }
    }
    private IReadOnlyList<RenamePlan> CreatePreview() =>
        RenameEngine.Preview(_files, new RenameRule(_find.Text, _replace.Text, _prefix.Text, _suffix.Text));
    private void RefreshPreview() => _preview.ItemsSource = CreatePreview();
    private void ApplyRename()
    {
        if (_files.Count == 0) return;
        var plans = CreatePreview();
        if (MessageBox.Show($"Rename {plans.Count} selected files?", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _lastBatch = RenameEngine.Apply(plans);
        _files.Clear();
        _files.AddRange(plans.Select(p => p.Destination));
        RefreshPreview();
    }
    private void UndoRename()
    {
        if (_lastBatch.Count == 0) { MessageBox.Show("No rename batch to undo."); return; }
        RenameEngine.Undo(_lastBatch);
        _files.Clear();
        _files.AddRange(_lastBatch.Select(p => p.Original));
        _lastBatch = Array.Empty<RenamePlan>();
        RefreshPreview();
    }
    private UIElement AppPage()
    {
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(Label("Local application launcher — remote launching is not yet migrated"));
        panel.Children.Add(Row(Action("Add EXE…", () => {
            var dialog = new OpenFileDialog { Filter = "Windows applications (*.exe)|*.exe", Title = "Select an application" };
            if (dialog.ShowDialog() == true) {
                var entry = new LauncherEntry(Path.GetFileNameWithoutExtension(dialog.FileName), dialog.FileName);
                lock (_settings) {
                    _settings.Applications.Add(entry);
                    try { _store.Save(_settings); } catch { _settings.Applications.Remove(entry); throw; }
                }
                _apps.Add((entry.Name, entry.Path)); _appsList.Items.Add(entry.Path);
            }
        }), Action("Launch selected", () => {
            var index = _appsList.SelectedIndex;
            if (index < 0) return;
            Process.Start(new ProcessStartInfo(_apps[index].Path) { UseShellExecute = true });
        }), Action("Remove selected", () => {
            var index = _appsList.SelectedIndex; if (index < 0) return;
            lock (_settings) {
                var entry = _settings.Applications[index]; _settings.Applications.RemoveAt(index);
                try { _store.Save(_settings); } catch { _settings.Applications.Insert(index, entry); throw; }
            }
            _apps.RemoveAt(index); _appsList.Items.RemoveAt(index);
        })));
        _appsList.MinHeight = 240;
        panel.Children.Add(_appsList);
        return panel;
    }

    private Button AsyncAction(string text, Func<Task> action)
    {
        var button = new Button { Content = text, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 4, 10, 4) };
        button.Click += async (_, _) => {
            button.IsEnabled = false;
            try { await action(); }
            catch (OperationCanceledException) { if (!_closing.IsCancellationRequested) _networkStatus.Text = "Operation timed out."; }
            catch (Exception ex) { _networkStatus.Text = ex.Message; }
            finally { button.IsEnabled = true; }
        };
        return button;
    }
    private void RefreshPeers()
    {
        lock (_settings) _trusted.ItemsSource = _settings.Peers.ToList();
        _trusted.DisplayMemberPath = nameof(TrustedPeer.Name);
    }
    private UIElement SettingsPage()
    {
        var page = new StackPanel { Margin = new Thickness(24) };
        var name = new TextBox { Text = _settings.DeviceName, MaxLength = 80, MaxWidth = 400, HorizontalAlignment = HorizontalAlignment.Left };
        page.Children.Add(Label("Computer display name")); page.Children.Add(name);
        page.Children.Add(Action("Save settings", () => {
            var value = name.Text.Trim();
            if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException("Enter a computer display name.");
            lock (_settings) {
                var previous = _settings.DeviceName; _settings.DeviceName = value;
                try { _store.Save(_settings); } catch { _settings.DeviceName = previous; throw; }
            }
            MessageBox.Show("Saved. Disable and enable networking to update discovery advertisements.");
        }));
        page.Children.Add(Label("Settings are stored for your Windows account in LocalAppData\\HomeBridge."));
        page.Children.Add(Label("HomeBridge " + CurrentVersion));
        var automatic = new CheckBox { Content = "Automatically check for updates when HomeBridge opens", IsChecked = _settings.AutomaticUpdateChecks, Margin = new Thickness(0, 14, 0, 8) };
        var previews = new CheckBox { Content = "Include preview releases", IsChecked = _settings.IncludePreviewUpdates, Margin = new Thickness(0, 0, 0, 8) };
        page.Children.Add(automatic); page.Children.Add(previews);
        page.Children.Add(Action("Save update preferences", () => {
            lock (_settings) {
                var oldAutomatic = _settings.AutomaticUpdateChecks; var oldPreview = _settings.IncludePreviewUpdates;
                _settings.AutomaticUpdateChecks = automatic.IsChecked == true; _settings.IncludePreviewUpdates = previews.IsChecked == true;
                try { _store.Save(_settings); } catch { _settings.AutomaticUpdateChecks = oldAutomatic; _settings.IncludePreviewUpdates = oldPreview; throw; }
            }
            _availableUpdate = null; _installUpdate.IsEnabled = false; _updateStatus.Text = "Update preferences saved.";
        }));
        var check = new Button { Content = "Check for updates", Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 10, 10, 10) };
        check.Click += async (_, _) => { check.IsEnabled = false; try { await CheckUpdates(false); } finally { check.IsEnabled = true; } };
        _installUpdate.Click += async (_, _) => await InstallUpdate();
        page.Children.Add(Row(check, _installUpdate)); page.Children.Add(_updateStatus);
        page.Children.Add(new TextBlock { Text = "Updates come from the HomeBridge GitHub Releases page. Installation closes HomeBridge and clears in-session rename undo. Windows will ask for administrator approval. Your saved settings and paired devices are kept.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) });
        return page;
    }
    private async Task CheckUpdates(bool automatic)
    {
        if (!await _updateGate.WaitAsync(0)) return;
        _installUpdate.IsEnabled = false; _availableUpdate = null;
        try {
            _updateStatus.Text = "Checking for updates…";
            _availableUpdate = await _updater.Check(CurrentVersion, _settings.IncludePreviewUpdates, _closing.Token);
            _updateStatus.Text = _availableUpdate is null ? "No newer release is available." : $"HomeBridge {_availableUpdate.Tag} is available. Choose Download and install update when ready.";
            _installUpdate.IsEnabled = _availableUpdate is not null;
            if (automatic && _availableUpdate is not null && !_closing.IsCancellationRequested)
                MessageBox.Show(_updateStatus.Text + "\nOpen Settings to install it.", "HomeBridge update available");
        }
        catch (Exception ex) { if (!_closing.IsCancellationRequested) _updateStatus.Text = "Could not check for updates. " + ex.Message; }
        finally { _updateGate.Release(); }
    }
    private async Task InstallUpdate()
    {
        if (!await _updateGate.WaitAsync(0)) return;
        var release = _availableUpdate;
        _installUpdate.IsEnabled = false;
        try {
            if (release is null) return;
            if (MessageBox.Show($"Download and install HomeBridge {release.Tag}? HomeBridge will close, and in-session rename undo will be cleared. Windows will request administrator approval.", "Install update", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            _updateStatus.Text = "Downloading and verifying the update…";
            using var installer = await _updater.Download(release, _store.DirectoryPath, _closing.Token);
            try {
                _closing.Token.ThrowIfCancellationRequested();
                var process = Process.Start(new ProcessStartInfo(installer.Path) {
                    UseShellExecute = true, Verb = "runas", Arguments = "/SP- /NORESTART",
                    WorkingDirectory = Path.GetDirectoryName(installer.Path)!
                }) ?? throw new IOException("The installer could not be started.");
                process.Dispose(); Close();
            }
            catch { installer.Delete(); throw; }
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { _updateStatus.Text = "Installation canceled. Your current version is still installed."; }
        catch (Exception ex) { if (!_closing.IsCancellationRequested) _updateStatus.Text = "Update not installed. " + ex.Message; }
        finally { if (!_closing.IsCancellationRequested) _installUpdate.IsEnabled = _availableUpdate is not null; _updateGate.Release(); }
    }
    private UIElement DevicesPage()
    {
        var page = new StackPanel { Margin = new Thickness(24) };
        page.Children.Add(new TextBlock { Text = "Local devices", FontSize = 24 });
        page.Children.Add(new TextBlock { Text = "Connect two PCs on the same private network. Select the other PC and compare the code on both screens. Nearby devices turn off when HomeBridge closes.", TextWrapping = TextWrapping.Wrap });
        page.Children.Add(_networkStatus);
        page.Children.Add(Label("1. On both PCs, turn on nearby devices. On the receiving PC, click Allow pairing."));
        page.Children.Add(Row(Action("Allow pairing for 2 minutes", () => {
            _network.Start(); _network.OpenNearbyPairing();
            _networkStatus.Text = "Ready to pair for two minutes. On the other PC, search and select this PC. Compare the code on both screens.";
        }), Action("Stop accepting pairing", () => { _network.CloseNearbyPairing(); _networkStatus.Text = "Nearby pairing is closed."; })));
        page.Children.Add(Label("2. Search, select the other PC below, then click Pair selected PC."));
        page.Children.Add(Row(AsyncAction("Turn on nearby devices", () => { _network.Start(); _networkStatus.Text = "Nearby devices are on. Search for another PC, or allow pairing so it can connect to you."; return Task.CompletedTask; }),
            AsyncAction("Disable networking", async () => { _screen.StopAll(); await _network.DisposeAsync(); _invitation.Clear(); _networkStatus.Text = "Local networking is off; invitations canceled."; }),
            AsyncAction("Search nearby PCs", async () => { _network.Start(); var devices = (await DiscoveryResponder.Scan(_closing.Token)).Where(d => d.Fingerprint != Identity.Fingerprint(_identity)).ToList(); _discovered.ItemsSource = devices; _networkStatus.Text = devices.Count == 0 ? "No nearby PCs found. Open HomeBridge on the other PC, turn on nearby devices, and check both PCs use the same private network." : "Select the other PC and click Pair selected PC. Compare the code on both screens."; })));
        page.Children.Add(_discovered);
        page.Children.Add(AsyncAction("Pair selected PC", async () => {
            if (_discovered.SelectedItem is not DiscoveredDevice device) throw new InvalidOperationException("Search for nearby PCs, then select one from the list.");
            if (_network.IsTrusted(device.Fingerprint)) { _networkStatus.Text = "This PC is already paired. Open Screen to share your display."; return; }
            _networkStatus.Text = "Connecting… Compare the code on both PCs and confirm on each.";
            try { await _network.PairNearby(device, (prompt, ct) => PairingDialog.Confirm(this, prompt, ct), _closing.Token); }
            catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException) {
                throw new InvalidOperationException("Pairing did not finish. On the other PC, click Allow pairing again, keep both PCs open, then retry. No automatic approval was granted.", ex);
            }
            RefreshPeers(); _networkStatus.Text = "Connected to " + device.Name + ". You can now open Screen to share your display.";
        }));
        var addresses = new ComboBox { MinWidth = 160 };
        foreach (var ip in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)
            .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(ip) && LanAddress.IsLocal(ip))) addresses.Items.Add(ip.ToString());
        if (addresses.Items.Count > 0) addresses.SelectedIndex = 0;
        var advanced = new StackPanel();
        page.Children.Add(new Expander { Header = "Advanced: pair with an invitation instead", Content = advanced, Margin = new Thickness(0, 16, 0, 12) });
        advanced.Children.Add(Label("Choose this PC's LAN address, create an invitation, and send it privately to the other PC:"));
        advanced.Children.Add(Row(addresses, Action("Create invitation", () => {
            if (addresses.SelectedItem is not string address) throw new InvalidOperationException("No private LAN IPv4 address available.");
            _invitation.Text = JsonSerializer.Serialize(_network.Invite(address));
        }), Action("Cancel invitation", () => { _network.CancelInvitation(); _invitation.Clear(); })));
        advanced.Children.Add(Label("Invitation (copy yours, or paste the other PC's invitation here):"));
        advanced.Children.Add(_invitation);
        advanced.Children.Add(AsyncAction("Pair using invitation", async () => {
            var invitation = JsonSerializer.Deserialize<Invitation>(_invitation.Text) ?? throw new InvalidDataException("Invitation missing.");
            invitation.Validate();
            if (MessageBox.Show($"Trust the device at {invitation.Address}?\nCompare this SHA-256 fingerprint with that device over a trusted channel:\n{invitation.Fingerprint}", "Confirm device identity", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            await _network.Pair(invitation, _closing.Token); _invitation.Clear(); RefreshPeers(); _networkStatus.Text = "Paired. Open Screen to start sharing.";
        }));
        page.Children.Add(Label("This device's SHA-256 fingerprint: " + Identity.Fingerprint(_identity)));
        page.Children.Add(Label("Saved trusted devices:")); page.Children.Add(_trusted); RefreshPeers();
        var refresh = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        refresh.Tick += (_, _) => { var selected = (_trusted.SelectedItem as TrustedPeer)?.Fingerprint; RefreshPeers(); if (selected is not null) _trusted.SelectedItem = _settings.Peers.FirstOrDefault(p => p.Fingerprint == selected); };
        refresh.Start(); Closed += (_, _) => refresh.Stop();
        page.Children.Add(Row(Action("Refresh trusted devices", RefreshPeers), Action("Forget selected device", () => {
            if (_trusted.SelectedItem is not TrustedPeer peer) return;
            _screen.StopAll();
            lock (_settings) {
                var previous = _settings.Peers.ToList(); _settings.Peers.Remove(peer);
                try { _store.Save(_settings); } catch { _settings.Peers = previous; throw; }
            }
            RefreshPeers(); _networkStatus.Text = "Local trust revoked. Also forget this PC on the other device for mutual removal.";
        }), AsyncAction("Authenticate selected discovery", async () => {
            if (_discovered.SelectedItem is not DiscoveredDevice device) throw new InvalidOperationException("Select a discovered device first.");
            await _network.Ping(device.Address, device.Fingerprint, _closing.Token); _networkStatus.Text = "Mutual device authentication succeeded.";
        }), AsyncAction("Check Windows service", async () => {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token); timeout.CancelAfter(2000);
            using var pipe = new NamedPipeClientStream(".", "HomeBridge.Status.v1", PipeDirection.In, PipeOptions.Asynchronous, TokenImpersonationLevel.Anonymous);
            await pipe.ConnectAsync(timeout.Token);
            using var reader = new StreamReader(pipe);
            var buffer = new char[256]; var count = await reader.ReadAsync(buffer.AsMemory(), timeout.Token);
            _networkStatus.Text = "Informational local service status: " + new string(buffer, 0, count);
        })));
        return new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}
