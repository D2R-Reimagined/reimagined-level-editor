using System.Windows;
using D2RLevel.Core;

namespace D2RLevel.App;

public partial class MainWindow
{
    private void GameplayAssets_Click(object sender, RoutedEventArgs e)
    {
        if (loading is not null) return;
        try
        {
            var browser = CreateGameplayBrowser();
            browser.Owner = ds1Window?.IsActive == true ? ds1Window : this;
            browser.ShowDialog();
        }
        catch (Exception ex) { Error(ex); }
    }

    private GameplayAssetBrowser CreateGameplayBrowser()
    {
        var sceneDocument = document ?? throw new InvalidOperationException(L.T("Open a paired scene to browse gameplay assets."));
        var map = pairedScene?.Collision?.Document ?? throw new InvalidOperationException(L.T("Open a paired scene to browse gameplay assets."));
        var links = placementLinks ?? throw new InvalidOperationException(L.T("Load the paired gameplay data first."));
        string? root = PresetPairing.Split(sceneDocument.SourcePath, "hd/env/preset")?.DataRoot
            ?? PresetPairing.Split(map.SourcePath, "global/tiles")?.DataRoot;
        var templates = (LevelProject.ForPreset(sceneDocument.SourcePath)?.Placements ?? [])
            .Concat(map.Units.Where(u => u.Type is 1 or 2).Select(u => new LevelPlacement("", u.Type, u.Id, u.Flags))).ToArray();
        return new(resolver, root, map.Act, map.Width, map.Height, templates, (entry, x, y, flags, visual) =>
        {
            if (document != sceneDocument || pairedScene?.Collision?.Document != map || placementLinks != links || entry.Act != map.Act)
                throw new InvalidOperationException(L.T("The scene changed. Reopen the gameplay browser."));
            Scene.CancelDrag();
            int index = links.AppendUnit(entry.Type, entry.Id!.Value, x, y, flags);
            if (entry.Type == 1)
            {
                npcAct = map.Act;
                npcPreview.Visuals[entry.Id.Value] = visual ?? new(entry.Key, SceneViewport.Placeholder(), true);
            }
            RefreshNpcs(); Ds1Preview.SetScene(pairedScene, pairedStatus); ds1Window?.RefreshFromWorkspace();
            ds1Window?.SelectGameplayUnit(index);
            if (npcItems.FirstOrDefault(i => i.Entity.GameplayUnitIndex == index) is { } item)
            { Hierarchy.SelectedItem = item.Entity; Scene.FrameSelected(); }
            PopulateInspector(); RefreshState();
        }, map.GameplayWarning ?? links.Warning);
    }
}
