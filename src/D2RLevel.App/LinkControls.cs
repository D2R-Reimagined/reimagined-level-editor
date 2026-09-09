using System.IO;
using System.Windows;
using D2RLevel.Core;
using Microsoft.Win32;

namespace D2RLevel.App;

public partial class MainWindow
{
    private PlacementLinks? placementLinks;
    private PresetDocument? linkedJson;
    private Ds1CollisionDocument? linkedDs1;
    private void InitializeLinks()
    {
        var ds1 = pairedScene?.Collision?.Document;
        if (linkedJson == document && linkedDs1 == ds1) return;
        if (linkedJson is not null) linkedJson.History.Changed -= LinkedHistoryChanged;
        linkedJson = document; linkedDs1 = ds1;
        placementLinks = document is not null && ds1 is not null ? new(document, ds1) : null;
        if (workspaceSession is not null && placementLinks?.Warning is null) placementLinks?.ConnectWorkspace();
        if (document is not null) document.History.Changed += LinkedHistoryChanged;
        RefreshLinkStatus();
    }
    private void LinkedHistoryChanged(object? subject)
    {
        if (subject is PresetEntity[] members) RefreshGroupMove(members);
        if (document?.History.Shared != true) return;
        try
        {
            if (subject is PresetEntity entity) SyncHistory(entity);
            RefreshNpcs();
            ds1Window?.RefreshFromWorkspace();
            Ds1Preview.SetScene(pairedScene, pairedStatus);
            PopulateInspector(); RefreshState();
        }
        catch (Exception ex) { Status.Text = "Edit applied; preview refresh failed: " + ex.Message; }
    }
    private void RefreshLinkStatus()
    {
        if (LinkStatus is null) return;
        ResetLinksButton.Visibility = placementLinks?.Warning is not null ? Visibility.Visible : Visibility.Collapsed;
        LinkStatus.ToolTip = placementLinks?.SidecarPath;
        var link = placementLinks?.Warning is null && Selected is { } entity ? placementLinks?.Find(entity) : null;
        ToggleLinkButton.Content = link is null ? "Link in DS1…" : "Unlink";
        ToggleLinkButton.IsEnabled = loading is null && Selected is { CanTransform: true, HasParent: false, GameplayUnitIndex: null } && placementLinks?.Warning is null;
        EditCollisionButton.IsEnabled = ToggleLinkButton.IsEnabled && placementLinks is not null;
        EditCollisionButton.Content = link?.Tiles.Length > 0 ? "Edit collision…" : "Add collision…";
        AlignUnitButton.Visibility = link?.Unit is not null ? Visibility.Visible : Visibility.Collapsed;
        AlignUnitButton.IsEnabled = ToggleLinkButton.IsEnabled;
        if (Selected?.GameplayUnitIndex is not null) { LinkStatus.Text = "DS1 NPC preview. Drag to move its gameplay position and patrol. Save Scene or Save linked pair to save. Static reference pose; no animation."; return; }
        if (placementLinks?.Warning is { } warning) { LinkStatus.Text = warning; return; }
        LinkStatus.Text = placementLinks is null ? "Linking unavailable: " + pairedStatus :
            link is null ? "No DS1 link. Select a unit or suggest a footprint in DS1, then link it to this HD model." :
            (link.Unit is { } unit ? $"Linked to DS1 unit #{unit.Index} (ID {unit.Id}). Unit movement snaps to subtiles.\n" : "") +
            (link.Tiles.Length > 0 ? $"Collision: {link.Tiles.Length} owned tiles; shared tiles stay blocked for other owners. Collision snaps to whole tiles." : "No linked floor collision yet. Use Add collision to attach a footprint.") +
            $"\n{link.UnitsPerTile:G} HD units/tile · translation only. Save linked pair to preserve both maps and links.";
        if (placementLinks?.HasMetadataChanges == true) LinkStatus.Text += "\nLink changes have not been saved.";
        if (link?.Unit is { } linkedUnit && linkedDs1 is not null && Selected is { } model)
        {
            var actual = linkedDs1.Units[linkedUnit.Index];
            double expectedX = model.Transform.Position.X * 5 / link.UnitsPerTile, expectedY = model.Transform.Position.Z * 5 / link.UnitsPerTile;
            LinkStatus.Text += $"\nHD origin: subtile ({expectedX:F1}, {expectedY:F1}); linked unit: ({actual.X}, {actual.Y}).";
            if (Math.Abs(actual.X - expectedX) > .5 || Math.Abs(actual.Y - expectedY) > .5)
                LinkStatus.Text += " Offset preserved. Align linked unit to HD to place this chosen record at the model's origin.";
        }
    }
    private bool EditLink(Action<PlacementLinks> edit)
    {
        try
        {
            if (Selected?.GameplayUnitIndex is not null) throw new InvalidOperationException("NPC previews already represent their DS1 record and do not need an HD link.");
            if (placementLinks is null) throw new InvalidOperationException("Load the JSON and matching DS1 first.");
            edit(placementLinks);
            RefreshLinkStatus();
            Status.Text = "Link updated. Save linked pair to keep the JSON, DS1 and links together for next time.";
            return true;
        }
        catch (Exception ex) { Error(ex); return false; }
    }
    private void EditCollision_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is null || pairedScene is null) return;
        Floors_Click(sender, e);
        ds1Window?.ConfigureLinkedCollision(placementLinks, Selected);
        ds1Window?.ReviewLinkedCollision();
    }
    private void AlignUnit_Click(object sender, RoutedEventArgs e)
    { if (Selected is { } entity) EditLink(links => links.AlignUnitToHd(entity)); }
    private void ToggleLink_Click(object sender, RoutedEventArgs e)
    {
        if (loading is not null || Selected is not { } entity) return;
        if (placementLinks?.Find(entity) is not null) EditLink(links => links.Unlink(entity));
        else Floors_Click(sender, e);
    }
    private void ResetLinks_Click(object sender, RoutedEventArgs e)
    {
        if (placementLinks is null || MessageBox.Show(this, "Reset this pair's invalid links and undo history? Map data stays as it is. The old link file is backed up as .bak.",
            "Reset invalid links", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { placementLinks.Reset(); workspaceSession?.CaptureResetLinkFile(); RefreshLinkStatus(); RefreshState(); }
        catch (Exception ex) { Error(ex); }
    }
    private void SavePair_Click(object sender, RoutedEventArgs e)
    {
        if (workspaceSession is not null) { SaveScene_Click(sender, e); return; }
        if (placementLinks is null) { Status.Text = "Load the matching DS1 first."; return; }
        var dialog = new OpenFolderDialog { Title = "Choose a folder for a new linked export (JSON + DS1 + local links)" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            string path = placementLinks.ExportPair(dialog.FolderName);
            Status.Text = "Saved and verified linked pair. Open this JSON next time: " + path;
            RefreshLinkStatus(); RefreshState(); RefreshNpcs();
            ds1Window?.RefreshFromWorkspace();
            Notify("Linked pair saved", "JSON, DS1 and links exported. Open this JSON next time:\n" + path);
        }
        catch (Exception ex) { Error(ex); }
    }
}
