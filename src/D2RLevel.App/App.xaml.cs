using System.Windows;

namespace D2RLevel.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var window = new MainWindow(e.Args);
        MainWindow = window;
        window.Show();
    }
}
