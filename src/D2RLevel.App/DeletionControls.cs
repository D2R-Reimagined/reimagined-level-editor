using System.IO;
using System.Windows;
using D2RLevel.Core;

namespace D2RLevel.App;

public partial class MainWindow
{
    private void DeleteModel_Click(object sender, RoutedEventArgs e)
    {
        if (loading is not null || document is null || Selected is not { } entity) return;
        try
        {
            Scene.CancelDrag();
            if (placementLinks is not null) placementLinks.DeleteModel(entity);
            else if (File.Exists(document.SourcePath + PlacementLinks.Suffix))
                throw new InvalidOperationException("Load the linked DS1 before deleting this object. " + pairedStatus);
            else document.DeleteModel(entity);
            SyncHistory(entity);
            Notify("Object deleted", entity.Name + "\nLinked unit and owned collision removed, if present. Shared collision preserved. Ctrl+Z to undo.");
        }
        catch (Exception ex) { Error(ex); }
    }
}
