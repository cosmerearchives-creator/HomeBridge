using System;
using System.Windows;

namespace HomeBridge.Desktop;

public sealed class App : Application
{
    [STAThread]
    public static void Main()
    {
        var app = new App();
        app.Run(new MainWindow());
    }
}
