using System.Windows;
using System.Windows.Controls;
using HomeBridge.Core;

namespace HomeBridge.Desktop;

public static class PairingDialog
{
    public static async Task<bool> Confirm(Window owner, PairingPrompt prompt, CancellationToken ct)
    {
        if (!owner.Dispatcher.CheckAccess()) return await owner.Dispatcher.InvokeAsync(() => Confirm(owner, prompt, ct)).Task.Unwrap();
        ct.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "Pair with " + prompt.Name + "?", FontSize = 22, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = "Check that BOTH PCs show exactly this code:", Margin = new Thickness(0, 18, 0, 12), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = prompt.Code, FontSize = 42, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center });
        panel.Children.Add(new TextBlock { Text = "Only confirm if you started pairing and can compare both screens. A nearby device name alone is not proof of identity.", Margin = new Thickness(0, 12, 0, 16), TextWrapping = TextWrapping.Wrap });
        var row = new WrapPanel();
        var yes = new Button { Content = "Codes match — pair", Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(0, 0, 10, 0) };
        var no = new Button { Content = "Cancel", Padding = new Thickness(14, 8, 14, 8) };
        row.Children.Add(yes); row.Children.Add(no); panel.Children.Add(row);
        var window = new Window { Owner = owner, Title = "Confirm pairing", Content = panel, Width = 450, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        yes.Click += (_, _) => { completion.TrySetResult(true); window.Close(); };
        no.Click += (_, _) => window.Close();
        window.Closed += (_, _) => completion.TrySetResult(false);
        window.PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { window.Close(); e.Handled = true; } };
        using var registration = ct.Register(() => owner.Dispatcher.BeginInvoke(new Action(() => { completion.TrySetResult(false); window.Close(); })));
        window.Show();
        return await completion.Task;
    }
}
