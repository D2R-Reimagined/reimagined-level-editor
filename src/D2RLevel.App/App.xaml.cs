using System.Windows;
using D2RLevel.Core;

namespace D2RLevel.App;

public partial class App : Application
{
    /// <summary>Set when the chosen language file could not be read; the main window surfaces it once.</summary>
    public static string? LanguageWarning { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Text is resolved while the window is constructed, so the language must be active before XAML loads.
        var settings = EditorSettings.Load(D2RLevel.App.MainWindow.SettingsPathFor(e.Args), out _);
        L.Initialize(D2RLevel.App.MainWindow.Argument(e.Args, "--language") ?? settings.Language, out var warning);
        LanguageWarning = warning;
        var window = new MainWindow(e.Args);
        MainWindow = window;
        window.Show();
    }
}