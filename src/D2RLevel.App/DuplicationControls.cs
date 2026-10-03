using D2RLevel.Core;

namespace D2RLevel.App;

public partial class MainWindow
{
    private void DuplicateAlongAxis(PresetEntity[] sources, Vector3d step, int repetitions)
    {
        try
        {
            if (document is null) return;
            if (sources.All(e => e.GameplayUnitIndex is not null)) { DuplicateUnits(sources, step, repetitions); return; }
            var result = document.DuplicateModels(sources, step, repetitions);
            Status.Text = L.T("Created {0} HD copies. Ctrl+Z undoes the row. DS1 units and collision links are not copied.", result.Copies.Length);
        }
        catch (Exception ex) { Error(ex); }
    }

    /// <summary>
    /// Repeats DS1 placements (NPCs, monsters, critters) along a whole-subtile step. Copies keep the
    /// source's type, ID and flags; patrol routes are not copied. Copies that would leave the map or
    /// land on a patrol anchor are skipped, and the rest are appended as one undoable edit.
    /// </summary>
    private void DuplicateUnits(PresetEntity[] sources, Vector3d step, int repetitions)
    {
        if (pairedScene?.Collision is not { } collision || placementLinks is null) throw new InvalidOperationException(L.T("Load the paired DS1 first."));
        if (placementLinks.Warning is { } warning) throw new InvalidOperationException(warning);
        var map = collision.Document;
        double subtile = Ds1Preview.UnitsPerTile / 5;
        int dx = checked((int)Math.Round(step.X / subtile)), dy = checked((int)Math.Round(step.Z / subtile));
        if (dx == 0 && dy == 0) return;
        var units = map.Units; var copies = new List<Ds1Unit>(); int skipped = 0;
        foreach (var source in sources)
        {
            int index = source.GameplayUnitIndex!.Value;
            if (placementLinks.Links.Any(l => l.Unit?.Index == index))
                throw new InvalidOperationException(L.T("This unit is linked to an HD model. Alt-drag that model to copy them together."));
            var unit = units[index];
            for (int r = 1; r <= repetitions; r++)
            {
                int x = unit.X + dx * r, y = unit.Y + dy * r;
                if (x < 0 || y < 0 || x >= map.Width * 5 || y >= map.Height * 5 || map.IsPatrolAnchor(x, y)) { skipped++; continue; }
                copies.Add(unit with { Index = -1, X = x, Y = y });
            }
        }
        if (copies.Count == 0) { Status.Text = L.T("No unit copies fit inside the map."); return; }
        Scene.CancelDrag();
        var added = placementLinks.AppendUnits(copies);
        var last = added.TakeLast(sources.Length).ToHashSet();
        SetSelection(npcItems.Where(i => last.Contains(i.Entity.GameplayUnitIndex!.Value)).Select(i => i.Entity));
        RefreshState();
        Status.Text = (skipped > 0
            ? L.T("Placed {0} unit copies; {1} outside the map or on a patrol anchor were skipped.", added.Length, skipped)
            : L.T("Placed {0} unit copies.", added.Length)) + " " + L.T("Patrol routes are not copied. Ctrl+Z undoes the row.");
    }

    private void SyncDuplication(ModelDuplication duplication)
    {
        if (document is null) return;
        for (int i = 0; i < duplication.Copies.Length; i++)
        {
            var copy = duplication.Copies[i];
            if (document.Entities.Contains(copy))
            {
                if (Scene.GetItem(copy) is null)
                {
                    if (!addedModels.TryGetValue(copy, out var item))
                    {
                        var source = duplication.Sources[i % duplication.Sources.Length];
                        var sourceItem = Scene.GetItem(source) ?? addedModels.GetValueOrDefault(source)
                            ?? throw new InvalidOperationException("Source model geometry is unavailable.");
                        item = sourceItem with { Entity = copy };
                        addedModels[copy] = item;
                    }
                    Scene.AddItem(item);
                }
                removedModels.Remove(copy);
            }
            else
            {
                if (Scene.GetItem(copy) is { } item) addedModels[copy] = item;
                Scene.RemoveItem(copy); removedModels.Add(copy);
            }
        }
        Search.Text = ""; Filter();
        SetSelection(document.Entities.Contains(duplication.Copies[0])
            ? duplication.Copies.TakeLast(duplication.Sources.Length) : duplication.Sources);
        PopulateInspector(); RefreshState();
    }
}
