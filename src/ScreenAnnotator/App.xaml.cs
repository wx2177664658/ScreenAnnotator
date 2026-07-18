using System.Windows;

namespace ScreenAnnotator;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Any(a => a.Equals("--self-test", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Tools.CapabilitySelfTest.Run(this);
            return;
        }

        var main = new MainWindow();
        MainWindow = main;
        main.Show();
    }
}
