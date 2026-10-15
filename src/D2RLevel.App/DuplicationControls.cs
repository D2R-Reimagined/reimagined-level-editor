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
        // Pasted sources may since have been deleted, so undo selects only those still in the scene.
        SetSelection(document.Entities.Contains(duplication.Copies[0])
            ? duplication.Copies.TakeLast(duplication.Sources.Length) : duplication.Sources.Where(document.Entities.Contains));
        PopulateInspector(); RefreshState();
    }

    private ModelClipboard? clipboard;

    /// <summary>Ctrl+C: remember the selected HD models and prefab placements (tents, wagons, buildings) for pasting.</summary>
    private void CopyModels()
    {
        try
        {
            if (document is null) return;
            var selected = SelectedEntities;
            var copyable = selected.Where(e => GroupMovement.CanGroup(e) && Scene.GetItem(e) is not null).ToArray();
            if (copyable.Length == 0) { Status.Text = L.T("Select HD models or prefabs to copy. DS1 units and terrain are not copied."); return; }
            clipboard = document.CopyModels(copyable);
            int skipped = selected.Length - copyable.Length;
            Status.Text = (skipped > 0 ? L.T("Copied {0} objects; {1} DS1 units, terrain or unpreviewed entities were skipped.", copyable.Length, skipped)
                : L.T("Copied {0} objects.", copyable.Length)) + " " + L.T("Ctrl+V pastes under the pointer.");
        }
        catch (Exception ex) { Error(ex); }
    }

    /// <summary>
    /// Ctrl+V: place one copy of the clipboard as one undoable edit, centred on the ground under the pointer, or one
    /// selection-width along X when the pointer is not over the viewport. DS1 units and collision links are not copied.
    /// </summary>
    private void PasteModels()
    {
        try
        {
            if (document is null || clipboard is null) return;
            if (clipboard.Document != document) { clipboard = null; Status.Text = L.T("The copied objects belong to another scene. Copy them again here."); return; }
            var bounds = System.Windows.Media.Media3D.Rect3D.Empty;
            for (int i = 0; i < clipboard.Sources.Length; i++)
                if ((Scene.GetItem(clipboard.Sources[i]) ?? addedModels.GetValueOrDefault(clipboard.Sources[i])) is { } item)
                    bounds.Union(SceneViewport.Transform(clipboard.Snapshots[i].Transform).TransformBounds(item.Geometry.Bounds));
            if (bounds.IsEmpty) throw new InvalidOperationException(L.T("The copied objects' geometry is unavailable. Copy them again."));
            double centreX = bounds.X + bounds.SizeX / 2, centreZ = bounds.Z + bounds.SizeZ / 2;
            Vector3d offset;
            if (Scene.PointerGround() is { } target)
            {
                // Follow the terrain: lift or lower by the ground difference between the copied spot and the target.
                double from = Scene.GroundHeight?.Invoke(centreX, centreZ) ?? 0;
                offset = new(target.X - centreX, double.IsFinite(from) ? target.Y - from : 0, target.Z - centreZ);
            }
            else offset = new(Math.Max(bounds.SizeX, 1), 0, 0);
            Scene.CancelDrag();
            var result = document.PasteModels(clipboard, offset);
            Status.Text = L.T("Pasted {0} objects. Ctrl+Z undoes the paste. DS1 units and collision links are not copied.", result.Copies.Length);
        }
        catch (Exception ex) { Error(ex); }
    }
}
