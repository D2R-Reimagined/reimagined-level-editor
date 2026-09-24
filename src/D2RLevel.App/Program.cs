using Reimagined.Integration;

namespace D2RLevel.App;

public static class Program
{
    public static ActivationHost? Integration { get; private set; }
    [STAThread]
    public static void Main(string[] args)
    {
        // Handles Velopack install/update/uninstall hooks and exits early when the updater invokes them.
        Velopack.VelopackApp.Build().Run();
        if (!args.Contains("--smoke-output"))
        {
            var preset = IntegrationFiles.Option(args, "--preset");
            var startup = preset == null ? null : new EditorRequest(Target: new(Preset: System.IO.Path.GetFullPath(preset), Assets: IntegrationFiles.Option(args, "--data")));
            if (!ActivationHost.Initialize(CompanionApps.Level, args, out var integration, startup)) return;
            Integration = integration;
        }
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
