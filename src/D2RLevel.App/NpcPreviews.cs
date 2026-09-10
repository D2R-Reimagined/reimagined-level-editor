using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media.Media3D;
using D2RLevel.Core;

namespace D2RLevel.App;

internal sealed record NpcVisual(string Name, Model3DGroup Geometry, bool Missing);
internal sealed record NpcPreviewSet(Dictionary<int, NpcVisual> Visuals, string[] Diagnostics);
internal static class NpcPreviewLoader
{
    public static NpcPreviewSet Load(LegacyFloorScene? scene, AssetResolver? assets, string? modRoot, IProgress<string> progress, CancellationToken token)
    {
        var visuals = new Dictionary<int, NpcVisual>(); var messages = new List<string>();
        if (scene?.Collision is null) return new(visuals, []);
        NpcCatalog? catalog = null;
        try { if (assets is not null) catalog = new(assets, modRoot); }
        catch (Exception ex) { messages.Add("NPC catalog: " + ex.Message); }
        foreach (var unit in scene.Collision.Document.Units.Where(u => u.Type == 1).DistinctBy(u => u.Id))
        {
            token.ThrowIfCancellationRequested();
            string name = "Unit " + unit.Id;
            try
            {
                var definition = catalog?.Lookup(scene.Map.Act, unit);
                name = definition?.Name ?? name;
                if (definition?.DefinitionPath is not { } path) throw new InvalidDataException(definition?.Warning ?? "NPC tables unavailable.");
                progress.Report("Loading NPC preview: " + name);
                var root = JsonNode.Parse(File.ReadAllText(catalog!.Resolve(path)))!;
                var entities = root["entities"]!.AsArray().OfType<JsonObject>().ToArray();
                var rootTransform = entities.SelectMany(e => e["components"]!.AsArray().OfType<JsonObject>()).FirstOrDefault(c => (string?)c["type"] == "TransformDefinitionComponent");
                double Scale(string axis) => (double?)rootTransform?["scale"]?[axis] ?? 1;
                var model = new Model3DGroup { Transform = new ScaleTransform3D(Scale("x"), Scale("y"), Scale("z")) };
                foreach (var component in entities.SelectMany(e => e["components"]!.AsArray().OfType<JsonObject>()).Where(c => (string?)c["type"] == "ModelDefinitionComponent"))
                {
                    var preview = SceneLoader.Load(PresetDocument.ModelPreview((string)component["filename"]!), assets, progress, token, false, catalog.Resolve);
                    messages.AddRange(preview.Diagnostics.Where(m => m.StartsWith("Model ") || m.StartsWith("Texture ")));
                    if (preview.Items.Count == 0 || preview.Items[0].IsPlaceholder) throw new InvalidDataException("Character mesh unavailable.");
                    model.Children.Add(preview.Items[0].Geometry);
                }
                if (model.Children.Count == 0) throw new InvalidDataException("No static character meshes.");
                model.Freeze(); visuals[unit.Id] = new(name, model, false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { visuals[unit.Id] = new(name, SceneViewport.Placeholder(), true); messages.Add($"NPC {name}: {ex.Message}"); }
        }
        return new(visuals, messages.ToArray());
    }
}

public partial class MainWindow
{
    private NpcPreviewSet npcPreview = new(new(), []);
    private readonly List<SceneItem> npcItems = new();
    private bool syncingNpcs;
    private int npcAct;
    private NpcGround npcGround = new([], CancellationToken.None);
    private void RefreshNpcs()
    {
        if (syncingNpcs) return;
        syncingNpcs = true;
        try
        {
            var selected = SelectedEntities;
            var existing = npcItems.Select(i => i.Entity).ToDictionary(e => e.GameplayUnitIndex!.Value);
            foreach (var item in npcItems) Scene.RemoveItem(item.Entity);
            npcItems.Clear();
            if (document is not null && ShowNpcs.IsChecked == true && pairedScene?.Collision is { } collision)
                foreach (var unit in collision.Document.Units.Where(u => u.Type == 1))
                {
                    var visual = (npcAct == pairedScene.Map.Act ? npcPreview.Visuals.GetValueOrDefault(unit.Id) : null) ?? new NpcVisual("Unit " + unit.Id, SceneViewport.Placeholder(), true);
                    double x = unit.X * Ds1Preview.UnitsPerTile / 5, z = unit.Y * Ds1Preview.UnitsPerTile / 5;
                    string name = $"{visual.Name} · DS1 #{unit.Index}" + (visual.Missing ? " (marker)" : "");
                    var transform = new EntityTransform(new(x, npcGround.Height(x,z), z), new(0, 0, 0, 1), new(1, 1, 1));
                    var entity = existing.TryGetValue(unit.Index, out var previous) && previous.Name == name
                        ? previous : PresetEntity.GameplayPreview(unit.Index, name, transform);
                    entity.UpdateGameplayTransform(transform);
                    var item = new SceneItem(entity, visual.Geometry, visual.Missing); npcItems.Add(item); Scene.AddItem(item);
                }
            Filter();
            SetSelection(selected.Select(e => e.GameplayUnitIndex is { } index ? npcItems.FirstOrDefault(i => i.Entity.GameplayUnitIndex == index)?.Entity : e).OfType<PresetEntity>());
        }
        finally { syncingNpcs = false; }
    }
    private void Npcs_Click(object sender, RoutedEventArgs e) { Scene.CancelDrag(); RefreshNpcs(); RefreshPathOverlay(); }

    /// <summary>
    /// Shows the selected DS1 unit's patrol route in the HD view, at the same scale the
    /// NPC previews use. Nothing here becomes an entity or reaches either document.
    /// </summary>
    private void RefreshPathOverlay()
    {
        if (ShowNpcs.IsChecked != true || pairedScene?.Collision is not { } collision
            || SelectedUnitIndex is not { } index || collision.Document.GameplayWarning is not null)
        { Scene.SetPathOverlay([]); return; }
        var units = collision.Document.Units;
        if (index < 0 || index >= units.Count) { Scene.SetPathOverlay([]); return; }
        var points = collision.Document.PatrolPoints(index);
        if (points.Count == 0) { Scene.SetPathOverlay([]); return; }
        double perSubtile = Ds1Preview.UnitsPerTile / 5;
        Point3D At(int sx, int sy)
        {
            double x = sx * perSubtile, z = sy * perSubtile;
            return new(x, npcGround.Height(x, z) + Ds1Preview.UnitsPerTile * 0.12, z);
        }
        var nodes = points.Select(p => At(p.X, p.Y)).Prepend(At(units[index].X, units[index].Y)).ToArray();
        Scene.SetPathOverlay(nodes, ds1Window?.SelectedPathPoint ?? -1);
    }

    /// <summary>The DS1 unit currently in focus, from either view.</summary>
    private int? SelectedUnitIndex =>
        Selected?.GameplayUnitIndex ?? (placementLinks is { Warning: null } links && Selected is { } entity
            ? links.Find(entity)?.Unit?.Index : null) ?? ds1Window?.SelectedUnitIndex;
    private void MoveNpc(PresetEntity entity, EntityTransform transform)
    {
        if (pairedScene?.Collision is not { } collision || placementLinks is null) throw new InvalidOperationException("Load the paired DS1 first.");
        if (placementLinks.Warning is { } warning) throw new InvalidOperationException(warning);
        if (transform.Orientation != entity.Transform.Orientation || transform.Scale != entity.Transform.Scale || transform.Position.Y != entity.Transform.Position.Y)
            throw new InvalidOperationException("DS1 NPCs support ground-plane movement only.");
        var index = entity.GameplayUnitIndex!.Value;
        if (placementLinks.Links.Any(l => l.Unit?.Index == index)) throw new InvalidOperationException("This unit is linked to an HD model. Move that model to keep its collision and placement together.");
        placementLinks.ConnectWorkspace();
        collision.Document.MoveUnit(index, checked((int)Math.Round(transform.Position.X * 5 / Ds1Preview.UnitsPerTile)), checked((int)Math.Round(transform.Position.Z * 5 / Ds1Preview.UnitsPerTile)));
        RefreshNpcs(); PopulateInspector(); RefreshState();
        Status.Text = "NPC position updated in DS1. Save Scene (or Save linked pair) to keep the change. Ctrl+Z to undo.";
    }
}
