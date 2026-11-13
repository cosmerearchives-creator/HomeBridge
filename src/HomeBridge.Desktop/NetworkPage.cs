using System.IO;
using System.Windows;
using System.Windows.Controls;
using HomeBridge.Core;

namespace HomeBridge.Desktop;

public sealed partial class MainWindow
{
    private readonly ListBox _nearby = new() { Height = 110 };
    private readonly ListBox _paired = new() { Height = 110, DisplayMemberPath = nameof(TrustedPeer.Name) };
    private readonly TextBlock _networkStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly NetworkAgent _network;
    private readonly StateStore _store = new();
    private readonly UserSettings _settings;
    private readonly System.Security.Cryptography.X509Certificates.X509Certificate2 _identity;

    private Button NetworkAction(string title, Func<Task> run, TextBlock? status = null)
    {
        var button = new Button { Content = title, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 4, 8, 4) };
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try { await run(); }
            catch (OperationCanceledException) { (status ?? _networkStatus).Text = "Canceled or timed out."; }
            catch (Exception ex) { (status ?? _networkStatus).Text = ex.Message; }
            finally { button.IsEnabled = true; }
        };
        return button;
    }

    private void RefreshPeers()
    {
        var pin = (_paired.SelectedItem as TrustedPeer)?.Fingerprint;
        TrustedPeer[] peers;
        lock (_settings) peers = _settings.Peers.ToArray();
        _paired.ItemsSource = peers;
        _paired.SelectedItem = peers.FirstOrDefault(p => p.Fingerprint == pin);
        var filePin = (_filePeer.SelectedItem as TrustedPeer)?.Fingerprint;
        if (_filePeer.ItemsSource is not TrustedPeer[] previous || !previous.SequenceEqual(peers))
        {
            _filePeer.ItemsSource = peers;
            _filePeer.SelectedItem = peers.FirstOrDefault(p => p.Fingerprint == filePin);
        }
    }

    private async Task<IReadOnlyList<DiscoveredDevice>> Discover()
    {
        var ownPin = Identity.Fingerprint(_identity);
        var devices = (await DiscoveryResponder.Scan(_closing.Token)).Where(d => d.Fingerprint != ownPin).ToArray();
        _nearby.ItemsSource = devices;
        TrustedPeer[] peers;
        lock (_settings) peers = _settings.Peers.ToArray();
        foreach (var peer in peers)
        {
            var candidate = devices.FirstOrDefault(d => d.Fingerprint == peer.Fingerprint && d.Address != peer.Address);
            if (candidate is null) continue;
            try
            {
                await _network.Ping(candidate.Address, peer.Fingerprint, _closing.Token);
                lock (_settings)
                {
                    var i = _settings.Peers.IndexOf(peer);
                    if (i < 0) continue;
                    _settings.Peers[i] = peer with { Address = candidate.Address };
                    try { _store.Save(_settings); } catch { _settings.Peers[i] = peer; throw; }
                }
            }
            catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or
                System.Security.Authentication.AuthenticationException or OperationCanceledException) { }
        }
        RefreshPeers();
        return devices;
    }

    private UIElement DevicesPage()
    {
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(Label("Pair computers on your private network"));
        panel.Children.Add(new TextBlock { Text = "Open HomeBridge on both PCs. Click Allow pairing on the receiving PC, then select it and click Pair on this PC. Compare the code on both screens.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(Row(NetworkAction("Find nearby PCs", async () =>
        {
            var devices = await Discover();
            _networkStatus.Text = devices.Count == 0 ? "No pairing-capable PCs replied. Open HomeBridge on the other PC and run diagnostics." : "Choose a nearby PC to pair.";
        }), NetworkAction("Allow pairing for 2 minutes", () =>
        {
            _network.Start();
            _network.OpenNearbyPairing();
            _networkStatus.Text = "Ready to pair. Select this PC on the other computer.";
            return Task.CompletedTask;
        }), Action("Stop accepting pairing", _network.CloseNearbyPairing)));
        panel.Children.Add(_nearby);
        panel.Children.Add(NetworkAction("Pair selected PC", async () =>
        {
            if (_nearby.SelectedItem is not DiscoveredDevice device) throw new InvalidOperationException("Select a nearby PC first.");
            if (_network.IsTrusted(device.Fingerprint)) { _networkStatus.Text = "This PC is already paired."; return; }
            _network.Start();
            await _network.PairNearby(device, (prompt, ct) => PairingDialog.Confirm(this, prompt, ct), _closing.Token);
            RefreshPeers();
            _networkStatus.Text = "Paired. Open Files to browse shares.";
        }));
        panel.Children.Add(Label("Paired computers"));
        panel.Children.Add(_paired);
        var refresh = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        refresh.Tick += (_, _) => RefreshPeers();
        refresh.Start();
        Closed += (_, _) => refresh.Stop();
        panel.Children.Add(Action("Forget selected PC", () =>
        {
            if (_paired.SelectedItem is not TrustedPeer peer) return;
            lock (_settings)
            {
                if (!_settings.Peers.Remove(peer)) return;
                try { _store.Save(_settings); } catch { _settings.Peers.Add(peer); throw; }
            }
            RefreshPeers();
            _networkStatus.Text = "Trust revoked on this PC.";
        }));
        panel.Children.Add(Label("Network diagnostics"));
        panel.Children.Add(Row(NetworkAction("Diagnose", Diagnose), NetworkAction("Diagnose & repair", async () =>
        {
            await Diagnose();
            if (MessageBox.Show(this, "Repair the HomeBridge discovery service and its Private network firewall rules? Windows will ask for administrator approval.", "Repair HomeBridge", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            await NetworkDiagnostics.Repair(_closing.Token);
            await Diagnose();
        })));
        panel.Children.Add(_networkStatus);
        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private async Task Diagnose()
    {
        _networkStatus.Text = "Checking this PC and nearby devices…";
        var local = await NetworkDiagnostics.Inspect(_closing.Token);
        var legacy = DeviceDiscovery.ScanAsync(TimeSpan.FromSeconds(2));
        var devices = await Discover();
        var legacyResults = await legacy;
        var results = new List<string> { local, $"UDP discovery received {devices.Count} pairing-capable replies and {legacyResults.Count} discovery-service replies (may include this PC)." };
        TrustedPeer[] peers;
        lock (_settings) peers = _settings.Peers.ToArray();
        results.AddRange(await Task.WhenAll(peers.Take(16).Select(async peer =>
        {
            try { await _network.Ping(peer.Address ?? "", peer.Fingerprint, _closing.Token); return $"{peer.Name}: authenticated connection succeeded."; }
            catch (Exception ex) { return $"{peer.Name}: unreachable or trust was revoked. Check that HomeBridge is running on both PCs. {ex.Message}"; }
        })));
        _networkStatus.Text = string.Join(Environment.NewLine, results);
    }
}
