using System.Windows;
using System.IO;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HomeBridge.Core;
using Microsoft.Win32;

namespace HomeBridge.Desktop;

public sealed class ScreenPage : DockPanel
{
    private readonly NetworkAgent _network;
    private readonly UserSettings _settings;
    private readonly ScreenConsent _consent = new();
    private readonly ComboBox _peers = new() { MinWidth = 200, DisplayMemberPath = nameof(TrustedPeer.Name) };
    private readonly TextBox _address = new() { MinWidth = 160 };
    private readonly TextBlock _status = new() { Text = "Screen sharing is off.", TextWrapping = TextWrapping.Wrap };
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private Window? _banner;
    private TextBlock _bannerText = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White, Margin = new Thickness(10) };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private CancellationTokenSource? _view;
    private bool _disposed;
    public ScreenPage(NetworkAgent network, UserSettings settings)
    {
        _network = network; _settings = settings; Margin = new Thickness(18);
        _peers.SelectionChanged += (_, _) => { if (_peers.SelectedItem is TrustedPeer { Address: not null } peer) _address.Text = peer.Address; };
        var server = new ScreenStreamServer(_consent, async ct => {
            ct.ThrowIfCancellationRequested();
            // Capture on the interactive dispatcher; GetDC and ReleaseDC run on the same thread.
            try { return await Dispatcher.InvokeAsync(() => { ct.ThrowIfCancellationRequested(); return ScreenCapture.PrimaryScreen(); }, DispatcherPriority.Background, ct); }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.ExternalException)
            { throw new IOException("Screen capture is unavailable on this Windows desktop.", ex); }
        }, network.IsTrusted);
        network.ScreenRequestHandler = server.Serve;
        var controls = new StackPanel(); SetDock(controls, Dock.Top); Children.Add(controls);
        controls.Children.Add(new TextBlock { Text = "Screen sharing — view only", FontSize = 24 });
        controls.Children.Add(new TextBlock { Text = "Pair both PCs in Devices first. On the sharing PC, select the viewing PC and approve sharing. On the viewing PC, select the sharing PC, enter its LAN address, and choose View screen. Everything on the primary display can be seen, including notifications.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 12) });
        var selection = new WrapPanel(); selection.Children.Add(_peers); selection.Children.Add(Button("Refresh paired devices", RefreshPeers)); controls.Children.Add(selection);
        var share = new WrapPanel(); share.Children.Add(Button("Approve sharing to selected PC", Approve)); share.Children.Add(Button("Stop sharing", StopSharing)); controls.Children.Add(share);
        controls.Children.Add(new TextBlock { Text = "Sharing PC's LAN IPv4 address:", Margin = new Thickness(0, 8, 0, 4) });
        var view = new WrapPanel(); view.Children.Add(_address);
        var start = Button("View screen", () => { });
        start.Click += async (_, _) => {
            if (_view is not null) return;
            if (_peers.SelectedItem is not TrustedPeer peer) { _status.Text = "Select a paired PC."; return; }
            var address = _address.Text.Trim(); _image.Source = null;
            using var cancel = new CancellationTokenSource(); _view = cancel; start.IsEnabled = false;
            try {
                _status.Text = "Connecting to the approved sharing PC…";
                await network.ViewScreen(address, peer.Fingerprint, async (jpeg, ct) => {
                    await Dispatcher.InvokeAsync(() => {
                        ct.ThrowIfCancellationRequested();
                        using var bytes = new MemoryStream(jpeg);
                        var frame = new BitmapImage(); frame.BeginInit(); frame.CacheOption = BitmapCacheOption.OnLoad;
                        frame.StreamSource = bytes; frame.EndInit(); frame.Freeze();
                        _image.Source = frame; _status.Text = "Viewing " + peer.Name + " — view only.";
                    }, DispatcherPriority.Background, ct);
                }, cancel.Token);
            }
            catch (OperationCanceledException) { _status.Text = "Screen viewing stopped or timed out."; }
            catch (Exception ex) { _status.Text = "Screen viewing ended. " + ex.Message; }
            finally { _image.Source = null; _view = null; start.IsEnabled = true; }
        };
        view.Children.Add(start); view.Children.Add(Button("Stop viewing", () => { _view?.Cancel(); _image.Source = null; })); controls.Children.Add(view);
        controls.Children.Add(_status); Children.Add(new Border { Background = Brushes.Black, Child = _image, Margin = new Thickness(0, 12, 0, 0) });
        RefreshPeers();
        _timer.Tick += (_, _) => {
            if (_consent.State == "Off") { if (_banner is not null) _status.Text = "Screen sharing stopped."; HideBanner(); return; }
            if (!_network.Running) { StopSharing(); return; }
            _bannerText.Text = _consent.State == "Sharing" ? "YOUR PRIMARY SCREEN IS BEING SHARED" : "SCREEN SHARING APPROVED — waiting up to 5 minutes for the selected PC";
        };
        _timer.Start(); SystemEvents.SessionSwitch += SessionChanged;
    }
    private static Button Button(string text, Action action)
    {
        var button = new Button { Content = text, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 4, 10, 4) };
        button.Click += (_, _) => { try { action(); } catch (Exception ex) { MessageBox.Show(ex.Message, "HomeBridge screen sharing"); } };
        return button;
    }
    private void RefreshPeers()
    {
        lock (_settings) _peers.ItemsSource = _settings.Peers.ToList();
        if (_peers.Items.Count > 0) _peers.SelectedIndex = 0;
    }
    private void Approve()
    {
        if (!_network.Running) throw new InvalidOperationException("Enable networking in Devices first.");
        if (_peers.SelectedItem is not TrustedPeer peer || !_network.IsTrusted(peer.Fingerprint)) throw new InvalidOperationException("Select a currently paired PC.");
        if (MessageBox.Show($"Allow {peer.Name} to view your entire primary screen? Close private windows and notifications first. This permits one connection for up to 30 minutes.\n\nDevice fingerprint:\n{peer.Fingerprint}", "Approve screen sharing", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        StopSharing();
        _bannerText = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White, Margin = new Thickness(10) };
        var panel = new StackPanel(); panel.Children.Add(_bannerText); panel.Children.Add(Button("STOP SHARING", StopSharing));
        _banner = new Window { Title = "HomeBridge — Screen sharing", Width = 430, Height = 160, Topmost = true, ResizeMode = ResizeMode.NoResize,
            Background = Brushes.DarkRed, Content = panel, ShowInTaskbar = true, WindowStyle = WindowStyle.ToolWindow, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        _banner.Closed += (_, _) => { _consent.Stop(); _banner = null; };
        _bannerText.Text = "SCREEN SHARING APPROVED FOR " + peer.Name;
        _banner.Show(); // Show the persistent stop control before granting access to any pixels.
        _consent.Approve(peer.Fingerprint); _status.Text = "Waiting for " + peer.Name + " to connect. Approval expires in five minutes.";
    }
    private void HideBanner()
    {
        if (_banner is null) return;
        var banner = _banner; _banner = null; banner.Close();
    }
    public void StopSharing() { _consent.Stop(); HideBanner(); }
    public void StopAll() { StopSharing(); _view?.Cancel(); _image.Source = null; }
    private void SessionChanged(object? sender, SessionSwitchEventArgs args)
    {
        if (args.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff or SessionSwitchReason.RemoteDisconnect or SessionSwitchReason.ConsoleDisconnect)
        { _consent.Stop(); Dispatcher.BeginInvoke(StopAll); }
    }
    public void Shutdown()
    {
        if (_disposed) return; _disposed = true;
        _timer.Stop(); SystemEvents.SessionSwitch -= SessionChanged; StopAll(); _network.ScreenRequestHandler = null;
    }
}
