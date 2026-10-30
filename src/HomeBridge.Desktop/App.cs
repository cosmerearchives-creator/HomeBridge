using System;
using System.Windows;
using System.Threading;
using System.Security.Principal;

namespace HomeBridge.Desktop;

public sealed class App : Application
{
    [STAThread]
    public static void Main()
    {
        using var single = new Mutex(true, "Global\\HomeBridge." + WindowsIdentity.GetCurrent().User!.Value, out var owner);
        if (!owner) { MessageBox.Show("HomeBridge is already open in this session."); return; }
        var app = new App();
        try { app.Run(new MainWindow()); }
        catch (Exception ex) { MessageBox.Show("HomeBridge could not start. Your saved state has been preserved.\n" + ex.Message, "HomeBridge"); }
        finally { single.ReleaseMutex(); }
    }
}
