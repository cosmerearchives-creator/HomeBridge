using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Threading.Tasks;

namespace HomeBridge.Desktop;

public sealed class MainWindow : Window
{
    private readonly List<string> _files = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly TextBox _find = new() { MinWidth = 120 };
    private readonly TextBox _replace = new() { MinWidth = 120 };
    private readonly TextBox _prefix = new() { MinWidth = 120 };
    private readonly TextBox _suffix = new() { MinWidth = 120 };
    private readonly ListView _preview = new() { MinHeight = 250 };
    private IReadOnlyList<RenamePlan> _lastBatch = Array.Empty<RenamePlan>();
    private readonly List<(string Name, string Path)> _apps = new();
    private readonly ListBox _appsList = new();

    public MainWindow()
    {
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
        tabs.Items.Add(Tab("Screen", Pending("Low-latency screen streaming and optional remote input")));
        tabs.Items.Add(Tab("Roku Media", Pending("DLNA background media service")));
        Content = tabs;
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
        new TextBlock { Text = "Working today: local file bulk rename and local application launching. The other HomeBridge modules will be migrated progressively. This build does not install or enable network services.", TextWrapping = TextWrapping.Wrap, FontSize = 15 }
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
    private UIElement SettingsPage()
    {
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(Label("Computer display name"));
        var name = new TextBox { Text = _settings.DisplayName, MaxWidth = 400, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(name);
        panel.Children.Add(Label("Settings are stored for your Windows account in LocalAppData\\HomeBridge."));
        panel.Children.Add(Action("Save settings", () => {
            _settings.DisplayName = name.Text.Trim();
            _settings.Save();
            MessageBox.Show("Saved. Restart the discovery service to update its advertised name.");
        }));
        panel.Children.Add(Label("This build only discovers PCs. Pairing and remote access remain disabled."));
        return panel;
    }
    private UIElement DevicesPage()
    {
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(Label("Nearby HomeBridge computers — discovery only, not paired"));
        var results = new ListBox { MinHeight = 250 };
        panel.Children.Add(Action("Scan network", () => {
            results.Items.Clear();
            results.Items.Add("Scanning your local network…");
            _ = Task.Run(async () => {
                try {
                    var computers = await DeviceDiscovery.ScanAsync(TimeSpan.FromSeconds(3));
                    await Dispatcher.InvokeAsync(() => {
                        results.Items.Clear();
                        foreach (var computer in computers) results.Items.Add(computer);
                        if (computers.Count == 0) results.Items.Add("No devices found. Check the service and Private network firewall.");
                    });
                } catch (Exception ex) {
                    await Dispatcher.InvokeAsync(() => { results.Items.Clear(); results.Items.Add(ex.Message); });
                }
            });
        }));
        panel.Children.Add(results);
        panel.Children.Add(Label("No remote commands or files are exposed by discovery. Secure pairing is the next milestone."));
        return panel;
    }
    private UIElement AppPage()
    {
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(Label("Local application launcher — remote launching is not yet migrated"));
        panel.Children.Add(Row(Action("Add EXE…", () => {
            var dialog = new OpenFileDialog { Filter = "Windows applications (*.exe)|*.exe", Title = "Select an application" };
            if (dialog.ShowDialog() == true) { _apps.Add((Path.GetFileNameWithoutExtension(dialog.FileName), dialog.FileName)); _appsList.Items.Add(dialog.FileName); }
        }), Action("Launch selected", () => {
            var index = _appsList.SelectedIndex;
            if (index < 0) return;
            Process.Start(new ProcessStartInfo(_apps[index].Path) { UseShellExecute = true });
        })));
        _appsList.MinHeight = 240;
        panel.Children.Add(_appsList);
        return panel;
    }
}
