using System.IO;
using System.Windows;
using D2RLevel.Core;

namespace D2RLevel.App;

public partial class MainWindow
{
    private LevelTableEdits? connectionEdits;
    private void Entrances_Click(object sender, RoutedEventArgs e)
    {
        if (loading is not null) return;
        try { var editor = CreateEntranceEditor(); editor.Owner = ds1Window?.IsActive == true ? ds1Window : this; editor.ShowDialog(); }
        catch (Exception ex) { Error(ex); }
    }
    private EntranceEditor CreateEntranceEditor()
    {
        var current = document ?? throw new InvalidOperationException(L.T("Open a paired scene to edit entrances and exits."));
        var map = pairedScene?.Collision?.Document ?? throw new InvalidOperationException(L.T("Open a paired scene to edit entrances and exits."));
        var links = placementLinks ?? throw new InvalidOperationException(L.T("Load the paired gameplay data first."));
        string? root = PresetPairing.Split(current.SourcePath, "hd/env/preset")?.DataRoot;
        bool canSave = StudioTableContext.Project is null && workspaceSession is not null && root is not null && resolver is not null && !Path.GetFullPath(root).Equals(Path.GetFullPath(resolver.DataRoot), StringComparison.OrdinalIgnoreCase);
        string? warning = links.Warning ?? (StudioTableContext.Project != null ? "Connection table edits belong to Mod Studio. Use Level properties to open a Vis/Warp cell in Studio." : null);
        if (canSave && connectionEdits is null)
        {
            var table = new GameDataTables(resolver, root).Read("levels");
            if (table.SourcePath is not null && table.Warning is null) connectionEdits = new(table.SourcePath, root!, current.History);
        }
        Scene.CancelDrag();
        // Activate the existing shared map history before any marker edits.
        if (links.Warning is null) links.ConnectWorkspace();
        var catalog = new EntranceConnections(resolver, root, canSave ? connectionEdits : null);
        return new(map, LevelProperties.Load(current.SourcePath, map.SourcePath, resolver), catalog, canSave ? connectionEdits : null, current.History, links.MoveExit,
            () => { Ds1Preview.SetScene(pairedScene, pairedStatus); ds1Window?.RefreshFromWorkspace(); RefreshState(); },
            canSave ? () => { workspaceSession!.Save(current, map, links, connectionEdits); RefreshState(); _ = RefreshLevelProperties(); } : null, warning);
    }
}
