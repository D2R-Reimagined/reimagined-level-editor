namespace D2RLevel.App;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        // Handles Velopack install/update/uninstall hooks and exits early when the updater invokes them.
        Velopack.VelopackApp.Build().Run();
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
