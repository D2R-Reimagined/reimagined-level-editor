using System.Windows;
using System.Windows.Media;
using D2RLevel.Core;

namespace D2RLevel.App;

public partial class MainWindow
{
    private int levelPropertiesRevision;
    private void ObjectProperties_Click(object sender, RoutedEventArgs e) => ShowLevelProperties(false);
    private void LevelProperties_Click(object sender, RoutedEventArgs e) => ShowLevelProperties(true);
    private void ShowLevelProperties(bool show)
    {
        EntityInspector.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        LevelDetails.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        var active = new SolidColorBrush(Color.FromRgb(40, 104, 108));
        var inactive = new SolidColorBrush(Color.FromRgb(53, 68, 86));
        LevelPropertiesButton.Background = show ? active : inactive;
        ObjectPropertiesButton.Background = show ? inactive : active;
    }

    private async Task RefreshLevelProperties(bool allowDuringLoad = false)
    {
        if (document is null || loading is not null && !allowDuringLoad) return;
        int revision = ++levelPropertiesRevision;
        var current = document; var map = pairedScene?.Ds1Path; var assets = resolver;
        LevelDetails.IsEnabled = false;
        try
        {
            var value = await Task.Run(() => LevelProperties.Load(current.SourcePath, map, assets));
            if (revision == levelPropertiesRevision && document == current && pairedScene?.Ds1Path == map && resolver == assets) LevelDetails.SetSnapshot(value);
        }
        catch (Exception ex) { if (document == current) Error(ex); }
        finally { if (revision == levelPropertiesRevision) LevelDetails.IsEnabled = loading is null; }
    }
}
