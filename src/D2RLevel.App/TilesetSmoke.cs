using System.IO;
using System.Windows;
using System.Windows.Media.Media3D;
using D2RLevel.Core;

namespace D2RLevel.App;

public partial class MainWindow
{
    /// <summary>
    /// Tileset-first level creation end to end on real assets: the wizard's Act 1 Cave layouts, loading one with the chosen tileset,
    /// creating the project, painting rows from the tileset palette with and without blocking, undo/redo, save/reopen, and a
    /// suggested exit in the new map.
    /// </summary>
    private async Task VerifyTilesetAuthoring(string output)
    {
        Directory.CreateDirectory(output);
        RenderingChecks.Run();
        var assets = resolver ?? throw new InvalidOperationException("Tileset smoke requires --data.");
        var results = new List<string> { "PASS synthetic brush: grid snapping, dominant-axis rows, negative direction, cancellation, quarter turns, model-width steps, terrain height, gizmo hidden while painting." };

        var wizard = new NewLevelWizard(assets, null, 1) { Owner = this }; wizard.Show();
        LevelTypeChoice type; TemplateChoice template;
        try
        {
            await wizard.Choose(3, "act1/caves/caveroom2.ds1");
            if (wizard.TemplateCount < 2 || wizard.LevelType is not { Name: "Act 1 - Cave" } chosenType || wizard.Template is not { IsLevel: true, LevelId: 13 } chosenTemplate)
                throw new InvalidOperationException($"The wizard did not offer Act 1 Cave layouts ({wizard.TemplateCount}).");
            type = chosenType; template = chosenTemplate;
            await CaptureAuthoring(wizard, Path.Combine(output, "new-level-wizard.png"));
            results.Add($"PASS wizard: Act 1 - Cave offers {wizard.TemplateCount} layouts with Cave Treasure 2 (level 13) among the level presets.");
        }
        finally { wizard.Close(); }

        if (!await LoadTemplate(type, template)) throw new InvalidOperationException("Template did not load: " + pairedStatus);
        if (pairedScene!.TilesetSource != TilesetSource.Chosen || pairedScene.Dt1Paths.Length == 0 || pairedScene.MissingCells != 0)
            throw new InvalidOperationException($"Template loaded with {pairedScene.TilesetSource} and {pairedScene.MissingCells} unresolved cells.");
        var (floorMain, floorSub) = CommonFloor(pairedScene) ?? throw new InvalidOperationException("The template has no drawable floor.");
        await CreateLevelAt(Path.Combine(output, "cave-arena"), "Cave arena", Ds1CollisionDocument.FloorKey(floorMain, floorSub), false, type);
        var project = LevelProject.ForPreset(document!.SourcePath) ?? throw new InvalidOperationException("The new level did not open as a project.");
        if (project.LevelType != 3 || project.LevelTypeName != "Act 1 - Cave" || workspaceSession is null || pairedScene?.Collision is null)
            throw new InvalidOperationException("The new level did not record its tileset or open as a workspace scene.");
        results.Add($"PASS creation: '{project.Name}' {project.Width}×{project.Height} records {project.LevelTypeName} and its {project.Tileset.Files.Length} DT1 files.");

        var window = OpenTilesetPalette(); await window.Ready;
        var wall = window.Catalog.FirstOrDefault(m => TilesetCatalog.Kind(m) == ModelKind.Walls && m.Path.Contains("/act1/caves/", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No cave wall among {window.ModelCount} palette models.");
        await window.SelectModel(wall.Path);
        if (window.PreviewItem is null) throw new InvalidOperationException("Palette preview failed for " + wall.Path);
        await CaptureAuthoring(window, Path.Combine(output, "tileset-palette.png"));
        results.Add($"PASS palette: {window.ModelCount} Act 1 cave models; {wall.Name} previews and arms the brush.");

        var map = pairedScene.Collision.Document; var doc = document!;
        double unitsPerTile = Ds1Preview.UnitsPerTile;
        var mapBefore = map.Serialize(); var jsonBefore = doc.Serialize(); int entitiesBefore = doc.Entities.Count;
        window.StartPainting();
        if (Scene.Brush is null) throw new InvalidOperationException("Paint did not arm the viewport brush.");
        Scene.FocusOn(map.Width * unitsPerTile / 2, map.Height * unitsPerTile / 2, unitsPerTile * 14); Scene.UpdateLayout();
        Point Ground(double x, double z) => Scene.Project(new Point3D(x, npcGround.Height(x, z), z)) ?? throw new InvalidOperationException("Paint point is off screen.");
        double cx = map.Width * unitsPerTile / 2, cz = map.Height * unitsPerTile / 2;

        // A row along X without blocking: HD only.
        if (!Scene.BeginBrush(Ground(cx, cz))) throw new InvalidOperationException("Brush stroke did not start.");
        double spacing = Math.Max(window.SnapUnits, Math.Round(Scene.BrushSize().X / window.SnapUnits) * window.SnapUnits);
        Scene.UpdateBrush(Ground(cx + spacing * 3.2, cz));
        int planned = Scene.BrushPlan.Count;
        if (planned < 3 || Scene.BrushPlan.Zip(Scene.BrushPlan.Skip(1)).Any(p => Math.Abs(p.Second.Position.X - p.First.Position.X - spacing) > 1e-6 || p.Second.Position.Z != p.First.Position.Z))
            throw new InvalidOperationException($"Row preview did not step {spacing} along X: " + string.Join(", ", Scene.BrushPlan.Select(t => t.Position)));
        await CaptureAuthoring(this, Path.Combine(output, "palette-row-preview.png"));
        Scene.EndBrush(true);
        var row = doc.Entities.Skip(entitiesBefore).ToArray();
        if (row.Length != planned || row.Any(e => Scene.GetItem(e) is null || e.PreviewModel != wall.Path) || SelectedEntities.Length != planned || !map.Serialize().SequenceEqual(mapBefore))
            throw new InvalidOperationException("Painting did not add the row's models and geometry, select them, and leave the DS1 alone: " + Status.Text);
        await CaptureAuthoring(this, Path.Combine(output, "palette-row.png"));
        Undo_Click(this, new());
        if (doc.Entities.Count != entitiesBefore || row.Any(e => Scene.GetItem(e) is not null)) throw new InvalidOperationException("One undo did not remove the painted row.");
        Redo_Click(this, new());
        if (row.Any(e => !doc.Entities.Contains(e) || Scene.GetItem(e) is null)) throw new InvalidOperationException("Redo did not restore the painted row.");
        results.Add($"PASS painting: {planned} pieces step {spacing:F1} HD units on the tile grid, select as a row, and undo/redo in one step without touching the DS1.");

        // A turned row along Z that blocks movement: each piece owns the floor tiles under it in the same edit.
        Scene.RotateBrush();
        if (window.Brush is not { } turned || Math.Abs(Math.Abs(turned.Orientation.Y) - Math.Sin(Math.PI / 4)) > 1e-9) throw new InvalidOperationException("The palette did not follow R.");
        window.SetBlockMovement(true);
        if (!window.BlockMovement) throw new InvalidOperationException("Block movement is unavailable on a paired workspace scene.");
        var afterRow = map.Serialize(); int linksBefore = placementLinks!.Links.Count, before = doc.Entities.Count;
        Scene.BeginBrush(Ground(cx - unitsPerTile * 3, cz - unitsPerTile * 3));
        Scene.UpdateBrush(Ground(cx - unitsPerTile * 3, cz + Scene.BrushSize().Z * 2.2 - unitsPerTile * 3));
        Scene.EndBrush(true);
        var blocking = doc.Entities.Skip(before).ToArray();
        if (blocking.Length < 2 || placementLinks.Links.Count != linksBefore + blocking.Length || blocking.Any(e => placementLinks.CollisionTiles(e).Length == 0)
            || !placementLinks.CollisionTiles(blocking[0]).All(t => map.HasOverride(t.X, t.Y)))
            throw new InvalidOperationException("Blocking pieces did not each own blocked DS1 tiles: " + Status.Text);
        await CaptureAuthoring(this, Path.Combine(output, "palette-blocking-row.png"));
        Undo_Click(this, new());
        if (!map.Serialize().SequenceEqual(afterRow) || placementLinks.Links.Count != linksBefore || blocking.Any(doc.Entities.Contains))
            throw new InvalidOperationException("One undo did not remove blocking pieces and their collision together.");
        Redo_Click(this, new());
        results.Add($"PASS blocking: {blocking.Length} turned pieces each own blocked DS1 tiles; one undo removes models, links and collision.");

        window.StopPainting();
        if (Scene.Brush is not null) throw new InvalidOperationException("Stopping did not disarm the brush.");
        workspaceSession!.Save(doc, map, placementLinks, connectionEdits);
        int savedCount = doc.Entities.Count; var savedMap = map.Serialize();
        await OpenWorkspaceScene(workspaceScenes.Single());
        if (document!.Entities.Count != savedCount || !pairedScene!.Collision!.Document.Serialize().SequenceEqual(savedMap) || placementLinks!.Warning is not null)
            throw new InvalidOperationException("Painted models and blocking did not survive save and reopen.");
        results.Add("PASS save/reopen: painted models, owned footprints and the DS1 reopen with healthy links.");

        // The new map has no exits: suggest a place for slot 0 and take it.
        var editor = CreateEntranceEditor(); editor.Owner = this; editor.Show();
        try
        {
            var newMap = pairedScene.Collision.Document;
            editor.SelectSource(0); editor.SuggestSpots();
            if (editor.SourceMap.Candidates.Count == 0) throw new InvalidOperationException("No exit suggestions for the new level: " + editor.StatusText);
            var spot = editor.SourceMap.Candidates[0];
            await CaptureAuthoring(editor, Path.Combine(output, "new-level-exit-suggestions.png"));
            editor.SourceMap.ChooseCandidate(0);
            if (!newMap.ExitTiles().Any(t => t.IsExit && t.Main == 0 && t.Hidden && (t.X, t.Y) == (spot.X, spot.Y)))
                throw new InvalidOperationException("Choosing a suggestion did not add exit 0: " + editor.StatusText);
            results.Add($"PASS exits: the new level's slot 0 takes suggestion 1 at tile {spot.X}, {spot.Y} ({spot.Openness:P0} open ground).");
        }
        finally { editor.Close(); }
        workspaceSession!.Save(document!, pairedScene.Collision.Document, placementLinks!, connectionEdits);
        File.WriteAllText(Path.Combine(output, "tileset-smoke.txt"), string.Join("\n", results) + "\nProgrammatic WPF interaction on extracted assets; not an in-game test.\n");
    }
}
