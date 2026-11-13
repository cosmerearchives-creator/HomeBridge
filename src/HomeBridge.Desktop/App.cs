using System.Windows;

namespace HomeBridge.Desktop;

public sealed class App : Application
{
    [STAThread]
    public static void Main(string[] args)
    {
        using var instance = new Mutex(true, @"Local\" + ExplorerCommands.Channel, out var first);
        var command = ExplorerCommands.Parse(args);
        if (!first)
        {
            try { ExplorerCommands.Forward(command ?? new("activate", null)); }
            catch (Exception ex) { MessageBox.Show("HomeBridge could not open the existing window. " + ex.Message); }
            return;
        }
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("Arcanum.xaml", UriKind.Relative) });
            app.Run(new MainWindow { StartupCommand = command });
        }
        catch (Exception ex) { MessageBox.Show("HomeBridge could not open. " + ex.Message); }
    }
}
