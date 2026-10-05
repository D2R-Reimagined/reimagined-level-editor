using System.IO;
using System.Windows;
using System.Windows.Media.Media3D;
using D2RLevel.Assets;
using D2RLevel.Core;

namespace D2RLevel.App;

public partial class MainWindow
{
    private async void ExtendGround_Click(object sender, RoutedEventArgs e)
    {
        if (loading is not null) return;
        if (document is null || resolver is null || pairedScene?.Collision is null)
        { Status.Text = L.T("Open a paired cave scene before extending its ground."); return; }
        if (document.IsDirty || pairedScene.Collision.Document.IsDirty || placementLinks?.HasMetadataChanges == true || connectionEdits?.IsDirty == true)
        { Status.Text = L.T("Save your scene, links and entrance edits before exporting a ground extension."); return; }
        try
        {
            Scene.CancelDrag();
            var context = new LevelTileset(pairedScene.Mask, pairedScene.Dt1Paths.Select(p =>
                "data/global/tiles/" + (PresetPairing.Split(p, "global/tiles")?.Relative ?? throw new InvalidDataException("Tileset path is outside data.")).Replace('\\', '/')).ToArray());
            var editor = new GroundExtensionEditor(document.SourcePath, pairedScene.Ds1Path, resolver, context, pairedScene.Collision,
                r => Scene.SetPathOverlay(r is null ? [] : new Point3D[] {
                    new(r.MinX * 10, r.Height + .3, r.MinY * 10), new(r.MaxX * 10, r.Height + .3, r.MinY * 10),
                    new(r.MaxX * 10, r.Height + .3, r.MaxY * 10), new(r.MinX * 10, r.Height + .3, r.MaxY * 10),
                    new(r.MinX * 10, r.Height + .3, r.MinY * 10) })) { Owner = this };
            editor.ShowDialog();
            Scene.SetPathOverlay([]);
            if (editor.ExportedPreset is { } output)
            {
                var root = PresetPairing.Split(output, "hd/env/preset")!.Value.DataRoot;
                await LoadWorkspace(root);
                var scene = workspaceScenes.Single(s => Path.GetFullPath(s.JsonPath).Equals(Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase));
                await OpenWorkspaceScene(scene);
                Status.Text = L.T("Ground extension opened. Inspect the HD boundary and gameplay floor, then save and reopen this scene. In-game traversal is not yet verified.");
            }
        }
        catch (Exception ex) { Error(ex); }
        finally { Scene.SetPathOverlay([]); }
    }
}
