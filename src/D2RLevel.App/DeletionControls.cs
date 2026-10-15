using System.IO;
using System.Windows;
using D2RLevel.Core;

namespace D2RLevel.App;

public partial class MainWindow
{
    private static bool CanDelete(PresetEntity e) => ModelDeletion.CanDelete(e);
    private void DeleteModel_Click(object sender, RoutedEventArgs e)
    {
        if (loading is not null || document is null || Selected is not { } entity) return;
        if (SelectedEntities.Length > 1) { DeleteModels(SelectedEntities); return; }
        if (entity.GameplayUnitIndex is not null) { Status.Text = L.T("NPC deletion is not supported in this preview yet."); return; }
        try
        {
            Scene.CancelDrag();
            if (placementLinks is not null) placementLinks.DeleteModel(entity);
            else if (File.Exists(document.SourcePath + PlacementLinks.Suffix))
                throw new InvalidOperationException("Load the linked DS1 before deleting this object. " + pairedStatus);
            else document.DeleteModel(entity);
            SyncHistory(entity);
            Notify(L.T("Object deleted"), entity.Name + "\n" + L.T("Linked unit and owned collision removed, if present. Shared collision preserved. Ctrl+Z to undo."));
        }
        catch (Exception ex) { Error(ex); }
    }

    /// <summary>Deletes every deletable model in a multi-selection as one undoable edit. NPCs, terrain and
    /// parented entities are skipped. If any deletion fails, none of them are applied.</summary>
    private void DeleteModels(PresetEntity[] selected)
    {
        if (document is null) return;
        var targets = selected.Where(CanDelete).Distinct().ToArray();
        int skipped = selected.Length - targets.Length;
        if (targets.Length == 0) { Status.Text = L.T("None of the selected objects can be deleted. NPCs, terrain and parented entities are not supported."); return; }
        try
        {
            Scene.CancelDrag();
            ModelDeletion.Delete(document, placementLinks, targets);
            Notify(L.T("{0} objects deleted", targets.Length),
                (skipped > 0 ? L.T("{0} NPCs, terrain or parented entities were skipped.", skipped) + "\n" : "") +
                L.T("Linked units and owned collision removed, if present. Shared collision preserved. Ctrl+Z to undo."));
        }
        catch (Exception ex) { Error(ex); }
    }

    /// <summary>Adds or removes the scene geometry of a bulk deletion after it is applied, undone or redone.</summary>
    private void SyncDeletion(ModelDeletion deletion)
    {
        if (document is null) return;
        foreach (var entity in deletion.Removed) SyncPresence(entity);
        Filter();
        SetSelection(deletion.Removed.Where(document.Entities.Contains));
        PopulateInspector(); RefreshState();
    }
}
